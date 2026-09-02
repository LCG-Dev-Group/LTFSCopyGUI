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

