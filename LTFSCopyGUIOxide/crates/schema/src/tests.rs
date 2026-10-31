use super::file_parser::FileParser;
use super::*;

#[test]
fn parses_compact_schema_into_lazy_backing_files() {
    let root = std::env::temp_dir().join(format!("ltfscopy_schema_test_{}", std::process::id()));
    let _ = std::fs::create_dir_all(&root);
    let input = root.join("sample.schema");
    let paths = [
        root.join("files.bin"),
        root.join("directories.bin"),
        root.join("file-index.bin"),
        root.join("directory-index.bin"),
        root.join("selection.bin"),
    ];
    let text = r#"<ltfsindex version="2.4.0"><creator>A &amp; B</creator><volumeuuid>00112233-4455-6677-8899-aabbccddeeff</volumeuuid><generationnumber>7</generationnumber><updatetime>2024-01-01T00:00:00Z</updatetime><location><partition>a</partition><startblock>12</startblock></location><allowpolicyupdate>True</allowpolicyupdate><volumelockstate>locked</volumelockstate><highestfileuid>99</highestfileuid><directory><name>root</name><readonly>False</readonly><contents><file><name>hello &amp; world.txt</name><length>5</length><openforwrite>True</openforwrite><fileuid>3</fileuid><extendedattributes><xattr><key>k</key><value>v</value></xattr></extendedattributes><extentinfo><extent><fileoffset>0</fileoffset><partition>a</partition><startblock>4</startblock><byteoffset>0</byteoffset><bytecount>5</bytecount></extent></extentinfo></file><directory><name>child</name><contents /></directory></contents></directory></ltfsindex>"#;
    std::fs::write(&input, text).expect("write test schema");

    let context = schema_context_from_file(input.to_string_lossy().into_owned(), paths.clone())
        .expect("parse test schema");
    assert_eq!(context.result.root_directory_count, 1);
    assert_eq!(context.metadata.creator, "A & B");
    assert_eq!(context.metadata.public.generation_number, 7);
    assert_eq!(context.metadata.public.location_start_block, 12);
    assert!(context.result.selection_count >= 3);

    assert!(
        std::fs::metadata(&paths[0])
            .expect("read file backing metadata")
            .len()
            > 0
    );

    let _ = std::fs::remove_dir_all(root);
}

#[test]
fn file_serializer_escapes_unicode_and_ampersands() {
    let value = FileData {
        name: "中文 & <name>.txt".to_owned(),
        length: 4,
        open_for_write: true,
        xattrs: vec![("key".to_owned(), "值 & value".to_owned())],
        extents: vec![LscExtent {
            partition: 1,
            start_block: 42,
            byte_offset: 7,
            byte_count: 9,
            ..Default::default()
        }],
        ..Default::default()
    };
    let xml =
        String::from_utf8(serialize_file(&value).expect("serialize file")).expect("UTF-8 file XML");
    assert!(xml.contains("中文 &amp; &lt;name&gt;.txt"));
    assert!(xml.contains("<readonly>false</readonly>"));
    assert!(xml.contains("<openforwrite>true</openforwrite>"));
    let parsed = FileParser::new(Reader::from_reader(Cursor::new(xml.as_bytes())))
        .parse()
        .expect("parse file XML");
    assert_eq!(parsed.name, value.name);
    assert_eq!(parsed.xattrs, value.xattrs);
    assert_eq!(
        parse_file_name_bytes(xml.as_bytes()).expect("parse file name"),
        value.name
    );
    assert_eq!(
        parse_file_length_bytes(xml.as_bytes()).expect("parse file length"),
        4
    );
    let summary = parse_file_summary_bytes(xml.as_bytes()).expect("parse file summary");
    assert_eq!(summary.name, value.name);
    assert_eq!(summary.info.length, value.length);
    assert_eq!(summary.info.partition, 1);
    assert_eq!(summary.info.start_block, 42);
    assert_eq!(summary.info.byte_offset, 7);
    assert_eq!(summary.info.byte_count, 9);
}

#[test]
fn schema_writer_starts_with_utf8_xml_declaration() {
    let root = std::env::temp_dir().join(format!(
        "ltfscopy_schema_writer_header_test_{}",
        std::process::id()
    ));
    let _ = std::fs::remove_dir_all(&root);
    std::fs::create_dir_all(&root).expect("create writer header test directory");
    let output = root.join("output.schema");
    let path_utf16: Vec<u16> = output.to_string_lossy().encode_utf16().collect();
    let name_utf16: Vec<u16> = "ltfsindex".encode_utf16().collect();
    let mut handle: *mut SchemaWriter = std::ptr::null_mut();

    assert_eq!(
        unsafe {
            lsc_writer_open(
                path_utf16.as_ptr(),
                u32::try_from(path_utf16.len()).expect("path length"),
                &mut handle,
            )
        },
        LSC_OK
    );
    assert!(!handle.is_null());

    assert_eq!(
        unsafe {
            lsc_writer_start(
                handle,
                name_utf16.as_ptr(),
                u32::try_from(name_utf16.len()).expect("name length"),
            )
        },
        LSC_OK
    );
    assert_eq!(
        unsafe {
            lsc_writer_end(
                handle,
                name_utf16.as_ptr(),
                u32::try_from(name_utf16.len()).expect("name length"),
            )
        },
        LSC_OK
    );
    assert_eq!(unsafe { lsc_writer_finish(handle) }, LSC_OK);
    unsafe { lsc_writer_destroy(handle) };

    let xml = std::fs::read_to_string(&output).expect("read schema writer output");
    assert_eq!(
        xml,
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<ltfsindex>\n</ltfsindex>"
    );

    let _ = std::fs::remove_dir_all(root);
}

#[test]
fn schema_writer_places_raw_elements_on_separate_lines() {
    let root = std::env::temp_dir().join(format!(
        "ltfscopy_schema_writer_lines_test_{}",
        std::process::id()
    ));
    let _ = std::fs::remove_dir_all(&root);
    std::fs::create_dir_all(&root).expect("create writer line test directory");
    let output = root.join("output.schema");
    let path_utf16: Vec<u16> = output.to_string_lossy().encode_utf16().collect();
    let mut handle: *mut SchemaWriter = std::ptr::null_mut();

    assert_eq!(
        unsafe {
            lsc_writer_open(
                path_utf16.as_ptr(),
                u32::try_from(path_utf16.len()).expect("path length"),
                &mut handle,
            )
        },
        LSC_OK
    );
    assert!(!handle.is_null());

    let start = |value: &str| value.encode_utf16().collect::<Vec<u16>>();
    let root_name = start("ltfsindex");
    let directory_name = start("directory");
    let contents_name = start("contents");
    let compact_file = b"<file><name>one.txt</name><length>1</length></file>";
    assert_eq!(
        unsafe {
            lsc_writer_start(
                handle,
                root_name.as_ptr(),
                u32::try_from(root_name.len()).expect("root name length"),
            )
        },
        LSC_OK
    );
    assert_eq!(
        unsafe {
            lsc_writer_start(
                handle,
                directory_name.as_ptr(),
                u32::try_from(directory_name.len()).expect("directory name length"),
            )
        },
        LSC_OK
    );
    assert_eq!(
        unsafe {
            lsc_writer_start(
                handle,
                contents_name.as_ptr(),
                u32::try_from(contents_name.len()).expect("contents name length"),
            )
        },
        LSC_OK
    );
    assert_eq!(
        unsafe {
            lsc_writer_raw(
                handle,
                compact_file.as_ptr(),
                u64::try_from(compact_file.len()).expect("fragment length"),
            )
        },
        LSC_OK
    );
    assert_eq!(
        unsafe {
            lsc_writer_end(
                handle,
                contents_name.as_ptr(),
                u32::try_from(contents_name.len()).expect("contents name length"),
            )
        },
        LSC_OK
    );
    assert_eq!(
        unsafe {
            lsc_writer_end(
                handle,
                directory_name.as_ptr(),
                u32::try_from(directory_name.len()).expect("directory name length"),
            )
        },
        LSC_OK
    );
    assert_eq!(
        unsafe {
            lsc_writer_end(
                handle,
                root_name.as_ptr(),
                u32::try_from(root_name.len()).expect("root name length"),
            )
        },
        LSC_OK
    );
    assert_eq!(unsafe { lsc_writer_finish(handle) }, LSC_OK);
    unsafe { lsc_writer_destroy(handle) };

    let xml = std::fs::read_to_string(&output).expect("read line-oriented schema");
    assert_eq!(
        xml,
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<ltfsindex>\n<directory>\n<contents>\n<file>\n<name>one.txt</name>\n<length>1</length>\n</file>\n</contents>\n</directory>\n</ltfsindex>"
    );

    let _ = std::fs::remove_dir_all(root);
}

#[test]
fn imported_pretty_file_records_drop_formatting_whitespace() {
    let root = std::env::temp_dir().join(format!(
        "ltfscopy_schema_pretty_file_test_{}",
        std::process::id()
    ));
    let _ = std::fs::remove_dir_all(&root);
    std::fs::create_dir_all(&root).expect("create pretty file test directory");
    let input = root.join("pretty.schema");
    let paths = [
        root.join("files.bin"),
        root.join("directories.bin"),
        root.join("file-index.bin"),
        root.join("directory-index.bin"),
        root.join("selection.bin"),
    ];
    let text = r#"<ltfsindex version="2.4.0">
  <directory>
<name>root</name>
<contents>
  <file>
    <name> spaced.txt </name>
    <length>1</length>
  </file>
</contents>
  </directory>
</ltfsindex>"#;
    std::fs::write(&input, text).expect("write pretty file schema");

    let context = schema_context_from_file(input.to_string_lossy().into_owned(), paths.clone())
        .expect("parse pretty file schema");
    let record = std::fs::read(&paths[0]).expect("read compacted file record");
    assert_eq!(
        std::str::from_utf8(&record).expect("UTF-8 file record"),
        "<file><name> spaced.txt </name><length>1</length></file>"
    );

    drop(context);
    let _ = std::fs::remove_dir_all(root);
}

#[test]
fn merges_schema_roots_directly_into_one_lazy_store() {
    let root =
        std::env::temp_dir().join(format!("ltfscopy_schema_merge_test_{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&root);
    std::fs::create_dir_all(&root).expect("create merge test directory");
    let first = root.join("first.schema");
    let second = root.join("second.schema");
    let paths = [
        root.join("files.bin"),
        root.join("directories.bin"),
        root.join("file-index.bin"),
        root.join("directory-index.bin"),
        root.join("selection.bin"),
    ];
    let first_text = r#"<ltfsindex version="2.4.0">
  <directory>
<name>root</name>
<contents>
  <file>
    <name>a.txt</name>
    <length>1</length>
  </file>
  <directory>
    <name>sub</name>
    <contents>
      <file>
        <name>b.txt</name>
        <length>2</length>
      </file>
    </contents>
  </directory>
</contents>
  </directory>
</ltfsindex>"#;
    let second_text = r#"<ltfsindex version="2.4.0"><directory><name>root</name><contents><file><name>c.txt</name><length>3</length></file></contents></directory></ltfsindex>"#;
    std::fs::write(&first, first_text).expect("write first schema");
    std::fs::write(&second, second_text).expect("write second schema");

    let context =
        schema_context_from_files(vec![first, second], "Search_test".to_owned(), paths.clone())
            .expect("merge test schemas");
    assert_eq!(context.result.root_file_count, 0);
    assert_eq!(context.result.root_directory_count, 1);

    let store = StoreContext {
        file_records: Mutex::new(File::open(&paths[0]).expect("open file backing")),
        directory_records: Mutex::new(File::open(&paths[1]).expect("open directory backing")),
        file_index: Mutex::new(File::open(&paths[2]).expect("open file index backing")),
        directory_index: Mutex::new(File::open(&paths[3]).expect("open directory index backing")),
    };
    let root_index =
        store_directory_index_entry(&store, context.result.root_directory_index_offset)
            .expect("read merged root index");
    let root_header = read_store_directory_header(&store, root_index.record_offset)
        .expect("read merged root header");
    assert_eq!(root_header.file_count, 2);
    assert_eq!(root_header.directory_count, 1);
    assert_eq!(root_header.total_file_count, 3);
    assert_eq!(root_header.total_directory_count, 1);

    let first_file_index = store_file_index_entry(&store, root_header.file_index_offset)
        .expect("read first merged file index");
    let first_file_bytes = read_store_at(
        &store.file_records,
        first_file_index.record_offset,
        first_file_index.record_length as usize,
        "merged file record",
    )
    .expect("read first merged file");
    let first_file = parse_file_bytes(&first_file_bytes).expect("parse first merged file");
    assert_eq!(first_file.name, "a.txt");
    assert!(!first_file_bytes.contains(&b'\n'));
    assert!(!first_file_bytes.contains(&b'\r'));
    assert_eq!(
        first_file
            .xattrs
            .iter()
            .find(|(key, _)| key == "Barcode")
            .map(|(_, value)| value.as_str()),
        Some("first")
    );

    drop(store);
    drop(context);
    let _ = std::fs::remove_dir_all(root);
}

#[test]
fn merges_same_name_directories_recursively_but_keeps_same_name_files() {
    let root = std::env::temp_dir().join(format!(
        "ltfscopy_schema_duplicate_directory_merge_test_{}",
        std::process::id()
    ));
    let _ = std::fs::remove_dir_all(&root);
    std::fs::create_dir_all(&root).expect("create duplicate directory merge test directory");
    let first = root.join("first.schema");
    let second = root.join("second.schema");
    let paths = [
        root.join("files.bin"),
        root.join("directories.bin"),
        root.join("file-index.bin"),
        root.join("directory-index.bin"),
        root.join("selection.bin"),
    ];
    let first_text = r#"<ltfsindex version="2.4.0"><directory><name>root</name><fileuid>900</fileuid><contents><file><name>same.txt</name><length>1</length><fileuid>42</fileuid></file><directory><name>same</name><fileuid>901</fileuid><contents><file><name>first.txt</name><length>2</length><fileuid>43</fileuid></file><directory><name>nested</name><fileuid>902</fileuid><contents><file><name>nested-first.txt</name><length>3</length><fileuid>44</fileuid></file></contents></directory></contents></directory></contents></directory></ltfsindex>"#;
    let second_text = r#"<ltfsindex version="2.4.0"><directory><name>root</name><fileuid>900</fileuid><contents><file><name>same.txt</name><length>4</length><fileuid>42</fileuid></file><directory><name>same</name><fileuid>901</fileuid><contents><file><name>second.txt</name><length>5</length><fileuid>43</fileuid></file><directory><name>nested</name><fileuid>902</fileuid><contents><file><name>nested-second.txt</name><length>6</length><fileuid>44</fileuid></file></contents></directory></contents></directory></contents></directory></ltfsindex>"#;
    std::fs::write(&first, first_text).expect("write first duplicate directory schema");
    std::fs::write(&second, second_text).expect("write second duplicate directory schema");

    let context = schema_context_from_files(
        vec![first, second],
        "Search_duplicate".to_owned(),
        paths.clone(),
    )
    .expect("merge duplicate directory schemas");
    assert_eq!(context.metadata.public.highest_file_uid, 8);
    assert_eq!(
        context.metadata.public.present_mask & PRESENT_HIGHEST_FILE_UID,
        PRESENT_HIGHEST_FILE_UID
    );
    let store = StoreContext {
        file_records: Mutex::new(File::open(&paths[0]).expect("open file backing")),
        directory_records: Mutex::new(File::open(&paths[1]).expect("open directory backing")),
        file_index: Mutex::new(File::open(&paths[2]).expect("open file index backing")),
        directory_index: Mutex::new(File::open(&paths[3]).expect("open directory index backing")),
    };

    let root_index =
        store_directory_index_entry(&store, context.result.root_directory_index_offset)
            .expect("read merged root index");
    let root_header = read_store_directory_header(&store, root_index.record_offset)
        .expect("read merged root header");
    assert_eq!(root_header.file_count, 2);
    assert_eq!(root_header.directory_count, 1);
    assert_eq!(root_header.total_file_count, 6);
    assert_eq!(root_header.total_directory_count, 2);

    let mut root_file_ids = Vec::new();
    let mut root_file_index_offset = root_header.file_index_offset;
    for _ in 0..root_header.file_count {
        let entry = store_file_index_entry(&store, root_file_index_offset)
            .expect("read merged root file index");
        let bytes = read_store_at(
            &store.file_records,
            entry.record_offset,
            entry.record_length as usize,
            "merged root file record",
        )
        .expect("read merged root file record");
        root_file_ids.push(
            parse_file_bytes(&bytes)
                .expect("parse merged root file")
                .file_uid,
        );
        root_file_index_offset = entry.next_offset;
    }
    assert_eq!(root_file_ids, vec![1, 4]);

    let same_index = store_directory_index_entry(&store, root_header.directory_index_offset)
        .expect("read merged same directory index");
    assert_eq!(same_index.next_offset, -1);
    let same_header = read_store_directory_header(&store, same_index.record_offset)
        .expect("read merged same directory header");
    assert_eq!(
        read_store_directory_scalars(&store, &same_header)
            .unwrap()
            .name
            .as_deref(),
        Some("same")
    );
    assert_eq!(
        read_store_directory_scalars(&store, &same_header)
            .expect("read merged same directory scalars")
            .file_uid,
        7
    );
    assert_eq!(same_header.file_count, 2);
    assert_eq!(same_header.directory_count, 1);
    assert_eq!(same_header.total_file_count, 4);
    assert_eq!(same_header.total_directory_count, 1);

    let nested_index = store_directory_index_entry(&store, same_header.directory_index_offset)
        .expect("read merged nested directory index");
    assert_eq!(nested_index.next_offset, -1);
    let nested_header = read_store_directory_header(&store, nested_index.record_offset)
        .expect("read merged nested directory header");
    assert_eq!(
        read_store_directory_scalars(&store, &nested_header)
            .unwrap()
            .name
            .as_deref(),
        Some("nested")
    );
    assert_eq!(
        read_store_directory_scalars(&store, &nested_header)
            .expect("read merged nested directory scalars")
            .file_uid,
        8
    );
    assert_eq!(nested_header.file_count, 2);
    assert_eq!(nested_header.directory_count, 0);
    assert_eq!(nested_header.total_file_count, 2);
    assert_eq!(nested_header.total_directory_count, 0);

    drop(store);
    drop(context);
    let _ = std::fs::remove_dir_all(root);
}

#[test]
fn searches_lazy_store_across_directories_and_wraps() {
    let root = std::env::temp_dir().join(format!(
        "ltfscopy_schema_search_test_{}",
        std::process::id()
    ));
    let _ = std::fs::remove_dir_all(&root);
    std::fs::create_dir_all(&root).expect("create search test directory");
    let input = root.join("search.schema");
    let paths = [
        root.join("files.bin"),
        root.join("directories.bin"),
        root.join("file-index.bin"),
        root.join("directory-index.bin"),
        root.join("selection.bin"),
    ];
    let text = r#"<ltfsindex version="2.4.0"><directory><name>root</name><contents><file><name>48188-root.txt</name><length>1</length></file><directory><name>sub</name><contents><file><name>48188-child.txt</name><length>2</length></file></contents></directory></contents></directory></ltfsindex>"#;
    std::fs::write(&input, text).expect("write search test schema");

    let context = schema_context_from_file(input.to_string_lossy().into_owned(), paths.clone())
        .expect("parse search test schema");
    let store = StoreContext {
        file_records: Mutex::new(File::open(&paths[0]).expect("open file backing")),
        directory_records: Mutex::new(File::open(&paths[1]).expect("open directory backing")),
        file_index: Mutex::new(File::open(&paths[2]).expect("open file index backing")),
        directory_index: Mutex::new(File::open(&paths[3]).expect("open directory index backing")),
    };
    let root_index =
        store_directory_index_entry(&store, context.result.root_directory_index_offset)
            .expect("read search root index");

    let first = search_store(
        &store,
        StoreSearchOptions {
            root_record_offset: root_index.record_offset,
            root_path: "root".to_owned(),
            keyword: "48188".to_owned(),
            case_sensitive: false,
            resume_kind: 0,
            resume_record_offset: -1,
            callback: None,
            user_data: ptr::null_mut(),
        },
    )
    .expect("search first result");
    assert_eq!(first.path, r"root\48188-root.txt");
    assert_eq!(first.directory_path, "root");

    let second = search_store(
        &store,
        StoreSearchOptions {
            root_record_offset: root_index.record_offset,
            root_path: "root".to_owned(),
            keyword: "48188".to_owned(),
            case_sensitive: false,
            resume_kind: LSC_SEARCH_MATCH_FILE,
            resume_record_offset: first.result.record_offset,
            callback: None,
            user_data: ptr::null_mut(),
        },
    )
    .expect("search second result");
    assert_eq!(second.path, r"root\sub\48188-child.txt");
    assert_eq!(second.directory_path, r"root\sub");

    let wrapped = search_store(
        &store,
        StoreSearchOptions {
            root_record_offset: root_index.record_offset,
            root_path: "root".to_owned(),
            keyword: "48188".to_owned(),
            case_sensitive: false,
            resume_kind: LSC_SEARCH_MATCH_FILE,
            resume_record_offset: second.result.record_offset,
            callback: None,
            user_data: ptr::null_mut(),
        },
    )
    .expect("search wrapped result");
    assert_eq!(wrapped.path, r"root\48188-root.txt");

    drop(store);
    drop(context);
    let _ = std::fs::remove_dir_all(root);
}

#[test]
fn sorts_selected_files_by_tape_position_in_native_code() {
    let root = std::env::temp_dir().join(format!(
        "ltfscopy_schema_tape_sort_test_{}",
        std::process::id()
    ));
    let _ = std::fs::remove_dir_all(&root);
    std::fs::create_dir_all(&root).expect("create tape sort test directory");
    let input = root.join("sort.schema");
    let paths = [
        root.join("files.bin"),
        root.join("directories.bin"),
        root.join("file-index.bin"),
        root.join("directory-index.bin"),
        root.join("selection.bin"),
    ];
    let output = root.join("tape-sort.run");
    let text = r#"<ltfsindex version="2.4.0"><directory><name>root</name><contents><file><name>b.txt</name><length>2</length><extentinfo><extent><partition>b</partition><startblock>2</startblock><byteoffset>0</byteoffset><bytecount>2</bytecount></extent></extentinfo></file><file><name>a.txt</name><length>3</length><extentinfo><extent><partition>a</partition><startblock>20</startblock><byteoffset>0</byteoffset><bytecount>3</bytecount></extent></extentinfo></file><directory><name>sub</name><contents><file><name>c.txt</name><length>4</length><extentinfo><extent><partition>a</partition><startblock>10</startblock><byteoffset>0</byteoffset><bytecount>4</bytecount></extent></extentinfo></file></contents></directory><file><name>d.txt</name><length>5</length><extentinfo><extent><partition>a</partition><startblock>10</startblock><byteoffset>0</byteoffset><bytecount>5</bytecount></extent></extentinfo></file></contents></directory></ltfsindex>"#;
    std::fs::write(&input, text).expect("write tape sort schema");

    let context = schema_context_from_file(input.to_string_lossy().into_owned(), paths.clone())
        .expect("parse tape sort schema");
    let store = StoreContext {
        file_records: Mutex::new(File::open(&paths[0]).expect("open file backing")),
        directory_records: Mutex::new(File::open(&paths[1]).expect("open directory backing")),
        file_index: Mutex::new(File::open(&paths[2]).expect("open file index backing")),
        directory_index: Mutex::new(File::open(&paths[3]).expect("open directory index backing")),
    };
    let result = sort_tape_files(
        &store,
        TapeSortOptions {
            root_file_index_offset: context.result.root_file_index_offset,
            root_file_count: context.result.root_file_count,
            root_directory_index_offset: context.result.root_directory_index_offset,
            root_directory_count: context.result.root_directory_count,
            selection_path: &paths[4],
            output_path: &output,
            callback: None,
            user_data: ptr::null_mut(),
        },
    )
    .expect("sort tape files");
    assert_eq!(result.file_count, 4);
    assert_eq!(result.partition_a_file_count, 3);
    assert_eq!(result.partition_b_file_count, 1);

    let mut reader = BufReader::new(File::open(&output).expect("open tape sort output"));
    let mut entries = Vec::new();
    while let Some(entry) = read_tape_sort_entry(&mut reader).expect("read tape sort output") {
        entries.push((entry.partition, entry.start_block, entry.path));
    }
    assert_eq!(
        entries,
        vec![
            (0, 10, "d.txt".to_owned()),
            (0, 10, "sub\\c.txt".to_owned()),
            (0, 20, "a.txt".to_owned()),
            (1, 2, "b.txt".to_owned()),
        ]
    );

    drop(reader);
    drop(store);
    drop(context);
    let _ = std::fs::remove_dir_all(root);
}

#[test]
fn sorts_directory_children_into_fixed_index_chains() {
    let root = std::env::temp_dir().join(format!(
        "ltfscopy_schema_directory_sort_test_{}",
        std::process::id()
    ));
    let _ = std::fs::remove_dir_all(&root);
    std::fs::create_dir_all(&root).expect("create directory sort test directory");
    let input = root.join("sort.schema");
    let paths = [
        root.join("files.bin"),
        root.join("directories.bin"),
        root.join("file-index.bin"),
        root.join("directory-index.bin"),
        root.join("selection.bin"),
    ];
    let file_output = root.join("sorted-files.bin");
    let directory_output = root.join("sorted-directories.bin");
    let text = r#"<ltfsindex version="2.4.0"><directory><name>root</name><contents><file><name>b.txt</name><length>2</length></file><file><name>a.txt</name><length>3</length></file><file><name>d.txt</name><length>4</length></file><directory><name>sub</name><contents><file><name>child.txt</name><length>5</length></file></contents></directory></contents></directory></ltfsindex>"#;
    std::fs::write(&input, text).expect("write directory sort schema");

    let context = schema_context_from_file(input.to_string_lossy().into_owned(), paths.clone())
        .expect("parse directory sort schema");
    let store = StoreContext {
        file_records: Mutex::new(File::open(&paths[0]).expect("open file backing")),
        directory_records: Mutex::new(File::open(&paths[1]).expect("open directory backing")),
        file_index: Mutex::new(File::open(&paths[2]).expect("open file index backing")),
        directory_index: Mutex::new(File::open(&paths[3]).expect("open directory index backing")),
    };
    let root_index =
        store_directory_index_entry(&store, context.result.root_directory_index_offset)
            .expect("read root directory index");
    let file_target_offset = File::open(&paths[2])
        .expect("open file index for metadata")
        .metadata()
        .expect("read file index metadata")
        .len() as i64;
    let directory_target_offset = File::open(&paths[3])
        .expect("open directory index for metadata")
        .metadata()
        .expect("read directory index metadata")
        .len() as i64;
    let result = sort_directory_children(
        &store,
        DirectorySortOptions {
            directory_record_offset: root_index.record_offset,
            sort_mode: DIRECTORY_SORT_MODE_CURRENT_CULTURE,
            locale_name: String::new(),
            file_target_index_offset: file_target_offset,
            directory_target_index_offset: directory_target_offset,
            file_output_path: &file_output,
            directory_output_path: &directory_output,
            callback: None,
            user_data: ptr::null_mut(),
        },
    )
    .expect("sort directory children");
    assert_eq!(result.file_count, 3);
    assert_eq!(result.directory_count, 1);

    let reader = TapeSortReader::new(&store).expect("map directory sort backing files");
    let mut file_reader = BufReader::new(File::open(&file_output).expect("open sorted files"));
    let mut names = Vec::new();
    for _ in 0..result.file_count {
        let mut bytes = vec![0u8; FILE_INDEX_ENTRY_SIZE as usize];
        file_reader
            .read_exact(&mut bytes)
            .expect("read sorted file index");
        let mut cursor = StoreCursor::new(&bytes);
        let _next_offset = cursor.i64().expect("read sorted file next offset");
        let record_offset = cursor.i64().expect("read sorted file record offset");
        let record_length = cursor.i64().expect("read sorted file record length");
        let _selection_index = cursor.i64().expect("read sorted file selection index");
        names.push(
            reader
                .read_file_summary(record_offset, record_length)
                .expect("read sorted file summary")
                .name,
        );
    }
    assert_eq!(names, vec!["a.txt", "b.txt", "d.txt"]);

    let mut directory_reader =
        BufReader::new(File::open(&directory_output).expect("open sorted directories"));
    let mut bytes = vec![0u8; DIRECTORY_INDEX_ENTRY_SIZE as usize];
    directory_reader
        .read_exact(&mut bytes)
        .expect("read sorted directory index");
    let mut cursor = StoreCursor::new(&bytes);
    assert_eq!(cursor.i64().expect("read directory next offset"), -1);
    assert_eq!(
        reader
            .read_directory_name(
                &reader
                    .read_directory_header(cursor.i64().expect("read directory record offset"))
                    .expect("read child directory header")
            )
            .expect("read child directory name"),
        "sub"
    );

    drop(directory_reader);
    drop(file_reader);
    drop(reader);
    drop(store);
    drop(context);
    let _ = std::fs::remove_dir_all(root);
}
