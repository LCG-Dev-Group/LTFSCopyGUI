Imports Microsoft.Win32.SafeHandles
Imports System.ComponentModel
Imports System.Diagnostics
Imports System.IO
Imports System.Runtime.InteropServices
Imports System.Text
Imports System.Threading

Friend NotInheritable Class DirectTapeCopyNative
    Friend Const DllName As String = "ltfscopy_fastreader.dll"
    Friend Const BridgeAbiVersion As UInteger = 1UI
    Friend Const ResultOk As Integer = 0
    Friend Const ResultTimeout As Integer = 1
    Friend Const ResultDone As Integer = 2
    Friend Const ResultBufferTooSmall As Integer = 3
    Friend Const ResultCancelled As Integer = -3
    Friend Const FlagEof As Integer = 1

    <Flags>
    Friend Enum HashFlags As UInteger
        SHA1 = 1UI << 0
        SHA256 = 1UI << 1
        SHA512 = 1UI << 2
        MD5 = 1UI << 3
        CRC32 = 1UI << 4
        BLAKE3 = 1UI << 5
        XxHash3 = 1UI << 6
        XxHash128 = 1UI << 7
    End Enum

    <StructLayout(LayoutKind.Sequential)>
    Friend Structure BridgeConfig
        Public StructSize As UInteger
        Public AbiVersion As UInteger
        Public SlotSize As UInteger
        Public Reserved As UInteger
        Public CapacityBytes As ULong
        Public HashMask As UInteger
        Public Reserved2 As UInteger
    End Structure

    <StructLayout(LayoutKind.Sequential)>
    Friend Structure NativeSlot
        Public Token As ULong
        Public FileIndex As Long
        Public FileOffset As ULong
        Public Data As IntPtr
        Public Length As UInteger
        Public Flags As UInteger
    End Structure

    <StructLayout(LayoutKind.Sequential)>
    Friend Structure NativeTapeExtent
        Public FileOffset As ULong
        Public ByteCount As ULong
        Public StartBlock As ULong
        Public ByteOffset As UInteger
        Public Partition As Byte
        <MarshalAs(UnmanagedType.ByValArray, SizeConst:=3)>
        Public Reserved As Byte()
    End Structure

    <UnmanagedFunctionPointer(CallingConvention.Winapi)>
    Friend Delegate Function TapeRetryCallback(userData As IntPtr,
                                                message As IntPtr,
                                                messageLength As UInteger,
                                                partition As Byte,
                                                block As ULong) As Integer

    <DllImport(DllName, ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Friend Shared Function lfr_bridge_abi_version() As UInteger
    End Function

    <DllImport(DllName, ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi, CharSet:=CharSet.Unicode)>
    Friend Shared Function lfr_bridge_create_consumer(name As String,
                                                       nameLength As UInteger,
                                                       ByRef config As BridgeConfig,
                                                       ByRef output As IntPtr) As Integer
    End Function

    <DllImport(DllName, ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi, CharSet:=CharSet.Unicode)>
    Friend Shared Function lfr_bridge_open_producer(name As String,
                                                     nameLength As UInteger,
                                                     ByRef output As IntPtr) As Integer
    End Function

    <DllImport(DllName, ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Friend Shared Function lfr_bridge_buffered_bytes(context As IntPtr) As ULong
    End Function

    <DllImport(DllName, ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Friend Shared Function lfr_bridge_buffer_capacity(context As IntPtr) As ULong
    End Function

    <DllImport(DllName, ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Friend Shared Function lfr_bridge_occupied_slots(context As IntPtr) As ULong
    End Function

    <DllImport(DllName, ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Friend Shared Function lfr_bridge_get_stats(context As IntPtr,
                                                 ByRef output As RustFastReaderProvider.PerformanceStats) As Integer
    End Function

    <DllImport(DllName, ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Friend Shared Function lfr_bridge_wait_until_buffered(context As IntPtr,
                                                           target As ULong,
                                                           timeoutMilliseconds As UInteger) As Integer
    End Function

    <DllImport(DllName, ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Friend Shared Function lfr_bridge_acquire_slot(context As IntPtr,
                                                    expectedFileIndex As Long,
                                                    timeoutMilliseconds As UInteger,
                                                    ByRef output As NativeSlot) As Integer
    End Function

    <DllImport(DllName, ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Friend Shared Function lfr_bridge_release_slot(context As IntPtr, token As ULong) As Integer
    End Function

    <DllImport(DllName, ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Friend Shared Function lfr_bridge_get_file_hashes(context As IntPtr,
                                                       fileIndex As Long,
                                                       <Out> buffer As Byte(),
                                                       capacity As UInteger,
                                                       ByRef written As UInteger) As Integer
    End Function

    <DllImport(DllName, ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Friend Shared Function lfr_bridge_last_error(context As IntPtr,
                                                  <Out> buffer As Byte(),
                                                  capacity As UInteger,
                                                  ByRef written As UInteger) As Integer
    End Function

    <DllImport(DllName, ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Friend Shared Function lfr_bridge_cancel(context As IntPtr) As Integer
    End Function

    <DllImport(DllName, ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Friend Shared Function lfr_bridge_producer_complete(context As IntPtr) As Integer
    End Function

    <DllImport(DllName, ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Friend Shared Function lfr_bridge_stream_tape_file(context As IntPtr,
                                                        tapeHandle As IntPtr,
                                                        fileIndex As Long,
                                                        fileLength As ULong,
                                                        sourceBlockSize As UInteger,
                                                        <[In]> extents As NativeTapeExtent(),
                                                        extentCount As UInteger,
                                                        timeoutSeconds As UInteger,
                                                        automaticRetries As UInteger,
                                                        retryCallback As TapeRetryCallback,
                                                        userData As IntPtr) As Integer
    End Function

    <DllImport(DllName, ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Friend Shared Sub lfr_bridge_destroy(context As IntPtr)
    End Sub

    Friend Shared Function FindNativeDll() As String
        Dim direct = Path.Combine(Application.StartupPath, DllName)
        If File.Exists(direct) Then Return direct
        Dim directory As New DirectoryInfo(Application.StartupPath)
        While directory IsNot Nothing
            For Each targetName As String In {"x86_64-pc-windows-msvc", "x86_64-win7-windows-msvc"}
                Dim candidate = Path.Combine(directory.FullName, "LtfsFastReader", "target", targetName, "release", DllName)
                If File.Exists(candidate) Then Return candidate
                candidate = Path.Combine(directory.FullName, "LtfsFastReader", "target", "release", DllName)
                If File.Exists(candidate) Then Return candidate
            Next
            directory = directory.Parent
        End While
        Return direct
    End Function

    Friend Shared Function LoadModule() As IntPtr
        Dim dllPath = FindNativeDll()
        If Not File.Exists(dllPath) Then Throw New DllNotFoundException($"{DllName} not found: {dllPath}")
        Dim result = Native.NativeMethods.LoadNativeLibrary(dllPath)
        If Not result.Succeeded Then Throw New Win32Exception(result.Win32Error, $"Unable to load native fast reader: {dllPath}")
        If lfr_bridge_abi_version() <> BridgeAbiVersion Then
            Native.NativeMethods.FreeLibrary(result.Handle)
            Throw New InvalidDataException("Native direct-copy bridge ABI mismatch")
        End If
        Return result.Handle
    End Function

    Friend Shared Function ParseHashes(text As String) As Dictionary(Of String, String)
        Dim result As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        If String.IsNullOrEmpty(text) Then Return result
        For Each part As String In text.Split({vbTab}, StringSplitOptions.RemoveEmptyEntries)
            Dim pair = part.Split(New Char() {"="c}, 2)
            If pair.Length = 2 Then result(pair(0)) = pair(1)
        Next
        Return result
    End Function

    Friend Shared Function ReadText(context As IntPtr,
                                    callNative As Func(Of Byte(), UInteger, TextResult)) As TextResult
        Dim buffer(2047) As Byte
        Dim result = callNative(buffer, CUInt(buffer.Length))
        If result.Code = ResultBufferTooSmall Then
            ReDim buffer(CInt(result.Written))
            result = callNative(buffer, CUInt(buffer.Length))
        End If
        If result.Code = ResultOk Then result.Text = Encoding.UTF8.GetString(buffer, 0, CInt(result.Written))
        Return result
    End Function

    Friend Structure TextResult
        Public Code As Integer
        Public Written As UInteger
        Public Text As String
    End Structure

    Friend Shared Function LastError(context As IntPtr) As String
        Dim result = ReadText(context,
                              Function(buffer, capacity)
                                  Dim written As UInteger
                                  Dim code = lfr_bridge_last_error(context, buffer, capacity, written)
                                  Return New TextResult With {.Code = code, .Written = written}
                              End Function)
        Return If(result.Text, String.Empty)
    End Function

    Friend Shared Sub ThrowResult(context As IntPtr, result As Integer, operation As String)
        If result = ResultOk Then Return
        If result = ResultCancelled Then Throw New OperationCanceledException($"Direct tape copy cancelled during {operation}")
        Dim message = If(context = IntPtr.Zero, String.Empty, LastError(context))
        If String.IsNullOrEmpty(message) Then message = $"Direct tape copy {operation} failed ({result})"
        Throw New IOException(message)
    End Sub

    Friend Shared Function BuildHashMask(files As IEnumerable(Of DirectTapeCopyFile)) As UInteger
        Dim result As HashFlags = 0
        If My.Settings.LTFSWriter_ChecksumEnabled_SHA1 Then result = result Or HashFlags.SHA1
        If My.Settings.LTFSWriter_ChecksumEnabled_SHA256 Then result = result Or HashFlags.SHA256
        If My.Settings.LTFSWriter_ChecksumEnabled_SHA512 Then result = result Or HashFlags.SHA512
        If My.Settings.LTFSWriter_ChecksumEnabled_MD5 Then result = result Or HashFlags.MD5
        If My.Settings.LTFSWriter_ChecksumEnabled_CRC32 Then result = result Or HashFlags.CRC32
        If My.Settings.LTFSWriter_ChecksumEnabled_BLAKE3 Then result = result Or HashFlags.BLAKE3
        If My.Settings.LTFSWriter_ChecksumEnabled_XxHash3 Then result = result Or HashFlags.XxHash3
        If My.Settings.LTFSWriter_ChecksumEnabled_XxHash128 Then result = result Or HashFlags.XxHash128
        If files IsNot Nothing Then
            For Each file In files
                If file Is Nothing OrElse file.Xattrs Is Nothing Then Continue For
                For Each value In file.Xattrs
                    Select Case value.Key
                        Case ltfsindex.file.xattr.HashType.SHA1 : result = result Or HashFlags.SHA1
                        Case ltfsindex.file.xattr.HashType.SHA256 : result = result Or HashFlags.SHA256
                        Case ltfsindex.file.xattr.HashType.SHA512 : result = result Or HashFlags.SHA512
                        Case ltfsindex.file.xattr.HashType.MD5 : result = result Or HashFlags.MD5
                        Case ltfsindex.file.xattr.HashType.CRC32 : result = result Or HashFlags.CRC32
                        Case ltfsindex.file.xattr.HashType.BLAKE3 : result = result Or HashFlags.BLAKE3
                        Case ltfsindex.file.xattr.HashType.XxHash3 : result = result Or HashFlags.XxHash3
                        Case ltfsindex.file.xattr.HashType.XxHash128 : result = result Or HashFlags.XxHash128
                    End Select
                Next
            Next
        End If
        Return CUInt(result)
    End Function
End Class

Public NotInheritable Class DirectTapeCopyBridgeConsumer
    Implements IDisposable, IFastReaderConsumer

    Private _context As IntPtr
    Private _module As IntPtr
    Private ReadOnly _capacity As Long
    Private _remaining As Long
    Private _disposed As Integer
    Private _peerError As String

    Public Sub New(name As String, blockSize As Integer, capacityBytes As Long, hashMask As UInteger, remainingBytes As Long)
        If String.IsNullOrWhiteSpace(name) Then Throw New ArgumentNullException(NameOf(name))
        If blockSize <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(blockSize))
        capacityBytes = (capacityBytes \ blockSize) * blockSize
        If capacityBytes < CLng(blockSize) * 2L Then Throw New ArgumentOutOfRangeException(NameOf(capacityBytes), "Direct-copy buffer requires at least two tape blocks")
        _capacity = capacityBytes
        _remaining = remainingBytes
        _module = DirectTapeCopyNative.LoadModule()
        Try
            Dim config As New DirectTapeCopyNative.BridgeConfig With {
                .StructSize = CUInt(Marshal.SizeOf(GetType(DirectTapeCopyNative.BridgeConfig))),
                .AbiVersion = DirectTapeCopyNative.BridgeAbiVersion,
                .SlotSize = CUInt(blockSize),
                .CapacityBytes = CULng(capacityBytes),
                .HashMask = hashMask}
            Dim result = DirectTapeCopyNative.lfr_bridge_create_consumer(name, CUInt(name.Length), config, _context)
            DirectTapeCopyNative.ThrowResult(_context, result, "create shared-memory consumer")
            If _context = IntPtr.Zero Then Throw New IOException("Native direct-copy bridge returned a null consumer")
        Catch
            Dispose()
            Throw
        End Try
    End Sub

    Public ReadOnly Property BufferedBytes As Long Implements IFastReaderConsumer.BufferedBytes
        Get
            Return If(_context = IntPtr.Zero, 0L, CLng(DirectTapeCopyNative.lfr_bridge_buffered_bytes(_context)))
        End Get
    End Property

    Public ReadOnly Property BufferCapacityBytes As Long Implements IFastReaderConsumer.BufferCapacityBytes
        Get
            Return If(_context = IntPtr.Zero, _capacity, CLng(DirectTapeCopyNative.lfr_bridge_buffer_capacity(_context)))
        End Get
    End Property

    Public ReadOnly Property OccupiedSlotCount As ULong Implements IFastReaderConsumer.OccupiedSlotCount
        Get
            Return If(_context = IntPtr.Zero, 0UL, DirectTapeCopyNative.lfr_bridge_occupied_slots(_context))
        End Get
    End Property

    Public ReadOnly Property RemainingBytes As Long Implements IFastReaderConsumer.RemainingBytes
        Get
            Return Math.Max(0L, Interlocked.Read(_remaining))
        End Get
    End Property

    Public Function ReadSlot(expectedFileIndex As Long, cancellationToken As CancellationToken) As RustFastReaderProvider.Slot Implements IFastReaderConsumer.ReadSlot
        ThrowIfDisposed()
        While True
            cancellationToken.ThrowIfCancellationRequested()
            Dim native As DirectTapeCopyNative.NativeSlot
            Dim result = DirectTapeCopyNative.lfr_bridge_acquire_slot(_context, expectedFileIndex, 100UI, native)
            If result = DirectTapeCopyNative.ResultOk Then
                Return New RustFastReaderProvider.Slot With {
                    .Token = native.Token,
                    .FileIndex = native.FileIndex,
                    .FileOffset = CLng(native.FileOffset),
                    .DataPtr = native.Data,
                    .Length = CInt(native.Length),
                    .Flags = CInt(native.Flags)}
            End If
            If result = DirectTapeCopyNative.ResultTimeout Then Continue While
            If result = DirectTapeCopyNative.ResultDone Then Throw New EndOfStreamException($"Direct tape source completed before file {expectedFileIndex} EOF")
            If result = DirectTapeCopyNative.ResultCancelled AndAlso Not String.IsNullOrEmpty(_peerError) Then Throw New IOException(_peerError)
            DirectTapeCopyNative.ThrowResult(_context, result, $"acquire file {expectedFileIndex} slot")
        End While
    End Function

    Public Sub AdvanceSlot(slot As RustFastReaderProvider.Slot) Implements IFastReaderConsumer.AdvanceSlot
        ThrowIfDisposed()
        DirectTapeCopyNative.ThrowResult(_context,
                                         DirectTapeCopyNative.lfr_bridge_release_slot(_context, slot.Token),
                                         "release shared-memory slot")
        If slot.Length > 0 Then Interlocked.Add(_remaining, -CLng(slot.Length))
    End Sub

    Public Function GetPerformanceStats() As RustFastReaderProvider.PerformanceStats Implements IFastReaderConsumer.GetPerformanceStats
        ThrowIfDisposed()
        Dim result As New RustFastReaderProvider.PerformanceStats With {
            .StructSize = CUInt(Marshal.SizeOf(GetType(RustFastReaderProvider.PerformanceStats)))}
        DirectTapeCopyNative.ThrowResult(_context,
                                         DirectTapeCopyNative.lfr_bridge_get_stats(_context, result),
                                         "read bridge statistics")
        Return result
    End Function

    Public Sub WaitForStreamFillFraction(fraction As Double, cancellationToken As CancellationToken) Implements IFastReaderConsumer.WaitForStreamFillFraction
        ThrowIfDisposed()
        Dim bounded = Math.Max(0.0, Math.Min(1.0, fraction))
        While True
            cancellationToken.ThrowIfCancellationRequested()
            Dim target = Math.Min(CLng(Math.Ceiling(BufferCapacityBytes * bounded)), RemainingBytes)
            If target <= 0 OrElse BufferedBytes >= target Then Return
            Dim result = DirectTapeCopyNative.lfr_bridge_wait_until_buffered(_context, CULng(target), 100UI)
            If result = DirectTapeCopyNative.ResultOk OrElse result = DirectTapeCopyNative.ResultDone Then Return
            If result = DirectTapeCopyNative.ResultTimeout Then Continue While
            If result = DirectTapeCopyNative.ResultCancelled AndAlso Not String.IsNullOrEmpty(_peerError) Then Throw New IOException(_peerError)
            DirectTapeCopyNative.ThrowResult(_context, result, "wait for bridge buffer fill")
        End While
    End Sub

    Public Function GetCompletedFileHashes(fileIndex As Long) As Dictionary(Of String, String) Implements IFastReaderConsumer.GetCompletedFileHashes
        ThrowIfDisposed()
        Dim result = DirectTapeCopyNative.ReadText(_context,
                                                    Function(buffer, capacity)
                                                        Dim written As UInteger
                                                        Dim code = DirectTapeCopyNative.lfr_bridge_get_file_hashes(_context, fileIndex, buffer, capacity, written)
                                                        Return New DirectTapeCopyNative.TextResult With {.Code = code, .Written = written}
                                                    End Function)
        DirectTapeCopyNative.ThrowResult(_context, result.Code, $"get file {fileIndex} hashes")
        Return DirectTapeCopyNative.ParseHashes(result.Text)
    End Function

    Public Sub Cancel() Implements IFastReaderConsumer.Cancel
        If _context <> IntPtr.Zero Then DirectTapeCopyNative.lfr_bridge_cancel(_context)
    End Sub

    Public Sub SetPeerError(message As String)
        _peerError = If(message, "Direct tape-copy source failed")
        Cancel()
    End Sub

    Private Sub ThrowIfDisposed()
        If Volatile.Read(_disposed) <> 0 OrElse _context = IntPtr.Zero Then Throw New ObjectDisposedException(NameOf(DirectTapeCopyBridgeConsumer))
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        If Interlocked.Exchange(_disposed, 1) <> 0 Then Return
        If _context <> IntPtr.Zero Then
            DirectTapeCopyNative.lfr_bridge_cancel(_context)
            DirectTapeCopyNative.lfr_bridge_destroy(_context)
            _context = IntPtr.Zero
        End If
        If _module <> IntPtr.Zero Then
            Native.NativeMethods.FreeLibrary(_module)
            _module = IntPtr.Zero
        End If
    End Sub
End Class

Public NotInheritable Class DirectTapeCopyBridgeProducer
    Implements IDisposable

    Private _context As IntPtr
    Private _module As IntPtr
    Private _disposed As Integer
    Private _retryDelegate As DirectTapeCopyNative.TapeRetryCallback

    Public Sub New(name As String)
        _module = DirectTapeCopyNative.LoadModule()
        Try
            Dim result = DirectTapeCopyNative.lfr_bridge_open_producer(name, CUInt(name.Length), _context)
            DirectTapeCopyNative.ThrowResult(_context, result, "open shared-memory producer")
        Catch
            Dispose()
            Throw
        End Try
    End Sub

    Public Sub StreamFile(tapeHandle As IntPtr,
                          file As DirectTapeCopyFile,
                          sourceBlockSize As Integer,
                          retryPrompt As Func(Of String, Boolean),
                          cancellationToken As CancellationToken)
        If file Is Nothing Then Throw New ArgumentNullException(NameOf(file))
        cancellationToken.ThrowIfCancellationRequested()
        Dim extents = file.Extents.OrderBy(Function(value) value.FileOffset).
            Select(Function(value) New DirectTapeCopyNative.NativeTapeExtent With {
                .FileOffset = CULng(value.FileOffset),
                .ByteCount = CULng(value.ByteCount),
                .StartBlock = CULng(value.StartBlock),
                .ByteOffset = CUInt(value.ByteOffset),
                .Partition = CByte(value.Partition),
                .Reserved = New Byte(2) {}}).ToArray()
        _retryDelegate = Function(userData, message, messageLength, partition, block)
                             If cancellationToken.IsCancellationRequested Then Return 0
                             Dim text = Marshal.PtrToStringAnsi(message, CInt(messageLength))
                             Return If(retryPrompt IsNot Nothing AndAlso retryPrompt(text), 1, 0)
                         End Function
        Dim result = DirectTapeCopyNative.lfr_bridge_stream_tape_file(
            _context,
            tapeHandle,
            file.Ordinal,
            CULng(file.Length),
            CUInt(sourceBlockSize),
            extents,
            CUInt(extents.Length),
            1800UI,
            3UI,
            _retryDelegate,
            IntPtr.Zero)
        GC.KeepAlive(_retryDelegate)
        DirectTapeCopyNative.ThrowResult(_context, result, $"stream source file {file.RelativePath}")
    End Sub

    Public Sub Complete()
        DirectTapeCopyNative.ThrowResult(_context,
                                         DirectTapeCopyNative.lfr_bridge_producer_complete(_context),
                                         "complete source producer")
    End Sub

    Public Sub Cancel()
        If _context <> IntPtr.Zero Then DirectTapeCopyNative.lfr_bridge_cancel(_context)
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        If Interlocked.Exchange(_disposed, 1) <> 0 Then Return
        If _context <> IntPtr.Zero Then
            DirectTapeCopyNative.lfr_bridge_destroy(_context)
            _context = IntPtr.Zero
        End If
        If _module <> IntPtr.Zero Then
            Native.NativeMethods.FreeLibrary(_module)
            _module = IntPtr.Zero
        End If
    End Sub
End Class
