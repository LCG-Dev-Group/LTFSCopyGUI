Imports System.ComponentModel
Imports System.IO
Imports System.Security.Cryptography
Imports System.Threading
Imports Blake3
Imports System.IO.Hashing
Imports System.Runtime.InteropServices
Imports NAudio.Wave
Imports System.Text
Imports System.Globalization
Imports System.Xml.Serialization
Imports System.Buffers
Imports System.Text.RegularExpressions
Imports LTFSCopyGUI.Native

<TypeConverter(GetType(ExpandableObjectConverter))>
Public Class IOManager
    Public Shared PublicArrayPool As ArrayPool(Of Byte) = ArrayPool(Of Byte).Create(16777216, 2048)

    Private Shared Function ResolveReadablePath(filename As String) As String
        Return FileSystemPathResolver.ResolveExistingFilePath(filename)
    End Function

    <TypeConverter(GetType(ExpandableObjectConverter))>
    Public Class fsReport
        Public fs As BufferedStream

        Public Sub New()
        End Sub

        Public Sub New(fst As BufferedStream)
            fs = fst
        End Sub
    End Class

    Public Event ErrorOccured(s As String)

    Public Shared Function FormatSize(byteCount As Long, Optional ByVal More As Boolean = False) As String
        Dim sym As String = If(byteCount >= 0, "", "-")
        byteCount = Math.Abs(byteCount)
        If My.Settings.Application_UseDecimalUnit Then
            If byteCount < 1000 Then
                Return sym & byteCount & " Bytes"
            ElseIf byteCount < 1000 ^ 2 Then
                Return sym & (byteCount / 1000).ToString("F2") & " KB"
            ElseIf byteCount < 1000 ^ 3 Then
                Return sym & (byteCount / 1000 ^ 2).ToString("F2") & " MB"
            ElseIf Not More OrElse byteCount < 1000 ^ 4 Then
                Return sym & (byteCount / 1000 ^ 3).ToString("F2") & " GB"
            ElseIf byteCount < 1000 ^ 5 Then
                Return sym & (byteCount / 1000 ^ 4).ToString("F2") & " TB"
            Else
                Return sym & (byteCount / 1000 ^ 5).ToString("F2") & " PB"
            End If
        Else
            If byteCount < 1024 Then
                Return sym & byteCount & " Bytes"
            ElseIf byteCount < 1024 ^ 2 Then
                Return sym & (byteCount / 1024).ToString("F2") & " KiB"
            ElseIf byteCount < 1024 ^ 3 Then
                Return sym & (byteCount / 1024 ^ 2).ToString("F2") & " MiB"
            ElseIf Not More OrElse byteCount < 1024 ^ 4 Then
                Return sym & (byteCount / 1024 ^ 3).ToString("F2") & " GiB"
            ElseIf byteCount < 1024 ^ 5 Then
                Return sym & (byteCount / 1024 ^ 4).ToString("F2") & " TiB"
            Else
                Return sym & (byteCount / 1024 ^ 5).ToString("F2") & " PiB"
            End If
        End If


    End Function
    Public Shared Function ExtractNumericString(input As String) As String
        If String.IsNullOrEmpty(input) Then Return String.Empty
        Dim m As Match = Regex.Match(input, "\d*\.?\d+")

        If m.Success Then
            Return m.Value
        Else
            Return String.Empty
        End If
    End Function

    Public Shared Function HexStringToByteArray(s As String) As Byte()
        s = s.ToUpper
        Dim dataList As New List(Of Byte)
        Dim charbuffer As String = ""
        Dim allowedChar() As String = {"0", "1", "2", "3", "4", "5", "6", "7", "8", "9", "A", "B", "C", "D", "E", "F"}
        For i As Integer = 0 To s.Length - 1
            If allowedChar.Contains(s(i)) Then
                charbuffer &= s(i)
            End If
            If charbuffer.Length = 2 Or (charbuffer.Length = 1 And s(i) = " ") Then
                dataList.Add(Convert.ToByte(charbuffer, 16))
                charbuffer = ""
            End If
        Next
        Return dataList.ToArray()
    End Function
    Public Shared Function Byte2Hex(bytes As Byte(), Optional ByVal TextShow As Boolean = False) As String
        Const HalfWidthChars As String = "~!@#$%^&*()_+-=|\ <>?,./:;""''{}[]0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz"
        If bytes Is Nothing Then Return ""
        If bytes.Length = 0 Then Return ""
        Dim sb As New StringBuilder
        Dim tb As String = ""
        Dim ln As New StringBuilder
        For i As Integer = 0 To bytes.Length - 1
            If i Mod 16 = 0 AndAlso TextShow Then
                ln.Append("|" & Hex(i).PadLeft(5) & "h: ")
            End If
            ln.Append(Convert.ToString((bytes(i) And &HFF) + &H100, 16).Substring(1).ToUpper)
            ln.Append(" ")
            Dim c As Char = Chr(bytes(i))
            If Not HalfWidthChars.Contains(c) Then
                tb &= "."
            Else
                tb &= c
            End If
            If i Mod 16 = 15 Then
                If TextShow Then
                    ln.Append(tb)
                End If
                sb.Append(ln.ToString().PadRight(74) & "|")
                sb.Append(vbCrLf)
                ln = New StringBuilder()
                tb = ""
            End If
        Next
        If TextShow And tb <> "" Then
            ln.Append(tb)
        End If
        If ln.Length > 0 Then sb.Append(ln.ToString().PadRight(74) & "|")
        Return sb.ToString()
    End Function

    Public Shared Function SHA1(filename As String, LogFile As String()) As String
        If LogFile.Contains("[hash] " & filename) Then

            Return LogFile(Array.IndexOf(LogFile, LogFile.First(Function(s As String) As Boolean
                                                                    Return s = "[hash] " & filename
                                                                End Function)) + 1).TrimStart(" "c).Substring(0, 40)
        End If
        Return ""
    End Function

    Public Shared Function SHA1(filename As String, Optional ByVal OnFinished As Action(Of String) = Nothing,
                                Optional ByVal fs As fsReport = Nothing,
                                Optional ByVal OnFileReading As _
                                   Action(Of EventedStream.ReadStreamEventArgs, EventedStream) = Nothing) As String
        If OnFinished Is Nothing Then

            Using _
                fsin0 As FileStream = File.Open(ResolveReadablePath(filename), FileMode.Open, FileAccess.Read, FileShare.Read)
                Dim fsinb As New BufferedStream(fsin0, 512 * 1024)
                Dim fsine As New EventedStream With {.baseStream = fsinb}
                If OnFileReading IsNot Nothing Then _
                    AddHandler fsine.Readed, Sub(args As EventedStream.ReadStreamEventArgs) OnFileReading(args, fsine)
                'Dim fsin As New IO.BufferedStream(fsine, 512 * 1024)
                Dim fsin As New BufferedStream(fsine, 512 * 1024)
                If fs IsNot Nothing Then fs.fs = fsin
                Using algo As SHA1 = Security.Cryptography.SHA1.Create()
                    fsin.Position = 0
                    Dim hashValue() As Byte

                    hashValue = algo.ComputeHash(fsine)
                    'While fsin.Read(block, 0, block.Length) > 0
                    '
                    'End While
                    fsin.Close()
                    Dim result As New StringBuilder()
                    For i As Integer = 0 To hashValue.Length - 1
                        result.Append(String.Format("{0:X2}", hashValue(i)))
                    Next
                    Return result.ToString()
                End Using
            End Using
        Else
            Dim resolvedFilename As String = ResolveReadablePath(filename)
            Dim thHash As New Thread(
                Sub()
                    Using fsin0 As FileStream = File.Open(resolvedFilename, FileMode.Open, FileAccess.Read)
                        Dim fsinb As New BufferedStream(fsin0, 512 * 1024)
                        Dim fsine As New EventedStream With {.baseStream = fsinb}
                        AddHandler fsine.Readed,
                                                  Sub(args As EventedStream.ReadStreamEventArgs) _
                                                  OnFileReading(args, fsine)
                        'Dim fsin As New IO.BufferedStream(fsine, 512 * 1024)
                        Dim fsin As New BufferedStream(fsine, 512 * 1024)
                        If fs IsNot Nothing Then fs.fs = fsin
                        Using algo As SHA1 = Security.Cryptography.SHA1.Create()
                            fsin.Position = 0
                            Dim hashValue() As Byte

                            hashValue = algo.ComputeHash(fsine)
                            'While fsin.Read(block, 0, block.Length) > 0
                            '
                            'End While
                            fsin.Close()
                            Dim result As New StringBuilder()
                            For i As Integer = 0 To hashValue.Length - 1
                                result.Append(String.Format("{0:X2}", hashValue(i)))
                            Next
                            OnFinished(result.ToString)
                        End Using
                    End Using
                End Sub)
            thHash.Start()
            Return ""
        End If
    End Function
    Public Shared Function GetSHA256(filename As String) As String
        Dim hashValue() As Byte
        Dim hasher As SHA256 = SHA256.Create()
        Using fshash As New FileStream(ResolveReadablePath(filename), FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous Or FileOptions.SequentialScan)
            hashValue = hasher.ComputeHash(fshash)
        End Using
        hasher.Dispose()
        Return BitConverter.ToString(hashValue).Replace("-", "").ToUpper()
    End Function
    Public Shared Function GetSHA512(filename As String) As String
        Dim hashValue() As Byte
        Dim hasher As SHA512 = SHA512.Create()
        Using fshash As New FileStream(ResolveReadablePath(filename), FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous Or FileOptions.SequentialScan)
            hashValue = hasher.ComputeHash(fshash)
        End Using
        hasher.Dispose()
        Return BitConverter.ToString(hashValue).Replace("-", "").ToUpper()
    End Function
    Public Shared Function GetMD5(filename As String) As String
        Dim hashValue() As Byte
        Dim hasher As MD5 = MD5.Create()
        Using fshash As New FileStream(ResolveReadablePath(filename), FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous Or FileOptions.SequentialScan)
            hashValue = hasher.ComputeHash(fshash)
        End Using
        hasher.Dispose()
        Return BitConverter.ToString(hashValue).Replace("-", "").ToUpper()
    End Function
    Public Shared Function GetBlake3(filename As String) As String
        Dim hasher As Hasher = Hasher.NewInstance()
        Dim pool As ArrayPool(Of Byte) = ArrayPool(Of Byte).Create(16, 16)
        Dim block() As Byte = pool.Rent(8388608)
        Try
            Using fshash As New FileStream(ResolveReadablePath(filename), FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous Or FileOptions.SequentialScan)
                Dim readed As Integer = Integer.MaxValue
                While readed > 0
                    readed = fshash.Read(block, 0, block.Length)
                    If readed = block.Length Then
                        hasher.UpdateWithJoin(block)
                    Else
                        Dim seg As New ArraySegment(Of Byte)(block, 0, readed)
                        hasher.UpdateWithJoin(seg)
                    End If
                End While
                Dim resultb3 As Hash = hasher.Finalize()
                hasher.Dispose()
                Return resultb3.ToString().ToUpper()
            End Using
        Finally
            pool.Return(block)
        End Try
    End Function
    Public Shared Function GetCRC32(filename As String) As String
        Dim hasher As New Crc32
        Dim pool As ArrayPool(Of Byte) = ArrayPool(Of Byte).Create(16, 16)
        Dim block() As Byte = pool.Rent(8388608)
        Try
            Using fshash As New FileStream(ResolveReadablePath(filename), FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous Or FileOptions.SequentialScan)
                Dim readed As Integer = Integer.MaxValue
                While readed > 0
                    readed = fshash.Read(block, 0, block.Length)
                    If readed = block.Length Then
                        hasher.Append(block)
                    Else
                        Dim seg As New ArraySegment(Of Byte)(block, 0, readed)
                        hasher.Append(seg)
                    End If
                End While
                Dim resultXxHash As Byte() = hasher.GetHashAndReset()
                Return BitConverter.ToString(resultXxHash).Replace("-", "").ToUpper()
            End Using
        Finally
            pool.Return(block)
        End Try
    End Function
    Public Shared Function GetXxHash3(filename As String) As String
        Dim hasher As New XxHash3
        Dim pool As ArrayPool(Of Byte) = ArrayPool(Of Byte).Create(16, 16)
        Dim block() As Byte = pool.Rent(8388608)
        Try
            Using fshash As New FileStream(ResolveReadablePath(filename), FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous Or FileOptions.SequentialScan)
                Dim readed As Integer = Integer.MaxValue
                While readed > 0
                    readed = fshash.Read(block, 0, block.Length)
                    If readed = block.Length Then
                        hasher.Append(block)
                    Else
                        Dim seg As New ArraySegment(Of Byte)(block, 0, readed)
                        hasher.Append(seg)
                    End If
                End While
                Dim resultXxHash As Byte() = hasher.GetHashAndReset()
                Return BitConverter.ToString(resultXxHash).Replace("-", "").ToUpper()
            End Using
        Finally
            pool.Return(block)
        End Try
    End Function
    Public Shared Function GetXxHash128(filename As String) As String
        Dim hasher As New XxHash128
        Dim pool As ArrayPool(Of Byte) = ArrayPool(Of Byte).Create(16, 16)
        Dim block() As Byte = pool.Rent(8388608)
        Try
            Using fshash As New FileStream(ResolveReadablePath(filename), FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous Or FileOptions.SequentialScan)
                Dim readed As Integer = Integer.MaxValue
                While readed > 0
                    readed = fshash.Read(block, 0, block.Length)
                    If readed = block.Length Then
                        hasher.Append(block)
                    Else
                        Dim seg As New ArraySegment(Of Byte)(block, 0, readed)
                        hasher.Append(seg)
                    End If
                End While
                Dim resultXxHash As Byte() = hasher.GetHashAndReset()
                Return BitConverter.ToString(resultXxHash).Replace("-", "").ToUpper()
            End Using
        Finally
            pool.Return(block)
        End Try
    End Function

    Public Shared Function ChecksumEquals(a As ltfsindex.file, b As ltfsindex.file, checksumtype As ltfsindex.file.xattr.HashType.Available) As Boolean
        Dim csa As String = Nothing, csb As String = Nothing
        Dim len As Integer
        Select Case checksumtype
            Case ltfsindex.file.xattr.HashType.Available.SHA1
                csa = a.GetXAttr(ltfsindex.file.xattr.HashType.SHA1)
                csb = b.GetXAttr(ltfsindex.file.xattr.HashType.SHA1)
                len = ltfsindex.file.xattr.HashLengthBytes.SHA1 * 2
            Case ltfsindex.file.xattr.HashType.Available.SHA256
                csa = a.GetXAttr(ltfsindex.file.xattr.HashType.SHA256)
                csb = b.GetXAttr(ltfsindex.file.xattr.HashType.SHA256)
                len = ltfsindex.file.xattr.HashLengthBytes.SHA256 * 2
            Case ltfsindex.file.xattr.HashType.Available.SHA512
                csa = a.GetXAttr(ltfsindex.file.xattr.HashType.SHA512)
                csb = b.GetXAttr(ltfsindex.file.xattr.HashType.SHA512)
                len = ltfsindex.file.xattr.HashLengthBytes.SHA512 * 2
            Case ltfsindex.file.xattr.HashType.Available.CRC32
                csa = a.GetXAttr(ltfsindex.file.xattr.HashType.CRC32)
                csb = b.GetXAttr(ltfsindex.file.xattr.HashType.CRC32)
                len = ltfsindex.file.xattr.HashLengthBytes.CRC32 * 2
            Case ltfsindex.file.xattr.HashType.Available.MD5
                csa = a.GetXAttr(ltfsindex.file.xattr.HashType.MD5)
                csb = b.GetXAttr(ltfsindex.file.xattr.HashType.MD5)
                len = ltfsindex.file.xattr.HashLengthBytes.MD5 * 2
            Case ltfsindex.file.xattr.HashType.Available.BLAKE3
                csa = a.GetXAttr(ltfsindex.file.xattr.HashType.BLAKE3)
                csb = b.GetXAttr(ltfsindex.file.xattr.HashType.BLAKE3)
                len = ltfsindex.file.xattr.HashLengthBytes.BLAKE3 * 2
            Case ltfsindex.file.xattr.HashType.Available.XxHash3
                csa = a.GetXAttr(ltfsindex.file.xattr.HashType.XxHash3)
                csb = b.GetXAttr(ltfsindex.file.xattr.HashType.XxHash3)
                len = ltfsindex.file.xattr.HashLengthBytes.XxHash3 * 2
            Case ltfsindex.file.xattr.HashType.Available.XxHash128
                csa = a.GetXAttr(ltfsindex.file.xattr.HashType.XxHash128)
                csb = b.GetXAttr(ltfsindex.file.xattr.HashType.XxHash128)
                len = ltfsindex.file.xattr.HashLengthBytes.XxHash128 * 2
        End Select
        If csa Is Nothing Then Return False
        If csb Is Nothing Then Return False
        If csa.Length <> len Then Return False
        If csb.Length <> len Then Return False
        Return csa.Equals(csb)
    End Function

    Public Shared Function FitImage(input As Bitmap, outputsize As Size) As Bitmap
        Dim result As New Bitmap(outputsize.Width, outputsize.Height, Imaging.PixelFormat.Format24bppRgb)
        If input Is Nothing Then Return result
        Dim g As Graphics = Graphics.FromImage(result)
        Dim outputw As Integer = input.Width
        Dim outputh As Integer = input.Height
        If outputw > outputsize.Width Then
            outputw = outputsize.Width
            outputh = CInt(input.Height / input.Width * outputsize.Width)
        End If
        If outputh > outputsize.Height Then
            outputh = outputsize.Height
            outputw = CInt(input.Width / input.Height * outputsize.Height)
        End If
        g.FillRectangle(Brushes.White, New Rectangle(0, 0, result.Width, result.Height))
        g.DrawImage(input, outputsize.Width \ 2 - outputw \ 2, outputsize.Height \ 2 - outputh \ 2, outputw, outputh)
        g.Dispose()
        Return result
    End Function

    <TypeConverter(GetType(ExpandableObjectConverter))>
    Public Class HashTask
        Public Event TaskStarted(Message As String)
        Public Event TaskCancelled(Message As String)
        Public Event TaskPaused(Message As String)
        Public Event TaskResumed(Message As String)
        Public Event TaskFinished(Message As String)
        Public Event ErrorOccured(Message As String)
        Public Event SHA1Changed(f As ltfsindex.file, Message As String)
        Public Event ProgressReport(Message As String)
        Public Property BufferWrite As Integer = 4 * 1024 * 1024
        Public Property schema As ltfsindex
        Public Property IgnoreExisting As Boolean = True
        Public Property ReportSkip As Boolean = True
        Private _TargetDirectory As String
        Public Property LogFile As String() = Array.Empty(Of String)()

        Public Property TargetDirectory As String
            Set(value As String)
                _TargetDirectory = value.TrimEnd("\"c) & "\"
            End Set
            Get
                Return _TargetDirectory
            End Get
        End Property

        Private _BaseDirectory As String
        Private fs As fsReport

        Public Property BaseDirectory As String
            Set(value As String)
                _BaseDirectory = value.TrimEnd("\"c) & "\"
            End Set
            Get
                Return _BaseDirectory
            End Get
        End Property

        Private Shared Function JoinSchemaPath(parentPath As String, childName As String) As String
            Dim safeName As String = If(childName, String.Empty)
            If String.IsNullOrEmpty(parentPath) Then Return safeName
            If String.IsNullOrEmpty(safeName) Then Return parentPath
            Return parentPath.TrimEnd("\"c) & "\" & safeName
        End Function

        Private Iterator Function EnumerateSelectedDirectoryFiles(directory As ltfsindex.directory,
                                                                   relativePath As String) As IEnumerable(Of ltfsindex.file)
            If directory Is Nothing OrElse Not directory.Selected Then Exit Function

            For Each file As ltfsindex.file In directory.EnumerateLazyFiles()
                If file Is Nothing OrElse Not file.Selected Then Continue For
                file.fullpath = JoinSchemaPath(relativePath, file.name)
                Yield file
            Next

            For Each childDirectory As ltfsindex.directory In directory.EnumerateLazyDirectories()
                If childDirectory Is Nothing OrElse Not childDirectory.Selected Then Continue For
                Dim childPath As String = JoinSchemaPath(relativePath, childDirectory.name)
                For Each file As ltfsindex.file In EnumerateSelectedDirectoryFiles(childDirectory, childPath)
                    Yield file
                Next
            Next
        End Function

        Private Iterator Function EnumerateSelectedFiles() As IEnumerable(Of ltfsindex.file)
            If schema Is Nothing Then Exit Function

            If schema._file IsNot Nothing Then
                For Each file As ltfsindex.file In schema._file
                    If file Is Nothing OrElse Not file.Selected Then Continue For
                    file.fullpath = If(file.name, String.Empty)
                    Yield file
                Next
            End If

            If schema._directory IsNot Nothing Then
                For Each directory As ltfsindex.directory In schema._directory
                    If directory Is Nothing OrElse Not directory.Selected Then Continue For
                    ' The root directory is the volume container and is not
                    ' part of the source path, matching the former name-reset
                    ' behavior without mutating its lazy metadata.
                    For Each file As ltfsindex.file In EnumerateSelectedDirectoryFiles(directory, String.Empty)
                        Yield file
                    Next
                Next
            End If
        End Function

        Public OperationLock As New Object
        Private thHash As Thread
        Private ReadOnly pauseGate As New ManualResetEventSlim(True)
        Private fout As FileStream = Nothing
        Private fob As BufferedStream = Nothing
        Dim f_outpath As String

        Public Sub Start()
            SyncLock OperationLock
                If schema Is Nothing Then
                    RaiseEvent ErrorOccured("Error: No schema.")
                    Exit Sub
                End If
                If Status <> TaskStatus.Idle Then
                    Exit Sub
                End If
                pauseGate.Set()
                fs = New fsReport()
                Dim thProg As New Thread(
                    Sub()
                        While Status <> TaskStatus.Idle
                            Try
                                If fs.fs IsNot Nothing Then
                                    If Not fs.fs.CanSeek Then Exit Try
                                    If fs.fs.Length = 0 Then Exit Try
                                    SyncLock OperationLock
                                        Dim p As Long = fs.fs.Position
                                        Dim l As Long = fs.fs.Length
                                        RaiseEvent ProgressReport("#fmax" & 10000)
                                        RaiseEvent ProgressReport("#fval" & p / l * 10000)
                                        RaiseEvent ProgressReport("#dmax" & l)
                                        RaiseEvent ProgressReport("#dval" & p)
                                    End SyncLock
                                End If
                            Catch ex As Exception
                                'RaiseEvent ErrorOccured(ex.ToString)
                            End Try

                            Thread.Sleep(100)
                        End While
                    End Sub)
                thHash = New Thread(
                    Sub()
                        SyncLock OperationLock
                            _Status = TaskStatus.Running
                            thProg.Start()
                        End SyncLock
                        RaiseEvent ProgressReport("#max10000")
                        RaiseEvent ProgressReport("#val0")
                        Dim totalSize As Long = 0
                        Dim hashedSize As Long = 0
                        Dim selectedFileCount As Long = 0
                        For Each f As ltfsindex.file In EnumerateSelectedFiles()
                            Interlocked.Increment(selectedFileCount)
                            Interlocked.Add(totalSize, f.length)
                        Next
                        If totalSize = 0 Then totalSize = 1
                        RaiseEvent ProgressReport("#smax" & totalSize)
                        ' Physical sorting only changes processing order.  Keep
                        ' the schema streaming so a large index does not retain
                        ' one file object per selected entry.
                        RaiseEvent ProgressReport("Streaming")
                        RaiseEvent ProgressReport("#max" & 10000)
                        RaiseEvent ProgressReport("#tmax" & selectedFileCount)
                        Dim progval As Long = 0
                        For Each f As ltfsindex.file In EnumerateSelectedFiles()
                            pauseGate.Wait()
                            Dim SkipCurrent As Boolean = False
                            Try
                                Dim relativePath As String = f.fullpath
                                If TargetDirectory <> "" AndAlso TargetDirectory <> "\" Then _
                                                 f_outpath = "\\?\" & Path.Combine(TargetDirectory, relativePath)
                                ' Schema paths are relative and may have been
                                ' combined with a base directory whose casing
                                ' differs from the actual SMB/NTFS path.
                                f.fullpath = FileSystemPathResolver.ResolveExistingFilePath(
                                    "\\?\" & Path.Combine(BaseDirectory, relativePath))

                                If f.sha1 Is Nothing Then f.sha1 = ""
                                If f.sha1 = "" Or Not IgnoreExisting Or f.sha1.Length <> 40 Or
                                                 ((TargetDirectory <> "" AndAlso TargetDirectory <> "\") And Not File.Exists(f_outpath)) Then
                                    RaiseEvent ProgressReport("[hash] " & f.fullpath)
                                    Try
                                        Dim action_writefile _
                                                As Action(Of EventedStream.ReadStreamEventArgs, EventedStream) =
                                                Sub(args As EventedStream.ReadStreamEventArgs, st As EventedStream)
                                                    pauseGate.Wait()
                                                    If fout IsNot Nothing Then
                                                        fout.Write(args.Buffer, args.Offset,
                                                                   CInt(Math.Min(args.Count, st.Length - fout.Position)))
                                                    End If
                                                End Sub

                                        If (TargetDirectory <> "" AndAlso TargetDirectory <> "\") Then
                                            If Not Directory.Exists(TargetDirectory) Then
                                                Try
                                                    Directory.CreateDirectory(TargetDirectory)
                                                Catch ex As Exception
                                                    RaiseEvent ErrorOccured(ex.ToString)
                                                End Try
                                            End If
                                            Try
                                                Dim outdir As String = New FileInfo(f_outpath).DirectoryName
                                                If Not Directory.Exists(outdir) Then
                                                    Directory.CreateDirectory(outdir)
                                                End If
                                                If File.Exists(f_outpath) Then
                                                    fout = Nothing
                                                    Exit Try
                                                End If
                                                fout = New FileStream(f_outpath, FileMode.CreateNew, FileAccess.Write, FileShare.Write,
                                                                         BufferWrite, FileOptions.WriteThrough)
                                                'fout = IO.File.OpenWrite(f_outpath)

                                                'fob = New IO.BufferedStream(fout, 1024 * 1024)
                                            Catch ex As Exception
                                                RaiseEvent ErrorOccured(ex.ToString)
                                            End Try
                                        End If
                                        Dim oldvalue As String = f.sha1
                                        f.sha1 = ""
                                        If File.Exists(f.fullpath) Then
                                            If LogFile.Count > 0 Then f.sha1 = SHA1(f.fullpath, LogFile)
                                            If oldvalue.Length = 40 AndAlso f.sha1.Length = 40 AndAlso oldvalue.ToUpper <> f.sha1.ToUpper Then
                                                RaiseEvent SHA1Changed(f, $"{oldvalue} -> {f.sha1}")
                                            End If
                                            If (Not IgnoreExisting) OrElse f.sha1.Length <> 40 Then
                                                f.sha1 = SHA1(f.fullpath, Nothing, fs, action_writefile)
                                                If oldvalue.Length = 40 AndAlso f.sha1.Length = 40 AndAlso oldvalue.ToUpper <> f.sha1.ToUpper Then
                                                    RaiseEvent SHA1Changed(f, $"{oldvalue} -> {f.sha1}")
                                                End If
                                            Else
                                                Exit Try
                                            End If
                                        Else
                                            Exit Try
                                        End If


                                        If fout IsNot Nothing Then
                                            Try
                                                'fob.Flush()
                                                'fob.Close()
                                                fout.Flush()
                                                fout.Close()
                                                Dim foinfo As New FileInfo(f_outpath)
                                                Dim fiinfo As New FileInfo(f.fullpath)
                                                foinfo.CreationTimeUtc = fiinfo.CreationTimeUtc
                                                foinfo.Attributes = fiinfo.Attributes
                                                foinfo.LastWriteTimeUtc = fiinfo.LastWriteTimeUtc
                                                fout.Dispose()
                                                fout = Nothing
                                            Catch ex As Exception
                                                RaiseEvent ErrorOccured(ex.ToString)
                                            End Try
                                        End If
                                        fs.fs.Dispose()
                                    Catch ex As Exception
                                        If fout IsNot Nothing Then
                                            Try
                                                'fob.Flush()
                                                'fob.Close()
                                                fout.Flush()
                                                fout.Close()
                                                fout.Dispose()
                                                fout = Nothing
                                            Catch ex2 As Exception
                                                RaiseEvent ErrorOccured(ex2.ToString)
                                            End Try
                                        End If
                                        If ex.Message = "正在中止线程。" Then Exit Try
                                        RaiseEvent ErrorOccured(ex.ToString)
                                    End Try
                                Else
                                    SkipCurrent = True
                                    If ReportSkip Then RaiseEvent ProgressReport("[skip] " & f.fullpath)
                                End If
                            Catch ex As Exception
                                If fout IsNot Nothing Then
                                    Try
                                        'fob.Flush()
                                        'fob.Close()
                                        fout.Flush()
                                        fout.Close()
                                        fout.Dispose()
                                        fout = Nothing
                                    Catch ex2 As Exception
                                        RaiseEvent ErrorOccured(ex2.ToString)
                                    End Try
                                End If
                                If ex.Message = "正在中止线程。" Then Exit Try
                                RaiseEvent ErrorOccured(ex.ToString)
                            End Try


                            Interlocked.Add(progval, 1)
                            SyncLock OperationLock
                                RaiseEvent ProgressReport("#dval" & 0)
                                Interlocked.Add(hashedSize, f.length)
                                RaiseEvent ProgressReport("#val" & hashedSize / totalSize * 10000)
                                RaiseEvent ProgressReport("#tval" & progval)
                                If ReportSkip OrElse (Not SkipCurrent) Then _
                                                 RaiseEvent ProgressReport("  " & f.sha1 & "  " & f.length & vbCrLf)
                                RaiseEvent ProgressReport("#ssum" & hashedSize)
                            End SyncLock

                            Thread.Sleep(0)
                        Next

                        RaiseEvent ProgressReport("#ssum" & hashedSize)
                        'RaiseEvent ProgressReport(Now.ToString)
                        SyncLock OperationLock
                            _Status = TaskStatus.Idle
                        End SyncLock
                        RaiseEvent TaskFinished("Finished")
                    End Sub)
                thHash.Start()
            End SyncLock
            RaiseEvent TaskStarted("")
        End Sub

        Public Sub [Stop]()
            SyncLock OperationLock
                Try
                    If Status = TaskStatus.Paused Then
                        pauseGate.Set()
                        RaiseEvent TaskResumed("Resumed")
                    End If
                    Try
                        fs.fs.Close()
                    Catch ex As Exception
                        RaiseEvent ErrorOccured(ex.ToString)
                    End Try
                    thHash.Abort()
                Catch ex As Exception
                    RaiseEvent ErrorOccured(ex.ToString)
                End Try
                Try
                    If fout IsNot Nothing Then
                        fout.Close()
                        If File.Exists(f_outpath) Then
                            File.Delete(f_outpath)
                        End If
                    End If
                Catch ex As Exception
                    RaiseEvent ErrorOccured(ex.ToString)
                End Try
                Try
                    _Status = TaskStatus.Idle
                    RaiseEvent TaskCancelled("Cancelled")
                Catch ex As Exception
                    RaiseEvent ErrorOccured(ex.ToString)
                End Try
            End SyncLock
        End Sub

        Public Sub Pause()
            SyncLock OperationLock
                Try
                    pauseGate.Reset()
                    _Status = TaskStatus.Paused
                    RaiseEvent TaskPaused("Paused")
                Catch ex As Exception
                    RaiseEvent ErrorOccured(ex.ToString)
                End Try
            End SyncLock
        End Sub

        Public Sub [Resume]()
            SyncLock OperationLock
                Try
                    pauseGate.Set()
                    _Status = TaskStatus.Running
                    RaiseEvent TaskResumed("Resumed")
                Catch ex As Exception
                    RaiseEvent ErrorOccured(ex.ToString)
                End Try
            End SyncLock
        End Sub

        Public Enum TaskStatus
            Idle
            Running
            Paused
        End Enum

        Private _Status As TaskStatus = TaskStatus.Idle

        Public ReadOnly Property Status As TaskStatus
            Get
                Return _Status
            End Get
        End Property
    End Class

    <TypeConverter(GetType(ExpandableObjectConverter))>
    Public Class EventedStream
        Inherits Stream
        Implements IDisposable

        Class ReadStreamEventArgs
            Inherits EventArgs

            Public Buffer() As Byte

            Public Offset As Integer

            Public Count As Integer

            Public Denied As Boolean = False
        End Class

        Class WriteStreamEventArgs
            Inherits EventArgs

            Public Buffer() As Byte

            Public Offset As Integer

            Public Count As Integer

            Public Denied As Boolean = False
        End Class

        Public Class FlushStreamEventArgs
            Inherits EventArgs

            Public Denied As Boolean = False
        End Class

        Public Class SetStreamLengthEventArgs
            Inherits EventArgs

            Public Value As Long

            Public Denied As Boolean = False
        End Class

        Public Class SeekStreamEventArgs
            Inherits EventArgs

            Public Offset As Long

            Public SeekOrigin As SeekOrigin

            Public Denied As Boolean = False
        End Class

        Public baseStream As Stream

        Public Overrides ReadOnly Property CanRead As Boolean
            Get
                Return baseStream.CanRead
            End Get
        End Property

        Public Overrides ReadOnly Property CanSeek As Boolean
            Get
                Return baseStream.CanSeek
            End Get
        End Property

        Public Overrides ReadOnly Property CanWrite As Boolean
            Get
                Return baseStream.CanWrite
            End Get
        End Property

        Public Overrides ReadOnly Property Length As Long
            Get
                Return baseStream.Length
            End Get
        End Property

        Public Overrides Property Position As Long
            Get
                Return baseStream.Position
            End Get
            Set(value As Long)
                baseStream.Position = value
            End Set
        End Property

        Public Overrides Sub Flush()
            Dim args As New FlushStreamEventArgs()
            RaiseEvent PreviewFlush(args)
            If args.Denied Then
                Return
            End If
            baseStream.Flush()
        End Sub

        Public Overrides Function Seek(offset As Long, origin As SeekOrigin) As Long
            Dim args As New SeekStreamEventArgs() With {.Offset = offset, .SeekOrigin = origin}
            RaiseEvent PreviewSeek(args)
            If args.Denied Then
                Return 0
            End If
            Return baseStream.Seek(offset, origin)
        End Function


        Public Overrides Sub SetLength(value As Long)
            Dim args As New SetStreamLengthEventArgs With {.Value = value}
            RaiseEvent PreviewSetLength(args)
            If args.Denied Then
                Return
            End If
            baseStream.SetLength(value)
        End Sub


        Public Overrides Function Read(buffer() As Byte, offset As Integer, count As Integer) As Integer
            Dim args As New ReadStreamEventArgs() With {.Buffer = buffer, .Offset = offset, .Count = count}
            RaiseEvent PreviewRead(args)
            If args.Denied Then
                Return 0
            End If
            Dim result As Integer = baseStream.Read(buffer, offset, count)
            RaiseEvent Readed(args)
            Return result
        End Function

        Public Overrides Sub Write(buffer() As Byte, offset As Integer, count As Integer)
            Dim args As New WriteStreamEventArgs With {.Buffer = buffer, .Offset = offset, .Count = count}
            RaiseEvent PreviewWrite(args)
            If args.Denied Then
                Return
            End If
            baseStream.Write(buffer, offset, count)
        End Sub

        Public Event PreviewFlush(args As FlushStreamEventArgs)
        Public Event PreviewSetLength(args As SetStreamLengthEventArgs)
        Public Event PreviewSeek(args As SeekStreamEventArgs)
        Public Event PreviewWrite(args As WriteStreamEventArgs)
        Public Event PreviewRead(args As ReadStreamEventArgs)
        Public Event Readed(args As ReadStreamEventArgs)
    End Class

    <TypeConverter(GetType(ExpandableObjectConverter))>
    Public Class IndexedLHashDirectory
        Public Property LTFSIndexDir As ltfsindex.directory
        Public Property LHash_Dir As ltfsindex.directory

        Public Sub New(index As ltfsindex.directory, lhash As ltfsindex.directory)
            LTFSIndexDir = index
            LHash_Dir = lhash
        End Sub
    End Class

    <TypeConverter(GetType(ExpandableObjectConverter))>
    Public Class CheckSumBlockwiseCalculator
        Private Property sha1 As SHA1
        Private Property sha256 As SHA256
        Private Property sha512 As SHA512
        Private Property CRC32 As Crc32
        Private Property md5 As MD5
        Private Property Blake As Hasher
        Private Property XxHash3 As XxHash3
        Private Property XxHash128 As XxHash128
        Private Property resultBytesSHA1 As Byte()
        Private Property resultBytesSHA256 As Byte()
        Private Property resultBytesSHA512 As Byte()
        Private Property resultBytesCRC32 As Byte()
        Private Property resultBytesMD5 As Byte()
        Private Property resultBytesBlake As Hash
        Private Property resultXxHash3 As Byte()
        Private Property resultXxHash128 As Byte()
        Private Property Lock As New Object
        Public Property StopFlag As Boolean = False
        Private Property thStarted As Boolean = False
        'Private Property BlakeStream As New MemoryStream
        'Private Property WrittenBlakeBlock1 As Int32 = 0
        'Private Property WrittenBlakeBlock2 As Int32 = 0
        Structure QueueBlock
            Public block As Byte()
            Public Len As Integer
            Public OnFinished As Action(Of Byte())
        End Structure
        Public EnQSig As New AutoResetEvent(False)
        Public DeQSig As New AutoResetEvent(False)
        Public Property q As New Queue(Of QueueBlock)

        Dim thHashAsync As New Task(
            Sub()
                While Not StopFlag
                    SyncLock Lock
                        While q.Count > 0
                            Dim blk As QueueBlock
                            SyncLock q
                                blk = q.Dequeue()
                                DeQSig.Set()
                            End SyncLock
                            With blk
                                If .Len = -1 Then .Len = .block.Length
                                Dim md5task As Task = Task.CompletedTask
                                If My.Settings.LTFSWriter_ChecksumEnabled_MD5 Then md5task = Task.Run(
                                Sub()
                                    md5.TransformBlock(.block, 0, .Len, .block, 0)
                                End Sub)
                                Dim sha1task As Task = Task.CompletedTask
                                If My.Settings.LTFSWriter_ChecksumEnabled_SHA1 Then sha1task = Task.Run(
                                Sub()
                                    sha1.TransformBlock(.block, 0, .Len, .block, 0)
                                End Sub)
                                Dim sha256task As Task = Task.CompletedTask
                                If My.Settings.LTFSWriter_ChecksumEnabled_SHA256 Then sha256task = Task.Run(
                                Sub()
                                    sha256.TransformBlock(.block, 0, .Len, .block, 0)
                                End Sub)
                                Dim sha512task As Task = Task.CompletedTask
                                If My.Settings.LTFSWriter_ChecksumEnabled_SHA512 Then sha512task = Task.Run(
                                Sub()
                                    sha512.TransformBlock(.block, 0, .Len, .block, 0)
                                End Sub)
                                Dim blaketask As Task = Task.CompletedTask
                                If My.Settings.LTFSWriter_ChecksumEnabled_BLAKE3 Then blaketask = Task.Run(
                                Sub()
                                    'BlakeStream.Write(.block, WrittenBlakeBlock1, .Len)
                                    'WrittenBlakeBlock1 += .Len
                                    Dim segment As New ArraySegment(Of Byte)(.block, 0, .Len)
                                    Try
                                        Blake.UpdateWithJoin(segment)
                                    Catch ex As Exception

                                    End Try
                                End Sub)
                                Dim crc32task As Task = Task.CompletedTask
                                If My.Settings.LTFSWriter_ChecksumEnabled_CRC32 Then crc32task = Task.Run(
                                Sub()
                                    Dim segment As New ArraySegment(Of Byte)(.block, 0, .Len)
                                    Try
                                        CRC32.Append(segment)
                                    Catch ex As Exception

                                    End Try
                                End Sub)
                                Dim xxhash3task As Task = Task.CompletedTask
                                If My.Settings.LTFSWriter_ChecksumEnabled_XxHash3 Then xxhash3task = Task.Run(
                                Sub()
                                    Dim segment As New ArraySegment(Of Byte)(.block, 0, .Len)
                                    Try
                                        XxHash3.Append(segment)
                                    Catch ex As Exception

                                    End Try
                                End Sub)
                                Dim xxhash128task As Task = Task.CompletedTask
                                If My.Settings.LTFSWriter_ChecksumEnabled_XxHash128 Then xxhash128task = Task.Run(
                                Sub()
                                    Dim segment As New ArraySegment(Of Byte)(.block, 0, .Len)
                                    Try
                                        XxHash128.Append(segment)
                                    Catch ex As Exception

                                    End Try
                                End Sub)
                                If My.Settings.LTFSWriter_ChecksumEnabled_SHA1 Then sha1task.Wait()
                                If My.Settings.LTFSWriter_ChecksumEnabled_SHA256 Then sha256task.Wait()
                                If My.Settings.LTFSWriter_ChecksumEnabled_SHA512 Then sha512task.Wait()
                                If My.Settings.LTFSWriter_ChecksumEnabled_CRC32 Then crc32task.Wait()
                                If My.Settings.LTFSWriter_ChecksumEnabled_MD5 Then md5task.Wait()
                                If My.Settings.LTFSWriter_ChecksumEnabled_BLAKE3 Then blaketask.Wait()
                                If My.Settings.LTFSWriter_ChecksumEnabled_XxHash3 Then xxhash3task.Wait()
                                If My.Settings.LTFSWriter_ChecksumEnabled_XxHash128 Then xxhash128task.Wait()
                            End With
                            If blk.OnFinished IsNot Nothing Then blk.OnFinished(blk.block)
                            blk.block = Nothing
                        End While
                    End SyncLock
                    EnQSig.WaitOne(10)
                End While
            End Sub)

        Public Sub New()
            Try
                If My.Settings.LTFSWriter_ChecksumEnabled_BLAKE3 Then Blake = Hasher.NewInstance()
            Catch
            End Try
            If My.Settings.LTFSWriter_ChecksumEnabled_SHA1 Then sha1 = SHA1.Create()
            If My.Settings.LTFSWriter_ChecksumEnabled_SHA256 Then sha256 = SHA256.Create()
            If My.Settings.LTFSWriter_ChecksumEnabled_SHA512 Then sha512 = SHA512.Create()
            If My.Settings.LTFSWriter_ChecksumEnabled_MD5 Then md5 = MD5.Create()
            Try
                If My.Settings.LTFSWriter_ChecksumEnabled_CRC32 Then CRC32 = New Crc32()
            Catch
            End Try
            Try
                If My.Settings.LTFSWriter_ChecksumEnabled_XxHash3 Then XxHash3 = New XxHash3()
            Catch
            End Try
            Try
                If My.Settings.LTFSWriter_ChecksumEnabled_XxHash128 Then XxHash128 = New XxHash128()
            Catch
            End Try
        End Sub

        Public Sub Propagate(block As Byte(), Optional ByVal Len As Integer = -1, Optional ByVal OnFinished As Action(Of Byte()) = Nothing)
            If block Is Nothing Then Throw New ArgumentNullException(NameOf(block))
            If Len = -1 Then Len = block.Length
            PropagateRange(block, 0, Len)
            If OnFinished IsNot Nothing Then OnFinished(block)
        End Sub

        Public Sub PropagateRange(block As Byte(), Offset As Integer, Len As Integer)
            If block Is Nothing Then Throw New ArgumentNullException(NameOf(block))
            If Offset < 0 OrElse Len < 0 OrElse Offset > block.Length - Len Then
                Throw New ArgumentOutOfRangeException(NameOf(Len))
            End If
            While q.Count > 0
                DeQSig.WaitOne(10)
            End While
            SyncLock Lock
                Dim sha1task As Task = Task.CompletedTask
                If My.Settings.LTFSWriter_ChecksumEnabled_SHA1 Then sha1task = Task.Run(
                    Sub()
                        sha1.TransformBlock(block, Offset, Len, block, Offset)
                    End Sub)
                Dim sha256task As Task = Task.CompletedTask
                If My.Settings.LTFSWriter_ChecksumEnabled_SHA256 Then sha256task = Task.Run(
                    Sub()
                        sha256.TransformBlock(block, Offset, Len, block, Offset)
                    End Sub)
                Dim sha512task As Task = Task.CompletedTask
                If My.Settings.LTFSWriter_ChecksumEnabled_SHA512 Then sha512task = Task.Run(
                    Sub()
                        sha512.TransformBlock(block, Offset, Len, block, Offset)
                    End Sub)
                Dim md5task As Task = Task.CompletedTask
                If My.Settings.LTFSWriter_ChecksumEnabled_MD5 Then md5task = Task.Run(
                    Sub()
                        md5.TransformBlock(block, Offset, Len, block, Offset)
                    End Sub)
                Dim blaketask As Task = Task.CompletedTask
                If My.Settings.LTFSWriter_ChecksumEnabled_BLAKE3 Then blaketask = Task.Run(
                    Sub()
                        Try
                            Dim segment As New ArraySegment(Of Byte)(block, Offset, Len)
                            Blake.UpdateWithJoin(segment)
                        Catch ex As Exception

                        End Try
                    End Sub)
                Dim crc32task As Task = Task.CompletedTask
                If My.Settings.LTFSWriter_ChecksumEnabled_CRC32 Then crc32task = Task.Run(
                    Sub()
                        Dim segment As New ArraySegment(Of Byte)(block, Offset, Len)
                        Try
                            CRC32.Append(segment)
                        Catch ex As Exception

                        End Try
                    End Sub)
                Dim xxhash3task As Task = Task.CompletedTask
                If My.Settings.LTFSWriter_ChecksumEnabled_XxHash3 Then xxhash3task = Task.Run(
                    Sub()
                        Dim segment As New ArraySegment(Of Byte)(block, Offset, Len)
                        Try
                            XxHash3.Append(segment)
                        Catch ex As Exception

                        End Try
                    End Sub)
                Dim xxhash128task As Task = Task.CompletedTask
                If My.Settings.LTFSWriter_ChecksumEnabled_XxHash128 Then xxhash128task = Task.Run(
                    Sub()
                        Dim segment As New ArraySegment(Of Byte)(block, Offset, Len)
                        Try
                            XxHash128.Append(segment)
                        Catch ex As Exception

                        End Try
                    End Sub)
                If My.Settings.LTFSWriter_ChecksumEnabled_BLAKE3 Then blaketask.Wait()
                If My.Settings.LTFSWriter_ChecksumEnabled_SHA1 Then sha1task.Wait()
                If My.Settings.LTFSWriter_ChecksumEnabled_SHA256 Then sha256task.Wait()
                If My.Settings.LTFSWriter_ChecksumEnabled_SHA512 Then sha512task.Wait()
                If My.Settings.LTFSWriter_ChecksumEnabled_CRC32 Then crc32task.Wait()
                If My.Settings.LTFSWriter_ChecksumEnabled_MD5 Then md5task.Wait()
                If My.Settings.LTFSWriter_ChecksumEnabled_XxHash3 Then xxhash3task.Wait()
                If My.Settings.LTFSWriter_ChecksumEnabled_XxHash128 Then xxhash128task.Wait()
            End SyncLock
        End Sub

        Public Sub PropagateAsync(block As Byte(), Optional ByVal Len As Integer = -1, Optional ByVal OnFinished As Action(Of Byte()) = Nothing)
            SyncLock Lock
                If Not thStarted Then
                    thHashAsync.Start()
                    thStarted = True
                End If
            End SyncLock
            If Len = -1 Then Len = block.Length
            While q.Count > 1024
                DeQSig.WaitOne(10)
            End While
            SyncLock Lock
                SyncLock q
                    q.Enqueue(New QueueBlock With {.block = block, .Len = Len, .OnFinished = OnFinished})
                    EnQSig.Set()
                End SyncLock
            End SyncLock
        End Sub

        Public Sub ProcessFinalBlock()
            While q.Count > 0
                DeQSig.WaitOne(10)
            End While
            SyncLock Lock
                If My.Settings.LTFSWriter_ChecksumEnabled_SHA1 Then
                    sha1.TransformFinalBlock({}, 0, 0)
                    resultBytesSHA1 = sha1.Hash
                End If
                If My.Settings.LTFSWriter_ChecksumEnabled_SHA256 Then
                    sha256.TransformFinalBlock({}, 0, 0)
                    resultBytesSHA256 = sha256.Hash
                End If
                If My.Settings.LTFSWriter_ChecksumEnabled_SHA512 Then
                    sha512.TransformFinalBlock({}, 0, 0)
                    resultBytesSHA512 = sha512.Hash
                End If
                Try
                    If My.Settings.LTFSWriter_ChecksumEnabled_CRC32 Then
                        resultBytesCRC32 = CRC32.GetHashAndReset()
                    End If
                Catch ex As Exception
                    resultBytesCRC32 = Nothing
                End Try
                If My.Settings.LTFSWriter_ChecksumEnabled_MD5 Then
                    md5.TransformFinalBlock({}, 0, 0)
                    resultBytesMD5 = md5.Hash
                End If
                Try
                    If My.Settings.LTFSWriter_ChecksumEnabled_BLAKE3 Then resultBytesBlake = Blake.Finalize()
                Catch ex As Exception
                    resultBytesBlake = Nothing
                End Try
                Try
                    If My.Settings.LTFSWriter_ChecksumEnabled_XxHash3 Then
                        resultXxHash3 = XxHash3.GetHashAndReset()
                    End If
                Catch ex As Exception
                    resultXxHash3 = Nothing
                End Try
                Try
                    If My.Settings.LTFSWriter_ChecksumEnabled_XxHash128 Then
                        resultXxHash128 = XxHash128.GetHashAndReset()
                    End If
                Catch ex As Exception
                    resultXxHash128 = Nothing
                End Try
            End SyncLock
            StopFlag = True
        End Sub

        Public ReadOnly Property SHA1Value As String
            Get
                SyncLock Lock
                    Try
                        Return BitConverter.ToString(resultBytesSHA1).Replace("-", "").ToUpper()
                    Catch ex As Exception
                        Return Nothing
                    End Try
                End SyncLock
            End Get
        End Property
        Public ReadOnly Property SHA256Value As String
            Get
                SyncLock Lock
                    Try
                        Return BitConverter.ToString(resultBytesSHA256).Replace("-", "").ToUpper()
                    Catch ex As Exception
                        Return Nothing
                    End Try
                End SyncLock
            End Get
        End Property
        Public ReadOnly Property SHA512Value As String
            Get
                SyncLock Lock
                    Try
                        Return BitConverter.ToString(resultBytesSHA512).Replace("-", "").ToUpper()
                    Catch ex As Exception
                        Return Nothing
                    End Try
                End SyncLock
            End Get
        End Property
        Public ReadOnly Property CRC32Value As String
            Get
                SyncLock Lock
                    Try
                        Return BitConverter.ToString(resultBytesCRC32).Replace("-", "").ToUpper()
                    Catch ex As Exception
                        Return Nothing
                    End Try
                End SyncLock
            End Get
        End Property

        Public ReadOnly Property MD5Value As String
            Get
                SyncLock Lock
                    Try
                        Return BitConverter.ToString(resultBytesMD5).Replace("-", "").ToUpper()
                    Catch ex As Exception
                        Return Nothing
                    End Try
                End SyncLock
            End Get
        End Property

        Public ReadOnly Property BlakeValue As String
            Get
                SyncLock Lock
                    Try
                        Return resultBytesBlake.ToString().ToUpper()
                    Catch ex As Exception
                        Return Nothing
                    End Try
                End SyncLock
            End Get
        End Property
        Public ReadOnly Property XXHash3Value As String
            Get
                SyncLock Lock
                    If resultXxHash3 Is Nothing Then Return Nothing
                    Try
                        Return BitConverter.ToString(resultXxHash3).Replace("-", "").ToUpper()
                    Catch ex As Exception
                        Return Nothing
                    End Try
                End SyncLock
            End Get
        End Property
        Public ReadOnly Property XXHash128Value As String
            Get
                SyncLock Lock
                    If resultXxHash128 Is Nothing Then Return Nothing
                    Try
                        Return BitConverter.ToString(resultXxHash128).Replace("-", "").ToUpper()
                    Catch ex As Exception
                        Return Nothing
                    End Try
                End SyncLock
            End Get
        End Property
    End Class

    <TypeConverter(GetType(ExpandableObjectConverter))>
    Public Class LTFSFileStream
        Inherits Stream
        Public Event LogPrint(s As String)
        Public ReadOnly Property FileInfo As ltfsindex.file
        Public Property TapeDrive As String
        Public Property BlockSize As Integer
        Public Property ExtraPartitionCount As Integer
        Public Shared OperationLock As New Object

        Public Sub New(file As ltfsindex.file, drive As String, blksize As Integer, xtrPCount As Integer)
            FileInfo = file
            TapeDrive = drive
            BlockSize = blksize
            ExtraPartitionCount = xtrPCount
            FileInfo.extentinfo.Sort(
                New Comparison(Of ltfsindex.file.extent)(
                    Function(a As ltfsindex.file.extent, b As ltfsindex.file.extent)
                        Return a.fileoffset.CompareTo(b.fileoffset)
                    End Function))
        End Sub

        Public Overrides ReadOnly Property CanRead As Boolean
            Get
                Return TapeDrive <> ""
            End Get
        End Property

        Public Overrides ReadOnly Property CanSeek As Boolean
            Get
                Return CanRead
            End Get
        End Property

        Public Overrides ReadOnly Property CanWrite As Boolean
            Get
                Return False
            End Get
        End Property

        Public Overrides ReadOnly Property Length As Long
            Get
                Return FileInfo.length
            End Get
        End Property

        Private _Position As Long

        Public Function GetExtent(offset As Long) As ltfsindex.file.extent
            SyncLock OperationLock
                For i As Integer = 0 To FileInfo.extentinfo.Count - 1
                    With FileInfo.extentinfo(i)
                        If .fileoffset <= offset AndAlso .fileoffset + .bytecount > offset Then
                            Return FileInfo.extentinfo(i)
                        End If
                    End With
                Next
            End SyncLock

            Return Nothing
        End Function

        Public Function WithinExtent(offset As Long, partition As Integer, ext As ltfsindex.file.extent) As Boolean
            If ext Is Nothing Then Return False
            With ext
                Return _
                    .fileoffset <= offset AndAlso .fileoffset + .bytecount > offset AndAlso
                    partition = Math.Min(ExtraPartitionCount, ext.partition)
            End With
        End Function

        Public Overrides Property Position As Long
            Get
                Return _Position
            End Get
            Set(value As Long)
                SyncLock OperationLock
                    If value < 0 Then value = 0
                    If value >= FileInfo.length Then value = FileInfo.length - 1
                    Dim ext As ltfsindex.file.extent = GetExtent(value)
                    Dim p As New TapeUtils.PositionData(TapeDrive)
                    Dim targetBlock As ULong = CULng(ext.startblock + (value - ext.fileoffset) \ BlockSize)
                    Dim targetPartition As Byte = CByte(Math.Min(ExtraPartitionCount, ext.partition))
                    If p.BlockNumber <> targetBlock OrElse p.PartitionNumber <> targetPartition Then
                        RaiseEvent _
                            LogPrint($"LOCATE {TapeDrive} B{targetBlock}P{targetPartition} (File position {value})")
                        TapeUtils.Locate(TapeDrive, targetBlock, targetPartition)
                    End If
                    _Position = value
                End SyncLock
            End Set
        End Property

        Public Overrides Sub Flush()
        End Sub

        Public Overrides Sub SetLength(value As Long)
            Throw New NotImplementedException()
        End Sub

        Public Overrides Sub Write(buffer() As Byte, offset As Integer, count As Integer)
            Throw New NotImplementedException()
        End Sub

        Public Overrides Function Seek(offset As Long, origin As SeekOrigin) As Long
            SyncLock ReadLock
                RaiseEvent LogPrint($"Seek Offset {offset} Origin {origin}")
                Select Case origin
                    Case SeekOrigin.Begin
                        Position = offset
                    Case SeekOrigin.Current
                        Position = Position + offset
                    Case SeekOrigin.End
                        Position = Length + offset
                End Select
                Return Position
            End SyncLock
        End Function

        Public Shared ReadLock As New Object

        Public Overrides Function Read(buffer() As Byte, offset As Integer, count As Integer) As Integer
            SyncLock ReadLock
                RaiseEvent LogPrint($"ReadFile: Offset {offset} Count{count}")
                Dim rBytes As Integer = 0
                Dim fCurrentPos As Long = Position
                Dim CUrrentP As Integer = New TapeUtils.PositionData(TapeDrive).PartitionNumber
                Dim ext As ltfsindex.file.extent = Nothing
                While rBytes < count
                    If Not WithinExtent(fCurrentPos, CUrrentP, ext) Then
                        ext = GetExtent(fCurrentPos)
                        Position = fCurrentPos
                        CUrrentP = New TapeUtils.PositionData(TapeDrive).PartitionNumber
                    End If
                    If ext Is Nothing Then Exit While
                    Dim fStartBlock As Long = ext.startblock + (fCurrentPos - ext.fileoffset + ext.byteoffset) \ BlockSize
                    Dim fByteOffset As Integer = CInt((ext.byteoffset + fCurrentPos - ext.fileoffset) Mod BlockSize)
                    Dim BytesRemaining As Long = ext.bytecount - (fCurrentPos - ext.fileoffset)
                    Dim data As Byte() = TapeUtils.ReadBlock(TapeDrive:=TapeDrive,
                                                             BlockSizeLimit:=CUInt(Math.Min(BlockSize, BytesRemaining)))
                    Dim bytesReaded As Integer = data.Length - fByteOffset
                    Dim destIndex As Integer = offset + rBytes
                    Array.Copy(data, fByteOffset, buffer, destIndex, Math.Min(bytesReaded, buffer.Length - destIndex))
                    rBytes += bytesReaded
                    fCurrentPos += bytesReaded
                End While
                _Position = fCurrentPos
                Return rBytes
            End SyncLock
        End Function
    End Class

    <TypeConverter(GetType(ExpandableObjectConverter))>
    Public Class NetworkCommand
        Public Property HashCode As Integer
        Public Property CommandType As CommandTypeDef

        Enum CommandTypeDef
            SCSICommand
            SCSISenseData
            General
            GeneralData
            SCSIIOCtlError
        End Enum

        Public Property PayLoad As New List(Of Byte())

        Public Function GetSerializedText() As String
            Dim writer As New XmlSerializer(GetType(NetworkCommand))
            Dim sb As New StringBuilder
            Dim t As New StringWriter(sb)
            writer.Serialize(t, Me)
            Return sb.ToString()
        End Function

        Public Shared Function FromXML(s As String) As NetworkCommand
            Dim reader As New XmlSerializer(GetType(NetworkCommand))
            Dim t As TextReader = New StringReader(s)
            Return CType(reader.Deserialize(t), NetworkCommand)
        End Function

        Public Function SendTo(target As Net.IPAddress, port As Integer) As NetworkCommand
            Dim rawdata() As Byte = Encoding.UTF8.GetBytes(GetSerializedText())
            Dim senddata As New List(Of Byte)
            senddata.AddRange(BitConverter.GetBytes(rawdata.Length))
            senddata.AddRange(rawdata)

            Dim _
                sck As _
                    New Net.Sockets.Socket(Net.Sockets.AddressFamily.InterNetwork, Net.Sockets.SocketType.Stream,
                                           Net.Sockets.ProtocolType.Tcp)
            sck.Connect(New Net.IPEndPoint(target, port))
            sck.SendTo(senddata.ToArray(), New Net.IPEndPoint(target, port))
            Dim header(3) As Byte
            Dim TaskFinished As Boolean = False
            Dim result As NetworkCommand = Nothing
            Task.Run(Sub()
                         Dim hLength As Integer = sck.Receive(header, 4, Net.Sockets.SocketFlags.None)
                         Dim length As Integer = BitConverter.ToInt32(header, 0)
                         Dim data(length - 1) As Byte
                         Dim dLength As Integer = sck.Receive(data, length, Net.Sockets.SocketFlags.None)
                         Dim msg As String = Encoding.UTF8.GetString(data)
                         sck.Close()
                         result = FromXML(msg)
                         TaskFinished = True
                     End Sub).Wait(10000)
            Return result
        End Function
    End Class

    Public Shared Function TryGetDirectoryCaseSensitive(path As String,
                                                         ByRef caseSensitive As Boolean) As Boolean
        caseSensitive = False
        If String.IsNullOrEmpty(path) Then Return False
        Try
            Dim result As NativeDirectoryCaseSensitiveResult = NativeMethods.QueryDirectoryCaseSensitive(path)
            If result Is Nothing Then Return False
            caseSensitive = result.CaseSensitive
            Return result.Succeeded
        Catch
            Return False
        End Try
    End Function
    Public Shared Function TryGetSparseSupport(directoryPath As String) As Boolean
        Try
            Dim tmpfname = IO.Path.Combine(directoryPath, $"LCG_{Now.ToString("yyyyMMdd_HHmmss.fffffff")}.tmp")
            Dim result = CreateSparceFile(tmpfname, 10 * 1024 * 1024)
            IO.File.Delete(tmpfname)
            Return result
        Catch
            Return False
        End Try
    End Function

    Public Shared Function TrySetSparseFile(stream As FileStream) As Boolean
        If stream Is Nothing Then Return False
        Try
            Dim nativeResult As NativeCallResult = NativeMethods.DeviceIoControl(
                stream.SafeFileHandle.DangerousGetHandle(),
                NativeMethods.FsctlSetSparse,
                IntPtr.Zero,
                0,
                IntPtr.Zero,
                0)
            Return nativeResult IsNot Nothing AndAlso nativeResult.Succeeded
        Catch
            Return False
        End Try
    End Function

    Public Shared Function CreateSparceFile(path As String, size As Long) As Boolean
        Dim result As Boolean = False
        Try
            Using fs As New FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None)

                ' 标记为稀疏文件
                result = TrySetSparseFile(fs)
                If Not result Then Return False

                ' 设置大小
                fs.SetLength(size)
            End Using
        Catch
        End Try
        Return result
    End Function
    Public Class StreamPcmPlayer
        Private waveOut As IWavePlayer
        Private waveProvider As BufferedWaveProvider
        Private waveFormat As WaveFormat
        Private isInitialized As Boolean = False

        ''' <summary>
        ''' 初始化播放器
        ''' </summary>
        ''' <param name="sampleRate">采样率（如 44100）</param>
        ''' <param name="channels">通道数（1=单声道，2=立体声）</param>
        ''' <param name="bitsPerSample">位深（如 16）</param>
        Public Sub Init(sampleRate As Integer, channels As Integer, bitsPerSample As Integer, isFloat As Boolean, Optional ByVal ReInitialize As Boolean = False, Optional ByVal MinBufferLen As Integer = 1048576)
            If isFloat Then
                waveFormat = New WaveFormat(sampleRate, channels)
            Else
                waveFormat = New WaveFormat(sampleRate, bitsPerSample, channels)
            End If
            If ReInitialize Then LastExtraBytes = {}
            waveProvider = New BufferedWaveProvider(waveFormat)
            waveProvider.DiscardOnBufferOverflow = True ' 防止溢出
            If waveProvider.BufferLength < MinBufferLen Then waveProvider.BufferLength = MinBufferLen
            waveOut = New WaveOut()
            waveOut.Init(waveProvider)
            waveOut.Volume = 1
            waveOut.Play()
            isInitialized = True
        End Sub
        Public Shared LastExtraBytes As Byte()

        ''' <summary>
        ''' 追加 PCM 数据流（字节数组）
        ''' </summary>
        ''' <param name="pcmBytes">PCM 数据块</param>
        Public Sub AddData(pcmBytes As Byte(), Optional ByVal convertFloatToInt As Boolean = False)
            If isInitialized Then
                If convertFloatToInt Then
                    If LastExtraBytes IsNot Nothing AndAlso LastExtraBytes.Length > 0 Then
                        pcmBytes = LastExtraBytes.Concat(pcmBytes).ToArray()
                    End If

                    If pcmBytes.Length Mod 4 <> 0 Then
                        Dim xtra As New List(Of Byte)
                        For i As Integer = (pcmBytes.Length \ 4) * 4 To pcmBytes.Length - 1
                            xtra.Add(pcmBytes(i))
                        Next
                        LastExtraBytes = xtra.ToArray()
                    Else
                        LastExtraBytes = {}
                    End If

                    Dim outValue As New List(Of Byte)
                    For i As Integer = 0 To ((pcmBytes.Length \ 4) * 4 - 1) Step 4
                        Dim fvalue As Single = BitConverter.ToSingle(pcmBytes, i)
                        Dim intvalue As Int16 = CShort(Math.Min(32767.0, Math.Max(-32768.0, fvalue * 32767.0)))
                        outValue.AddRange(BitConverter.GetBytes(intvalue))
                    Next
                    pcmBytes = outValue.ToArray()
                End If

                While waveProvider.BufferedBytes + pcmBytes.Length > waveProvider.BufferLength
                    Thread.Sleep(20)
                End While
                ' 直接添加 PCM 数据
                waveProvider.AddSamples(pcmBytes, 0, pcmBytes.Length)
            End If
        End Sub
        Public Sub Flush()
            If Not IsPlaying Then Exit Sub
            If LastExtraBytes Is Nothing OrElse LastExtraBytes.Length = 0 Then
            Else
                While waveProvider.BufferedBytes + LastExtraBytes.Length > waveProvider.BufferLength
                    Thread.Sleep(100)
                End While
                waveProvider.AddSamples(LastExtraBytes, 0, LastExtraBytes.Length)
            End If
        End Sub
        ''' <summary>
        ''' 判断是否正在播放（缓冲区有数据 且 播放器状态为 Playing）
        ''' </summary>
        Public ReadOnly Property IsPlaying As Boolean
            Get
                If Not isInitialized OrElse waveOut Is Nothing Then Return False
                Return waveOut.PlaybackState = PlaybackState.Playing AndAlso waveProvider.BufferedBytes > 0
            End Get
        End Property
        ''' <summary>
        ''' 停止播放并释放资源
        ''' </summary>
        Public Sub StopPlayback()
            If waveOut IsNot Nothing Then
                waveOut.Stop()
                waveOut.Dispose()
                waveOut = Nothing
            End If
            isInitialized = False
        End Sub
    End Class

    Public Class WaveFileHelper
        Public Shared Sub FixOrAddWavHeader(ByRef wavData As Byte(), ByRef sampleRate As Integer, ByRef channels As Short, ByRef bitsPerSample As Short)
            Const HeaderSize As Integer = 44

            Dim isWav As Boolean = False
            If wavData.Length >= HeaderSize Then
                Dim riff As String = Encoding.ASCII.GetString(wavData, 0, 4)
                Dim wave As String = Encoding.ASCII.GetString(wavData, 8, 4)
                isWav = (riff = "RIFF" AndAlso wave = "WAVE")
            End If

            If isWav Then
                ' 提取并更新参数
                channels = BitConverter.ToInt16(wavData, 22)
                sampleRate = BitConverter.ToInt32(wavData, 24)
                bitsPerSample = BitConverter.ToInt16(wavData, 34)

                Dim expectedDataSize As Integer = wavData.Length - HeaderSize
                BitConverter.GetBytes(wavData.Length - 8).CopyTo(wavData, 4)
                BitConverter.GetBytes(expectedDataSize).CopyTo(wavData, 40)
            Else
                ' 生成 header 并拼接
                Dim dataSize As Integer = wavData.Length
                Dim header As Byte() = GenerateWavHeader(dataSize, sampleRate, channels, bitsPerSample)
                Dim newData(dataSize + HeaderSize - 1) As Byte
                Array.Copy(header, 0, newData, 0, HeaderSize)
                Array.Copy(wavData, 0, newData, HeaderSize, dataSize)
                wavData = newData
            End If
        End Sub
        Public Shared Function AnalyzeAndRemoveWavHeader(ByVal data As Byte(),
                                          ByRef sampleRate As Integer,
                                          ByRef channels As Integer,
                                          ByRef bitsPerSample As Integer,
                                          ByRef isFloat As Boolean,
                                          ByRef ResultChanged As Boolean) As Byte()

            If data.Length < 44 Then Return data

            ' 检查前4个字节是否为 "RIFF"，后跟 "WAVE"
            If Encoding.ASCII.GetString(data, 0, 4) = "RIFF" AndAlso Encoding.ASCII.GetString(data, 8, 4) = "WAVE" Then
                ' 解析 WAV 头
                ResultChanged = False
                Dim value As Integer = BitConverter.ToInt32(data, 24)
                If sampleRate <> value Then ResultChanged = True
                sampleRate = value
                value = BitConverter.ToInt16(data, 22)
                If channels <> value Then ResultChanged = True
                channels = value
                Dim subChunkSize As Integer = BitConverter.ToInt32(data, 16)
                value = BitConverter.ToInt16(data, 34)
                If bitsPerSample <> value Then ResultChanged = True
                bitsPerSample = value
                Dim wFormatTag As Short = BitConverter.ToInt16(data, 20)
                value = CInt((wFormatTag = &H3))
                If CInt(isFloat) <> value Then ResultChanged = True
                isFloat = ((value) <> 0)
                ' 拷贝纯 PCM 数据
                Dim pcmLength As Integer = data.Length - 28 - subChunkSize
                Dim pcmData(pcmLength - 1) As Byte
                Buffer.BlockCopy(data, 28 + subChunkSize, pcmData, 0, pcmLength)

                Return pcmData
            End If

            ' 不是有效 WAV，返回原始数据
            Return data
        End Function

        ' 生成 WAV 文件头
        Public Shared Function GenerateWavHeader(dataSize As Integer, sampleRate As Integer, channels As Short, bitsPerSample As Short) As Byte()
            Dim header(43) As Byte
            Dim byteRate As Integer = sampleRate * channels * bitsPerSample \ 8
            Dim blockAlign As Short = CShort(channels * bitsPerSample \ 8)
            Dim chunkSize As Integer = 36 + dataSize

            Array.Copy(Encoding.ASCII.GetBytes("RIFF"), 0, header, 0, 4)
            BitConverter.GetBytes(chunkSize).CopyTo(header, 4)
            Array.Copy(Encoding.ASCII.GetBytes("WAVE"), 0, header, 8, 4)
            Array.Copy(Encoding.ASCII.GetBytes("fmt "), 0, header, 12, 4)
            BitConverter.GetBytes(16).CopyTo(header, 16)                 ' Subchunk1Size
            BitConverter.GetBytes(CShort(1)).CopyTo(header, 20)          ' AudioFormat = 1 (PCM)
            BitConverter.GetBytes(channels).CopyTo(header, 22)
            BitConverter.GetBytes(sampleRate).CopyTo(header, 24)
            BitConverter.GetBytes(byteRate).CopyTo(header, 28)
            BitConverter.GetBytes(blockAlign).CopyTo(header, 32)
            BitConverter.GetBytes(bitsPerSample).CopyTo(header, 34)
            Array.Copy(Encoding.ASCII.GetBytes("data"), 0, header, 36, 4)
            BitConverter.GetBytes(dataSize).CopyTo(header, 40)

            Return header
        End Function

    End Class
End Class
Public Class ZBCDeviceHelper
    Public Property handle As IntPtr
    Public Property MaximumLBA As ULong
    Public Property SectorLength As UInt16 = 512
    Public Property CommandLengthLimit As Integer = 524288

    Private _CMRStartLBA As ULong
    Public ReadOnly Property CMRStartLBA As ULong
        Get
            Return _CMRStartLBA
        End Get
    End Property
    Private _CMRLBACount As ULong
    Public ReadOnly Property CMRLBACount As ULong
        Get
            Return _CMRLBACount
        End Get
    End Property
    Public ReadOnly Property CMREndLBA As ULong
        Get
            Return CULng(CMRStartLBA + CMRLBACount - 1)
        End Get
    End Property
    Public Class Zone
        Public Enum ZoneTypeDef As Byte
            Reserved = 0
            Conventional = 1
            Sequential = 2
        End Enum
        Public Property ZoneType As ZoneTypeDef
        Public Enum ZoneConditionDef As Byte
            NOT_WRITE_POINTER = 0
            EMPTY = 1
            IMPLICIT_OPENED = 2
            EXPLICIT_OPENED = 3
            CLOSED = 4
            FULL = &HE
        End Enum
        Public Property ZoneCondition As ZoneConditionDef
        Public Property NON_SEQ As Boolean
        Public Property RESET As Boolean
        Public ReadOnly Property WRITER_POINTER_LBA_INVALID As Boolean
            Get
                Return ZoneCondition = ZoneConditionDef.NOT_WRITE_POINTER OrElse ZoneCondition = ZoneConditionDef.FULL
            End Get
        End Property
        Public Property ZoneLength As ULong
        Public Property ZoneStartLBA As ULong
        Public ReadOnly Property ZoneEndLBA As ULong
            Get
                Return CULng(Math.Max(0, ZoneStartLBA + ZoneLength - 1))
            End Get
        End Property
        Public Property ZoneWritePointerLBA As ULong
        Public Sub New()

        End Sub
        Public Sub New(RawData As Byte(), Optional ByVal StartByte As Integer = 0)
            If RawData.Length < StartByte + 64 Then Exit Sub
            ZoneType = CType(RawData(StartByte + 0) And &HF, ZoneTypeDef)
            ZoneCondition = CType(RawData(StartByte + 1) >> 4 And &HF, ZoneConditionDef)
            NON_SEQ = ((RawData(StartByte + 1) >> 1 And 1) <> 0)
            RESET = ((RawData(StartByte + 1) >> 0 And 1) <> 0)
            ZoneLength = BigEndianConverter.ToUInt64(RawData, StartByte + 8)
            ZoneStartLBA = BigEndianConverter.ToUInt64(RawData, StartByte + 16)
            ZoneWritePointerLBA = BigEndianConverter.ToUInt64(RawData, StartByte + 24)
        End Sub
    End Class
    Public Property ZoneList As New List(Of Zone)
    Private ZoneLBAMap As New Dictionary(Of ULong, Zone)
    Public Sub InitDevice()
        TapeUtils.LoadEject(handle, TapeUtils.LoadOption.LoadThreaded)
        Dim MP03 As Byte() = TapeUtils.ModeSense(handle, 3)
        SectorLength = CUShort(BigEndianConverter.ToUInt16(MP03, 12))
        ReportZones()
        LoadData()
    End Sub
    Public Sub ReportZones(Optional ByVal opt As Byte = 0)
        Dim data0 As Byte() = TapeUtils.SCSIReadParam(handle, {&H95, 0,
                                                      0, 0, 0, 0, 0, 0, 0, 0,
                                                      0, 0, 0, &H40,
                                                      0, 0}, 64)
        MaximumLBA = BigEndianConverter.ToUInt64(data0, 8)
        Dim ZoneListLen As UInteger = BigEndianConverter.ToUInt32(data0, 0)
        Dim ZoneCount As UInteger = ZoneListLen \ 64UI
        Dim currLBA As ULong = 0
        ZoneList.Clear()
        ZoneLBAMap.Clear()
        While True
            Dim data1 As Byte() = TapeUtils.SCSIReadParam(handle, {&H95, 0,
                                                          CByte(CLng((currLBA >> 56)) And &HFF),
                                                          CByte(CLng((currLBA >> 48)) And &HFF),
                                                          CByte(CLng((currLBA >> 40)) And &HFF),
                                                          CByte(CLng((currLBA >> 32)) And &HFF),
                                                          CByte(CLng((currLBA >> 24)) And &HFF),
                                                          CByte(CLng((currLBA >> 16)) And &HFF),
                                                          CByte(CLng((currLBA >> 8)) And &HFF),
                                                          CByte(CLng((currLBA >> 0)) And &HFF),
                                                          CByte((CommandLengthLimit >> 24) And &HFF),
                                                          CByte((CommandLengthLimit >> 16) And &HFF),
                                                          CByte((CommandLengthLimit >> 8) And &HFF),
                                                          CByte((CommandLengthLimit >> 0) And &HFF),
                                                          CByte(&H80 Or opt), 0}, CommandLengthLimit)
            ZoneListLen = BigEndianConverter.ToUInt32(data1, 0)
            If ZoneListLen = 0 Then Exit While
            ZoneCount = ZoneListLen \ 64UI
            Dim readed As Zone = Nothing
            For i As Integer = 0 To CInt(ZoneCount - 1)
                readed = New Zone(data1, 64 + 64 * i)
                ZoneList.Add(readed)
                ZoneLBAMap.Add(readed.ZoneStartLBA, readed)
            Next
            If readed IsNot Nothing Then currLBA = readed.ZoneStartLBA + readed.ZoneLength
            If ZoneListLen < data1.Length - 64 Then Exit While
        End While
        ZoneList.Sort(New Comparison(Of Zone)(Function(a As Zone, b As Zone) As Integer
                                                  Return a.ZoneStartLBA.CompareTo(b.ZoneStartLBA)
                                              End Function))
        Dim ZIDC1 As Integer = -1
        For i As Integer = 0 To ZoneList.Count - 1
            If ZoneList(i).ZoneType = Zone.ZoneTypeDef.Conventional Then
                _CMRStartLBA = ZoneList(i).ZoneStartLBA
                ZIDC1 = i
                Exit For
            End If
        Next
        If ZIDC1 >= 0 Then
            Dim idstep1 As Integer = Math.Max(1, ZoneList.Count \ 1000)
            For i As Integer = ZIDC1 To ZoneList.Count - 1 Step idstep1
                If (i + idstep1) >= ZoneList.Count OrElse (ZoneList(i).ZoneType = Zone.ZoneTypeDef.Conventional AndAlso ZoneList(i + idstep1).ZoneType <> Zone.ZoneTypeDef.Conventional) Then
                    Dim found As Boolean = False
                    For j As Integer = i + 1 To Math.Min(i + idstep1 - 1, ZoneList.Count - 1)
                        If ZoneList(j).ZoneType <> Zone.ZoneTypeDef.Conventional Then
                            _CMRLBACount = CULng(ZoneList(j - 1).ZoneEndLBA - CMRStartLBA + 1)
                            found = True
                            Exit For
                        End If
                    Next
                    If found Then Exit For
                End If
            Next
        End If
    End Sub
    Public Sub RefreshZoneCondition(ToRefresh As Zone)
        Dim data1 As Byte() = TapeUtils.SCSIReadParam(handle, {&H95, 0,
                                                          CByte(CLng((ToRefresh.ZoneStartLBA >> 56)) And &HFF),
                                                          CByte(CLng((ToRefresh.ZoneStartLBA >> 48)) And &HFF),
                                                          CByte(CLng((ToRefresh.ZoneStartLBA >> 40)) And &HFF),
                                                          CByte(CLng((ToRefresh.ZoneStartLBA >> 32)) And &HFF),
                                                          CByte(CLng((ToRefresh.ZoneStartLBA >> 24)) And &HFF),
                                                          CByte(CLng((ToRefresh.ZoneStartLBA >> 16)) And &HFF),
                                                          CByte(CLng((ToRefresh.ZoneStartLBA >> 8)) And &HFF),
                                                          CByte(CLng((ToRefresh.ZoneStartLBA >> 0)) And &HFF),
                                                          0, 0, 0, 128,
                                                          &H80, 0}, CommandLengthLimit)
        Dim readed As New Zone(data1, 64)
        With ToRefresh
            .NON_SEQ = readed.NON_SEQ
            .RESET = readed.RESET
            .ZoneCondition = readed.ZoneCondition
            .ZoneLength = readed.ZoneLength
            .ZoneStartLBA = readed.ZoneStartLBA
            .ZoneType = readed.ZoneType
            .ZoneWritePointerLBA = readed.ZoneWritePointerLBA
        End With
    End Sub
    Public Function GetZoneByLBA(LBA As ULong) As Zone
        Dim result As Zone = Nothing
        If ZoneList Is Nothing OrElse ZoneList.Count = 0 Then ReportZones()
        If ZoneList Is Nothing OrElse ZoneList.Count = 0 Then Return Nothing
        If ZoneLBAMap.TryGetValue(LBA, result) Then Return result
        Dim SearchStart As Integer = 0, SearchEnd As Integer = ZoneList.Count - 1
        Dim idx As Integer = (SearchStart + SearchEnd) \ 2
        If LBA > ZoneList.Last.ZoneEndLBA Then Return Nothing
        While Not (ZoneList(idx).ZoneStartLBA <= LBA AndAlso ZoneList(idx).ZoneEndLBA >= LBA)
            If ZoneList(idx).ZoneStartLBA > LBA Then
                SearchEnd = idx
            ElseIf ZoneList(idx).ZoneEndLBA < LBA Then
                SearchStart = idx
            Else
                Return Nothing
            End If
            If (SearchEnd - SearchStart) > 1 Then
                idx = (SearchStart + SearchEnd) \ 2
            Else
                idx += 1
            End If
        End While
        Return ZoneList(idx)
    End Function
    Public Function CloseAllZones(Optional ByRef sense As Byte() = Nothing) As Boolean
        Dim senseFin As Boolean = False
        Dim senseresult As Byte() = Array.Empty(Of Byte)()
        Dim result As Boolean = TapeUtils.SendSCSICommand(handle, {&H94, &H1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0}, Nothing, 1,
                                         Function(sdata As Byte())
                                             senseresult = sdata
                                             senseFin = True
                                             Return True
                                         End Function)
        For i As Integer = 0 To 10
            If senseFin Then Exit For
            Thread.Sleep(1)
        Next
        sense = senseresult
        Return result
    End Function
    Public Function ResetWritePointer(LowestLBA As ULong, Optional ByRef sense As Byte() = Nothing) As Boolean
        Dim senseFin As Boolean = False
        Dim senseresult As Byte() = Array.Empty(Of Byte)()
        Dim result As Boolean = TapeUtils.SendSCSICommand(
            handle, {&H94, &H4,
            CByte(CLng((LowestLBA >> 56)) And &HFF),
            CByte(CLng((LowestLBA >> 48)) And &HFF),
            CByte(CLng((LowestLBA >> 40)) And &HFF),
            CByte(CLng((LowestLBA >> 32)) And &HFF),
            CByte(CLng((LowestLBA >> 24)) And &HFF),
            CByte(CLng((LowestLBA >> 16)) And &HFF),
            CByte(CLng((LowestLBA >> 8)) And &HFF),
            CByte(CLng((LowestLBA >> 0)) And &HFF),
            0, 0, 0, 0, 0, 0}, Nothing, 1,
                                         Function(sdata As Byte())
                                             senseresult = sdata
                                             senseFin = True
                                             Return True
                                         End Function)

        For i As Integer = 0 To 10
            If senseFin Then Exit For
            Thread.Sleep(1)
        Next
        sense = senseresult
        Return result
    End Function
    Public Function OpenZone(LowestLBA As ULong, Optional ByRef sense As Byte() = Nothing) As Boolean
        Dim senseFin As Boolean = False
        Dim senseresult As Byte() = Array.Empty(Of Byte)()
        Dim result As Boolean = TapeUtils.SendSCSICommand(
            handle, {&H94, &H3,
            CByte(CLng((LowestLBA >> 56)) And &HFF),
            CByte(CLng((LowestLBA >> 48)) And &HFF),
            CByte(CLng((LowestLBA >> 40)) And &HFF),
            CByte(CLng((LowestLBA >> 32)) And &HFF),
            CByte(CLng((LowestLBA >> 24)) And &HFF),
            CByte(CLng((LowestLBA >> 16)) And &HFF),
            CByte(CLng((LowestLBA >> 8)) And &HFF),
            CByte(CLng((LowestLBA >> 0)) And &HFF),
            0, 0, 0, 0, 0, 0}, Nothing, 1,
                                         Function(sdata As Byte())
                                             senseresult = sdata
                                             senseFin = True
                                             Return True
                                         End Function)

        For i As Integer = 0 To 10
            If senseFin Then Exit For
            Thread.Sleep(1)
        Next
        sense = senseresult
        Return result
    End Function
    Public Function CloseZone(LowestLBA As ULong, Optional ByRef sense As Byte() = Nothing) As Boolean
        Dim senseFin As Boolean = False
        Dim senseresult As Byte() = Array.Empty(Of Byte)()
        Dim result As Boolean = TapeUtils.SendSCSICommand(
            handle, {&H94, &H1,
            CByte(CLng((LowestLBA >> 56)) And &HFF),
            CByte(CLng((LowestLBA >> 48)) And &HFF),
            CByte(CLng((LowestLBA >> 40)) And &HFF),
            CByte(CLng((LowestLBA >> 32)) And &HFF),
            CByte(CLng((LowestLBA >> 24)) And &HFF),
            CByte(CLng((LowestLBA >> 16)) And &HFF),
            CByte(CLng((LowestLBA >> 8)) And &HFF),
            CByte(CLng((LowestLBA >> 0)) And &HFF),
            0, 0, 0, 0, 0, 0}, Nothing, 1,
                                         Function(sdata As Byte())
                                             senseresult = sdata
                                             senseFin = True
                                             Return True
                                         End Function)

        For i As Integer = 0 To 10
            If senseFin Then Exit For
            Thread.Sleep(1)
        Next
        sense = senseresult
        Return result
    End Function
    Public Function FinishZone(LowestLBA As ULong, Optional ByRef sense As Byte() = Nothing) As Boolean
        Dim senseFin As Boolean = False
        Dim senseresult As Byte() = Array.Empty(Of Byte)()
        Dim result As Boolean = TapeUtils.SendSCSICommand(
            handle, {&H94, &H2,
            CByte(CLng((LowestLBA >> 56)) And &HFF),
            CByte(CLng((LowestLBA >> 48)) And &HFF),
            CByte(CLng((LowestLBA >> 40)) And &HFF),
            CByte(CLng((LowestLBA >> 32)) And &HFF),
            CByte(CLng((LowestLBA >> 24)) And &HFF),
            CByte(CLng((LowestLBA >> 16)) And &HFF),
            CByte(CLng((LowestLBA >> 8)) And &HFF),
            CByte(CLng((LowestLBA >> 0)) And &HFF),
            0, 0, 0, 0, 0, 0}, Nothing, 1,
                                         Function(sdata As Byte())
                                             senseresult = sdata
                                             senseFin = True
                                             Return True
                                         End Function)

        For i As Integer = 0 To 10
            If senseFin Then Exit For
            Thread.Sleep(1)
        Next
        sense = senseresult
        Return result
    End Function
    Public Function ReadBytes(StartLBA As ULong, ByVal ByteOffset As UInt16, ReadLen As ULong) As Byte()
        Dim result As New List(Of Byte)
        Dim remain As ULong = ReadLen
        Dim oncereadsectorcount As Integer = CInt(Math.Truncate(CommandLengthLimit / SectorLength))
        Dim currentLBA As ULong = StartLBA
        While remain > 0
            Dim data As Byte() = TapeUtils.SCSIReadParam(handle, {
                &H28, 0,
                CByte(CLng((StartLBA >> 24)) And &HFF),
                CByte(CLng((StartLBA >> 16)) And &HFF),
                CByte(CLng((StartLBA >> 8)) And &HFF),
                CByte(CLng((StartLBA >> 0)) And &HFF),
                0,
                CByte((oncereadsectorcount >> 8) And &HFF),
                CByte((oncereadsectorcount >> 0) And &HFF),
                 0}, oncereadsectorcount * SectorLength)
            If currentLBA = StartLBA AndAlso ByteOffset > 0 Then
                data = data.Skip(ByteOffset).ToArray()
            End If
            If data.Length > remain Then data = data.Take(CInt(remain)).ToArray()
            result.AddRange(data)
            remain = CULng(remain - data.Length)
            currentLBA = CULng(currentLBA + oncereadsectorcount)
        End While
        Return result.ToArray()
    End Function
    Public Function WriteBytes(ByVal source As Byte(), StartLBA As ULong, ByVal ByteOffset As UInt16, Optional ByVal Conventional As Boolean = True) As Boolean
        Dim result As New List(Of Byte)
        Dim remain As Integer = source.Length
        Dim oncewritesectorcount As Integer = CInt(Math.Truncate(CommandLengthLimit / SectorLength))
        Dim currentLBA As ULong = StartLBA
        Dim totalSector As ULong = CULng(Math.Ceiling((source.Length + ByteOffset) / SectorLength))
        Dim EndLBA As ULong = CULng(StartLBA + totalSector - 1)
        Dim tempData As New Dictionary(Of ULong, Byte())
        Dim zone0 As Zone = Nothing, zone1 As Zone = Nothing
        If ByteOffset > 0 Then
            source = ReadBytes(StartLBA, 0, ByteOffset).Concat(source).ToArray()
        End If
        If Not Conventional Then
            ' check zone condition
            zone0 = GetZoneByLBA(StartLBA)
            zone1 = GetZoneByLBA(EndLBA)
            If zone0.ZoneStartLBA < StartLBA Then
                If zone0.ZoneCondition = Zone.ZoneConditionDef.EMPTY Then
                    'empty
                    Dim empty(SectorLength - 1) As Byte
                    OpenZone(zone0.ZoneStartLBA)
                    For lba As ULong = zone0.ZoneStartLBA To CULng(StartLBA - 1)
                        WriteBytes(empty, lba, 0, True)
                    Next
                ElseIf (Not zone0.WRITER_POINTER_LBA_INVALID) AndAlso zone0.ZoneWritePointerLBA <= StartLBA Then
                    'data end < write position
                    If zone0.ZoneCondition = Zone.ZoneConditionDef.CLOSED Then OpenZone(zone0.ZoneStartLBA)
                    Dim empty(SectorLength - 1) As Byte
                    For lba As ULong = zone0.ZoneWritePointerLBA To CULng(StartLBA - 1)
                        WriteBytes(empty, lba, 0, True)
                    Next
                ElseIf ((Not zone0.WRITER_POINTER_LBA_INVALID) AndAlso zone0.ZoneWritePointerLBA > StartLBA) OrElse zone0.ZoneCondition = Zone.ZoneConditionDef.FULL Then
                    'data end >= write position
                    For lba As ULong = zone0.ZoneStartLBA To CULng(StartLBA - 1)
                        tempData.Add(lba, ReadBytes(lba, 0, SectorLength))
                    Next
                    ResetWritePointer(zone0.ZoneStartLBA)
                    OpenZone(zone0.ZoneStartLBA)
                    For lba As ULong = zone0.ZoneStartLBA To CULng(StartLBA - 1)
                        WriteBytes(tempData(lba), lba, 0, True)
                    Next
                Else
                    'cmr zone
                End If
            End If
            If zone1.ZoneEndLBA > EndLBA Then
                For lba As ULong = CULng(EndLBA + 1) To zone1.ZoneEndLBA
                    tempData.Add(lba, ReadBytes(lba, 0, SectorLength))
                Next
                ResetWritePointer(zone1.ZoneStartLBA)
            End If
        End If
        Dim currentZone As Zone = zone0
        Dim currentEndZone As Zone = Nothing
        While remain > 0
            Dim sendlen As Integer = Math.Min(oncewritesectorcount * SectorLength, remain)
            Dim currentsendsectorcount As Integer = CInt(Math.Ceiling(sendlen / SectorLength))
            currentEndZone = GetZoneByLBA(CULng(currentLBA + currentsendsectorcount - 1))
            For i As Integer = ZoneList.IndexOf(currentZone) + 1 To ZoneList.IndexOf(currentEndZone)
                OpenZone(ZoneList(i).ZoneStartLBA)
            Next
            Dim toSend(currentsendsectorcount * SectorLength - 1) As Byte
            Array.Copy(source, source.Length - remain, toSend, 0, sendlen)
            TapeUtils.SendSCSICommand(handle, {
                &H2A, 0,
                CByte(CLng((currentLBA >> 24)) And &HFF),
                CByte(CLng((currentLBA >> 16)) And &HFF),
                CByte(CLng((currentLBA >> 8)) And &HFF),
                CByte(CLng((currentLBA >> 0)) And &HFF),
                0,
                CByte((currentsendsectorcount >> 8) And &HFF),
                CByte((currentsendsectorcount >> 0) And &HFF),
                 0}, toSend, 0)

            remain -= sendlen
            currentLBA = CULng(currentLBA + currentsendsectorcount)
            If Not Conventional Then
                Dim nextZone As Zone = GetZoneByLBA(currentLBA)
                If nextZone.ZoneStartLBA > currentEndZone.ZoneStartLBA Then
                    For i As Integer = ZoneList.IndexOf(currentZone) To ZoneList.IndexOf(currentZone)
                        FinishZone(ZoneList(i).ZoneStartLBA)
                    Next
                Else
                    For i As Integer = ZoneList.IndexOf(currentZone) To ZoneList.IndexOf(currentEndZone) - 1
                        FinishZone(ZoneList(i).ZoneStartLBA)
                    Next
                End If
                currentZone = nextZone
            End If
        End While
        If Not Conventional AndAlso tempData.Count > 0 Then
            For Each kv In tempData.OrderBy(Function(x) x.Key)
                WriteBytes(kv.Value, kv.Key, 0, True)
            Next
        End If
        If Not Conventional Then
            'check zone condition
            RefreshZoneCondition(currentEndZone)
            If currentEndZone.ZoneCondition <> Zone.ZoneConditionDef.FULL AndAlso
                currentEndZone.ZoneCondition <> Zone.ZoneConditionDef.CLOSED AndAlso
                currentEndZone.ZoneCondition <> Zone.ZoneConditionDef.NOT_WRITE_POINTER Then
                CloseZone(currentEndZone.ZoneStartLBA)
            End If
        End If
        Return True
    End Function
    Public Property Data As ZBCDataHelper
    Public Property DataStartLBA As ULong
    Public Property DataEndLBA As ULong
    Public Sub LoadData()
        Dim vol1 As Byte() = ReadBytes(0, 0, SectorLength)
        Dim header As String = BitConverter.ToString(vol1, &H163, 16)
        If Not header.StartsWith("LCGZBC") Then Exit Sub
        DataStartLBA = CULng(BigEndianConverter.ToUInt64(vol1, &H1CE + 8) And &HFFFFFF)
        Dim ZBCDataLen As ULong = CULng((BigEndianConverter.ToUInt64(vol1, &H1CE + 12) And &HFFFFFF) * SectorLength)
        DataEndLBA = CULng(DataStartLBA + Math.Ceiling(ZBCDataLen / SectorLength) - 1)
        Data = ZBCDataHelper.FromXML(BitConverter.ToString(ReadBytes(DataStartLBA, 0, ZBCDataLen)).TrimEnd(CChar(vbNullChar)))
    End Sub
    Public Sub SaveData()
        Dim vol1 As Byte() = ReadBytes(0, 0, SectorLength)
        Dim header As Byte() = Encoding.ASCII.GetBytes("LCGZBC")
        Array.Copy(header, 0, vol1, &H163, header.Length)
        Dim dataBinary As Byte() = Encoding.UTF8.GetBytes(Data.GetSerializedText())
        Dim StartLBABytes As Byte() = BigEndianConverter.GetBytes(DataStartLBA)
        Dim LenLBABytes As Byte() = BigEndianConverter.GetBytes(CULng(Math.Ceiling((dataBinary.Length) / SectorLength)))
        Array.Copy(StartLBABytes, 0, vol1, &H1CE + 8, 8)
        Array.Copy(LenLBABytes, 0, vol1, &H1CE + 12, 8)
        WriteBytes(vol1, 0, 0, True)
    End Sub
    <Serializable>
    Public Class ZBCDataHelper
        <XmlIgnore>
        Public Device As ZBCDeviceHelper
        Public Property CMRDataStartLBA As ULong
        Public Property CMRDataLength As ULong
        Public Sub WriteCMRData(toWrite As Byte())
            CMRDataLength = CULng(toWrite.Length)
            Device.WriteBytes(toWrite, CMRDataStartLBA, 0, True)
        End Sub
        Public Function ReadCMRData() As Byte()
            Return Device.ReadBytes(CMRDataStartLBA, 0, CMRDataLength)
        End Function
        Public Property DataStreamList As New List(Of DataStream)

        <Serializable>
        Public Class DataStream
            Inherits Stream

            Public Parent As ZBCDataHelper
            Public Property StartLBA As ULong
            Public Property MaxLength As ULong = ULong.MaxValue

            Private _Position As ULong = 0
            Private _Length As ULong = 0
            Private SectorResidue As Byte()

            Public Overrides ReadOnly Property CanRead As Boolean
                Get
                    Return Parent IsNot Nothing
                End Get
            End Property
            Public Overrides ReadOnly Property CanSeek As Boolean
                Get
                    Return Parent IsNot Nothing
                End Get
            End Property
            Public Overrides ReadOnly Property CanWrite As Boolean
                Get
                    Return Parent IsNot Nothing
                End Get
            End Property
            Public Overrides ReadOnly Property Length As Long
                Get
                    Return CLng(_Length)
                End Get
            End Property
            Public Overrides Property Position As Long
                Get
                    Return CLng(_Position)
                End Get
                Set(value As Long)
                    Seek(value, SeekOrigin.Begin)
                End Set
            End Property
            Public ReadOnly Property CurrentLBA As ULong
                Get
                    Return BytePosToLBA(CULng(Position))
                End Get
            End Property
            Public ReadOnly Property CurrentZone As Zone
                Get
                    Return Parent.Device.GetZoneByLBA(CurrentLBA)
                End Get
            End Property

            Public Overrides Sub SetLength(value As Long)
                SyncLock Me
                    _Length = CULng(value)
                End SyncLock
            End Sub

            Private Function BytePosToLBA(pos As ULong) As ULong
                Return StartLBA + pos \ Parent.Device.SectorLength
            End Function
            Private Function BytePosToSectorOffset(pos As ULong) As Integer
                Return CInt(pos Mod CUInt(Parent.Device.SectorLength))
            End Function
            Private Function GetZoneForBytePos(pos As ULong) As Zone
                Return Parent.Device.GetZoneByLBA(BytePosToLBA(pos))
            End Function

            Public Overrides Sub Flush()

            End Sub
            Private Sub Load()
                Dim currZone As Zone = CurrentZone
                Dim currLBA As ULong = CurrentLBA
                Dim currWP As ULong = currZone.ZoneWritePointerLBA
                Dim residueBytes As Integer = BytePosToSectorOffset(CULng(Position))
                If currWP <> currLBA + 1 Then
                    Dim ZoneRewriteBuffer(CInt(Parent.Device.SectorLength * (currLBA - currZone.ZoneStartLBA) - 1)) As Byte
                    ZoneRewriteBuffer = Parent.Device.ReadBytes(currZone.ZoneStartLBA, 0, CULng(ZoneRewriteBuffer.Length))
                End If

            End Sub

            Public Overrides Function Read(buffer() As Byte, offset As Integer, count As Integer) As Integer
                count = CInt(Math.Min(count, Length - Position))
                If count > 0 Then
                    Dim result As Byte() = Parent.Device.ReadBytes(BytePosToLBA(CULng(Position)), CUShort(BytePosToSectorOffset(CULng(Position))), CULng(count))
                    Array.Copy(result, 0, buffer, offset, count)
                End If
                Return count
            End Function

            Public Overrides Sub Write(buffer() As Byte, offset As Integer, count As Integer)
                '拼数据

                '写整LBA

                '多余丢缓存

            End Sub

            Public Overrides Function Seek(offset As Long, origin As SeekOrigin) As Long
                Dim target As Long
                Select Case origin
                    Case SeekOrigin.Begin
                        target = offset
                    Case SeekOrigin.Current
                        target = Position + offset
                    Case SeekOrigin.End
                        target = Length + offset
                End Select
                If target > Position Then
                    '写零
                Else
                    Flush()
                    _Position = CULng(target)
                    '重建缓存

                End If
                Return Position
            End Function
        End Class

        Public Sub CreateLTFSDefault(Optional ByVal SinglePartition As Boolean = False)
            CMRDataStartLBA = CULng(Device.DataStartLBA + 1024)
            DataStreamList.Clear()
            Dim indexpartition As New DataStream With {.StartLBA = CULng(Device.CMREndLBA + 1)}
            DataStreamList.Add(indexpartition)
            If SinglePartition Then
                indexpartition.MaxLength = CULng((Device.MaximumLBA - indexpartition.StartLBA + 1) * Device.SectorLength)
            Else
                indexpartition.MaxLength = CULng(Math.Ceiling(107374182400 / Device.SectorLength) * Device.SectorLength)
                Dim datapartition As New DataStream With {.StartLBA = CULng(Device.CMREndLBA + 1 + Math.Ceiling(107374182400 / Device.SectorLength))}
                datapartition.MaxLength = CULng((Device.MaximumLBA - datapartition.StartLBA + 1) * Device.SectorLength)
                DataStreamList.Add(datapartition)
            End If
        End Sub

        Public Function GetSerializedText() As String
            Dim writer As New XmlSerializer(GetType(ZBCDataHelper))
            Dim sb As New StringBuilder
            Dim t As New StringWriter(sb)
            writer.Serialize(t, Me)
            Return sb.ToString()
        End Function

        Public Shared Function FromXML(s As String) As ZBCDataHelper
            Dim reader As New XmlSerializer(GetType(ZBCDataHelper))
            Dim t As TextReader = New StringReader(s)
            Return CType(reader.Deserialize(t), ZBCDataHelper)
        End Function

    End Class
End Class
Public Class DiskQuery

    '========================
    ' Public function
    '========================
    Public Shared Function QuerySectorInfo(hDevice As IntPtr) As (LBACount As ULong, SectorSize As Integer)

        Dim totalBytes As Long = GetDiskLength(hDevice)
        Dim sectorSize As Integer = GetSectorSize(hDevice)

        If sectorSize <= 0 Then Throw New Exception("Invalid sector size")

        Dim lbaCount As ULong = CULng(totalBytes) \ CULng(sectorSize)

        Return (lbaCount, sectorSize)

    End Function

    '========================
    ' 1. Total size (bytes)
    '========================
    Public Shared Function GetDiskLength(hDevice As IntPtr) As Long
        Dim outInfo As Long = 0

        Dim outSize As Integer = Marshal.SizeOf(Of Long)()

        Dim outPtr As IntPtr = Marshal.AllocHGlobal(outSize)

        Try
            Dim nativeResult As NativeCallResult = NativeMethods.DeviceIoControl(
                hDevice,
                NativeMethods.IoctlDiskGetLengthInfo,
                IntPtr.Zero,
                0,
                outPtr,
                CUInt(outSize))

            nativeResult.ThrowIfFailed("IOCTL_DISK_GET_LENGTH_INFO failed.")
            If nativeResult.BytesReturned < CUInt(outSize) Then
                Throw New InvalidDataException("IOCTL_DISK_GET_LENGTH_INFO returned an incomplete result.")
            End If

            outInfo = Marshal.ReadInt64(outPtr)

            Return outInfo

        Finally
            Marshal.FreeHGlobal(outPtr)
        End Try

    End Function

    '========================
    ' 2. Sector size
    '========================
    Public Shared Function GetSectorSize(hDevice As IntPtr) As Integer
        Const PropertyStandardQuery As UInteger = 0
        Const StorageAccessAlignmentProperty As UInteger = 6

        ' input buffer
        Dim querySize As Integer = Marshal.SizeOf(Of STORAGE_PROPERTY_QUERY)()
        Dim inPtr As IntPtr = Marshal.AllocHGlobal(querySize)

        ' output buffer
        Dim outSize As Integer = 1024
        Dim outPtr As IntPtr = Marshal.AllocHGlobal(outSize)

        Try
            ' fill input struct
            Dim query As New STORAGE_PROPERTY_QUERY With {
                .PropertyId = StorageAccessAlignmentProperty,
                .QueryType = PropertyStandardQuery,
                .AdditionalParameters = 0
            }

            Marshal.StructureToPtr(query, inPtr, False)

            Dim nativeResult As NativeCallResult = NativeMethods.DeviceIoControl(
                hDevice,
                NativeMethods.IoctlStorageQueryProperty,
                inPtr,
                CUInt(querySize),
                outPtr,
                CUInt(outSize))

            nativeResult.ThrowIfFailed("IOCTL_STORAGE_QUERY_PROPERTY failed.")
            If nativeResult.BytesReturned = 0 Then
                Throw New InvalidDataException("IOCTL_STORAGE_QUERY_PROPERTY returned no data.")
            End If

            Dim desc As STORAGE_ACCESS_ALIGNMENT_DESCRIPTOR =
                Marshal.PtrToStructure(Of STORAGE_ACCESS_ALIGNMENT_DESCRIPTOR)(outPtr)

            Return CInt(desc.BytesPerLogicalSector)

        Finally
            Marshal.FreeHGlobal(inPtr)
            Marshal.FreeHGlobal(outPtr)
        End Try

    End Function

    '========================
    ' Structures
    '========================

    <StructLayout(LayoutKind.Sequential)>
    Private Structure GET_LENGTH_INFORMATION
        Public Length As Long
    End Structure

    <StructLayout(LayoutKind.Sequential)>
    Private Structure STORAGE_PROPERTY_QUERY
        Public PropertyId As UInteger
        Public QueryType As UInteger
        Public AdditionalParameters As Byte
    End Structure

    <StructLayout(LayoutKind.Sequential)>
    Private Structure STORAGE_ACCESS_ALIGNMENT_DESCRIPTOR
        Public Version As UInteger
        Public Size As UInteger
        Public BytesPerCacheLine As UInteger
        Public BytesOffsetForCacheAlignment As UInteger
        Public BytesPerLogicalSector As UInteger
        Public BytesPerPhysicalSector As UInteger
        Public BytesOffsetForSectorAlignment As UInteger
    End Structure

End Class
Public Class ExplorerUtils
    Implements IComparer(Of String)

    Public Function Compare(ByVal x As String, ByVal y As String) As Integer _
        Implements IComparer(Of String).Compare
        Dim result As NativeStringCompareResult = NativeMethods.CompareLogical(x, y)
        result.ThrowIfFailed("Compare file names failed.")
        Return result.Comparison
    End Function
End Class


Public Module ByteFormatter
    Public Enum Style
        SI
        IEC
        JEDEC
    End Enum

    Public Function FormatBytes(bytes As Long,
                                Optional decimals As Integer = 2,
                                Optional style As Style = Style.IEC,
                                Optional culture As CultureInfo = Nothing) As String
        If culture Is Nothing Then culture = CultureInfo.CurrentCulture

        If bytes = 0L Then
            Return "0 B"
        End If

        Dim negative As Boolean = (bytes < 0)
        Dim value As Double = Math.Abs(CDbl(bytes))

        Dim unitBase As Double
        Dim units As String()

        Select Case style
            Case Style.SI
                unitBase = 1000.0
                units = New String() {"B", "kB", "MB", "GB", "TB", "PB", "EB", "ZB", "YB"}
            Case Style.IEC
                unitBase = 1024.0
                units = New String() {"B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB", "ZiB", "YiB"}
            Case Else
                unitBase = 1024.0
                units = New String() {"B", "KB", "MB", "GB", "TB", "PB", "EB", "ZB", "YB"}
        End Select

        Dim unitIndex = 0
        While unitIndex < units.Length - 1 AndAlso value >= unitBase
            value /= unitBase
            unitIndex += 1
        End While

        Dim decimalsToUse As Integer = If(unitIndex = 0, 0, decimals)

        If negative Then value = -value

        Return String.Format(culture, "{0} {1}",
                             value.ToString("N" & decimalsToUse, culture),
                             units(unitIndex))
    End Function
End Module

Public Class BigEndianConverter
    Public Shared Function ToUInt16(data As Byte(), Optional ByVal StartIndex As Integer = 0) As UInt32
        Dim result As UInt32
        For i As Integer = StartIndex To StartIndex + 1
            result <<= 8
            If i < data.Length Then result = result Or data(i)
        Next
        Return result
    End Function
    Public Shared Function ToUInt32(data As Byte(), Optional ByVal StartIndex As Integer = 0) As UInt32
        Dim result As UInt32
        For i As Integer = StartIndex To StartIndex + 3
            result <<= 8
            If i < data.Length Then result = result Or data(i)
        Next
        Return result
    End Function
    Public Shared Function ToUInt64(data As Byte(), Optional ByVal StartIndex As Integer = 0) As UInt32
        Dim result As UInt32
        For i As Integer = StartIndex To StartIndex + 7
            result <<= 8
            If i < data.Length Then result = result Or data(i)
        Next
        Return result
    End Function
    Public Shared Function GetBytes(ByVal input As UInt64) As Byte()
        Dim result(7) As Byte
        For i As Integer = result.Length - 1 To 0 Step -1
            result(i) = CByte(CLng(input) And &HFF)
            input >>= 8
        Next
        Return result
    End Function
    Public Shared Function GetBytes(ByVal input As UInt32) As Byte()
        Dim result(3) As Byte
        For i As Integer = result.Length - 1 To 0 Step -1
            result(i) = CByte(input And &HFF)
            input >>= 8
        Next
        Return result
    End Function
    Public Shared Function GetBytes(ByVal input As UInt16) As Byte()
        Dim result(1) As Byte
        For i As Integer = result.Length - 1 To 0 Step -1
            result(i) = CByte(input And &HFF)
            input >>= 8
        Next
        Return result
    End Function
    Public Shared Function GetValue(data As Byte(), startbyte As Integer, endbyte As Integer) As Long
        Dim result As Long = 0
        For i As Integer = startbyte To endbyte
            result <<= 8
            result = result Or data(i)
        Next
        Return result
    End Function
End Class
