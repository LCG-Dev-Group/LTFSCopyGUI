#![allow(clippy::missing_safety_doc)]

use hashbrown::HashMap;
use memmap2::{Mmap, MmapOptions};
use quick_xml::escape::{escape, unescape};
use quick_xml::events::{BytesDecl, BytesEnd, BytesStart, BytesText, Event};
use quick_xml::{Reader, Writer};
use rayon::slice::ParallelSliceMut;
use std::cmp::Ordering;
use std::collections::BinaryHeap;
use std::ffi::c_void;
use std::fs::{File, OpenOptions};
use std::io::{BufRead, BufReader, BufWriter, Cursor, Read, Seek, SeekFrom, Write};
use std::path::{Path, PathBuf};
use std::ptr;
use std::slice;
use std::str;
use std::sync::atomic::{AtomicU64, Ordering as AtomicOrdering};
use std::sync::{Arc, Mutex, MutexGuard, OnceLock};

#[cfg(windows)]
#[link(name = "kernel32")]
unsafe extern "system" {
    fn CompareStringEx(
        locale_name: *const u16,
        compare_flags: u32,
        left: *const u16,
        left_length: i32,
        right: *const u16,
        right_length: i32,
        version_information: *mut c_void,
        reserved: *mut c_void,
        sort_version: isize,
    ) -> i32;
}

#[cfg(windows)]
#[link(name = "shlwapi")]
unsafe extern "system" {
    fn StrCmpLogicalW(left: *const u16, right: *const u16) -> i32;
}

pub const LSC_OK: i32 = 0;
pub const LSC_ERROR: i32 = -1;
pub const LSC_INVALID_ARGUMENT: i32 = -2;
pub const LSC_INVALID_DATA: i32 = -3;
pub const LSC_BUFFER_TOO_SMALL: i32 = -4;
pub const LSC_UNSUPPORTED_ENCODING: i32 = -5;

const DIRECTORY_MAGIC: i32 = 0x4c53_4452; // LSDR
const DIRECTORY_VERSION: i32 = 2;
const DIRECTORY_HEADER_SIZE: i64 = 64;
const FILE_INDEX_ENTRY_SIZE: i64 = 32;
const DIRECTORY_INDEX_ENTRY_SIZE: i64 = 24;

const PRESENT_CREATOR: u32 = 1 << 0;
const PRESENT_VOLUME_UUID: u32 = 1 << 1;
const PRESENT_GENERATION_NUMBER: u32 = 1 << 2;
const PRESENT_UPDATE_TIME: u32 = 1 << 3;
const PRESENT_LOCATION: u32 = 1 << 4;
const PRESENT_PREVIOUS_LOCATION: u32 = 1 << 5;
const PRESENT_ALLOW_POLICY_UPDATE: u32 = 1 << 6;
const PRESENT_DATA_PLACEMENT_POLICY: u32 = 1 << 7;
const PRESENT_VOLUME_LOCK_STATE: u32 = 1 << 8;
const PRESENT_HIGHEST_FILE_UID: u32 = 1 << 9;

static LAST_ERROR: OnceLock<Mutex<String>> = OnceLock::new();
static MERGE_TEMP_SEQUENCE: AtomicU64 = AtomicU64::new(0);

fn last_error() -> &'static Mutex<String> {
    LAST_ERROR.get_or_init(|| Mutex::new(String::new()))
}

fn set_last_error(message: impl Into<String>) {
    if let Ok(mut value) = last_error().lock() {
        *value = message.into();
    }
}

fn ffi_call<F>(call: F) -> i32
where
    F: FnOnce() -> Result<(), String> + std::panic::UnwindSafe,
{
    match std::panic::catch_unwind(call) {
        Ok(Ok(())) => LSC_OK,
        Ok(Err(error)) => {
            set_last_error(error);
            LSC_ERROR
        }
        Err(_) => {
            set_last_error("ltfscopy_schema panicked while servicing an FFI call");
            LSC_ERROR
        }
    }
}

fn ffi_call_value<T, F>(call: F, output: *mut T) -> i32
where
    F: FnOnce() -> Result<T, String> + std::panic::UnwindSafe,
{
    if output.is_null() {
        set_last_error("output pointer is null");
        return LSC_INVALID_ARGUMENT;
    }
    match std::panic::catch_unwind(call) {
        Ok(Ok(value)) => {
            // SAFETY: the caller supplied a non-null output pointer and owns it.
            unsafe { *output = value };
            LSC_OK
        }
        Ok(Err(error)) => {
            set_last_error(error);
            LSC_ERROR
        }
        Err(_) => {
            set_last_error("ltfscopy_schema panicked while servicing an FFI call");
            LSC_ERROR
        }
    }
}

fn invalid(message: impl Into<String>) -> String {
    message.into()
}

unsafe fn utf16_slice<'a>(ptr: *const u16, len: u32) -> Result<&'a [u16], String> {
    if len == 0 {
        return Ok(&[]);
    }
    if ptr.is_null() {
        return Err(invalid("UTF-16 pointer is null"));
    }
    // SAFETY: the FFI contract requires `ptr` to reference `len` UTF-16 code units.
    Ok(unsafe { slice::from_raw_parts(ptr, len as usize) })
}

unsafe fn utf16_string(ptr: *const u16, len: u32) -> Result<String, String> {
    let value = unsafe { utf16_slice(ptr, len) }?;
    String::from_utf16(value).map_err(|_| invalid("invalid UTF-16 input"))
}

unsafe fn byte_slice<'a>(ptr: *const u8, len: usize) -> Result<&'a [u8], String> {
    if len == 0 {
        return Ok(&[]);
    }
    if ptr.is_null() {
        return Err(invalid("byte pointer is null"));
    }
    // SAFETY: the FFI contract requires `ptr` to reference `len` bytes.
    Ok(unsafe { slice::from_raw_parts(ptr, len) })
}

fn copy_utf16(value: &str, buffer: *mut u16, capacity: u32, required: *mut u32) -> i32 {
    let encoded: Vec<u16> = value.encode_utf16().collect();
    let required_length = match encoded.len().checked_add(1) {
        Some(value) => value,
        None => {
            set_last_error("UTF-16 output is too long");
            return LSC_ERROR;
        }
    };
    if !required.is_null() {
        // SAFETY: `required` is an output pointer supplied by the caller.
        unsafe { *required = required_length.min(u32::MAX as usize) as u32 };
    }
    if required_length > capacity as usize {
        return LSC_BUFFER_TOO_SMALL;
    }
    if buffer.is_null() {
        set_last_error("UTF-16 output pointer is null");
        return LSC_INVALID_ARGUMENT;
    }
    // SAFETY: capacity was checked against the string and its terminator.
    unsafe {
        ptr::copy_nonoverlapping(encoded.as_ptr(), buffer, encoded.len());
        *buffer.add(encoded.len()) = 0;
    }
    LSC_OK
}

fn copy_bytes(value: &[u8], buffer: *mut u8, capacity: u32, written: *mut u32) -> i32 {
    if !written.is_null() {
        // SAFETY: `written` is an output pointer supplied by the caller.
        unsafe { *written = value.len().min(u32::MAX as usize) as u32 };
    }
    if value.len() > capacity as usize {
        return LSC_BUFFER_TOO_SMALL;
    }
    if value.is_empty() {
        return LSC_OK;
    }
    if buffer.is_null() {
        set_last_error("byte output pointer is null");
        return LSC_INVALID_ARGUMENT;
    }
    // SAFETY: capacity was checked against the destination length.
    unsafe { ptr::copy_nonoverlapping(value.as_ptr(), buffer, value.len()) };
    LSC_OK
}

fn copy_bytes_wide(value: &[u8], buffer: *mut u8, capacity: u64, written: *mut u64) -> i32 {
    if !written.is_null() {
        // written is an output pointer supplied by the caller.
        unsafe { *written = value.len() as u64 };
    }
    if value.len() as u64 > capacity {
        return LSC_BUFFER_TOO_SMALL;
    }
    if value.is_empty() {
        return LSC_OK;
    }
    if buffer.is_null() {
        set_last_error("byte output pointer is null");
        return LSC_INVALID_ARGUMENT;
    }
    // SAFETY: capacity was checked against the destination length.
    unsafe { ptr::copy_nonoverlapping(value.as_ptr(), buffer, value.len()) };
    LSC_OK
}

#[repr(C)]
#[derive(Clone, Copy, Default)]
pub struct LscSchemaResult {
    pub struct_size: u32,
    pub abi_version: u32,
    pub root_file_index_offset: i64,
    pub root_file_count: u64,
    pub root_directory_index_offset: i64,
    pub root_directory_count: u64,
    pub selection_count: u64,
}

#[repr(C)]
#[derive(Clone, Copy, Default)]
pub struct LscSchemaMetadata {
    pub struct_size: u32,
    pub abi_version: u32,
    pub present_mask: u32,
    pub reserved: u32,
    pub generation_number: u64,
    pub location_partition: u32,
    pub location_reserved: u32,
    pub location_start_block: u64,
    pub previous_location_partition: u32,
    pub previous_location_reserved: u32,
    pub previous_location_start_block: u64,
    pub allow_policy_update: u32,
    pub data_placement_policy: u32,
    pub volume_lock_state: u32,
    pub highest_file_uid: i64,
}

#[repr(C)]
#[derive(Clone, Copy, Default)]
pub struct LscFileInfo {
    pub struct_size: u32,
    pub abi_version: u32,
    pub length: i64,
    pub read_only: u32,
    pub open_for_write: u32,
    pub file_uid: i64,
    pub xattr_count: u32,
    pub extent_count: u32,
}

#[repr(C)]
#[derive(Clone, Copy, Default)]
pub struct LscExtent {
    pub file_offset: i64,
    pub partition: u32,
    pub reserved: u32,
    pub start_block: i64,
    pub byte_offset: i64,
    pub byte_count: i64,
}

#[repr(C)]
#[derive(Clone, Copy)]
pub struct LscUtf16Slice {
    pub ptr: *const u16,
    pub len: u32,
}

impl Default for LscUtf16Slice {
    fn default() -> Self {
        Self {
            ptr: ptr::null(),
            len: 0,
        }
    }
}

#[repr(C)]
#[derive(Clone, Copy, Default)]
pub struct LscXattrInput {
    pub key: LscUtf16Slice,
    pub value: LscUtf16Slice,
}

#[repr(C)]
#[derive(Clone, Copy, Default)]
pub struct LscExtentInput {
    pub file_offset: i64,
    pub partition: u32,
    pub reserved: u32,
    pub start_block: i64,
    pub byte_offset: i64,
    pub byte_count: i64,
}

#[repr(C)]
#[derive(Clone, Copy, Default)]
pub struct LscFileInput {
    pub struct_size: u32,
    pub reserved: u32,
    pub name: LscUtf16Slice,
    pub length: i64,
    pub read_only: u32,
    pub open_for_write: u32,
    pub creation_time: LscUtf16Slice,
    pub change_time: LscUtf16Slice,
    pub modify_time: LscUtf16Slice,
    pub access_time: LscUtf16Slice,
    pub backup_time: LscUtf16Slice,
    pub file_uid: i64,
    pub symlink: LscUtf16Slice,
    pub xattrs: *const LscXattrInput,
    pub xattr_count: u32,
    pub extents: *const LscExtentInput,
    pub extent_count: u32,
}

#[repr(C)]
#[derive(Clone, Copy, Default)]
pub struct LscStoreDirectoryInfo {
    pub struct_size: u32,
    pub abi_version: u32,
    pub scalar_offset: i64,
    pub scalar_length: i64,
    pub file_index_offset: i64,
    pub file_count: i64,
    pub directory_index_offset: i64,
    pub directory_count: i64,
    pub total_file_count: i64,
    pub total_directory_count: i64,
    pub read_only: u32,
    pub reserved: u32,
    pub file_uid: i64,
}

#[repr(C)]
#[derive(Clone, Copy, Default)]
pub struct LscStoreFileIndexEntry {
    pub struct_size: u32,
    pub abi_version: u32,
    pub next_offset: i64,
    pub record_offset: i64,
    pub record_length: i64,
    pub selection_index: i64,
}

// The index analyzer only needs the file name, byte length, and first tape
// extent.  Keeping this ABI separate from LscFileInfo lets the backing-store
// path avoid constructing xattrs, timestamps, and all remaining extents for
// every file in a large schema.
#[repr(C)]
#[derive(Clone, Copy, Default)]
pub struct LscStoreFileSummary {
    pub struct_size: u32,
    pub abi_version: u32,
    pub length: i64,
    pub partition: u32,
    pub reserved: u32,
    pub start_block: i64,
    pub byte_offset: i64,
    pub byte_count: i64,
}

#[repr(C)]
#[derive(Clone, Copy, Default)]
pub struct LscStoreDirectoryIndexEntry {
    pub struct_size: u32,
    pub abi_version: u32,
    pub next_offset: i64,
    pub record_offset: i64,
    pub selection_index: i64,
}

pub const LSC_SEARCH_MATCH_DIRECTORY: u32 = 1;
pub const LSC_SEARCH_MATCH_FILE: u32 = 2;

#[repr(C)]
#[derive(Clone, Copy, Default)]
pub struct LscStoreSearchResult {
    pub struct_size: u32,
    pub abi_version: u32,
    pub found: u32,
    pub match_kind: u32,
    pub parent_directory_record_offset: i64,
    pub record_offset: i64,
    pub record_length: i64,
    pub file_index: i64,
}

pub type LscStoreSearchProgressCallback =
    unsafe extern "system" fn(processed: u64, total: u64, user_data: *mut c_void);

#[repr(C)]
#[derive(Clone, Copy, Default)]
pub struct LscStoreTapeSortResult {
    pub struct_size: u32,
    pub abi_version: u32,
    pub file_count: u64,
    pub partition_a_file_count: u64,
    pub partition_b_file_count: u64,
}

pub type LscStoreTapeSortProgressCallback =
    unsafe extern "system" fn(processed: u64, total: u64, user_data: *mut c_void);

#[repr(C)]
#[derive(Clone, Copy, Default)]
pub struct LscStoreDirectorySortResult {
    pub struct_size: u32,
    pub abi_version: u32,
    pub file_count: u64,
    pub directory_count: u64,
}

pub type LscStoreDirectorySortProgressCallback =
    unsafe extern "system" fn(processed: u64, total: u64, user_data: *mut c_void);

#[derive(Default, Clone)]
struct SchemaMetadata {
    public: LscSchemaMetadata,
    creator: String,
    volume_uuid: String,
    update_time: String,
}

pub struct SchemaContext {
    result: LscSchemaResult,
    metadata: SchemaMetadata,
}

#[derive(Default, Clone)]
struct FileData {
    name: String,
    length: i64,
    read_only: bool,
    open_for_write: bool,
    creation_time: Option<String>,
    change_time: Option<String>,
    modify_time: Option<String>,
    access_time: Option<String>,
    backup_time: Option<String>,
    file_uid: i64,
    xattrs: Vec<(String, String)>,
    symlink: Option<String>,
    extents: Vec<LscExtent>,
}

fn event_name_start(value: &BytesStart<'_>) -> String {
    value.local_name().as_ref().to_owned()
}

fn event_name_end(value: &BytesEnd<'_>) -> String {
    value.local_name().as_ref().to_owned()
}

fn is_name(value: &BytesStart<'_>, expected: &str) -> bool {
    value.local_name().as_ref() == expected
}

fn decode_text(value: &str) -> Result<String, String> {
    unescape(value)
        .map(|text| text.into_owned())
        .map_err(|error| format!("invalid XML escape: {error}"))
}

fn decode_cdata(value: &str) -> Result<String, String> {
    Ok(value.to_owned())
}

fn decode_general_ref(value: &str) -> Result<String, String> {
    match value {
        "amp" => Ok("&".to_owned()),
        "lt" => Ok("<".to_owned()),
        "gt" => Ok(">".to_owned()),
        "quot" => Ok("\"".to_owned()),
        "apos" => Ok("'".to_owned()),
        value if value.starts_with("#x") || value.starts_with("#X") => {
            let number = u32::from_str_radix(&value[2..], 16)
                .map_err(|_| invalid("invalid hexadecimal XML character reference"))?;
            char::from_u32(number)
                .map(|value| value.to_string())
                .ok_or_else(|| invalid("invalid XML character reference"))
        }
        value if value.starts_with('#') => {
            let number = value[1..]
                .parse::<u32>()
                .map_err(|_| invalid("invalid decimal XML character reference"))?;
            char::from_u32(number)
                .map(|value| value.to_string())
                .ok_or_else(|| invalid("invalid XML character reference"))
        }
        _ => Err(invalid("unsupported XML entity reference")),
    }
}

fn decode_schema_value(value: String) -> String {
    value.replace("%25", "%")
}

fn parse_bool(value: &str) -> Option<bool> {
    match value.trim().to_ascii_lowercase().as_str() {
        "true" | "1" => Some(true),
        "false" | "0" => Some(false),
        _ => None,
    }
}

fn is_xml_whitespace(value: &str) -> bool {
    !value.is_empty()
        && value
            .chars()
            .all(|character| matches!(character, ' ' | '\t' | '\r' | '\n'))
}

fn is_file_record_formatting_element(name: &str) -> bool {
    name == "file"
        || name == "extendedattributes"
        || name == "xattr"
        || name == "extentinfo"
        || name == "extent"
}

fn is_file_record_formatting_whitespace(elements: &[bool], value: &str) -> bool {
    is_xml_whitespace(value) && elements.last().copied().unwrap_or(false)
}

fn parse_partition(value: &str) -> Option<u32> {
    match value.trim().to_ascii_lowercase().as_str() {
        "a" => Some(0),
        "b" => Some(1),
        _ => None,
    }
}

fn write_i32(output: &mut impl Write, value: i32) -> Result<(), String> {
    output
        .write_all(&value.to_le_bytes())
        .map_err(|error| error.to_string())
}

fn write_i64(output: &mut impl Write, value: i64) -> Result<(), String> {
    output
        .write_all(&value.to_le_bytes())
        .map_err(|error| error.to_string())
}

fn read_i64_at(bytes: &[u8], offset: usize, label: &str) -> Result<i64, String> {
    let end = offset
        .checked_add(8)
        .ok_or_else(|| invalid(format!("{label} offset overflow")))?;
    if end > bytes.len() {
        return Err(invalid(format!("{label} is truncated")));
    }
    Ok(i64::from_le_bytes(
        bytes[offset..end]
            .try_into()
            .map_err(|_| invalid(format!("invalid {label}")))?,
    ))
}

fn write_i64_at(bytes: &mut [u8], offset: usize, value: i64, label: &str) -> Result<(), String> {
    let end = offset
        .checked_add(8)
        .ok_or_else(|| invalid(format!("{label} offset overflow")))?;
    if end > bytes.len() {
        return Err(invalid(format!("{label} is truncated")));
    }
    bytes[offset..end].copy_from_slice(&value.to_le_bytes());
    Ok(())
}

fn rebase_offset(value: i64, base: i64, label: &str) -> Result<i64, String> {
    if value == -1 {
        return Ok(-1);
    }
    if value < -1 {
        return Err(invalid(format!("invalid {label} offset")));
    }
    value
        .checked_add(base)
        .ok_or_else(|| invalid(format!("{label} offset overflow")))
}

fn write_nullable_string(output: &mut impl Write, value: Option<&str>) -> Result<(), String> {
    let Some(value) = value else {
        return write_i32(output, -1);
    };
    let bytes = value.as_bytes();
    let length =
        i32::try_from(bytes.len()).map_err(|_| invalid("schema scalar string is too long"))?;
    write_i32(output, length)?;
    output.write_all(bytes).map_err(|error| error.to_string())
}

fn write_file_uid_element<W: Write>(writer: &mut Writer<W>, file_uid: i64) -> Result<(), String> {
    writer
        .write_event(Event::Start(BytesStart::new("fileuid")))
        .map_err(|error| error.to_string())?;
    write_xml_text(writer, &file_uid.to_string())?;
    writer
        .write_event(Event::End(BytesEnd::new("fileuid")))
        .map_err(|error| error.to_string())
}

fn rewrite_file_record_uid(bytes: &[u8], file_uid: i64) -> Result<Vec<u8>, String> {
    let mut reader = Reader::from_reader(Cursor::new(bytes));
    let mut writer = Writer::new(Vec::with_capacity(bytes.len() + 24));
    let mut buffer = Vec::new();
    let mut depth = 0usize;
    let mut root_seen = false;
    let mut root_closed = false;
    let mut file_uid_found = false;
    let mut replacing_file_uid_at_depth = None;

    loop {
        let event = reader
            .read_event_into(&mut buffer)
            .map_err(|error| error.to_string())?
            .into_owned();
        buffer.clear();

        match event {
            Event::Start(value) => {
                if replacing_file_uid_at_depth.is_some() {
                    depth = depth
                        .checked_add(1)
                        .ok_or_else(|| invalid("file record XML depth overflow"))?;
                    continue;
                }

                if depth == 0 {
                    if root_seen || !is_name(&value, "file") {
                        return Err(invalid("file record root element must be file"));
                    }
                    root_seen = true;
                }
                depth = depth
                    .checked_add(1)
                    .ok_or_else(|| invalid("file record XML depth overflow"))?;
                if depth == 2 && is_name(&value, "fileuid") && !file_uid_found {
                    writer
                        .write_event(Event::Start(value))
                        .map_err(|error| error.to_string())?;
                    replacing_file_uid_at_depth = Some(depth);
                } else {
                    writer
                        .write_event(Event::Start(value))
                        .map_err(|error| error.to_string())?;
                }
            }
            Event::Empty(value) => {
                if replacing_file_uid_at_depth.is_some() {
                    continue;
                }
                if depth == 0 {
                    if root_seen || !is_name(&value, "file") {
                        return Err(invalid("file record root element must be file"));
                    }
                    root_seen = true;
                    writer
                        .write_event(Event::Start(value))
                        .map_err(|error| error.to_string())?;
                    write_file_uid_element(&mut writer, file_uid)?;
                    writer
                        .write_event(Event::End(BytesEnd::new("file")))
                        .map_err(|error| error.to_string())?;
                    file_uid_found = true;
                    root_closed = true;
                } else if depth == 1 && is_name(&value, "fileuid") && !file_uid_found {
                    writer
                        .write_event(Event::Start(value))
                        .map_err(|error| error.to_string())?;
                    write_xml_text(&mut writer, &file_uid.to_string())?;
                    writer
                        .write_event(Event::End(BytesEnd::new("fileuid")))
                        .map_err(|error| error.to_string())?;
                    file_uid_found = true;
                } else {
                    writer
                        .write_event(Event::Empty(value))
                        .map_err(|error| error.to_string())?;
                }
            }
            Event::End(value) => {
                if let Some(file_uid_depth) = replacing_file_uid_at_depth {
                    if depth == file_uid_depth && event_name_end(&value) == "fileuid" {
                        write_xml_text(&mut writer, &file_uid.to_string())?;
                        writer
                            .write_event(Event::End(value))
                            .map_err(|error| error.to_string())?;
                        file_uid_found = true;
                        replacing_file_uid_at_depth = None;
                    }
                    depth = depth
                        .checked_sub(1)
                        .ok_or_else(|| invalid("file record XML depth underflow"))?;
                    continue;
                }

                if depth == 0 {
                    return Err(invalid("file record XML has an unexpected end element"));
                }
                if depth == 1 && event_name_end(&value) == "file" {
                    if !file_uid_found {
                        write_file_uid_element(&mut writer, file_uid)?;
                        file_uid_found = true;
                    }
                    writer
                        .write_event(Event::End(value))
                        .map_err(|error| error.to_string())?;
                    depth = 0;
                    root_closed = true;
                } else {
                    writer
                        .write_event(Event::End(value))
                        .map_err(|error| error.to_string())?;
                    depth -= 1;
                }
            }
            Event::Text(value) => {
                if replacing_file_uid_at_depth.is_none() {
                    writer
                        .write_event(Event::Text(value))
                        .map_err(|error| error.to_string())?;
                }
            }
            Event::CData(value) => {
                if replacing_file_uid_at_depth.is_none() {
                    writer
                        .write_event(Event::CData(value))
                        .map_err(|error| error.to_string())?;
                }
            }
            Event::Comment(value) => {
                if replacing_file_uid_at_depth.is_none() {
                    writer
                        .write_event(Event::Comment(value))
                        .map_err(|error| error.to_string())?;
                }
            }
            Event::PI(value) => {
                if replacing_file_uid_at_depth.is_none() {
                    writer
                        .write_event(Event::PI(value))
                        .map_err(|error| error.to_string())?;
                }
            }
            Event::GeneralRef(value) => {
                decode_general_ref(value.as_ref())?;
                if replacing_file_uid_at_depth.is_none() {
                    writer
                        .write_event(Event::GeneralRef(value))
                        .map_err(|error| error.to_string())?;
                }
            }
            Event::Decl(_) => {
                return Err(invalid("XML declaration is not valid inside a file record"));
            }
            Event::DocType(_) => return Err(invalid("unsafe XML construct in file record")),
            Event::Eof => break,
        }
    }

    if !root_seen || !root_closed || depth != 0 || replacing_file_uid_at_depth.is_some() {
        return Err(invalid("file record XML is incomplete"));
    }
    Ok(writer.into_inner())
}

struct IndexChain {
    first: i64,
    last: i64,
    count: u64,
}

impl Default for IndexChain {
    fn default() -> Self {
        Self {
            first: -1,
            last: -1,
            count: 0,
        }
    }
}

struct DirectoryState {
    offset: i64,
    selection_index: i64,
    files: IndexChain,
    directories: IndexChain,
    total_file_count: i64,
    total_directory_count: i64,
}

struct StoreOutput {
    file_records: BufWriter<File>,
    directory_records: BufWriter<File>,
    file_index: File,
    directory_index: File,
    selection: File,
    file_index_data: Vec<u8>,
    directory_index_data: Vec<u8>,
    selection_data: Vec<u8>,
    file_records_position: i64,
    directory_records_position: i64,
    selection_count: u64,
    next_file_uid: i64,
}

struct CountingWriter<'a, W: Write> {
    inner: &'a mut W,
    written: usize,
}

impl<W: Write> Write for CountingWriter<'_, W> {
    fn write(&mut self, buffer: &[u8]) -> std::io::Result<usize> {
        let written = self.inner.write(buffer)?;
        self.written = self.written.checked_add(written).ok_or_else(|| {
            std::io::Error::new(std::io::ErrorKind::Other, "written byte count overflow")
        })?;
        Ok(written)
    }

    fn flush(&mut self) -> std::io::Result<()> {
        self.inner.flush()
    }
}

impl StoreOutput {
    fn new(paths: &[PathBuf; 5]) -> Result<Self, String> {
        let open = |path: &Path| {
            OpenOptions::new()
                .create(true)
                .truncate(true)
                .read(true)
                .write(true)
                .open(path)
                .map_err(|error| {
                    format!(
                        "cannot create schema backing file {}: {error}",
                        path.display()
                    )
                })
        };
        Ok(Self {
            file_records: BufWriter::with_capacity(1024 * 1024, open(&paths[0])?),
            directory_records: BufWriter::with_capacity(256 * 1024, open(&paths[1])?),
            file_index: open(&paths[2])?,
            directory_index: open(&paths[3])?,
            selection: open(&paths[4])?,
            file_index_data: Vec::new(),
            directory_index_data: Vec::new(),
            selection_data: Vec::new(),
            file_records_position: 0,
            directory_records_position: 0,
            selection_count: 0,
            next_file_uid: 1,
        })
    }

    fn allocate_file_uid(&mut self) -> Result<i64, String> {
        let file_uid = self.next_file_uid;
        self.next_file_uid = self
            .next_file_uid
            .checked_add(1)
            .ok_or_else(|| invalid("too many file UIDs in merged schema"))?;
        Ok(file_uid)
    }

    fn allocate_selection(&mut self) -> Result<i64, String> {
        let index = i64::try_from(self.selection_count)
            .map_err(|_| invalid("too many schema selection records"))?;
        self.selection_data.push(1);
        self.selection_count += 1;
        Ok(index)
    }

    fn finish(&mut self) -> Result<(), String> {
        self.file_records
            .flush()
            .map_err(|error| error.to_string())?;
        self.directory_records
            .flush()
            .map_err(|error| error.to_string())?;
        self.file_index
            .write_all(&self.file_index_data)
            .map_err(|error| error.to_string())?;
        self.directory_index
            .write_all(&self.directory_index_data)
            .map_err(|error| error.to_string())?;
        self.selection
            .write_all(&self.selection_data)
            .map_err(|error| error.to_string())?;
        self.file_index.flush().map_err(|error| error.to_string())?;
        self.directory_index
            .flush()
            .map_err(|error| error.to_string())?;
        self.selection.flush().map_err(|error| error.to_string())?;
        Ok(())
    }

    fn begin_directory(&mut self) -> Result<DirectoryState, String> {
        let offset = self.directory_records_position;
        let selection_index = self.allocate_selection()?;
        write_i32(&mut self.directory_records, DIRECTORY_MAGIC)?;
        write_i32(&mut self.directory_records, DIRECTORY_VERSION)?;
        write_i64(&mut self.directory_records, -1)?;
        write_i32(&mut self.directory_records, 0)?;
        write_i32(&mut self.directory_records, 0)?;
        write_i64(&mut self.directory_records, -1)?;
        write_i32(&mut self.directory_records, 0)?;
        write_i64(&mut self.directory_records, -1)?;
        write_i32(&mut self.directory_records, 0)?;
        write_i64(&mut self.directory_records, 0)?;
        write_i64(&mut self.directory_records, 0)?;
        self.directory_records_position += DIRECTORY_HEADER_SIZE;
        Ok(DirectoryState {
            offset,
            selection_index,
            files: IndexChain::default(),
            directories: IndexChain::default(),
            total_file_count: 0,
            total_directory_count: 0,
        })
    }

    fn append_file_index(
        &mut self,
        chain: &mut IndexChain,
        record_offset: i64,
        record_length: i64,
        selection_index: i64,
    ) -> Result<(), String> {
        let entry_offset = i64::try_from(self.file_index_data.len())
            .map_err(|_| invalid("file index is too large"))?;
        self.file_index_data
            .extend_from_slice(&(-1i64).to_le_bytes());
        self.file_index_data
            .extend_from_slice(&record_offset.to_le_bytes());
        self.file_index_data
            .extend_from_slice(&record_length.to_le_bytes());
        self.file_index_data
            .extend_from_slice(&selection_index.to_le_bytes());
        if chain.last >= 0 {
            let position = usize::try_from(chain.last)
                .map_err(|_| invalid("file index offset is too large"))?;
            let end = position
                .checked_add(8)
                .ok_or_else(|| invalid("file index offset overflow"))?;
            if end > self.file_index_data.len() {
                return Err(invalid("file index chain points outside the backing data"));
            }
            self.file_index_data[position..end].copy_from_slice(&entry_offset.to_le_bytes());
        } else {
            chain.first = entry_offset;
        }
        chain.last = entry_offset;
        chain.count += 1;
        Ok(())
    }

    fn append_directory_index(
        &mut self,
        chain: &mut IndexChain,
        record_offset: i64,
        selection_index: i64,
    ) -> Result<(), String> {
        let entry_offset = i64::try_from(self.directory_index_data.len())
            .map_err(|_| invalid("directory index is too large"))?;
        self.directory_index_data
            .extend_from_slice(&(-1i64).to_le_bytes());
        self.directory_index_data
            .extend_from_slice(&record_offset.to_le_bytes());
        self.directory_index_data
            .extend_from_slice(&selection_index.to_le_bytes());
        if chain.last >= 0 {
            let position = usize::try_from(chain.last)
                .map_err(|_| invalid("directory index offset is too large"))?;
            let end = position
                .checked_add(8)
                .ok_or_else(|| invalid("directory index offset overflow"))?;
            if end > self.directory_index_data.len() {
                return Err(invalid(
                    "directory index chain points outside the backing data",
                ));
            }
            self.directory_index_data[position..end].copy_from_slice(&entry_offset.to_le_bytes());
        } else {
            chain.first = entry_offset;
        }
        chain.last = entry_offset;
        chain.count += 1;
        Ok(())
    }

    fn join_file_chains(
        &mut self,
        target: &mut IndexChain,
        source: &IndexChain,
    ) -> Result<(), String> {
        if source.count == 0 {
            return Ok(());
        }
        if target.last >= 0 {
            let position = usize::try_from(target.last)
                .map_err(|_| invalid("file index offset is too large"))?;
            let end = position
                .checked_add(8)
                .ok_or_else(|| invalid("file index offset overflow"))?;
            if end > self.file_index_data.len() {
                return Err(invalid("file index chain points outside the backing data"));
            }
            self.file_index_data[position..end].copy_from_slice(&source.first.to_le_bytes());
        } else {
            target.first = source.first;
        }
        target.last = source.last;
        target.count = target
            .count
            .checked_add(source.count)
            .ok_or_else(|| invalid("too many files in merged schema"))?;
        Ok(())
    }

    fn join_directory_chains(
        &mut self,
        target: &mut IndexChain,
        source: &IndexChain,
    ) -> Result<(), String> {
        if source.count == 0 {
            return Ok(());
        }
        if target.last >= 0 {
            let position = usize::try_from(target.last)
                .map_err(|_| invalid("directory index offset is too large"))?;
            let end = position
                .checked_add(8)
                .ok_or_else(|| invalid("directory index offset overflow"))?;
            if end > self.directory_index_data.len() {
                return Err(invalid(
                    "directory index chain points outside the backing data",
                ));
            }
            self.directory_index_data[position..end].copy_from_slice(&source.first.to_le_bytes());
        } else {
            target.first = source.first;
        }
        target.last = source.last;
        target.count = target
            .count
            .checked_add(source.count)
            .ok_or_else(|| invalid("too many directories in merged schema"))?;
        Ok(())
    }

    fn normalize_merge_directories(
        &mut self,
        root: &mut DirectoryState,
        directory_records_path: &Path,
    ) -> Result<(), String> {
        // Directory records are streamed into a buffered writer while the
        // merge sources are appended.  Flush them before opening a reader for
        // the names and headers that drive directory de-duplication.
        self.directory_records
            .flush()
            .map_err(|error| error.to_string())?;
        let directory_records = OpenOptions::new()
            .read(true)
            .write(true)
            .open(directory_records_path)
            .map_err(|error| {
                format!(
                    "cannot open merged directory records {}: {error}",
                    directory_records_path.display()
                )
            })?;
        let directory_records_length = self.directory_records_position;
        let mut normalizer = MergeDirectoryNormalizer {
            store: self,
            directory_records,
            directory_records_length,
        };
        normalizer.normalize_root(root)
    }

    fn append_merge_source(
        &mut self,
        source: &MergeSourceResult,
    ) -> Result<(IndexChain, IndexChain), String> {
        let directory_records_base = self.directory_records_position;
        let file_index_base = i64::try_from(self.file_index_data.len())
            .map_err(|_| invalid("file index is too large"))?;
        let directory_index_base = i64::try_from(self.directory_index_data.len())
            .map_err(|_| invalid("directory index is too large"))?;
        let selection_base = i64::try_from(self.selection_count)
            .map_err(|_| invalid("schema selection data is too large"))?;

        if source.file_records_length < 0
            || source.directory_records_length < 0
            || source.file_index_length < 0
            || source.directory_index_length < 0
        {
            return Err(invalid("invalid merge source backing file length"));
        }

        let mut file_index = std::fs::read(&source.paths[2]).map_err(|error| {
            format!(
                "cannot read merge source file index {}: {error}",
                source.paths[2].display()
            )
        })?;
        if i64::try_from(file_index.len())
            .map_err(|_| invalid("merge source file index is too large"))?
            != source.file_index_length
            || file_index.len() % FILE_INDEX_ENTRY_SIZE as usize != 0
        {
            return Err(invalid("merge source file index is truncated"));
        }

        let mut source_file_records = File::open(&source.paths[0]).map_err(|error| {
            format!(
                "cannot open merge source file records {}: {error}",
                source.paths[0].display()
            )
        })?;
        let source_file_records_length = source_file_records
            .metadata()
            .map_err(|error| format!("cannot stat merge source file records: {error}"))?
            .len();
        if source_file_records_length
            != u64::try_from(source.file_records_length)
                .map_err(|_| invalid("merge source file records are too large"))?
        {
            return Err(invalid("merge source file records changed while merging"));
        }
        for entry in file_index.chunks_exact_mut(FILE_INDEX_ENTRY_SIZE as usize) {
            let next_offset = read_i64_at(entry, 0, "merge source file index next offset")?;
            let record_offset = read_i64_at(entry, 8, "merge source file record offset")?;
            let record_length = read_i64_at(entry, 16, "merge source file record length")?;
            let selection_index = read_i64_at(entry, 24, "merge source file selection index")?;
            if record_offset < 0 || record_length <= 0 {
                return Err(invalid("invalid merge source file record range"));
            }
            let record_end = record_offset
                .checked_add(record_length)
                .ok_or_else(|| invalid("merge source file record offset overflow"))?;
            if record_end > source.file_records_length {
                return Err(invalid(
                    "merge source file record is outside the backing file",
                ));
            }
            let record_length_usize = usize::try_from(record_length)
                .map_err(|_| invalid("merge source file record is too large"))?;
            source_file_records
                .seek(SeekFrom::Start(u64::try_from(record_offset).map_err(
                    |_| invalid("invalid merge source file record offset"),
                )?))
                .map_err(|error| format!("cannot seek merge source file record: {error}"))?;
            let mut record = vec![0u8; record_length_usize];
            source_file_records
                .read_exact(&mut record)
                .map_err(|error| format!("cannot read merge source file record: {error}"))?;
            let file_uid = self.allocate_file_uid()?;
            let rewritten = rewrite_file_record_uid(&record, file_uid)?;
            let rewritten_offset = self.file_records_position;
            let rewritten_length = i64::try_from(rewritten.len())
                .map_err(|_| invalid("merged file record is too large"))?;
            self.file_records
                .write_all(&rewritten)
                .map_err(|error| format!("cannot append merge source file record: {error}"))?;
            self.file_records_position = self
                .file_records_position
                .checked_add(rewritten_length)
                .ok_or_else(|| invalid("file records are too large"))?;

            write_i64_at(
                entry,
                0,
                rebase_offset(next_offset, file_index_base, "file index")?,
                "merge file index next offset",
            )?;
            write_i64_at(entry, 8, rewritten_offset, "merge file record offset")?;
            write_i64_at(entry, 16, rewritten_length, "merge file record length")?;
            write_i64_at(
                entry,
                24,
                rebase_offset(selection_index, selection_base, "selection")?,
                "merge file selection index",
            )?;
        }
        self.file_index_data.extend_from_slice(&file_index);

        let mut directory_records = std::fs::read(&source.paths[1]).map_err(|error| {
            format!(
                "cannot read merge source directory records {}: {error}",
                source.paths[1].display()
            )
        })?;
        if i64::try_from(directory_records.len())
            .map_err(|_| invalid("merge source directory records are too large"))?
            != source.directory_records_length
        {
            return Err(invalid(
                "merge source directory records changed while merging",
            ));
        }

        let source_directory_index = std::fs::read(&source.paths[3]).map_err(|error| {
            format!(
                "cannot read merge source directory index {}: {error}",
                source.paths[3].display()
            )
        })?;
        if source_directory_index.len() % DIRECTORY_INDEX_ENTRY_SIZE as usize != 0 {
            return Err(invalid("merge source directory index is truncated"));
        }
        for entry in source_directory_index.chunks_exact(DIRECTORY_INDEX_ENTRY_SIZE as usize) {
            let record_offset = read_i64_at(entry, 8, "merge source directory record offset")?;
            let local_offset = usize::try_from(record_offset)
                .map_err(|_| invalid("invalid merge source directory record offset"))?;
            let end = local_offset
                .checked_add(DIRECTORY_HEADER_SIZE as usize)
                .ok_or_else(|| invalid("merge source directory record offset overflow"))?;
            if end > directory_records.len() {
                return Err(invalid("merge source directory record is truncated"));
            }
            let header = &mut directory_records[local_offset..end];
            let magic = i32::from_le_bytes(
                header[0..4]
                    .try_into()
                    .map_err(|_| invalid("invalid merge source directory header"))?,
            );
            let version = i32::from_le_bytes(
                header[4..8]
                    .try_into()
                    .map_err(|_| invalid("invalid merge source directory header"))?,
            );
            if magic != DIRECTORY_MAGIC || version != DIRECTORY_VERSION {
                return Err(invalid("invalid merge source directory header"));
            }
            let scalar_offset = read_i64_at(header, 8, "merge source scalar offset")?;
            let file_index_offset = read_i64_at(header, 24, "merge source file index offset")?;
            let directory_index_offset =
                read_i64_at(header, 36, "merge source directory index offset")?;
            write_i64_at(
                header,
                8,
                rebase_offset(scalar_offset, directory_records_base, "directory scalar")?,
                "merge directory scalar offset",
            )?;
            write_i64_at(
                header,
                24,
                rebase_offset(file_index_offset, file_index_base, "file index")?,
                "merge directory file index offset",
            )?;
            write_i64_at(
                header,
                36,
                rebase_offset(
                    directory_index_offset,
                    directory_index_base,
                    "directory index",
                )?,
                "merge directory index offset",
            )?;
        }
        self.directory_records
            .write_all(&directory_records)
            .map_err(|error| format!("cannot append merge source directory records: {error}"))?;
        self.directory_records_position = self
            .directory_records_position
            .checked_add(source.directory_records_length)
            .ok_or_else(|| invalid("directory records are too large"))?;

        let mut directory_index = source_directory_index;
        if i64::try_from(directory_index.len())
            .map_err(|_| invalid("merge source directory index is too large"))?
            != source.directory_index_length
        {
            return Err(invalid(
                "merge source directory index changed while merging",
            ));
        }
        for entry in directory_index.chunks_exact_mut(DIRECTORY_INDEX_ENTRY_SIZE as usize) {
            let next_offset = read_i64_at(entry, 0, "merge source directory index next offset")?;
            let record_offset = read_i64_at(entry, 8, "merge source directory record offset")?;
            let selection_index = read_i64_at(entry, 16, "merge source directory selection index")?;
            write_i64_at(
                entry,
                0,
                rebase_offset(next_offset, directory_index_base, "directory index")?,
                "merge directory index next offset",
            )?;
            write_i64_at(
                entry,
                8,
                rebase_offset(record_offset, directory_records_base, "directory record")?,
                "merge directory record offset",
            )?;
            write_i64_at(
                entry,
                16,
                rebase_offset(selection_index, selection_base, "selection")?,
                "merge directory selection index",
            )?;
        }
        self.directory_index_data
            .extend_from_slice(&directory_index);

        let selection = std::fs::read(&source.paths[4]).map_err(|error| {
            format!(
                "cannot read merge source selection {}: {error}",
                source.paths[4].display()
            )
        })?;
        if u64::try_from(selection.len()).map_err(|_| invalid("selection data is too large"))?
            != source.selection_count
        {
            return Err(invalid("merge source selection data changed while merging"));
        }
        self.selection_data.extend_from_slice(&selection);
        self.selection_count = self
            .selection_count
            .checked_add(source.selection_count)
            .ok_or_else(|| invalid("schema selection data is too large"))?;

        let files = IndexChain {
            first: rebase_offset(source.files.first, file_index_base, "file index")?,
            last: rebase_offset(source.files.last, file_index_base, "file index")?,
            count: source.files.count,
        };
        let directories = IndexChain {
            first: rebase_offset(
                source.directories.first,
                directory_index_base,
                "directory index",
            )?,
            last: rebase_offset(
                source.directories.last,
                directory_index_base,
                "directory index",
            )?,
            count: source.directories.count,
        };
        Ok((files, directories))
    }

    fn finish_directory(
        &mut self,
        state: &DirectoryState,
        values: &DirectoryValues,
    ) -> Result<(), String> {
        let scalar_offset = self.directory_records_position;
        let mut scalar = Vec::with_capacity(256);
        write_nullable_string(&mut scalar, values.name.as_deref())?;
        scalar
            .write_all(&[u8::from(values.read_only)])
            .map_err(|error| error.to_string())?;
        write_nullable_string(&mut scalar, values.creation_time.as_deref())?;
        write_nullable_string(&mut scalar, values.change_time.as_deref())?;
        write_nullable_string(&mut scalar, values.modify_time.as_deref())?;
        write_nullable_string(&mut scalar, values.access_time.as_deref())?;
        write_nullable_string(&mut scalar, values.backup_time.as_deref())?;
        write_i64(&mut scalar, values.file_uid)?;
        let scalar_length = i32::try_from(scalar.len())
            .map_err(|_| invalid("directory scalar record is too long"))?;
        self.directory_records
            .write_all(&scalar)
            .map_err(|error| error.to_string())?;
        self.directory_records_position +=
            i64::try_from(scalar.len()).map_err(|_| invalid("directory records are too large"))?;

        let restore_position = self.directory_records_position;
        self.directory_records
            .seek(SeekFrom::Start((state.offset + 8) as u64))
            .map_err(|error| error.to_string())?;
        write_i64(&mut self.directory_records, scalar_offset)?;
        write_i32(&mut self.directory_records, scalar_length)?;
        write_i32(&mut self.directory_records, 0)?;
        write_i64(&mut self.directory_records, state.files.first)?;
        write_i32(
            &mut self.directory_records,
            i32::try_from(state.files.count).map_err(|_| invalid("too many files in directory"))?,
        )?;
        write_i64(&mut self.directory_records, state.directories.first)?;
        write_i32(
            &mut self.directory_records,
            i32::try_from(state.directories.count)
                .map_err(|_| invalid("too many directories in directory"))?,
        )?;
        write_i64(&mut self.directory_records, state.total_file_count)?;
        write_i64(&mut self.directory_records, state.total_directory_count)?;
        self.directory_records
            .seek(SeekFrom::Start(restore_position as u64))
            .map_err(|error| error.to_string())?;
        Ok(())
    }

    fn append_file_record_raw<R: BufRead>(
        &mut self,
        reader: &mut Reader<R>,
        start: BytesStart<'static>,
    ) -> Result<(i64, i64), String> {
        let offset = self.file_records_position;
        let mut sink = CountingWriter {
            inner: &mut self.file_records,
            written: 0,
        };
        {
            let mut writer = Writer::new(&mut sink);
            writer
                .write_event(Event::Start(start))
                .map_err(|error| error.to_string())?;
            let mut depth = 1i32;
            let mut elements = vec![true];
            let mut buffer = Vec::new();
            while depth > 0 {
                let event = reader
                    .read_event_into(&mut buffer)
                    .map_err(|error| error.to_string())?
                    .into_owned();
                buffer.clear();
                match event {
                    Event::Start(value) => {
                        elements.push(is_file_record_formatting_element(
                            value.local_name().as_ref(),
                        ));
                        writer
                            .write_event(Event::Start(value))
                            .map_err(|error| error.to_string())?;
                        depth += 1;
                    }
                    Event::Empty(value) => writer
                        .write_event(Event::Empty(value))
                        .map_err(|error| error.to_string())?,
                    Event::End(value) => {
                        writer
                            .write_event(Event::End(value))
                            .map_err(|error| error.to_string())?;
                        elements
                            .pop()
                            .ok_or_else(|| invalid("file record XML depth underflow"))?;
                        depth -= 1;
                    }
                    Event::Text(value) => {
                        if !is_file_record_formatting_whitespace(&elements, value.as_ref()) {
                            writer
                                .write_event(Event::Text(value))
                                .map_err(|error| error.to_string())?;
                        }
                    }
                    Event::CData(value) => writer
                        .write_event(Event::CData(value))
                        .map_err(|error| error.to_string())?,
                    Event::Comment(_) | Event::PI(_) => {}
                    Event::Decl(_) => {
                        return Err(invalid("XML declaration is not valid inside a file record"));
                    }
                    Event::DocType(_) => return Err(invalid("unsafe XML construct in schema")),
                    Event::GeneralRef(value) => {
                        decode_general_ref(value.as_ref())?;
                        writer
                            .write_event(Event::GeneralRef(value))
                            .map_err(|error| error.to_string())?;
                    }
                    Event::Eof => return Err(invalid("unexpected end of XML file record")),
                }
            }
        }
        let length =
            i64::try_from(sink.written).map_err(|_| invalid("file record is too large"))?;
        self.file_records_position += length;
        Ok((offset, length))
    }

    fn append_file_record_with_barcode<R: BufRead>(
        &mut self,
        reader: &mut Reader<R>,
        start: BytesStart<'static>,
        barcode: &str,
    ) -> Result<(i64, i64), String> {
        let offset = self.file_records_position;
        let mut sink = CountingWriter {
            inner: &mut self.file_records,
            written: 0,
        };
        {
            let mut writer = Writer::new(&mut sink);
            writer
                .write_event(Event::Start(start))
                .map_err(|error| error.to_string())?;

            let mut depth = 1i32;
            let mut elements = vec![true];
            let mut has_extended_attributes = false;
            let mut buffer = Vec::new();
            while depth > 0 {
                let event = reader
                    .read_event_into(&mut buffer)
                    .map_err(|error| error.to_string())?
                    .into_owned();
                buffer.clear();
                match event {
                    Event::Start(value) => {
                        if depth == 1 && is_name(&value, "extendedattributes") {
                            has_extended_attributes = true;
                            writer
                                .write_event(Event::Start(value.clone()))
                                .map_err(|error| error.to_string())?;
                            copy_extended_attributes_with_barcode(reader, &mut writer, barcode)?;
                        } else {
                            elements.push(is_file_record_formatting_element(
                                value.local_name().as_ref(),
                            ));
                            writer
                                .write_event(Event::Start(value))
                                .map_err(|error| error.to_string())?;
                            depth += 1;
                        }
                    }
                    Event::Empty(value) => {
                        if depth == 1 && is_name(&value, "extendedattributes") {
                            has_extended_attributes = true;
                            let name = event_name_start(&value);
                            writer
                                .write_event(Event::Start(value))
                                .map_err(|error| error.to_string())?;
                            write_barcode_xattr(&mut writer, barcode)?;
                            writer
                                .write_event(Event::End(BytesEnd::new(name)))
                                .map_err(|error| error.to_string())?;
                        } else {
                            writer
                                .write_event(Event::Empty(value))
                                .map_err(|error| error.to_string())?;
                        }
                    }
                    Event::End(value) => {
                        depth -= 1;
                        if depth == 0 && !has_extended_attributes {
                            write_barcode_container(&mut writer, barcode)?;
                        }
                        writer
                            .write_event(Event::End(value))
                            .map_err(|error| error.to_string())?;
                        elements
                            .pop()
                            .ok_or_else(|| invalid("file record XML depth underflow"))?;
                    }
                    Event::Text(value) => {
                        if !is_file_record_formatting_whitespace(&elements, value.as_ref()) {
                            writer
                                .write_event(Event::Text(value))
                                .map_err(|error| error.to_string())?;
                        }
                    }
                    Event::CData(value) => writer
                        .write_event(Event::CData(value))
                        .map_err(|error| error.to_string())?,
                    Event::Comment(_) | Event::PI(_) => {}
                    Event::Decl(_) => {
                        return Err(invalid("XML declaration is not valid inside a file record"));
                    }
                    Event::DocType(_) => return Err(invalid("unsafe XML construct in schema")),
                    Event::GeneralRef(value) => {
                        decode_general_ref(value.as_ref())?;
                        writer
                            .write_event(Event::GeneralRef(value))
                            .map_err(|error| error.to_string())?;
                    }
                    Event::Eof => return Err(invalid("unexpected end of XML file record")),
                }
            }
        }
        let length =
            i64::try_from(sink.written).map_err(|_| invalid("file record is too large"))?;
        self.file_records_position += length;
        Ok((offset, length))
    }

    fn append_empty_file(&mut self, barcode: Option<&str>) -> Result<(i64, i64), String> {
        let offset = self.file_records_position;
        let mut serialized = Vec::with_capacity(if barcode.is_some() { 128 } else { 7 });
        {
            let mut writer = Writer::new(&mut serialized);
            if let Some(barcode) = barcode {
                writer
                    .write_event(Event::Start(BytesStart::new("file")))
                    .map_err(|error| error.to_string())?;
                write_barcode_container(&mut writer, barcode)?;
                writer
                    .write_event(Event::End(BytesEnd::new("file")))
                    .map_err(|error| error.to_string())?;
            } else {
                writer
                    .write_event(Event::Empty(BytesStart::new("file")))
                    .map_err(|error| error.to_string())?;
            }
        }
        let length =
            i64::try_from(serialized.len()).map_err(|_| invalid("file record is too large"))?;
        self.file_records
            .write_all(&serialized)
            .map_err(|error| error.to_string())?;
        self.file_records_position += length;
        Ok((offset, length))
    }
}

fn write_barcode_xattr<W: Write>(writer: &mut Writer<W>, barcode: &str) -> Result<(), String> {
    writer
        .write_event(Event::Start(BytesStart::new("xattr")))
        .map_err(|error| error.to_string())?;
    write_xml_element(writer, "key", Some("Barcode"))?;
    write_xml_element(writer, "value", Some(barcode))?;
    writer
        .write_event(Event::End(BytesEnd::new("xattr")))
        .map_err(|error| error.to_string())
}

fn write_barcode_container<W: Write>(writer: &mut Writer<W>, barcode: &str) -> Result<(), String> {
    writer
        .write_event(Event::Start(BytesStart::new("extendedattributes")))
        .map_err(|error| error.to_string())?;
    write_barcode_xattr(writer, barcode)?;
    writer
        .write_event(Event::End(BytesEnd::new("extendedattributes")))
        .map_err(|error| error.to_string())
}

fn copy_extended_attributes_with_barcode<R: BufRead, W: Write>(
    reader: &mut Reader<R>,
    writer: &mut Writer<W>,
    barcode: &str,
) -> Result<(), String> {
    let mut depth = 1i32;
    let mut elements = vec![true];
    let mut buffer = Vec::new();
    while depth > 0 {
        let event = reader
            .read_event_into(&mut buffer)
            .map_err(|error| error.to_string())?
            .into_owned();
        buffer.clear();
        match event {
            Event::Start(value) => {
                elements.push(is_file_record_formatting_element(
                    value.local_name().as_ref(),
                ));
                writer
                    .write_event(Event::Start(value))
                    .map_err(|error| error.to_string())?;
                depth += 1;
            }
            Event::Empty(value) => writer
                .write_event(Event::Empty(value))
                .map_err(|error| error.to_string())?,
            Event::End(value) => {
                depth -= 1;
                if depth == 0 {
                    write_barcode_xattr(writer, barcode)?;
                }
                writer
                    .write_event(Event::End(value))
                    .map_err(|error| error.to_string())?;
                elements
                    .pop()
                    .ok_or_else(|| invalid("extended attribute XML depth underflow"))?;
            }
            Event::Text(value) => {
                if !is_file_record_formatting_whitespace(&elements, value.as_ref()) {
                    writer
                        .write_event(Event::Text(value))
                        .map_err(|error| error.to_string())?;
                }
            }
            Event::CData(value) => writer
                .write_event(Event::CData(value))
                .map_err(|error| error.to_string())?,
            Event::Comment(_) | Event::PI(_) => {}
            Event::Decl(_) => {
                return Err(invalid(
                    "XML declaration is not valid inside extended attributes",
                ));
            }
            Event::DocType(_) => return Err(invalid("unsafe XML construct in schema")),
            Event::GeneralRef(value) => {
                decode_general_ref(value.as_ref())?;
                writer
                    .write_event(Event::GeneralRef(value))
                    .map_err(|error| error.to_string())?;
            }
            Event::Eof => {
                return Err(invalid(
                    "unexpected end of XML extended attribute container",
                ));
            }
        }
    }
    Ok(())
}

pub struct StoreContext {
    file_records: Mutex<File>,
    directory_records: Mutex<File>,
    file_index: Mutex<File>,
    directory_index: Mutex<File>,
}

struct StoreCursor<'a> {
    bytes: &'a [u8],
    position: usize,
}

impl<'a> StoreCursor<'a> {
    fn new(bytes: &'a [u8]) -> Self {
        Self { bytes, position: 0 }
    }

    fn take(&mut self, length: usize) -> Result<&'a [u8], String> {
        let end = self
            .position
            .checked_add(length)
            .ok_or_else(|| invalid("schema backing record length overflow"))?;
        if end > self.bytes.len() {
            return Err(invalid("schema backing record is truncated"));
        }
        let result = &self.bytes[self.position..end];
        self.position = end;
        Ok(result)
    }

    fn i32(&mut self) -> Result<i32, String> {
        let bytes = self.take(4)?;
        Ok(i32::from_le_bytes(
            bytes.try_into().expect("fixed-size integer"),
        ))
    }

    fn i64(&mut self) -> Result<i64, String> {
        let bytes = self.take(8)?;
        Ok(i64::from_le_bytes(
            bytes.try_into().expect("fixed-size integer"),
        ))
    }

    fn boolean(&mut self) -> Result<bool, String> {
        Ok(self.take(1)?[0] != 0)
    }

    fn nullable_string(&mut self) -> Result<Option<String>, String> {
        let length = self.i32()?;
        if length == -1 {
            return Ok(None);
        }
        if length < 0 || length > 64 * 1024 * 1024 {
            return Err(invalid("invalid schema backing string length"));
        }
        let bytes = self.take(length as usize)?;
        let value = str::from_utf8(bytes)
            .map_err(|_| invalid("schema backing string is not valid UTF-8"))?;
        Ok(Some(value.to_owned()))
    }
}

struct StoreDirectoryHeader {
    scalar_offset: i64,
    scalar_length: i64,
    file_index_offset: i64,
    file_count: i64,
    directory_index_offset: i64,
    directory_count: i64,
    total_file_count: i64,
    total_directory_count: i64,
}

struct MergeDirectoryNormalizer<'a> {
    store: &'a mut StoreOutput,
    directory_records: File,
    directory_records_length: i64,
}

impl MergeDirectoryNormalizer<'_> {
    fn read_bytes(&mut self, offset: i64, length: usize, label: &str) -> Result<Vec<u8>, String> {
        if offset < 0 {
            return Err(invalid(format!("invalid {label} offset")));
        }
        let length = i64::try_from(length).map_err(|_| invalid(format!("{label} is too large")))?;
        let end = offset
            .checked_add(length)
            .ok_or_else(|| invalid(format!("{label} offset overflow")))?;
        if end > self.directory_records_length {
            return Err(invalid(format!("{label} is outside the backing file")));
        }
        self.directory_records
            .seek(SeekFrom::Start(
                u64::try_from(offset).map_err(|_| invalid(format!("invalid {label} offset")))?,
            ))
            .map_err(|error| format!("cannot seek {label}: {error}"))?;
        let mut bytes = vec![0u8; length as usize];
        self.directory_records
            .read_exact(&mut bytes)
            .map_err(|error| format!("cannot read {label}: {error}"))?;
        Ok(bytes)
    }

    fn read_directory_header(
        &mut self,
        record_offset: i64,
    ) -> Result<StoreDirectoryHeader, String> {
        let bytes = self.read_bytes(
            record_offset,
            DIRECTORY_HEADER_SIZE as usize,
            "merge directory header",
        )?;
        let mut cursor = StoreCursor::new(&bytes);
        if cursor.i32()? != DIRECTORY_MAGIC || cursor.i32()? != DIRECTORY_VERSION {
            return Err(invalid("invalid merge directory header"));
        }
        let scalar_offset = cursor.i64()?;
        let scalar_length = i64::from(cursor.i32()?);
        let _reserved = cursor.i32()?;
        let file_index_offset = cursor.i64()?;
        let file_count = i64::from(cursor.i32()?);
        let directory_index_offset = cursor.i64()?;
        let directory_count = i64::from(cursor.i32()?);
        let total_file_count = cursor.i64()?;
        let total_directory_count = cursor.i64()?;

        let scalar_minimum = record_offset
            .checked_add(DIRECTORY_HEADER_SIZE)
            .ok_or_else(|| invalid("merge directory record offset overflow"))?;
        let scalar_end = scalar_offset
            .checked_add(scalar_length)
            .ok_or_else(|| invalid("merge directory scalar offset overflow"))?;
        if scalar_offset < scalar_minimum
            || scalar_length < 0
            || scalar_end > self.directory_records_length
            || file_index_offset < -1
            || directory_index_offset < -1
            || file_count < 0
            || directory_count < 0
            || total_file_count < 0
            || total_directory_count < 0
        {
            return Err(invalid("invalid merge directory counts or indexes"));
        }
        Ok(StoreDirectoryHeader {
            scalar_offset,
            scalar_length,
            file_index_offset,
            file_count,
            directory_index_offset,
            directory_count,
            total_file_count,
            total_directory_count,
        })
    }

    fn read_directory_name(
        &mut self,
        header: &StoreDirectoryHeader,
    ) -> Result<Option<String>, String> {
        let bytes = self.read_bytes(
            header.scalar_offset,
            read_store_length(header.scalar_length, "merge directory scalar record")?,
            "merge directory scalar",
        )?;
        let mut cursor = StoreCursor::new(&bytes);
        cursor.nullable_string()
    }

    fn read_directory_index_entry(&self, offset: i64) -> Result<(i64, i64, i64), String> {
        if offset < 0 {
            return Err(invalid("invalid merge directory index offset"));
        }
        let offset = usize::try_from(offset)
            .map_err(|_| invalid("merge directory index offset is too large"))?;
        let end = offset
            .checked_add(DIRECTORY_INDEX_ENTRY_SIZE as usize)
            .ok_or_else(|| invalid("merge directory index offset overflow"))?;
        if end > self.store.directory_index_data.len() {
            return Err(invalid("merge directory index chain is truncated"));
        }
        Ok((
            read_i64_at(
                &self.store.directory_index_data,
                offset,
                "merge directory index next offset",
            )?,
            read_i64_at(
                &self.store.directory_index_data,
                offset + 8,
                "merge directory record offset",
            )?,
            read_i64_at(
                &self.store.directory_index_data,
                offset + 16,
                "merge directory selection index",
            )?,
        ))
    }

    fn read_file_index_next(&self, offset: i64) -> Result<i64, String> {
        if offset < 0 {
            return Err(invalid("invalid merge file index offset"));
        }
        let offset =
            usize::try_from(offset).map_err(|_| invalid("merge file index offset is too large"))?;
        let end = offset
            .checked_add(FILE_INDEX_ENTRY_SIZE as usize)
            .ok_or_else(|| invalid("merge file index offset overflow"))?;
        if end > self.store.file_index_data.len() {
            return Err(invalid("merge file index chain is truncated"));
        }
        read_i64_at(
            &self.store.file_index_data,
            offset,
            "merge file index next offset",
        )
    }

    fn read_directory_chain(&self, first: i64, count: i64) -> Result<IndexChain, String> {
        if count < 0 {
            return Err(invalid("invalid merge directory count"));
        }
        let count =
            u64::try_from(count).map_err(|_| invalid("merge directory count is too large"))?;
        if count == 0 {
            if first != -1 {
                return Err(invalid(
                    "merge directory index is non-empty with zero count",
                ));
            }
            return Ok(IndexChain::default());
        }
        if first < 0 {
            return Err(invalid("merge directory index chain is truncated"));
        }

        let mut current = first;
        let mut last = -1;
        for _ in 0..count {
            let (next, _, _) = self.read_directory_index_entry(current)?;
            last = current;
            current = next;
        }
        if current != -1 {
            return Err(invalid("merge directory index chain has an invalid length"));
        }
        Ok(IndexChain { first, last, count })
    }

    fn read_file_chain(&self, first: i64, count: i64) -> Result<IndexChain, String> {
        if count < 0 {
            return Err(invalid("invalid merge file count"));
        }
        let count = u64::try_from(count).map_err(|_| invalid("merge file count is too large"))?;
        if count == 0 {
            if first != -1 {
                return Err(invalid("merge file index is non-empty with zero count"));
            }
            return Ok(IndexChain::default());
        }
        if first < 0 {
            return Err(invalid("merge file index chain is truncated"));
        }

        let mut current = first;
        let mut last = -1;
        for _ in 0..count {
            let next = self.read_file_index_next(current)?;
            last = current;
            current = next;
        }
        if current != -1 {
            return Err(invalid("merge file index chain has an invalid length"));
        }
        Ok(IndexChain { first, last, count })
    }

    fn write_directory_index_next(&mut self, offset: i64, next: i64) -> Result<(), String> {
        if offset < 0 {
            return Err(invalid("invalid merge directory index offset"));
        }
        let offset = usize::try_from(offset)
            .map_err(|_| invalid("merge directory index offset is too large"))?;
        let end = offset
            .checked_add(8)
            .ok_or_else(|| invalid("merge directory index offset overflow"))?;
        if end > self.store.directory_index_data.len() {
            return Err(invalid("merge directory index chain is truncated"));
        }
        self.store.directory_index_data[offset..end].copy_from_slice(&next.to_le_bytes());
        Ok(())
    }

    fn write_directory_file_uid(
        &mut self,
        header: &StoreDirectoryHeader,
        file_uid: i64,
    ) -> Result<(), String> {
        let uid_offset = header
            .scalar_offset
            .checked_add(
                header
                    .scalar_length
                    .checked_sub(8)
                    .ok_or_else(|| invalid("merge directory scalar record is truncated"))?,
            )
            .ok_or_else(|| invalid("merge directory scalar offset overflow"))?;
        let uid_end = uid_offset
            .checked_add(8)
            .ok_or_else(|| invalid("merge directory scalar offset overflow"))?;
        let scalar_end = header
            .scalar_offset
            .checked_add(header.scalar_length)
            .ok_or_else(|| invalid("merge directory scalar offset overflow"))?;
        if uid_offset < header.scalar_offset
            || uid_end > scalar_end
            || uid_end > self.directory_records_length
        {
            return Err(invalid("merge directory scalar record is truncated"));
        }
        self.directory_records
            .seek(SeekFrom::Start(u64::try_from(uid_offset).map_err(
                |_| invalid("invalid merge directory scalar offset"),
            )?))
            .map_err(|error| format!("cannot seek merge directory file UID: {error}"))?;
        self.directory_records
            .write_all(&file_uid.to_le_bytes())
            .map_err(|error| format!("cannot update merge directory file UID: {error}"))
    }

    fn write_directory_header(
        &mut self,
        record_offset: i64,
        header: &StoreDirectoryHeader,
    ) -> Result<(), String> {
        let file_count = i32::try_from(header.file_count)
            .map_err(|_| invalid("too many files in merged directory"))?;
        let directory_count = i32::try_from(header.directory_count)
            .map_err(|_| invalid("too many directories in merged directory"))?;
        let mut bytes = self.read_bytes(
            record_offset,
            DIRECTORY_HEADER_SIZE as usize,
            "merge directory header",
        )?;
        write_i64_at(
            &mut bytes,
            24,
            header.file_index_offset,
            "merge directory file index offset",
        )?;
        bytes[32..36].copy_from_slice(&file_count.to_le_bytes());
        write_i64_at(
            &mut bytes,
            36,
            header.directory_index_offset,
            "merge directory index offset",
        )?;
        bytes[44..48].copy_from_slice(&directory_count.to_le_bytes());
        write_i64_at(
            &mut bytes,
            48,
            header.total_file_count,
            "merge directory total file count",
        )?;
        write_i64_at(
            &mut bytes,
            56,
            header.total_directory_count,
            "merge directory total directory count",
        )?;
        self.directory_records
            .seek(SeekFrom::Start(u64::try_from(record_offset).map_err(
                |_| invalid("invalid merge directory record offset"),
            )?))
            .map_err(|error| format!("cannot seek merge directory header: {error}"))?;
        self.directory_records
            .write_all(&bytes)
            .map_err(|error| format!("cannot update merge directory header: {error}"))?;
        Ok(())
    }

    fn merge_directory_records(
        &mut self,
        primary_record_offset: i64,
        duplicate_record_offset: i64,
    ) -> Result<(), String> {
        if primary_record_offset == duplicate_record_offset {
            return Ok(());
        }
        let mut primary = self.read_directory_header(primary_record_offset)?;
        let duplicate = self.read_directory_header(duplicate_record_offset)?;

        let mut primary_files =
            self.read_file_chain(primary.file_index_offset, primary.file_count)?;
        let duplicate_files =
            self.read_file_chain(duplicate.file_index_offset, duplicate.file_count)?;
        self.store
            .join_file_chains(&mut primary_files, &duplicate_files)?;

        let mut primary_directories =
            self.read_directory_chain(primary.directory_index_offset, primary.directory_count)?;
        let duplicate_directories =
            self.read_directory_chain(duplicate.directory_index_offset, duplicate.directory_count)?;
        self.store
            .join_directory_chains(&mut primary_directories, &duplicate_directories)?;

        primary.file_index_offset = primary_files.first;
        primary.file_count = i64::try_from(primary_files.count)
            .map_err(|_| invalid("too many files in merged directory"))?;
        primary.directory_index_offset = primary_directories.first;
        primary.directory_count = i64::try_from(primary_directories.count)
            .map_err(|_| invalid("too many directories in merged directory"))?;
        primary.total_file_count = primary
            .total_file_count
            .checked_add(duplicate.total_file_count)
            .ok_or_else(|| invalid("too many files in merged directory"))?;
        primary.total_directory_count = primary
            .total_directory_count
            .checked_add(duplicate.total_directory_count)
            .ok_or_else(|| invalid("too many directories in merged directory"))?;
        self.write_directory_header(primary_record_offset, &primary)
    }

    fn normalize_directory_chain(&mut self, chain: &mut IndexChain) -> Result<(), String> {
        if chain.count == 0 {
            chain.first = -1;
            chain.last = -1;
            return Ok(());
        }

        let original_count = chain.count;
        let mut current = chain.first;
        let mut previous = -1;
        let mut primary_records = Vec::new();
        let mut primary_by_name = HashMap::<String, i64>::new();

        for _ in 0..original_count {
            let (next, child_record_offset, _) = self.read_directory_index_entry(current)?;
            if child_record_offset < 0 {
                return Err(invalid("invalid merge directory record offset"));
            }
            let child_header = self.read_directory_header(child_record_offset)?;
            let child_name = self.read_directory_name(&child_header)?.unwrap_or_default();

            if let Some(&primary_record_offset) = primary_by_name.get(&child_name) {
                self.merge_directory_records(primary_record_offset, child_record_offset)?;
                if previous >= 0 {
                    self.write_directory_index_next(previous, next)?;
                } else {
                    chain.first = next;
                }
                if current == chain.last {
                    chain.last = previous;
                }
                chain.count = chain
                    .count
                    .checked_sub(1)
                    .ok_or_else(|| invalid("merge directory count underflow"))?;
            } else {
                primary_by_name.insert(child_name, child_record_offset);
                primary_records.push(child_record_offset);
                previous = current;
            }
            current = next;
        }

        if current != -1 {
            return Err(invalid("merge directory index chain has an invalid length"));
        }
        if chain.count == 0 {
            chain.first = -1;
            chain.last = -1;
        }

        // A duplicate directory is removed from its parent's chain, but its
        // children are joined into the first directory with that name.  Walk
        // each surviving primary once so nested duplicate directories are
        // normalized after all sibling merges have been attached.
        for primary_record_offset in primary_records {
            self.normalize_directory(primary_record_offset)?;
        }
        Ok(())
    }

    fn calculate_directory_totals(
        &mut self,
        direct_file_count: i64,
        directories: &IndexChain,
    ) -> Result<(i64, i64), String> {
        if direct_file_count < 0 {
            return Err(invalid("invalid merge directory file count"));
        }
        let mut total_files = direct_file_count;
        let mut total_directories = 0i64;
        let mut current = directories.first;
        for _ in 0..directories.count {
            let (next, child_record_offset, _) = self.read_directory_index_entry(current)?;
            let child_header = self.read_directory_header(child_record_offset)?;
            total_files = total_files
                .checked_add(child_header.total_file_count)
                .ok_or_else(|| invalid("too many files in merged directory"))?;
            total_directories = total_directories
                .checked_add(
                    1i64.checked_add(child_header.total_directory_count)
                        .ok_or_else(|| invalid("too many directories in merged directory"))?,
                )
                .ok_or_else(|| invalid("too many directories in merged directory"))?;
            current = next;
        }
        if current != -1 {
            return Err(invalid("merge directory index chain has an invalid length"));
        }
        Ok((total_files, total_directories))
    }

    fn normalize_directory(&mut self, record_offset: i64) -> Result<StoreDirectoryHeader, String> {
        let mut header = self.read_directory_header(record_offset)?;
        let file_uid = self.store.allocate_file_uid()?;
        self.write_directory_file_uid(&header, file_uid)?;
        let files = self.read_file_chain(header.file_index_offset, header.file_count)?;
        let mut directories =
            self.read_directory_chain(header.directory_index_offset, header.directory_count)?;
        self.normalize_directory_chain(&mut directories)?;
        let direct_file_count = i64::try_from(files.count)
            .map_err(|_| invalid("too many files in merged directory"))?;
        let (total_files, total_directories) =
            self.calculate_directory_totals(direct_file_count, &directories)?;

        header.file_index_offset = files.first;
        header.file_count = i64::try_from(files.count)
            .map_err(|_| invalid("too many files in merged directory"))?;
        header.directory_index_offset = directories.first;
        header.directory_count = i64::try_from(directories.count)
            .map_err(|_| invalid("too many directories in merged directory"))?;
        header.total_file_count = total_files;
        header.total_directory_count = total_directories;
        self.write_directory_header(record_offset, &header)?;
        Ok(header)
    }

    fn normalize_root(&mut self, root: &mut DirectoryState) -> Result<(), String> {
        let root_file_count = i64::try_from(root.files.count)
            .map_err(|_| invalid("too many files in merged schema"))?;
        root.files = self.read_file_chain(root.files.first, root_file_count)?;
        self.normalize_directory_chain(&mut root.directories)?;
        let (total_files, total_directories) =
            self.calculate_directory_totals(root_file_count, &root.directories)?;
        root.total_file_count = total_files;
        root.total_directory_count = total_directories;
        self.directory_records
            .flush()
            .map_err(|error| error.to_string())?;
        Ok(())
    }
}

#[derive(Default)]
struct StoreDirectoryScalars {
    name: Option<String>,
    read_only: bool,
    creation_time: Option<String>,
    change_time: Option<String>,
    modify_time: Option<String>,
    access_time: Option<String>,
    backup_time: Option<String>,
    file_uid: i64,
}

fn read_store_at(
    file: &Mutex<File>,
    offset: i64,
    length: usize,
    label: &str,
) -> Result<Vec<u8>, String> {
    if offset < 0 {
        return Err(invalid(format!("invalid {label} offset")));
    }
    let offset = u64::try_from(offset).map_err(|_| invalid(format!("invalid {label} offset")))?;
    let mut file = file
        .lock()
        .map_err(|_| invalid(format!("{label} backing file is poisoned")))?;
    file.seek(SeekFrom::Start(offset))
        .map_err(|error| format!("cannot seek {label} backing file: {error}"))?;
    let mut bytes = vec![0u8; length];
    file.read_exact(&mut bytes)
        .map_err(|error| format!("cannot read {label} backing file: {error}"))?;
    Ok(bytes)
}

fn read_store_length(value: i64, label: &str) -> Result<usize, String> {
    if value < 0 {
        return Err(invalid(format!("invalid {label} length")));
    }
    usize::try_from(value).map_err(|_| invalid(format!("{label} is too large")))
}

fn read_store_directory_header(
    context: &StoreContext,
    record_offset: i64,
) -> Result<StoreDirectoryHeader, String> {
    let bytes = read_store_at(
        &context.directory_records,
        record_offset,
        DIRECTORY_HEADER_SIZE as usize,
        "directory header",
    )?;
    let mut cursor = StoreCursor::new(&bytes);
    if cursor.i32()? != DIRECTORY_MAGIC || cursor.i32()? != DIRECTORY_VERSION {
        return Err(invalid("invalid schema backing directory header"));
    }
    let scalar_offset = cursor.i64()?;
    let scalar_length = i64::from(cursor.i32()?);
    let _reserved = cursor.i32()?;
    let file_index_offset = cursor.i64()?;
    let file_count = i64::from(cursor.i32()?);
    let directory_index_offset = cursor.i64()?;
    let directory_count = i64::from(cursor.i32()?);
    let total_file_count = cursor.i64()?;
    let total_directory_count = cursor.i64()?;

    let scalar_minimum = record_offset
        .checked_add(DIRECTORY_HEADER_SIZE)
        .ok_or_else(|| invalid("schema backing directory offset overflow"))?;
    if scalar_offset < scalar_minimum || scalar_length < 0 {
        return Err(invalid("invalid schema backing directory scalar record"));
    }
    if file_index_offset < -1
        || directory_index_offset < -1
        || file_count < 0
        || directory_count < 0
        || total_file_count < 0
        || total_directory_count < 0
    {
        return Err(invalid(
            "invalid schema backing directory counts or indexes",
        ));
    }
    Ok(StoreDirectoryHeader {
        scalar_offset,
        scalar_length,
        file_index_offset,
        file_count,
        directory_index_offset,
        directory_count,
        total_file_count,
        total_directory_count,
    })
}

fn read_store_directory_scalars(
    context: &StoreContext,
    header: &StoreDirectoryHeader,
) -> Result<StoreDirectoryScalars, String> {
    let bytes = read_store_at(
        &context.directory_records,
        header.scalar_offset,
        read_store_length(header.scalar_length, "directory scalar record")?,
        "directory scalar",
    )?;
    let mut cursor = StoreCursor::new(&bytes);
    let result = StoreDirectoryScalars {
        name: cursor.nullable_string()?,
        read_only: cursor.boolean()?,
        creation_time: cursor.nullable_string()?,
        change_time: cursor.nullable_string()?,
        modify_time: cursor.nullable_string()?,
        access_time: cursor.nullable_string()?,
        backup_time: cursor.nullable_string()?,
        file_uid: cursor.i64()?,
    };
    Ok(result)
}

fn store_file_index_entry(
    context: &StoreContext,
    offset: i64,
) -> Result<LscStoreFileIndexEntry, String> {
    let bytes = read_store_at(
        &context.file_index,
        offset,
        FILE_INDEX_ENTRY_SIZE as usize,
        "file index",
    )?;
    let mut cursor = StoreCursor::new(&bytes);
    Ok(LscStoreFileIndexEntry {
        struct_size: std::mem::size_of::<LscStoreFileIndexEntry>() as u32,
        abi_version: 1,
        next_offset: cursor.i64()?,
        record_offset: cursor.i64()?,
        record_length: cursor.i64()?,
        selection_index: cursor.i64()?,
    })
}

fn store_directory_file_bytes(context: &StoreContext, record_offset: i64) -> Result<i64, String> {
    let header = read_store_directory_header(context, record_offset)?;
    if header.file_count == 0 {
        return Ok(0);
    }
    if header.file_index_offset < 0 {
        return Err(invalid("invalid schema backing file index"));
    }

    let count = usize::try_from(header.file_count)
        .map_err(|_| invalid("schema backing file count is too large"))?;
    let mut entry_offset = header.file_index_offset;
    let mut total = 0i64;
    for _ in 0..count {
        if entry_offset < 0 {
            return Err(invalid("schema backing file index chain is truncated"));
        }
        let entry = store_file_index_entry(context, entry_offset)?;
        let record_length = read_store_length(entry.record_length, "schema backing file record")?;
        let bytes = read_store_at(
            &context.file_records,
            entry.record_offset,
            record_length,
            "file record",
        )?;
        let length = parse_file_length_bytes(&bytes)?;
        total = total
            .checked_add(length)
            .ok_or_else(|| invalid("schema file byte count overflow"))?;
        entry_offset = entry.next_offset;
    }
    Ok(total)
}

fn store_directory_index_entry(
    context: &StoreContext,
    offset: i64,
) -> Result<LscStoreDirectoryIndexEntry, String> {
    let bytes = read_store_at(
        &context.directory_index,
        offset,
        DIRECTORY_INDEX_ENTRY_SIZE as usize,
        "directory index",
    )?;
    let mut cursor = StoreCursor::new(&bytes);
    Ok(LscStoreDirectoryIndexEntry {
        struct_size: std::mem::size_of::<LscStoreDirectoryIndexEntry>() as u32,
        abi_version: 1,
        next_offset: cursor.i64()?,
        record_offset: cursor.i64()?,
        selection_index: cursor.i64()?,
    })
}

fn read_store_file_into(
    file: &mut File,
    offset: i64,
    length: usize,
    buffer: &mut Vec<u8>,
    label: &str,
) -> Result<(), String> {
    if offset < 0 {
        return Err(invalid(format!("invalid {label} offset")));
    }
    let offset = u64::try_from(offset).map_err(|_| invalid(format!("invalid {label} offset")))?;
    file.seek(SeekFrom::Start(offset))
        .map_err(|error| format!("cannot seek {label} backing file: {error}"))?;
    buffer.resize(length, 0);
    file.read_exact(buffer)
        .map_err(|error| format!("cannot read {label} backing file: {error}"))
}

struct StoreSearchReader<'a> {
    file_records: MutexGuard<'a, File>,
    directory_records: MutexGuard<'a, File>,
    file_index: MutexGuard<'a, File>,
    directory_index: MutexGuard<'a, File>,
    header_buffer: Vec<u8>,
    scalar_buffer: Vec<u8>,
    index_buffer: Vec<u8>,
    record_buffer: Vec<u8>,
}

impl<'a> StoreSearchReader<'a> {
    fn new(context: &'a StoreContext) -> Result<Self, String> {
        Ok(Self {
            file_records: context
                .file_records
                .lock()
                .map_err(|_| invalid("file records backing file is poisoned"))?,
            directory_records: context
                .directory_records
                .lock()
                .map_err(|_| invalid("directory records backing file is poisoned"))?,
            file_index: context
                .file_index
                .lock()
                .map_err(|_| invalid("file index backing file is poisoned"))?,
            directory_index: context
                .directory_index
                .lock()
                .map_err(|_| invalid("directory index backing file is poisoned"))?,
            header_buffer: Vec::with_capacity(DIRECTORY_HEADER_SIZE as usize),
            scalar_buffer: Vec::new(),
            index_buffer: Vec::with_capacity(FILE_INDEX_ENTRY_SIZE as usize),
            record_buffer: Vec::new(),
        })
    }

    fn read_directory_header(
        &mut self,
        record_offset: i64,
    ) -> Result<StoreDirectoryHeader, String> {
        read_store_file_into(
            &mut self.directory_records,
            record_offset,
            DIRECTORY_HEADER_SIZE as usize,
            &mut self.header_buffer,
            "directory header",
        )?;
        let mut cursor = StoreCursor::new(&self.header_buffer);
        if cursor.i32()? != DIRECTORY_MAGIC || cursor.i32()? != DIRECTORY_VERSION {
            return Err(invalid("invalid schema backing directory header"));
        }
        let scalar_offset = cursor.i64()?;
        let scalar_length = i64::from(cursor.i32()?);
        let _reserved = cursor.i32()?;
        let file_index_offset = cursor.i64()?;
        let file_count = i64::from(cursor.i32()?);
        let directory_index_offset = cursor.i64()?;
        let directory_count = i64::from(cursor.i32()?);
        let total_file_count = cursor.i64()?;
        let total_directory_count = cursor.i64()?;

        let scalar_minimum = record_offset
            .checked_add(DIRECTORY_HEADER_SIZE)
            .ok_or_else(|| invalid("schema backing directory offset overflow"))?;
        if scalar_offset < scalar_minimum || scalar_length < 0 {
            return Err(invalid("invalid schema backing directory scalar record"));
        }
        if file_index_offset < -1
            || directory_index_offset < -1
            || file_count < 0
            || directory_count < 0
            || total_file_count < 0
            || total_directory_count < 0
        {
            return Err(invalid(
                "invalid schema backing directory counts or indexes",
            ));
        }
        Ok(StoreDirectoryHeader {
            scalar_offset,
            scalar_length,
            file_index_offset,
            file_count,
            directory_index_offset,
            directory_count,
            total_file_count,
            total_directory_count,
        })
    }

    fn read_directory_name(&mut self, header: &StoreDirectoryHeader) -> Result<String, String> {
        read_store_file_into(
            &mut self.directory_records,
            header.scalar_offset,
            read_store_length(header.scalar_length, "directory scalar record")?,
            &mut self.scalar_buffer,
            "directory scalar",
        )?;
        let mut cursor = StoreCursor::new(&self.scalar_buffer);
        Ok(cursor.nullable_string()?.unwrap_or_default())
    }

    fn read_file_index_entry(&mut self, offset: i64) -> Result<LscStoreFileIndexEntry, String> {
        read_store_file_into(
            &mut self.file_index,
            offset,
            FILE_INDEX_ENTRY_SIZE as usize,
            &mut self.index_buffer,
            "file index",
        )?;
        let mut cursor = StoreCursor::new(&self.index_buffer);
        Ok(LscStoreFileIndexEntry {
            struct_size: std::mem::size_of::<LscStoreFileIndexEntry>() as u32,
            abi_version: 1,
            next_offset: cursor.i64()?,
            record_offset: cursor.i64()?,
            record_length: cursor.i64()?,
            selection_index: cursor.i64()?,
        })
    }

    fn read_directory_index_entry(
        &mut self,
        offset: i64,
    ) -> Result<LscStoreDirectoryIndexEntry, String> {
        read_store_file_into(
            &mut self.directory_index,
            offset,
            DIRECTORY_INDEX_ENTRY_SIZE as usize,
            &mut self.index_buffer,
            "directory index",
        )?;
        let mut cursor = StoreCursor::new(&self.index_buffer);
        Ok(LscStoreDirectoryIndexEntry {
            struct_size: std::mem::size_of::<LscStoreDirectoryIndexEntry>() as u32,
            abi_version: 1,
            next_offset: cursor.i64()?,
            record_offset: cursor.i64()?,
            selection_index: cursor.i64()?,
        })
    }

    fn read_file_name(&mut self, offset: i64, length: i64) -> Result<String, String> {
        read_store_file_into(
            &mut self.file_records,
            offset,
            read_store_length(length, "schema file record")?,
            &mut self.record_buffer,
            "file record",
        )?;
        parse_file_name_bytes(&self.record_buffer)
    }
}

fn map_tape_sort_file(file: &Mutex<File>, label: &str) -> Result<Option<Mmap>, String> {
    let file = file
        .lock()
        .map_err(|_| invalid(format!("{label} backing file is poisoned")))?;
    let length = file
        .metadata()
        .map_err(|error| format!("cannot stat {label} backing file: {error}"))?
        .len();
    if length == 0 {
        return Ok(None);
    }
    // SAFETY: the lazy backing files are immutable while native tape sort is
    // running.  The mapping is kept alive for the duration of the reader.
    unsafe { MmapOptions::new().map(&*file) }
        .map(Some)
        .map_err(|error| format!("cannot map {label} backing file: {error}"))
}

struct TapeSortReader {
    file_records: Option<Mmap>,
    directory_records: Option<Mmap>,
    file_index: Option<Mmap>,
    directory_index: Option<Mmap>,
}

impl TapeSortReader {
    fn new(context: &StoreContext) -> Result<Self, String> {
        Ok(Self {
            file_records: map_tape_sort_file(&context.file_records, "file records")?,
            directory_records: map_tape_sort_file(&context.directory_records, "directory records")?,
            file_index: map_tape_sort_file(&context.file_index, "file index")?,
            directory_index: map_tape_sort_file(&context.directory_index, "directory index")?,
        })
    }

    fn slice<'a>(
        map: &'a Option<Mmap>,
        offset: i64,
        length: i64,
        label: &str,
    ) -> Result<&'a [u8], String> {
        if offset < 0 || length < 0 {
            return Err(invalid(format!("invalid {label} range")));
        }
        let offset =
            usize::try_from(offset).map_err(|_| invalid(format!("invalid {label} offset")))?;
        let length =
            usize::try_from(length).map_err(|_| invalid(format!("{label} is too large")))?;
        let end = offset
            .checked_add(length)
            .ok_or_else(|| invalid(format!("{label} range overflow")))?;
        let Some(map) = map.as_ref() else {
            if length == 0 {
                return Ok(&[]);
            }
            return Err(invalid(format!("{label} backing file is empty")));
        };
        if end > map.len() {
            return Err(invalid(format!("{label} backing file is truncated")));
        }
        Ok(&map[offset..end])
    }

    fn read_directory_header(&self, record_offset: i64) -> Result<StoreDirectoryHeader, String> {
        let bytes = Self::slice(
            &self.directory_records,
            record_offset,
            DIRECTORY_HEADER_SIZE,
            "directory header",
        )?;
        let mut cursor = StoreCursor::new(bytes);
        if cursor.i32()? != DIRECTORY_MAGIC || cursor.i32()? != DIRECTORY_VERSION {
            return Err(invalid("invalid schema backing directory header"));
        }
        let scalar_offset = cursor.i64()?;
        let scalar_length = i64::from(cursor.i32()?);
        let _reserved = cursor.i32()?;
        let file_index_offset = cursor.i64()?;
        let file_count = i64::from(cursor.i32()?);
        let directory_index_offset = cursor.i64()?;
        let directory_count = i64::from(cursor.i32()?);
        let total_file_count = cursor.i64()?;
        let total_directory_count = cursor.i64()?;
        let scalar_minimum = record_offset
            .checked_add(DIRECTORY_HEADER_SIZE)
            .ok_or_else(|| invalid("schema backing directory offset overflow"))?;
        if scalar_offset < scalar_minimum || scalar_length < 0 {
            return Err(invalid("invalid schema backing directory scalar record"));
        }
        if file_index_offset < -1
            || directory_index_offset < -1
            || file_count < 0
            || directory_count < 0
            || total_file_count < 0
            || total_directory_count < 0
        {
            return Err(invalid(
                "invalid schema backing directory counts or indexes",
            ));
        }
        Ok(StoreDirectoryHeader {
            scalar_offset,
            scalar_length,
            file_index_offset,
            file_count,
            directory_index_offset,
            directory_count,
            total_file_count,
            total_directory_count,
        })
    }

    fn read_directory_name(&self, header: &StoreDirectoryHeader) -> Result<String, String> {
        let bytes = Self::slice(
            &self.directory_records,
            header.scalar_offset,
            header.scalar_length,
            "directory scalar",
        )?;
        let mut cursor = StoreCursor::new(bytes);
        Ok(cursor.nullable_string()?.unwrap_or_default())
    }

    fn read_file_index_entry(&self, offset: i64) -> Result<LscStoreFileIndexEntry, String> {
        let bytes = Self::slice(
            &self.file_index,
            offset,
            FILE_INDEX_ENTRY_SIZE,
            "file index",
        )?;
        let mut cursor = StoreCursor::new(bytes);
        Ok(LscStoreFileIndexEntry {
            struct_size: std::mem::size_of::<LscStoreFileIndexEntry>() as u32,
            abi_version: 1,
            next_offset: cursor.i64()?,
            record_offset: cursor.i64()?,
            record_length: cursor.i64()?,
            selection_index: cursor.i64()?,
        })
    }

    fn read_directory_index_entry(
        &self,
        offset: i64,
    ) -> Result<LscStoreDirectoryIndexEntry, String> {
        let bytes = Self::slice(
            &self.directory_index,
            offset,
            DIRECTORY_INDEX_ENTRY_SIZE,
            "directory index",
        )?;
        let mut cursor = StoreCursor::new(bytes);
        Ok(LscStoreDirectoryIndexEntry {
            struct_size: std::mem::size_of::<LscStoreDirectoryIndexEntry>() as u32,
            abi_version: 1,
            next_offset: cursor.i64()?,
            record_offset: cursor.i64()?,
            selection_index: cursor.i64()?,
        })
    }

    fn read_file_summary(&self, offset: i64, length: i64) -> Result<ParsedFileSummary, String> {
        let bytes = Self::slice(&self.file_records, offset, length, "file record")?;
        parse_file_summary_bytes(bytes)
    }
}

const TAPE_SORT_CHUNK_SIZE: usize = 262_144;
const TAPE_SORT_PROGRESS_INTERVAL: u64 = 4096;

struct TapeSortEntry {
    partition: u8,
    start_block: i64,
    length: i64,
    path: String,
    sequence: u64,
}

impl TapeSortEntry {
    fn compare_key(left: &Self, right: &Self) -> Ordering {
        let left_partition = u8::from(left.partition != 0);
        let right_partition = u8::from(right.partition != 0);
        left_partition
            .cmp(&right_partition)
            .then_with(|| left.start_block.cmp(&right.start_block))
            // VB's StringComparer.Ordinal compares UTF-16 code units.  Use
            // the same ordering here instead of Rust's Unicode scalar order
            // so the native fast path emits exactly the same tape order.
            .then_with(|| left.path.encode_utf16().cmp(right.path.encode_utf16()))
            .then_with(|| left.sequence.cmp(&right.sequence))
    }
}

struct TapeSortRunCursor {
    reader: BufReader<File>,
}

impl TapeSortRunCursor {
    fn new(path: &Path) -> Result<Self, String> {
        let file = File::open(path)
            .map_err(|error| format!("cannot open tape sort run {}: {error}", path.display()))?;
        Ok(Self {
            reader: BufReader::with_capacity(1024 * 1024, file),
        })
    }

    fn next(&mut self) -> Result<Option<TapeSortEntry>, String> {
        read_tape_sort_entry(&mut self.reader)
    }
}

struct TapeSortHeapItem {
    entry: TapeSortEntry,
    run_id: usize,
}

impl PartialEq for TapeSortHeapItem {
    fn eq(&self, other: &Self) -> bool {
        self.run_id == other.run_id
            && TapeSortEntry::compare_key(&self.entry, &other.entry) == Ordering::Equal
    }
}

impl Eq for TapeSortHeapItem {}

impl PartialOrd for TapeSortHeapItem {
    fn partial_cmp(&self, other: &Self) -> Option<Ordering> {
        Some(self.cmp(other))
    }
}

impl Ord for TapeSortHeapItem {
    fn cmp(&self, other: &Self) -> Ordering {
        // BinaryHeap is a max heap.  Reverse the key so the smallest tape
        // entry is returned first.
        TapeSortEntry::compare_key(&other.entry, &self.entry)
            .then_with(|| other.run_id.cmp(&self.run_id))
    }
}

fn write_tape_sort_u32<W: Write>(writer: &mut W, mut value: u32) -> Result<(), String> {
    // BinaryWriter.Write(String) uses a 7-bit encoded byte length.  Keeping
    // the run format compatible lets the existing VB output reader consume
    // the native result without another conversion pass.
    while value >= 0x80 {
        writer
            .write_all(&[(value as u8) | 0x80])
            .map_err(|error| error.to_string())?;
        value >>= 7;
    }
    writer
        .write_all(&[value as u8])
        .map_err(|error| error.to_string())
}

fn write_tape_sort_entry<W: Write>(writer: &mut W, entry: &TapeSortEntry) -> Result<(), String> {
    writer
        .write_all(&[entry.partition])
        .map_err(|error| error.to_string())?;
    writer
        .write_all(&entry.start_block.to_le_bytes())
        .map_err(|error| error.to_string())?;
    writer
        .write_all(&entry.length.to_le_bytes())
        .map_err(|error| error.to_string())?;
    let path = entry.path.as_bytes();
    let path_length =
        u32::try_from(path.len()).map_err(|_| invalid("tape sort path is too long to write"))?;
    write_tape_sort_u32(writer, path_length)?;
    writer.write_all(path).map_err(|error| error.to_string())
}

fn read_tape_sort_u32<R: Read>(reader: &mut R) -> Result<u32, String> {
    let mut result = 0u32;
    for shift in (0..35).step_by(7) {
        let mut byte = [0u8; 1];
        reader
            .read_exact(&mut byte)
            .map_err(|error| format!("cannot read tape sort path length: {error}"))?;
        let value = u32::from(byte[0] & 0x7f);
        result |= value
            .checked_shl(shift)
            .ok_or_else(|| invalid("invalid tape sort path length"))?;
        if byte[0] & 0x80 == 0 {
            return Ok(result);
        }
    }
    Err(invalid("invalid tape sort path length"))
}

fn read_tape_sort_entry<R: Read>(reader: &mut R) -> Result<Option<TapeSortEntry>, String> {
    let mut partition = [0u8; 1];
    match reader.read(&mut partition) {
        Ok(0) => return Ok(None),
        Ok(1) => {}
        Ok(_) => unreachable!("one-byte tape sort read returned more than one byte"),
        Err(error) => return Err(format!("cannot read tape sort partition: {error}")),
    }

    let mut block = [0u8; 8];
    reader
        .read_exact(&mut block)
        .map_err(|error| format!("cannot read tape sort start block: {error}"))?;
    let mut length = [0u8; 8];
    reader
        .read_exact(&mut length)
        .map_err(|error| format!("cannot read tape sort file length: {error}"))?;
    let path_length = read_tape_sort_u32(reader)? as usize;
    let mut path = vec![0u8; path_length];
    reader
        .read_exact(&mut path)
        .map_err(|error| format!("cannot read tape sort path: {error}"))?;
    let path = String::from_utf8(path).map_err(|_| invalid("tape sort path is not valid UTF-8"))?;

    Ok(Some(TapeSortEntry {
        partition: partition[0],
        start_block: i64::from_le_bytes(block),
        length: i64::from_le_bytes(length),
        path,
        // The run format does not need to persist a tie-breaker.  The heap
        // uses the run id for equal keys during the final merge.
        sequence: 0,
    }))
}

fn tape_sort_run_path(output_path: &Path) -> PathBuf {
    let sequence = MERGE_TEMP_SEQUENCE.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
    let timestamp = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|value| value.as_nanos())
        .unwrap_or_default();
    let name = format!(
        "ltfscopy_tape_sort_{}_{}_{}.run",
        std::process::id(),
        timestamp,
        sequence
    );
    output_path
        .parent()
        .unwrap_or_else(|| Path::new("."))
        .join(name)
}

fn merge_tape_sort_runs(run_paths: &[PathBuf], output_path: &Path) -> Result<(), String> {
    if run_paths.is_empty() {
        File::create(output_path)
            .map_err(|error| format!("cannot create tape sort output: {error}"))?;
        return Ok(());
    }

    if run_paths.len() == 1 {
        // The collection phase already produced a complete sorted run.  A
        // rename avoids reading and writing the entire result a second time.
        let _ = std::fs::remove_file(output_path);
        if std::fs::rename(&run_paths[0], output_path).is_ok() {
            return Ok(());
        }
    }

    let output = File::create(output_path)
        .map_err(|error| format!("cannot create tape sort output: {error}"))?;
    let mut writer = BufWriter::with_capacity(1024 * 1024, output);
    let mut cursors = Vec::with_capacity(run_paths.len());
    let mut heap = BinaryHeap::with_capacity(run_paths.len());
    for (run_id, path) in run_paths.iter().enumerate() {
        let mut cursor = TapeSortRunCursor::new(path)?;
        if let Some(entry) = cursor.next()? {
            heap.push(TapeSortHeapItem { entry, run_id });
        }
        cursors.push(cursor);
    }

    while let Some(item) = heap.pop() {
        write_tape_sort_entry(&mut writer, &item.entry)?;
        if let Some(entry) = cursors[item.run_id].next()? {
            heap.push(TapeSortHeapItem {
                entry,
                run_id: item.run_id,
            });
        }
    }
    writer.flush().map_err(|error| error.to_string())
}

struct TapeSortDirectoryFrame {
    path: String,
    next_file_offset: i64,
    remaining_files: u64,
    next_directory_offset: i64,
    remaining_directories: u64,
}

fn tape_sort_directory_frame(
    header: &StoreDirectoryHeader,
    path: String,
) -> Result<TapeSortDirectoryFrame, String> {
    Ok(TapeSortDirectoryFrame {
        path,
        next_file_offset: header.file_index_offset,
        remaining_files: u64::try_from(header.file_count)
            .map_err(|_| invalid("invalid tape sort file count"))?,
        next_directory_offset: header.directory_index_offset,
        remaining_directories: u64::try_from(header.directory_count)
            .map_err(|_| invalid("invalid tape sort directory count"))?,
    })
}

fn tape_sort_child_path(parent: &str, name: &str) -> String {
    // This deliberately mirrors Form1's existing `parent & name & "\\"`
    // behavior, including the trailing slash for an empty directory name.
    let mut result = String::with_capacity(parent.len() + name.len() + 1);
    result.push_str(parent);
    result.push_str(name);
    result.push('\\');
    result
}

fn tape_sort_file_path(parent: &str, name: &str) -> String {
    let mut result = String::with_capacity(parent.len() + name.len());
    result.push_str(parent);
    result.push_str(name);
    result
}

struct TapeSortCollectionState<'a> {
    entries: Vec<TapeSortEntry>,
    run_paths: Vec<PathBuf>,
    output_path: &'a Path,
    total: u64,
    processed: u64,
    selected_count: u64,
    partition_a_count: u64,
    partition_b_count: u64,
    callback: Option<LscStoreTapeSortProgressCallback>,
    user_data: *mut c_void,
}

impl<'a> TapeSortCollectionState<'a> {
    fn new(
        output_path: &'a Path,
        total: u64,
        callback: Option<LscStoreTapeSortProgressCallback>,
        user_data: *mut c_void,
    ) -> Self {
        Self {
            entries: Vec::with_capacity(TAPE_SORT_CHUNK_SIZE),
            run_paths: Vec::new(),
            output_path,
            total,
            processed: 0,
            selected_count: 0,
            partition_a_count: 0,
            partition_b_count: 0,
            callback,
            user_data,
        }
    }

    fn notify(&self) {
        if let Some(callback) = self.callback {
            if self.processed == 1
                || self.processed % TAPE_SORT_PROGRESS_INTERVAL == 0
                || self.processed >= self.total
            {
                // SAFETY: the callback and user data remain valid for this
                // synchronous native operation.
                unsafe { callback(self.processed, self.total, self.user_data) };
            }
        }
    }

    fn visit_file(
        &mut self,
        reader: &TapeSortReader,
        selection: &[u8],
        parent_path: &str,
        entry: LscStoreFileIndexEntry,
    ) -> Result<(), String> {
        self.processed = self.processed.saturating_add(1);
        self.notify();

        let selected = if entry.selection_index < 0 {
            true
        } else {
            usize::try_from(entry.selection_index)
                .ok()
                .and_then(|index| selection.get(index))
                .map(|value| *value != 0)
                .unwrap_or(true)
        };
        if !selected {
            return Ok(());
        }

        let summary = reader.read_file_summary(entry.record_offset, entry.record_length)?;
        let partition = u8::try_from(summary.info.partition)
            .map_err(|_| invalid("invalid tape sort partition label"))?;
        if partition == 0 {
            self.partition_a_count = self.partition_a_count.saturating_add(1);
        } else {
            self.partition_b_count = self.partition_b_count.saturating_add(1);
        }
        self.selected_count = self.selected_count.saturating_add(1);
        let sequence = self.selected_count;
        self.entries.push(TapeSortEntry {
            partition,
            start_block: summary.info.start_block,
            length: summary.info.length,
            path: tape_sort_file_path(parent_path, &summary.name),
            sequence,
        });
        if self.entries.len() >= TAPE_SORT_CHUNK_SIZE {
            self.flush_run()?;
        }
        Ok(())
    }

    fn flush_run(&mut self) -> Result<(), String> {
        if self.entries.is_empty() {
            return Ok(());
        }
        self.entries
            .par_sort_unstable_by(TapeSortEntry::compare_key);
        let path = tape_sort_run_path(self.output_path);
        let file = File::create(&path)
            .map_err(|error| format!("cannot create tape sort run {}: {error}", path.display()))?;
        self.run_paths.push(path);
        let mut writer = BufWriter::with_capacity(1024 * 1024, file);
        for entry in &self.entries {
            write_tape_sort_entry(&mut writer, entry)?;
        }
        writer.flush().map_err(|error| error.to_string())?;
        self.entries.clear();
        Ok(())
    }
}

fn collect_tape_sort_directory(
    reader: &TapeSortReader,
    directory_offset: i64,
    path: String,
    selection: &[u8],
    state: &mut TapeSortCollectionState<'_>,
) -> Result<(), String> {
    let header = reader.read_directory_header(directory_offset)?;
    let mut frames = vec![tape_sort_directory_frame(&header, path)?];
    while !frames.is_empty() {
        let top = frames.len() - 1;
        if frames[top].remaining_files > 0 {
            let index_offset = frames[top].next_file_offset;
            let parent_path = frames[top].path.clone();
            let entry = reader.read_file_index_entry(index_offset)?;
            frames[top].next_file_offset = entry.next_offset;
            frames[top].remaining_files -= 1;
            state.visit_file(reader, selection, &parent_path, entry)?;
            continue;
        }

        if frames[top].remaining_directories > 0 {
            let index_offset = frames[top].next_directory_offset;
            let entry = reader.read_directory_index_entry(index_offset)?;
            frames[top].next_directory_offset = entry.next_offset;
            frames[top].remaining_directories -= 1;
            let child_header = reader.read_directory_header(entry.record_offset)?;
            let child_name = reader.read_directory_name(&child_header)?;
            let child_path = tape_sort_child_path(&frames[top].path, &child_name);
            frames.push(tape_sort_directory_frame(&child_header, child_path)?);
            continue;
        }

        frames.pop();
    }
    Ok(())
}

fn tape_sort_total_files(
    reader: &TapeSortReader,
    root_file_index_offset: i64,
    root_file_count: u64,
    root_directory_index_offset: i64,
    root_directory_count: u64,
) -> Result<u64, String> {
    if root_file_count > 0 && root_file_index_offset < 0 {
        return Err(invalid("invalid tape sort root file index"));
    }
    if root_directory_count > 0 && root_directory_index_offset < 0 {
        return Err(invalid("invalid tape sort root directory index"));
    }

    let mut total = root_file_count;
    let mut index_offset = root_directory_index_offset;
    for _ in 0..root_directory_count {
        if index_offset < 0 {
            return Err(invalid("invalid tape sort root directory index chain"));
        }
        let entry = reader.read_directory_index_entry(index_offset)?;
        let header = reader.read_directory_header(entry.record_offset)?;
        let directory_total = u64::try_from(header.total_file_count)
            .map_err(|_| invalid("invalid tape sort directory file count"))?;
        total = total
            .checked_add(directory_total)
            .ok_or_else(|| invalid("tape sort file count overflow"))?;
        index_offset = entry.next_offset;
    }
    Ok(total)
}

fn collect_tape_sort_roots(
    reader: &TapeSortReader,
    root_file_index_offset: i64,
    root_file_count: u64,
    root_directory_index_offset: i64,
    root_directory_count: u64,
    selection: &[u8],
    state: &mut TapeSortCollectionState<'_>,
) -> Result<(), String> {
    let mut file_index_offset = root_file_index_offset;
    for _ in 0..root_file_count {
        if file_index_offset < 0 {
            return Err(invalid("invalid tape sort root file index chain"));
        }
        let entry = reader.read_file_index_entry(file_index_offset)?;
        file_index_offset = entry.next_offset;
        state.visit_file(reader, selection, "", entry)?;
    }

    let mut directory_index_offset = root_directory_index_offset;
    for _ in 0..root_directory_count {
        if directory_index_offset < 0 {
            return Err(invalid("invalid tape sort root directory index chain"));
        }
        let entry = reader.read_directory_index_entry(directory_index_offset)?;
        directory_index_offset = entry.next_offset;
        // The schema root directory is a container and is intentionally not
        // included in the generated path, matching Form1's existing logic.
        collect_tape_sort_directory(reader, entry.record_offset, String::new(), selection, state)?;
    }
    state.flush_run()
}

fn sort_tape_files(
    context: &StoreContext,
    root_file_index_offset: i64,
    root_file_count: u64,
    root_directory_index_offset: i64,
    root_directory_count: u64,
    selection_path: &Path,
    output_path: &Path,
    callback: Option<LscStoreTapeSortProgressCallback>,
    user_data: *mut c_void,
) -> Result<LscStoreTapeSortResult, String> {
    let selection = std::fs::read(selection_path).map_err(|error| {
        format!(
            "cannot read schema selection backing file {}: {error}",
            selection_path.display()
        )
    })?;
    let reader = TapeSortReader::new(context)?;
    let total = tape_sort_total_files(
        &reader,
        root_file_index_offset,
        root_file_count,
        root_directory_index_offset,
        root_directory_count,
    )?;
    let mut state = TapeSortCollectionState::new(output_path, total, callback, user_data);
    let result = (|| -> Result<LscStoreTapeSortResult, String> {
        collect_tape_sort_roots(
            &reader,
            root_file_index_offset,
            root_file_count,
            root_directory_index_offset,
            root_directory_count,
            &selection,
            &mut state,
        )?;
        state.notify();
        merge_tape_sort_runs(&state.run_paths, output_path)?;
        Ok(LscStoreTapeSortResult {
            struct_size: std::mem::size_of::<LscStoreTapeSortResult>() as u32,
            abi_version: 1,
            file_count: state.selected_count,
            partition_a_file_count: state.partition_a_count,
            partition_b_file_count: state.partition_b_count,
        })
    })();
    for path in &state.run_paths {
        let _ = std::fs::remove_file(path);
    }
    result
}

const DIRECTORY_SORT_MODE_LOGICAL: u32 = 1;
const DIRECTORY_SORT_MODE_CURRENT_CULTURE: u32 = 2;
const DIRECTORY_SORT_CHUNK_SIZE: usize = 65_536;
const DIRECTORY_SORT_PROGRESS_INTERVAL: u64 = 4096;

struct DirectorySortComparer {
    mode: u32,
    locale_name: Vec<u16>,
}

impl DirectorySortComparer {
    fn new(mode: u32, locale_name: String) -> Result<Self, String> {
        if mode != DIRECTORY_SORT_MODE_LOGICAL && mode != DIRECTORY_SORT_MODE_CURRENT_CULTURE {
            return Err(invalid("invalid directory sort mode"));
        }
        if locale_name.contains('\0') {
            return Err(invalid("invalid directory sort locale"));
        }
        let mut locale_name = locale_name.encode_utf16().collect::<Vec<_>>();
        // An empty locale name is the Windows invariant locale.  Keep a
        // non-null empty UTF-16 string so CompareStringEx can distinguish it
        // from a null system-default locale.
        locale_name.push(0);
        Ok(Self { mode, locale_name })
    }

    fn compare_names(&self, left: &[u16], right: &[u16]) -> Ordering {
        #[cfg(windows)]
        {
            if self.mode == DIRECTORY_SORT_MODE_LOGICAL {
                // SAFETY: both keys are valid, NUL-terminated UTF-16 strings
                // owned by their entries and remain alive for this call.
                return unsafe { StrCmpLogicalW(left.as_ptr(), right.as_ptr()) }.cmp(&0);
            }

            let locale = self.locale_name.as_ptr();
            let left_length = i32::try_from(left.len().saturating_sub(1)).unwrap_or(i32::MAX);
            let right_length = i32::try_from(right.len().saturating_sub(1)).unwrap_or(i32::MAX);
            // SAFETY: all pointers refer to valid UTF-16 buffers for the
            // specified lengths; the optional version/reserved arguments are
            // null as required by CompareStringEx.
            let result = unsafe {
                CompareStringEx(
                    locale,
                    0,
                    left.as_ptr(),
                    left_length,
                    right.as_ptr(),
                    right_length,
                    ptr::null_mut(),
                    ptr::null_mut(),
                    0,
                )
            };
            return match result {
                1 => Ordering::Less,
                2 => Ordering::Equal,
                3 => Ordering::Greater,
                _ => left.cmp(right),
            };
        }

        #[cfg(not(windows))]
        {
            let _ = self;
            left.cmp(right)
        }
    }

    fn compare(&self, left: &DirectorySortEntry, right: &DirectorySortEntry) -> Ordering {
        self.compare_names(&left.name_key, &right.name_key)
            .then_with(|| left.sequence.cmp(&right.sequence))
    }
}

struct DirectorySortEntry {
    record_offset: i64,
    record_length: i64,
    selection_index: i64,
    name: String,
    name_key: Vec<u16>,
    sequence: u64,
}

impl DirectorySortEntry {
    fn new(
        record_offset: i64,
        record_length: i64,
        selection_index: i64,
        name: String,
        sequence: u64,
    ) -> Self {
        let mut name_key = name.encode_utf16().collect::<Vec<_>>();
        name_key.push(0);
        Self {
            record_offset,
            record_length,
            selection_index,
            name,
            name_key,
            sequence,
        }
    }
}

fn directory_sort_run_path(output_path: &Path, is_file: bool) -> PathBuf {
    let sequence = MERGE_TEMP_SEQUENCE.fetch_add(1, AtomicOrdering::Relaxed);
    let timestamp = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|value| value.as_nanos())
        .unwrap_or_default();
    let kind = if is_file { "file" } else { "directory" };
    output_path
        .parent()
        .unwrap_or_else(|| Path::new("."))
        .join(format!(
            "ltfscopy_directory_sort_{}_{}_{}_{}.run",
            std::process::id(),
            timestamp,
            sequence,
            kind
        ))
}

fn write_directory_sort_entry<W: Write>(
    writer: &mut W,
    entry: &DirectorySortEntry,
) -> Result<(), String> {
    writer
        .write_all(&entry.record_offset.to_le_bytes())
        .map_err(|error| error.to_string())?;
    writer
        .write_all(&entry.record_length.to_le_bytes())
        .map_err(|error| error.to_string())?;
    writer
        .write_all(&entry.selection_index.to_le_bytes())
        .map_err(|error| error.to_string())?;
    writer
        .write_all(&entry.sequence.to_le_bytes())
        .map_err(|error| error.to_string())?;
    let name = entry.name.as_bytes();
    let name_length =
        u32::try_from(name.len()).map_err(|_| invalid("directory sort name is too long"))?;
    write_tape_sort_u32(writer, name_length)?;
    writer.write_all(name).map_err(|error| error.to_string())
}

fn read_directory_sort_i64<R: Read>(reader: &mut R) -> Result<Option<i64>, String> {
    let mut first = [0u8; 1];
    match reader.read(&mut first) {
        Ok(0) => return Ok(None),
        Ok(1) => {}
        Ok(_) => unreachable!("one-byte directory sort read returned more than one byte"),
        Err(error) => return Err(format!("cannot read directory sort entry: {error}")),
    }
    let mut bytes = [0u8; 8];
    bytes[0] = first[0];
    reader
        .read_exact(&mut bytes[1..])
        .map_err(|error| format!("cannot read directory sort record offset: {error}"))?;
    Ok(Some(i64::from_le_bytes(bytes)))
}

fn read_directory_sort_entry<R: Read>(
    reader: &mut R,
) -> Result<Option<DirectorySortEntry>, String> {
    let Some(record_offset) = read_directory_sort_i64(reader)? else {
        return Ok(None);
    };
    let mut record_length = [0u8; 8];
    reader
        .read_exact(&mut record_length)
        .map_err(|error| format!("cannot read directory sort record length: {error}"))?;
    let mut selection_index = [0u8; 8];
    reader
        .read_exact(&mut selection_index)
        .map_err(|error| format!("cannot read directory sort selection index: {error}"))?;
    let mut sequence = [0u8; 8];
    reader
        .read_exact(&mut sequence)
        .map_err(|error| format!("cannot read directory sort sequence: {error}"))?;
    let name_length = read_tape_sort_u32(reader)? as usize;
    let mut name = vec![0u8; name_length];
    reader
        .read_exact(&mut name)
        .map_err(|error| format!("cannot read directory sort name: {error}"))?;
    let name =
        String::from_utf8(name).map_err(|_| invalid("directory sort name is not valid UTF-8"))?;
    Ok(Some(DirectorySortEntry::new(
        record_offset,
        i64::from_le_bytes(record_length),
        i64::from_le_bytes(selection_index),
        name,
        u64::from_le_bytes(sequence),
    )))
}

struct DirectorySortRunCursor {
    reader: BufReader<File>,
}

impl DirectorySortRunCursor {
    fn new(path: &Path) -> Result<Self, String> {
        let file = File::open(path).map_err(|error| {
            format!("cannot open directory sort run {}: {error}", path.display())
        })?;
        Ok(Self {
            reader: BufReader::with_capacity(1024 * 1024, file),
        })
    }

    fn next(&mut self) -> Result<Option<DirectorySortEntry>, String> {
        read_directory_sort_entry(&mut self.reader)
    }
}

struct DirectorySortHeapItem {
    entry: DirectorySortEntry,
    run_id: usize,
    comparer: Arc<DirectorySortComparer>,
}

impl PartialEq for DirectorySortHeapItem {
    fn eq(&self, other: &Self) -> bool {
        self.run_id == other.run_id
            && self.comparer.compare(&self.entry, &other.entry) == Ordering::Equal
    }
}

impl Eq for DirectorySortHeapItem {}

impl PartialOrd for DirectorySortHeapItem {
    fn partial_cmp(&self, other: &Self) -> Option<Ordering> {
        Some(self.cmp(other))
    }
}

impl Ord for DirectorySortHeapItem {
    fn cmp(&self, other: &Self) -> Ordering {
        // BinaryHeap is a max heap.  Reverse the name ordering so the
        // smallest child is returned first.
        self.comparer
            .compare(&other.entry, &self.entry)
            .then_with(|| other.run_id.cmp(&self.run_id))
    }
}

struct DirectorySortProgress {
    processed: u64,
    total: u64,
    callback: Option<LscStoreDirectorySortProgressCallback>,
    user_data: *mut c_void,
}

impl DirectorySortProgress {
    fn new(
        total: u64,
        callback: Option<LscStoreDirectorySortProgressCallback>,
        user_data: *mut c_void,
    ) -> Self {
        Self {
            processed: 0,
            total,
            callback,
            user_data,
        }
    }

    fn visit(&mut self) {
        self.processed = self.processed.saturating_add(1);
        if let Some(callback) = self.callback {
            if self.processed == 1
                || self.processed % DIRECTORY_SORT_PROGRESS_INTERVAL == 0
                || self.processed >= self.total
            {
                // SAFETY: the callback and user data remain valid for this
                // synchronous native operation.
                unsafe { callback(self.processed, self.total, self.user_data) };
            }
        }
    }
}

fn flush_directory_sort_run(
    entries: &mut Vec<DirectorySortEntry>,
    output_path: &Path,
    is_file: bool,
    comparer: &DirectorySortComparer,
    run_paths: &mut Vec<PathBuf>,
) -> Result<(), String> {
    if entries.is_empty() {
        return Ok(());
    }
    entries.par_sort_unstable_by(|left, right| comparer.compare(left, right));
    let path = directory_sort_run_path(output_path, is_file);
    let result = (|| -> Result<(), String> {
        let file = File::create(&path).map_err(|error| {
            format!(
                "cannot create directory sort run {}: {error}",
                path.display()
            )
        })?;
        let mut writer = BufWriter::with_capacity(1024 * 1024, file);
        for entry in entries.iter() {
            write_directory_sort_entry(&mut writer, entry)?;
        }
        writer.flush().map_err(|error| error.to_string())
    })();
    if result.is_err() {
        let _ = std::fs::remove_file(&path);
    } else {
        run_paths.push(path);
        entries.clear();
    }
    result
}

fn collect_directory_sort_runs(
    reader: &TapeSortReader,
    first_index_offset: i64,
    item_count: u64,
    is_file: bool,
    output_path: &Path,
    comparer: &DirectorySortComparer,
    progress: &mut DirectorySortProgress,
    run_paths: &mut Vec<PathBuf>,
) -> Result<(), String> {
    if item_count == 0 {
        return Ok(());
    }
    if first_index_offset < 0 {
        return Err(invalid("invalid lazy schema index chain"));
    }

    let mut index_offset = first_index_offset;
    let mut entries = Vec::with_capacity(DIRECTORY_SORT_CHUNK_SIZE);
    for sequence in 0..item_count {
        if index_offset < 0 {
            return Err(invalid("invalid lazy schema index chain"));
        }
        let (next_offset, record_offset, record_length, selection_index, name) = if is_file {
            let entry = reader.read_file_index_entry(index_offset)?;
            let summary = reader.read_file_summary(entry.record_offset, entry.record_length)?;
            (
                entry.next_offset,
                entry.record_offset,
                entry.record_length,
                entry.selection_index,
                summary.name,
            )
        } else {
            let entry = reader.read_directory_index_entry(index_offset)?;
            let child_header = reader.read_directory_header(entry.record_offset)?;
            let name = reader.read_directory_name(&child_header)?;
            (
                entry.next_offset,
                entry.record_offset,
                0,
                entry.selection_index,
                name,
            )
        };
        progress.visit();
        entries.push(DirectorySortEntry::new(
            record_offset,
            record_length,
            selection_index,
            name,
            sequence,
        ));
        if entries.len() >= DIRECTORY_SORT_CHUNK_SIZE {
            flush_directory_sort_run(&mut entries, output_path, is_file, comparer, run_paths)?;
        }
        index_offset = next_offset;
    }
    flush_directory_sort_run(&mut entries, output_path, is_file, comparer, run_paths)
}

fn write_sorted_directory_index_entry<W: Write>(
    writer: &mut W,
    entry: &DirectorySortEntry,
    next_offset: i64,
    is_file: bool,
) -> Result<(), String> {
    writer
        .write_all(&next_offset.to_le_bytes())
        .map_err(|error| error.to_string())?;
    writer
        .write_all(&entry.record_offset.to_le_bytes())
        .map_err(|error| error.to_string())?;
    if is_file {
        writer
            .write_all(&entry.record_length.to_le_bytes())
            .map_err(|error| error.to_string())?;
    }
    writer
        .write_all(&entry.selection_index.to_le_bytes())
        .map_err(|error| error.to_string())
}

fn merge_directory_sort_runs(
    run_paths: &[PathBuf],
    output_path: &Path,
    target_index_offset: i64,
    item_count: u64,
    is_file: bool,
    comparer: Arc<DirectorySortComparer>,
) -> Result<(), String> {
    if target_index_offset < 0 {
        return Err(invalid("invalid lazy schema target index offset"));
    }
    let entry_size = if is_file {
        FILE_INDEX_ENTRY_SIZE
    } else {
        DIRECTORY_INDEX_ENTRY_SIZE
    };
    let output = File::create(output_path).map_err(|error| {
        format!(
            "cannot create sorted directory index {}: {error}",
            output_path.display()
        )
    })?;
    let mut writer = BufWriter::with_capacity(1024 * 1024, output);
    let mut cursors = Vec::with_capacity(run_paths.len());
    let mut heap = BinaryHeap::with_capacity(run_paths.len());
    for (run_id, path) in run_paths.iter().enumerate() {
        let mut cursor = DirectorySortRunCursor::new(path)?;
        if let Some(entry) = cursor.next()? {
            heap.push(DirectorySortHeapItem {
                entry,
                run_id,
                comparer: Arc::clone(&comparer),
            });
        }
        cursors.push(cursor);
    }

    let mut written = 0u64;
    while let Some(item) = heap.pop() {
        let next_entry = cursors[item.run_id].next()?;
        if let Some(entry) = next_entry {
            heap.push(DirectorySortHeapItem {
                entry,
                run_id: item.run_id,
                comparer: Arc::clone(&comparer),
            });
        }
        let next_offset = if heap.is_empty() {
            -1
        } else {
            let next_position = written
                .checked_add(1)
                .and_then(|value| value.checked_mul(entry_size as u64))
                .and_then(|value| i64::try_from(value).ok())
                .and_then(|value| target_index_offset.checked_add(value))
                .ok_or_else(|| invalid("lazy schema sorted index offset overflow"))?;
            next_position
        };
        write_sorted_directory_index_entry(&mut writer, &item.entry, next_offset, is_file)?;
        written = written
            .checked_add(1)
            .ok_or_else(|| invalid("lazy schema sorted index count overflow"))?;
    }
    if written != item_count {
        return Err(invalid("lazy schema sort lost an index entry"));
    }
    writer.flush().map_err(|error| error.to_string())
}

fn sort_directory_children(
    context: &StoreContext,
    directory_record_offset: i64,
    sort_mode: u32,
    locale_name: String,
    file_target_index_offset: i64,
    directory_target_index_offset: i64,
    file_output_path: &Path,
    directory_output_path: &Path,
    callback: Option<LscStoreDirectorySortProgressCallback>,
    user_data: *mut c_void,
) -> Result<LscStoreDirectorySortResult, String> {
    if directory_record_offset < 0 {
        return Err(invalid("invalid lazy schema directory record offset"));
    }
    let comparer = Arc::new(DirectorySortComparer::new(sort_mode, locale_name)?);
    let reader = TapeSortReader::new(context)?;
    let header = reader.read_directory_header(directory_record_offset)?;
    let file_count = u64::try_from(header.file_count)
        .map_err(|_| invalid("invalid lazy schema directory file count"))?;
    let directory_count = u64::try_from(header.directory_count)
        .map_err(|_| invalid("invalid lazy schema directory count"))?;
    let total = file_count
        .checked_add(directory_count)
        .ok_or_else(|| invalid("lazy schema directory child count overflow"))?;
    let mut progress = DirectorySortProgress::new(total, callback, user_data);
    let mut run_paths = Vec::new();
    let result = (|| -> Result<LscStoreDirectorySortResult, String> {
        collect_directory_sort_runs(
            &reader,
            header.file_index_offset,
            file_count,
            true,
            file_output_path,
            &comparer,
            &mut progress,
            &mut run_paths,
        )?;
        merge_directory_sort_runs(
            &run_paths,
            file_output_path,
            file_target_index_offset,
            file_count,
            true,
            Arc::clone(&comparer),
        )?;
        for path in run_paths.drain(..) {
            let _ = std::fs::remove_file(path);
        }

        collect_directory_sort_runs(
            &reader,
            header.directory_index_offset,
            directory_count,
            false,
            directory_output_path,
            &comparer,
            &mut progress,
            &mut run_paths,
        )?;
        merge_directory_sort_runs(
            &run_paths,
            directory_output_path,
            directory_target_index_offset,
            directory_count,
            false,
            Arc::clone(&comparer),
        )?;
        Ok(LscStoreDirectorySortResult {
            struct_size: std::mem::size_of::<LscStoreDirectorySortResult>() as u32,
            abi_version: 1,
            file_count,
            directory_count,
        })
    })();
    for path in &run_paths {
        let _ = std::fs::remove_file(path);
    }
    result
}

struct StoreSearchHit {
    result: LscStoreSearchResult,
    path: String,
    directory_path: String,
}

struct StoreSearchState<'a> {
    keyword: &'a str,
    folded_keyword: String,
    case_sensitive: bool,
    resume_kind: u32,
    resume_offset: i64,
    resume_active: bool,
    processed: u64,
    total: u64,
    callback: Option<LscStoreSearchProgressCallback>,
    user_data: *mut c_void,
}

impl StoreSearchState<'_> {
    fn visit(&mut self) {
        self.processed = self.processed.saturating_add(1);
        if let Some(callback) = self.callback {
            if self.processed == 1 || self.processed % 256 == 0 || self.processed >= self.total {
                // SAFETY: the callback and user data are supplied by the FFI caller and remain
                // valid for the duration of the synchronous search call.
                unsafe { callback(self.processed, self.total, self.user_data) };
            }
        }
    }

    fn contains(&self, value: &str) -> bool {
        if self.keyword.is_empty() {
            return true;
        }
        if self.case_sensitive {
            return value.contains(self.keyword);
        }
        if value.is_ascii() && self.keyword.is_ascii() {
            return value.to_ascii_lowercase().contains(&self.folded_keyword);
        }
        value.to_lowercase().contains(&self.folded_keyword)
    }

    fn is_resume(&self, kind: u32, record_offset: i64) -> bool {
        self.resume_active && self.resume_kind == kind && self.resume_offset == record_offset
    }
}

fn append_search_path(path: &mut String, name: &str) -> usize {
    let original_length = path.len();
    if name.is_empty() {
        return original_length;
    }
    if !path.is_empty() {
        path.push('\\');
    }
    path.push_str(name);
    original_length
}

fn search_directory_contents(
    reader: &mut StoreSearchReader<'_>,
    directory_offset: i64,
    header: StoreDirectoryHeader,
    path: &mut String,
    state: &mut StoreSearchState<'_>,
) -> Result<Option<StoreSearchHit>, String> {
    let mut file_index_offset = header.file_index_offset;
    for file_index in 0..header.file_count {
        if file_index_offset < 0 {
            return Err(invalid("invalid schema backing file index chain"));
        }
        let entry = reader.read_file_index_entry(file_index_offset)?;
        let name = reader.read_file_name(entry.record_offset, entry.record_length)?;
        state.visit();

        if state.resume_active {
            if state.is_resume(LSC_SEARCH_MATCH_FILE, entry.record_offset) {
                state.resume_active = false;
            }
            file_index_offset = entry.next_offset;
            continue;
        }

        if state.contains(&name) {
            let original_length = append_search_path(path, &name);
            let full_path = path.clone();
            path.truncate(original_length);
            return Ok(Some(StoreSearchHit {
                result: LscStoreSearchResult {
                    struct_size: std::mem::size_of::<LscStoreSearchResult>() as u32,
                    abi_version: 1,
                    found: 1,
                    match_kind: LSC_SEARCH_MATCH_FILE,
                    parent_directory_record_offset: directory_offset,
                    record_offset: entry.record_offset,
                    record_length: entry.record_length,
                    file_index,
                },
                path: full_path,
                directory_path: path.clone(),
            }));
        }
        file_index_offset = entry.next_offset;
    }

    let mut directory_index_offset = header.directory_index_offset;
    for _ in 0..header.directory_count {
        if directory_index_offset < 0 {
            return Err(invalid("invalid schema backing directory index chain"));
        }
        let entry = reader.read_directory_index_entry(directory_index_offset)?;
        let child_header = reader.read_directory_header(entry.record_offset)?;
        let child_name = reader.read_directory_name(&child_header)?;
        state.visit();

        let was_resuming = state.resume_active;
        if was_resuming && state.is_resume(LSC_SEARCH_MATCH_DIRECTORY, entry.record_offset) {
            state.resume_active = false;
        }

        let original_length = append_search_path(path, &child_name);
        if !was_resuming && state.contains(&child_name) {
            let full_path = path.clone();
            path.truncate(original_length);
            return Ok(Some(StoreSearchHit {
                result: LscStoreSearchResult {
                    struct_size: std::mem::size_of::<LscStoreSearchResult>() as u32,
                    abi_version: 1,
                    found: 1,
                    match_kind: LSC_SEARCH_MATCH_DIRECTORY,
                    parent_directory_record_offset: directory_offset,
                    record_offset: entry.record_offset,
                    record_length: 0,
                    file_index: -1,
                },
                path: full_path,
                directory_path: path.clone(),
            }));
        }

        let result =
            search_directory_contents(reader, entry.record_offset, child_header, path, state)?;
        path.truncate(original_length);
        if result.is_some() {
            return Ok(result);
        }
        directory_index_offset = entry.next_offset;
    }

    Ok(None)
}

fn search_store(
    context: &StoreContext,
    root_record_offset: i64,
    root_path: String,
    keyword: String,
    case_sensitive: bool,
    resume_kind: u32,
    resume_record_offset: i64,
    callback: Option<LscStoreSearchProgressCallback>,
    user_data: *mut c_void,
) -> Result<StoreSearchComputation, String> {
    if root_record_offset < 0 {
        return Err(invalid("invalid schema search root record offset"));
    }
    if resume_kind != 0
        && resume_kind != LSC_SEARCH_MATCH_DIRECTORY
        && resume_kind != LSC_SEARCH_MATCH_FILE
    {
        return Err(invalid("invalid schema search resume kind"));
    }

    let mut reader = StoreSearchReader::new(context)?;
    let root_header = reader.read_directory_header(root_record_offset)?;
    let total_file_count = u64::try_from(root_header.total_file_count)
        .map_err(|_| invalid("schema search file count is too large"))?;
    let total_directory_count = u64::try_from(root_header.total_directory_count)
        .map_err(|_| invalid("schema search directory count is too large"))?;
    let base_total = 1u64
        .checked_add(total_file_count)
        .and_then(|value| value.checked_add(total_directory_count))
        .ok_or_else(|| invalid("schema search entry count overflow"))?;
    let total = if resume_kind == 0 {
        base_total
    } else {
        base_total
            .checked_mul(2)
            .ok_or_else(|| invalid("schema search progress count overflow"))?
    };

    let folded_keyword = keyword.to_lowercase();
    let mut state = StoreSearchState {
        keyword: &keyword,
        folded_keyword,
        case_sensitive,
        resume_kind,
        resume_offset: resume_record_offset,
        resume_active: resume_kind != 0,
        processed: 0,
        total,
        callback,
        user_data,
    };
    let first_path = root_path.clone();
    let mut path = root_path;
    state.visit();
    let mut hit = search_directory_contents(
        &mut reader,
        root_record_offset,
        root_header,
        &mut path,
        &mut state,
    )?;

    if hit.is_none() && resume_kind != 0 {
        state.resume_active = false;
        path = first_path;
        state.visit();
        let root_header = reader.read_directory_header(root_record_offset)?;
        hit = search_directory_contents(
            &mut reader,
            root_record_offset,
            root_header,
            &mut path,
            &mut state,
        )?;
    }

    if let Some(hit) = hit {
        Ok(StoreSearchComputation {
            result: hit.result,
            path: hit.path,
            directory_path: hit.directory_path,
        })
    } else {
        Ok(StoreSearchComputation::default())
    }
}

#[derive(Default)]
struct StoreSearchComputation {
    result: LscStoreSearchResult,
    path: String,
    directory_path: String,
}

#[derive(Default)]
struct DirectoryValues {
    name: Option<String>,
    read_only: bool,
    creation_time: Option<String>,
    change_time: Option<String>,
    modify_time: Option<String>,
    access_time: Option<String>,
    backup_time: Option<String>,
    file_uid: i64,
}

struct SchemaParser<R: BufRead> {
    reader: Reader<R>,
    store: StoreOutput,
    metadata: SchemaMetadata,
    barcode: Option<String>,
}

struct ParsedMergeSource {
    store: StoreOutput,
    files: IndexChain,
    directories: IndexChain,
    total_files: i64,
    total_directories: i64,
}

struct MergeSourceResult {
    paths: [PathBuf; 5],
    files: IndexChain,
    directories: IndexChain,
    total_files: i64,
    total_directories: i64,
    file_records_length: i64,
    directory_records_length: i64,
    file_index_length: i64,
    directory_index_length: i64,
    selection_count: u64,
}

impl<R: BufRead> SchemaParser<R> {
    fn new(reader: Reader<R>, store: StoreOutput) -> Self {
        Self {
            reader,
            store,
            metadata: SchemaMetadata {
                public: LscSchemaMetadata {
                    struct_size: std::mem::size_of::<LscSchemaMetadata>() as u32,
                    abi_version: 1,
                    ..Default::default()
                },
                ..Default::default()
            },
            barcode: None,
        }
    }

    fn with_barcode(mut self, barcode: String) -> Self {
        self.barcode = Some(barcode);
        self
    }

    fn append_file_record(&mut self, value: BytesStart<'static>) -> Result<(i64, i64), String> {
        if let Some(barcode) = self.barcode.as_deref() {
            self.store
                .append_file_record_with_barcode(&mut self.reader, value, barcode)
        } else {
            self.store.append_file_record_raw(&mut self.reader, value)
        }
    }

    fn append_empty_file_record(&mut self) -> Result<(i64, i64), String> {
        self.store.append_empty_file(self.barcode.as_deref())
    }

    fn next_event(&mut self, buffer: &mut Vec<u8>) -> Result<Event<'static>, String> {
        self.reader
            .read_event_into(buffer)
            .map(|event| event.into_owned())
            .map_err(|error| error.to_string())
    }

    fn parse(mut self) -> Result<SchemaContext, String> {
        let mut buffer = Vec::new();
        let root = loop {
            match self.next_event(&mut buffer)? {
                Event::Start(value) => break value,
                Event::Empty(value) => {
                    if is_name(&value, "ltfsindex") {
                        break value;
                    }
                    return Err(invalid("schema root element was not found"));
                }
                Event::Decl(_) | Event::Comment(_) | Event::PI(_) => continue,
                Event::Text(value)
                    if value
                        .as_ref()
                        .chars()
                        .all(|character| character.is_ascii_whitespace()) =>
                {
                    continue;
                }
                Event::DocType(_) | Event::GeneralRef(_) => {
                    return Err(invalid("unsafe XML construct in schema"));
                }
                _ => return Err(invalid("schema root element was not found")),
            }
        };

        let mut root_files = IndexChain::default();
        let mut root_directories = IndexChain::default();
        if is_name(&root, "ltfsindex") && !root.is_empty() {
            loop {
                match self.next_event(&mut buffer)? {
                    Event::Start(value) => {
                        self.parse_index_child(value, &mut root_files, &mut root_directories)?
                    }
                    Event::Empty(value) => {
                        self.parse_index_empty(value, &mut root_files, &mut root_directories)?
                    }
                    Event::End(value) => {
                        if event_name_end(&value) == "ltfsindex" {
                            break;
                        }
                    }
                    Event::Text(_) | Event::CData(_) | Event::Comment(_) | Event::PI(_) => {}
                    Event::Decl(_) => {
                        return Err(invalid("XML declaration is not valid inside the schema"));
                    }
                    Event::DocType(_) | Event::GeneralRef(_) => {
                        return Err(invalid("unsafe XML construct in schema"));
                    }
                    Event::Eof => return Err(invalid("unexpected end of schema")),
                }
            }
        } else if is_name(&root, "directory") {
            let (offset, selection, _, _) = self.parse_directory(root)?;
            self.store
                .append_directory_index(&mut root_directories, offset, selection)?;
        } else if !is_name(&root, "ltfsindex") {
            return Err(invalid(
                "schema root element must be ltfsindex or directory",
            ));
        }

        self.store.finish()?;
        let public = self.metadata.public;
        let result = LscSchemaResult {
            struct_size: std::mem::size_of::<LscSchemaResult>() as u32,
            abi_version: 1,
            root_file_index_offset: root_files.first,
            root_file_count: root_files.count,
            root_directory_index_offset: root_directories.first,
            root_directory_count: root_directories.count,
            selection_count: self.store.selection_count,
        };
        Ok(SchemaContext {
            result,
            metadata: SchemaMetadata {
                public,
                ..self.metadata
            },
        })
    }

    fn parse_merge_contents(mut self) -> Result<ParsedMergeSource, String> {
        let mut buffer = Vec::new();
        let root = loop {
            match self.next_event(&mut buffer)? {
                Event::Start(value) => break value,
                Event::Empty(value) => {
                    if is_name(&value, "ltfsindex") || is_name(&value, "directory") {
                        break value;
                    }
                    return Err(invalid("schema root element was not found"));
                }
                Event::Decl(_) | Event::Comment(_) | Event::PI(_) => continue,
                Event::Text(value)
                    if value
                        .as_ref()
                        .chars()
                        .all(|character| character.is_ascii_whitespace()) =>
                {
                    continue;
                }
                Event::DocType(_) | Event::GeneralRef(_) => {
                    return Err(invalid("unsafe XML construct in schema"));
                }
                _ => return Err(invalid("schema root element was not found")),
            }
        };

        let mut state = DirectoryState {
            offset: -1,
            selection_index: -1,
            files: IndexChain::default(),
            directories: IndexChain::default(),
            total_file_count: 0,
            total_directory_count: 0,
        };

        if is_name(&root, "directory") {
            self.parse_directory_contents(root, &mut state)?;
        } else if is_name(&root, "ltfsindex") && !root.is_empty() {
            // The merge operation has historically flattened the first
            // schema directory into the generated search root.  Keep that
            // behavior while avoiding creation of an intermediate directory
            // record that would immediately be discarded.
            let mut buffer = Vec::new();
            loop {
                match self.next_event(&mut buffer)? {
                    Event::Start(value) if is_name(&value, "directory") => {
                        self.parse_directory_contents(value, &mut state)?;
                        break;
                    }
                    Event::Empty(value) if is_name(&value, "directory") => {
                        self.parse_directory_contents(value, &mut state)?;
                        break;
                    }
                    Event::End(value) if event_name_end(&value) == "ltfsindex" => break,
                    Event::Decl(_) => {
                        return Err(invalid("XML declaration is not valid inside the schema"));
                    }
                    Event::DocType(_) | Event::GeneralRef(_) => {
                        return Err(invalid("unsafe XML construct in schema"));
                    }
                    Event::Eof => return Err(invalid("unexpected end of schema")),
                    _ => {}
                }
            }
        } else {
            return Err(invalid(
                "schema root element must be ltfsindex or directory",
            ));
        }

        Ok(ParsedMergeSource {
            store: self.store,
            files: state.files,
            directories: state.directories,
            total_files: state.total_file_count,
            total_directories: state.total_directory_count,
        })
    }

    fn parse_directory_contents(
        &mut self,
        start: BytesStart<'static>,
        state: &mut DirectoryState,
    ) -> Result<(), String> {
        if start.is_empty() {
            return Ok(());
        }
        let mut buffer = Vec::new();
        loop {
            match self.next_event(&mut buffer)? {
                Event::Start(value) => match event_name_start(&value).as_str() {
                    "contents" | "_directory" | "_file" => {
                        self.parse_directory_container(value, state)?
                    }
                    "file" => {
                        self.append_child_file(value, state)?;
                    }
                    "directory" => {
                        self.append_child_directory(value, state)?;
                    }
                    _ => self.skip_element(value)?,
                },
                Event::Empty(value) => match event_name_start(&value).as_str() {
                    "contents" | "_directory" | "_file" => {}
                    "file" => self.append_empty_file(state)?,
                    "directory" => self.append_empty_directory(state)?,
                    _ => {}
                },
                Event::End(value) if event_name_end(&value) == "directory" => break,
                Event::Decl(_) => {
                    return Err(invalid("XML declaration is not valid inside a directory"));
                }
                Event::DocType(_) | Event::GeneralRef(_) => {
                    return Err(invalid("unsafe XML construct in schema"));
                }
                Event::Eof => return Err(invalid("unexpected end of directory")),
                Event::End(_)
                | Event::Text(_)
                | Event::CData(_)
                | Event::Comment(_)
                | Event::PI(_) => {}
            }
        }
        Ok(())
    }

    fn parse_index_child(
        &mut self,
        value: BytesStart<'static>,
        files: &mut IndexChain,
        directories: &mut IndexChain,
    ) -> Result<(), String> {
        let name = event_name_start(&value);
        match name.as_str() {
            "creator" => {
                self.metadata.creator = decode_schema_value(self.read_text(value)?);
                self.metadata.public.present_mask |= PRESENT_CREATOR;
            }
            "volumeuuid" => {
                self.metadata.volume_uuid = decode_schema_value(self.read_text(value)?);
                self.metadata.public.present_mask |= PRESENT_VOLUME_UUID;
            }
            "generationnumber" => {
                let text = self.read_text(value)?;
                if let Ok(number) = text.trim().parse::<u64>() {
                    self.metadata.public.generation_number = number;
                    self.metadata.public.present_mask |= PRESENT_GENERATION_NUMBER;
                }
            }
            "updatetime" => {
                self.metadata.update_time = decode_schema_value(self.read_text(value)?);
                self.metadata.public.present_mask |= PRESENT_UPDATE_TIME;
            }
            "location" => {
                let (partition, start_block) = self.parse_location(value)?;
                self.metadata.public.location_partition = partition;
                self.metadata.public.location_start_block = start_block;
                self.metadata.public.present_mask |= PRESENT_LOCATION;
            }
            "previousgenerationlocation" => {
                let (partition, start_block) = self.parse_location(value)?;
                self.metadata.public.previous_location_partition = partition;
                self.metadata.public.previous_location_start_block = start_block;
                self.metadata.public.present_mask |= PRESENT_PREVIOUS_LOCATION;
            }
            "allowpolicyupdate" => {
                if let Some(value) = parse_bool(self.read_text(value)?.as_str()) {
                    self.metadata.public.allow_policy_update = u32::from(value);
                    self.metadata.public.present_mask |= PRESENT_ALLOW_POLICY_UPDATE;
                }
            }
            "dataplacementpolicy" => {
                self.metadata.public.data_placement_policy = 1;
                self.metadata.public.present_mask |= PRESENT_DATA_PLACEMENT_POLICY;
                self.skip_element(value)?;
            }
            "volumelockstate" => {
                let text = self.read_text(value)?.trim().to_ascii_lowercase();
                self.metadata.public.volume_lock_state = match text.as_str() {
                    "locked" => 1,
                    "permlocked" => 2,
                    _ => 0,
                };
                self.metadata.public.present_mask |= PRESENT_VOLUME_LOCK_STATE;
            }
            "highestfileuid" => {
                if let Ok(number) = self.read_text(value)?.trim().parse::<i64>() {
                    self.metadata.public.highest_file_uid = number;
                    self.metadata.public.present_mask |= PRESENT_HIGHEST_FILE_UID;
                }
            }
            "file" => {
                let (offset, length) = self.append_file_record(value)?;
                let selection = self.store.allocate_selection()?;
                self.store
                    .append_file_index(files, offset, length, selection)?;
            }
            "directory" => {
                let (offset, selection, total_files, total_directories) =
                    self.parse_directory(value)?;
                self.store
                    .append_directory_index(directories, offset, selection)?;
                let _ = (total_files, total_directories);
            }
            "_file" | "_directory" | "contents" => {
                self.parse_root_container(value, files, directories)?;
            }
            _ => self.skip_element(value)?,
        }
        Ok(())
    }

    fn parse_index_empty(
        &mut self,
        value: BytesStart<'static>,
        files: &mut IndexChain,
        directories: &mut IndexChain,
    ) -> Result<(), String> {
        let name = event_name_start(&value);
        match name.as_str() {
            "dataplacementpolicy" => {
                self.metadata.public.data_placement_policy = 1;
                self.metadata.public.present_mask |= PRESENT_DATA_PLACEMENT_POLICY;
            }
            "file" => {
                let offset = self
                    .store
                    .file_records
                    .seek(SeekFrom::End(0))
                    .map_err(|error| error.to_string())? as i64;
                let selection = self.store.allocate_selection()?;
                self.store
                    .file_records
                    .write_all(b"<file/>")
                    .map_err(|error| error.to_string())?;
                self.store.append_file_index(files, offset, 7, selection)?;
            }
            "directory" => {
                let (offset, selection, _, _) =
                    self.write_empty_directory(DirectoryValues::default())?;
                self.store
                    .append_directory_index(directories, offset, selection)?;
            }
            _ => {}
        }
        Ok(())
    }

    fn parse_root_container(
        &mut self,
        start: BytesStart<'static>,
        files: &mut IndexChain,
        directories: &mut IndexChain,
    ) -> Result<(), String> {
        if start.is_empty() {
            return Ok(());
        }
        let expected = event_name_start(&start);
        let mut buffer = Vec::new();
        loop {
            match self.next_event(&mut buffer)? {
                Event::Start(value) => {
                    let name = event_name_start(&value);
                    match name.as_str() {
                        "file" => {
                            let (offset, length) = self.append_file_record(value)?;
                            let selection = self.store.allocate_selection()?;
                            self.store
                                .append_file_index(files, offset, length, selection)?;
                        }
                        "directory" => {
                            let (offset, selection, _, _) = self.parse_directory(value)?;
                            self.store
                                .append_directory_index(directories, offset, selection)?;
                        }
                        "_file" | "_directory" | "contents" => {
                            self.parse_root_container(value, files, directories)?
                        }
                        _ => self.skip_element(value)?,
                    }
                }
                Event::Empty(value) => self.parse_index_empty(value, files, directories)?,
                Event::End(value) => {
                    if event_name_end(&value) == expected {
                        break;
                    }
                }
                Event::Text(_) | Event::CData(_) | Event::Comment(_) | Event::PI(_) => {}
                Event::Decl(_) => {
                    return Err(invalid("XML declaration is not valid inside the schema"));
                }
                Event::DocType(_) | Event::GeneralRef(_) => {
                    return Err(invalid("unsafe XML construct in schema"));
                }
                Event::Eof => return Err(invalid("unexpected end of schema container")),
            }
        }
        Ok(())
    }

    fn parse_directory(
        &mut self,
        start: BytesStart<'static>,
    ) -> Result<(i64, i64, i64, i64), String> {
        let mut state = self.store.begin_directory()?;
        let mut values = DirectoryValues::default();
        if !start.is_empty() {
            let mut buffer = Vec::new();
            loop {
                match self.next_event(&mut buffer)? {
                    Event::Start(value) => {
                        let name = event_name_start(&value);
                        match name.as_str() {
                            "name" => {
                                values.name = Some(decode_schema_value(self.read_text(value)?))
                            }
                            "readonly" => {
                                values.read_only =
                                    parse_bool(self.read_text(value)?.as_str()).unwrap_or(false)
                            }
                            "creationtime" => {
                                values.creation_time =
                                    Some(decode_schema_value(self.read_text(value)?))
                            }
                            "changetime" => {
                                values.change_time =
                                    Some(decode_schema_value(self.read_text(value)?))
                            }
                            "modifytime" => {
                                values.modify_time =
                                    Some(decode_schema_value(self.read_text(value)?))
                            }
                            "accesstime" => {
                                values.access_time =
                                    Some(decode_schema_value(self.read_text(value)?))
                            }
                            "backuptime" => {
                                values.backup_time =
                                    Some(decode_schema_value(self.read_text(value)?))
                            }
                            "fileuid" => {
                                values.file_uid = self.read_text(value)?.trim().parse().unwrap_or(0)
                            }
                            "contents" | "_directory" | "_file" => {
                                self.parse_directory_container(value, &mut state)?
                            }
                            "file" => self.append_child_file(value, &mut state)?,
                            "directory" => self.append_child_directory(value, &mut state)?,
                            _ => self.skip_element(value)?,
                        }
                    }
                    Event::Empty(value) => match event_name_start(&value).as_str() {
                        "contents" | "_directory" | "_file" => {}
                        "file" => self.append_empty_file(&mut state)?,
                        "directory" => self.append_empty_directory(&mut state)?,
                        _ => {}
                    },
                    Event::End(value) => {
                        if event_name_end(&value) == "directory" {
                            break;
                        }
                    }
                    Event::Text(_) | Event::CData(_) | Event::Comment(_) | Event::PI(_) => {}
                    Event::Decl(_) => {
                        return Err(invalid("XML declaration is not valid inside a directory"));
                    }
                    Event::DocType(_) | Event::GeneralRef(_) => {
                        return Err(invalid("unsafe XML construct in schema"));
                    }
                    Event::Eof => return Err(invalid("unexpected end of directory")),
                }
            }
        }
        self.store.finish_directory(&state, &values)?;
        Ok((
            state.offset,
            state.selection_index,
            state.total_file_count,
            state.total_directory_count,
        ))
    }

    fn parse_directory_container(
        &mut self,
        start: BytesStart<'static>,
        state: &mut DirectoryState,
    ) -> Result<(), String> {
        if start.is_empty() {
            return Ok(());
        }
        let expected = event_name_start(&start);
        let mut buffer = Vec::new();
        loop {
            match self.next_event(&mut buffer)? {
                Event::Start(value) => match event_name_start(&value).as_str() {
                    "file" => self.append_child_file(value, state)?,
                    "directory" => self.append_child_directory(value, state)?,
                    "contents" | "_directory" | "_file" => {
                        self.parse_directory_container(value, state)?
                    }
                    _ => self.skip_element(value)?,
                },
                Event::Empty(value) => match event_name_start(&value).as_str() {
                    "file" => self.append_empty_file(state)?,
                    "directory" => self.append_empty_directory(state)?,
                    "contents" | "_directory" | "_file" => {}
                    _ => {}
                },
                Event::End(value) => {
                    if event_name_end(&value) == expected {
                        break;
                    }
                }
                Event::Text(_) | Event::CData(_) | Event::Comment(_) | Event::PI(_) => {}
                Event::Decl(_) => {
                    return Err(invalid("XML declaration is not valid inside a directory"));
                }
                Event::DocType(_) | Event::GeneralRef(_) => {
                    return Err(invalid("unsafe XML construct in schema"));
                }
                Event::Eof => return Err(invalid("unexpected end of directory container")),
            }
        }
        Ok(())
    }

    fn append_child_file(
        &mut self,
        value: BytesStart<'static>,
        state: &mut DirectoryState,
    ) -> Result<(), String> {
        let (offset, length) = self.append_file_record(value)?;
        let selection = self.store.allocate_selection()?;
        self.store
            .append_file_index(&mut state.files, offset, length, selection)?;
        state.total_file_count += 1;
        Ok(())
    }

    fn append_empty_file(&mut self, state: &mut DirectoryState) -> Result<(), String> {
        let (offset, length) = self.append_empty_file_record()?;
        let selection = self.store.allocate_selection()?;
        self.store
            .append_file_index(&mut state.files, offset, length, selection)?;
        state.total_file_count += 1;
        Ok(())
    }

    fn append_child_directory(
        &mut self,
        value: BytesStart<'static>,
        state: &mut DirectoryState,
    ) -> Result<(), String> {
        let (offset, selection, total_files, total_directories) = self.parse_directory(value)?;
        self.store
            .append_directory_index(&mut state.directories, offset, selection)?;
        state.total_file_count += total_files;
        state.total_directory_count += 1 + total_directories;
        Ok(())
    }

    fn append_empty_directory(&mut self, state: &mut DirectoryState) -> Result<(), String> {
        let (offset, selection, total_files, total_directories) =
            self.write_empty_directory(DirectoryValues::default())?;
        self.store
            .append_directory_index(&mut state.directories, offset, selection)?;
        state.total_file_count += total_files;
        state.total_directory_count += 1 + total_directories;
        Ok(())
    }

    fn write_empty_directory(
        &mut self,
        values: DirectoryValues,
    ) -> Result<(i64, i64, i64, i64), String> {
        let state = self.store.begin_directory()?;
        self.store.finish_directory(&state, &values)?;
        Ok((state.offset, state.selection_index, 0, 0))
    }

    fn parse_location(&mut self, start: BytesStart<'static>) -> Result<(u32, u64), String> {
        if start.is_empty() {
            return Ok((0, 0));
        }
        let expected = event_name_start(&start);
        let mut partition = 0;
        let mut start_block = 0;
        let mut buffer = Vec::new();
        loop {
            match self.next_event(&mut buffer)? {
                Event::Start(value) => match event_name_start(&value).as_str() {
                    "partition" => {
                        partition = parse_partition(&self.read_text(value)?).unwrap_or(0)
                    }
                    "startblock" => {
                        start_block = self.read_text(value)?.trim().parse().unwrap_or(0)
                    }
                    _ => self.skip_element(value)?,
                },
                Event::Empty(_) => {}
                Event::End(value) if event_name_end(&value) == expected => break,
                Event::End(_)
                | Event::Text(_)
                | Event::CData(_)
                | Event::Comment(_)
                | Event::PI(_) => {}
                Event::Decl(_) => {
                    return Err(invalid("XML declaration is not valid inside a location"));
                }
                Event::DocType(_) | Event::GeneralRef(_) => {
                    return Err(invalid("unsafe XML construct in schema"));
                }
                Event::Eof => return Err(invalid("unexpected end of location")),
            }
        }
        Ok((partition, start_block))
    }

    fn read_text(&mut self, start: BytesStart<'static>) -> Result<String, String> {
        if start.is_empty() {
            return Ok(String::new());
        }
        let expected = event_name_start(&start);
        let mut result = String::new();
        let mut buffer = Vec::new();
        loop {
            match self.next_event(&mut buffer)? {
                Event::Text(value) => result.push_str(&decode_text(value.as_ref())?),
                Event::CData(value) => result.push_str(&decode_cdata(value.as_ref())?),
                Event::GeneralRef(value) => result.push_str(&decode_general_ref(value.as_ref())?),
                Event::Start(value) => self.skip_element(value)?,
                Event::Empty(_) | Event::Comment(_) | Event::PI(_) => {}
                Event::End(value) if event_name_end(&value) == expected => break,
                Event::End(_) => {}
                Event::Decl(_) => return Err(invalid("XML declaration is not valid inside text")),
                Event::DocType(_) => return Err(invalid("unsafe XML construct in schema")),
                Event::Eof => return Err(invalid("unexpected end of XML text")),
            }
        }
        Ok(result)
    }

    fn skip_element(&mut self, start: BytesStart<'static>) -> Result<(), String> {
        if start.is_empty() {
            return Ok(());
        }
        let mut depth = 1i32;
        let mut buffer = Vec::new();
        while depth > 0 {
            match self.next_event(&mut buffer)? {
                Event::Start(_) => depth += 1,
                Event::End(_) => depth -= 1,
                Event::Empty(_)
                | Event::Text(_)
                | Event::CData(_)
                | Event::Comment(_)
                | Event::PI(_) => {}
                Event::Decl(_) => {
                    return Err(invalid("XML declaration is not valid inside an element"));
                }
                Event::DocType(_) | Event::GeneralRef(_) => {
                    return Err(invalid("unsafe XML construct in schema"));
                }
                Event::Eof => return Err(invalid("unexpected end of XML element")),
            }
        }
        Ok(())
    }
}

struct FileParser<R: BufRead> {
    reader: Reader<R>,
}

impl<R: BufRead> FileParser<R> {
    fn new(reader: Reader<R>) -> Self {
        Self { reader }
    }

    fn next_event(&mut self, buffer: &mut Vec<u8>) -> Result<Event<'static>, String> {
        self.reader
            .read_event_into(buffer)
            .map(|event| event.into_owned())
            .map_err(|error| error.to_string())
    }

    fn parse(mut self) -> Result<FileData, String> {
        let mut buffer = Vec::new();
        let root = loop {
            match self.next_event(&mut buffer)? {
                Event::Start(value) => break value,
                Event::Empty(value) if is_name(&value, "file") => {
                    return Ok(FileData {
                        open_for_write: true,
                        ..Default::default()
                    });
                }
                Event::Decl(_) | Event::Comment(_) | Event::PI(_) => continue,
                Event::Text(value)
                    if value
                        .as_ref()
                        .chars()
                        .all(|character| character.is_ascii_whitespace()) =>
                {
                    continue;
                }
                Event::DocType(_) | Event::GeneralRef(_) => {
                    return Err(invalid("unsafe XML construct in file record"));
                }
                _ => return Err(invalid("file record root element was not found")),
            }
        };
        if !is_name(&root, "file") {
            return Err(invalid("file record root element must be file"));
        }

        let mut result = FileData {
            open_for_write: true,
            ..Default::default()
        };
        if root.is_empty() {
            return Ok(result);
        }
        loop {
            match self.next_event(&mut buffer)? {
                Event::Start(value) => self.parse_child(value, &mut result)?,
                Event::Empty(value) => self.parse_empty_child(value, &mut result)?,
                Event::End(value) if event_name_end(&value) == "file" => break,
                Event::End(_)
                | Event::Text(_)
                | Event::CData(_)
                | Event::Comment(_)
                | Event::PI(_) => {}
                Event::Decl(_) => {
                    return Err(invalid("XML declaration is not valid inside a file record"));
                }
                Event::DocType(_) | Event::GeneralRef(_) => {
                    return Err(invalid("unsafe XML construct in file record"));
                }
                Event::Eof => return Err(invalid("unexpected end of file record")),
            }
        }
        Ok(result)
    }

    // Search and statistics only need one scalar from a lazy file record.
    // Keeping these paths separate from `parse` avoids allocating xattrs,
    // extents, and all of the other file metadata for every visited entry.
    fn parse_name(mut self) -> Result<String, String> {
        let mut buffer = Vec::new();
        let root = loop {
            match self.next_event(&mut buffer)? {
                Event::Start(value) => break value,
                Event::Empty(value) if is_name(&value, "file") => return Ok(String::new()),
                Event::Decl(_) | Event::Comment(_) | Event::PI(_) => continue,
                Event::Text(value)
                    if value
                        .as_ref()
                        .chars()
                        .all(|character| character.is_ascii_whitespace()) =>
                {
                    continue;
                }
                Event::DocType(_) | Event::GeneralRef(_) => {
                    return Err(invalid("unsafe XML construct in file record"));
                }
                _ => return Err(invalid("file record root element was not found")),
            }
        };
        if !is_name(&root, "file") {
            return Err(invalid("file record root element must be file"));
        }
        if root.is_empty() {
            return Ok(String::new());
        }

        loop {
            match self.next_event(&mut buffer)? {
                Event::Start(value) if is_name(&value, "name") => {
                    return Ok(decode_schema_value(self.read_text(value)?));
                }
                Event::Start(value) => self.skip_element(value)?,
                Event::Empty(value) if is_name(&value, "name") => return Ok(String::new()),
                Event::Empty(_)
                | Event::Text(_)
                | Event::CData(_)
                | Event::Comment(_)
                | Event::PI(_) => {}
                Event::End(value) if event_name_end(&value) == "file" => return Ok(String::new()),
                Event::End(_) => {}
                Event::Decl(_) => {
                    return Err(invalid("XML declaration is not valid inside a file record"));
                }
                Event::DocType(_) | Event::GeneralRef(_) => {
                    return Err(invalid("unsafe XML construct in file record"));
                }
                Event::Eof => return Err(invalid("unexpected end of file record")),
            }
        }
    }

    fn parse_length(mut self) -> Result<i64, String> {
        let mut buffer = Vec::new();
        let root = loop {
            match self.next_event(&mut buffer)? {
                Event::Start(value) => break value,
                Event::Empty(value) if is_name(&value, "file") => return Ok(0),
                Event::Decl(_) | Event::Comment(_) | Event::PI(_) => continue,
                Event::Text(value)
                    if value
                        .as_ref()
                        .chars()
                        .all(|character| character.is_ascii_whitespace()) =>
                {
                    continue;
                }
                Event::DocType(_) | Event::GeneralRef(_) => {
                    return Err(invalid("unsafe XML construct in file record"));
                }
                _ => return Err(invalid("file record root element was not found")),
            }
        };
        if !is_name(&root, "file") {
            return Err(invalid("file record root element must be file"));
        }
        if root.is_empty() {
            return Ok(0);
        }

        loop {
            match self.next_event(&mut buffer)? {
                Event::Start(value) if is_name(&value, "length") => {
                    return Ok(self.read_text(value)?.trim().parse().unwrap_or(0));
                }
                Event::Start(value) => self.skip_element(value)?,
                Event::Empty(value) if is_name(&value, "length") => return Ok(0),
                Event::Empty(_)
                | Event::Text(_)
                | Event::CData(_)
                | Event::Comment(_)
                | Event::PI(_) => {}
                Event::End(value) if event_name_end(&value) == "file" => return Ok(0),
                Event::End(_) => {}
                Event::Decl(_) => {
                    return Err(invalid("XML declaration is not valid inside a file record"));
                }
                Event::DocType(_) | Event::GeneralRef(_) => {
                    return Err(invalid("unsafe XML construct in file record"));
                }
                Event::Eof => return Err(invalid("unexpected end of file record")),
            }
        }
    }

    fn parse_summary(mut self) -> Result<ParsedFileSummary, String> {
        let mut result = ParsedFileSummary {
            info: LscStoreFileSummary {
                struct_size: std::mem::size_of::<LscStoreFileSummary>() as u32,
                abi_version: 1,
                ..Default::default()
            },
            ..Default::default()
        };
        let mut buffer = Vec::new();
        let root = loop {
            match self.next_event(&mut buffer)? {
                Event::Start(value) => break value,
                Event::Empty(value) if is_name(&value, "file") => return Ok(result),
                Event::Decl(_) | Event::Comment(_) | Event::PI(_) => continue,
                Event::Text(value)
                    if value
                        .as_ref()
                        .chars()
                        .all(|character| character.is_ascii_whitespace()) =>
                {
                    continue;
                }
                Event::DocType(_) | Event::GeneralRef(_) => {
                    return Err(invalid("unsafe XML construct in file record"));
                }
                _ => return Err(invalid("file record root element was not found")),
            }
        };
        if !is_name(&root, "file") {
            return Err(invalid("file record root element must be file"));
        }
        if root.is_empty() {
            return Ok(result);
        }

        loop {
            match self.next_event(&mut buffer)? {
                Event::Start(value) if is_name(&value, "name") => {
                    result.name = decode_schema_value(self.read_text(value)?);
                }
                Event::Start(value) if is_name(&value, "length") => {
                    result.info.length = self.read_text(value)?.trim().parse().unwrap_or(0);
                }
                Event::Start(value) if is_name(&value, "extentinfo") => {
                    self.parse_first_extent(value, &mut result.info)?;
                }
                Event::Start(value) => self.skip_element(value)?,
                Event::Empty(value) if is_name(&value, "name") => result.name.clear(),
                Event::Empty(value) if is_name(&value, "length") => result.info.length = 0,
                Event::Empty(_) => {}
                Event::End(value) if event_name_end(&value) == "file" => break,
                Event::End(_)
                | Event::Text(_)
                | Event::CData(_)
                | Event::Comment(_)
                | Event::PI(_) => {}
                Event::Decl(_) => {
                    return Err(invalid("XML declaration is not valid inside a file record"));
                }
                Event::DocType(_) | Event::GeneralRef(_) => {
                    return Err(invalid("unsafe XML construct in file record"));
                }
                Event::Eof => return Err(invalid("unexpected end of file record")),
            }
        }
        Ok(result)
    }

    fn parse_first_extent(
        &mut self,
        start: BytesStart<'static>,
        result: &mut LscStoreFileSummary,
    ) -> Result<(), String> {
        if start.is_empty() {
            return Ok(());
        }
        let expected = event_name_start(&start);
        let mut found = false;
        let mut buffer = Vec::new();
        loop {
            match self.next_event(&mut buffer)? {
                Event::Start(value) if is_name(&value, "extent") => {
                    if !found {
                        let extent = self.parse_extent(value)?;
                        result.partition = extent.partition;
                        result.start_block = extent.start_block;
                        result.byte_offset = extent.byte_offset;
                        result.byte_count = extent.byte_count;
                        found = true;
                    } else {
                        // Tape sorting only needs the first extent.  The
                        // remaining extents still have to be consumed to
                        // reach the end of extentinfo, but parsing all of
                        // their numeric fields is unnecessary.
                        self.skip_element(value)?;
                    }
                }
                Event::Empty(value) if is_name(&value, "extent") => {
                    found = true;
                }
                Event::Start(value) => self.skip_element(value)?,
                Event::Empty(_) => {}
                Event::End(value) if event_name_end(&value) == expected => break,
                Event::End(_)
                | Event::Text(_)
                | Event::CData(_)
                | Event::Comment(_)
                | Event::PI(_) => {}
                Event::Decl(_) => {
                    return Err(invalid("XML declaration is not valid inside extents"));
                }
                Event::DocType(_) | Event::GeneralRef(_) => {
                    return Err(invalid("unsafe XML construct in extents"));
                }
                Event::Eof => return Err(invalid("unexpected end of extents")),
            }
        }
        Ok(())
    }

    fn parse_child(
        &mut self,
        value: BytesStart<'static>,
        result: &mut FileData,
    ) -> Result<(), String> {
        match event_name_start(&value).as_str() {
            "name" => result.name = decode_schema_value(self.read_text(value)?),
            "length" => result.length = self.read_text(value)?.trim().parse().unwrap_or(0),
            "readonly" => {
                result.read_only = parse_bool(self.read_text(value)?.as_str()).unwrap_or(false)
            }
            "openforwrite" => {
                result.open_for_write = parse_bool(self.read_text(value)?.as_str()).unwrap_or(true)
            }
            "creationtime" => {
                result.creation_time = Some(decode_schema_value(self.read_text(value)?))
            }
            "changetime" => result.change_time = Some(decode_schema_value(self.read_text(value)?)),
            "modifytime" => result.modify_time = Some(decode_schema_value(self.read_text(value)?)),
            "accesstime" => result.access_time = Some(decode_schema_value(self.read_text(value)?)),
            "backuptime" => result.backup_time = Some(decode_schema_value(self.read_text(value)?)),
            "fileuid" => result.file_uid = self.read_text(value)?.trim().parse().unwrap_or(0),
            "symlink" => result.symlink = Some(decode_schema_value(self.read_text(value)?)),
            "extendedattributes" => self.parse_xattrs(value, result)?,
            "extentinfo" => self.parse_extents(value, result)?,
            _ => self.skip_element(value)?,
        }
        Ok(())
    }

    fn parse_empty_child(
        &mut self,
        value: BytesStart<'static>,
        result: &mut FileData,
    ) -> Result<(), String> {
        match event_name_start(&value).as_str() {
            "extendedattributes" | "extentinfo" => {}
            "symlink" => result.symlink = Some(String::new()),
            _ => {}
        }
        Ok(())
    }

    fn parse_xattrs(
        &mut self,
        start: BytesStart<'static>,
        result: &mut FileData,
    ) -> Result<(), String> {
        if start.is_empty() {
            return Ok(());
        }
        let expected = event_name_start(&start);
        let mut buffer = Vec::new();
        loop {
            match self.next_event(&mut buffer)? {
                Event::Start(value) if is_name(&value, "xattr") => {
                    result.xattrs.push(self.parse_xattr(value)?)
                }
                Event::Start(value) => self.skip_element(value)?,
                Event::Empty(value) if is_name(&value, "xattr") => {
                    result.xattrs.push((String::new(), String::new()))
                }
                Event::Empty(_) => {}
                Event::End(value) if event_name_end(&value) == expected => break,
                Event::End(_)
                | Event::Text(_)
                | Event::CData(_)
                | Event::Comment(_)
                | Event::PI(_) => {}
                Event::Decl(_) => {
                    return Err(invalid("XML declaration is not valid inside attributes"));
                }
                Event::DocType(_) | Event::GeneralRef(_) => {
                    return Err(invalid("unsafe XML construct in file record"));
                }
                Event::Eof => return Err(invalid("unexpected end of attributes")),
            }
        }
        Ok(())
    }

    fn parse_xattr(&mut self, start: BytesStart<'static>) -> Result<(String, String), String> {
        if start.is_empty() {
            return Ok((String::new(), String::new()));
        }
        let mut key = String::new();
        let mut value = String::new();
        let mut buffer = Vec::new();
        loop {
            match self.next_event(&mut buffer)? {
                Event::Start(child) if is_name(&child, "key") => {
                    key = decode_schema_value(self.read_text(child)?)
                }
                Event::Start(child) if is_name(&child, "value") => {
                    value = decode_schema_value(self.read_text(child)?)
                }
                Event::Start(child) => self.skip_element(child)?,
                Event::Empty(_) => {}
                Event::End(child) if event_name_end(&child) == "xattr" => break,
                Event::End(_)
                | Event::Text(_)
                | Event::CData(_)
                | Event::Comment(_)
                | Event::PI(_) => {}
                Event::Decl(_) => {
                    return Err(invalid("XML declaration is not valid inside an attribute"));
                }
                Event::DocType(_) | Event::GeneralRef(_) => {
                    return Err(invalid("unsafe XML construct in file record"));
                }
                Event::Eof => return Err(invalid("unexpected end of xattr")),
            }
        }
        Ok((key, value))
    }

    fn parse_extents(
        &mut self,
        start: BytesStart<'static>,
        result: &mut FileData,
    ) -> Result<(), String> {
        if start.is_empty() {
            return Ok(());
        }
        let expected = event_name_start(&start);
        let mut buffer = Vec::new();
        loop {
            match self.next_event(&mut buffer)? {
                Event::Start(value) if is_name(&value, "extent") => {
                    result.extents.push(self.parse_extent(value)?)
                }
                Event::Start(value) => self.skip_element(value)?,
                Event::Empty(value) if is_name(&value, "extent") => {
                    result.extents.push(LscExtent::default())
                }
                Event::Empty(_) => {}
                Event::End(value) if event_name_end(&value) == expected => break,
                Event::End(_)
                | Event::Text(_)
                | Event::CData(_)
                | Event::Comment(_)
                | Event::PI(_) => {}
                Event::Decl(_) => {
                    return Err(invalid("XML declaration is not valid inside extents"));
                }
                Event::DocType(_) | Event::GeneralRef(_) => {
                    return Err(invalid("unsafe XML construct in file record"));
                }
                Event::Eof => return Err(invalid("unexpected end of extents")),
            }
        }
        Ok(())
    }

    fn parse_extent(&mut self, start: BytesStart<'static>) -> Result<LscExtent, String> {
        if start.is_empty() {
            return Ok(LscExtent::default());
        }
        let mut result = LscExtent::default();
        let mut buffer = Vec::new();
        loop {
            match self.next_event(&mut buffer)? {
                Event::Start(value) => {
                    let name = event_name_start(&value);
                    let text = self.read_text(value)?;
                    match name.as_str() {
                        "fileoffset" => result.file_offset = text.trim().parse().unwrap_or(0),
                        "partition" => result.partition = parse_partition(&text).unwrap_or(0),
                        "startblock" => result.start_block = text.trim().parse().unwrap_or(0),
                        "byteoffset" => result.byte_offset = text.trim().parse().unwrap_or(0),
                        "bytecount" => result.byte_count = text.trim().parse().unwrap_or(0),
                        _ => {}
                    }
                }
                Event::Empty(_) => {}
                Event::End(value) if event_name_end(&value) == "extent" => break,
                Event::End(_)
                | Event::Text(_)
                | Event::CData(_)
                | Event::Comment(_)
                | Event::PI(_) => {}
                Event::Decl(_) => {
                    return Err(invalid("XML declaration is not valid inside an extent"));
                }
                Event::DocType(_) | Event::GeneralRef(_) => {
                    return Err(invalid("unsafe XML construct in file record"));
                }
                Event::Eof => return Err(invalid("unexpected end of extent")),
            }
        }
        Ok(result)
    }

    fn read_text(&mut self, start: BytesStart<'static>) -> Result<String, String> {
        if start.is_empty() {
            return Ok(String::new());
        }
        let expected = event_name_start(&start);
        let mut result = String::new();
        let mut buffer = Vec::new();
        loop {
            match self.next_event(&mut buffer)? {
                Event::Text(value) => result.push_str(&decode_text(value.as_ref())?),
                Event::CData(value) => result.push_str(&decode_cdata(value.as_ref())?),
                Event::GeneralRef(value) => result.push_str(&decode_general_ref(value.as_ref())?),
                Event::Start(value) => self.skip_element(value)?,
                Event::Empty(_) | Event::Comment(_) | Event::PI(_) => {}
                Event::End(value) if event_name_end(&value) == expected => break,
                Event::End(_) => {}
                Event::Decl(_) => return Err(invalid("XML declaration is not valid inside text")),
                Event::DocType(_) => return Err(invalid("unsafe XML construct in file record")),
                Event::Eof => return Err(invalid("unexpected end of XML text")),
            }
        }
        Ok(result)
    }

    fn skip_element(&mut self, start: BytesStart<'static>) -> Result<(), String> {
        if start.is_empty() {
            return Ok(());
        }
        let mut depth = 1i32;
        let mut buffer = Vec::new();
        while depth > 0 {
            match self.next_event(&mut buffer)? {
                Event::Start(_) => depth += 1,
                Event::End(_) => depth -= 1,
                Event::Empty(_)
                | Event::Text(_)
                | Event::CData(_)
                | Event::Comment(_)
                | Event::PI(_) => {}
                Event::Decl(_) => {
                    return Err(invalid("XML declaration is not valid inside an element"));
                }
                Event::DocType(_) => {
                    return Err(invalid("unsafe XML construct in file record"));
                }
                Event::GeneralRef(value) => {
                    // A normal XML entity such as `&amp;` may occur inside
                    // a skipped text field (most commonly a file name).  It
                    // is safe after the same whitelist validation used by
                    // `read_text`.
                    decode_general_ref(value.as_ref())?;
                }
                Event::Eof => return Err(invalid("unexpected end of XML element")),
            }
        }
        Ok(())
    }
}

fn parse_file_bytes(bytes: &[u8]) -> Result<FileData, String> {
    FileParser::new(Reader::from_reader(Cursor::new(bytes))).parse()
}

fn parse_file_name_bytes(bytes: &[u8]) -> Result<String, String> {
    FileParser::new(Reader::from_reader(Cursor::new(bytes))).parse_name()
}

fn parse_file_length_bytes(bytes: &[u8]) -> Result<i64, String> {
    FileParser::new(Reader::from_reader(Cursor::new(bytes))).parse_length()
}

#[derive(Default)]
struct ParsedFileSummary {
    name: String,
    info: LscStoreFileSummary,
}

fn parse_file_summary_bytes(bytes: &[u8]) -> Result<ParsedFileSummary, String> {
    FileParser::new(Reader::from_reader(Cursor::new(bytes))).parse_summary()
}

// HPLTFS consumes the index as a line-oriented XML document.  Keep the
// indentation width at zero so every element starts on its own line without
// changing the compact, schema-compatible tag layout.
fn new_xml_writer<W: Write>(inner: W) -> Writer<W> {
    Writer::new_with_indent(inner, b' ', 0)
}

fn file_data_from_input(input: &LscFileInput) -> Result<FileData, String> {
    let text = |slice: LscUtf16Slice| unsafe { utf16_string(slice.ptr, slice.len) };
    let optional = |slice: LscUtf16Slice| -> Result<Option<String>, String> {
        if slice.ptr.is_null() && slice.len == 0 {
            Ok(None)
        } else {
            text(slice).map(Some)
        }
    };
    let mut result = FileData {
        name: text(input.name)?,
        length: input.length,
        read_only: input.read_only != 0,
        open_for_write: input.open_for_write != 0,
        creation_time: optional(input.creation_time)?,
        change_time: optional(input.change_time)?,
        modify_time: optional(input.modify_time)?,
        access_time: optional(input.access_time)?,
        backup_time: optional(input.backup_time)?,
        file_uid: input.file_uid,
        symlink: optional(input.symlink)?,
        ..Default::default()
    };
    if input.xattr_count > 0 {
        if input.xattrs.is_null() {
            return Err(invalid("xattr input pointer is null"));
        }
        // SAFETY: the FFI contract supplies an array with xattr_count entries.
        let entries = unsafe { slice::from_raw_parts(input.xattrs, input.xattr_count as usize) };
        for entry in entries {
            result.xattrs.push((text(entry.key)?, text(entry.value)?));
        }
    }
    if input.extent_count > 0 {
        if input.extents.is_null() {
            return Err(invalid("extent input pointer is null"));
        }
        // SAFETY: the FFI contract supplies an array with extent_count entries.
        let entries = unsafe { slice::from_raw_parts(input.extents, input.extent_count as usize) };
        result.extents = entries
            .iter()
            .map(|value| LscExtent {
                file_offset: value.file_offset,
                partition: value.partition,
                reserved: 0,
                start_block: value.start_block,
                byte_offset: value.byte_offset,
                byte_count: value.byte_count,
            })
            .collect();
    }
    Ok(result)
}

fn write_xml_text<W: Write>(writer: &mut Writer<W>, value: &str) -> Result<(), String> {
    let escaped = escape(value);
    writer
        .write_event(Event::Text(BytesText::from_escaped(escaped)))
        .map_err(|error| error.to_string())
}

fn write_xml_element<W: Write>(
    writer: &mut Writer<W>,
    name: &str,
    value: Option<&str>,
) -> Result<(), String> {
    writer
        .write_event(Event::Start(BytesStart::new(name)))
        .map_err(|error| error.to_string())?;
    if let Some(value) = value {
        write_xml_text(writer, value)?;
    }
    writer
        .write_event(Event::End(BytesEnd::new(name)))
        .map_err(|error| error.to_string())
}

fn serialize_file(value: &FileData) -> Result<Vec<u8>, String> {
    let mut writer = Writer::new(Vec::with_capacity(1024));
    writer
        .write_event(Event::Start(BytesStart::new("file")))
        .map_err(|error| error.to_string())?;
    write_xml_element(&mut writer, "name", Some(&value.name))?;
    write_xml_element(&mut writer, "length", Some(&value.length.to_string()))?;
    write_xml_element(
        &mut writer,
        "readonly",
        Some(if value.read_only { "true" } else { "false" }),
    )?;
    write_xml_element(
        &mut writer,
        "openforwrite",
        Some(if value.open_for_write {
            "true"
        } else {
            "false"
        }),
    )?;
    write_xml_element(&mut writer, "creationtime", value.creation_time.as_deref())?;
    write_xml_element(&mut writer, "changetime", value.change_time.as_deref())?;
    write_xml_element(&mut writer, "modifytime", value.modify_time.as_deref())?;
    write_xml_element(&mut writer, "accesstime", value.access_time.as_deref())?;
    write_xml_element(&mut writer, "backuptime", value.backup_time.as_deref())?;
    write_xml_element(&mut writer, "fileuid", Some(&value.file_uid.to_string()))?;
    writer
        .write_event(Event::Start(BytesStart::new("extendedattributes")))
        .map_err(|error| error.to_string())?;
    for (key, attr_value) in &value.xattrs {
        writer
            .write_event(Event::Start(BytesStart::new("xattr")))
            .map_err(|error| error.to_string())?;
        write_xml_element(&mut writer, "key", Some(key))?;
        write_xml_element(&mut writer, "value", Some(attr_value))?;
        writer
            .write_event(Event::End(BytesEnd::new("xattr")))
            .map_err(|error| error.to_string())?;
    }
    writer
        .write_event(Event::End(BytesEnd::new("extendedattributes")))
        .map_err(|error| error.to_string())?;
    if let Some(symlink) = value.symlink.as_deref() {
        write_xml_element(&mut writer, "symlink", Some(symlink))?;
    }
    writer
        .write_event(Event::Start(BytesStart::new("extentinfo")))
        .map_err(|error| error.to_string())?;
    for extent in &value.extents {
        writer
            .write_event(Event::Start(BytesStart::new("extent")))
            .map_err(|error| error.to_string())?;
        write_xml_element(
            &mut writer,
            "fileoffset",
            Some(&extent.file_offset.to_string()),
        )?;
        write_xml_element(
            &mut writer,
            "partition",
            Some(if extent.partition == 0 { "a" } else { "b" }),
        )?;
        write_xml_element(
            &mut writer,
            "startblock",
            Some(&extent.start_block.to_string()),
        )?;
        write_xml_element(
            &mut writer,
            "byteoffset",
            Some(&extent.byte_offset.to_string()),
        )?;
        write_xml_element(
            &mut writer,
            "bytecount",
            Some(&extent.byte_count.to_string()),
        )?;
        writer
            .write_event(Event::End(BytesEnd::new("extent")))
            .map_err(|error| error.to_string())?;
    }
    writer
        .write_event(Event::End(BytesEnd::new("extentinfo")))
        .map_err(|error| error.to_string())?;
    writer
        .write_event(Event::End(BytesEnd::new("file")))
        .map_err(|error| error.to_string())?;
    Ok(writer.into_inner())
}

fn write_fragment<W: Write>(writer: &mut Writer<W>, bytes: &[u8]) -> Result<(), String> {
    let mut reader = Reader::from_reader(Cursor::new(bytes));
    let mut buffer = Vec::new();
    loop {
        let event = reader
            .read_event_into(&mut buffer)
            .map_err(|error| error.to_string())?
            .into_owned();
        buffer.clear();
        match event {
            Event::Start(value) => writer.write_event(Event::Start(value)),
            Event::Empty(value) => writer.write_event(Event::Empty(value)),
            Event::End(value) => writer.write_event(Event::End(value)),
            Event::Text(value) => writer.write_event(Event::Text(value)),
            Event::CData(value) => writer.write_event(Event::CData(value)),
            Event::Comment(_) | Event::PI(_) => Ok(()),
            Event::Decl(_) => return Err(invalid("XML declaration in fragment")),
            Event::DocType(_) => return Err(invalid("unsafe XML construct in fragment")),
            Event::GeneralRef(value) => {
                decode_general_ref(value.as_ref())?;
                writer.write_event(Event::GeneralRef(value))
            }
            Event::Eof => break,
        }
        .map_err(|error| error.to_string())?;
    }
    Ok(())
}

fn schema_context_from_file(
    input_path: String,
    paths: [PathBuf; 5],
) -> Result<Box<SchemaContext>, String> {
    let input = File::open(&input_path)
        .map_err(|error| format!("cannot open schema {}: {error}", input_path))?;
    let reader = Reader::from_reader(BufReader::with_capacity(64 * 1024, input));
    let parser = SchemaParser::new(reader, StoreOutput::new(&paths)?);
    match parser.parse() {
        Ok(context) => Ok(Box::new(context)),
        Err(error) => {
            for path in &paths {
                let _ = std::fs::remove_file(path);
            }
            Err(error)
        }
    }
}

fn merge_source_paths(index: usize, temp_directory: &Path) -> [PathBuf; 5] {
    let sequence = MERGE_TEMP_SEQUENCE.fetch_add(1, AtomicOrdering::Relaxed);
    let timestamp = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|value| value.as_nanos())
        .unwrap_or_default();
    let prefix = format!(
        "ltfscopy_merge_{}_{}_{}_{}",
        std::process::id(),
        timestamp,
        sequence,
        index
    );
    [
        temp_directory.join(format!("{prefix}_file_records.tmp")),
        temp_directory.join(format!("{prefix}_directory_records.tmp")),
        temp_directory.join(format!("{prefix}_file_index.tmp")),
        temp_directory.join(format!("{prefix}_directory_index.tmp")),
        temp_directory.join(format!("{prefix}_selection.tmp")),
    ]
}

fn cleanup_merge_source_paths(paths: &[PathBuf; 5]) {
    for path in paths {
        let _ = std::fs::remove_file(path);
    }
}

fn parse_merge_source(
    index: usize,
    input_path: PathBuf,
    temp_directory: &Path,
) -> Result<MergeSourceResult, String> {
    let paths = merge_source_paths(index, temp_directory);
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        let input = File::open(&input_path)
            .map_err(|error| format!("cannot open schema {}: {error}", input_path.display()))?;
        let reader = Reader::from_reader(BufReader::with_capacity(64 * 1024, input));
        let barcode = input_path
            .file_stem()
            .map(|value| value.to_string_lossy().into_owned())
            .unwrap_or_default();
        let parser = SchemaParser::new(reader, StoreOutput::new(&paths)?).with_barcode(barcode);
        let ParsedMergeSource {
            mut store,
            files,
            directories,
            total_files,
            total_directories,
        } = parser.parse_merge_contents().map_err(|error| {
            format!(
                "cannot parse merge source {}: {error}",
                input_path.display()
            )
        })?;
        store.finish()?;
        let source = MergeSourceResult {
            paths: paths.clone(),
            files,
            directories,
            total_files,
            total_directories,
            file_records_length: store.file_records_position,
            directory_records_length: store.directory_records_position,
            file_index_length: i64::try_from(store.file_index_data.len())
                .map_err(|_| invalid("merge source file index is too large"))?,
            directory_index_length: i64::try_from(store.directory_index_data.len())
                .map_err(|_| invalid("merge source directory index is too large"))?,
            selection_count: store.selection_count,
        };
        drop(store);
        Ok(source)
    }));

    match result {
        Ok(result) => {
            if result.is_err() {
                cleanup_merge_source_paths(&paths);
            }
            result
        }
        Err(_) => {
            cleanup_merge_source_paths(&paths);
            Err(invalid("merge worker panicked while parsing schema"))
        }
    }
}

fn parse_merge_sources(
    input_paths: Vec<PathBuf>,
    temp_directory: PathBuf,
) -> Result<Vec<MergeSourceResult>, String> {
    let input_count = input_paths.len();
    if input_count == 0 {
        return Ok(Vec::new());
    }

    let worker_count = std::thread::available_parallelism()
        .map(|value| value.get())
        .unwrap_or(1)
        .min(4)
        .min(input_count);
    let queue = Arc::new(Mutex::new(
        input_paths.into_iter().enumerate().collect::<Vec<_>>(),
    ));
    let (sender, receiver) = std::sync::mpsc::channel();
    let mut sources: Vec<Option<MergeSourceResult>> = (0..input_count).map(|_| None).collect();

    let worker_result = std::thread::scope(|scope| -> Result<Option<String>, String> {
        for _ in 0..worker_count {
            let queue = Arc::clone(&queue);
            let sender = sender.clone();
            let temp_directory = temp_directory.clone();
            scope.spawn(move || {
                loop {
                    let task = match queue.lock() {
                        Ok(mut queue) => queue.pop(),
                        Err(_) => break,
                    };
                    let Some((index, input_path)) = task else {
                        break;
                    };
                    let result = parse_merge_source(index, input_path, &temp_directory);
                    if sender.send((index, result)).is_err() {
                        break;
                    }
                }
            });
        }
        drop(sender);

        let mut first_error = None;
        for _ in 0..input_count {
            let (index, result) = receiver
                .recv()
                .map_err(|_| invalid("merge workers stopped unexpectedly"))?;
            if index >= input_count {
                return Err(invalid("merge worker returned an invalid schema index"));
            }
            match result {
                Ok(source) => sources[index] = Some(source),
                Err(error) => {
                    if first_error.is_none() {
                        first_error = Some(error);
                    }
                }
            }
        }
        Ok(first_error)
    });

    if let Err(error) = worker_result {
        for source in sources.iter().filter_map(Option::as_ref) {
            cleanup_merge_source_paths(&source.paths);
        }
        return Err(error);
    }
    if let Some(error) = worker_result.ok().flatten() {
        for source in sources.iter().filter_map(Option::as_ref) {
            cleanup_merge_source_paths(&source.paths);
        }
        return Err(error);
    }

    sources
        .into_iter()
        .enumerate()
        .map(|(index, source)| {
            source.ok_or_else(|| invalid(format!("merge source {index} was not produced")))
        })
        .collect()
}

fn schema_context_from_files(
    input_paths: Vec<PathBuf>,
    root_name: String,
    paths: [PathBuf; 5],
) -> Result<Box<SchemaContext>, String> {
    let mut store = StoreOutput::new(&paths)?;
    let mut root_state = store.begin_directory()?;

    let temp_directory = paths[0]
        .parent()
        .filter(|value| !value.as_os_str().is_empty())
        .unwrap_or_else(|| Path::new("."))
        .to_path_buf();
    let sources = parse_merge_sources(input_paths, temp_directory)?;
    let result = (|| -> Result<Box<SchemaContext>, String> {
        for source in &sources {
            let (files, directories) = store.append_merge_source(source)?;
            store.join_file_chains(&mut root_state.files, &files)?;
            store.join_directory_chains(&mut root_state.directories, &directories)?;
            root_state.total_file_count = root_state
                .total_file_count
                .checked_add(source.total_files)
                .ok_or_else(|| invalid("too many files in merged schema"))?;
            root_state.total_directory_count = root_state
                .total_directory_count
                .checked_add(source.total_directories)
                .ok_or_else(|| invalid("too many directories in merged schema"))?;
        }

        store.normalize_merge_directories(&mut root_state, &paths[1])?;
        let highest_file_uid = store
            .next_file_uid
            .checked_sub(1)
            .ok_or_else(|| invalid("merged schema file UID underflow"))?;

        let root_values = DirectoryValues {
            name: Some(root_name),
            ..Default::default()
        };
        store.finish_directory(&root_state, &root_values)?;

        let mut root_directories = IndexChain::default();
        store.append_directory_index(
            &mut root_directories,
            root_state.offset,
            root_state.selection_index,
        )?;
        store.finish()?;

        let result = LscSchemaResult {
            struct_size: std::mem::size_of::<LscSchemaResult>() as u32,
            abi_version: 1,
            root_file_index_offset: -1,
            root_file_count: 0,
            root_directory_index_offset: root_directories.first,
            root_directory_count: root_directories.count,
            selection_count: store.selection_count,
        };
        let metadata = SchemaMetadata {
            public: LscSchemaMetadata {
                struct_size: std::mem::size_of::<LscSchemaMetadata>() as u32,
                abi_version: 1,
                present_mask: PRESENT_HIGHEST_FILE_UID,
                highest_file_uid,
                ..Default::default()
            },
            ..Default::default()
        };
        Ok(Box::new(SchemaContext { result, metadata }))
    })();
    for source in &sources {
        cleanup_merge_source_paths(&source.paths);
    }
    result
}

fn get_schema_string(context: &SchemaContext, field: u32) -> Option<&str> {
    match field {
        1 => Some(context.metadata.creator.as_str()),
        2 => Some(context.metadata.volume_uuid.as_str()),
        3 => Some(context.metadata.update_time.as_str()),
        _ => None,
    }
}

fn file_string(data: &FileData, field: u32) -> Option<&str> {
    match field {
        1 => Some(data.name.as_str()),
        2 => data.creation_time.as_deref(),
        3 => data.change_time.as_deref(),
        4 => data.modify_time.as_deref(),
        5 => data.access_time.as_deref(),
        6 => data.backup_time.as_deref(),
        7 => data.symlink.as_deref(),
        _ => None,
    }
}

pub struct FileContext {
    data: FileData,
}

pub struct SchemaWriter {
    writer: Option<Writer<BufWriter<File>>>,
}

fn writer_mut(writer: &mut SchemaWriter) -> Result<&mut Writer<BufWriter<File>>, String> {
    writer
        .writer
        .as_mut()
        .ok_or_else(|| invalid("schema writer is already finished"))
}

fn copy_store_file_record_to_writer(
    writer: &mut Writer<BufWriter<File>>,
    store: &StoreContext,
    record_offset: i64,
    record_length: u64,
) -> Result<(), String> {
    if record_offset < 0 || record_length == 0 {
        return Err(invalid("invalid schema store file record range"));
    }
    let length = usize::try_from(record_length)
        .map_err(|_| invalid("schema store file record is too large"))?;
    let offset = u64::try_from(record_offset)
        .map_err(|_| invalid("invalid schema store file record offset"))?;
    let mut source = store
        .file_records
        .lock()
        .map_err(|_| invalid("file records backing file is poisoned"))?;
    source
        .seek(SeekFrom::Start(offset))
        .map_err(|error| format!("cannot seek file records backing file: {error}"))?;

    let mut bytes = vec![0u8; length];
    source
        .read_exact(&mut bytes)
        .map_err(|error| format!("cannot read file records backing file: {error}"))?;
    write_fragment(writer, &bytes)
}

fn writer_name(ptr: *const u16, len: u32) -> Result<String, String> {
    unsafe { utf16_string(ptr, len) }
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn lsc_last_error(
    buffer: *mut u16,
    capacity: u32,
    required: *mut u32,
) -> i32 {
    let value = last_error()
        .lock()
        .map(|value| value.clone())
        .unwrap_or_else(|_| "unknown ltfscopy_schema error".to_owned());
    copy_utf16(&value, buffer, capacity, required)
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn lsc_parse_schema_file(
    input_path: *const u16,
    input_path_len: u32,
    file_records_path: *const u16,
    file_records_path_len: u32,
    directory_records_path: *const u16,
    directory_records_path_len: u32,
    file_index_path: *const u16,
    file_index_path_len: u32,
    directory_index_path: *const u16,
    directory_index_path_len: u32,
    selection_path: *const u16,
    selection_path_len: u32,
    output: *mut *mut SchemaContext,
) -> i32 {
    ffi_call_value(
        || {
            let input = unsafe { utf16_string(input_path, input_path_len) }?;
            let paths = [
                PathBuf::from(unsafe { utf16_string(file_records_path, file_records_path_len) }?),
                PathBuf::from(unsafe {
                    utf16_string(directory_records_path, directory_records_path_len)
                }?),
                PathBuf::from(unsafe { utf16_string(file_index_path, file_index_path_len) }?),
                PathBuf::from(unsafe {
                    utf16_string(directory_index_path, directory_index_path_len)
                }?),
                PathBuf::from(unsafe { utf16_string(selection_path, selection_path_len) }?),
            ];
            Ok(Box::into_raw(schema_context_from_file(input, paths)?))
        },
        output,
    )
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn lsc_merge_schema_files(
    input_paths: *const u16,
    input_paths_len: u32,
    root_name: *const u16,
    root_name_len: u32,
    file_records_path: *const u16,
    file_records_path_len: u32,
    directory_records_path: *const u16,
    directory_records_path_len: u32,
    file_index_path: *const u16,
    file_index_path_len: u32,
    directory_index_path: *const u16,
    directory_index_path_len: u32,
    selection_path: *const u16,
    selection_path_len: u32,
    output: *mut *mut SchemaContext,
) -> i32 {
    ffi_call_value(
        || {
            let joined_paths = unsafe { utf16_string(input_paths, input_paths_len) }?;
            let input_paths = joined_paths
                .split('\0')
                .filter(|value| !value.is_empty())
                .map(PathBuf::from)
                .collect::<Vec<_>>();
            let root_name = unsafe { utf16_string(root_name, root_name_len) }?;
            let paths = [
                PathBuf::from(unsafe { utf16_string(file_records_path, file_records_path_len) }?),
                PathBuf::from(unsafe {
                    utf16_string(directory_records_path, directory_records_path_len)
                }?),
                PathBuf::from(unsafe { utf16_string(file_index_path, file_index_path_len) }?),
                PathBuf::from(unsafe {
                    utf16_string(directory_index_path, directory_index_path_len)
                }?),
                PathBuf::from(unsafe { utf16_string(selection_path, selection_path_len) }?),
            ];
            Ok(Box::into_raw(schema_context_from_files(
                input_paths,
                root_name,
                paths,
            )?))
        },
        output,
    )
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn lsc_store_open(
    file_records_path: *const u16,
    file_records_path_len: u32,
    directory_records_path: *const u16,
    directory_records_path_len: u32,
    file_index_path: *const u16,
    file_index_path_len: u32,
    directory_index_path: *const u16,
    directory_index_path_len: u32,
    output: *mut *mut StoreContext,
) -> i32 {
    ffi_call_value(
        || {
            let file_records_path =
                PathBuf::from(unsafe { utf16_string(file_records_path, file_records_path_len) }?);
            let directory_records_path = PathBuf::from(unsafe {
                utf16_string(directory_records_path, directory_records_path_len)
            }?);
            let file_index_path =
                PathBuf::from(unsafe { utf16_string(file_index_path, file_index_path_len) }?);
            let directory_index_path = PathBuf::from(unsafe {
                utf16_string(directory_index_path, directory_index_path_len)
            }?);
            let open = |path: &Path, label: &str| {
                File::open(path).map_err(|error| {
                    format!(
                        "cannot open {label} backing file {}: {error}",
                        path.display()
                    )
                })
            };
            Ok(Box::into_raw(Box::new(StoreContext {
                file_records: Mutex::new(open(&file_records_path, "file records")?),
                directory_records: Mutex::new(open(&directory_records_path, "directory records")?),
                file_index: Mutex::new(open(&file_index_path, "file index")?),
                directory_index: Mutex::new(open(&directory_index_path, "directory index")?),
            })))
        },
        output,
    )
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn lsc_store_close(context: *mut StoreContext) {
    if !context.is_null() {
        // SAFETY: the handle was returned by lsc_store_open and is destroyed once.
        unsafe { drop(Box::from_raw(context)) };
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn lsc_store_get_directory_info(
    context: *const StoreContext,
    record_offset: i64,
    output: *mut LscStoreDirectoryInfo,
) -> i32 {
    if context.is_null() {
        set_last_error("schema store context is null");
        return LSC_INVALID_ARGUMENT;
    }
    ffi_call_value(
        || {
            // SAFETY: context was checked for null and remains alive until close.
            let context = unsafe { &*context };
            let header = read_store_directory_header(context, record_offset)?;
            let scalars = read_store_directory_scalars(context, &header)?;
            Ok(LscStoreDirectoryInfo {
                struct_size: std::mem::size_of::<LscStoreDirectoryInfo>() as u32,
                abi_version: 1,
                scalar_offset: header.scalar_offset,
                scalar_length: header.scalar_length,
                file_index_offset: header.file_index_offset,
                file_count: header.file_count,
                directory_index_offset: header.directory_index_offset,
                directory_count: header.directory_count,
                total_file_count: header.total_file_count,
                total_directory_count: header.total_directory_count,
                read_only: u32::from(scalars.read_only),
                reserved: 0,
                file_uid: scalars.file_uid,
            })
        },
        output,
    )
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn lsc_store_get_directory_file_bytes(
    context: *const StoreContext,
    record_offset: i64,
    output: *mut i64,
) -> i32 {
    if context.is_null() {
        set_last_error("schema store context is null");
        return LSC_INVALID_ARGUMENT;
    }
    ffi_call_value(
        || {
            // SAFETY: context was checked for null and remains alive until close.
            store_directory_file_bytes(unsafe { &*context }, record_offset)
        },
        output,
    )
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn lsc_store_copy_directory_string(
    context: *const StoreContext,
    record_offset: i64,
    field: u32,
    buffer: *mut u16,
    capacity: u32,
    required: *mut u32,
) -> i32 {
    if context.is_null() {
        set_last_error("schema store context is null");
        return LSC_INVALID_ARGUMENT;
    }
    let mut value = String::new();
    let status = ffi_call_value(
        || {
            // SAFETY: context was checked for null and remains alive until close.
            let context = unsafe { &*context };
            let header = read_store_directory_header(context, record_offset)?;
            let scalars = read_store_directory_scalars(context, &header)?;
            let value = match field {
                1 => scalars.name,
                2 => scalars.creation_time,
                3 => scalars.change_time,
                4 => scalars.modify_time,
                5 => scalars.access_time,
                6 => scalars.backup_time,
                _ => return Err(invalid("unknown schema store directory string field")),
            };
            Ok(value.unwrap_or_default())
        },
        &mut value,
    );
    if status != LSC_OK {
        return status;
    }
    copy_utf16(&value, buffer, capacity, required)
}

fn copy_optional_utf16(value: &str, buffer: *mut u16, capacity: u32, required: *mut u32) -> i32 {
    if value.is_empty() {
        if !required.is_null() {
            // SAFETY: `required` is an output pointer supplied by the caller.
            unsafe { *required = 0 };
        }
        if !buffer.is_null() && capacity > 0 {
            // SAFETY: the caller supplied at least one UTF-16 output slot.
            unsafe { *buffer = 0 };
        }
        return LSC_OK;
    }
    copy_utf16(value, buffer, capacity, required)
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn lsc_store_search(
    context: *const StoreContext,
    root_record_offset: i64,
    root_path: *const u16,
    root_path_len: u32,
    keyword: *const u16,
    keyword_len: u32,
    case_sensitive: u32,
    resume_kind: u32,
    resume_record_offset: i64,
    callback: Option<LscStoreSearchProgressCallback>,
    user_data: *mut c_void,
    output: *mut LscStoreSearchResult,
    path_buffer: *mut u16,
    path_capacity: u32,
    path_required: *mut u32,
    directory_path_buffer: *mut u16,
    directory_path_capacity: u32,
    directory_path_required: *mut u32,
) -> i32 {
    if context.is_null() || output.is_null() {
        set_last_error("schema store context or search output is null");
        return LSC_INVALID_ARGUMENT;
    }
    let root_path = match unsafe { utf16_string(root_path, root_path_len) } {
        Ok(value) => value,
        Err(error) => {
            set_last_error(error);
            return LSC_INVALID_ARGUMENT;
        }
    };
    let keyword = match unsafe { utf16_string(keyword, keyword_len) } {
        Ok(value) => value,
        Err(error) => {
            set_last_error(error);
            return LSC_INVALID_ARGUMENT;
        }
    };

    let mut computation = StoreSearchComputation::default();
    let status = ffi_call_value(
        || {
            // SAFETY: context was checked for null and remains alive until the synchronous call
            // returns.
            search_store(
                unsafe { &*context },
                root_record_offset,
                root_path,
                keyword,
                case_sensitive != 0,
                resume_kind,
                resume_record_offset,
                callback,
                user_data,
            )
        },
        &mut computation,
    );
    if status != LSC_OK {
        return status;
    }

    // SAFETY: output was checked for null and is owned by the caller.
    unsafe { *output = computation.result };
    let path_status =
        copy_optional_utf16(&computation.path, path_buffer, path_capacity, path_required);
    let directory_path_status = copy_optional_utf16(
        &computation.directory_path,
        directory_path_buffer,
        directory_path_capacity,
        directory_path_required,
    );
    if path_status != LSC_OK {
        return path_status;
    }
    directory_path_status
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn lsc_store_tape_sort(
    context: *const StoreContext,
    root_file_index_offset: i64,
    root_file_count: u64,
    root_directory_index_offset: i64,
    root_directory_count: u64,
    selection_path: *const u16,
    selection_path_len: u32,
    output_path: *const u16,
    output_path_len: u32,
    callback: Option<LscStoreTapeSortProgressCallback>,
    user_data: *mut c_void,
    output: *mut LscStoreTapeSortResult,
) -> i32 {
    if context.is_null() || output.is_null() {
        set_last_error("schema store or tape sort output is null");
        return LSC_INVALID_ARGUMENT;
    }
    let selection_path = match unsafe { utf16_string(selection_path, selection_path_len) } {
        Ok(value) => PathBuf::from(value),
        Err(error) => {
            set_last_error(error);
            return LSC_INVALID_ARGUMENT;
        }
    };
    let output_path = match unsafe { utf16_string(output_path, output_path_len) } {
        Ok(value) => PathBuf::from(value),
        Err(error) => {
            set_last_error(error);
            return LSC_INVALID_ARGUMENT;
        }
    };

    let status = ffi_call_value(
        || {
            // SAFETY: context was checked for null and remains alive until
            // this synchronous operation returns.
            sort_tape_files(
                unsafe { &*context },
                root_file_index_offset,
                root_file_count,
                root_directory_index_offset,
                root_directory_count,
                &selection_path,
                &output_path,
                callback,
                user_data,
            )
        },
        output,
    );
    if status != LSC_OK {
        let _ = std::fs::remove_file(output_path);
    }
    status
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn lsc_store_sort_directory_children(
    context: *const StoreContext,
    directory_record_offset: i64,
    sort_mode: u32,
    locale_name: *const u16,
    locale_name_len: u32,
    file_target_index_offset: i64,
    directory_target_index_offset: i64,
    file_output_path: *const u16,
    file_output_path_len: u32,
    directory_output_path: *const u16,
    directory_output_path_len: u32,
    callback: Option<LscStoreDirectorySortProgressCallback>,
    user_data: *mut c_void,
    output: *mut LscStoreDirectorySortResult,
) -> i32 {
    if context.is_null() || output.is_null() {
        set_last_error("schema store or directory sort output is null");
        return LSC_INVALID_ARGUMENT;
    }
    let locale_name = match unsafe { utf16_string(locale_name, locale_name_len) } {
        Ok(value) => value,
        Err(error) => {
            set_last_error(error);
            return LSC_INVALID_ARGUMENT;
        }
    };
    let file_output_path = match unsafe { utf16_string(file_output_path, file_output_path_len) } {
        Ok(value) => PathBuf::from(value),
        Err(error) => {
            set_last_error(error);
            return LSC_INVALID_ARGUMENT;
        }
    };
    let directory_output_path =
        match unsafe { utf16_string(directory_output_path, directory_output_path_len) } {
            Ok(value) => PathBuf::from(value),
            Err(error) => {
                set_last_error(error);
                return LSC_INVALID_ARGUMENT;
            }
        };

    let status = ffi_call_value(
        || {
            // SAFETY: context was checked for null and remains alive until
            // this synchronous operation returns.
            sort_directory_children(
                unsafe { &*context },
                directory_record_offset,
                sort_mode,
                locale_name,
                file_target_index_offset,
                directory_target_index_offset,
                &file_output_path,
                &directory_output_path,
                callback,
                user_data,
            )
        },
        output,
    );
    if status != LSC_OK {
        let _ = std::fs::remove_file(file_output_path);
        let _ = std::fs::remove_file(directory_output_path);
    }
    status
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn lsc_store_get_file_index_entry(
    context: *const StoreContext,
    offset: i64,
    output: *mut LscStoreFileIndexEntry,
) -> i32 {
    if context.is_null() {
        set_last_error("schema store context is null");
        return LSC_INVALID_ARGUMENT;
    }
    ffi_call_value(
        || {
            // SAFETY: context was checked for null and remains alive until close.
            store_file_index_entry(unsafe { &*context }, offset)
        },
        output,
    )
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn lsc_store_get_directory_index_entry(
    context: *const StoreContext,
    offset: i64,
    output: *mut LscStoreDirectoryIndexEntry,
) -> i32 {
    if context.is_null() {
        set_last_error("schema store context is null");
        return LSC_INVALID_ARGUMENT;
    }
    ffi_call_value(
        || {
            // SAFETY: context was checked for null and remains alive until close.
            store_directory_index_entry(unsafe { &*context }, offset)
        },
        output,
    )
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn lsc_store_copy_file_record(
    context: *const StoreContext,
    record_offset: i64,
    record_length: u64,
    buffer: *mut u8,
    capacity: u64,
    written: *mut u64,
) -> i32 {
    if context.is_null() {
        set_last_error("schema store context is null");
        return LSC_INVALID_ARGUMENT;
    }
    let length = match usize::try_from(record_length) {
        Ok(value) => value,
        Err(_) => {
            set_last_error("schema store file record is too large");
            return LSC_ERROR;
        }
    };
    let mut value = Vec::new();
    let status = ffi_call_value(
        || {
            // SAFETY: context was checked for null and remains alive until close.
            read_store_at(
                unsafe { &(*context).file_records },
                record_offset,
                length,
                "file record",
            )
        },
        &mut value,
    );
    if status != LSC_OK {
        return status;
    }
    copy_bytes_wide(&value, buffer, capacity, written)
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn lsc_store_copy_file_name(
    context: *const StoreContext,
    record_offset: i64,
    record_length: u64,
    buffer: *mut u16,
    capacity: u32,
    required: *mut u32,
) -> i32 {
    if context.is_null() {
        set_last_error("schema store context is null");
        return LSC_INVALID_ARGUMENT;
    }
    let length = match usize::try_from(record_length) {
        Ok(value) => value,
        Err(_) => {
            set_last_error("schema store file record is too large");
            return LSC_ERROR;
        }
    };
    let mut value = String::new();
    let status = ffi_call_value(
        || {
            // SAFETY: context was checked for null and remains alive until close.
            let bytes = read_store_at(
                unsafe { &(*context).file_records },
                record_offset,
                length,
                "file record",
            )?;
            Ok(parse_file_name_bytes(&bytes)?)
        },
        &mut value,
    );
    if status != LSC_OK {
        return status;
    }
    copy_utf16(&value, buffer, capacity, required)
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn lsc_store_copy_file_summary(
    context: *const StoreContext,
    record_offset: i64,
    record_length: u64,
    name_buffer: *mut u16,
    name_capacity: u32,
    name_required: *mut u32,
    output: *mut LscStoreFileSummary,
) -> i32 {
    if context.is_null() || output.is_null() {
        set_last_error("schema store context or file summary output is null");
        return LSC_INVALID_ARGUMENT;
    }
    let length = match usize::try_from(record_length) {
        Ok(value) => value,
        Err(_) => {
            set_last_error("schema store file record is too large");
            return LSC_ERROR;
        }
    };
    let mut value = ParsedFileSummary::default();
    let status = ffi_call_value(
        || {
            // SAFETY: context was checked for null and remains alive until close.
            let bytes = read_store_at(
                unsafe { &(*context).file_records },
                record_offset,
                length,
                "file record",
            )?;
            parse_file_summary_bytes(&bytes)
        },
        &mut value,
    );
    if status != LSC_OK {
        return status;
    }

    // SAFETY: output was checked for null and is owned by the caller.
    unsafe { *output = value.info };
    copy_utf16(&value.name, name_buffer, name_capacity, name_required)
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn lsc_schema_get_result(
    context: *const SchemaContext,
    output: *mut LscSchemaResult,
) -> i32 {
    if context.is_null() || output.is_null() {
        set_last_error("schema result pointer is null");
        return LSC_INVALID_ARGUMENT;
    }
    // SAFETY: both pointers are checked and are owned by the caller/library contract.
    unsafe { *output = (*context).result };
    LSC_OK
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn lsc_schema_get_metadata(
    context: *const SchemaContext,
    output: *mut LscSchemaMetadata,
) -> i32 {
    if context.is_null() || output.is_null() {
        set_last_error("schema metadata pointer is null");
        return LSC_INVALID_ARGUMENT;
    }
    // SAFETY: both pointers are checked and are owned by the caller/library contract.
    unsafe { *output = (*context).metadata.public };
    LSC_OK
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn lsc_schema_copy_string(
    context: *const SchemaContext,
    field: u32,
    buffer: *mut u16,
    capacity: u32,
    required: *mut u32,
) -> i32 {
    if context.is_null() {
        set_last_error("schema context is null");
        return LSC_INVALID_ARGUMENT;
    }
    // SAFETY: context was checked for null and remains alive until destroy.
    let Some(value) = (unsafe { get_schema_string(&*context, field) }) else {
        set_last_error("unknown schema string field");
        return LSC_INVALID_ARGUMENT;
    };
    copy_utf16(value, buffer, capacity, required)
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn lsc_schema_destroy(context: *mut SchemaContext) {
    if !context.is_null() {
        // SAFETY: the handle was returned by lsc_parse_schema_file and is destroyed once.
        unsafe { drop(Box::from_raw(context)) };
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn lsc_file_parse(
    data: *const u8,
    length: u64,
    output: *mut *mut FileContext,
) -> i32 {
    ffi_call_value(
        || {
            let bytes = unsafe {
                byte_slice(
                    data,
                    usize::try_from(length).map_err(|_| invalid("file record is too large"))?,
                )
            }?;
            let parsed = parse_file_bytes(bytes)?;
            Ok(Box::into_raw(Box::new(FileContext { data: parsed })))
        },
        output,
    )
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn lsc_file_get_info(
    context: *const FileContext,
    output: *mut LscFileInfo,
) -> i32 {
    if context.is_null() || output.is_null() {
        set_last_error("file info pointer is null");
        return LSC_INVALID_ARGUMENT;
    }
    // SAFETY: both pointers are checked and the file context is owned by the caller.
    let data = unsafe { &(*context).data };
    unsafe {
        *output = LscFileInfo {
            struct_size: std::mem::size_of::<LscFileInfo>() as u32,
            abi_version: 1,
            length: data.length,
            read_only: u32::from(data.read_only),
            open_for_write: u32::from(data.open_for_write),
            file_uid: data.file_uid,
            xattr_count: data.xattrs.len().min(u32::MAX as usize) as u32,
            extent_count: data.extents.len().min(u32::MAX as usize) as u32,
        };
    }
    LSC_OK
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn lsc_file_copy_string(
    context: *const FileContext,
    field: u32,
    buffer: *mut u16,
    capacity: u32,
    required: *mut u32,
) -> i32 {
    if context.is_null() {
        set_last_error("file context is null");
        return LSC_INVALID_ARGUMENT;
    }
    // SAFETY: context was checked for null and remains alive until destroy.
    let value = unsafe { file_string(&(*context).data, field) }.unwrap_or("");
    copy_utf16(value, buffer, capacity, required)
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn lsc_file_copy_xattr_string(
    context: *const FileContext,
    index: u32,
    field: u32,
    buffer: *mut u16,
    capacity: u32,
    required: *mut u32,
) -> i32 {
    if context.is_null() {
        set_last_error("file context is null");
        return LSC_INVALID_ARGUMENT;
    }
    // SAFETY: context was checked for null and remains alive until destroy.
    let data = unsafe { &(*context).data };
    let Some(pair) = data.xattrs.get(index as usize) else {
        set_last_error("xattr index is out of range");
        return LSC_INVALID_ARGUMENT;
    };
    copy_utf16(
        if field == 0 { &pair.0 } else { &pair.1 },
        buffer,
        capacity,
        required,
    )
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn lsc_file_get_extent(
    context: *const FileContext,
    index: u32,
    output: *mut LscExtent,
) -> i32 {
    if context.is_null() || output.is_null() {
        set_last_error("file extent pointer is null");
        return LSC_INVALID_ARGUMENT;
    }
    // SAFETY: context and output were checked for null.
    let Some(value) = (unsafe { (&(*context).data.extents).get(index as usize) }) else {
        set_last_error("extent index is out of range");
        return LSC_INVALID_ARGUMENT;
    };
    unsafe { *output = *value };
    LSC_OK
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn lsc_file_destroy(context: *mut FileContext) {
    if !context.is_null() {
        // SAFETY: the handle was returned by lsc_file_parse and is destroyed once.
        unsafe { drop(Box::from_raw(context)) };
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn lsc_file_serialize(
    input: *const LscFileInput,
    buffer: *mut u8,
    capacity: u32,
    written: *mut u32,
) -> i32 {
    if input.is_null() {
        set_last_error("file input pointer is null");
        return LSC_INVALID_ARGUMENT;
    }
    // SAFETY: input was checked for null and remains valid for this synchronous call.
    let value =
        match unsafe { file_data_from_input(&*input) }.and_then(|value| serialize_file(&value)) {
            Ok(value) => value,
            Err(error) => {
                set_last_error(error);
                return LSC_ERROR;
            }
        };
    copy_bytes(&value, buffer, capacity, written)
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn lsc_writer_open(
    path: *const u16,
    path_len: u32,
    output: *mut *mut SchemaWriter,
) -> i32 {
    ffi_call_value(
        || {
            let path = PathBuf::from(unsafe { utf16_string(path, path_len) }?);
            let file = OpenOptions::new()
                .create(true)
                .truncate(true)
                .write(true)
                .open(&path)
                .map_err(|error| {
                    format!("cannot open schema output {}: {error}", path.display())
                })?;
            let mut writer = new_xml_writer(BufWriter::with_capacity(64 * 1024, file));
            writer
                .write_event(Event::Decl(BytesDecl::new("1.0", Some("UTF-8"), None)))
                .map_err(|error| error.to_string())?;
            Ok(Box::into_raw(Box::new(SchemaWriter {
                writer: Some(writer),
            })))
        },
        output,
    )
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn lsc_writer_start(
    writer: *mut SchemaWriter,
    name: *const u16,
    name_len: u32,
) -> i32 {
    ffi_call(|| {
        if writer.is_null() {
            return Err(invalid("schema writer is null"));
        }
        let name = writer_name(name, name_len)?;
        // SAFETY: writer was checked for null and is owned by the caller.
        writer_mut(unsafe { &mut *writer })?
            .write_event(Event::Start(BytesStart::new(name)))
            .map_err(|error| error.to_string())
    })
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn lsc_writer_start_attribute(
    writer: *mut SchemaWriter,
    name: *const u16,
    name_len: u32,
    attribute_name: *const u16,
    attribute_name_len: u32,
    attribute_value: *const u16,
    attribute_value_len: u32,
) -> i32 {
    ffi_call(|| {
        if writer.is_null() {
            return Err(invalid("schema writer is null"));
        }
        let name = writer_name(name, name_len)?;
        let attribute_name = writer_name(attribute_name, attribute_name_len)?;
        let attribute_value = writer_name(attribute_value, attribute_value_len)?;
        let mut start = BytesStart::new(name);
        start.push_attribute((attribute_name.as_str(), attribute_value.as_str()));
        // SAFETY: writer was checked for null and is owned by the caller.
        writer_mut(unsafe { &mut *writer })?
            .write_event(Event::Start(start))
            .map_err(|error| error.to_string())
    })
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn lsc_writer_empty(
    writer: *mut SchemaWriter,
    name: *const u16,
    name_len: u32,
) -> i32 {
    ffi_call(|| {
        if writer.is_null() {
            return Err(invalid("schema writer is null"));
        }
        let name = writer_name(name, name_len)?;
        // SAFETY: writer was checked for null and is owned by the caller.
        writer_mut(unsafe { &mut *writer })?
            .write_event(Event::Empty(BytesStart::new(name)))
            .map_err(|error| error.to_string())
    })
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn lsc_writer_end(
    writer: *mut SchemaWriter,
    name: *const u16,
    name_len: u32,
) -> i32 {
    ffi_call(|| {
        if writer.is_null() {
            return Err(invalid("schema writer is null"));
        }
        let name = writer_name(name, name_len)?;
        // SAFETY: writer was checked for null and is owned by the caller.
        writer_mut(unsafe { &mut *writer })?
            .write_event(Event::End(BytesEnd::new(name)))
            .map_err(|error| error.to_string())
    })
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn lsc_writer_element(
    writer: *mut SchemaWriter,
    name: *const u16,
    name_len: u32,
    value: *const u16,
    value_len: u32,
) -> i32 {
    ffi_call(|| {
        if writer.is_null() {
            return Err(invalid("schema writer is null"));
        }
        let name = writer_name(name, name_len)?;
        let value = writer_name(value, value_len)?;
        // SAFETY: writer was checked for null and is owned by the caller.
        let writer = writer_mut(unsafe { &mut *writer })?;
        writer
            .write_event(Event::Start(BytesStart::new(name.as_str())))
            .map_err(|error| error.to_string())?;
        write_xml_text(writer, &value)?;
        writer
            .write_event(Event::End(BytesEnd::new(name)))
            .map_err(|error| error.to_string())
    })
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn lsc_writer_file(
    writer: *mut SchemaWriter,
    input: *const LscFileInput,
) -> i32 {
    ffi_call(|| {
        if writer.is_null() || input.is_null() {
            return Err(invalid("schema writer or file input is null"));
        }
        // SAFETY: input was checked for null and is valid for this synchronous call.
        let value = unsafe { file_data_from_input(&*input) }?;
        let bytes = serialize_file(&value)?;
        // SAFETY: writer was checked for null and is owned by the caller.
        write_fragment(writer_mut(unsafe { &mut *writer })?, &bytes)
    })
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn lsc_writer_raw(
    writer: *mut SchemaWriter,
    data: *const u8,
    length: u64,
) -> i32 {
    ffi_call(|| {
        if writer.is_null() {
            return Err(invalid("schema writer is null"));
        }
        let data = unsafe {
            byte_slice(
                data,
                usize::try_from(length).map_err(|_| invalid("XML fragment is too large"))?,
            )
        }?;
        // SAFETY: writer was checked for null and is owned by the caller.
        write_fragment(writer_mut(unsafe { &mut *writer })?, data)
    })
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn lsc_writer_store_file_record(
    writer: *mut SchemaWriter,
    store: *const StoreContext,
    record_offset: i64,
    record_length: u64,
) -> i32 {
    if writer.is_null() || store.is_null() {
        set_last_error("schema writer or schema store is null");
        return LSC_INVALID_ARGUMENT;
    }
    if record_offset < 0 || record_length == 0 {
        set_last_error("invalid schema store file record range");
        return LSC_INVALID_ARGUMENT;
    }
    ffi_call(|| {
        // SAFETY: both pointers were checked above and remain owned by the
        // caller for the duration of this synchronous FFI call.
        let writer = writer_mut(unsafe { &mut *writer })?;
        let store = unsafe { &*store };
        copy_store_file_record_to_writer(writer, store, record_offset, record_length)
    })
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn lsc_writer_store_directory_files(
    writer: *mut SchemaWriter,
    store: *const StoreContext,
    directory_record_offset: i64,
) -> i32 {
    if writer.is_null() || store.is_null() {
        set_last_error("schema writer or schema store is null");
        return LSC_INVALID_ARGUMENT;
    }
    if directory_record_offset < 0 {
        set_last_error("invalid schema store directory record offset");
        return LSC_INVALID_ARGUMENT;
    }
    ffi_call(|| {
        // SAFETY: both pointers were checked above and remain owned by the
        // caller for the duration of this synchronous FFI call.
        let writer = writer_mut(unsafe { &mut *writer })?;
        let store = unsafe { &*store };
        let header = read_store_directory_header(store, directory_record_offset)?;
        if header.file_count == 0 {
            return Ok(());
        }
        if header.file_index_offset < 0 {
            return Err(invalid("invalid schema backing file index"));
        }

        let count = usize::try_from(header.file_count)
            .map_err(|_| invalid("schema backing file count is too large"))?;
        let mut entry_offset = header.file_index_offset;
        for _ in 0..count {
            if entry_offset < 0 {
                return Err(invalid("schema backing file index chain is truncated"));
            }
            let entry = store_file_index_entry(store, entry_offset)?;
            let record_length = u64::try_from(entry.record_length)
                .map_err(|_| invalid("invalid schema backing file record length"))?;
            copy_store_file_record_to_writer(writer, store, entry.record_offset, record_length)?;
            entry_offset = entry.next_offset;
        }
        Ok(())
    })
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn lsc_writer_finish(writer: *mut SchemaWriter) -> i32 {
    ffi_call(|| {
        if writer.is_null() {
            return Err(invalid("schema writer is null"));
        }
        // SAFETY: writer was checked for null and is owned by the caller.
        let writer = unsafe { &mut *writer };
        let Some(writer) = writer.writer.take() else {
            return Err(invalid("schema writer is already finished"));
        };
        writer
            .into_inner()
            .into_inner()
            .map_err(|error| error.to_string())?
            .sync_all()
            .map_err(|error| error.to_string())
    })
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn lsc_writer_destroy(writer: *mut SchemaWriter) {
    if !writer.is_null() {
        // SAFETY: the handle was returned by lsc_writer_open and is destroyed once.
        unsafe { drop(Box::from_raw(writer)) };
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_compact_schema_into_lazy_backing_files() {
        let root =
            std::env::temp_dir().join(format!("ltfscopy_schema_test_{}", std::process::id()));
        let _ = std::fs::create_dir_all(&root);
        let input = root.join("sample.schema");
        let paths = [
            root.join("files.bin"),
            root.join("directories.bin"),
            root.join("file-index.bin"),
            root.join("directory-index.bin"),
            root.join("selection.bin"),
        ];
        let text = r#"<ltfsindex version="2.4.0"><creator>A &amp; B</creator><volumeuuid>00112233-4455-6677-8899-aabbccddeeff</volumeuuid><generationnumber>7</generationnumber><updatetime>2024-01-01T00:00:00Z</updatetime><location><partition>a</partition><startblock>12</startblock></location><allowpolicyupdate>True</allowpolicyupdate><volumelockstate>locked</volumelockstate><highestfileuid>99</highestfileuid><directory><name>root</name><readonly>False</readonly><contents><file><name>hello &amp; world.txt</name><length>5</length><openforwrite>True</openforwrite><fileuid>3</fileuid><extendedattributes><xattr><key>k</key><value>v</value></xattr></extendedattributes><extentinfo><extent><fileoffset>0</fileoffset><partition>a</partition><startblock>4</startblock><byteoffset>0</byteoffset><bytecount>5</bytecount></extent></extentinfo></file><directory><name>child</name><contents /></directory></contents></directory></ltfsindex>"#;
        std::fs::write(&input, text).expect("write test schema");

        let context = schema_context_from_file(input.to_string_lossy().into_owned(), paths.clone())
            .expect("parse test schema");
        assert_eq!(context.result.root_directory_count, 1);
        assert_eq!(context.metadata.creator, "A & B");
        assert_eq!(context.metadata.public.generation_number, 7);
        assert_eq!(context.metadata.public.location_start_block, 12);
        assert!(context.result.selection_count >= 3);

        assert!(
            std::fs::metadata(&paths[0])
                .expect("read file backing metadata")
                .len()
                > 0
        );

        let _ = std::fs::remove_dir_all(root);
    }

    #[test]
    fn file_serializer_escapes_unicode_and_ampersands() {
        let value = FileData {
            name: "中文 & <name>.txt".to_owned(),
            length: 4,
            open_for_write: true,
            xattrs: vec![("key".to_owned(), "值 & value".to_owned())],
            extents: vec![LscExtent {
                partition: 1,
                start_block: 42,
                byte_offset: 7,
                byte_count: 9,
                ..Default::default()
            }],
            ..Default::default()
        };
        let xml = String::from_utf8(serialize_file(&value).expect("serialize file"))
            .expect("UTF-8 file XML");
        assert!(xml.contains("中文 &amp; &lt;name&gt;.txt"));
        assert!(xml.contains("<readonly>false</readonly>"));
        assert!(xml.contains("<openforwrite>true</openforwrite>"));
        let parsed = FileParser::new(Reader::from_reader(Cursor::new(xml.as_bytes())))
            .parse()
            .expect("parse file XML");
        assert_eq!(parsed.name, value.name);
        assert_eq!(parsed.xattrs, value.xattrs);
        assert_eq!(
            parse_file_name_bytes(xml.as_bytes()).expect("parse file name"),
            value.name
        );
        assert_eq!(
            parse_file_length_bytes(xml.as_bytes()).expect("parse file length"),
            4
        );
        let summary = parse_file_summary_bytes(xml.as_bytes()).expect("parse file summary");
        assert_eq!(summary.name, value.name);
        assert_eq!(summary.info.length, value.length);
        assert_eq!(summary.info.partition, 1);
        assert_eq!(summary.info.start_block, 42);
        assert_eq!(summary.info.byte_offset, 7);
        assert_eq!(summary.info.byte_count, 9);
    }

    #[test]
    fn schema_writer_starts_with_utf8_xml_declaration() {
        let root = std::env::temp_dir().join(format!(
            "ltfscopy_schema_writer_header_test_{}",
            std::process::id()
        ));
        let _ = std::fs::remove_dir_all(&root);
        std::fs::create_dir_all(&root).expect("create writer header test directory");
        let output = root.join("output.schema");
        let path_utf16: Vec<u16> = output.to_string_lossy().encode_utf16().collect();
        let name_utf16: Vec<u16> = "ltfsindex".encode_utf16().collect();
        let mut handle: *mut SchemaWriter = std::ptr::null_mut();

        assert_eq!(
            unsafe {
                lsc_writer_open(
                    path_utf16.as_ptr(),
                    u32::try_from(path_utf16.len()).expect("path length"),
                    &mut handle,
                )
            },
            LSC_OK
        );
        assert!(!handle.is_null());

        assert_eq!(
            unsafe {
                lsc_writer_start(
                    handle,
                    name_utf16.as_ptr(),
                    u32::try_from(name_utf16.len()).expect("name length"),
                )
            },
            LSC_OK
        );
        assert_eq!(
            unsafe {
                lsc_writer_end(
                    handle,
                    name_utf16.as_ptr(),
                    u32::try_from(name_utf16.len()).expect("name length"),
                )
            },
            LSC_OK
        );
        assert_eq!(unsafe { lsc_writer_finish(handle) }, LSC_OK);
        unsafe { lsc_writer_destroy(handle) };

        let xml = std::fs::read_to_string(&output).expect("read schema writer output");
        assert_eq!(
            xml,
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<ltfsindex>\n</ltfsindex>"
        );

        let _ = std::fs::remove_dir_all(root);
    }

    #[test]
    fn schema_writer_places_raw_elements_on_separate_lines() {
        let root = std::env::temp_dir().join(format!(
            "ltfscopy_schema_writer_lines_test_{}",
            std::process::id()
        ));
        let _ = std::fs::remove_dir_all(&root);
        std::fs::create_dir_all(&root).expect("create writer line test directory");
        let output = root.join("output.schema");
        let path_utf16: Vec<u16> = output.to_string_lossy().encode_utf16().collect();
        let mut handle: *mut SchemaWriter = std::ptr::null_mut();

        assert_eq!(
            unsafe {
                lsc_writer_open(
                    path_utf16.as_ptr(),
                    u32::try_from(path_utf16.len()).expect("path length"),
                    &mut handle,
                )
            },
            LSC_OK
        );
        assert!(!handle.is_null());

        let start = |value: &str| value.encode_utf16().collect::<Vec<u16>>();
        let root_name = start("ltfsindex");
        let directory_name = start("directory");
        let contents_name = start("contents");
        let compact_file = b"<file><name>one.txt</name><length>1</length></file>";
        assert_eq!(
            unsafe {
                lsc_writer_start(
                    handle,
                    root_name.as_ptr(),
                    u32::try_from(root_name.len()).expect("root name length"),
                )
            },
            LSC_OK
        );
        assert_eq!(
            unsafe {
                lsc_writer_start(
                    handle,
                    directory_name.as_ptr(),
                    u32::try_from(directory_name.len()).expect("directory name length"),
                )
            },
            LSC_OK
        );
        assert_eq!(
            unsafe {
                lsc_writer_start(
                    handle,
                    contents_name.as_ptr(),
                    u32::try_from(contents_name.len()).expect("contents name length"),
                )
            },
            LSC_OK
        );
        assert_eq!(
            unsafe {
                lsc_writer_raw(
                    handle,
                    compact_file.as_ptr(),
                    u64::try_from(compact_file.len()).expect("fragment length"),
                )
            },
            LSC_OK
        );
        assert_eq!(
            unsafe {
                lsc_writer_end(
                    handle,
                    contents_name.as_ptr(),
                    u32::try_from(contents_name.len()).expect("contents name length"),
                )
            },
            LSC_OK
        );
        assert_eq!(
            unsafe {
                lsc_writer_end(
                    handle,
                    directory_name.as_ptr(),
                    u32::try_from(directory_name.len()).expect("directory name length"),
                )
            },
            LSC_OK
        );
        assert_eq!(
            unsafe {
                lsc_writer_end(
                    handle,
                    root_name.as_ptr(),
                    u32::try_from(root_name.len()).expect("root name length"),
                )
            },
            LSC_OK
        );
        assert_eq!(unsafe { lsc_writer_finish(handle) }, LSC_OK);
        unsafe { lsc_writer_destroy(handle) };

        let xml = std::fs::read_to_string(&output).expect("read line-oriented schema");
        assert_eq!(
            xml,
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<ltfsindex>\n<directory>\n<contents>\n<file>\n<name>one.txt</name>\n<length>1</length>\n</file>\n</contents>\n</directory>\n</ltfsindex>"
        );

        let _ = std::fs::remove_dir_all(root);
    }

    #[test]
    fn imported_pretty_file_records_drop_formatting_whitespace() {
        let root = std::env::temp_dir().join(format!(
            "ltfscopy_schema_pretty_file_test_{}",
            std::process::id()
        ));
        let _ = std::fs::remove_dir_all(&root);
        std::fs::create_dir_all(&root).expect("create pretty file test directory");
        let input = root.join("pretty.schema");
        let paths = [
            root.join("files.bin"),
            root.join("directories.bin"),
            root.join("file-index.bin"),
            root.join("directory-index.bin"),
            root.join("selection.bin"),
        ];
        let text = r#"<ltfsindex version="2.4.0">
  <directory>
    <name>root</name>
    <contents>
      <file>
        <name> spaced.txt </name>
        <length>1</length>
      </file>
    </contents>
  </directory>
</ltfsindex>"#;
        std::fs::write(&input, text).expect("write pretty file schema");

        let context = schema_context_from_file(input.to_string_lossy().into_owned(), paths.clone())
            .expect("parse pretty file schema");
        let record = std::fs::read(&paths[0]).expect("read compacted file record");
        assert_eq!(
            std::str::from_utf8(&record).expect("UTF-8 file record"),
            "<file><name> spaced.txt </name><length>1</length></file>"
        );

        drop(context);
        let _ = std::fs::remove_dir_all(root);
    }

    #[test]
    fn merges_schema_roots_directly_into_one_lazy_store() {
        let root =
            std::env::temp_dir().join(format!("ltfscopy_schema_merge_test_{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&root);
        std::fs::create_dir_all(&root).expect("create merge test directory");
        let first = root.join("first.schema");
        let second = root.join("second.schema");
        let paths = [
            root.join("files.bin"),
            root.join("directories.bin"),
            root.join("file-index.bin"),
            root.join("directory-index.bin"),
            root.join("selection.bin"),
        ];
        let first_text = r#"<ltfsindex version="2.4.0">
  <directory>
    <name>root</name>
    <contents>
      <file>
        <name>a.txt</name>
        <length>1</length>
      </file>
      <directory>
        <name>sub</name>
        <contents>
          <file>
            <name>b.txt</name>
            <length>2</length>
          </file>
        </contents>
      </directory>
    </contents>
  </directory>
</ltfsindex>"#;
        let second_text = r#"<ltfsindex version="2.4.0"><directory><name>root</name><contents><file><name>c.txt</name><length>3</length></file></contents></directory></ltfsindex>"#;
        std::fs::write(&first, first_text).expect("write first schema");
        std::fs::write(&second, second_text).expect("write second schema");

        let context =
            schema_context_from_files(vec![first, second], "Search_test".to_owned(), paths.clone())
                .expect("merge test schemas");
        assert_eq!(context.result.root_file_count, 0);
        assert_eq!(context.result.root_directory_count, 1);

        let store = StoreContext {
            file_records: Mutex::new(File::open(&paths[0]).expect("open file backing")),
            directory_records: Mutex::new(File::open(&paths[1]).expect("open directory backing")),
            file_index: Mutex::new(File::open(&paths[2]).expect("open file index backing")),
            directory_index: Mutex::new(
                File::open(&paths[3]).expect("open directory index backing"),
            ),
        };
        let root_index =
            store_directory_index_entry(&store, context.result.root_directory_index_offset)
                .expect("read merged root index");
        let root_header = read_store_directory_header(&store, root_index.record_offset)
            .expect("read merged root header");
        assert_eq!(root_header.file_count, 2);
        assert_eq!(root_header.directory_count, 1);
        assert_eq!(root_header.total_file_count, 3);
        assert_eq!(root_header.total_directory_count, 1);

        let first_file_index = store_file_index_entry(&store, root_header.file_index_offset)
            .expect("read first merged file index");
        let first_file_bytes = read_store_at(
            &store.file_records,
            first_file_index.record_offset,
            first_file_index.record_length as usize,
            "merged file record",
        )
        .expect("read first merged file");
        let first_file = parse_file_bytes(&first_file_bytes).expect("parse first merged file");
        assert_eq!(first_file.name, "a.txt");
        assert!(!first_file_bytes.contains(&b'\n'));
        assert!(!first_file_bytes.contains(&b'\r'));
        assert_eq!(
            first_file
                .xattrs
                .iter()
                .find(|(key, _)| key == "Barcode")
                .map(|(_, value)| value.as_str()),
            Some("first")
        );

        drop(store);
        drop(context);
        let _ = std::fs::remove_dir_all(root);
    }

    #[test]
    fn merges_same_name_directories_recursively_but_keeps_same_name_files() {
        let root = std::env::temp_dir().join(format!(
            "ltfscopy_schema_duplicate_directory_merge_test_{}",
            std::process::id()
        ));
        let _ = std::fs::remove_dir_all(&root);
        std::fs::create_dir_all(&root).expect("create duplicate directory merge test directory");
        let first = root.join("first.schema");
        let second = root.join("second.schema");
        let paths = [
            root.join("files.bin"),
            root.join("directories.bin"),
            root.join("file-index.bin"),
            root.join("directory-index.bin"),
            root.join("selection.bin"),
        ];
        let first_text = r#"<ltfsindex version="2.4.0"><directory><name>root</name><fileuid>900</fileuid><contents><file><name>same.txt</name><length>1</length><fileuid>42</fileuid></file><directory><name>same</name><fileuid>901</fileuid><contents><file><name>first.txt</name><length>2</length><fileuid>43</fileuid></file><directory><name>nested</name><fileuid>902</fileuid><contents><file><name>nested-first.txt</name><length>3</length><fileuid>44</fileuid></file></contents></directory></contents></directory></contents></directory></ltfsindex>"#;
        let second_text = r#"<ltfsindex version="2.4.0"><directory><name>root</name><fileuid>900</fileuid><contents><file><name>same.txt</name><length>4</length><fileuid>42</fileuid></file><directory><name>same</name><fileuid>901</fileuid><contents><file><name>second.txt</name><length>5</length><fileuid>43</fileuid></file><directory><name>nested</name><fileuid>902</fileuid><contents><file><name>nested-second.txt</name><length>6</length><fileuid>44</fileuid></file></contents></directory></contents></directory></contents></directory></ltfsindex>"#;
        std::fs::write(&first, first_text).expect("write first duplicate directory schema");
        std::fs::write(&second, second_text).expect("write second duplicate directory schema");

        let context = schema_context_from_files(
            vec![first, second],
            "Search_duplicate".to_owned(),
            paths.clone(),
        )
        .expect("merge duplicate directory schemas");
        assert_eq!(context.metadata.public.highest_file_uid, 8);
        assert_eq!(
            context.metadata.public.present_mask & PRESENT_HIGHEST_FILE_UID,
            PRESENT_HIGHEST_FILE_UID
        );
        let store = StoreContext {
            file_records: Mutex::new(File::open(&paths[0]).expect("open file backing")),
            directory_records: Mutex::new(File::open(&paths[1]).expect("open directory backing")),
            file_index: Mutex::new(File::open(&paths[2]).expect("open file index backing")),
            directory_index: Mutex::new(
                File::open(&paths[3]).expect("open directory index backing"),
            ),
        };

        let root_index =
            store_directory_index_entry(&store, context.result.root_directory_index_offset)
                .expect("read merged root index");
        let root_header = read_store_directory_header(&store, root_index.record_offset)
            .expect("read merged root header");
        assert_eq!(root_header.file_count, 2);
        assert_eq!(root_header.directory_count, 1);
        assert_eq!(root_header.total_file_count, 6);
        assert_eq!(root_header.total_directory_count, 2);

        let mut root_file_ids = Vec::new();
        let mut root_file_index_offset = root_header.file_index_offset;
        for _ in 0..root_header.file_count {
            let entry = store_file_index_entry(&store, root_file_index_offset)
                .expect("read merged root file index");
            let bytes = read_store_at(
                &store.file_records,
                entry.record_offset,
                entry.record_length as usize,
                "merged root file record",
            )
            .expect("read merged root file record");
            root_file_ids.push(
                parse_file_bytes(&bytes)
                    .expect("parse merged root file")
                    .file_uid,
            );
            root_file_index_offset = entry.next_offset;
        }
        assert_eq!(root_file_ids, vec![1, 4]);

        let same_index = store_directory_index_entry(&store, root_header.directory_index_offset)
            .expect("read merged same directory index");
        assert_eq!(same_index.next_offset, -1);
        let same_header = read_store_directory_header(&store, same_index.record_offset)
            .expect("read merged same directory header");
        assert_eq!(
            read_store_directory_scalars(&store, &same_header)
                .unwrap()
                .name
                .as_deref(),
            Some("same")
        );
        assert_eq!(
            read_store_directory_scalars(&store, &same_header)
                .expect("read merged same directory scalars")
                .file_uid,
            7
        );
        assert_eq!(same_header.file_count, 2);
        assert_eq!(same_header.directory_count, 1);
        assert_eq!(same_header.total_file_count, 4);
        assert_eq!(same_header.total_directory_count, 1);

        let nested_index = store_directory_index_entry(&store, same_header.directory_index_offset)
            .expect("read merged nested directory index");
        assert_eq!(nested_index.next_offset, -1);
        let nested_header = read_store_directory_header(&store, nested_index.record_offset)
            .expect("read merged nested directory header");
        assert_eq!(
            read_store_directory_scalars(&store, &nested_header)
                .unwrap()
                .name
                .as_deref(),
            Some("nested")
        );
        assert_eq!(
            read_store_directory_scalars(&store, &nested_header)
                .expect("read merged nested directory scalars")
                .file_uid,
            8
        );
        assert_eq!(nested_header.file_count, 2);
        assert_eq!(nested_header.directory_count, 0);
        assert_eq!(nested_header.total_file_count, 2);
        assert_eq!(nested_header.total_directory_count, 0);

        drop(store);
        drop(context);
        let _ = std::fs::remove_dir_all(root);
    }

    #[test]
    fn searches_lazy_store_across_directories_and_wraps() {
        let root = std::env::temp_dir().join(format!(
            "ltfscopy_schema_search_test_{}",
            std::process::id()
        ));
        let _ = std::fs::remove_dir_all(&root);
        std::fs::create_dir_all(&root).expect("create search test directory");
        let input = root.join("search.schema");
        let paths = [
            root.join("files.bin"),
            root.join("directories.bin"),
            root.join("file-index.bin"),
            root.join("directory-index.bin"),
            root.join("selection.bin"),
        ];
        let text = r#"<ltfsindex version="2.4.0"><directory><name>root</name><contents><file><name>48188-root.txt</name><length>1</length></file><directory><name>sub</name><contents><file><name>48188-child.txt</name><length>2</length></file></contents></directory></contents></directory></ltfsindex>"#;
        std::fs::write(&input, text).expect("write search test schema");

        let context = schema_context_from_file(input.to_string_lossy().into_owned(), paths.clone())
            .expect("parse search test schema");
        let store = StoreContext {
            file_records: Mutex::new(File::open(&paths[0]).expect("open file backing")),
            directory_records: Mutex::new(File::open(&paths[1]).expect("open directory backing")),
            file_index: Mutex::new(File::open(&paths[2]).expect("open file index backing")),
            directory_index: Mutex::new(
                File::open(&paths[3]).expect("open directory index backing"),
            ),
        };
        let root_index =
            store_directory_index_entry(&store, context.result.root_directory_index_offset)
                .expect("read search root index");

        let first = search_store(
            &store,
            root_index.record_offset,
            "root".to_owned(),
            "48188".to_owned(),
            false,
            0,
            -1,
            None,
            ptr::null_mut(),
        )
        .expect("search first result");
        assert_eq!(first.path, r"root\48188-root.txt");
        assert_eq!(first.directory_path, "root");

        let second = search_store(
            &store,
            root_index.record_offset,
            "root".to_owned(),
            "48188".to_owned(),
            false,
            LSC_SEARCH_MATCH_FILE,
            first.result.record_offset,
            None,
            ptr::null_mut(),
        )
        .expect("search second result");
        assert_eq!(second.path, r"root\sub\48188-child.txt");
        assert_eq!(second.directory_path, r"root\sub");

        let wrapped = search_store(
            &store,
            root_index.record_offset,
            "root".to_owned(),
            "48188".to_owned(),
            false,
            LSC_SEARCH_MATCH_FILE,
            second.result.record_offset,
            None,
            ptr::null_mut(),
        )
        .expect("search wrapped result");
        assert_eq!(wrapped.path, r"root\48188-root.txt");

        drop(store);
        drop(context);
        let _ = std::fs::remove_dir_all(root);
    }

    #[test]
    fn sorts_selected_files_by_tape_position_in_native_code() {
        let root = std::env::temp_dir().join(format!(
            "ltfscopy_schema_tape_sort_test_{}",
            std::process::id()
        ));
        let _ = std::fs::remove_dir_all(&root);
        std::fs::create_dir_all(&root).expect("create tape sort test directory");
        let input = root.join("sort.schema");
        let paths = [
            root.join("files.bin"),
            root.join("directories.bin"),
            root.join("file-index.bin"),
            root.join("directory-index.bin"),
            root.join("selection.bin"),
        ];
        let output = root.join("tape-sort.run");
        let text = r#"<ltfsindex version="2.4.0"><directory><name>root</name><contents><file><name>b.txt</name><length>2</length><extentinfo><extent><partition>b</partition><startblock>2</startblock><byteoffset>0</byteoffset><bytecount>2</bytecount></extent></extentinfo></file><file><name>a.txt</name><length>3</length><extentinfo><extent><partition>a</partition><startblock>20</startblock><byteoffset>0</byteoffset><bytecount>3</bytecount></extent></extentinfo></file><directory><name>sub</name><contents><file><name>c.txt</name><length>4</length><extentinfo><extent><partition>a</partition><startblock>10</startblock><byteoffset>0</byteoffset><bytecount>4</bytecount></extent></extentinfo></file></contents></directory><file><name>d.txt</name><length>5</length><extentinfo><extent><partition>a</partition><startblock>10</startblock><byteoffset>0</byteoffset><bytecount>5</bytecount></extent></extentinfo></file></contents></directory></ltfsindex>"#;
        std::fs::write(&input, text).expect("write tape sort schema");

        let context = schema_context_from_file(input.to_string_lossy().into_owned(), paths.clone())
            .expect("parse tape sort schema");
        let store = StoreContext {
            file_records: Mutex::new(File::open(&paths[0]).expect("open file backing")),
            directory_records: Mutex::new(File::open(&paths[1]).expect("open directory backing")),
            file_index: Mutex::new(File::open(&paths[2]).expect("open file index backing")),
            directory_index: Mutex::new(
                File::open(&paths[3]).expect("open directory index backing"),
            ),
        };
        let result = sort_tape_files(
            &store,
            context.result.root_file_index_offset,
            context.result.root_file_count,
            context.result.root_directory_index_offset,
            context.result.root_directory_count,
            &paths[4],
            &output,
            None,
            ptr::null_mut(),
        )
        .expect("sort tape files");
        assert_eq!(result.file_count, 4);
        assert_eq!(result.partition_a_file_count, 3);
        assert_eq!(result.partition_b_file_count, 1);

        let mut reader = BufReader::new(File::open(&output).expect("open tape sort output"));
        let mut entries = Vec::new();
        while let Some(entry) = read_tape_sort_entry(&mut reader).expect("read tape sort output") {
            entries.push((entry.partition, entry.start_block, entry.path));
        }
        assert_eq!(
            entries,
            vec![
                (0, 10, "d.txt".to_owned()),
                (0, 10, "sub\\c.txt".to_owned()),
                (0, 20, "a.txt".to_owned()),
                (1, 2, "b.txt".to_owned()),
            ]
        );

        drop(reader);
        drop(store);
        drop(context);
        let _ = std::fs::remove_dir_all(root);
    }

    #[test]
    fn sorts_directory_children_into_fixed_index_chains() {
        let root = std::env::temp_dir().join(format!(
            "ltfscopy_schema_directory_sort_test_{}",
            std::process::id()
        ));
        let _ = std::fs::remove_dir_all(&root);
        std::fs::create_dir_all(&root).expect("create directory sort test directory");
        let input = root.join("sort.schema");
        let paths = [
            root.join("files.bin"),
            root.join("directories.bin"),
            root.join("file-index.bin"),
            root.join("directory-index.bin"),
            root.join("selection.bin"),
        ];
        let file_output = root.join("sorted-files.bin");
        let directory_output = root.join("sorted-directories.bin");
        let text = r#"<ltfsindex version="2.4.0"><directory><name>root</name><contents><file><name>b.txt</name><length>2</length></file><file><name>a.txt</name><length>3</length></file><file><name>d.txt</name><length>4</length></file><directory><name>sub</name><contents><file><name>child.txt</name><length>5</length></file></contents></directory></contents></directory></ltfsindex>"#;
        std::fs::write(&input, text).expect("write directory sort schema");

        let context = schema_context_from_file(input.to_string_lossy().into_owned(), paths.clone())
            .expect("parse directory sort schema");
        let store = StoreContext {
            file_records: Mutex::new(File::open(&paths[0]).expect("open file backing")),
            directory_records: Mutex::new(File::open(&paths[1]).expect("open directory backing")),
            file_index: Mutex::new(File::open(&paths[2]).expect("open file index backing")),
            directory_index: Mutex::new(
                File::open(&paths[3]).expect("open directory index backing"),
            ),
        };
        let root_index =
            store_directory_index_entry(&store, context.result.root_directory_index_offset)
                .expect("read root directory index");
        let file_target_offset = File::open(&paths[2])
            .expect("open file index for metadata")
            .metadata()
            .expect("read file index metadata")
            .len() as i64;
        let directory_target_offset = File::open(&paths[3])
            .expect("open directory index for metadata")
            .metadata()
            .expect("read directory index metadata")
            .len() as i64;
        let result = sort_directory_children(
            &store,
            root_index.record_offset,
            DIRECTORY_SORT_MODE_CURRENT_CULTURE,
            String::new(),
            file_target_offset,
            directory_target_offset,
            &file_output,
            &directory_output,
            None,
            ptr::null_mut(),
        )
        .expect("sort directory children");
        assert_eq!(result.file_count, 3);
        assert_eq!(result.directory_count, 1);

        let reader = TapeSortReader::new(&store).expect("map directory sort backing files");
        let mut file_reader = BufReader::new(File::open(&file_output).expect("open sorted files"));
        let mut names = Vec::new();
        for _ in 0..result.file_count {
            let mut bytes = vec![0u8; FILE_INDEX_ENTRY_SIZE as usize];
            file_reader
                .read_exact(&mut bytes)
                .expect("read sorted file index");
            let mut cursor = StoreCursor::new(&bytes);
            let _next_offset = cursor.i64().expect("read sorted file next offset");
            let record_offset = cursor.i64().expect("read sorted file record offset");
            let record_length = cursor.i64().expect("read sorted file record length");
            let _selection_index = cursor.i64().expect("read sorted file selection index");
            names.push(
                reader
                    .read_file_summary(record_offset, record_length)
                    .expect("read sorted file summary")
                    .name,
            );
        }
        assert_eq!(names, vec!["a.txt", "b.txt", "d.txt"]);

        let mut directory_reader =
            BufReader::new(File::open(&directory_output).expect("open sorted directories"));
        let mut bytes = vec![0u8; DIRECTORY_INDEX_ENTRY_SIZE as usize];
        directory_reader
            .read_exact(&mut bytes)
            .expect("read sorted directory index");
        let mut cursor = StoreCursor::new(&bytes);
        assert_eq!(cursor.i64().expect("read directory next offset"), -1);
        assert_eq!(
            reader
                .read_directory_name(
                    &reader
                        .read_directory_header(cursor.i64().expect("read directory record offset"))
                        .expect("read child directory header")
                )
                .expect("read child directory name"),
            "sub"
        );

        drop(directory_reader);
        drop(file_reader);
        drop(reader);
        drop(store);
        drop(context);
        let _ = std::fs::remove_dir_all(root);
    }
}
