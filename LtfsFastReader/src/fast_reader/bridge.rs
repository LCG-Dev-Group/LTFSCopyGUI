fn bridge_align_up(value: usize, alignment: usize) -> Option<usize> {
    value
        .checked_add(alignment.checked_sub(1)?)
        .map(|value| value & !(alignment - 1))
}

fn bridge_layout(slot_size: usize, slot_count: usize) -> Option<(usize, usize)> {
    let header_size = bridge_align_up(std::mem::size_of::<BridgeHeader>(), 64)?;
    let slot_stride = bridge_align_up(
        std::mem::size_of::<BridgeSlotHeader>().checked_add(slot_size)?,
        64,
    )?;
    let mapping_size = header_size.checked_add(slot_stride.checked_mul(slot_count)?)?;
    Some((slot_stride, mapping_size))
}

fn bridge_name(base: &str, suffix: &str) -> Vec<u16> {
    wide(&format!("{base}.{suffix}"))
}

fn bridge_decode_name(name: *const u16, name_len: u32) -> Result<String, i32> {
    if name.is_null() || name_len == 0 || name_len > 512 {
        return Err(LFR_INVALID);
    }
    let slice = unsafe { std::slice::from_raw_parts(name, name_len as usize) };
    let value = String::from_utf16(slice).map_err(|_| LFR_INVALID)?;
    if value.contains('\0') || value.contains('\\') && !value.starts_with("Local\\") {
        return Err(LFR_INVALID);
    }
    Ok(value)
}

impl LfrBridgeContext {
    fn header(&self) -> &BridgeHeader {
        unsafe { &*self.view.as_ptr().cast::<BridgeHeader>() }
    }

    fn slot_header(&self, sequence: u64) -> &BridgeSlotHeader {
        let header = self.header();
        let slot_index = sequence % header.slot_count as u64;
        let offset =
            header.header_size as usize + header.slot_stride as usize * slot_index as usize;
        unsafe { &*self.view.as_ptr().add(offset).cast::<BridgeSlotHeader>() }
    }

    fn slot_header_ptr(&self, sequence: u64) -> *mut BridgeSlotHeader {
        let header = self.header();
        let slot_index = sequence % header.slot_count as u64;
        let offset =
            header.header_size as usize + header.slot_stride as usize * slot_index as usize;
        unsafe { self.view.as_ptr().add(offset).cast::<BridgeSlotHeader>() }
    }

    fn slot_data(&self, sequence: u64) -> *mut u8 {
        let slot = self.slot_header(sequence) as *const BridgeSlotHeader as *mut u8;
        unsafe { slot.add(std::mem::size_of::<BridgeSlotHeader>()) }
    }

    fn cancelled(&self) -> bool {
        self.header().cancelled.load(Ordering::Acquire) != 0
            || unsafe { WaitForSingleObject(self.cancel_event.0, 0) == WAIT_OBJECT_0 }
    }

    fn shared_error(&self) -> String {
        let header = self.header();
        let length = (header.error_len.load(Ordering::Acquire) as usize).min(BRIDGE_ERROR_CAPACITY);
        if length == 0 {
            return String::new();
        }
        String::from_utf8_lossy(&header.error[..length]).into_owned()
    }

    fn set_error(&self, error: impl std::fmt::Display) {
        let text = error.to_string();
        if let Ok(mut local) = self.local_error.lock() {
            *local = text.clone();
        }
        let header = self.header();
        if header.error_len.load(Ordering::Acquire) == 0 {
            let bytes = text.as_bytes();
            let length = bytes.len().min(BRIDGE_ERROR_CAPACITY);
            unsafe {
                std::ptr::copy_nonoverlapping(
                    bytes.as_ptr(),
                    header.error.as_ptr() as *mut u8,
                    length,
                );
            }
            header.error_len.store(length as u32, Ordering::Release);
        }
    }
}

fn bridge_context<'a>(context: *mut LfrBridgeContext) -> Result<&'a LfrBridgeContext, i32> {
    unsafe { context.as_ref().ok_or(LFR_INVALID) }
}

fn bridge_wait_semaphore(context: &LfrBridgeContext, semaphore: HANDLE, timeout_ms: u32) -> i32 {
    let started = Instant::now();
    loop {
        if context.cancelled() {
            return LFR_CANCELLED;
        }
        if !context.shared_error().is_empty() {
            return LFR_ERROR;
        }
        let remaining = if timeout_ms == u32::MAX {
            BRIDGE_WAIT_SLICE_MS
        } else {
            let elapsed = started.elapsed().as_millis().min(u32::MAX as u128) as u32;
            if elapsed >= timeout_ms {
                return LFR_TIMEOUT;
            }
            BRIDGE_WAIT_SLICE_MS.min(timeout_ms - elapsed)
        };
        match unsafe { WaitForSingleObject(semaphore, remaining) } {
            WAIT_OBJECT_0 => return LFR_OK,
            WAIT_TIMEOUT => continue,
            WAIT_FAILED => return LFR_ERROR,
            _ => return LFR_ERROR,
        }
    }
}

fn bridge_publish(
    context: &LfrBridgeContext,
    file_index: i64,
    file_offset: u64,
    data: &[u8],
    flags: u32,
    hashes: &str,
) -> io::Result<()> {
    if context.role != BridgeRole::Producer {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "bridge context is not a producer",
        ));
    }
    let header = context.header();
    if data.len() > header.slot_size as usize || hashes.len() > BRIDGE_HASH_CAPACITY {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "bridge slot payload is too large",
        ));
    }
    let wait_started = Instant::now();
    match bridge_wait_semaphore(context, context.empty.0, u32::MAX) {
        LFR_OK => {}
        LFR_CANCELLED => return Err(cancelled_error()),
        _ => return Err(io::Error::other(context.shared_error())),
    }
    header.publish_wait_ns.fetch_add(
        wait_started.elapsed().as_nanos().min(u64::MAX as u128) as u64,
        Ordering::Relaxed,
    );
    if context.cancelled() {
        unsafe { ReleaseSemaphore(context.empty.0, 1, null_mut()) };
        return Err(cancelled_error());
    }

    let sequence = header.write_index.load(Ordering::Relaxed);
    // The producer is single-writer by contract.  Keep this cross-process
    // mutation explicit instead of manufacturing a Rust mutable reference
    // from an immutable context shared with FFI callers.
    let slot = unsafe { &mut *context.slot_header_ptr(sequence) };
    slot.token = sequence.wrapping_add(1);
    slot.file_index = file_index;
    slot.file_offset = file_offset;
    slot.length = data.len() as u32;
    slot.flags = flags;
    slot.hash_len = hashes.len() as u32;
    slot.hashes.fill(0);
    if !hashes.is_empty() {
        slot.hashes[..hashes.len()].copy_from_slice(hashes.as_bytes());
    }
    if !data.is_empty() {
        unsafe {
            std::ptr::copy_nonoverlapping(data.as_ptr(), context.slot_data(sequence), data.len());
        }
    }
    std::sync::atomic::fence(Ordering::Release);
    header
        .write_index
        .store(sequence.wrapping_add(1), Ordering::Release);
    header
        .buffered_bytes
        .fetch_add(data.len() as u64, Ordering::AcqRel);
    header.occupied_slots.fetch_add(1, Ordering::AcqRel);
    header
        .bytes_published
        .fetch_add(data.len() as u64, Ordering::Relaxed);
    if unsafe { ReleaseSemaphore(context.full.0, 1, null_mut()) } == 0 {
        return Err(io::Error::last_os_error());
    }
    Ok(())
}

fn bridge_open_named_handles(
    base: &str,
    create: bool,
    slot_count: usize,
) -> io::Result<(Handle, Handle, Handle)> {
    let empty_name = bridge_name(base, "empty");
    let full_name = bridge_name(base, "full");
    let cancel_name = bridge_name(base, "cancel");
    unsafe {
        let empty = if create {
            CreateSemaphoreW(
                null(),
                slot_count as i32,
                slot_count as i32,
                empty_name.as_ptr(),
            )
        } else {
            OpenSemaphoreW(SEMAPHORE_ALL_ACCESS, 0, empty_name.as_ptr())
        };
        if empty.is_null() {
            return Err(io::Error::last_os_error());
        }
        let full = if create {
            CreateSemaphoreW(null(), 0, slot_count as i32, full_name.as_ptr())
        } else {
            OpenSemaphoreW(SEMAPHORE_ALL_ACCESS, 0, full_name.as_ptr())
        };
        if full.is_null() {
            CloseHandle(empty);
            return Err(io::Error::last_os_error());
        }
        let cancel = if create {
            CreateEventW(null(), 1, 0, cancel_name.as_ptr())
        } else {
            OpenEventW(EVENT_ALL_ACCESS, 0, cancel_name.as_ptr())
        };
        if cancel.is_null() {
            CloseHandle(empty);
            CloseHandle(full);
            return Err(io::Error::last_os_error());
        }
        Ok((Handle(empty), Handle(full), Handle(cancel)))
    }
}

fn bridge_validate_config(config: &LfrBridgeConfig) -> Result<(usize, usize, usize), i32> {
    if config.struct_size as usize != std::mem::size_of::<LfrBridgeConfig>()
        || config.abi_version != LFR_BRIDGE_ABI_VERSION
        || config.slot_size == 0
        || config.capacity_bytes < config.slot_size as u64 * 2
        || !config
            .capacity_bytes
            .is_multiple_of(config.slot_size as u64)
        || config.hash_mask & !LFR_HASH_ALL != 0
    {
        return Err(LFR_INVALID);
    }
    let slot_count = (config.capacity_bytes / config.slot_size as u64) as usize;
    if !(2..=BRIDGE_MAX_SLOT_COUNT).contains(&slot_count) {
        return Err(LFR_INVALID);
    }
    let (slot_stride, mapping_size) =
        bridge_layout(config.slot_size as usize, slot_count).ok_or(LFR_INVALID)?;
    if mapping_size as u64 > BRIDGE_MAX_MAPPING_BYTES {
        return Err(LFR_INVALID);
    }
    Ok((slot_count, slot_stride, mapping_size))
}

#[unsafe(no_mangle)]
pub extern "system" fn lfr_bridge_abi_version() -> u32 {
    LFR_BRIDGE_ABI_VERSION
}

#[unsafe(no_mangle)]
/// Creates the consumer side of a named pagefile-backed tape bridge.
///
/// # Safety
/// `name` must reference `name_len` valid UTF-16 code units, `config` must
/// reference a valid `LfrBridgeConfig`, and `output` must be writable.
pub unsafe extern "system" fn lfr_bridge_create_consumer(
    name: *const u16,
    name_len: u32,
    config: *const LfrBridgeConfig,
    output: *mut *mut LfrBridgeContext,
) -> i32 {
    if config.is_null() || output.is_null() {
        return LFR_INVALID;
    }
    unsafe { *output = null_mut() };
    let name = match bridge_decode_name(name, name_len) {
        Ok(value) => value,
        Err(code) => return code,
    };
    let config = unsafe { &*config };
    let (slot_count, slot_stride, mapping_size) = match bridge_validate_config(config) {
        Ok(value) => value,
        Err(code) => return code,
    };
    let mapping_name = bridge_name(&name, "mapping");
    let mapping = unsafe {
        CreateFileMappingW(
            INVALID_HANDLE_VALUE,
            null(),
            PAGE_READWRITE,
            ((mapping_size as u64) >> 32) as u32,
            mapping_size as u32,
            mapping_name.as_ptr(),
        )
    };
    if mapping.is_null() {
        return LFR_ERROR;
    }
    let mapping = Handle(mapping);
    let view = unsafe { MapViewOfFile(mapping.0, FILE_MAP_ALL_ACCESS, 0, 0, mapping_size).Value };
    let Some(view) = NonNull::new(view.cast::<u8>()) else {
        return LFR_ERROR;
    };
    let handles = match bridge_open_named_handles(&name, true, slot_count) {
        Ok(value) => value,
        Err(_) => {
            unsafe {
                UnmapViewOfFile(MEMORY_MAPPED_VIEW_ADDRESS {
                    Value: view.as_ptr().cast(),
                })
            };
            return LFR_ERROR;
        }
    };
    unsafe {
        std::ptr::write_bytes(view.as_ptr(), 0, mapping_size);
        std::ptr::write(
            view.as_ptr().cast::<BridgeHeader>(),
            BridgeHeader {
                magic: BRIDGE_MAGIC,
                abi_version: LFR_BRIDGE_ABI_VERSION,
                header_size: bridge_align_up(std::mem::size_of::<BridgeHeader>(), 64).unwrap()
                    as u32,
                slot_size: config.slot_size,
                slot_count: slot_count as u32,
                slot_stride: slot_stride as u32,
                hash_mask: config.hash_mask,
                mapping_size: mapping_size as u64,
                write_index: AtomicU64::new(0),
                read_index: AtomicU64::new(0),
                buffered_bytes: AtomicU64::new(0),
                occupied_slots: AtomicU64::new(0),
                bytes_read: AtomicU64::new(0),
                bytes_published: AtomicU64::new(0),
                read_wait_ns: AtomicU64::new(0),
                hash_ns: AtomicU64::new(0),
                publish_wait_ns: AtomicU64::new(0),
                cancelled: AtomicU32::new(0),
                producer_done: AtomicU32::new(0),
                producer_attached: AtomicU32::new(0),
                consumer_attached: AtomicU32::new(1),
                error_len: AtomicU32::new(0),
                reserved: 0,
                error: [0; BRIDGE_ERROR_CAPACITY],
            },
        );
    }
    let context = Box::new(LfrBridgeContext {
        role: BridgeRole::Consumer,
        _mapping: mapping,
        empty: handles.0,
        full: handles.1,
        cancel_event: handles.2,
        view,
        _mapping_size: mapping_size,
        current_token: Mutex::new(None),
        completed_hashes: Mutex::new(FxHashMap::default()),
        worker_thread: Mutex::new(None),
        local_error: Mutex::new(String::new()),
    });
    unsafe { *output = Box::into_raw(context) };
    LFR_OK
}

#[unsafe(no_mangle)]
/// Opens the producer side of an existing named tape bridge.
///
/// # Safety
/// `name` must reference `name_len` valid UTF-16 code units and `output` must
/// be writable.
pub unsafe extern "system" fn lfr_bridge_open_producer(
    name: *const u16,
    name_len: u32,
    output: *mut *mut LfrBridgeContext,
) -> i32 {
    if output.is_null() {
        return LFR_INVALID;
    }
    unsafe { *output = null_mut() };
    let name = match bridge_decode_name(name, name_len) {
        Ok(value) => value,
        Err(code) => return code,
    };
    let mapping_name = bridge_name(&name, "mapping");
    let mapping = unsafe { OpenFileMappingW(FILE_MAP_ALL_ACCESS, 0, mapping_name.as_ptr()) };
    if mapping.is_null() {
        return LFR_ERROR;
    }
    let mapping = Handle(mapping);
    let view = unsafe { MapViewOfFile(mapping.0, FILE_MAP_ALL_ACCESS, 0, 0, 0).Value };
    let Some(view) = NonNull::new(view.cast::<u8>()) else {
        return LFR_ERROR;
    };
    let header = unsafe { &*view.as_ptr().cast::<BridgeHeader>() };
    if header.magic != BRIDGE_MAGIC
        || header.abi_version != LFR_BRIDGE_ABI_VERSION
        || header.header_size as usize
            != bridge_align_up(std::mem::size_of::<BridgeHeader>(), 64).unwrap()
        || header.slot_count < 2
        || header.slot_count as usize > BRIDGE_MAX_SLOT_COUNT
    {
        unsafe {
            UnmapViewOfFile(MEMORY_MAPPED_VIEW_ADDRESS {
                Value: view.as_ptr().cast(),
            })
        };
        return LFR_INVALID;
    }
    let expected = bridge_layout(header.slot_size as usize, header.slot_count as usize);
    if expected != Some((header.slot_stride as usize, header.mapping_size as usize)) {
        unsafe {
            UnmapViewOfFile(MEMORY_MAPPED_VIEW_ADDRESS {
                Value: view.as_ptr().cast(),
            })
        };
        return LFR_INVALID;
    }
    if header
        .producer_attached
        .compare_exchange(0, 1, Ordering::AcqRel, Ordering::Acquire)
        .is_err()
    {
        unsafe {
            UnmapViewOfFile(MEMORY_MAPPED_VIEW_ADDRESS {
                Value: view.as_ptr().cast(),
            })
        };
        return LFR_INVALID;
    }
    let handles = match bridge_open_named_handles(&name, false, header.slot_count as usize) {
        Ok(value) => value,
        Err(_) => {
            header.producer_attached.store(0, Ordering::Release);
            unsafe {
                UnmapViewOfFile(MEMORY_MAPPED_VIEW_ADDRESS {
                    Value: view.as_ptr().cast(),
                })
            };
            return LFR_ERROR;
        }
    };
    let context = Box::new(LfrBridgeContext {
        role: BridgeRole::Producer,
        _mapping: mapping,
        empty: handles.0,
        full: handles.1,
        cancel_event: handles.2,
        view,
        _mapping_size: header.mapping_size as usize,
        current_token: Mutex::new(None),
        completed_hashes: Mutex::new(FxHashMap::default()),
        worker_thread: Mutex::new(None),
        local_error: Mutex::new(String::new()),
    });
    unsafe { *output = Box::into_raw(context) };
    LFR_OK
}

#[unsafe(no_mangle)]
/// Returns the number of payload bytes currently buffered in the bridge.
///
/// # Safety
/// `context` must be null or a live bridge context returned by this module.
pub unsafe extern "system" fn lfr_bridge_buffered_bytes(context: *mut LfrBridgeContext) -> u64 {
    bridge_context(context)
        .map(|context| context.header().buffered_bytes.load(Ordering::Acquire))
        .unwrap_or(0)
}

#[unsafe(no_mangle)]
/// Returns the payload capacity of the bridge.
///
/// # Safety
/// `context` must be null or a live bridge context returned by this module.
pub unsafe extern "system" fn lfr_bridge_buffer_capacity(context: *mut LfrBridgeContext) -> u64 {
    bridge_context(context)
        .map(|context| context.header().slot_size as u64 * context.header().slot_count as u64)
        .unwrap_or(0)
}

#[unsafe(no_mangle)]
/// Returns the number of occupied ring slots.
///
/// # Safety
/// `context` must be null or a live bridge context returned by this module.
pub unsafe extern "system" fn lfr_bridge_occupied_slots(context: *mut LfrBridgeContext) -> u64 {
    bridge_context(context)
        .map(|context| context.header().occupied_slots.load(Ordering::Acquire))
        .unwrap_or(0)
}

#[unsafe(no_mangle)]
/// Copies bridge performance counters to `output`.
///
/// # Safety
/// `context` must be a live bridge context and `output` must reference a
/// writable `LfrStats` whose size field is initialized correctly.
pub unsafe extern "system" fn lfr_bridge_get_stats(
    context: *mut LfrBridgeContext,
    output: *mut LfrStats,
) -> i32 {
    let context = match bridge_context(context) {
        Ok(value) => value,
        Err(code) => return code,
    };
    if output.is_null()
        || unsafe { (*output).struct_size as usize != std::mem::size_of::<LfrStats>() }
    {
        return LFR_INVALID;
    }
    let header = context.header();
    unsafe {
        *output = LfrStats {
            struct_size: std::mem::size_of::<LfrStats>() as u32,
            abi_version: LFR_ABI_VERSION,
            bytes_read: header.bytes_read.load(Ordering::Relaxed),
            bytes_published: header.bytes_published.load(Ordering::Relaxed),
            buffered_bytes: header.buffered_bytes.load(Ordering::Acquire),
            occupied_slots: header.occupied_slots.load(Ordering::Acquire),
            read_wait_ns: header.read_wait_ns.load(Ordering::Relaxed),
            hash_ns: header.hash_ns.load(Ordering::Relaxed),
            publish_wait_ns: header.publish_wait_ns.load(Ordering::Relaxed),
        };
    }
    LFR_OK
}

#[unsafe(no_mangle)]
/// Waits until the requested amount of payload is buffered or the producer
/// reaches a terminal state.
///
/// # Safety
/// `context` must be a live bridge context returned by this module.
pub unsafe extern "system" fn lfr_bridge_wait_until_buffered(
    context: *mut LfrBridgeContext,
    target: u64,
    timeout_ms: u32,
) -> i32 {
    let context = match bridge_context(context) {
        Ok(value) => value,
        Err(code) => return code,
    };
    let started = Instant::now();
    loop {
        let header = context.header();
        if context.cancelled() {
            return LFR_CANCELLED;
        }
        if !context.shared_error().is_empty() {
            return LFR_ERROR;
        }
        let occupied_slots = header.occupied_slots.load(Ordering::Acquire);
        if header.buffered_bytes.load(Ordering::Acquire) >= target
            // A ring full of partial data and EOF markers may contain fewer
            // payload bytes than the high watermark. The consumer must drain
            // it before the producer can make further progress.
            || occupied_slots >= header.slot_count as u64
            || (header.producer_done.load(Ordering::Acquire) != 0 && occupied_slots > 0)
        {
            return LFR_OK;
        }
        if header.producer_done.load(Ordering::Acquire) != 0 {
            return LFR_DONE;
        }
        if timeout_ms != u32::MAX && started.elapsed() >= Duration::from_millis(timeout_ms as u64) {
            return LFR_TIMEOUT;
        }
        let wait_ms = if timeout_ms == u32::MAX {
            BRIDGE_WAIT_SLICE_MS
        } else {
            BRIDGE_WAIT_SLICE_MS.min(
                timeout_ms
                    .saturating_sub(started.elapsed().as_millis().min(u32::MAX as u128) as u32),
            )
        };
        match unsafe { WaitForSingleObject(context.cancel_event.0, wait_ms) } {
            WAIT_OBJECT_0 => return LFR_CANCELLED,
            WAIT_TIMEOUT => {}
            _ => return LFR_ERROR,
        }
    }
}

#[unsafe(no_mangle)]
/// Acquires the next consumer slot in file/offset order.
///
/// # Safety
/// `context` must be a live consumer context and `output` must be writable.
pub unsafe extern "system" fn lfr_bridge_acquire_slot(
    context: *mut LfrBridgeContext,
    expected_file_index: i64,
    timeout_ms: u32,
    output: *mut LfrSlot,
) -> i32 {
    let context = match bridge_context(context) {
        Ok(value) => value,
        Err(code) => return code,
    };
    if context.role != BridgeRole::Consumer || output.is_null() {
        return LFR_INVALID;
    }
    let mut current = match context.current_token.lock() {
        Ok(value) => value,
        Err(_) => return LFR_ERROR,
    };
    if current.is_some() {
        return LFR_INVALID;
    }
    let wait_started = Instant::now();
    let wait_result = bridge_wait_semaphore(context, context.full.0, timeout_ms);
    context.header().read_wait_ns.fetch_add(
        wait_started.elapsed().as_nanos().min(u64::MAX as u128) as u64,
        Ordering::Relaxed,
    );
    if wait_result != LFR_OK {
        if wait_result == LFR_TIMEOUT
            && context.header().producer_done.load(Ordering::Acquire) != 0
            && context.header().occupied_slots.load(Ordering::Acquire) == 0
        {
            return LFR_DONE;
        }
        return wait_result;
    }
    std::sync::atomic::fence(Ordering::Acquire);
    let sequence = context.header().read_index.load(Ordering::Relaxed);
    let slot = context.slot_header(sequence);
    if slot.token != sequence.wrapping_add(1) || slot.file_index != expected_file_index {
        context.set_error(format!(
            "bridge slot order mismatch: expected file={expected_file_index}, actual file={} token={}",
            slot.file_index, slot.token
        ));
        return LFR_ERROR;
    }
    if slot.length > context.header().slot_size || slot.hash_len as usize > BRIDGE_HASH_CAPACITY {
        context.set_error("bridge slot metadata is invalid");
        return LFR_ERROR;
    }
    if slot.flags & FLAG_EOF != 0 {
        let hash_len = slot.hash_len as usize;
        let hashes = String::from_utf8_lossy(&slot.hashes[..hash_len]).into_owned();
        if let Ok(mut completed) = context.completed_hashes.lock() {
            completed.insert(slot.file_index, hashes);
        }
    }
    unsafe {
        *output = LfrSlot {
            token: slot.token,
            file_index: slot.file_index,
            file_offset: slot.file_offset,
            data: context.slot_data(sequence),
            length: slot.length,
            flags: slot.flags,
        };
    }
    *current = Some(slot.token);
    LFR_OK
}

#[unsafe(no_mangle)]
/// Releases the slot previously returned by `lfr_bridge_acquire_slot`.
///
/// # Safety
/// `context` must be a live consumer context and `token` must belong to its
/// currently acquired slot.
pub unsafe extern "system" fn lfr_bridge_release_slot(
    context: *mut LfrBridgeContext,
    token: u64,
) -> i32 {
    let context = match bridge_context(context) {
        Ok(value) => value,
        Err(code) => return code,
    };
    let mut current = match context.current_token.lock() {
        Ok(value) => value,
        Err(_) => return LFR_ERROR,
    };
    if *current != Some(token) {
        return LFR_INVALID;
    }
    let sequence = context.header().read_index.load(Ordering::Relaxed);
    let slot = context.slot_header(sequence);
    if slot.token != token {
        return LFR_INVALID;
    }
    let length = slot.length as u64;
    context
        .header()
        .buffered_bytes
        .fetch_sub(length, Ordering::AcqRel);
    context
        .header()
        .occupied_slots
        .fetch_sub(1, Ordering::AcqRel);
    context
        .header()
        .read_index
        .store(sequence.wrapping_add(1), Ordering::Release);
    *current = None;
    if unsafe { ReleaseSemaphore(context.empty.0, 1, null_mut()) } == 0 {
        return LFR_ERROR;
    }
    LFR_OK
}

#[unsafe(no_mangle)]
/// Copies the completed hash string for a file to the caller's buffer.
///
/// # Safety
/// `context` must be a live bridge context; `buffer` and `written` must be
/// valid for the supplied capacity.
pub unsafe extern "system" fn lfr_bridge_get_file_hashes(
    context: *mut LfrBridgeContext,
    file_index: i64,
    buffer: *mut u8,
    capacity: u32,
    written: *mut u32,
) -> i32 {
    let context = match bridge_context(context) {
        Ok(value) => value,
        Err(code) => return code,
    };
    let completed = match context.completed_hashes.lock() {
        Ok(value) => value,
        Err(_) => return LFR_ERROR,
    };
    match completed.get(&file_index) {
        Some(value) => native_copy_text(value, buffer, capacity, written),
        None => LFR_TIMEOUT,
    }
}

#[unsafe(no_mangle)]
/// Copies the bridge's terminal error, if any, to the caller's buffer.
///
/// # Safety
/// `context` must be a live bridge context; `buffer` and `written` must be
/// valid for the supplied capacity.
pub unsafe extern "system" fn lfr_bridge_last_error(
    context: *mut LfrBridgeContext,
    buffer: *mut u8,
    capacity: u32,
    written: *mut u32,
) -> i32 {
    let context = match bridge_context(context) {
        Ok(value) => value,
        Err(code) => return code,
    };
    let local = context
        .local_error
        .lock()
        .map(|value| value.clone())
        .unwrap_or_default();
    let text = if local.is_empty() {
        context.shared_error()
    } else {
        local
    };
    native_copy_text(&text, buffer, capacity, written)
}

#[unsafe(no_mangle)]
/// Cancels the bridge and any synchronous SCSI operation owned by its producer.
///
/// # Safety
/// `context` must be a live bridge context returned by this module.
pub unsafe extern "system" fn lfr_bridge_cancel(context: *mut LfrBridgeContext) -> i32 {
    let context = match bridge_context(context) {
        Ok(value) => value,
        Err(code) => return code,
    };
    context.header().cancelled.store(1, Ordering::Release);
    unsafe { SetEvent(context.cancel_event.0) };
    if let Ok(worker) = context.worker_thread.lock()
        && let Some(worker) = worker.as_ref()
    {
        unsafe { CancelSynchronousIo(worker.0) };
    }
    LFR_OK
}

#[unsafe(no_mangle)]
/// Marks a producer as complete after all data and EOF slots are published.
///
/// # Safety
/// `context` must be a live producer context returned by this module.
pub unsafe extern "system" fn lfr_bridge_producer_complete(context: *mut LfrBridgeContext) -> i32 {
    let context = match bridge_context(context) {
        Ok(value) => value,
        Err(code) => return code,
    };
    if context.role != BridgeRole::Producer {
        return LFR_INVALID;
    }
    context.header().producer_done.store(1, Ordering::Release);
    LFR_OK
}

#[repr(C)]
struct ScsiRequest {
    pass: SCSI_PASS_THROUGH_DIRECT,
    sense: [u8; 64],
}

fn scsi_command(
    handle: HANDLE,
    cdb: [u8; 16],
    cdb_length: u8,
    data_in: u8,
    data: *mut u8,
    data_length: u32,
    timeout_seconds: u32,
) -> io::Result<ScsiRequest> {
    let mut request = ScsiRequest {
        pass: SCSI_PASS_THROUGH_DIRECT {
            Length: std::mem::size_of::<SCSI_PASS_THROUGH_DIRECT>() as u16,
            ScsiStatus: 0,
            PathId: 0,
            TargetId: 0,
            Lun: 0,
            CdbLength: cdb_length,
            SenseInfoLength: 64,
            DataIn: data_in,
            DataTransferLength: data_length,
            TimeOutValue: timeout_seconds,
            DataBuffer: data.cast(),
            SenseInfoOffset: std::mem::offset_of!(ScsiRequest, sense) as u32,
            Cdb: cdb,
        },
        sense: [0; 64],
    };
    let mut returned = 0u32;
    let ok = unsafe {
        DeviceIoControl(
            handle,
            IOCTL_SCSI_PASS_THROUGH_DIRECT,
            (&mut request as *mut ScsiRequest).cast(),
            std::mem::size_of::<ScsiRequest>() as u32,
            (&mut request as *mut ScsiRequest).cast(),
            std::mem::size_of::<ScsiRequest>() as u32,
            &mut returned,
            null_mut(),
        )
    };
    if ok == 0 {
        return Err(io::Error::last_os_error());
    }
    Ok(request)
}

fn sense_description(request: &ScsiRequest) -> String {
    let key = request.sense[2] & 0x0f;
    let asc = request.sense[12];
    let ascq = request.sense[13];
    format!(
        "SCSI status=0x{:02X}, sense key=0x{key:02X}, ASC/ASCQ={asc:02X}/{ascq:02X}",
        request.pass.ScsiStatus
    )
}

fn scsi_locate(handle: HANDLE, partition: u8, block: u64, timeout_seconds: u32) -> io::Result<()> {
    let mut cdb = [0u8; 16];
    cdb[0] = 0x92;
    cdb[1] = 0x02; // CP=1, logical-object identifier
    cdb[3] = partition;
    cdb[4..12].copy_from_slice(&block.to_be_bytes());
    let request = scsi_command(
        handle,
        cdb,
        16,
        SCSI_IOCTL_DATA_UNSPECIFIED as u8,
        null_mut(),
        0,
        timeout_seconds,
    )?;
    let key = request.sense[2] & 0x0f;
    if request.pass.ScsiStatus != 0 || key != 0 {
        return Err(io::Error::other(format!(
            "LOCATE partition={partition} block={block} failed: {}",
            sense_description(&request)
        )));
    }
    Ok(())
}

fn scsi_read_block(
    handle: HANDLE,
    allocation_length: u32,
    timeout_seconds: u32,
    buffer: &mut [u8],
) -> io::Result<usize> {
    if allocation_length == 0
        || allocation_length > 0x00FF_FFFF
        || allocation_length as usize > buffer.len()
    {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "invalid tape READ length",
        ));
    }
    let mut cdb = [0u8; 16];
    cdb[0] = 0x08;
    cdb[2] = (allocation_length >> 16) as u8;
    cdb[3] = (allocation_length >> 8) as u8;
    cdb[4] = allocation_length as u8;
    let request = scsi_command(
        handle,
        cdb,
        6,
        SCSI_IOCTL_DATA_IN as u8,
        buffer.as_mut_ptr(),
        allocation_length,
        timeout_seconds,
    )?;
    let key = request.sense[2] & 0x0f;
    let filemark = request.sense[2] & 0x80 != 0;
    let eom = request.sense[2] & 0x40 != 0;
    let ili = request.sense[2] & 0x20 != 0;
    if request.pass.ScsiStatus != 0 || filemark || eom || (key != 0 && key != 1) {
        return Err(io::Error::other(format!(
            "READ failed: {}",
            sense_description(&request)
        )));
    }
    let residual = i32::from_be_bytes([
        request.sense[3],
        request.sense[4],
        request.sense[5],
        request.sense[6],
    ]);
    let actual = if ili && residual > 0 {
        allocation_length.saturating_sub(residual as u32)
    } else {
        allocation_length
    };
    if actual == 0 {
        return Err(io::Error::new(
            io::ErrorKind::UnexpectedEof,
            "tape READ returned an empty block",
        ));
    }
    Ok(actual as usize)
}

fn bridge_wait_retry(context: &LfrBridgeContext, delay_seconds: u32) -> io::Result<()> {
    match unsafe { WaitForSingleObject(context.cancel_event.0, delay_seconds.saturating_mul(1000)) }
    {
        WAIT_OBJECT_0 => Err(cancelled_error()),
        WAIT_TIMEOUT => Ok(()),
        _ => Err(io::Error::last_os_error()),
    }
}

#[allow(clippy::too_many_arguments)]
fn bridge_read_block_with_retry(
    context: &LfrBridgeContext,
    handle: HANDLE,
    partition: u8,
    block: u64,
    allocation_length: u32,
    timeout_seconds: u32,
    automatic_retries: u32,
    retry_callback: LfrTapeRetryCallback,
    user_data: *mut c_void,
    locate_before_read: bool,
    buffer: &mut [u8],
) -> io::Result<usize> {
    let retry_delays = [1u32, 2, 4];
    let mut need_locate = locate_before_read;
    loop {
        let mut last_error = None;
        for attempt in 0..=automatic_retries {
            if context.cancelled() {
                return Err(cancelled_error());
            }
            let result = if need_locate || attempt > 0 {
                scsi_locate(handle, partition, block, timeout_seconds).and_then(|_| {
                    scsi_read_block(handle, allocation_length, timeout_seconds, buffer)
                })
            } else {
                scsi_read_block(handle, allocation_length, timeout_seconds, buffer)
            };
            match result {
                Ok(length) => return Ok(length),
                Err(error) => last_error = Some(error),
            }
            need_locate = true;
            if attempt < automatic_retries {
                let delay = retry_delays.get(attempt as usize).copied().unwrap_or(4);
                bridge_wait_retry(context, delay)?;
            }
        }
        let error = last_error.unwrap_or_else(|| io::Error::other("unknown tape READ failure"));
        let message = format!(
            "source tape read failed after {} retries at partition={} block={}: {error}",
            automatic_retries, partition, block
        );
        let retry = retry_callback.map_or(0, |callback| unsafe {
            callback(
                user_data,
                message.as_ptr(),
                message.len() as u32,
                partition,
                block,
            )
        });
        if retry == 1 {
            need_locate = true;
            continue;
        }
        return Err(io::Error::other(message));
    }
}

#[unsafe(no_mangle)]
/// Reads one LTFS file from a physical tape using synchronous SCSI calls and
/// publishes its logical bytes to the bridge in order.
///
/// # Safety
/// `context` must be a live producer context; `tape_handle` must be a valid
/// tape handle; `extents` must reference `extent_count` initialized extents;
/// and the callback/user-data pair must remain valid for the duration of the
/// call.
pub unsafe extern "system" fn lfr_bridge_stream_tape_file(
    context: *mut LfrBridgeContext,
    tape_handle: HANDLE,
    file_index: i64,
    file_length: u64,
    source_block_size: u32,
    extents: *const LfrTapeExtent,
    extent_count: u32,
    timeout_seconds: u32,
    automatic_retries: u32,
    retry_callback: LfrTapeRetryCallback,
    user_data: *mut c_void,
) -> i32 {
    let context = match bridge_context(context) {
        Ok(value) => value,
        Err(code) => return code,
    };
    if context.role != BridgeRole::Producer
        || tape_handle.is_null()
        || tape_handle == INVALID_HANDLE_VALUE
        || file_index < 0
        || source_block_size == 0
        || source_block_size > 0x00FF_FFFF
        || timeout_seconds == 0
        || automatic_retries > 10
        || (extent_count > 0 && extents.is_null())
    {
        return LFR_INVALID;
    }
    let mut values = if extent_count == 0 {
        Vec::new()
    } else {
        unsafe { std::slice::from_raw_parts(extents, extent_count as usize) }.to_vec()
    };
    values.sort_by_key(|extent| extent.file_offset);
    let mut expected_offset = 0u64;
    for extent in &values {
        if extent.file_offset != expected_offset
            || extent.byte_count == 0
            || extent.byte_offset as u64 >= source_block_size as u64
        {
            context.set_error("source LTFS extents do not provide contiguous logical coverage");
            return LFR_INVALID;
        }
        expected_offset = match expected_offset.checked_add(extent.byte_count) {
            Some(value) => value,
            None => return LFR_INVALID,
        };
    }
    if expected_offset != file_length || (file_length > 0 && values.is_empty()) {
        context.set_error(format!(
            "source LTFS extent length mismatch: extents={expected_offset}, file={file_length}"
        ));
        return LFR_INVALID;
    }

    let thread_handle = unsafe { OpenThread(THREAD_TERMINATE, 0, GetCurrentThreadId()) };
    if thread_handle.is_null() {
        context.set_error(io::Error::last_os_error());
        return LFR_ERROR;
    }
    if let Ok(mut worker) = context.worker_thread.lock() {
        *worker = Some(Handle(thread_handle));
    } else {
        unsafe { CloseHandle(thread_handle) };
        return LFR_ERROR;
    }

    let result = (|| -> io::Result<()> {
        let enabled = native_hash_options(context.header().hash_mask);
        let mut hashes = HashSet::new(&enabled)?;
        let target_slot_size = context.header().slot_size as usize;
        let mut source_buffer = vec![0u8; source_block_size as usize];
        let mut target_buffer = Vec::with_capacity(target_slot_size);
        let mut logical_offset = 0u64;

        for extent in &values {
            let mut remaining = extent.byte_count;
            let mut byte_offset = extent.byte_offset as usize;
            let mut physical_block = extent.start_block;
            let mut locate_before_read = true;
            while remaining > 0 {
                let required = remaining
                    .saturating_add(byte_offset as u64)
                    .min(source_block_size as u64) as u32;
                let read_length = bridge_read_block_with_retry(
                    context,
                    tape_handle,
                    extent.partition,
                    physical_block,
                    required,
                    timeout_seconds,
                    automatic_retries,
                    retry_callback,
                    user_data,
                    locate_before_read,
                    &mut source_buffer,
                )?;
                locate_before_read = false;
                if read_length <= byte_offset {
                    return Err(io::Error::new(
                        io::ErrorKind::UnexpectedEof,
                        format!(
                            "source block is shorter than extent byte offset: partition={} block={} read={} offset={}",
                            extent.partition, physical_block, read_length, byte_offset
                        ),
                    ));
                }
                let take = (read_length - byte_offset).min(remaining as usize);
                let mut source_offset = byte_offset;
                let source_end = source_offset + take;
                while source_offset < source_end {
                    let copy =
                        (target_slot_size - target_buffer.len()).min(source_end - source_offset);
                    target_buffer
                        .extend_from_slice(&source_buffer[source_offset..source_offset + copy]);
                    source_offset += copy;
                    if target_buffer.len() == target_slot_size {
                        let hash_started = Instant::now();
                        hashes.update(&target_buffer)?;
                        context.header().hash_ns.fetch_add(
                            hash_started.elapsed().as_nanos().min(u64::MAX as u128) as u64,
                            Ordering::Relaxed,
                        );
                        bridge_publish(context, file_index, logical_offset, &target_buffer, 0, "")?;
                        logical_offset += target_buffer.len() as u64;
                        context
                            .header()
                            .bytes_read
                            .fetch_add(target_buffer.len() as u64, Ordering::Relaxed);
                        target_buffer.clear();
                    }
                }
                remaining -= take as u64;
                byte_offset = 0;
                if remaining > 0 {
                    physical_block = physical_block.checked_add(1).ok_or_else(|| {
                        io::Error::new(
                            io::ErrorKind::InvalidData,
                            "source tape block address overflow",
                        )
                    })?;
                }
            }
        }
        if !target_buffer.is_empty() {
            let hash_started = Instant::now();
            hashes.update(&target_buffer)?;
            context.header().hash_ns.fetch_add(
                hash_started.elapsed().as_nanos().min(u64::MAX as u128) as u64,
                Ordering::Relaxed,
            );
            bridge_publish(context, file_index, logical_offset, &target_buffer, 0, "")?;
            logical_offset += target_buffer.len() as u64;
            context
                .header()
                .bytes_read
                .fetch_add(target_buffer.len() as u64, Ordering::Relaxed);
        }
        if logical_offset != file_length {
            return Err(io::Error::new(
                io::ErrorKind::UnexpectedEof,
                format!(
                    "source file length mismatch: read={logical_offset}, expected={file_length}"
                ),
            ));
        }
        let hash_started = Instant::now();
        let hashes = hashes.finish()?;
        context.header().hash_ns.fetch_add(
            hash_started.elapsed().as_nanos().min(u64::MAX as u128) as u64,
            Ordering::Relaxed,
        );
        bridge_publish(context, file_index, logical_offset, &[], FLAG_EOF, &hashes)?;
        Ok(())
    })();

    if let Ok(mut worker) = context.worker_thread.lock() {
        worker.take();
    }
    match result {
        Ok(()) => LFR_OK,
        Err(error) if error.kind() == io::ErrorKind::Interrupted => LFR_CANCELLED,
        Err(error) => {
            context.set_error(error);
            LFR_ERROR
        }
    }
}

#[unsafe(no_mangle)]
/// Destroys a bridge context returned by this module.
///
/// # Safety
/// `context` must be null or a live context that has not already been
/// destroyed.
pub unsafe extern "system" fn lfr_bridge_destroy(context: *mut LfrBridgeContext) {
    if context.is_null() {
        return;
    }
    let context = unsafe { Box::from_raw(context) };
    if context.role == BridgeRole::Producer {
        context
            .header()
            .producer_attached
            .store(0, Ordering::Release);
    } else {
        context
            .header()
            .consumer_attached
            .store(0, Ordering::Release);
    }
    drop(context);
}

