#![allow(non_snake_case)]

use md5::Md5;
use rustc_hash::FxHashMap;
use sha1::Sha1;
use sha2::{Digest, Sha256, Sha512};
use std::alloc::{GlobalAlloc, Layout};
use std::collections::VecDeque;
use std::ffi::{OsStr, c_void};
use std::io::{self, Write};
use std::mem::ManuallyDrop;
use std::os::windows::ffi::OsStrExt;
use std::os::windows::io::AsRawHandle;
use std::ptr::{NonNull, null, null_mut};
use std::sync::atomic::{AtomicU32, AtomicU64, Ordering};
use std::sync::{Arc, Condvar, Mutex};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};
use windows_sys::Win32::Foundation::{
    CloseHandle, ERROR_ACCESS_DENIED, ERROR_BAD_NET_NAME, ERROR_BAD_PATHNAME, ERROR_DIRECTORY,
    ERROR_FILE_NOT_FOUND, ERROR_FILENAME_EXCED_RANGE, ERROR_HANDLE_EOF, ERROR_INVALID_HANDLE,
    ERROR_INVALID_NAME, ERROR_INVALID_PARAMETER, ERROR_IO_PENDING, ERROR_LOCK_VIOLATION,
    ERROR_NETWORK_ACCESS_DENIED, ERROR_NOT_FOUND, ERROR_NOT_SUPPORTED, ERROR_PATH_NOT_FOUND,
    ERROR_SHARING_VIOLATION, GENERIC_READ, HANDLE, INVALID_HANDLE_VALUE, WAIT_FAILED,
    WAIT_OBJECT_0, WAIT_TIMEOUT,
};
use windows_sys::Win32::Storage::FileSystem::{
    CreateFileW, FILE_ALIGNMENT_INFO, FILE_ATTRIBUTE_NORMAL, FILE_FLAG_NO_BUFFERING,
    FILE_FLAG_OVERLAPPED, FILE_SHARE_DELETE, FILE_SHARE_READ, FILE_SHARE_WRITE, FILE_STORAGE_INFO,
    FileAlignmentInfo, FileStorageInfo, GetFileInformationByHandleEx, GetFileSizeEx, OPEN_EXISTING,
    ReadFile,
};
use windows_sys::Win32::Storage::IscsiDisc::{
    IOCTL_SCSI_PASS_THROUGH_DIRECT, SCSI_IOCTL_DATA_IN, SCSI_IOCTL_DATA_UNSPECIFIED,
    SCSI_PASS_THROUGH_DIRECT,
};
use windows_sys::Win32::System::IO::{
    CancelIoEx, CancelSynchronousIo, CreateIoCompletionPort, DeviceIoControl, GetOverlappedResult,
    GetQueuedCompletionStatusEx, OVERLAPPED, OVERLAPPED_ENTRY, PostQueuedCompletionStatus,
};
use windows_sys::Win32::System::Ioctl::{
    AtaDataTypeIdentify, DEVICE_SEEK_PENALTY_DESCRIPTOR, IOCTL_STORAGE_QUERY_PROPERTY,
    PropertyStandardQuery, ProtocolTypeAta, STORAGE_PROPERTY_QUERY,
    STORAGE_PROTOCOL_DATA_DESCRIPTOR, STORAGE_PROTOCOL_SPECIFIC_DATA,
    StorageDeviceProtocolSpecificProperty, StorageDeviceSeekPenaltyProperty,
};
use windows_sys::Win32::System::Memory::{
    CreateFileMappingW, FILE_MAP_ALL_ACCESS, MEMORY_MAPPED_VIEW_ADDRESS, MapViewOfFile,
    OpenFileMappingW, PAGE_READWRITE, UnmapViewOfFile,
};
use windows_sys::Win32::System::Threading::{
    CreateEventW, CreateSemaphoreW, EVENT_ALL_ACCESS, GetCurrentThreadId, OpenEventW,
    OpenSemaphoreW, OpenThread, ReleaseSemaphore, SEMAPHORE_ALL_ACCESS, SetEvent, THREAD_TERMINATE,
    WaitForSingleObject,
};

#[global_allocator]
static GLOBAL_ALLOCATOR: mimalloc::MiMalloc = mimalloc::MiMalloc;

mod fast_reader;

pub use fast_reader::*;
