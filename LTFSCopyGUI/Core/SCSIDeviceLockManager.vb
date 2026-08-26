Imports System
Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.Runtime.ExceptionServices
Imports System.Security.Cryptography
Imports System.Text
Imports System.Threading

Public NotInheritable Class SCSIDeviceLockManager
    Private Shared ReadOnly _instance As New SCSIDeviceLockManager()

    Private Const MutexNamespace As String = "Local\LTFSCopyGUI.SCSI."
    Private Const CommandQueueCapacity As Integer = 16

    Private ReadOnly _registryLock As New Object()
    Private ReadOnly _pathStates As New Dictionary(Of String, DeviceLockState)(StringComparer.OrdinalIgnoreCase)
    Private ReadOnly _handleStates As New Dictionary(Of Long, DeviceLockState)()
    Private ReadOnly _fallbackState As New DeviceLockState(String.Empty, False)

    Private Sub New()
    End Sub

    Public Shared ReadOnly Property Instance As SCSIDeviceLockManager
        Get
            Return _instance
        End Get
    End Property

    Public ReadOnly Property FallbackLock As Object
        Get
            Return _fallbackState.LocalLock
        End Get
    End Property

    Public Function GetLock(devicePath As String) As Object
        Return GetPathState(devicePath).LocalLock
    End Function

    Public Function GetLock(handle As IntPtr) As Object
        Return GetHandleState(handle).LocalLock
    End Function

    Public Function RegisterHandle(devicePath As String, handle As IntPtr) As Object
        Dim result As DeviceLockState = GetPathState(devicePath)
        If IsInvalidHandle(handle) Then Return result.LocalLock

        result.ActivateQueue()

        SyncLock _registryLock
            _handleStates(handle.ToInt64()) = result
        End SyncLock
        Return result.LocalLock
    End Function

    Public Sub UnregisterHandle(handle As IntPtr)
        If IsInvalidHandle(handle) Then Return

        SyncLock _registryLock
            _handleStates.Remove(handle.ToInt64())
        End SyncLock
    End Sub

    Public Function CanOpenDevice(devicePath As String) As Boolean
        Dim state As DeviceLockState = GetPathState(devicePath)
        If Not state.CrossProcessEnabled Then Return True
        If HasLocalWriter(state) Then Return True
        Return IsWriterGateAvailable(state)
    End Function

    Public Function IsWriterLockedByOtherProcess(devicePath As String) As Boolean
        Return Not CanOpenDevice(devicePath)
    End Function

    Public Function TryEnterOperation(devicePath As String,
                                      Optional timeoutMilliseconds As Integer = 0) As IDisposable
        Return TryEnterOperation(GetPathState(devicePath), timeoutMilliseconds)
    End Function

    Public Function TryEnterOperation(handle As IntPtr,
                                      Optional timeoutMilliseconds As Integer = 0) As IDisposable
        Return TryEnterOperation(GetHandleState(handle), timeoutMilliseconds)
    End Function

    ' SCSI commands must wait for another operation in this process to finish.
    ' The process gate is still probed without waiting so a different process
    ' owning the device can be reported as busy instead of blocking forever.
    Public Function EnterOperation(devicePath As String) As IDisposable
        Return TryEnterOperation(GetPathState(devicePath), 0, True)
    End Function

    Public Function EnterOperation(handle As IntPtr) As IDisposable
        Return TryEnterOperation(GetHandleState(handle), 0, True)
    End Function

    ''' <summary>
    ''' Queues one device operation and waits synchronously for its result.
    ''' The queue is FIFO and is shared by all handles registered for the same
    ''' device path.  A false result means that the cross-process device gate
    ''' was not available; exceptions from the operation are propagated to the
    ''' caller.
    ''' </summary>
    Public Function ExecuteQueuedOperation(devicePath As String,
                                            operation As Action,
                                            Optional cancellationToken As CancellationToken = Nothing) As Boolean
        If operation Is Nothing Then Throw New ArgumentNullException(NameOf(operation))
        Return ExecuteQueuedOperation(GetPathState(devicePath), operation, cancellationToken)
    End Function

    ''' <summary>
    ''' Queues one device operation for the device associated with a handle.
    ''' </summary>
    Public Function ExecuteQueuedOperation(handle As IntPtr,
                                            operation As Action,
                                            Optional cancellationToken As CancellationToken = Nothing) As Boolean
        If operation Is Nothing Then Throw New ArgumentNullException(NameOf(operation))
        Return ExecuteQueuedOperation(GetHandleState(handle), operation, cancellationToken)
    End Function

    ''' <summary>
    ''' Adds a FIFO barrier after all currently queued operations.  This is
    ''' used by handle teardown so a native handle is not closed while a
    ''' queued SCSI command still owns a buffer or is using the handle.
    ''' </summary>
    Public Function WaitForQueuedOperations(devicePath As String,
                                            Optional cancellationToken As CancellationToken = Nothing) As Boolean
        Return ExecuteQueuedOperation(GetPathState(devicePath), Sub()
                                                                   ' The queue order is the barrier.
                                                               End Sub,
                                                               cancellationToken)
    End Function

    Public Function WaitForQueuedOperations(handle As IntPtr,
                                            Optional cancellationToken As CancellationToken = Nothing) As Boolean
        Return ExecuteQueuedOperation(GetHandleState(handle), Sub()
                                                                   ' The queue order is the barrier.
                                                               End Sub,
                                                               cancellationToken)
    End Function

    ''' <summary>
    ''' Cancels commands that are waiting in this device queue.  A command
    ''' that has already started is allowed to finish because the native SCSI
    ''' call cannot be safely interrupted while its unmanaged buffer is live.
    ''' </summary>
    Public Sub CancelQueuedOperations(devicePath As String)
        CancelQueuedOperations(GetPathState(devicePath))
    End Sub

    Public Sub CancelQueuedOperations(handle As IntPtr)
        CancelQueuedOperations(GetHandleState(handle))
    End Sub

    ''' <summary>
    ''' Prevents new commands from using a handle that is about to be closed
    ''' and cancels commands that have not started yet.  A later open of the
    ''' same device reactivates the path queue.
    ''' </summary>
    Public Sub CloseQueuedOperations(handle As IntPtr)
        CloseQueuedOperations(GetHandleState(handle))
    End Sub

    Public Function AcquireWriterLease(devicePath As String,
                                       sessionId As String,
                                       Optional timeoutMilliseconds As Integer = 10000) As WriterLease
        Dim state As DeviceLockState = GetPathState(devicePath)
        If Not state.CrossProcessEnabled Then Return Nothing

        SyncLock _registryLock
            If state.ActiveWriter IsNot Nothing Then Return Nothing
        End SyncLock

        Dim lease As New WriterLease(Me,
                                     state,
                                     If(String.IsNullOrWhiteSpace(sessionId),
                                        $"writer-{Guid.NewGuid().ToString("N").Substring(0, 8)}",
                                        sessionId))
        If lease.Start(timeoutMilliseconds) Then Return lease

        lease.Dispose()
        Return Nothing
    End Function

    Private Function TryEnterOperation(state As DeviceLockState,
                                       timeoutMilliseconds As Integer,
                                       Optional waitForLocalLock As Boolean = False) As IDisposable
        Dim localLockTaken As Boolean = False
        Dim processGateTaken As Boolean = False

        Try
            If waitForLocalLock Then
                Monitor.Enter(state.LocalLock)
                localLockTaken = True
            ElseIf timeoutMilliseconds <= 0 Then
                localLockTaken = Monitor.TryEnter(state.LocalLock)
            Else
                localLockTaken = Monitor.TryEnter(state.LocalLock, timeoutMilliseconds)
            End If

            If Not localLockTaken Then Return Nothing

            If state.CrossProcessEnabled AndAlso Not HasLocalWriter(state) Then
                ' Do not turn a local wait into an unbounded cross-process
                ' wait.  The caller has already serialized local operations;
                ' a foreign owner should be reported as busy immediately.
                Dim processGateTimeout As Integer = If(waitForLocalLock, 0, timeoutMilliseconds)
                If Not WaitForMutex(state.ProcessGate, processGateTimeout, Nothing) Then
                    Monitor.Exit(state.LocalLock)
                    localLockTaken = False
                    Return Nothing
                End If
                processGateTaken = True

                If Not IsWriterGateAvailable(state) Then
                    state.ProcessGate.ReleaseMutex()
                    processGateTaken = False
                    Monitor.Exit(state.LocalLock)
                    localLockTaken = False
                    Return Nothing
                End If
            End If

            Return New OperationScope(state, processGateTaken)
        Catch
            If processGateTaken Then
                Try
                    state.ProcessGate.ReleaseMutex()
                Catch
                End Try
            End If
            If localLockTaken Then
                Try
                    Monitor.Exit(state.LocalLock)
                Catch
                End Try
            End If
            Throw
        End Try
    End Function

    Private Function TryEnterQueuedOperation(state As DeviceLockState,
                                              cancellationToken As CancellationToken,
                                              takeLocalLock As Boolean) As IDisposable
        Dim localLockTaken As Boolean = False
        Dim processGateTaken As Boolean = False

        Try
            If takeLocalLock Then
                ' Legacy callers still use SyncLock(GetLock(...)) for
                ' multi-command sequences.  The queue worker must honor that
                ' lock, but its wait must remain cancellable.
                Do While Not Monitor.TryEnter(state.LocalLock, 100)
                    If cancellationToken.IsCancellationRequested Then
                        Throw New OperationCanceledException(cancellationToken)
                    End If
                Loop
                localLockTaken = True
                If cancellationToken.IsCancellationRequested Then
                    Monitor.Exit(state.LocalLock)
                    localLockTaken = False
                    Throw New OperationCanceledException(cancellationToken)
                End If
            End If

            cancellationToken.ThrowIfCancellationRequested()

            If state.CrossProcessEnabled AndAlso Not HasLocalWriter(state) Then
                ' The per-device queue already serializes callers in this
                ' process.  Probe the cross-process gate without blocking so
                ' an external owner remains a normal "busy" result.
                If Not WaitForMutex(state.ProcessGate, 0, Nothing) Then
                    If localLockTaken Then
                        Monitor.Exit(state.LocalLock)
                        localLockTaken = False
                    End If
                    Return Nothing
                End If
                processGateTaken = True

                If Not IsWriterGateAvailable(state) Then
                    state.ProcessGate.ReleaseMutex()
                    processGateTaken = False
                    If localLockTaken Then
                        Monitor.Exit(state.LocalLock)
                        localLockTaken = False
                    End If
                    Return Nothing
                End If
            End If

            Return New OperationScope(state, processGateTaken, localLockTaken)
        Catch
            If processGateTaken Then
                Try
                    state.ProcessGate.ReleaseMutex()
                Catch
                End Try
            End If
            If localLockTaken Then
                Try
                    Monitor.Exit(state.LocalLock)
                Catch
                End Try
            End If
            Throw
        End Try
    End Function

    Private Function ExecuteQueuedOperation(state As DeviceLockState,
                                            operation As Action,
                                            cancellationToken As CancellationToken) As Boolean
        Dim cancellationVersion As Integer
        Dim enqueueToken As CancellationToken
        Dim queueClosed As Boolean
        Dim linkedCancellationSource As CancellationTokenSource = Nothing
        SyncLock state.QueueControlLock
            queueClosed = state.QueueClosed
            cancellationVersion = state.QueueCancellationVersion
            Dim queueCancellationToken As CancellationToken = state.QueueCancellationSource.Token
            If cancellationToken.CanBeCanceled Then
                ' A caller cancellation and a device close/cancel must both
                ' wake a producer blocked on a full queue.  The version check
                ' below also protects the small race between this snapshot
                ' and command start.
                linkedCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    queueCancellationToken)
                enqueueToken = linkedCancellationSource.Token
            Else
                enqueueToken = queueCancellationToken
            End If
        End SyncLock

        Try
            cancellationToken.ThrowIfCancellationRequested()
            If queueClosed Then Return False

            ' A legacy SyncLock is a transaction boundary.  Execute commands
            ' inline while that lock is held so the queue worker cannot deadlock
            ' trying to acquire a monitor owned by the submitting thread.  The
            ' worker still acquires the same monitor for normal queued callers,
            ' so it cannot interleave with the transaction.
            If Monitor.IsEntered(state.LocalLock) Then
                enqueueToken.ThrowIfCancellationRequested()
                If cancellationVersion <> Volatile.Read(state.QueueCancellationVersion) Then
                    Throw New OperationCanceledException(enqueueToken)
                End If

                Dim operationScope As IDisposable = TryEnterQueuedOperation(state,
                                                                              enqueueToken,
                                                                              False)
                If operationScope Is Nothing Then Return False

                Using operationScope
                    operation()
                End Using
                Return True
            End If

            Dim command As New QueuedCommand(
                Function() As Boolean
                    Dim operationScope As IDisposable = TryEnterQueuedOperation(state,
                                                                                  enqueueToken,
                                                                                  True)
                    If operationScope Is Nothing Then Return False

                    Using operationScope
                        operation()
                    End Using
                    Return True
                End Function,
                enqueueToken,
                cancellationVersion)

            EnsureQueueWorker(state)
            Try
                ' BlockingCollection's default ConcurrentQueue is FIFO.  A
                ' cancellation token also makes a full queue cancellable while a
                ' producer is waiting for one of the 16 slots.
                state.CommandQueue.Add(command, enqueueToken)
            Catch
                command.Dispose()
                Throw
            End Try

            Try
                command.WaitForCompletion()
                Return command.WasExecuted
            Finally
                command.Dispose()
            End Try

        Finally
            If linkedCancellationSource IsNot Nothing Then
                linkedCancellationSource.Dispose()
            End If
        End Try
    End Function

    Private Sub CancelQueuedOperations(state As DeviceLockState)
        Dim sourceToCancel As CancellationTokenSource
        SyncLock state.QueueControlLock
            sourceToCancel = state.QueueCancellationSource
            state.QueueCancellationSource = New CancellationTokenSource()
            Interlocked.Increment(state.QueueCancellationVersion)
        End SyncLock

        Try
            ' Do this outside QueueControlLock; cancellation callbacks are
            ' allowed to run synchronously.
            sourceToCancel.Cancel()
        Catch
        End Try
    End Sub

    Private Sub CloseQueuedOperations(state As DeviceLockState)
        Dim sourceToCancel As CancellationTokenSource
        SyncLock state.QueueControlLock
            state.QueueClosed = True
            sourceToCancel = state.QueueCancellationSource
            state.QueueCancellationSource = New CancellationTokenSource()
            Interlocked.Increment(state.QueueCancellationVersion)
        End SyncLock

        Try
            sourceToCancel.Cancel()
        Catch
        End Try
    End Sub

    Private Shared Sub EnsureQueueWorker(state As DeviceLockState)
        SyncLock state.QueueStartLock
            If state.QueueWorker IsNot Nothing Then Return

            state.QueueWorker = New Thread(Sub() QueueWorkerMain(state)) With {
                .IsBackground = True,
                .Name = $"SCSI command queue - {If(String.IsNullOrEmpty(state.Key), "fallback", state.Key)}"
            }
            state.QueueWorker.Start()
        End SyncLock
    End Sub

    Private Shared Sub QueueWorkerMain(state As DeviceLockState)
        Try
            For Each command As QueuedCommand In state.CommandQueue.GetConsumingEnumerable()
                command.Run(Volatile.Read(state.QueueCancellationVersion))
            Next
        Catch ex As Exception
            ' The queue is intentionally process-lifetime, but do not leave
            ' callers blocked if its consumer ever fails unexpectedly.
            Dim command As QueuedCommand = Nothing
            While state.CommandQueue.TryTake(command)
                command.Fail(ex)
            End While
        End Try
    End Sub

    Private Function GetPathState(devicePath As String) As DeviceLockState
        If String.IsNullOrWhiteSpace(devicePath) Then Return _fallbackState

        Dim key As String = NormalizePath(devicePath)
        SyncLock _registryLock
            Dim result As DeviceLockState = Nothing
            If Not _pathStates.TryGetValue(key, result) Then
                result = New DeviceLockState(key, True)
                _pathStates.Add(key, result)
            End If
            Return result
        End SyncLock
    End Function

    Private Function GetHandleState(handle As IntPtr) As DeviceLockState
        If IsInvalidHandle(handle) Then Return _fallbackState

        SyncLock _registryLock
            Dim result As DeviceLockState = Nothing
            If Not _handleStates.TryGetValue(handle.ToInt64(), result) Then
                result = New DeviceLockState($"handle-{handle.ToInt64()}", False)
                _handleStates.Add(handle.ToInt64(), result)
            End If
            Return result
        End SyncLock
    End Function

    Private Function HasLocalWriter(state As DeviceLockState) As Boolean
        SyncLock _registryLock
            Return state.ActiveWriter IsNot Nothing AndAlso state.ActiveWriter.IsAcquired
        End SyncLock
    End Function

    Private Shared Function IsWriterGateAvailable(state As DeviceLockState) As Boolean
        If Not state.CrossProcessEnabled Then Return True

        Try
            If state.WriterGate.WaitOne(0) Then
                state.WriterGate.ReleaseMutex()
                Return True
            End If
            Return False
        Catch ex As AbandonedMutexException
            Try
                state.WriterGate.ReleaseMutex()
            Catch
            End Try
            Return True
        End Try
    End Function

    Private Function WaitForMutex(mutex As Mutex,
                                  timeoutMilliseconds As Integer,
                                  cancellation As WaitHandle) As Boolean
        If timeoutMilliseconds = 0 Then
            If cancellation IsNot Nothing AndAlso cancellation.WaitOne(0) Then Return False
            Try
                Return mutex.WaitOne(0)
            Catch ex As AbandonedMutexException
                Return True
            End Try
        End If

        Dim infinite As Boolean = timeoutMilliseconds < 0
        Dim stopwatch As Stopwatch = Stopwatch.StartNew()

        Do
            If cancellation IsNot Nothing AndAlso cancellation.WaitOne(0) Then Return False

            Dim waitMilliseconds As Integer
            If infinite Then
                waitMilliseconds = 100
            Else
                Dim remaining As Long = CLng(timeoutMilliseconds) - stopwatch.ElapsedMilliseconds
                If remaining <= 0 Then Return False
                waitMilliseconds = CInt(Math.Min(100L, remaining))
            End If

            Try
                If mutex.WaitOne(waitMilliseconds) Then Return True
            Catch ex As AbandonedMutexException
                Return True
            End Try
        Loop
    End Function

    Private Shared Function BuildMutexName(kind As String, key As String) As String
        Dim bytes As Byte() = Encoding.UTF8.GetBytes(key)
        Dim hash As Byte()
        Using sha As SHA256 = SHA256.Create()
            hash = sha.ComputeHash(bytes)
        End Using

        Dim builder As New StringBuilder(hash.Length * 2)
        For Each value As Byte In hash
            builder.Append(value.ToString("x2"))
        Next
        Return $"{MutexNamespace}{kind}.{builder}"
    End Function

    Private Shared Function NormalizePath(devicePath As String) As String
        Dim result As String = devicePath.Trim()
        Dim deviceName As String = result

        If deviceName.StartsWith("\\.", StringComparison.OrdinalIgnoreCase) Then
            deviceName = deviceName.Substring(4)
        End If

        Dim tapeIndex As Integer
        If deviceName.StartsWith("TAPE", StringComparison.OrdinalIgnoreCase) AndAlso
           Integer.TryParse(deviceName.Substring(4), tapeIndex) Then
            Return $"\\.\TAPE{tapeIndex}"
        End If

        If Integer.TryParse(deviceName, tapeIndex) Then
            Return $"\\.\TAPE{tapeIndex}"
        End If

        Return result
    End Function

    Private Shared Function IsInvalidHandle(handle As IntPtr) As Boolean
        Return handle = IntPtr.Zero OrElse handle = New IntPtr(-1)
    End Function

    Friend NotInheritable Class DeviceLockState
        Public ReadOnly Key As String
        Public ReadOnly LocalLock As New Object()
        Public ReadOnly CrossProcessEnabled As Boolean
        Public ReadOnly ProcessGate As Mutex
        Public ReadOnly WriterGate As Mutex
        Public ActiveWriter As WriterLease
        Friend ReadOnly CommandQueue As New BlockingCollection(Of QueuedCommand)(CommandQueueCapacity)
        Friend ReadOnly QueueControlLock As New Object()
        Friend ReadOnly QueueStartLock As New Object()
        Friend QueueCancellationSource As CancellationTokenSource = New CancellationTokenSource()
        Friend QueueCancellationVersion As Integer
        Friend QueueClosed As Boolean
        Friend QueueWorker As Thread

        Public Sub New(key As String, crossProcessEnabled As Boolean)
            Me.Key = key
            Me.CrossProcessEnabled = crossProcessEnabled
            If crossProcessEnabled Then
                ProcessGate = New Mutex(False, BuildMutexName("operation", key))
                WriterGate = New Mutex(False, BuildMutexName("writer", key))
            End If
        End Sub

        Friend Sub ActivateQueue()
            SyncLock QueueControlLock
                If Not QueueClosed Then Return

                QueueCancellationSource = New CancellationTokenSource()
                Interlocked.Increment(QueueCancellationVersion)
                QueueClosed = False
            End SyncLock
        End Sub
    End Class

    Private NotInheritable Class OperationScope
        Implements IDisposable

        Private ReadOnly _state As DeviceLockState
        Private ReadOnly _processGateTaken As Boolean
        Private ReadOnly _localLockTaken As Boolean
        Private _disposed As Integer

        Public Sub New(state As DeviceLockState,
                       processGateTaken As Boolean,
                       Optional localLockTaken As Boolean = True)
            _state = state
            _processGateTaken = processGateTaken
            _localLockTaken = localLockTaken
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            If Interlocked.Exchange(_disposed, 1) <> 0 Then Return

            Try
                If _processGateTaken Then
                    _state.ProcessGate.ReleaseMutex()
                End If
            Finally
                If _localLockTaken Then Monitor.Exit(_state.LocalLock)
            End Try
        End Sub
    End Class

    Friend NotInheritable Class QueuedCommand
        Implements IDisposable

        Private ReadOnly _operation As Func(Of Boolean)
        Private ReadOnly _cancellationToken As CancellationToken
        Private ReadOnly _cancellationVersion As Integer
        Private ReadOnly _completed As New ManualResetEventSlim(False)
        Private ReadOnly _stateLock As New Object()
        Private _failure As Exception
        Private _wasExecuted As Boolean
        Private _disposed As Integer

        Public Sub New(operation As Func(Of Boolean),
                       cancellationToken As CancellationToken,
                       cancellationVersion As Integer)
            _operation = operation
            _cancellationToken = cancellationToken
            _cancellationVersion = cancellationVersion
        End Sub

        Public ReadOnly Property WasExecuted As Boolean
            Get
                SyncLock _stateLock
                    Return _wasExecuted
                End SyncLock
            End Get
        End Property

        Public Function TryStart(currentCancellationVersion As Integer) As Boolean
            SyncLock _stateLock
                If _cancellationToken.IsCancellationRequested OrElse
                   _cancellationVersion <> currentCancellationVersion Then
                    _failure = New OperationCanceledException(_cancellationToken)
                    Return False
                End If
                Return True
            End SyncLock
        End Function

        Public Sub Run(currentCancellationVersion As Integer)
            If Not TryStart(currentCancellationVersion) Then
                _completed.Set()
                Return
            End If

            Try
                Dim executed As Boolean = _operation()
                SyncLock _stateLock
                    _wasExecuted = executed
                End SyncLock
            Catch ex As Exception
                SyncLock _stateLock
                    _failure = ex
                End SyncLock
            Finally
                _completed.Set()
            End Try
        End Sub

        Public Sub Fail(exception As Exception)
            SyncLock _stateLock
                _failure = exception
            End SyncLock
            _completed.Set()
        End Sub

        Public Sub WaitForCompletion()
            _completed.Wait()

            Dim failure As Exception
            SyncLock _stateLock
                failure = _failure
            End SyncLock

            If failure IsNot Nothing Then
                ExceptionDispatchInfo.Capture(failure).Throw()
            End If
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            If Interlocked.Exchange(_disposed, 1) <> 0 Then Return
            _completed.Dispose()
        End Sub
    End Class

    Public NotInheritable Class WriterLease
        Implements IDisposable

        Private ReadOnly _manager As SCSIDeviceLockManager
        Private ReadOnly _state As DeviceLockState
        Private ReadOnly _releaseRequested As New ManualResetEvent(False)
        Private ReadOnly _ready As New ManualResetEvent(False)
        Private ReadOnly _sessionId As String
        Private _leaseThread As Thread
        Private _acquiredValue As Integer
        Private _disposed As Integer
        Private _eventsDisposed As Integer

        Private Const DisposeJoinTimeoutMilliseconds As Integer = 1000

        Friend Sub New(manager As SCSIDeviceLockManager,
                        state As DeviceLockState,
                        sessionId As String)
            _manager = manager
            _state = state
            _sessionId = sessionId
        End Sub

        Public ReadOnly Property DevicePath As String
            Get
                Return _state.Key
            End Get
        End Property

        Public ReadOnly Property SessionId As String
            Get
                Return _sessionId
            End Get
        End Property

        Public ReadOnly Property IsAcquired As Boolean
            Get
                Return Volatile.Read(_acquiredValue) <> 0
            End Get
        End Property

        Friend Function Start(timeoutMilliseconds As Integer) As Boolean
            Dim waitTimeout As Integer = If(timeoutMilliseconds < 0, Timeout.Infinite, timeoutMilliseconds)
            _leaseThread = New Thread(AddressOf LeaseThreadMain) With {
                .IsBackground = True,
                .Name = $"SCSI writer lease - {_state.Key}"
            }
            _leaseThread.Start(waitTimeout)

            If waitTimeout = Timeout.Infinite Then
                _ready.WaitOne()
            Else
                Dim readyTimeout As Integer = CInt(Math.Min(CLng(Integer.MaxValue), CLng(waitTimeout) + 1000L))
                _ready.WaitOne(readyTimeout)
            End If
            Return IsAcquired
        End Function

        Private Sub LeaseThreadMain(argument As Object)
            Try
                Dim timeoutMilliseconds As Integer = CInt(argument)
                Dim processGateTaken As Boolean = False
                Dim writerGateTaken As Boolean = False

                Try
                    If _releaseRequested.WaitOne(0) Then Return
                    If Not _manager.WaitForMutex(_state.ProcessGate, timeoutMilliseconds, _releaseRequested) Then Return
                    processGateTaken = True
                    If Not _manager.WaitForMutex(_state.WriterGate, timeoutMilliseconds, _releaseRequested) Then Return
                    writerGateTaken = True

                    SyncLock _manager._registryLock
                        If _state.ActiveWriter IsNot Nothing Then Return
                        _state.ActiveWriter = Me
                        Volatile.Write(_acquiredValue, 1)
                    End SyncLock
                Catch
                Finally
                    If processGateTaken Then
                        Try
                            _state.ProcessGate.ReleaseMutex()
                        Catch
                        End Try
                    End If
                    _ready.Set()
                End Try

                If Not IsAcquired Then
                    If writerGateTaken Then
                        Try
                            _state.WriterGate.ReleaseMutex()
                        Catch
                        End Try
                    End If
                    Return
                End If

                _releaseRequested.WaitOne()

                Dim localLockTaken As Boolean = False
                Dim releaseGateTaken As Boolean = False
                Try
                    Monitor.Enter(_state.LocalLock)
                    localLockTaken = True
                    If _manager.WaitForMutex(_state.ProcessGate, Timeout.Infinite, Nothing) Then
                        releaseGateTaken = True
                    End If

                    SyncLock _manager._registryLock
                        If ReferenceEquals(_state.ActiveWriter, Me) Then
                            _state.ActiveWriter = Nothing
                            Volatile.Write(_acquiredValue, 0)
                        End If
                    End SyncLock

                    If writerGateTaken Then
                        Try
                            _state.WriterGate.ReleaseMutex()
                        Catch
                        End Try
                    End If
                Finally
                    If releaseGateTaken Then
                        Try
                            _state.ProcessGate.ReleaseMutex()
                        Catch
                        End Try
                    End If
                    If localLockTaken Then
                        Monitor.Exit(_state.LocalLock)
                    End If
                End Try
            Finally
                If Volatile.Read(_disposed) <> 0 Then
                    DisposeEvents()
                End If
            End Try
        End Sub

        Private Sub DisposeEvents()
            If Interlocked.Exchange(_eventsDisposed, 1) <> 0 Then Return

            Try
                _ready.Dispose()
            Finally
                _releaseRequested.Dispose()
            End Try
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            If Interlocked.Exchange(_disposed, 1) <> 0 Then Return

            _releaseRequested.Set()
            Dim leaseThread As Thread = _leaseThread
            If leaseThread Is Nothing Then
                DisposeEvents()
                Return
            End If

            If ReferenceEquals(Thread.CurrentThread, leaseThread) Then Return

            ' The lease thread may be waiting for a SCSI operation to release
            ' the device lock.  Never make window shutdown wait for that command.
            If Not leaseThread.Join(DisposeJoinTimeoutMilliseconds) Then Return

            DisposeEvents()
        End Sub
    End Class
End Class
