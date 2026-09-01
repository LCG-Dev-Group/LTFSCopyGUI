#![allow(non_snake_case)]

use md5::Md5;
use rustc_hash::FxHashMap;
use sha1::Sha1;
use sha2::{Digest, Sha256, Sha512};
use std::alloc::{GlobalAlloc, Layout};
use std::collections::VecDeque;
use std::ffi::OsStr;
use std::io::{self, Write};
use std::mem::ManuallyDrop;
use std::os::windows::ffi::OsStrExt;
use std::os::windows::io::AsRawHandle;
use std::ptr::{NonNull, null, null_mut};
use std::sync::atomic::{AtomicU64, Ordering};
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
use windows_sys::Win32::System::Threading::{CreateEventW, SetEvent, WaitForSingleObject};

#[global_allocator]
static GLOBAL_ALLOCATOR: mimalloc::MiMalloc = mimalloc::MiMalloc;

const FLAG_EOF: u32 = 1;
const IO_QUEUE_DEPTH: usize = 16;
const HASH_CHUNK_SIZE: usize = 1024 * 1024;
const DEFAULT_DIRECT_IO_ALIGNMENT: usize = 4096;
const MIN_DIRECT_BUFFER_ALIGNMENT: usize = 64 * 1024;
const CANCEL_POLL_MS: u32 = 100;
const DEFAULT_READ_STALL_TIMEOUT_MS: u32 = 30_000;
const DEFAULT_IO_CANCEL_GRACE_MS: u32 = 5_000;
const DEFAULT_MAX_CONSECUTIVE_FILE_RETRIES: u32 = 3;
const DEFAULT_FILE_RETRY_BASE_DELAY_MS: u32 = 1_000;
const MIN_READ_STALL_TIMEOUT_MS: u32 = 1_000;
const MAX_READ_STALL_TIMEOUT_MS: u32 = 3_600_000;
const MIN_IO_CANCEL_GRACE_MS: u32 = 100;
const MAX_IO_CANCEL_GRACE_MS: u32 = 60_000;
const MAX_FILE_RETRIES: u32 = 10;
const MIN_FILE_RETRY_BASE_DELAY_MS: u32 = 100;
const MAX_FILE_RETRY_BASE_DELAY_MS: u32 = 60_000;
const ATA_NOMINAL_ROTATION_RATE_MIN_RPM: u16 = 0x0401;

#[cfg(all(debug_assertions, not(test)))]
struct DebugLog {
    started: Instant,
    file: Mutex<Option<std::fs::File>>,
}

#[cfg(all(debug_assertions, not(test)))]
static DEBUG_LOG: std::sync::OnceLock<DebugLog> = std::sync::OnceLock::new();

#[cfg(all(debug_assertions, not(test)))]
fn debug_log_impl(arguments: std::fmt::Arguments<'_>) {
    let logger = DEBUG_LOG.get_or_init(|| {
        let file_name = format!("lfr-{}.log", std::process::id());
        let path = std::env::current_dir()
            .unwrap_or_else(|_| std::path::PathBuf::from("."))
            .join(file_name);
        let file = match std::fs::OpenOptions::new()
            .create(true)
            .append(true)
            .open(&path)
        {
            Ok(mut file) => {
                writeln!(
                    file,
                    "\nlog-open\tpid={}\tpath={}",
                    std::process::id(),
                    path.display()
                )
                .ok();
                file.flush().ok();
                Some(file)
            }
            Err(error) => {
                eprintln!(
                    "LFR_DEBUG_LOG_OPEN_ERROR\tpath={}\terror={error}",
                    path.display()
                );
                None
            }
        };
        DebugLog {
            started: Instant::now(),
            file: Mutex::new(file),
        }
    });

    let mut file = match logger.file.lock() {
        Ok(file) => file,
        Err(poisoned) => {
            logger.file.clear_poison();
            poisoned.into_inner()
        }
    };
    let Some(file) = file.as_mut() else {
        return;
    };
    let unix_ms = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap_or_default()
        .as_millis();
    let elapsed_ms = logger.started.elapsed().as_millis();
    let current_thread = thread::current();
    let thread_name = current_thread.name().unwrap_or("-");
    writeln!(
        file,
        "unix_ms={unix_ms}\telapsed_ms={elapsed_ms}\tpid={}\ttid={:?}\tthread={thread_name}\t{arguments}",
        std::process::id(),
        current_thread.id()
    )
    .ok();
    file.flush().ok();
}

#[cfg(all(debug_assertions, not(test)))]
macro_rules! debug_log {
    ($($argument:tt)*) => {
        debug_log_impl(format_args!($($argument)*))
    };
}

#[cfg(any(not(debug_assertions), test))]
macro_rules! debug_log {
    ($($argument:tt)*) => {};
}

// C-FFI
pub const LFR_ABI_VERSION: u32 = 3;
pub const LFR_OK: i32 = 0;
pub const LFR_TIMEOUT: i32 = 1;
pub const LFR_DONE: i32 = 2;
pub const LFR_BUFFER_TOO_SMALL: i32 = 3;
pub const LFR_INVALID: i32 = -1;
pub const LFR_ERROR: i32 = -2;
pub const LFR_CANCELLED: i32 = -3;
pub const LFR_HASH_SHA1: u32 = 1 << 0;
pub const LFR_HASH_SHA256: u32 = 1 << 1;
pub const LFR_HASH_SHA512: u32 = 1 << 2;
pub const LFR_HASH_MD5: u32 = 1 << 3;
pub const LFR_HASH_CRC32: u32 = 1 << 4;
pub const LFR_HASH_BLAKE3: u32 = 1 << 5;
pub const LFR_HASH_XXH3: u32 = 1 << 6;
pub const LFR_HASH_XXH128: u32 = 1 << 7;
const LFR_HASH_ALL: u32 = LFR_HASH_SHA1
    | LFR_HASH_SHA256
    | LFR_HASH_SHA512
    | LFR_HASH_MD5
    | LFR_HASH_CRC32
    | LFR_HASH_BLAKE3
    | LFR_HASH_XXH3
    | LFR_HASH_XXH128;

#[repr(C)]
pub struct LfrConfig {
    pub struct_size: u32,
    pub abi_version: u32,
    pub slot_size: u32,
    pub read_chunk_size: u32,
    pub queue_depth: u32,
    pub capacity_bytes: u64,
    pub small_open_concurrency: u32,
    pub small_active_files: u32,
    pub small_inflight_bytes: u64,
    pub small_threshold: u64,
    pub hash_mask: u32,
    pub next_file_prime_depth: u32,
    pub read_stall_timeout_ms: u32,
    pub io_cancel_grace_ms: u32,
    pub max_consecutive_file_retries: u32,
    pub file_retry_base_delay_ms: u32,
}

#[repr(C)]
pub struct LfrSlot {
    pub token: u64,
    pub file_index: i64,
    pub file_offset: u64,
    pub data: *const u8,
    pub length: u32,
    pub flags: u32,
}

#[repr(C)]
pub struct LfrStats {
    pub struct_size: u32,
    pub abi_version: u32,
    pub bytes_read: u64,
    pub bytes_published: u64,
    pub buffered_bytes: u64,
    pub occupied_slots: u64,
    pub read_wait_ns: u64,
    pub hash_ns: u64,
    pub publish_wait_ns: u64,
}

#[derive(Clone)]
struct NativeFileTask {
    index: u64,
    len: u64,
    path: String,
    selected: bool,
}

struct NativeSlot {
    buffer: Box<[u8]>,
    token: u64,
    file_index: u64,
    file_offset: u64,
    length: u32,
    flags: u32,
    full: bool,
}

struct NativeState {
    slots: Vec<NativeSlot>,
    files: FxHashMap<u64, NativeFileTask>,
    file_order: Vec<u64>,
    write_index: u64,
    read_index: u64,
    buffered_bytes: u64,
    occupied_slots: usize,
    selected_bytes: u64,
    started: bool,
    done: bool,
    cancelled: bool,
    error: String,
    results: FxHashMap<u64, String>,
}

#[derive(Default)]
struct NativeTelemetry {
    bytes_read: AtomicU64,
    bytes_published: AtomicU64,
    read_wait_ns: Arc<AtomicU64>,
    hash_ns: AtomicU64,
    publish_wait_ns: AtomicU64,
}

struct NativeShared {
    state: Mutex<NativeState>,
    changed: Condvar,
    telemetry: NativeTelemetry,
    worker_watch: Mutex<NativeWorkerWatch>,
}

struct NativeWorkerWatch {
    file_index: Option<u64>,
    stage: &'static str,
    last_progress: Instant,
    cancel_requested_at: Option<Instant>,
    cancel_error: Option<i32>,
}

struct NativeConfig {
    slot_size: usize,
    read_chunk_size: usize,
    queue_depth: usize,
    capacity_bytes: u64,
    small_open_concurrency: usize,
    small_active_files: usize,
    small_inflight_bytes: usize,
    small_threshold: u64,
    hash_mask: u32,
    next_file_prime_depth: usize,
    io_policy: ReaderIoPolicy,
}

#[derive(Clone, Copy)]
struct ReaderIoPolicy {
    read_stall_timeout: Duration,
    cancel_grace: Duration,
    max_consecutive_file_retries: u8,
    retry_base_delay_ms: u64,
}

impl Default for ReaderIoPolicy {
    fn default() -> Self {
        Self {
            read_stall_timeout: Duration::from_millis(DEFAULT_READ_STALL_TIMEOUT_MS as u64),
            cancel_grace: Duration::from_millis(DEFAULT_IO_CANCEL_GRACE_MS as u64),
            max_consecutive_file_retries: DEFAULT_MAX_CONSECUTIVE_FILE_RETRIES as u8,
            retry_base_delay_ms: DEFAULT_FILE_RETRY_BASE_DELAY_MS as u64,
        }
    }
}

pub struct LfrContext {
    shared: Arc<NativeShared>,
    config: NativeConfig,
    cancel_event: Handle,
    worker: Mutex<Option<JoinHandle<()>>>,
    worker_watchdog: Mutex<Option<JoinHandle<()>>>,
}

fn cancelled_error() -> io::Error {
    io::Error::new(io::ErrorKind::Interrupted, "fastreader operation cancelled")
}

fn is_cancelled(cancel_event: HANDLE) -> bool {
    !cancel_event.is_null() && unsafe { WaitForSingleObject(cancel_event, 0) == WAIT_OBJECT_0 }
}

fn file_retry_delay(policy: ReaderIoPolicy, retry_number: u8) -> Duration {
    debug_assert!(retry_number > 0);
    let shift = u32::from(retry_number.saturating_sub(1)).min(31);
    Duration::from_millis(policy.retry_base_delay_ms.saturating_mul(1u64 << shift))
}

fn wait_for_file_retry(
    cancel_event: HANDLE,
    policy: ReaderIoPolicy,
    retry_number: u8,
) -> io::Result<()> {
    let delay = file_retry_delay(policy, retry_number);
    if cancel_event.is_null() {
        thread::sleep(delay);
        return Ok(());
    }

    let timeout_ms = delay.as_millis().min(u32::MAX as u128) as u32;
    match unsafe { WaitForSingleObject(cancel_event, timeout_ms) } {
        WAIT_OBJECT_0 => Err(cancelled_error()),
        WAIT_TIMEOUT => Ok(()),
        WAIT_FAILED => Err(io::Error::last_os_error()),
        result => Err(io::Error::other(format!(
            "unexpected retry wait result: {result}"
        ))),
    }
}

fn wide(s: &str) -> Vec<u16> {
    OsStr::new(s).encode_wide().chain(Some(0)).collect()
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
struct DriveMediaInfo {
    incurs_seek_penalty: Option<bool>,
    nominal_rotation_rate: Option<u16>,
}

impl DriveMediaInfo {
    fn is_hdd(self) -> bool {
        self.incurs_seek_penalty == Some(true)
            || self
                .nominal_rotation_rate
                .is_some_and(|rpm| (ATA_NOMINAL_ROTATION_RATE_MIN_RPM..u16::MAX).contains(&rpm))
    }
}

fn ascii_drive_letter(value: u8) -> Option<char> {
    value
        .is_ascii_alphabetic()
        .then(|| (value as char).to_ascii_uppercase())
}

/// Extracts a device path suitable for storage IOCTLs without changing the
/// path that is later passed to CreateFileW.
fn storage_device_path(path: &str) -> Option<String> {
    let bytes = path.as_bytes();

    if bytes.len() >= 4 && &bytes[..4] == br"\\?\" {
        let tail = &bytes[4..];
        if tail.len() >= 4 && tail[..4].eq_ignore_ascii_case(b"UNC\\") {
            return None;
        }
        if tail.len() >= 2 {
            let drive = ascii_drive_letter(tail[0])?;
            // The second form accepts the extended drive spelling supplied
            // by the managed catalog (\\?\D\...), while leaving that source
            // spelling untouched for actual file opens.
            if tail[1] == b':' || tail[1] == b'\\' {
                return Some(format!(r"\\.\{drive}:"));
            }
        }
        if tail.len() >= 8 && tail[..7].eq_ignore_ascii_case(b"Volume{") {
            let closing_brace = tail.iter().position(|byte| *byte == b'}')?;
            let volume = std::str::from_utf8(&tail[..=closing_brace]).ok()?;
            return Some(format!(r"\\?\{volume}"));
        }
        return None;
    }

    if bytes.len() >= 4 && &bytes[..4] == br"\\.\" {
        let tail = &bytes[4..];
        if tail.len() >= 2 && tail[1] == b':' {
            let drive = ascii_drive_letter(tail[0])?;
            return Some(format!(r"\\.\{drive}:"));
        }
        return None;
    }

    if bytes.starts_with(br"\\") {
        return None;
    }
    if bytes.len() >= 2 && bytes[1] == b':' {
        let drive = ascii_drive_letter(bytes[0])?;
        return Some(format!(r"\\.\{drive}:"));
    }
    None
}

fn query_seek_penalty(handle: HANDLE) -> Option<bool> {
    let query = STORAGE_PROPERTY_QUERY {
        PropertyId: StorageDeviceSeekPenaltyProperty,
        QueryType: PropertyStandardQuery,
        AdditionalParameters: [0],
    };
    let mut descriptor = DEVICE_SEEK_PENALTY_DESCRIPTOR::default();
    let mut returned = 0u32;
    let succeeded = unsafe {
        DeviceIoControl(
            handle,
            IOCTL_STORAGE_QUERY_PROPERTY,
            (&raw const query).cast(),
            std::mem::size_of_val(&query) as u32,
            (&raw mut descriptor).cast(),
            std::mem::size_of_val(&descriptor) as u32,
            &mut returned,
            null_mut(),
        )
    };
    (succeeded != 0
        && returned as usize
            > std::mem::offset_of!(DEVICE_SEEK_PENALTY_DESCRIPTOR, IncursSeekPenalty))
    .then_some(descriptor.IncursSeekPenalty)
}

#[repr(C)]
struct AtaIdentifyQuery {
    property_id: i32,
    query_type: i32,
    protocol: STORAGE_PROTOCOL_SPECIFIC_DATA,
    identify: [u8; 512],
}

fn ata_rotation_rate_from_query(query: &AtaIdentifyQuery, returned: u32) -> Option<u16> {
    let descriptor_size = std::mem::size_of::<STORAGE_PROTOCOL_DATA_DESCRIPTOR>();
    if returned < descriptor_size as u32
        || query.property_id as u32 != descriptor_size as u32
        || (query.query_type as u32) < descriptor_size as u32
    {
        return None;
    }

    let protocol = &query.protocol;
    let data_start = 2 * std::mem::size_of::<u32>() + protocol.ProtocolDataOffset as usize;
    let data_end = data_start.checked_add(protocol.ProtocolDataLength as usize)?;
    let query_bytes = unsafe {
        std::slice::from_raw_parts(
            (&raw const *query).cast::<u8>(),
            std::mem::size_of_val(query),
        )
    };
    if protocol.ProtocolType != ProtocolTypeAta
        || protocol.ProtocolDataLength < 512
        || data_end > query_bytes.len()
        || data_end > returned as usize
    {
        return None;
    }

    let word_217 = data_start + 217 * 2;
    Some(u16::from_le_bytes([
        query_bytes[word_217],
        query_bytes[word_217 + 1],
    ]))
}

fn query_ata_rotation_rate(handle: HANDLE) -> Option<u16> {
    let mut query = AtaIdentifyQuery {
        property_id: StorageDeviceProtocolSpecificProperty,
        query_type: PropertyStandardQuery,
        protocol: STORAGE_PROTOCOL_SPECIFIC_DATA {
            ProtocolType: ProtocolTypeAta,
            DataType: AtaDataTypeIdentify as u32,
            ProtocolDataOffset: std::mem::size_of::<STORAGE_PROTOCOL_SPECIFIC_DATA>() as u32,
            ProtocolDataLength: 512,
            ..Default::default()
        },
        identify: [0; 512],
    };
    let mut returned = 0u32;
    let succeeded = unsafe {
        DeviceIoControl(
            handle,
            IOCTL_STORAGE_QUERY_PROPERTY,
            (&raw const query).cast(),
            std::mem::size_of_val(&query) as u32,
            (&raw mut query).cast(),
            std::mem::size_of_val(&query) as u32,
            &mut returned,
            null_mut(),
        )
    };
    if succeeded == 0 {
        return None;
    }
    ata_rotation_rate_from_query(&query, returned)
}

fn query_drive_media(device_path: &str) -> Option<DriveMediaInfo> {
    let path_w = wide(device_path);
    let handle = unsafe {
        CreateFileW(
            path_w.as_ptr(),
            0,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            null(),
            OPEN_EXISTING,
            0,
            null_mut(),
        )
    };
    if handle == INVALID_HANDLE_VALUE {
        return None;
    }
    let handle = Handle(handle);
    let result = DriveMediaInfo {
        incurs_seek_penalty: query_seek_penalty(handle.0),
        nominal_rotation_rate: query_ata_rotation_rate(handle.0),
    };
    (result != DriveMediaInfo::default()).then_some(result)
}

fn files_include_hdd(files: &[NativeFileTask]) -> bool {
    let mut media_by_device = FxHashMap::<String, bool>::default();
    files.iter().any(|file| {
        let Some(device_path) = storage_device_path(&file.path) else {
            return false;
        };
        if let Some(is_hdd) = media_by_device.get(&device_path) {
            return *is_hdd;
        }
        let is_hdd = query_drive_media(&device_path).is_some_and(DriveMediaInfo::is_hdd);
        media_by_device.insert(device_path, is_hdd);
        is_hdd
    })
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
struct FileReadScheduling {
    small_open_concurrency: usize,
    small_active_files: usize,
    cross_file_prefetch: bool,
}

fn file_read_scheduling(
    configured_open_concurrency: usize,
    configured_active_files: usize,
    is_hdd: bool,
) -> FileReadScheduling {
    if is_hdd {
        FileReadScheduling {
            small_open_concurrency: 1,
            small_active_files: 1,
            cross_file_prefetch: false,
        }
    } else {
        FileReadScheduling {
            small_open_concurrency: configured_open_concurrency,
            small_active_files: configured_active_files,
            cross_file_prefetch: true,
        }
    }
}

struct Handle(HANDLE);
unsafe impl Send for Handle {}

impl Drop for Handle {
    fn drop(&mut self) {
        unsafe {
            if !self.0.is_null() && self.0 != INVALID_HANDLE_VALUE {
                CloseHandle(self.0);
            }
        }
    }
}

struct Xxh3_64 {
    h: xxhash_rust::xxh3::Xxh3,
}
impl Xxh3_64 {
    fn new() -> Self {
        Self {
            h: xxhash_rust::xxh3::Xxh3::new(),
        }
    }
    fn update(&mut self, data: &[u8]) {
        self.h.update(data);
    }
    fn finish(&self) -> [u8; 8] {
        self.h.digest().to_be_bytes()
    }
}

struct Xxh3_128 {
    h: xxhash_rust::xxh3::Xxh3,
}
impl Xxh3_128 {
    fn new() -> Self {
        Self {
            h: xxhash_rust::xxh3::Xxh3::new(),
        }
    }
    fn update(&mut self, data: &[u8]) {
        self.h.update(data);
    }
    fn finish(&self) -> [u8; 16] {
        self.h.digest128().to_be_bytes()
    }
}

struct HashSet {
    sha1: Option<Sha1>,
    sha256: Option<Sha256>,
    sha512: Option<Sha512>,
    md5: Option<Md5>,
    crc32: Option<crc32fast::Hasher>,
    blake3: Option<blake3::Hasher>,
    xxh3: Option<Xxh3_64>,
    xxh128: Option<Xxh3_128>,
}

fn hex(bytes: &[u8]) -> String {
    const DIGITS: &[u8; 16] = b"0123456789ABCDEF";
    let mut s = String::with_capacity(bytes.len() * 2);
    for &byte in bytes {
        s.push(DIGITS[(byte >> 4) as usize] as char);
        s.push(DIGITS[(byte & 0x0F) as usize] as char);
    }
    s
}

impl HashSet {
    fn new(enabled: &FxHashMap<String, bool>) -> io::Result<Self> {
        Ok(Self {
            sha1: if *enabled.get("SHA1").unwrap_or(&false) {
                Some(Sha1::new())
            } else {
                None
            },
            sha256: if *enabled.get("SHA256").unwrap_or(&false) {
                Some(Sha256::new())
            } else {
                None
            },
            sha512: if *enabled.get("SHA512").unwrap_or(&false) {
                Some(Sha512::new())
            } else {
                None
            },
            md5: if *enabled.get("MD5").unwrap_or(&false) {
                Some(Md5::new())
            } else {
                None
            },
            crc32: if *enabled.get("CRC32").unwrap_or(&false) {
                Some(crc32fast::Hasher::new())
            } else {
                None
            },
            blake3: if *enabled.get("BLAKE3").unwrap_or(&false) {
                Some(blake3::Hasher::new())
            } else {
                None
            },
            xxh3: if *enabled.get("XxHash3").unwrap_or(&false) {
                Some(Xxh3_64::new())
            } else {
                None
            },
            xxh128: if *enabled.get("XxHash128").unwrap_or(&false) {
                Some(Xxh3_128::new())
            } else {
                None
            },
        })
    }

    fn update(&mut self, slice: &[u8]) -> io::Result<()> {
        if let Some(h) = self.sha1.as_mut() {
            h.update(slice);
        }
        if let Some(h) = self.sha256.as_mut() {
            h.update(slice);
        }
        if let Some(h) = self.sha512.as_mut() {
            h.update(slice);
        }
        if let Some(h) = self.md5.as_mut() {
            h.update(slice);
        }
        if let Some(c) = self.crc32.as_mut() {
            c.update(slice);
        }
        if let Some(h) = self.blake3.as_mut() {
            h.update(slice);
        }
        if let Some(h) = self.xxh3.as_mut() {
            h.update(slice);
        }
        if let Some(h) = self.xxh128.as_mut() {
            h.update(slice);
        }
        Ok(())
    }

    fn finish(&mut self) -> io::Result<String> {
        let mut parts = Vec::new();
        if let Some(h) = self.sha1.take() {
            parts.push(format!("SHA1={}", hex(&h.finalize())));
        }
        if let Some(h) = self.sha256.take() {
            parts.push(format!("SHA256={}", hex(&h.finalize())));
        }
        if let Some(h) = self.sha512.take() {
            parts.push(format!("SHA512={}", hex(&h.finalize())));
        }
        if let Some(h) = self.md5.take() {
            parts.push(format!("MD5={}", hex(&h.finalize())));
        }
        if let Some(c) = self.crc32.take() {
            parts.push(format!("CRC32={}", hex(&c.finalize().to_be_bytes())));
        }
        if let Some(h) = self.blake3.as_ref() {
            parts.push(format!(
                "BLAKE3={}",
                h.finalize().to_hex().to_string().to_uppercase()
            ));
        }
        if let Some(h) = self.xxh3.as_ref() {
            parts.push(format!("XxHash3={}", hex(&h.finish())));
        }
        if let Some(h) = self.xxh128.as_ref() {
            parts.push(format!("XxHash128={}", hex(&h.finish())));
        }
        Ok(parts.join("\t"))
    }
}

#[derive(Clone, Copy)]
enum RequestState {
    Idle,
    InFlight,
    Completed(Result<u32, u32>),
}

struct AlignedBuffer {
    pointer: NonNull<u8>,
    layout: Layout,
}

unsafe impl Send for AlignedBuffer {}

impl AlignedBuffer {
    fn new(len: usize, alignment: usize) -> io::Result<Self> {
        let layout = Layout::from_size_align(len, alignment).map_err(|_| {
            io::Error::new(
                io::ErrorKind::InvalidInput,
                format!("invalid direct-I/O buffer layout: length={len} alignment={alignment}"),
            )
        })?;
        let pointer =
            NonNull::new(unsafe { GLOBAL_ALLOCATOR.alloc_zeroed(layout) }).ok_or_else(|| {
                io::Error::new(
                    io::ErrorKind::OutOfMemory,
                    format!("unable to allocate {len} aligned direct-I/O bytes"),
                )
            })?;
        Ok(Self { pointer, layout })
    }

    fn as_mut_ptr(&mut self) -> *mut u8 {
        self.pointer.as_ptr()
    }

    fn as_slice(&self, len: usize) -> &[u8] {
        debug_assert!(len <= self.layout.size());
        unsafe { std::slice::from_raw_parts(self.pointer.as_ptr(), len) }
    }
}

impl Drop for AlignedBuffer {
    fn drop(&mut self) {
        unsafe { GLOBAL_ALLOCATOR.dealloc(self.pointer.as_ptr(), self.layout) }
    }
}

fn valid_direct_io_alignment(value: u32) -> Option<usize> {
    let value = value as usize;
    (value >= 512 && value.is_power_of_two() && value <= 1024 * 1024).then_some(value)
}

fn direct_io_requirements(file: HANDLE) -> (usize, usize) {
    let mut transfer_alignment = DEFAULT_DIRECT_IO_ALIGNMENT;
    let mut buffer_alignment = MIN_DIRECT_BUFFER_ALIGNMENT;

    let mut storage = FILE_STORAGE_INFO::default();
    if unsafe {
        GetFileInformationByHandleEx(
            file,
            FileStorageInfo,
            (&raw mut storage).cast(),
            std::mem::size_of_val(&storage) as u32,
        )
    } != 0
    {
        if let Some(value) = valid_direct_io_alignment(storage.LogicalBytesPerSector) {
            transfer_alignment = value;
        }
        for value in [
            storage.PhysicalBytesPerSectorForAtomicity,
            storage.PhysicalBytesPerSectorForPerformance,
            storage.FileSystemEffectivePhysicalBytesPerSectorForAtomicity,
        ] {
            if let Some(value) = valid_direct_io_alignment(value) {
                buffer_alignment = buffer_alignment.max(value);
            }
        }
    }

    let mut alignment = FILE_ALIGNMENT_INFO::default();
    if unsafe {
        GetFileInformationByHandleEx(
            file,
            FileAlignmentInfo,
            (&raw mut alignment).cast(),
            std::mem::size_of_val(&alignment) as u32,
        )
    } != 0
        && let Some(value) = alignment
            .AlignmentRequirement
            .checked_add(1)
            .and_then(valid_direct_io_alignment)
    {
        buffer_alignment = buffer_alignment.max(value);
    }

    (transfer_alignment, buffer_alignment)
}

fn align_up(value: usize, alignment: usize) -> io::Result<usize> {
    debug_assert!(alignment.is_power_of_two());
    value
        .checked_add(alignment - 1)
        .map(|value| value & !(alignment - 1))
        .ok_or_else(|| {
            io::Error::new(
                io::ErrorKind::InvalidInput,
                "direct-I/O request size overflow",
            )
        })
}

struct ReadRequest {
    overlapped: OVERLAPPED,
    buffer: AlignedBuffer,
    offset: u64,
    requested: u32,
    logical: u32,
    state: RequestState,
}

impl ReadRequest {
    fn new(buffer_size: usize, buffer_alignment: usize) -> io::Result<Self> {
        Ok(Self {
            overlapped: OVERLAPPED::default(),
            buffer: AlignedBuffer::new(buffer_size, buffer_alignment)?,
            offset: 0,
            requested: 0,
            logical: 0,
            state: RequestState::Idle,
        })
    }
}

struct AsyncSequentialReader {
    file: ManuallyDrop<Handle>,
    completion_port: ManuallyDrop<Handle>,
    requests: Vec<ReadRequest>,
    file_len: u64,
    chunk_size: usize,
    transfer_alignment: usize,
    next_submit: u64,
    next_consume: u64,
    outstanding: usize,
    cancel_event: HANDLE,
    io_policy: ReaderIoPolicy,
    read_wait_counter: Option<Arc<AtomicU64>>,
    last_completion_progress: Instant,
}

enum AsyncReaderRunError {
    Io(io::Error),
    Consumer(io::Error),
}

fn completed_request_at(requests: &[ReadRequest], offset: u64) -> Option<usize> {
    requests.iter().position(|request| {
        request.offset == offset && matches!(request.state, RequestState::Completed(_))
    })
}

impl AsyncSequentialReader {
    fn open_from_with_depth(
        path: &str,
        expected_len: u64,
        start_offset: u64,
        chunk_size: usize,
        cancel_event: HANDLE,
        queue_depth: usize,
        io_policy: ReaderIoPolicy,
    ) -> io::Result<Self> {
        if is_cancelled(cancel_event) {
            return Err(cancelled_error());
        }
        if chunk_size == 0 || chunk_size > u32::MAX as usize {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "invalid asynchronous read chunk size",
            ));
        }
        if !(1..=128).contains(&queue_depth) {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "invalid asynchronous read queue depth",
            ));
        }
        if start_offset > expected_len {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                format!(
                    "invalid asynchronous read start offset: offset={start_offset} length={expected_len}"
                ),
            ));
        }

        let path_w = wide(path);
        let file = unsafe {
            CreateFileW(
                path_w.as_ptr(),
                GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                null(),
                OPEN_EXISTING,
                FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OVERLAPPED | FILE_FLAG_NO_BUFFERING,
                null_mut(),
            )
        };
        if file == INVALID_HANDLE_VALUE {
            return Err(io::Error::last_os_error());
        }
        let file = Handle(file);
        let (transfer_alignment, buffer_alignment) = direct_io_requirements(file.0);
        if !chunk_size.is_multiple_of(transfer_alignment) {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                format!(
                    "direct-I/O chunk size is not sector aligned: chunk={chunk_size} alignment={transfer_alignment}"
                ),
            ));
        }

        let mut actual_len = 0i64;
        if unsafe { GetFileSizeEx(file.0, &mut actual_len) } == 0 {
            return Err(io::Error::last_os_error());
        }
        if actual_len < 0 || actual_len as u64 != expected_len {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                format!("file length changed: expected={expected_len} actual={actual_len}"),
            ));
        }

        let completion_port = unsafe { CreateIoCompletionPort(file.0, null_mut(), 0, 0) };
        if completion_port.is_null() {
            return Err(io::Error::last_os_error());
        }
        let completion_port = Handle(completion_port);
        let remaining_len = expected_len - start_offset;
        let request_count = if remaining_len == 0 {
            0
        } else {
            remaining_len
                .div_ceil(chunk_size as u64)
                .min(queue_depth as u64) as usize
        };
        let request_buffer_size = align_up(
            remaining_len.min(chunk_size as u64) as usize,
            transfer_alignment,
        )?;
        let requests = (0..request_count)
            .map(|_| ReadRequest::new(request_buffer_size, buffer_alignment))
            .collect::<io::Result<Vec<_>>>()?;

        Ok(Self {
            file: ManuallyDrop::new(file),
            completion_port: ManuallyDrop::new(completion_port),
            requests,
            file_len: expected_len,
            chunk_size,
            transfer_alignment,
            next_submit: start_offset,
            next_consume: start_offset,
            outstanding: 0,
            cancel_event,
            io_policy,
            read_wait_counter: None,
            last_completion_progress: Instant::now(),
        })
    }

    fn submit(&mut self, request_index: usize) -> io::Result<()> {
        if is_cancelled(self.cancel_event) {
            return Err(cancelled_error());
        }
        if self.next_submit >= self.file_len {
            return Ok(());
        }
        let request = &mut self.requests[request_index];
        debug_assert!(matches!(request.state, RequestState::Idle));
        let offset = self.next_submit;
        let logical = (self.file_len - offset).min(self.chunk_size as u64) as u32;
        let requested = align_up(logical as usize, self.transfer_alignment)? as u32;
        request.overlapped = OVERLAPPED::default();
        request.overlapped.Anonymous.Anonymous.Offset = offset as u32;
        request.overlapped.Anonymous.Anonymous.OffsetHigh = (offset >> 32) as u32;
        request.offset = offset;
        request.requested = requested;
        request.logical = logical;
        request.state = RequestState::InFlight;

        let ok = unsafe {
            ReadFile(
                self.file.0,
                request.buffer.as_mut_ptr(),
                requested,
                null_mut(),
                &mut request.overlapped,
            )
        };
        if ok == 0 {
            let error = io::Error::last_os_error();
            if error.raw_os_error() != Some(ERROR_IO_PENDING as i32) {
                request.state = RequestState::Idle;
                return Err(error);
            }
        }
        self.next_submit += logical as u64;
        self.outstanding += 1;
        Ok(())
    }

    fn prime(&mut self) -> io::Result<()> {
        self.prime_limit(self.requests.len())
    }

    fn prime_limit(&mut self, limit: usize) -> io::Result<()> {
        let mut submitted = 0usize;
        for index in 0..self.requests.len() {
            if submitted >= limit || self.next_submit >= self.file_len {
                break;
            }
            if !matches!(self.requests[index].state, RequestState::Idle) {
                continue;
            }
            self.submit(index)?;
            submitted += 1;
        }
        Ok(())
    }

    fn receive_completions(&mut self) -> io::Result<()> {
        let wait_started = Instant::now();
        let mut entries = [OVERLAPPED_ENTRY::default(); IO_QUEUE_DEPTH];
        let mut removed = 0u32;
        if unsafe {
            GetQueuedCompletionStatusEx(
                self.completion_port.0,
                entries.as_mut_ptr(),
                entries.len() as u32,
                &mut removed,
                CANCEL_POLL_MS,
                0,
            )
        } == 0
        {
            self.record_read_wait(wait_started.elapsed());
            let error = io::Error::last_os_error();
            if error.raw_os_error() == Some(WAIT_TIMEOUT as i32) {
                if is_cancelled(self.cancel_event) {
                    return Err(cancelled_error());
                }
                if self.outstanding > 0
                    && self.last_completion_progress.elapsed() >= self.io_policy.read_stall_timeout
                {
                    debug_log!(
                        "event=large-read-stall\tnext_consume={}\tnext_submit={}\toutstanding={}\ttimeout_ms={}",
                        self.next_consume,
                        self.next_submit,
                        self.outstanding,
                        self.io_policy.read_stall_timeout.as_millis()
                    );
                    return Err(io::Error::new(
                        io::ErrorKind::TimedOut,
                        format!(
                            "asynchronous read made no I/O completion progress for {} ms at offset {} with {} requests outstanding",
                            self.io_policy.read_stall_timeout.as_millis(),
                            self.next_consume,
                            self.outstanding
                        ),
                    ));
                }
                return Ok(());
            }
            debug_log!("event=large-read-iocp-error\terror={error}");
            return Err(error);
        }
        self.record_read_wait(wait_started.elapsed());
        if removed > 0 {
            self.last_completion_progress = Instant::now();
        }

        for entry in &entries[..removed as usize] {
            let Some(request) = self
                .requests
                .iter_mut()
                .find(|request| std::ptr::eq(&request.overlapped, entry.lpOverlapped))
            else {
                return Err(io::Error::new(
                    io::ErrorKind::InvalidData,
                    "unknown IOCP completion",
                ));
            };
            if !matches!(request.state, RequestState::InFlight) {
                return Err(io::Error::new(
                    io::ErrorKind::InvalidData,
                    "duplicate IOCP completion",
                ));
            }
            let mut transferred = 0u32;
            let completion = if unsafe {
                GetOverlappedResult(self.file.0, &request.overlapped, &mut transferred, 0)
            } != 0
            {
                Ok(transferred)
            } else {
                Err(io::Error::last_os_error().raw_os_error().unwrap_or(1) as u32)
            };
            request.state = RequestState::Completed(completion);
            self.outstanding -= 1;
        }
        Ok(())
    }

    fn set_read_wait_counter(&mut self, counter: Arc<AtomicU64>) {
        self.read_wait_counter = Some(counter);
    }

    fn record_read_wait(&self, elapsed: Duration) {
        if let Some(counter) = self.read_wait_counter.as_ref() {
            counter.fetch_add(
                elapsed.as_nanos().min(u64::MAX as u128) as u64,
                Ordering::Relaxed,
            );
        }
    }

    fn run<F>(&mut self, mut consume: F) -> Result<(), AsyncReaderRunError>
    where
        F: FnMut(u64, &[u8]) -> io::Result<()>,
    {
        // A reader may already contain a few requests submitted while the
        // preceding file is being consumed. Fill the rest of the queue here.
        self.prime().map_err(AsyncReaderRunError::Io)?;
        while self.next_consume < self.file_len {
            if is_cancelled(self.cancel_event) {
                return Err(AsyncReaderRunError::Io(cancelled_error()));
            }
            let next_ready = completed_request_at(&self.requests, self.next_consume);
            let Some(index) = next_ready else {
                self.receive_completions()
                    .map_err(AsyncReaderRunError::Io)?;
                continue;
            };

            let result = match self.requests[index].state {
                RequestState::Completed(result) => result,
                _ => unreachable!(),
            };
            let transferred = result.map_err(|code| {
                AsyncReaderRunError::Io(io::Error::from_raw_os_error(code as i32))
            })?;
            let requested = self.requests[index].requested;
            let logical = self.requests[index].logical;
            if transferred < logical || transferred > requested {
                return Err(AsyncReaderRunError::Io(io::Error::new(
                    io::ErrorKind::UnexpectedEof,
                    format!(
                        "short direct-I/O read at offset {}: logical={} requested={} actual={}",
                        self.next_consume, logical, requested, transferred
                    ),
                )));
            }
            consume(
                self.requests[index].offset,
                self.requests[index].buffer.as_slice(logical as usize),
            )
            .map_err(AsyncReaderRunError::Consumer)?;
            self.next_consume += logical as u64;
            self.requests[index].state = RequestState::Idle;
            self.submit(index).map_err(AsyncReaderRunError::Io)?;
        }
        Ok(())
    }

    fn abandon_pending_io(&mut self) -> bool {
        // Windows may still dereference the request buffers and OVERLAPPED
        // values, and CloseHandle may block on a wedged remote redirector.
        // Leak both memory and handles as one ownership unit.
        let leaked = std::mem::take(&mut self.requests);
        std::mem::forget(leaked);
        self.outstanding = 0;
        true
    }

    fn cancel_and_drain(&mut self) -> bool {
        if self.outstanding == 0 {
            return false;
        }
        if unsafe { CancelIoEx(self.file.0, null()) } == 0 {
            let code = io::Error::last_os_error().raw_os_error();
            if code != Some(ERROR_NOT_FOUND as i32) {
                // Continue draining: already completed requests may still have queued packets.
            }
        }
        let mut entries = [OVERLAPPED_ENTRY::default(); IO_QUEUE_DEPTH];
        let drain_started = Instant::now();
        while self.outstanding > 0 {
            if drain_started.elapsed() >= self.io_policy.cancel_grace {
                debug_log!(
                    "event=large-read-cancel-drain-stall\toutstanding={}\tgrace_ms={}\taction=abandon",
                    self.outstanding,
                    self.io_policy.cancel_grace.as_millis()
                );
                return self.abandon_pending_io();
            }
            let mut removed = 0u32;
            if unsafe {
                GetQueuedCompletionStatusEx(
                    self.completion_port.0,
                    entries.as_mut_ptr(),
                    entries.len() as u32,
                    &mut removed,
                    CANCEL_POLL_MS,
                    0,
                )
            } == 0
            {
                let error = io::Error::last_os_error();
                if error.raw_os_error() != Some(WAIT_TIMEOUT as i32) {
                    debug_log!(
                        "event=large-read-cancel-drain-error\toutstanding={}\terror={error}\taction=abandon",
                        self.outstanding
                    );
                    return self.abandon_pending_io();
                }
                continue;
            }
            self.outstanding = self.outstanding.saturating_sub(removed as usize);
        }
        false
    }
}

impl Drop for AsyncSequentialReader {
    fn drop(&mut self) {
        if self.cancel_and_drain() {
            return;
        }
        unsafe {
            ManuallyDrop::drop(&mut self.file);
            ManuallyDrop::drop(&mut self.completion_port);
        }
    }
}

fn next_file_retry(
    policy: ReaderIoPolicy,
    consecutive_retries: &mut u8,
    made_progress: bool,
) -> Option<u8> {
    if made_progress {
        *consecutive_retries = 0;
    }
    if *consecutive_retries >= policy.max_consecutive_file_retries {
        return None;
    }
    *consecutive_retries += 1;
    Some(*consecutive_retries)
}

fn retryable_file_read_error(error: &io::Error) -> bool {
    if let Some(code) = error.raw_os_error() {
        return !matches!(
            code as u32,
            ERROR_FILE_NOT_FOUND
                | ERROR_PATH_NOT_FOUND
                | ERROR_ACCESS_DENIED
                | ERROR_INVALID_HANDLE
                | ERROR_SHARING_VIOLATION
                | ERROR_LOCK_VIOLATION
                | ERROR_HANDLE_EOF
                | ERROR_NOT_SUPPORTED
                | ERROR_NETWORK_ACCESS_DENIED
                | ERROR_BAD_NET_NAME
                | ERROR_INVALID_PARAMETER
                | ERROR_INVALID_NAME
                | ERROR_BAD_PATHNAME
                | ERROR_FILENAME_EXCED_RANGE
                | ERROR_DIRECTORY
        );
    }
    !matches!(
        error.kind(),
        io::ErrorKind::Interrupted
            | io::ErrorKind::NotFound
            | io::ErrorKind::InvalidInput
            | io::ErrorKind::InvalidData
            | io::ErrorKind::PermissionDenied
            | io::ErrorKind::Unsupported
    )
}

fn exhausted_file_read_error(
    policy: ReaderIoPolicy,
    path: &str,
    offset: u64,
    error: io::Error,
) -> io::Error {
    io::Error::new(
        error.kind(),
        format!(
            "read failed for {path} at offset {offset} after {} consecutive retries: {error}",
            policy.max_consecutive_file_retries
        ),
    )
}

struct ReaderPreparationPlan {
    chunk_size: usize,
    queue_depth: usize,
    prime_depth: usize,
    cancel_event: HANDLE,
    read_wait_counter: Option<Arc<AtomicU64>>,
    io_policy: ReaderIoPolicy,
}

fn prepare_reader_once(
    path: &str,
    expected_len: u64,
    plan: ReaderPreparationPlan,
) -> io::Result<AsyncSequentialReader> {
    let ReaderPreparationPlan {
        chunk_size,
        queue_depth,
        prime_depth,
        cancel_event,
        read_wait_counter,
        io_policy,
    } = plan;
    let mut reader = AsyncSequentialReader::open_from_with_depth(
        path,
        expected_len,
        0,
        chunk_size,
        cancel_event,
        queue_depth,
        io_policy,
    )?;
    if let Some(counter) = read_wait_counter {
        reader.set_read_wait_counter(counter);
    }
    reader.prime_limit(prime_depth)?;
    Ok(reader)
}

struct OverlappedReadPlan {
    chunk_size: usize,
    queue_depth: usize,
    cancel_event: HANDLE,
    io_policy: ReaderIoPolicy,
    read_wait_counter: Option<Arc<AtomicU64>>,
    prepared_reader: Option<AsyncSequentialReader>,
    initial_error: Option<io::Error>,
}

fn read_file_overlapped_with_depth<F, D>(
    path: &str,
    expected_len: u64,
    plan: OverlappedReadPlan,
    mut discard_later_reads: D,
    mut consume: F,
) -> io::Result<()>
where
    F: FnMut(u64, &[u8]) -> io::Result<()>,
    D: FnMut(),
{
    let OverlappedReadPlan {
        chunk_size,
        queue_depth,
        cancel_event,
        io_policy,
        read_wait_counter,
        mut prepared_reader,
        initial_error,
    } = plan;
    let mut next_offset = 0u64;
    let mut consecutive_retries = 0u8;

    if let Some(error) = initial_error {
        discard_later_reads();
        if !retryable_file_read_error(&error) {
            return Err(error);
        }
        let Some(retry_number) = next_file_retry(io_policy, &mut consecutive_retries, false) else {
            return Err(exhausted_file_read_error(
                io_policy,
                path,
                next_offset,
                error,
            ));
        };
        wait_for_file_retry(cancel_event, io_policy, retry_number)?;
    }

    if expected_len == 0 {
        let _reader = match prepared_reader.take() {
            Some(reader) => reader,
            None => AsyncSequentialReader::open_from_with_depth(
                path,
                expected_len,
                0,
                chunk_size,
                cancel_event,
                queue_depth,
                io_policy,
            )?,
        };
        return Ok(());
    }

    while next_offset < expected_len {
        if is_cancelled(cancel_event) {
            return Err(cancelled_error());
        }

        let attempt_offset = next_offset;
        let open_result = match prepared_reader.take() {
            Some(reader) => Ok(reader),
            None => AsyncSequentialReader::open_from_with_depth(
                path,
                expected_len,
                next_offset,
                chunk_size,
                cancel_event,
                queue_depth,
                io_policy,
            ),
        };
        let mut reader = match open_result {
            Ok(mut reader) => {
                if let Some(counter) = read_wait_counter.as_ref() {
                    reader.set_read_wait_counter(Arc::clone(counter));
                }
                reader
            }
            Err(error) => {
                discard_later_reads();
                if !retryable_file_read_error(&error) {
                    return Err(error);
                }
                let Some(retry_number) =
                    next_file_retry(io_policy, &mut consecutive_retries, false)
                else {
                    return Err(exhausted_file_read_error(
                        io_policy,
                        path,
                        next_offset,
                        error,
                    ));
                };
                wait_for_file_retry(cancel_event, io_policy, retry_number)?;
                continue;
            }
        };

        let run_result = reader.run(|offset, slice| {
            if offset != next_offset {
                return Err(io::Error::new(
                    io::ErrorKind::InvalidData,
                    format!(
                        "asynchronous read order mismatch: expected={next_offset} actual={offset}"
                    ),
                ));
            }
            consume(offset, slice)?;
            next_offset = next_offset.saturating_add(slice.len() as u64);
            Ok(())
        });

        match run_result {
            Ok(()) => return Ok(()),
            Err(AsyncReaderRunError::Consumer(error)) => {
                drop(reader);
                discard_later_reads();
                return Err(error);
            }
            Err(AsyncReaderRunError::Io(error)) => {
                let made_progress = next_offset > attempt_offset;
                drop(reader);
                discard_later_reads();
                if !retryable_file_read_error(&error) {
                    return Err(error);
                }
                let Some(retry_number) =
                    next_file_retry(io_policy, &mut consecutive_retries, made_progress)
                else {
                    return Err(exhausted_file_read_error(
                        io_policy,
                        path,
                        next_offset,
                        error,
                    ));
                };
                wait_for_file_retry(cancel_event, io_policy, retry_number)?;
            }
        }
    }
    Ok(())
}

fn read_file_overlapped<F>(
    path: &str,
    expected_len: u64,
    chunk_size: usize,
    cancel_event: HANDLE,
    io_policy: ReaderIoPolicy,
    consume: F,
) -> io::Result<()>
where
    F: FnMut(u64, &[u8]) -> io::Result<()>,
{
    read_file_overlapped_with_depth(
        path,
        expected_len,
        OverlappedReadPlan {
            chunk_size,
            queue_depth: IO_QUEUE_DEPTH,
            cancel_event,
            io_policy,
            read_wait_counter: None,
            prepared_reader: None,
            initial_error: None,
        },
        || {},
        consume,
    )
}

const SMALL_BUFFER_CLASSES: [usize; 7] = [
    4 * 1024,
    16 * 1024,
    64 * 1024,
    256 * 1024,
    1024 * 1024,
    2 * 1024 * 1024,
    4 * 1024 * 1024,
];
const SMALL_QUEUE_CLASS_COUNT: usize = SMALL_BUFFER_CLASSES.len() + 1;

#[derive(Clone)]
struct SmallFileTask {
    index: u64,
    len: u64,
    path: String,
}

enum SmallFileStatus {
    Pending,
    Opening,
    InFlight,
    Ready { data: Vec<u8>, reserved: usize },
    Failed { message: String, retryable: bool },
    Borrowed,
}

struct SmallFileEntry {
    task: SmallFileTask,
    status: SmallFileStatus,
    attempts: u8,
    queue_generation: u64,
    retry_buffer: Option<(Vec<u8>, usize)>,
}

#[derive(Clone, Copy)]
struct SmallQueueItem {
    index: u64,
    generation: u64,
    priority: bool,
}

struct SmallFileState {
    entries: FxHashMap<u64, SmallFileEntry>,
    queues: [VecDeque<SmallQueueItem>; SMALL_QUEUE_CLASS_COUNT],
    next_queue_generation: u64,
    buffers: FxHashMap<usize, Vec<Vec<u8>>>,
    active_files: usize,
    reserved_bytes: usize,
    max_active_files: usize,
    max_reserved_bytes: usize,
    shutdown: bool,
    fatal_error: Option<String>,
}

impl SmallFileState {
    fn new() -> Self {
        Self {
            entries: FxHashMap::default(),
            queues: std::array::from_fn(|_| VecDeque::new()),
            next_queue_generation: 0,
            buffers: FxHashMap::default(),
            active_files: 0,
            reserved_bytes: 0,
            max_active_files: 0,
            max_reserved_bytes: 0,
            shutdown: false,
            fatal_error: None,
        }
    }

    fn take_buffer(&mut self, capacity: usize) -> Vec<u8> {
        if capacity == 0 {
            return Vec::new();
        }
        let mut buffer = self
            .buffers
            .get_mut(&capacity)
            .and_then(Vec::pop)
            .unwrap_or_else(|| vec![0u8; capacity]);
        buffer.resize(capacity, 0);
        buffer
    }

    fn return_buffer(&mut self, mut buffer: Vec<u8>, capacity: usize) {
        if capacity == 0 {
            return;
        }
        buffer.clear();
        self.buffers.entry(capacity).or_default().push(buffer);
    }

    fn enqueue_index(&mut self, index: u64, class: usize, priority: bool) {
        let generation = self.next_queue_generation;
        self.next_queue_generation = self.next_queue_generation.wrapping_add(1);
        if let Some(entry) = self.entries.get_mut(&index) {
            entry.queue_generation = generation;
        }
        let item = SmallQueueItem {
            index,
            generation,
            priority,
        };
        if priority {
            self.queues[class].push_front(item);
        } else {
            self.queues[class].push_back(item);
        }
    }

    fn clean_queue_front(&mut self, class: usize) -> Option<SmallQueueItem> {
        loop {
            let item = self.queues[class].front().copied()?;
            let valid = self
                .entries
                .get(&item.index)
                .map(|entry| {
                    entry.queue_generation == item.generation
                        && matches!(entry.status, SmallFileStatus::Pending)
                })
                .unwrap_or(false);
            if valid {
                return Some(item);
            }
            self.queues[class].pop_front();
        }
    }

    fn prioritize_pending(&mut self, index: u64) -> bool {
        let Some(class) = self.entries.get(&index).and_then(|entry| {
            matches!(entry.status, SmallFileStatus::Pending)
                .then(|| small_buffer_class(entry.task.len).map(|(class, _)| class))
                .flatten()
        }) else {
            return false;
        };
        self.enqueue_index(index, class, true);
        true
    }

    fn take_next_pending(
        &mut self,
        inflight_byte_limit: usize,
        demand_reserve: usize,
    ) -> Option<(u64, usize)> {
        let mut selected: Option<(usize, SmallQueueItem)> = None;
        for class in 0..SMALL_QUEUE_CLASS_COUNT {
            let Some(item) = self.clean_queue_front(class) else {
                continue;
            };
            let capacity = self
                .entries
                .get(&item.index)
                .and_then(|entry| entry.retry_buffer.as_ref().map(|(_, reserved)| *reserved))
                .unwrap_or_else(|| small_queue_capacity(class));
            // A retry buffer is already included in reserved_bytes. It must
            // remain schedulable even when speculative look-ahead files have
            // consumed every other byte of the small-file budget.
            let has_retained_reservation = self
                .entries
                .get(&item.index)
                .is_some_and(|entry| entry.retry_buffer.is_some());
            // Speculative look-ahead must leave enough budget for the ordered
            // consumer's next small file. Otherwise a larger current file can
            // be stranded behind smaller, later files that consumed the whole
            // budget and cannot be released until the current file completes.
            let reservation_limit = if item.priority {
                inflight_byte_limit
            } else {
                inflight_byte_limit.saturating_sub(demand_reserve)
            };
            if !has_retained_reservation
                && self.reserved_bytes.saturating_add(capacity) > reservation_limit
            {
                continue;
            }
            let should_select = selected
                .map(|(_, current)| {
                    if item.priority != current.priority {
                        item.priority
                    } else if item.priority {
                        item.generation > current.generation
                    } else {
                        item.generation < current.generation
                    }
                })
                .unwrap_or(true);
            if should_select {
                selected = Some((class, item));
            }
        }

        let (class, item) = selected?;
        let removed = self.queues[class].pop_front()?;
        debug_assert_eq!(removed.index, item.index);
        debug_assert_eq!(removed.generation, item.generation);
        let capacity = self
            .entries
            .get(&item.index)
            .and_then(|entry| entry.retry_buffer.as_ref().map(|(_, reserved)| *reserved))
            .unwrap_or_else(|| small_queue_capacity(class));
        Some((item.index, capacity))
    }
}

#[derive(Clone, Copy)]
struct SharedHandle(HANDLE);

unsafe impl Send for SharedHandle {}
unsafe impl Sync for SharedHandle {}

struct SmallOperation {
    overlapped: OVERLAPPED,
    file: Handle,
    buffer: Vec<u8>,
    index: u64,
    expected: u32,
    reserved: usize,
    started_at: Instant,
    cancel_requested_at: Option<Instant>,
    timed_out: bool,
}

unsafe impl Send for SmallOperation {}

struct SmallWorkerActivity {
    file_index: u64,
    stage: &'static str,
    started_at: Instant,
    cancel_requested_at: Option<Instant>,
    cancel_error: Option<i32>,
    timed_out: bool,
}

struct SmallShared {
    state: Mutex<SmallFileState>,
    changed: Condvar,
    operations: Mutex<FxHashMap<usize, Box<SmallOperation>>>,
    worker_handles: Mutex<Vec<usize>>,
    worker_activities: Mutex<Vec<Option<SmallWorkerActivity>>>,
    completion_port: SharedHandle,
    active_limit: usize,
    inflight_byte_limit: usize,
    demand_reserve: usize,
    completion_batch: usize,
    cancel_event: SharedHandle,
    io_policy: ReaderIoPolicy,
}

struct CachedSmallFile {
    data: Vec<u8>,
    reserved: usize,
}

struct SmallFilePool {
    shared: Arc<SmallShared>,
    completion_port: Handle,
    workers: Vec<JoinHandle<()>>,
    completion_thread: Option<JoinHandle<()>>,
}

impl SmallFilePool {
    fn enqueue(&self, task: SmallFileTask, priority: bool) {
        let Some((class, _)) = small_buffer_class(task.len) else {
            return;
        };
        let mut state = self.shared.state.lock().unwrap();
        if state.shutdown || state.fatal_error.is_some() {
            return;
        }
        if state.entries.contains_key(&task.index) {
            if priority && state.prioritize_pending(task.index) {
                let _reserved_bytes = state.reserved_bytes;
                self.shared.changed.notify_all();
                drop(state);
                debug_log!(
                    "event=small-demand-prioritized\tfile={}\tlength={}\treserved_bytes={}\tinflight_limit={}\tdemand_reserve={}\tpath={:?}",
                    task.index,
                    task.len,
                    _reserved_bytes,
                    self.shared.inflight_byte_limit,
                    self.shared.demand_reserve,
                    task.path
                );
            }
            return;
        }
        let index = task.index;
        state.entries.insert(
            index,
            SmallFileEntry {
                task,
                status: SmallFileStatus::Pending,
                attempts: 0,
                queue_generation: 0,
                retry_buffer: None,
            },
        );
        state.enqueue_index(index, class, priority);
        self.shared.changed.notify_all();
    }
}

fn small_buffer_class(len: u64) -> Option<(usize, usize)> {
    if len == 0 {
        return Some((0, 0));
    }
    SMALL_BUFFER_CLASSES
        .iter()
        .enumerate()
        .find(|(_, capacity)| len <= **capacity as u64)
        .map(|(class, capacity)| (class + 1, *capacity))
}

fn small_queue_capacity(class: usize) -> usize {
    debug_assert!(class < SMALL_QUEUE_CLASS_COUNT);
    class
        .checked_sub(1)
        .and_then(|class| SMALL_BUFFER_CLASSES.get(class).copied())
        .unwrap_or(0)
}

fn small_failure(
    shared: &SmallShared,
    index: u64,
    mut buffer: Vec<u8>,
    reserved: usize,
    error: io::Error,
) {
    let mut state = shared.state.lock().unwrap();
    state.active_files = state.active_files.saturating_sub(1);
    buffer.resize(reserved, 0);
    if let Some(entry) = state.entries.get_mut(&index) {
        // Keep the original reservation across retry backoff. Releasing it
        // here lets later prefetched files consume the whole budget, after
        // which the ordered consumer cannot retry this file or advance far
        // enough to release those later files.
        entry.retry_buffer = Some((buffer, reserved));
        entry.status = SmallFileStatus::Failed {
            message: error.to_string(),
            retryable: retryable_file_read_error(&error),
        };
    } else {
        state.reserved_bytes = state.reserved_bytes.saturating_sub(reserved);
        state.return_buffer(buffer, reserved);
    }
    shared.changed.notify_all();
}

fn small_set_worker_activity(
    shared: &SmallShared,
    worker_id: usize,
    file_index: u64,
    stage: &'static str,
) {
    let mut activities = shared.worker_activities.lock().unwrap();
    activities[worker_id] = Some(SmallWorkerActivity {
        file_index,
        stage,
        started_at: Instant::now(),
        cancel_requested_at: None,
        cancel_error: None,
        timed_out: false,
    });
}

fn small_clear_worker_activity(shared: &SmallShared, worker_id: usize) {
    shared.worker_activities.lock().unwrap()[worker_id] = None;
}

fn small_completion_can_exit(shared: &SmallShared) -> bool {
    if !shared.state.lock().unwrap().shutdown {
        return false;
    }
    if shared
        .worker_activities
        .lock()
        .unwrap()
        .iter()
        .any(Option::is_some)
    {
        return false;
    }
    shared.operations.lock().unwrap().is_empty()
}

fn small_open_worker(shared: Arc<SmallShared>, worker_id: usize) {
    loop {
        let (task, reserved, buffer) = {
            let mut state = shared.state.lock().unwrap();
            loop {
                if state.shutdown {
                    return;
                }
                if state.fatal_error.is_some() {
                    state = shared.changed.wait(state).unwrap();
                    continue;
                }
                if state.active_files < shared.active_limit
                    && let Some((index, reserved)) =
                        state.take_next_pending(shared.inflight_byte_limit, shared.demand_reserve)
                {
                    let (task, retry_buffer) = {
                        let entry = state.entries.get_mut(&index).unwrap();
                        entry.status = SmallFileStatus::Opening;
                        entry.attempts += 1;
                        (entry.task.clone(), entry.retry_buffer.take())
                    };
                    state.active_files += 1;
                    let (reserved, buffer) = match retry_buffer {
                        Some((mut buffer, retained)) => {
                            debug_assert_eq!(retained, reserved);
                            buffer.resize(retained, 0);
                            (retained, buffer)
                        }
                        None => {
                            state.reserved_bytes += reserved;
                            (reserved, state.take_buffer(reserved))
                        }
                    };
                    state.max_active_files = state.max_active_files.max(state.active_files);
                    state.max_reserved_bytes = state.max_reserved_bytes.max(state.reserved_bytes);
                    break (task, reserved, buffer);
                }
                state = shared.changed.wait(state).unwrap();
            }
        };

        let path_w = wide(&task.path);
        small_set_worker_activity(&shared, worker_id, task.index, "CreateFileW");
        let raw_file = unsafe {
            CreateFileW(
                path_w.as_ptr(),
                GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                null(),
                OPEN_EXISTING,
                FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OVERLAPPED,
                null_mut(),
            )
        };
        let open_error = (raw_file == INVALID_HANDLE_VALUE).then(io::Error::last_os_error);
        small_clear_worker_activity(&shared, worker_id);
        if let Some(error) = open_error {
            small_failure(&shared, task.index, buffer, reserved, error);
            continue;
        }
        let file = Handle(raw_file);

        if shared.state.lock().unwrap().shutdown {
            small_failure(
                &shared,
                task.index,
                buffer,
                reserved,
                io::Error::new(io::ErrorKind::Interrupted, "reader shutting down"),
            );
            drop(file);
            continue;
        }

        if task.len == 0 {
            let mut state = shared.state.lock().unwrap();
            state.active_files = state.active_files.saturating_sub(1);
            if let Some(entry) = state.entries.get_mut(&task.index) {
                entry.status = SmallFileStatus::Ready {
                    data: buffer,
                    reserved,
                };
            }
            shared.changed.notify_all();
            continue;
        }

        small_set_worker_activity(&shared, worker_id, task.index, "CreateIoCompletionPort");
        let associated = unsafe {
            CreateIoCompletionPort(raw_file, shared.completion_port.0, task.index as usize, 0)
        };
        let association_error = associated.is_null().then(io::Error::last_os_error);
        small_clear_worker_activity(&shared, worker_id);
        if let Some(error) = association_error {
            small_failure(&shared, task.index, buffer, reserved, error);
            continue;
        }

        let mut operation = Box::new(SmallOperation {
            overlapped: OVERLAPPED::default(),
            file,
            buffer,
            index: task.index,
            expected: task.len as u32,
            reserved,
            started_at: Instant::now(),
            cancel_requested_at: None,
            timed_out: false,
        });
        let operation_key = (&mut operation.overlapped as *mut OVERLAPPED) as usize;
        {
            let mut state = shared.state.lock().unwrap();
            if let Some(entry) = state.entries.get_mut(&task.index) {
                entry.status = SmallFileStatus::InFlight;
            }
        }
        let mut operations = shared.operations.lock().unwrap();
        operations.insert(operation_key, operation);
        let operation = operations.get_mut(&operation_key).unwrap();
        small_set_worker_activity(&shared, worker_id, task.index, "ReadFile submission");
        let ok = unsafe {
            ReadFile(
                operation.file.0,
                operation.buffer.as_mut_ptr(),
                operation.expected,
                null_mut(),
                &mut operation.overlapped,
            )
        };
        let read_error = (ok == 0).then(io::Error::last_os_error);
        small_clear_worker_activity(&shared, worker_id);
        if let Some(error) = read_error
            && error.raw_os_error() != Some(ERROR_IO_PENDING as i32)
        {
            let operation = operations.remove(&operation_key).unwrap();
            drop(operations);
            small_failure(&shared, task.index, operation.buffer, reserved, error);
        }
    }
}

fn small_operation_watchdog(shared: &SmallShared) -> bool {
    let shutdown = shared.state.lock().unwrap().shutdown;
    let now = Instant::now();
    let mut fatal_error = None;
    let mut must_leak_and_exit = false;

    {
        let worker_handles = shared.worker_handles.lock().unwrap();
        let mut activities = shared.worker_activities.lock().unwrap();
        for (worker_id, activity) in activities.iter_mut().enumerate() {
            let Some(activity) = activity.as_mut() else {
                continue;
            };
            if let Some(cancel_requested_at) = activity.cancel_requested_at {
                if now.duration_since(cancel_requested_at) >= shared.io_policy.cancel_grace
                    && activity.timed_out
                    && !shutdown
                {
                    fatal_error = Some(format!(
                        "small-file synchronous call did not recover within {} ms after cancellation: file={} stage={} cancel_error={}",
                        shared.io_policy.cancel_grace.as_millis(),
                        activity.file_index,
                        activity.stage,
                        activity
                            .cancel_error
                            .map(|error| error.to_string())
                            .unwrap_or_else(|| "none".into())
                    ));
                    break;
                }
                continue;
            }

            let timed_out = activity.started_at.elapsed() >= shared.io_policy.read_stall_timeout;
            if !shutdown && !timed_out {
                continue;
            }
            let Some(&worker_handle) = worker_handles.get(worker_id) else {
                continue;
            };
            let cancelled = unsafe { CancelSynchronousIo(worker_handle as HANDLE) };
            activity.cancel_requested_at = Some(now);
            activity.timed_out = timed_out;
            if cancelled == 0 {
                activity.cancel_error = io::Error::last_os_error().raw_os_error();
            }
            debug_log!(
                "event=small-sync-cancel\tworker={}\tfile={}\tstage={}\ttimed_out={}\tresult={}\terror={:?}",
                worker_id,
                activity.file_index,
                activity.stage,
                timed_out,
                cancelled,
                activity.cancel_error
            );
        }
    }

    if let Some(message) = fatal_error.take() {
        debug_log!("event=small-sync-fatal\tmessage={message:?}");
        let mut state = shared.state.lock().unwrap();
        if state.fatal_error.is_none() {
            state.fatal_error = Some(message);
        }
        shared.changed.notify_all();
        return false;
    }

    {
        let mut operations = match shared.operations.try_lock() {
            Ok(operations) => operations,
            Err(std::sync::TryLockError::WouldBlock) => return false,
            Err(std::sync::TryLockError::Poisoned(poisoned)) => poisoned.into_inner(),
        };
        for operation in operations.values_mut() {
            if let Some(cancel_requested_at) = operation.cancel_requested_at {
                if now.duration_since(cancel_requested_at) >= shared.io_policy.cancel_grace {
                    if operation.timed_out && !shutdown {
                        fatal_error = Some(format!(
                            "small-file read cancellation did not complete within {} ms: file={} offset=0 length={}",
                            shared.io_policy.cancel_grace.as_millis(),
                            operation.index,
                            operation.expected
                        ));
                    }
                    must_leak_and_exit = true;
                    break;
                }
                continue;
            }

            let timed_out = operation.started_at.elapsed() >= shared.io_policy.read_stall_timeout;
            if !shutdown && !timed_out {
                continue;
            }

            operation.cancel_requested_at = Some(now);
            operation.timed_out = timed_out;
            let _cancelled = unsafe { CancelIoEx(operation.file.0, &operation.overlapped) };
            #[cfg(all(debug_assertions, not(test)))]
            let cancel_error = (_cancelled == 0).then(|| io::Error::last_os_error().raw_os_error());
            debug_log!(
                "event=small-async-cancel\tfile={}\tlength={}\ttimed_out={}\tresult={}\terror={:?}",
                operation.index,
                operation.expected,
                timed_out,
                _cancelled,
                cancel_error.flatten()
            );
        }

        if must_leak_and_exit {
            debug_log!(
                "event=small-async-abandon\toperations={}\taction=leak-and-exit",
                operations.len()
            );
            for (_, operation) in operations.drain() {
                // A remote redirector may still own this OVERLAPPED and buffer.
                // Keep their addresses alive if cancellation itself is wedged.
                std::mem::forget(operation);
            }
        }
    }

    if let Some(message) = fatal_error {
        debug_log!("event=small-async-fatal\tmessage={message:?}");
        let mut state = shared.state.lock().unwrap();
        if state.fatal_error.is_none() {
            state.fatal_error = Some(message);
        }
        shared.changed.notify_all();
    }

    must_leak_and_exit
}

fn small_completion_worker(shared: Arc<SmallShared>) {
    let mut entries = vec![OVERLAPPED_ENTRY::default(); shared.completion_batch];
    loop {
        let mut removed = 0u32;
        let ok = unsafe {
            GetQueuedCompletionStatusEx(
                shared.completion_port.0,
                entries.as_mut_ptr(),
                entries.len() as u32,
                &mut removed,
                CANCEL_POLL_MS,
                0,
            )
        };
        if ok == 0 {
            let error = io::Error::last_os_error();
            if error.raw_os_error() == Some(WAIT_TIMEOUT as i32) {
                if small_operation_watchdog(&shared) {
                    return;
                }
                if small_completion_can_exit(&shared) {
                    return;
                }
                continue;
            }
            let mut report_error = false;
            {
                let mut state = shared.state.lock().unwrap();
                if state.fatal_error.is_none() {
                    state.fatal_error = Some(format!("small-file IOCP failed: {error}"));
                    report_error = true;
                }
                shared.changed.notify_all();
            }
            if report_error {
                eprintln!("IOCP_ERROR\t{error}");
                io::stderr().flush().ok();
                let now = Instant::now();
                let mut operations = shared.operations.lock().unwrap();
                for operation in operations.values_mut() {
                    operation.cancel_requested_at.get_or_insert(now);
                    unsafe {
                        CancelIoEx(operation.file.0, &operation.overlapped);
                    }
                }
            }
            if small_operation_watchdog(&shared) {
                return;
            }
            if small_completion_can_exit(&shared) {
                return;
            }
            thread::sleep(Duration::from_millis(10));
            continue;
        }

        for completion in &entries[..removed as usize] {
            if completion.lpOverlapped.is_null() {
                continue;
            }
            let key = completion.lpOverlapped as usize;
            let mut operation = {
                let mut operations = shared.operations.lock().unwrap();
                let Some(operation) = operations.remove(&key) else {
                    continue;
                };
                operation
            };
            let mut transferred = 0u32;
            let result: io::Result<()> = if unsafe {
                GetOverlappedResult(operation.file.0, &operation.overlapped, &mut transferred, 0)
            } != 0
            {
                if transferred == operation.expected {
                    Ok(())
                } else {
                    Err(io::Error::new(
                        io::ErrorKind::UnexpectedEof,
                        format!(
                            "short asynchronous read: expected={} actual={transferred}",
                            operation.expected
                        ),
                    ))
                }
            } else {
                let error = io::Error::last_os_error();
                if operation.timed_out {
                    Err(io::Error::new(
                        io::ErrorKind::TimedOut,
                        format!(
                            "asynchronous read timed out after {} ms: {error}",
                            shared.io_policy.read_stall_timeout.as_millis()
                        ),
                    ))
                } else {
                    Err(error)
                }
            };
            operation.buffer.truncate(transferred as usize);

            let mut state = shared.state.lock().unwrap();
            state.active_files = state.active_files.saturating_sub(1);
            if state.shutdown || !state.entries.contains_key(&operation.index) {
                state.reserved_bytes = state.reserved_bytes.saturating_sub(operation.reserved);
                state.return_buffer(operation.buffer, operation.reserved);
                state.entries.remove(&operation.index);
            } else {
                match result {
                    Ok(()) => {
                        if let Some(entry) = state.entries.get_mut(&operation.index) {
                            entry.status = SmallFileStatus::Ready {
                                data: operation.buffer,
                                reserved: operation.reserved,
                            };
                        }
                    }
                    Err(error) => {
                        operation.buffer.resize(operation.reserved, 0);
                        if let Some(entry) = state.entries.get_mut(&operation.index) {
                            entry.retry_buffer = Some((operation.buffer, operation.reserved));
                            entry.status = SmallFileStatus::Failed {
                                message: error.to_string(),
                                retryable: retryable_file_read_error(&error),
                            };
                        } else {
                            state.reserved_bytes =
                                state.reserved_bytes.saturating_sub(operation.reserved);
                            state.return_buffer(operation.buffer, operation.reserved);
                        }
                    }
                }
            }
            shared.changed.notify_all();
        }

        if small_operation_watchdog(&shared) {
            return;
        }

        if small_completion_can_exit(&shared) {
            return;
        }
    }
}

impl SmallFilePool {
    fn new(
        open_concurrency: usize,
        active_limit: usize,
        inflight_byte_limit: usize,
        small_threshold: u64,
        completion_batch: usize,
        cancel_event: HANDLE,
        io_policy: ReaderIoPolicy,
    ) -> io::Result<Self> {
        let Some((_, demand_reserve)) = small_buffer_class(small_threshold) else {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "small-file threshold exceeds the largest buffer class",
            ));
        };
        if open_concurrency == 0
            || active_limit == 0
            || inflight_byte_limit < 64 * 1024
            || inflight_byte_limit < demand_reserve
            || !(1..=128).contains(&completion_batch)
        {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "invalid small-file pool configuration",
            ));
        }
        let raw_port = unsafe { CreateIoCompletionPort(INVALID_HANDLE_VALUE, null_mut(), 0, 0) };
        if raw_port.is_null() {
            return Err(io::Error::last_os_error());
        }
        let completion_port = Handle(raw_port);
        let shared = Arc::new(SmallShared {
            state: Mutex::new(SmallFileState::new()),
            changed: Condvar::new(),
            operations: Mutex::new(FxHashMap::default()),
            worker_handles: Mutex::new(Vec::with_capacity(open_concurrency)),
            worker_activities: Mutex::new(
                std::iter::repeat_with(|| None)
                    .take(open_concurrency)
                    .collect(),
            ),
            completion_port: SharedHandle(raw_port),
            active_limit,
            inflight_byte_limit,
            demand_reserve,
            completion_batch,
            cancel_event: SharedHandle(cancel_event),
            io_policy,
        });
        let mut workers = Vec::with_capacity(open_concurrency);
        for worker_id in 0..open_concurrency {
            let worker_shared = Arc::clone(&shared);
            let worker = thread::spawn(move || small_open_worker(worker_shared, worker_id));
            shared
                .worker_handles
                .lock()
                .unwrap()
                .push(worker.as_raw_handle() as HANDLE as usize);
            workers.push(worker);
        }
        let completion_shared = Arc::clone(&shared);
        let completion_thread = Some(thread::spawn(move || {
            small_completion_worker(completion_shared)
        }));
        Ok(Self {
            shared,
            completion_port,
            workers,
            completion_thread,
        })
    }

    fn wait_take(&self, task: SmallFileTask) -> io::Result<CachedSmallFile> {
        let queue_class = small_buffer_class(task.len)
            .map(|(class, _)| class)
            .ok_or_else(|| io::Error::new(io::ErrorKind::InvalidInput, "file is not small"))?;
        self.enqueue(task.clone(), true);
        let mut state = self.shared.state.lock().unwrap();
        loop {
            if is_cancelled(self.shared.cancel_event.0) {
                return Err(cancelled_error());
            }
            if let Some(message) = state.fatal_error.as_ref() {
                return Err(io::Error::other(message.clone()));
            }
            let Some(entry) = state.entries.get_mut(&task.index) else {
                drop(state);
                self.enqueue(task.clone(), true);
                state = self.shared.state.lock().unwrap();
                continue;
            };
            match &entry.status {
                SmallFileStatus::Ready { .. } => {
                    let status = std::mem::replace(&mut entry.status, SmallFileStatus::Borrowed);
                    if let SmallFileStatus::Ready { data, reserved } = status {
                        return Ok(CachedSmallFile { data, reserved });
                    }
                }
                SmallFileStatus::Failed { message, retryable } => {
                    let attempts = entry.attempts;
                    if !retryable {
                        return Err(io::Error::other(format!(
                            "non-retryable read failure for {}: {message}",
                            task.path
                        )));
                    }
                    if attempts > self.shared.io_policy.max_consecutive_file_retries {
                        return Err(io::Error::other(format!(
                            "read failed for {} after {} consecutive retries: {message}",
                            task.path, self.shared.io_policy.max_consecutive_file_retries
                        )));
                    }

                    drop(state);
                    wait_for_file_retry(
                        self.shared.cancel_event.0,
                        self.shared.io_policy,
                        attempts,
                    )?;
                    state = self.shared.state.lock().unwrap();
                    let Some(entry) = state.entries.get_mut(&task.index) else {
                        continue;
                    };
                    if entry.attempts == attempts
                        && matches!(entry.status, SmallFileStatus::Failed { .. })
                    {
                        entry.status = SmallFileStatus::Pending;
                        state.enqueue_index(task.index, queue_class, true);
                        self.shared.changed.notify_all();
                    }
                }
                _ => {
                    state = self
                        .shared
                        .changed
                        .wait_timeout(state, Duration::from_millis(CANCEL_POLL_MS as u64))
                        .unwrap()
                        .0;
                }
            }
        }
    }

    #[cfg(test)]
    fn put_back(&self, index: u64, cached: CachedSmallFile) {
        let mut state = self.shared.state.lock().unwrap();
        if let Some(entry) = state.entries.get_mut(&index) {
            entry.status = SmallFileStatus::Ready {
                data: cached.data,
                reserved: cached.reserved,
            };
        }
        self.shared.changed.notify_all();
    }

    fn release(&self, index: u64, cached: CachedSmallFile) {
        let mut state = self.shared.state.lock().unwrap();
        state.entries.remove(&index);
        state.reserved_bytes = state.reserved_bytes.saturating_sub(cached.reserved);
        state.return_buffer(cached.data, cached.reserved);
        self.shared.changed.notify_all();
    }

    fn shutdown(&mut self) {
        {
            let mut state = self.shared.state.lock().unwrap();
            state.shutdown = true;
            self.shared.changed.notify_all();
        }
        for worker in self.workers.drain(..) {
            let _ = worker.join();
        }
        {
            let now = Instant::now();
            let mut operations = self.shared.operations.lock().unwrap();
            for operation in operations.values_mut() {
                operation.cancel_requested_at.get_or_insert(now);
                unsafe {
                    CancelIoEx(operation.file.0, &operation.overlapped);
                }
            }
        }
        unsafe {
            PostQueuedCompletionStatus(self.completion_port.0, 0, 0, null());
        }
        if let Some(completion_thread) = self.completion_thread.take() {
            let join_result = completion_thread.join();
            let mut operations = match self.shared.operations.lock() {
                Ok(operations) => operations,
                Err(poisoned) => {
                    eprintln!("STATE_POISON\tsmall-file operation mutex poisoned during shutdown");
                    io::stderr().flush().ok();
                    self.shared.operations.clear_poison();
                    poisoned.into_inner()
                }
            };
            if join_result.is_err() || !operations.is_empty() {
                for (_, operation) in operations.drain() {
                    std::mem::forget(operation);
                }
            }
        }
    }
}

impl Drop for SmallFilePool {
    fn drop(&mut self) {
        self.shutdown()
    }
}

fn native_lock(shared: &NativeShared) -> std::sync::MutexGuard<'_, NativeState> {
    match shared.state.lock() {
        Ok(state) => state,
        Err(poisoned) => {
            let mut state = poisoned.into_inner();
            record_native_poison(&mut state, "mutex lock");
            shared.state.clear_poison();
            shared.changed.notify_all();
            state
        }
    }
}

fn record_native_poison(state: &mut NativeState, operation: &str) {
    let message = format!("native fast-reader state mutex poisoned during {operation}");
    if state.error.is_empty() {
        state.error = message.clone();
    }
    state.done = true;
    eprintln!("STATE_POISON\t{message}");
    io::stderr().flush().ok();
}

fn native_wait<'a>(
    shared: &NativeShared,
    state: std::sync::MutexGuard<'a, NativeState>,
    operation: &str,
) -> std::sync::MutexGuard<'a, NativeState> {
    match shared.changed.wait(state) {
        Ok(state) => state,
        Err(poisoned) => {
            let mut state = poisoned.into_inner();
            record_native_poison(&mut state, operation);
            shared.state.clear_poison();
            shared.changed.notify_all();
            state
        }
    }
}

fn native_wait_timeout<'a>(
    shared: &NativeShared,
    state: std::sync::MutexGuard<'a, NativeState>,
    timeout: Duration,
    operation: &str,
) -> (
    std::sync::MutexGuard<'a, NativeState>,
    std::sync::WaitTimeoutResult,
) {
    match shared.changed.wait_timeout(state, timeout) {
        Ok(result) => result,
        Err(poisoned) => {
            let (mut state, wait_result) = poisoned.into_inner();
            record_native_poison(&mut state, operation);
            shared.state.clear_poison();
            shared.changed.notify_all();
            (state, wait_result)
        }
    }
}

fn native_hash_options(mask: u32) -> FxHashMap<String, bool> {
    let mut enabled = FxHashMap::default();
    enabled.insert("SHA1".to_string(), mask & LFR_HASH_SHA1 != 0);
    enabled.insert("SHA256".to_string(), mask & LFR_HASH_SHA256 != 0);
    enabled.insert("SHA512".to_string(), mask & LFR_HASH_SHA512 != 0);
    enabled.insert("MD5".to_string(), mask & LFR_HASH_MD5 != 0);
    enabled.insert("CRC32".to_string(), mask & LFR_HASH_CRC32 != 0);
    enabled.insert("BLAKE3".to_string(), mask & LFR_HASH_BLAKE3 != 0);
    enabled.insert("XxHash3".to_string(), mask & LFR_HASH_XXH3 != 0);
    enabled.insert("XxHash128".to_string(), mask & LFR_HASH_XXH128 != 0);
    enabled
}

fn native_set_error(shared: &NativeShared, error: impl ToString) {
    let error = error.to_string();
    debug_log!("event=native-error\tmessage={error:?}");
    let mut state = native_lock(shared);
    if state.error.is_empty() {
        state.error = error;
    }
    state.done = true;
    shared.changed.notify_all();
}

fn native_worker_progress(shared: &NativeShared, file_index: Option<u64>, stage: &'static str) {
    let mut watch = match shared.worker_watch.lock() {
        Ok(watch) => watch,
        Err(poisoned) => {
            shared.worker_watch.clear_poison();
            poisoned.into_inner()
        }
    };
    #[cfg(all(debug_assertions, not(test)))]
    let stage_changed = watch.file_index != file_index || watch.stage != stage;
    watch.file_index = file_index;
    watch.stage = stage;
    watch.last_progress = Instant::now();
    watch.cancel_requested_at = None;
    watch.cancel_error = None;
    drop(watch);
    #[cfg(all(debug_assertions, not(test)))]
    if stage_changed {
        debug_log!("event=worker-stage\tfile={file_index:?}\tstage={stage:?}");
    }
}

fn native_worker_watchdog(shared: Arc<NativeShared>, policy: ReaderIoPolicy, worker_thread: usize) {
    loop {
        thread::sleep(Duration::from_millis(CANCEL_POLL_MS as u64));
        let (done, cancelled, has_error, ring_full) = {
            let state = native_lock(&shared);
            (
                state.done,
                state.cancelled,
                !state.error.is_empty(),
                state.occupied_slots == state.slots.len(),
            )
        };
        if done || has_error {
            return;
        }
        if cancelled {
            debug_log!("event=watchdog-cancelled\taction=cancel-synchronous-io");
            unsafe {
                CancelSynchronousIo(worker_thread as HANDLE);
            }
            return;
        }
        if ring_full {
            continue;
        }

        let now = Instant::now();
        let fatal_error = {
            let mut watch = match shared.worker_watch.lock() {
                Ok(watch) => watch,
                Err(poisoned) => {
                    shared.worker_watch.clear_poison();
                    poisoned.into_inner()
                }
            };
            if watch.stage == "wait for small file" {
                // Small-file workers have per-call watchdogs. The native
                // worker is only waiting on their condition variable here.
                watch.last_progress = now;
                watch.cancel_requested_at = None;
                watch.cancel_error = None;
                None
            } else if now.duration_since(watch.last_progress) < policy.read_stall_timeout {
                None
            } else if let Some(cancel_requested_at) = watch.cancel_requested_at {
                if now.duration_since(cancel_requested_at) >= policy.cancel_grace {
                    Some(format!(
                        "native worker made no progress for {} ms after synchronous I/O cancellation: file={} stage={} cancel_error={}",
                        policy.cancel_grace.as_millis(),
                        watch
                            .file_index
                            .map(|index| index.to_string())
                            .unwrap_or_else(|| "none".into()),
                        watch.stage,
                        watch
                            .cancel_error
                            .map(|error| error.to_string())
                            .unwrap_or_else(|| "none".into())
                    ))
                } else {
                    None
                }
            } else {
                debug_log!(
                    "event=watchdog-stall\tfile={:?}\tstage={:?}\tstall_ms={}\taction=cancel-synchronous-io",
                    watch.file_index,
                    watch.stage,
                    now.duration_since(watch.last_progress).as_millis()
                );
                let cancelled = unsafe { CancelSynchronousIo(worker_thread as HANDLE) };
                watch.cancel_requested_at = Some(now);
                if cancelled == 0 {
                    watch.cancel_error = io::Error::last_os_error().raw_os_error();
                }
                debug_log!(
                    "event=watchdog-cancel-result\tfile={:?}\tstage={:?}\tresult={}\terror={:?}",
                    watch.file_index,
                    watch.stage,
                    cancelled,
                    watch.cancel_error
                );
                None
            }
        };
        if let Some(error) = fatal_error {
            debug_log!("event=watchdog-fatal\tmessage={error:?}");
            native_set_error(&shared, error);
            return;
        }
    }
}

fn native_publish(
    shared: &NativeShared,
    file_index: u64,
    file_offset: u64,
    data: &[u8],
    flags: u32,
) -> io::Result<()> {
    let mut state = native_lock(shared);
    loop {
        if state.cancelled {
            return Err(cancelled_error());
        }
        if !state.error.is_empty() {
            return Err(io::Error::other(state.error.clone()));
        }
        let slot_index = state.write_index as usize % state.slots.len();
        if !state.slots[slot_index].full {
            if data.len() > state.slots[slot_index].buffer.len() {
                return Err(io::Error::new(
                    io::ErrorKind::InvalidData,
                    "native slot is too small",
                ));
            }
            let token = state.write_index + 1;
            let slot = &mut state.slots[slot_index];
            if !data.is_empty() {
                slot.buffer[..data.len()].copy_from_slice(data);
            }
            slot.token = token;
            slot.file_index = file_index;
            slot.file_offset = file_offset;
            slot.length = data.len() as u32;
            slot.flags = flags;
            slot.full = true;
            state.write_index += 1;
            state.buffered_bytes += data.len() as u64;
            state.occupied_slots += 1;
            if !data.is_empty() {
                shared
                    .telemetry
                    .bytes_published
                    .fetch_add(data.len() as u64, Ordering::Relaxed);
            }
            shared.changed.notify_all();
            native_worker_progress(shared, Some(file_index), "publish");
            return Ok(());
        }
        let wait_started = Instant::now();
        state = native_wait(shared, state, "publish wait");
        shared.telemetry.publish_wait_ns.fetch_add(
            wait_started.elapsed().as_nanos().min(u64::MAX as u128) as u64,
            Ordering::Relaxed,
        );
    }
}

fn native_publish_batch(
    shared: &NativeShared,
    file_index: u64,
    file_offset: u64,
    data: &[u8],
    slot_size: usize,
) -> io::Result<()> {
    let mut consumed = 0usize;
    #[cfg(all(debug_assertions, not(test)))]
    let mut waited_for_capacity = false;
    while consumed < data.len() {
        let mut state = native_lock(shared);
        while state.occupied_slots == state.slots.len() {
            if state.cancelled {
                return Err(cancelled_error());
            }
            if !state.error.is_empty() {
                return Err(io::Error::other(state.error.clone()));
            }
            #[cfg(all(debug_assertions, not(test)))]
            if !waited_for_capacity {
                debug_log!(
                    "event=publish-buffer-full\tfile={}\toffset={}\tbuffered_bytes={}\toccupied_slots={}",
                    file_index,
                    file_offset + consumed as u64,
                    state.buffered_bytes,
                    state.occupied_slots
                );
                waited_for_capacity = true;
            }
            let wait_started = Instant::now();
            state = native_wait(shared, state, "batch publish wait");
            shared.telemetry.publish_wait_ns.fetch_add(
                wait_started.elapsed().as_nanos().min(u64::MAX as u128) as u64,
                Ordering::Relaxed,
            );
        }
        if state.cancelled {
            return Err(cancelled_error());
        }
        if !state.error.is_empty() {
            return Err(io::Error::other(state.error.clone()));
        }

        let remaining_slots = data[consumed..].len().div_ceil(slot_size);
        let batch_slots = remaining_slots.min(state.slots.len() - state.occupied_slots);
        let first_write_index = state.write_index;
        let mut batch_bytes = 0usize;
        for batch_index in 0..batch_slots {
            let start = consumed + batch_bytes;
            let end = (start + slot_size).min(data.len());
            let slice = &data[start..end];
            let slot_index = (first_write_index as usize + batch_index) % state.slots.len();
            let token = first_write_index + batch_index as u64 + 1;
            let slot = &mut state.slots[slot_index];
            debug_assert!(!slot.full);
            slot.buffer[..slice.len()].copy_from_slice(slice);
            slot.token = token;
            slot.file_index = file_index;
            slot.file_offset = file_offset + start as u64;
            slot.length = slice.len() as u32;
            slot.flags = 0;
            slot.full = true;
            batch_bytes += slice.len();
        }
        state.write_index += batch_slots as u64;
        state.buffered_bytes += batch_bytes as u64;
        state.occupied_slots += batch_slots;
        shared
            .telemetry
            .bytes_published
            .fetch_add(batch_bytes as u64, Ordering::Relaxed);
        consumed += batch_bytes;
        shared.changed.notify_all();
        native_worker_progress(shared, Some(file_index), "publish batch");
    }
    #[cfg(all(debug_assertions, not(test)))]
    if waited_for_capacity {
        debug_log!(
            "event=publish-buffer-resumed\tfile={}\toffset={}\tbytes={}",
            file_index,
            file_offset,
            data.len()
        );
    }
    Ok(())
}

fn native_run_worker(
    shared: Arc<NativeShared>,
    config: NativeConfig,
    cancel_event: HANDLE,
    files: Vec<NativeFileTask>,
) {
    debug_log!(
        "event=worker-start\tfiles={}\tcapacity_bytes={}\tslot_size={}\tread_chunk_size={}\tqueue_depth={}",
        files.len(),
        config.capacity_bytes,
        config.slot_size,
        config.read_chunk_size,
        config.queue_depth
    );
    native_worker_progress(&shared, None, "detect target drive media");
    let is_hdd = files_include_hdd(&files);
    let scheduling = file_read_scheduling(
        config.small_open_concurrency,
        config.small_active_files,
        is_hdd,
    );
    debug_log!(
        "event=worker-scheduling\tis_hdd={}\tsmall_open_concurrency={}\tsmall_active_files={}\tcross_file_prefetch={}",
        is_hdd,
        scheduling.small_open_concurrency,
        scheduling.small_active_files,
        scheduling.cross_file_prefetch
    );
    native_worker_progress(&shared, None, "create small-file pool");
    let enabled = native_hash_options(config.hash_mask);
    let mut small_pool = match SmallFilePool::new(
        scheduling.small_open_concurrency,
        scheduling.small_active_files,
        config.small_inflight_bytes,
        config.small_threshold,
        64,
        cancel_event,
        config.io_policy,
    ) {
        Ok(pool) => pool,
        Err(error) => {
            native_set_error(&shared, error);
            return;
        }
    };

    if scheduling.cross_file_prefetch {
        for file in &files {
            if file.len <= config.small_threshold {
                small_pool.enqueue(
                    SmallFileTask {
                        index: file.index,
                        len: file.len,
                        path: file.path.clone(),
                    },
                    false,
                );
            }
        }
    }

    let run_result = (|| -> io::Result<()> {
        let mut prepared_large: Option<(u64, io::Result<AsyncSequentialReader>)> = None;
        for (position, file) in files.iter().enumerate() {
            #[cfg(all(debug_assertions, not(test)))]
            let file_started = Instant::now();
            debug_log!(
                "event=file-start\tfile={}\tlength={}\tposition={}\ttotal_files={}\tpath={:?}",
                file.index,
                file.len,
                position + 1,
                files.len(),
                file.path
            );
            native_worker_progress(&shared, Some(file.index), "start file");
            if is_cancelled(cancel_event) {
                return Err(cancelled_error());
            }

            let mut current_reader = None;
            let mut current_initial_error = None;
            if file.len > config.small_threshold {
                native_worker_progress(&shared, Some(file.index), "prepare current large file");
                let current_result = match prepared_large.take() {
                    Some((index, result)) if index == file.index => result,
                    Some(_) => {
                        return Err(io::Error::new(
                            io::ErrorKind::InvalidData,
                            "native next-file reader order mismatch",
                        ));
                    }
                    None => prepare_reader_once(
                        &file.path,
                        file.len,
                        ReaderPreparationPlan {
                            chunk_size: config.read_chunk_size,
                            queue_depth: config.queue_depth,
                            prime_depth: config.queue_depth,
                            cancel_event,
                            read_wait_counter: Some(Arc::clone(&shared.telemetry.read_wait_ns)),
                            io_policy: config.io_policy,
                        },
                    ),
                }
                .and_then(|mut reader| {
                    // A look-ahead reader starts shallow. Fill the current
                    // file's complete IOCP queue before doing other work.
                    reader.prime()?;
                    Ok(reader)
                });

                match current_result {
                    Ok(reader) => current_reader = Some(reader),
                    Err(error) => current_initial_error = Some(error),
                }
            }

            // Keep one request for the next large file in flight while the
            // current file is consumed. It is discarded together with all
            // later current-file requests if the current read fails.
            if scheduling.cross_file_prefetch
                && (file.len <= config.small_threshold || current_reader.is_some())
                && let Some(next) = files.get(position + 1)
                && next.len > config.small_threshold
            {
                native_worker_progress(&shared, Some(next.index), "prepare next large file");
                prepared_large = Some((
                    next.index,
                    prepare_reader_once(
                        &next.path,
                        next.len,
                        ReaderPreparationPlan {
                            chunk_size: config.read_chunk_size,
                            queue_depth: config.queue_depth,
                            prime_depth: config.next_file_prime_depth,
                            cancel_event,
                            read_wait_counter: Some(Arc::clone(&shared.telemetry.read_wait_ns)),
                            io_policy: config.io_policy,
                        },
                    ),
                ));
            }

            let mut hashes = HashSet::new(&enabled)?;
            if file.len <= config.small_threshold {
                native_worker_progress(&shared, Some(file.index), "wait for small file");
                let task = SmallFileTask {
                    index: file.index,
                    len: file.len,
                    path: file.path.clone(),
                };
                let cached = small_pool.wait_take(task).map_err(|error| {
                    io::Error::new(
                        error.kind(),
                        format!(
                            "small-file read failed: file={} path={}: {error}",
                            file.index, file.path
                        ),
                    )
                })?;
                let hash_started = Instant::now();
                hashes.update(&cached.data)?;
                shared.telemetry.hash_ns.fetch_add(
                    hash_started.elapsed().as_nanos().min(u64::MAX as u128) as u64,
                    Ordering::Relaxed,
                );
                shared
                    .telemetry
                    .bytes_read
                    .fetch_add(cached.data.len() as u64, Ordering::Relaxed);
                native_publish_batch(&shared, file.index, 0, &cached.data, config.slot_size)?;
                small_pool.release(file.index, cached);
            } else {
                native_worker_progress(&shared, Some(file.index), "read large file");
                read_file_overlapped_with_depth(
                    &file.path,
                    file.len,
                    OverlappedReadPlan {
                        chunk_size: config.read_chunk_size,
                        queue_depth: config.queue_depth,
                        cancel_event,
                        io_policy: config.io_policy,
                        read_wait_counter: Some(Arc::clone(&shared.telemetry.read_wait_ns)),
                        prepared_reader: current_reader,
                        initial_error: current_initial_error,
                    },
                    || {
                        prepared_large.take();
                    },
                    |offset, slice| {
                        shared
                            .telemetry
                            .bytes_read
                            .fetch_add(slice.len() as u64, Ordering::Relaxed);
                        let hash_started = Instant::now();
                        hashes.update(slice)?;
                        shared.telemetry.hash_ns.fetch_add(
                            hash_started.elapsed().as_nanos().min(u64::MAX as u128) as u64,
                            Ordering::Relaxed,
                        );
                        native_publish_batch(&shared, file.index, offset, slice, config.slot_size)
                    },
                )
                .map_err(|error| {
                    io::Error::new(
                        error.kind(),
                        format!(
                            "large-file read failed: file={} path={}: {error}",
                            file.index, file.path
                        ),
                    )
                })?;
            }
            native_worker_progress(&shared, Some(file.index), "finish hashes");
            let result = hashes.finish()?;
            {
                let mut state = native_lock(&shared);
                state.results.insert(file.index, result);
                shared.changed.notify_all();
            }
            native_publish(&shared, file.index, file.len, &[], FLAG_EOF)?;
            debug_log!(
                "event=file-complete\tfile={}\tlength={}\telapsed_ms={}\tpath={:?}",
                file.index,
                file.len,
                file_started.elapsed().as_millis(),
                file.path
            );
        }
        Ok(())
    })();

    match &run_result {
        Ok(()) => {
            debug_log!("event=worker-complete\tresult=ok");
            let mut state = native_lock(&shared);
            state.done = true;
            shared.changed.notify_all();
        }
        Err(error) if error.kind() == io::ErrorKind::Interrupted => {
            debug_log!("event=worker-complete\tresult=cancelled\terror={error}");
            let mut state = native_lock(&shared);
            state.cancelled = true;
            state.done = true;
            shared.changed.notify_all();
        }
        Err(error) => native_set_error(&shared, error),
    }
    small_pool.shutdown();
    debug_log!("event=worker-exit");
}

fn native_context<'a>(context: *mut LfrContext) -> Result<&'a LfrContext, i32> {
    unsafe { context.as_ref().ok_or(LFR_INVALID) }
}

fn native_copy_text(text: &str, buffer: *mut u8, capacity: u32, written: *mut u32) -> i32 {
    unsafe {
        if written.is_null() {
            return LFR_INVALID;
        }
        *written = text.len() as u32;
        if buffer.is_null() || capacity < text.len() as u32 + 1 {
            return LFR_BUFFER_TOO_SMALL;
        }
        std::ptr::copy_nonoverlapping(text.as_ptr(), buffer, text.len());
        *buffer.add(text.len()) = 0;
    }
    LFR_OK
}

#[unsafe(no_mangle)]
pub extern "system" fn lfr_abi_version() -> u32 {
    LFR_ABI_VERSION
}

#[unsafe(no_mangle)]
/// Creates a native fast-reader context.
///
/// # Safety
/// `config` and `output` must be valid, writable pointers for the duration of the call.
pub unsafe extern "system" fn lfr_create(
    config: *const LfrConfig,
    output: *mut *mut LfrContext,
) -> i32 {
    if config.is_null() || output.is_null() {
        debug_log!("event=create-rejected\treason=null-argument");
        return LFR_INVALID;
    }
    let config = unsafe { &*config };
    debug_log!(
        "event=create-request\tabi={}\tslot_size={}\tread_chunk_size={}\tqueue_depth={}\tcapacity_bytes={}\tsmall_open_concurrency={}\tsmall_active_files={}\tsmall_inflight_bytes={}\tsmall_threshold={}\thash_mask=0x{:X}\tnext_file_prime_depth={}\tread_stall_timeout_ms={}\tio_cancel_grace_ms={}\tmax_retries={}\tretry_base_delay_ms={}",
        config.abi_version,
        config.slot_size,
        config.read_chunk_size,
        config.queue_depth,
        config.capacity_bytes,
        config.small_open_concurrency,
        config.small_active_files,
        config.small_inflight_bytes,
        config.small_threshold,
        config.hash_mask,
        config.next_file_prime_depth,
        config.read_stall_timeout_ms,
        config.io_cancel_grace_ms,
        config.max_consecutive_file_retries,
        config.file_retry_base_delay_ms
    );
    if config.struct_size as usize != std::mem::size_of::<LfrConfig>()
        || config.abi_version != LFR_ABI_VERSION
        || config.slot_size == 0
        || config.read_chunk_size < config.slot_size
        || config.read_chunk_size > 64 * 1024 * 1024
        || !config.read_chunk_size.is_multiple_of(config.slot_size)
        || !(1..=128).contains(&config.queue_depth)
        || config.capacity_bytes < config.slot_size as u64 * 2
        || config.capacity_bytes > usize::MAX as u64
        || !config
            .capacity_bytes
            .is_multiple_of(config.slot_size as u64)
        || !(1..=128).contains(&config.small_open_concurrency)
        || !(1..=1024).contains(&config.small_active_files)
        || config.small_inflight_bytes < 64 * 1024
        || config.small_inflight_bytes < config.slot_size as u64
        || config.small_inflight_bytes > usize::MAX as u64
        || !(64 * 1024..=4 * 1024 * 1024).contains(&config.small_threshold)
        || small_buffer_class(config.small_threshold)
            .is_none_or(|(_, capacity)| config.small_inflight_bytes < capacity as u64)
        || config.hash_mask & !LFR_HASH_ALL != 0
        || !(1..=16).contains(&config.next_file_prime_depth)
        || config.next_file_prime_depth > config.queue_depth
        || !(MIN_READ_STALL_TIMEOUT_MS..=MAX_READ_STALL_TIMEOUT_MS)
            .contains(&config.read_stall_timeout_ms)
        || !(MIN_IO_CANCEL_GRACE_MS..=MAX_IO_CANCEL_GRACE_MS).contains(&config.io_cancel_grace_ms)
        || config.max_consecutive_file_retries > MAX_FILE_RETRIES
        || !(MIN_FILE_RETRY_BASE_DELAY_MS..=MAX_FILE_RETRY_BASE_DELAY_MS)
            .contains(&config.file_retry_base_delay_ms)
    {
        debug_log!("event=create-rejected\treason=invalid-config");
        return LFR_INVALID;
    }
    let slot_size = config.slot_size as usize;
    let slot_count = (config.capacity_bytes / config.slot_size as u64) as usize;
    let slots = (0..slot_count)
        .map(|_| NativeSlot {
            buffer: vec![0u8; slot_size].into_boxed_slice(),
            token: 0,
            file_index: 0,
            file_offset: 0,
            length: 0,
            flags: 0,
            full: false,
        })
        .collect();
    let cancel_event = unsafe { CreateEventW(null(), 1, 0, null()) };
    if cancel_event.is_null() {
        debug_log!(
            "event=create-failed\toperation=CreateEventW\terror={}",
            io::Error::last_os_error()
        );
        return LFR_ERROR;
    }
    let context = Box::new(LfrContext {
        shared: Arc::new(NativeShared {
            state: Mutex::new(NativeState {
                slots,
                files: FxHashMap::default(),
                file_order: Vec::new(),
                write_index: 0,
                read_index: 0,
                buffered_bytes: 0,
                occupied_slots: 0,
                selected_bytes: 0,
                started: false,
                done: false,
                cancelled: false,
                error: String::new(),
                results: FxHashMap::default(),
            }),
            changed: Condvar::new(),
            telemetry: NativeTelemetry::default(),
            worker_watch: Mutex::new(NativeWorkerWatch {
                file_index: None,
                stage: "starting",
                last_progress: Instant::now(),
                cancel_requested_at: None,
                cancel_error: None,
            }),
        }),
        config: NativeConfig {
            slot_size,
            read_chunk_size: config.read_chunk_size as usize,
            queue_depth: config.queue_depth as usize,
            capacity_bytes: config.capacity_bytes,
            small_open_concurrency: config.small_open_concurrency as usize,
            small_active_files: config.small_active_files as usize,
            small_inflight_bytes: config.small_inflight_bytes as usize,
            small_threshold: config.small_threshold,
            hash_mask: config.hash_mask,
            next_file_prime_depth: config.next_file_prime_depth as usize,
            io_policy: ReaderIoPolicy {
                read_stall_timeout: Duration::from_millis(config.read_stall_timeout_ms as u64),
                cancel_grace: Duration::from_millis(config.io_cancel_grace_ms as u64),
                max_consecutive_file_retries: config.max_consecutive_file_retries as u8,
                retry_base_delay_ms: config.file_retry_base_delay_ms as u64,
            },
        },
        cancel_event: Handle(cancel_event),
        worker: Mutex::new(None),
        worker_watchdog: Mutex::new(None),
    });
    let context = Box::into_raw(context);
    unsafe { *output = context };
    debug_log!("event=create-complete\tcontext={context:p}\tslots={slot_count}");
    LFR_OK
}

#[unsafe(no_mangle)]
/// Adds a file to a context before it is started.
///
/// # Safety
/// `context` must be a valid context pointer, and `path` must point to `path_len` UTF-16 code units.
pub unsafe extern "system" fn lfr_add_file(
    context: *mut LfrContext,
    index: i64,
    path: *const u16,
    path_len: u32,
    expected_len: u64,
) -> i32 {
    let context = match native_context(context) {
        Ok(value) => value,
        Err(code) => return code,
    };
    if index < 0 || path.is_null() {
        return LFR_INVALID;
    }
    let path =
        match String::from_utf16(unsafe { std::slice::from_raw_parts(path, path_len as usize) }) {
            Ok(value) => value,
            Err(_) => return LFR_INVALID,
        };
    let file_index = index as u64;
    let mut state = native_lock(&context.shared);
    if state.started || state.files.contains_key(&file_index) {
        return LFR_INVALID;
    }
    state.file_order.push(file_index);
    state.files.insert(
        file_index,
        NativeFileTask {
            index: file_index,
            len: expected_len,
            path,
            selected: false,
        },
    );
    LFR_OK
}

#[unsafe(no_mangle)]
/// Selects a file for processing.
///
/// # Safety
/// `context` must be a valid context pointer created by `lfr_create`.
pub unsafe extern "system" fn lfr_select_file(context: *mut LfrContext, index: i64) -> i32 {
    let context = match native_context(context) {
        Ok(value) => value,
        Err(code) => return code,
    };
    if index < 0 {
        return LFR_INVALID;
    }
    let mut state = native_lock(&context.shared);
    if state.started {
        return LFR_INVALID;
    }
    match state.files.get_mut(&(index as u64)) {
        Some(file) => {
            file.selected = true;
            LFR_OK
        }
        None => LFR_INVALID,
    }
}

#[unsafe(no_mangle)]
/// Starts processing the selected files.
///
/// # Safety
/// `context` must be a valid context pointer created by `lfr_create` and not concurrently destroyed.
pub unsafe extern "system" fn lfr_start(context: *mut LfrContext) -> i32 {
    let context = match native_context(context) {
        Ok(value) => value,
        Err(code) => return code,
    };
    let files = {
        let mut state = native_lock(&context.shared);
        if state.started {
            return LFR_INVALID;
        }
        state.started = true;
        let files = state
            .file_order
            .iter()
            .filter_map(|index| state.files.get(index))
            .filter(|file| file.selected)
            .cloned()
            .collect::<Vec<_>>();
        state.selected_bytes = files
            .iter()
            .fold(0u64, |total, file| total.saturating_add(file.len));
        files
    };
    #[cfg(all(debug_assertions, not(test)))]
    let selected_bytes = files
        .iter()
        .fold(0u64, |total, file| total.saturating_add(file.len));
    debug_log!(
        "event=start\tcontext={context:p}\tfiles={}\tselected_bytes={selected_bytes}",
        files.len()
    );
    let shared = Arc::clone(&context.shared);
    let config = NativeConfig {
        slot_size: context.config.slot_size,
        read_chunk_size: context.config.read_chunk_size,
        queue_depth: context.config.queue_depth,
        capacity_bytes: context.config.capacity_bytes,
        small_open_concurrency: context.config.small_open_concurrency,
        small_active_files: context.config.small_active_files,
        small_inflight_bytes: context.config.small_inflight_bytes,
        small_threshold: context.config.small_threshold,
        hash_mask: context.config.hash_mask,
        next_file_prime_depth: context.config.next_file_prime_depth,
        io_policy: context.config.io_policy,
    };
    let cancel_event = context.cancel_event.0 as usize;
    let worker =
        thread::spawn(move || native_run_worker(shared, config, cancel_event as HANDLE, files));
    let worker_thread = worker.as_raw_handle() as HANDLE as usize;
    debug_log!("event=start-worker-created\tthread_handle=0x{worker_thread:X}");
    let watchdog_shared = Arc::clone(&context.shared);
    let watchdog_policy = context.config.io_policy;
    let watchdog = thread::spawn(move || {
        native_worker_watchdog(watchdog_shared, watchdog_policy, worker_thread)
    });
    let (mut worker_slot, worker_mutex_poisoned) = match context.worker.lock() {
        Ok(worker_slot) => (worker_slot, false),
        Err(poisoned) => {
            native_set_error(
                &context.shared,
                "native fast-reader worker mutex poisoned during start",
            );
            context.worker.clear_poison();
            (poisoned.into_inner(), true)
        }
    };
    *worker_slot = Some(worker);
    let (mut watchdog_slot, watchdog_mutex_poisoned) = match context.worker_watchdog.lock() {
        Ok(watchdog_slot) => (watchdog_slot, false),
        Err(poisoned) => {
            native_set_error(
                &context.shared,
                "native fast-reader watchdog mutex poisoned during start",
            );
            context.worker_watchdog.clear_poison();
            (poisoned.into_inner(), true)
        }
    };
    *watchdog_slot = Some(watchdog);
    if worker_mutex_poisoned || watchdog_mutex_poisoned {
        LFR_ERROR
    } else {
        LFR_OK
    }
}

#[unsafe(no_mangle)]
/// Returns the number of bytes currently buffered.
///
/// # Safety
/// `context` must be a valid context pointer created by `lfr_create`.
pub unsafe extern "system" fn lfr_buffered_bytes(context: *mut LfrContext) -> u64 {
    match native_context(context) {
        Ok(context) => native_lock(&context.shared).buffered_bytes,
        Err(_) => 0,
    }
}

#[unsafe(no_mangle)]
/// Returns the configured native buffer capacity.
///
/// # Safety
/// `context` must be a valid context pointer created by `lfr_create`.
pub unsafe extern "system" fn lfr_buffer_capacity(context: *mut LfrContext) -> u64 {
    match native_context(context) {
        Ok(context) => context.config.capacity_bytes,
        Err(_) => 0,
    }
}

#[unsafe(no_mangle)]
/// Returns the number of occupied output slots.
///
/// # Safety
/// `context` must be a valid context pointer created by `lfr_create`.
pub unsafe extern "system" fn lfr_occupied_slots(context: *mut LfrContext) -> u64 {
    match native_context(context) {
        Ok(context) => native_lock(&context.shared).occupied_slots as u64,
        Err(_) => 0,
    }
}

#[unsafe(no_mangle)]
/// Retrieves processing statistics.
///
/// # Safety
/// `context` must be valid and `output` must point to a writable `LfrStats` value with the expected ABI size.
pub unsafe extern "system" fn lfr_get_stats(
    context: *mut LfrContext,
    output: *mut LfrStats,
) -> i32 {
    let context = match native_context(context) {
        Ok(value) => value,
        Err(code) => return code,
    };
    if output.is_null()
        || unsafe { (*output).struct_size as usize != std::mem::size_of::<LfrStats>() }
    {
        return LFR_INVALID;
    }
    let state = native_lock(&context.shared);
    unsafe {
        *output = LfrStats {
            struct_size: std::mem::size_of::<LfrStats>() as u32,
            abi_version: LFR_ABI_VERSION,
            bytes_read: context.shared.telemetry.bytes_read.load(Ordering::Relaxed),
            bytes_published: context
                .shared
                .telemetry
                .bytes_published
                .load(Ordering::Relaxed),
            buffered_bytes: state.buffered_bytes,
            occupied_slots: state.occupied_slots as u64,
            read_wait_ns: context
                .shared
                .telemetry
                .read_wait_ns
                .load(Ordering::Relaxed),
            hash_ns: context.shared.telemetry.hash_ns.load(Ordering::Relaxed),
            publish_wait_ns: context
                .shared
                .telemetry
                .publish_wait_ns
                .load(Ordering::Relaxed),
        };
    }
    LFR_OK
}

#[unsafe(no_mangle)]
/// Reports whether processing has completed.
///
/// # Safety
/// `context` must be a valid context pointer created by `lfr_create`.
pub unsafe extern "system" fn lfr_is_done(context: *mut LfrContext) -> i32 {
    match native_context(context) {
        Ok(context) => i32::from(native_lock(&context.shared).done),
        Err(_) => 1,
    }
}

#[unsafe(no_mangle)]
/// Waits until the requested amount of data is buffered or progress stops.
///
/// # Safety
/// `context` must be a valid context pointer created by `lfr_create`.
pub unsafe extern "system" fn lfr_wait_until_buffered(
    context: *mut LfrContext,
    target: u64,
    timeout_ms: u32,
) -> i32 {
    let context = match native_context(context) {
        Ok(value) => value,
        Err(code) => return code,
    };
    let mut state = native_lock(&context.shared);
    let stagnant_limit = Duration::from_millis(timeout_ms as u64);
    let mut unchanged_since = Instant::now();
    let mut last_buffered_bytes = state.buffered_bytes;
    let mut last_occupied_slots = state.occupied_slots;
    loop {
        if state.cancelled {
            return LFR_CANCELLED;
        }
        if !state.error.is_empty() {
            return LFR_ERROR;
        }
        if state.buffered_bytes >= target || state.done || state.occupied_slots == state.slots.len()
        {
            return LFR_OK;
        }
        if timeout_ms == 0 {
            return LFR_TIMEOUT;
        }
        if timeout_ms == u32::MAX {
            state = native_wait(&context.shared, state, "buffer wait");
        } else {
            if state.buffered_bytes != last_buffered_bytes
                || state.occupied_slots != last_occupied_slots
            {
                last_buffered_bytes = state.buffered_bytes;
                last_occupied_slots = state.occupied_slots;
                unchanged_since = Instant::now();
            }
            let timeout = stagnant_limit.saturating_sub(unchanged_since.elapsed());
            if timeout.is_zero() {
                return if context.shared.telemetry.bytes_read.load(Ordering::Acquire)
                    >= state.selected_bytes
                {
                    LFR_OK
                } else {
                    LFR_TIMEOUT
                };
            }
            let (next, result) =
                native_wait_timeout(&context.shared, state, timeout, "timed buffer wait");
            state = next;
            if result.timed_out() {
                if state.buffered_bytes != last_buffered_bytes
                    || state.occupied_slots != last_occupied_slots
                {
                    last_buffered_bytes = state.buffered_bytes;
                    last_occupied_slots = state.occupied_slots;
                    unchanged_since = Instant::now();
                    continue;
                }
                return if context.shared.telemetry.bytes_read.load(Ordering::Acquire)
                    >= state.selected_bytes
                {
                    LFR_OK
                } else {
                    LFR_TIMEOUT
                };
            }
        }
    }
}

#[unsafe(no_mangle)]
/// Acquires the next output slot.
///
/// # Safety
/// `context` must be valid and `output` must point to writable storage for an `LfrSlot`.
pub unsafe extern "system" fn lfr_acquire_slot(
    context: *mut LfrContext,
    expected_file_index: i64,
    timeout_ms: u32,
    output: *mut LfrSlot,
) -> i32 {
    let context = match native_context(context) {
        Ok(value) => value,
        Err(code) => return code,
    };
    if output.is_null() {
        return LFR_INVALID;
    }
    let start = std::time::Instant::now();
    let mut state = native_lock(&context.shared);
    loop {
        let slot_index = state.read_index as usize % state.slots.len();
        let slot = &state.slots[slot_index];
        if slot.full {
            if expected_file_index >= 0 && slot.file_index != expected_file_index as u64 {
                return LFR_ERROR;
            }
            unsafe {
                *output = LfrSlot {
                    token: slot.token,
                    file_index: slot.file_index as i64,
                    file_offset: slot.file_offset,
                    data: slot.buffer.as_ptr(),
                    length: slot.length,
                    flags: slot.flags,
                };
            }
            return LFR_OK;
        }
        if state.cancelled {
            return LFR_CANCELLED;
        }
        if !state.error.is_empty() {
            return LFR_ERROR;
        }
        if state.done {
            return LFR_DONE;
        }
        if timeout_ms == 0 {
            return LFR_TIMEOUT;
        }
        if timeout_ms == u32::MAX {
            state = native_wait(&context.shared, state, "slot acquisition wait");
        } else {
            let timeout = Duration::from_millis(timeout_ms as u64).saturating_sub(start.elapsed());
            if timeout.is_zero() {
                return LFR_TIMEOUT;
            }
            let (next, result) = native_wait_timeout(
                &context.shared,
                state,
                timeout,
                "timed slot acquisition wait",
            );
            state = next;
            if result.timed_out() {
                return LFR_TIMEOUT;
            }
        }
    }
}

#[unsafe(no_mangle)]
/// Releases a previously acquired output slot.
///
/// # Safety
/// `context` must be a valid context pointer and `token` must identify the currently acquired slot.
pub unsafe extern "system" fn lfr_release_slot(context: *mut LfrContext, token: u64) -> i32 {
    let context = match native_context(context) {
        Ok(value) => value,
        Err(code) => return code,
    };
    let mut state = native_lock(&context.shared);
    let slot_index = state.read_index as usize % state.slots.len();
    let slot = &state.slots[slot_index];
    if !slot.full || slot.token != token {
        return LFR_INVALID;
    }
    let length = slot.length as u64;
    let slot = &mut state.slots[slot_index];
    slot.full = false;
    slot.length = 0;
    slot.flags = 0;
    state.read_index += 1;
    state.buffered_bytes = state.buffered_bytes.saturating_sub(length);
    state.occupied_slots = state.occupied_slots.saturating_sub(1);
    context.shared.changed.notify_all();
    LFR_OK
}

#[unsafe(no_mangle)]
/// Hashes a file independently of the streaming worker.
///
/// # Safety
/// `context` must be valid; `buffer` and `written` must follow the output-buffer contract when non-null.
pub unsafe extern "system" fn lfr_hash_file(
    context: *mut LfrContext,
    index: i64,
    buffer: *mut u8,
    capacity: u32,
    written: *mut u32,
) -> i32 {
    let context = match native_context(context) {
        Ok(value) => value,
        Err(code) => return code,
    };
    if index < 0 {
        return LFR_INVALID;
    }
    let file = {
        let state = native_lock(&context.shared);
        match state.files.get(&(index as u64)) {
            Some(file) => file.clone(),
            None => return LFR_INVALID,
        }
    };
    let enabled = native_hash_options(context.config.hash_mask);
    let mut hashes = match HashSet::new(&enabled) {
        Ok(value) => value,
        Err(error) => {
            native_set_error(&context.shared, error);
            return LFR_ERROR;
        }
    };
    let result = read_file_overlapped(
        &file.path,
        file.len,
        HASH_CHUNK_SIZE,
        context.cancel_event.0,
        context.config.io_policy,
        |_offset, slice| hashes.update(slice),
    )
    .and_then(|_| hashes.finish());
    match result {
        Ok(text) => native_copy_text(&text, buffer, capacity, written),
        Err(error) if error.kind() == io::ErrorKind::Interrupted => LFR_CANCELLED,
        Err(error) => {
            native_set_error(&context.shared, error);
            LFR_ERROR
        }
    }
}

#[unsafe(no_mangle)]
/// Retrieves hashes produced by the streaming worker.
///
/// # Safety
/// `context` must be valid; `buffer` and `written` must follow the output-buffer contract when non-null.
pub unsafe extern "system" fn lfr_get_file_hashes(
    context: *mut LfrContext,
    index: i64,
    buffer: *mut u8,
    capacity: u32,
    written: *mut u32,
) -> i32 {
    let context = match native_context(context) {
        Ok(value) => value,
        Err(code) => return code,
    };
    let state = native_lock(&context.shared);
    match state.results.get(&(index as u64)) {
        Some(text) => native_copy_text(text, buffer, capacity, written),
        None if state.cancelled => LFR_CANCELLED,
        None if !state.error.is_empty() => LFR_ERROR,
        None => LFR_TIMEOUT,
    }
}

#[unsafe(no_mangle)]
/// Retrieves the last context error message.
///
/// # Safety
/// `context` must be valid; `buffer` and `written` must follow the output-buffer contract when non-null.
pub unsafe extern "system" fn lfr_last_error(
    context: *mut LfrContext,
    buffer: *mut u8,
    capacity: u32,
    written: *mut u32,
) -> i32 {
    let context = match native_context(context) {
        Ok(value) => value,
        Err(code) => return code,
    };
    let state = native_lock(&context.shared);
    native_copy_text(&state.error, buffer, capacity, written)
}

#[unsafe(no_mangle)]
/// Cancels processing.
///
/// # Safety
/// `context` must be a valid context pointer created by `lfr_create`.
pub unsafe extern "system" fn lfr_cancel(context: *mut LfrContext) -> i32 {
    let context = match native_context(context) {
        Ok(value) => value,
        Err(code) => return code,
    };
    {
        let mut state = native_lock(&context.shared);
        #[cfg(all(debug_assertions, not(test)))]
        let snapshot = (state.buffered_bytes, state.occupied_slots, state.done);
        state.cancelled = true;
        context.shared.changed.notify_all();
        debug_log!(
            "event=cancel\tcontext={context:p}\tbuffered_bytes={}\toccupied_slots={}\tdone={}",
            snapshot.0,
            snapshot.1,
            snapshot.2
        );
    }
    unsafe {
        SetEvent(context.cancel_event.0);
    }
    LFR_OK
}

#[unsafe(no_mangle)]
/// Destroys a context and waits for its worker to exit.
///
/// # Safety
/// `context` must be null or a context returned by `lfr_create` that has not already been destroyed.
pub unsafe extern "system" fn lfr_destroy(context: *mut LfrContext) {
    if context.is_null() {
        return;
    }
    #[cfg(all(debug_assertions, not(test)))]
    let destroy_started = Instant::now();
    debug_log!("event=destroy-begin\tcontext={context:p}");
    let context = unsafe { Box::from_raw(context) };
    {
        let mut state = native_lock(&context.shared);
        state.cancelled = true;
        context.shared.changed.notify_all();
    }
    unsafe {
        SetEvent(context.cancel_event.0);
    }
    let worker = match context.worker.lock() {
        Ok(worker) => worker,
        Err(poisoned) => {
            eprintln!("STATE_POISON\tnative fast-reader worker mutex poisoned during destroy");
            io::stderr().flush().ok();
            context.worker.clear_poison();
            poisoned.into_inner()
        }
    }
    .take();
    if let Some(worker) = worker {
        debug_log!("event=destroy-worker-join-begin");
        let _result = worker.join();
        debug_log!(
            "event=destroy-worker-join-end\telapsed_ms={}\tpanicked={}",
            destroy_started.elapsed().as_millis(),
            _result.is_err()
        );
    }
    let watchdog = match context.worker_watchdog.lock() {
        Ok(watchdog) => watchdog,
        Err(poisoned) => {
            eprintln!("STATE_POISON\tnative fast-reader watchdog mutex poisoned during destroy");
            io::stderr().flush().ok();
            context.worker_watchdog.clear_poison();
            poisoned.into_inner()
        }
    }
    .take();
    if let Some(watchdog) = watchdog {
        debug_log!("event=destroy-watchdog-join-begin");
        let _result = watchdog.join();
        debug_log!(
            "event=destroy-watchdog-join-end\telapsed_ms={}\tpanicked={}",
            destroy_started.elapsed().as_millis(),
            _result.is_err()
        );
    }
    debug_log!(
        "event=destroy-complete\telapsed_ms={}",
        destroy_started.elapsed().as_millis()
    );
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;
    use std::path::PathBuf;
    use std::thread;
    use std::time::{SystemTime, UNIX_EPOCH};

    struct TempFile(PathBuf);

    impl TempFile {
        fn create(label: &str, data: &[u8]) -> io::Result<Self> {
            let unique = SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap()
                .as_nanos();
            let path = std::env::temp_dir().join(format!(
                "ltfscopy-fastreader-{label}-{}-{unique}.bin",
                std::process::id()
            ));
            fs::write(&path, data)?;
            Ok(Self(path))
        }
    }

    impl Drop for TempFile {
        fn drop(&mut self) {
            let _ = fs::remove_file(&self.0);
        }
    }

    fn native_test_config(slot_size: u32, capacity_bytes: u64) -> LfrConfig {
        LfrConfig {
            struct_size: std::mem::size_of::<LfrConfig>() as u32,
            abi_version: LFR_ABI_VERSION,
            slot_size,
            read_chunk_size: slot_size.max(16 * 1024),
            queue_depth: 4,
            capacity_bytes,
            small_open_concurrency: 4,
            small_active_files: 8,
            small_inflight_bytes: 2 * 1024 * 1024,
            small_threshold: 64 * 1024,
            hash_mask: LFR_HASH_CRC32,
            next_file_prime_depth: 1,
            read_stall_timeout_ms: DEFAULT_READ_STALL_TIMEOUT_MS,
            io_cancel_grace_ms: DEFAULT_IO_CANCEL_GRACE_MS,
            max_consecutive_file_retries: DEFAULT_MAX_CONSECUTIVE_FILE_RETRIES,
            file_retry_base_delay_ms: DEFAULT_FILE_RETRY_BASE_DELAY_MS,
        }
    }

    #[test]
    fn extended_paths_extract_storage_device_without_rewriting_the_source() {
        let standard = r"\\?\d:\a\b\c\d.txt";
        let volume = r"\\?\Volume{01234567-89ab-cdef-0123-456789abcdef}\a\d.txt";

        assert_eq!(storage_device_path(standard).as_deref(), Some(r"\\.\D:"));
        assert_eq!(
            storage_device_path(volume).as_deref(),
            Some(r"\\?\Volume{01234567-89ab-cdef-0123-456789abcdef}")
        );
        assert_eq!(standard, r"\\?\d:\a\b\c\d.txt");
    }

    #[test]
    fn network_unc_paths_do_not_claim_a_local_storage_device() {
        assert_eq!(storage_device_path(r"\\server\share\a\d.txt"), None);
        assert_eq!(storage_device_path(r"\\?\UNC\server\share\a\d.txt"), None);
    }

    #[test]
    fn drive_media_is_hdd_for_seek_penalty_or_reported_rpm() {
        assert!(
            DriveMediaInfo {
                incurs_seek_penalty: Some(true),
                nominal_rotation_rate: None,
            }
            .is_hdd()
        );
        assert!(
            DriveMediaInfo {
                incurs_seek_penalty: Some(false),
                nominal_rotation_rate: Some(7_200),
            }
            .is_hdd()
        );
        for non_hdd_rate in [0, 1, 1_024, u16::MAX] {
            assert!(
                !DriveMediaInfo {
                    incurs_seek_penalty: Some(false),
                    nominal_rotation_rate: Some(non_hdd_rate),
                }
                .is_hdd(),
                "incorrectly classified ATA rotation word {non_hdd_rate:#06x} as HDD"
            );
        }
    }

    #[test]
    fn hdd_scheduling_uses_one_worker_and_disables_cross_file_prefetch() {
        assert_eq!(
            file_read_scheduling(16, 64, true),
            FileReadScheduling {
                small_open_concurrency: 1,
                small_active_files: 1,
                cross_file_prefetch: false,
            }
        );
        assert_eq!(
            file_read_scheduling(16, 64, false),
            FileReadScheduling {
                small_open_concurrency: 16,
                small_active_files: 64,
                cross_file_prefetch: true,
            }
        );
    }

    #[test]
    fn ata_identify_query_reads_nominal_rotation_word_217() {
        let descriptor_size = std::mem::size_of::<STORAGE_PROTOCOL_DATA_DESCRIPTOR>();
        assert_eq!(std::mem::offset_of!(AtaIdentifyQuery, protocol), 8);
        assert_eq!(
            std::mem::offset_of!(AtaIdentifyQuery, identify),
            descriptor_size
        );

        let mut query = AtaIdentifyQuery {
            property_id: descriptor_size as i32,
            query_type: std::mem::size_of::<AtaIdentifyQuery>() as i32,
            protocol: STORAGE_PROTOCOL_SPECIFIC_DATA {
                ProtocolType: ProtocolTypeAta,
                DataType: AtaDataTypeIdentify as u32,
                ProtocolDataOffset: std::mem::size_of::<STORAGE_PROTOCOL_SPECIFIC_DATA>() as u32,
                ProtocolDataLength: 512,
                ..Default::default()
            },
            identify: [0; 512],
        };
        query.identify[217 * 2..217 * 2 + 2].copy_from_slice(&7_200u16.to_le_bytes());

        assert_eq!(
            ata_rotation_rate_from_query(&query, std::mem::size_of_val(&query) as u32),
            Some(7_200)
        );
    }

    #[test]
    fn large_file_catalog_lookup_handles_90000_files() {
        const FILE_COUNT: usize = 90_000;
        let path = r"C:\ltfscopy-fastreader-large-catalog-placeholder.bin"
            .encode_utf16()
            .collect::<Vec<_>>();
        let config = native_test_config(4096, 32 * 1024);

        unsafe {
            let context = create_native_test_context(&config);
            for index in 0..FILE_COUNT {
                assert_eq!(
                    lfr_add_file(context, index as i64, path.as_ptr(), path.len() as u32, 0,),
                    LFR_OK
                );
            }
            assert_eq!(
                lfr_add_file(
                    context,
                    (FILE_COUNT - 1) as i64,
                    path.as_ptr(),
                    path.len() as u32,
                    0,
                ),
                LFR_INVALID
            );

            for index in 0..FILE_COUNT {
                assert_eq!(lfr_select_file(context, index as i64), LFR_OK);
            }
            assert_eq!(lfr_select_file(context, FILE_COUNT as i64), LFR_INVALID);
            lfr_destroy(context);
        }
    }

    unsafe fn create_native_test_context(config: &LfrConfig) -> *mut LfrContext {
        let mut context = null_mut();
        assert_eq!(unsafe { lfr_create(config, &mut context) }, LFR_OK);
        assert!(!context.is_null());
        context
    }

    #[test]
    fn native_config_rejects_out_of_contract_values() {
        unsafe fn assert_invalid(label: &str, config: &LfrConfig) {
            let mut context = null_mut();
            assert_eq!(
                unsafe { lfr_create(config, &mut context) },
                LFR_INVALID,
                "accepted invalid config: {label}"
            );
            assert!(context.is_null());
        }

        let mut config = native_test_config(4096, 32 * 1024);
        config.queue_depth = 0;
        unsafe { assert_invalid("queue depth", &config) };

        let config = native_test_config(4096, 32 * 1024 + 1);
        unsafe { assert_invalid("unaligned capacity", &config) };

        let mut config = native_test_config(4096, 32 * 1024);
        config.hash_mask = 1 << 31;
        unsafe { assert_invalid("hash mask", &config) };

        let mut config = native_test_config(4096, 32 * 1024);
        config.read_stall_timeout_ms = MIN_READ_STALL_TIMEOUT_MS - 1;
        unsafe { assert_invalid("stall timeout", &config) };

        let mut config = native_test_config(4096, 32 * 1024);
        config.max_consecutive_file_retries = MAX_FILE_RETRIES + 1;
        unsafe { assert_invalid("retry count", &config) };

        let mut config = native_test_config(4096, 32 * 1024);
        config.small_threshold = 2 * 1024 * 1024 + 1;
        config.small_inflight_bytes = 2 * 1024 * 1024 + 1;
        unsafe { assert_invalid("small inflight class", &config) };
    }

    #[test]
    fn native_copy_text_distinguishes_buffer_queries_from_invalid_arguments() {
        let mut written = 0u32;
        assert_eq!(
            native_copy_text("abc", null_mut(), 0, &mut written),
            LFR_BUFFER_TOO_SMALL
        );
        assert_eq!(written, 3);

        let mut too_small = [0u8; 3];
        assert_eq!(
            native_copy_text("abc", too_small.as_mut_ptr(), 3, &mut written),
            LFR_BUFFER_TOO_SMALL
        );

        let mut enough = [0xFFu8; 4];
        assert_eq!(
            native_copy_text("abc", enough.as_mut_ptr(), 4, &mut written),
            LFR_OK
        );
        assert_eq!(&enough, b"abc\0");
        assert_eq!(
            native_copy_text("abc", null_mut(), 0, null_mut()),
            LFR_INVALID
        );
    }

    #[test]
    fn poisoned_native_state_is_reported_and_not_silently_reused() {
        let config = native_test_config(4096, 32 * 1024);
        unsafe {
            let context = create_native_test_context(&config);
            let shared = Arc::clone(&(*context).shared);
            let panic_result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
                let _state = shared.state.lock().unwrap();
                panic!("intentional state poison");
            }));
            assert!(panic_result.is_err());

            let state = native_lock(&shared);
            assert!(state.done);
            assert!(state.error.contains("mutex poisoned"));
            drop(state);
            assert!(!shared.state.is_poisoned());
            lfr_destroy(context);
        }
    }

    #[test]
    fn completed_requests_are_selected_by_offset() {
        let mut requests = vec![
            ReadRequest::new(4096, MIN_DIRECT_BUFFER_ALIGNMENT).unwrap(),
            ReadRequest::new(4096, MIN_DIRECT_BUFFER_ALIGNMENT).unwrap(),
            ReadRequest::new(4096, MIN_DIRECT_BUFFER_ALIGNMENT).unwrap(),
        ];
        for (request, offset) in requests.iter_mut().zip([8192, 0, 4096]) {
            request.offset = offset;
            request.state = RequestState::Completed(Ok(1));
        }

        assert_eq!(completed_request_at(&requests, 0), Some(1));
        assert_eq!(completed_request_at(&requests, 4096), Some(2));
        assert_eq!(completed_request_at(&requests, 12288), None);
    }

    #[test]
    fn reader_primes_concurrent_current_and_shallow_lookahead_queues() -> io::Result<()> {
        const CHUNK: usize = 4096;
        let expected = vec![0x5Au8; CHUNK * 6];
        let file = TempFile::create("concurrent-prime", &expected)?;
        let path = file.0.to_str().unwrap();

        let current = prepare_reader_once(
            path,
            expected.len() as u64,
            ReaderPreparationPlan {
                chunk_size: CHUNK,
                queue_depth: 4,
                prime_depth: 4,
                cancel_event: null_mut(),
                read_wait_counter: None,
                io_policy: ReaderIoPolicy::default(),
            },
        )?;
        assert_eq!(current.outstanding, 4);
        assert_eq!(current.next_submit, (CHUNK * 4) as u64);
        drop(current);

        let lookahead = prepare_reader_once(
            path,
            expected.len() as u64,
            ReaderPreparationPlan {
                chunk_size: CHUNK,
                queue_depth: 4,
                prime_depth: 1,
                cancel_event: null_mut(),
                read_wait_counter: None,
                io_policy: ReaderIoPolicy::default(),
            },
        )?;
        assert_eq!(lookahead.outstanding, 1);
        assert_eq!(lookahead.next_submit, CHUNK as u64);
        drop(lookahead);
        Ok(())
    }

    #[test]
    fn file_retries_are_limited_and_reset_after_progress() {
        let policy = ReaderIoPolicy::default();
        let mut retries = 0;
        assert_eq!(next_file_retry(policy, &mut retries, false), Some(1));
        assert_eq!(next_file_retry(policy, &mut retries, false), Some(2));
        assert_eq!(next_file_retry(policy, &mut retries, false), Some(3));
        assert_eq!(next_file_retry(policy, &mut retries, false), None);

        assert_eq!(next_file_retry(policy, &mut retries, true), Some(1));
        assert_eq!(retries, 1);
    }

    #[test]
    fn file_retry_backoff_is_exponential() {
        let policy = ReaderIoPolicy::default();
        assert_eq!(file_retry_delay(policy, 1), Duration::from_secs(1));
        assert_eq!(file_retry_delay(policy, 2), Duration::from_secs(2));
        assert_eq!(file_retry_delay(policy, 3), Duration::from_secs(4));
    }

    #[test]
    fn only_recoverable_file_read_errors_are_retried() {
        assert!(retryable_file_read_error(&io::Error::new(
            io::ErrorKind::TimedOut,
            "timeout"
        )));
        assert!(retryable_file_read_error(&io::Error::new(
            io::ErrorKind::UnexpectedEof,
            "short read"
        )));
        assert!(!retryable_file_read_error(&io::Error::new(
            io::ErrorKind::InvalidData,
            "length changed"
        )));
        assert!(!retryable_file_read_error(&io::Error::new(
            io::ErrorKind::PermissionDenied,
            "denied"
        )));
        assert!(!retryable_file_read_error(&io::Error::from_raw_os_error(
            ERROR_FILE_NOT_FOUND as i32
        )));
        assert!(!retryable_file_read_error(&io::Error::from_raw_os_error(
            ERROR_SHARING_VIOLATION as i32
        )));
        assert!(retryable_file_read_error(&io::Error::from_raw_os_error(
            windows_sys::Win32::Foundation::ERROR_NETNAME_DELETED as i32
        )));
        assert!(!retryable_file_read_error(&io::Error::new(
            io::ErrorKind::NotFound,
            "missing without a Windows status"
        )));
    }

    #[test]
    fn overlapped_reader_preserves_bytes_and_order() -> io::Result<()> {
        const CHUNK: usize = 4096;
        for (case, len) in [
            ("empty", 0usize),
            ("small", 173usize),
            ("partial", CHUNK * 2 + 37),
            ("queue16", CHUNK * (IO_QUEUE_DEPTH + 3) + 211),
        ] {
            let expected = (0..len)
                .map(|index| ((index * 31 + 7) % 251) as u8)
                .collect::<Vec<_>>();
            let file = TempFile::create(case, &expected)?;
            let mut actual = Vec::with_capacity(len);
            read_file_overlapped(
                file.0.to_str().unwrap(),
                len as u64,
                CHUNK,
                null_mut(),
                ReaderIoPolicy::default(),
                |offset, slice| {
                    assert_eq!(offset, actual.len() as u64);
                    actual.extend_from_slice(slice);
                    Ok(())
                },
            )?;
            assert_eq!(actual, expected, "failed case {case}");
        }
        Ok(())
    }

    #[test]
    fn overlapped_reader_rejects_length_changes() -> io::Result<()> {
        let file = TempFile::create("length", b"abcdef")?;
        let result = read_file_overlapped(
            file.0.to_str().unwrap(),
            5,
            4096,
            null_mut(),
            ReaderIoPolicy::default(),
            |_offset, _slice| Ok(()),
        );
        assert_eq!(result.unwrap_err().kind(), io::ErrorKind::InvalidData);
        Ok(())
    }

    #[test]
    fn small_file_pool_prefetches_once_within_limits() -> io::Result<()> {
        let mut pool = SmallFilePool::new(
            8,
            12,
            2 * 1024 * 1024,
            64 * 1024,
            16,
            null_mut(),
            ReaderIoPolicy::default(),
        )?;
        let mut files = Vec::new();
        let mut tasks = Vec::new();
        let mut expected = Vec::new();
        for index in 0..24u64 {
            let len = 1000 + index as usize * 317;
            let data = (0..len)
                .map(|offset| ((offset * 17 + index as usize) % 251) as u8)
                .collect::<Vec<_>>();
            let file = TempFile::create(&format!("small-pool-文件-{index}"), &data)?;
            let task = SmallFileTask {
                index,
                len: len as u64,
                path: file.0.to_str().unwrap().to_string(),
            };
            pool.enqueue(task.clone(), false);
            files.push(file);
            tasks.push(task);
            expected.push(data);
        }

        for index in 0..tasks.len() {
            let cached = pool.wait_take(tasks[index].clone())?;
            assert_eq!(cached.data, expected[index]);
            if index == 0 {
                pool.put_back(tasks[index].index, cached);
                fs::remove_file(&files[index].0)?;
                let cached_again = pool.wait_take(tasks[index].clone())?;
                assert_eq!(cached_again.data, expected[index]);
                pool.release(tasks[index].index, cached_again);
            } else {
                pool.release(tasks[index].index, cached);
            }
        }

        {
            let state = pool.shared.state.lock().unwrap();
            assert!(state.max_active_files <= 12);
            assert!(state.max_reserved_bytes <= 2 * 1024 * 1024);
        }
        pool.shutdown();
        Ok(())
    }

    #[test]
    fn small_file_queue_buckets_choose_oldest_fitting_and_priority_items() {
        fn add_pending(state: &mut SmallFileState, index: u64, len: u64, priority: bool) {
            let (class, _) = small_buffer_class(len).unwrap();
            state.entries.insert(
                index,
                SmallFileEntry {
                    task: SmallFileTask {
                        index,
                        len,
                        path: String::new(),
                    },
                    status: SmallFileStatus::Pending,
                    attempts: 0,
                    queue_generation: 0,
                    retry_buffer: None,
                },
            );
            state.enqueue_index(index, class, priority);
        }

        let mut state = SmallFileState::new();
        add_pending(&mut state, 0, 200_000, false); // 256 KiB class
        add_pending(&mut state, 1, 1_000_000, false); // 1 MiB class
        add_pending(&mut state, 2, 1_000, false); // 4 KiB class
        add_pending(&mut state, 3, 0, false); // zero-length class

        assert_eq!(
            state.take_next_pending(256 * 1024, 0),
            Some((0, 256 * 1024))
        );
        assert_eq!(state.take_next_pending(256 * 1024, 0), Some((2, 4 * 1024)));

        add_pending(&mut state, 4, 16_000, true);
        add_pending(&mut state, 6, 8_000, true);
        assert_eq!(
            state.take_next_pending(1024 * 1024, 0),
            Some((6, 16 * 1024))
        );
        assert_eq!(
            state.take_next_pending(1024 * 1024, 0),
            Some((4, 16 * 1024))
        );
        assert_eq!(
            state.take_next_pending(1024 * 1024, 0),
            Some((1, 1024 * 1024))
        );
        assert_eq!(state.take_next_pending(1024 * 1024, 0), Some((3, 0)));

        add_pending(&mut state, 5, 1_000, false);
        state.entries.remove(&5);
        add_pending(&mut state, 5, 1_000, false);
        assert_eq!(state.take_next_pending(4 * 1024, 0), Some((5, 4 * 1024)));
    }

    #[test]
    fn demanded_small_file_uses_budget_reserved_from_speculative_prefetch() {
        fn add_pending(state: &mut SmallFileState, index: u64, len: u64) {
            let (class, _) = small_buffer_class(len).unwrap();
            state.entries.insert(
                index,
                SmallFileEntry {
                    task: SmallFileTask {
                        index,
                        len,
                        path: String::new(),
                    },
                    status: SmallFileStatus::Pending,
                    attempts: 0,
                    queue_generation: 0,
                    retry_buffer: None,
                },
            );
            state.enqueue_index(index, class, false);
        }

        let mib = 1024 * 1024;
        let inflight_limit = 4 * mib;
        let demand_reserve = 2 * mib;
        let mut state = SmallFileState::new();
        add_pending(&mut state, 1, 600_000);
        add_pending(&mut state, 2, 600_000);
        add_pending(&mut state, 3, 600_000);
        add_pending(&mut state, 10, 1_500_000);

        for expected_index in [1, 2] {
            let (index, reserved) = state
                .take_next_pending(inflight_limit, demand_reserve)
                .unwrap();
            assert_eq!(index, expected_index);
            assert_eq!(reserved, mib);
            state.reserved_bytes += reserved;
            state.entries.get_mut(&index).unwrap().status = SmallFileStatus::Ready {
                data: Vec::new(),
                reserved,
            };
        }

        assert_eq!(
            state.take_next_pending(inflight_limit, demand_reserve),
            None
        );
        assert!(state.prioritize_pending(10));
        assert_eq!(
            state.take_next_pending(inflight_limit, demand_reserve),
            Some((10, 2 * mib))
        );
    }

    #[test]
    fn small_file_retry_remains_schedulable_at_the_inflight_limit() {
        let reserved = 64 * 1024;
        let (class, _) = small_buffer_class(reserved as u64).unwrap();
        let mut state = SmallFileState::new();
        state.entries.insert(
            7,
            SmallFileEntry {
                task: SmallFileTask {
                    index: 7,
                    len: reserved as u64,
                    path: String::new(),
                },
                status: SmallFileStatus::InFlight,
                attempts: 0,
                queue_generation: 0,
                retry_buffer: None,
            },
        );
        state.active_files = 1;
        state.reserved_bytes = reserved;
        let shared = SmallShared {
            state: Mutex::new(state),
            changed: Condvar::new(),
            operations: Mutex::new(FxHashMap::default()),
            worker_handles: Mutex::new(Vec::new()),
            worker_activities: Mutex::new(Vec::new()),
            completion_port: SharedHandle(null_mut()),
            active_limit: 1,
            inflight_byte_limit: reserved,
            demand_reserve: reserved,
            completion_batch: 1,
            cancel_event: SharedHandle(null_mut()),
            io_policy: ReaderIoPolicy::default(),
        };

        small_failure(
            &shared,
            7,
            vec![0u8; reserved],
            reserved,
            io::Error::new(io::ErrorKind::TimedOut, "injected retryable failure"),
        );

        let mut state = shared.state.lock().unwrap();
        assert_eq!(state.active_files, 0);
        assert_eq!(state.reserved_bytes, reserved);
        let entry = state.entries.get_mut(&7).unwrap();
        assert!(matches!(
            entry.status,
            SmallFileStatus::Failed {
                retryable: true,
                ..
            }
        ));
        assert_eq!(
            entry.retry_buffer.as_ref().map(|(_, reserved)| *reserved),
            Some(reserved)
        );
        entry.status = SmallFileStatus::Pending;
        state.enqueue_index(7, class, true);

        // The retry's reservation is already counted in reserved_bytes, so
        // checking reserved_bytes + reserved would strand it permanently.
        assert_eq!(
            state.take_next_pending(reserved, reserved),
            Some((7, reserved))
        );
        assert_eq!(state.reserved_bytes, reserved);
    }

    #[test]
    fn small_sync_watchdog_surfaces_a_call_stuck_after_cancellation() {
        let state = SmallFileState::new();
        let policy = ReaderIoPolicy {
            read_stall_timeout: Duration::from_millis(1),
            cancel_grace: Duration::from_millis(1),
            ..ReaderIoPolicy::default()
        };
        let shared = SmallShared {
            state: Mutex::new(state),
            changed: Condvar::new(),
            operations: Mutex::new(FxHashMap::default()),
            worker_handles: Mutex::new(Vec::new()),
            worker_activities: Mutex::new(vec![Some(SmallWorkerActivity {
                file_index: 19,
                stage: "CreateFileW",
                started_at: Instant::now() - Duration::from_secs(1),
                cancel_requested_at: Some(Instant::now() - Duration::from_secs(1)),
                cancel_error: Some(ERROR_NOT_FOUND as i32),
                timed_out: true,
            })]),
            completion_port: SharedHandle(null_mut()),
            active_limit: 1,
            inflight_byte_limit: 64 * 1024,
            demand_reserve: 64 * 1024,
            completion_batch: 1,
            cancel_event: SharedHandle(null_mut()),
            io_policy: policy,
        };

        assert!(!small_operation_watchdog(&shared));
        let state = shared.state.lock().unwrap();
        let error = state.fatal_error.as_deref().unwrap();
        assert!(error.contains("file=19"));
        assert!(error.contains("stage=CreateFileW"));
    }

    #[test]
    fn native_worker_progress_clears_a_pending_sync_cancellation() {
        let config = native_test_config(4096, 32 * 1024);
        unsafe {
            let context = create_native_test_context(&config);
            let shared = Arc::clone(&(*context).shared);
            {
                let mut watch = shared.worker_watch.lock().unwrap();
                watch.cancel_requested_at = Some(Instant::now());
                watch.cancel_error = Some(ERROR_NOT_FOUND as i32);
            }

            native_worker_progress(&shared, Some(23), "test progress");
            let watch = shared.worker_watch.lock().unwrap();
            assert_eq!(watch.file_index, Some(23));
            assert_eq!(watch.stage, "test progress");
            assert!(watch.cancel_requested_at.is_none());
            assert!(watch.cancel_error.is_none());
            drop(watch);
            lfr_destroy(context);
        }
    }

    #[test]
    fn refill_wait_only_accepts_a_stagnant_short_tail_after_all_input_is_read() {
        let config = native_test_config(4096, 32 * 1024);
        unsafe {
            let context = create_native_test_context(&config);
            let shared = Arc::clone(&(*context).shared);
            {
                let mut state = native_lock(&shared);
                state.started = true;
                state.selected_bytes = 8192;
                state.buffered_bytes = 4096;
                state.occupied_slots = 1;
            }

            assert_eq!(lfr_wait_until_buffered(context, 8192, 20), LFR_TIMEOUT);

            shared.telemetry.bytes_read.store(8192, Ordering::Release);
            assert_eq!(lfr_wait_until_buffered(context, 8192, 20), LFR_OK);
            lfr_destroy(context);
        }
    }

    #[test]
    fn refill_wait_restarts_its_no_change_window_when_slots_change() {
        let config = native_test_config(4096, 32 * 1024);
        unsafe {
            let context = create_native_test_context(&config);
            let shared = Arc::clone(&(*context).shared);
            {
                let mut state = native_lock(&shared);
                state.started = true;
                state.selected_bytes = 8192;
                state.buffered_bytes = 4096;
                state.occupied_slots = 1;
            }
            shared.telemetry.bytes_read.store(8192, Ordering::Release);

            let notifier = Arc::clone(&shared);
            let update = thread::spawn(move || {
                thread::sleep(Duration::from_millis(15));
                let mut state = native_lock(&notifier);
                // EOF changes slot occupancy without changing buffered bytes.
                state.occupied_slots = 2;
                notifier.changed.notify_all();
            });
            let started = Instant::now();
            assert_eq!(lfr_wait_until_buffered(context, 8192, 30), LFR_OK);
            assert!(started.elapsed() >= Duration::from_millis(35));
            update.join().unwrap();
            lfr_destroy(context);
        }
    }

    #[test]
    fn refill_wait_is_woken_by_error_and_cancellation() {
        let config = native_test_config(4096, 32 * 1024);
        unsafe {
            let error_context = create_native_test_context(&config);
            {
                let shared = &(*error_context).shared;
                let mut state = native_lock(shared);
                state.started = true;
                state.selected_bytes = 8192;
                state.error = "injected read failure".into();
            }
            assert_eq!(
                lfr_wait_until_buffered(error_context, 8192, 1000),
                LFR_ERROR
            );
            lfr_destroy(error_context);

            let cancel_context = create_native_test_context(&config);
            {
                let shared = &(*cancel_context).shared;
                let mut state = native_lock(shared);
                state.started = true;
                state.selected_bytes = 8192;
            }
            let context_address = cancel_context as usize;
            let waiter = thread::spawn(move || {
                lfr_wait_until_buffered(context_address as *mut LfrContext, 8192, 1000)
            });
            thread::sleep(Duration::from_millis(10));
            assert_eq!(lfr_cancel(cancel_context), LFR_OK);
            assert_eq!(waiter.join().unwrap(), LFR_CANCELLED);
            lfr_destroy(cancel_context);
        }
    }

    #[test]
    fn final_file_below_prefill_target_streams_all_bytes_and_hash_before_eof() -> io::Result<()> {
        let expected = (0..(3 * 4096 + 37))
            .map(|index| ((index * 19 + 11) % 251) as u8)
            .collect::<Vec<_>>();
        let file = TempFile::create("short-final-tail", &expected)?;
        let path = file.0.to_str().unwrap().encode_utf16().collect::<Vec<_>>();
        let config = native_test_config(4096, 64 * 1024);

        unsafe {
            let context = create_native_test_context(&config);
            assert_eq!(
                lfr_add_file(
                    context,
                    0,
                    path.as_ptr(),
                    path.len() as u32,
                    expected.len() as u64,
                ),
                LFR_OK
            );
            assert_eq!(lfr_select_file(context, 0), LFR_OK);
            assert_eq!(lfr_start(context), LFR_OK);
            assert_eq!(lfr_wait_until_buffered(context, 48 * 1024, 1000), LFR_OK);

            let mut actual = Vec::new();
            loop {
                let mut slot = LfrSlot {
                    token: 0,
                    file_index: -1,
                    file_offset: 0,
                    data: null(),
                    length: 0,
                    flags: 0,
                };
                assert_eq!(lfr_acquire_slot(context, 0, 1000, &mut slot), LFR_OK);
                assert_eq!(slot.file_offset, actual.len() as u64);
                if slot.flags & FLAG_EOF != 0 {
                    assert_eq!(slot.length, 0);
                    let mut hashes = [0u8; 128];
                    let mut written = 0;
                    assert_eq!(
                        lfr_get_file_hashes(
                            context,
                            0,
                            hashes.as_mut_ptr(),
                            hashes.len() as u32,
                            &mut written,
                        ),
                        LFR_OK
                    );
                    assert!(
                        std::str::from_utf8(&hashes[..written as usize])
                            .unwrap()
                            .starts_with("CRC32=")
                    );
                    assert_eq!(lfr_release_slot(context, slot.token), LFR_OK);
                    break;
                }
                actual
                    .extend_from_slice(std::slice::from_raw_parts(slot.data, slot.length as usize));
                assert_eq!(lfr_release_slot(context, slot.token), LFR_OK);
            }
            assert_eq!(actual, expected);
            lfr_destroy(context);
        }
        Ok(())
    }

    #[test]
    fn eof_is_published_after_data_fills_every_slot() -> io::Result<()> {
        const SLOT_SIZE: usize = 4096;
        const SLOT_COUNT: usize = 8;
        let expected = (0..(SLOT_SIZE * SLOT_COUNT))
            .map(|index| ((index * 23 + 3) % 251) as u8)
            .collect::<Vec<_>>();
        let file = TempFile::create("full-ring-before-eof", &expected)?;
        let path = file.0.to_str().unwrap().encode_utf16().collect::<Vec<_>>();
        let config = native_test_config(SLOT_SIZE as u32, expected.len() as u64);

        unsafe {
            let context = create_native_test_context(&config);
            assert_eq!(
                lfr_add_file(
                    context,
                    0,
                    path.as_ptr(),
                    path.len() as u32,
                    expected.len() as u64,
                ),
                LFR_OK
            );
            assert_eq!(lfr_select_file(context, 0), LFR_OK);
            assert_eq!(lfr_start(context), LFR_OK);
            assert_eq!(lfr_wait_until_buffered(context, u64::MAX, 1000), LFR_OK);
            assert_eq!(lfr_occupied_slots(context), SLOT_COUNT as u64);

            let mut actual = Vec::new();
            let mut eof_count = 0;
            loop {
                let mut slot = LfrSlot {
                    token: 0,
                    file_index: -1,
                    file_offset: 0,
                    data: null(),
                    length: 0,
                    flags: 0,
                };
                assert_eq!(lfr_acquire_slot(context, 0, 1000, &mut slot), LFR_OK);
                assert_eq!(slot.file_offset, actual.len() as u64);
                if slot.flags & FLAG_EOF != 0 {
                    eof_count += 1;
                    assert_eq!(slot.length, 0);
                    assert_eq!(lfr_release_slot(context, slot.token), LFR_OK);
                    break;
                }
                actual
                    .extend_from_slice(std::slice::from_raw_parts(slot.data, slot.length as usize));
                assert_eq!(lfr_release_slot(context, slot.token), LFR_OK);
            }
            assert_eq!(actual, expected);
            assert_eq!(eof_count, 1);
            lfr_destroy(context);
        }
        Ok(())
    }

    #[test]
    fn multiple_small_files_keep_data_and_eof_order_when_slots_wrap() -> io::Result<()> {
        let expected = (0..5u64)
            .map(|file_index| {
                (0..(700 + file_index as usize * 113))
                    .map(|offset| ((offset * 31 + file_index as usize * 7) % 251) as u8)
                    .collect::<Vec<_>>()
            })
            .collect::<Vec<_>>();
        let files = expected
            .iter()
            .enumerate()
            .map(|(index, data)| TempFile::create(&format!("wrapped-small-{index}"), data))
            .collect::<io::Result<Vec<_>>>()?;
        let paths = files
            .iter()
            .map(|file| file.0.to_str().unwrap().encode_utf16().collect::<Vec<_>>())
            .collect::<Vec<_>>();
        let config = native_test_config(4096, 4 * 4096);

        unsafe {
            let context = create_native_test_context(&config);
            for index in 0..expected.len() {
                assert_eq!(
                    lfr_add_file(
                        context,
                        index as i64,
                        paths[index].as_ptr(),
                        paths[index].len() as u32,
                        expected[index].len() as u64,
                    ),
                    LFR_OK
                );
                assert_eq!(lfr_select_file(context, index as i64), LFR_OK);
            }
            assert_eq!(lfr_start(context), LFR_OK);

            for (index, expected_file) in expected.iter().enumerate() {
                let mut actual = Vec::new();
                let mut eof_count = 0;
                loop {
                    let mut slot = LfrSlot {
                        token: 0,
                        file_index: -1,
                        file_offset: 0,
                        data: null(),
                        length: 0,
                        flags: 0,
                    };
                    assert_eq!(
                        lfr_acquire_slot(context, index as i64, 1000, &mut slot),
                        LFR_OK
                    );
                    assert_eq!(slot.file_offset, actual.len() as u64);
                    if slot.flags & FLAG_EOF != 0 {
                        eof_count += 1;
                        assert_eq!(slot.length, 0);
                        assert_eq!(lfr_release_slot(context, slot.token), LFR_OK);
                        break;
                    }
                    actual.extend_from_slice(std::slice::from_raw_parts(
                        slot.data,
                        slot.length as usize,
                    ));
                    assert_eq!(lfr_release_slot(context, slot.token), LFR_OK);
                }
                assert_eq!(&actual, expected_file);
                assert_eq!(eof_count, 1);
            }
            lfr_destroy(context);
        }
        Ok(())
    }

    #[test]
    fn native_abi_streams_ordered_files_from_stable_native_slots() -> io::Result<()> {
        let first_data = (0..(96 * 1024 + 37))
            .map(|index| ((index * 13 + 5) % 251) as u8)
            .collect::<Vec<_>>();
        let second_data = (0..(80 * 1024 + 11))
            .map(|index| ((index * 29 + 3) % 251) as u8)
            .collect::<Vec<_>>();
        let first = TempFile::create("native-abi-first-文件", &first_data)?;
        let second = TempFile::create("native-abi-second-文件", &second_data)?;
        let first_path = first.0.to_str().unwrap().encode_utf16().collect::<Vec<_>>();
        let second_path = second
            .0
            .to_str()
            .unwrap()
            .encode_utf16()
            .collect::<Vec<_>>();
        let config = LfrConfig {
            struct_size: std::mem::size_of::<LfrConfig>() as u32,
            abi_version: LFR_ABI_VERSION,
            slot_size: 4096,
            read_chunk_size: 16 * 1024,
            queue_depth: 16,
            capacity_bytes: 32 * 1024,
            small_open_concurrency: 4,
            small_active_files: 8,
            small_inflight_bytes: 2 * 1024 * 1024,
            small_threshold: 64 * 1024,
            hash_mask: LFR_HASH_CRC32,
            next_file_prime_depth: 8,
            read_stall_timeout_ms: DEFAULT_READ_STALL_TIMEOUT_MS,
            io_cancel_grace_ms: DEFAULT_IO_CANCEL_GRACE_MS,
            max_consecutive_file_retries: DEFAULT_MAX_CONSECUTIVE_FILE_RETRIES,
            file_retry_base_delay_ms: DEFAULT_FILE_RETRY_BASE_DELAY_MS,
        };

        unsafe {
            let mut context = null_mut();
            assert_eq!(lfr_create(&config, &mut context), LFR_OK);
            assert!(!context.is_null());
            assert_eq!(
                lfr_add_file(
                    context,
                    0,
                    first_path.as_ptr(),
                    first_path.len() as u32,
                    first_data.len() as u64,
                ),
                LFR_OK
            );
            assert_eq!(
                lfr_add_file(
                    context,
                    1,
                    second_path.as_ptr(),
                    second_path.len() as u32,
                    second_data.len() as u64,
                ),
                LFR_OK
            );
            assert_eq!(lfr_select_file(context, 0), LFR_OK);
            assert_eq!(lfr_select_file(context, 1), LFR_OK);
            assert_eq!(lfr_start(context), LFR_OK);

            for (index, expected) in [(0i64, &first_data), (1i64, &second_data)] {
                let mut actual = Vec::with_capacity(expected.len());
                loop {
                    let mut slot = LfrSlot {
                        token: 0,
                        file_index: -1,
                        file_offset: 0,
                        data: null(),
                        length: 0,
                        flags: 0,
                    };
                    assert_eq!(
                        lfr_acquire_slot(context, index, u32::MAX, &mut slot),
                        LFR_OK
                    );
                    assert_eq!(slot.file_index, index);
                    if slot.flags & FLAG_EOF != 0 {
                        assert_eq!(slot.file_offset, expected.len() as u64);
                        assert_eq!(slot.length, 0);
                        assert_eq!(lfr_release_slot(context, slot.token), LFR_OK);
                        break;
                    }
                    assert!(!slot.data.is_null());
                    assert_eq!(slot.file_offset, actual.len() as u64);
                    actual.extend_from_slice(std::slice::from_raw_parts(
                        slot.data,
                        slot.length as usize,
                    ));
                    assert_eq!(lfr_release_slot(context, slot.token), LFR_OK);
                }
                assert_eq!(&actual, expected);

                let mut hashes = [0u8; 256];
                let mut written = 0u32;
                assert_eq!(
                    lfr_get_file_hashes(
                        context,
                        index,
                        hashes.as_mut_ptr(),
                        hashes.len() as u32,
                        &mut written,
                    ),
                    LFR_OK
                );
                let hashes = std::str::from_utf8(&hashes[..written as usize]).unwrap();
                assert!(hashes.starts_with("CRC32="));
            }
            assert_eq!(lfr_wait_until_buffered(context, u64::MAX, 1000), LFR_OK);
            assert_eq!(lfr_is_done(context), 1);
            let mut stats = LfrStats {
                struct_size: std::mem::size_of::<LfrStats>() as u32,
                abi_version: 0,
                bytes_read: 0,
                bytes_published: 0,
                buffered_bytes: 0,
                occupied_slots: u64::MAX,
                read_wait_ns: 0,
                hash_ns: 0,
                publish_wait_ns: 0,
            };
            assert_eq!(lfr_get_stats(context, &mut stats), LFR_OK);
            assert_eq!(stats.abi_version, LFR_ABI_VERSION);
            assert_eq!(
                stats.bytes_read,
                (first_data.len() + second_data.len()) as u64
            );
            assert_eq!(
                stats.bytes_published,
                (first_data.len() + second_data.len()) as u64
            );
            assert_eq!(stats.buffered_bytes, 0);
            assert_eq!(stats.occupied_slots, 0);
            lfr_destroy(context);
        }
        Ok(())
    }
}
