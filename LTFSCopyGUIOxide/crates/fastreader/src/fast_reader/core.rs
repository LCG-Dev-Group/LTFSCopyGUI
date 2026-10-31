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

// Cross-process tape bridge ABI.  This is intentionally independent from the
// disk-reader ABI above: the source is a synchronous SCSI producer and the
// target is the only consumer of a pagefile-backed shared-memory ring.
pub const LFR_BRIDGE_ABI_VERSION: u32 = 1;
pub const LFR_RETRYABLE: i32 = 4;
const BRIDGE_MAGIC: u64 = 0x3145_4744_4952_424C; // "LBRIDGE1"
const BRIDGE_ERROR_CAPACITY: usize = 1024;
const BRIDGE_HASH_CAPACITY: usize = 768;
const BRIDGE_WAIT_SLICE_MS: u32 = 100;
const BRIDGE_MAX_SLOT_COUNT: u64 = i32::MAX as u64;
const BRIDGE_MAX_MAPPING_BYTES: u64 = 64 * 1024 * 1024 * 1024;

#[repr(C)]
pub struct LfrBridgeConfig {
    pub struct_size: u32,
    pub abi_version: u32,
    pub slot_size: u32,
    pub reserved: u32,
    pub capacity_bytes: u64,
    pub hash_mask: u32,
    pub reserved2: u32,
}

#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct LfrTapeExtent {
    pub file_offset: u64,
    pub byte_count: u64,
    pub start_block: u64,
    pub byte_offset: u32,
    pub partition: u8,
    pub reserved: [u8; 3],
}

pub type LfrTapeRetryCallback = Option<
    unsafe extern "system" fn(
        user_data: *mut c_void,
        message: *const u8,
        message_len: u32,
        partition: u8,
        block: u64,
    ) -> i32,
>;

#[repr(C, align(64))]
struct BridgeHeader {
    magic: u64,
    abi_version: u32,
    header_size: u32,
    slot_size: u32,
    slot_count: u32,
    slot_stride: u32,
    hash_mask: u32,
    mapping_size: u64,
    write_index: AtomicU64,
    read_index: AtomicU64,
    buffered_bytes: AtomicU64,
    occupied_slots: AtomicU64,
    bytes_read: AtomicU64,
    bytes_published: AtomicU64,
    read_wait_ns: AtomicU64,
    hash_ns: AtomicU64,
    publish_wait_ns: AtomicU64,
    cancelled: AtomicU32,
    producer_done: AtomicU32,
    producer_attached: AtomicU32,
    consumer_attached: AtomicU32,
    error_len: AtomicU32,
    reserved: u32,
    error: [u8; BRIDGE_ERROR_CAPACITY],
}

#[repr(C, align(64))]
struct BridgeSlotHeader {
    token: u64,
    file_index: i64,
    file_offset: u64,
    length: u32,
    flags: u32,
    hash_len: u32,
    reserved: u32,
    hashes: [u8; BRIDGE_HASH_CAPACITY],
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum BridgeRole {
    Consumer,
    Producer,
}

pub struct LfrBridgeContext {
    role: BridgeRole,
    _mapping: Handle,
    empty: Handle,
    full: Handle,
    cancel_event: Handle,
    view: NonNull<u8>,
    _mapping_size: usize,
    current_token: Mutex<Option<u64>>,
    completed_hashes: Mutex<FxHashMap<i64, String>>,
    worker_thread: Mutex<Option<Handle>>,
    local_error: Mutex<String>,
}

unsafe impl Send for LfrBridgeContext {}
unsafe impl Sync for LfrBridgeContext {}

impl Drop for LfrBridgeContext {
    fn drop(&mut self) {
        unsafe {
            UnmapViewOfFile(MEMORY_MAPPED_VIEW_ADDRESS {
                Value: self.view.as_ptr().cast(),
            });
        }
    }
}

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

