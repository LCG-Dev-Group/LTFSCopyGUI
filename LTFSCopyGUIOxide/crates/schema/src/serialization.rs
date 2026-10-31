//! XML serialization of LTFS file records and fragments.

use quick_xml::Reader;
use quick_xml::Writer;
use quick_xml::escape::escape;
use quick_xml::events::BytesEnd;
use quick_xml::events::BytesStart;
use quick_xml::events::BytesText;
use quick_xml::events::Event;
use std::io::Cursor;
use std::io::Write;
use std::slice;
use std::str;

use super::{
    FileData, LscExtent, LscFileInput, LscUtf16Slice, decode_general_ref, invalid, utf16_string,
};

// HPLTFS consumes the index as a line-oriented XML document.  Keep the
// indentation width at zero so every element starts on its own line without
// changing the compact, schema-compatible tag layout.
pub(super) fn new_xml_writer<W: Write>(inner: W) -> Writer<W> {
    Writer::new_with_indent(inner, b' ', 0)
}

pub(super) fn file_data_from_input(input: &LscFileInput) -> Result<FileData, String> {
    let text = |slice: LscUtf16Slice| unsafe { utf16_string(slice.ptr, slice.len) };
    let optional = |slice: LscUtf16Slice| -> Result<Option<String>, String> {
        if slice.ptr.is_null() && slice.len == 0 {
            Ok(None)
        } else {
            text(slice).map(Some)
        }
    };
    let mut result = FileData {
        name: text(input.name)?,
        length: input.length,
        read_only: input.read_only != 0,
        open_for_write: input.open_for_write != 0,
        creation_time: optional(input.creation_time)?,
        change_time: optional(input.change_time)?,
        modify_time: optional(input.modify_time)?,
        access_time: optional(input.access_time)?,
        backup_time: optional(input.backup_time)?,
        file_uid: input.file_uid,
        symlink: optional(input.symlink)?,
        ..Default::default()
    };
    if input.xattr_count > 0 {
        if input.xattrs.is_null() {
            return Err(invalid("xattr input pointer is null"));
        }
        // SAFETY: the FFI contract supplies an array with xattr_count entries.
        let entries = unsafe { slice::from_raw_parts(input.xattrs, input.xattr_count as usize) };
        for entry in entries {
            result.xattrs.push((text(entry.key)?, text(entry.value)?));
        }
    }
    if input.extent_count > 0 {
        if input.extents.is_null() {
            return Err(invalid("extent input pointer is null"));
        }
        // SAFETY: the FFI contract supplies an array with extent_count entries.
        let entries = unsafe { slice::from_raw_parts(input.extents, input.extent_count as usize) };
        result.extents = entries
            .iter()
            .map(|value| LscExtent {
                file_offset: value.file_offset,
                partition: value.partition,
                reserved: 0,
                start_block: value.start_block,
                byte_offset: value.byte_offset,
                byte_count: value.byte_count,
            })
            .collect();
    }
    Ok(result)
}

pub(super) fn write_xml_text<W: Write>(writer: &mut Writer<W>, value: &str) -> Result<(), String> {
    let escaped = escape(value);
    writer
        .write_event(Event::Text(BytesText::from_escaped(escaped)))
        .map_err(|error| error.to_string())
}

pub(super) fn write_xml_element<W: Write>(
    writer: &mut Writer<W>,
    name: &str,
    value: Option<&str>,
) -> Result<(), String> {
    writer
        .write_event(Event::Start(BytesStart::new(name)))
        .map_err(|error| error.to_string())?;
    if let Some(value) = value {
        write_xml_text(writer, value)?;
    }
    writer
        .write_event(Event::End(BytesEnd::new(name)))
        .map_err(|error| error.to_string())
}

pub(super) fn serialize_file(value: &FileData) -> Result<Vec<u8>, String> {
    let mut writer = Writer::new(Vec::with_capacity(1024));
    writer
        .write_event(Event::Start(BytesStart::new("file")))
        .map_err(|error| error.to_string())?;
    write_xml_element(&mut writer, "name", Some(&value.name))?;
    write_xml_element(&mut writer, "length", Some(&value.length.to_string()))?;
    write_xml_element(
        &mut writer,
        "readonly",
        Some(if value.read_only { "true" } else { "false" }),
    )?;
    write_xml_element(
        &mut writer,
        "openforwrite",
        Some(if value.open_for_write {
            "true"
        } else {
            "false"
        }),
    )?;
    write_xml_element(&mut writer, "creationtime", value.creation_time.as_deref())?;
    write_xml_element(&mut writer, "changetime", value.change_time.as_deref())?;
    write_xml_element(&mut writer, "modifytime", value.modify_time.as_deref())?;
    write_xml_element(&mut writer, "accesstime", value.access_time.as_deref())?;
    write_xml_element(&mut writer, "backuptime", value.backup_time.as_deref())?;
    write_xml_element(&mut writer, "fileuid", Some(&value.file_uid.to_string()))?;
    writer
        .write_event(Event::Start(BytesStart::new("extendedattributes")))
        .map_err(|error| error.to_string())?;
    for (key, attr_value) in &value.xattrs {
        writer
            .write_event(Event::Start(BytesStart::new("xattr")))
            .map_err(|error| error.to_string())?;
        write_xml_element(&mut writer, "key", Some(key))?;
        write_xml_element(&mut writer, "value", Some(attr_value))?;
        writer
            .write_event(Event::End(BytesEnd::new("xattr")))
            .map_err(|error| error.to_string())?;
    }
    writer
        .write_event(Event::End(BytesEnd::new("extendedattributes")))
        .map_err(|error| error.to_string())?;
    if let Some(symlink) = value.symlink.as_deref() {
        write_xml_element(&mut writer, "symlink", Some(symlink))?;
    }
    writer
        .write_event(Event::Start(BytesStart::new("extentinfo")))
        .map_err(|error| error.to_string())?;
    for extent in &value.extents {
        writer
            .write_event(Event::Start(BytesStart::new("extent")))
            .map_err(|error| error.to_string())?;
        write_xml_element(
            &mut writer,
            "fileoffset",
            Some(&extent.file_offset.to_string()),
        )?;
        write_xml_element(
            &mut writer,
            "partition",
            Some(if extent.partition == 0 { "a" } else { "b" }),
        )?;
        write_xml_element(
            &mut writer,
            "startblock",
            Some(&extent.start_block.to_string()),
        )?;
        write_xml_element(
            &mut writer,
            "byteoffset",
            Some(&extent.byte_offset.to_string()),
        )?;
        write_xml_element(
            &mut writer,
            "bytecount",
            Some(&extent.byte_count.to_string()),
        )?;
        writer
            .write_event(Event::End(BytesEnd::new("extent")))
            .map_err(|error| error.to_string())?;
    }
    writer
        .write_event(Event::End(BytesEnd::new("extentinfo")))
        .map_err(|error| error.to_string())?;
    writer
        .write_event(Event::End(BytesEnd::new("file")))
        .map_err(|error| error.to_string())?;
    Ok(writer.into_inner())
}

pub(super) fn write_fragment<W: Write>(writer: &mut Writer<W>, bytes: &[u8]) -> Result<(), String> {
    let mut reader = Reader::from_reader(Cursor::new(bytes));
    let mut buffer = Vec::new();
    loop {
        let event = reader
            .read_event_into(&mut buffer)
            .map_err(|error| error.to_string())?
            .into_owned();
        buffer.clear();
        match event {
            Event::Start(value) => writer.write_event(Event::Start(value)),
            Event::Empty(value) => writer.write_event(Event::Empty(value)),
            Event::End(value) => writer.write_event(Event::End(value)),
            Event::Text(value) => writer.write_event(Event::Text(value)),
            Event::CData(value) => writer.write_event(Event::CData(value)),
            Event::Comment(_) | Event::PI(_) => Ok(()),
            Event::Decl(_) => return Err(invalid("XML declaration in fragment")),
            Event::DocType(_) => return Err(invalid("unsafe XML construct in fragment")),
            Event::GeneralRef(value) => {
                decode_general_ref(value.as_ref())?;
                writer.write_event(Event::GeneralRef(value))
            }
            Event::Eof => break,
        }
        .map_err(|error| error.to_string())?;
    }
    Ok(())
}
