Imports System.Collections.Concurrent
Imports System.IO
Imports System.IO.Pipelines
Imports System.Threading
Imports System.Buffers
Imports System.Runtime.InteropServices
Imports Serilog
Imports Serilog.Context

' 高性能文件数据提供器：
' - 仅暴露一个 PipeReader（单读者），内部 Pipe 使用 256MiB 背压阈值（可配），避免过量内存占用
' - 小文件(<16KiB)积极缓存到内存，最多缓存 1000 个，超出则排队等待
' - 大文件采用 FileStream 异步顺序读取，按顺序积极写入 Pipe（默认开启），以充分利用 256MiB 管线缓存
' - 通过 AutoResetEvent 可选地控制文件间的连续处理（当 requireSignal=True 时，消费者每完成一个文件需调用 RequestNextFile 开始下一个）
' - 生产端自动在后台填充小文件缓存，并顺序将（小/大）文件内容写入 Pipe
' 用法建议（示意）：
'   ' 连续积极缓存（默认）：
'   Dim provider = New FileDataProvider(WriteList)
'   provider.Start()
'   For Each fr In WriteList
'       Dim remaining = fr.File.length
'       While remaining > 0
'           Dim result = Await provider.Reader.ReadAsync()
'           Dim buffer = result.Buffer
'           Dim toConsume = Math.Min(remaining, buffer.Length)
'           Dim slice = buffer.Slice(0, toConsume)
'           ' 将 slice 写入磁带（略）
'           provider.Reader.AdvanceTo(slice.End, slice.End)
'           remaining -= toConsume
'           If result.IsCompleted AndAlso remaining > 0 Then Throw New EndOfStreamException()
'       End While
'   Next
'   Await provider.CompleteAsync()
'   ' 事件驱动（与旧设计兼容）：
'   Dim provider2 = New FileDataProvider(WriteList, requireSignal:=True)
'   provider2.Start()
'   For i = 0 To WriteList.Count - 1
'       provider2.RequestNextFile()
'       ' 同上消费逻辑...

Public Class FileDataProvider
    Private ReadOnly _pipe As Pipe
    Private ReadOnly _writer As PipeWriter
    Public ReadOnly Property Reader As PipeReader
    Public ReadOnly RingBuffer As SpscRingBuffer
    Private _ringBufferEnabled As Boolean

    Private ReadOnly _writeList As List(Of LTFSWriter.FileRecord)
    Private ReadOnly _smallThreshold As Long
    Private ReadOnly _smallCacheCapacity As Integer
    Private ReadOnly _requireSignal As Boolean

    Private ReadOnly _smallCacheQueue As New ConcurrentQueue(Of Tuple(Of LTFSWriter.FileRecord, Byte()))
    Private ReadOnly _smallCacheMap As New ConcurrentDictionary(Of LTFSWriter.FileRecord, Byte())

    Private ReadOnly _nextFileSignal As New AutoResetEvent(False)
    Private ReadOnly _cts As New CancellationTokenSource()

    Private _currentIndex As Integer = -1
    Private _started As Integer = 0
    Private _current As LTFSWriter.FileRecord = Nothing
    Private ReadOnly _logSessionId As String = $"fileprovider-{Guid.NewGuid().ToString("N").Substring(0, 8)}"

    Public ReadOnly Property Current As LTFSWriter.FileRecord
        Get
            Return _current
        End Get
    End Property

    ' 参数：
    ' - pipeBufferMiB: Pipe 背压阈值（默认 256MiB）
    ' - smallThresholdBytes: 小文件阈值（默认 16KiB）
    ' - smallCacheCapacity: 小文件缓存容量上限（默认 1000 个）
    ' - requireSignal: 是否需要外部通过 RequestNextFile 触发下一个文件（默认 False=积极连续缓存）
    Public Sub New(writeList As List(Of LTFSWriter.FileRecord),
                   Optional pipeBufferBytes As Long = 256 << 20,
                   Optional smallThresholdBytes As Long = 16 * 1024,
                   Optional smallCacheCapacity As Integer = 1000,
                   Optional requireSignal As Boolean = False)

        'The writer already owns a stable indexed plan.  Keep that list by
        'reference; copying several million FileRecord references here creates
        'another large peak without improving provider safety.
        _writeList = writeList
        _smallThreshold = Math.Max(1, smallThresholdBytes)
        _smallCacheCapacity = Math.Max(1, smallCacheCapacity)
        _requireSignal = requireSignal
        _ringBufferEnabled = My.Settings.LTFSWriter_RingBufferEnabled
        If _ringBufferEnabled Then
            RingBuffer = New SpscRingBuffer(pipeBufferBytes)
        Else
            Dim pause As Long = pipeBufferBytes
            Dim resumeTh As Long = Math.Max(1L, (pause \ 4) * 3)
            _pipe = New Pipe(New PipeOptions(
                pauseWriterThreshold:=pause,
                resumeWriterThreshold:=resumeTh,
                minimumSegmentSize:=My.Settings.LTFSWriter_MinimumSegmentSize,
                useSynchronizationContext:=False
            ))
            Reader = _pipe.Reader
            _writer = _pipe.Writer
        End If
    End Sub

    Public Sub Start()
        If Interlocked.Exchange(_started, 1) <> 0 Then
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(FileDataProvider))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FileProvider")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                        Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Lifecycle")
                            Log.Warning("File data provider start was ignored because the provider was already started.")
                        End Using
                    End Using
                End Using
            End Using
            Return
        End If
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(FileDataProvider))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FileProvider")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Lifecycle")
                        Log.Information("File data provider started. FileCount={FileCount} RingBufferEnabled={RingBufferEnabled} PipeBufferBytes={PipeBufferBytes} SmallThresholdBytes={SmallThresholdBytes} SmallCacheCapacity={SmallCacheCapacity} RequireSignal={RequireSignal}.",
                                        _writeList.Count,
                                        _ringBufferEnabled,
                                        If(_ringBufferEnabled, RingBuffer.Capacity, 0L),
                                        _smallThreshold,
                                        _smallCacheCapacity,
                                        _requireSignal)
                    End Using
                End Using
            End Using
        End Using
        Task.Run(AddressOf PreloadSmallFilesAsync)
        Task.Run(AddressOf ProducerLoopAsync)
        ' 积极模式下，立即允许开始
        If Not _requireSignal Then _nextFileSignal.Set()
    End Sub

    ' 由消费者调用，指示可以开始传输下一个文件（仅当 requireSignal=True 时需要）
    Public Sub RequestNextFile()
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(FileDataProvider))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FileProvider")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "FileSwitch")
                        Log.Information("File data provider requested the next file. CurrentIndex={CurrentIndex}.", Interlocked.CompareExchange(_currentIndex, 0, 0))
                    End Using
                End Using
            End Using
        End Using
        _nextFileSignal.Set()
    End Sub

    Public Sub Cancel()
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(FileDataProvider))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FileProvider")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Cancellation")
                        Log.Information("File data provider cancellation requested. CurrentIndex={CurrentIndex}.", Interlocked.CompareExchange(_currentIndex, 0, 0))
                    End Using
                End Using
            End Using
        End Using
        _cts.Cancel()
        _nextFileSignal.Set()
    End Sub

    Public Function CompleteAsync() As Task
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(FileDataProvider))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FileProvider")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Lifecycle")
                        Log.Information("File data provider completion started. CurrentIndex={CurrentIndex} CachedSmallFiles={CachedSmallFiles}.",
                                        Interlocked.CompareExchange(_currentIndex, 0, 0),
                                        _smallCacheMap.Count)
                    End Using
                End Using
            End Using
        End Using
        Try
            _cts.Cancel()
            _nextFileSignal.Set()
        Finally
            Try
                If _ringBufferEnabled Then
                    RingBuffer.Complete()
                Else
                    _writer.Complete()
                    Reader.Complete()
                End If
            Catch ex As Exception
                Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(FileDataProvider))
                    Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FileProvider")
                        Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                            Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Error")
                                Log.Error(ex, "File data provider buffer completion failed.")
                            End Using
                        End Using
                    End Using
                End Using
            End Try
        End Try
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(FileDataProvider))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FileProvider")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Lifecycle")
                        Log.Information("File data provider completion requested. CachedSmallFiles={CachedSmallFiles}.", _smallCacheMap.Count)
                    End Using
                End Using
            End Using
        End Using
        Return Task.CompletedTask
    End Function

    Private Async Function ProducerLoopAsync() As Task
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(FileDataProvider))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FileProvider")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Lifecycle")
                        Log.Information("File data provider producer loop started.")
                    End Using
                End Using
            End Using
        End Using
        Try
            While Not _cts.IsCancellationRequested
                ' requireSignal=True 时按事件推进；否则积极连续推进
                If _requireSignal Then
                    _nextFileSignal.WaitOne()
                    If _cts.IsCancellationRequested Then Exit While
                End If

                Dim nextIdx As Integer = Interlocked.Increment(_currentIndex)
                If nextIdx >= _writeList.Count Then Exit While

                Dim fr As LTFSWriter.FileRecord = _writeList(nextIdx)
                If nextIdx - 1 >= 0 Then
                    _writeList(nextIdx - 1) = Nothing
                End If
                _current = fr

                Dim isSmallFile = fr IsNot Nothing AndAlso fr.File IsNot Nothing AndAlso fr.File.length < _smallThreshold AndAlso fr.FileOffset = 0 AndAlso fr.File.length = fr.SegmentLength
                Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(FileDataProvider))
                    Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FileProvider")
                        Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                            Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "FileRead")
                                Log.Information("File data provider began reading a file. FileIndex={FileIndex} SourcePath={SourcePath} Length={Length} SmallFile={SmallFile} CacheHit={CacheHit}.",
                                                nextIdx,
                                                If(fr Is Nothing, String.Empty, fr.SourcePath),
                                                If(fr Is Nothing OrElse fr.File Is Nothing, 0L, fr.File.length),
                                                isSmallFile,
                                                isSmallFile AndAlso _smallCacheMap.ContainsKey(fr))
                            End Using
                        End Using
                    End Using
                End Using

                If isSmallFile Then
                    Dim data As Byte() = Nothing
                    If Not _smallCacheMap.TryRemove(fr, data) Then
                        data = ReadAllBytesSafe(fr)
                    Else
                        Dim tmp As Tuple(Of LTFSWriter.FileRecord, Byte()) = Nothing
                        While _smallCacheQueue.TryDequeue(tmp)
                            If tmp IsNot Nothing AndAlso tmp.Item1 Is fr Then Exit While
                        End While
                    End If

                    If data IsNot Nothing AndAlso data.Length > 0 Then
                        If _ringBufferEnabled Then
                            WriteAllToRing(data, _cts.Token)
                        Else
                            _writer.Write(data.AsSpan())
                            Dim res = Await _writer.FlushAsync(_cts.Token)
                            If res.IsCanceled OrElse res.IsCompleted Then Exit While
                        End If
                    End If
                Else
                    ' 大文件：流式拷贝到 Pipe（积极缓存，受 Pipe 背压调节）
                    Await StreamFileToPipeAsync(fr, _cts.Token)
                End If
                Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(FileDataProvider))
                    Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FileProvider")
                        Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                            Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "FileCompleted")
                                Log.Information("File data provider completed a file. FileIndex={FileIndex} SourcePath={SourcePath} CachedSmallFiles={CachedSmallFiles}.",
                                                nextIdx,
                                                If(fr Is Nothing, String.Empty, fr.SourcePath),
                                                _smallCacheMap.Count)
                            End Using
                        End Using
                    End Using
                End Using
                ' 在积极模式下，自动继续下一个文件；在信号模式下，等待下一次 RequestNextFile
                If _requireSignal = False Then
                    ' 继续循环即可
                End If
            End While
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(FileDataProvider))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FileProvider")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                        Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Lifecycle")
                            Log.Information("File data provider producer loop completed. CurrentIndex={CurrentIndex} CancellationRequested={CancellationRequested}.",
                                            Interlocked.CompareExchange(_currentIndex, 0, 0),
                                            _cts.IsCancellationRequested)
                        End Using
                    End Using
                End Using
            End Using
        Catch ex As Exception
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(FileDataProvider))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FileProvider")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                        Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Error")
                            Log.Error(ex, "File data provider producer loop failed. CurrentIndex={CurrentIndex}.", Interlocked.CompareExchange(_currentIndex, 0, 0))
                        End Using
                    End Using
                End Using
            End Using
            MessageBox.Show(ex.ToString())
            Try
                If _ringBufferEnabled Then
                    RingBuffer.Complete()
                Else
                    _writer.Complete()
                    Reader.Complete()
                End If
            Catch completionEx As Exception
                Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(FileDataProvider))
                    Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FileProvider")
                        Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                            Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Error")
                                Log.Error(completionEx, "File data provider failed to complete its output after a producer error.")
                            End Using
                        End Using
                    End Using
                End Using
            End Try
        End Try
    End Function

    Private Sub WriteAllToRing(data As Byte(), ct As CancellationToken)
        Dim offset As Integer = 0
        While offset < data.Length
            Dim seg = RingBuffer.GetWriteSegment(1, ct)
            Dim n As Integer = Math.Min(seg.Count, data.Length - offset)
            Buffer.BlockCopy(data, offset, seg.Array, seg.Offset, n)
            RingBuffer.AdvanceWrite(n)
            offset += n
        End While
    End Sub

    Private Function ReadAllBytesSafe(fr As LTFSWriter.FileRecord) As Byte()
        Try
            Dim result As Byte() = fr.ReadAllBytes()
            fr.IsOpened = True
            Return result
        Catch ex As Exception
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(FileDataProvider))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FileProvider")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                        Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "FileRead")
                            Log.Warning(ex, "File data provider managed file read failed; switching to direct file fallback. SourcePath={SourcePath}.", fr.SourcePath)
                        End Using
                    End Using
                End Using
            End Using
            While True
                Try
                    Dim result As Byte() = File.ReadAllBytes(fr.SourcePath)
                    fr.File.length = result.Length
                    fr.IsOpened = True
                    Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(FileDataProvider))
                        Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FileProvider")
                            Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                                Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "FileRead")
                                    Log.Information("File data provider direct file fallback completed. SourcePath={SourcePath} Bytes={Bytes}.", fr.SourcePath, result.Length)
                                End Using
                            End Using
                        End Using
                    End Using
                    Return result
                Catch fallbackEx As Exception
                    Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(FileDataProvider))
                        Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FileProvider")
                            Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                                Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Error")
                                    Log.Error(fallbackEx, "File data provider direct file fallback failed. SourcePath={SourcePath}.", fr.SourcePath)
                                End Using
                            End Using
                        End Using
                    End Using
                    Select Case MessageBox.Show(New Form With {.TopMost = True}, $"{My.Resources.ResText_WErr }{vbCrLf}{fallbackEx.ToString}", My.Resources.ResText_Warning, MessageBoxButtons.AbortRetryIgnore)
                        Case DialogResult.Abort
                            fr.IsOpened = False
                            fr.File = Nothing
                            Throw fallbackEx
                        Case DialogResult.Retry
                            Continue While
                        Case DialogResult.Ignore
                            fr.IsOpened = False
                            fr.File = Nothing
                            Return Array.Empty(Of Byte)()
                    End Select
                End Try
            End While
        End Try
        Return Array.Empty(Of Byte)()
    End Function
    Private Async Function StreamFileToPipeAsync(fr As LTFSWriter.FileRecord, ct As CancellationToken) As Task
        Dim fs As FileStream = Nothing
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(FileDataProvider))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FileProvider")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "FileRead")
                        Log.Information("File data provider stream read started. SourcePath={SourcePath} FileOffset={FileOffset} SegmentLength={SegmentLength}.",
                                        fr.SourcePath,
                                        fr.FileOffset,
                                        fr.SegmentLength)
                    End Using
                End Using
            End Using
        End Using
        Try
            fs = New FileStream(fr.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, My.Settings.LTFSWriter_FileStreamBufferSize, FileOptions.Asynchronous Or FileOptions.SequentialScan)
            If fr.File.length = 0 Then
                fr.File.length = fs.Length
                fr.FileOffset = 0
                fr.SegmentLength = fs.Length
            End If
            fr.IsOpened = True
        Catch ex As Exception
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(FileDataProvider))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FileProvider")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                        Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "FileRead")
                            Log.Warning(ex, "File data provider stream open failed; using FileRecord fallback. SourcePath={SourcePath}.", fr.SourcePath)
                        End Using
                    End Using
                End Using
            End Using
            ' 备用：尝试使用现有 FileRecord 打开
            Try
                Select Case fr.Open(BufferSize:=64 * 1024)
                    Case DialogResult.Ignore
                        Exit Function
                    Case DialogResult.Abort
                        Throw New IOException("Open aborted")
                End Select
                fs = fr.fs
            Catch fallbackEx As Exception
                Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(FileDataProvider))
                    Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FileProvider")
                        Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                            Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Error")
                                Log.Error(fallbackEx, "File data provider FileRecord fallback open failed. SourcePath={SourcePath}.", fr.SourcePath)
                            End Using
                        End Using
                    End Using
                End Using
                fr.IsOpened = False
                fr.File = Nothing
                Exit Function
            End Try
        End Try

        Dim totalReadLen As Long = 0
        Using fs
            If fr.FileOffset > 0 Then fs.Seek(fr.FileOffset, SeekOrigin.Begin)
            If _ringBufferEnabled Then
                Dim minChunk As Integer = 1024 * 1024
                While Not ct.IsCancellationRequested
                    If totalReadLen >= fr.SegmentLength Then Exit While
                    Dim seg = RingBuffer.GetWriteSegment(minChunk, ct)
                    If seg.Count = 0 Then
                        ' 理论上不会（除非 completed/disposed/canceled）
                        Exit While
                    End If

                    Dim n As Integer = Await fs.ReadAsync(seg.Array, seg.Offset, seg.Count, ct).ConfigureAwait(False)
                    If n = 0 Then Exit While
                    RingBuffer.AdvanceWrite(CInt(Math.Min(n, fr.SegmentLength - totalReadLen)))
                    Interlocked.Add(totalReadLen, n)
                End While
            Else
                Dim minSize As Integer = 64 * 1024
                While Not ct.IsCancellationRequested
                    If totalReadLen >= fr.SegmentLength Then Exit While
                    Dim dest As Memory(Of Byte) = _writer.GetMemory(minSize)
                    Dim seg As New ArraySegment(Of Byte)
                    If Not MemoryMarshal.TryGetArray(Of Byte)(dest, seg) Then
                        Throw New Exception("TryGetArray failed")
                    End If
                    Dim cap As Integer = Math.Min(minSize, seg.Count)
                    Dim n = Await fs.ReadAsync(seg.Array, seg.Offset, cap, ct).ConfigureAwait(False)
                    If n = 0 Then Exit While
                    _writer.Advance(CInt(Math.Min(n, fr.SegmentLength - totalReadLen)))
                    Interlocked.Add(totalReadLen, n)
                    Dim result = Await _writer.FlushAsync(ct).ConfigureAwait(False)
                    If result.IsCanceled OrElse result.IsCompleted Then Exit While
                End While
            End If
        End Using
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(FileDataProvider))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FileProvider")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "FileRead")
                        Log.Information("File data provider stream read completed. SourcePath={SourcePath} BytesRead={BytesRead} CancellationRequested={CancellationRequested}.",
                                        fr.SourcePath,
                                        totalReadLen,
                                        ct.IsCancellationRequested)
                    End Using
                End Using
            End Using
        End Using
    End Function

    Private Async Function PreloadSmallFilesAsync() As Task
        Dim cachedCount As Integer = 0
        Try
            For Each fr In _writeList
                If _cts.IsCancellationRequested Then Exit For
                If fr.File IsNot Nothing AndAlso fr.File.length < _smallThreshold Then
                    ' 控制小文件缓存上限
                    While _smallCacheQueue.Count >= _smallCacheCapacity AndAlso Not _cts.IsCancellationRequested
                        Await Task.Delay(10, _cts.Token)
                    End While

                    Dim data As Byte() = Nothing
                    Try
                        data = ReadAllBytesSafe(fr)
                    Catch
                        data = Nothing
                    End Try
                    If data IsNot Nothing Then
                        _smallCacheMap.TryAdd(fr, data)
                        _smallCacheQueue.Enqueue(Tuple.Create(fr, data))
                        cachedCount += 1
                    End If
                End If
            Next
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(FileDataProvider))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FileProvider")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                        Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Cache")
                            Log.Information("File data provider small-file preload completed. CachedFiles={CachedFiles} CacheEntries={CacheEntries}.", cachedCount, _smallCacheMap.Count)
                        End Using
                    End Using
                End Using
            End Using
        Catch ex As Exception
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(FileDataProvider))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FileProvider")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                        Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Error")
                            Log.Error(ex, "File data provider small-file preload failed. CachedFiles={CachedFiles}.", cachedCount)
                        End Using
                    End Using
                End Using
            End Using
        End Try
    End Function
End Class
Public Class HardDriveDataProvider
    Private ReadOnly _pipe As Pipe
    Private ReadOnly _writer As PipeWriter
    Public ReadOnly Property Reader As PipeReader
    Public ReadOnly RingBuffer As SpscRingBuffer
    Private _ringBufferEnabled As Boolean

    Private ReadOnly _cts As New CancellationTokenSource()

    Private _started As Integer = 0
    Private ReadOnly _logSessionId As String = $"harddrive-provider-{Guid.NewGuid().ToString("N").Substring(0, 8)}"
    Public Property DevicePath As String
    Public Property StartLBA As ULong
    Public Property SectorCount As Long
    Public Property SectorLength As Integer = 512
    Public Property SectorLenUpdated As Boolean = False

    Public Class Config
        Public Property DrivePath As String
        Public Property StartLBA As ULong
        Public Property SectorCount As Long
    End Class
    ' 参数：
    ' - pipeBufferMiB: Pipe 背压阈值（默认 256MiB）
    Public Sub New(path As String, StartLBA As ULong, SectorCount As Long,
                   Optional pipeBufferBytes As Long = 256 << 20)

        _ringBufferEnabled = My.Settings.LTFSWriter_RingBufferEnabled
        DevicePath = path
        Me.StartLBA = StartLBA
        Me.SectorCount = SectorCount
        If _ringBufferEnabled Then
            RingBuffer = New SpscRingBuffer(pipeBufferBytes)
        Else
            Dim pause As Long = pipeBufferBytes
            Dim resumeTh As Long = Math.Max(1L, (pause \ 4) * 3)
            _pipe = New Pipe(New PipeOptions(
                pauseWriterThreshold:=pause,
                resumeWriterThreshold:=resumeTh,
                minimumSegmentSize:=My.Settings.LTFSWriter_MinimumSegmentSize,
                useSynchronizationContext:=False
            ))
            Reader = _pipe.Reader
            _writer = _pipe.Writer
        End If
    End Sub

    Public Sub Start()
        If Interlocked.Exchange(_started, 1) <> 0 Then
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(HardDriveDataProvider))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "HardDriveProvider")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                        Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Lifecycle")
                            Log.Warning("Hard drive data provider start was ignored because the provider was already started. DevicePath={DevicePath}.", DevicePath)
                        End Using
                    End Using
                End Using
            End Using
            Return
        End If
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(HardDriveDataProvider))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "HardDriveProvider")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Lifecycle")
                        Log.Information("Hard drive data provider started. DevicePath={DevicePath} StartLba={StartLba} SectorCount={SectorCount} RingBufferEnabled={RingBufferEnabled}.",
                                        DevicePath,
                                        StartLBA,
                                        SectorCount,
                                        _ringBufferEnabled)
                    End Using
                End Using
            End Using
        End Using
        Task.Run(AddressOf ProducerLoopAsync)
    End Sub


    Public Sub Cancel()
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(HardDriveDataProvider))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "HardDriveProvider")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Cancellation")
                        Log.Information("Hard drive data provider cancellation requested. DevicePath={DevicePath}.", DevicePath)
                    End Using
                End Using
            End Using
        End Using
        _cts.Cancel()
    End Sub

    Public Function CompleteAsync() As Task
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(HardDriveDataProvider))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "HardDriveProvider")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Lifecycle")
                        Log.Information("Hard drive data provider completion started. DevicePath={DevicePath}.", DevicePath)
                    End Using
                End Using
            End Using
        End Using
        Try
            _cts.Cancel()
        Finally
            Try
                If _ringBufferEnabled Then
                    RingBuffer.Complete()
                Else
                    _writer.Complete()
                    Reader.Complete()
                End If
            Catch ex As Exception
                Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(HardDriveDataProvider))
                    Using categoryScope As IDisposable = LogContext.PushProperty("Category", "HardDriveProvider")
                        Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                            Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Error")
                                Log.Error(ex, "Hard drive data provider buffer completion failed. DevicePath={DevicePath}.", DevicePath)
                            End Using
                        End Using
                    End Using
                End Using
            End Try
        End Try
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(HardDriveDataProvider))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "HardDriveProvider")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Lifecycle")
                        Log.Information("Hard drive data provider completion requested. DevicePath={DevicePath}.", DevicePath)
                    End Using
                End Using
            End Using
        End Using
        Return Task.CompletedTask
    End Function

    Private Async Function ProducerLoopAsync() As Task
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(HardDriveDataProvider))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "HardDriveProvider")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Lifecycle")
                        Log.Information("Hard drive data provider producer loop started. DevicePath={DevicePath}.", DevicePath)
                    End Using
                End Using
            End Using
        End Using
        Try
            While Not _cts.IsCancellationRequested

                ' 大文件：流式拷贝到 Pipe（积极缓存，受 Pipe 背压调节）
                Await StreamDiskToPipeAsync(DevicePath, StartLBA, SectorCount, _cts.Token)

            End While
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(HardDriveDataProvider))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "HardDriveProvider")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                        Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Lifecycle")
                            Log.Information("Hard drive data provider producer loop completed. DevicePath={DevicePath} CancellationRequested={CancellationRequested}.", DevicePath, _cts.IsCancellationRequested)
                        End Using
                    End Using
                End Using
            End Using
        Catch ex As Exception
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(HardDriveDataProvider))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "HardDriveProvider")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                        Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Error")
                            Log.Error(ex, "Hard drive data provider producer loop failed. DevicePath={DevicePath}.", DevicePath)
                        End Using
                    End Using
                End Using
            End Using
            MessageBox.Show(ex.ToString())
            Try
                If _ringBufferEnabled Then
                    RingBuffer.Complete()
                Else
                    _writer.Complete()
                    Reader.Complete()
                End If
            Catch completionEx As Exception
                Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(HardDriveDataProvider))
                    Using categoryScope As IDisposable = LogContext.PushProperty("Category", "HardDriveProvider")
                        Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                            Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Error")
                                Log.Error(completionEx, "Hard drive data provider failed to complete its output after a producer error. DevicePath={DevicePath}.", DevicePath)
                            End Using
                        End Using
                    End Using
                End Using
            End Try
        End Try
    End Function

    Private Sub WriteAllToRing(data As Byte(), ct As CancellationToken)
        Dim offset As Integer = 0
        While offset < data.Length
            Dim seg = RingBuffer.GetWriteSegment(1, ct)
            Dim n As Integer = Math.Min(seg.Count, data.Length - offset)
            Buffer.BlockCopy(data, offset, seg.Array, seg.Offset, n)
            RingBuffer.AdvanceWrite(n)
            offset += n
        End While
    End Sub
    Private Async Function StreamDiskToPipeAsync(path As String, StartLBA As ULong, SectorCount As Long, ct As CancellationToken) As Task
        Dim driveHandle As IntPtr
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(HardDriveDataProvider))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "HardDriveProvider")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "DeviceRead")
                        Log.Information("Hard drive stream read started. DevicePath={DevicePath} StartLba={StartLba} RequestedSectorCount={RequestedSectorCount}.",
                                        path,
                                        StartLBA,
                                        SectorCount)
                    End Using
                End Using
            End Using
        End Using
        If Not TapeUtils.OpenTapeDrive(path, driveHandle) Then
            Dim openError As Integer = TapeUtils.LastWin32Error
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(HardDriveDataProvider))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "HardDriveProvider")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                        Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Error")
                            Log.Error("Hard drive stream read could not open the device. DevicePath={DevicePath} Win32Error={Win32Error}.", path, openError)
                        End Using
                    End Using
                End Using
            End Using
            Throw New ComponentModel.Win32Exception(openError, $"Unable to open hard drive device: {path}")
        End If
        Try
            Dim batchSize As Integer = 128
            With DiskQuery.QuerySectorInfo(driveHandle)
                SectorLength = .SectorSize
                batchSize = 65536 \ SectorLength
                If SectorCount < 0 Then
                    SectorCount = CLng(.LBACount - StartLBA)
                End If
                Me.SectorCount = SectorCount
                SectorLenUpdated = True
            End With
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(HardDriveDataProvider))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "HardDriveProvider")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                        Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "DeviceRead")
                            Log.Information("Hard drive sector geometry resolved. DevicePath={DevicePath} SectorLength={SectorLength} SectorCount={SectorCount}.",
                                            path,
                                            SectorLength,
                                            SectorCount)
                        End Using
                    End Using
                End Using
            End Using
            Dim LBA As ULong = StartLBA
            Dim EndLBA As ULong = CULng(StartLBA + SectorCount - 1)
            If _ringBufferEnabled Then
                Dim minChunk As Integer = SectorLength * batchSize
                While Not ct.IsCancellationRequested
                    If LBA > EndLBA Then Exit While
                    Dim batch As Integer = CInt(Math.Min(batchSize, EndLBA - LBA + 1))
                    Dim totalBytes As Integer = batch * SectorLength
                    Dim dataPtr As IntPtr = TapeUtils.SCSIReadParamUnmanaged(driveHandle, {&H28, 0,
                    CByte(CLng((LBA >> 24)) And &HFF), CByte(CLng((LBA >> 16)) And &HFF),
                    CByte(CLng((LBA >> 8)) And &HFF), CByte(CLng((LBA >> 0)) And &HFF),
                    0, CByte((batch >> 8) And &HFF), CByte((batch >> 0) And &HFF), 0}, totalBytes)
                    LBA = CULng(LBA + batch)

                    Dim remaining As Integer = totalBytes
                    Dim srcOffset As Integer = 0
                    While remaining > 0
                        Dim seg = RingBuffer.GetWriteSegment(remaining, ct)
                        If seg.Count = 0 Then Exit While
                        Dim toCopy As Integer = Math.Min(remaining, seg.Count)
                        Marshal.Copy(IntPtr.Add(dataPtr, srcOffset), seg.Array, seg.Offset, toCopy)
                        RingBuffer.AdvanceWrite(toCopy)
                        srcOffset += toCopy
                        remaining -= toCopy
                    End While
                    Marshal.FreeHGlobal(dataPtr)
                End While
            Else
                Dim minSize As Integer = SectorLength * batchSize
                While Not ct.IsCancellationRequested
                    If LBA > EndLBA Then Exit While
                    Dim batch As Integer = CInt(Math.Min(batchSize, EndLBA - LBA + 1))
                    Dim totalBytes As Integer = batch * SectorLength
                    Dim dataPtr As IntPtr = TapeUtils.SCSIReadParamUnmanaged(driveHandle, {&H28, 0,
                    CByte(CLng((LBA >> 24)) And &HFF), CByte(CLng((LBA >> 16)) And &HFF),
                    CByte(CLng((LBA >> 8)) And &HFF), CByte(CLng((LBA >> 0)) And &HFF),
                    0, CByte((batch >> 8) And &HFF), CByte((batch >> 0) And &HFF), 0}, totalBytes)
                    LBA = CULng(LBA + batch)

                    Dim remaining As Integer = totalBytes
                    Dim srcOffset As Integer = 0
                    While remaining > 0
                        Dim dest As Memory(Of Byte) = _writer.GetMemory(minSize)
                        Dim seg As New ArraySegment(Of Byte)
                        If Not MemoryMarshal.TryGetArray(Of Byte)(dest, seg) Then
                            Throw New Exception("TryGetArray failed")
                        End If
                        Dim cap As Integer = Math.Min(minSize, seg.Count)
                        Dim toCopy As Integer = Math.Min(remaining, cap)
                        Marshal.Copy(IntPtr.Add(dataPtr, srcOffset), seg.Array, seg.Offset, toCopy)
                        _writer.Advance(toCopy)
                        srcOffset += toCopy
                        remaining -= toCopy
                    End While
                    Marshal.FreeHGlobal(dataPtr)
                    Dim result = Await _writer.FlushAsync(ct).ConfigureAwait(False)
                    If result.IsCanceled OrElse result.IsCompleted Then Exit While
                End While
            End If
        Catch ex As Exception
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(HardDriveDataProvider))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "HardDriveProvider")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                        Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Error")
                            Log.Error(ex, "Hard drive stream read failed. DevicePath={DevicePath} StartLba={StartLba} SectorCount={SectorCount}.",
                                      path,
                                      StartLBA,
                                      SectorCount)
                        End Using
                    End Using
                End Using
            End Using
            Throw ex
        Finally
            TapeUtils.CloseTapeDrive(driveHandle)
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(HardDriveDataProvider))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "HardDriveProvider")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                        Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "DeviceRead")
                            Log.Information("Hard drive stream read closed. DevicePath={DevicePath}.", path)
                        End Using
                    End Using
                End Using
            End Using
        End Try

    End Function

End Class
