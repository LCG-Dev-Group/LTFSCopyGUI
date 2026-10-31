// Included separately by each crate; no process-global logging subscriber.
#[cfg(all(debug_assertions, not(test)))]
struct DebugLog {
    started: std::time::Instant,
    file: std::sync::Mutex<Option<std::fs::File>>,
}

#[cfg(all(debug_assertions, not(test)))]
static DEBUG_LOG: std::sync::OnceLock<DebugLog> = std::sync::OnceLock::new();

#[cfg(all(debug_assertions, not(test)))]
fn debug_log_impl(arguments: std::fmt::Arguments<'_>) {
    use std::io::Write;

    // Cache the switch so diagnostic ablation does not add environment reads per event.
    let logger = DEBUG_LOG.get_or_init(|| {
        if std::env::var_os("LTFSCOPY_DEBUG_LOG").is_some_and(|value| value == "0") {
            return DebugLog {
                started: std::time::Instant::now(),
                file: std::sync::Mutex::new(None),
            };
        }

        // Each crate owns its logger even when linked into the same DLL.
        let file_name = format!("{DEBUG_LOG_PREFIX}-{}.log", std::process::id());
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
                    "{DEBUG_LOG_PREFIX}_DEBUG_LOG_OPEN_ERROR\tpath={}\terror={error}",
                    path.display()
                );
                None
            }
        };
        DebugLog {
            started: std::time::Instant::now(),
            file: std::sync::Mutex::new(file),
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
    let current_thread = std::thread::current();
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
