Imports System
Imports System.Collections.Generic
Imports System.Security.Cryptography
Imports System.Text
Imports System.Threading

Public NotInheritable Class SCSIDeviceLockManager
    Private Shared ReadOnly _instance As New SCSIDeviceLockManager()

    Private Const MutexNamespace As String = "Local\LTFSCopyGUI.SCSI."

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

        Public Sub New(key As String, crossProcessEnabled As Boolean)
            Me.Key = key
            Me.CrossProcessEnabled = crossProcessEnabled
            If crossProcessEnabled Then
                ProcessGate = New Mutex(False, BuildMutexName("operation", key))
                WriterGate = New Mutex(False, BuildMutexName("writer", key))
            End If
        End Sub
    End Class

    Private NotInheritable Class OperationScope
        Implements IDisposable

        Private ReadOnly _state As DeviceLockState
        Private ReadOnly _processGateTaken As Boolean
        Private _disposed As Integer

        Public Sub New(state As DeviceLockState,
                       processGateTaken As Boolean)
            _state = state
            _processGateTaken = processGateTaken
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            If Interlocked.Exchange(_disposed, 1) <> 0 Then Return

            Try
                If _processGateTaken Then
                    _state.ProcessGate.ReleaseMutex()
                End If
            Finally
                Monitor.Exit(_state.LocalLock)
            End Try
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
