# LTFSCopyGUIOxide

Rust workspace for LTFSCopyGUI native components. Run Cargo commands from this directory so the shared `.cargo/config.toml` is applied.

- `crates/fastreader`: asynchronous reading, hashing and tape copy bridge; compiled as an internal Rust library.
- `crates/schema`: LTFS XML parsing, backing store, search and sorting; compiled as an internal Rust library.

```powershell
cargo build -p ltfscopy-oxide
cargo test --workspace
cargo clippy --workspace --all-targets --all-features -- -D warnings
```

The root `ltfscopy-oxide` package links both internal crates into the single `ltfscopy_oxide.dll`. The VB project builds this package and copies its DLL and PDB from the shared `target/` directory. All existing C ABI entry point names remain unchanged.

Windows 7 builds use the existing nightly toolchain with `rust-src`:

```powershell
cargo +nightly build -Z build-std=std,panic_abort --target x86_64-win7-windows-msvc --release -p ltfscopy-oxide --lib
```

Source files live under `crates/<name>/src/`, following [pixi's crate layout](https://github.com/prefix-dev/pixi/tree/main/crates/pixi_core). Cargo discovers each library at `src/lib.rs`; the FastReader benchmark has an explicit binary target. Edition and dependency versions are maintained in the workspace manifest and inherited by members, following [pixi's workspace configuration](https://github.com/prefix-dev/pixi/blob/main/Cargo.toml).
