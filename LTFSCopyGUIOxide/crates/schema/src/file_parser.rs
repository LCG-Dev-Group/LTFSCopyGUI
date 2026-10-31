//! Parsing of individual LTFS file records.

use quick_xml::Reader;
use quick_xml::events::BytesStart;
use quick_xml::events::Event;
use std::io::BufRead;
use std::io::Cursor;

use super::{
    FileData, LscExtent, LscStoreFileSummary, decode_cdata, decode_general_ref,
    decode_schema_value, decode_text, event_name_end, event_name_start, invalid, is_name,
    parse_bool, parse_partition,
};

pub(super) struct FileParser<R: BufRead> {
    reader: Reader<R>,
}

impl<R: BufRead> FileParser<R> {
    pub(super) fn new(reader: Reader<R>) -> Self {
        Self { reader }
    }

    fn next_event(&mut self, buffer: &mut Vec<u8>) -> Result<Event<'static>, String> {
        self.reader
            .read_event_into(buffer)
            .map(|event| event.into_owned())
            .map_err(|error| error.to_string())
    }

    pub(super) fn parse(mut self) -> Result<FileData, String> {
        let mut buffer = Vec::new();
        let root = loop {
            match self.next_event(&mut buffer)? {
                Event::Start(value) => break value,
                Event::Empty(value) if is_name(&value, "file") => {
                    return Ok(FileData {
                        open_for_write: true,
                        ..Default::default()
                    });
                }
                Event::Decl(_) | Event::Comment(_) | Event::PI(_) => continue,
                Event::Text(value)
                    if value
                        .as_ref()
                        .chars()
                        .all(|character| character.is_ascii_whitespace()) =>
                {
                    continue;
                }
                Event::DocType(_) | Event::GeneralRef(_) => {
                    return Err(invalid("unsafe XML construct in file record"));
                }
                _ => return Err(invalid("file record root element was not found")),
            }
        };
        if !is_name(&root, "file") {
            return Err(invalid("file record root element must be file"));
        }

        let mut result = FileData {
            open_for_write: true,
            ..Default::default()
        };
        if root.is_empty() {
            return Ok(result);
        }
        loop {
            match self.next_event(&mut buffer)? {
                Event::Start(value) => self.parse_child(value, &mut result)?,
                Event::Empty(value) => self.parse_empty_child(value, &mut result)?,
                Event::End(value) if event_name_end(&value) == "file" => break,
                Event::End(_)
                | Event::Text(_)
                | Event::CData(_)
                | Event::Comment(_)
                | Event::PI(_) => {}
                Event::Decl(_) => {
                    return Err(invalid("XML declaration is not valid inside a file record"));
                }
                Event::DocType(_) | Event::GeneralRef(_) => {
                    return Err(invalid("unsafe XML construct in file record"));
                }
                Event::Eof => return Err(invalid("unexpected end of file record")),
            }
        }
        Ok(result)
    }

    // Search and statistics only need one scalar from a lazy file record.
    // Keeping these paths separate from `parse` avoids allocating xattrs,
    // extents, and all of the other file metadata for every visited entry.
    fn parse_name(mut self) -> Result<String, String> {
        let mut buffer = Vec::new();
        let root = loop {
            match self.next_event(&mut buffer)? {
                Event::Start(value) => break value,
                Event::Empty(value) if is_name(&value, "file") => return Ok(String::new()),
                Event::Decl(_) | Event::Comment(_) | Event::PI(_) => continue,
                Event::Text(value)
                    if value
                        .as_ref()
                        .chars()
                        .all(|character| character.is_ascii_whitespace()) =>
                {
                    continue;
                }
                Event::DocType(_) | Event::GeneralRef(_) => {
                    return Err(invalid("unsafe XML construct in file record"));
                }
                _ => return Err(invalid("file record root element was not found")),
            }
        };
        if !is_name(&root, "file") {
            return Err(invalid("file record root element must be file"));
        }
        if root.is_empty() {
            return Ok(String::new());
        }

        loop {
            match self.next_event(&mut buffer)? {
                Event::Start(value) if is_name(&value, "name") => {
                    return Ok(decode_schema_value(self.read_text(value)?));
                }
                Event::Start(value) => self.skip_element(value)?,
                Event::Empty(value) if is_name(&value, "name") => return Ok(String::new()),
                Event::Empty(_)
                | Event::Text(_)
                | Event::CData(_)
                | Event::Comment(_)
                | Event::PI(_) => {}
                Event::End(value) if event_name_end(&value) == "file" => return Ok(String::new()),
                Event::End(_) => {}
                Event::Decl(_) => {
                    return Err(invalid("XML declaration is not valid inside a file record"));
                }
                Event::DocType(_) | Event::GeneralRef(_) => {
                    return Err(invalid("unsafe XML construct in file record"));
                }
                Event::Eof => return Err(invalid("unexpected end of file record")),
            }
        }
    }

    fn parse_length(mut self) -> Result<i64, String> {
        let mut buffer = Vec::new();
        let root = loop {
            match self.next_event(&mut buffer)? {
                Event::Start(value) => break value,
                Event::Empty(value) if is_name(&value, "file") => return Ok(0),
                Event::Decl(_) | Event::Comment(_) | Event::PI(_) => continue,
                Event::Text(value)
                    if value
                        .as_ref()
                        .chars()
                        .all(|character| character.is_ascii_whitespace()) =>
                {
                    continue;
                }
                Event::DocType(_) | Event::GeneralRef(_) => {
                    return Err(invalid("unsafe XML construct in file record"));
                }
                _ => return Err(invalid("file record root element was not found")),
            }
        };
        if !is_name(&root, "file") {
            return Err(invalid("file record root element must be file"));
        }
        if root.is_empty() {
            return Ok(0);
        }

        loop {
            match self.next_event(&mut buffer)? {
                Event::Start(value) if is_name(&value, "length") => {
                    return Ok(self.read_text(value)?.trim().parse().unwrap_or(0));
                }
                Event::Start(value) => self.skip_element(value)?,
                Event::Empty(value) if is_name(&value, "length") => return Ok(0),
                Event::Empty(_)
                | Event::Text(_)
                | Event::CData(_)
                | Event::Comment(_)
                | Event::PI(_) => {}
                Event::End(value) if event_name_end(&value) == "file" => return Ok(0),
                Event::End(_) => {}
                Event::Decl(_) => {
                    return Err(invalid("XML declaration is not valid inside a file record"));
                }
                Event::DocType(_) | Event::GeneralRef(_) => {
                    return Err(invalid("unsafe XML construct in file record"));
                }
                Event::Eof => return Err(invalid("unexpected end of file record")),
            }
        }
    }

    fn parse_summary(mut self) -> Result<ParsedFileSummary, String> {
        let mut result = ParsedFileSummary {
            info: LscStoreFileSummary {
                struct_size: std::mem::size_of::<LscStoreFileSummary>() as u32,
                abi_version: 1,
                ..Default::default()
            },
            ..Default::default()
        };
        let mut buffer = Vec::new();
        let root = loop {
            match self.next_event(&mut buffer)? {
                Event::Start(value) => break value,
                Event::Empty(value) if is_name(&value, "file") => return Ok(result),
                Event::Decl(_) | Event::Comment(_) | Event::PI(_) => continue,
                Event::Text(value)
                    if value
                        .as_ref()
                        .chars()
                        .all(|character| character.is_ascii_whitespace()) =>
                {
                    continue;
                }
                Event::DocType(_) | Event::GeneralRef(_) => {
                    return Err(invalid("unsafe XML construct in file record"));
                }
                _ => return Err(invalid("file record root element was not found")),
            }
        };
        if !is_name(&root, "file") {
            return Err(invalid("file record root element must be file"));
        }
        if root.is_empty() {
            return Ok(result);
        }

        loop {
            match self.next_event(&mut buffer)? {
                Event::Start(value) if is_name(&value, "name") => {
                    result.name = decode_schema_value(self.read_text(value)?);
                }
                Event::Start(value) if is_name(&value, "length") => {
                    result.info.length = self.read_text(value)?.trim().parse().unwrap_or(0);
                }
                Event::Start(value) if is_name(&value, "extentinfo") => {
                    self.parse_first_extent(value, &mut result.info)?;
                }
                Event::Start(value) => self.skip_element(value)?,
                Event::Empty(value) if is_name(&value, "name") => result.name.clear(),
                Event::Empty(value) if is_name(&value, "length") => result.info.length = 0,
                Event::Empty(_) => {}
                Event::End(value) if event_name_end(&value) == "file" => break,
                Event::End(_)
                | Event::Text(_)
                | Event::CData(_)
                | Event::Comment(_)
                | Event::PI(_) => {}
                Event::Decl(_) => {
                    return Err(invalid("XML declaration is not valid inside a file record"));
                }
                Event::DocType(_) | Event::GeneralRef(_) => {
                    return Err(invalid("unsafe XML construct in file record"));
                }
                Event::Eof => return Err(invalid("unexpected end of file record")),
            }
        }
        Ok(result)
    }

    fn parse_first_extent(
        &mut self,
        start: BytesStart<'static>,
        result: &mut LscStoreFileSummary,
    ) -> Result<(), String> {
        if start.is_empty() {
            return Ok(());
        }
        let expected = event_name_start(&start);
        let mut found = false;
        let mut buffer = Vec::new();
        loop {
            match self.next_event(&mut buffer)? {
                Event::Start(value) if is_name(&value, "extent") => {
                    if !found {
                        let extent = self.parse_extent(value)?;
                        result.partition = extent.partition;
                        result.start_block = extent.start_block;
                        result.byte_offset = extent.byte_offset;
                        result.byte_count = extent.byte_count;
                        found = true;
                    } else {
                        // Tape sorting only needs the first extent.  The
                        // remaining extents still have to be consumed to
                        // reach the end of extentinfo, but parsing all of
                        // their numeric fields is unnecessary.
                        self.skip_element(value)?;
                    }
                }
                Event::Empty(value) if is_name(&value, "extent") => {
                    found = true;
                }
                Event::Start(value) => self.skip_element(value)?,
                Event::Empty(_) => {}
                Event::End(value) if event_name_end(&value) == expected => break,
                Event::End(_)
                | Event::Text(_)
                | Event::CData(_)
                | Event::Comment(_)
                | Event::PI(_) => {}
                Event::Decl(_) => {
                    return Err(invalid("XML declaration is not valid inside extents"));
                }
                Event::DocType(_) | Event::GeneralRef(_) => {
                    return Err(invalid("unsafe XML construct in extents"));
                }
                Event::Eof => return Err(invalid("unexpected end of extents")),
            }
        }
        Ok(())
    }

    fn parse_child(
        &mut self,
        value: BytesStart<'static>,
        result: &mut FileData,
    ) -> Result<(), String> {
        match event_name_start(&value).as_str() {
            "name" => result.name = decode_schema_value(self.read_text(value)?),
            "length" => result.length = self.read_text(value)?.trim().parse().unwrap_or(0),
            "readonly" => {
                result.read_only = parse_bool(self.read_text(value)?.as_str()).unwrap_or(false)
            }
            "openforwrite" => {
                result.open_for_write = parse_bool(self.read_text(value)?.as_str()).unwrap_or(true)
            }
            "creationtime" => {
                result.creation_time = Some(decode_schema_value(self.read_text(value)?))
            }
            "changetime" => result.change_time = Some(decode_schema_value(self.read_text(value)?)),
            "modifytime" => result.modify_time = Some(decode_schema_value(self.read_text(value)?)),
            "accesstime" => result.access_time = Some(decode_schema_value(self.read_text(value)?)),
            "backuptime" => result.backup_time = Some(decode_schema_value(self.read_text(value)?)),
            "fileuid" => result.file_uid = self.read_text(value)?.trim().parse().unwrap_or(0),
            "symlink" => result.symlink = Some(decode_schema_value(self.read_text(value)?)),
            "extendedattributes" => self.parse_xattrs(value, result)?,
            "extentinfo" => self.parse_extents(value, result)?,
            _ => self.skip_element(value)?,
        }
        Ok(())
    }

    fn parse_empty_child(
        &mut self,
        value: BytesStart<'static>,
        result: &mut FileData,
    ) -> Result<(), String> {
        match event_name_start(&value).as_str() {
            "extendedattributes" | "extentinfo" => {}
            "symlink" => result.symlink = Some(String::new()),
            _ => {}
        }
        Ok(())
    }

    fn parse_xattrs(
        &mut self,
        start: BytesStart<'static>,
        result: &mut FileData,
    ) -> Result<(), String> {
        if start.is_empty() {
            return Ok(());
        }
        let expected = event_name_start(&start);
        let mut buffer = Vec::new();
        loop {
            match self.next_event(&mut buffer)? {
                Event::Start(value) if is_name(&value, "xattr") => {
                    result.xattrs.push(self.parse_xattr(value)?)
                }
                Event::Start(value) => self.skip_element(value)?,
                Event::Empty(value) if is_name(&value, "xattr") => {
                    result.xattrs.push((String::new(), String::new()))
                }
                Event::Empty(_) => {}
                Event::End(value) if event_name_end(&value) == expected => break,
                Event::End(_)
                | Event::Text(_)
                | Event::CData(_)
                | Event::Comment(_)
                | Event::PI(_) => {}
                Event::Decl(_) => {
                    return Err(invalid("XML declaration is not valid inside attributes"));
                }
                Event::DocType(_) | Event::GeneralRef(_) => {
                    return Err(invalid("unsafe XML construct in file record"));
                }
                Event::Eof => return Err(invalid("unexpected end of attributes")),
            }
        }
        Ok(())
    }

    fn parse_xattr(&mut self, start: BytesStart<'static>) -> Result<(String, String), String> {
        if start.is_empty() {
            return Ok((String::new(), String::new()));
        }
        let mut key = String::new();
        let mut value = String::new();
        let mut buffer = Vec::new();
        loop {
            match self.next_event(&mut buffer)? {
                Event::Start(child) if is_name(&child, "key") => {
                    key = decode_schema_value(self.read_text(child)?)
                }
                Event::Start(child) if is_name(&child, "value") => {
                    value = decode_schema_value(self.read_text(child)?)
                }
                Event::Start(child) => self.skip_element(child)?,
                Event::Empty(_) => {}
                Event::End(child) if event_name_end(&child) == "xattr" => break,
                Event::End(_)
                | Event::Text(_)
                | Event::CData(_)
                | Event::Comment(_)
                | Event::PI(_) => {}
                Event::Decl(_) => {
                    return Err(invalid("XML declaration is not valid inside an attribute"));
                }
                Event::DocType(_) | Event::GeneralRef(_) => {
                    return Err(invalid("unsafe XML construct in file record"));
                }
                Event::Eof => return Err(invalid("unexpected end of xattr")),
            }
        }
        Ok((key, value))
    }

    fn parse_extents(
        &mut self,
        start: BytesStart<'static>,
        result: &mut FileData,
    ) -> Result<(), String> {
        if start.is_empty() {
            return Ok(());
        }
        let expected = event_name_start(&start);
        let mut buffer = Vec::new();
        loop {
            match self.next_event(&mut buffer)? {
                Event::Start(value) if is_name(&value, "extent") => {
                    result.extents.push(self.parse_extent(value)?)
                }
                Event::Start(value) => self.skip_element(value)?,
                Event::Empty(value) if is_name(&value, "extent") => {
                    result.extents.push(LscExtent::default())
                }
                Event::Empty(_) => {}
                Event::End(value) if event_name_end(&value) == expected => break,
                Event::End(_)
                | Event::Text(_)
                | Event::CData(_)
                | Event::Comment(_)
                | Event::PI(_) => {}
                Event::Decl(_) => {
                    return Err(invalid("XML declaration is not valid inside extents"));
                }
                Event::DocType(_) | Event::GeneralRef(_) => {
                    return Err(invalid("unsafe XML construct in file record"));
                }
                Event::Eof => return Err(invalid("unexpected end of extents")),
            }
        }
        Ok(())
    }

    fn parse_extent(&mut self, start: BytesStart<'static>) -> Result<LscExtent, String> {
        if start.is_empty() {
            return Ok(LscExtent::default());
        }
        let mut result = LscExtent::default();
        let mut buffer = Vec::new();
        loop {
            match self.next_event(&mut buffer)? {
                Event::Start(value) => {
                    let name = event_name_start(&value);
                    let text = self.read_text(value)?;
                    match name.as_str() {
                        "fileoffset" => result.file_offset = text.trim().parse().unwrap_or(0),
                        "partition" => result.partition = parse_partition(&text).unwrap_or(0),
                        "startblock" => result.start_block = text.trim().parse().unwrap_or(0),
                        "byteoffset" => result.byte_offset = text.trim().parse().unwrap_or(0),
                        "bytecount" => result.byte_count = text.trim().parse().unwrap_or(0),
                        _ => {}
                    }
                }
                Event::Empty(_) => {}
                Event::End(value) if event_name_end(&value) == "extent" => break,
                Event::End(_)
                | Event::Text(_)
                | Event::CData(_)
                | Event::Comment(_)
                | Event::PI(_) => {}
                Event::Decl(_) => {
                    return Err(invalid("XML declaration is not valid inside an extent"));
                }
                Event::DocType(_) | Event::GeneralRef(_) => {
                    return Err(invalid("unsafe XML construct in file record"));
                }
                Event::Eof => return Err(invalid("unexpected end of extent")),
            }
        }
        Ok(result)
    }

    fn read_text(&mut self, start: BytesStart<'static>) -> Result<String, String> {
        if start.is_empty() {
            return Ok(String::new());
        }
        let expected = event_name_start(&start);
        let mut result = String::new();
        let mut buffer = Vec::new();
        loop {
            match self.next_event(&mut buffer)? {
                Event::Text(value) => result.push_str(&decode_text(value.as_ref())?),
                Event::CData(value) => result.push_str(&decode_cdata(value.as_ref())?),
                Event::GeneralRef(value) => result.push_str(&decode_general_ref(value.as_ref())?),
                Event::Start(value) => self.skip_element(value)?,
                Event::Empty(_) | Event::Comment(_) | Event::PI(_) => {}
                Event::End(value) if event_name_end(&value) == expected => break,
                Event::End(_) => {}
                Event::Decl(_) => return Err(invalid("XML declaration is not valid inside text")),
                Event::DocType(_) => return Err(invalid("unsafe XML construct in file record")),
                Event::Eof => return Err(invalid("unexpected end of XML text")),
            }
        }
        Ok(result)
    }

    fn skip_element(&mut self, start: BytesStart<'static>) -> Result<(), String> {
        if start.is_empty() {
            return Ok(());
        }
        let mut depth = 1i32;
        let mut buffer = Vec::new();
        while depth > 0 {
            match self.next_event(&mut buffer)? {
                Event::Start(_) => depth += 1,
                Event::End(_) => depth -= 1,
                Event::Empty(_)
                | Event::Text(_)
                | Event::CData(_)
                | Event::Comment(_)
                | Event::PI(_) => {}
                Event::Decl(_) => {
                    return Err(invalid("XML declaration is not valid inside an element"));
                }
                Event::DocType(_) => {
                    return Err(invalid("unsafe XML construct in file record"));
                }
                Event::GeneralRef(value) => {
                    // A normal XML entity such as `&amp;` may occur inside
                    // a skipped text field (most commonly a file name).  It
                    // is safe after the same whitelist validation used by
                    // `read_text`.
                    decode_general_ref(value.as_ref())?;
                }
                Event::Eof => return Err(invalid("unexpected end of XML element")),
            }
        }
        Ok(())
    }
}

pub(super) fn parse_file_bytes(bytes: &[u8]) -> Result<FileData, String> {
    FileParser::new(Reader::from_reader(Cursor::new(bytes))).parse()
}

pub(super) fn parse_file_name_bytes(bytes: &[u8]) -> Result<String, String> {
    FileParser::new(Reader::from_reader(Cursor::new(bytes))).parse_name()
}

pub(super) fn parse_file_length_bytes(bytes: &[u8]) -> Result<i64, String> {
    FileParser::new(Reader::from_reader(Cursor::new(bytes))).parse_length()
}

#[derive(Default)]
pub(super) struct ParsedFileSummary {
    pub(super) name: String,
    pub(super) info: LscStoreFileSummary,
}

pub(super) fn parse_file_summary_bytes(bytes: &[u8]) -> Result<ParsedFileSummary, String> {
    FileParser::new(Reader::from_reader(Cursor::new(bytes))).parse_summary()
}
