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


