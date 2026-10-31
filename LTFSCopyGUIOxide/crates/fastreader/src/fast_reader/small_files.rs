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

