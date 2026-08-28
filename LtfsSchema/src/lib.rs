#![allow(clippy::missing_safety_doc)]

use quick_xml::escape::{escape, unescape};
use quick_xml::events::{BytesEnd, BytesStart, BytesText, Event};
use quick_xml::{Reader, Writer};
use std::fs::{File, OpenOptions};
use std::io::{BufRead, BufReader, BufWriter, Cursor, Read, Seek, SeekFrom, Write};
use std::path::{Path, PathBuf};
use std::ptr;
use std::slice;
use std::str;
use std::sync::{Mutex, OnceLock};

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

#[repr(C)]
#[derive(Clone, Copy, Default)]
pub struct LscStoreDirectoryIndexEntry {
    pub struct_size: u32,
    pub abi_version: u32,
    pub next_offset: i64,
    pub record_offset: i64,
    pub selection_index: i64,
}

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
        })
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
            let mut buffer = Vec::new();
            while depth > 0 {
                let event = reader
                    .read_event_into(&mut buffer)
                    .map_err(|error| error.to_string())?
                    .into_owned();
                buffer.clear();
                match event {
                    Event::Start(value) => {
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
                        depth -= 1;
                    }
                    Event::Text(value) => writer
                        .write_event(Event::Text(value))
                        .map_err(|error| error.to_string())?,
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
                    }
                    Event::Text(value) => writer
                        .write_event(Event::Text(value))
                        .map_err(|error| error.to_string())?,
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
    let mut buffer = Vec::new();
    while depth > 0 {
        let event = reader
            .read_event_into(&mut buffer)
            .map_err(|error| error.to_string())?
            .into_owned();
        buffer.clear();
        match event {
            Event::Start(value) => {
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
            }
            Event::Text(value) => writer
                .write_event(Event::Text(value))
                .map_err(|error| error.to_string())?,
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

struct MergeSourceResult {
    store: StoreOutput,
    files: IndexChain,
    directories: IndexChain,
    total_files: i64,
    total_directories: i64,
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

    fn parse_merge_contents(mut self) -> Result<MergeSourceResult, String> {
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

        Ok(MergeSourceResult {
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

fn schema_context_from_files(
    input_paths: Vec<PathBuf>,
    root_name: String,
    paths: [PathBuf; 5],
) -> Result<Box<SchemaContext>, String> {
    let mut store = StoreOutput::new(&paths)?;
    let mut root_state = store.begin_directory()?;

    for input_path in input_paths {
        let input = File::open(&input_path)
            .map_err(|error| format!("cannot open schema {}: {error}", input_path.display()))?;
        let reader = Reader::from_reader(BufReader::with_capacity(64 * 1024, input));
        let barcode = input_path
            .file_stem()
            .map(|value| value.to_string_lossy().into_owned())
            .unwrap_or_default();
        let parser = SchemaParser::new(reader, store).with_barcode(barcode);
        let MergeSourceResult {
            store: next_store,
            files,
            directories,
            total_files,
            total_directories,
        } = parser.parse_merge_contents()?;
        store = next_store;
        store.join_file_chains(&mut root_state.files, &files)?;
        store.join_directory_chains(&mut root_state.directories, &directories)?;
        root_state.total_file_count = root_state
            .total_file_count
            .checked_add(total_files)
            .ok_or_else(|| invalid("too many files in merged schema"))?;
        root_state.total_directory_count = root_state
            .total_directory_count
            .checked_add(total_directories)
            .ok_or_else(|| invalid("too many directories in merged schema"))?;
    }

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
            ..Default::default()
        },
        ..Default::default()
    };
    Ok(Box::new(SchemaContext { result, metadata }))
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
            Ok(Box::into_raw(Box::new(SchemaWriter {
                writer: Some(Writer::new(BufWriter::with_capacity(64 * 1024, file))),
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
        writer_mut(unsafe { &mut *writer })?
            .get_mut()
            .write_all(&bytes)
            .map_err(|error| error.to_string())
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
            ..Default::default()
        };
        let xml = String::from_utf8(serialize_file(&value).expect("serialize file"))
            .expect("UTF-8 file XML");
        assert!(xml.contains("中文 &amp; &lt;name&gt;.txt"));
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
        let first_text = r#"<ltfsindex version="2.4.0"><directory><name>root</name><contents><file><name>a.txt</name><length>1</length></file><directory><name>sub</name><contents><file><name>b.txt</name><length>2</length></file></contents></directory></contents></directory></ltfsindex>"#;
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
}
