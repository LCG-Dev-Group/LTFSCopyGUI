use super::*;

// Keep the implementation in responsibility-sized files while retaining one
// private module namespace. This preserves the existing internal visibility
// and makes the public C ABI independent of the source-file layout.
include!("core.rs");
include!("media.rs");
include!("hash.rs");
include!("async_reader.rs");
include!("small_files.rs");
include!("native_reader.rs");
include!("bridge.rs");

#[cfg(test)]
mod tests {
    include!("tests.rs");
}
