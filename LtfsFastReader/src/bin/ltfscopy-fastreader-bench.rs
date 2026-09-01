use ltfscopy_fastreader::{
    LFR_DONE, LFR_HASH_XXH3, LFR_HASH_XXH128, LFR_OK, LfrConfig, LfrContext, LfrSlot, LfrStats,
    lfr_acquire_slot, lfr_add_file, lfr_cancel, lfr_create, lfr_destroy, lfr_get_stats,
    lfr_release_slot, lfr_select_file, lfr_start, lfr_wait_until_buffered,
};
use std::env;
use std::fs;
use std::mem::size_of;
use std::path::{Path, PathBuf};
use std::ptr::null_mut;
use std::time::Instant;

fn value(args: &[String], name: &str, default: u64) -> u64 {
    args.windows(2)
        .find(|pair| pair[0] == name)
        .and_then(|pair| pair[1].parse().ok())
        .unwrap_or(default)
}

fn collect(path: &Path, output: &mut Vec<(PathBuf, u64)>) -> std::io::Result<()> {
    let metadata = fs::metadata(path)?;
    if metadata.is_file() {
        output.push((path.to_owned(), metadata.len()));
    } else {
        for entry in fs::read_dir(path)? {
            collect(&entry?.path(), output)?;
        }
    }
    Ok(())
}

fn check(code: i32, operation: &str) {
    assert_eq!(code, LFR_OK, "{operation} failed with native status {code}");
}

fn main() -> std::io::Result<()> {
    let args: Vec<String> = env::args().collect();
    let Some(source) = args.get(1) else {
        eprintln!(
            "usage: ltfscopy-fastreader-bench <file-or-directory> [--pipeline-capacity-mib 6144] [--pipeline-slot-kib 512] [--pipeline-read-mib 1] [--pipeline-depth 16] [--pipeline-prefill 75]"
        );
        std::process::exit(2);
    };
    let capacity = value(&args, "--pipeline-capacity-mib", 6144) * 1024 * 1024;
    let slot_size = value(&args, "--pipeline-slot-kib", 512) * 1024;
    let read_size = value(&args, "--pipeline-read-mib", 1) * 1024 * 1024;
    let depth = value(&args, "--pipeline-depth", 16) as u32;
    let prefill_percent = value(&args, "--pipeline-prefill", 75).min(100);

    let mut files = Vec::new();
    collect(Path::new(source), &mut files)?;
    files.sort_by(|left, right| left.0.cmp(&right.0));
    let total: u64 = files.iter().map(|file| file.1).sum();
    if files.is_empty() {
        return Err(std::io::Error::new(
            std::io::ErrorKind::NotFound,
            "no files",
        ));
    }

    let config = LfrConfig {
        struct_size: size_of::<LfrConfig>() as u32,
        abi_version: ltfscopy_fastreader::LFR_ABI_VERSION,
        slot_size: slot_size as u32,
        read_chunk_size: read_size as u32,
        queue_depth: depth,
        capacity_bytes: capacity,
        small_open_concurrency: 16,
        small_active_files: 64,
        small_inflight_bytes: 256 * 1024 * 1024,
        small_threshold: 2 * 1024 * 1024,
        hash_mask: LFR_HASH_XXH3 | LFR_HASH_XXH128,
        next_file_prime_depth: 1,
        read_stall_timeout_ms: 30_000,
        io_cancel_grace_ms: 5_000,
        max_consecutive_file_retries: 3,
        file_retry_base_delay_ms: 1_000,
    };
    let mut context: *mut LfrContext = null_mut();
    unsafe {
        check(lfr_create(&config, &mut context), "create");
        for (index, (path, length)) in files.iter().enumerate() {
            let wide: Vec<u16> = path.as_os_str().to_string_lossy().encode_utf16().collect();
            check(
                lfr_add_file(
                    context,
                    index as i64,
                    wide.as_ptr(),
                    wide.len() as u32,
                    *length,
                ),
                "add file",
            );
            check(lfr_select_file(context, index as i64), "select file");
        }
        let started = Instant::now();
        check(lfr_start(context), "start");
        let target = total.min(capacity.saturating_mul(prefill_percent) / 100);
        check(
            lfr_wait_until_buffered(context, target, u32::MAX),
            "prefill",
        );
        let prefill_elapsed = started.elapsed();
        let mut stats = LfrStats {
            struct_size: size_of::<LfrStats>() as u32,
            abi_version: 0,
            bytes_read: 0,
            bytes_published: 0,
            buffered_bytes: 0,
            occupied_slots: 0,
            read_wait_ns: 0,
            hash_ns: 0,
            publish_wait_ns: 0,
        };
        check(lfr_get_stats(context, &mut stats), "get prefill stats");
        println!(
            "prefill: {:.2} GiB in {:.3}s = {:.1} MiB/s, occupied={} slots",
            stats.buffered_bytes as f64 / 1073741824.0,
            prefill_elapsed.as_secs_f64(),
            stats.buffered_bytes as f64 / 1048576.0 / prefill_elapsed.as_secs_f64(),
            stats.occupied_slots
        );

        let mut consumed = 0u64;
        loop {
            let mut slot = LfrSlot {
                token: 0,
                file_index: 0,
                file_offset: 0,
                data: std::ptr::null(),
                length: 0,
                flags: 0,
            };
            let result = lfr_acquire_slot(context, -1, u32::MAX, &mut slot);
            if result == LFR_DONE {
                break;
            }
            check(result, "acquire slot");
            consumed += slot.length as u64;
            check(lfr_release_slot(context, slot.token), "release slot");
        }
        check(lfr_get_stats(context, &mut stats), "get final stats");
        let elapsed = started.elapsed();
        println!(
            "total: {:.2} GiB in {:.3}s = {:.1} MiB/s; consumed={:.2} GiB",
            stats.bytes_read as f64 / 1073741824.0,
            elapsed.as_secs_f64(),
            stats.bytes_read as f64 / 1048576.0 / elapsed.as_secs_f64(),
            consumed as f64 / 1073741824.0
        );
        println!(
            "stages: io_wait={:.1}ms hash={:.1}ms publish_wait={:.1}ms",
            stats.read_wait_ns as f64 / 1e6,
            stats.hash_ns as f64 / 1e6,
            stats.publish_wait_ns as f64 / 1e6
        );
        let _ = lfr_cancel(context);
        lfr_destroy(context);
    }
    Ok(())
}
