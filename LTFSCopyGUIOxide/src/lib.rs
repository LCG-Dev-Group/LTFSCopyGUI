//! Unified native library exporting the FastReader and Schema C APIs.

pub use ltfscopy_fastreader::{
    lfr_abi_version, lfr_acquire_slot, lfr_add_file, lfr_bridge_abi_version,
    lfr_bridge_acquire_slot, lfr_bridge_buffer_capacity, lfr_bridge_buffered_bytes,
    lfr_bridge_cancel, lfr_bridge_create_consumer, lfr_bridge_destroy, lfr_bridge_get_file_hashes,
    lfr_bridge_get_stats, lfr_bridge_last_error, lfr_bridge_occupied_slots,
    lfr_bridge_open_producer, lfr_bridge_producer_complete, lfr_bridge_release_slot,
    lfr_bridge_stream_tape_file, lfr_bridge_wait_until_buffered, lfr_buffer_capacity,
    lfr_buffered_bytes, lfr_cancel, lfr_create, lfr_destroy, lfr_get_file_hashes, lfr_get_stats,
    lfr_hash_file, lfr_is_done, lfr_last_error, lfr_occupied_slots, lfr_release_slot,
    lfr_select_file, lfr_start, lfr_wait_until_buffered,
};

pub use ltfscopy_schema::{
    lsc_file_copy_string, lsc_file_copy_xattr_string, lsc_file_destroy, lsc_file_get_extent,
    lsc_file_get_info, lsc_file_parse, lsc_file_serialize, lsc_last_error, lsc_merge_schema_files,
    lsc_parse_schema_file, lsc_schema_copy_string, lsc_schema_destroy, lsc_schema_get_metadata,
    lsc_schema_get_result, lsc_store_close, lsc_store_copy_directory_string,
    lsc_store_copy_file_name, lsc_store_copy_file_record, lsc_store_copy_file_summary,
    lsc_store_get_directory_file_bytes, lsc_store_get_directory_index_entry,
    lsc_store_get_directory_info, lsc_store_get_file_index_entry, lsc_store_open, lsc_store_search,
    lsc_store_sort_directory_children, lsc_store_tape_sort, lsc_writer_destroy, lsc_writer_element,
    lsc_writer_empty, lsc_writer_end, lsc_writer_file, lsc_writer_finish, lsc_writer_open,
    lsc_writer_raw, lsc_writer_start, lsc_writer_start_attribute, lsc_writer_store_directory_files,
    lsc_writer_store_file_record,
};
