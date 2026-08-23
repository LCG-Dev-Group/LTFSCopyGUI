Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Net
Imports System.Threading
Imports System.Threading.Tasks
Imports Serilog
Imports Serilog.Context
Imports Zhaobang.FtpServer.Authenticate
Imports Zhaobang.FtpServer.Connections
Imports Zhaobang.FtpServer.File

Public Class FTPService
    Private ReadOnly _logSessionId As String = $"ftp-service-{Guid.NewGuid().ToString("N").Substring(0, 8)}"
    Private ReadOnly _lifecycleSync As New Object
    Private _server As Zhaobang.FtpServer.FtpServer
    Private _serverTask As Task
    Private _stopTokenSource As CancellationTokenSource

    Public TapeDrive As String
    Public BlockSize As Integer = 524288
    Public ExtraPartitionCount As Integer = 1
    Public port As Integer
    Public schema As ltfsindex

    ' Leave Username and Password empty to use anonymous FTP by default.
    ' When Username is set, that exact username/password pair is also
    ' accepted. Anonymous access remains enabled unless AllowAnonymous is
    ' explicitly set to False.
    Public Username As String = String.Empty
    Public Password As String = String.Empty
    Public AllowAnonymous As Boolean = True

    Public Event LogPrint(s As String)

    Private Class ConfiguredAuthenticator
        Implements IAuthenticator

        Private ReadOnly _username As String
        Private ReadOnly _password As String
        Private ReadOnly _allowAnonymous As Boolean

        Public Sub New(username As String, password As String, allowAnonymous As Boolean)
            _username = If(username, String.Empty)
            _password = If(password, String.Empty)
            _allowAnonymous = allowAnonymous
        End Sub

        Public Function Authenticate(userName As String, password As String) As Boolean Implements IAuthenticator.Authenticate
            If _allowAnonymous AndAlso
               (String.Equals(userName, "anonymous", StringComparison.OrdinalIgnoreCase) OrElse
                String.Equals(userName, "ftp", StringComparison.OrdinalIgnoreCase)) Then
                Return True
            End If

            If String.IsNullOrWhiteSpace(_username) Then Return False
            Return String.Equals(userName, _username, StringComparison.Ordinal) AndAlso
                   String.Equals(If(password, String.Empty), _password, StringComparison.Ordinal)
        End Function
    End Class

    Private Class LTFSFileProviderFactory
        Implements IFileProviderFactory

        Private ReadOnly _root As ltfsindex.directory
        Private ReadOnly _tapeDrive As String
        Private ReadOnly _blockSize As Integer
        Private ReadOnly _extraPartitionCount As Integer
        Private ReadOnly _logHandler As Action(Of String)

        Public Sub New(root As ltfsindex.directory,
                       tapeDrive As String,
                       blockSize As Integer,
                       extraPartitionCount As Integer,
                       logHandler As Action(Of String))
            _root = root
            _tapeDrive = tapeDrive
            _blockSize = blockSize
            _extraPartitionCount = extraPartitionCount
            _logHandler = logHandler
        End Sub

        Public Function GetProvider(user As String) As IFileProvider Implements IFileProviderFactory.GetProvider
            Return New LTFSFileProvider(_root, _tapeDrive, _blockSize, _extraPartitionCount, _logHandler)
        End Function
    End Class

    Private Class LTFSFileProvider
        Implements IMLstFileProvider

        Private ReadOnly _logSessionId As String = $"ftp-filesystem-{Guid.NewGuid().ToString("N").Substring(0, 8)}"
        Private ReadOnly _root As ltfsindex.directory
        Private ReadOnly _tapeDrive As String
        Private ReadOnly _blockSize As Integer
        Private ReadOnly _extraPartitionCount As Integer
        Private ReadOnly _logHandler As Action(Of String)
        Private _workingStack As List(Of ltfsindex.directory)

        Private Class ResolvedPath
            Public Property Directory As ltfsindex.directory
            Public Property File As ltfsindex.file
            Public Property DirectoryStack As List(Of ltfsindex.directory)
        End Class

        Public Sub New(root As ltfsindex.directory,
                       tapeDrive As String,
                       blockSize As Integer,
                       extraPartitionCount As Integer,
                       logHandler As Action(Of String))
            If root Is Nothing Then Throw New ArgumentNullException(NameOf(root))

            _root = root
            _tapeDrive = tapeDrive
            _blockSize = blockSize
            _extraPartitionCount = extraPartitionCount
            _logHandler = logHandler
            _workingStack = New List(Of ltfsindex.directory) From {root}

            LogInformation("LTFS FTP provider created. TapeDrive={TapeDrive} BlockSize={BlockSize} ExtraPartitionCount={ExtraPartitionCount}.",
                           _tapeDrive, _blockSize, _extraPartitionCount)
        End Sub

        Public Function GetWorkingDirectory() As String Implements IFileProvider.GetWorkingDirectory
            If _workingStack.Count <= 1 Then Return "/"

            Dim names As New List(Of String)
            For i As Integer = 1 To _workingStack.Count - 1
                names.Add(_workingStack(i).name)
            Next
            Return "/" & String.Join("/", names.ToArray())
        End Function

        Public Function SetWorkingDirectory(path As String) As Boolean Implements IFileProvider.SetWorkingDirectory
            Dim resolved As ResolvedPath = ResolvePath(path)
            If resolved Is Nothing OrElse resolved.Directory Is Nothing Then Return False

            _workingStack = New List(Of ltfsindex.directory)(resolved.DirectoryStack)
            LogDebug("FTP working directory changed. DirectoryName={DirectoryName}.", GetWorkingDirectory())
            Return True
        End Function

        Public Function CreateDirectoryAsync(path As String) As Task Implements IFileProvider.CreateDirectoryAsync
            Throw ReadOnlyOperation("CreateDirectory", path)
        End Function

        Public Function DeleteDirectoryAsync(path As String) As Task Implements IFileProvider.DeleteDirectoryAsync
            Throw ReadOnlyOperation("DeleteDirectory", path)
        End Function

        Public Function DeleteAsync(path As String) As Task Implements IFileProvider.DeleteAsync
            Throw ReadOnlyOperation("Delete", path)
        End Function

        Public Function RenameAsync(fromPath As String, toPath As String) As Task Implements IFileProvider.RenameAsync
            Throw ReadOnlyOperation("Rename", $"{fromPath} -> {toPath}")
        End Function

        Public Function OpenFileForReadAsync(path As String) As Task(Of Stream) Implements IFileProvider.OpenFileForReadAsync
            Dim resolved As ResolvedPath = RequirePath(path)
            If resolved.File Is Nothing Then
                Throw New FileNoAccessException($"Path '{path}' is not a file.")
            End If

            Dim fileInfo As ltfsindex.file = resolved.File
            LogInformation("FTP file read started. FileName={FileName} FileLength={FileLength}.", fileInfo.name, fileInfo.length)
            RaiseLog($"OpenFileForReadAsync file={fileInfo.name}")

            Dim input As New IOManager.LTFSFileStream(fileInfo, _tapeDrive, _blockSize, _extraPartitionCount)
            AddHandler input.LogPrint, Sub(message As String)
                                           RaiseLog(message)
                                       End Sub

            Dim result As Stream = New BufferedStream(input, TapeUtils.GlobalBlockLimit)
            LogInformation("FTP file read stream opened. FileName={FileName}.", fileInfo.name)
            Return Task.FromResult(result)
        End Function

        Public Function OpenFileForWriteAsync(path As String) As Task(Of Stream) Implements IFileProvider.OpenFileForWriteAsync
            Throw ReadOnlyOperation("OpenFileForWrite", path)
        End Function

        Public Function CreateFileForWriteAsync(path As String) As Task(Of Stream) Implements IFileProvider.CreateFileForWriteAsync
            Throw ReadOnlyOperation("CreateFileForWrite", path)
        End Function

        Public Function GetNameListingAsync(path As String) As Task(Of IEnumerable(Of String)) Implements IFileProvider.GetNameListingAsync
            Dim resolved As ResolvedPath = RequirePath(path)
            Dim result As New List(Of String)

            If resolved.File IsNot Nothing Then
                result.Add(resolved.File.name)
            Else
                AddChildNames(resolved.Directory, result)
            End If

            LogDebug("FTP name listing completed. Path={Path} EntryCount={EntryCount}.", path, result.Count)
            Return Task.FromResult(Of IEnumerable(Of String))(result)
        End Function

        Public Function GetListingAsync(path As String) As Task(Of IEnumerable(Of FileSystemEntry)) Implements IFileProvider.GetListingAsync
            Dim resolved As ResolvedPath = RequirePath(path)
            Dim result As New List(Of FileSystemEntry)

            If resolved.File IsNot Nothing Then
                result.Add(ToFileSystemEntry(resolved.File))
            Else
                AddChildEntries(resolved.Directory, result)
            End If

            LogDebug("FTP directory listing completed. Path={Path} EntryCount={EntryCount}.", path, result.Count)
            Return Task.FromResult(Of IEnumerable(Of FileSystemEntry))(result)
        End Function

        Public Function GetItemAsync(path As String) As Task(Of FileSystemEntry) Implements IMLstFileProvider.GetItemAsync
            Dim resolved As ResolvedPath = RequirePath(path)
            If resolved.File IsNot Nothing Then
                Return Task.FromResult(ToFileSystemEntry(resolved.File))
            End If

            Return Task.FromResult(ToFileSystemEntry(resolved.Directory,
                                                     resolved.Directory Is _root))
        End Function

        Public Function GetChildItems(path As String) As Task(Of IEnumerable(Of FileSystemEntry)) Implements IMLstFileProvider.GetChildItems
            Dim resolved As ResolvedPath = RequirePath(path)
            If resolved.File IsNot Nothing Then
                Throw New ArgumentException($"Path '{path}' is not a directory.")
            End If

            Dim result As New List(Of FileSystemEntry)
            AddChildEntries(resolved.Directory, result)
            Return Task.FromResult(Of IEnumerable(Of FileSystemEntry))(result)
        End Function

        Private Function ResolvePath(path As String) As ResolvedPath
            Dim normalized As String = If(path, String.Empty).Trim().Replace(ChrW(92), "/"c)
            Dim isAbsolute As Boolean = normalized.StartsWith("/", StringComparison.Ordinal)
            Dim segments() As String = normalized.Split(New Char() {"/"c}, StringSplitOptions.RemoveEmptyEntries)
            Dim stack As New List(Of ltfsindex.directory)

            If isAbsolute Then
                stack.Add(_root)
            Else
                stack.AddRange(_workingStack)
            End If

            Dim fileResult As ltfsindex.file = Nothing
            For i As Integer = 0 To segments.Length - 1
                Dim segment As String = segments(i).Trim()
                If segment.Length = 0 OrElse segment = "." Then Continue For

                If segment = ".." Then
                    If stack.Count > 1 Then stack.RemoveAt(stack.Count - 1)
                    Continue For
                End If

                Dim current As ltfsindex.directory = stack(stack.Count - 1)
                Dim childDirectory As ltfsindex.directory = FindDirectory(current, segment)
                If childDirectory IsNot Nothing Then
                    stack.Add(childDirectory)
                    Continue For
                End If

                fileResult = FindFile(current, segment)
                If fileResult Is Nothing OrElse i <> segments.Length - 1 Then Return Nothing
                Exit For
            Next

            If fileResult IsNot Nothing Then
                Return New ResolvedPath With {
                    .File = fileResult,
                    .DirectoryStack = stack
                }
            End If

            Return New ResolvedPath With {
                .Directory = stack(stack.Count - 1),
                .DirectoryStack = stack
            }
        End Function

        Private Function RequirePath(path As String) As ResolvedPath
            Dim resolved As ResolvedPath = ResolvePath(path)
            If resolved Is Nothing Then Throw New FileNoAccessException($"Path '{path}' does not exist.")
            Return resolved
        End Function

        Private Shared Function FindDirectory(parent As ltfsindex.directory, name As String) As ltfsindex.directory
            If parent Is Nothing OrElse parent.contents Is Nothing OrElse parent.contents._directory Is Nothing Then Return Nothing

            For Each child As ltfsindex.directory In parent.contents._directory
                If String.Equals(child.name, name, StringComparison.OrdinalIgnoreCase) Then Return child
            Next
            Return Nothing
        End Function

        Private Shared Function FindFile(parent As ltfsindex.directory, name As String) As ltfsindex.file
            If parent Is Nothing OrElse parent.contents Is Nothing OrElse parent.contents._file Is Nothing Then Return Nothing

            For Each child As ltfsindex.file In parent.contents._file
                If String.Equals(child.name, name, StringComparison.OrdinalIgnoreCase) Then Return child
            Next
            Return Nothing
        End Function

        Private Shared Sub AddChildNames(parent As ltfsindex.directory, result As List(Of String))
            If parent Is Nothing OrElse parent.contents Is Nothing Then Return
            If parent.contents._directory IsNot Nothing Then
                For Each child As ltfsindex.directory In parent.contents._directory
                    result.Add(child.name)
                Next
            End If
            If parent.contents._file IsNot Nothing Then
                For Each child As ltfsindex.file In parent.contents._file
                    result.Add(child.name)
                Next
            End If
        End Sub

        Private Shared Sub AddChildEntries(parent As ltfsindex.directory, result As List(Of FileSystemEntry))
            If parent Is Nothing OrElse parent.contents Is Nothing Then Return
            If parent.contents._directory IsNot Nothing Then
                For Each child As ltfsindex.directory In parent.contents._directory
                    result.Add(ToFileSystemEntry(child, False))
                Next
            End If
            If parent.contents._file IsNot Nothing Then
                For Each child As ltfsindex.file In parent.contents._file
                    result.Add(ToFileSystemEntry(child))
                Next
            End If
        End Sub

        Private Shared Function ToFileSystemEntry(fileInfo As ltfsindex.file) As FileSystemEntry
            Return New FileSystemEntry With {
                .Name = fileInfo.name,
                .LastWriteTime = GetEntryTime(fileInfo.modifytime, fileInfo.changetime, fileInfo.creationtime),
                .Length = fileInfo.length,
                .IsDirectory = False,
                .IsReadOnly = fileInfo.[readonly]
            }
        End Function

        Private Shared Function ToFileSystemEntry(directoryInfo As ltfsindex.directory, isRoot As Boolean) As FileSystemEntry
            Return New FileSystemEntry With {
                .Name = If(isRoot, "/", directoryInfo.name),
                .LastWriteTime = GetEntryTime(directoryInfo.modifytime, directoryInfo.changetime, directoryInfo.creationtime),
                .Length = 0,
                .IsDirectory = True,
                .IsReadOnly = directoryInfo.[readonly]
            }
        End Function

        Private Shared Function GetEntryTime(ParamArray values() As String) As Date
            For Each value As String In values
                If Not String.IsNullOrWhiteSpace(value) Then
                    Return FTPService.ParseTimeStamp(value).ToUniversalTime()
                End If
            Next
            Return Date.UtcNow
        End Function

        Private Function ReadOnlyOperation(operation As String, path As String) As FileNoAccessException
            LogWarning("FTP read-only operation rejected. Operation={Operation} Path={Path}.", operation, path)
            Return New FileNoAccessException("The LTFS FTP service is read-only.")
        End Function

        Private Sub RaiseLog(message As String)
            If _logHandler IsNot Nothing Then _logHandler(message)
        End Sub

        Private Sub LogInformation(messageTemplate As String, ParamArray values() As Object)
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(LTFSFileProvider))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FTP")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                        Log.Information(messageTemplate, values)
                    End Using
                End Using
            End Using
        End Sub

        Private Sub LogDebug(messageTemplate As String, ParamArray values() As Object)
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(LTFSFileProvider))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FTP")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                        Log.Debug(messageTemplate, values)
                    End Using
                End Using
            End Using
        End Sub

        Private Sub LogWarning(messageTemplate As String, ParamArray values() As Object)
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(LTFSFileProvider))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FTP")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                        Log.Warning(messageTemplate, values)
                    End Using
                End Using
            End Using
        End Sub
    End Class

    Public Sub StartService()
        If schema Is Nothing OrElse schema._directory Is Nothing OrElse schema._directory.Count = 0 Then
            LogWarning("FTP service start was skipped because no schema is loaded.")
            Exit Sub
        End If
        If port < 1 OrElse port > 65535 Then Throw New ArgumentOutOfRangeException(NameOf(port))

        SyncLock _lifecycleSync
            If _serverTask IsNot Nothing Then Throw New InvalidOperationException("The FTP service is already running.")

            LogInformation("FTP service start requested. TapeDrive={TapeDrive} Port={Port} BlockSize={BlockSize} ExtraPartitionCount={ExtraPartitionCount}.",
                           TapeDrive, port, BlockSize, ExtraPartitionCount)

            Dim root As ltfsindex.directory = schema._directory(0)
            Dim fileProviderFactory As New LTFSFileProviderFactory(
                root,
                TapeDrive,
                BlockSize,
                ExtraPartitionCount,
                Sub(message As String)
                    RaiseEvent LogPrint(message)
                End Sub)
            Dim stopTokenSource As New CancellationTokenSource
            Dim server As New Zhaobang.FtpServer.FtpServer(
                New IPEndPoint(IPAddress.Any, port),
                fileProviderFactory,
                New LocalDataConnectionFactory(),
                New ConfiguredAuthenticator(Username, Password, AllowAnonymous))

            Try
                Dim serverTask As Task = server.RunAsync(stopTokenSource.Token)
                If serverTask.IsFaulted Then serverTask.GetAwaiter().GetResult()

                _server = server
                _serverTask = serverTask
                _stopTokenSource = stopTokenSource
                LogInformation("FTP service started. Port={Port}.", port)
            Catch ex As Exception
                stopTokenSource.Cancel()
                stopTokenSource.Dispose()
                LogError(ex, "FTP service start failed. Port={Port}.", port)
                Throw
            End Try
        End SyncLock
    End Sub

    Public Sub StopService()
        StopServiceAsync().GetAwaiter().GetResult()
    End Sub

    Public Async Function StopServiceAsync() As Task
        Dim stopTokenSource As CancellationTokenSource
        Dim serverTask As Task

        SyncLock _lifecycleSync
            stopTokenSource = _stopTokenSource
            serverTask = _serverTask
        End SyncLock

        If stopTokenSource Is Nothing OrElse serverTask Is Nothing Then Return

        LogInformation("FTP service stop requested.")
        Try
            stopTokenSource.Cancel()
            Await serverTask.ConfigureAwait(False)
            LogInformation("FTP service stopped.")
        Catch ex As Exception
            LogError(ex, "FTP service stop failed.")
            Throw
        Finally
            SyncLock _lifecycleSync
                If Object.ReferenceEquals(_stopTokenSource, stopTokenSource) Then
                    _stopTokenSource = Nothing
                    _serverTask = Nothing
                    _server = Nothing
                End If
            End SyncLock
            stopTokenSource.Dispose()
        End Try
    End Function

    Private Sub LogInformation(messageTemplate As String, ParamArray values() As Object)
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(FTPService))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FTP")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Log.Information(messageTemplate, values)
                End Using
            End Using
        End Using
    End Sub

    Private Sub LogWarning(messageTemplate As String, ParamArray values() As Object)
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(FTPService))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FTP")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Log.Warning(messageTemplate, values)
                End Using
            End Using
        End Using
    End Sub

    Private Sub LogError(exception As Exception, messageTemplate As String, ParamArray values() As Object)
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(FTPService))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FTP")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Log.Error(exception, messageTemplate, values)
                End Using
            End Using
        End Using
    End Sub

    Public Shared Function ParseTimeStamp(t As String) As Date
        'yyyy-MM-ddTHH:mm:ss.fffffff00Z
        Try
            Return Date.ParseExact(t, "yyyy-MM-ddTHH:mm:ss.fffffff00Z", Globalization.CultureInfo.InvariantCulture)
        Catch ex As Exception
            Return New Date()
        End Try
    End Function
End Class
