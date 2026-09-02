Imports System.IO
Imports System.IO.Pipes
Imports System.Diagnostics
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json
Imports Newtonsoft.Json.Linq

Public NotInheritable Class DirectTapeCopyClipboardDescriptor
    Public Property Version As Integer = DirectTapeCopyProtocol.ProtocolVersion
    Public Property PipeName As String
    Public Property Token As String
    Public Property VolumeUuid As Guid
    Public Property GenerationNumber As ULong
End Class

Public NotInheritable Class DirectTapeCopyManifest
    Public Property Version As Integer = DirectTapeCopyProtocol.ProtocolVersion
    Public Property VolumeUuid As Guid
    Public Property GenerationNumber As ULong
    Public Property SourceBlockSize As Integer
    Public Property Files As New List(Of DirectTapeCopyFile)
    Public Property Directories As New List(Of DirectTapeCopyDirectory)
End Class

Public NotInheritable Class DirectTapeCopyDirectory
    Public Property RelativePath As String
    Public Property [ReadOnly] As Boolean
    Public Property CreationTime As String
    Public Property ChangeTime As String
    Public Property ModifyTime As String
    Public Property AccessTime As String
    Public Property BackupTime As String
End Class

Public NotInheritable Class DirectTapeCopyFile
    Public Property Ordinal As Long
    Public Property RelativePath As String
    Public Property Length As Long
    Public Property [ReadOnly] As Boolean
    Public Property OpenForWrite As Boolean
    Public Property CreationTime As String
    Public Property ChangeTime As String
    Public Property ModifyTime As String
    Public Property AccessTime As String
    Public Property BackupTime As String
    Public Property Symlink As String
    Public Property Xattrs As New List(Of DirectTapeCopyXattr)
    Public Property Extents As New List(Of DirectTapeCopyExtent)
End Class

Public NotInheritable Class DirectTapeCopyXattr
    Public Property Key As String
    Public Property Value As String
End Class

Public NotInheritable Class DirectTapeCopyExtent
    Public Property FileOffset As Long
    Public Property ByteCount As Long
    Public Property StartBlock As Long
    Public Property ByteOffset As Long
    Public Property Partition As Integer
End Class

Public NotInheritable Class DirectTapeCopyStartRequest
    Public Property BridgeName As String
    Public Property MaterialOrdinals As New List(Of Long)
End Class

Friend NotInheritable Class DirectTapeCopyProtocol
    Friend Const ProtocolVersion As Integer = 1
    Friend Const ClipboardFormat As String = "LTFSCopyGUI.DirectTapeCopy.v1"
    Private Const MaximumFrameBytes As Integer = 4 * 1024 * 1024
    Private Shared ReadOnly Utf8 As New UTF8Encoding(False, True)

    Friend Shared Sub WriteMessage(stream As Stream, messageType As String, payload As Object)
        Dim envelope As New JObject From {
            {"type", messageType},
            {"payload", If(payload Is Nothing, JValue.CreateNull(), JToken.FromObject(payload))}}
        Dim bytes = Utf8.GetBytes(envelope.ToString(Formatting.None))
        If bytes.Length <= 0 OrElse bytes.Length > MaximumFrameBytes Then Throw New InvalidDataException($"Direct-copy control frame is too large: {bytes.Length}")
        Dim length = BitConverter.GetBytes(bytes.Length)
        stream.Write(length, 0, length.Length)
        stream.Write(bytes, 0, bytes.Length)
        stream.Flush()
    End Sub

    Friend Shared Function ReadMessage(stream As Stream) As JObject
        Dim lengthBytes(3) As Byte
        ReadExactly(stream, lengthBytes, 0, lengthBytes.Length)
        Dim length = BitConverter.ToInt32(lengthBytes, 0)
        If length <= 0 OrElse length > MaximumFrameBytes Then Throw New InvalidDataException($"Invalid direct-copy control frame length: {length}")
        Dim data(length - 1) As Byte
        ReadExactly(stream, data, 0, data.Length)
        Return JObject.Parse(Utf8.GetString(data))
    End Function

    Friend Shared Function MessageType(message As JObject) As String
        Return CStr(message("type"))
    End Function

    Friend Shared Function Payload(Of T)(message As JObject) As T
        Dim value = message("payload")
        If value Is Nothing OrElse value.Type = JTokenType.Null Then Return Nothing
        Return value.ToObject(Of T)()
    End Function

    Private Shared Sub ReadExactly(stream As Stream, buffer As Byte(), offset As Integer, count As Integer)
        While count > 0
            Dim read = stream.Read(buffer, offset, count)
            If read <= 0 Then Throw New EndOfStreamException("Direct-copy peer disconnected")
            offset += read
            count -= read
        End While
    End Sub

    Friend Shared Sub WriteManifest(stream As Stream, manifest As DirectTapeCopyManifest)
        Dim header As New DirectTapeCopyManifest With {
            .Version = manifest.Version,
            .VolumeUuid = manifest.VolumeUuid,
            .GenerationNumber = manifest.GenerationNumber,
            .SourceBlockSize = manifest.SourceBlockSize,
            .Files = Nothing,
            .Directories = Nothing}
        WriteMessage(stream, "manifest", header)
        WriteBatches(stream, "directories", manifest.Directories)
        WriteBatches(stream, "files", manifest.Files)
        WriteMessage(stream, "manifest-end", New With {
                         .fileCount = manifest.Files.Count,
                         .directoryCount = manifest.Directories.Count})
    End Sub

    Private Shared Sub WriteBatches(Of T)(stream As Stream, messageType As String, values As IList(Of T))
        If values Is Nothing OrElse values.Count = 0 Then Return
        Dim offset As Integer = 0
        While offset < values.Count
            Dim count = Math.Min(256, values.Count - offset)
            While True
                Dim batch = values.Skip(offset).Take(count).ToList()
                Try
                    WriteMessage(stream, messageType, batch)
                    offset += count
                    Exit While
                Catch ex As InvalidDataException When count > 1
                    count = Math.Max(1, count \ 2)
                End Try
            End While
        End While
    End Sub

    Friend Shared Function ReadManifest(stream As Stream) As DirectTapeCopyManifest
        Dim first = ReadMessage(stream)
        If MessageType(first) <> "manifest" Then Throw New InvalidDataException("Direct-copy source did not send a manifest")
        Dim result = Payload(Of DirectTapeCopyManifest)(first)
        result.Files = New List(Of DirectTapeCopyFile)
        result.Directories = New List(Of DirectTapeCopyDirectory)
        While True
            Dim message = ReadMessage(stream)
            Select Case MessageType(message)
                Case "files"
                    result.Files.AddRange(Payload(Of List(Of DirectTapeCopyFile))(message))
                Case "directories"
                    result.Directories.AddRange(Payload(Of List(Of DirectTapeCopyDirectory))(message))
                Case "manifest-end"
                    Exit While
                Case "error"
                    Throw New IOException(Payload(Of String)(message))
                Case Else
                    Throw New InvalidDataException($"Unexpected direct-copy message: {MessageType(message)}")
            End Select
        End While
        Return result
    End Function

    Friend Shared Function IsSafeRelativePath(value As String) As Boolean
        If String.IsNullOrWhiteSpace(value) OrElse Path.IsPathRooted(value) Then Return False
        Dim normalized = value.Replace("/"c, "\"c)
        For Each component In normalized.Split(New Char() {"\"c}, StringSplitOptions.None)
            If String.IsNullOrEmpty(component) OrElse component = "." OrElse component = ".." Then Return False
            If component.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 Then Return False
        Next
        Return True
    End Function
End Class

Public NotInheritable Class DirectTapeCopySourceOffer
    Implements IDisposable

    Private ReadOnly _manifest As DirectTapeCopyManifest
    Private ReadOnly _validateSource As Func(Of Boolean)
    Private ReadOnly _streamSource As Action(Of DirectTapeCopyStartRequest, CancellationToken)
    Private ReadOnly _cancellation As New CancellationTokenSource()
    Private ReadOnly _thread As Thread
    Private _server As NamedPipeServerStream
    Private _disposed As Integer

    Public ReadOnly Property Descriptor As DirectTapeCopyClipboardDescriptor

    Public Sub New(manifest As DirectTapeCopyManifest,
                   validateSource As Func(Of Boolean),
                   streamSource As Action(Of DirectTapeCopyStartRequest, CancellationToken))
        If manifest Is Nothing Then Throw New ArgumentNullException(NameOf(manifest))
        _manifest = manifest
        _validateSource = validateSource
        _streamSource = streamSource
        Dim id = Guid.NewGuid().ToString("N")
        Descriptor = New DirectTapeCopyClipboardDescriptor With {
            .PipeName = $"LTFSCopyGUI.DirectTapeCopy.{Process.GetCurrentProcess().Id}.{id}",
            .Token = Guid.NewGuid().ToString("N"),
            .VolumeUuid = manifest.VolumeUuid,
            .GenerationNumber = manifest.GenerationNumber}
        _thread = New Thread(AddressOf Serve) With {.IsBackground = True, .Name = "LTFS direct-copy source control"}
        _thread.Start()
    End Sub

    Private Sub Serve()
        Try
            _server = New NamedPipeServerStream(Descriptor.PipeName,
                                                PipeDirection.InOut,
                                                1,
                                                PipeTransmissionMode.Byte,
                                                PipeOptions.Asynchronous,
                                                64 * 1024,
                                                64 * 1024)
            _server.WaitForConnection()
            Dim hello = DirectTapeCopyProtocol.ReadMessage(_server)
            If DirectTapeCopyProtocol.MessageType(hello) <> "hello" OrElse
               Not String.Equals(DirectTapeCopyProtocol.Payload(Of String)(hello), Descriptor.Token, StringComparison.Ordinal) Then
                Throw New UnauthorizedAccessException("Invalid direct-copy offer token")
            End If
            If _validateSource IsNot Nothing AndAlso Not _validateSource() Then Throw New IOException("Source LTFS volume or index generation changed")
            DirectTapeCopyProtocol.WriteManifest(_server, _manifest)
            Dim startMessage = DirectTapeCopyProtocol.ReadMessage(_server)
            If DirectTapeCopyProtocol.MessageType(startMessage) <> "start" Then Throw New InvalidDataException("Direct-copy target did not send a start request")
            Dim request = DirectTapeCopyProtocol.Payload(Of DirectTapeCopyStartRequest)(startMessage)
            If request Is Nothing OrElse String.IsNullOrWhiteSpace(request.BridgeName) Then Throw New InvalidDataException("Direct-copy start request is invalid")
            If _validateSource IsNot Nothing AndAlso Not _validateSource() Then Throw New IOException("Source LTFS volume or index generation changed")

            Threading.Tasks.Task.Run(
                Sub()
                    Try
                        Dim message = DirectTapeCopyProtocol.ReadMessage(_server)
                        If DirectTapeCopyProtocol.MessageType(message) = "cancel" Then _cancellation.Cancel()
                    Catch
                        _cancellation.Cancel()
                    End Try
                End Sub)
            _streamSource(request, _cancellation.Token)
            If _server.IsConnected Then DirectTapeCopyProtocol.WriteMessage(_server, "complete", Nothing)
        Catch ex As OperationCanceledException
        Catch ex As Exception
            Try
                If _server IsNot Nothing AndAlso _server.IsConnected Then DirectTapeCopyProtocol.WriteMessage(_server, "error", ex.Message)
            Catch
            End Try
        Finally
            Try
                If _server IsNot Nothing Then _server.Dispose()
            Catch
            End Try
            _server = Nothing
        End Try
    End Sub

    Public Sub Cancel()
        _cancellation.Cancel()
        Try
            If _server IsNot Nothing Then _server.Dispose()
        Catch
        End Try
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        If Interlocked.Exchange(_disposed, 1) <> 0 Then Return
        Cancel()
        _cancellation.Dispose()
    End Sub
End Class

Public NotInheritable Class DirectTapeCopyTargetSession
    Implements IDisposable

    Private ReadOnly _client As NamedPipeClientStream
    Private _disposed As Integer
    Public ReadOnly Property Descriptor As DirectTapeCopyClipboardDescriptor
    Public ReadOnly Property Manifest As DirectTapeCopyManifest

    Private Sub New(descriptor As DirectTapeCopyClipboardDescriptor,
                    client As NamedPipeClientStream,
                    manifest As DirectTapeCopyManifest)
        Me.Descriptor = descriptor
        _client = client
        Me.Manifest = manifest
    End Sub

    Public Shared Function Connect(descriptor As DirectTapeCopyClipboardDescriptor, timeoutMilliseconds As Integer) As DirectTapeCopyTargetSession
        If descriptor Is Nothing OrElse descriptor.Version <> DirectTapeCopyProtocol.ProtocolVersion Then Throw New InvalidDataException("Unsupported direct-copy clipboard descriptor")
        Dim client As New NamedPipeClientStream(".", descriptor.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous)
        Try
            client.Connect(timeoutMilliseconds)
            DirectTapeCopyProtocol.WriteMessage(client, "hello", descriptor.Token)
            Dim manifest = DirectTapeCopyProtocol.ReadManifest(client)
            If manifest.Version <> DirectTapeCopyProtocol.ProtocolVersion OrElse
               manifest.VolumeUuid <> descriptor.VolumeUuid OrElse
               manifest.GenerationNumber <> descriptor.GenerationNumber Then
                Throw New InvalidDataException("Direct-copy manifest does not match the clipboard offer")
            End If
            Return New DirectTapeCopyTargetSession(descriptor, client, manifest)
        Catch
            client.Dispose()
            Throw
        End Try
    End Function

    Public Sub Start(bridgeName As String,
                     materialOrdinals As IEnumerable(Of Long),
                     Optional remoteFailure As Action(Of String) = Nothing)
        Dim request As New DirectTapeCopyStartRequest With {
            .BridgeName = bridgeName,
            .MaterialOrdinals = materialOrdinals.Distinct().OrderBy(Function(value) value).ToList()}
        DirectTapeCopyProtocol.WriteMessage(_client, "start", request)
        Threading.Tasks.Task.Run(
            Sub()
                Try
                    Dim message = DirectTapeCopyProtocol.ReadMessage(_client)
                    If DirectTapeCopyProtocol.MessageType(message) = "error" AndAlso remoteFailure IsNot Nothing Then
                        remoteFailure(DirectTapeCopyProtocol.Payload(Of String)(message))
                    End If
                Catch ex As Exception
                    If Volatile.Read(_disposed) = 0 AndAlso remoteFailure IsNot Nothing Then remoteFailure(ex.Message)
                End Try
            End Sub)
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        If Interlocked.Exchange(_disposed, 1) <> 0 Then Return
        Try
            If _client.IsConnected Then DirectTapeCopyProtocol.WriteMessage(_client, "cancel", Nothing)
        Catch
        End Try
        _client.Dispose()
    End Sub
End Class
