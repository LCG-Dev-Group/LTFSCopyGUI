Imports System
Imports System.ComponentModel
Imports Serilog
Imports Serilog.Context

Public Class Form1
    Private ReadOnly _logSessionId As String = $"index-analyzer-{Guid.NewGuid().ToString("N").Substring(0, 8)}"
    Public schema As ltfsindex
    Public contents As ltfsindex.contentsDef
    Public filelist As New List(Of String)

    Public Class TapeFileInfo
        Public Property Path As String
        Public Property Partition As ltfsindex.PartitionLabel
        Public Property BlockNumber As Long
        Public Property FileLength As Long
    End Class

    Private Class TapeExtentInfo
        Public Property StartBlock As Long
        Public Property ByteOffset As Long
        Public Property ByteCount As Long
        Public Property FileUid As Long
    End Class

    Private NotInheritable Class TapeExtentRunCursor
        Implements IDisposable

        Private ReadOnly _stream As IO.FileStream
        Private ReadOnly _reader As IO.BinaryReader
        Private _current As TapeExtentInfo

        Public Sub New(path As String, runId As Integer)
            Me.RunId = runId
            _stream = New IO.FileStream(path, IO.FileMode.Open, IO.FileAccess.Read, IO.FileShare.Read,
                                        1 << 16, IO.FileOptions.SequentialScan)
            _reader = New IO.BinaryReader(_stream, System.Text.Encoding.UTF8, leaveOpen:=False)
        End Sub

        Public ReadOnly Property RunId As Integer
        Public ReadOnly Property Current As TapeExtentInfo
            Get
                Return _current
            End Get
        End Property

        Public Function MoveNext() As Boolean
            If _stream.Position >= _stream.Length Then
                _current = Nothing
                Return False
            End If
            _current = New TapeExtentInfo With {
                .StartBlock = _reader.ReadInt64(),
                .ByteOffset = _reader.ReadInt64(),
                .ByteCount = _reader.ReadInt64(),
                .FileUid = _reader.ReadInt64()}
            Return True
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            If _reader IsNot Nothing Then _reader.Dispose()
        End Sub
    End Class

    Private NotInheritable Class TapeExtentRunComparer
        Implements IComparer(Of TapeExtentRunCursor)

        Public Function Compare(left As TapeExtentRunCursor, right As TapeExtentRunCursor) As Integer _
            Implements IComparer(Of TapeExtentRunCursor).Compare
            Dim result As Integer = left.Current.StartBlock.CompareTo(right.Current.StartBlock)
            If result <> 0 Then Return result
            result = left.Current.ByteOffset.CompareTo(right.Current.ByteOffset)
            If result <> 0 Then Return result
            result = left.Current.ByteCount.CompareTo(right.Current.ByteCount)
            If result <> 0 Then Return result
            result = left.Current.FileUid.CompareTo(right.Current.FileUid)
            If result <> 0 Then Return result
            Return left.RunId.CompareTo(right.RunId)
        End Function
    End Class

    Private Shared Function CreateExtentSortRun(items As List(Of TapeExtentInfo)) As String
        If items Is Nothing OrElse items.Count = 0 Then Return Nothing
        items.Sort(Function(left As TapeExtentInfo, right As TapeExtentInfo) As Integer
                       Dim result As Integer = left.StartBlock.CompareTo(right.StartBlock)
                       If result <> 0 Then Return result
                       result = left.ByteOffset.CompareTo(right.ByteOffset)
                       If result <> 0 Then Return result
                       result = left.ByteCount.CompareTo(right.ByteCount)
                       If result <> 0 Then Return result
                       Return left.FileUid.CompareTo(right.FileUid)
                   End Function)
        Dim path As String = IO.Path.Combine(IO.Path.GetTempPath(), $"LCG_EXTENT_SORT_{Guid.NewGuid():N}.tmp")
        Try
            Using stream As New IO.FileStream(path, IO.FileMode.CreateNew, IO.FileAccess.Write, IO.FileShare.Read,
                                              1 << 16, IO.FileOptions.SequentialScan)
                Using writer As New IO.BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen:=False)
                    For Each item As TapeExtentInfo In items
                        writer.Write(item.StartBlock)
                        writer.Write(item.ByteOffset)
                        writer.Write(item.ByteCount)
                        writer.Write(item.FileUid)
                    Next
                End Using
            End Using
            items.Clear()
            Return path
        Catch
            Try
                If IO.File.Exists(path) Then IO.File.Delete(path)
            Catch
            End Try
            Throw
        End Try
    End Function

    Private Shared Iterator Function EnumerateExtentSortRuns(runPaths As List(Of String)) As IEnumerable(Of TapeExtentInfo)
        If runPaths Is Nothing OrElse runPaths.Count = 0 Then Exit Function
        Dim cursors As New List(Of TapeExtentRunCursor)
        Dim active As New SortedSet(Of TapeExtentRunCursor)(New TapeExtentRunComparer())
        Try
            For i As Integer = 0 To runPaths.Count - 1
                Dim cursor As New TapeExtentRunCursor(runPaths(i), i)
                cursors.Add(cursor)
                If cursor.MoveNext() Then active.Add(cursor)
            Next
            While active.Count > 0
                Dim cursor As TapeExtentRunCursor = active.Min
                active.Remove(cursor)
                Dim item As TapeExtentInfo = cursor.Current
                Yield item
                If cursor.MoveNext() Then active.Add(cursor)
            End While
        Finally
            For Each cursor As TapeExtentRunCursor In cursors
                cursor.Dispose()
            Next
        End Try
    End Function

    Private Shared Function ExtentPhysicalStart(value As TapeExtentInfo) As Decimal
        Return CDec(value.StartBlock) * 524288D + CDec(value.ByteOffset)
    End Function

    Private Const TapeSortChunkSize As Integer = 8192

    Private NotInheritable Class TapeFileRunCursor
        Implements IDisposable

        Private ReadOnly _stream As IO.FileStream
        Private ReadOnly _reader As IO.BinaryReader
        Private _current As TapeFileInfo

        Public Sub New(path As String, runId As Integer)
            Me.RunId = runId
            _stream = New IO.FileStream(path, IO.FileMode.Open, IO.FileAccess.Read, IO.FileShare.Read,
                                        1 << 16, IO.FileOptions.SequentialScan)
            _reader = New IO.BinaryReader(_stream, System.Text.Encoding.UTF8, leaveOpen:=False)
        End Sub

        Public ReadOnly Property RunId As Integer
        Public ReadOnly Property Current As TapeFileInfo
            Get
                Return _current
            End Get
        End Property

        Public Function MoveNext() As Boolean
            If _stream.Position >= _stream.Length Then
                _current = Nothing
                Return False
            End If

            _current = New TapeFileInfo With {
                .Partition = CType(_reader.ReadByte(), ltfsindex.PartitionLabel),
                .BlockNumber = _reader.ReadInt64(),
                .FileLength = _reader.ReadInt64(),
                .Path = _reader.ReadString()}
            Return True
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            If _reader IsNot Nothing Then _reader.Dispose()
        End Sub
    End Class

    Private NotInheritable Class TapeFileRunComparer
        Implements IComparer(Of TapeFileRunCursor)

        Public Function Compare(left As TapeFileRunCursor, right As TapeFileRunCursor) As Integer _
            Implements IComparer(Of TapeFileRunCursor).Compare
            Dim result As Integer = CompareTapeFileInfo(left.Current, right.Current)
            If result <> 0 Then Return result
            Return left.RunId.CompareTo(right.RunId)
        End Function
    End Class

    Private Shared Function CompareTapeFileInfo(left As TapeFileInfo, right As TapeFileInfo) As Integer
        If left.BlockNumber <> right.BlockNumber Then Return left.BlockNumber.CompareTo(right.BlockNumber)
        Return StringComparer.Ordinal.Compare(If(left.Path, String.Empty), If(right.Path, String.Empty))
    End Function

    Private Shared Function CreateTapeSortRun(items As List(Of TapeFileInfo)) As String
        If items Is Nothing OrElse items.Count = 0 Then Return Nothing
        items.Sort(AddressOf CompareTapeFileInfo)
        Dim path As String = IO.Path.Combine(IO.Path.GetTempPath(), $"LCG_TAPE_SORT_{Guid.NewGuid():N}.tmp")
        Try
            Using stream As New IO.FileStream(path, IO.FileMode.CreateNew, IO.FileAccess.Write, IO.FileShare.Read,
                                              1 << 16, IO.FileOptions.SequentialScan)
                Using writer As New IO.BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen:=False)
                    For Each item As TapeFileInfo In items
                        writer.Write(CByte(item.Partition))
                        writer.Write(item.BlockNumber)
                        writer.Write(item.FileLength)
                        writer.Write(If(item.Path, String.Empty))
                    Next
                End Using
            End Using
            items.Clear()
            Return path
        Catch
            Try
                If IO.File.Exists(path) Then IO.File.Delete(path)
            Catch
            End Try
            Throw
        End Try
    End Function

    Private Shared Iterator Function EnumerateTapeSortRuns(runPaths As List(Of String)) As IEnumerable(Of TapeFileInfo)
        If runPaths Is Nothing OrElse runPaths.Count = 0 Then Exit Function
        Dim cursors As New List(Of TapeFileRunCursor)
        Dim active As New SortedSet(Of TapeFileRunCursor)(New TapeFileRunComparer())
        Try
            For i As Integer = 0 To runPaths.Count - 1
                Dim cursor As New TapeFileRunCursor(runPaths(i), i)
                cursors.Add(cursor)
                If cursor.MoveNext() Then active.Add(cursor)
            Next

            While active.Count > 0
                Dim cursor As TapeFileRunCursor = active.Min
                active.Remove(cursor)
                Dim item As TapeFileInfo = cursor.Current
                Yield item
                If cursor.MoveNext() Then active.Add(cursor)
            End While
        Finally
            For Each cursor As TapeFileRunCursor In cursors
                cursor.Dispose()
            Next
        End Try
    End Function

    Private NotInheritable Class DirectoryPath
        Public Property Directory As ltfsindex.directory
        Public Property ParentPath As String
    End Class

    Private Iterator Function EnumerateDirectoryTapeFiles(directory As ltfsindex.directory,
                                                           parentPath As String) As IEnumerable(Of TapeFileInfo)
        If directory Is Nothing Then Exit Function
        Dim pending As New Stack(Of DirectoryPath)
        pending.Push(New DirectoryPath With {.Directory = directory, .ParentPath = parentPath})
        While pending.Count > 0
            Dim current As DirectoryPath = pending.Pop()
            Dim currentPath As String = current.ParentPath & If(current.Directory.name, String.Empty) & "\"
            For Each item As ltfsindex.file In current.Directory.EnumerateLazyFiles()
                Dim info As TapeFileInfo = CreateTapeFileInfo(item, currentPath)
                If info IsNot Nothing Then Yield info
            Next
            For Each child As ltfsindex.directory In current.Directory.EnumerateLazyDirectories()
                pending.Push(New DirectoryPath With {.Directory = child, .ParentPath = currentPath})
            Next
        End While
    End Function

    Private Sub LogFileOperationWarning(operation As String, filePath As String, ex As Exception)
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(Form1))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "IndexAnalyzer")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Error")
                        Log.Warning(ex, "Index analyzer file operation failed. Operation={Operation} FilePath={FilePath}.", operation, filePath)
                    End Using
                End Using
            End Using
        End Using
    End Sub

    Private Shared Function FileContainsText(filePath As String, value As String, caseSensitive As Boolean) As Boolean
        If String.IsNullOrEmpty(value) Then Return True
        Using reader As New IO.StreamReader(filePath, System.Text.Encoding.UTF8,
                                             detectEncodingFromByteOrderMarks:=True,
                                             bufferSize:=1 << 16)
            While Not reader.EndOfStream
                Dim line As String = reader.ReadLine()
                If line Is Nothing Then Exit While
                If caseSensitive Then
                    If line.IndexOf(value, StringComparison.Ordinal) >= 0 Then Return True
                ElseIf line.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0 Then
                    Return True
                End If
            End While
        End Using
        Return False
    End Function

    Private Shared Sub WriteMergeElement(writer As System.Xml.XmlWriter, elementName As String, value As String)
        writer.WriteStartElement(elementName)
        If value IsNot Nothing Then writer.WriteString(value)
        writer.WriteEndElement()
    End Sub

    Private Shared Sub WriteMergedFile(writer As System.Xml.XmlWriter,
                                        source As ltfsindex.file,
                                        barcode As String,
                                        serializer As System.Xml.Serialization.XmlSerializer,
                                        namespaces As System.Xml.Serialization.XmlSerializerNamespaces)
        If source Is Nothing Then Return

        'Do not modify a lazy source file just to add the merge barcode.  A
        'mutation would register every visited source object in its backing
        'store.  Clone one file at a time and release it after serialization.
        Dim outputFile As ltfsindex.file = source.GetCopy(source.fileuid)
        If Not String.IsNullOrEmpty(barcode) Then outputFile.SetXattr("Barcode", barcode)
        serializer.Serialize(writer, outputFile, namespaces)
    End Sub

    Private Shared Sub WriteMergedDirectory(writer As System.Xml.XmlWriter,
                                             source As ltfsindex.directory,
                                             barcode As String,
                                             serializer As System.Xml.Serialization.XmlSerializer,
                                             namespaces As System.Xml.Serialization.XmlSerializerNamespaces)
        If source Is Nothing Then Return

        writer.WriteStartElement("directory")
        WriteMergeElement(writer, "name", source.name)
        WriteMergeElement(writer, "readonly", source.[readonly].ToString())
        WriteMergeElement(writer, "creationtime", source.creationtime)
        WriteMergeElement(writer, "changetime", source.changetime)
        WriteMergeElement(writer, "modifytime", source.modifytime)
        WriteMergeElement(writer, "accesstime", source.accesstime)
        WriteMergeElement(writer, "backuptime", source.backuptime)
        WriteMergeElement(writer, "fileuid", source.fileuid.ToString(System.Globalization.CultureInfo.InvariantCulture))
        writer.WriteStartElement("contents")
        For Each childFile As ltfsindex.file In source.EnumerateLazyFiles()
            WriteMergedFile(writer, childFile, barcode, serializer, namespaces)
        Next
        For Each childDirectory As ltfsindex.directory In source.EnumerateLazyDirectories()
            WriteMergedDirectory(writer, childDirectory, barcode, serializer, namespaces)
        Next
        writer.WriteEndElement()
        writer.WriteEndElement()
    End Sub

    Private Function BuildMergedSchema(schemaFiles As IO.FileInfo(),
                                       pattern As String,
                                       infoText As Text.StringBuilder,
                                       ByRef progressValue As Integer) As ltfsindex
        Dim tempPath As String = IO.Path.Combine(IO.Path.GetTempPath(), $"LCG_MERGE_{Guid.NewGuid():N}.schema")
        Try
            Dim settings As New System.Xml.XmlWriterSettings With {
                .Encoding = New Text.UTF8Encoding(False),
                .OmitXmlDeclaration = True,
                .Indent = False,
                .CloseOutput = False}
            Dim serializer As New System.Xml.Serialization.XmlSerializer(GetType(ltfsindex.file))
            Dim namespaces As New System.Xml.Serialization.XmlSerializerNamespaces
            namespaces.Add(String.Empty, String.Empty)

            Using output As New IO.FileStream(tempPath, IO.FileMode.CreateNew, IO.FileAccess.Write, IO.FileShare.Read,
                                              1 << 16, IO.FileOptions.SequentialScan)
                Using writer As System.Xml.XmlWriter = System.Xml.XmlWriter.Create(output, settings)
                    writer.WriteStartElement("ltfsindex")
                    writer.WriteAttributeString("version", "2.4.0")
                    writer.WriteStartElement("directory")
                    WriteMergeElement(writer, "name", $"Search_{pattern}")
                    WriteMergeElement(writer, "readonly", False.ToString())
                    writer.WriteStartElement("contents")

                    For Each schemaFile As IO.FileInfo In schemaFiles
                        Try
                            If Not FileContainsText(schemaFile.FullName, pattern, My.Settings.Application_CaseSensitiveSearch) Then Continue For

                            SyncLock infoText
                                infoText.AppendLine(schemaFile.Name)
                            End SyncLock

                            Dim sourceSchema As ltfsindex = ltfsindex.FromSchemaFile(schemaFile.FullName)
                            If sourceSchema Is Nothing OrElse sourceSchema._directory Is Nothing OrElse sourceSchema._directory.Count = 0 Then
                                Throw New IO.InvalidDataException("Schema has no root directory.")
                            End If

                            Dim sourceRoot As ltfsindex.directory = sourceSchema._directory(0)
                            Dim barcode As String = IO.Path.GetFileNameWithoutExtension(schemaFile.Name)
                            For Each rootFile As ltfsindex.file In sourceRoot.EnumerateLazyFiles()
                                WriteMergedFile(writer, rootFile, barcode, serializer, namespaces)
                            Next
                            For Each rootDirectory As ltfsindex.directory In sourceRoot.EnumerateLazyDirectories()
                                WriteMergedDirectory(writer, rootDirectory, barcode, serializer, namespaces)
                            Next
                        Catch ex As Exception
                            LogFileOperationWarning("Merge", schemaFile.FullName, ex)
                        Finally
                            Threading.Interlocked.Increment(progressValue)
                        End Try
                    Next

                    writer.WriteEndElement()
                    writer.WriteEndElement()
                    writer.WriteEndElement()
                End Using
            End Using

            Return ltfsindex.FromSchemaFile(tempPath)
        Finally
            Try
                If IO.File.Exists(tempPath) Then IO.File.Delete(tempPath)
            Catch
            End Try
        End Try
    End Function

    Private Shared Sub NormalizeMergedDirectories(root As ltfsindex.directory)
        If root Is Nothing Then Exit Sub
        If root.HasUnmaterializedLazyContents Then Return

        Dim pending As New Stack(Of ltfsindex.directory)
        pending.Push(root)
        While pending.Count > 0
            Dim current As ltfsindex.directory = pending.Pop()
            Dim byName As New Dictionary(Of String, ltfsindex.directory)(StringComparer.Ordinal)
            For Each child As ltfsindex.directory In current.EnumerateLazyDirectories()
                If child Is Nothing Then Continue For
                If child.name Is Nothing Then child.name = String.Empty

                Dim primary As ltfsindex.directory = Nothing
                If Not byName.TryGetValue(child.name, primary) Then
                    byName.Add(child.name, child)
                    Continue For
                End If

                ' Move only the duplicate directory's direct children.  Each
                ' lazy child is read and attached independently; its subtree
                ' remains in the backing store and is not materialized.
                For Each childFile As ltfsindex.file In child.EnumerateLazyFiles()
                    primary.AddFile(childFile)
                Next
                For Each grandchild As ltfsindex.directory In child.EnumerateLazyDirectories()
                    primary.AddDirectory(grandchild)
                Next
                current.RemoveDirectory(child)
            Next

            For Each child As ltfsindex.directory In current.EnumerateLazyDirectories()
                If child IsNot Nothing Then pending.Push(child)
            Next
        End While
    End Sub

    Private Shared Sub SortMergedRoot(index As ltfsindex)
        If index Is Nothing OrElse index._directory Is Nothing OrElse index._directory.Count = 0 Then Exit Sub
        Dim oldRoot As ltfsindex.directory = index._directory(0)
        If oldRoot Is Nothing Then Exit Sub

        If oldRoot.HasUnmaterializedLazyContents Then
            'The large merge result is already disk-backed.  Sort its index
            'chains externally; this keeps only a small sort run in memory.
            oldRoot.SortChildrenByName(
                Function(left As String, right As String) As Integer
                    Return String.Compare(left, right, StringComparison.Ordinal)
                End Function,
                Function(left As String, right As String) As Integer
                    Return String.Compare(left, right, StringComparison.Ordinal)
                End Function)
            Return
        End If

        'The small/eager merge root owns its child lists.  Sort those lists in
        'place instead of copying every child into two additional lists.
        oldRoot.SortMaterializedChildren(
            Function(left As ltfsindex.file, right As ltfsindex.file) As Integer
                Dim leftBarcode As String = left.GetXAttr("Barcode")
                Dim rightBarcode As String = right.GetXAttr("Barcode")
                If leftBarcode IsNot Nothing AndAlso rightBarcode IsNot Nothing AndAlso leftBarcode <> rightBarcode Then
                    Return String.Compare(leftBarcode, rightBarcode, StringComparison.Ordinal)
                End If
                Return String.Compare(left.name, right.name, StringComparison.Ordinal)
            End Function,
            Function(left As ltfsindex.directory, right As ltfsindex.directory) As Integer
                Return String.Compare(left.name, right.name, StringComparison.Ordinal)
            End Function)
    End Sub

    Private Sub Button2_Click(sender As Object, e As EventArgs) Handles Button2.Click
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(Form1))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "IndexAnalyzer")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "SchemaLoad")
                        Log.Information("Schema load requested by the user. FilePath={FilePath}.", TextBox1.Text)
                    End Using
                End Using
            End Using
        End Using
        LoadSchemaFile()
    End Sub
    Public Sub LoadSchemaFile(Optional ByVal ReloadFile As Boolean = True)
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(Form1))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "IndexAnalyzer")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "SchemaLoad")
                        Log.Information("Schema load started. FilePath={FilePath} ReloadFile={ReloadFile}.", TextBox1.Text, ReloadFile)
                    End Using
                End Using
            End Using
        End Using
        Dim th As New Threading.Thread(
            Sub()
                Dim sortRunPaths As New List(Of String)
                Try
                    Invoke(Sub() Label4.Text = CStr(SchemaLoadText.Items(0)))
                    Invoke(Sub() Label4.Text = CStr(SchemaLoadText.Items(1)))
                    Invoke(Sub() Label4.Text = CStr(SchemaLoadText.Items(2)))

                    If ReloadFile Or schema Is Nothing Then
                        schema = ltfsindex.FromSchemaFile(TextBox1.Text)
                    End If
                    Dim aRuns As New List(Of String)
                    Dim bRuns As New List(Of String)
                    Dim aChunk As New List(Of TapeFileInfo)(TapeSortChunkSize)
                    Dim bChunk As New List(Of TapeFileInfo)(TapeSortChunkSize)
                    Dim total As Long = 0
                    Dim aCount As Long = 0
                    Dim bCount As Long = 0
                    Invoke(Sub() Label4.Text = CStr(SchemaLoadText.Items(3)))
                    If schema IsNot Nothing Then
                        For Each info As TapeFileInfo In EnumerateSchemaTapeFiles(schema)
                            total += 1
                            If info.Partition = ltfsindex.PartitionLabel.a Then
                                aChunk.Add(info)
                                aCount += 1
                                If aChunk.Count >= TapeSortChunkSize Then
                                    Dim runPath As String = CreateTapeSortRun(aChunk)
                                    aRuns.Add(runPath)
                                    sortRunPaths.Add(runPath)
                                End If
                            Else
                                bChunk.Add(info)
                                bCount += 1
                                If bChunk.Count >= TapeSortChunkSize Then
                                    Dim runPath As String = CreateTapeSortRun(bChunk)
                                    bRuns.Add(runPath)
                                    sortRunPaths.Add(runPath)
                                End If
                            End If
                        Next
                    End If
                    If aChunk.Count > 0 Then
                        Dim runPath As String = CreateTapeSortRun(aChunk)
                        aRuns.Add(runPath)
                        sortRunPaths.Add(runPath)
                    End If
                    If bChunk.Count > 0 Then
                        Dim runPath As String = CreateTapeSortRun(bChunk)
                        bRuns.Add(runPath)
                        sortRunPaths.Add(runPath)
                    End If
                    Invoke(Sub() Label4.Text = CStr(SchemaLoadText.Items(4)))
                    SyncLock filelist
                        filelist.Clear()
                    End SyncLock
                    Invoke(Sub() Label4.Text = CStr(SchemaLoadText.Items(5)))
                    Dim counter As Long = 0
                    Dim ran As New Random
                    Dim stepval As Integer = ran.Next(100, 1000)

                    Dim outputChunk As New Text.StringBuilder(64 * 1024)
                    Dim flushOutput As Action =
                        Sub()
                            If outputChunk.Length = 0 Then Return
                            Dim value As String = outputChunk.ToString()
                            outputChunk.Clear()
                            Invoke(Sub() TextBox2.AppendText(value))
                        End Sub
                    Dim fdir As String = TextBox3.Text
                    If fdir.EndsWith("\") Then fdir = fdir.TrimEnd("\"c)
                    fdir &= "\"
                    Dim tdir As String = TextBox4.Text
                    If tdir.EndsWith("\") Then tdir = tdir.TrimEnd("\"c)
                    tdir &= "\"
                    If Not CheckBox1.Checked Then
                        Invoke(Sub() TextBox2.Text = $"Partition{vbTab}Startblock{vbTab}Length{vbTab}Path{vbCrLf}")
                        For Each f As TapeFileInfo In EnumerateTapeSortRuns(aRuns).Concat(EnumerateTapeSortRuns(bRuns))
                            outputChunk.Append(f.Partition.ToString).Append(vbTab).Append(f.BlockNumber).Append(vbTab).Append(f.FileLength).Append(vbTab).Append(f.Path).Append(vbCrLf)
                            counter += 1
                            If counter Mod stepval = 0 Then
                                Invoke(Sub() Label4.Text = $"{SchemaLoadText.Items(5)}{counter}/{total}")
                                stepval = ran.Next(100, 1000)
                            End If
                            If outputChunk.Length >= 64 * 1024 Then flushOutput()
                        Next
                    Else
                        Invoke(Sub() TextBox2.Text = "chcp 65001" & vbCrLf)
                        For Each f As TapeFileInfo In EnumerateTapeSortRuns(aRuns).Concat(EnumerateTapeSortRuns(bRuns))
                            If CheckBox2.Checked Then
                                outputChunk.Append($"echo f|robocopy ""{fdir}{f.Path}"" ""{tdir }{f.Path}"" /Copy:D /MIR /W:10 /R:10 /J{vbCrLf}")
                            Else
                                outputChunk.Append($"echo f|xcopy /J /D /Y ""{fdir}{f.Path}"" ""{tdir }{f.Path}""{vbCrLf}")
                            End If
                            counter += 1
                            If counter Mod stepval = 0 Then
                                Invoke(Sub() Label4.Text = $"{SchemaLoadText.Items(5)}{counter}/{total}")
                                stepval = ran.Next(100, 1000)
                            End If
                            If outputChunk.Length >= 64 * 1024 Then flushOutput()
                        Next
                    End If
                    flushOutput()
                    Invoke(Sub() Label4.Text = CStr(SchemaLoadText.Items(6)))
                    Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(Form1))
                        Using categoryScope As IDisposable = LogContext.PushProperty("Category", "IndexAnalyzer")
                            Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                                Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "SchemaLoad")
                                    Log.Information("Schema load completed. FilePath={FilePath} FileCount={FileCount} PartitionAFileCount={PartitionAFileCount} PartitionBFileCount={PartitionBFileCount}.", TextBox1.Text, total, aCount, bCount)
                                End Using
                            End Using
                        End Using
                    End Using
                Catch ex As Exception
                    Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(Form1))
                        Using categoryScope As IDisposable = LogContext.PushProperty("Category", "IndexAnalyzer")
                            Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                                Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Error")
                                    Log.Error(ex, "Schema load failed. FilePath={FilePath} ReloadFile={ReloadFile}.", TextBox1.Text, ReloadFile)
                                End Using
                            End Using
                        End Using
                    End Using
                    Invoke(Sub() TextBox2.Text = ex.Message)
                Finally
                    For Each runPath As String In sortRunPaths
                        Try
                            If IO.File.Exists(runPath) Then IO.File.Delete(runPath)
                        Catch
                        End Try
                    Next
                End Try
                Invoke(Sub()
                           Button1.Enabled = True
                           Button2.Enabled = True
                           Button3.Enabled = True
                           CheckBox1.Enabled = True
                           TextBox1.Enabled = True
                           TextBox2.Enabled = True
                           TextBox3.Enabled = True
                           TextBox4.Enabled = True
                           Label4.Visible = False
                       End Sub)
            End Sub) With {.IsBackground = True}
        Button1.Enabled = False
        Button2.Enabled = False
        Button3.Enabled = False
        CheckBox1.Enabled = False
        TextBox1.Enabled = False
        TextBox2.Enabled = False
        TextBox3.Enabled = False
        TextBox4.Enabled = False
        Label4.Visible = True
        th.Start()
    End Sub
    Public Function LookforXMLEndPosition(ByRef s As String, ByVal Target As String, ByVal StartPos As String) As Long
        Dim i As Integer = CInt(StartPos)
        Dim TargetBra As String = $"<{Target}>"
        Dim TargetKet As String = $"</{Target}>"
        While i < s.Length - 1
            i += 1
            If s.Substring(i, TargetBra.Length).Equals(TargetBra) Then
                i = CInt(LookforXMLEndPosition(s, Target, CStr(i)))
                Continue While
            End If
            If s.Substring(i, TargetKet.Length).Equals(TargetKet) Then
                Return i
            End If
        End While
        Return i
    End Function

    Private Sub Button1_Click(sender As Object, e As EventArgs) Handles Button1.Click
        If IO.File.Exists(TextBox1.Text) Then
            OpenFileDialog1.FileName = TextBox1.Text
            OpenFileDialog1.InitialDirectory = New IO.FileInfo(TextBox1.Text).DirectoryName
        End If
        If OpenFileDialog1.ShowDialog = DialogResult.OK Then
            TextBox1.Text = OpenFileDialog1.FileName
            Button2_Click(sender, e)
        End If
    End Sub

    Private Iterator Function EnumerateSchemaTapeFiles(index As ltfsindex) As IEnumerable(Of TapeFileInfo)
        If index Is Nothing Then Exit Function
        If index._file IsNot Nothing Then
            For Each rootFile As ltfsindex.file In index._file
                Dim info As TapeFileInfo = CreateTapeFileInfo(rootFile, String.Empty)
                If info IsNot Nothing Then Yield info
            Next
        End If
        If index._directory Is Nothing Then Exit Function
        For Each rootDirectory As ltfsindex.directory In index._directory
            If rootDirectory Is Nothing Then Continue For
            For Each childDirectory As ltfsindex.directory In rootDirectory.EnumerateLazyDirectories()
                For Each info As TapeFileInfo In EnumerateDirectoryTapeFiles(childDirectory, String.Empty)
                    Yield info
                Next
            Next
            For Each childFile As ltfsindex.file In rootDirectory.EnumerateLazyFiles()
                Dim info As TapeFileInfo = CreateTapeFileInfo(childFile, String.Empty)
                If info IsNot Nothing Then Yield info
            Next
        Next
    End Function

    Public Sub ScanFile(ByVal directory As ltfsindex.directory, ByVal flist As List(Of TapeFileInfo), Optional ByVal ParentPath As String = "\")
        If directory Is Nothing OrElse flist Is Nothing Then Exit Sub
        For Each info As TapeFileInfo In EnumerateDirectoryTapeFiles(directory, ParentPath)
            SyncLock flist
                flist.Add(info)
            End SyncLock
        Next
    End Sub

    Private Function CreateTapeFileInfo(f As ltfsindex.file, parentPath As String) As TapeFileInfo
        If f Is Nothing OrElse Not f.Selected Then Return Nothing
        Dim blockNumber As Long = 0
        Dim partition As ltfsindex.PartitionLabel = ltfsindex.PartitionLabel.a
        If f.extentinfo IsNot Nothing AndAlso f.extentinfo.Count > 0 Then
            blockNumber = f.extentinfo(0).startblock
            partition = f.extentinfo(0).partition
        End If
        Return New TapeFileInfo With {
            .BlockNumber = blockNumber,
            .Partition = partition,
            .Path = parentPath & If(f.name, ""),
            .FileLength = f.length}
    End Function

    Private Sub AddTapeFileInfo(f As ltfsindex.file, parentPath As String, flist As List(Of TapeFileInfo))
        Dim info As TapeFileInfo = CreateTapeFileInfo(f, parentPath)
        If info Is Nothing OrElse flist Is Nothing Then Exit Sub
        SyncLock flist
            flist.Add(info)
        End SyncLock
    End Sub

    Private Sub Button3_Click(sender As Object, e As EventArgs) Handles Button3.Click
        If SaveFileDialog1.ShowDialog = DialogResult.OK Then
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(Form1))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "IndexAnalyzer")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                        Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "FileWrite")
                            Log.Information("Generated output write started. FilePath={FilePath}.", SaveFileDialog1.FileName)
                        End Using
                    End Using
                End Using
            End Using
            Try
                IO.File.WriteAllText(SaveFileDialog1.FileName, TextBox2.Text, New Text.UTF8Encoding(False))
                Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(Form1))
                    Using categoryScope As IDisposable = LogContext.PushProperty("Category", "IndexAnalyzer")
                        Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                            Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "FileWrite")
                                Log.Information("Generated output write completed. FilePath={FilePath}.", SaveFileDialog1.FileName)
                            End Using
                        End Using
                    End Using
                End Using
            Catch ex As Exception
                Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(Form1))
                    Using categoryScope As IDisposable = LogContext.PushProperty("Category", "IndexAnalyzer")
                        Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                            Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Error")
                                Log.Error(ex, "Generated output write failed. FilePath={FilePath}.", SaveFileDialog1.FileName)
                            End Using
                        End Using
                    End Using
                End Using
                Throw
            End Try
        End If
    End Sub
    Public LoadComplete As Boolean = False
    Public Sub LoadSetting()
        TextBox1.Text = My.Settings.IndexAnalyzer_LastFile
        TextBox3.Text = My.Settings.IndexAnalyzer_Src
        TextBox4.Text = My.Settings.IndexAnalyzer_Dest
        CheckBox1.Checked = My.Settings.IndexAnalyzer_GenCMD
        Text = $"{FormTitle.Text} - {ApplicationWheels.ApplicationInfo}"
    End Sub
    Private Async Sub Form1_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(Form1))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "IndexAnalyzer")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Lifecycle")
                        Log.Information("Index analyzer window loading.")
                    End Using
                End Using
            End Using
        End Using
        LoadSetting()
        LoadComplete = True
        Await RefreshDeviceList()
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(Form1))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "IndexAnalyzer")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Lifecycle")
                        Log.Information("Index analyzer window loaded.")
                    End Using
                End Using
            End Using
        End Using
    End Sub

    Private Sub Button4_Click(sender As Object, e As EventArgs) Handles Button4.Click
        If schema Is Nothing Then Exit Sub
        Dim schfile As ltfsindex = schema.Clone()
        If FileBrowser.ShowDialog(schfile) = DialogResult.OK Then
            schema = schfile
        End If
        LoadSchemaFile(False)
    End Sub

    Private Sub CheckBox1_CheckedChanged(sender As Object, e As EventArgs) Handles CheckBox1.CheckedChanged
        If Not LoadComplete Then Exit Sub
        LoadSchemaFile(False)
    End Sub

    Private Sub Button5_Click(sender As Object, e As EventArgs) Handles Button5.Click
        If schema Is Nothing Then Exit Sub
        Dim hw As New HashTaskWindow With {.schema = schema, .BaseDirectory = TextBox3.Text, .TargetDirectory = TextBox4.Text}
        hw.Show()
    End Sub

    Private Sub Form1_Closing(sender As Object, e As CancelEventArgs) Handles Me.Closing
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(Form1))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "IndexAnalyzer")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Lifecycle")
                        Log.Information("Index analyzer window closing.")
                    End Using
                End Using
            End Using
        End Using
        My.Settings.IndexAnalyzer_LastFile = TextBox1.Text
        My.Settings.IndexAnalyzer_Src = TextBox3.Text
        My.Settings.IndexAnalyzer_Dest = TextBox4.Text
        My.Settings.IndexAnalyzer_GenCMD = CheckBox1.Checked
        My.Settings.Save()
    End Sub
    Public Class IndexedDirectory
        Public LTFSIndexDir As ltfsindex.directory
        Public IO_Dir As IO.DirectoryInfo
        Public Sub New(index As ltfsindex.directory, dir As IO.DirectoryInfo)
            LTFSIndexDir = index
            IO_Dir = dir
        End Sub
    End Class
    Public Class ldirStack
        Private ldir As New List(Of ltfsindex.directory)
        Public ReadOnly Property IsEmpty As Boolean
            Get
                Return ldir.Count = 0
            End Get
        End Property
        Public Sub Push(v As ltfsindex.directory)
            ldir.Add(v)
        End Sub
        Public Function Pop() As ltfsindex.directory
            Try
                Dim r As ltfsindex.directory = ldir.Last
                ldir.RemoveAt(ldir.Count - 1)
                Return r
            Catch ex As Exception
                Return Nothing
            End Try
        End Function
    End Class
    Private Sub Button6_Click(sender As Object, e As EventArgs) Handles Button6.Click
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(Form1))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "IndexAnalyzer")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "SchemaBuild")
                        Log.Information("Schema generation from directory started. SourceDirectory={SourceDirectory}.", TextBox3.Text)
                    End Using
                End Using
            End Using
        End Using
        Try
            Dim RootDir As IO.DirectoryInfo = New IO.DirectoryInfo(TextBox3.Text)
            Dim fid As Long = 0
            schema = LazySchemaReader.BuildFromDirectory(
                RootDir,
                Function(sourceFile As IO.FileInfo, sequenceNumber As Long) As ltfsindex.file
                    Dim outputFile As New ltfsindex.file With {
                        .name = sourceFile.Name,
                        .length = sourceFile.Length,
                        .extentinfo = New List(Of ltfsindex.file.extent)({
                            New ltfsindex.file.extent With {.startblock = sequenceNumber}})}
                    Try
                        outputFile.creationtime = sourceFile.CreationTimeUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffffff00Z")
                        outputFile.accesstime = sourceFile.LastAccessTimeUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffffff00Z")
                        outputFile.modifytime = sourceFile.LastWriteTimeUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffffff00Z")
                        outputFile.changetime = outputFile.modifytime
                    Catch ex As Exception
                        LogFileOperationWarning("MetadataUpdate", sourceFile.FullName, ex)
                    End Try
                    Return outputFile
                End Function,
                fid)
            LoadSchemaFile(False)
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(Form1))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "IndexAnalyzer")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                        Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "SchemaBuild")
                            Log.Information("Schema generation from directory completed. SourceDirectory={SourceDirectory} FileCount={FileCount}.", TextBox3.Text, fid)
                        End Using
                    End Using
                End Using
            End Using
        Catch ex As Exception
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(Form1))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "IndexAnalyzer")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                        Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Error")
                            Log.Error(ex, "Schema generation from directory failed. SourceDirectory={SourceDirectory}.", TextBox3.Text)
                        End Using
                    End Using
                End Using
            End Using
            MessageBox.Show(New Form With {.TopMost = True}, ex.ToString)
        End Try
    End Sub

    Private Sub Button7_Click(sender As Object, e As EventArgs) Handles Button7.Click
        Dim selectedPath As String = SelectFolder(TextBox3.Text)
        If Not String.IsNullOrEmpty(selectedPath) Then TextBox3.Text = selectedPath
    End Sub

    Private Sub Button8_Click(sender As Object, e As EventArgs) Handles Button8.Click
        Dim selectedPath As String = SelectFolder(TextBox4.Text)
        If Not String.IsNullOrEmpty(selectedPath) Then TextBox4.Text = selectedPath
    End Sub

    Private Sub Button9_Click(sender As Object, e As EventArgs) Handles Button9.Click
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(Form1))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "IndexAnalyzer")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "SchemaConvert")
                        Log.Information("Schema conversion started. DirectoryPath={DirectoryPath}.", TextBox1.Text)
                    End Using
                End Using
            End Using
        End Using
        Try
            Dim f() As IO.FileInfo = New IO.DirectoryInfo(TextBox1.Text).GetFiles("*.schema")
            For Each fl As IO.FileInfo In f
                schema = ltfsindex.FromSchemaFile(fl.FullName)
                schema.SaveFile(fl.FullName)
                TextBox2.AppendText(fl.FullName & vbCrLf)
            Next
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(Form1))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "IndexAnalyzer")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                        Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "SchemaConvert")
                            Log.Information("Schema conversion completed. DirectoryPath={DirectoryPath} FileCount={FileCount}.", TextBox1.Text, f.Length)
                        End Using
                    End Using
                End Using
            End Using
        Catch ex As Exception
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(Form1))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "IndexAnalyzer")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                        Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Error")
                            Log.Error(ex, "Schema conversion failed. DirectoryPath={DirectoryPath}.", TextBox1.Text)
                        End Using
                    End Using
                End Using
            End Using
            MessageBox.Show(New Form With {.TopMost = True}, ex.ToString)
        End Try

    End Sub

    Private Sub Form1_Click(sender As Object, e As EventArgs) Handles Me.Click
        Static q As Integer
        q += 1
        If q >= 10 Then
            Button9.Visible = True
        End If
    End Sub

    Private Sub Button10_Click(sender As Object, e As EventArgs) Handles Button10.Click
        ShowConfigurator()
    End Sub

    Private Sub 查找ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 查找ToolStripMenuItem.Click
        Dim patt As String = ""
        If DisplayHelper.ShowInputDialog("Search kw", "Search", patt) <> DialogResult.OK Then Exit Sub
        If patt <> "" Then
            Enabled = False
            Dim dir As String = TextBox1.Text.Substring(0, TextBox1.Text.LastIndexOf("\"))
            Dim result As New Text.StringBuilder

            If Not IO.Directory.Exists(dir) Then Exit Sub
            Dim f() As IO.FileInfo = New IO.DirectoryInfo(dir).GetFiles("*.schema")
            Dim progmax As Integer = f.Length
            Dim progval As Integer = 0
            Dim th As New Threading.Thread(
                Sub()
                    Parallel.ForEach(Of IO.FileInfo)(f,
                        Sub(fl As IO.FileInfo)
                            Try
                                If FileContainsText(fl.FullName, patt, My.Settings.Application_CaseSensitiveSearch) Then
                                    SyncLock result
                                        result.AppendLine(fl.Name)
                                    End SyncLock
                                End If
                            Catch ex As Exception
                                LogFileOperationWarning("Search", fl.FullName, ex)
                            End Try
                            Threading.Interlocked.Increment(progval)
                        End Sub)
                    Invoke(Sub() Enabled = True)
                End Sub) With {.IsBackground = True}
            Dim thprog As New Threading.Thread(
                Sub()
                    While True
                        Threading.Thread.Sleep(200)
                        Dim exitflag As Boolean = (progval >= progmax)
                        Invoke(
                            Sub()
                                TextBox2.Text = "Search for " & patt & " in file "
                                TextBox2.AppendText(progval & "/" & progmax & vbCrLf)
                                SyncLock result
                                    TextBox2.AppendText(result.ToString)
                                End SyncLock
                            End Sub)
                        If exitflag Then Exit While
                    End While
                End Sub) With {.IsBackground = True}
            th.Start()
            thprog.Start()
        End If
    End Sub

    Private Sub 错误检查ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 错误检查ToolStripMenuItem.Click
        Enabled = False
        Dim dir As String = TextBox1.Text.Substring(0, TextBox1.Text.LastIndexOf("\"))
        Dim result As New Text.StringBuilder

        If Not IO.Directory.Exists(dir) Then Exit Sub
        Dim f() As IO.FileInfo = New IO.DirectoryInfo(dir).GetFiles("*.schema")
        Dim progmax As Integer = f.Length
        Dim progval As Integer = 0

        Dim th As New Threading.Thread(
            Sub()
                    Parallel.ForEach(f,
                        Sub(fl As IO.FileInfo)
                            Dim extentRuns As New List(Of String)
                            Try
                                Dim extentChunk As New List(Of TapeExtentInfo)(TapeSortChunkSize)
                                Dim sch As ltfsindex = ltfsindex.FromSchemaFile(fl.FullName)
                                Dim collectExtents As Action(Of ltfsindex.file) =
                                    Sub(file As ltfsindex.file)
                                        If file Is Nothing OrElse file.extentinfo Is Nothing Then Return
                                        For Each ext As ltfsindex.file.extent In file.extentinfo
                                            If ext Is Nothing Then Continue For
                                            extentChunk.Add(New TapeExtentInfo With {
                                                .StartBlock = ext.startblock,
                                                .ByteOffset = ext.byteoffset,
                                                .ByteCount = ext.bytecount,
                                                .FileUid = file.fileuid})
                                            If extentChunk.Count >= TapeSortChunkSize Then
                                                extentRuns.Add(CreateExtentSortRun(extentChunk))
                                            End If
                                        Next
                                    End Sub
                                If sch IsNot Nothing AndAlso sch._file IsNot Nothing Then
                                    For Each rootFile As ltfsindex.file In sch._file
                                        collectExtents(rootFile)
                                    Next
                                End If
                                ltfsindex.WSort(sch._directory,
                                            collectExtents, Nothing)
                                If extentChunk.Count > 0 Then extentRuns.Add(CreateExtentSortRun(extentChunk))

                                Dim previous As TapeExtentInfo = Nothing
                                For Each current As TapeExtentInfo In EnumerateExtentSortRuns(extentRuns)
                                    If previous IsNot Nothing AndAlso
                                       ExtentPhysicalStart(current) < ExtentPhysicalStart(previous) + CDec(previous.ByteCount) Then
                                        SyncLock result
                                            result.AppendLine($"Error with {fl.Name}: fid {current.FileUid}")
                                        End SyncLock
                                    End If
                                    previous = current
                                Next
                            Catch ex As Exception
                                LogFileOperationWarning("ExtentCheck", fl.FullName, ex)
                                result.Append(ex.ToString)
                            Finally
                                For Each runPath As String In extentRuns
                                    Try
                                        If IO.File.Exists(runPath) Then IO.File.Delete(runPath)
                                    Catch
                                    End Try
                                Next
                            End Try
                        Threading.Interlocked.Increment(progval)
                    End Sub)
                Invoke(Sub() Enabled = True)
            End Sub) With {.IsBackground = True}
        Dim thprog As New Threading.Thread(
            Sub()
                While True
                    Threading.Thread.Sleep(200)
                    Dim exitflag As Boolean = (progval >= progmax)
                    Invoke(
                        Sub()
                            TextBox2.Text = "Checking files..."
                            TextBox2.AppendText(progval & "/" & progmax & vbCrLf)
                            SyncLock result
                                TextBox2.AppendText(result.ToString)
                            End SyncLock
                        End Sub)
                    If exitflag Then Exit While
                End While
            End Sub) With {.IsBackground = True}
        th.Start()
        thprog.Start()
    End Sub

    Private Sub 合并文件ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 合并文件ToolStripMenuItem.Click
        Dim patt As String = ""
        If DisplayHelper.ShowInputDialog("Search kw", "Search", patt) <> DialogResult.OK Then Exit Sub
        If patt <> "" Then
            Enabled = False
            Dim dir As String = TextBox1.Text.Substring(0, TextBox1.Text.LastIndexOf("\"))
            Dim infoText As New Text.StringBuilder
            If Not IO.Directory.Exists(dir) Then Exit Sub
            Dim f() As IO.FileInfo = New IO.DirectoryInfo(dir).GetFiles("*.schema")
            Dim progmax As Integer = f.Length
            Dim progval As Integer = 0
            Dim mergedSchema As ltfsindex = Nothing
            Dim th As New Threading.Thread(
                Sub()
                    Try
                        mergedSchema = BuildMergedSchema(f, patt, infoText, progval)
                        If mergedSchema IsNot Nothing AndAlso mergedSchema._directory IsNot Nothing AndAlso mergedSchema._directory.Count > 0 Then
                            NormalizeMergedDirectories(mergedSchema._directory(0))
                            SortMergedRoot(mergedSchema)
                        End If
                        schema = mergedSchema
                    Catch ex As Exception
                        LogFileOperationWarning("Merge", TextBox1.Text, ex)
                    Finally
                        Invoke(Sub() Enabled = True)
                    End Try
                End Sub) With {.IsBackground = True}
            Dim thprog As New Threading.Thread(
                Sub()
                    While True
                        Threading.Thread.Sleep(200)
                        Dim exitflag As Boolean = (progval >= progmax)
                        Invoke(
                            Sub()
                                TextBox2.Text = "Search for " & patt & " in file "
                                TextBox2.AppendText(progval & "/" & progmax & vbCrLf)
                                SyncLock infoText
                                    TextBox2.AppendText(infoText.ToString)
                                End SyncLock
                            End Sub)
                        If exitflag Then Exit While
                    End While
                End Sub) With {.IsBackground = True}
            th.Start()
            thprog.Start()
        End If
    End Sub

    Private Sub 未校验检查ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 未校验检查ToolStripMenuItem.Click
        Dim patt As String = ""
        If DisplayHelper.ShowInputDialog("Search kw", "Search", patt) <> DialogResult.OK Then Exit Sub
        If patt <> "" Then
            Enabled = False
            Dim dir As String = TextBox1.Text.Substring(0, TextBox1.Text.LastIndexOf("\"))
            Dim infoText As New Text.StringBuilder
            If Not IO.Directory.Exists(dir) Then Exit Sub
            Dim f() As IO.FileInfo = New IO.DirectoryInfo(dir).GetFiles("*.schema")
            Dim progmax As Integer = f.Length
            Dim progval As Integer = 0
            Dim th As New Threading.Thread(
                Sub()
                    Parallel.ForEach(Of IO.FileInfo)(f,
                        Sub(fl As IO.FileInfo)
                            Try
                                If FileContainsText(fl.FullName, patt, My.Settings.Application_CaseSensitiveSearch) Then
                                    Dim UNum As Integer = 0
                                    Dim result As New Text.StringBuilder
                                    result.AppendLine(fl.Name)
                                    Dim rsch As ltfsindex = ltfsindex.FromSchemaFile(fl.FullName)
                                    Dim q As New Stack(Of ltfsindex.directory)
                                    If rsch IsNot Nothing AndAlso rsch._directory IsNot Nothing Then
                                        For i As Integer = rsch._directory.Count - 1 To 0 Step -1
                                            q.Push(rsch._directory(i))
                                        Next
                                    End If
                                    While q.Count > 0
                                        Dim d As ltfsindex.directory = q.Pop()
                                        For Each f2 As ltfsindex.file In d.EnumerateLazyFiles()
                                            If f2.sha1.Length <> 40 Then
                                                result.AppendLine($"--[{f2.fileuid}]{f2.name}")
                                                Threading.Interlocked.Increment(UNum)
                                            End If
                                        Next
                                        For Each childDirectory As ltfsindex.directory In d.EnumerateLazyDirectories()
                                            q.Push(childDirectory)
                                        Next
                                    End While
                                    If UNum = 0 Then Exit Try
                                    SyncLock infoText
                                        infoText.AppendLine(result.ToString())
                                    End SyncLock
                                End If
                            Catch ex As Exception
                                LogFileOperationWarning("UnverifiedCheck", fl.FullName, ex)
                            End Try
                            Threading.Interlocked.Increment(progval)
                        End Sub)
                    Invoke(Sub() Enabled = True)
                End Sub) With {.IsBackground = True}
            Dim thprog As New Threading.Thread(
                Sub()
                    While True
                        Threading.Thread.Sleep(200)
                        Dim exitflag As Boolean = (progval >= progmax)
                        Invoke(
                            Sub()
                                TextBox2.Text = "Search for " & patt & " in file "
                                TextBox2.AppendText(progval & "/" & progmax & vbCrLf)
                                SyncLock infoText
                                    TextBox2.AppendText(infoText.ToString)
                                End SyncLock
                            End Sub)
                        If exitflag Then Exit While
                    End While
                End Sub) With {.IsBackground = True}
            th.Start()
            thprog.Start()
        End If
    End Sub

    Private Sub 查看ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 查看ToolStripMenuItem.Click
        Dim LWF As New LTFSWriter With {.Barcode = "BROWSE", .TapeDrive = "", .OfflineMode = True}
        Dim OnLWFLoad As New EventHandler(Sub()
                                              LWF.Invoke(Sub()
                                                             LWF.schema = schema
                                                             LWF.ShowXAttr_Barcode = True
                                                             LWF.RefreshDisplay()
                                                             LWF.ToolStripStatusLabel1.Text = "BROWSE"
                                                         End Sub)
                                              RemoveHandler LWF.Load, OnLWFLoad
                                          End Sub
            )
        AddHandler LWF.Load, OnLWFLoad
        LWF.Show()
    End Sub
    <TypeConverter(GetType(ListTypeDescriptor(Of List(Of TapeUtils.BlockDevice), TapeUtils.BlockDevice)))>
    Dim DevList As List(Of TapeUtils.BlockDevice)
    Private _deviceScanInProgress As Boolean
    Public Async Function RefreshDeviceList() As Task
        If _deviceScanInProgress Then
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(Form1))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Device")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                        Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "DeviceRefresh")
                            Log.Warning("Tape device refresh was skipped because another scan is already running.")
                        End Using
                    End Using
                End Using
            End Using
            Return
        End If

        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(Form1))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Device")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "DeviceRefresh")
                        Log.Information("Tape device refresh started.")
                    End Using
                End Using
            End Using
        End Using
        _deviceScanInProgress = True
        LoadComplete = False
        Button11.Enabled = False

        Dim lastIndex As Integer = ComboBox1.SelectedIndex
        Dim scanSucceeded As Boolean = False
        Try
            ' Device enumeration performs SetupAPI, device opens and SCSI Inquiry calls.
            ' Keep all of that work off the UI thread so the first form can paint.
            Dim scannedDevices As List(Of TapeUtils.BlockDevice) = Await Task.Run(
                Function() TapeUtils.GetTapeDriveList())

            If IsDisposed OrElse Disposing Then Return

            ComboBox1.BeginUpdate()
            Try
                ComboBox1.Items.Clear()
                DevList = scannedDevices
                For Each D As TapeUtils.BlockDevice In DevList
                    ComboBox1.Items.Add(D.ToString())
                Next
                ComboBox1.SelectedIndex = Math.Min(ComboBox1.Items.Count - 1, Math.Max(0, lastIndex))
                If ComboBox1.SelectedIndex >= 0 Then
                    Button27.Enabled = (DevList(ComboBox1.SelectedIndex).DriveLetter.Length = 0)
                End If
            Finally
                ComboBox1.EndUpdate()
            End Try
            scanSucceeded = True
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(Form1))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Device")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                        Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "DeviceRefresh")
                            Log.Information("Tape device refresh completed. DeviceCount={DeviceCount}.", scannedDevices.Count)
                        End Using
                    End Using
                End Using
            End Using
        Catch ex As Exception
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(Form1))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Device")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                        Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Error")
                            Log.Error(ex, "Tape device refresh failed.")
                        End Using
                    End Using
                End Using
            End Using
            If Not IsDisposed AndAlso Not Disposing Then
                MessageBox.Show(New Form With {.TopMost = True}, ex.ToString(), My.Resources.ResText_Warning)
            End If
        Finally
            _deviceScanInProgress = False
            LoadComplete = True
            If Not IsDisposed AndAlso Not Disposing Then Button11.Enabled = True
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(Form1))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Device")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                        Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "DeviceRefresh")
                            Log.Information("Tape device refresh finished. Succeeded={Succeeded}.", scanSucceeded)
                        End Using
                    End Using
                End Using
            End Using
        End Try
    End Function
    Private Async Sub Button11_Click(sender As Object, e As EventArgs) Handles Button11.Click
        If _deviceScanInProgress Then Exit Sub
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(Form1))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Device")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "DeviceRefresh")
                        Log.Information("Tape device refresh requested by the user.")
                    End Using
                End Using
            End Using
        End Using
        If Not IsAdministrator Then
            If MessageBox.Show(New Form With {.TopMost = True}, My.Resources.ResText_UACConfirm, My.Resources.ResText_Warning, MessageBoxButtons.OKCancel) = DialogResult.Cancel Then Exit Sub
            If StartElevated() Then
                Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(Form1))
                    Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Device")
                        Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                            Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "PrivilegeCheck")
                                Log.Information("Device refresh requested elevation; the current process will exit after elevation starts.")
                            End Using
                        End Using
                    End Using
                End Using
                ExitCurrentProcess()
            End If
            Exit Sub
        End If
        Await RefreshDeviceList()
    End Sub

    Private Sub Button12_Click(sender As Object, e As EventArgs) Handles Button12.Click
        TabControl1.SelectedIndex = 1
    End Sub

    Private Sub Button13_Click(sender As Object, e As EventArgs) Handles Button13.Click
        ShowTapeCopy()
    End Sub

    Private Sub Button14_Click(sender As Object, e As EventArgs) Handles Button14.Click
        ShowChangerTool()
    End Sub

    Private Async Sub Button27_Click(sender As Object, e As EventArgs) Handles Button27.Click
        If Not LoadComplete OrElse _deviceScanInProgress Then Exit Sub
        If DevList IsNot Nothing AndAlso DevList.Count > 0 AndAlso ComboBox1.SelectedIndex >= 0 Then
            Await RefreshDeviceList()
            If Button27.Enabled = False Then Exit Sub
            Dim device As TapeUtils.BlockDevice = DevList(ComboBox1.SelectedIndex)
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(Form1))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Navigation")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                        Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "WindowOpen")
                            Log.Information("Writer navigation requested from the index analyzer. DevicePath={DevicePath}.", device.DevicePath)
                        End Using
                    End Using
                End Using
            End Using
            TapeUtils.CheckSwitchConfig(device)
            My.Settings.Save()
            ShowWriter(device.DevicePath)
        End If
    End Sub

    Private Sub ComboBox1_SelectedIndexChanged(sender As Object, e As EventArgs) Handles ComboBox1.SelectedIndexChanged
        If Not LoadComplete Then Exit Sub
        If ComboBox1.SelectedIndex >= 0 Then
            If DevList(ComboBox1.SelectedIndex).DriveLetter.Length > 0 Then
                Button27.Enabled = False
            Else
                Button27.Enabled = True
            End If
        End If
    End Sub

    Private Sub Button15_Click(sender As Object, e As EventArgs) Handles Button15.Click
        Dim nws As New IOManager.NetworkCommand
        Dim result As IOManager.NetworkCommand
        Dim addr As New Net.IPAddress(0)
        Net.IPAddress.TryParse("127.0.0.1", addr)
        nws.CommandType = IOManager.NetworkCommand.CommandTypeDef.General
        nws.HashCode = 0
        nws.PayLoad.Clear()
        nws.PayLoad.Add(Guid.Empty.ToByteArray())
        nws.PayLoad.Add(System.Text.Encoding.UTF8.GetBytes("ltfswriter"))
        result = nws.SendTo(addr, 25900)
        Dim frmID As New Guid(result.PayLoad(0))
        nws.HashCode = 1
        nws.PayLoad.Clear()
        nws.PayLoad.Add(frmID.ToByteArray())
        nws.PayLoad.Add(System.Text.Encoding.UTF8.GetBytes("ltfswriter"))
        nws.PayLoad.Add(System.Text.Encoding.UTF8.GetBytes("-t"))
        nws.PayLoad.Add(System.Text.Encoding.UTF8.GetBytes("0"))
        result = nws.SendTo(addr, 25900)
        MessageBox.Show(System.Text.Encoding.UTF8.GetString(result.PayLoad(0)))
        nws.HashCode = 2
        nws.PayLoad.Clear()
        nws.PayLoad.Add(frmID.ToByteArray())
        nws.PayLoad.Add(System.Text.Encoding.UTF8.GetBytes("ltfswriter"))
        nws.PayLoad.Add(System.Text.Encoding.UTF8.GetBytes("show"))
        result = nws.SendTo(addr, 25900)
        MessageBox.Show(System.Text.Encoding.UTF8.GetString(result.PayLoad(0)))

        nws.HashCode = 3
        nws.PayLoad.Clear()
        nws.PayLoad.Add(frmID.ToByteArray())
        nws.PayLoad.Add(System.Text.Encoding.UTF8.GetBytes("ltfswriter"))
        nws.PayLoad.Add(System.Text.Encoding.UTF8.GetBytes("gettext"))
        nws.PayLoad.Add(System.Text.Encoding.UTF8.GetBytes(""))
        result = nws.SendTo(addr, 25900)
        MessageBox.Show(System.Text.Encoding.UTF8.GetString(result.PayLoad(0)))
        MessageBox.Show(System.Text.Encoding.UTF8.GetString(result.PayLoad(1)))

    End Sub

    Public Sub Button16_Click(sender As Object, e As EventArgs) Handles Button16.Click
        Dim SP1 As New SettingPanel
        SP1.PropertyGrid1.SelectedObject = My.MySettings.Default
        SP1.MenuStrip1.Visible = True
        SP1.PropertyGrid1.Top += SP1.MenuStrip1.Height
        SP1.PropertyGrid1.Height -= SP1.MenuStrip1.Height
        If SP1.ShowDialog() = DialogResult.OK Then
            LoadSetting()
            My.Settings.Save()
        End If
    End Sub
End Class
