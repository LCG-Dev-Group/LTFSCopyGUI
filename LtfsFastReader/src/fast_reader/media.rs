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


