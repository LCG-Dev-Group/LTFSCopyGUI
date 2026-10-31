//! C ABI entry points and handle lifecycle.

use quick_xml::events::BytesDecl;
use quick_xml::events::BytesEnd;
use quick_xml::events::BytesStart;
use quick_xml::events::Event;
use std::ffi::c_void;
use std::fs::File;
use std::fs::OpenOptions;
use std::io::BufWriter;
use std::path::Path;
use std::path::PathBuf;
use std::str;
use std::sync::Mutex;

use super::{
    DirectorySortOptions, FileContext, LSC_ERROR, LSC_INVALID_ARGUMENT, LSC_OK, LscExtent,
    LscFileInfo, LscFileInput, LscSchemaMetadata, LscSchemaResult, LscStoreDirectoryIndexEntry,
    LscStoreDirectoryInfo, LscStoreDirectorySortProgressCallback, LscStoreDirectorySortResult,
    LscStoreFileIndexEntry, LscStoreFileSummary, LscStoreSearchProgressCallback,
    LscStoreSearchResult, LscStoreTapeSortProgressCallback, LscStoreTapeSortResult,
    ParsedFileSummary, SchemaContext, SchemaWriter, StoreContext, StoreSearchComputation,
    StoreSearchOptions, TapeSortOptions, byte_slice, copy_bytes, copy_bytes_wide,
    copy_store_file_record_to_writer, copy_utf16, ffi_call, ffi_call_value, file_data_from_input,
    file_string, get_schema_string, invalid, last_error, new_xml_writer, parse_file_bytes,
    parse_file_name_bytes, parse_file_summary_bytes, read_store_at, read_store_directory_header,
    read_store_directory_scalars, schema_context_from_file, schema_context_from_files,
    search_store, serialize_file, set_last_error, sort_directory_children, sort_tape_files,
    store_directory_file_bytes, store_directory_index_entry, store_file_index_entry, utf16_string,
    write_fragment, write_xml_text, writer_mut, writer_name,
};

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
                StoreSearchOptions {
                    root_record_offset,
                    root_path,
                    keyword,
                    case_sensitive: case_sensitive != 0,
                    resume_kind,
                    resume_record_offset,
                    callback,
                    user_data,
                },
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
                TapeSortOptions {
                    root_file_index_offset,
                    root_file_count,
                    root_directory_index_offset,
                    root_directory_count,
                    selection_path: &selection_path,
                    output_path: &output_path,
                    callback,
                    user_data,
                },
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
                DirectorySortOptions {
                    directory_record_offset,
                    sort_mode,
                    locale_name,
                    file_target_index_offset,
                    directory_target_index_offset,
                    file_output_path: &file_output_path,
                    directory_output_path: &directory_output_path,
                    callback,
                    user_data,
                },
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
            parse_file_name_bytes(&bytes)
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
    // SAFETY: the caller supplies a valid context for this synchronous call.
    let extents = unsafe { &(*context).data.extents };
    let Some(value) = extents.get(index as usize) else {
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
