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

    #[test]
    fn bridge_validation_accepts_multi_gigabyte_capacity() {
        let capacity = 30u64 * 1024 * 1024 * 1024;
        let config = LfrBridgeConfig {
            struct_size: std::mem::size_of::<LfrBridgeConfig>() as u32,
            abi_version: LFR_BRIDGE_ABI_VERSION,
            slot_size: 512 * 1024,
            reserved: 0,
            capacity_bytes: capacity,
            hash_mask: 0,
            reserved2: 0,
        };
        let (slot_count, slot_stride, mapping_size) = bridge_validate_config(&config).unwrap();
        assert_eq!(slot_count, 61440);
        assert!(slot_stride >= config.slot_size as usize);
        assert!(mapping_size as u64 >= capacity);
        assert!(mapping_size as u64 <= BRIDGE_MAX_MAPPING_BYTES);
    }

    #[test]
    fn bridge_ring_round_trip_preserves_order_and_hashes() {
        let unique = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        let name = format!("Local\\LfrBridgeTest-{}-{unique}", std::process::id());
        let name_wide = wide(&name);
        let config = LfrBridgeConfig {
            struct_size: std::mem::size_of::<LfrBridgeConfig>() as u32,
            abi_version: LFR_BRIDGE_ABI_VERSION,
            slot_size: 16,
            reserved: 0,
            capacity_bytes: 32,
            hash_mask: LFR_HASH_CRC32,
            reserved2: 0,
        };
        let mut consumer = null_mut();
        assert_eq!(
            unsafe {
                lfr_bridge_create_consumer(
                    name_wide.as_ptr(),
                    (name_wide.len() - 1) as u32,
                    &config,
                    &mut consumer,
                )
            },
            LFR_OK
        );
        assert!(!consumer.is_null());
        let mut producer = null_mut();
        assert_eq!(
            unsafe {
                lfr_bridge_open_producer(
                    name_wide.as_ptr(),
                    (name_wide.len() - 1) as u32,
                    &mut producer,
                )
            },
            LFR_OK
        );
        assert!(!producer.is_null());

        let (ready_tx, ready_rx) = std::sync::mpsc::channel();
        let producer_address = producer as usize;
        let writer = thread::spawn(move || {
            let producer = producer_address as *mut LfrBridgeContext;
            unsafe {
                bridge_publish(&*producer, 7, 0, b"0123456789ABCDEF", 0, "").unwrap();
                bridge_publish(&*producer, 7, 16, b"fedcba9876543210", 0, "").unwrap();
            }
            ready_tx.send(()).unwrap();
            unsafe {
                bridge_publish(&*producer, 7, 32, &[], FLAG_EOF, "CRC32=12345678").unwrap();
                assert_eq!(lfr_bridge_producer_complete(producer), LFR_OK);
                lfr_bridge_destroy(producer);
            }
        });
        ready_rx.recv().unwrap();
        assert_eq!(unsafe { lfr_bridge_buffered_bytes(consumer) }, 32);
        for (offset, expected, flags) in [
            (0u64, b"0123456789ABCDEF".as_slice(), 0u32),
            (16u64, b"fedcba9876543210".as_slice(), 0u32),
            (32u64, b"".as_slice(), FLAG_EOF),
        ] {
            let mut slot = LfrSlot {
                token: 0,
                file_index: -1,
                file_offset: 0,
                data: null(),
                length: 0,
                flags: 0,
            };
            assert_eq!(
                unsafe { lfr_bridge_acquire_slot(consumer, 7, 2_000, &mut slot) },
                LFR_OK
            );
            assert_eq!(slot.file_offset, offset);
            assert_eq!(slot.flags, flags);
            assert_eq!(slot.length as usize, expected.len());
            if !expected.is_empty() {
                assert_eq!(
                    unsafe { std::slice::from_raw_parts(slot.data, slot.length as usize) },
                    expected
                );
            }
            assert_eq!(
                unsafe { lfr_bridge_release_slot(consumer, slot.token) },
                LFR_OK
            );
        }
        writer.join().unwrap();
        let mut hashes = [0u8; 64];
        let mut written = 0u32;
        assert_eq!(
            unsafe {
                lfr_bridge_get_file_hashes(
                    consumer,
                    7,
                    hashes.as_mut_ptr(),
                    hashes.len() as u32,
                    &mut written,
                )
            },
            LFR_OK
        );
        assert_eq!(&hashes[..written as usize], b"CRC32=12345678");
        assert_eq!(unsafe { lfr_bridge_buffered_bytes(consumer) }, 0);
        assert_eq!(unsafe { lfr_bridge_occupied_slots(consumer) }, 0);
        unsafe { lfr_bridge_destroy(consumer) };
    }

    #[test]
    fn bridge_wait_respects_15_75_watermarks_and_full_ring_liveness() {
        let unique = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        let name = format!(
            "Local\\LfrBridgeWatermarkTest-{}-{unique}",
            std::process::id()
        );
        let name_wide = wide(&name);
        let config = LfrBridgeConfig {
            struct_size: std::mem::size_of::<LfrBridgeConfig>() as u32,
            abi_version: LFR_BRIDGE_ABI_VERSION,
            slot_size: 10,
            reserved: 0,
            capacity_bytes: 100,
            hash_mask: 0,
            reserved2: 0,
        };
        let mut consumer = null_mut();
        assert_eq!(
            unsafe {
                lfr_bridge_create_consumer(
                    name_wide.as_ptr(),
                    (name_wide.len() - 1) as u32,
                    &config,
                    &mut consumer,
                )
            },
            LFR_OK
        );
        let mut producer = null_mut();
        assert_eq!(
            unsafe {
                lfr_bridge_open_producer(
                    name_wide.as_ptr(),
                    (name_wide.len() - 1) as u32,
                    &mut producer,
                )
            },
            LFR_OK
        );

        unsafe {
            bridge_publish(&*producer, 9, 0, &[1; 10], 0, "").unwrap();
            bridge_publish(&*producer, 9, 10, &[2; 5], 0, "").unwrap();
        }
        assert_eq!(unsafe { lfr_bridge_buffered_bytes(consumer) }, 15);
        assert_eq!(
            unsafe { lfr_bridge_wait_until_buffered(consumer, 75, 150) },
            LFR_TIMEOUT
        );

        for index in 0..6u64 {
            unsafe {
                bridge_publish(&*producer, 9, 15 + index * 10, &[3; 10], 0, "").unwrap();
            }
        }
        assert_eq!(unsafe { lfr_bridge_buffered_bytes(consumer) }, 75);
        assert_eq!(
            unsafe { lfr_bridge_wait_until_buffered(consumer, 75, 150) },
            LFR_OK
        );

        for _ in 0..8 {
            let mut slot = LfrSlot {
                token: 0,
                file_index: -1,
                file_offset: 0,
                data: null(),
                length: 0,
                flags: 0,
            };
            assert_eq!(
                unsafe { lfr_bridge_acquire_slot(consumer, 9, 500, &mut slot) },
                LFR_OK
            );
            assert_eq!(
                unsafe { lfr_bridge_release_slot(consumer, slot.token) },
                LFR_OK
            );
        }
        assert_eq!(unsafe { lfr_bridge_buffered_bytes(consumer) }, 0);

        // Ten short payloads occupy every slot while accounting for only 10%
        // of byte capacity. Waiting for 75% must yield so the consumer can
        // drain slots and unblock the producer.
        for index in 0..10u64 {
            unsafe {
                bridge_publish(&*producer, 9, 75 + index, &[4], 0, "").unwrap();
            }
        }
        assert_eq!(unsafe { lfr_bridge_buffered_bytes(consumer) }, 10);
        assert_eq!(unsafe { lfr_bridge_occupied_slots(consumer) }, 10);
        assert_eq!(
            unsafe { lfr_bridge_wait_until_buffered(consumer, 75, 150) },
            LFR_OK
        );

        for _ in 0..10 {
            let mut slot = LfrSlot {
                token: 0,
                file_index: -1,
                file_offset: 0,
                data: null(),
                length: 0,
                flags: 0,
            };
            assert_eq!(
                unsafe { lfr_bridge_acquire_slot(consumer, 9, 500, &mut slot) },
                LFR_OK
            );
            assert_eq!(
                unsafe { lfr_bridge_release_slot(consumer, slot.token) },
                LFR_OK
            );
        }
        assert_eq!(unsafe { lfr_bridge_producer_complete(producer) }, LFR_OK);
        unsafe {
            lfr_bridge_destroy(producer);
            lfr_bridge_destroy(consumer);
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
