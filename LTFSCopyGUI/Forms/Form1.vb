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
                Try
                    Invoke(Sub() Label4.Text = CStr(SchemaLoadText.Items(0)))
                    Dim s As String = ""
                    If ReloadFile Or schema Is Nothing Then s = IO.File.ReadAllText(TextBox1.Text)
                    Invoke(Sub() Label4.Text = CStr(SchemaLoadText.Items(1)))
                    Invoke(Sub() Label4.Text = CStr(SchemaLoadText.Items(2)))

                    If ReloadFile Or schema Is Nothing Then
                        If s.Contains("XMLSchema") Then
                            schema = ltfsindex.FromXML(s)
                        Else
                            schema = ltfsindex.FromSchemaText(s)
                        End If
                    End If
                    contents = New ltfsindex.contentsDef With {._directory = schema._directory(0).contents._directory, ._file = schema._directory(0).contents._file}
                    Dim flist As New List(Of TapeFileInfo)
                    ScanFile(contents, flist, "")
                    Dim alist As New List(Of TapeFileInfo)
                    Dim blist As New List(Of TapeFileInfo)
                    Invoke(Sub() Label4.Text = CStr(SchemaLoadText.Items(3)))
                    Parallel.ForEach(flist,
                        Sub(f As TapeFileInfo)
                            If f.Partition = ltfsindex.PartitionLabel.a Then
                                SyncLock alist
                                    alist.Add(f)
                                End SyncLock
                            Else
                                SyncLock blist
                                    blist.Add(f)
                                End SyncLock
                            End If
                        End Sub)
                    Invoke(Sub() Label4.Text = CStr(SchemaLoadText.Items(4)))
                    Parallel.ForEach({alist, blist},
                        Sub(nlist As List(Of TapeFileInfo))
                            If nlist Is alist Then
                                alist.Sort(New Comparison(Of TapeFileInfo)(Function(a As TapeFileInfo, b As TapeFileInfo) As Integer
                                                                               If a.BlockNumber <> b.BlockNumber Then
                                                                                   Return a.BlockNumber.CompareTo(b.BlockNumber)
                                                                               Else
                                                                                   Return a.Path.CompareTo(b.Path)
                                                                               End If
                                                                           End Function))
                            ElseIf nlist Is blist Then
                                blist.Sort(New Comparison(Of TapeFileInfo)(Function(a As TapeFileInfo, b As TapeFileInfo) As Integer
                                                                               If a.BlockNumber <> b.BlockNumber Then
                                                                                   Return a.BlockNumber.CompareTo(b.BlockNumber)
                                                                               Else
                                                                                   Return a.Path.CompareTo(b.Path)
                                                                               End If
                                                                           End Function))
                            End If
                        End Sub)
                    filelist = New List(Of String)
                    Invoke(Sub() Label4.Text = CStr(SchemaLoadText.Items(5)))
                    Dim counter As Integer = 0
                    Dim total As Integer = alist.Count + blist.Count
                    Dim ran As New Random
                    Dim stepval As Integer = ran.Next(100, 1000)

                    Dim p As New Text.StringBuilder()
                    Dim fdir As String = TextBox3.Text
                    If fdir.EndsWith("\") Then fdir = fdir.TrimEnd("\"c)
                    fdir &= "\"
                    Dim tdir As String = TextBox4.Text
                    If tdir.EndsWith("\") Then tdir = tdir.TrimEnd("\"c)
                    tdir &= "\"
                    If Not CheckBox1.Checked Then
                        p.Append($"Partition{vbTab}Startblock{vbTab}Length{vbTab}Path{vbCrLf}")
                        For Each f As TapeFileInfo In alist.Concat(blist)
                            p.Append(f.Partition.ToString & vbTab & f.BlockNumber & vbTab & f.FileLength & vbTab & f.Path & vbCrLf)
                            filelist.Add(f.Path)
                            Threading.Interlocked.Increment(counter)
                            If counter Mod stepval = 0 Then
                                Invoke(Sub() Label4.Text = $"{SchemaLoadText.Items(5)}{counter}/{total}")
                                stepval = ran.Next(100, 1000)
                            End If
                        Next
                    Else
                        p.Append("chcp 65001" & vbCrLf)
                        For Each f As TapeFileInfo In alist.Concat(blist)
                            If CheckBox2.Checked Then
                                p.Append($"echo f|robocopy ""{fdir}{f.Path}"" ""{tdir }{f.Path}"" /Copy:D /MIR /W:10 /R:10 /J{vbCrLf}")
                            Else
                                p.Append($"echo f|xcopy /J /D /Y ""{fdir}{f.Path}"" ""{tdir }{f.Path}""{vbCrLf}")
                            End If
                            filelist.Add(f.Path)
                            Threading.Interlocked.Increment(counter)
                            If counter Mod stepval = 0 Then
                                Invoke(Sub() Label4.Text = $"{SchemaLoadText.Items(5)}{counter}/{total}")
                                stepval = ran.Next(100, 1000)
                            End If
                        Next
                    End If
                    Invoke(Sub() Label4.Text = CStr(SchemaLoadText.Items(6)))
                    Invoke(Sub() TextBox2.Text = p.ToString)
                    Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(Form1))
                        Using categoryScope As IDisposable = LogContext.PushProperty("Category", "IndexAnalyzer")
                            Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                                Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "SchemaLoad")
                                    Log.Information("Schema load completed. FilePath={FilePath} FileCount={FileCount} PartitionAFileCount={PartitionAFileCount} PartitionBFileCount={PartitionBFileCount}.", TextBox1.Text, flist.Count, alist.Count, blist.Count)
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

    Public Sub ScanFile(ByRef contents As ltfsindex.contentsDef, ByVal flist As List(Of TapeFileInfo), Optional ByVal ParentPath As String = "\")
        If contents._directory IsNot Nothing Then
            If contents._directory.Count > 0 Then
                Parallel.ForEach(contents._directory,
                    Sub(d As ltfsindex.directory)
                        ScanFile(d.contents, flist, ParentPath & d.name & "\")
                    End Sub)
            End If
        End If
        If contents._file IsNot Nothing Then
            If contents._file.Count > 0 Then
                Parallel.ForEach(contents._file,
                    Sub(f As ltfsindex.file)
                        If f IsNot Nothing Then
                            If Not f.Selected Then Exit Sub
                        End If
                        If f.extentinfo IsNot Nothing Then
                            If f.extentinfo.Count > 0 Then
                                SyncLock flist
                                    flist.Add(New TapeFileInfo With {.BlockNumber = f.extentinfo(0).startblock,
                                              .Partition = f.extentinfo(0).partition,
                                              .Path = ParentPath & f.name,
                                              .FileLength = f.length})
                                End SyncLock
                                Exit Sub
                            End If
                        End If
                        SyncLock flist
                            flist.Add(New TapeFileInfo With {.BlockNumber = 0,
                                  .Partition = ltfsindex.PartitionLabel.a,
                                  .Path = ParentPath & f.name,
                                  .FileLength = f.length})
                        End SyncLock
                    End Sub)
            End If
        End If
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
            schema = New ltfsindex
            Dim fid As Integer = 0
            Dim RootDir As IO.DirectoryInfo = New IO.DirectoryInfo(TextBox3.Text)
            'Dim BasePath As String = RootDir.Parent.FullName
            Dim q As New List(Of IndexedDirectory)
            q.Add(New IndexedDirectory(New ltfsindex.directory With {.name = RootDir.Name}, RootDir))
            schema._directory.Add(q(0).LTFSIndexDir)
            While q.Count > 0
                Dim qtmp As New List(Of IndexedDirectory)
                For Each d As IndexedDirectory In q
                    d.LTFSIndexDir.contents._file = New List(Of ltfsindex.file)
                    For Each f As IO.FileInfo In d.IO_Dir.GetFiles
                        Threading.Interlocked.Add(fid, 1)
                        Dim fnew As New ltfsindex.file With {
                             .name = f.Name,
                             .length = f.Length,
                             .extentinfo = New List(Of ltfsindex.file.extent)({New ltfsindex.file.extent With {.startblock = fid}})}
                        Try
                            fnew.creationtime = f.CreationTimeUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffffff00Z")
                            fnew.accesstime = f.LastAccessTimeUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffffff00Z")
                            fnew.modifytime = f.LastWriteTimeUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffffff00Z")
                            fnew.changetime = fnew.modifytime
                        Catch ex As Exception
                            LogFileOperationWarning("MetadataUpdate", f.FullName, ex)
                        End Try
                        d.LTFSIndexDir.contents._file.Add(fnew)
                    Next
                    For Each sd As IO.DirectoryInfo In d.IO_Dir.GetDirectories
                        Dim ld As New ltfsindex.directory With {.name = sd.Name}
                        Try
                            ld.creationtime = sd.CreationTimeUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffffff00Z")
                            ld.accesstime = sd.LastAccessTimeUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffffff00Z")
                            ld.modifytime = sd.LastWriteTimeUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffffff00Z")
                            ld.changetime = ld.modifytime
                        Catch ex As Exception
                            LogFileOperationWarning("DirectoryMetadataUpdate", sd.FullName, ex)
                        End Try
                        d.LTFSIndexDir.contents._directory.Add(ld)
                        qtmp.Add(New IndexedDirectory(ld, sd))
                    Next
                Next
                q = qtmp
            End While
            Dim ds As New ldirStack
            fid = 0
            ds.Push(schema._directory(0))
            While Not ds.IsEmpty
                Dim currentdir As ltfsindex.directory = ds.Pop()
                For i As Integer = 0 To currentdir.contents._file.Count - 1
                    Threading.Interlocked.Add(fid, 1)
                    currentdir.contents._file(i).extentinfo(0).startblock = fid
                Next
                For i As Integer = currentdir.contents._directory.Count - 1 To 0 Step -1
                    ds.Push(currentdir.contents._directory(i))
                Next
            End While
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
                Dim s As String = IO.File.ReadAllText(fl.FullName)
                If s.Contains("XMLSchema") Then
                    schema = ltfsindex.FromXML(s)
                Else
                    schema = ltfsindex.FromSchemaText(s)
                End If
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
                                Dim sch As String = IO.File.ReadAllText(fl.FullName)
                                If If(My.Settings.Application_CaseSensitiveSearch, sch.Contains(patt), sch.ToLower().Contains(patt.ToLower)) Then
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
                        Try
                            Dim extlist As New List(Of ltfsindex.file.extent)
                            Dim sch As ltfsindex = ltfsindex.FromSchFile(fl.FullName)
                            ltfsindex.WSort(sch._directory,
                                            Sub(fid As ltfsindex.file)
                                                If fid.extentinfo Is Nothing Then Exit Sub
                                                For Each ext As ltfsindex.file.extent In fid.extentinfo
                                                    ext.TempInfo = fid
                                                    extlist.Add(ext)
                                                Next
                                            End Sub, Nothing)
                            extlist.Sort(New Comparison(Of ltfsindex.file.extent)(Function(a As ltfsindex.file.extent, b As ltfsindex.file.extent) As Integer
                                                                                      If a.startblock <> b.startblock Then
                                                                                          Return a.startblock.CompareTo(b.startblock)
                                                                                      Else
                                                                                          Return a.byteoffset.CompareTo(b.byteoffset)
                                                                                      End If
                                                                                  End Function))
                            For i As Integer = 1 To extlist.Count - 1
                                If extlist(i).startblock * 524288 + extlist(i).byteoffset < extlist(i - 1).startblock * 524288 + extlist(i - 1).byteoffset + extlist(i - 1).bytecount Then
                                    result.AppendLine($"Error with {fl.Name}: fid {CType(extlist(i).TempInfo, ltfsindex.file).fileuid}")
                                End If
                            Next
                        Catch ex As Exception
                            LogFileOperationWarning("ExtentCheck", fl.FullName, ex)
                            result.Append(ex.ToString)
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
            Dim result As New ltfsindex
            result._directory = New List(Of ltfsindex.directory)
            result._directory.Add(New ltfsindex.directory With {.name = $"Search_{patt}"})
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
                                Dim sch As String = IO.File.ReadAllText(fl.FullName)
                                If If(My.Settings.Application_CaseSensitiveSearch, sch.Contains(patt), sch.ToLower().Contains(patt.ToLower)) Then
                                    SyncLock infoText
                                        infoText.AppendLine(fl.Name)
                                    End SyncLock
                                    Dim rsch As ltfsindex = ltfsindex.FromSchemaText(sch)
                                    Dim qf As New List(Of ltfsindex.directory)
                                    qf.AddRange(rsch._directory)
                                    While qf.Count > 0
                                        Dim qf2 As New List(Of ltfsindex.directory)
                                        For Each d As ltfsindex.directory In qf
                                            For Each fr As ltfsindex.file In d.contents._file
                                                fr.extendedattributes.Add(New ltfsindex.file.xattr With {.key = "Barcode", .value = fl.Name.Substring(0, fl.Name.Length - fl.Extension.Length)})
                                            Next
                                            qf2.AddRange(d.contents._directory)
                                        Next
                                        qf = qf2
                                    End While
                                    result._directory(0).contents._file.AddRange(rsch._directory(0).contents._file)
                                    result._directory(0).contents._directory.AddRange(rsch._directory(0).contents._directory)
                                End If
                            Catch ex As Exception
                                LogFileOperationWarning("Merge", fl.FullName, ex)
                            End Try
                            Threading.Interlocked.Increment(progval)
                        End Sub)
                    Dim q As New List(Of ltfsindex.directory)
                    q.Add(result._directory(0))
                    While q.Count > 0
                        Dim q2 As New List(Of ltfsindex.directory)
                        For Each d As ltfsindex.directory In q
                            With d.contents._directory
                                For i As Integer = .Count - 1 To 0 Step -1
                                    If d.contents._directory(i) Is Nothing Then
                                        d.contents._directory.RemoveAt(i)
                                    Else
                                        If d.contents._directory(i).name Is Nothing Then d.contents._directory(i).name = ""
                                    End If
                                Next
                                For i As Integer = .Count - 1 To 1 Step -1
                                    For j As Integer = 0 To i - 1
                                        If .ElementAt(i).name.Equals(.ElementAt(j).name) Then
                                            .ElementAt(j).contents._file.AddRange(.ElementAt(i).contents._file)
                                            .ElementAt(j).contents._directory.AddRange(.ElementAt(i).contents._directory)
                                            .RemoveAt(i)
                                            Exit For
                                        End If

                                    Next
                                Next
                            End With
                            q2.AddRange(d.contents._directory)
                        Next
                        q = q2
                    End While

                    result._directory(0).contents._directory.Sort(New Comparison(Of ltfsindex.directory)(
                                                                  Function(a As ltfsindex.directory, b As ltfsindex.directory) As Integer
                                                                      Return a.name.CompareTo(b.name)
                                                                  End Function))
                    result._directory(0).contents._file.Sort(New Comparison(Of ltfsindex.file)(
                                                             Function(a As ltfsindex.file, b As ltfsindex.file) As Integer
                                                                 If a.GetXAttr("Barcode") IsNot Nothing AndAlso
                                                                    b.GetXAttr("Barcode") IsNot Nothing AndAlso
                                                                    a.GetXAttr("Barcode") <> b.GetXAttr("Barcode") Then
                                                                     Return a.GetXAttr("Barcode").CompareTo(b.GetXAttr("Barcode"))
                                                                 End If
                                                                 Return a.name.CompareTo(b.name)
                                                             End Function))

                    schema = result
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
                                Dim sch As String = IO.File.ReadAllText(fl.FullName)
                                If If(My.Settings.Application_CaseSensitiveSearch, sch.Contains(patt), sch.ToLower().Contains(patt.ToLower)) Then
                                    Dim UNum As Integer = 0
                                    Dim result As New Text.StringBuilder
                                    result.AppendLine(fl.Name)
                                    Dim rsch As ltfsindex = ltfsindex.FromSchemaText(sch)
                                    Dim q As New List(Of ltfsindex.directory)
                                    q.AddRange(rsch._directory)
                                    While q.Count > 0
                                        Dim q2 As New List(Of ltfsindex.directory)
                                        For Each d As ltfsindex.directory In q
                                            For Each f2 As ltfsindex.file In d.contents._file
                                                If f2.sha1.Length <> 40 Then
                                                    result.AppendLine($"--[{f2.fileuid}]{f2.name}")
                                                    Threading.Interlocked.Increment(UNum)
                                                End If
                                            Next
                                            q2.AddRange(d.contents._directory)
                                        Next
                                        q = q2
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
