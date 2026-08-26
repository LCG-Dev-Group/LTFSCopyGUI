Imports System.IO
Imports System.Runtime.InteropServices
Imports System.Text
Imports System.ComponentModel
Imports System.Threading
Imports System.Xml.Serialization
Imports LTFSCopyGUI.Native

Public Class SCSIFailureException
    Inherits Win32Exception

    Public ReadOnly Property SenseData As Byte()

    Public Sub New(operation As String,
                   errorCode As Integer,
                   senseData As Byte())
        MyBase.New(If(errorCode = 0, 31, errorCode),
                   If(String.IsNullOrWhiteSpace(operation), "SCSI command failed.", operation))
        Me.SenseData = TapeUtils.NormalizeSCSISense(senseData, True)
    End Sub
End Class

<TypeConverter(GetType(ExpandableObjectConverter))>
<Serializable>
Public Class TapeUtils
    <ThreadStatic>
    Private Shared _lastWin32Error As Integer

    Public Shared ReadOnly Property LastWin32Error As Integer
        Get
            Return _lastWin32Error
        End Get
    End Property

    Private Shared Sub SetLastNativeError(errorCode As Integer)
        _lastWin32Error = errorCode
    End Sub

    Friend Shared Function NormalizeSCSISense(senseData As Byte(), failure As Boolean) As Byte()
        Dim normalizedSense(63) As Byte
        If senseData IsNot Nothing Then
            Array.Copy(senseData, normalizedSense, Math.Min(senseData.Length, normalizedSense.Length))
        End If

        If failure Then
            Dim hasSenseData As Boolean = False
            For Each senseByte As Byte In normalizedSense
                If senseByte <> 0 Then
                    hasSenseData = True
                    Exit For
                End If
            Next
            If Not hasSenseData Then
                normalizedSense(0) = &H70
                normalizedSense(2) = &H4
                normalizedSense(7) = &HA
                normalizedSense(12) = &H44
                normalizedSense(13) = 0
            End If
        End If
        Return normalizedSense
    End Function

    Private Shared Sub ThrowIfSCSIFailed(succeeded As Boolean,
                                         operation As String,
                                         senseData As Byte())
        If Not succeeded Then
            Throw New SCSIFailureException(operation, LastWin32Error, senseData)
        End If
    End Sub


    Public Shared ReadOnly SCSILockManager As SCSIDeviceLockManager = SCSIDeviceLockManager.Instance

    <Obsolete("Use SCSILockManager.GetLock(devicePath/handle) instead.")>
    Public Shared ReadOnly SCSIOperationLock As Object = SCSILockManager.FallbackLock

    Public Shared Function GetSCSIOperationLock(TapeDrive As String) As Object
        Return SCSILockManager.GetLock(TapeDrive)
    End Function

    Public Shared Function GetSCSIOperationLock(handle As IntPtr) As Object
        Return SCSILockManager.GetLock(handle)
    End Function

    Public Shared Function IOCtlDirect(TapeDrive As String,
                                           cdb As Byte(),
                                           dataBuffer As IntPtr,
                                           bufferLength As UInt32,
                                           dataIn As Byte,
                                           timeoutValue As UInt32,
                                           ByRef senseBuffer As Byte(),
                                           Optional cancellationToken As CancellationToken = Nothing) As Boolean

        Dim driveHandle As IntPtr
        If Not OpenTapeDrive(TapeDrive, driveHandle) Then Return False

        Try
            SyncLock SCSILockManager.GetLock(TapeDrive)
                RaiseEvent IOCtlStart()
                Try
                    Return IOCtl.IOCtlDirect(driveHandle,
                                             cdb,
                                             dataBuffer,
                                             bufferLength,
                                             dataIn,
                                             timeoutValue,
                                             senseBuffer,
                                             cancellationToken:=cancellationToken)
                Finally
                    RaiseEvent IOCtlFinished()
                End Try
            End SyncLock
        Finally
            CloseTapeDrive(driveHandle)
        End Try
    End Function

    Public Shared Function IOCtlDirect(handle As IntPtr,
                                       cdb As Byte(),
                                       dataBuffer As IntPtr,
                                       bufferLength As UInt32,
                                       dataIn As Byte,
                                       timeoutValue As UInt32,
                                       ByRef senseBuffer As Byte(),
                                       Optional TargetID As Byte = 0,
                                       Optional LUN As Byte = 0,
                                       Optional ByRef BytesReturned As UInteger = 0,
                                       Optional cancellationToken As CancellationToken = Nothing) As Boolean
        Return IOCtl.IOCtlDirect(handle,
                                 cdb,
                                 dataBuffer,
                                 bufferLength,
                                 dataIn,
                                 timeoutValue,
                                 senseBuffer,
                                 TargetID,
                                 LUN,
                                 BytesReturned,
                                 cancellationToken)
    End Function

    Public Class IOCtl
        Public Shared Function IOCtlDirect(handle As IntPtr,
                                                   cdb As Byte(),
                                                   dataBuffer As IntPtr,
                                                   bufferLength As UInt32,
                                                   dataIn As Byte,
                                                   timeoutValue As UInt32,
                                                   ByRef sense As Byte(),
                                                   Optional ByVal TargetID As Byte = 0,
                                                   Optional ByVal LUN As Byte = 0,
                                                   Optional ByRef BytesReturned As UInteger = 0,
                                                   Optional cancellationToken As CancellationToken = Nothing) As Boolean
            If handle = IntPtr.Zero OrElse handle = New IntPtr(-1) Then
                sense = NormalizeSCSISense(Nothing, True)
                SetLastNativeError(6)
                Return False
            End If

            Dim nativeResult As NativeScsiResult = Nothing
            Dim operationExecuted As Boolean = SCSIDeviceLockManager.Instance.ExecuteQueuedOperation(
                handle,
                Sub()
                    nativeResult = NativeMethods.ExecuteScsiPassThrough(
                        handle,
                        cdb,
                        dataBuffer,
                        bufferLength,
                        dataIn,
                        timeoutValue,
                        TargetID,
                        LUN)
                End Sub,
                cancellationToken)
            If Not operationExecuted Then
                BytesReturned = 0
                sense = NormalizeSCSISense(Nothing, True)
                SetLastNativeError(170)
                Return False
            End If

            BytesReturned = nativeResult.BytesReturned
            sense = NormalizeSCSISense(nativeResult.Sense, Not nativeResult.Succeeded)
            Dim nativeError As Integer = nativeResult.Win32Error
            If Not nativeResult.Succeeded AndAlso nativeError = 0 Then nativeError = 31
            SetLastNativeError(nativeError)
            Return nativeResult.Succeeded
        End Function
    End Class

    Public Shared Function TapeSCSIIOCtlUnmanaged(handle As IntPtr,
                                           cdb As Byte(),
                                           dataBuffer As IntPtr,
                                           bufferLength As UInt32,
                                           dataIn As Byte,
                                           timeoutValue As UInt32,
                                           ByRef senseBuffer As Byte(),
                                           Optional cancellationToken As CancellationToken = Nothing) As Boolean
        RaiseEvent IOCtlStart()
        Try
            Return IOCtl.IOCtlDirect(handle,
                                     cdb,
                                     dataBuffer,
                                     bufferLength,
                                     dataIn,
                                     timeoutValue,
                                     senseBuffer,
                                     cancellationToken:=cancellationToken)
        Finally
            RaiseEvent IOCtlFinished()
        End Try
    End Function

    Public Class SATDef
        Public Enum Protocol As Byte
            ATAHWRst = 0
            ATASWRst = 1
            NonData = 3
            PIODataIn = 4
            PIODataOut = 5
            DMA = 6
            DevDiag = 8
            DevRst = 9
            UDMADataIn = &HA
            UDMADataOut = &HB
            NCQ = &HC
            RespInfo = &HF
        End Enum
        Public Enum T_LENGTH_TYPE As Byte
            NODATA = 0
            FEATURE_FIELD = 1
            COUNT_FIELD = 2
            TPSIU = 3
        End Enum
    End Class
    Public Shared Function SAT12(handle As IntPtr,
                                 Protocol As SATDef.Protocol,
                                 OFF_LINE As Byte, CK_COND As Boolean, T_TYPE As Boolean, T_DIR As Boolean,
                                 BYTE_BLOCK As Boolean, T_LENGTH As SATDef.T_LENGTH_TYPE,
                                 FEATURES As Byte,
                                 COUNT As Byte,
                                 LBA As Integer,
                                 DEVICE As Byte,
                                 COMMAND As Byte,
                                 dataBuffer As IntPtr, bufferLength As Integer,
                                 timeoutValue As UInt32,
                                 ByRef senseBuffer As Byte(),
                                 Optional cancellationToken As CancellationToken = Nothing) As Boolean
        RaiseEvent IOCtlStart()
        Dim cdb As Byte() = {&HA1,
            CByte((Protocol And &HF) << 1),
            CByte(((OFF_LINE And &H3) << 6) Or (CShort(CK_COND) << 5) Or (CShort(T_TYPE) << 4) Or (CShort(T_DIR) << 3) Or (CShort(BYTE_BLOCK) << 2) Or T_LENGTH),
            FEATURES,
            COUNT,
            CByte(LBA And &HFF), CByte((LBA >> 8) And &HFF), CByte((LBA >> 16) And &HFF),
            DEVICE,
            COMMAND,
            0, 0}
        Try
            Return IOCtl.IOCtlDirect(handle,
                                     cdb,
                                     dataBuffer,
                                     CUInt(bufferLength),
                                     CByte(T_DIR),
                                     timeoutValue,
                                     senseBuffer,
                                     cancellationToken:=cancellationToken)
        Finally
            RaiseEvent IOCtlFinished()
        End Try
    End Function
    Public Shared Function SAT16(handle As IntPtr,
                                 Protocol As SATDef.Protocol,
                                 OFF_LINE As Byte, CK_COND As Boolean, T_TYPE As Boolean, T_DIR As Boolean,
                                 BYTE_BLOCK As Boolean, T_LENGTH As SATDef.T_LENGTH_TYPE,
                                 FEATURES As UShort,
                                 COUNT As UShort,
                                 LBA As Long,
                                 DEVICE As Byte,
                                 COMMAND As Byte,
                                 dataBuffer As IntPtr, bufferLength As Integer,
                                 timeoutValue As UInt32,
                                 ByRef senseBuffer As Byte(),
                                 Optional cancellationToken As CancellationToken = Nothing) As Boolean
        RaiseEvent IOCtlStart()
        Dim cdb As Byte() = {&H85,
            CByte((Protocol And &HF) << 1),
            CByte(((OFF_LINE And &H3) << 6) Or (CShort(CK_COND) << 5) Or (CShort(T_TYPE) << 4) Or (CShort(T_DIR) << 3) Or (CShort(BYTE_BLOCK) << 2) Or T_LENGTH),
            CByte((FEATURES >> 8) And &HFF), CByte(FEATURES And &HFF),
            CByte((COUNT >> 8) And &HFF), CByte(COUNT And &HFF),
            CByte((LBA >> 24) And &HFF), CByte(LBA And &HFF), CByte((LBA >> 32) And &HFF), CByte((LBA >> 8) And &HFF), CByte((LBA >> 40) And &HFF), CByte((LBA >> 16) And &HFF),
            DEVICE,
            COMMAND,
            0}
        Try
            Return IOCtl.IOCtlDirect(handle,
                                     cdb,
                                     dataBuffer,
                                     CUInt(bufferLength),
                                     CByte(T_DIR),
                                     timeoutValue,
                                     senseBuffer,
                                     cancellationToken:=cancellationToken)
        Finally
            RaiseEvent IOCtlFinished()
        End Try
    End Function
    Public Shared Function SAT32(handle As IntPtr,
                                 Protocol As SATDef.Protocol,
                                 OFF_LINE As Byte, CK_COND As Boolean, T_TYPE As Boolean, T_DIR As Boolean,
                                 BYTE_BLOCK As Boolean, T_LENGTH As SATDef.T_LENGTH_TYPE,
                                 LBA As Long,
                                 FEATURES As UShort,
                                 COUNT As UShort,
                                 DEVICE As Byte,
                                 COMMAND As Byte,
                                 ICC As Byte,
                                 AUXILIARY As UInteger,
                                 dataBuffer As IntPtr, bufferLength As Integer,
                                 timeoutValue As UInt32,
                                 ByRef senseBuffer As Byte(),
                                 Optional cancellationToken As CancellationToken = Nothing) As Boolean
        RaiseEvent IOCtlStart()
        Dim cdb As Byte() = {&H7F, 0, 0, 0, 0, 0, 0, &H18, &H1F, &HF0,
            CByte((Protocol And &HF) << 1),
            CByte(((OFF_LINE And &H3) << 6) Or (CShort(CK_COND) << 5) Or (CShort(T_TYPE) << 4) Or (CShort(T_DIR) << 3) Or (CShort(BYTE_BLOCK) << 2) Or T_LENGTH),
            0, 0,
            CByte((LBA >> 40) And &HFF), CByte((LBA >> 32) And &HFF), CByte((LBA >> 24) And &HFF), CByte((LBA >> 16) And &HFF), CByte((LBA >> 8) And &HFF), CByte(LBA And &HFF),
            CByte((FEATURES >> 8) And &HFF), CByte(FEATURES And &HFF),
            CByte((COUNT >> 8) And &HFF), CByte(COUNT And &HFF),
            DEVICE,
            COMMAND,
            0,
            ICC,
            CByte((AUXILIARY >> 24) And &HFF), CByte((AUXILIARY >> 16) And &HFF), CByte((AUXILIARY >> 8) And &HFF), CByte(AUXILIARY)}
        Try
            Return IOCtl.IOCtlDirect(handle,
                                     cdb,
                                     dataBuffer,
                                     CUInt(bufferLength),
                                     CByte(T_DIR),
                                     timeoutValue,
                                     senseBuffer,
                                     cancellationToken:=cancellationToken)
        Finally
            RaiseEvent IOCtlFinished()
        End Try
    End Function

    Public Shared Event IOCtlStart()
    Public Shared Event IOCtlFinished()
    <XmlIgnore>
    Public Shared Property DriveOpenCount As New SerializableDictionary(Of String, Integer)

    <XmlIgnore>
    Public Shared Property DriveHandle As New SerializableDictionary(Of String, IntPtr)
    Public Shared Function OpenTapeDrive(TapeDrive As String, ByRef handle As IntPtr) As Boolean
        handle = IntPtr.Zero
        Dim operationScope As IDisposable = SCSILockManager.EnterOperation(TapeDrive)
        If operationScope Is Nothing Then
            handle = IntPtr.Zero
            SetLastNativeError(170)
            Return False
        End If

        Using operationScope
            SyncLock DriveHandle
                If Not DriveHandle.ContainsKey(TapeDrive) Then
                    DriveOpenCount.Add(TapeDrive, 0)
                    DriveHandle.Add(TapeDrive, Nothing)
                End If
                If DriveOpenCount(TapeDrive) = 0 Then
                    Select Case DriverTypeSetting
                        Case DriverType.TapeStream
                            If File.Exists(TapeDrive) Then
                                Dim vt As New TapeImage(TapeDrive)
                                handle = New IntPtr(vt.GetHashCode())
                                TapeStreamMapping.MappingTable.Add(handle, vt)
                            Else
                                Dim openResult As NativeHandleResult = NativeMethods.OpenFile(
                                    TapeDrive,
                                    GENERIC_READ Or GENERIC_WRITE,
                                    0,
                                    OPEN_EXISTING,
                                    FILE_ATTRIBUTE_NORMAL)
                                SetLastNativeError(openResult.Win32Error)
                                handle = openResult.Handle
                            End If
                        Case Else
                            Dim openResult As NativeHandleResult = NativeMethods.OpenFile(
                                TapeDrive,
                                GENERIC_READ Or GENERIC_WRITE,
                                0,
                                OPEN_EXISTING,
                                FILE_ATTRIBUTE_NORMAL)
                            SetLastNativeError(openResult.Win32Error)
                            handle = openResult.Handle
                    End Select
                    If handle = IntPtr.Zero OrElse handle = New IntPtr(-1) Then
                        DriveHandle(TapeDrive) = IntPtr.Zero
                        Return False
                    End If
                    DriveHandle(TapeDrive) = handle
                Else
                    handle = DriveHandle(TapeDrive)
                    If handle = IntPtr.Zero OrElse handle = New IntPtr(-1) Then Return False
                End If
                DriveOpenCount(TapeDrive) += 1
                SCSILockManager.RegisterHandle(TapeDrive, handle)
                Return True
            End SyncLock
        End Using
    End Function
    Public Shared Function CloseTapeDrive(handle As IntPtr,
                                          Optional lockTimeoutMilliseconds As Integer = -1) As Boolean
        Dim deviceLock As Object = SCSILockManager.GetLock(handle)
        Dim lockTaken As Boolean = False
        Try
            If lockTimeoutMilliseconds < 0 Then
                Threading.Monitor.Enter(deviceLock)
                lockTaken = True
            Else
                lockTaken = Threading.Monitor.TryEnter(deviceLock, lockTimeoutMilliseconds)
            End If

            If Not lockTaken Then
                SetLastNativeError(170)
                Return False
            End If

            SyncLock DriveHandle
                If DriveHandle.ContainsValue(handle) Then
                    For Each key As String In DriveHandle.Keys
                        If DriveHandle(key).Equals(handle) Then
                            If DriveOpenCount(key) > 1 Then
                                DriveOpenCount(key) -= 1
                                Return True
                            ElseIf DriveOpenCount(key) = 1 Then
                                DriveOpenCount(key) -= 1
                                DriveHandle(key) = IntPtr.Zero
                                Exit For
                            Else
                                Return True
                            End If
                        End If
                    Next
                End If
                Dim result As Boolean
                ' This is the final close path.  Stop accepting new queue
                ' entries before invalidating the native handle; the local
                ' device lock above has already waited for a running queued
                ' command to finish.
                SCSILockManager.CloseQueuedOperations(handle)
                Select Case DriverTypeSetting
                    Case DriverType.TapeStream
                        Dim ts As TapeImage = Nothing
                        TapeStreamMapping.MappingTable.TryGetValue(handle, ts)
                        If ts IsNot Nothing Then
                            ts.CloseFile()
                            TapeStreamMapping.MappingTable.Remove(handle)
                            ts = Nothing
                            result = True
                        Else
                            Dim closeResult As NativeCallResult = NativeMethods.CloseHandle(handle)
                            SetLastNativeError(closeResult.Win32Error)
                            result = closeResult.Succeeded
                        End If
                    Case Else
                        Dim closeResult As NativeCallResult = NativeMethods.CloseHandle(handle)
                        SetLastNativeError(closeResult.Win32Error)
                        result = closeResult.Succeeded
                End Select
                SCSILockManager.UnregisterHandle(handle)
                Return result
            End SyncLock
        Finally
            If lockTaken Then Threading.Monitor.Exit(deviceLock)
        End Try
    End Function

    Private Shared Function RunOpenedTapeOperation(Of TResult)(TapeDrive As String,
                                                               operation As Func(Of IntPtr, TResult)) As TResult
        If operation Is Nothing Then Throw New ArgumentNullException(NameOf(operation))

        SyncLock SCSILockManager.GetLock(TapeDrive)
            Dim handle As IntPtr
            If Not OpenTapeDrive(TapeDrive, handle) Then Throw New Exception($"Cannot open {TapeDrive}")

            Dim result As TResult
            Try
                result = operation(handle)
            Catch
                ' Preserve the command exception, but do not leak the open
                ' reference when a SCSI failure is propagated.
                Try
                    CloseTapeDrive(handle)
                Catch
                End Try
                Throw
            End Try

            If Not CloseTapeDrive(handle) Then Throw New Exception($"Cannot close {TapeDrive}")
            Return result
        End SyncLock
    End Function

    Public Shared Function IsOpened(TapeDrive As String) As Boolean
        SyncLock DriveHandle
            If DriveHandle.ContainsKey(TapeDrive) Then
                Return DriveOpenCount(TapeDrive) > 0
            Else
                Return False
            End If
        End SyncLock
    End Function
    Public Shared Function IsOpened(handle As IntPtr) As Boolean
        SyncLock DriveHandle
            If DriveHandle.ContainsValue(handle) Then
                For Each key As String In DriveHandle.Keys
                    If DriveHandle(key).Equals(handle) Then
                        If DriveOpenCount(key) > 0 Then
                            Return True
                        End If
                    End If
                Next
            Else
                Return False
            End If
        End SyncLock
        Return False
    End Function


    Structure LPSECURITY_ATTRIBUTES
        Dim nLength As UInt32
        Dim lpSecurityDescriptor As UIntPtr
        Dim bInheritHandle As Boolean
    End Structure
    Public Const GENERIC_READ As UInteger = &H80000000UI
    Public Const GENERIC_WRITE As UInteger = &H40000000UI
    Public Const OPEN_EXISTING As UInteger = 3UI
    Public Const FILE_ATTRIBUTE_NORMAL As UInteger = &H80UI

    Public Shared AllowPartition As Boolean = True
    Public Shared Function ReadAppInfo(TapeDrive As String) As String
        'TC_MAM_APPLICATION_VENDOR = 0x0800 LEN = 8
        'TC_MAM_APPLICATION_NAME = 0x0801 = 0x0800 LEN = 32
        'TC_MAM_APPLICATION_VERSION = 0x0802 = 0x0800 LEN = 8
        Return ReadMAMAttributeString(TapeDrive, 8, 0).TrimEnd(" "c) &
            " " & ReadMAMAttributeString(TapeDrive, 8, 1).TrimEnd(" "c) &
            " " & ReadMAMAttributeString(TapeDrive, 8, 2).TrimEnd(" "c)
    End Function
    Public Shared Function ReadBarcode(TapeDrive As String) As String
        'TC_MAM_BARCODE = 0x0806 LEN = 32
        Return ReadMAMAttributeString(TapeDrive, 8, 6).TrimEnd(" "c)
    End Function
    Public Shared Function ReadBarcode(handle As IntPtr) As String
        'TC_MAM_BARCODE = 0x0806 LEN = 32
        Return ReadMAMAttributeString(handle, 8, 6).TrimEnd(" "c)
    End Function
    Public Shared Function ReadRemainingCapacity(TapeDrive As String, Optional ByVal Partition As Byte = 0) As UInt64
        Return CULng(MAMAttribute.FromTapeDrive(TapeDrive, &H0, Partition).AsNumeric)
    End Function
    Public Shared Function TestUnitReady(handle As IntPtr) As Byte()
        Select Case DriverTypeSetting
            Case DriverType.TapeStream
                Dim ts As TapeImage = Nothing
                TapeStreamMapping.MappingTable.TryGetValue(handle, ts)
                Return If(ts IsNot Nothing, TapeImage.SenseData.NoSense, TapeImage.SenseData.NotPresent)
            Case Else
                SyncLock SCSILockManager.GetLock(handle)
                    Dim result As Byte() = {}
                    Try
                        SCSIReadParam(handle, {0, 0, 0, 0, 0, 0}, 0, Function(sense As Byte()) As Boolean
                                                                         result = sense
                                                                         Return True
                                                                     End Function)
                    Catch ex As SCSIFailureException
                        ' TestUnitReady historically reports the sense data
                        ' to its caller instead of throwing for a not-ready
                        ' device.  Preserve that contract for transport
                        ' failures as well.
                        result = ex.SenseData
                    End Try
                    Return result
                End SyncLock
        End Select

    End Function
    Public Shared Function CheckSwitchConfig(handle As IntPtr) As Boolean
        Return CheckSwitchConfig(Inquiry(handle))
    End Function
    Public Shared Function CheckSwitchConfig(Drive As BlockDevice) As Boolean
        Try
            If Drive Is Nothing Then Return False
            If Drive.ProductId.Contains("LTO") OrElse Drive.ProductId.Contains("Ultrium") Then
                My.Settings.TapeUtils_DriverType = DriverType.LTO
                If Drive.ProductId.Contains("Ultrium 3") OrElse Drive.ProductId.Contains("Ultrium 2") OrElse Drive.ProductId.Contains("Ultrium 1") OrElse
                Drive.ProductId.Contains("Gen 3") OrElse Drive.ProductId.Contains("Gen 2") OrElse Drive.ProductId.Contains("Gen 1") Then
                    My.Settings.LTFSWriter_DisablePartition = True
                Else
                    My.Settings.LTFSWriter_DisablePartition = False
                End If
                Return True
            ElseIf Drive.ProductId.Contains("3592") Then
                My.Settings.TapeUtils_DriverType = DriverType.IBM3592
                If Drive.ProductId.Contains("E06") OrElse Drive.ProductId.Contains("E05") OrElse Drive.ProductId.Contains("J1A") Then
                    My.Settings.LTFSWriter_DisablePartition = True
                Else
                    My.Settings.LTFSWriter_DisablePartition = False
                End If
                AllowPartition = Not My.Settings.LTFSWriter_DisablePartition
                Return True
            ElseIf Drive.ProductId.Contains("T10000") Then
                My.Settings.TapeUtils_DriverType = DriverType.T10K
                My.Settings.LTFSWriter_DisablePartition = False
            ElseIf Drive.ProductId.Contains("TapeImage") Then
                My.Settings.TapeUtils_DriverType = DriverType.TapeStream
                My.Settings.LTFSWriter_DisablePartition = False
            End If
        Catch ex As Exception

        End Try
        Return False
    End Function
    Public Shared Function Inquiry(handle As IntPtr) As BlockDevice
        SyncLock SCSILockManager.GetLock(handle)
            If DriverTypeSetting = DriverType.TapeStream Then
                Dim tapeImage As TapeImage = Nothing
                TapeStreamMapping.MappingTable.TryGetValue(handle, tapeImage)
                If tapeImage Is Nothing Then Return Nothing

                Return New BlockDevice With {
                    .SerialNumber = If(tapeImage.MediumSN, String.Empty),
                    .VendorId = "LTFSCopyGUI",
                    .ProductId = "TapeImage"
                }
            End If

            Dim PageLen As Byte = CByte(SCSIReadParam(handle:=handle, cdbData:=New Byte() {&H12, 1, &H80, 0, 4, 0}, paramLen:=4, senseReport:=Nothing, timeout:=10)(3) + 4)
            If PageLen <> 4 Then
                Dim PageData() As Byte = SCSIReadParam(handle:=handle, cdbData:=New Byte() {&H12, 1, &H80, 0, PageLen, 0}, paramLen:=PageLen, senseReport:=Nothing, timeout:=10)
                Dim SN As String = Encoding.ASCII.GetString(PageData.Skip(4).ToArray()).Replace(vbNullChar, "").TrimEnd(" "c).TrimStart(" "c)
                PageData = SCSIReadParam(handle:=handle, cdbData:=New Byte() {&H12, 0, 0, 0, &H60, 0}, paramLen:=&H60, senseReport:=Nothing, timeout:=10)
                Dim Vendor As String = Encoding.ASCII.GetString(PageData.Skip(8).Take(8).ToArray()).Replace(vbNullChar, "").TrimEnd(" "c).TrimStart(" "c)
                Dim Product As String = Encoding.ASCII.GetString(PageData.Skip(16).Take(16).ToArray()).Replace(vbNullChar, "").TrimEnd(" "c).TrimStart(" "c)
                Return New BlockDevice With {.SerialNumber = SN, .VendorId = Vendor, .ProductId = Product}
            Else
                PageLen = CByte(SCSIReadParam(handle:=handle, cdbData:=New Byte() {&H12, 0, 0, 0, 5, 0}, paramLen:=5, senseReport:=Nothing, timeout:=10)(4) + 4)
                If PageLen = 4 Then Return Nothing
                Dim PageData() As Byte = SCSIReadParam(handle:=handle, cdbData:=New Byte() {&H12, 0, 0, 0, PageLen, 0}, paramLen:=PageLen, senseReport:=Nothing, timeout:=10)
                Dim SN As String = Encoding.ASCII.GetString(PageData.Skip(36).Take(8).ToArray()).Replace(vbNullChar, "").TrimEnd(" "c).TrimStart(" "c)
                Dim Vendor As String = Encoding.ASCII.GetString(PageData.Skip(8).Take(8).ToArray()).Replace(vbNullChar, "").TrimEnd(" "c).TrimStart(" "c)
                Dim Product As String = Encoding.ASCII.GetString(PageData.Skip(16).Take(16).ToArray()).Replace(vbNullChar, "").TrimEnd(" "c).TrimStart(" "c)
                Product &= " " & Encoding.ASCII.GetString(PageData.Skip(32).Take(4).ToArray()).Replace(vbNullChar, "").TrimEnd(" "c).TrimStart(" "c)
                Return New BlockDevice With {.SerialNumber = SN, .VendorId = Vendor, .ProductId = Product}
            End If
        End SyncLock
    End Function
    Public Shared Function Inquiry(TapeDrive As String) As BlockDevice
        Return RunOpenedTapeOperation(TapeDrive,
                                      Function(handle As IntPtr) As BlockDevice
                                          Return Inquiry(handle)
                                      End Function)
    End Function
    Public Class BlockLimits
        Public MaximumBlockLength As UInt64
        Public MinimumBlockLength As UInt16
        Public Shared Widening Operator CType(data As BlockLimits) As UInt64
            Return data.MaximumBlockLength
        End Operator
    End Class
    Public Shared Property GlobalBlockLimit As Integer = 1048576
#Region "SCSIOP_READ"
    Public Shared Function ReadBlock(handle As IntPtr, Optional ByRef sense As Byte() = Nothing, Optional ByVal BlockSizeLimit As UInteger = &H80000, Optional ByVal Truncate As Boolean = False) As Byte()
        If DriverTypeSetting = DriverType.TapeStream Then
            SyncLock SCSILockManager.GetLock(handle)
                Dim vt As TapeImage = Nothing
                TapeStreamMapping.MappingTable.TryGetValue(handle, vt)
                If vt Is Nothing Then
                    sense = TapeImage.SenseData.NotPresent
                    Return {}
                End If

                Dim data As Byte() = vt.ReadBlock(sense)
                If data Is Nothing Then data = Array.Empty(Of Byte)()

                Dim blockLimit As Integer = CInt(Math.Min(CLng(BlockSizeLimit), CLng(Math.Max(0, GlobalBlockLimit))))
                If Truncate AndAlso data.Length > blockLimit Then
                    Dim truncated As Byte() = Array.Empty(Of Byte)()
                    If blockLimit > 0 Then
                        ReDim truncated(blockLimit - 1)
                        Array.Copy(data, truncated, blockLimit)
                    End If

                    ' Match the native READ result for a short requested
                    ' transfer: ILI is informational (sense key 0), while
                    ' the caller receives exactly the requested bytes.
                    Dim iliDiffBytes As Integer = blockLimit - data.Length
                    Dim iliSense As Byte() = TapeImage.SenseData.NoSense.ToArray()
                    iliSense(0) = &HF0
                    iliSense(2) = &H20
                    iliSense(3) = CByte((iliDiffBytes >> 24) And &HFF)
                    iliSense(4) = CByte((iliDiffBytes >> 16) And &HFF)
                    iliSense(5) = CByte((iliDiffBytes >> 8) And &HFF)
                    iliSense(6) = CByte(iliDiffBytes And &HFF)
                    iliSense(7) = &H10
                    iliSense(16) = &H2C
                    iliSense(17) = If(iliDiffBytes > 0, CByte(&H73), CByte(&H72))
                    sense = iliSense
                    Return truncated
                End If
                Return data
            End SyncLock
        End If
        Dim senseRaw(63) As Byte
        BlockSizeLimit = CUInt(Math.Min(BlockSizeLimit, GlobalBlockLimit))
        If sense Is Nothing Then sense = {}
        Dim RawDataU As IntPtr
        Dim DiffBytes As Int32
        Dim DataLen As Integer
        SyncLock SCSILockManager.GetLock(handle)
            Select Case DriverTypeSetting
                Case DriverType.SLR1
                    Dim BlockCount As Integer = CInt(Math.Ceiling(BlockSizeLimit / 512))
                    RawDataU = SCSIReadParamUnmanaged(handle:=handle, cdbData:=New Byte() {8, 1, CByte((BlockCount >> 16) And &HFF), CByte((BlockCount >> 8) And &HFF), CByte(BlockCount And &HFF), 0},
                                              paramLen:=CInt(BlockSizeLimit), senseReport:=Function(senseData As Byte()) As Boolean
                                                                                               senseRaw = senseData
                                                                                               Return True
                                                                                           End Function)
                Case Else
                    RawDataU = SCSIReadParamUnmanaged(handle:=handle, cdbData:=New Byte() {8, 0, CByte((BlockSizeLimit >> 16) And &HFF), CByte((BlockSizeLimit >> 8) And &HFF), CByte(BlockSizeLimit And &HFF), 0},
                                              paramLen:=CInt(BlockSizeLimit), senseReport:=Function(senseData As Byte()) As Boolean
                                                                                               senseRaw = senseData
                                                                                               Return True
                                                                                           End Function)

            End Select
            sense = senseRaw
            For i As Integer = 3 To 6
                DiffBytes <<= 8
                DiffBytes = DiffBytes Or sense(i)
            Next
            If Truncate Then DiffBytes = Math.Max(DiffBytes, 0)
            DataLen = CInt(Math.Min(BlockSizeLimit, BlockSizeLimit - DiffBytes))
            If Not Truncate AndAlso DiffBytes < 0 AndAlso (BlockSizeLimit - DiffBytes) < GlobalBlockLimit Then
                Marshal.FreeHGlobal(RawDataU)
                Dim p As New PositionData(handle:=handle)
                Locate(handle:=handle, BlockAddress:=CULng(p.BlockNumber - 1), Partition:=p.PartitionNumber)
                Return ReadBlock(handle:=handle, sense:=sense, BlockSizeLimit:=CUInt(BlockSizeLimit - DiffBytes), Truncate:=Truncate)
            End If

        End SyncLock
        Dim RawData(DataLen - 1) As Byte
        Marshal.Copy(RawDataU, RawData, 0, CInt(Math.Min(BlockSizeLimit, DataLen)))
        Marshal.FreeHGlobal(RawDataU)
        Return RawData
    End Function
    Public Shared Function ReadBlock(TapeDrive As String) As Byte()
        Return ReadBlock(TapeDrive, Nothing, &H80000, False)
    End Function
    Public Shared Function ReadBlock(TapeDrive As String, BlockSizeLimit As UInteger) As Byte()
        Return ReadBlock(TapeDrive, Nothing, BlockSizeLimit, False)
    End Function
    Public Shared Function ReadBlock(TapeDrive As String, ByRef sense As Byte(), BlockSizeLimit As UInteger) As Byte()
        Return ReadBlock(TapeDrive, sense, BlockSizeLimit, False)
    End Function
    Public Shared Function ReadBlock(TapeDrive As String, ByRef sense As Byte()) As Byte()
        Return ReadBlock(TapeDrive, sense, &H80000, False)
    End Function
    Public Shared Function ReadBlock(TapeDrive As String, ByRef sense As Byte(), ByVal BlockSizeLimit As UInteger, ByVal Truncate As Boolean) As Byte()
        SyncLock SCSILockManager.GetLock(TapeDrive)
            Dim handle As IntPtr
            If Not OpenTapeDrive(TapeDrive, handle) Then Throw New Exception($"Cannot open {TapeDrive}")
            Dim result As Byte()
            Try
                result = ReadBlock(handle, sense, BlockSizeLimit, Truncate)
            Catch
                Try
                    CloseTapeDrive(handle)
                Catch
                End Try
                Throw
            End Try
            If Not CloseTapeDrive(handle) Then Throw New Exception($"Cannot close {TapeDrive}")
            Return result
        End SyncLock
    End Function

#End Region
#Region "SCSIOP_READ_BLOCK_LIMITS"
    Public Shared Function ReadBlockLimits(TapeDrive As String) As BlockLimits
        Return RunOpenedTapeOperation(TapeDrive,
                                      Function(handle As IntPtr) As BlockLimits
                                          Return ReadBlockLimits(handle)
                                      End Function)
    End Function
    Public Shared Function ReadBlockLimits(handle As IntPtr) As BlockLimits
        Select Case DriverTypeSetting
            Case DriverType.TapeStream
                Return New BlockLimits With {.MinimumBlockLength = 0, .MaximumBlockLength = 16777216}
            Case Else
                Dim data As Byte() = SCSIReadParam(handle, {5, 0, 0, 0, 0, 0}, 6)
                Return New BlockLimits With {.MaximumBlockLength = CULng(data(1)) << 16 Or CULng(data(2)) << 8 Or data(3),
            .MinimumBlockLength = CUShort(data(4)) << 8 Or data(5)}
        End Select
    End Function
#End Region
#Region "SCSIOP_READ_BUFFER"
    Public Shared Function ReadBuffer(handle As IntPtr, BufferID As Byte) As Byte()
        Return ReadBuffer(handle, BufferID, 2)
    End Function
    Public Shared Function ReadBuffer(handle As IntPtr, BufferID As Byte, Mode As Byte) As Byte()
        'Get EEPROM buffer Length
        Dim cdbD0 As Byte() = {&H3C, 3, BufferID, 0, 0, 0, 0, 0, 4, 0}
        Dim lenData As Byte() = {0, 0, 0, 0}
        Dim data0 As IntPtr = Marshal.AllocHGlobal(lenData.Length)
        Dim sense(64) As Byte
        SyncLock SCSILockManager.GetLock(handle)
            Try
                Marshal.Copy(lenData, 0, data0, lenData.Length)
                Flush(handle)
                Dim lengthResult As Boolean = TapeSCSIIOCtlUnmanaged(handle, cdbD0, data0, CUInt(lenData.Length), 1, 60, sense)
                ThrowIfSCSIFailed(lengthResult, "SCSI read buffer length", sense)
                Marshal.Copy(data0, lenData, 0, lenData.Length)
            Finally
                Marshal.FreeHGlobal(data0)
            End Try
            Dim BufferLen As Integer
            For i As Integer = 1 To lenData.Length - 1
                BufferLen <<= 8
                BufferLen = BufferLen Or lenData(i)
            Next
            If BufferLen = &HFFFFFF Then BufferLen += 1
            'Dump EEPROM
            Dim Offset As Integer = 0
            Dim Remain As Integer = BufferLen
            Dim Seg As Integer = GlobalBlockLimit
            Dim result As New List(Of Byte)
            While Remain > 0
                Dim seglen As Integer = Math.Min(Seg, Remain)
                Dim cdbD1 As Byte() = {&H3C, Mode, BufferID, CByte((Offset >> 16) And &HFF), CByte((Offset >> 8) And &HFF), CByte(Offset And &HFF), CByte((seglen >> 16) And &HFF), CByte((seglen >> 8) And &HFF), CByte(seglen And &HFF), 0}
                Offset += seglen
                Remain -= seglen
                Dim dumpData(seglen - 1) As Byte
                Dim data1 As IntPtr = Marshal.AllocHGlobal(dumpData.Length)
                Try
                    Marshal.Copy(dumpData, 0, data1, dumpData.Length)
                    Dim segmentResult As Boolean = TapeSCSIIOCtlUnmanaged(handle, cdbD1, data1, CUInt(dumpData.Length), 1, 60, sense)
                    ThrowIfSCSIFailed(segmentResult, "SCSI read buffer segment", sense)
                    Marshal.Copy(data1, dumpData, 0, dumpData.Length)
                Finally
                    Marshal.FreeHGlobal(data1)
                End Try
                result.AddRange(dumpData)
            End While
            Return result.ToArray()
        End SyncLock
    End Function
    Public Shared Function ReadBuffer(TapeDrive As String, BufferID As Byte) As Byte()
        Return ReadBuffer(TapeDrive, BufferID, 2)
    End Function
    Public Shared Function ReadBuffer(TapeDrive As String, BufferID As Byte, Mode As Byte) As Byte()
        Return RunOpenedTapeOperation(TapeDrive,
                                      Function(handle As IntPtr) As Byte()
                                          Return ReadBuffer(handle, BufferID, Mode)
                                      End Function)
    End Function
    Public Shared Function ReceiveDiagCM(TapeDrive As String, Optional ByVal len10h As Integer = 0) As Byte()
        Return RunOpenedTapeOperation(TapeDrive,
                                      Function(handle As IntPtr) As Byte()
                                          Return ReceiveDiagCM(handle, len10h)
                                      End Function)
    End Function
    Public Shared Function ReceiveDiagCM(handle As IntPtr, Optional ByVal len10h As Integer = 0) As Byte()
        Dim bufferrawdata As Byte()
        SyncLock SCSILockManager.GetLock(handle)
            Dim commandResult As Boolean = SendSCSICommand(handle, {&H1D, &H11, 0, 0, &H14, 0}, {&HB0, 0, 0, &H10, 0, 0, 0, 0, 0, 0, &H1F, &HE0, 0, 0, 0, &H15, 0, 0, 0, 8}, 0)
            If Not commandResult Then Throw New SCSIFailureException("SCSI receive diagnostic setup", LastWin32Error, Nothing)
            Dim len As UInteger = &HC7A2
            If len10h = 0 Then len10h = ReadBuffer(handle, &H10).Length
            If len10h > 0 Then len = CUInt(6 + (len10h \ 16) * 50 + (len10h Mod 16) * 3)
            bufferrawdata = SCSIReadParam(handle, {&H1C, 1, &HB0, CByte((len >> 8) And &HFF), CByte(len And &HFF), 0}, &HC7A2)
        End SyncLock

        Dim bufferdgtext As String = Encoding.ASCII.GetString(bufferrawdata, 6, bufferrawdata.Count - 6)
        bufferdgtext = bufferdgtext.Replace(Chr(0), "")
        Dim textlines As String() = bufferdgtext.Split({vbCr, vbLf}, StringSplitOptions.RemoveEmptyEntries)
        Dim bufferdata As New List(Of Byte)
        For Each l As String In textlines
            If l Is Nothing OrElse l.Length <= 2 Then Continue For
            Dim dataline As String() = l.Split({" "}, StringSplitOptions.RemoveEmptyEntries)
            For Each b As String In dataline
                Try
                    bufferdata.Add(Convert.ToByte(b, 16))
                Catch ex As Exception
                    If b IsNot Nothing Then
                        Throw New Exception($"Error with line:{l}{vbCrLf}    byte {b}({ex.ToString()}){vbCrLf}")
                    Else
                        Throw New Exception($"Error with line:{l}{vbCrLf}({ex.ToString()}){vbCrLf}")
                    End If
                End Try
            Next
        Next
        Return bufferdata.ToArray()
    End Function
#End Region

#Region "SCSIOP_WRITE_BUFFER"
    Public Shared Function WriteBuffer(handle As IntPtr, BufferID As Byte, Data As Byte()) As Boolean
        Return WriteBuffer(handle, BufferID, 2, Data)
    End Function
    Public Shared Function WriteBuffer(handle As IntPtr, BufferID As Byte, Mode As Byte, Data As Byte()) As Boolean
        'Get EEPROM buffer Length
        WriteBuffer = False
        Dim len As Integer = Data.Length
        Dim cdb As Byte() = {&H3B, Mode, BufferID, 0, 0, 0, CByte(((len >> 16) And &HFF)), CByte(((len >> 8) And &HFF)), CByte((len And &HFF)), 0}
        Dim DataPtr As IntPtr = Marshal.AllocHGlobal(len)
        Try
            Marshal.Copy(Data, 0, DataPtr, len)
            Dim sense(64) As Byte
            SyncLock SCSILockManager.GetLock(handle)
                Flush(handle)
                'Write Buffer Data
                Return TapeSCSIIOCtlUnmanaged(handle, cdb, DataPtr, CUInt(len), 0, 60, sense)
            End SyncLock
        Finally
            Marshal.FreeHGlobal(DataPtr)
        End Try
    End Function
    Public Shared Function WriteBuffer(TapeDrive As String, BufferID As Byte, Data As Byte()) As Boolean
        Return WriteBuffer(TapeDrive, BufferID, 2, Data)
    End Function
    Public Shared Function WriteBuffer(TapeDrive As String, BufferID As Byte, Mode As Byte, Data As Byte()) As Boolean
        Return RunOpenedTapeOperation(TapeDrive,
                                      Function(handle As IntPtr) As Boolean
                                          Return WriteBuffer(handle, BufferID, Mode, Data)
                                      End Function)
    End Function
#End Region

    Public Shared Function ReadFileMark(handle As IntPtr, Optional ByRef sense As Byte() = Nothing) As Boolean
        SyncLock SCSILockManager.GetLock(handle)
            Dim data As Byte() = ReadBlock(handle:=handle, sense:=sense)
            If data.Length = 0 Then Return True
            Dim p As New PositionData(handle)
            If Not AllowPartition Then
                Space6(handle:=handle, Count:=-1, Code:=LocateDestType.Block)
            Else
                Locate(handle:=handle, BlockAddress:=CULng(p.BlockNumber - 1), Partition:=p.PartitionNumber)
            End If
            Return False
        End SyncLock
    End Function
    Public Shared Function ReadFileMark(TapeDrive As String, Optional ByRef sense As Byte() = Nothing) As Boolean
        SyncLock SCSILockManager.GetLock(TapeDrive)
            Dim handle As IntPtr
            If Not OpenTapeDrive(TapeDrive, handle) Then Throw New Exception($"Cannot open {TapeDrive}")
            Dim result As Boolean
            Try
                result = ReadFileMark(handle, sense)
            Catch
                Try
                    CloseTapeDrive(handle)
                Catch
                End Try
                Throw
            End Try
            If Not CloseTapeDrive(handle) Then Throw New Exception($"Cannot close {TapeDrive}")
            Return result
        End SyncLock
    End Function
    Public Shared Function ReadToFileMark(handle As IntPtr) As Byte()
        Return ReadToFileMark(handle:=handle, BlockSizeLimit:=&H80000)
    End Function
    Public Shared Function ReadToFileMark(handle As IntPtr, ByVal BlockSizeLimit As UInteger) As Byte()
        SyncLock SCSILockManager.GetLock(handle)
            Dim buffer As New List(Of Byte)
            BlockSizeLimit = CUInt(Math.Min(BlockSizeLimit, GlobalBlockLimit))
            While True
                Dim sense(63) As Byte
                Dim readData As Byte() = ReadBlock(handle, sense, BlockSizeLimit)
                Dim Add_Key As UInt16 = CUShort(CInt(sense(12)) << 8 Or sense(13))
                If readData.Length > 0 Then
                    buffer.AddRange(readData)
                End If
                If (Add_Key >= 1 And Add_Key <> 4) Then
                    Exit While
                End If
            End While
            Return buffer.ToArray()
        End SyncLock
    End Function
    Public Shared Function ReadToFileMark(handle As IntPtr, outputFileName As String) As Boolean
        Return ReadToFileMark(handle:=handle, outputFileName:=outputFileName, BlockSizeLimit:=&H80000)
    End Function
    Public Shared Function ReadToFileMark(handle As IntPtr, outputFileName As String, ByVal BlockSizeLimit As Integer) As Boolean
        Return ReadToFileMark(handle, outputFileName, CUInt(BlockSizeLimit))
    End Function
    Public Shared Function ReadToFileMark(handle As IntPtr, outputFileName As String, ByVal BlockSizeLimit As UInteger) As Boolean
        SyncLock SCSILockManager.GetLock(handle)
            ' The old implementation issued a READ POSITION probe here, but
            ' never used its result.  It also made TapeImage index loading
            ' depend on a native SCSI command against a managed pseudo handle.
            Dim buffer As New FileStream(outputFileName, FileMode.Create, FileAccess.ReadWrite, FileShare.Read)
            BlockSizeLimit = CUInt(Math.Min(BlockSizeLimit, GlobalBlockLimit))
            While True
                Dim sense(63) As Byte
                Dim readData As Byte() = ReadBlock(handle, sense, BlockSizeLimit)
                Dim Add_Key As UInt16 = CUShort(CInt(sense(12)) << 8 Or sense(13))
                If readData.Length > 0 Then
                    buffer.Write(readData, 0, readData.Length)
                End If
                If (Add_Key >= 1 And Add_Key <> 4) Then
                    Exit While
                End If
            End While
            buffer.Close()
            Return True
        End SyncLock
    End Function
    Public Shared Function ReadToFileMark(TapeDrive As String) As Byte()
        Return ReadToFileMark(TapeDrive:=TapeDrive, BlockSizeLimit:=&H80000)
    End Function
    Public Shared Function ReadToFileMark(TapeDrive As String, ByVal BlockSizeLimit As UInteger) As Byte()
        Return RunOpenedTapeOperation(TapeDrive,
                                      Function(handle As IntPtr) As Byte()
                                          Return ReadToFileMark(handle, BlockSizeLimit)
                                      End Function)
    End Function
    Public Shared Function ReadToFileMark(TapeDrive As String, outputFileName As String) As Boolean
        Return ReadToFileMark(TapeDrive:=TapeDrive, outputFileName:=outputFileName, BlockSizeLimit:=&H80000)
    End Function
    Public Shared Function ReadToFileMark(TapeDrive As String, outputFileName As String, ByVal BlockSizeLimit As Integer) As Boolean
        Return ReadToFileMark(TapeDrive, outputFileName, CUInt(BlockSizeLimit))
    End Function
    Public Shared Function ReadToFileMark(TapeDrive As String, outputFileName As String, ByVal BlockSizeLimit As UInteger) As Boolean
        Return RunOpenedTapeOperation(TapeDrive,
                                      Function(handle As IntPtr) As Boolean
                                          Return ReadToFileMark(handle, outputFileName, BlockSizeLimit)
                                      End Function)
    End Function
    Public Shared Function ReadEOWPosition(TapeDrive As String) As Byte()
        Dim lenData As Byte() = SCSIReadParam(TapeDrive, {&HA3, &H1F, &H45, 2, 0, 0, 0, 0, 0, 2, 0, 0}, 2)
        Dim len As UInt16 = lenData(0)
        len <<= 8
        len = len Or lenData(1)
        len = CUShort(len + 2)
        Dim rawParam As Byte() = SCSIReadParam(TapeDrive:=TapeDrive, cdbData:=New Byte() {&HA3, &H1F, &H45, 2, 0, 0, 0, 0, CByte(len >> 8), CByte(len And &HFF), 0, 0}, paramLen:=len)
        Return rawParam.Skip(4).ToArray()
    End Function
    Public Enum LocateDestType
        Block = 0
        FileMark = 1
        EOD = 3
    End Enum
    Public Shared Function Locate(handle As IntPtr, BlockAddress As UInt64, Partition As Byte) As UInt16
        Return Locate(handle:=handle, BlockAddress:=BlockAddress, Partition:=Partition, DestType:=0)
    End Function
    Public Shared Function Locate(handle As IntPtr, BlockAddress As UInt64, Partition As Byte, ByVal DestType As LocateDestType, Optional ByRef sensereturn As Byte() = Nothing, Optional ByVal timeout As Integer = 1800) As UInt16
        SyncLock SCSILockManager.GetLock(handle)
            Dim sense(63) As Byte
            Select Case DriverTypeSetting

                Case DriverType.M2488

                Case DriverType.SLR3
                    Select Case DestType
                        Case LocateDestType.Block
                            SCSIReadParam(handle:=handle, cdbData:=New Byte() {&H2B, 4, 0, CByte(BlockAddress >> 24 And &HFFUL), CByte(BlockAddress >> 16 And &HFFUL), CByte(BlockAddress >> 8 And &HFFUL), CByte(BlockAddress And &HFFUL),
                                                                 0, 0, 0}, paramLen:=0, senseReport:=Function(senseData As Byte()) As Boolean
                                                                                                         sense = senseData
                                                                                                         Return True
                                                                                                     End Function, timeout:=timeout)
                        Case LocateDestType.FileMark
                            Locate(handle, 0, 0)
                            Space6(handle:=handle, Count:=CInt(BlockAddress), Code:=LocateDestType.FileMark)
                        Case LocateDestType.EOD
                            If Not ReadPosition(handle).EOD Then
                                Dim commandResultSlr3 As Boolean = SendSCSICommand(handle:=handle, cdbData:=New Byte() {&H11, 3, 0, 0, 0, 0}, DataIn:=1, senseReport:=Function(senseData As Byte()) As Boolean
                                                                                                                                                         sense = senseData
                                                                                                                                                         Return True
                                                                                                                                                     End Function, TimeOut:=timeout)
                                ThrowIfSCSIFailed(commandResultSlr3, "SCSI locate to end of data", sense)
                            End If
                    End Select
                Case DriverType.SLR1
                    Select Case DestType
                        Case LocateDestType.Block
                            SCSIReadParam(handle:=handle, cdbData:=New Byte() {&HC, 0, CByte(BlockAddress >> 16 And &HFUL), CByte(BlockAddress >> 8 And &HFFUL), CByte(BlockAddress And &HFFUL),
                                                            0}, paramLen:=0, senseReport:=Function(senseData As Byte()) As Boolean
                                                                                              sense = senseData
                                                                                              Return True
                                                                                          End Function, timeout:=timeout)
                        Case LocateDestType.FileMark
                            Locate(handle, 0, 0)
                            Space6(handle:=handle, Count:=CInt(BlockAddress), Code:=LocateDestType.FileMark)
                        Case LocateDestType.EOD
                            If Not ReadPosition(handle).EOD Then
                                Dim commandResultSlr1 As Boolean = SendSCSICommand(handle:=handle, cdbData:=New Byte() {&H11, 3, 0, 0, 0, 0}, DataIn:=1, senseReport:=Function(senseData As Byte()) As Boolean
                                                                                                                                                         sense = senseData
                                                                                                                                                         Return True
                                                                                                                                                     End Function, TimeOut:=timeout)
                                ThrowIfSCSIFailed(commandResultSlr1, "SCSI locate to end of data", sense)
                            End If
                    End Select
                Case DriverType.TapeStream
                    Dim ts As TapeImage = Nothing
                    TapeStreamMapping.MappingTable.TryGetValue(handle, ts)
                    If ts Is Nothing Then

                    Else
                        Select Case DestType
                            Case LocateDestType.Block
                                If ts.Position.PartitionNumber <> Partition Then ts.ChangePartition(Partition, sense)
                                ts.LocateByBlock(CLng(BlockAddress), sense)
                            Case LocateDestType.FileMark
                                If ts.Position.PartitionNumber <> Partition Then ts.ChangePartition(Partition, sense)
                                ts.LocateByFilemark(CLng(BlockAddress), sense)
                            Case LocateDestType.EOD
                                If ts.Position.PartitionNumber <> Partition Then ts.ChangePartition(Partition, sense)
                                ts.LocateToEOD(sense)
                        End Select
                    End If
                Case Else
                    If AllowPartition OrElse DestType <> 0 Then
                        Dim CP As Byte = 0
                        If ReadPosition(handle).PartitionNumber <> Partition Then CP = 1
                        SCSIReadParam(handle:=handle, cdbData:=New Byte() {&H92, CByte((DestType << 3) Or (CP << 1)), 0, Partition,
                                                        CByte(BlockAddress >> 56 And &HFFUL), CByte(BlockAddress >> 48 And &HFFUL), CByte(BlockAddress >> 40 And &HFFUL), CByte(BlockAddress >> 32 And &HFFUL),
                                                        CByte(BlockAddress >> 24 And &HFFUL), CByte(BlockAddress >> 16 And &HFFUL), CByte(BlockAddress >> 8 And &HFFUL), CByte(BlockAddress And &HFFUL),
                                                        0, 0, 0, 0}, paramLen:=0, senseReport:=Function(senseData As Byte()) As Boolean
                                                                                                   sense = senseData
                                                                                                   Return True
                                                                                               End Function, timeout:=timeout)
                    Else
                        SCSIReadParam(handle:=handle, cdbData:=New Byte() {&H2B, 0, 0, CByte(BlockAddress >> 24 And &HFFUL), CByte(BlockAddress >> 16 And &HFFUL), CByte(BlockAddress >> 8 And &HFFUL), CByte(BlockAddress And &HFFUL),
                                                        0, 0, 0}, paramLen:=0, senseReport:=Function(senseData As Byte()) As Boolean
                                                                                                sense = senseData
                                                                                                Return True
                                                                                            End Function, timeout:=timeout)
                    End If
            End Select


            Dim Add_Code As UInt16 = CUShort((CInt(sense(12)) << 8) Or sense(13))
            If Add_Code <> 0 AndAlso ((sense(2) And &HF) <> 8) Then
                If DestType = LocateDestType.EOD Then
                    If Not ReadPosition(handle).EOD Then
                        Dim commandResultFallback As Boolean = SendSCSICommand(handle:=handle, cdbData:=New Byte() {&H11, 3, 0, 0, 0, 0}, DataIn:=1, senseReport:=Function(senseData As Byte()) As Boolean
                                                                                                                                                     sense = senseData
                                                                                                                                                     Return True
                                                                                                                                                 End Function)
                        ThrowIfSCSIFailed(commandResultFallback, "SCSI locate to end of data", sense)
                    End If
                ElseIf DestType = LocateDestType.FileMark Then
                    Locate(handle, 0, 0)
                    Space6(handle:=handle, Count:=CInt(BlockAddress), Code:=LocateDestType.FileMark)
                Else

                    SCSIReadParam(handle:=handle, cdbData:=New Byte() {&H92, CByte(DestType << 3), 0, 0,
                                            CByte(BlockAddress >> 56 And &HFFUL), CByte(BlockAddress >> 48 And &HFFUL), CByte(BlockAddress >> 40 And &HFFUL), CByte(BlockAddress >> 32 And &HFFUL),
                                            CByte(BlockAddress >> 24 And &HFFUL), CByte(BlockAddress >> 16 And &HFFUL), CByte(BlockAddress >> 8 And &HFFUL), CByte(BlockAddress And &HFFUL),
                                            0, 0, 0, 0}, paramLen:=64, senseReport:=Function(senseData As Byte()) As Boolean
                                                                                        sense = senseData
                                                                                        Return True
                                                                                    End Function)

                End If
                Add_Code = CUShort(CInt(sense(12)) << 8 Or sense(13))
            Else
                Add_Code = 0
            End If
            sensereturn = sense
            Return Add_Code
        End SyncLock
    End Function
    Public Shared Function Locate(TapeDrive As String, BlockAddress As UInt64, Partition As Byte) As UInt16
        Return Locate(TapeDrive:=TapeDrive, BlockAddress:=BlockAddress, Partition:=Partition, DestType:=0)
    End Function
    Public Shared Function Locate(TapeDrive As String, BlockAddress As Int64, Partition As Byte, ByVal DestType As LocateDestType) As UInt16
        Return Locate(TapeDrive, CULng(BlockAddress), Partition, DestType)
    End Function
    Public Shared Function Locate(TapeDrive As String, BlockAddress As ULong, Partition As ltfsindex.PartitionLabel, DestType As LocateDestType) As UInt16
        Return Locate(TapeDrive:=TapeDrive, BlockAddress:=BlockAddress, Partition:=CByte(Partition), DestType:=DestType)
    End Function
    Public Shared Function Locate(TapeDrive As String, BlockAddress As UInt64, Partition As Byte, ByVal DestType As LocateDestType, Optional ByRef sensereturn As Byte() = Nothing, Optional ByVal timeout As Integer = 1800) As UInt16
        SyncLock SCSILockManager.GetLock(TapeDrive)
            Dim handle As IntPtr
            If Not OpenTapeDrive(TapeDrive, handle) Then Throw New Exception($"Cannot open {TapeDrive}")
            Dim result As UInt16
            Try
                result = Locate(handle, BlockAddress, Partition, DestType, sensereturn, timeout)
            Catch
                Try
                    CloseTapeDrive(handle)
                Catch
                End Try
                Throw
            End Try
            If Not CloseTapeDrive(handle) Then Throw New Exception($"Cannot close {TapeDrive}")
            Return result
        End SyncLock
    End Function
    Public Shared Function Space6(handle As IntPtr, Count As Integer) As UInt16
        Return Space6(handle:=handle, Count:=Count, Code:=0)
    End Function
    Public Shared Function Space6(handle As IntPtr, Count As Integer, Code As LocateDestType) As UInt16
        Dim sense(63) As Byte
        SCSIReadParam(handle:=handle, cdbData:=New Byte() {&H11, CByte(Code), CByte((Count >> 16) And &HFF), CByte((Count >> 8) And &HFF), CByte(Count And &HFF),
                                            0}, paramLen:=64, senseReport:=Function(senseData As Byte()) As Boolean
                                                                               sense = senseData
                                                                               Return True
                                                                           End Function)
        Dim Add_Code As UInt16 = CUShort((CInt(sense(12)) << 8) Or sense(13))
        Return Add_Code
    End Function
    Public Shared Function Space6(TapeDrive As String, Count As Integer) As UInt16
        Return Space6(TapeDrive:=TapeDrive, Count:=Count, Code:=0)
    End Function
    Public Shared Function Space6(TapeDrive As String, Count As Integer, ByVal Code As LocateDestType) As UInt16
        Return RunOpenedTapeOperation(TapeDrive,
                                      Function(handle As IntPtr) As UInt16
                                          Return Space6(handle, Count, Code)
                                      End Function)
    End Function
    Public Shared Function SCSIReadParam(handle As IntPtr, cdbData As Byte(), paramLen As Integer) As Byte()
        Return SCSIReadParam(handle, cdbData, paramLen, Nothing)
    End Function
    Public Shared Function SCSIReadParam(handle As IntPtr, cdbData As Byte(), paramLen As Integer, ByVal senseReport As Func(Of Byte(), Boolean), Optional ByVal timeout As Integer = 60000, Optional cancellationToken As CancellationToken = Nothing) As Byte()
        Dim paramData(paramLen - 1) As Byte
        Dim dataBuffer As IntPtr = Marshal.AllocHGlobal(paramLen)
        Try
            Marshal.Copy(paramData, 0, dataBuffer, paramLen)
            Dim senseData(63) As Byte
            Dim succeeded As Boolean = TapeSCSIIOCtlUnmanaged(handle,
                                                               cdbData,
                                                               dataBuffer,
                                                               CUInt(paramLen),
                                                               1,
                                                               CUInt(timeout),
                                                               senseData,
                                                               cancellationToken)
            Marshal.Copy(dataBuffer, paramData, 0, paramLen)
            If senseReport IsNot Nothing Then senseReport(senseData)
            If Not succeeded Then
                Throw New SCSIFailureException("SCSI parameter read", LastWin32Error, senseData)
            End If
            Return paramData
        Finally
            Marshal.FreeHGlobal(dataBuffer)
        End Try
    End Function
    Public Shared Function SCSIReadParam(TapeDrive As String, cdbData As Byte(), paramLen As Integer) As Byte()
        Return SCSIReadParam(TapeDrive, cdbData, paramLen, Nothing)
    End Function
    Public Shared Function SCSIReadParam(TapeDrive As String, cdbData As Byte(), paramLen As Integer, ByVal senseReport As Func(Of Byte(), Boolean), Optional cancellationToken As CancellationToken = Nothing) As Byte()
        SyncLock SCSILockManager.GetLock(TapeDrive)
            Dim handle As IntPtr
            If Not OpenTapeDrive(TapeDrive, handle) Then Throw New Exception($"Cannot open {TapeDrive}")
            Dim result As Byte()
            Try
                result = SCSIReadParam(handle, cdbData, paramLen, senseReport, cancellationToken:=cancellationToken)
            Catch
                Try
                    CloseTapeDrive(handle)
                Catch
                End Try
                Throw
            End Try
            If Not CloseTapeDrive(handle) Then Throw New Exception($"Cannot close {TapeDrive}")
            Return result
        End SyncLock
    End Function
    Public Shared Function SCSIReadParamUnmanaged(handle As IntPtr, cdbData As Byte(), paramLen As Integer) As IntPtr
        Return SCSIReadParamUnmanaged(handle, cdbData, paramLen, Nothing)
    End Function
    Public Shared Function SCSIReadParamUnmanaged(handle As IntPtr, cdbData As Byte(), paramLen As Integer, ByVal senseReport As Func(Of Byte(), Boolean), Optional cancellationToken As CancellationToken = Nothing) As IntPtr
        Dim paramData(paramLen - 1) As Byte
        Dim dataBuffer As IntPtr = Marshal.AllocHGlobal(paramLen)
        Try
            Marshal.Copy(paramData, 0, dataBuffer, paramLen)
            Dim senseData(63) As Byte
            While Not TapeSCSIIOCtlUnmanaged(handle,
                                             cdbData,
                                             dataBuffer,
                                             CUInt(paramLen),
                                             1,
                                             60000,
                                             senseData,
                                             cancellationToken)
                Dim ErrCode As Integer = LastWin32Error
                Dim win32ex As New Win32Exception(ErrCode)
                Dim ActiveFrm = ApplicationWheels.GetActiveWindow()
                Dim dResult As DialogResult
                ActiveFrm.Invoke(Sub() dResult = MessageBox.Show(ActiveFrm, $"{My.Resources.StrSCSIFail}{vbCrLf}{ParseSenseData(senseData)}{vbCrLf}{vbCrLf}ErrCode: 0x{ErrCode.ToString("X8")}h{vbCrLf}{win32ex.Message}",
                                            My.Resources.ResText_Warning,
                                            MessageBoxButtons.AbortRetryIgnore,
                                            Nothing,
                                            MessageBoxDefaultButton.Button2,
                                            MessageBoxOptions.DefaultDesktopOnly))
                Select Case dResult
                    Case DialogResult.Abort
                        Throw New SCSIFailureException("SCSI parameter read", ErrCode, senseData)
                    Case DialogResult.Retry

                    Case DialogResult.Ignore
                        Exit While
                End Select
            End While
            If senseReport IsNot Nothing Then senseReport(senseData)
            Return dataBuffer
        Catch
            Marshal.FreeHGlobal(dataBuffer)
            Throw
        End Try
    End Function
    Public Shared Function SCSIReadParamUnmanaged(TapeDrive As String, cdbData As Byte(), paramLen As Integer) As IntPtr
        Return SCSIReadParamUnmanaged(TapeDrive, cdbData, paramLen, Nothing)
    End Function
    Public Shared Function SCSIReadParamUnmanaged(TapeDrive As String, cdbData As Byte(), paramLen As Integer, ByVal senseReport As Func(Of Byte(), Boolean), Optional cancellationToken As CancellationToken = Nothing) As IntPtr
        SyncLock SCSILockManager.GetLock(TapeDrive)
            Dim handle As IntPtr
            If Not OpenTapeDrive(TapeDrive, handle) Then Throw New Exception($"Cannot open {TapeDrive}")
            Dim result As IntPtr
            Try
                result = SCSIReadParamUnmanaged(handle, cdbData, paramLen, senseReport, cancellationToken:=cancellationToken)
            Catch
                Try
                    CloseTapeDrive(handle)
                Catch
                End Try
                Throw
            End Try
            If Not CloseTapeDrive(handle) Then Throw New Exception($"Cannot close {TapeDrive}")
            Return result
        End SyncLock
    End Function
    Public Shared Function LogSense(TapeDrive As String, PageCode As Byte, SubPageCode As Byte, Optional ByVal senseReport As Func(Of Byte(), Boolean) = Nothing, Optional PageControl As Byte = &H1) As Byte()
        Return RunOpenedTapeOperation(TapeDrive,
                                      Function(handle As IntPtr) As Byte()
                                          Return LogSense(handle, PageCode, SubPageCode, senseReport, PageControl)
                                      End Function)
    End Function
    Public Shared Function LogSense(handle As IntPtr, PageCode As Byte, SubPageCode As Byte, Optional ByVal senseReport As Func(Of Byte(), Boolean) = Nothing, Optional PageControl As Byte = &H1) As Byte()
        Select Case DriverTypeSetting
            Case DriverType.TapeStream
                Dim ts As TapeImage = Nothing
                TapeStreamMapping.MappingTable.TryGetValue(handle, ts)
                Dim sense(63) As Byte
                If ts IsNot Nothing Then
                    Dim Header(3) As Byte
                    Dim headerResult As Boolean = ts.HandleSCSICommand({&H4D, 0, PageControl << 6 Or PageCode, SubPageCode, 0, 0, 0, 0, 4, 0}, {}, 1, 4, Header, sense)
                    If Not headerResult Then Throw New SCSIFailureException("SCSI log sense header", 31, sense)
                    If Header.Length < 4 Then Return {0, 0, 0, 0}
                    Dim PageLen As Integer = Header(2)
                    PageLen <<= 8
                    PageLen = PageLen Or Header(3)
                    Dim Result(PageLen + 4 - 1) As Byte
                    Dim dataResult As Boolean = ts.HandleSCSICommand({&H4D, 0, PageControl << 6 Or PageCode, SubPageCode, 0, 0, 0, CByte(((PageLen + 4) >> 8) And &HFF), CByte((PageLen + 4) And &HFF), 0}, {}, 1, PageLen + 4, Result, sense)
                    If Not dataResult Then Throw New SCSIFailureException("SCSI log sense data", 31, sense)
                    If senseReport IsNot Nothing Then
                        senseReport(sense)
                    End If
                    Return Result
                Else
                    Return Nothing
                End If
            Case Else
                SyncLock SCSILockManager.GetLock(handle)
                    Dim Header As Byte() = SCSIReadParam(handle, {&H4D, 0, (PageControl << 6) Or PageCode, SubPageCode, 0, 0, 0, 0, 4, 0}, 4)
                    If Header.Length < 4 Then Return {0, 0, 0, 0}
                    Dim PageLen As Integer = Header(2)
                    PageLen <<= 8
                    PageLen = PageLen Or Header(3)
                    Return SCSIReadParam(handle:=handle, cdbData:=New Byte() {&H4D, 0, CByte((PageControl << 6) Or PageCode), SubPageCode, 0, 0, 0, CByte(((PageLen + 4) >> 8) And &HFF), CByte((PageLen + 4) And &HFF), 0}, paramLen:=PageLen + 4, senseReport:=senseReport)
                End SyncLock
        End Select

    End Function
    Public Shared Function ModeSense(handle As IntPtr, PageID As Byte, Optional ByVal senseReport As Func(Of Byte(), Boolean) = Nothing, Optional ByVal SkipHeader As Boolean = True) As Byte()
        Select Case DriverTypeSetting
            Case DriverType.TapeStream
                Dim ts As TapeImage = Nothing
                TapeStreamMapping.MappingTable.TryGetValue(handle, ts)
                Dim sense(63) As Byte
                If ts IsNot Nothing Then
                    Dim Header(3) As Byte
                    Dim headerResult As Boolean = ts.HandleSCSICommand({&H1A, 0, PageID, 0, 4, 0}, {}, 1, 4, Header, sense)
                    If Not headerResult Then Throw New SCSIFailureException("SCSI mode sense header", 31, sense)
                    If Header.Length < 4 Then Return {0, 0, 0, 0}
                    Dim PageLen As Byte = Header(0)
                    If PageLen = 0 Then Return {0, 0, 0, 0}
                    Dim DescriptorLen As Byte = Header(3)
                    Dim Result(PageLen + 1 - 1) As Byte
                    Dim dataResult As Boolean = ts.HandleSCSICommand({&H1A, 0, PageID, 0, CByte(PageLen + 1), 0}, {}, 1, PageLen + 1, Result, sense)
                    If Not dataResult Then Throw New SCSIFailureException("SCSI mode sense data", 31, sense)
                    If senseReport IsNot Nothing Then
                        senseReport(sense)
                    End If
                    If SkipHeader Then
                        Return Result.Skip(4 + DescriptorLen).ToArray()
                    Else
                        Return Result
                    End If
                End If
            Case Else
                SyncLock SCSILockManager.GetLock(handle)
                    Dim Header As Byte() = SCSIReadParam(handle, {&H1A, 0, PageID, 0, 4, 0}, 4)
                    If Header.Length = 0 Then Return {0, 0, 0, 0}
                    Dim PageLen As Byte = Header(0)
                    If PageLen = 0 Then Return {0, 0, 0, 0}
                    Dim DescriptorLen As Byte = Header(3)
                    If SkipHeader Then
                        Return SCSIReadParam(handle:=handle, cdbData:=New Byte() {&H1A, 0, PageID, 0, CByte(PageLen + 1), 0}, paramLen:=PageLen + 1, senseReport:=senseReport).Skip(4 + DescriptorLen).ToArray()
                    Else
                        Return SCSIReadParam(handle:=handle, cdbData:=New Byte() {&H1A, 0, PageID, 0, CByte(PageLen + 1), 0}, paramLen:=PageLen + 1, senseReport:=senseReport).ToArray()
                    End If
                End SyncLock
        End Select
        Return Array.Empty(Of Byte)()
    End Function
    Public Shared Function ModeSense(TapeDrive As String, PageID As Byte, Optional ByVal senseReport As Func(Of Byte(), Boolean) = Nothing, Optional ByVal SkipHeader As Boolean = True) As Byte()
        Return RunOpenedTapeOperation(TapeDrive,
                                      Function(handle As IntPtr) As Byte()
                                          Return ModeSense(handle, PageID, senseReport, SkipHeader)
                                      End Function)
    End Function
    Public Shared Function ModeSelect(handle As IntPtr, PageData As Byte(), Optional ByVal senseReport As Func(Of Byte(), Boolean) = Nothing) As Byte()
        Dim data As Byte() = (New Byte() {0, 0, &H10, 0}).Concat(PageData).ToArray()
        Dim sense(63) As Byte
        If data.Length < 256 Then
            Dim commandResultShort As Boolean = SendSCSICommand(handle:=handle, cdbData:=New Byte() {&H15, &H10, 0, 0, CByte(data.Length), 0}, Data:=data, DataIn:=0,
                                                                senseReport:=Function(senseData As Byte()) As Boolean
                                                                                 sense = senseData
                                                                                 Return True
                                                                             End Function)
            ThrowIfSCSIFailed(commandResultShort, "SCSI mode select", sense)
        Else
            data = (New Byte() {0, 0, 0, &H10, 0, 0, 0, 0}).Concat(PageData).ToArray()
            Dim commandResultLong As Boolean = SendSCSICommand(handle:=handle, cdbData:=New Byte() {&H55, &H10, 0, 0, 0, 0, 0, CByte((data.Length >> 8) And &HFF), CByte(data.Length And &HFF), 0}, Data:=data, DataIn:=0,
                                                               senseReport:=Function(senseData As Byte()) As Boolean
                                                                                sense = senseData
                                                                                Return True
                                                                            End Function)
            ThrowIfSCSIFailed(commandResultLong, "SCSI mode select", sense)
        End If
        Return sense
    End Function
    Public Shared Function ModeSelect(TapeDrive As String, PageData As Byte(), Optional ByVal senseReport As Func(Of Byte(), Boolean) = Nothing) As Byte()
        Return RunOpenedTapeOperation(TapeDrive,
                                      Function(handle As IntPtr) As Byte()
                                          Return ModeSelect(handle, PageData, senseReport)
                                      End Function)
    End Function
    Public Shared Function ReadDensityCode(handle As IntPtr) As Byte
        Return SCSIReadParam(handle, {&H1A, 0, 0, 0, &HC, 0}, 12)(4)
    End Function
    Public Shared Function ReadDensityCode(TapeDrive As String) As Byte
        Return RunOpenedTapeOperation(TapeDrive,
                                      Function(handle As IntPtr) As Byte
                                          Return ReadDensityCode(handle)
                                      End Function)
    End Function
    Public Shared Function SetCapacity(handle As IntPtr, Optional ByVal Capacity As UInt16 = &HFFFF, Optional ByVal senseReport As Func(Of Byte(), Boolean) = Nothing) As Boolean
        Select Case DriverTypeSetting
            Case DriverType.TapeStream
                Return True
            Case DriverType.IBM3592
                Dim mode23 As Byte() = ModeSense(handle, &H23)
                If mode23 Is Nothing OrElse mode23.Length < 12 Then Return False
                mode23(11) = 1
                If Capacity >= &HFE80 Then
                    mode23(12) = 0
                Else
                    mode23(12) = CByte((Capacity >> 8) And &HFF)
                End If
                Dim modeSelectResult As Byte() = ModeSelect(handle, mode23, senseReport)
                If modeSelectResult Is Nothing OrElse modeSelectResult.Length = 0 Then Return False
                Return True
            Case Else
                Return SendSCSICommand(handle:=handle, cdbData:=New Byte() {&HB, 0, 0, CByte((Capacity >> 8) And &HFF), CByte(Capacity And &HFF), 0}, senseReport:=senseReport)
        End Select
    End Function
    Public Shared Function SetBarcode(handle As IntPtr, barcode As String, Optional ByVal senseReport As Func(Of Byte(), Boolean) = Nothing) As Boolean
        Dim cdb As Byte() = {&H8D, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, &H29, 0, 0}
        Dim data As Byte() = {0, 0, 0, &H25, &H8, &H6, &H1, 0, &H20, &H20, &H20, &H20, &H20, &H20, &H20, &H20,
                    &H20, &H20, &H20, &H20, &H20, &H20, &H20, &H20, &H20, &H20, &H20, &H20, &H20, &H20, &H20, &H20, &H20, &H20, &H20, &H20, &H20, &H20, &H20, &H20, &H20}
        barcode = barcode.PadRight(32).Substring(0, 32)
        For i As Integer = 0 To barcode.Length - 1
            data(9 + i) = CByte(Asc(barcode(i)) And &HFF)
        Next
        Select Case DriverTypeSetting
            Case DriverType.TapeStream
                Dim ts As TapeImage = Nothing
                TapeStreamMapping.MappingTable.TryGetValue(handle, ts)
                If ts IsNot Nothing Then
                    Dim sense() As Byte = {}
                    Dim commandResult As Boolean = ts.HandleSCSICommand(cdb, data, 0, data.Length, Nothing, sense)
                    If senseReport IsNot Nothing Then senseReport(sense)
                    Return commandResult
                Else
                    Return False
                End If
            Case Else
                Return SendSCSICommand(handle, cdb, data, 0, senseReport)
        End Select
    End Function
    Public Shared Function SetBarcode(TapeDrive As String, barcode As String, Optional ByVal senseReport As Func(Of Byte(), Boolean) = Nothing) As Boolean
        Return RunOpenedTapeOperation(TapeDrive,
                                      Function(handle As IntPtr) As Boolean
                                          Return SetBarcode(handle, barcode, senseReport)
                                      End Function)
    End Function
    Public Shared Function SetBlockSize(handle As IntPtr) As Byte()
        Return SetBlockSize(handle, &H80000)
    End Function
    Public Shared Function SetBlockSize(handle As IntPtr, BlockSize As Int64) As Byte()
        Return SetBlockSize(handle, CULng(BlockSize))
    End Function
    Public Shared Function SetBlockSize(handle As IntPtr, ByVal BlockSize As UInt64) As Byte()
        Select Case DriverTypeSetting
            Case DriverType.TapeStream
                Return TapeImage.SenseData.NoSense
            Case Else
                Dim sense(63) As Byte
                SyncLock SCSILockManager.GetLock(handle)
                    Dim DensityCode As Byte = ReadDensityCode(handle:=handle)
                    BlockSize = CULng(Math.Min(BlockSize, GlobalBlockLimit))
                    Dim commandResult As Boolean = SendSCSICommand(handle:=handle, cdbData:=New Byte() {&H15, &H10, 0, 0, &HC, 0},
                                                                    Data:=New Byte() {0, 0, &H10, 8, DensityCode, 0, 0, 0, 0, CByte(BlockSize >> 16 And &HFFUL), CByte(BlockSize >> 8 And &HFFUL), CByte(BlockSize And &HFFUL)}, DataIn:=0,
                                                                   senseReport:=Function(senseData As Byte()) As Boolean
                                                                                    sense = senseData
                                                                                    Return True
                                                                                End Function)
                    ThrowIfSCSIFailed(commandResult, "SCSI set block size", sense)
                End SyncLock
                Return sense
        End Select
    End Function
    Public Shared Function SetBlockSize(TapeDrive As String) As Byte()
        Return SetBlockSize(TapeDrive, &H80000)
    End Function
    Public Shared Function SetBlockSize(TapeDrive As String, BlockSize As Integer) As Byte()
        Return SetBlockSize(TapeDrive, CUInt(BlockSize))
    End Function
    Public Shared Function SetBlockSize(TapeDrive As String, ByVal BlockSize As UInteger) As Byte()
        Return RunOpenedTapeOperation(TapeDrive,
                                      Function(handle As IntPtr) As Byte()
                                          Return SetBlockSize(handle, BlockSize)
                                      End Function)
    End Function
    Public Enum AttributeFormat
        Binary = &H0
        Ascii = &H1
        Text = &H2
        Reserved = &H3
    End Enum
    Public Shared Function SetMAMAttribute(handle As IntPtr, PageID As UInt16, Data As Byte(), Optional ByVal Format As AttributeFormat = AttributeFormat.Binary, Optional ByVal PartitionNumber As Byte = 0, Optional ByVal SenseReport As Func(Of Byte(), Boolean) = Nothing) As Boolean
        Dim Param_LEN As UInt64 = CULng(Data.Length + 9)
        Dim cdb As Byte() = {&H8D, 0, 0, 0, 0, 0, 0, PartitionNumber, 0, 0,
                                     CByte(CLng((Param_LEN >> 24)) And &HFF), CByte(CLng((Param_LEN >> 16)) And &HFF), CByte(CLng((Param_LEN >> 8)) And &HFF), CByte(CLng(Param_LEN) And &HFF), 0, 0}
        Dim param As Byte() = {CByte(CLng((Param_LEN >> 24)) And &HFF), CByte(CLng((Param_LEN >> 16)) And &HFF), CByte(CLng((Param_LEN >> 8)) And &HFF), CByte(CLng(Param_LEN) And &HFF),
                                       CByte((PageID >> 8) And &HFF), CByte(PageID And &HFF), CByte(Format),
                                       CByte((Data.Length >> 8) And &HFF), CByte(Data.Length And &HFF)}
        param = param.Concat(Data).ToArray()
        Select Case DriverTypeSetting
            Case DriverType.TapeStream
                Dim ts As TapeImage = Nothing
                TapeStreamMapping.MappingTable.TryGetValue(handle, ts)
                If ts IsNot Nothing Then
                    Dim sense As Byte() = {}
                    Dim commandResult As Boolean = ts.HandleSCSICommand(cdb, param, 0, param.Length, {}, sense)
                    If SenseReport IsNot Nothing Then SenseReport(sense)
                    Return commandResult
                Else
                    Return False
                End If
            Case Else
                Return SendSCSICommand(handle, cdb, param, 0, SenseReport)
        End Select
    End Function
    Public Shared Function SetMAMAttribute(TapeDrive As String, PageID As UInt16, Data As Byte(), Optional ByVal Format As AttributeFormat = AttributeFormat.Binary, Optional ByVal PartitionNumber As Byte = 0, Optional ByVal SenseReport As Func(Of Byte(), Boolean) = Nothing) As Boolean
        Return RunOpenedTapeOperation(TapeDrive,
                                      Function(handle As IntPtr) As Boolean
                                          Return SetMAMAttribute(handle, PageID, Data, Format, PartitionNumber, SenseReport)
                                      End Function)
    End Function
    Public Shared Function SetMAMAttribute(handle As IntPtr, PageID As UInt16, Data As String, Optional ByVal Format As AttributeFormat = AttributeFormat.Ascii, Optional ByVal PartitionNumber As Byte = 0, Optional ByVal SenseReport As Func(Of Byte(), Boolean) = Nothing) As Boolean
        Return SetMAMAttribute(handle, PageID, Encoding.UTF8.GetBytes(Data), Format, PartitionNumber, SenseReport)
    End Function
    Public Shared Function SetMAMAttribute(TapeDrive As String, PageID As UInt16, Data As String, Optional ByVal Format As AttributeFormat = AttributeFormat.Ascii, Optional ByVal PartitionNumber As Byte = 0, Optional ByVal SenseReport As Func(Of Byte(), Boolean) = Nothing) As Boolean
        Return SetMAMAttribute(TapeDrive, PageID, Encoding.UTF8.GetBytes(Data), Format, PartitionNumber, SenseReport)
    End Function
    Public Shared Function WriteVCI(TapeDrive As String, Generation As UInt64, block0 As UInt64, block1 As UInt64,
                                    UUID As String, Optional ByVal SenseReport As Func(Of Byte(), Boolean) = Nothing) As Boolean
        Return WriteVCI(TapeDrive, Generation, block0, block1, UUID, 1, SenseReport)
    End Function
    Public Shared Function WriteVCI(TapeDrive As String, Generation As UInt64, block0 As UInt64, block1 As UInt64,
                                    UUID As String, ByVal ExtraPartitionCount As Byte, Optional ByVal SenseReport As Func(Of Byte(), Boolean) = Nothing) As Boolean
        Return RunOpenedTapeOperation(TapeDrive,
                                      Function(handle As IntPtr) As Boolean
                                          Return WriteVCI(handle:=handle, Generation:=Generation, block0:=block0, block1:=block1, UUID:=UUID, ExtraPartitionCount:=ExtraPartitionCount, SenseReport:=SenseReport)
                                      End Function)
    End Function
    Public Shared Function WriteVCI(handle As IntPtr, Generation As UInt64, block0 As UInt64, block1 As UInt64,
                                    UUID As String, Optional ByVal SenseReport As Func(Of Byte(), Boolean) = Nothing) As Boolean
        Return WriteVCI(handle, Generation, block0, block1, UUID, 1, SenseReport)
    End Function
    Public Shared Function WriteVCI(handle As IntPtr, Generation As UInt64, block0 As UInt64, block1 As UInt64,
                                    UUID As String, ByVal ExtraPartitionCount As Byte, Optional ByVal SenseReport As Func(Of Byte(), Boolean) = Nothing) As Boolean
        SyncLock SCSILockManager.GetLock(handle)
            Flush(handle)
            Dim VCIData As Byte()
            Dim VCI As Byte() = GetMAMAttributeBytes(handle, 0, 9)
            If VCI Is Nothing OrElse VCI.Length = 0 Then Return False
            If ExtraPartitionCount > 0 Then
                VCIData = {8, 0, 0, 0, 0, VCI(VCI.Length - 4), VCI(VCI.Length - 3), VCI(VCI.Length - 2), VCI(VCI.Length - 1),
                CByte(CLng(Generation >> 56) And &HFF), CByte(CLng(Generation >> 48) And &HFF), CByte(CLng(Generation >> 40) And &HFF), CByte(CLng(Generation >> 32) And &HFF),
                CByte(CLng(Generation >> 24) And &HFF), CByte(CLng(Generation >> 16) And &HFF), CByte(CLng(Generation >> 8) And &HFF), CByte(CLng(Generation) And &HFF),
                CByte(CLng(block0 >> 56) And &HFF), CByte(CLng(block0 >> 48) And &HFF), CByte(CLng(block0 >> 40) And &HFF), CByte(CLng(block0 >> 32) And &HFF),
                CByte(CLng(block0 >> 24) And &HFF), CByte(CLng(block0 >> 16) And &HFF), CByte(CLng(block0 >> 8) And &HFF), CByte(CLng(block0) And &HFF),
                0, &H2B, &H4C, &H54, &H46, &H53, 0}
                VCIData = VCIData.Concat(Encoding.ASCII.GetBytes(UUID.PadRight(36).Substring(0, 36))).ToArray
                VCIData = VCIData.Concat({0, 1}).ToArray
                Dim Succ As Boolean = SetMAMAttribute(handle, &H80C, VCIData, AttributeFormat.Binary, 0, SenseReport)
                If Not Succ Then Return False
            End If
            VCIData = {8, 0, 0, 0, 0, VCI(VCI.Length - 4), VCI(VCI.Length - 3), VCI(VCI.Length - 2), VCI(VCI.Length - 1),
                CByte(CLng(Generation >> 56) And &HFF), CByte(CLng(Generation >> 48) And &HFF), CByte(CLng(Generation >> 40) And &HFF), CByte(CLng(Generation >> 32) And &HFF),
                CByte(CLng(Generation >> 24) And &HFF), CByte(CLng(Generation >> 16) And &HFF), CByte(CLng(Generation >> 8) And &HFF), CByte(CLng(Generation) And &HFF),
                CByte(CLng(block1 >> 56) And &HFF), CByte(CLng(block1 >> 48) And &HFF), CByte(CLng(block1 >> 40) And &HFF), CByte(CLng(block1 >> 32) And &HFF),
                CByte(CLng(block1 >> 24) And &HFF), CByte(CLng(block1 >> 16) And &HFF), CByte(CLng(block1 >> 8) And &HFF), CByte(CLng(block1) And &HFF),
                0, &H2B, &H4C, &H54, &H46, &H53, 0}
            VCIData = VCIData.Concat(Encoding.ASCII.GetBytes(UUID.PadRight(36).Substring(0, 36))).ToArray()
            VCIData = VCIData.Concat({0, 1}).ToArray()
            Return SetMAMAttribute(handle, &H80C, VCIData, AttributeFormat.Binary, ExtraPartitionCount, SenseReport)
        End SyncLock
    End Function
    Public Shared Function ParseAdditionalSenseCode(Add_Code As UInt16) As String
        Dim Msg As New StringBuilder
        Select Case Add_Code
            Case &H0
                Msg.Append("No addition sense")
            Case &H1
                Msg.Append("Filemark detected")
            Case &H2
                Msg.Append("End of Tape detected")
            Case &H4
                Msg.Append("Beginning of Tape detected")
            Case &H5
                Msg.Append("End of Data detected")
            Case &H16
                Msg.Append("Operation in progress")
            Case &H18
                Msg.Append("Erase operation in progress")
            Case &H19
                Msg.Append("Locate operation in progress")
            Case &H1A
                Msg.Append("Rewind operation in progress")
            Case &H400
                Msg.Append("LUN not ready, cause not reportable")
            Case &H401
                Msg.Append("LUN in process of becoming ready")
            Case &H402
                Msg.Append("LUN not ready, Initializing command required")
            Case &H404
                Msg.Append("LUN not ready, format in progress")
            Case &H407
                Msg.Append("Command in progress")
            Case &H409
                Msg.Append("LUN not ready, self-test in progress")
            Case &H40C
                Msg.Append("LUN not accessible, port in unavailable state")
            Case &H412
                Msg.Append("Logical unit offline")
            Case &H800
                Msg.Append("Logical unit communication failure")
            Case &HB00
                Msg.Append("Warning")
            Case &HB01
                Msg.Append("Thermal limit exceeded")
            Case &HC00
                Msg.Append("Write error")
            Case &HE01
                Msg.Append("Information unit too short")
            Case &HE02
                Msg.Append("Information unit too long")
            Case &HE03
                Msg.Append("SK Illegal Request")
            Case &H1001
                Msg.Append("Logical block guard check failed")
            Case &H1100
                Msg.Append("Unrecovered read error")
            Case &H1112
                Msg.Append("Media Auxiliary Memory read error")
            Case &H1400
                Msg.Append("Recorded entity not found")
            Case &H1403
                Msg.Append("End of Data not found")
            Case &H1A00
                Msg.Append("Parameter list length error")
            Case &H2000
                Msg.Append("Invalid command operation code")
            Case &H2400
                Msg.Append("Invalid field in Command Descriptor Block")
            Case &H2500
                Msg.Append("LUN not supported")
            Case &H2600
                Msg.Append("Invalid field in parameter list")
            Case &H2601
                Msg.Append("Parameter not supported")
            Case &H2602
                Msg.Append("Parameter value invalid")
            Case &H2604
                Msg.Append("Invalid release of persistent reservation")
            Case &H2610
                Msg.Append("Data decryption key fail limit reached")
            Case &H2680
                Msg.Append("Invalid CA certificate")
            Case &H2700
                Msg.Append("Write-protected")
            Case &H2708
                Msg.Append("Too many logical objects on partition to support operation")
            Case &H2800
                Msg.Append("Not ready to ready transition, medium may have changed")
            Case &H2901
                Msg.Append("Power-on reset")
            Case &H2902
                Msg.Append("SCSI bus reset")
            Case &H2903
                Msg.Append("Bus device reset")
            Case &H2904
                Msg.Append("Internal firmware reboot")
            Case &H2907
                Msg.Append("I_T nexus loss occurred")
            Case &H2A01
                Msg.Append("Mode parameters changed")
            Case &H2A02
                Msg.Append("Log parameters changed")
            Case &H2A03
                Msg.Append("Reservations pre-empted")
            Case &H2A04
                Msg.Append("Reservations released")
            Case &H2A05
                Msg.Append("Registrations pre-empted")
            Case &H2A06
                Msg.Append("Asymmetric access state changed")
            Case &H2A07
                Msg.Append("Asymmetric access state transition failed")
            Case &H2A08
                Msg.Append("Priority changed")
            Case &H2A0D
                Msg.Append("Data encryption capabilities changed")
            Case &H2A10
                Msg.Append("Timestamp changed")
            Case &H2A11
                Msg.Append("Data encryption parameters changed by another initiator")
            Case &H2A12
                Msg.Append("Data encryption parameters changed by a vendor-specific event")
            Case &H2A13
                Msg.Append("Data Encryption Key Instance Counter has changed")
            Case &H2A14
                Msg.Append("SA creation capabilities data has changed")
            Case &H2A15
                Msg.Append("Medium removal prevention pre-empted")
            Case &H2A80
                Msg.Append("Security configuration changed")
            Case &H2C00
                Msg.Append("Command sequence invalid")
            Case &H2C07
                Msg.Append("Previous busy status")
            Case &H2C08
                Msg.Append("Previous task set full status")
            Case &H2C09
                Msg.Append("Previous reservation conflict status")
            Case &H2C0B
                Msg.Append("Not reserved")
            Case &H2F00
                Msg.Append("Commands cleared by another initiator")
            Case &H3000
                Msg.Append("Incompatible medium installed")
            Case &H3001
                Msg.Append("Cannot read media, unknown format")
            Case &H3002
                Msg.Append("Cannot read media: incompatible format")
            Case &H3003
                Msg.Append("Cleaning cartridge installed")
            Case &H3004
                Msg.Append("Cannot write medium")
            Case &H3005
                Msg.Append("Cannot write medium, incompatible format")
            Case &H3006
                Msg.Append("Cannot format, incompatible medium")
            Case &H3007
                Msg.Append("Cleaning failure")
            Case &H300C
                Msg.Append("WORM medium—overwrite attempted")
            Case &H300D
                Msg.Append("WORM medium—integrity check failed")
            Case &H3100
                Msg.Append("Medium format corrupted")
            Case &H3700
                Msg.Append("Rounded parameter")
            Case &H3A00
                Msg.Append("Medium not present")
            Case &H3A04
                Msg.Append("Medium not present, Media Auxiliary Memory accessible")
            Case &H3B00
                Msg.Append("Sequential positioning error")
            Case &H3B0C
                Msg.Append("Position past BOM")
            Case &H3B1C
                Msg.Append("Too many logical objects on partition to support operation.")
            Case &H3E00
                Msg.Append("Logical unit has not self-configured yet")
            Case &H3F01
                Msg.Append("Microcode has been changed")
            Case &H3F03
                Msg.Append("Inquiry data has changed")
            Case &H3F05
                Msg.Append("Device identifier changed")
            Case &H3F0E
                Msg.Append("Reported LUNs data has changed")
            Case &H3F0F
                Msg.Append("Echo buffer overwritten")
            Case &H4300
                Msg.Append("Message error")
            Case &H4400
                Msg.Append("Internal target failure")
            Case &H4500
                Msg.Append("Selection/reselection failure")
            Case &H4700
                Msg.Append("SCSI parity error")
            Case &H4800
                Msg.Append("Initiator Detected Error message received")
            Case &H4900
                Msg.Append("Invalid message")
            Case &H4B00
                Msg.Append("Data phase error")
            Case &H4B02
                Msg.Append("Too much write data")
            Case &H4B03
                Msg.Append("ACK/NAK timeout")
            Case &H4B04
                Msg.Append("NAK received")
            Case &H4B05
                Msg.Append("Data offset error")
            Case &H4B06
                Msg.Append("Initiator response timeout")
            Case &H4D00
                Msg.Append("Tagged overlapped command")
            Case &H4E00
                Msg.Append("Overlapped commands")
            Case &H5000
                Msg.Append("Write append error")
            Case &H5200
                Msg.Append("Cartridge fault")
            Case &H5300
                Msg.Append("Media load or eject failed")
            Case &H5301
                Msg.Append("Unload tape failure")
            Case &H5302
                Msg.Append("Medium removal prevented")
            Case &H5303
                Msg.Append("Insufficient resources")
            Case &H5304
                Msg.Append("Medium thread or unthread failure")
            Case &H5504
                Msg.Append("Insufficient registration resources")
            Case &H5506
                Msg.Append("Media Auxiliary Memory full")
            Case &H5B01
                Msg.Append("Threshold condition met")
            Case &H5D00
                Msg.Append("Failure prediction threshold exceeded")
            Case &H5DFF
                Msg.Append("Failure prediction threshold exceeded (false)")
            Case &H5E01
                Msg.Append("Idle condition activated by timer")
            Case &H7400
                Msg.Append("Security error")
            Case &H7401
                Msg.Append("Unable to decrypt data")
            Case &H7402
                Msg.Append("Unencrypted data encountered while decrypting")
            Case &H7403
                Msg.Append("Incorrect data encryption key")
            Case &H7404
                Msg.Append("Cryptographic integrity validation failed")
            Case &H7405
                Msg.Append("Key-associated data descriptors changed.")
            Case &H7408
                Msg.Append("Digital signature validation failure")
            Case &H7409
                Msg.Append("Encryption mode mismatch on read")
            Case &H740A
                Msg.Append("Encrypted block not RAW read-enabled")
            Case &H740B
                Msg.Append("Incorrect encryption parameters")
            Case &H7421
                Msg.Append("Data encryption configuration prevented")
            Case &H7440
                Msg.Append("Authentication failed")
            Case &H7461
                Msg.Append("External data encryption Key Manager access error")
            Case &H7462
                Msg.Append("External data encryption Key Manager error")
            Case &H7463
                Msg.Append("External data encryption management—key not found")
            Case &H7464
                Msg.Append("External data encryption management—request not authorized")
            Case &H746E
                Msg.Append("External data encryption control time-out")
            Case &H746F
                Msg.Append("External data encryption control unknown error")
            Case &H7471
                Msg.Append("Logical Unit access not authorized")
            Case &H7480
                Msg.Append("KAD changed")
            Case &H7482
                Msg.Append("Crypto KAD in CM failure")
            Case &H8282
                Msg.Append("Drive requires cleaning")
            Case &H8283
                Msg.Append("Bad microcode detected")
            Case Else
                Msg.Append("")
        End Select
        If Add_Code >> 8 = &H40 Then
            Msg.Append("Diagnostic failure on component " & Hex(Add_Code And &HFF) & "h")
        End If
        Return Msg.ToString()
    End Function
    Public Shared Function ParseDriveCode(Drv_Code As UInt16) As String
        Select Case Drv_Code
            Case &H0
                Return " GOOD"
            Case &H1
                Return " BAD"
            Case &H2
                Return " DONE"
            Case &H3
                Return " ABORTED"
            Case &H4
                Return " INVALID_CONFIG_VALUE"
            Case &H5
                Return " INVALID_CONFIG_NAME"
            Case &H6
                Return "UNHANDLED (Place holder until real status in known.)"
            Case &H7
                Return "INVALID_PARAMETER (Invalid parameter supplied to function)"
            Case &H401
                Return "AC_UNSUPPORTED_CMD_OPCODE (A automation controller has requested an unsupported command.)"
            Case &H402
                Return "AC_BUSY_CMD_REJECTED"
            Case &H403
                Return "AC_RAMBIST_FAILED"
            Case &H404
                Return "AC_INVALID_CMD_CHKSUM"
            Case &H405
                Return "AC_INVALID_BAUDRATE"
            Case &H406
                Return "AC_INVALID_CMD_WHILE_LOAD_UNLOAD_PENDING"
            Case &H407
                Return "AC_TIMEOUT_WAITING_TO_END_IMMED_CMD"
            Case &H408
                Return "AC_RAM_FRAMING_ERROR"
            Case &H409
                Return "AC_RAM_OVERRUN_ERROR"
            Case &H40A
                Return "AC_INVALID_CMD_LENGTH"
            Case &H40B
                Return "AC_BYTE_BUFFER_FRAMING_ERROR"
            Case &H40C
                Return "AC_BYTE_BUFFER_OVERRUN_ERROR"
            Case &H40D
                Return "AC_CMD_ACTIVE_ABORT_REJECTED"
            Case &H40E
                Return "AC_INVALID_RSP_ACKNOWLEDGEMENT"
            Case &H40F
                Return "AC_TRANSMISSION_TIMEOUT"
            Case &H410
                Return "AC_DID_NOT_RECEIVE_ETX"
            Case &H411
                Return "AC_CANCEL_CMD_PACKET_TIMER_ERROR"
            Case &H412
                Return "AC_CUSTOM_BYTE_ERROR"
            Case &H413
                Return "AC_RSP_ACKNOWLEDGEMENT_TIMEOUT"
            Case &H414
                Return "AC_CANCEL_RSP_ACK_TIMER_ERROR"
            Case &H415
                Return "AC_UNEXPECTED_BYTE_RECEIVED"
            Case &H416
                Return "AC_ZERO_LENGTH_CMD"
            Case &H417
                Return "AC_INVALID_CMD_RESERVED_FIELD"
            Case &H418
                Return "AC_RAMBIST_DID_NOT_COMPLETE"
            Case &H419
                Return "AC_IGNORED_BYTE_RECEIVED_WHILE_XMIT_RSP"
            Case &H41A
                Return "AC_INVALID_CDB_DATA_LENGTH"
            Case &H41B
                Return "AC_FW_IMAGE_TOO_BIG"
            Case &H41C
                Return "AC_RSP_TOO_LONG (ACI response longer than available buffer)"
            Case &H41D
                Return "AC_DID_NOT_RCV_ACK_TO_PROGRAM_FLASH"
            Case &H41E
                Return "AC_NULL_POLLING_FUNCTION (Attempt to create a polling object instance without a polling function.)"
            Case &H41F
                Return "AC_MAX_POLLERGEIST_LIMIT_EXCEEDED (Attempt to create more polling object instances than allowed.)"
            Case &H420
                Return "AC_ACCESSING_NON_EXISTENT_POLLERGEIST (Attempt to access a polling object instance that doesn't exist.)"
            Case &H421
                Return "AC_CMD_LENGTH_TOO_LONG (ACI command longer than available buffer.)"
            Case &H422
                Return "AC_CRAM_OVERFLOW (ACI has ran out of CRAM.)"
            Case &H423
                Return "AC_IMAGE_SENT_TOO_BIG (ACI has received a FW image larger than expected.)"
            Case &H424
                Return "AC_INVALID_CMD_PARM_VALUE (An ACI command parameter contains an invalid value.)"
            Case &H425
                Return "AC_OUT_OF_MEMORY (ACI acMalloc() failed, insufficient memory available to process command.)"
            Case &H426
                Return "AC_CONTROL_QUEUE_FULL (The ACI Control queue is full.)"
            Case &H427
                Return "AC_RESPONSE_QUEUE_FULL (The ACI Response queue is full.)"
            Case &H428
                Return "AC_CONTROL_QUEUE_EMPTY (The ACI Control queue is empty.)"
            Case &H429
                Return "AC_RESPONSE_QUEUE_EMPTY (The ACI Response queue is empty.)"
            Case &H42A
                Return "AC_RESPONSE_NAKED (The ACI Response packet was NAKed.)"
            Case &H42B
                Return "AC_UNSUPPORTED_PPL_CMD (ACI attempted to execute an unsupported PPL command.)"
            Case &H42C
                Return "AC_INVALID_PARAMETER (The ACI has detected a command with a parameter out-of-range.)"
            Case &H42D
                Return "AC_OVERLAPPED_PPL_CMD (ACI attempted to execute a PPL command before previous command completed.)"
            Case &H42E
                Return "AC_EXCESSIVE_RAW_DATA (ACI received more raw data than expected, see Write Buffer PPL command.)"
            Case &H42F
                Return "AC_SLOW_CMD (Internal status indicating a SLOW ACI command rather than a fast one.)"
            Case &H430
                Return "AC_XFER_PENDING (Internal status indicating an operation has initiated a DMA transfer.)"
            Case &H431
                Return "AC_UPGRADE_FW (Internal status indicating that a FW image has been downloaded so the f/w should be upgraded.)"
            Case &H432
                Return "AC_UPGRADE_ABORTED (Internal status indicating that a FW image download has been aborted.)"
            Case &H433
                Return "AC_SCSI_CMD_TERMINATED (Surrogate SCSI command terminated due to a SCSI Reset/Abort for LUN.)"
            Case &H434
                Return "AC_DRAM_ALLOCATION_ERROR (The ACI has not been allocated the amount of DRAM it requires.)"
            Case &H435
                Return "AC_CRAM_ALLOCATION_ERROR (The ACI has not been allocated the amount of CRAM it requires.)"
            Case &H436
                Return "AC_INVALID_SCSI_EXCHANGE_ID (An ACI surrogate SCSI command packet contained an invalid Exchange ID.)"
            Case &H437
                Return "AC_UNEXPECTED_EXCHANGE_ID (A CCB contained an invalid exchange ID when it should be valid.)"
            Case &H438
                Return "AC_SCSI_QUEUE_FULL (The ACI Surrogate SCSI queue is empty.)"
            Case &H439
                Return "AC_SCSI_QUEUE_EMPTY (The ACI Surrogate SCSI queue is empty.)"
            Case &H43A
                Return "AC_INVALID_SCSI_QUEUE_ENTRY (A request for an invalid entry in Surrogate SCSI queue has been made.)"
            Case &H43B
                Return "AC_INVALID_SCSI_CDB (The ACI has received an acSurrogateNotifyOp with an unknown SCSI CDB type.)"
            Case &H43C
                Return "AC_UNEXPECTED_SCSI_DATA_LENGTH (The SCSI Data Length parameter has changed unexpectedly.)"
            Case &H43D
                Return "AC_PPL_CMD_PENDING (Internal status indicating a PPL Command is being executed.)"
            Case &H43E
                Return "AC_TO_BUSY_FOR_FAST_CMD (Used in tracepoints to identify cause of AC_BUSY_CMD_REJECTED error.)"
            Case &H43F
                Return "AC_TO_BUSY_FOR_IMMED_CMD (Used in tracepoints to identify cause of AC_BUSY_CMD_REJECTED error.)"
            Case &H440
                Return "AC_TO_BUSY_FOR_SLOW_CMD (Used in tracepoints to identify cause of AC_BUSY_CMD_REJECTED error.)"
            Case &H441
                Return "AC_TO_BUSY_FOR_SCSI_CMD (Used in tracepoints to identify cause of AC_BUSY_CMD_REJECTED error.)"
            Case &H442
                Return "AC_TO_BUSY_COS_OVERLAPPED_CMD (Used in tracepoints to identify cause of AC_BUSY_CMD_REJECTED error.)"
            Case &H443
                Return "AC_TO_BUSY_COS_COMMS_RESYNCH (Used in tracepoints to identify cause of AC_BUSY_CMD_REJECTED error.)"
            Case &H444
                Return "AC_TO_BUSY_COS_UPGRADING (Used in tracepoints to identify cause of AC_BUSY_CMD_REJECTED error.)"
            Case &H445
                Return "AC_TO_BUSY_COS_OUT_OF_MEMORY (Used in tracepoints to identify cause of AC_BUSY_CMD_REJECTED error.)"
            Case &H446
                Return "AC_RESYNCHRONISING_COMMS (ACI is resynchronising comms with library after persistant comms failure.)"
            Case &H447
                Return "AC_UNSUPPORTED_OPCODE (The SCSI Opcode is not supported in this F/W release.)"
            Case &H448
                Return "AC_RESET_ACI_REQUIRED (Internal status indicating an ACI Reset is required.)"
            Case &H449
                Return "AC_RESET_DRIVE_REQUIRED (Internal status indicating a full Drive Reset is required.)"
            Case &H44A
                Return "AC_INVALID_MEMORY_ID (CCB Contains an invalid Memory ID.)"
            Case &H44B
                Return "AC_CCB_ALLOCATION_ERROR (All Command Control Blocks have been allocated.)"
            Case &H44C
                Return "AC_INVALID_CCB (The returned or referenced Command Control Block is invalid.)"
            Case &H44D
                Return "AC_TIMEOUT_CMD_ABORTED (Response Timeout - command aborted and response sent.)"
            Case &H44E
                Return "AC_TIMEOUT_CMD_CONTINUED (Response Timeout - response sent but command allowed to continue.)"
            Case &H44F
                Return "AC_RESPONSE_TIMEOUT (Response Timeout - ACI failed to send response within Response Period.)"
            Case &H450
                Return "AC_TO_BUSY_COS_CCB_QUEUE_FULL (Used in tracepoints to identify cause of AC_BUSY_CMD_REJECTED error.)"
            Case &H451
                Return "AC_REPEATED_SEQUENCE_NUMBER (ACI Command contains sequence number of previous command, command ignored.)"
            Case &H452
                Return "AC_SLOW_CMD_BEFORE_INIT (Internal status indicating a SLOW ACI command being executed before Drive Ready event.)"
            Case &H453
                Return "AC_ACKNOWLEDGEMENT_PERIOD_EXCEEDED (Failed to transmit ACK/NAK within Packet Acknowledgement Period.)"
            Case &H454
                Return "AC_DIRECT_CMD (Internal status indicating a DIRECT ACI command rather than a fast or Slow one.)"
            Case &H455
                Return "AC_TO_BUSY_FOR_DIRECT_CMD (Used in tracepoints to identify cause of AC_BUSY_CMD_REJECTED error.)"
            Case &H456
                Return "AC_BURST_SIZE_UNKNOWN (Cannot perform operation because the SCSI Burst size is zero or unknown.)"
            Case &H457
                Return "AC_PRIMARY_INTERFACE_NOT_CONFIGURED (Command not supported because the primary interface has not been enabled.)"
            Case &H458
                Return "AC_NO_TASK_OBJECT (Unable to get a new task object.)"
            Case &H459
                Return "AC_FRAME_NAKED_ACI_CMD_PENDING (An ADI frame has been received while one or more ACI commands are outstanding.)"
            Case &H45A
                Return "AC_NOT_ADI_FRAME (An SOF was received in ACI Mode but didn't turn out to be a valid Frame.)"
            Case &H45B
                Return "AC_REQUEST_DATA_LENGTH_ERROR (The SSC device server has not requested the amount of data supplied with the ACI SCSI command.)"
            Case &H45C
                Return "AC_UNSUPPORTED_PORT_ID (The specified automation port is not supported.)"
            Case &H45D
                Return "AC_RSP_PENDING (This status indicates a function shall send a response when it has finished)"
            Case &H480
                Return "AC_TEST_FAILED_COS_EXECUTING_DIRECT_CMD (ACI Self Test failure- The ACI shouldn't be executing a Direct Command while performing the Self Test.)"
            Case &H481
                Return "AC_TEST_FAILED_COS_EXECUTING_SLOW_CMD (ACI Self Test failure- The ACI shouldn't be executing a Slow Command while performing the Self Test.)"
            Case &H482
                Return "AC_TEST_FAILED_COS_CTRL_QUE_NOT_EMPTY (ACI Self Test failure- If the ACI is idle, the Control Queue should be empty.)"
            Case &H483
                Return "AC_TEST_FAILED_COS_RESPONSE_QUE_NOT_EMPTY (ACI Self Test failure - If the ACI is idle, the Response Queue should be empty.)"
            Case &H484
                Return "AC_TEST_FAILED_COS_SCSI_QUE_NOT_EMPTY (ACI Self Test failure- If the ACI is idle, the Surrogate SCSI queue should be empty.)"
            Case &H485
                Return "AC_TEST_FAILED_COS_SMALL_DATA_LEAK (ACI Self Test failure- If the ACI is idle, Only the Self Test command should be allocated in the Small Data Region.)"
            Case &H486
                Return "AC_TEST_FAILED_COS_LARGE_DATA_LEAK (ACI Self Test failure- If the ACI is idle, No memory from the Large Data Region should be allocated.)"
            Case &H487
                Return "AC_TEST_FAILED_COS_CRAM_DATA_LEAK (ACI Self Test failure- If the ACI is idle, No CRAM memory should be allocated.)"
            Case &H488
                Return "AC_TEST_FAILED_COS_CCB_MALLOC_LEAK (ACI Self Test failure- If the ACI is idle, only 1 CCB should be allocated.)"
            Case &H800
                Return "BMM_INIT_ERROR (The buffer manager failed to initialise correctly)"
            Case &H801
                Return "BMM_MODULE_ID_UNKOWN_ERROR (No Buffer Allocation description exist for the supplied Module ID)"
            Case &H802
                Return "BMM_REQUEST_QUEUE_OVF_ERROR (The request queue has over run)"
            Case &H803
                Return "BMM_PRIORITY_QUEUE_OVF_ERROR (The priority request queue has over run)"
            Case &H804
                Return "BMM_DATASET_IDX_ERROR (BMMDataSetIdxToAddr() has been passed an invalid Idx)"
            Case &H805
                Return "BMM_DSIT_IDX_ERROR (BMMDataSetIdxToDSITAddr() has been passed an invalid Idx)"
            Case &H806
                Return "BMM_ACN_IDX_ERROR (BMMDataSetIdxToACNandLPOSAddr() has been passed an invalid Idx)"
            Case &H807
                Return "BMM_WRAP_IDX_ERROR (BMMDataSetIdxToWrapAddr() has been passed an invalid Data Set Idx)"
            Case &H808
                Return "BMM_NOTIFICATION_QUEUE_OVF_ERROR (The Notification queue has overflowed. See BMMRequestXferEx().)"
            Case &H809
                Return "BMM_NOTIFICATION_C1_CHECK_FAIL_ERROR (The notification was not SDL signal for DSIT read. See BMMXferComplete().)"
            Case &H80A
                Return "BMM_REQUEST_QUEUE_UVF_ERROR (The Notification queue has underflowed. See BMMXferComplete().)"
            Case &H1800
                Return "DI_NO_ERROR"
            Case &H1801
                Return "DI_INVALID_COMMAND"
            Case &H1802
                Return "DI_INVALID_PARAMETER"
            Case &H1803
                Return "DI_DRIVE_NOT_READY"
            Case &H1804
                Return "DI_COMMAND_FAILED"
            Case &H1805
                Return "DI_COMMAND_ABORTED"
            Case &H1806
                Return "DI_TOO_FEW_PARMS"
            Case &H1807
                Return "DI_TOO_MANY_PARMS"
            Case &H1808
                Return "DI_COMMAND_DENIED"
            Case &H1809
                Return "DI_CDB_OPCODE_ERR (Operation Code not supported in Diagnostic CDB command)"
            Case &H180A
                Return "DI_CDB_PAGE_CODE_ERR (Page Code not supported in Diagnostic CDB command)"
            Case &H180B
                Return "DI_CDB_BUF_ID_ERR (Buffer ID not supported in READ/WRITE BUFFER command)"
            Case &H180C
                Return "DI_PARITY_ERROR (Parity error on serial receive)"
            Case &H180D
                Return "DI_FRAMING_ERROR (Framing error on serial receive)"
            Case &H180E
                Return "DI_OVERFLOW_ERROR (Overflow erroron serial receive)"
            Case &H180F
                Return "DI_INPUT_TOO_LONG (Excessive input length, exceeds 220 characters)"
            Case &H1810
                Return "DI_POST_NOT_EXECUTED (POST Not Executed)"
            Case &H1820
                Return "DI_REG_TEST_FAILED (Error Detected During Register Walking 1 Test)"
            Case &H1821
                Return "DI_BIST_TEST_FAILED (Built-in Selftest Failure)"
            Case &H1822
                Return "DI_UNSUPPORTED_TEST_COMMAND (No Test AvailableFor The Parameters Provided)"
            Case &H1823
                Return "DI_MEM_TEST_FAILED (Error Detected During Memory Test)"
            Case &H1830
                Return "DI_POST_FAILED_BRUTUS_DATA_BUS (POST Failed Brutus Internal SRAM Data Bus Test)"
            Case &H1831
                Return "DI_POST_FAILED_BRUTUS_ADDR_BUS (POST Failed Brutus Internal SRAM Address Bus Test)"
            Case &H1840
                Return "DI_POST_FAILED_DRAM_MPU_PORT (POST Failed DRAM MPU Port Test)"
            Case &H1841
                Return "DI_POST_FAILED_DRAM_DATA_BUS (POST Failed DRAM Data Bus Test)"
            Case &H1842
                Return "DI_POST_FAILED_DRAM_ADDR_BUS (POST Failed DRAM Addr Bus Test)"
            Case &H1843
                Return "DI_POST_FAILED_YURI_REG (POST Failed Yuri Register Test)"
            Case &H1844
                Return "DI_POST_FAILED_YURI_BIST (POST Failed Yuri BIST (Built In Self Test))"
            Case &H1845
                Return "DI_POST_FAILED_IMAGE_CHECKSUM (POST Failed Firmware Image Checksum)"
            Case &H1846
                Return "DI_POST_FAILED_CRAM_DATA_BUS (POST Failed CRAM Data Bus Test)"
            Case &H1847
                Return "DI_POST_FAILED_CRAM_ADDR_BUS (POST Failed CRAM Address Bus Test)"
            Case &H1848
                Return "DI_POST_FAILED_SELWAY_REG (POST Failed Selway Register Test)"
            Case &H1849
                Return "DI_POST_FAILED_SELWAY_DATA_BUS (POST Failed Selway Buffer Data Bus Test)"
            Case &H184A
                Return "DI_POST_FAILED_SELWAY_ADDR_BUS (POST Failed Selway Buffer Address Bus Test)"
            Case &H184B
                Return "DI_EXIT_DC_MODE (Internal status instructing DI to exit Data Collection Mode)"
            Case &H184C
                Return "DI_SLOW_CMD (Internal status instructing DI to execute cmd as a Slow command)"
            Case &H184D
                Return "DI_BUSY_CMD_REJECTED (Diagnostic Control has rejected a command because it's already executing a command.)"
            Case &H184E
                Return "DI_MEM_RELEASE_VIOLATION (Attempted to release Diagnostic Diagnostic Results memory that hasn't been allocated.)"
            Case &H184F
                Return "DI_RESULTS_RESERVED (Unable to perform Diagnostic command as the Diagnostic Results memory is already reserved.)"
            Case &H1850
                Return "DI_RESULTS_OVERFLOW (Diagnostic Results data has overflow the memory allocated for results.)"
            Case &H1851
                Return "DI_UNSUPPORT_SCSI_OPCODE (DI has received an unsupported SCSI opcode in diExecScsiDiagOp.)"
            Case &H1852
                Return "DI_INVALID_BAUDRATE (DI attemptedto set an illegal Baud Rate.)"
            Case &H1853
                Return "DI_READ_ONLY_CONFIG (Set Config attempted to set a Read-Only configuration.)"
            Case &H1854
                Return "DI_XMIT_RESPONSE (Indicates DI needs to send response in Port Buffer before completing cmd.)"
            Case &H1855
                Return "DI_POST_FAILED_EXT_SRAM_DATA_BUS (POST Failed Brutus External SRAM Data Bus Test)"
            Case &H1856
                Return "DI_POST_FAILED_EXT_SRAM_ADDR_BUS (POST Failed Brutus External SRAM Address Bus Test)"
            Case &H1857
                Return "DI_PROCESS_FLM_LOG (Internal status indicating that a Log command specifies a FLM log.)"
            Case &H1858
                Return "DI_OPERATION_INCOMPLETE (Returned by some log extraction functions to indicate there is more data to extract)"
            Case &H1859
                Return "DI_COMMS_ERROR (Some sort of comms error (framing/overrun etc) detected on Serial Test Port)"
            Case &H185A
                Return "DI_RECEIVE_TIMEOUT (The Serial Test Port has timed-out receiving data)"
            Case &H185B
                Return "DI_UNSUPPORTED_OPERATION (A Device Server has requested an operation not supported by diPortIF)"
            Case &H185C
                Return "DI_NO_TASK_OBJECT (Unable to get a new task object.)"
            Case &H185D
                Return "DI_ERR_LUN_NOT_CONFIGURED (SCSI Command cannot be executed in Limited Operation Mode!)"
            Case &H185E
                Return "DI_STATUS_BEFORE_PROTOCOL (Internal status instructing DI to send Status before starting protocol test)"
            Case &H185F
                Return "DI_CDB_BUF_ACCESS_ERR (Memory access denied to READ/WRITE BUFFER command)"
            Case &H1860
                Return "DI_MEMORY_ACCESS_ERR (Memory access denied to READ/WRITE MEMORY command)"
            Case &H1861
                Return "DI_POST_FAILED_ECC_URERR (POST Failed Unrecoverable ECC error detected)"
            Case &H1862
                Return "DI_INVALID_FIELD_IN_CDB (A field is invalid in a Diagnostic CDB command)"
            Case &H1863
                Return "DI_POST_FAILED_IMAGE_CS (POST Failed Image checksum invalid)"
            Case &H1864
                Return "DI_POST_IMAGE_CS_INCOMPLETE (POST Failed Image checksum incomplete)"
            Case &H1865
                Return "DI_EEPROM_ACCESS_ERR (Invalid Length/Address in READ/WRITE MEMORY command)"
            Case &H1866
                Return "DI_WRITE_CM_EEPROM_ACCESS_DENIED (Drive Configuration Table returned permission denied in response to Write CM/EEPROM operation)"
            Case &H1867
                Return "DI_ROOT_KEY_NOT_FOUND (Verify root key failed as no root key found in OTP)"
            Case &H1868
                Return "DI_ROOT_KEY_VERIFICATION_FAILED (Root key is incorrect)"
            Case &H1869
                Return "DI_DRIVE_IN_UNSECURED_STATE (Drive unexpectedly in unsecured state)"
            Case &H186A
                Return "DI_PUBLIC_KEY_NOT_FOUND (RSA Public key not found )"
            Case &H186B
                Return "DI_PRIVATE_KEY_NOT_FOUND (RSA Private key not found )"
            Case &H186C
                Return "DI_RSA_KEY_NOT_FOUND (RSA Key pair not found )"
            Case &H186D
                Return "DI_RSA_KEY_ENCRYPT_DECRYPT_FAILED (Data following encryption/decryption with RSA key is incorrect)"
            Case &H186E
                Return "DI_ROOT_KEY_DUPLICATED (Root key generated when one already exists)"
            Case &H186F
                Return "DI_UNEXPECTED_SECURITY_STATE (Drive in unexpected security state)"
            Case &H1870
                Return "DI_RSA_KEY_DUPLICATED (RSA key storage was attempted when one already exists)"
            Case &H1871
                Return "DI_OTP_PUBLIC_KEY_DUPLICATED (OTP Public key already exists)"
            Case &H1872
                Return "DI_OTP_PUBLIC_KEY_NOT_FOUND (OTP Public key not found)"
            Case &H1873
                Return "DI_MGMNT_ARM_TESTID_INVALID (Management ARM testid is not supported)"
            Case &H1874
                Return "DI_HUMIDITY_UNAVAILABLE (Unable to obtain Humdity Value)"
            Case &H1875
                Return "DI_TEMP_UNAVAILABLE (Unable to obtain Humdity Value)"
            Case &H1876
                Return "DI_OTP_WRITE_FAILURE (Unable to perform OTP write)"
            Case &H1877
                Return "DI_OTP_READ_FAILURE (Unable to perform OTP Read)"
            Case &H1878
                Return "DI_OTP_WRPROT_SET (Unable to perform OTP Write, WP set)"
            Case &H1879
                Return "DI_OTP_BANK_NOT_EMPTY (Unable to perform OTP Write, Bank has been previously written to)"
            Case &H187A
                Return "DI_OTP_WRITE_EXCEEDS_MAX_TIME (OTP Write has taken to long)"
            Case &H187B
                Return "DI_OTP_STATUS_NOT_READY (OTP STATUS_RDY not set when expected)"
            Case &H187C
                Return "DI_OTP_RAW_STATUS_NOT_READY (OTP STATUS_RDY not set for bank re-read following write)"
            Case &H187D
                Return "DI_POST_FAILED_OTP_SECURITY_STATE (POST Failed OTP Security state in Debug Mode! )"
            Case &H187E
                Return "DI_OTP_BLANK_CHECK_PARMS_INVALID (Chacking OTP Banks blank, parameters out of bounds  )"
            Case &H187F
                Return "DI_MEMORY_BOUNDS_ERR (Supplied Parameters out of bounds for this Buffer ID)"
            Case &H1880
                Return "DI_POST_FAILED_OTP_VERIFY_DEBUG_SECURITY_STATE (POST Failed OTP verifying Debug Mode! )"
            Case &H1881
                Return "DI_POST_FAILED_OTP_SET_DEBUG_SECURITY_STATE (POST Failed OTP setting Debug Mode! )"
            Case &H1882
                Return "DI_POST_FAILED_OTP_SET_DEBUG_SECURITY_STATE_UNSECURED (POST succesfully set Debug Mode! )"
            Case &H1883
                Return "DI_ISERIAL_BINARY_READ_ERR (Failed to read binary data from the iSerial port!)"
            Case &H1884
                Return "DI_ERT_TEST_IN_PROGRESS (Attempting to run ERT test whilst ERT test in progress)"
            Case &H1885
                Return "DI_RO_TEST_UNEXPECTED_PORT_IF_OP (SCSI Infrastructure requested a Read Only Test PortIF operation with an invalid/obsolete TaskPtr)"
            Case &H1886
                Return "DI_RO_TEST_ILLEGAL_TEST_STATE (Read Only Test operation with an invalid TestState)"
            Case &H1887
                Return "DI_RO_TEST_INVALID_TAPE_TYPE (Read Only Test attempted with an invalid TapeType)"
            Case &H1888
                Return "DI_RO_DATA_PHASE_ERROR (A Read Only Test command requested more SCSI data (out) than was available!)"
            Case &H1889
                Return "DI_RO_UNEXPECTED_CDB_OP (SCSI Infrastructure requested a read only test PortIF operation with an unexpected Opcode)"
            Case &H188A
                Return "DI_HI_PHY_SETTINGS_INVALID (PHY setting parameter incorrect)"
            Case &H188B
                Return "DI_WERT_TEST_INVALID_TAPE_TYPE (Write Ony Test attempted with an invalid TapeType)"
            Case &H188C
                Return "DI_WERT_TEST_ILLEGAL_TEST_STATE (Internal Wert Test operation completed with an invalid TestState)"
            Case &H188D
                Return "DI_FLASH_ROOT_KEY_ERASE_FAIL (An attempt to erase the FLASH area containing the ROOT key failed)"
            Case &H188E
                Return "DI_FLASH_ROOT_KEY_WRITE_FAIL (An attempt to write the FLASH area containing the ROOT key failed)"
            Case &H188F
                Return "DI_FLASH_ROOT_KEY_VERIFY_FAIL (An attempt to verify the FLASH area containing the ROOT key failed)"
            Case &H1890
                Return "DI_FLASH_ROOT_KEY_READ_FAIL (An attempt to read the FLASH area containing the ROOT key failed)"
            Case &H1891
                Return "DI_CMD_UNSUPPORTED_IN_THIS_CODE (Clear Buffer ID 0xB2,0xB4,0xB5,0xB6,0xB7,0xB8,0xBB,or 0xBF only supported in Manufauring FW code)"
            Case &H1892
                Return "DI_POST_FIPS_COMPLIANCE_FAILURE (POST failed as one or more ports will remain open when POST fails, which is not FIPS compliant)"
            Case &H1C00
                Return "DR_ERROR_BAD_CART_TYPE (Attempting to Load a cartridge of a type that Drive Control cannot handle)"
            Case &H1C01
                Return "DR_ERROR_UNLOAD_WITH_PMR (Attempt to unload a cartridge when Prevent Medium Removal is on)"
            Case &H1C02
                Return "DR_FUG_NO_IMAGE (No firmware image available for upgrade)"
            Case &H1C03
                Return "DR_FUG_INCOMPLETE (Firmware image is incomplete)"
            Case &H1C04
                Return "DR_FUG_CORRUPT (Firmware image has checksum or other errors)"
            Case &H1C05
                Return "DR_FUG_INCOMPATIBLE (Firmware image is not compatible with drive configuration)"
            Case &H1C06
                Return "DR_FUG_TOO_BIG (Firmware image is too big to upgrade from)"
            Case &H1C07
                Return "DR_FUG_INTERNAL_ERROR (Internal error in Drive Control firmware upgrade code)"
            Case &H1C08
                Return "DR_POWERON_WITH_FWCART (A FW upgrade cartridge was in the drive when it powered on)"
            Case &H1C09
                Return "DR_BADCM_NOTHREAD (A load without threading has been requested for a cartridge with unusable CM)"
            Case &H1C0A
                Return "DR_BADCM_WRITABLE (Tried to load a writable cartridge with an unusable CM)"
            Case &H1C0B
                Return "DR_WRITETAB_CHANGE (Write protect tab setting was changed during a load)"
            Case &H1C0C
                Return "DR_NON_HP_CLEANING_CART (A non-HPE cleaning cartridge was put in the drive)"
            Case &H1C0D
                Return "DR_UNKNOWN_MANUF_CLEANING_CART (Cannot determine manufacturer of cleaning cartridge)"
            Case &H1C0E
                Return "DR_FUG_NO_IMAGE_SPACE (No DRAM space reserved to hold firmware image)"
            Case &H1C0F
                Return "DR_POWERON_WITH_CLEANINGCART (A cleaning cartridge was in the drive when it powered on)"
            Case &H1C10
                Return "DR_POWERON_EJECT (Drive Control did an eject during the power-on sequence)"
            Case &H1C11
                Return "DR_FWCART (Firmware upgrade cartridge loaded. Expected data cartridge.)"
            Case &H1C12
                Return "DR_NOT_FWCART (Expected firmware upgrade cartridge but got something else on load)"
            Case &H1C13
                Return "DR_THERMAL_FAIL (Failure due to drive temperature being out of acceptable range.)"
            Case &H1C14
                Return "DR_NON_HP_INIT_CLEANING_CART (Cleaning cartridge initialised by a non-HPE drive)"
            Case &H1C15
                Return "DR_CANNOT_CREATE_FWCART (Trying to convert a non data cartridge into a FW upgrade cartridge)"
            Case &H1C16
                Return "DR_NO_CART_PRESENT (Operation failed bacause cartridge is not present or not ready)"
            Case &H1C17
                Return "DR_CLEANING_CART (Cleaning cartridge loaded. Expected data cartridge.)"
            Case &H1C18
                Return "DR_NOT_CLEANING_CART (Expected cleaning cartridge but got something else on load)"
            Case &H1C19
                Return "DR_CANNOT_READ_FID (Cartridge CM is unusable and failed to read the FID)"
            Case &H1C1A
                Return "DR_FUG_NO_IMAGE_INFO (Firmware upgrade image information is unavailable)"
            Case &H1C1B
                Return "DR_FUG_BAD_IMAGE_INFO (Could not find firmware information in new image)"
            Case &H1C1C
                Return "DR_TAPE_LOAD_FAILED_ERROR (Used to signify that a tape load has failed)"
            Case &H1C1D
                Return "DR_EOD_INVALID (Used to signify that the EOD Validity field is not Good)"
            Case &H1C1E
                Return "DR_FUG_INCOMPATIBLE_SUB_PERSONALITY (Firmware image is not compatible with drive sub-personality configuration)"
            Case &H1C1F
                Return "DR_UNEXPECTED_CALLBACK_TIMER (A TMR Timer fired but the ID doesn't match what was expected)"
            Case &H1C20
                Return "DR_FUG_SIGNATURE_INCORRECT (Firmware image has incorrect signature)"
            Case &H1C21
                Return "DR_FUG_UPGRADE_IMAGE_INCOMPATIBLE (Firmware upgrade image version is too old)"
            Case &H1C22
                Return "DR_FUG_IMAGE_IS_TOO_LARGE (Firmware upgrade image version is too large)"
            Case &H1C23
                Return "DR_FUG_INCORRECT_TAPE_FORMAT (Unknown tape format encountered during firmware upgrade)"
            Case &H1C24
                Return "DR_FUG_HOST_FAILURE (Unknown host error during firmware upgrade)"
            Case &H1C25
                Return "DR_INVALID_CHANGE_PARTITION_OPERATION (Unable to change to the specified partition because- We haven't got a loaded two parititon tape, we are already in that specified partition or there is no such partition)"
            Case &H1C26
                Return "DR_PARTITIONS_NOT_SUPPORTED_ON_THIS_MEDIA (Cannot perform a multi partition operation as partitions are not supported on this media)"
            Case &H1C27
                Return "DR_INVALID_NUMBER_OF_PARTITIONS (Media doesn't support this number of partitions)"
            Case &H1C28
                Return "DR_TAPE_CURRENTLY_IN_REQUIRED_FORMAT (Format not required as the tape is already formatted as required)"
            Case &H1C29
                Return "DR_INVALID_PARTITION_SIZES (Requested sizes of paritions are outside valid range for this media)"
            Case &H1C2A
                Return "DR_REINITIALISE_MCC_TASK (DR determined that MCC is hung and we are going to attempt to reinitialise that task)"
            Case &H1C2B
                Return "DR_FUG_INVALID_IMAGE (Unknown update image format)"
            Case &H1C2C
                Return "DR_BOOTLOADER_VERSION_TOO_OLD (Bootloader is too old. Drive contains newer, backward-compatible version)"
            Case &H1C2D
                Return "DR_BOOTLOADER_INCOMPATIBLE (Incompatible bootloader configuration)"
            Case &H1C2E
                Return "DR_BOOTLOADER_SIGNATURE_FAILURE (Bootloader payload signature incorrect)"
            Case &H1C2F
                Return "DR_BOOTLOADER_TOO_BIG (Bootloader too big)"
            Case &H1C30
                Return "DR_FUG_CANT_READ_BL_VERSION (Can't read bootloader version from flash)"
            Case &H2000
                Return "DM_NO_ERROR (Synonymous with GOOD status.)"
            Case &H2001
                Return "DM_INVALID_PARAMETERS (The value of a parameter received with a Drive Monitor operation falls outside its valid range.)"
            Case &H2400
                Return "EI_COMMAND_HOLDER_FULL"
            Case &H2401
                Return "EI_BAD_COMMAND_HANDLE"
            Case &H2402
                Return "EI_EMPTY_COMMAND_HANDLE"
            Case &H2403
                Return "EI_NO_TAPE_LOADED"
            Case &H2404
                Return "EI_ALREADY_LOADED"
            Case &H2405
                Return "EI_IN_DIAG_MODE"
            Case &H2406
                Return "EI_NOT_IN_DIAG_MODE"
            Case &H2407
                Return "EI_WRITE_PROTECT (Tried to write to write protected cartridge)"
            Case &H2408
                Return "EI_ABORT_ACTIVE (Aborted an active command)"
            Case &H2409
                Return "EI_ABORT_INACTIVE (Aborted a command before it became active)"
            Case &H240A
                Return "EI_ABORT_NONE (Tried to abort a command that was not queued)"
            Case &H240B
                Return "EI_INVALID_STATE (Invalid state requested of EII State Manager)"
            Case &H240C
                Return "EI_INVALID_FWTYPE (EII tried to process unsupported firmware upgrade type)"
            Case &H240D
                Return "EI_CANNOT_ABORT (EII state manager could not handle abort request)"
            Case &H240E
                Return "EI_ABORT_ABORTING (Tried to abort a command that was already being aborted)"
            Case &H240F
                Return "EI_BAD_MODULE (Tried to get a command for the wrong module)"
            Case &H2410
                Return "EI_CMD_QUEUE_NOT_EMPTY (Command Specified in eiNotifyOp would not have been top of queue)"
            Case &H2411
                Return "EI_DELETE_QUEUED_CMD (Attempt to delete a queued EII command (FW bug))"
            Case &H2412
                Return "EI_DELETE_EXECUTING_CMD (Multiple attempts to delete an executing EII command (FW bug))"
            Case &H2413
                Return "EI_QUEUE_QUEUED_CMD (Attempt to queue a queued EII command (FW bug))"
            Case &H2414
                Return "EI_QUEUE_EXECUTING_CMD (Attempt to queue an executing EII command (FW bug))"
            Case &H2415
                Return "EI_CMD_QUEUE_EMPTY (Attempt to remove a command from empty EII command queue (FW bug))"
            Case &H2416
                Return "EI_EXECUTE_QUEUED_CMD (Attempt to execute a command still on the EII command queue (FW bug))"
            Case &H2417
                Return "EI_MULTIPLE_EXECUTE (Attempt to execute an EII command when another is executing (FW bug))"
            Case &H2418
                Return "EI_NOT_EXECUTING (Attempt to stop executing an EII command that was not executing (FW bug))"
            Case &H2419
                Return "EI_HANDLE_MODULE_CHANGE (The module putting a commnad on the EII command queue is not the module that allocated it (FW bug))"
            Case &H2801
                Return "FP_FORCED_EJECT (Failure due to use of forced eject)	"
            Case &H2C01
                Return "HI_UNKNOWN_OPCODE"
            Case &H2C02
                Return "HI_RESERVED_FIELD_SET"
            Case &H2C03
                Return "HI_UNKNOWN_MODE_PAGE"
            Case &H2C04
                Return "HI_FIRMWARE_BUG"
            Case &H2C05
                Return "HI_PARAMETER_LIST_LENGTH_ERROR"
            Case &H2C06
                Return "HI_ALREADY_PREVENTED"
            Case &H2C07
                Return "HI_NOT_PREVENTED"
            Case &H2C08
                Return "HI_TOO_MANY_HOSTS"
            Case &H2C09
                Return "HI_32_BIT_OVERFLOW"
            Case &H2C0A
                Return "HI_INVALID_SPACE_CODE"
            Case &H2C0B
                Return "HI_BAD_INQUIRY_PAGE"
            Case &H2C0C
                Return "HI_NOT_THE_RESERVER"
            Case &H2C0D
                Return "HI_NOT_RESERVED"
            Case &H2C0E
                Return "HI_THIRD_PARTY_BAD"
            Case &H2C0F
                Return "HI_THIRD_PARTY_HOST"
            Case &H2C10
                Return "HI_RESERVED"
            Case &H2C11
                Return "HI_READ_BUFFER_ID"
            Case &H2C12
                Return "HI_READ_BUFFER_MODE"
            Case &H2C13
                Return "HI_WRITE_BUFFER_ID"
            Case &H2C14
                Return "HI_WRITE_BUFFER_MODE"
            Case &H2C15
                Return "HI_MAIN_BUFFER_MODE"
            Case &H2C16
                Return "HI_WRITE_BUFFER_HEADER"
            Case &H2C17
                Return "HI_NO_EVPD"
            Case &H2C18
                Return "HI_DRIVE_NOT_READY"
            Case &H2C19
                Return "HI_DENSITY_MEDIUM_NO_TAPE"
            Case &H2C1A
                Return "HI_ARM_FW_ERROR_CODE0 (Used by Embedded ARM FW)"
            Case &H2C1B
                Return "HI_ARM_POST_FAIL (INF_HI_ARM_POST_FAIL)"
            Case &H2C1C
                Return "HI_TX_FAIL (INF_HI_TX_FAIL)"
            Case &H2C1D
                Return "HI_INF_HI_ARM_POST_SDRAM_TEST_FAILED (INF_HI_ARM_POST_SDRAM_TEST_FAILED)"
            Case &H2C1E
                Return "HI_INF_HI_ARM_POST_SDRAM_BIST_TIMEOUT (INF_HI_ARM_POST_SDRAM_BIST_TIMEOUT)"
            Case &H2C1F
                Return "HI_INF_HI_ARM_POST_SDRAM_MEMACCESS (INF_HI_ARM_POST_SDRAM_MEMACCESS)"
            Case &H2C20
                Return "HI_INF_HI_ARM_POST_ATMEL_MEM_TEST (INF_HI_ARM_POST_ATMEL_MEM_TEST)"
            Case &H2C21
                Return "HI_INF_HI_ARM_POST_FAIL_NO_OLGA_CONNECTED (INF_HI_ARM_POST_FAIL_NO_OLGA_CONNECTED)"
            Case &H2C22
                Return "HI_INF_HI_ARM_POST_FC_DIAG_CC1 (INF_HI_ARM_POST_FC_DIAG_CC1)"
            Case &H2C23
                Return "HI_INF_HI_ARM_POST_FC_DIAG_COUNTERS (INF_HI_ARM_POST_FC_DIAG_COUNTERS)"
            Case &H2C24
                Return "HI_INF_HI_ARM_POST_FC_DIAG_FIFOTEST (INF_HI_ARM_POST_FC_DIAG_FIFOTEST)"
            Case &H2C25
                Return "HI_INF_HI_ARM_POST_FC_DIAG_INT (INF_HI_ARM_POST_FC_DIAG_INT)"
            Case &H2C26
                Return "HI_INF_HI_ARM_POST_FC_DIAG_REG_CHECK (INF_HI_ARM_POST_FC_DIAG_REG_CHECK)"
            Case &H2C27
                Return "HI_SPI_HI_REPORT_PARITY_ERROR_STATUS (SPI_HI_REPORT_PARITY_ERROR_STATUS)"
            Case &H2C28
                Return "HI_SPI_HI_ARM_POST_REG_DIAGS_FAILED (SPI_HI_ARM_POST_REG_DIAGS_FAILED)"
            Case &H2C29
                Return "HI_SPI_HI_ARM_POST_PDC_BIST_TIMEOUT (SPI_HI_ARM_POST_PDC_BIST_TIMEOUT)"
            Case &H2C2A
                Return "HI_SPI_HI_ARM_POST_PDC_RAM_BIST_ERROR (SPI_HI_ARM_POST_PDC_RAM_BIST_ERROR)"
            Case &H2C2B
                Return "HI_SPI_HI_ARM_POST_BC_BIST_TIMEOUT (SPI_HI_ARM_POST_BC_BIST_TIMEOUT)"
            Case &H2C2C
                Return "HI_SPI_HI_ARM_POST_BC_RAM_BIST_ERROR (SPI_HI_ARM_POST_BC_RAM_BIST_ERROR)"
            Case &H2C2D
                Return "HI_SPI_BUFF_CHAN_1_CRC_ERROR (SPI_BUFF_CHAN_1_CRC_ERROR)"
            Case &H2C2E
                Return "HI_SPI_BUFF_CHAN_1_FIFO_PARITY_ERROR (SPI_BUFF_CHAN_1_FIFO_PARITY_ERROR)"
            Case &H2C2F
                Return "HI_SPI_SYNC_OFFSET_ERROR (SPI_SYNC_OFFSET_ERROR)"
            Case &H2C30
                Return "HI_SPI_ILLEGAL_WRITE_ERROR (SPI_ILLEGAL_WRITE_ERROR)"
            Case &H2C31
                Return "HI_SPI_ILLEGAL_CMD_ERROR (SPI_ILLEGAL_CMD_ERROR)"
            Case &H2C32
                Return "HI_SPI_FIFO_OVER_UNDER_FLOW_ERROR (SPI_FIFO_OVER_UNDER_FLOW_ERROR)"
            Case &H2C33
                Return "HI_SPI_IDE_MESSAGE_RECEIVED (SPI_IDE_MESSAGE_RECEIVED)"
            Case &H2C34
                Return "HI_SPI_BDR_MESSAGE_RECEIVED (SPI_BDR_MESSAGE_RECEIVED)"
            Case &H2C35
                Return "HI_SPI_ABORT_TASK_MESSAGE_RECEIVED (SPI_ABORT_TASK_MESSAGE_RECEIVED)"
            Case &H2C36
                Return "HI_SPI_PARITY_ERROR_MESSAGE_RECEIVED (SPI_PARITY_ERROR_MESSAGE_RECEIVED)"
            Case &H2C37
                Return "HI_SPI_HI_ARM_POST_REC_MGR_DIAGS_FAILED (SPI_HI_ARM_POST_REC_MGR_DIAGS_FAILED)"
            Case &H2C38
                Return "HI_SPI_HI_ARM_POST_HOST_PORT_DIAGS_FAILED (SPI_HI_ARM_POST_HOST_PORT_DIAGS_FAILED)"
            Case &H2C39
                Return "HI_BM_TITOV_HOST_PORT_CTRL_PREMATURE_DREQ (BM_TITOV_HOST_PORT_CTRL_PREMATURE_DREQ)"
            Case &H2C3A
                Return "HI_BM_TITOV_HOST_PORT_CTRL_PARITY_ERROR (BM_TITOV_HOST_PORT_CTRL_PARITY_ERROR)"
            Case &H2C3B
                Return "HI_BM_TITOV_HOST_PORT_CTRL_CRC_ERROR (BM_TITOV_HOST_PORT_CTRL_CRC_ERROR)"
            Case &H2C3C
                Return "HI_BM_TITOV_HOST_PORT_CTRL_FIFO_OVERFLOW (BM_TITOV_HOST_PORT_CTRL_FIFO_OVERFLOW)"
            Case &H2C3D
                Return "HI_BM_TITOV_HOST_PORT_CTRL_DMA_OVERRUN (BM_TITOV_HOST_PORT_CTRL_DMA_OVERRUN)"
            Case &H2C3E
                Return "HI_BM_TITOV_HOST_PORT_CTRL_OUTSTANDING_ERROR (BM_TITOV_HOST_PORT_CTRL_OUTSTANDING_ERROR)"
            Case &H2C3F
                Return "HI_BM_TITOV_HOST_PORT_CTRL_SYNC_DATA_ERROR (BM_TITOV_HOST_PORT_CTRL_SYNC_DATA_ERROR)"
            Case &H2C40
                Return "HI_BM_CHECK_BUFF_CRCS_MISMATCH (BM_TITOV_HOST_PORT_CTRL_SYNC_DATA_ERROR)"
            Case &H2C41
                Return "HI_BM_CHECK_CRC_PASSED (BM_TITOV_HOST_PORT_CTRL_SYNC_DATA_ERROR)"
            Case &H2C42
                Return "SAS_ACTIVE_CABLE_ERROR (SAS_ACTIVE_CABLE_ERROR)"
            Case &H2C50
                Return "HI_ILLEGAL_SCSI_CMD (HW or FW does not recognize the CDB of SCSI command)"
            Case &H2C51
                Return "HI_SEL_ABORTED_CMD (SCSI Macro command was aborted because drive was selected first)"
            Case &H2C52
                Return "HI_ATN (ATN was pulled by initiator)"
            Case &H2C53
                Return "HI_RESELECT_TIMEOUT (Initiator did not respond to reselect within reselect timeout period)"
            Case &H2C54
                Return "HI_NO_PI_TASK (Internal port interface task queue was empty)"
            Case &H2C55
                Return "HI_TOO_MANY_PI_TASKS (No room left in internal port interface task queue)"
            Case &H2C56
                Return "HI_PARITY_SCSI (Parity error on SCSI bus)"
            Case &H2C57
                Return "HI_PARITY_BUFFER (Parity error in mini-buffer)"
            Case &H2C58
                Return "HI_INVALID_VALUE (Attempted to use invalid value internally)"
            Case &H2C59
                Return "HI_FIFO_NOT_EMPTY (SCSI Fifo not empty when attempting to write to it)"
            Case &H2C5A
                Return "HI_NOT_CONNECTED (Attempted to issue SCSI macro target command while not in target mod)"
            Case &H2C5B
                Return "HI_WRONG_HOST (Attempted to communicate with Host X while connected to Host Y)"
            Case &H2C5C
                Return "HI_WRONG_BUS_STATE (Attempted SCSI macro command while in incorrect bus phase)"
            Case &H2C5D
                Return "HI_NO_INFO_ON_HOST (This host has not communicated with us previously)"
            Case &H2C5E
                Return "HI_INVALID_SPEED (Saved SCSI bus speed for this host is corrupted)"
            Case &H2C5F
                Return "HI_INVALID_SCSI_ID (SCSI ID is out of range (0-15))"
            Case &H2C60
                Return "HI_INVALID_GROUP_CODE (Group code in CDB not supported)"
            Case &H2C61
                Return "HI_OVERLAPPED_CMD (Host attempted to issue overlapped command)"
            Case &H2C62
                Return "HI_NOT_ENOUGH_BUFFER_SPACE (Internal requestor asked for more space than available in the mini-buffer)"
            Case &H2C63
                Return "HI_NO_MINI_BUFFER (Mini-buffer is non-functional)"
            Case &H2C64
                Return "HI_BUFFER_IN_USE (Internal requestor is denied access to the mini-buffer)"
            Case &H2C65
                Return "HI_STATUS_INTERRUPTED (SCSI status phase failed)"
            Case &H2C66
                Return "HI_IDE (Received Initiator Detected Error Message)"
            Case &H2C67
                Return "HI_MPE (Received Message Parity Error Message)"
            Case &H2C68
                Return "HI_BDR (Received Bus Device Reset Message)"
            Case &H2C69
                Return "HI_ABORT_MSG (Received Abort Message)"
            Case &H2C6A
                Return "HI_NO_MEDIA_INFO (Failed Media Information check)"
            Case &H2C6B
                Return "HI_NO_MEDIA (No Tape in Drive)"
            Case &H2C6C
                Return "HI_LOADING_MEDIA (Loading Tape)"
            Case &H2C6D
                Return "HI_MEDIA_CHANGED (Media present but not loaded)"
            Case &H2C6E
                Return "HI_CLEANING (Cleaning heads)"
            Case &H2C6F
                Return "HI_RESET (Got a PON or SCSI Reset)"
            Case &H2C70
                Return "HI_SCSI_MODE_CHANGE (Mode change (LVD/SE) on SCSI bus)"
            Case &H2C71
                Return "HI_SCSI_GROSS_ERROR (Gross error detected by SCSI Macro)"
            Case &H2C72
                Return "HI_ILI_LONG (Illegal length record - too long)"
            Case &H2C73
                Return "HI_ILI_SHORT (Illegal length record - too short)"
            Case &H2C74
                Return "HI_CRC_ERROR (CRC error on read)"
            Case &H2C75
                Return "HI_BURST_SIZE_TOO_LARGE (Requested Burst Size larger than is supported)"
            Case &H2C76
                Return "HI_INVALID_FIELD_IN_MODE_PARAMETER_LIST (There was an invalid field in the mode parameter list for this mode select command)"
            Case &H2C77
                Return "HI_UNLOADING_MEDIA (Unloading Tape)"
            Case &H2C78
                Return "HI_PARAMETER_OUT_OF_RANGE (Internal requestor supplied parameter is out of range)"
            Case &H2C79
                Return "HI_INVALID_ALLOCATION_LENGTH (Allocation length exceeds permitted length)"
            Case &H2C7A
                Return "HI_INVALID_PAGE_CODE (Unsupported Pagecode)"
            Case &H2C7B
                Return "HI_INVALID_PAGE_CODE_IN_PARM_LIST (Unsupported Pagecode)"
            Case &H2C7C
                Return "HI_BOT (BOT encountered on space)"
            Case &H2C7D
                Return "HI_EOT (EOT encountered on space)"
            Case &H2C7E
                Return "HI_BLANK_CHECK_VIRGIN_MEDIA (The media has never been written to before and there was an attempt to read/verify data. Returned by hiPerformPreExeChecks)"
            Case &H2C7F
                Return "HI_POSITION_LOST (TEMP code for returning status after write behind error)"
            Case &H2C80
                Return "HI_LOG_SELECT_PCR_ERROR (PCR error in Log Select command)"
            Case &H2C81
                Return "HI_NON_RESETABLE_LOG_PAGE (The Page Code is not a resetable page)"
            Case &H2C82
                Return "HI_NON_WRITABLE_LOG_PAGE (The Page Code is not a writable page)"
            Case &H2C83
                Return "HI_LOG_PAGE_HEADER_RSVD_BIT_SET (The reserved bit in the Log Page header has been set)"
            Case &H2C84
                Return "HI_LOG_SELECT_PAGE_LENGTH_ERROR (Log Select Page Length incorrect)"
            Case &H2C85
                Return "HI_LOG_SELECT_PARAMETER_HEADER_ERROR (There is an error with the Log Parameter Header)"
            Case &H2C86
                Return "HI_LOG_SELECT_PARAM_LIST_LENGTH_ERROR (Log Select Parameter list length error)"
            Case &H2C87
                Return "HI_LOG_SENSE_PAGE_CODE_ERROR (Log Sense Page Code is invalid)"
            Case &H2C88
                Return "HI_LOG_SENSE_PC_ERROR (Log Sense PC Code is in Error)"
            Case &H2C89
                Return "HI_PARAMETER_HEADER_ERROR (Log Select Error - Parameter Header Error)"
            Case &H2C8A
                Return "HI_RESTART_LP (Restart the Logical Pipeline after a format error)"
            Case &H2C8E
                Return "HI_BUFFER_MANAGER_ERROR (Buffer Manager has been interrupted with an Error)"
            Case &H2C8F
                Return "HI_CHECK_YOUR_SCSI_CABLES (HIF has finished all of the retries for a data phase)"
            Case &H2C90
                Return "HI_LOG_SELECT_PARAM_LIST_ERROR (Log Select parameter list length error)"
            Case &H2C91
                Return "HI_NO_FREE_SENSE_BUFFERS (hiPopulateSenseBuffer() could not find any free sense data buffers)"
            Case &H2C92
                Return "HI_FAILURE_PREDITION_THRESHOLD_EXCEEDED_FALSE (This error code is sent when we check condition a CDB as a result of the Test flag being set in the Information Exceptions Mode Page)"
            Case &H2C93
                Return "HI_RESET_AFTER_GE (Used to POST UA 2900 after detected Selway GE)"
            Case &H2C94
                Return "HI_RETURN_GOOD_STATUS (Used to force good status to be returned)"
            Case &H2C95
                Return "HI_INQUIRY_FIRMWARE_BUG (Indicates a FW bug in inquiry page handling)"
            Case &H2C96
                Return "HI_SET_MEDIUM_REMOVAL_FW_BUG (Indicates a FW bug in PreventAllowMediaRemove)"
            Case &H2C97
                Return "HI_MODEPAGE_FW_FIRMWARE_BUG (Indicates a FW bug in Mode Page parsing)"
            Case &H2C98
                Return "HI_EWEOM (Write/Write Filemarks inside EWEOM)"
            Case &H2C99
                Return "HI_ILLEGAL_SCSI_MACRO_CMD (Firmware incorrectly programmed SCSI macro)"
            Case &H2C9A
                Return "HI_UNSUPPORTED_LUN (Indicates that an Unsupported LUN was specified in the SCSI Identify Message)"
            Case &H2C9B
                Return "HI_ABORT_IN_PROGRESS (Aborting a Previous Cmd)"
            Case &H2C9C
                Return "HI_ABORTING_AND_NO_DISCONNECT (Reject a cmd that ca,'t be queued whilst abort processing is active)"
            Case &H2C9D
                Return "HI_COMMAND_PHASE_RETRIES_FAILED (HIF has finished all of the retries for a command phase)"
            Case &H2C9E
                Return "HI_PARAMETER_NOT_SUPPORTED (A request for an invalid page code has been sent)"
            Case &H2C9F
                Return "HI_BUFFER_OFFSET_GOOD (Used internally by Read Write Buffer code, should never be reported to the Host)"
            Case &H2CA0
                Return "HI_OLD_STYLE_OPERATION_IN_PROGRESS (Reported when an Immediate command is executing and a subsequent command received)"
            Case &H2CA1
                Return "HI_ILI_LONG_AND_EOR (Illegal length record - too long and EOR in FIFO. Occurs when rec is long by less than FIFO length)"
            Case &H2CA2
                Return "HI_ILI_SHORT_AND_CRC (Illegal length record - too short with bad CRC)"
            Case &H2CA3
                Return "HI_ILI_LONG_AND_CRC (Illegal length record - too long with bad CRC)"
            Case &H2CA4
                Return "HI_LUN_NOT_CONFIGURED (Drive is the process of becoming ready)"
            Case &H2CA5
                Return "HI_ILI_LONG_RESIDUE_READ_ERROR (ILI long has been detected, but a read error was encountered during residue flush)"
            Case &H2CA6
                Return "HI_INIT_CMD_REQUIRED (Tape is loaded but not threaded, init comamnd is required)"
            Case &H2CA7
                Return "HI_ILI_LONG_FLUSH_TIMEOUT (ILI long has been detected, but flush for residue timed out)"
            Case &H2CA8
                Return "HI_EL_TORITO_CORRUPT (CDROM El Torito Identifier is corrupt)"
            Case &H2CA9
                Return "HI_GAGARIN_ASIC_NOT_SUPPORTED (Gagarin ASIC not supported anymore)"
            Case &H2CAA
                Return "HI_INVALID_SELWAY_ASIC_REVISION (Invalid revision of Selway ASIC)"
            Case &H2CAB
                Return "HI_MAM_ATTRIB_HEADER_TRUNCATED (Parameter list length specified has resulted in a attribute header truncation)"
            Case &H2CAC
                Return "HI_MAM_INVALID_FIELD_IN_ATTRIB_HEADER (Reserved field set in attribute header)"
            Case &H2CAD
                Return "HI_MAM_ATTRIB_ID_NOT_ASCENDING (Attribute ID's need to be in ascending order)"
            Case &H2CAE
                Return "HI_MAM_ATTRIB_FORMAT_UNSUPPORTED (Attribute header specifiecs a unsupported attribute value)"
            Case &H2CAF
                Return "HI_MAM_ATTRIB_ID_INVALID (Attrib ID unsupported)"
            Case &H2CB0
                Return "HI_MAM_FORMAT_INCORRECT_FOR_ATTRIBUTE_ID (Incorrect Format for this attribute ID)"
            Case &H2CB1
                Return "HI_MAM_ATTRIB_LENGTH_INCORRECT (Attribute header specifies an incorrect length for this attribute)"
            Case &H2CB2
                Return "HI_MAM_NO_SPACE_FOR_ATTRIB (Host attribute area in MAM is full)"
            Case &H2CB3
                Return "HI_MAM_DEL_NON_EXISTANT_ATTRIB_ID (Write Attrib command attempted to delete non-existant attribute)"
            Case &H2CB4
                Return "HI_MAM_INVALID_SERVICE_ACTION (Write Attrib command attempted to delete non-existant attribute)"
            Case &H2CB5
                Return "HI_MAM_HOST_ATTRIB_AREA_NOT_VALID (Read attrib failed due to Host Attribute area not being valid)"
            Case &H2CB6
                Return "HI_MAM_INVALID_FIELD_IN_ATTRIB_DATA (Attribute data is invalid for format)"
            Case &H2CB7
                Return "HI_FAILURE_PREDITION_THRESHOLD_EXCEEDED (A Tape Alert flag has been set and the next SCSI command needs to be check conditioned)"
            Case &H2CB8
                Return "HI_GWIF_IDLE_CAUSE_UNKNOWN (Parameter used by hiGWIFIdleOp/hiFlsuhGWIFIdleOp NEVER returned to HOST)"
            Case &H2CB9
                Return "HI_GWIF_IDLE_CAUSE_READ_ERROR (Parameter used by hiGWIFIdleOp/hiFlsuhGWIFIdleOp NEVER returned to HOST)"
            Case &H2CBA
                Return "HI_GWIF_IDLE_CAUSE_WRITE_ERROR (Parameter used by hiGWIFIdleOp/hiFlsuhGWIFIdleOp NEVER returned to HOST)"
            Case &H2CBB
                Return "HI_MAM_ACCESSIBLE (MAM is accessible but cartridge is in load HOLD position - Unit Attention)"
            Case &H2CBC
                Return "HI_MEDIA_NOT_PRESENT_MAM_ACCESSIBLE (MAM is accessible but cartridge is in load HOLD position - Not Ready)"
            Case &H2CBD
                Return "HI_INVALID_PI_TASK (Internal port interface task queue error - invalid task)"
            Case &H2CBE
                Return "HI_WRITE_INHIBIT_BAD_CM (Unable to write due to bad CM)"
            Case &H2CBF
                Return "HI_SELWAY_ATN_IGNORED (Selway ignored ATN on Request Sense)"
            Case &H2CC0
                Return "HI_INVALID_LEOT_WRAPS_REQ (Invalid number of wraps requested for LEOT)"
            Case &H2CC1
                Return "HI_INVALID_LEOT_POS_REQ (Invalid LEOT request compared to current position)"
            Case &H2CC2
                Return "HI_MAM_NOT_ACCESSIBLE (CM is in readable position but cannot be read)"
            Case &H2CC3
                Return "HI_INVALID_NEXUS (SCSI Sequencer was asked to reconnect during invalid nexus)"
            Case &H2CC4
                Return "HI_DATA_PHASE_RETRY_FAILED (SCSI Sequencer received a hiRetryDataBurst which failed)"
            Case &H2CC5
                Return "HI_INQUIRY_DATA_TOO_LONG (Inquiry Data Too Long)"
            Case &H2CC6
                Return "HI_REQUEST_SENSE_DATA_TOO_LONG (Request Sense Data Too Long)"
            Case &H2CC7
                Return "HI_INVALID_INQUIRY_DATA_LUN (Invalid Lun for storing Inquriy Data in MiniBuffer)"
            Case &H2CC8
                Return "HI_NO_FREE_SENSE_SLOT (No Free Slot to Store Request Sense Data)"
            Case &H2CC9
                Return "HI_SURROAGTE_SCSI_NOT_CONFIGURABLE (Surrogate SCSI not configurable)"
            Case &H2CCA
                Return "HI_INVALID_SURROGATE_LUN (Surrogate SCSI Lun not a valid Lun)"
            Case &H2CCB
                Return "HI_SURROGATE_SCSI_COMMAND (Surrogate SCSI Command arrived)"
            Case &H2CCC
                Return "HI_INVALID_TAPE_TYPE (Incompatible tape type)"
            Case &H2CCD
                Return "HI_INVALID_EXCHANGE_ID (The supplied exchange is invalid)"
            Case &H2CCE
                Return "HI_INVALID_DEV_CFG_SDCA_VALUE (Invalid value for Dev Cfg SDCA)"
            Case &H2CCF
                Return "HI_WRITE_BUFFER_PARAMETER_ERROR (Bad length for write buffer command)"
            Case &H2CD0
                Return "HI_ECHO_BUFFER_OVERWRITTEN (Echo Buffer has been overwritten by another host)"
            Case &H2CD1
                Return "HI_HOST_REPORTED_ERROR (Never reported to Host. Used to signify a specical entry in Fault Log. See DDT 69873)"
            Case &H2CD2
                Return "HI_HEBRIDES_GOOD (Never reported to Host - Hebrides gave good status)"
            Case &H2CD3
                Return "HI_HEBRIDES_BUS_ERROR (Hebrides detected a bus error)"
            Case &H2CD4
                Return "HI_UNKNOWN_OPERATION (Hebrides detected an unknown internal opcode)"
            Case &H2CD5
                Return "HI_BAD_CONTEXT (Hebrides detected a bad context ID)"
            Case &H2CD6
                Return "HI_BAD_PARAMETERS (Hebrides detected bad parameters for an internal operation)"
            Case &H2CD7
                Return "HI_DRIVE_ERROR (Hebrides has encountered a FM/EOD. Should not be reported)"
            Case &H2CD8
                Return "HI_RESEL_TIMEOUT (Hebrides SCSI reselection timeout)"
            Case &H2CD9
                Return "HI_INTERNAL_ERROR (Hebrides internal operation failed due to a PIF/MIF buffer parity error)"
            Case &H2CDA
                Return "HI_COMMAND_OUTSTANDING (Should not be reported)"
            Case &H2CDB
                Return "HI_NO_COMMAND_OUTSTANDING (Should not be reported)"
            Case &H2CDC
                Return "HI_NOT_IN_AUTO_MODE_STATUS (Should not be reported)"
            Case &H2CDD
                Return "HI_ARM_POST_FAILURE (ARM POST Failure)"
            Case &H2CDE
                Return "HI_BAD_DATA_LEN_STATUS (Hebrides detected Bad Data Length - FC_DPL mismatch to CDB allocation length)"
            Case &H2CDF
                Return "HI_SELECTED_WHILST_RESELECTING (Never reported to Host)"
            Case &H2CE0
                Return "HI_BAD_STATE (Firmware defect)"
            Case &H2CE1
                Return "HI_BAD_CONFIG (Firmware defect)"
            Case &H2CE2
                Return "HI_MAILBOX_OP_TIME_OUT (The HostIF asic has not responded to a mail box operation within 10ms )"
            Case &H2CE3
                Return "HI_INTERNAL_FW_REBOOT (Got an Internal Firmware Reboot)"
            Case &H2CE4
                Return "HI_SCSI_BUS_RESET (SCSI Bus Reset signal asserted by host)"
            Case &H2CE5
                Return "HI_SCSI_BDR (Bus Device Reset message sent by host)"
            Case &H2CE6
                Return "HI_TRANSCEIVERS_TO_SE (Transceivers changed to SE)"
            Case &H2CE7
                Return "HI_TRANSCEIVERS_TO_LVD (Transceivers changed to LVD)"
            Case &H2CE8
                Return "HI_POWER_ON_RESET (Got a Power ON Reset)"
            Case &H2CE9
                Return "HI_ARM_BOOTLOAD_DOWNLOAD_CHECKSUM_FAIL (Checksum failure when copying bootloader code into Lucan Dual Port RAM)"
            Case &H2CEA
                Return "HI_AMUNSDEN_HAS_BEEN_RESET (DDT73055 - Amunsden has been reset prior to receving the hiPowerOneEvent from IONA)"
            Case &H2CEB
                Return "HI_DDT72972_LF_STALL_ERROR (LF stall on reads)"
            Case &H2CEC
                Return "HI_LOADED_NOT_AVAILABLE (Tape is threaded but drive shows it as unloaded. DDT75213)"
            Case &H2CED
                Return "HI_ARM_VERIFY_DR_TAPE_FAILED (The ARM FW has determined that this is not a DR tape)"
            Case &H2CEE
                Return "HI_LU_INVENTORY_CHANGED (There has been a change in the support Logical Unit inventory)"
            Case &H2CEF
                Return "HI_INVALID_PORT_ID (An invalid Port ID has been Logged In)"
            Case &H2CF0
                Return "HI_INVALID_CFG_SURROGATE_LUN_OPCODE (An invalid Lun operation code has been passed to hiConfigureSurrogateLun() )"
            Case &H2CF1
                Return "HI_OVERSIZED_FIXED_MODE_REQUEST (Fixed mode request was too large)"
            Case &H2CF2
                Return "HI_LUCAN_EXPAND_DOWNLOAD_SIZE_FAIL (Decompression size mismatch while expanding and programming the Lucan FPGA code)"
            Case &H2CF3
                Return "HI_MORE_TO_DO"
            Case &H2CFD
                Return "HI_LOG_SELECT_CHANGED (SCSI command failed because another host has changed the log pages)"
            Case &H2CFE
                Return "HI_FIRMWARE_UPDATED (SCSI command failed because new firmware has been downloaded)"
            Case &H2CFF
                Return "HI_MODE_PARAMETERS_CHANGED (SCSI command failed because another host has changed the mode pages)"
            Case &H2D00
                Return "HI_RAE_ERROR (Hebrides is reporting a failure due to a read ahead error)"
            Case &H2D01
                Return "HI_WBE_ERROR (Hebrides is reporting a failure due to a write behind error)"
            Case &H2D04
                Return "HI_STREAMING_MODE (Drive requested single shot read/write while streamin)"
            Case &H2D05
                Return "HI_HANDLE_NOT_FOUND (Search for a handle for a given context ID failed)"
            Case &H2D06
                Return "HI_EXTENDED_RESET_COMPLETE (This dispatcher has completed the handling of the extended reset rewind )"
            Case &H2D07
                Return "HI_LOADED_FWUPGRADE_UNKNOWN_CART (FW upgrade/unknown cartridge loaded but not threaded)"
            Case &H2D08
                Return "HI_LOADED_CLEANING_CART (Cleaning tape loaded but not threaded)"
            Case &H2D09
                Return "HI_FW_UPGRADE_IN_PROGRESS (Drive control has set drDriveStatus to DR_FW_UPGRADE)"
            Case &H2D0A
                Return "HI_LUN_NOT_READY_OP_IN_PROGRESS (Immediate Load/Unload in progress)"
            Case &H2D0B
                Return "HI_OPERATION_IN_PROGRESS (Reported when an Immediate command is executing and a subsequent command received)"
            Case &H2D0C
                Return "HI_I_T_NEXUS_LOSS_OCCURRED (I_T Nexus Loss Occurred)"
            Case &H2D0D
                Return "HI_TOO_MANY_LOGICAL_UNITS (The maximum number of surrogate logical units have been defined)"
            Case &H2D0E
                Return "HI_INVALID_SURROGATE_INQ_PAGE (The supplied surrogate inquiry page is incorrect)"
            Case &H2D0F
                Return "HI_INQ_CACHE_FULL (The surrogate logical unit inquiry area is full)"
            Case &H2D10
                Return "HI_INQ_CACHE_CORRUPTED (The surrogate logical unit inquiry area has been corrupted)"
            Case &H2D11
                Return "HI_PORTIF_IS_ENABLED (The operation has been denied as the SCSI port is currently enabled)"
            Case &H2D12
                Return "HI_NO_SENSE_DATA_STORED (ACI has attempted a Surrogate SCSI status operation with out first setting up any sense data)"
            Case &H2D13
                Return "HI_NO_SURROGATE_CMD (This is no nexus live for this surrogate logical unit)"
            Case &H2D14
                Return "HI_INVALID_SCSI_STATUS (An invalid Scsi status value has been supplied)"
            Case &H2D15
                Return "HI_FWD_CMD_TO_LIB (Can not process this command, for some reason send it to lib)"
            Case &H2D16
                Return "HI_SURROGATE_LUN_DELETED (The hiConfigureSurrogateLunOp has resulted in a LUN being deleted)"
            Case &H2D17
                Return "HI_INVALID_SUB_PAGE_CODE (Unsupported Sub Pagecode)"
            Case &H2D18
                Return "HI_FIRST_BURST"
            Case &H2D19
                Return "HI_ACK_NAK_TIMEOUT"
            Case &H2D1A
                Return "HI_NAK_RECEIVED"
            Case &H2D1B
                Return "HI_INITIATOR_RSP_TIMEOUT"
            Case &H2D1C
                Return "HI_DATA_OFFSET_ERROR"
            Case &H2D1D
                Return "HI_IU_TOO_SHORT"
            Case &H2D1E
                Return "HI_TOO_MUCH_WRITE_DATA"
            Case &H2D1F
                Return "HI_CHAN1_CRC_ERROR_BUFF_CRC_RECHECK_GOOD (Chan1 dectected CRC error, subsiquent check of buffer found CRC to be good)"
            Case &H2D20
                Return "HI_CHAN1_CRC_ERROR_BUFF_CRC_RECHECK_BAD (Chan1 dectected CRC error, subsiquent check of buffer found CRC to be wrong)"
            Case &H2D21
                Return "HI_ERR_PHY_TEST_FUNCTION_IN_PROGRESS"
            Case &H2D22
                Return "HI_ERR_INVALID_CONFIG_ACTION"
            Case &H2D23
                Return "HI_ERR_INVALID_RELATIVE_PORT (F/W Defect, caller specified invalid Relative Target Port)"
            Case &H2D24
                Return "HI_ADI_VERIFY_ILI_ERROR (ILI encountered performing ADI Verify)"
            Case &H2D25
                Return "HI_ADI_VERIFY_READ_AHEAD_ERROR (Indicates a read ahead error was encountered during ADI Verify. The read ahead error should be reported to host not this error!)"
            Case &H2D26
                Return "HI_ERR_COUNTERMAND_QUEUE_OVERFLOW (No space on queue for hiCountermandOp)"
            Case &H3000
                Return "LF_NO_ERROR (Synonymous with GOOD status.)"
            Case &H3001
                Return "LF_ABORTED (Operation of the Logical Formatter has been aborted.)"
            Case &H3002
                Return "LF_BUSY (A Logical Formatter process has received a operation request while in a transient state.)"
            Case &H3003
                Return "LF_INVALID_PARAMETERS (The value of a parameter received with a Logical Formatter operation request falls outside its valid range.)"
            Case &H3004
                Return "LF_UNSUPPORTED_OPERATION (A Logical Formatter process has received an operation request while in a mode that does not support that operation.)"
            Case &H3010
                Return "LF_POWERON_RESET_FAILURE (An error condition occured during execution of the Logical Formatter's Power-On or Reset algorithm.)"
            Case &H3011
                Return "LF_UNEXPECTED_INTERRUPT (A Logical Formatter process has recieved a signal from the hardware at an unexpected time.)"
            Case &H3012
                Return "LF_UNEXPECTED_DISCARD_COMPLETE (A Logical Formatter process has recieved a DiscardComplete signal from the hardware at an unexpected time.)"
            Case &H301C
                Return "LF_COMPRESSOR_PARITY_ERROR_CODE (The Logical Formatter has encountered a Compressor parity error.)"
            Case &H301D
                Return "LF_E2E_RECORD_CRC_ERROR_ENCOUNTERED_READ (The Logical Formatter has encountered an E2E CRC error while unformatting the data stream.)"
            Case &H301E
                Return "LF_E2E_RECORD_CRC_ERROR_ENCOUNTERED_WRITE (The Logical Formatter has encountered an E2E CRC error while formatting the data stream.)"
            Case &H301F
                Return "LF_PACKER_STARVED (The Codeword Packer contains data bits that cannot be self-flushed.)"
            Case &H3020
                Return "LF_DATA_PATH_NOT_EMPTY (The Hardware Functional Blocks that form the Logical Formatter data path contain data.)"
            Case &H3021
                Return "LF_FILE_MARK_ENCOUNTERED (The Logical Formatter has encountered a File Mark codeword while unformatting the data stream.)"
            Case &H3022
                Return "LF_RECOVERABLE_FORMAT_ERROR_ENCOUNTERED (The Logical Formatter has encountered a format error while unformatting the data stream.)"
            Case &H3023
                Return "LF_UNRECOVERABLE_FORMAT_ERROR_ENCOUNTERED (The Logical Formatter has encountered a format error while unformatting the data stream.)"
            Case &H3024
                Return "LF_END_MARKER_NOT_REQUIRED (The Logical Formatter has not inserted an End Marker in the current DataSet because it's empty.)"
            Case &H3025
                Return "LF_DATA_PATH_PAUSED (One or more Hardware Functional Blocks in the Logical Formatter are paused.)"
            Case &H3026
                Return "LF_FILE_MARK_PENDING (The Logical Formatter has a File Mark pending meaning it's logically before the Filemark but physically after it.)"
            Case &H3027
                Return "LF_RESTART_LP (Restart the Logical Formatter hardware.)"
            Case &H3028
                Return "LF_BEYOND_TARGET (The Logical Media has provided a Data Set with an Access Point beyond the target position.)"
            Case &H3029
                Return "LF_RECORD_CRC_ERROR_ENCOUNTERED (The Logical Formatter has encountered a CRC error while unformatting the data stream.)"
            Case &H302A
                Return "LF_PRIME_FAILED (The Logical Formatter's C1LFI Hardware Functional Block has failed to prime.)"
            Case &H302B
                Return "LF_ZERO_LEN_REC_ERROR_ENCOUNTERED (The Logical Formatter has encountered a zero length record error.)"
            Case &H302C
                Return "LF_RSVD_CODEWORD_ERROR_ENCOUNTERED (The Logical Formatter has encountered a reserved codeword error.)"
            Case &H302D
                Return "LF_FILE_MARK_IN_REC_ERROR_ENCOUNTERED (The Logical Formatter has encountered a filemark in record error.)"
            Case &H302E
                Return "LF_DECOMP_ERROR_ENCOUNTERED (The Logical Formatter has encountered a decompression error.)"
            Case &H302F
                Return "LF_EOD_ENCOUNTERED (The Logical Formatter has encountered End-Of-Data.)"
            Case &H3030
                Return "LF_WR_FM_FAIL_ABORTED (The Logical Formatter failed to write a filemark - aborted.)"
            Case &H3031
                Return "LF_WR_FM_FAIL_DATA_PATH_NOT_EMPTY (The Logical Formatter failed to write a filemark - data path not empty.)"
            Case &H3032
                Return "LF_WR_FM_FAIL_UNSUPPORTED_OP (The Logical Formatter failed to write a filemark - unsupported op.)"
            Case &H3033
                Return "LF_UNEXPECTED_DATA_TYPE (The Logical Formatter has received a non-user data set.)"
            Case &H3034
                Return "LF_WR_FM_FAIL_PACKER_PAUSED (The Logical Formatter failed to write a filemark - packer paused.)"
            Case &H3035
                Return "LF_ERR_UNEXPECTED_ACCESS_POINT_INTERRUPT (The Logical Formatter received an unexpected Access Point interrupt.)"
            Case &H3036
                Return "LF_ERR_UNABLE_TO_INSERT_AN_END_MARKER (The Logical Formatter was unable to insert an End Marker.)"
            Case &H3037
                Return "LF_ERR_UNABLE_TO_COMPLETE_NEW_DATA_KEY_PROCESS (The Logical Formatter was unable to complete the new LTO5 Data Key process.)"
            Case &H3040
                Return "LF_DATA_COLLECTION_NO_MORE_DATA (Logical Media not able to supply any more datasets.)"
            Case &H3301
                Return "LF_HAL_ABORTED (Operation of the Logical Formatter's Hardware Abstraction Layer has been aborted.)"
            Case &H3302
                Return "LF_HAL_INVALID_PARAMETER (The value of a parameter passed to a function in the Logical Formatter's Hardware Abstraction Layer falls outside its valid range.)"
            Case &H3303
                Return "LF_HAL_ILLEGAL_INTERNAL_STATE (A function in the Logical Formatter's Hardware Abstraction Layer has detected an illegal combination of variable values.)"
            Case &H3304
                Return "LF_HAL_UNSUPPORTED_SERVICE (A function in the Logical Formatter's Hardware Abstraction Layer has received a request while in a mode that does not support that request.)"
            Case &H3305
                Return "LF_HAL_HARDWARE_EVENT_MISSING (The Logical Formatter's hardware has failed to signal (issue an interrupt for) an event expected by the firmware.)"
            Case &H3306
                Return "LF_HAL_HARDWARE_EVENT_MISSING_SHORT_FINAL_BURST (During a Buffer transfer which ends with a final burst of ten bytes or less, the Logical Formatter's hardware failed to signal the transfer completion.)"
            Case &H3307
                Return "LF_HAL_HARDWARE_EVENT_MISSING_SHORT_TRANSFER (During a Buffer transfer of ten bytes or less, the Logical Formatter's hardware failed to signal the transfer completion.)"
            Case &H3308
                Return "LF_HAL_HARDWARE_EVENT_MISSING_FIRST_1K_SHORT_FINAL_BURST (During a Buffer transfer which ends with a final burst of ten bytes or less within the first 1k DRAM page.)"
            Case &H3309
                Return "LF_HOST_PARITY_ERROR_DETECTED (A parity error was detected transferring data between Iona/Lucan and Amundsen.)"
            Case &H330A
                Return "LF_HAL_MISSING_INT_DATAPATH_NOT_EMPTY (Wanted to start a timer for a potential missing NextDAEmpty interrupt but the pipeline wasn't empty.)"
            Case &H330B
                Return "LF_C1LFI_FIFO_HARD_ERR (LF's C1LFI block detected a parity error while reading a byte out of its FIFO.)"
            Case &H330C
                Return "LF_CORRECTABLE_SDRAM_CORRUPTION_ON_RESTORE (LF detected a correctable SDRAM corruption during Restore.)"
            Case &H330D
                Return "LF_UNCORRECTABLE_SDRAM_CORRUPTION_ON_RESTORE (LF detected an uncorrectable SDRAM corruption during Restore.)"
            Case &H330E
                Return "LF_SDRAM_CORRUPTION_BUFFER_INFO (LF SDRAM corruption Buffer info)"
            Case &H3310
                Return "LF_HAL_PIPELINE_STALLED (LF PIPELINE STALLED)"
            Case &H3311
                Return "LF_HAL_COMPRESSOR_RESET (LF COMPRESSOR RESET)"
            Case &H3312
                Return "LF_HAL_STALL_TIMER_ID_CONFUSION (LF STALL TIMER ID CONFUSION)"
            Case &H3320
                Return "LF_HAL_PACKER_OVERRUN (LF PACKER OVERRUN)"
            Case &H3321
                Return "LF_HAL_NON_EMPTY_PACKED_SEGMENT (LF NON-EMPTY PACKED SEGMENT)"
            Case &H3322
                Return "LF_HAL_PACKER_MISSED_EOR (LF PACKER MISSED EOR)"
            Case &H3323
                Return "LF_HAL_UNPACKER_OVERRUN (LF UNPACKER OVERRUN)"
            Case &H3324
                Return "LF_HAL_PACKER_ALIGNED_EOR (LF PACKER ALIGNED EOR)"
            Case &H3325
                Return "LF_HAL_PACKER_DATA_IN_PACKER (LF PACKER DATA IN PACKER)"
            Case &H3326
                Return "LF_HAL_COMPRESSOR_AT_RECORD_BOUNDARY (LF COMPRESSOR AT RECORD BOUNDARY)"
            Case &H3327
                Return "LF_HAL_RECORD_BOUNDARY_STATUS (LF RECORD BOUNDARY STATUS)"
            Case &H332A
                Return "LF_HAL_NO_END_MARKER_FOUND_WHILE_WRITING (LF NO END MARKER FOUND IN PARTIALLY FULL DATASET WHILE WRITING)"
            Case &H332B
                Return "LF_HAL_NO_END_MARKER_FOUND_WHILE_READING (LF NO END MARKER FOUND IN PARTIALLY FULL DATASET WHILE READING)"
            Case &H332C
                Return "LF_HAL_ADJUSTING_BAD_VALID_DATA_LENGTH_WHILE_WRITING (LF BAD VALID DATA LENGTH DETECTED WHILE WRITING)"
            Case &H332D
                Return "LF_HAL_ADJUSTING_BAD_VALID_DATA_LENGTH_WHILE_READING (LF BAD VALID DATA LENGTH DETECTED WHILE READING)"
            Case &H3330
                Return "LF_HAL_UNEXPECTED_CURRENT_REG_VAL (LF PACKER OVERRUN WITH UNEXPECTED CURRENT REG VAL)"
            Case &H3331
                Return "LF_HAL_UNEXPECTED_NEXT_REG_VAL (LF PACKER OVERRUN WITH UNEXPECTED NEXT REG VAL)"
            Case &H3332
                Return "LF_HAL_BAD_ACCESS_POINT_WHILE_WRITING (LF BAD ACCESS POINT VALUE DETECTED WHILE WRITING)"
            Case &H3333
                Return "LF_HAL_BAD_ACCESS_POINT_WHILE_READING (LF BAD ACCESS POINT VALUE DETECTED WHILE READING)"
            Case &H3334
                Return "LF_HAL_ADJUSTING_BAD_ACCESS_POINT_WHILE_WRITING (LF ADJUSTING BAD ACCESS POINT WHILE WRITING)"
            Case &H3335
                Return "LF_HAL_ADJUSTING_BAD_ACCESS_POINT_WHILE_READING (LF ADJUSTING BAD ACCESS POINT WHILE READING)"
            Case &H3336
                Return "LF_HAL_UNABLE_TO_FIND_A_GOOD_ACCESS_POINT_WHILE_WRITING (LF UNABLE TO FIND A GOOD ACCESS POINT WHILE WRITING)"
            Case &H3337
                Return "LF_HAL_UNABLE_TO_FIND_A_GOOD_ACCESS_POINT_WHILE_READING (LF UNABLE TO FIND A GOOD ACCESS POINT WHILE READING)"
            Case &H3338
                Return "LF_HAL_UNABLE_TO_ADJUST_ACCESS_POINT (LF UNABLE TO ADJUST ACCESS POINT)"
            Case &H3339
                Return "LF_ERR_UNABLE_TO_RELEASE (LF UNABLE TO RELEASE)"
            Case &H333A
                Return "LF_ERR_UNSAFE_TO_INSERT_AN_END_MARKER (LF UNSAFE TO INSERT AN END MARKER)"
            Case &H333B
                Return "LF_ERR_END_MARKER_INSERTION (LF END MARKER INSERTION WAS UNSUCCESSFUL)"
            Case &H333C
                Return "LF_ERR_END_MARKER_INSERTION_BAD (LF END MARKER INSERTION WAS BAD)"
            Case &H3340
                Return "LF_HAL_COMPRESSOR_NOT_HUNG_WHEN_EXPECTED (LF PACKER OVERRUN WITH COMPRESSOR NOT HUNG WHEN EXPECTED)"
            Case &H3341
                Return "LF_TRNG_BIST_FAILURE (LF TRNG BIST FAILURE)"
            Case &H3342
                Return "LF_MONOBIT_TEST_FAILURE (LF RNG MONOBIT TEST FAILURE)"
            Case &H3343
                Return "LF_TWOBIT_TEST_FAILURE (LF RNG TWOBIT TEST FAILURE)"
            Case &H3344
                Return "LF_RNG_FAILURE (LF RNG FAILURE)"
            Case &H3345
                Return "LF_IGNORED_TRNG_BIST_FAILURE (LF IGNORING TRNG BIST FAILURE)"
            Case &H3346
                Return "LF_ERR_TRNG_BIST_FAILED_IN_AN_UNEXPECTED_WAY (LF TRNG BIST FAILED IN AN UNEXPECTED WAY)"
            Case &H3347
                Return "LF_ERR_SKIPPING_AN_ALL_ZERO_SAMPLE_FROM_THE_TRNG (LF SKIPPING AN ALL ZERO SAMPLE FROM THE TRUE RANDOM NUMBER GENERATOR)"
            Case &H3350
                Return "LF_HAL_BAD_ENCRYPTION_KEY_INDEX (LF Bad Encryption Key Index detected)"
            Case &H3351
                Return "LF_ENCRYPT_DECOMP_ERR_ENCOUNTERED (LF Encrypt Decompression error detected)"
            Case &H3352
                Return "LF_ENCRYPT_REC_CRC_ERR_ENCOUNTERED (LF Encrypt Record CRC error detected)"
            Case &H3353
                Return "LF_ENCRYPT_CRYPT_ERR_ENCOUNTERED (LF Encrypt Crypt error detected)"
            Case &H3354
                Return "LF_ENCRYPT_UNPACKERLITE_ERR_ENCOUNTERED (LF Encrypt UnpackerLite error detected)"
            Case &H3355
                Return "LF_CRYPT_GCM_TAG_ERR (LF Crypt GCM Tag error detected)"
            Case &H3356
                Return "LF_CRYPT_EOR_ALIGN_ERR (LF Crypt EoR alignment error detected)"
            Case &H3357
                Return "LF_CRYPT_EOR_BEFORE_TAG_ERR (LF Crypt EoR before Tag error detected)"
            Case &H3358
                Return "LF_CRYPT_LF_DATA_VALID_ERR (LF Crypt lf_data_valid error detected)"
            Case &H3359
                Return "LF_CRYPT_EOR_FOUND_ERR (LF Crypt EoR found error detected)"
            Case &H335A
                Return "LF_POST_KEY_WRAP_FAILURE (LF POST KEY WRAP FAILURE)"
            Case &H335B
                Return "LF_POST_KEY_UNWRAP_FAILURE (LF POST KEY UNWRAP FAILURE)"
            Case &H335C
                Return "LF_POST_KEY_UNWRAP_IV_FAILURE (LF POST KEY UNWRAP IV FAILURE)"
            Case &H335D
                Return "LF_KEY_UNWRAP_IV_FAILURE (LF KEY UNWRAP IV FAILURE)"
            Case &H3360
                Return "LF_ENCRYPTED_DATA_DETECTED (LF Encryption Boundary detected)"
            Case &H3361
                Return "LF_ENCRYPTION_KEY_MISMATCH (LF Encryption Key mismatch detected)"
            Case &H3362
                Return "LF_ENCRYPTION_AAD_MISMATCH (LF Encryption AAD mismatch detected)"
            Case &H3363
                Return "LF_ENCRYPTION_UAD_MISMATCH (LF Encryption UAD mismatch detected)"
            Case &H3364
                Return "LF_UNENCRYPTED_DATA_DETECTED (LF Unencryption Boundary detected)"
            Case &H3365
                Return "LF_ENCRYPTION_KAD_MISMATCH (LF Encryption Key Signature mismatch detected while in RAW read mode)"
            Case &H3366
                Return "LF_RAW_RD_ATTEMPTED (LF RAW read attempted on encrypted data)"
            Case &H3367
                Return "LF_EXTERNALLY_ENCRYPTED_DATA_DETECTED (LF Externally Encrypted data detected)"
            Case &H3368
                Return "LF_NON_EXTERNALLY_ENCRYPTED_DATA_DETECTED (LF Non-Externally Encrypted data detected)"
            Case &H3369
                Return "LF_INCORRECT_ENCRYPTION_PARAMS_DETECTED (LF RAW Read M-KAD mismatch detected)"
            Case &H3370
                Return "LF_ENCRYPTION_KAT_FAILED (LF Encryption Known Answer Test has failed)"
            Case &H3371
                Return "LF_DECRYPTION_KAT_FAILED (LF Decryption Known Answer Test has failed)"
            Case &H3372
                Return "LF_RNG_KAT_FAILED (LF Random Number Generator Known Answer Test has failed)"
            Case &H3373
                Return "LF_RNG_CONTINUOUS_TEST_FAILED (LF Random Number Generator Continuous Test has failed)"
            Case &H3380
                Return "LF_BUFF_PORT_DISABLE_FAILED (LF Buffer Port has not disabled)"
            Case &H3381
                Return "LF_BUFF_PORT_LFPOUT_FIFO_EMPTY_FAILED (LF Buffer Port lfpout FIFO has not emptied)"
            Case &H3382
                Return "LF_COMPRESSOR_HW_BUG_WORKAROUND_FAILED (LF Check of Popovich Compressor HW Bug workaround shows the special pattern check has failed return CR 502855)"
            Case &H33FF
                Return "LF_UNDEFINED_ERROR (A non-specific error has occurred in the Logical Formatter.)"
            Case &H3400
                Return "LM_CACHE_OVERFLOW (DataSet has been received when the Cache is already full)"
            Case &H3401
                Return "LM_UNEXPECTED_DATASET (DataSet has been located in the Cache where it shouldn't be)"
            Case &H3402
                Return "LM_UNEXPECTED_TAG (Tag DataSet has been located in the Cache where it shouldn't be)"
            Case &H3403
                Return "LM_DATASET_NOT_LOCKED (Attempted to unlock a DataSet which isn't locked)"
            Case &H3404
                Return "LM_CACHE_EMPTY (Expected at least one DataSet in the Cache)"
            Case &H3405
                Return "LM_DUPLICATE_INDEX (DataSet index appears in the Cache more than once)"
            Case &H3406
                Return "LM_INDEX_OUT_OF_RANGE (DataSet Index is too larger to be valid)"
            Case &H3407
                Return "LM_INVALID_DATASET (Cache entry doesn't contain valid DataSet)"
            Case &H3408
                Return "LM_EOD_ENCOUNTERED (End-Of-Data has been encountered)"
            Case &H3409
                Return "LM_EXCESSIVE_TAGS (Number of Tag DataSets in the Cache exceed limit)"
            Case &H340A
                Return "LM_UNEXPECTED_CACHE_ENTRY (A DataSet is positioned in the Cache incorrectly)"
            Case &H340B
                Return "LM_MISPLACED_INDEX (One or mode DataSet indices are missing from Cache)"
            Case &H340C
                Return "LM_INVALID_VIRTUAL_MODE (Not a recognised Virtual Mode)"
            Case &H340D
                Return "LM_MULTIPLE_DATASETS_LOCKED (Operation not supported when more than one DataSet locked)"
            Case &H340E
                Return "LM_BLANK_TAPE (The tape is unformatted or contains no user DataSets)"
            Case &H340F
                Return "LM_BROKEN_LINKED_LIST (One or more Cache pointers are invalid)"
            Case &H3410
                Return "LM_DATASET_NOT_AVAILABLE (No DataSets in Cache to fulfil request)"
            Case &H3411
                Return "LM_OUTSTANDING_OPERATIONS (Operation not supported while there are operations outstanding)"
            Case &H3412
                Return "LM_DATASETS_LOCKED (Operation not supported while DataSets are locked)"
            Case &H3413
                Return "LM_TARGET_NOT_FOUND (Target DataSet has not been located)"
            Case &H3414
                Return "LM_TARGET_FOUND (Target DataSet has been located)"
            Case &H3415
                Return "LM_CACHE_NOT_INITIALISED (Cache has not be initialised)"
            Case &H3416
                Return "LM_UNSUPPORTED_OPERATION (Received an operation which isn't support in the current mode)"
            Case &H3417
                Return "LM_READ_ONLY_DATASET (LF has attempted to rewrite a read-only DataSet)"
            Case &H3418
                Return "LM_TEST_TIMEOUT (A test has taken too long to complete)"
            Case &H3419
                Return "LM_LP_CACHE_TOO_SLOW (Too many pending LP Cache operations)"
            Case &H341A
                Return "LM_PP_CACHE_TOO_SLOW (Too many pending PP Cache operations)"
            Case &H341B
                Return "LM_UNEXPECTED_RESPONSE (Received an inappropriate reposnse)"
            Case &H341C
                Return "LM_INVALID_POINTER (Linked-List 'Next' pointer is invalid)"
            Case &H341D
                Return "LM_CRAM_TRANSFER_PENDING (CRAM transfer started but not finished)"
            Case &H341E
                Return "LM_INSUFFICIENT_CRAM (Allocated insufficient CRAM)"
            Case &H341F
                Return "LM_NEED_POSN_TO_APPEND (DataSet is available in LM but drive isn't positioned to append)"
            Case &H3420
                Return "LM_FLUSH_REQUIRED (DataSets in LM, Flush WITH_EOD required before the current operation)"
            Case &H3421
                Return "LM_EOD_REQUIRED (LM flushed but EOD required before the current operation)"
            Case &H3422
                Return "LM_UNSUPPORTED_DATASET_TYPE (The specified DataSet type is not supported by the operation)"
            Case &H3423
                Return "LM_UNSUPPORTED_CRAM_TYPE (The specified CRAM DataSet type is not supported)"
            Case &H3424
                Return "LM_NOT_POSITIONED_AT_EOD (LF has attempted an operation away from EOD which can only be performed at EOD)"
            Case &H3425
                Return "LM_DATASET_NUMBER_MISMATCH (Search DataSet Available DataSet No. doesn't match DSIT contents.)"
            Case &H3426
                Return "LM_ENTERING_BACKUP_MODE (LF has requested a dataset while LM is changing to Backup Mode.)"
            Case &H3427
                Return "LM_DS_HAS_NO_ACCESS_POINT (LM has recieved a PP space dataset with no access point.)"
            Case &H3428
                Return "LM_PREVIOUS_DS_HAS_NO_AP (LM has recieved a previous PP space dataset with no access point.)"
            Case &H3480
                Return "LM_GENERATE_EOD (LM is generating an EOD dataset)"
            Case &H37FF
                Return "LM_UNDEFINED_ERROR"
            Case &H3800
                Return "LP_NO_ERROR (Synonymous with GOOD status.)"
            Case &H3801
                Return "LP_ABORTED_OPERATION"
            Case &H3802
                Return "LP_BUSY (Logical Pipeline Control has received an operation request while in a transient state.)"
            Case &H3803
                Return "LP_INVALID_PARAMETERS (The value of a parameter received with a Logical Pipeline Control operation request falls outside its valid range.)"
            Case &H3804
                Return "LP_UNSUPPORTED_OPERATION (Logical Pipeline Control has received an operation request while in a mode that does not allow that operation.)"
            Case &H3805
                Return "LP_WRITE_BEHIND_ERROR_PENDING (Logical Pipeline Control has aborted the operation because of a write behind error.)"
            Case &H3806
                Return "LP_FILE_MARK_ENCOUNTERED (Logical Pipeline Control has detected an unexpected File Mark during a Space operation.)"
            Case &H3BFF
                Return "LP_UNDEFINED_ERROR (A non-specific error has occurred in Logical Pipeline Control.)"
            Case &H3C00
                Return "mcNoError"
            Case &H3C01
                Return "mcAbortedCmdError"
            Case &H3C02
                Return "mcUnsupportedCmdError"
            Case &H3C03
                Return "mcBadParamError"
            Case &H3D01
                Return "mcUndefinedDataObjError"
            Case &H3E01
                Return "mcWaitForSignal"
            Case &H3E02
                Return "mcObjCreateFailed"
            Case &H3E03
                Return "mcObjExecuteFailed"
            Case &H3E04
                Return "mcCmLposValuesSuspect"
            Case &H3E05
                Return "mcNotifyClientListFull"
            Case &H3E06
                Return "mcPosNotifyListFull"
            Case &H3E07
                Return "mcNotifyExistsParamDifferent"
            Case &H3E08
                Return "mcNotifyEventCreateFailed"
            Case &H3E09
                Return "mcNotifyKeyMapFailed"
            Case &H3E0A
                Return "mcNotifyIndexTooLarge"
            Case &H3E0B
                Return "mcTooManyMcCmdObjects"
            Case &H3E0C
                Return "mcCleaningCartExpired"
            Case &H3E0D
                Return "mcUnknownCartFormat (Cannot determine, or do not recognise, cartridge format)"
            Case &H3E0E
                Return "mcCleaningCartThreadError (Cleaning cartridge could not be threaded)"
            Case &H3E0F
                Return "mcTooManyMiCmdObjects"
            Case &H3E10
                Return "mcListingCommandObjects (INFO - Listing command objects in queue)"
            Case &H3E11
                Return "mcMaxHeadBrushesReached (INFO - The max head brushes have been reached)"
            Case &H3FFE
                Return "mcPureVirtualCalled (C++ pure virtual function called)"
            Case &H3FFF
                Return "mcUndefinedError"
            Case &H4002
                Return "nv_INVALID_PARAM"
            Case &H4003
                Return "nv_DATA_LENGTH_INVALID (Data length exceed table length)"
            Case &H4004
                Return "nv_INVALID_EEPROM (Not a valid Eeprom)"
            Case &H4005
                Return "nv_CHKSUM_LOGIC_ERROR (Write to eeprom failed, eeprom invalid)"
            Case &H4006
                Return "nv_CHKSUM_READ_ERROR (Checksum read did not match checksum written)"
            Case &H4007
                Return "nv_GETDATA_UNSUPPORTED_DATA_TYPE (An UnSupported Data Type was requested from Non Volatile Data Manager)"
            Case &H4008
                Return "nv_SETDATA_UNSUPPORTED_DATA_TYPE (An UnSupported Data Type was requested to be set in Non Volatile Data Manager)"
            Case &H4011
                Return "nv_PCA_EEPROM_ABSENT"
            Case &H4012
                Return "nv_PCA_EEPROM_VOID"
            Case &H4013
                Return "nv_PCA_EEPROM_CORRUPT"
            Case &H4014
                Return "nv_PCA_TABLE_INVALID"
            Case &H4015
                Return "nv_READ_ERT_LOG_UPDATE_FAILED (A failure occured while trying to update the Read ERT log in the PCA Eeprom)"
            Case &H4016
                Return "nv_WRITE_ERT_LOG_UPDATE_FAILED (A failure occured while trying to update the Write ERT log in the PCA Eeprom)"
            Case &H4017
                Return "nv_WRTFLTCNTRS_LOG_UPDATE_FAILED (A failure occured while trying to update the Write Fault Counters log in the PCA Eeprom)"
            Case &H4018
                Return "nv_TAPES_USED_LOG_UPDATE_FAILED (A failure occured while trying to update the Tapes Used logs in the PCA Eeprom)"
            Case &H4019
                Return "nv_PCA_TABLE1_INVALID"
            Case &H401A
                Return "nv_PCA_TABLE2_INVALID"
            Case &H4021
                Return "nv_HEAD_EEPROM_ABSENT"
            Case &H4022
                Return "nv_HEAD_EEPROM_VOID"
            Case &H4023
                Return "nv_HEAD_EEPROM_CORRUPT"
            Case &H4024
                Return "nv_HEAD_TABLE_INVALID"
            Case &H4025
                Return "nv_TABLE_A0_INVALID (Head/Flex Manufacturing Parameters Table Invalid)"
            Case &H4026
                Return "nv_TABLE_A1_INVALID (Head Tuning Parameters Table Invalid)"
            Case &H4027
                Return "nv_HEAD_TABLE3_INVALID"
            Case &H4028
                Return "nv_TABLE_A3_INVALID (Formatter Data Skew Parameters Table Invalid)"
            Case &H4031
                Return "nv_MECH_EEPROM_ABSENT"
            Case &H4032
                Return "nv_MECH_EEPROM_VOID"
            Case &H4033
                Return "nv_MECH_EEPROM_CORRUPT"
            Case &H4034
                Return "nv_MECH_TABLE_INVALID"
            Case &H4035
                Return "nv_DRIVE_FAULT_LOG_UPDATE_FAILED (A failure occured while trying to update the Drive Fault logs in the PCA Eeprom)"
            Case &H4036
                Return "nv_FAULT_LOG_POINTER_ERROR (An Algorithm error occured while trying to update the Drive Fault Logs in the Eeprom)"
            Case &H4037
                Return "nv_MECH_TABLE3_INVALID"
            Case &H4038
                Return "nv_MECH_TABLE4_INVALID"
            Case &H4039
                Return "nv_ABORTED_SERVO_FAULT_LOG_ADDITION (The Servo Fault could not be logged because of Eprom access failure)"
            Case &H403A
                Return "nv_MECH_TABLE2_INVALID"
            Case &H403B
                Return "nv_MECH_TABLE1_INVALID"
            Case &H4041
                Return "nv_CM_EEPROM_ABSENT"
            Case &H4042
                Return "nv_CM_EEPROM_VOID"
            Case &H4043
                Return "nv_CM_EEPROM_CORRUPT (The CM could not be written before an unload causing probable corruption in the CM.)"
            Case &H4044
                Return "nv_CM_INVALID_PROTECTED_PAGE_TABLE (An invalid protected page table was found.)"
            Case &H4045
                Return "nv_CM_INVALID_UNPROTECTED_PAGE_TABLE (A CRC error was discoverd over the Unprotected Page Table)"
            Case &H4046
                Return "nv_CM_UNINITIALISED (Not really an error, Indicates a fresh cartridge)"
            Case &H4047
                Return "nv_CM_INVALID_CRC"
            Case &H4048
                Return "nv_CM_INVALID_CRC_CMI (An invalid CRC over the Cartridge Manufacturers Information page was found)"
            Case &H4049
                Return "nv_CM_INVALID_CRC_MMI (An invalid CRC over the Media Manufacturers Information page was found)"
            Case &H404A
                Return "nv_CM_INVALID_CRC_ID (An invalid CRC over the Initialisation Data page was found)"
            Case &H404B
                Return "nv_CM_INITIALISED (An initialisation table was request to be created for a CM with a valid initialisation table in it.)"
            Case &H404C
                Return "nv_CM_NO_EMPTY_PAGE_TABLE (While trying to add a page descriptor to the Unprotected page table a failure occured.)"
            Case &H404D
                Return "nv_CM_INVALID_PAGE_ENTRY_UNPROTECTED (An Unprotected Page Table Entry was attempted with an invalid Page ID.  Page ID is the next parameter shown)"
            Case &H404E
                Return "nv_CM_INVALID_CRC_DMS (An invalid CRC over the Drive Manufacturers Support page was found.)"
            Case &H4050
                Return "nv_CM_TAPE_DIR_VOID (An access to the Tape Directory was requested before it was read from the CM.)"
            Case &H4051
                Return "nv_CM_WRAP_SECTION_HAS_INVALID_CRC (A CRC error was detected in the Tape Directory while being read.)"
            Case &H4052
                Return "nv_CM_INVALID_WRAP_SECTION_REQUESTED (Data for an illegal wrap section was requested from the Tape Directory.)"
            Case &H4053
                Return "nv_CM_INSUFFICIENT_CRAM_FOR_CM_DATA (The Buffer Manager does not have enough CRAM to hold the CM.)"
            Case &H4054
                Return "nv_CM_ABORTED_WRITE_PROTECT (The Write Protect operation was aborted due to bogus initialisation data address in CRAM.)"
            Case &H4055
                Return "nv_CM_WRAP_SECTION_HAS_INVALID_DATA (A consistency error was detected in the Tape Directory while being read.)"
            Case &H4056
                Return "nv_CM_SUSPENDED_APPEND_PAGE_FULL (No more entries can be added to the Suspended Appends page.)"
            Case &H4057
                Return "nv_CM_TAPE_DIR_HEADER_HAS_INVALID_CRC (A CRC error was detected in the the Tape Directory header while being read.)"
            Case &H4058
                Return "nv_CM_EOD_TAPE_DIR_HEADER_HAS_INVALID_CRC (A CRC error was detected in the the Tape Directory header while trying to rebuild it from the EOD copy.)"
            Case &H4060
                Return "nv_CM_EOD_PAGE_VOID (An access to a non-existant EOD page was attempted.)"
            Case &H4061
                Return "nv_CM_INVALID_CRC_EOD (An invalid CRC over the End Of Data <EOD> page was found)"
            Case &H4062
                Return "nv_CM_INITIALISATION_PAGE_VOID (An access to a non-existant Initialisation page was attempted.)"
            Case &H4070
                Return "nv_CM_TAPE_WRITE_PASS_PAGE_VOID (An access to a non-existant tape write pass page was attempted.)"
            Case &H4080
                Return "nv_CM_TAPE_ALERT_PAGE_VOID (An access to a non-existant tape alert page was attempted.)"
            Case &H4090
                Return "nv_CM_NO_USAGE_DATA_AVAILABLE (There is no Usage Data available in the Cartridge Memory.)"
            Case &H4091
                Return "nv_CM_USAGE_PAGE_LOGIC_ERROR (Usage Pages are out of order and cannot be accessed.)"
            Case &H4092
                Return "nv_CM_USAGE_PAGE_CRC_ERROR (The last updated usage page has a CRC error. Data INVALID )"
            Case &H40A0
                Return "nv_CM_NO_MECH_DATA_AVAILABLE (There is no Mech Sub Page Data available in the Cartridge Memory.)"
            Case &H40A1
                Return "nv_CM_MECH_SUB_PAGE_CRC_ERROR (The last updated Mechanism sub page has a CRC error. Data INVALID )"
            Case &H40A2
                Return "nv_INTERNAL_FAILURE (There has been a failure executing self test. This failure is logged in the Fault Log)"
            Case &H40A3
                Return "nv_CM_TAPE_ALERT_CRC_ERROR"
            Case &H40A4
                Return "nv_CM_EOD_PAGE_CRC_ERROR"
            Case &H40A5
                Return "nv_CM_SUSPEND_APPEND_CRC_ERROR"
            Case &H40A6
                Return "nv_CM_MEDIA_MANUF_CRC_ERROR"
            Case &H40A7
                Return "nv_CM_MECHANISM_CRC_ERROR"
            Case &H40A8
                Return "nv_CM_APPLICATION_SPECIFIC_CRC_ERROR"
            Case &H40A9
                Return "nv_UNKNOWN_CART_TYPE_IN_CM"
            Case &H40AA
                Return "nv_CM_FLUSH_TIMEOUT (A CM Flush operation (CRAM to CM) was aborted. Probable cause was a timeout condition.)"
            Case &H40AB
                Return "nv_PCA_PERSISTENT_RESERVATION_TABLE_INVALID"
            Case &H40AC
                Return "nv_CM_INCONSISTENT_WITH_FID (A Specific request to check the consistency between the FID and CM pages shows that an inconsistency exists)"
            Case &H40AD
                Return "nv_EEPROM_READ_ERROR (Unable to read a word from either the head or PCA EEPROM.)"
            Case &H40AE
                Return "nv_TUNING_REV_NO_INVALIDATION_ERROR (A call to nvInvalidateTuningRevNo was made with an incorrect parameter.)"
            Case &H40AF
                Return "nv_GENERAL_INFO (General info about NVDS.)"
            Case &H40B0
                Return "nv_CM_INVALID_CRC_FATAL_ERROR (An invalid CRC over the Fatal Error page was found)"
            Case &H40B1
                Return "nv_CM_UCI_INFORMATION_NOT_AVAILABLE (Not enough information was available to form a correct Unique Cartridge Identity)"
            Case &H40B2
                Return "nv_CANNOT_CHANGE_FIXED_PERSONALITY_BYTE (nvSetPersonalityByte() was asked to change the personality but this variant of code has a fixed personality)"
            Case &H40B3
                Return "nv_PERSONALITY_CHANGE_NOT_ALLOWED_NO_KEY_MATCH (nvSetPersonalityByte() was asked to change the personality but the key provided did not allow this.)"
            Case &H40B4
                Return "nv_PERSONALITY_CHANGE_NOT_ALLOWED (nvSetPersonalityByte() was asked to change the personality but cannot change zero to non-zero or non-zero to zero)"
            Case &H40B5
                Return "nv_CM_INVALID_PAGE_ENTRY_PROTECTED (A protected Page Table Entry was attempted with an invalid Page ID.)"
            Case &H40B6
                Return "nv_CM_CARTRIDGE_CONTENT_CRC_ERROR (The Cartridge Content Data page has a CRC error. Data INVALID )"
            Case &H40B7
                Return "nv_CM_CARTRIDGE_CONTENT_PAGE_VOID (An access to a non-existant Cartridge Content Data page was attempted.)"
            Case &H40B8
                Return "nv_CM_INCORRECT_PAGE_VERSION (The requested field to be accessed does not exist in this version of the page)"
            Case &H40B9
                Return "nv_HAT_NOT_SUPPORTED (This products EEPROM configuration does not support the Persistant storage for the Host Access Table)"
            Case &H40BA
                Return "nv_HAT_NOT_INITIALISED (The persistant storage area for the HAT has never been written to and is therefore not initialised)"
            Case &H40BB
                Return "nv_DI_NOT_INITIALISED (The persistant storage area for the Device Identifier has never been written to and is therefore not initialised)"
            Case &H40BC
                Return "nv_DI_UNSUPPORTED_LUN (The persistant storage area for the Device Identifier only supports storage for 2 LUNS - more has been asked for)"
            Case &H40BD
                Return "nv_CM_OUT_OF_BOUNDS_ACCESS (A request to access an area of CM which is out of bounds has occurred.  The access was disallowed)"
            Case &H40BE
                Return "nv_CM_ALTERNATE_UCI_INFORMATION_NOT_AVAILABLE (Not enough information was available to form a correct Alternate Unique Cartridge Identity)"
            Case &H40BF
                Return "nv_CM_NO_MECH_RELATED_KAD (nvCMGetMechRelatedKAD() was asked to retrieve the KAD in the mech related page but no KAD has been stored)"
            Case &H40C0
                Return "nv_CM_KAD_TOO_BIG (The KAD length in the Mech related page which was asked to be stored or retrieved was too large)"
            Case &H40C1
                Return "nv_CM_NO_MECH_RELATED_PAGE_AVAILABLE (The mech related page has not been created (or tape not loaded) so storage or retrieval of the KAD data is not allowed now)"
            Case &H40C2
                Return "nv_INVALID_STTF_LOG_IDX (This error indicates a request to access a STTF log idex that is outside the permitted range)"
            Case &H40C3
                Return "nv_NO_SAVED_SNAPSHOT_EVENT_CONFIG (This status indicates that the EEPROM doesn't contain previously saved snapshot event configuration data)"
            Case &H40C4
                Return "nv_CM_SUSPENDED_APPEND_PAGE_VOID (An access to a non-existant Suspended Append page was attempted.)"
            Case &H40C5
                Return "nv_CM_FATAL_ERROR_PAGE_VOID (An access to a non-existant Fatal Error page was attempted.)"
            Case &H40C6
                Return "nv_CM_MECH_SPECIFIC_PAGE_VOID (An access to a non-existant Mechanism Specific page was attempted.)"
            Case &H40C7
                Return "nv_CM_KAD_ACCESS_NOT_SUPPORTED (An access to a non-existant Fatal Error page was attempted.)"
            Case &H40C8
                Return "nv_CM_MECH_SPECIFIC_CRC_ERROR (The Mech Specific page has a CRC error. Data INVALID )"
            Case &H40C9
                Return "nv_CM_NO_MECH_SPECIFIC_KAD (nvCMGetMechSpecificKAD() was asked to retrieve the KAD in the mech specific page but no KAD has been stored)"
            Case &H40CA
                Return "nv_CM_NO_MECH_SPECIFIC_PAGE_AVAILABLE (The mech specific page has not been created (or tape not loaded) so storage or retrieval of the KAD data is not allowed now)"
            Case &H40CB
                Return "nv_SECURITY_PARAMETER_NOT_AVAILABLE (The Certificate storage table does not have an entry for the security paramter requested (i.e. it is not stored in the table))"
            Case &H40CC
                Return "nv_NO_SECURITY_PARAMETER_SLOT_FREE (The Certificate storage table does not have a free slot to place the security paramter into)"
            Case &H40CD
                Return "nv_UNKNOWN_SECURITY_PARAMETER (An unknown security paramter was asked for)"
            Case &H40CE
                Return "nv_SECURITY_PARAMETER_TOO_BIG (The security parameter to be stored is too large to fit in the table)"
            Case &H40CF
                Return "nv_CLEARING_SECURITY_PARAMETER_DENIED (This security parameter is not allowed to be cleared)"
            Case &H40D0
                Return "nv_SECURITY_TABLE_BAD_CRC_ON_DIRECTORY (The Initialisation check of the security table showed a bad CRC on the directory entries - we have to clear and rebuild the whole table)"
            Case &H40D1
                Return "nv_MECH_TABLE8_INVALID"
            Case &H40D2
                Return "nv_SECURITY_ENCRYPTED_RECORD_IS_TOO_LARGE (The security parameter was encrypted, however the encrypted record size ends up being too large for storage)"
            Case &H40D3
                Return "nv_KMS_SECURITY_PARAMETER_TOO_BIG (The KMS security parameter to be stored is too large to fit in the table)"
            Case &H40D4
                Return "nv_PCA2_EEPROM_ABSENT (The PCA2 EEPROM is not supported on this platform)"
            Case &H40D5
                Return "nv_UNKNOWN_KMA_SECURITY_PARAMETER (An unknown KMS security paramter was asked for)"
            Case &H40D6
                Return "nv_KMS_SECURITY_ENCRYPTED_RECORD_IS_TOO_LARGE (The KMS security parameter was encrypted, however the encrypted record size ends up being too large for storage)"
            Case &H40D7
                Return "nv_KMS_SECURITY_TABLE_BAD_CRC_ON_DIRECTORY (The Initialisation check of the KMS security table showed a bad CRC on the directory entries - we have to clear and rebuild the whole table)"
            Case &H40D8
                Return "nv_KMS_SECURITY_PARAMETER_NOT_AVAILABLE (The KMS Security table does not have an entry for the KMS security paramter requested (i.e. it is not stored in the table))"
            Case &H40D9
                Return "nv_SECURITY_FIELD_MUST_BE_MOD_16 (The Secure EEPROM storage will only allow encryption of field sizes divisible by 16, e.g. 16, 32, 48 etc)"
            Case &H40DA
                Return "nv_OTP_KEY_NOT_SET (A Decrypt/encrypt operation has been attempted when the OTP Public key is empty)"
            Case &H40DB
                Return "nv_INVALID_DRIVE_KEY_DETECTED_DURING_INITIALISATION (The check of the Drives RSA Key Pair failed.  Parameter 1 gives the actual error propogated from the GetParameter function)"
            Case &H40DC
                Return "nv_NOT_A_TWO_PARTITION_CAPABLE_CARTRIDGE (A two partition type of operation was requested but the cartridge has only been formatted as a single partition cartridge - operation invalid)"
            Case &H40DD
                Return "nv_CM_TRANSPONDER_SN_ERROR (The transponder serial number check byte does not agree with the transponder serial number.)"
            Case &H40DE
                Return "nv_CM_REPORTED_CM_SIZE_ERROR (The reported LTO CM Mfr Info CM size is not valid.)"
            Case &H40DF
                Return "nv_CM_TRANSPONDER_TYPE_ERROR (The transponder type is not valid.)"
            Case &H40E0
                Return "nv_CARTRIDGE_PARTITION_CAPABILITY_EXCEEDED (Operation requested partition number not supported for this cartridge type)"
            Case &H40E1
                Return "nv_CM_APPLICATION_SPECIFIC_PAGE_VOID (An access to a non-existant application specific page was attempted.)"
            Case &H40E2
                Return "nv_CM_READ_WRITE_ERROR_TAPE_NOT_LOADED (An attempt to read or write to the CM failed, however the drive status indicates that no tape was loaded so this may be a spurious issue)"
            Case &H40E4
                Return "nv_TAPE_USAGE_UNKNOWN_LICENCEE (Insert into the tape roller usage log came across an unknown licencee - no data inserted into the table)"
            Case &H40E5
                Return "nv_ENCRYPTION_PARAMETER_TOO_BIG (The Encryption parameter to be stored will be too large to fit in the allowed space)"
            Case &H40E6
                Return "nv_MECH_HAS_NOT_BEEN_PAIRED_TO_PCA (The POST Test to check the Mech pairing with the PCA has shown that the EEPROM has not been paired or has been tampered with)"
            Case &H40E7
                Return "nv_HEAD_FLEX_HAS_NOT_BEEN_PAIRED_TO_PCA (The POST Test to check the Head/Flex pairing with the PCA has shown that the EEPROM has not been paired or has been tampered with)"
            Case &H40E8
                Return "nv_CAL_TABLE_UNKNOWN_SPECIAL_CONTROL_WORD (The special control word in the cal table is not known)"
            Case &H40E9
                Return "nv_PHY_TUNING_TABLE_INCOMPATIBLE_WITH_DRIVE (The product/interface given in the Phy Tuning revision control word shows that it is incompatible with this drive type)"
            Case &H40EA
                Return "nv_FLASH_PROG_ERR_ATTEMPT (An attempt to program a write-protected sector of the serial flash was made)"
            Case &H40EB
                Return "nv_CM_CACHED (Not really an error, Indicates cached CM)"
            Case &H40EC
                Return "nv_UNPROTECTED_PAGE_ERROR (An error has been detected in the unprotected page table during load or unload)"
            Case &H40ED
                Return "nv_CM_APPLICATION_SPECIFIC_CREATION_SUSPECT (The FW is trying to build an application specific page but does not know what generation of tape is loaded)"
            Case &H4400
                Return "OS_GIVE_SEM_FAIL ('vGiveSem' failed to signal a semaphore)"
            Case &H4C00
                Return "C1_HOLD (C1 has finished before C2 is ready for the dataset)"
            Case &H4C01
                Return "PF_INVALID_CONFIG_NAME (Physical formatter has been sent an invalid config)"
            Case &H4C02
                Return "PF_INVALID_CONFIG_VALUE (Physical formatter has been sent an invalid config value)"
            Case &H4C03
                Return "PF_C2_HW_BUSY (Physical formatter C2 hardware is currently processing a dataset)"
            Case &H4C04
                Return "PF_C2_CONTROL_DS0_GO_BIT_SET (Physical formatter C2 control DS0 register go bit is set)"
            Case &H4C05
                Return "PF_C2_CONTROL_DS1_GO_BIT_SET (Physical formatter C2 control DS1 register go bit is set)"
            Case &H4C06
                Return "PF_C1_CONTROL_GO_BIT_SET (Physical formatter C1 control register go bit is set)"
            Case &H4C07
                Return "PF_CCQR_CONTROL_GO_BIT_SET (Physical formatter CCQ Reader control register go bit is set)"
            Case &H4C08
                Return "PF_RCC_CONTROL_GO_BIT_SET (Physical formatter Read Chain Controller control register go bit is set)"
            Case &H4C09
                Return "PF_INVALID_WRITE_LOG_CHANNEL_NUMBER (Physical Formatter has been asked for the error rate log for a channel which does not exist)"
            Case &H4C0A
                Return "PF_INVALID_READ_LOG_CHANNEL_NUMBER (Physical Formatter has been asked for the error rate logs for a channel which does not exist)"
            Case &H4C0B
                Return "PF_CALLBACK_TIMER_NOT_SET (Physical Formatter could not set callback timer to enable Hyperion read gate)"
            Case &H4C0C
                Return "PF_RCC_DS0_STUCK (Physical Formatter Read Chain Controller DS0 stuck)"
            Case &H4C0D
                Return "PF_RCC_DS1_STUCK (Physical Formatter Read Chain Controller DS1 stuck)"
            Case &H4C0E
                Return "PF_DDT63434_WP_UPDATE_FORCED_TO_WORKAROUND (Physical Formatter WP Update - using Yuri bug workaround DDT63434)"
            Case &H4C0F
                Return "PF_NO_VALID_FORMAT (Format other than Gen1 or Gen2)"
            Case &H4C10
                Return "PF_SDRAM_ERROR_DETECTED (SDRAM Error has been detected)"
            Case &H4C11
                Return "PF_CCPS_PRESENTED_BELOW_THRESHOLD (The number of CCPs Presented has fallen below the warning threshold)"
            Case &H4C12
                Return "PF_CCP_OVERWRITTEN (A CCP has been overwritten)"
            Case &H4C13
                Return "PF_C2_ERROR (A C2 error has been reported)"
            Case &H4C14
                Return "PF_C2_INFO (General PF information)"
            Case &H4C15
                Return "PF_UNEXPECTED_FORMAT_COMPLETED (Unexpected format done signal arrived)"
            Case &H4C16
                Return "PF_CHECK_CCQ_WRITE_REWRITE_LOG (Parma 1 = CCQSetsWritten, Param 2 = CCQSetRewrites, Param 3 - pfStatus)"
            Case &H4C17
                Return "PF_WRITE_ERROR_RATE_LOG_ROLLOVER (No of CCPS have rolled over causing a reset of the write error rate log)"
            Case &H4C18
                Return "PF_READ_ERROR_RATE_LOG_ROLLOVER (No of CCPS have rolled over causing a reset of the Read error rate log)"
            Case &H5000
                Return "PP_EOD_NOT_FOUND (Have not found EOD on tape)"
            Case &H5001
                Return "PP_BLANK_TAPE (Have not found any data on tape)"
            Case &H5002
                Return "PP_EOD_ENCOUNTERED (EOD Encountered)"
            Case &H5003
                Return "PP_UNDEFINED_ERROR (Que?)"
            Case &H5004
                Return "PP_START_LPOS_BEFORE_PREVIOUS (Start LPOS of Data Set is before LPOS of previous Data Set)"
            Case &H5005
                Return "PP_START_LPOS_LONG_WAY_AFTER_PREVIOUS (Start LPOS is more than 1 metre after previous Data Set)"
            Case &H5008
                Return "INVALID_CONFIGNUM (An invalid configuration has been requested)"
            Case &H5009
                Return "ABORT_REJECTED_CONTINUE (Not used)"
            Case &H501E
                Return "PP_SEARCH_ACTIVE (Search in progress)"
            Case &H501F
                Return "PP_MECHCMD_TIMEOUT (Mech Control command never responded)"
            Case &H5020
                Return "PP_COMMAND_NOT_ALLOWED (Command not allowed in this variant)"
            Case &H5080
                Return "INVALID_TAPE_TYPE (The tape type returned is not valid for this product.)"
            Case &H5081
                Return "PP_ABORTERT (The Error Rate Test has been aborted)"
            Case &H5083
                Return "PP_PF_WRITE_ERROR (PF reported a write error (excessive RWWs))"
            Case &H5084
                Return "PP_PF_READ_ERROR (PF reported a read error (C2))"
            Case &H5086
                Return "PP_PF_STREAM_FAILED (PF reported a streamfail)"
            Case &H5087
                Return "PP_ERT_REACHED_C1THRESHOLD (Error threshold reached)"
            Case &H5088
                Return "PP_UNKNOWN_NOTIFICATION (Unexpected Mech Control)"
            Case &H5089
                Return "PP_DATA_MISCOMPARE"
            Case &H508A
                Return "PP_REACHED_FOURMETER_GIVEUP_POINT (Drive has gone 4 metres since last Data Set was reported)"
            Case &H508B
                Return "PP_INVALID_SPEED_FOR_ERT (Speed requested for ERT s out of valid range)"
            Case &H508C
                Return "PP_4M_NOTIFY_MISSING (Notify for 4m giveup point is missing)"
            Case &H508D
                Return "PP_WRITE_REACHED_EOT_ERR (EOT reached before requested datasets done)"
            Case &H508E
                Return "PP_INVALID_STARTPOS_FOR_ERT (Start position requested for ERT s out of valid range)"
            Case &H508F
                Return "PP_EXPECTED_DATASET_NOT_FOUND (Cannot find expected Data Set Number)"
            Case &H5090
                Return "PP_BLANK_CHECK (Cannot read anything off tape in last 4 metres)"
            Case &H5091
                Return "PP_TOO_MANY_FLUSHED_DATA_SETS (Too many Data Sets returned while flushing.)"
            Case &H5092
                Return "PP_FAILED_TO_FIND_TARGET (Could not find the target during a space operation.)"
            Case &H5093
                Return "PP_FAILED_TO_FIND_APPEND_ACN (Could not find the target ACN during a search operation.)"
            Case &H5094
                Return "PP_WP_CORRUPT (Write Pass on write has been corrupted.)"
            Case &H5095
                Return "PP_READ_TOO_MANY_DATASETS (ERT read more datasets than expected.)"
            Case &H5096
                Return "PP_INVALID_DATASET_INDEX_FOR_WRITES (Logical Media has supplied an Invalid Data Set Index.)"
            Case &H5097
                Return "PP_DATASET_WRITTEN_BEFORE_BOW (Data Set written before BOW.)"
            Case &H5098
                Return "PP_READ_REACHED_EOT_ERR (EOT reached during reading)"
            Case &H5099
                Return "PP_BOW_FLAGS_NON_ZERO (Warning- Data Set Flags non-zero)"
            Case &H509A
                Return "PP_UNEXPECTED_EOD_ENCOUNTERED (EOD has been read, but is not the one in the CM)"
            Case &H509B
                Return "PP_UNEXPECTED_WRAP_FOR_DATASET (The Data Set that has been read should not be on this wrap)"
            Case &H509C
                Return "PP_SPACE_TARGET_BEYOND_EOD (The Space command is to a logical position that is beyond EOD)"
            Case &H509D
                Return "PP_WRITE_ERT_NOT_ALLOWED (The current cartridge cannot be written to - Write ERT is not allowed.)"
            Case &H509E
                Return "PP_C2_ERROR_IGNORED (The Data Set reported as C2 uncorrectable, is considered GOOD.)"
            Case &H509F
                Return "PP_UCI_IN_DSIT_INCORRECT (The DSIT contains an invalid UCI.)"
            Case &H50A0
                Return "PP_WORM_DATA_HAS_SUSPECT_INTEGRITY (The drive has determined that the WORM data may have been tampered with.)"
            Case &H50A1
                Return "PP_INVALID_PREVIOUS_DS_INDEX (The Logical Media has given an illegal Data Set Index for an Append.)"
            Case &H50A2
                Return "PP_POS_TOO_EARLY_FOR_APPEND (A search operation appears to have found the target too early.)"
            Case &H50A3
                Return "PP_LTO4_FORMAT_VIOLATION_ALGORITHM_ID (An LTO4 dataset was encountered whose Encryption Algorithm Id was not 0x01.)"
            Case &H50A4
                Return "PP_SET_TAPE_WRITE_PASS_VALUE (The value of TapeWritePassValue CurrentWrap and SDL sheet.)"
            Case &H50A5
                Return "PP_MAX_ATS_SPEED_CHANGED (Shows the value that the Max ATS has been set to.)"
            Case &H50A6
                Return "PP_LOSS_OF_LPOS_DETECTED (A 7501 (Loss of LPOS) has been detected => reducing servo MR bias.)"
            Case &H50A7
                Return "PP_RAMP_TO_SPEED_SIG_CONSUMED (A RampToSpeedRsp signal has arrived whilst trying to abort)"
            Case &H50A8
                Return "PP_EXCESSIVE_FUJI_OFFTRACKS (Excessive offtracks have been seen on a Fuji tape)"
            Case &H50A9
                Return "PP_TAPE_NOT_READY_FOR_ERT (Trying to perform an ERT whilst the tape is not threaded)"
            Case &H50AA
                Return "PP_RESTART_WRITE_WITH_NO_ABORT (The FW may have followed a path where we have not aborted the formatter before a re-write)"
            Case &H50AB
                Return "PP_WRITE_PAST_EOW_FORMAT_VIOLATION (The FW has managed to write a dataset that finishes past EOW)"
            Case &H50AC
                Return "PP_LTO5_FORMAT_VIOLATION_ALGORITHM_ID (An LTO5 dataset was encountered whose Encryption Algorithm Id was not 0x02)"
            Case &H50AD
                Return "PP_UNEXPECTED_ERROR_FROM_NVDS (A call to an NVDS function returned with an unexpected error (placed in parameter 2))"
            Case &H50AE
                Return "PP_LTO6_FORMAT_VIOLATION_ALGORITHM_ID (An LTO6 dataset was encountered whose Encryption Algorithm Id was not 0x03.)"
            Case &H50AF
                Return "PP_DATASET_STARTED_4M_AWAY_FROM_EOW (A Dataset was started over 4M away from EOW but reached 4M giveup within 450mm of EOW)"
            Case &H50B0
                Return "PP_CLEAN_BEING_PERFORMED_FOR_A_750C (There have been enougth 750C errors whilst trying to write this dataset to trigger a head clean Param 1 = Total head cleans this load for 750C, Param 2 bitmap of which retry had a 750C)"
            Case &H50B1
                Return "PP_LOG_RETRY_NUMBER_AND_TYPE (This is for information only and highlights which retry is being used Param 1 = Retry Number, Param 2 = Type of retry 0 = Write, 1 = Read, 2 = Space)"
            Case &H50B2
                Return "PP_WRITING_DRIVE_ID_MSB (This is for information only and displays the drive ID from the DSIT of the writing drive (upper 12 bytes of 16))"
            Case &H50B3
                Return "PP_WRITING_DRIVE_ID_LSB (This is for information only and displays the drive ID from the DSIT of the writing drive (lowwer 4 bytes of 16).It is contains two confidence bytes)"
            Case &H50B4
                Return "PP_NO_VALID_EOD_WRITTEN (Have not read EOD on tape and CM contents does not show a valid EOD)"
            Case &H50B5
                Return "PP_INVALID_DATASET_STORE_VALUE (Attempt to rebuild corrupt tape directory wrap resulted in an incorrect dataset store index)"
            Case &H50C0
                Return "PP_WRAP_ZERO_OVERWRITE_DEBUG (CR 505838 - Wrap 0 overwrite investigation)"
            Case &H50C1
                Return "PP_WRAP_ZERO_OVERWRITE_DEBUG_CONTINUED (CR 505838 - Wrap 0 overwrite investigation)"
            Case &H50C2
                Return "PP_5093_APPEND_OFFSET_ORDER_CHANGED (Change offset in an attempt to be able to read the append point)"
            Case &H5400
                Return "PE_SWITCH_TRACE_LOGS (Not an error - just requests trace log bank switch)"
            Case &H5401
                Return "PE_READ_RETRY_TUNING_POSITION (Shows the LPos and MM position for the start of tuning)"
            Case &H5800
                Return "rwc_SUCCESS"
            Case &H5801
                Return "rwc_NULL_POINT"
            Case &H5802
                Return "rwc_INVALID_PARAM"
            Case &H5803
                Return "rwc_NO_HARDWARE"
            Case &H5804
                Return "rwc_SPI_TRANSFER_ERROR"
            Case &H5805
                Return "rwc_PROMETHEUS_SET_ERROR"
            Case &H5806
                Return "rwc_HYPERION_SET_ERROR"
            Case &H5807
                Return "rwc_DAUGHTER_SET_ERROR"
            Case &H5808
                Return "rwc_CALIB_DIDNOT_COMPLETE"
            Case &H5809
                Return "rwc_SERVOBIASSTATUS_FALSE"
            Case &H5810
                Return "rwc_WRONG_NO_OF_PARMS"
            Case &H5820
                Return "rwc_PROMETHEUS_0_DEFAULT_SETUP_ERROR (Could not set Prometheus 0 to default values)"
            Case &H5821
                Return "rwc_PROMETHEUS_1_DEFAULT_SETUP_ERROR (Could not set Prometheus 1 to default values)"
            Case &H5822
                Return "rwc_PROMETHEUS_2_DEFAULT_SETUP_ERROR (Could not set Prometheus 2 to default values)"
            Case &H5823
                Return "rwc_PROMETHEUS_3_DEFAULT_SETUP_ERROR (Could not set Prometheus 3 to default values)"
            Case &H5824
                Return "rwc_UNKNOWN_MOD_LEVEL (A Valid Mod Level was not received from the PCA Eeprom. Configuring drive with Mouri BGA default values)"
            Case &H5825
                Return "rwc_PROMETHEUS_4_DEFAULT_SETUP_ERROR (Could not set Prometheus 4 to default values)"
            Case &H5826
                Return "rwc_WRITE_ELEM_RES_FAIL (A failure was detected during a Write Element Resistance test for Artemis)"
            Case &H5827
                Return "rwc_READ_ELEM_RES_FAIL (A failure was detected during a Read Element Resistance test for Apollo)"
            Case &H5828
                Return "rwc_WRITE_CHIP_PLL_LOCK_FAIL (Artemis did not achieve PLL lock. param 1 is Artemis (0=BumpA,1=BumpB,0xFF=No Artemis was specified) param2 is value of the artemis PLL register.)"
            Case &H5829
                Return "rwc_INCORRECT_CAL_TABLE_FOR_WRITE_CHIP (The cal table is not correct for the write driver chip used on this PCA)"
            Case &H5830
                Return "rwc_HYPERION_0_DEFAULT_SETUP_ERROR (Could not set Hyperion 0 to default values)"
            Case &H5831
                Return "rwc_HYPERION_1_DEFAULT_SETUP_ERROR (Could not set Hyperion 1 to default values)"
            Case &H5840
                Return "rwc_DIAGNOSTIC_SETUP_ERROR (Could not initialize the Diagnostic Data rwInitDiagnosticData )"
            Case &H5841
                Return "rwc_UNEXPECTED_DIRECTION_VALUE (Did not get either INTODRIVE or INTOCARTRIDGE)"
            Case &H5842
                Return "rwc_REPORT_CAL_TABLE_FW_REV_WRITE_CHIP (Report some relevent parameters at power on, param 1 Chip ID, param 2 Cal table revision, param 3 FW revision)"
            Case &H5850
                Return "rwc_DELILAH_AUTOCAL_FAILURE (Delilah failed to autocalibrate)"
            Case &H5851
                Return "rwc_GIDEON_AUTOCAL_FAIL (Gideon failed to autocalibrate- param1=GideonRegisters, param2=RegisterValue, param3=nothing)"
            Case &H5852
                Return "rwc_GIDEON_HT_RESET_FAIL (Gideon HyperTransport PLL failed to come out of reset- param1=GideonRegisters, param2=RegisterValue, param3=nothing)"
            Case &H5853
                Return "rwc_HT_SYNC_ERROR (HT Sync Failure- param1=bus, param2=count, param3=type)"
            Case &H5854
                Return "rwc_HT_PARITY_ERROR (HT Parity error- param1=bus, param2=count, param3=type)"
            Case &H5855
                Return "rwc_HT_UNEXPECTED_PARITY (HT Unexpected Parity state at power-on- param1=HT_PARITY_ERROR, param2=HT_PARITY_ERROR_COUNT_0, param3=HT_PARITY_ERROR_COUNT_1)"
            Case &H5856
                Return "rwc_NO_VALID_BLO_DAC_VALUES_FOUND (A Valid Blo Dac Gideon Register setting could not be determined in the manual calibration mode.)"
            Case &H5857
                Return "rwc_SET_ATS_FAILURE (The Mech Interface firmware returned a failure to RWC firmware when requested to set an ATS clock frequency for Gideon calibration.)"
            Case &H5858
                Return "rwc_RNG_MAX_SAMPLES_TAKEN (The Random Bit Sequence Generator failed to collect the required samples in the allowed time.)"
            Case &H5859
                Return "rwc_NO_VALID_BLO_DAC_VALUES_FOUND_ON_FIRST_CAL_ATTEMPT (A Valid Blo Dac Gideon Register setting could not be determined in the manual calibration mode, retrying the Gideon offset calibration.)"
            Case &H585A
                Return "rwc_HT_ALIGN_FAILED (HT_ALIGN_FAILED HT_CONTROL bits 30:28 not set to 1. Param_1 = bit 30 , Param_2 = bit 29, Param_3 = bit 28)"
            Case &H585B
                Return "rwc_HT_IN_UNEXPECTED_STATE (HT_IN_UNEXPECTED_STATE HT reg is not as expected should be enabled Param_1 = Value of HT reg 0x147, Param_2 = Ammount of HT parity errors)"
            Case &H585C
                Return "rwc_HT_TURNING_OFF_PARITY_CHECKING (HT_TURNING_OFF_PARITY_CHECKING Stop checking HT parity otherwise the FW will eventually lock up)"
            Case &H585D
                Return "rwc_HT_PARITY_ERROR_THRESHOLD_EXCEEDED (HT_PARITY_ERROR_THRESHOLD_EXCEEDED Have had too many HT parity errors)"
            Case &H5860
                Return "rwc_INCOMPATIBLE_TAPE_LOADED (Expected either a compatible data tape but got something else !?)"
            Case &H5861
                Return "rwc_INVALID_CAL_TABLE_CONTROL_WORD (In invalid control word was found while parsing the cal table to program up a ASIC. Param_1 = control word, Param_2 = an integer value unique to each ASIC. !?)"
            Case &H5870
                Return "rwc_FAILED_TO_DISABLE_AMUNDSENS_WEQ (Couldn't write to Amundsen's WEQ Control register)"
            Case &H5871
                Return "rwc_ILLEGAL_WEQ_SETUP_REQUESTED (Illegal WEQ setup requested)"
            Case &H5880
                Return "rwc_INVALID_EEPROM_CAL_TABLE_AREA (The EEPROM cal table area contains at least one invalid entry)"
            Case &H5881
                Return "rwc_INVALID_EEPROM_PER_DRIVE_CAL_TABLE (The Per Drive Cal Table in the EEPROM contains at least one invalid entry)"
            Case &H5882
                Return "rwc_DEPRECATED_EEPROM_PER_DRIVE_CAL_TABLE (The Per Drive Cal Table in the EEPROM contains a revision number that is less than the revision number found in the base cal table)"
            Case &H5890
                Return "rwc_ESTIMATE_BOOST"
            Case &H5891
                Return "rwc_ADJUST_BOOST"
            Case &H5892
                Return "rwc_BOOST_DATASETS"
            Case &H5893
                Return "rwc_RESET_AFIRS_ON_CHANNEL (A request to reset the Afirs on channel, param1, was made to RWC.)"
            Case &H5894
                Return "rwc_READ_WITH_WRITE_BUMP_ENABLED (The drive will read using the alternate, or secondary, or writing, bump)"
            Case &H5895
                Return "rwc_READ_WITH_WRITE_BUMP_DISABLED (The drive will return to using the primary bump for reading)"
            Case &H58A0
                Return "rwc_CALIBRATE_WRITE_FAILED (The Write ERT operation executed as part of the FFIR calibration during load failed to complete enough datasets. param_1 = datasets written, param_2 = datasets expected)"
            Case &H58A1
                Return "rwc_FFIR_CALIBRATE_STARTED (A channel ERT result has indicated that an FFIR tuning pass is required. param_1 = channel, param_2 = CCPsSentToC1, param_3 = C1CodewordErrors)"
            Case &H58A2
                Return "rwc_FFIR_CALIBRATE_FAILED (An FFIR calibration attempt at Load has failed. Bad status was returned from the ERT operation)"
            Case &H58A3
                Return "rwc_OVERWRITE_UNCORRECTABLE_DATASETS (Uncorrectable datasets reported in Overwrite Test. Params P1=1st attempt, P2=2/3 attempt, P3=Fwd=even/Rev=odd)"
            Case &H58A4
                Return "rwc_OVERWRITE_THRESHOLD_C2_DATASETS (Threshold C2 (max C2 needed to correct) datasets reported in an Overwrite Test. Params P1=1st attempt, P2=2/3 attempt, P3=Fwd=even/Rev=odd)"
            Case &H58A5
                Return "rwc_OVERWRITE_TEST_WRITE_FAILED (Write ERT executed in Overwrite Test failed to complete enough datasets. Params P1=datasets written, P2=datasets expected)"
            Case &H58A6
                Return "rwc_OVERWRITE_TEST_STARTED (Overwrite Test has started currently starts on first tape movement after tape load)"
            Case &H58A7
                Return "rwc_OVERWRITE_TEST_FAILED (Overwrite Test has failed. Bad status was returned from the ERT operation)"
            Case &H58A8
                Return "rwc_OVERWRITE_AVERAGE_C2 (The average C2 needed to correct datasets reported in Overwrite Test. Params P1=1st attempt, P2=2/3 attempt, P3=Fwd=even/Rev=odd)"
            Case &H58A9
                Return "rwc_OVERWRITE_TEST_MARGINAL (An Overwrite test has passed but is considered marginal)"
            Case &H58AA
                Return "rwc_VIWP_TEMP_FAULT_ENTRY_A (Params P1 = DAC6, P2 = DAC7, P3 = WCDSM)"
            Case &H58AB
                Return "rwc_VIWP_TEMP_FAULT_ENTRY_B (Params P1 = DAC6, P2 = DAC7, P3 = WCDSM)"
            Case &H58AC
                Return "rwc_VIWP_TEMP_FAULT_ENTRY_C (Params P1 = DAC6, P2 = DAC7, P3 = WCDSM)"
            Case &H58AD
                Return "rwc_OVERWRITE_TEST_PASSED (An Overwrite test has passed)"
            Case &H58BA
                Return "rwc_VIWP_VALUES (param1 = viwp_A, param2 = viwp_B)"
            Case &H58BB
                Return "rwc_VIWP_TEMP_CALCULATED_A (param1 = DAC6, param2 = DAC7, param3 = WCDSM)"
            Case &H58BC
                Return "rwc_VIWP_TEMP_CALCULATED_B (param1 = DAC6, param2 = DAC7, param3 = WCDSM)"
            Case &H58CA
                Return "rwc_RMATCH_VALUES (param1 = rmatch_A, param2 = rmatch_b)"
            Case &H58D8
                Return "rwc_MC_ADAPTION_UNEXPECTED_ADAPTION_MODE (param1 = MC adaption mode, param2 = expected adaption mode)"
            Case &H58D9
                Return "rwc_MC_ADAPTION_UNEXPECTED_CHANNEL (param1 = MC adapted channel, param2 = expected channel)"
            Case &H58DA
                Return "rwc_MC_ADAPTION_CHANNEL_RESULT (param1 = 0 Chan failed, 1 Chan no change, 2 Chan better, param2 = Pre SNR, param3 = Post SNR)"
            Case &H58DB
                Return "rwc_ADAPTION_STILL_IN_PROGRESS (RWC attempted to start adaption but it looks like adaption is already in progress, this should not happen)"
            Case &H58DC
                Return "rwc_WAITING_FOR_SHADOW_COPY_PPC_IN_IDLE (RWC Checking to see if shadow copy done when PPC is in an IDLE state, this should not happen)"
            Case &H58DE
                Return "rwc_ADAPTION_RESOURCE_COUNT_HIGHWATERMARK (RWC attempted to start adaption but it looks like adaption is already in progress - the resource count for this semaphore has reached a new highwatermark level)"
            Case &H58F0
                Return "rwc_SETUP_SERVO"
            Case &H58F1
                Return "rwc_SMUX"
            Case &H58F2
                Return "rwc_BEN"
            Case &H58FF
                Return "rwc_UNDEFINED_ERROR"
            Case &H6401
                Return "SA_GAGARIN_ASIC_REVISION_CHECK_FAILED (Gagarin ASIC revision check failed , Asic is not a Gagarin or Yuri)"
            Case &H6402
                Return "PROC_IDLE_TIME_LESS_THAN_30_PER_CENT (ProcessorIdle Time is < 30%)"
            Case &H6801
                Return "TI_ERR_HANDLER_CALLED (The Cmicro Error Handler function has been called (cm_ErrorHandler))"
            Case &H6802
                Return "TI_ERR_IMPLICIT_CONSUMPTION (An implicit signal consuption has occurred! )"
            Case &H6803
                Return "TI_ERR_OUT_OF_TIMER_STORAGE (No free SDL timer storage (in SDLTimerFreeList) to set new timer - increase MAX_SDL_TIMERS in TightInteg/cmTmr.c )"
            Case &H6C00
                Return "TLM_INIT_ERROR (TraceLogger Init failed! BMM returned No GOOD)"
            Case &H6C01
                Return "TLM_NO_MORE_LIVE_TRACE_ENTRIES (Not an error, just an indication we've reached the last Live Trace entry)"
            Case &H6C02
                Return "TLM_MAX_LIVE_TRACES_REACHED (The drive has insufficient resources to set more Live Traces)"
            Case &H6C03
                Return "TLM_FLUSH_PENDING (Indicates a flush has started and the initiator must wait to receive SDL signal diTraceLogFlushed)"
            Case &H6C04
                Return "TLM_RSP_PENDING (Indicates an operation has started but the caller must wait for the callback function to be executed)"
            Case &H6C05
                Return "TLM_SYSARM_HW_FLUSH_FAILED (Indicates that the TraceLogging SysARM HW Flush All blocking function timed out)"
            Case &H6C06
                Return "TLM_SERVOARM_HW_FLUSH_FAILED (Indicates that the TraceLogging ServoARM HW Flush Trace channel blocking function timed out)"
            Case &H7400
                Return "miNoError"
            Case &H7401
                Return "miAbortedCmdError"
            Case &H7402
                Return "miUnsupportedCmdError"
            Case &H7403
                Return "miWrongNumParamsError"
            Case &H7404
                Return "miInvalidParamError"
            Case &H7405
                Return "miCmdAlreadyInProgError"
            Case &H7406
                Return "miCmdNotAllowedNowError"
            Case &H7407
                Return "miCmdProcessingError"
            Case &H7408
                Return "miDspEventOccurred (A DSP event has occurred.  (Most likely an off-track event unless the default was changed.))"
            Case &H7409
                Return "miObsoleteCmdError"
            Case &H740A
                Return "miIncompleteMechInit"
            Case &H740B
                Return "miServoInterruptTimingFault"
            Case &H740C
                Return "miObsoleteOrUnsupportedMechType (Mech Type (SensorRev) specified in Mech EEPROM is either obsolete or unsupported.)"
            Case &H740D
                Return "miObsoleteCmd300C"
            Case &H740E
                Return "miObsoleteCmd300D"
            Case &H740F
                Return "miInvalidTaskError (Current Task for servo system is of unknown type.  Most likely caused by a firmware bug!)"
            Case &H7410
                Return "miLoadDiagCmdNotAllowedNow (Some condition persists that prohibits a Load)"
            Case &H7411
                Return "miUnloadDiagCmdNotAllowedNow (Some condition persists that prohibits an UnLoad)"
            Case &H7412
                Return "miShuttleCmdNotAllowedNow (Shuttle tape command can not be executed at this time.)"
            Case &H7413
                Return "miSetCartTypeCmdNotAllowedNow (Set Cartridge Type command can not be executed at this time.)"
            Case &H7414
                Return "miSetMechTypeCmdNotAllowedNow (Set Cartridge Type command can not be executed at this time.)"
            Case &H7415
                Return "miSetTensionCmdNotAllowedNow (Set Tension command can not be executed at this time.)"
            Case &H7416
                Return "miSetSpeedCmdNotAllowedNow (Set Speed command can not be executed at this time.)"
            Case &H7417
                Return "miAdjustSpeedCmdNotAllowedNow (Adjust Speed command can not be executed at this time.)"
            Case &H7418
                Return "miSetPositionCmdNotAllowedNow (Set Position command can not be executed at this time.)"
            Case &H7419
                Return "miCancelSetPosCmdNotAllowedNow (The Cancel Set Position command can not be executed at this time.  Most likely because there is no previous set position command active.)"
            Case &H741A
                Return "miSetPosAndSpeedCmdNotAllowedNow (Set Position and Speed command can not be executed at this time.)"
            Case &H741B
                Return "miCalServoCmdNotAllowedNow (Servo Calibration command can not be executed at this time.)"
            Case &H741C
                Return "miEndOfTapeCalCmdNotAllowedNow (End of Tape Servo Calibration command can not be executed at this time.)"
            Case &H741D
                Return "miServoInitCmdNotAllowedNow (Servo Initialization command can not be executed at this time.)"
            Case &H741E
                Return "miLoadCmdNotAllowedNow (Load Cartridge command can not be executed at this time.)"
            Case &H741F
                Return "miGrabCmdNotAllowedNow (Grab leader pin command can not be executed at this time.)"
            Case &H7420
                Return "miLoadAndGrabCmdNotAllowedNow (Load and Grab leader pin command can not be executed at this time.)"
            Case &H7421
                Return "miUngrabCmdNotAllowedNow (Ungrab leader pin command can not be executed at this time.)"
            Case &H7422
                Return "miUnloadCmdNotAllowedNow (Unload cartridge command can not be executed at this time.)"
            Case &H7423
                Return "miThreadCmdNotAllowedNow (Thread command can not be executed at this time.)"
            Case &H7424
                Return "miUnthreadCmdNotAllowedNow (Unthread command can not be executed at this time.)"
            Case &H7425
                Return "miRecoverTapeCmdNotAllowedNow (Recover Tape command can not be executed at this time.)"
            Case &H7426
                Return "miHeadCleanCmdNotAllowedNow (Head clean command can not be executed at this time.)"
            Case &H7427
                Return "miPowerOnCalsCmdNotAllowedNow (Power-on calibration command can not be executed at this time.)"
            Case &H7428
                Return "miSetNotifyCmdNotAllowedNow (Set Notify command can not be executed at this time.)"
            Case &H7429
                Return "miWaitUntilEventCmdNotAllowedNow (Wait Until Event command can not be executed at this time.  Most likely because tape is not moving.)"
            Case &H742A
                Return "miSetHeadPositionCmdNotAllowedNow (Set Head Position command can not be executed at this time.  Most likely because a servo calibration is in progress.)"
            Case &H742B
                Return "miSetTrackingOffsetCmdNotAllowedNow (Set tracking offset command can not be executed at this time.  Most likely because a servo calibration is in progress.)"
            Case &H742C
                Return "miDspLearnVIOffsetNotAllowed (A DSP command to learn the VI offset is not allowed now because tape is moving or another task is in progress. )"
            Case &H742D
                Return "miSetGenCmdNotAllowedNow (The Set Gen command is not allowed now. The format can not be changed at this time.)"
            Case &H742E
                Return "miForcedEjectCmdNotAllowedNow (The Forced Eject command is not allowed now.  Most likely another operation is in progress.)"
            Case &H742F
                Return "miUnstickTapeCmdNotAllowedNow (The command to try to unstick the tape from the head can not be executed at this time.)"
            Case &H7430
                Return "miIllegalSensorState (Sensors are in a state that indicate that the sensors or Callisto is not working correctly)"
            Case &H7431
                Return "miIllegalSensorSequenceOnLoad (Sensors are in a state that indicate that the sensors or Callisto is not working correctly on a Load)"
            Case &H7432
                Return "miIllegalSensorSequenceOnGrab (Sensors are in a state that indicate that the sensors or Callisto is not working correctly on a Grab)"
            Case &H7433
                Return "miIllegalSensorSequenceOnUngrab (Sensors are in a state that indicate that the sensors or Callisto is not working correctly on an UnGrab)"
            Case &H7434
                Return "miIllegalSensorSequenceOnUnload (Sensors are in a state that indicate that the sensors or Callisto is not working correctly on an UnLoad)"
            Case &H7435
                Return "miRDSensorTimeOut (The RD sensor stopped toggling, usually the loader mechanism is blocked or motor not working)"
            Case &H7436
                Return "miCartUnsafeToLoad (A Runaway condition of the FRM has been detected.  This is usually a broken tape.)"
            Case &H7437
                Return "miUnexpectedLPOnGrab (One of the LP sensors is asserted (and only one) at the beginning of the Grab.)"
            Case &H7438
                Return "miUnexpectedLPStartOfGrab (One of the LP sensors is asserted (and only one) at the beginning of the Grab. This is logged but not a failure.)"
            Case &H7439
                Return "miWriteProtectSensorNotInSpecifiedState (The write protect sensor does not match the expected state.)"
            Case &H743A
                Return "miCartPresentSensorNotInSpecifiedState (The Cartridge Present sensor does not match the expected state.)"
            Case &H743B
                Return "miRotateReelCmdNotAllowedNow (Rotate reel command can not be executed at this time.)"
            Case &H743C
                Return "miMeasureKtCmdNotAllowedNow (Measure Kt command can not be executed at this time.)"
            Case &H743D
                Return "miFRMFaultWhileRotating (The fault line on the FRM Driver asserted while the reel was rotating)"
            Case &H743E
                Return "miBRMFaultWhileRotating (The fault line on the BRM Driver asserted while the reel was rotating)"
            Case &H7440
                Return "miCallistoBusTestError"
            Case &H7441
                Return "miSetSpeedCmdNotAllowedNowTapeIsSnapped (Set Speed command can not be executed because the tape is snapped.)"
            Case &H7442
                Return "miSetSpeedCmdNotAllowedNowTapeIsUnthreaded (Set Speed command can not be executed because the tape is unthreaded.)"
            Case &H7443
                Return "miSetPositionCmdNotAllowedNowTapeIsSnapped (Set Position command can not be executed because the tape is snapped.)"
            Case &H7444
                Return "miSetPositionCmdNotAllowedNowTapeIsUnthreaded (Set Position command can not be executed because the tape is unthreaded.)"
            Case &H7445
                Return "miSetPosAndSpeedCmdNotAllowedNowTapeIsSnapped (Set Position and Speed command can not be executed because the tape is snapped.)"
            Case &H7446
                Return "miSetPosAndSpeedCmdNotAllowedNowTapeIsUnthreaded (Set Position and Speed command can not be executed because the tape is unthreaded.)"
            Case &H7447
                Return "miAdjustSpeedCmdNotAllowedNowTapeIsSnapped (Adjust speed command can not be executed because the tape is snapped.)"
            Case &H7448
                Return "miAdjustSpeedCmdNotAllowedNowTapeIsUnthreaded (Adjust speed command can not be executed because the tape is unthreaded.)"
            Case &H7449
                Return "miCalServoCmdNotAllowedNowTapeIsSnapped (Servo Cal command can not be executed because the tape is snapped.)"
            Case &H744A
                Return "miCalServoCmdNotAllowedNowTapeIsUnthreaded (Servo Cal command can not be executed because the tape is unthreaded.)"
            Case &H744F
                Return "miInTransitAfterInitNoCart"
            Case &H7450
                Return "miInTransitAfterInit"
            Case &H7451
                Return "miUnGrabAfterInit"
            Case &H7452
                Return "miUnknownAfterInit"
            Case &H7453
                Return "miTimedOutWaitingToSendCmdToGetDspHeadCleaningInfo"
            Case &H7454
                Return "miTimedOutWaitingForDspResponseWithHeadCleaningInfo"
            Case &H7455
                Return "miTimedOutWaitingToSetUpDspForHeadCleaningCmd"
            Case &H7456
                Return "miTimedOutWaitingForDspResponseToHeadCleaningSetup"
            Case &H7457
                Return "miTimedOutWaitingForDspToCompleteHeadCleaningCmd"
            Case &H7458
                Return "miHeadCleanEngageTimeOut"
            Case &H7459
                Return "miHeadCleanParkingTimeOut"
            Case &H745A
                Return "miHeadCleanCyclingTimeOut"
            Case &H745B
                Return "miIllegalSensorStateAfterInit (The Sensor states read at init are illegal.  Can't make sense of the results.)"
            Case &H745C
                Return "miTimedOutWaitingToRestoreDspAfterHeadCleaningCmd"
            Case &H745D
                Return "miTimedOutWaitingForDspDuringPostHeadCleanRestoration"
            Case &H745F
                Return "miUnableToSendDspCompensatorCoefs"
            Case &H7460
                Return "miOrionArmDownLoadError"
            Case &H7461
                Return "miInvalidDspOpCodeError"
            Case &H7462
                Return "miUnableToSendDspCmdError"
            Case &H7463
                Return "miUnableToSendDspSeekCmd"
            Case &H7464
                Return "miOrionFailedToCompleteSeekCmd (Command mailbox abandoned)"
            Case &H7465
                Return "miSendLongTermDspCmdTimeOut"
            Case &H7466
                Return "miSendShortTermOrionCmdTimeOut (Command mailbox abandoned)"
            Case &H7467
                Return "miDspLongTermCmdProtocolError"
            Case &H7468
                Return "miDspShortTermCmdProtocolError"
            Case &H7469
                Return "miTooManyParamsOnDspLongTermCmd"
            Case &H746A
                Return "miTooManyParamsOnDspShortTermCmd"
            Case &H746B
                Return "miTooManyResultsOnDspLongTermCmd"
            Case &H746C
                Return "miTooManyResultsOnDspShortTermCmd"
            Case &H746D
                Return "miLongTermDspCmdAlreadyInProg"
            Case &H746E
                Return "miShortTermDspCmdAlreadyInProg"
            Case &H746F
                Return "miLongTermDspCmdDoneButNoneInProg"
            Case &H7470
                Return "miShortTermDspCmdDoneButNoneInProg"
            Case &H7471
                Return "miUnableToSendDspLearnVIoffsetCmd"
            Case &H7472
                Return "miOrionFailedToCompleteLearnVIoffset (Command mailbox abandoned)"
            Case &H7473
                Return "miUnableToSendDspCalViCmd"
            Case &H7474
                Return "miOrionFailedToCompleteCalViCmd (Command mailbox abandoned)"
            Case &H7475
                Return "miTooManyDataPointsRequested"
            Case &H7476
                Return "miNoScopeDataAvailFromDsp"
            Case &H7477
                Return "miDspFailedToCompleteCmdDuringInitProcess"
            Case &H7478
                Return "miUnableToSendDspTuneParams"
            Case &H7479
                Return "miDspFailedToBootProperly"
            Case &H747A
                Return "miTimeOutOnSendOfClearDspFaultLog"
            Case &H747B
                Return "miTimeOutOnCompletionOfClearDspFaultLog"
            Case &H747C
                Return "miUnableToSendDspHeadCleanCmd"
            Case &H747D
                Return "miAbortCmdAlreadyInProg (An abort command was requested while one is already in progress.)"
            Case &H747E
                Return "miAbortCmdTimedOut (An abort command has timed-out while waiting for the tape to stop.)"
            Case &H747F
                Return "miAdjustSpeedCmdAlreadyInProg (An AdjustSpeed command was requested while one is already in progress.)"
            Case &H7480
                Return "miGeneralLoadFailure"
            Case &H7481
                Return "miLoadEPNotSeen (EP did not transition on a Load.  Most likely because no cartridge is present.)"
            Case &H7482
                Return "miLoadCDNotSeen (CD did not transition on a Load)"
            Case &H7483
                Return "miGrabCGNotSeen (CG did not transition on a Grab)"
            Case &H7484
                Return "miGrabLPNotSeen (LP did not transition on a Grab)"
            Case &H7485
                Return "miAbortedLoadRecovery (Too many retries to recover on a load or unload)"
            Case &H7486
                Return "miLoadRotCheckFailure (Cartridge not free to rotate when Cartridge Down.)"
            Case &H7487
                Return "miLoadRDTimeOutEP (The RD sensor stopped toggling while EP durring a Load)"
            Case &H7488
                Return "miLoadRDTimeOutIT (The RD sensor stopped toggling while IT during a Load)"
            Case &H7489
                Return "miLoadRDTimeOutCD (The RD sensor stopped toggling while CD during a Load)"
            Case &H748A
                Return "miGrabRDTimeOutCD (The RD sensor stopped toggling while CD during a Grab)"
            Case &H748B
                Return "miGrabRDTimeOutLP (The RD sensor stopped toggling while LP during a Grab)"
            Case &H748C
                Return "miInvokedTensionRampRetry"
            Case &H748D
                Return "miLoadFRMRunaway (Too much rotation was detected in the cartridge before it was threaded.)"
            Case &H748E
                Return "miCartNotLoaded (A Grab was requested, but the cartridge was not loaded)"
            Case &H748F
                Return "miUnexpectedGrabDuringLoad (Sensors indicate grabber unexpectedly moved into a grab position during a load operation.)"
            Case &H7490
                Return "miParkBackOff2 (While Parking the LP, we incremented the LM Voltage from .25V to .5V)"
            Case &H7491
                Return "miParkBackOff3 (While Parking the LP, we incremented the LM Voltage from .5V to .75V)"
            Case &H7492
                Return "miParkBackOff4 (While Parking the LP, we incremented the LM Voltage from .75V to 1.0V)"
            Case &H7493
                Return "miThreadBackOff2 (While Threading, we incremented the LM Voltage from .25V to .5V)"
            Case &H7494
                Return "miThreadBackOff3 (While Threading, we incremented the LM Voltage from .5V to .75V)"
            Case &H7495
                Return "miThreadBackOff4 (While Threading, we incremented the LM Voltage from .75V to 1.0V)"
            Case &H749A
                Return "miLoadFailedSuspectNoCartridgePresent (Load command failed and no CM was detected, therefore it is likely there is no cartridge present.)"
            Case &H74A0
                Return "miGeneralUnLoadFailure"
            Case &H74A1
                Return "miUnLoadEPNotSeen (EP did not transition while UnLoading)"
            Case &H74A2
                Return "miUnLoadCDNotSeen (CD did not transition while UnLoading)"
            Case &H74A3
                Return "miUnGrabCGNotSeen (CG did not transition while UnGrabbing)"
            Case &H74A4
                Return "miUnGrabLPNotSeen (LP did not transition while UnGrabbing)"
            Case &H74A6
                Return "miUnGrabRDTimeOutCG (RD stopped toggling while CG durring UnGrab)"
            Case &H74A7
                Return "miUnGrabRDTimeOutLP (RD stopped toggling while LP durring UnGrab)"
            Case &H74A8
                Return "miUnGrabRDTimeOutCD (RD stopped toggling while CD durring UnGrab)"
            Case &H74A9
                Return "miUnLoadRDTimeOutCD (RD stopped toggling while CD durring UnLoad)"
            Case &H74AA
                Return "miUnLoadRDTimeOutIT (RD stopped toggling while IT durring UnLoad)"
            Case &H74AC
                Return "miUnLoadFRMRunaway (Too much rotation of the cartridge was detected while tape was not threaded)"
            Case &H74AD
                Return "miLoadControlWatchDogTimerExpired (The load,unload,grab or ungrab operation timed out)"
            Case &H74C0
                Return "miTimeOutWhileDeslackingCartridge"
            Case &H74C1
                Return "miEmergencyStopError"
            Case &H74C2
                Return "miAlreadyPastTargetPosition"
            Case &H74C3
                Return "miSetSpeedTimeOutError"
            Case &H74C4
                Return "miTimeOutWaitingForAnLpos"
            Case &H74C5
                Return "miFatalReelFaultErrorCode"
            Case &H74C6
                Return "miSafetyLimitStopReached"
            Case &H74C7
                Return "miLposCalcWithInvalidLP0"
            Case &H74C8
                Return "miMissedTargetPosition"
            Case &H74C9
                Return "miPreviousTapeMotionCmdInProg"
            Case &H74CA
                Return "miSetSpeedTimeOutError2"
            Case &H74CB
                Return "miUnthreadingTimeOutError"
            Case &H74CC
                Return "miRemoveSlackProcessTimedOut"
            Case &H74CD
                Return "miLeaderMayHaveDisconnected"
            Case &H74CE
                Return "miTimeOutWaitingForRadiiEstimate"
            Case &H74CF
                Return "miRadiiEstimationProcessFailed"
            Case &H74D0
                Return "miRecoverTapeTimeOutError"
            Case &H74D1
                Return "miInvalidCartridgeType"
            Case &H74D2
                Return "miCalReelDvrOffsetTimeOut"
            Case &H74D3
                Return "miTimeOutWaitingForSpecifiedEvent"
            Case &H74D4
                Return "miFrontReelMotorHallSensorFault"
            Case &H74D5
                Return "miBackReelMotorHallSensorFault"
            Case &H74D6
                Return "miPrethreadingDeslackingTimeOut (Deslacking process did not complete.  Most likely due to back reel motor failing to rotate.)"
            Case &H74D7
                Return "miThreadingTimeOutWaitingForHalfMoonToPassFirstRoller (Threading time out while waiting for half moon to pass the first roller.)"
            Case &H74D8
                Return "miThreadingTimeOutWaitingForSpeedUpPosition (Timed out waiting for tape to reach position for speed-up during a thread.)"
            Case &H74D9
                Return "miThreadingTimeOutWaitingForPosToBeginStop (Time out while waiting for tape to reach the position to stop threading.)"
            Case &H74DA
                Return "miUnthreadCmdAbortedTapeMotion (An unthread command was issued and it aborted a tape motion operation that was already in progress.)"
            Case &H74DB
                Return "miCartridgeTypeNotSpecified (The cartridge type has not been specified either from LTO-CM or via a serial port command.)"
            Case &H74DC
                Return "miDspTapeSpeedSuspicious (The Tape speed reported by the DSP is significantly different than the tape speed indicated by the hall sensors.)"
            Case &H74DD
                Return "miSpecifiedTensionIsOutOfRange (Specified value of tension is either too high or too low.)"
            Case &H74DE
                Return "miTimedOutWaitingForPanicStopToComplete (Panic stop process did not complete properly.)"
            Case &H74DF
                Return "miSetTensionTimeOutError (Timed-out waiting for the proper tape tension to be established.)"
            Case &H74E0
                Return "miHeadSelectionTimeOut"
            Case &H74E1
                Return "miUnableToPositionHead"
            Case &H74E2
                Return "miSpeedTooLowForEnablingHeads"
            Case &H74E3
                Return "miSpeedTooLowForHeadServo"
            Case &H74E4
                Return "miSpeedTooLowForSensorCal"
            Case &H74E5
                Return "miSeekAttemptedWhileTapeStopped (DSP seek command was attempted while tape speed was zero.)"
            Case &H74E6
                Return "miSensorCalAttemptedWhileTapeStopped (DSP sensor cal command was attempted while tape speed was zero.)"
            Case &H74E7
                Return "miSpeedTooLowForVIOffset (The speed is too low to attempt a VI offset command in recover tape.  Most likely the tape is stuck to the head)"
            Case &H7500
                Return "miWriteFaultFirst"
            Case &H7501
                Return "miWriteFaultTooLongSinceValidLPOS (It has been too long since a valid LPOS was read, writing is not allowed.)"
            Case &H7502
                Return "miWriteFaultDspRecoveryInProgress (DSP tracking recovery operations are in progress, writing is not allowed.)"
            Case &H7503
                Return "miWriteFaultTapeStartUpIncomplete (Tape motion or DSP tracking start-up operations incomplete, writing is not allowed.)"
            Case &H7504
                Return "miWriteFaultDspNotTracking (DSP is not tracking properly on the tape, writing is not allowed.)"
            Case &H7505
                Return "miWriteFaultTapeSpeedTooLow (The current tape speed is too low, writing is not allowed.)"
            Case &H7508
                Return "miWriteFaultDSPIdle (Write is not allowed. DSP in Idle mode.)"
            Case &H7509
                Return "miWriteFaultDSPCalib"
            Case &H750A
                Return "miWriteFaultDSPVITrkFollow"
            Case &H750B
                Return "miWriteFaultDSPTapeOffTrack"
            Case &H750C
                Return "miWriteFaultDSPDemodChanOut"
            Case &H750D
                Return "miWriteFaultDSPSeek"
            Case &H750E
                Return "miWriteFaultDSPuPVI"
            Case &H750F
                Return "miWriteFaultWriteUnsafe"
            Case &H7510
                Return "miWriteFaultUnknown"
            Case &H7511
                Return "miWriteFaultMultiple"
            Case &H7512
                Return "miWriteFaultATSClockFrozen (Write is not allowed; ATS clock frequency clipped)"
            Case &H7513
                Return "miWriteFaultPOR (Write is not allowed; Power On or Reset occurred)"
            Case &H7514
                Return "miWriteFaultMicroJogApplied (Write is not allowed; Micro Jog is being applied)"
            Case &H7515
                Return "miWriteFaultOrionParityError (Write is not allowed; Orion TCM parity error detected)"
            Case &H7516
                Return "miWriteFaultLast (Write is not allowed; 12v power has been lost)"
            Case &H7517
                Return "miWriteFaultDSPTrackingOffsetApplied (Write is not allowed; Tracking offset is being applied)"
            Case &H7518
                Return "miWriteFaultTensionOffset (Write is not allowed because tension offset is being applied.)"
            Case &H7519
                Return "miWriteFaultBandIDVerification (Write is not allowed because servo bands could not be identified with GOOD status.)"
            Case &H75BB
                Return "miWriteFaultDSPStillOffTrack (Write cannot recommence while still offtrack)"
            Case &H7600
                Return "miSetSpeedCmdInvalidParam"
            Case &H7601
                Return "miAdjustSpeedCmdInvalidParam"
            Case &H7602
                Return "miSetPositionCmdInvalidParam"
            Case &H7603
                Return "miSetHeadPositionCmdInvalidParam"
            Case &H7604
                Return "miSetHeadTableCmdInvalidParam"
            Case &H7605
                Return "miOrionStatsCmdInvalidParam"
            Case &H7606
                Return "miGetServoFaultCmdInvalidParam"
            Case &H7607
                Return "miSetTrackingOffsetCmdInvalidParam"
            Case &H7608
                Return "miSetNotifyCmdInvalidParam"
            Case &H7609
                Return "miClearNotifyCmdNullHandle"
            Case &H760A
                Return "miAtsDiagCmdInvalidParam"
            Case &H760B
                Return "miHallCalCmdInvalidParam"
            Case &H760C
                Return "miRadiusCalCmdInvalidParam"
            Case &H760D
                Return "miWaitUntilEventCmdInvalidParam"
            Case &H760E
                Return "miConvertLposCmdInvalidParam"
            Case &H760F
                Return "miShuttleCmdInvalidParam"
            Case &H7610
                Return "miGetDspFaultLogInvalidParam"
            Case &H7611
                Return "miSetGenCmdInvalidParam"
            Case &H7612
                Return "miSetTestModeCmdInvalidParam (SetTestMode command parameter is out of range or is invalid.)"
            Case &H7613
                Return "miEepromServoTableCmdInvalidParam (Eeprom servo table command parameter is out of range or is invalid.)"
            Case &H7614
                Return "miRotateCmdInvalidParam"
            Case &H7615
                Return "miMeasureKtCmdInvalidParam"
            Case &H7616
                Return "miSleepRecursionFault (Sleep Recursion fault in MechInterface.  Most likely a firmware bug)"
            Case &H7620
                Return "miSetSpeedTimeOut2 (Set speed operation timed-out during a recover tape command.)"
            Case &H7621
                Return "miTimedOutWaitingForAlmostParked (Timed-out waiting for the pin sensors to indicate almost parked.)"
            Case &H7622
                Return "miTimedOutWaitingForFullParkIndication (Timed-out waiting for the pin sensors to indicate fully parked.)"
            Case &H7623
                Return "miUnthreadingTimeOutWaitingForFullTension (Unthread timed-out waiting for the full leader pin seating tension to be established.)"
            Case &H7624
                Return "miCyclingTensionDidNotParkPin (Cycling the pull-in tension did not achieve pin park.)"
            Case &H7625
                Return "miRethreadRetryDidNotParkPin (Rethreading and then unthreading again did not get the leader pin parked.)"
            Case &H7626
                Return "miTimedOutWaitingForAlmostParkedRetry (Timed-out during the rethread/re-unthread recovery while waiting for almost parked.)"
            Case &H7627
                Return "miUnableToParkLeaderPin (All recovery algorithms exhausted and still unable to park leader pin!)"
            Case &H7628
                Return "miRethreadingTimeOutWaitingForStop (Rethread recovery operation timed-out waiting for tape to come to a stop.)"
            Case &H7629
                Return "miRethreadingTimeOut2 (Rethreading time out while waiting for the tape to reach the required position.)"
            Case &H762A
                Return "miRethreadingDeslackingTimeOut (Deslacking process did not complete.  Most likely due to back reel motor failing to rotate.)"
            Case &H762B
                Return "miLeaderPinCameUnparked (Leader pin was parked as indicated by both LP sensors, but then came unparked.)"
            Case &H762C
                Return "miRecovTapeCompleteButOriginalCmdFailed (The tape has been recovered and leader pin parked, but the original operation failed and was abandoned.)"
            Case &H762D
                Return "miRethreadingTimeOut3 (Rethreading time out while waiting for the tape to reach the required position.)"
            Case &H762E
                Return "miRethreadingTimeOutWaitingForStop2 (Rethread recovery operation timed-out waiting for tape to come to a stop.)"
            Case &H762F
                Return "miSpecialUnthreadingRecoveryWasUsed (A special unthreading recovery operation was needed to un-jam the leader block.)"
            Case &H7630
                Return "miRethreadRetryDidNotRecover (An iteration of the special unthreading recovery operation did not succeed.)"
            Case &H7631
                Return "miStopTapeTimeOutError (The stop tape operation took longer than expected.)"
            Case &H7632
                Return "miPosErrorExceededLimit (Attempt to stop the tape while still too far away from the specified position.)"
            Case &H7633
                Return "miTapeStoppedByAbortCmd (An abort command was issued and it stopped the tape motion operation that was already in progress.)"
            Case &H7634
                Return "miTapeThicknessTooLarge (The tape thickness is too large to be handled properly by the servo system.)"
            Case &H7635
                Return "miAdjustSpeedTimeOutError (ATS speed change operation (AdjustSpeed) timed-out waiting for tape to reach target speed.)"
            Case &H7636
                Return "miAttemptToAdjustSpeedWhileTapeStopped (ATS speed change operation (AdjustSpeed) was attempted while the tape was not moving.)"
            Case &H7637
                Return "miPinNotParkedAfterCCWRotation (While trying to park the pin LP2 was seen, but LP1 was not seen after rotating the grabber CCW.)"
            Case &H7638
                Return "miThreadingTimeOutWaitingForHalfMoonSeating (Threading time out while waiting for the half moon to seat onto the back reel.)"
            Case &H7639
                Return "miTapeStoppedUnexpectedly (Tape motion apparently stopped while waiting for head positioning to complete.)"
            Case &H763A
                Return "miTimedOutDuringUnthreadRewind (Unthread timed-out during the initial rewind of tape back into the cartridge.)"
            Case &H763B
                Return "miTimedOutDuringSetPosAndSpeedOperation (Timed-out during operation to set tape position and speed.)"
            Case &H763C
                Return "miTimedOutDuringSetPositionOperation (Timed-out during operation to set tape position.)"
            Case &H763D
                Return "miTimedOutDuringRecoverTapeOperation (Timed-out during a RecoverTape operation.)"
            Case &H763E
                Return "miFrontReelStalledWhileDeslacking (Front reel did not rotate during tape slack removal process.)"
            Case &H763F
                Return "miThreadingTimeOutWaitingForLeaderBlockSeating (Timed out waiting for back reel to reach position where leader block is fully seated.)"
            Case &H7640
                Return "miThreadingTimeOutWaitingForReversal (Timed out waiting for back reel to rotate to the threading reversal point.)"
            Case &H7641
                Return "miThreadingTimeOutWaitingForReverseThreadedPos (Timed out waiting for back reel to rotate to the reverse threaded position.)"
            Case &H7642
                Return "miReverseThreadSetTensionTimeOutError (Timed out waiting for tension to be established during the reverse threading process.)"
            Case &H7643
                Return "miThreadingTimeOutWaitingForRevThreadTargetPos (Timed out waiting for the reverse threading target position.)"
            Case &H7644
                Return "miUnableToDetachLeaderBlock (Timed out waiting for leader block to detach during an unthread.)"
            Case &H7645
                Return "miReverseUnthreadSetTensionTimeOutError (Timed out waiting for tension to be established during the reverse unthreading process.)"
            Case &H7646
                Return "miReverseUnthreadSetSpeedTimeOutError (Timed out waiting for speed to be established during the reverse unthreading process.)"
            Case &H7647
                Return "miFatalReelFaultWhileStoppingTape (A fatal reel fault occurred during the time when the tape was being stopped.)"
            Case &H7648
                Return "miBackReelStalledWhileDeterminingThreadDirection (Back reel stalled and was not rotating when determining thread direction.)"
            Case &H7649
                Return "miDetermineThreadDirectionOperationTimedOut (An operation that determines thread direction timed out.)"
            Case &H764A
                Return "miUnableToMaintainSpeed_TapeIsMoving (Tape speed error integrator value is very large.  Tape is still moving.  This is a warning.)"
            Case &H764B
                Return "miTimedOutDuringUnthreadReversal (Timed out waiting for back reel to rotate to the unthreading reversal point.)"
            Case &H764C
                Return "miTimedOutWaitingForUnthreadReveralSlowDown (Timed out waiting for tape to reach unthreading reversal slow down point.)"
            Case &H764D
                Return "miTimedOutWaitingForUnthreadTurnAroundPoint (Timed out waiting for tape to reach unthreading turn around point.)"
            Case &H764E
                Return "miFrontReelRotatingWrongDirection (Front reel turning CCW but should be CW after going through critical point during recover tape process.)"
            Case &H764F
                Return "miStuckTapeDuringRadiusControlledRewind (Tape motion unexpectedly stopped during recover tape process of rewinding based on radius estimate.)"
            Case &H7650
                Return "miUnableToDetachLeaderBlockInRecovTapeWhenRevThreaded (Unable to detach leader block during recover tape operation when reverse threaded.)"
            Case &H7651
                Return "miUnableToDetachLeaderBlockInRecovTapeWhenForThreaded (Unable to detach leader block during recover tape operation when forward threaded.)"
            Case &H7652
                Return "miCartridgeIndentificationProcessFailed (Unable to indentify cartridge type during recover-tape operation.)"
            Case &H7653
                Return "miTapeThicknessInCmIsSuspect (The tape thickness reported by the CM looks suspicious.  It's too large or too small.)"
            Case &H7654
                Return "miInvalidTapeThicknessInCm (The tape thickness reported by the CM is invalid and can not be used.)"
            Case &H7655
                Return "miInvalidPackRadiusInCm (The full pack radius reported by the CM appears to be invalid and will not be used.)"
            Case &H7656
                Return "miInvalidTapeLengthInCm (The tape length reported by the CM appears to be invalid and will not be used.)"
            Case &H7657
                Return "miInvalidReelInertiaInCm (The empty reel inertia reported by the CM appears to be invalid and will not be used.)"
            Case &H7658
                Return "miInvalidReelRadiusInCm (The empty reel radius reported by the CM appears to be invalid and will not be used.)"
            Case &H7659
                Return "miFrontReelStalledWhileBackReelRotated (Front reel not rotating properly.  Possibly due to the tape sticking to the tape lifter.)"
            Case &H765A
                Return "miBestDirForRecoveryIsUnknown (Recovery process was unable to determine best direction to move tape.)"
            Case &H765B
                Return "miFrontReelDidNotReverseDirectionDuringThread (Front reel did not reverse direction after passing through critical region for threading reversal.)"
            Case &H765C
                Return "miBackReelStopOperationTimedOut (Back reel did stop as expected during a tape recovery process.)"
            Case &H765D
                Return "miBackReelPushedClockwiseDuringLeaderBlockDemate (WARNING- Leader block demate process resorted to pushing the back reel CW!)"
            Case &H765E
                Return "miRethreadingTimeOutDuringLeaderBlockDemateRetry (Rethreading position time out during leader block demate retry process.)"
            Case &H765F
                Return "miLeaderBlockDemateRetriesExhausted (Leader block demate retry process was unsuccessful.)"
            Case &H7660
                Return "miLeaderBlockYankWasNeeded (Indicates is was necessary to yank on the tape to detach the leader block.)"
            Case &H7661
                Return "miTimedOutWaitingForOverWrapPosition (Timed out waiting for the required number of layers to over wrap the leader block.)"
            Case &H7662
                Return "miTimedOutWaitingForUnwrapPosition (Timed out waiting for the tape to unwrap from the leader block during a reverse thread.)"
            Case &H7663
                Return "miTimedOutWaitingForReturnToLockInPosition (Timed out waiting for a return to the original lock in position during a reverse thread.)"
            Case &H7664
                Return "miResetDspTuningParams (DSP tuning params were reloaded into the DSP prior to a VI-sensor cal command.   (This is a recovery algorithm.))"
            Case &H7665
                Return "miServoTestModeENABLED (Indicates a special test mode has been enabled that will generate specific faults.)"
            Case &H7666
                Return "miServoTestModeDISABLED (Indicates the special test mode has now been DISABLED.  Normal operation will resume.)"
            Case &H7667
                Return "miFrontReelDidNotReverseDirAfterThreadingTensionRamp (Front reel is not running in reverse direction after the tension ramp after passing through critical region during a thread.)"
            Case &H7668
                Return "miFrontReelDidNotReverseDirDuringUnthreadStep1 (Front reel did not reverse direction after passing through critical region during an unthread.)"
            Case &H7669
                Return "miFrontReelDidNotReverseDirDuringUnthreadStep2 (Front reel did not reverse direction after passing through critical region during an unthread.)"
            Case &H766A
                Return "miTimedOutWaitingForThreadingApproachSpeed (Timed out waiting for speed to reduce while approaching the critical region during a thread.)"
            Case &H766B
                Return "miUsingSecondaryHeadSetForSensorCal (Recovery process is using a secondary head set to perform a Sensor Cal operation.)"
            Case &H766C
                Return "miUsingSecondaryHeadSetForAziCal (Recovery process is using a secondary head set to perform an azimuth cal operation.)"
            Case &H766D
                Return "miTooCloseToBotForSensorCal (ServoCal can not be done now because too close to beginning of tape.)"
            Case &H766E
                Return "miThreadingTimeOutWaitingForHalfMoonToApproachHead (Timed out waiting for half moon to approach head during a thread.)"
            Case &H766F
                Return "miUnThreadingTimeOutWaitingForHalfMoonToApproachHead (Timed out waiting for half moon to approach head during an unthread.)"
            Case &H7670
                Return "miThreadingTimeOutWaitingForHalfMoonToPassHead (Timed out waiting for half moon to pass head during a thread.)"
            Case &H7671
                Return "miUnThreadingTimeOutWaitingForHalfMoonToPassHead (Timed out waiting for half moon to pass head during an unthread.)"
            Case &H7672
                Return "miUnThreadingTimeOutWaitingForGuideEntry (Timed out waiting for half moon to enter guide during an unthread.)"
            Case &H7673
                Return "miServoTimingReferenceMismatch (Servo timing reference mismatch during timing reference calibration)"
            Case &H7674
                Return "miMissedTargetPositionByTooMuch (Set position and speed operation missed the target objectives by way too much. Probably due to DSP retries.)"
            Case &H7675
                Return "miTimedOutWaitingToDeactivateTapeLifter (Timed out waiting for the grabber to rotate enough to deactivate the tape lifter and lower the tape onto the head)"
            Case &H7676
                Return "miMechParamsNotUpdatedFromCM (CM read after threading, MechParams cannot be updated at this time)"
            Case &H7677
                Return "miMechSensorsNotInExpectedStateAfterLoad (Mech Sensors not in expected state after a load command.)"
            Case &H7678
                Return "miMechSensorsNotInExpectedStateAfterUnload (Mech Sensors not in expected state after an unload command.)"
            Case &H7679
                Return "miMechSensorsNotInExpectedStateAfterGrab (Mech Sensors not in expected state after a grab command.)"
            Case &H767A
                Return "miMechSensorsNotInExpectedStateAfterUngrab (Mech Sensors not in expected state after an ungrab command.)"
            Case &H767B
                Return "miPreloadingDeslackingTimeOut (Deslacking process did not complete.  Most likely due to back reel motor failing to rotate.)"
            Case &H767C
                Return "miFRMTensionRampTimeout (FRM tension ramp process did not complete.  Most likely due to excessive tape slippage in the cartridge.)"
            Case &H767D
                Return "miFRMRotationDetectedDuringUngrab (FRM rotation was unexpectedly detected while ungrabbing the cartridge leader pin)"
            Case &H767E
                Return "miLPOSMediaMfgStringCheckSumFlt (The calculated LPOS media manufacturers string checksum does not match what was read from the tape.)"
            Case &H767F
                Return "miLPAssertedPriorToLoading (The LP sensor was asserted prior to loading, possibly a stuck or faulty sensor)"
            Case &H7680
                Return "miRelockingDspOntoTape (The DSP was commanded to re-lock onto servo code.   (This is a recovery algorithm.))"
            Case &H7681
                Return "miRetryDspSeekCommand (A retry was necessary on a DSP seek command.   (This is a recovery algorithm.))"
            Case &H7682
                Return "miRetryDspSensorCalCommand (A retry was necessary on a DSP VI-sensor cal command.   (This is a recovery algorithm.))"
            Case &H7683
                Return "miRetryDspAziCalCommand (A retry was necessary on a DSP azimuth cal command.   (This is a recovery algorithm.))"
            Case &H7684
                Return "miRetryDspViOffsetCalCommand (A retry was necessary on a DSP command to learn the VI-offset.   (This is a recovery algorithm.))"
            Case &H7685
                Return "miThreadingTimeOutUnableToInitiateMotion (Unable to thread.  An ungrab/regrab/rethread recovery process will now be attempted.)"
            Case &H7686
                Return "miPinDetectFaultDuringThreading (Thread operation being retried because the pin detect sensor indicates parked when not parked.)"
            Case &H7687
                Return "miTimeOutWaitingForThreadToStop (Threading recovery could not get tape stopped in a reasonable time period.)"
            Case &H7688
                Return "miPrethreadingRegrabTimeOut (Timed out waiting for a regrab to complete prior to threading.)"
            Case &H7689
                Return "miReadMediaManufacturesStringTimeOut (Timed out trying to read the media manufacturers information.  Additional retries may still be possible.)"
            Case &H768A
                Return "miParkDetectionViaMotorStall (A faulty LP sensor has made it neccessary to detect pin parking via FRM stall.)"
            Case &H768B
                Return "miFalseParkingIndication (Attempt to park failed.  LP sensor most likely asserted when it should not have.)"
            Case &H768C
                Return "miSeekRecoveryNowUsingVIcal (A VI cal was necessary to recover a DSP seek command failure.   (This is a recovery algorithm.))"
            Case &H768D
                Return "miFRMRequiredRestart (The FRM Driver required a reset and was restarted)"
            Case &H768E
                Return "miBRMRequiredRestart (The BRM Driver required a reset and was restarted)"
            Case &H768F
                Return "miContinuityCheckFailedH (The reel motor flex continuity check failed, the line is stuck high)"
            Case &H7690
                Return "miContinuityCheckFailedL (The reel motor flex continuity check failed, the line is stuck low)"
            Case &H7691
                Return "miUnableToReadMediaManufacturesString (Unable to read the media manufacturers information from the LPOS bit stream.)"
            Case &H7692
                Return "miFullEjectLoadRetryInvoked (A full eject was invoked as part of a load fault recovery)"
            Case &H7693
                Return "miNudgedGrabberToHelpParkPin (Gently nudged grabber in order to help park leader pin. NOT a fault.  Info only.)"
            Case &H7694
                Return "miRegrabbingLeaderPinAfterNudgingGrabberOpen (Leader pin may have come ungrabbed.  Regrabbing pin.)"
            Case &H7695
                Return "miCartridgeLoadOnInitFailed (A cartridge load as part of init failed.)"
            Case &H7696
                Return "miStuckLPSensorRecoveryInProgress (A Special recovery to try to free a stuck LP sensor is in progress)"
            Case &H7697
                Return "miStuckLPSensorRecoveryFailed (The recovery effort to try to free a stuck LP sensor failed)"
            Case &H7698
                Return "miR2OverRotatedDuringLeaderBlockDemate (The BRM over rotated during leader block demate in recover tape. Info only)"
            Case &H7699
                Return "miFrontReelDacOffsetIsOutOfRange (The front reel motor DAC offset is too large or too small.)"
            Case &H769A
                Return "miBackReelDacOffsetIsOutOfRange (The back reel motor DAC offset is too large or too small.)"
            Case &H769B
                Return "miForcedEjectCommandInvoked (A Forced Eject command was invoked, THIS IS NOT A FAULT, INFO ONLY)"
            Case &H769C
                Return "miLpos1SanityCheckFailed (The LPOS lock-up process produced a value that does pass the basic sanity check.)"
            Case &H769D
                Return "miLpos2SanityCheckFailed (The LPOS lock-up process produced a value that does pass the basic sanity check.)"
            Case &H769E
                Return "miUnableToFindBestDirForRecovery (The REMOVING_SLACK operation timed out in the section where it is trying to determine the best direction for recovery)"
            Case &H769F
                Return "miRadiusEstimateSuspect (The radii estimation process in recover tape yielded a negative result )"
            Case &H76A0
                Return "miUnableToSendDspSetGenCmd"
            Case &H76A1
                Return "miDspFailedToCompleteSetGenCmd"
            Case &H76A2
                Return "miEmptyDspInterrupt"
            Case &H76A3
                Return "miTimeOutDuringReadDspFaultLog"
            Case &H76A4
                Return "miTimeOutDuringClearDspFaultLog"
            Case &H76A5
                Return "miUnableToSendDspWriteDataMemCmd"
            Case &H76A6
                Return "miDspFailedToCompleteWriteDataMemCmd"
            Case &H76A7
                Return "miUnableToSendDspSampleRateParams"
            Case &H76A8
                Return "miThreadingFault (A threading fault occurred.)"
            Case &H76A9
                Return "miUnthreadingFault (An unthreading fault occurred)"
            Case &H76AA
                Return "miUnableToSendDspPCADiagCmd"
            Case &H76AB
                Return "miDspFailedToCompletePCADiagCmd"
            Case &H76AC
                Return "miUnableToMoveTape (Unable to initiate tape motion. Tape is stuck!)"
            Case &H76AD
                Return "miTapeStillStuckAfterUsingTapeLifter (Unable to initiate tape motion even after using tape lifter. Tape is stuck!)"
            Case &H76AE
                Return "miTapeStillStuckAfterRelaxingTension (Unable to initiate tape motion even after relaxing hard pull. Tape is stuck!)"
            Case &H76AF
                Return "miUnableToMaintainSpeed_TapeIsStopped (Tape speed error integrator value is very large.  Tape is stuck and not moving.)"
            Case &H76B0
                Return "miTapeStillStuckAfterTryingOppositeDirection (Unable to initiate tape motion even after pulling the opposite direction. Tape is stuck!)"
            Case &H76B1
                Return "miTapeCameFreeAfterTryingOppositeDirection (Tape finally came free after pulling in the opposite direction.)"
            Case &H76B2
                Return "miUnableToReadLposForDetermingLP0 (Unable to read enough valid LPOS words from any servo head to determine LP0.)"
            Case &H76B3
                Return "miChangingSpeedAndOrHeadDuringServoCal (Switching to a different speed or different head as part of a Servo Cal retry and recovery process.)"
            Case &H76B4
                Return "miSimulatedDspFault (This fault code is for firmware debug only.  It simulates a DSP fault code.)"
            Case &H76B5
                Return "miRelaxedGrabberDuringThread (Relaxed grabber during thread to help pull leader pin out of cartridge.  NOT a fault.  Info only.)"
            Case &H76B6
                Return "miTooManyResultsOnOrionCmd (An Orion command returned more results than the maximum allowed)"
            Case &H76B7
                Return "miEmptyOrionInterrupt (The servo ASIC interrupted the system processor for no good reason. )"
            Case &H76B8
                Return "miLpos0SanityCheckFailed (The LPOS lock-up process produced a value that does pass the basic sanity check.)"
            Case &H76B9
                Return "miLpos3SanityCheckFailed (The LPOS lock-up process produced a value that does pass the basic sanity check.)"
            Case &H76BA
                Return "miRecoverTapeCompletedSuccessfully (RecoverTape Completed Successfully, NOT A FAULT, INFO ONLY)"
            Case &H76BB
                Return "miTooManyParamsOnOrionCmd (An Orion command was sent with more than the maximum allowed number of parameters)"
            Case &H76BC
                Return "miHardwareDetectedTapeOffTrack (Hardware detected offtrack, info only)"
            Case &H76BD
                Return "miUnexpectedOrionPowerOnReset (The servo ARM reported an unexpected reset.)"
            Case &H76BE
                Return "miUnableToSendOrionCmd (No Orion command mailboxes were available)"
            Case &H76BF
                Return "miWriteProtectSensorNotInSpecifiedHighState (The write protect sensor does not match the specified high state.)"
            Case &H76C0
                Return "miWriteProtectSensorNotInSpecifiedLowState (The write protect sensor does not match the specified low state.)"
            Case &H76C1
                Return "miUsingDefaultReelTuningValues (EEPROM values unavailable. Invalid table revision. Default servo tuning values are being used instead.)"
            Case &H76C2
                Return "miUsingDefaultHeadPosTuningValues (EEPROM values unavailable. Invalid table revision. Default values are being used instead.)"
            Case &H76C3
                Return "miChangePowerModeNotAllowedNow (The drive is not in the correct state to allow the power mode to be changed.)"
            Case &H76C4
                Return "miInvalidPowerModeParam (The power mode requested is invalid.)"
            Case &H76C5
                Return "miCoolingFanStalled (The cooling fan is stalled or running significantly below normal operating speed)"
            Case &H76C6
                Return "miCoolingFanWorkingAfterStall (The cooling fan was stalled but is now operating normally)"
            Case &H76C7
                Return "miServoArmITCMParityError (A servo ARM memory parity error was detected.  Drive must be reset!)"
            Case &H76C8
                Return "miServoArmDTCM1ParityError (A servo ARM memory parity error was detected.  Drive must be reset!)"
            Case &H76C9
                Return "miServoArmDTCM2ParityError (A servo ARM memory parity error was detected. Info Only )"
            Case &H76CA
                Return "miServoArmDTCM3ParityError (A servo ARM memory parity error was detected.  Drive must be reset!)"
            Case &H76CB
                Return "miOrionResetDueToServoArmParityError (MI resetting Orion. A fatal Servo ARM memory parity error was detected.)"
            Case &H76CC
                Return "miServoArmTimeoutFault (The Servo ARM did not respond within the alotted timeout, shutting down actuator motor.)"
            Case &H76CD
                Return "miLoadInhibitedReelDriverOffsetOutOfRange (The drive is LOAD inibited. Reel driver offset out of range.  Loading a tape would put it at risk!)"
            Case &H76CE
                Return "miHeadTurnoffTimeOutError (Deceleration to the head turn-off speed took longer than expected.)"
            Case &H76CF
                Return "miAdjustSpeedCmdTerminatedDueToAtsEnabled (An AdjustSpeed command was terminated early because ATS mode was enabled.)"
            Case &H76D0
                Return "miReelTuningParamsUnchanged (Change to reel tuning parameters not allowed now. Leaving them unchanged. Info Only)"
            Case &H76D1
                Return "miTapePositionDiscrepancyFault (Tape Position indicated via LPOS does not agree with Tape Position via Halls)"
            Case &H76D2
                Return "miSpeedDiscrepancyFault (The speeds indicated by the reels do not agree with each other)"
            Case &H76D3
                Return "miSpecialReelSpeedFault (Speed fault when reels are being controlled in special reel servo mode)"
            Case &H76D4
                Return "miAtsCommandNotAllowedNow (An ATS command cannot be executed at this time)"
            Case &H76D5
                Return "miDeslackingTimeOutAfterFrontReelStalled (Unable to pull in all the slack tape after the front reel stalled during a RecoverTape operation.)"
            Case &H76D6
                Return "miFrontReelStallRetriesExhausted (Front reel stalled and all retries have been exhausted.)"
            Case &H76D7
                Return "miTimedOutWhileConfirmingSlackTapeIsGone (Timed out while rotating back reel to confirm slack tape was removed.)"
            Case &H76D8
                Return "miR2StalledWhileConfirmingSlackTapeIsGone (Back reel stalled during process to confirm slack tape was removed.)"
            Case &H76D9
                Return "miFrontReelStallRecoveryWasSuccessful (Successfully recovered from when the front reel stalled.  This is not a new failure.  It is information only.)"
            Case &H76DA
                Return "miTapeLiftOperationStillInProgress (A new tape lifter operation was attempted before the previous tape lifter operation completed.)"
            Case &H76DB
                Return "miTapeLiftAttemptedPriorToCompletingTapeDrop (A tape lift operation was attempted while a previous tape drop operation was still in progress.)"
            Case &H76DC
                Return "miTapeDropAttemptedPriorToCompletingTapeLift (A tape drop operation was attempted while a previous tape lift operation was still in progress.)"
            Case &H76DD
                Return "miInvalidTapeLifterTargetState (Tape lifter target state is not valid. Operation can not be completed.)"
            Case &H76DE
                Return "miTimedOutWaitingForTapeLifterToDropTape (Tape lifter did not drop the tape onto the head within the alloted time.)"
            Case &H76DF
                Return "miTimedOutWaitingForTapeLifterToLiftTape (Tape lifter did not lift the tape off the head within the alloted time.)"
            Case &H76E0
                Return "miTapeLifterOperationAttemptedPriorToGrabMode (A tape lifter operation was requested prior to the mech mode being grabbed.)"
            Case &H76E1
                Return "miTapeLifterDiagCmdTimedOut (The tape lifter diag cmd timed out waiting for motion to complete.)"
            Case &H76E2
                Return "miTapeLifterTimeOutDuringLift (Timed out waiting for the tape lifter to lift the tape off the head.)"
            Case &H76E3
                Return "miTapeLifterTimeOutDuringDrop (Timed out waiting for the tape lifter to drop the tape onto the head.)"
            Case &H76E4
                Return "miCGNotSeenDuringPinParkingSweep (CG sensor not seen during pin parking grabber sweep - INFO ONLY)"
            Case &H76E5
                Return "miRetryingPinParkingSweep (Initial attempt to park the pin failed.  Retrying with larger grabber motion)"
            Case &H76E6
                Return "miReelDriverFaultDuringDriverCal (The fault line on the reel motor driver asserted during driver offset nulling)"
            Case &H76E7
                Return "miTapeDropDidNotCompleteInTime (The tape lifter did not drop the tape onto the head quickly enough.)"
            Case &H76E8
                Return "miReelMotorFlexCheckFailed (The reel motor flex connection is suspect, the dataset field in fault log indicates which lines are in error)"
            Case &H76E9
                Return "miTapeTiltCalibrationFailed (Tape tilt calibration failed.  Drive write inhibited)"
            Case &H76EA
                Return "miUsingDefaultServoCompensatorValues (EEPROM values unavailable. Default dsp servo compensator values are being used instead.)"
            Case &H76EB
                Return "miCoolingFanDisabled (Drive has cooled.  The cooling fan has been turned OFF!)"
            Case &H76EC
                Return "miCoolingFanEnabled (Drive is getting too hot.  The cooling fan has been turned ON!)"
            Case &H76ED
                Return "miResumingNormalTapeSpeeds (Drive has cooled.  Resuming normal tape speeds.)"
            Case &H76EE
                Return "miTemperatureRequiresSlowerSpeed (Drive is getting too hot.  Tape speed now being reduced to minimum!)"
            Case &H76EF
                Return "miAsicTemperaturesExceeded (ASIC temperatures are too high!  Operations must stop!  Cartridge must be ejected!)"
            Case &H76F0
                Return "miBufferAddressMustBeEven (The address into the main DRAM buffer must be on an even byte boundary.)"
            Case &H76F1
                Return "miBufferAddressOutOfRange (The address into the main DRAM buffer is outside the range allowed for the servo system to use.)"
            Case &H76F2
                Return "miInvalidScopeMode (The specified mode is not valid.)"
            Case &H76F3
                Return "miInvalidScopeChanSize (The specified scope channel bit width is not supported.)"
            Case &H76F4
                Return "miTriggerPosBeyondEndOfTrace (The specified scope trigger position is too large compared to the specified number of data packets in the trace.)"
            Case &H76F5
                Return "miBufferLengthMustBeEven (The length of the buffer must be larger than zero and an even number.)"
            Case &H76F6
                Return "miInvalidScopeSourceID (The specified source number is not valid.)"
            Case &H76F7
                Return "miInvalidScopeBufferFormat (The specified scope buffer format parameter is not valid.)"
            Case &H76F8
                Return "miDriverCalFactorOutOfBounds (A reel driver calibration factor is out-of-range.)"
            Case &H76F9
                Return "miStaticTorqLossFactorOutOfBounds (A reel driver calibration factor for static torque loss is out-of-range.)"
            Case &H76FA
                Return "miDynamicTorqLossFactorOutOfBounds (A reel driver calibration factor for dynamic torque loss is out-of-range.)"
            Case &H76FB
                Return "miUpperTemperatureLimitExceeded (The temperature is above the maximum limit.)"
            Case &H76FC
                Return "miLowerTemperatureLimitExceeded (The temperature is below the minimum limit.)"
            Case &H76FD
                Return "miUsingDefaultServoTuningValues (EEPROM values unavailable. Default servo tuning values are being used instead.)"
            Case &H76FE
                Return "miUnsafeToThreadCart (Previous fault conditions have made it unsafe to thread this cartridge.)"
            Case &H76FF
                Return "miTapePathTemperatureExceeded (Tape temperature is too high!  Operations must stop!  Cartridge must be ejected!)"
            Case &H7700
                Return "miStartOfDspErrorCodes (Base number for constructing DSP error codes.  This is not an actual error.)"
            Case &H7701
                Return "miDspFault_0x01 (DSP Fault- ProcResetFlt - TMS320 was just reset due either to hardware pin assertion or receipt of the Reset command.)"
            Case &H7702
                Return "miDspFault_0x02 (DSP Fault- CheckSumFlt - The DSP checksum failed after a hardware/software reset.)"
            Case &H7703
                Return "miDspFault_0x03 (DSP Fault- UnsupCommand - Unsupported command op code.)"
            Case &H7704
                Return "miDspFault_0x04 (DSP Fault- IllegComSeq - Illegal command sequence.)"
            Case &H7705
                Return "miDspFault_0x05 (DSP Fault- OperSysAlertFlt - AlertBit set when a seek or CalibrateVI command. Usually associated with recovery from a tracking fault.)"
            Case &H7706
                Return "miDspFault_0x06 (DSP Fault- InhTapeSrvFlt - The DSP was asked to do a tape seek when the uP said this was not a safe operation to do.)"
            Case &H7707
                Return "miDspFault_0x07 (DSP Fault- VIOffsetFlt - A seek or VI calibration command was issued but the mech has not learned the VI offset yet.)"
            Case &H7708
                Return "miDspFault_0x08 (DSP Fault- StrokeSizeFlt - The stroke measured by the VI sensor hardware was not large enough.)"
            Case &H7709
                Return "miDspFault_0x09 (DSP Fault- PwrAmpOffsetFlt - Excessive actuator power amp offset.)"
            Case &H770A
                Return "miDspFault_0x0A (DSP Fault- uPTimeoutFlt - Brutus uP timeout.)"
            Case &H770B
                Return "miDspFault_0x0B (DSP Fault- The tracking PLL in the servo front end did not lock up at power up. INFO ONLY fault log entry )"
            Case &H770C
                Return "miDspFault_0x0C (DSP Fault- IllegCommandFlt - The servo firmware was sent a command it could not interpret )"
            Case &H770D
                Return "miDspFault_0x0D (DSP Fault- TimerUnavailableFlt - No timers were available for use in the servo firmware)"
            Case &H770E
                Return "miDspFault_0x0E (DSP Fault- spare)"
            Case &H770F
                Return "miDspFault_0x0F (DSP Fault- spare)"
            Case &H7710
                Return "miDspFault_0x10 (DSP Fault- CalibMidBndFlt - Unable to find a top servo band.)"
            Case &H7711
                Return "miDspFault_0x11 (DSP Fault- CalibTrk0Flt - Unable to lock to track 0 on a top servo band.)"
            Case &H7712
                Return "miDspFault_0x12 (DSP Fault- CalibIDFlt - Unable to verify band ID on a top servo band.)"
            Case &H7713
                Return "miDspFault_0x13 (DSP Fault- spare)"
            Case &H7714
                Return "miDspFault_0x14 (DSP Fault- spare)"
            Case &H7715
                Return "miDspFault_0x15 (DSP Fault- spare)"
            Case &H7716
                Return "miDspFault_0x16 (DSP Fault- spare)"
            Case &H7717
                Return "miDspFault_0x17 (DSP Fault- spare)"
            Case &H7718
                Return "miDspFault_0x18 (DSP Fault- spare)"
            Case &H7719
                Return "miDspFault_0x19 (DSP Fault- spare)"
            Case &H771A
                Return "miDspFault_0x1A (DSP Fault- spare)"
            Case &H771B
                Return "miDspFault_0x1B (DSP Fault- spare)"
            Case &H771C
                Return "miDspFault_0x1C (DSP Fault- spare)"
            Case &H771D
                Return "miDspFault_0x1D (DSP Fault- spare)"
            Case &H771E
                Return "miDspFault_0x1E (DSP Fault- spare)"
            Case &H771F
                Return "miDspFault_0x1F (DSP Fault- spare)"
            Case &H7720
                Return "miDspFault_0x20 (DSP Fault- VITrackingFlt - The track follow loop could not stay at the desired set point.)"
            Case &H7721
                Return "miDspFault_0x21 (DSP Fault- spare)"
            Case &H7722
                Return "miDspFault_0x22 (DSP Fault- spare)"
            Case &H7723
                Return "miDspFault_0x23 (DSP Fault- spare)"
            Case &H7724
                Return "miDspFault_0x24 (DSP Fault- spare)"
            Case &H7725
                Return "miDspFault_0x25 (DSP Fault- spare)"
            Case &H7726
                Return "miDspFault_0x26 (DSP Fault- spare)"
            Case &H7727
                Return "miDspFault_0x27 (DSP Fault- spare)"
            Case &H7728
                Return "miDspFault_0x28 (DSP Fault- spare)"
            Case &H7729
                Return "miDspFault_0x29 (DSP Fault- spare)"
            Case &H772A
                Return "miDspFault_0x2A (DSP Fault- spare)"
            Case &H772B
                Return "miDspFault_0x2B (DSP Fault- spare)"
            Case &H772C
                Return "miDspFault_0x2C (DSP Fault- spare)"
            Case &H772D
                Return "miDspFault_0x2D (DSP Fault- spare)"
            Case &H772E
                Return "miDspFault_0x2E (DSP Fault- spare)"
            Case &H772F
                Return "miDspFault_0x2F (DSP Fault- spare)"
            Case &H7730
                Return "miDspFault_0x30 (DSP Fault- TapeTrackFlt - Couldn't stay locked to tape servo code.)"
            Case &H7731
                Return "miDspFault_0x31 (DSP Fault- spare)"
            Case &H7732
                Return "miDspFault_0x32 (DSP Fault- spare)"
            Case &H7733
                Return "miDspFault_0x33 (DSP Fault- spare)"
            Case &H7734
                Return "miDspFault_0x34 (DSP Fault- spare)"
            Case &H7735
                Return "miDspFault_0x35 (DSP Fault- spare)"
            Case &H7736
                Return "miDspFault_0x36 (DSP Fault- spare)"
            Case &H7737
                Return "miDspFault_0x37 (DSP Fault- spare)"
            Case &H7738
                Return "miDspFault_0x38 (DSP Fault- spare)"
            Case &H7739
                Return "miDspFault_0x39 (DSP Fault- spare)"
            Case &H773A
                Return "miDspFault_0x3A (DSP Fault- spare)"
            Case &H773B
                Return "miDspFault_0x3B (DSP Fault- spare)"
            Case &H773C
                Return "miDspFault_0x3C (DSP Fault- spare)"
            Case &H773D
                Return "miDspFault_0x3D (DSP Fault- spare)"
            Case &H773E
                Return "miDspFault_0x3E (DSP Fault- spare)"
            Case &H773F
                Return "miDspFault_0x3F (DSP Fault- spare)"
            Case &H7740
                Return "miDspFault_0x40 (DSP Fault- AccelFlt - No tape servo data during seek acceleration phase.)"
            Case &H7741
                Return "miDspFault_0x41 (DSP Fault- AccelTOFlt - Acceleration timeout fault.)"
            Case &H7742
                Return "miDspFault_0x42 (DSP Fault- spare)"
            Case &H7743
                Return "miDspFault_0x43 (DSP Fault- DecelFlt - No tape servo data during seek deceleration phase.)"
            Case &H7744
                Return "miDspFault_0x44 (DSP Fault- spare)"
            Case &H7745
                Return "miDspFault_0x45 (DSP Fault- spare)"
            Case &H7746
                Return "miDspFault_0x46 (DSP Fault- VIGrossSetlFlt - Seek failure during gross settle)"
            Case &H7747
                Return "miDspFault_0x47 (DSP Fault- TapeGrossSetlFlt - Seek failure during gross settle)"
            Case &H7748
                Return "miDspFault_0x48 (DSP Fault- spare)"
            Case &H7749
                Return "miDspFault_0x49 (DSP Fault- spare)"
            Case &H774A
                Return "miDspFault_0x4A (DSP Fault- spare)"
            Case &H774B
                Return "miDspFault_0x4B (DSP Fault- FineSetlFlt - No tape servo data during seek fine settle phase)"
            Case &H774C
                Return "miDspFault_0x4C (DSP Fault- AzimuthFlt - Too few samples to generate an azimuth correction)"
            Case &H774D
                Return "miDspFault_0x4D (DSP Fault- TimingRefFlt - Too few samples to generate a valid timing reference)"
            Case &H774E
                Return "miDspFault_0x4E (DSP Fault- spare)"
            Case &H774F
                Return "miDspFault_0x4F (DSP Fault- spare)"
            Case &H7750
                Return "miRudeAwakening (MI task was awakened for no apparent good reason)"
            Case &H7751
                Return "miOrionTraceFIFONotEmpty (Orion trace port FIFO not empty when preparing to hibernate)"
            Case &H7752
                Return "miHeadCleanRDTimeOutIT (The RD sensor stopped toggling while IT during a head clean)"
            Case &H7753
                Return "miFRMNotEnabled (The FRM Driver could not be enabled because the DAC's are disabled)"
            Case &H7754
                Return "miBRMNotEnabled (The BRM Driver could not be enabled because the DAC's are disabled)"
            Case &H7755
                Return "miTwelveVoltPowerFail (12v power has been lost)"
            Case &H7756
                Return "miCatastrophicServoFault (A catastrophic servo fault occurred)"
            Case &H7757
                Return "miAdjustTensionCmdNotAllowedNow (Adjust Tension command can not be executed at this time.)"
            Case &H7758
                Return "miExcessiveOfftracksBOT (Excessive offtracks at BOT, NOT a fault, info only"
            Case &H7759
                Return "miExcessiveOfftracksEOT (Excessive offtracks at EOT, NOT a fault, info only)"
            Case &H775A
                Return "miStickyTapeAlertOnRead (A tape alert was raised on a read)"
            Case &H775B
                Return "miStickyTapeAlertOnWrite (A tape alert was raised on a write)"
            Case &H775C
                Return "miStickyTapeAlertOnSpace (A tape alert was raised on a space)"
            Case &H77FF
                Return "miEndOfDspErrorCodes (Denotes end of MI error codes.  This is not an actual error.)"
            Case &H7800
                Return "EXH_UNRECOGNISED_EXCEPTION"
            Case &H7801
                Return "EXH_FATAL_ASSERT_CALLED (A Fatal Assert has been seen.  Parameter 1 shows the PC.  Parameter 2 the error code for the Assert)"
            Case &H7802
                Return "EXH_LOG_ASSERT_CALLED (A Logged Assert has been called.  Parameter 1 shows the PC. Parameter 2 shows the File ID, Parameter 3 shows the Line Number)"
            Case &H7803
                Return "EXH_ASSERT_CALLED (An Assert has been called.  Parameter 1 shows the PC.)"
            Case &H7830
                Return "Servo Acquire Err wo ACT"
            Case &H7836
                Return "Servo PES Acquire Error w ACT"
            Case &H7C01
                Return "GSPI_ERR_BUFOVRFL"
            Case &H7C02
                Return "GSPI_ERR_TIMEOUT"
            Case &H7C10
                Return "SPI_ERR_EEPROM_WRITE (EEPROM Write did not complete (still in progress))"
            Case &H7C11
                Return "SPI_ERR_FLASH_WRITE (FLASH Write did not complete)"
            Case &H7C12
                Return "SPI_ERR_FLASH_READ (FLASH Read did not complete)"
            Case &H7C13
                Return "SPI_ERR_EEPROM_WRITE_PROTECT (EEPROM Write Protect bits are set)"
            Case &H7C14
                Return "SPI_ERR_FLASH_VERIFY (EEPROM Write did not complete (still in progress))"
            Case &H8000
                Return "CM_ADDRESS_OUT_OF_LIMITS"
            Case &H8001
                Return "CM_SPI_WRITING_PROBLEMS"
            Case &H8002
                Return "CM_WRONG_NUMBER_OF_BITS_RETURNED"
            Case &H8003
                Return "CM_NACK_ERROR"
            Case &H8004
                Return "CM_UNRECOGNISED_DATA_RECEIVED"
            Case &H8005
                Return "CM_SPI_READING_PROBLEMS"
            Case &H8006
                Return "CM_PARITY_ERROR"
            Case &H8007
                Return "CM_COLLISION_ERROR"
            Case &H8008
                Return "CM_OVERFLOW_ERROR"
            Case &H8009
                Return "CM_UNDERFLOW_ERROR"
            Case &H800A
                Return "CM_OVERFLOW_ERROR_SENDING"
            Case &H800B
                Return "CM_NRBITS_ON_DATA_RCV_ERROR"
            Case &H800C
                Return "CM_IMPOSIBLE_ADDRESS_SITUATION"
            Case &H800D
                Return "CM_INVALID_CONFIG_NAME"
            Case &H800E
                Return "CM_INVALID_CONFIG_VALUE"
            Case &H800F
                Return "CM_CRC_ERROR"
            Case &H8010
                Return "CM_SERIAL_NUMBER_CHECK_FAILED"
            Case &H8011
                Return "CM_ERROR_BIT_SET"
            Case &H8012
                Return "CM_TYPE_OF_TRANSPONDER_NOT_RECOGNISED"
            Case &H8013
                Return "CM_RF_CHANNEL_ALREADY_OPENED"
            Case &H8014
                Return "CM_RF_CHANNEL_ALREADY_CLOSED"
            Case &H8015
                Return "CM_EOT_POLL_TO"
            Case &H8400
                Return "FL_NOT_YET_IMPLEMENTED (Log Not Yet Implemented)"
            Case &H8401
                Return "FL_NO_MORE_ENTRIES (No more entries to extract)"
            Case &H8402
                Return "FL_NV_NOT_IN_USE (Uninitialised NV Logs OR The log requested is a volatile Log only)"
            Case &H8403
                Return "FL_BAD_SPI_XFER (An SPIXferRequest Failed)"
            Case &H8404
                Return "FL_MISSED_LOG_ENTRIES (Some Fault entries were not placed in the log due to flushing occuring)"
            Case &H8405
                Return "FL_LOG_SIZE_MISMATCH (A sizeof operation of an entry is different to what the logging system thinks it is)"
            Case &H8406
                Return "FL_TAPE_LOADED_INFO (A tape has been loaded and the fault log entry shows the load count and cartridge serial number)"
            Case &H8407
                Return "FL_FLUSH_IN_PROGRESS (Requested operation cannot be performed at this time as a flush to NV is currently in progress)"
            Case &H8408
                Return "FL_UNEXPECTED_CALLBACK_TIMER (The quick flush callback timer was called - but with the incorrect timer ID)"
            Case &H8480
                Return "SS_LOGGING_IN_PROGRESS (A snapshot change is not allow now as a snapshot is currently in progress)"
            Case &H8481
                Return "SS_LOG_NOT_FOUND (A request to read a snapshot log has failed because the requested log number does not exist)"
            Case &H8482
                Return "SS_SNAPSHOT_NOT_ALLOWED_NOW (Snapshotting is not allow now as a one or more preconditions have not been passed)"
            Case &H8483
                Return "SS_LOG_NOT_BEING_READ (A request to stop reading a log found that the log was not being read in the first place)"
            Case &H8484
                Return "SS_TOO_MANY_COMMANDS (An attempt was made to insert too many cdbs for snapshotting.)"
            Case &H8485
                Return "SS_END_OF_COMMAND_SET (The end of the snapshot command set has been reached.)"
            Case &H8486
                Return "SS_COMMAND_SET_UPDATE_IN_PROGRESS (An attempt to access the log snapshot command set while it is being updated.)"
            Case &H8487
                Return "SS_DATA_PHASE_ERROR (A snapshot command requested more SCSI data (out) than was available!)"
            Case &H8488
                Return "SS_UNEXPECTED_PORT_IF_OP (SCSI Infrastructure requested a snapshot PortIF operation with an invalid/obsolete TaskPtr)"
            Case &H8489
                Return "SS_LOG_UNAVAILABLE_BEING_FILLED (The specified snapshot log is unavailable cos it's being filled/created.)"
            Case &H848A
                Return "SS_LOG_UNAVAILABLE_BEING_READ (The specified snapshot log is unavailable cos it's being read.)"
            Case &H848B
                Return "SS_LOG_UNAVAILABLE_UNEXPECTED_STATUS (The specified snapshot log is unavailable cos its status is unexpected.)"
            Case &H848C
                Return "SS_COMMAND_SET_IN_USE (The command set cannot changed while it is being read.)"
            Case &H848D
                Return "SS_ILLEGAL_LOGGING_STATUS (The data structure that contains status has an illegal value!)"
            Case &H848E
                Return "SS_MAX_LOG_LENGTH_ERR (The specified snapshot LogSize config is invalid (range error).)"
            Case &H848F
                Return "SS_ERR_NO_TASK_OBJECTS (No task objects available in Scsi Task Manager. This is a fatal error)"
            Case &H8490
                Return "SS_NO_FREE_SSTF_LOGS (The Snapshot log sub-system is unable to save a snapshot log to flash because it's full)"
            Case &H8491
                Return "SS_ERR_PROTECTED_STTF_LOGS (An operation was attempted that cannot be performed when there are protected STTF logs in flash)"
            Case &H8492
                Return "SS_STTF_CONFIG_IN_PROGRESS (Cannot create a snapshot or STTF log while a STTF Flash configuration is in progress.)"
            Case &H8493
                Return "SS_SNAPSHOT_LOGGING_ABORTED (Snapshot logging aborted; possibly because a FORCE SNAPSHOT command was aborted by the host.)"
            Case &H8494
                Return "SS_STTF_FLASH_WRITE_ERR (spi_write() failed writing STTF log to flash.)"
            Case &H8800
                Return "INF_OEV_EVENT_LIST_FULL (INF OnEvent- EventSet - event list full, event not set)"
            Case &H8801
                Return "INF_OEV_EVENT_NOT_FOUND (INF OnEvent- Event not found in list)"
            Case &H8802
                Return "INF_OEV_EVENT_LIST_IDX_OUT_OF_BOUNDS (INF OnEvent- EventGetIdx - Index out of bounds)"
            Case &H8803
                Return "INF_ARM_UNDEFINED_INSTRUCTION (INF ARM exception vector taken- Undefined instruction)"
            Case &H8804
                Return "INF_ARM_SOFTWARE_INTERRUPT (INF ARM exception vector taken- Software interrupt)"
            Case &H8805
                Return "INF_ARM_PREFETCH_ABORT (INF ARM exception vector taken- Prefetch abort)"
            Case &H8806
                Return "INF_ARM_DATA_ABORT (INF ARM exception vector taken- Data abort)"
            Case &H8807
                Return "INF_ECC_RECOVERABLE (INF ECC event- Recoverable ECC error detected)"
            Case &H8808
                Return "INF_ECC_UNRECOVERABLE (INF ECC event- Unrecoverable ECC error detected)"
            Case &H8809
                Return "INF_BOOT_FAILURE (INF Boot Failure- Firmware image failed to boot)"
            Case &H880A
                Return "INF_ACI_RESET_LINE_ACTIVATED (This status indicates the ACI_RST_L input on the ACI connector has been activated.)"
            Case &H880B
                Return "INF_ITCM_ECC_RECOVERABLE (INF ECC event- Recoverable ITCM ECC error detected)"
            Case &H880C
                Return "INF_DTCM_ECC_RECOVERABLE (INF ECC event- Recoverable DTCM ECC error detected)"
            Case &H880D
                Return "INF_ENTERING_DIAG_LITE (INF the System ARM is about to enter DiagLite)"
            Case &H880E
                Return "INF_TRNG_FAILURE_START_OF_INIT (INF the True Random Number Generator's init routine failed at the start)"
            Case &H880F
                Return "INF_TRNG_FAILURE_BIST_NEVER_COMPLETED (INF the True Random Number Generator's BIST never completed)"
            Case &H8810
                Return "INF_TRNG_FAILURE_BIST_SIG (INF the True Random Number Generator's BIST SIG never became non-zero)"
            Case &H8811
                Return "INF_TRNG_FAILURE_RESEED (INF the True Random Number Generator has failed to re-seed)"
            Case &H8812
                Return "INF_TRNG_FAILURE_CACHE_FILL (INF the True Random Number Generator has failed to fill its cache)"
            Case &H8813
                Return "INF_INTERRUPTS_ENABLED_WHEN_THEY_SHOULD_NOT_BE (INF RTKCritIntSectBeg() has detected that we are in a nested critical section but interrupts are enabled (they should not be))"
            Case &H8814
                Return "INF_FUG_COMPARE_FAILED (INF FUG Flash write and reads succeeded, but a subsequent compare failed)"
            Case &H8815
                Return "INF_TRNG_UNABLE_TO_RESET (INF the True Random Number Generator's init routine failed to reset)"
            Case &H8816
                Return "INF_TRNG_RNG_LOGIC_SELFTESTS_FAILED (INF the True Random Number Generator's logic self test failed)"
            Case &H8817
                Return "INF_TRNG_ENTROPY_SOURCE_LOGIC_SELFTESTS_FAILED (INF the True Random Number Generator's entropy source logic test failed)"
            Case &H8818
                Return "INF_TRNG_INIT_TIMEOUT_FAILED (INF the True Random Number Generator has timed out waiting for initialisation)"
            Case &H8819
                Return "INF_TRNG_CONTINUOUS_TEST_FAILED (INF the True Random Number Generator's continuous test has failed)"
            Case &H881A
                Return "INF_TRNG_NO_DATA_AVAILABLE (INF the True Random Number Generator has no data available to read)"
            Case &H881B
                Return "INF_SYS_WATCHDOG_TIMER_FIRED (INF The SysARM Watchdog Timer has fired - this indicates that the SysARM FW has locked up)"
            Case &H881C
                Return "INF_OS_REINITIALISE_MCC_EXTRA_WORKAROUND_CALLED (INF Whilst reinitialising the MCC task we found that the current P0 task was MCC, so reset this.  This is informational only)"
            Case &H881D
                Return "INF_BOOT_FAILURE_DETECTED (INF Previous boot attempt failed, see trace log for details)"
            Case &H881E
                Return "INF_BOOT_STATUS_FAILURE (INF Failed to read flash for bootloader version during POST boot status check)"
            Case &H881F
                Return "INF_BOOT_INFO_FAILURE (INF Failed to read flash for firmware version info during POST)"
            Case &H8820
                Return "INF_CURRENT_TASK_STACK_CORRUPT (INF Current task stask corrupt)"
            Case &H8821
                Return "INF_CURRENT_TASK_STACK_FULL (INF Current task stask full)"
            Case &H8822
                Return "INF_NEXT_TASK_STACK_CORRUPT (INF Next task stask corrupt)"
            Case &H8C00
                Return "CS_END_SECTION_NOT_BEGUN (CRSEndCritIntSect was ended without CRSBegIntSect)"
            Case &H8C01
                Return "CS_BEGIN_SECTION_INTS_OFF (CRSBegCritIntSect found ints already off)"
            Case &H9401
                Return "SI_ERR_UA_PWR_ON_RESET (Power On Rest UA)"
            Case &H9402
                Return "SI_ERR_UA_FIRMWARE_UPDATED (FW reboot after upgrade UA)"
            Case &H9403
                Return "SI_ERR_UA_SCSI_BUS_RESET (SCSI bus reset UA)"
            Case &H9404
                Return "SI_ERR_UA_BUS_DEVICE_RESET (BDR reset UA)"
            Case &H9405
                Return "SI_ERR_UA_DEVICE_INTERNAL_RESET (Soft Reset UA)"
            Case &H9406
                Return "SI_ERR_UA_TRANSCEIVERS_TO_SE"
            Case &H9407
                Return "SI_ERR_UA_TRANSCEIVERS_TO_LVD"
            Case &H9408
                Return "SI_ERR_UA_NEXUS_LOST"
            Case &H9409
                Return "SI_ERR_UA_MEDIA_CHANGED"
            Case &H940A
                Return "SI_ERR_UA_MODE_PARAMETER_CHANGED"
            Case &H940B
                Return "SI_ERR_UA_LOG_VALUES_CHANGED"
            Case &H940C
                Return "SI_ERR_UNSUPPORTED_TASK_MGMT_FUNC"
            Case &H940D
                Return "SI_LUN_HAS_TOO_MANY_TASK_OBJECTS"
            Case &H940E
                Return "SI_ERR_UNSUPPORTED_LUN"
            Case &H940F
                Return "SI_ERR_INVALID_FIELD_IN_CDB"
            Case &H9410
                Return "SI_ERR_UNSUPPORTED_OPCODE"
            Case &H9412
                Return "SI_ERR_UNSUPPORTED_CMD_HANDLER_REQUEST"
            Case &H9413
                Return "SI_ERR_UNAVAILABLE_OPCODE"
            Case &H9414
                Return "SI_ERR_NOT_FAST_ACI_CMD"
            Case &H9415
                Return "SI_RSP_PENDING"
            Case &H9416
                Return "SI_ERR_ABORTED"
            Case &H9417
                Return "SI_ERR_RESERVED"
            Case &H9418
                Return "SI_ERR_INVALID_GROUP_CODE"
            Case &H9419
                Return "SI_ERR_TRUNCATED_MODE_PAGE"
            Case &H941A
                Return "SI_ERR_INVALID_FIELD_MODE_DATA"
            Case &H941B
                Return "SI_ERR_SPACE_REC_BOT_ENCOUNTERED"
            Case &H941C
                Return "SI_ERR_SPACE_FM_BOT_ENCOUNTERED"
            Case &H941D
                Return "SI_ERR_FIRMWARE_BUG"
            Case &H941E
                Return "SI_ERR_ECHO_BUFFER_OVERWRITTEN"
            Case &H941F
                Return "SI_ERR_REPORT_DENSITY_MEDIA_NOT_PRESENT"
            Case &H9420
                Return "SI_ERR_OVERLAPPED_CMD"
            Case &H9421
                Return "SI_ERR_ERASE_OPERATION_IN_PROGRESS"
            Case &H9422
                Return "SI_ERR_LOCATE_OPERATION_IN_PROGRESS"
            Case &H9423
                Return "SI_ERR_REWIND_OPERATION_IN_PROGRESS"
            Case &H9424
                Return "SI_ERR_WRITE_INHIBIT_TAPEDR"
            Case &H9425
                Return "SI_ERR_UA_DEVICE_IDENTIFIER_CHANGED"
            Case &H9426
                Return "SI_ERR_TRUNCATED_LOG_DATA"
            Case &H9427
                Return "SI_ERR_NON_CLEARABLE_LOG_PAGE"
            Case &H9428
                Return "SI_ERR_INVALID_FIELD_LOG_DATA"
            Case &H9429
                Return "SI_FWD_TASK_TO_LIB (Not a reportable error code)"
            Case &H942A
                Return "SI_ERR_INVALID_SURROGATE_LUN"
            Case &H942B
                Return "SI_ERR_INVALID_SURROGATE_INQ_PAGE"
            Case &H942C
                Return "SI_ERR_TOO_MANY_SUR_SCSI_LUNS"
            Case &H942D
                Return "SI_ERR_INQ_CACHE_CORRUPTED"
            Case &H942E
                Return "SI_ERR_INQ_CACHE_FULL"
            Case &H942F
                Return "SI_ERR_INVALID_EXCHANGE_ID"
            Case &H9430
                Return "SI_ERR_NO_SENSE_DATA_PROVIDED"
            Case &H9431
                Return "SI_ERR_INVALID_SCSI_STATUS"
            Case &H9432
                Return "SI_ERR_UA_REPORTED_LUNS_DATA_HAS_CHANGED"
            Case &H9433
                Return "SI_ERR_PARAMETER_NOT_SUPPORTED_IN_CDB"
            Case &H9434
                Return "SI_ERR_LOAD_OPERATION_IN_PROGRESS"
            Case &H9435
                Return "SI_ERR_UNLOAD_OPERATION_IN_PROGRESS"
            Case &H9436
                Return "SI_ERR_EWEOM"
            Case &H9437
                Return "SI_ERR_INVALID_FIELD_WR_BUFFER_DESCRIPTOR"
            Case &H9438
                Return "SI_ERR_FAILURE_PREDITION_THRESHOLD_EXCEEDED_FALSE"
            Case &H9439
                Return "SI_ERR_FAILURE_PREDITION_THRESHOLD_EXCEEDED"
            Case &H943A
                Return "SI_ERR_ECHO_BUFFER_INVALID"
            Case &H943B
                Return "SI_ERR_LUN_NOT_CONFIGURED"
            Case &H943C
                Return "SI_ERR_INVALID_FIELD_PR_OUT_DATA"
            Case &H943D
                Return "SI_ERR_PR_OUT_PARAM_LIST_LENGTH_ERROR_IN_CDB"
            Case &H943E
                Return "SI_ERR_PR_OUT_TRUNCATED_DATA"
            Case &H943F
                Return "SI_ERR_UA_RESERVATIONS_RELEASED"
            Case &H9440
                Return "SI_ERR_UA_REGISTRATIONS_PREEMPTED"
            Case &H9441
                Return "SI_ERR_UA_RESERVATIONS_PREEMPTED"
            Case &H9442
                Return "SI_ERR_INVALID_RELEASE_PERSISTENT_RESERVATION"
            Case &H9443
                Return "SI_ERR_WORM_OVERWRITE_ATTEMPTED"
            Case &H9444
                Return "SI_ERR_WORM_CANNOT_ERASE"
            Case &H9445
                Return "SI_ERR_CLEANING_DRIVE"
            Case &H9446
                Return "SI_ERR_LOADING_MEDIA"
            Case &H9448
                Return "SI_ERR_UNLOADING_MEDIA"
            Case &H9449
                Return "SI_ERR_FW_UPGRADE_IN_PROGRESS"
            Case &H944A
                Return "SI_ERR_WRITE_PROTECT"
            Case &H944B
                Return "SI_ERR_WRITE_INHIBIT_BAD_CM"
            Case &H944C
                Return "SI_ERR_LOADED_UNKNOWN_CART (Unknown cartridge loaded but not threaded)"
            Case &H944D
                Return "SI_ERR_INIT_CMD_REQUIRED (Tape is loaded but not threaded, init comamnd is required)"
            Case &H944E
                Return "SI_ERR_MEDIA_NOT_PRESENT_MAM_ACCESSIBLE (MAM is accessible but cartridge is in load HOLD position - Not Ready)"
            Case &H944F
                Return "SI_ERR_LOADED_NOT_AVAILABLE (Tape is threaded but drive shows it as unloaded. DDT75213)"
            Case &H9450
                Return "SI_ERR_NO_MEDIA_LOADED"
            Case &H9451
                Return "SI_INVALID_PORT_ID (An invalid Port ID has been Logged In)"
            Case &H9452
                Return "SI_ERR_OFFLINE_OPERATION_IN_PROGRESS (RMC LU has been taken Offline by the ADC RMC Logical Unit Mode page)"
            Case &H9453
                Return "SI_ERR_WRITE_INHIBIT_WRONG_TAPE_GEN (E.g. Gen III product not allowed to write to Gen I media)"
            Case &H9454
                Return "SI_ERR_POSITION_PAST_BOM"
            Case &H9455
                Return "SI_ERR_NO_DEFAULT_WWN (Used by WWN Module to signify that there is no default WWN)"
            Case &H9456
                Return "SI_ERR_NO_CURRENT_VALUE_FOR_WWN (Used by WWN Module to signify that there is no current WWN)"
            Case &H9457
                Return "SI_ERR_WWN_NOT_CHANGED"
            Case &H9458
                Return "SI_ERR_NO_DEFAULT_VALUE_FOR_WWN (Used by WWN Module to signify that there is no default WWN)"
            Case &H9459
                Return "SI_ERR_PR_OUT_INSUFFICIENT_REGISTRATION_RESOURCES (Used in Fibre Channel when we have reached maximum number of WWNs)"
            Case &H945A
                Return "SI_ERR_LIB_CHECK_CONDITION_SURROGATE_CMD (Used in surrogate SCSI to cause siSetScsiStatus to set task Scsi status to 2)"
            Case &H945B
                Return "SI_ERR_WRITE_INHIBIT_SUSPECT_INTEGRITY"
            Case &H945C
                Return "SI_ERR_BUSY_CMD_DURING_LU_RESET (This commad should get BUSY'd during the reseting of the logical unit, DDT 1000205284)"
            Case &H945D
                Return "SI_OPERATION_COMPLETE"
            Case &H945E
                Return "SI_ERR_TOO_MANY_BRIDGE_LUNS (The maximum number of Bridge LUNs has been reached.)"
            Case &H945F
                Return "SI_SMC_UNIT_ATTENTION (Unit Attention condition has been detected for this initiator to the SMC LUN)"
            Case &H9460
                Return "SI_SMC_NOT_READY (The Remote SMC Device Server is in the NOT READY State)"
            Case &H9461
                Return "SI_ERR_MODE_SENSE_CACHE_CORRUPTED (The Bridging Mode Sense Cache has become corrupt due to a f/w defect)"
            Case &H9462
                Return "SI_ERR_MODE_SENSE_CACHE_FULL (The Bridging Mode Sense cache is full, caching operation failed!)"
            Case &H9463
                Return "SI_ADT_PORT_LOGGED_OUT (Unable to forward command to Automation Device as the ADT port is logged-out)"
            Case &H9464
                Return "SI_RESEND_SMC_SCSI_COMMAND (Used to instruct the Bridge Device Server to resend a SCSI command to the Remote SMC LUN)"
            Case &H9465
                Return "SI_ERR_INIT_CMD_REQUIRED_CLEANING_CART (Cleaning cartridge seated but not threaded)"
            Case &H9466
                Return "SI_ERR_INIT_CMD_REQUIRED_FWUPGRADE_CART (FW upgrade cartridge is seated but not threaded)"
            Case &H9467
                Return "SI_ERR_INVALID_DRIVE_SERIAL_NUMBER (Invalid value in supplied drive serial number)"
            Case &H9468
                Return "SI_ERR_WWN_ALREADY_IN_USE (siCheckWWN() has detected that the supplied WWN is already assigned)"
            Case &H9469
                Return "SI_ERR_TAGGED_OVERLAPPED_CMD"
            Case &H946A
                Return "SI_ERR_RETURN_GOOD_STATUS"
            Case &H946B
                Return "SI_LOCK_SMC_CACHE_FAILED (Internal return status indicating SMC cache wasn't locked)"
            Case &H946C
                Return "SI_ERR_SMC_CACHE_LOCKED (Reported if siSmcLockCache() is called when the cache isn't unlocked)"
            Case &H946D
                Return "SI_BRIDGE_READY_OPERATION_IN_PROGRESS (Bridging Manager is busy. It already has an open exchange with the remote SMC device server)"
            Case &H946E
                Return "SI_BRIDGE_NOT_READY_OPERATION_IN_PROGRESS (Bridging Manager is busy. It already has an open exchange with the remote SMC device server)"
            Case &H946F
                Return "SI_ERR_INVALID_VENDOR_ID"
            Case &H9470
                Return "SI_ERR_INVALID_PRODUCT_ID"
            Case &H9471
                Return "SI_ERR_MEDIUM_REMOVAL_PREVENTED"
            Case &H9472
                Return "SI_FWD_TASK_TO_DRIVE (Not a reportable error code)"
            Case &H9473
                Return "SI_ERR_LOGOUT_IN_BAD_STATE (Bridging Manager received notification of an ADT Port logout while awaiting TMF response to abort synchronising PAMR command)"
            Case &H9474
                Return "SI_ERR_NOT_RESERVED (If OIR bit set in Device Config mode page, then commands will return this if there is no (persistent) reservation)"
            Case &H9475
                Return "SI_ERR_INITIALISATION_IN_PROGRESS (Automation Controller is initialising drive)"
            Case &H9476
                Return "SI_ERR_INVALID_FIELD_IN_MEDIUM_ATTRIBUTE (Invalid field in SCSI Set Medium Attribute command parameter list [ADC-2])"
            Case &H9477
                Return "SI_ERR_TRUNCATED_MEDIUM_ATTRIBUTE (A medium attribute in a SCSI Set Medium Attribute command parameter list is truncated [ADC-2])"
            Case &H9478
                Return "SI_ERR_INVALID_FIELD_IN_LOG_DATA_CACHE (Invalid field in SCSI Set Log Data Cache command parameter list)"
            Case &H9479
                Return "SI_ERR_TRUNCATED_LOG_PAGE_DESCRIPTOR (A log page descriptor in a SCSI Set Log Data Cache command is truncated)"
            Case &H947A
                Return "SI_ERR_INVALID_FIELD_IN_PARAMETER_LIST (Invalid field in parameter list)"
            Case &H947B
                Return "SI_ERR_INVALID_FIELD_SP_OUT_DATA"
            Case &H947C
                Return "SI_ERR_SP_OUT_PARAM_LIST_LENGTH_ERROR_IN_CDB"
            Case &H947D
                Return "SI_ERR_SI_UA_DATA_ENCR_PARAMS_CHANGED_BY_ANOTHER_INIT (Encryption UA)"
            Case &H947E
                Return "SI_ERR_SI_UA_DATA_ENCR_PARAMS_CHANGED_BY_VEND_SPEC_EVENT (Encryption UA)"
            Case &H947F
                Return "SI_ERR_SP_KEY_INSTANCE_CTR_CHANGED"
            Case &H9480
                Return "SI_ERR_UNSUPPORTED_ENCRYPTION_CARTRIDGE (This cartridge cannot be used for encryption)"
            Case &H9481
                Return "SI_ERR_EN_FUG_DOWNLOAD_ALEADY_IN_PROGRESS (Another Initiator has started a download sequence)"
            Case &H9482
                Return "SI_ERR_EN_FUG_IMAGE_NOT_MOD4 (FUGImage needs to be mod4)"
            Case &H9483
                Return "SI_ERR_EN_FUG_HEAD_CS_FAILURE (FUGImage has bad CS on first chunk)"
            Case &H9484
                Return "SI_ERR_EN_FUG_REBOOT_STARTED (Reboot in progress)"
            Case &H9485
                Return "SI_ERR_BOOT_INFO_AREA_CORRUPT (The boot info area of the serial flash is corrupt. EnFUG command set will not work)"
            Case &H9486
                Return "SI_ERR_SET_SP_PARAM_FOR_LF_NO_KEY_AVAILABLE (siSPSetSecurityParametersForPipelineUse()- No key available but we are told to encrypt or decrypt)"
            Case &H9487
                Return "SI_ERR_SET_MGMT_URI_NOT_ALLOWED (SetManagementURI cmd is only allowed when dirve is not ready)"
            Case &H9488
                Return "SI_ERR_NOT_ENOUGH_SPACE_FOR_URI (Parameter list legnth specified too many URI descriptors. Used in SetManagementURI command)"
            Case &H9489
                Return "SI_ERR_REACHED_PPC_EOT (Logical EOT has been detected)"
            Case &H948A
                Return "SI_ERR_DECRYPT_KEY_FAIL_LIMIT_REACHED (Encryption- Exhaustive-search attack prevention)"
            Case &H948B
                Return "SI_ERR_TITAN_NV_HAT_COUNT_INVALID (siInitTitanAccessControls()- Stored HAT Count in NV is > SI_MAX_HAT_ENTRIES )"
            Case &H948C
                Return "SI_ERR_TITAN_NV_HAT_DEFAULT_PORT_INVALID (siInitTitanAccessControls()- Stored Default mapId for this port is invalid )"
            Case &H948D
                Return "SI_ERR_WRITE_CMD_ALLOC_LENGTH_ERROR_IN_CDB (If encryption mode is set to ENCRYPT, only up to 8MB record size is allowed)"
            Case &H948E
                Return "SI_ERR_TOO_MANY_STALLED_TASKS (Too many stalled auto mode commands already exist in the slow device server queue)"
            Case &H948F
                Return "SI_ERR_INVALID_FIXED_FORMAT_SENSE_DATA (This internal error is reported when the drive detects invalid sense data from the remote SMC device server)"
            Case &H9490
                Return "SI_ERR_CRYPTOGRAPHIC_KEY_UNAVAILABLE (Returned to host when KMS did not provide a key)"
            Case &H9491
                Return "SI_ERR_PARAM_LIST_LENGTH_ERROR_IN_CDB"
            Case &H9492
                Return "SI_ERR_TRUNCATED_SNAPSHOT_COMMAND_DESCRIPTOR (A command descriptor in the SET SNAPSHOT COMMAND SET parameter list was truncated)"
            Case &H9493
                Return "SI_ERR_TRUNCATED_SNAPSHOT_CONFIG_DESCRIPTOR (The configuration descriptor in the SET SNAPSHOT CONFIG parameter list was truncated)"
            Case &H9494
                Return "SI_ERR_SERVICE_BUFFER_SNAPSHOT_IN_PROGRESS (Failed to start Service Buffer snapshot one is already in progress!)"
            Case &H9495
                Return "SI_ERR_RSA_SIGNATURE_KAT_FAILED (SI RSA Signature Known Answer Test has failed)"
            Case &H9496
                Return "SI_ERR_DATA_ENCR_CONF_PREVENTED (Cannot set security parameters, the drive has been (externally) configured to not accept security parameters)"
            Case &H9497
                Return "SI_ERR_EXT_DATA_ENCR_CONTROL_ERROR (KMS reported it failed to obtain security parameters, the command cannot be executed without security parameters)"
            Case &H9498
                Return "SI_ERR_EXT_DATA_ENCR_KM_ACCESS_ERROR (KMS reported it failed to obtain security parameters, unrecoverable error)"
            Case &H9499
                Return "SI_ERR_EXT_DATA_ENCR_KM_ERROR (KMS reported it failed to obtain security parameters, error when trying to access key)"
            Case &H949A
                Return "SI_ERR_EXT_DATA_ENCR_KEY_NOT_FOUND (KMS reported it failed to obtain security parameters, key not found)"
            Case &H949B
                Return "SI_ERR_INCORRECT_DATA_ENCR_KEY (KMS reported it failed to obtain security parameters, incorrect key)"
            Case &H949D
                Return "SI_ERR_EXT_DATA_ENCR_CONTROL_TIMEOUT (Timer expired when waiting for security parameters from KMS)"
            Case &H949E
                Return "SI_ERR_UA_DATA_ENCR_CAPABILITIES_CHANGED (Encryption UA)"
            Case &H949F
                Return "SI_ERR_EXT_DATA_ENCR_RQST_NOT_AUTHORISED (KMS reported it failed to obtain security parameters, request not authorised (e.g. loaded tape is non-encryption capable))"
            Case &H94A0
                Return "SI_ERR_NOT_READY_SELFTEST_IN_PROGRESS (Drive is in a special Maintenance or Selftest mode)"
            Case &H94A1
                Return "SI_ERR_TRUNCATED_LIVE_TRACE_DATA (The parameter list of a SET LIVE TRACE POINTS/VARIABLES command was truncated)"
            Case &H94A2
                Return "SI_ERR_TRUNCATED_TIMESTAMP_DATA (The parameter data of a SET TIMESTAMP command was truncated)"
            Case &H94A3
                Return "SI_ERR_UA_TIMESTAMP_CHANGED (Timestamp changed UA)"
            Case &H94A4
                Return "SI_ERR_STTF_SPI_XFER_REQUEST (Failed to initiate an SPI transfer to read a STTF Log from flash memory)"
            Case &H94A5
                Return "SI_ERR_SERVICE_LOCATION_DESCRIPTOR_TRUNCATED (The parameter data of a SET SERVICE LOCATION command was truncated)"
            Case &H94A6
                Return "SI_ERR_PHYSICAL_PORT_NOT_IN_POINT_TO_POINT (Cannot enable NPIV port when the physical port is in Loop Mode)"
            Case &H94A7
                Return "SI_ERR_TRUNCATED_PREVENT_MEDIUM_REMOVAL_DATA (The parameter data of a SET PREVENT MEDIUM REMOVAL command was truncated)"
            Case &H94A8
                Return "SI_ERR_INSUFFICIENT_INITIATOR_RESOURCES (The device server doesn't have sufficient recourses to process all the initiators in the parameter list)"
            Case &H94A9
                Return "SI_ERR_SET_PMR_PROHIBITED_WHEN_SMC_ENABLED (SET PREVENT MEDIUM REMOVAL command not allowed when SMC LUN enabled!)"
            Case &H94AA
                Return "SI_ERR_SET_PMR_PROHIBITED_NO_NPIV_PORT (SET PREVENT MEDIUM REMOVAL command not allowed when an NPIV is not associated with the SMC Logical Unit)"
            Case &H94AB
                Return "SI_ERR_SET_LEGACY_RESERV_PROHIBITED_WHEN_SMC_ENABLED (SET LEGACY RESERVATION command not allowed when SMC LUN enabled!)"
            Case &H94AC
                Return "SI_ERR_SET_LEGACY_RESERV_PROHIBITED_NO_NPIV_PORT (SET LEGACY RESERVATION command not allowed when an NPIV is not associated with the SMC Logical Unit)"
            Case &H94AD
                Return "SI_ERR_TRUNCATED_LEGACY_RESERVATION_DATA (The parameter data of a SET LEGACY RESERVATIONS command was truncated)"
            Case &H94AE
                Return "SI_ERR_TRUNCATED_PERSISTENT_RESERVATION_DATA (The parameter data of a SET PERSISTENT RESERVATIONS command was truncated)"
            Case &H94AF
                Return "SI_ERR_INSUFFICIENT_RESERVATION_KEY_RESOURCES (The device server doesn't have sufficient recourses to process all the reservation keys in the parameter list)"
            Case &H94B0
                Return "SI_ERR_SA_CREATION_PARAMETER_VALUE_INVALID (Error detected in SP OUT cmd or its data either- when attempting to establish a CCS; or, after decryted and integrity checked)"
            Case &H94B1
                Return "SI_ERR_SA_CREATION_PARAMETER_VALUE_REJECTED (Error detected in SP OUT cmd Authentication step- SAI_AC or SAI_DS is wrong)"
            Case &H94B2
                Return "SI_ERR_SA_AUTHENTICATION_FAILED (Error detected in SP OUT cmd Authentication step- Authentication failed)"
            Case &H94B3
                Return "SI_ERR_CONFLICTING_SA_CREATION_RQST (A host tried to initiate a new SA whilst another one in progress)"
            Case &H94B4
                Return "SI_ERR_SA_CREATION_IN_PROGRESS (The creation of a SA is in progress, command not allowed)"
            Case &H94B5
                Return "SI_ERR_EN_FUG_IMAGE_TOO_LARGE (FUGImage too large for flash)"
            Case &H94B6
                Return "SI_ERR_BRIDGING_PORT_NOT_REGISTERED (The bridging manager has attempted to send a SCSI command to the remote SMC LUN before the bridging port has been registered/configured!)"
            Case &H94B7
                Return "SI_ERR_SA_CA_FUNCTION_RETURNED_BAD_LENGTH (A Cryptographic Algorithm function returned an unexpected length)"
            Case &H94B8
                Return "SI_ERR_TRUNCATED_BEACON_LED_CONFIG_DATA (The Set Beacon LED Config parameter list is truncated)"
            Case &H94B9
                Return "SI_ERR_NO_TASK_OBJECTS (No task objects available in Scsi Task Manager. This is a fatal error)"
            Case &H94BA
                Return "SI_ERR_TRUNCATED_IP_CONFIG_DATA (The Set IP Config parameter list is truncated)"
            Case &H94BB
                Return "SI_ERR_SA_CE_FUNCTION_RETURNED_BAD_LENGTH (A Certificate Manager function returned an unexpected length)"
            Case &H94BC
                Return "SI_ERR_SC_CE_FUNCTION_RETURNED_BAD_LENGTH (A Certificate Manager function returned an unexpected length on a Security Configuration command)"
            Case &H94BD
                Return "SI_ERR_SC_CA_FUNCTION_RETURNED_BAD_LENGTH (A Cryptographic Algorithm function returned an unexpected length on a Security Configuration command)"
            Case &H94BE
                Return "SI_ERR_SC_MANAGEMENT_HOST_CERT_NOT_PRESENT (The SPOUT signature could not be authenticated because the Management Host Certificate public key is not present)"
            Case &H94BF
                Return "SI_ERR_TRUNCATED_ADVANCED_PRIMARY_PORT_DESCRIPTOR (An Advanced Primary Port Descriptor is truncated)"
            Case &H94C0
                Return "SI_ERR_NO_MEDIA_LOADED_CART_PRESENT (A cartridge is not loaded but it has been detected in the jaws of the drive)"
            Case &H94C1
                Return "SI_ERR_TRUNCATED_AUTOMATION_ATTRIBUTE_DESCRIPTOR (An Automation Device Attribute Descriptor header was truncated)"
            Case &H94C2
                Return "SI_ERR_TRUNCATED_AUTOMATION_DEVICE_ATTRIBUTE_DATA (The Automation Device Attribute value was truncated)"
            Case &H94C3
                Return "SI_ERR_TRUNCATED_AER_CONTROL_DATA (The AER Control Descriptor was truncated)"
            Case &H94C4
                Return "SI_ERR_SYMBOLIC_NAME_NOT_CHANGED (Status indicating that the specified Symbolic Name is the same as the current symbolic name )"
            Case &H94C5
                Return "SI_ERR_TRUNCATED_ADVANCED_PORT_SETTINGS_DATA (The SET ADVANCED PORT SETTINGS parameter list was truncated)"
            Case &H94C6
                Return "SI_ERR_SYMBOLIC_NAME_TOO_LONG (The specified Symbolic Name exceeds maximum permitted length!)"
            Case &H94C7
                Return "SI_ERR_SYMBOLIC_NAME_TERMINATION (The specified symbolic name is not null terminated!)"
            Case &H94C8
                Return "SI_ERR_DUPLICATE_LIVE_TRACE_POINT_ID (The specified Live Trace point has been specified multiple times in the parameter list!)"
            Case &H94C9
                Return "SI_ERR_INVALID_LIVE_TRACE_POINT_ID (The specified Live Trace point Id is invalid, it must be 0000h)"
            Case &H94CA
                Return "SI_ERR_FIRMWARE_FILE_TOO_BIG (The firmware file sent is too big)"
            Case &H94CB
                Return "SI_ERR_FUG_TAPE_CONTAINS_DATA (Firmware upgrade tape contains data)"
            Case &H94CC
                Return "SI_ERR_FUG_TAPE_FLASH_READ_FAILURE (Can't read flash for firmware upgrade tape)"
            Case &H94CD
                Return "SI_FUG_INCORRECT_TAPE_FORMAT (Incompatible tape format encountered while creating firmware upgrade tape)"
            Case &H94CE
                Return "SI_ERR_FORMAT_MEDIUM_OPERATION_IN_PROGRESS"
            Case &H94CF
                Return "SI_ERR_CANNOT_FORMAT_INCOMPATIBLE_MEDIUM (The cartridge does not support formatting (partitioning))"
            Case &H94D0
                Return "SI_ERR_SC_ROOT_CA_CERT_NOT_PRESENT (The SPOUT signature could not be authenticated because the Root CA Certificate is not present)"
            Case &H94D1
                Return "SI_ERR_SYMBOLIC_PORT_NAME_MISMATCH (In Data Path Failover the symbolic Names of Port A & B must be the same, they're not)"
            Case &H94D2
                Return "SI_ERR_CLEAR_RSA_KEY_REJECTED (Attempt to clear RSA Key pair not allowed, SECURE MODE disabled )"
            Case &H94D3
                Return "SI_ERR_UA_FAILOVER_SESSION_RELEASED (A failover session was released by an intiator that did not own it)"
            Case &H94D4
                Return "SI_ERR_ROOT_CA_CERTIFICATE_REJECTED_NOT_A_CA_CERTIFICATE (The certificate is not a Root CA certificate)"
            Case &H94D5
                Return "SI_ERR_UA_SECURITY_CONFIGURATION_CHANGED (Security configuration changed UA)"
            Case &H94D6
                Return "SI_ERR_SC_DEVICE_CERTIFICATE_PRESENT (Attempt to erase root CA certificate when drive certificate is present)"
            Case &H94D7
                Return "SI_ERR_WRITE_PROTECTED_BY_LIBRARY (The WP bit in the RMC Logical Unit mode page 0Eh/03h is set to 1, i.e. the library has write protected the media!)"
            Case &H94D8
                Return "SI_ERR_SEQUENTIAL_POSITIONING_ERROR (The logical position supplied in the ALLOW OVERWRITE CDB does not match current logical position)"
            Case &H94D9
                Return "SI_ERR_ILLEGAL_CMD_NOT_APPEND_ONLY_MODE (The ALLOW OVERWRITE CDB is not allowed if Append-Only mode is not enabled)"
            Case &H94DA
                Return "SI_ERR_OPERATOR_SELECTED_WRITE_PROTECT (Append-Only mode is enabled but the Allow Overwrite variable is not set to 'Format')"
            Case &H94DB
                Return "SI_ERR_IDLE_CONDITION_ACTIVATED_BY_TIMER (When the drive is in idle (hibernate mode), the REQUEST SENSE data will reflect this state)"
            Case &H94DC
                Return "SI_ERR_FAILOVER_SESSION_SEQUENCE_ERROR (The drive received a failover restricted command from an I_T_L nexus not associated with a failover session)"
            Case &H94DD
                Return "SI_ERR_FAILOVER_COMMAND_SEQUENCE_ERROR (The drive received a command that did not have the expect Failover Sequence Count in the CDB Control byte)"
            Case &H94DE
                Return "SI_ERR_DUPLICATE_FAILOVER_SESSION_KEY (A failover session has already been established using the specified failover session key)"
            Case &H94DF
                Return "SI_ERR_INVALID_FAILOVER_KEY (No failover session has been established with the specified failover session key)"
            Case &H94E0
                Return "SI_ERR_TRUNCATED_PATH_FAILOVER_PARAMETER_DATA (The SET PATH FAILOVER LICENCE KEY parameter list was truncated)"
            Case &H94E1
                Return "SI_ERR_NO_FREE_FAILOVER_SESSION_AVAILABLE (All failover sessions have been allocated)"
            Case &H94E2
                Return "SI_ERR_FIPS_SELFTEST_FAILURE (One or more OpenSSL FIPS selftests failed when SME set to 1 by SPOut command)"
            Case &H94E3
                Return "SI_ERR_INVALID_INTERNAL_LUN (Invalid internal LUN number)"
            Case &H94E4
                Return "SI_ERR_ROLE_BASED_AUTHENTICATION_FAILED (Role-based authentication of primary port I_T nexus failed)"
            Case &H94E5
                Return "SI_ERR_UA_FAILOVER_SMC_STATE_CHANGED (The failover session I_T_L nexus settings have changed)"
            Case &H94E6
                Return "SI_ERR_UA_FAILOVER_SMC_DEVICE_SERVER_MOVED (The media changer control path has moved to a different drive)"
            Case &H94E7
                Return "SI_ERR_SC_AUTHENTICATION_REQUEST_FAILED (Security Configuration Protocol Authentication Request failed)"
            Case &H94E8
                Return "SI_ERR_SC_DEVICE_CERT_DRIVE_KEY_MISMATCH (The device certificate does not contain this drive's public key)"
            Case &H94E9
                Return "SI_ERR_SC_CLIENT_CA_NAME_SAME_AS_ROOT_CA_NAME (The name of the client CA certificate is the same as the root CA name)"
            Case &H94EA
                Return "SI_ERR_SC_PUBLIC_KEY_ALREADY_IN_WHITELIST (The public key is already in the whitelist and must be deleted before being replaced)"
            Case &H94EB
                Return "SI_ERR_UA_MEDIUM_REMOVAL_PREVENTION_PREEMPTED (An initiators Prevent Medium Removal has been preempted by another initiator)"
            Case &H94EC
                Return "SI_ERR_WRONG_PARTITION_REPORT_EOD (Failover return NCN command tried to reposition to a partition that doesn't exist)"
            Case &H94ED
                Return "SI_ERR_INVALID_RSA_KEY (An RSA key is corrupted)"
            Case &H94EE
                Return "SI_ERR_UNKNOWN_SIGNATURE_VERIFICATION_KEY (Whitelist does not contain the required public key)"
            Case &H94EF
                Return "SI_ERR_UNABLE_TO_DECRYPT_DATA (An error occurred while decrypting data)"
            Case &H94F0
                Return "SI_ERR_CRYPTOGRAPHIC_INTEGRITY_VALIDATION_FAILURE (An error occurred while performing a signature verification)"
            Case &H94F1
                Return "SI_ERR_TOO_MANY_LOGICAL_OBJECTS_CANNOT_SUPPORT_OP (The command cannot return info requested because the logical position cannot be represented)"
            Case &H94F2
                Return "SI_ERR_TRUNCATED_STTF_CONFIG_DATA (The Set STTF Config parameter list is truncated)"
            Case &H94F3
                Return "SI_ERR_TRUNCATED_DRIVE_HEALTH_DATA (The SET DRIVE HEALTH RULES parameter list is truncated)"
            Case &H94F4
                Return "SI_ERR_TOO_MANY_RULES_TO_PROCESS (The device server has insufficient buffer space to process the number of rules specified in the parameter list)"
            Case &H94F5
                Return "SI_DRIVE_HEALTH_ALGORITHM_STATE_CHANGE (Reported to Drive Monitor when the Drive Health Algorithm changes Tape Alert flags & front panel LEDs)"
            Case &H94F6
                Return "SI_ERR_NOT_READY_HOST_SPECIFIED_SENSE_DATA (The SET PRIMARY PORT TO NOT READY command has put the primary port into a NOT READY state and has specified the sense data to report)"
            Case &H94F7
                Return "SI_ERR_NO_SECONDARY_IMAGE (Attempt to delete non-existant firmware image)"
            Case &H94F8
                Return "SI_ERR_PARAMETER_LIST_LENGTH_TOO_LONG (The parameter list length of a SCSI command exceeds the size of the device server's buffer)"
            Case &H94F9
                Return "SI_ERR_SEND_TMF_COMPLETE_BAD_TASK (The ADT Encapsulated SCSI transport layer sent SDL signal siSendTaskManagementRsp with a suspicious TaskPtr)"
            Case &H94FA
                Return "SI_ERR_SEND_CDB_COMPLETE_BAD_TASK (The ADT Encapsulated SCSI transport layer sent SDL signal siSendCdbRsp with a suspicious TaskPtr)"
            Case &H94FB
                Return "SI_ERR_DELETE_FIRMWARE_IMAGE_FAILED (Attempting to delete the secondary firmware image failed)"
            Case &H94FC
                Return "SI_ERR_READ_FIRMWARE_IMAGE_CACHE_FAILED (Attempting to read the firmware cache failed)"
            Case &H94FD
                Return "SI_ERR_VERIFY_OPERATION_IN_PROGRESS (The drive is not ready because it is currently performing a VERIFY operation)"
            Case &H94FE
                Return "SI_ERR_SET_EDV_BAD_SME_IS_SET (Set EDV command failed because Secure Mode is enabled (FIPS-related))"
            Case &H94FF
                Return "SI_ERR_FW_DEFECT_INVALID_DEVICE_SERVER (Firmware Defect return A SCSI function has been called specifying an invalid device server Id)"
            Case &H9801
                Return "AD_QUEUE_FULL (Resource Issue- The ADI failed to queue an object because the queue is full.)"
            Case &H9802
                Return "AD_QUEUE_EMPTY (The ADI failed to get a queue item because the queue was empty.)"
            Case &H9803
                Return "AD_FCB_ALLOCATION_ERROR (Resource Issue- The ADI was unable to allocate a new Frame Control Block)"
            Case &H9804
                Return "AD_MALLOC_ERROR (Resource Issue- The ADI doesn't have sufficient memory to complete the current operation.)"
            Case &H9805
                Return "AD_INVALID_QUEUE (F/W defect- An invalid queue was was referenced)"
            Case &H9806
                Return "AD_EXCHANGE_ALLOCATION_ERROR (Resource Issue- Unable to allocate a new exchange ID, all Exchange IDs are in use.)"
            Case &H9807
                Return "AD_INVALID_STATE_MACHINE_EVENT (F/W defect- F/W generated an invalid state-machine event.)"
            Case &H9808
                Return "AD_INVALID_STATE_MACHINE (F/W defect- F/W referenced an invalid f/w state-machine.)"
            Case &H9809
                Return "AD_INVALID_FCB (F/W defect- F/W referenced an invalid FCB Handle.)"
            Case &H980A
                Return "AD_NO_TASK_OBJECT (Unable to get a new task object.)"
            Case &H980B
                Return "AD_EXCHANGE_ID_TABLE_FULL (The SCSI Exchange ID table is full.)"
            Case &H980C
                Return "AD_INVALID_EXCHANGE (A new exchange has been started with an ID of an existing exchange.)"
            Case &H980D
                Return "AD_INVALID_FIELD_IN_SCSI_CMD_IU (The ADT Port I/F received a SCSI COMMAND IU containing an invalid field.)"
            Case &H980E
                Return "AD_INVALID_FIELD_IN_SCSI_DATA_IU (The ADT Port I/F received a SCSI DATA IU containing an invalid field.)"
            Case &H980F
                Return "AD_INVALID_FIELD_IN_SCSI_RESPONSE_IU (The ADT Port I/F received a SCSI RESPONSE IU containing an invalid field.)"
            Case &H9810
                Return "AD_INVALID_CONTEXT (The ADT Port I/F received a SCSI operation from a Device Server with an invalid Context ID.)"
            Case &H9811
                Return "AD_PAYLOAD_EXCEEDS_MAX (Unable to generate Frame because it exceeds Max Payload Size.)"
            Case &H9812
                Return "AD_MORE_DATA_THAN_EXPECTED (The ADT Port I/F received more SCSI data than permited within a burst.)"
            Case &H9813
                Return "AD_INCORRECT_RELATIVE_OFFSET (The ADI Port I/F has received a Data IU with an offset outside the current burst.)"
            Case &H9814
                Return "AD_UNSUPPORTED_OPERATION (The ADT Port I/F received a command from a Device Server that it cannot support.)"
            Case &H9815
                Return "AD_TRANSMISSION_ERROR (The ADT Port did not receive an ACK for a frame it transmitted.)"
            Case &H9816
                Return "AD_TASK_QUEUE_FULL (Temp need to replace with SI_ERR_TASK_QUEUE_FULL when available)"
            Case &H9817
                Return "AD_SELFTEST_FCB_MALLOC_FAILED (SelfTest FAILED- Unexpected FCBs allocated.)"
            Case &H9818
                Return "AD_SELFTEST_EXCHANGES_FAILED (SelfTest FAILED- Unexpected exchange open.)"
            Case &H9819
                Return "AD_SELFTEST_QUEUES_FAILED (SelfTest FAILED- Queue not empty.)"
            Case &H981A
                Return "AD_SELFTEST_SCSI_STATE_FAILED (SelfTest FAILED- SCSI Exchange not in State IDLE.)"
            Case &H981B
                Return "AD_EXCESSIVE_FRAMING_ERRORS (The ADT port has detected an excessive number of framing errors.)"
            Case &H981C
                Return "AD_SELFTEST_NON_ZERO_ACK_OFFSET (SelfTest FAILED- Non zero lib ACK offset.)"
            Case &H981D
                Return "AD_DATA_OFFSET_ERROR_IN_TRANSFER_READY (The received TRANSFER READY IU contained an invalid BUFFER OFFSET field.)"
            Case &H981E
                Return "AD_UNEXPECTED_ACK (ADT Port received an ACK frame but not for frame it was expecting.)"
            Case &H981F
                Return "AD_UNEXPECTED_NAK (ADT Port received an NAK frame but not for frame it was expecting.)"
            Case &H9820
                Return "AD_GIVING_UP_INITIATING_LOGIN (The ADT Port is giving up initiating a Port Login after a number of failed attempts.)"
            Case &H9821
                Return "AD_RECEIVED_LATE_ACK (The ADT Port is discarding an ACK frame it received after the acknowledgement timeout.)"
            Case &H9822
                Return "AD_ACKNOWLEDGEMENT_TIMEOUT (The ADT Port did not receive acknowledgement within the Acknowledgement Period.)"
            Case &H9823
                Return "AD_CHECKSUM_ERROR (The ADT Port received a frame with a checksum error.)"
            Case &H9824
                Return "AD_PORT_DISABLED (The ADT Port received a frame when the port was disabled.)"
            Case &H9825
                Return "AD_INFORMATION_UNIT_TOO_SHORT (The received TRANSFER READY IU was shorter than expected.)"
            Case &H9826
                Return "AD_INFORMATION_UNIT_TOO_LONG (The received TRANSFER READY IU was longer than expected.)"
            Case &H9827
                Return "AD_ZERO_BURST_LENGTH_TRANSFER_READY (The received TRANSFER READY IU contained a BURST LENGTH value of zero.)"
            Case &H9828
                Return "AD_ABORTED_CMD_PORT_LOGGED_OUT (The ADT Port failed to execute a SCSI operation because it's logged-out.)"
            Case &H9829
                Return "AD_ABORTED_CMD_BUSY (The ADT Port failed to execute a SCSI operation because it's busy doing something else.)"
            Case &H982A
                Return "AD_BRIDGE_SCSI_CMD_ABORTED (The ADT Port aborted bridged SCSI command due to a communication failure.)"
            Case &H982B
                Return "AD_UNSUPPORTED_TASK_MGMT_FUNC (This error reflects the Response Code in a SCSI RESPONSE IU received by the drive.)"
            Case &H982C
                Return "AD_INVALID_SCSI_RESPONSE_CODE (This error reflects the Response Code in a SCSI RESPONSE IU received by the drive.)"
            Case &H982D
                Return "AD_INVALID_FIELD_IN_SCSI_IU (This error reflects the Response Code in a SCSI RESPONSE IU received by the drive.)"
            Case &H982E
                Return "AD_MORE_DATA_TRANSFERRED_THAN_REQUESTED (This error reflects the Response Code in a SCSI RESPONSE IU received by the drive.)"
            Case &H982F
                Return "AD_TASK_MANAGEMENT_FUNC_FAILED (This error reflects the Response Code in a SCSI RESPONSE IU received by the drive.)"
            Case &H9830
                Return "AD_SERVICE_DELIVERY_FAILED (This error reflects the Response Code in a SCSI RESPONSE IU received by the drive.)"
            Case &H9831
                Return "AD_INVALID_LUN_IN_TASK_MANAGEMENT_IU (This error reflects the Response Code in a SCSI RESPONSE IU received by the drive.)"
            Case &H9832
                Return "AD_FAILED_TO_TRANSMIT_SCSI_CMD_OR_TMF (The ADT Port failed to transmit a SCSI COMMAND IU or TASK MANAGEMENT FUNC IU.)"
            Case &H9833
                Return "AD_ZERO_LENGTH_SCSI_DATA_IU (The ADT Port received a zero length SCSI DATA IU.)"
            Case &H9834
                Return "AD_DATA_IU_RECEIVED_UNEXPECTEDLY (The ADI Port I/F has received a Data IU unexpectedly.)"
            Case &H9835
                Return "AD_NULL_FRAME_POINTER (Unexpected NULL frame pointer.)"
            Case &H9836
                Return "AD_AUTOMATION_DEVICE_FW_DEFECT (This error indicates the drive has detected a library firmware defect!)"
            Case &H9837
                Return "AD_INVALID_ADT_PORT (The specified Port is not a recognised ADT port)"
            Case &H9838
                Return "AD_COMPLETE_COMMAND_WITH_UNIT_ATTENTION (Instructs Bridging Manager NOT to resend a command with a UNIT ATTENTION Sense Key)"
            Case &H9839
                Return "AD_RSP_PENDING (This status indicates a function shall send a response when it has finished)"
            Case &H983A
                Return "AD_DTD_FRAME_NAKED (Reported in ADI/AMI Command History log to indicate the library NAKed a frame from the drive.)"
            Case &H983B
                Return "AD_DIAGNOSTIC_PORT_RESET_EVENT (Reported in ADI/AMI Command History Log to indicate a PORT RESET event occured due to a diagnostic command)"
            Case &H983C
                Return "AD_UNEXPECTED_REBOOT_SCSI_OP (Not reported to a host but indicates a f/w defect sequencing a firmware reboot related to an ADT RESET IU)"
            Case &H983D
                Return "AD_SMC_TEST_UNIT_ATTENTION (The error code is specifically used for test purposes only, it should never been seen in customer firmware)"
            Case &H983E
                Return "AD_FRAME_SAVED_DURING_PORT_RESET (Not reported to a host but indicates that the recieved frame was saved while the ADI port was being reset)"
            Case &H983F
                Return "AD_OVERWRITE_FRAME_DURING_PORT_RESET (Not reported to a host but indicates that the previously save frame was overwritten by another while the ADI port was being reset)"
            Case &H9840
                Return "AD_IMPLICIT_LOGOUT_EVENT_RECEIVED (Not reported directly to a host but used in the ADI Command History log to indicate an Implicit Logout event)"
            Case &H9841
                Return "AD_MAILBOX_TRANSMIT_TIMEOUT (Management ARM Mailbox failed to complete maAdtTransmitFrame request within timeout period!)"
            Case &H9C01
                Return "MA_IADT_RESET_LINE_ACTIVATED (The Ethernet RESET line has been actived!)"
            Case &H9C02
                Return "MA_ERR_INVALID_EVENT (The System ARM maHandleEvent() function has received an illegal event)"
            Case &H9C03
                Return "MA_ERR_CONNECT_TO_INVALID_PORT (This error is reported if an invalid iADT port is specified for connection.)"
            Case &H9C04
                Return "MA_SENSEA_ASSERTED (The iADT SENSEa input is Asserted by the library controller)"
            Case &H9C05
                Return "MA_SENSEA_DEASSERTED (The iADT SENSEa input is not asserted by the library controller)"
            Case &H9C06
                Return "MA_SENSEA_NOT_AVAILABLE (The iADT SENSEa input has been read yet)"
            Case &H9C07
                Return "MA_RECV_FAIL (Failure during socket read)"
            Case &H9C08
                Return "MA_SEND_FAIL (Failure during socket write)"
            Case &H9C09
                Return "MA_CFG_REJECT (MgmtArm rejected the maConfigNetworkIF options)"
            Case &H9C0A
                Return "MA_SELF_TEST_PASS (Requested Self Test passed)"
            Case &H9C0B
                Return "MA_SELF_TEST_FAIL (Requested Self Test failed)"
            Case &H9C0C
                Return "MA_MGMT_ASSERTED (Fatal condition caused the Management ARM to die a painful death)"
            Case &H9C0D
                Return "MA_MAILBOX_FULL (Could not queue the message, the Mailbox is full)"
            Case &H9C0E
                Return "MA_RECEIVE_FRAME_MALLOC_FAILED (Could not allocate memory to copy an ADT frame)"
            Case &H9C0F
                Return "MA_EVENT_TRIGGERED (The awaited event has occurred)"
            Case &H9C10
                Return "MA_MAILBOX_EMPTY (The Mailbox is empty)"
            Case &H9C11
                Return "MA_PORT_NOT_FOUND (The command failed because the requested port does not exist)"
            Case &H9C12
                Return "MA_CONNECT_FAILED (Failed to connect to the remote host)"
            Case &H9C13
                Return "MA_POST_NOT_AVAILABLE (POST status not available)"
            Case &H9C14
                Return "MA_NO_TASK_OBJECT (Unable to get a new task object.)"
            Case &H9C15
                Return "MA_ERR_TASK_QUEUE_FULL (Cannot execute SCSI command because another is in progress)"
            Case &H9C16
                Return "MA_MORE_DATA_THAN_EXPECTED (The KMS Agent Port I/F received more SCSI data than permited within a burst.)"
            Case &H9C17
                Return "MA_PKI_TYPE_INVALID (PKI type invalid)"
            Case &H9C18
                Return "MA_PKI_FORMAT_INVALID (PKI format invalid)"
            Case &H9C19
                Return "MA_PKI_CONVERSION_ERROR (PKI conversion error)"
            Case &H9C1A
                Return "MA_PKI_PROCESSING_ERROR (PKI processing error)"
            Case &H9C1B
                Return "MA_FRAME_BUFFER_ERROR (Frame Buffer Unrecoverable Error)"
            Case &H9C1C
                Return "MA_BUFFER_BRIDGE_ERROR (Buffer Bridge Unrecoverable Error)"
            Case &H9C1D
                Return "MA_DTCM_ERROR (DTCM Unrecoverable Error)"
            Case &H9C1E
                Return "MA_ITCM_ERROR (ITCM Unrecoverable Error)"
            Case &H9C1F
                Return "MA_UNDEF_INSTRUCTION (Undefined Instruction Error)"
            Case &H9C20
                Return "MA_PREFETCH_ABORT (Prefetch Abort Error)"
            Case &H9C21
                Return "MA_DATA_ABORT (Data Abort Error)"
            Case &H9C22
                Return "MA_MGMT_ASSERTED_DTRAP (Fatal condition caused the Management ARM to Invoke Dtrap())"
            Case &H9C23
                Return "MA_INVALID_FIELD_IN_CDB (Invalid field in CDB)"
            Case &H9C24
                Return "MA_INVALID_FIELD_IN_PARAM_LIST (Invalid field in parameter list)"
            Case &H9C25
                Return "MA_LED_TEST_PASS (MA LED Test passed)"
            Case &H9C26
                Return "MA_LED_TEST_FAIL (MA LED Test Failed)"
            Case &H9C27
                Return "MA_POST_FAIL_TCM (TCM POST Failed)"
            Case &H9C28
                Return "MA_POST_FAIL_FRAME_BUFFER (Frame Buffer POST Failed)"
            Case &H9C29
                Return "MA_POST_FAIL_MAC_REGISTER (MAC Register POST Failed)"
            Case &H9C2A
                Return "MA_POST_FAIL_PHY_REGISTER (PHY Register POST Failed)"
            Case &H9C2B
                Return "MA_POST_FAIL_VIC (VIC POST Failed)"
            Case &H9C2C
                Return "MA_POST_FAIL_TIMER (Timer POST Failed)"
            Case &H9C2D
                Return "MA_POST_FAIL_SYS_CONTROLLER (Sys Controller POST Failed)"
            Case &H9C2E
                Return "MA_POST_FAIL_TRACE_POINT (Tracepoint POST Failed)"
            Case &H9C2F
                Return "MA_FIPS_SELFTEST_FAILURE (OpenSSL known answer test failed)"
            Case &H9C30
                Return "MA_ISERIAL_UNEXPECTED_RECEIVE_ERR (The System ARM received data on the iSerial port unexpectedly)"
            Case &H9C31
                Return "MA_KMS_AGENT_MSG_QUEUE_ERR (The KMS Agent Message Queue is full so the message cannot be queued)"
            Case &H9C32
                Return "MA_POST_FAIL_OPENSSL (OpenSSL POST Failed)"
            Case &HA000
                Return "srNoError"
            Case &HA001
                Return "srPanic (The servo system has shutdown due to panic conditions)"
            Case &HA002
                Return "srTaskFaultBufferOverFlow (The Servo Task fault buffer has overflowed some entries have been lost)"
            Case &HA003
                Return "srMasterFaultBufferOverFlow (The Servo Interrupt fault buffer has overflowed some entries have been lost)"
            Case &HA005
                Return "srTimerNotAvailable (The Servo firmware requested a timer when none available)"
            Case &HA006
                Return "srStatsReportTimeout (Timeout occured whilst waiting to report Servo statistics)"
            Case &HA007
                Return "srInvalidTask (The current Servo firmware task is not valid a task)"
            Case &HA008
                Return "srAccelSeekTimeout (Accel phase of the seek did not complete within alloted time)"
            Case &HA009
                Return "srDecelSeekTimeout (Decel phase of the seek did not complete within alloted time)"
            Case &HA00A
                Return "srSettleWindowSeekTimeout (Settle window criteria was not met at the end of the Decel phase of the seek)"
            Case &HA00B
                Return "srHandOffSeekTimeout (Hand off to tracking phase of the seek did not complete within alloted time)"
            Case &HA00C
                Return "srUnknownServoModeDuringSeek (Servo mode was not recognised during seek)"
            Case &HA00D
                Return "srNegStictionPulseTimerFlt (A timer fault occurred during neg stiction pulse)"
            Case &HA00E
                Return "srPosStictionPulseTimerFlt (A timer fault occurred during pos stiction pulse)"
            Case &HA00F
                Return "srBotStopTimerFlt (A timer fault occurred during bottom crash stop cal)"
            Case &HA010
                Return "srTopStopTimerFlt (A timer fault occurred during top crash stop cal)"
            Case &HA011
                Return "srStrokeSizeTooSmallFlt (The measured actuator stroke size is too small)"
            Case &HA012
                Return "srTSBTimerFlt (A timer fault occurred during TSB calibration)"
            Case &HA013
                Return "srCalibrateTSB0Timeout (TSB0 calibration did not complete within the alloted time)"
            Case &HA014
                Return "srCalibrateTSB1Timeout (TSB1 calibration did not complete within the alloted time)"
            Case &HA015
                Return "srCalibrateTSB2Timeout (TSB2 calibration did not complete within the alloted time)"
            Case &HA016
                Return "srCalibrateTSB3Timeout (TSB3 calibration did not complete within the alloted time)"
            Case &HA017
                Return "srServoTrackOutOfBounds (An invalid servo track number was specified for a seek)"
            Case &HA018
                Return "srViSeekTargetOutOfRange (The calculated final target for a VI seek was out of range)"
            Case &HA019
                Return "srTapeSeekTargetOutOfRange (The calculated final target for a Tape seek was out of range)"
            Case &HA01A
                Return "srUnableToFindRequestedTSB (Unable to find the requested TSB)"
            Case &HA01B
                Return "srNoTapeServoCodeFound (Unable to find Servo Code on the tape)"
            Case &HA01C
                Return "srUnableToIdentifyTSB (Unable to identify the current Top Servo Band)"
            Case &HA01D
                Return "srUnknownSeekType (Unable to identify the requested Seek Type)"
            Case &HA01E
                Return "srUnableToAttemptTapeLockNotViTracking (Unable to lock to tape when not tracking on VI)"
            Case &HA01F
                Return "srTimedOutWaitingForTapeLock (Unable to lock to tape servo code within alloted time)"
            Case &HA020
                Return "srWriteWhilePrimaryBumpOut (Primary servo bump went out whilst writing)"
            Case &HA021
                Return "srTapeServoInhibit (System ARM is not allowing tape servo operations)"
            Case &HA022
                Return "srViOffsetCalibrationNotDone (VI offset calibration has not been done)"
            Case &HA023
                Return "srInvalidScopeMode (The specified Servo scope mode is not valid.)"
            Case &HA024
                Return "srInvalidScopeChanSize (The specified Servo scope channel bit width is not supported.)"
            Case &HA025
                Return "srInvalidScopeSourceID (The specified Servo source number is not valid.)"
            Case &HA026
                Return "srTriggerPosBeyondEndOfTrace (The specified Servo scope trigger position is too large compared to the specified number of data packets in the trace.)"
            Case &HA027
                Return "srServoScopeSourceAddressNotFound (The specified Servo scope source address was not found in the address array.)"
            Case &HA028
                Return "srServoScopeSourceSizeNotFound (The specified Servo scope source bit size was not found in the size array.)"
            Case &HA029
                Return "srMeasureServoHeadsTimeout (A Measure Servo Heads command did not complete within the alloted time.)"
            Case &HA02A
                Return "srSystemRequiredRevertToVi (The main system ARM flagged the need to revert to VI.)"
            Case &HA02B
                Return "srIdentifiedTSBsMatchButAreInvalid (Band ID had good skew values from both bumps but was unable to identify the band.)"
            Case &HA02C
                Return "srIdentifiedTSBMismatch (Band ID had good skew values from both bumps but identified different valid bands for each bump.)"
            Case &HA02D
                Return "srNotEnoughGoodChannelsToBandID (Not enough good demod channels whilst tape tracking to attempt band ID.)"
            Case &HA02E
                Return "srNotEnoughGoodChannelsToBandIDDuringTSBCalibration (Not enough good demod channels to attempt band ID whilst searching for tape servo during TSB calibration.)"
            Case &HA02F
                Return "srUnableToLockOnTapeWithServoCodePresentDuringTSBCalibration (Tape servo code available from at least one channel but unable to achieve tape lock, suspect head.)"
            Case &HA030
                Return "srInvalidCommand (Servo command is incorrectly formatted)"
            Case &HA031
                Return "srCommandNotAllowedNow (The Servo Command cannot be executed now)"
            Case &HA032
                Return "srTapeSpeedTooHighForDiagnostic (The Servo Command cannot be executed with the current tape speed)"
            Case &HA033
                Return "srObsoleteCmdError"
            Case &HA034
                Return "srFault_0x34 (Servo fault)"
            Case &HA035
                Return "srFault_0x35 (Servo fault)"
            Case &HA036
                Return "srFault_0x36 (Servo fault)"
            Case &HA037
                Return "srInvalidCommandParameter (One or more of the command parameters are invalid)"
            Case &HA038
                Return "srUnsupportedCommand (The servo command sent is not supported)"
            Case &HA039
                Return "srInvalidCommandSequence (The servo command sent is out of sequence)"
            Case &HA03A
                Return "srMechInterfaceTimeoutFault (uPTimeoutFlt - MechInterface did not respond within the alotted timeout, shutting down reel motors! )"
            Case &HA03B
                Return "srInitServoCommandAlreadyInProgress (Init servo command already in progress.)"
            Case &HA03C
                Return "srWriteMemoryServoCommandAlreadyInProgress (Write memory servo command already in progress.)"
            Case &HA03D
                Return "srReadMemoryServoCommandAlreadyInProgress (Read memory servo command already in progress.)"
            Case &HA03E
                Return "srSetGenerationServoCommandAlreadyInProgress (Set generation servo command already in progress.)"
            Case &HA03F
                Return "srSetScopeModeServoCommandAlreadyInProgress (Set scope mode servo command already in progress.)"
            Case &HA040
                Return "srPCADiagnosticServoCommandAlreadyInProgress (PCA diagnostic servo command already in progress.)"
            Case &HA041
                Return "srLiveStatisticsServoCommandAlreadyInProgress (Live statistics servo command already in progress.)"
            Case &HA042
                Return "srSeekCommandAlreadyInProgress (Seek command already in progress.)"
            Case &HA043
                Return "srReportTSBsServoCommandAlreadyInProgress (Report TSBs servo command already in progress.)"
            Case &HA044
                Return "srDefineScopeSourcesServoCommandAlreadyInProgress (Define scope sources servo command already in progress.)"
            Case &HA045
                Return "srConfigureOscillatorServoCommandAlreadyInProgress (Configure oscillator servo command already in progress.)"
            Case &HA046
                Return "srDefineSignalNodesServoCommandAlreadyInProgress (Define Signal Nodes servo command already in progress.)"
            Case &HA047
                Return "srCalibrateViStrokeServoCommandAlreadyInProgress (Calibrate VI stroke servo command already in progress.)"
            Case &HA048
                Return "srCalibrateTSBsServoCommandAlreadyInProgress (Calibrate TSBs servo command already in progress.)"
            Case &HA049
                Return "srReportServoStatusServoCommandAlreadyInProgress (Report servo status servo command already in progress.)"
            Case &HA04A
                Return "srConfigureTestDacsServoCommandAlreadyInProgress (Configure test DACs servo command already in progress.)"
            Case &HA04B
                Return "srReportServoConfigServoCommandAlreadyInProgress (Report servo configuration servo command already in progress.)"
            Case &HA04C
                Return "srMeasureServoHeadsServoCommandAlreadyInProgress (Measure servo heads servo command already in progress.)"
            Case &HA04D
                Return "srCopyMemoryBlockServoCommandAlreadyInProgress (Copy memory block servo command already in progress.)"
            Case &HA04E
                Return "srReportEnumAddressServoCommandAlreadyInProgress (Report Enum address servo command already in progress.)"
            Case &HA04F
                Return "srSetCompensatorCoefsServoCommandAlreadyInProgress (Set compensator coefficients servo command already in progress.)"
            Case &HA050
                Return "srSetHeadPosTuningParamsServoCommandAlreadyInProgress (Set head position tuning parameters servo command already in progress.)"
            Case &HA051
                Return "srCleanHeadCommandAlreadyInProgress (Clean head servo command already in progress.)"
            Case &HA052
                Return "srAccelerometerSelfTestCommandAlreadyInProgress (Accelerometer self test servo command already in progress.)"
            Case &HA053
                Return "srTSBCalibrationWatchdogTimeout (TSB calibration routine did not complete within alloted time.)"
            Case &HA054
                Return "srServoRequiredRevertToVi (The servo interrupt flagged the need to revert to VI, tape position data went away whilst tracking most likely.)"
            Case &HA055
                Return "srActuatorRecoveryAttempted (The servo system attempted to recover the actuator to a known state.)"
            Case &HA056
                Return "srServoInterruptOverrun (The servo Interrupt execution was longer than the interrupt rate.)"
            Case &HA057
                Return "srInvalidViGainAtDestination (The calculated VI gain at destination of a seek is invalid.)"
            Case &HA058
                Return "srAdaptiveEnteredFailsafe (The adaptive servo algorithm entered fail safe.)"
            Case &HA060
                Return "srMechRevNotRecognised (The Mech Revision was not recognised whilst trying to configure compensator.)"
            Case &HA061
                Return "srUpwardCleanHeadMotionTimerFlt (Timer unavailable for motion to top crashstop as part of head clean routine.)"
            Case &HA062
                Return "srDownwardCleanHeadMotionTimerFlt (Timer unavailable for motion to bottom crashstop as part of head clean routine.)"
            Case &HA063
                Return "srCleanHeadDwellTimerFlt (Timer unavailable for motion to bottom crashstop as part of head clean routine.)"
            Case &HA064
                Return "srCleanHeadSeekAtTopStopFlt (Seek at top crashstop during head clean.)"
            Case &HA065
                Return "srCleanHeadSeekAtBottomStopFlt (Seek at bottom crashstop during head clean.)"
            Case &HA066
                Return "srUnableToSetCompensatorByMechRev (Compensator configuration already set from EEPROM content or Mech Rev param missing.)"
            Case &HA067
                Return "srUnrecognisedHeadMode (Specified head mode is not recognised.)"
            Case &HA068
                Return "srUnableToDetermineTargetViGain (Target VI gain for use at seek destination could not be determined.)"
            Case &HA069
                Return "srDefectScanCommandAlreadyInProgress (A defect scan is already in progress.)"
            Case &HA06A
                Return "srTopStopVelocityTimeout (Unable to detect motion has ceased at the top stop during a sensor stroke cal.)"
            Case &HA06B
                Return "srBottomStopVelocityTimeout (Unable to detect motion has ceased at the bottom stop during a sensor stroke cal.)"
            Case &HA06C
                Return "srNotTrackingOnTargetTSB (Target TSB does not match the TSB returned from BandID.)"
            Case &HA06D
                Return "srCountDownHasBegun"
            Case &HA06E
                Return "srSensorFunctionalFault (Actuator position sensor is not functioning correctly.)"
            Case &HA06F
                Return "srViOscillatorFault (VI oscillator is not oscillating)"
            Case &HA070
                Return "srServoArmTraceportFifoOverflow (The traceport FIFO between system and servo ARMs overflowed.)"
            Case &HA071
                Return "srUnableToSendResetTraceportRequest (Unable to reset traceport hardware, event mailbox was busy)"
            Case &HA072
                Return "srTooCloseToTheEdgeOfTheBand (VI seek landed too close to the edge of servo code to attempt tape tracking.)"
            Case &HA073
                Return "srStrokeSizeTooBigFlt (The measured actuator stroke size is too big.)"
            Case &HA074
                Return "srViTableIndexOutOfRange (The index into the VI table is out of range.)"
            Case &HA075
                Return "srTSBCalibrationValueError (The learned VI value for one or more TSBs is invalid.)"
            Case &HA076
                Return "srInertialSystemNotLockedOrSkipCountTooHigh (The Inertial system is not locked or too many frame skips for BandID.)"
            Case &HA077
                Return "srTSBIdentifiedByBumpA (TSB was only identified by BumpA.)"
            Case &HA078
                Return "srTSBIdentifiedByBumpB (TSB was only identified by BumpB.)"
            Case &HA079
                Return "srSearchForTSBsValueError (TSB search algorithm did not obtain valid results.)"
            Case &HA07A
                Return "srInvalidBandIDDeltaTimeValue (BandID obtained an invalid delta time value from the hardware.)"
            Case &HA080
                Return "srDemodOutTopA (Demod data unavailable from elements- TopA.)"
            Case &HA081
                Return "srDemodOutBottomA (Demod data unavailable from elements- BottomA.)"
            Case &HA082
                Return "srDemodOutTopABottomA (Demod data unavailable from elements- TopA, BottomA.)"
            Case &HA083
                Return "srDemodOutTopB (Demod data unavailable from elements- TopB.)"
            Case &HA084
                Return "srDemodOutTopATopB (Demod data unavailable from elements- TopA, TopB.)"
            Case &HA085
                Return "srDemodOutBottomATopB (Demod data unavailable from elements- BottomA, TopB.)"
            Case &HA086
                Return "srDemodOutTopABottomATopB (Demod data unavailable from elements- TopA, BottomA, TopB.)"
            Case &HA087
                Return "srDemodOutBottomB (Demod data unavailable from elements- BottomB.)"
            Case &HA088
                Return "srDemodOutTopABottomB (Demod data unavailable from elements- TopA, BottomB.)"
            Case &HA089
                Return "srDemodOutBottomABottomB (Demod data unavailable from elements- BottomA, BottomB.)"
            Case &HA08A
                Return "srDemodOutTopABottomABottomB (Demod data unavailable from elements- TopA, BottomA, BottomB.)"
            Case &HA08B
                Return "srDemodOutTopBBottomB (Demod data unavailable from elements- TopB, BottomB.)"
            Case &HA08C
                Return "srDemodOutTopATopBBottomB (Demod data unavailable from elements- TopA, TopB, BottomB.)"
            Case &HA08D
                Return "srDemodOutBottomATopBBottomB (Demod data unavailable from elements- BottomA, TopB, BottomB.)"
            Case &HA08E
                Return "srDemodOutTopABottomATopBBottomB (Demod data unavailable from elements- TopA, BottomA, TopB, BottomB.)"
            Case &HA08F
                Return "srFault_0x8F (Reserved Servo fault.)"
            Case &HA401
                Return "CE_ERR_ROOT_CA_CERTIFICATE_REJECTED (A Root CA certificate already exists and authentication is not possible without a Management Host certificate)"
            Case &HA402
                Return "CE_ERR_MGMT_HOST_CERT_REJECTED_BECAUSE_ROOT_CA_CERT_DOES_NOT_EXIST (The Management Host certificate was rejected because the Root CA does not exist)"
            Case &HA403
                Return "CE_ERR_UNKNOWN_OR_UNEXPECTED_STATE (Unknown or unexpected Certificate Manager state)"
            Case &HA404
                Return "CE_ERR_MGMT_HOST_CERT_VERIFICATION_AGAINST_ROOT_CA_CERT_FAILED (The Management Host certificate failed verification)"
            Case &HA405
                Return "CE_ERR_PUBLIC_KEY_LENGTH_MISMATCH (The Public Key length returned does not match the length in the Public Key descriptor)"
            Case &HA406
                Return "CE_ERR_INVALID_CERTIFICATE_USAGE_OR_INDEX CertificateManagerID (The certificate usage and index do not specify a valid certificate storage slot)"
            Case &HA407
                Return "CE_ERR_INVALID_WHITELIST_USAGE_OR_INDEX  CertificateManagerID (The certificate usage and index do not specify a valid whitelist public key storage slot)"
            Case &HA408
                Return "CE_ERR_DEVICE_CERT_VERIFICATION_AGAINST_ROOT_CA_CERT_FAILED CertificateManagerID (The Device certificate failed verification)"
            Case &HA409
                Return "CE_ERR_DEVICE_CERT_REJECTED_BECAUSE_ROOT_CA_CERT_DOES_NOT_EXIST CertificateManagerID (The Device certificate was rejected because the Root CA does not exist)"
            Case &HA801
                Return "CA_UNSUPPORTED_ALGORITHM (AlgorithmId indicated an unsupported algorithm)"
            Case &HA802
                Return "CA_INVALID_KEY_LENGTH (Invalid key length specified)"
            Case &HA803
                Return "CA_ALGORITHM_INDEX_NOT_FOUND"
            Case &HA804
                Return "CA_INVALID_ARGUMENT_NULL_POINTER (One or more pointer arguments were NULL)"
            Case &HA805
                Return "CA_INVALID_ALGORITHM_DATABASE"
            Case &HA806
                Return "CA_AUTH_SIGNATURE_INVALID (Initiator message signature is invalid)"
            Case &HA807
                Return "CA_MESSAGE_DIGEST_BUFFER_TOO_SMALL (Message digest buffer is too small)"
            Case &HA808
                Return "CA_PRNG_INSUFFICIENT_ENTROPY (Insufficient entropy)"
            Case &HA809
                Return "CA_DECRYPTION_AUTHENTICATION_FAILURE (Decryption authentication failure)"
            Case &HA80A
                Return "CA_CERTIFICATION_AUTHORITY_HASH_MISMATCH (Certification authority hash mismatch)"
            Case &HA80B
                Return "CA_RANDOM_NUMBER_GENERATION_FAILED (Random number generation failed)"
            Case &HA80C
                Return "CA_DH_PUBLIC_VALUE_GENERATION_FAILED (Diffie-Hellman public value creation failed)"
            Case &HA80D
                Return "CA_BN_CONVERSION_FAILED (Bignum conversion failed)"
            Case &HA80E
                Return "CA_MESSAGE_DIGEST_FAILURE (Message digest failure)"
            Case &HA80F
                Return "CA_SIGNING_FAILURE (Signature failure)"
            Case &HA810
                Return "CA_ENCRYPTION_ARGUMENT_ERROR (Encryption argument error)"
            Case &HA811
                Return "CA_ENCRYPTION_FAILURE (Encryption failure)"
            Case &HA812
                Return "CA_OPENSSL_INITIALIZATION_FAILURE (OpenSSL initialization failure)"
            Case &HA813
                Return "CA_OPENSSL_D2I_CONVERSION_FAILURE (OpenSSL d2i conversion failure)"
            Case &HA814
                Return "CA_BUFFER_TOO_SMALL (Buffer is too small)"
            Case &HA815
                Return "CA_CAVP_FAILURE (One or more CAVP tests failed)"
            Case &HA816
                Return "CA_FIPS_SELFTEST_FAILURE (One or more OpenSSL FIPS selftests failed at poweron with SME set to 1)"
            Case &HA817
                Return "CA_RSA_KEY_GENERATION_FAILURE (Generation of new RSA key pair failed)"
            Case &HA818
                Return "CA_OPENSSL_MALLOC_FAILURE (OpenSSL memory allocation failure)"
            Case &HAC01
                Return "MCC_AFIR2_RS_COUNT_IS_ZERO (revi() failed, AFIR2 RS Counter is zero)"
            Case &HAC02
                Return "MCC_SANDSTROM_READ_TIMEOUT (SAMDSTROM read timeout)"
            Case &HAC03
                Return "MCC_PP_ADAPT_NO_DATA_DETECTED (PP Adaptation failed to read. Possible No Data Detected.)"
            Case &HAC04
                Return "MCC_AFIR2_ADAPT_NO_DATA_DETECTED (AFIR2 Adaptation failed to read. Possible No Data Detected)"
            Case &HF401
                Return " HYPER_ERROR_READ_SMI_STATUS_TIMEOUT_BEFORE (sandstrom read failed waiting for status bit)"
            Case &HF402
                Return " HYPER_ERROR_READ_SMI_STATUS_TIMEOUT_AFTER (sandstrom read failed waiting for data)"
            Case &HF403
                Return " HYPER_ERROR_INIT_READ_SSTROM_RDSP_CONTROL (hyper_init return failed to read READ_SSTROM_RDSP_CONTROL)"
            Case &HF404
                Return " HYPER_ERROR_INIT_READ_READ_DSP_TESPORT_ADDRESS (hyper_init return failed to read READ_DSP_TESPORT_ADDRESS)"
            Case &HF405
                Return " HYPER_ERROR_INIT_READ_DSP_TESPORT_DATA_BAD (hyper_init return failed to read DSP_TESPORT_DATA_BAD)"
            Case &HF406
                Return " HYPER_ERROR_INIT_READ_SSTROM_RDSP_PARITY_ERROR (hyper_init return failed to read SSTROM_RDSP_PARITY_ERROR)"
            Case &HF407
                Return " HYPER_ERROR_INIT_READ_SSTROM_RDSP_SYNC_ERROR (hyper_init return failed to read SSTROM_RDSP_SYNC_ERROR)"
            Case &HF408
                Return " HYPER_ERROR_INIT_ORION_PAGE_IN_USE (hyper_init return Orion page in use)"
            Case &HF409
                Return " HYPER_ERROR_INIT_HT_CONTROL_CHECK (hyper_init return HT CONTROL check failed)"
            Case &HF40A
                Return " HYPER_ERROR_INIT_HT_PARITY_ERROR_NOT_ZERO (hyper_init return HT PARITY NOT ZERO)"
            Case &HF40B
                Return " HYPER_ERROR_INIT_HT_SYNC_ERROR_NOT_ZERO (hyper_init return HT SYNC NOT ZERO)"
            Case &HF40C
                Return " GWIF_PENDING"
            Case &HF801
                Return " GWIF_PENDING"
            Case &HF802
                Return " GWIF_NO_CHANGE"
            Case Else
                Return $"0x{Hex(Drv_Code).ToUpper().PadLeft(4, "0"c)}"
        End Select
    End Function
    Public Shared Function ParseSenseData(sense As Byte()) As String
        Dim Msg As New StringBuilder
        Dim Fixed As Boolean = False
        Dim Add_Code As Integer
        Dim Valid As Boolean = ((sense(0) >> 7) = 1)
        If (sense(0) And &H7F) = &H70 Then
            Msg.AppendLine("Error code represents current error")
            Fixed = True
        ElseIf (sense(0) And &H7F) = &H71 Then
            Msg.AppendLine("Error code represents deferred error")
            Fixed = True
        End If
        If Fixed Then
            If sense(2) >> 7 = 1 Then
                Msg.AppendLine("Filemark encountered")
            End If
            If ((sense(2) >> 6) And &H1) = 1 Then
                Msg.AppendLine("EOM encountered")
            End If
            If ((sense(2) >> 5) And &H1) = 1 Then
                Msg.AppendLine("Blocklen mismatch")
            End If
            Dim sensekey As Byte = CByte(sense(2) And &HF)
            Msg.Append("Sense key: ")
            Select Case sensekey
                Case 0
                    Msg.AppendLine("NO SENSE")
                Case 1
                    Msg.AppendLine("RECOVERED ERROR")
                Case 2
                    Msg.AppendLine("NOT READY")
                Case 3
                    Msg.AppendLine("MEDIUM ERROR")
                Case 4
                    Msg.AppendLine("HARDWARE ERROR")
                Case 5
                    Msg.AppendLine("ILLEGAL REQUEST")
                Case 6
                    Msg.AppendLine("UNIT ATTENTION")
                Case 7
                    Msg.AppendLine("DATA PROTECT")
                Case 8
                    Msg.AppendLine("BLANK CHECK")
                Case 9
                    Msg.AppendLine("VENDOR SPECIFIC")
                Case 10
                    Msg.AppendLine("COPY ABORTED")
                Case 11
                    Msg.AppendLine("ABORTED COMMAND")
                Case 12
                    Msg.AppendLine("EQUAL")
                Case 13
                    Msg.AppendLine("VOLUME OVERFLOW")
                Case 14
                    Msg.AppendLine("MISCOMPARE")
                Case 15
                    Msg.AppendLine("RESERVED")
            End Select
            If Valid Then
                Msg.AppendLine("Info bytes: " & Byte2Hex({sense(3), sense(4), sense(5), sense(6)}))
            End If
            Dim Add_Len As Byte = sense(7)
            Add_Code = CInt(sense(12)) << 8 Or sense(13)
            Dim SKSV As Boolean = ((sense(15) >> 7) = 1)
            Dim CD As Boolean = ((sense(15) >> 6) And 1) = 1
            Dim BPV As Boolean = ((sense(15) >> 3) And 1) = 1

            If SKSV Then
                If sensekey = 5 Then
                    Msg.AppendLine("Error byte = " & (CInt(sense(16)) << 8 Or sense(17)) & " bit = " & (sense(15) And 7))
                ElseIf sensekey = 0 Or sensekey = 2 Then
                    Msg.AppendLine("Progress = " & (CInt(sense(16)) << 8 Or sense(17)))
                End If
            Else
                Dim driveErrorCode As String = Byte2Hex(New Byte() {sense(16), sense(17)}).Replace(" ", "")
                Msg.Append("Drive Error Code: 0x" & driveErrorCode & " ")
                Msg.AppendLine(ParseDriveCode(CUShort(sense(16)) << 8 Or sense(17)))
            End If
            If ((sense(21) >> 3) And 1) = 1 Then
                Msg.AppendLine("Clean is required")
            End If
        End If
        Dim additionalCode As String = Byte2Hex(New Byte() {sense(12), sense(13)}).Replace(" ", "")
        Msg.Append("Additional code: 0x" & additionalCode & " ")
        Msg.AppendLine(ParseAdditionalSenseCode(CUShort(Add_Code)))
        Return Msg.ToString()
    End Function
    Public Shared Function PreventMediaRemoval(handle As IntPtr, Optional ByVal senseReport As Func(Of Byte(), Boolean) = Nothing) As Boolean
        Return SendSCSICommand(handle, {&H1E, 0, 0, 0, 1, 0}, Nothing, 1, senseReport)
    End Function
    Public Shared Function PreventMediaRemoval(TapeDrive As String, Optional ByVal senseReport As Func(Of Byte(), Boolean) = Nothing) As Boolean
        Return RunOpenedTapeOperation(TapeDrive,
                                      Function(handle As IntPtr) As Boolean
                                          Return PreventMediaRemoval(handle, senseReport)
                                      End Function)
    End Function
    Public Shared Function AllowMediumRemoval(handle As IntPtr, Optional ByVal senseReport As Func(Of Byte(), Boolean) = Nothing) As Boolean
        Return SendSCSICommand(handle, {&H1E, 0, 0, 0, 0, 0}, Nothing, 1, senseReport)
    End Function
    Public Shared Function AllowMediumRemoval(TapeDrive As String, Optional ByVal senseReport As Func(Of Byte(), Boolean) = Nothing) As Boolean
        Return RunOpenedTapeOperation(TapeDrive,
                                      Function(handle As IntPtr) As Boolean
                                          Return AllowMediumRemoval(handle, senseReport)
                                      End Function)
    End Function
    Public Shared Function DoReload(TapeDrive As String, Lock As Boolean, EncryptionKey As Byte()) As Boolean
        Return RunOpenedTapeOperation(TapeDrive,
                                      Function(handle As IntPtr) As Boolean
                                          Return DoReload(handle, Lock, EncryptionKey)
                                      End Function)
    End Function
    Public Shared Function DoReload(handle As IntPtr, Lock As Boolean, EncryptionKey As Byte()) As Boolean
        SyncLock SCSILockManager.GetLock(handle)
            Dim Loc As New PositionData(handle)
            If Not AllowMediumRemoval(handle) Then Return False
            If Not LoadEject(handle:=handle, LoadOption:=LoadOption.Unthread) Then Return False
            If Not LoadEject(handle:=handle, LoadOption:=LoadOption.LoadThreaded, EncryptionKey:=EncryptionKey) Then Return False
            If Lock AndAlso Not PreventMediaRemoval(handle) Then Return False
            If Locate(handle:=handle, BlockAddress:=Loc.BlockNumber, Partition:=Loc.PartitionNumber, DestType:=LocateDestType.Block) <> 0 Then Return False

        End SyncLock
        Return True
    End Function
    Public Shared Function ReserveUnit(TapeDrive As String, Optional ByVal senseReport As Func(Of Byte(), Boolean) = Nothing) As Boolean
        Return RunOpenedTapeOperation(TapeDrive,
                                      Function(handle As IntPtr) As Boolean
                                          Return ReserveUnit(handle, senseReport)
                                      End Function)
    End Function
    Public Shared Function ReserveUnit(handle As IntPtr, Optional ByVal senseReport As Func(Of Byte(), Boolean) = Nothing) As Boolean
        Return SendSCSICommand(handle, {&H16, 0, 0, 0, 0, 0}, Nothing, 1, senseReport)
    End Function
    Public Shared Function ReleaseUnit(TapeDrive As String, Optional ByVal senseReport As Func(Of Byte(), Boolean) = Nothing) As Boolean
        Return RunOpenedTapeOperation(TapeDrive,
                                      Function(handle As IntPtr) As Boolean
                                          Return ReleaseUnit(handle, senseReport)
                                      End Function)
    End Function
    Public Shared Function ReleaseUnit(handle As IntPtr, Optional ByVal senseReport As Func(Of Byte(), Boolean) = Nothing) As Boolean
        Return SendSCSICommand(handle, {&H17, 0, 0, 0, 0, 0}, Nothing, 1, senseReport)
    End Function

    Public Shared Function ReadMAMAttributeString(TapeDrive As String, PageCode_H As Byte, PageCode_L As Byte) As String 'TC_MAM_BARCODE = 0x0806 LEN = 32
        Return Encoding.UTF8.GetString(GetMAMAttributeBytes(TapeDrive, PageCode_H, PageCode_L).ToArray())
    End Function
    Public Shared Function ReadMAMAttributeString(handle As IntPtr, PageCode_H As Byte, PageCode_L As Byte) As String 'TC_MAM_BARCODE = 0x0806 LEN = 32
        Return Encoding.UTF8.GetString(GetMAMAttributeBytes(handle, PageCode_H, PageCode_L).ToArray())
    End Function
    Public Class PositionData
        Public Property BOP As Boolean
        Public Property EOP As Boolean
        Public Property MPU As Boolean
        Public Property PartitionNumber As Byte
        Public Property BlockNumber As UInt64
        Public Property FileNumber As UInt64
        Public Property SetNumber As UInt64
        Public Property AddSenseKey As UInt16
        Public ReadOnly Property EOD As Boolean
            Get
                Return AddSenseKey = 5
            End Get
        End Property
        Public Sub New()

        End Sub
        Public Sub New(TapeDrive As String)
            Dim data As PositionData = ReadPosition(TapeDrive)
            BOP = data.BOP
            EOP = data.EOP
            MPU = data.MPU
            AddSenseKey = data.AddSenseKey
            PartitionNumber = data.PartitionNumber
            BlockNumber = data.BlockNumber
            FileNumber = data.FileNumber
            SetNumber = data.SetNumber
        End Sub
        Public Sub New(handle As IntPtr)
            Dim data As PositionData = ReadPosition(handle)
            BOP = data.BOP
            EOP = data.EOP
            MPU = data.MPU
            AddSenseKey = data.AddSenseKey
            PartitionNumber = data.PartitionNumber
            BlockNumber = data.BlockNumber
            FileNumber = data.FileNumber
            SetNumber = data.SetNumber
        End Sub
        Public Overrides Function ToString() As String
            Dim Xtrs As String = ""
            If BOP Then Xtrs &= " BOP"
            If EOP Then Xtrs &= " EOP"
            If MPU Then Xtrs &= " MPU"
            If EOD Then Xtrs &= " EOD"
            Return $"P{PartitionNumber} B{BlockNumber} FM{FileNumber} SET{SetNumber}{Xtrs}"
        End Function
    End Class
    Public Shared DriverTypeSetting As DriverType = DriverType.LTO
    Public Enum DriverType As Integer
        LTO = 0
        T10K = 3
        IBM3592 = 5
        SLR1 = 4
        SLR3 = 2
        M2488 = 1
        TapeStream = 100
        ZBCDevice = 101
        Debug = -1
    End Enum
    Public Shared Function ReadPosition(handle As IntPtr) As PositionData
        Return ReadPosition(handle, DriverType.LTO)
    End Function
    Public Shared Function ReadPosition(handle As IntPtr, ByVal DriverType As DriverType) As PositionData
        If DriverTypeSetting <> DriverType.LTO Then DriverType = DriverTypeSetting
        Dim param As Byte()
        Dim result As New PositionData
        Dim sense As Byte() = Array.Empty(Of Byte)()
        Select Case DriverType
            Case DriverType.M2488
            Case DriverType.SLR3
                param = SCSIReadParam(handle, {&H34, 1, 0, 0, 0, 0, 0, 0, 0, 0}, 32)
                result.BOP = (((param(0) >> 7) And &H1) <> 0)
                result.EOP = (((param(0) >> 6) And &H1) <> 0)
                For i As Integer = 0 To 3
                    result.BlockNumber <<= 8
                    result.BlockNumber = result.BlockNumber Or param(4 + i)
                Next
            Case DriverType.SLR1
                param = SCSIReadParam(handle, {&H2, 0, 0, 0, 3, 0}, 3)
                For i As Integer = 0 To 2
                    result.BlockNumber <<= 8
                    result.BlockNumber = result.BlockNumber Or param(0 + i)
                Next
            Case DriverType.TapeStream
                Dim ts As TapeImage = Nothing
                TapeStreamMapping.MappingTable.TryGetValue(handle, ts)
                If ts IsNot Nothing Then
                    result = ts.ReadPosition()
                Else
                    sense = TestUnitReady(handle)
                End If
            Case Else
                If AllowPartition Then
                    param = SCSIReadParam(handle, {&H34, 6, 0, 0, 0, 0, 0, 0, 0, 0}, 32, Function(sdata As Byte())
                                                                                             sense = sdata
                                                                                             Return True
                                                                                         End Function)
                    result.BOP = (((param(0) >> 7) And &H1) <> 0)
                    result.EOP = (((param(0) >> 6) And &H1) <> 0)
                    result.MPU = (((param(0) >> 3) And &H1) <> 0)
                    For i As Integer = 0 To 3
                        result.PartitionNumber <<= 8
                        result.PartitionNumber = result.PartitionNumber Or param(4 + i)
                    Next
                    For i As Integer = 0 To 7
                        result.BlockNumber <<= 8
                        result.BlockNumber = result.BlockNumber Or param(8 + i)
                        result.FileNumber <<= 8
                        result.FileNumber = result.FileNumber Or param(16 + i)
                        result.SetNumber <<= 8
                        result.SetNumber = result.SetNumber Or param(24 + i)
                    Next
                Else
                    param = SCSIReadParam(handle, {&H34, 0, 0, 0, 0, 0, 0, 0, 0, 0}, 32)
                    result.BOP = (((param(0) >> 7) And &H1) <> 0)
                    result.EOP = (((param(0) >> 6) And &H1) <> 0)
                    For i As Integer = 0 To 3
                        result.BlockNumber <<= 8
                        result.BlockNumber = result.BlockNumber Or param(4 + i)
                    Next
                End If
        End Select
        If sense IsNot Nothing AndAlso sense.Length >= 14 Then result.AddSenseKey = CUShort(CInt(sense(12)) << 8 Or sense(13))
        Return result
    End Function
    Public Shared Function ReadPosition(TapeDrive As String) As PositionData
        Return ReadPosition(TapeDrive, My.Settings.TapeUtils_DriverType)
    End Function
    Public Shared Function ReadPosition(TapeDrive As String, ByVal DriverType As DriverType) As PositionData
        Return RunOpenedTapeOperation(TapeDrive,
                                      Function(handle As IntPtr) As PositionData
                                          Return ReadPosition(handle, DriverType)
                                      End Function)
    End Function
    Public Shared Function Write(TapeDrive As String, Data As Byte(), Optional cancellationToken As CancellationToken = Nothing) As Byte()
        Return RunOpenedTapeOperation(TapeDrive,
                                      Function(handle As IntPtr) As Byte()
                                          Return Write(handle, Data, cancellationToken)
                                      End Function)
    End Function
    Public Shared Function Write(handle As IntPtr, Data As Byte(), Optional cancellationToken As CancellationToken = Nothing) As Byte()
        Dim sense(63) As Byte
        cancellationToken.ThrowIfCancellationRequested()
        Select Case DriverTypeSetting
            Case DriverType.SLR3
                Dim succ As Boolean =
            SendSCSICommandUnmanaged(handle, {&HA, 0, CByte((Data.Length >> 16) And &HFF), CByte((Data.Length >> 8) And &HFF), CByte(Data.Length And &HFF), 0}, Data, 0,
                        Function(senseData As Byte()) As Boolean
                            sense = senseData
                            Return True
                        End Function,
                        cancellationToken:=cancellationToken)
                If Not succ Then
                    Throw New SCSIFailureException("SCSI write", LastWin32Error, sense)
                End If
            Case DriverType.SLR1
                Dim BlockCount As Integer = CInt(Math.Ceiling(Data.Length / 512))
                Dim succ As Boolean =
            SendSCSICommandUnmanaged(handle, {&HA, 1, CByte((BlockCount >> 16) And &HFF), CByte((BlockCount >> 8) And &HFF), CByte(BlockCount And &HFF), 0}, Data, 0,
                        Function(senseData As Byte()) As Boolean
                            sense = senseData
                            Return True
                        End Function,
                        cancellationToken:=cancellationToken)
                If Not succ Then
                    Throw New SCSIFailureException("SCSI write", LastWin32Error, sense)
                End If
            Case DriverType.TapeStream
                Dim ts As TapeImage = Nothing
                TapeStreamMapping.MappingTable.TryGetValue(handle, ts)
                If ts Is Nothing Then
                    sense = TapeImage.SenseData.NotPresent
                Else
                    cancellationToken.ThrowIfCancellationRequested()
                    ts.WriteBlock(Data)
                    sense = TapeImage.SenseData.NoSense
                End If
            Case Else
                Dim succ As Boolean =
            SendSCSICommandUnmanaged(handle, {&HA, 0, CByte((Data.Length >> 16) And &HFF), CByte((Data.Length >> 8) And &HFF), CByte(Data.Length And &HFF), 0}, Data, 0,
                        Function(senseData As Byte()) As Boolean
                            sense = senseData
                            Return True
                        End Function,
                        cancellationToken:=cancellationToken)
                If Not succ Then
                    Throw New SCSIFailureException("SCSI write", LastWin32Error, sense)
                End If
        End Select
        Return sense
    End Function
    Public Shared Function Write(TapeDrive As String, Data As IntPtr, Length As Integer, Optional cancellationToken As CancellationToken = Nothing) As Byte()
        Return Write(TapeDrive, Data, CUInt(Length), cancellationToken)
    End Function
    Public Shared Function Write(TapeDrive As String, Data As IntPtr, Length As UInteger, Optional cancellationToken As CancellationToken = Nothing) As Byte()
        Return Write(TapeDrive, Data, Length, False, cancellationToken)
    End Function
    Public Shared Function Write(handle As IntPtr, Data As IntPtr, Length As UInteger, ByVal senseEnabled As Boolean, Optional cancellationToken As CancellationToken = Nothing) As Byte()
        Dim sense() As Byte = {0, 0, 0}
        If senseEnabled Then ReDim sense(63)
        cancellationToken.ThrowIfCancellationRequested()
        Select Case DriverTypeSetting
            Case DriverType.TapeStream
                Dim ts As TapeImage = Nothing
                TapeStreamMapping.MappingTable.TryGetValue(handle, ts)
                If ts Is Nothing Then
                    sense = TapeImage.SenseData.NotPresent
                Else
                    cancellationToken.ThrowIfCancellationRequested()
                    Dim dataArr(CInt(Length - 1)) As Byte
                    Marshal.Copy(Data, dataArr, 0, CInt(Length))
                    ts.WriteBlock(dataArr)
                    sense = TapeImage.SenseData.NoSense
                End If
            Case Else
                Dim cdbData As Byte() = {&HA, 0, CByte(Length >> 16 And &HFF), CByte(Length >> 8 And &HFF), CByte(Length And &HFF), 0}
                Dim succ As Boolean = TapeSCSIIOCtlUnmanaged(handle, cdbData, Data, Length, 0, 900, sense, cancellationToken)
                ThrowIfSCSIFailed(succ, "SCSI write", sense)
        End Select
        Return sense
    End Function
    Public Shared Function Write(TapeDrive As String, Data As IntPtr, Length As UInteger, ByVal senseEnabled As Boolean, Optional cancellationToken As CancellationToken = Nothing) As Byte()
        Return RunOpenedTapeOperation(TapeDrive,
                                      Function(handle As IntPtr) As Byte()
                                          Return Write(handle, Data, Length, senseEnabled, cancellationToken)
                                      End Function)
    End Function
    Public Shared Function Write(TapeDrive As String, Data As Byte(), BlockSize As UInteger, Optional cancellationToken As CancellationToken = Nothing) As Byte()
        Return Write(TapeDrive, Data, CInt(BlockSize), cancellationToken)
    End Function
    Public Shared Function Write(TapeDrive As String, Data As Byte(), BlockSize As Long, Optional cancellationToken As CancellationToken = Nothing) As Byte()
        Return Write(TapeDrive, Data, CInt(BlockSize), cancellationToken)
    End Function
    Public Shared Function Write(TapeDrive As String, Data As Byte(), BlockSize As Integer, Optional cancellationToken As CancellationToken = Nothing) As Byte()
        Return RunOpenedTapeOperation(TapeDrive,
                                      Function(handle As IntPtr) As Byte()
                                          Return Write(handle, Data, BlockSize, cancellationToken)
                                      End Function)
    End Function

    Public Shared Function Write(handle As IntPtr, Data As Byte(), BlockSize As Integer, Optional cancellationToken As CancellationToken = Nothing) As Byte()
        If Data.Length <= BlockSize Then
            Return Write(handle, Data, cancellationToken)
        End If
        Dim sense(63) As Byte
        Dim ts As TapeImage = Nothing
        Select Case DriverTypeSetting
            Case DriverType.TapeStream
                TapeStreamMapping.MappingTable.TryGetValue(handle, ts)
                If ts Is Nothing Then
                    sense = TapeImage.SenseData.NotPresent
                    Return sense
                End If
        End Select
        Dim cdbData As Byte() = {}
        Dim dataBuffer As IntPtr = Marshal.AllocHGlobal(BlockSize)
        Try
            For i As Integer = 0 To Data.Length - 1 Step BlockSize
                cancellationToken.ThrowIfCancellationRequested()
                Dim TransferLen As UInteger = CUInt(Math.Min(BlockSize, Data.Length - i))
                Select Case My.Settings.TapeUtils_DriverType
                    Case DriverType.SLR1
                        cdbData = {&HA, 1, CByte(TransferLen >> 16 And &HFF), CByte(TransferLen >> 8 And &HFF), CByte(TransferLen And &HFF), 0}
                    Case Else
                        cdbData = {&HA, 0, CByte(TransferLen >> 16 And &HFF), CByte(TransferLen >> 8 And &HFF), CByte(TransferLen And &HFF), 0}
                End Select
                Marshal.Copy(Data, i, dataBuffer, CInt(TransferLen))
                Select Case DriverTypeSetting
                    Case DriverType.TapeStream
                        Dim dataArr(CInt(TransferLen - 1)) As Byte
                        Marshal.Copy(dataBuffer, dataArr, 0, CInt(TransferLen))
                        ts.WriteBlock(dataArr)
                        sense = TapeImage.SenseData.NoSense
                    Case Else
                        Dim succ As Boolean = TapeSCSIIOCtlUnmanaged(handle, cdbData, dataBuffer, TransferLen, 0, 60000, sense, cancellationToken)
                        ThrowIfSCSIFailed(succ, "SCSI write", sense)
                End Select
            Next
            Return sense
        Finally
            Marshal.FreeHGlobal(dataBuffer)
        End Try
    End Function
    Public Shared Function Write(TapeDrive As String, Data As Stream, Optional cancellationToken As CancellationToken = Nothing) As Byte()
        Return Write(TapeDrive, Data, 524288, False, cancellationToken)
    End Function
    Public Shared Function Write(TapeDrive As String, Data As Stream, ByVal BlockSize As UInteger, Optional cancellationToken As CancellationToken = Nothing) As Byte()
        Return Write(TapeDrive, Data, CInt(BlockSize), False, cancellationToken)
    End Function
    Public Shared Function Write(TapeDrive As String, Data As Stream, ByVal BlockSize As Integer, senseEnabled As Boolean, Optional cancellationToken As CancellationToken = Nothing) As Byte()
        Return RunOpenedTapeOperation(TapeDrive,
                                      Function(handle As IntPtr) As Byte()
                                          Return Write(handle, Data, BlockSize, senseEnabled, cancellationToken:=cancellationToken)
                                      End Function)
    End Function
    Public Shared Function Write(handle As IntPtr, Data As Stream, ByVal BlockSize As Integer, senseEnabled As Boolean, Optional ByVal ProgressReport As Action(Of Long) = Nothing, Optional ByVal DataRead As Action(Of Byte(), Integer, Integer) = Nothing, Optional cancellationToken As CancellationToken = Nothing) As Byte()
        Dim sense(63) As Byte
        cancellationToken.ThrowIfCancellationRequested()
        Dim ts As TapeImage = Nothing
        Select Case DriverTypeSetting
            Case DriverType.TapeStream
                TapeStreamMapping.MappingTable.TryGetValue(handle, ts)
                If ts Is Nothing Then
                    sense = TapeImage.SenseData.NotPresent
                    Return sense
                End If
        End Select
        BlockSize = Math.Min(BlockSize, GlobalBlockLimit)
        Dim DataBuffer(BlockSize - 1) As Byte
        Dim DataPtr As IntPtr = Marshal.AllocHGlobal(BlockSize)
        Dim DataLen As Integer
        Dim succ As Boolean
        Try
            DataLen = Data.Read(DataBuffer, 0, BlockSize)
            While DataLen > 0
                cancellationToken.ThrowIfCancellationRequested()
                If DataRead IsNot Nothing Then DataRead(DataBuffer, 0, DataLen)
                Dim cdbData As Byte() = {&HA, 0, CByte((DataLen >> 16) And &HFF), CByte((DataLen >> 8) And &HFF), CByte(DataLen And &HFF), 0}
                Marshal.Copy(DataBuffer, 0, DataPtr, DataLen)
                Do
                    cancellationToken.ThrowIfCancellationRequested()
                    Select Case DriverTypeSetting
                        Case DriverType.TapeStream
                            ts.WriteBlock(DataBuffer)
                            sense = TapeImage.SenseData.NoSense
                            succ = True
                        Case Else
                            succ = TapeSCSIIOCtlUnmanaged(handle, cdbData, DataPtr, CUInt(DataLen), 0, 60000, sense, cancellationToken)

                    End Select
                    If succ Then
                        If ProgressReport IsNot Nothing Then
                            ProgressReport(DataLen)
                        End If
                        Exit Do
                    Else
                        Dim ActiveFrm = ApplicationWheels.GetActiveWindow()
                        Dim dResult As DialogResult
                        ActiveFrm.Invoke(Sub() dResult = MessageBox.Show(ActiveFrm, $"写入出错：SCSI指令执行失败", "警告", MessageBoxButtons.AbortRetryIgnore))
                        Select Case dResult
                            Case DialogResult.Abort
                                Exit While
                            Case DialogResult.Retry
                            Case DialogResult.Ignore
                                Exit Do
                        End Select
                    End If
                Loop
                DataLen = Data.Read(DataBuffer, 0, BlockSize)
            End While
            If Not succ Then
                Throw New SCSIFailureException("SCSI write", LastWin32Error, sense)
            End If
            Return {0, 0, 0}
        Finally
            Try
                Data.Close()
            Catch
            End Try
            Marshal.FreeHGlobal(DataPtr)
        End Try
    End Function
    Public Shared Function Write(TapeDrive As String, sourceFile As String, Optional cancellationToken As CancellationToken = Nothing) As Byte()
        Return Write(TapeDrive, sourceFile, 524288, False, cancellationToken)
    End Function
    Public Shared Function Write(TapeDrive As String, sourceFile As String, ByVal BlockLen As Integer, Optional cancellationToken As CancellationToken = Nothing) As Byte()
        Return Write(TapeDrive, sourceFile, BlockLen, False, cancellationToken)
    End Function
    Public Shared Function Write(TapeDrive As String, sourceFile As String, ByVal BlockLen As UInteger, Optional cancellationToken As CancellationToken = Nothing) As Byte()
        Return Write(TapeDrive, sourceFile, CInt(BlockLen), False, cancellationToken)
    End Function
    Public Shared Function Write(TapeDrive As String, sourceFile As String, ByVal BlockLen As Integer, ByVal senseEnabled As Boolean, Optional cancellationToken As CancellationToken = Nothing) As Byte()
        Return RunOpenedTapeOperation(TapeDrive,
                                      Function(handle As IntPtr) As Byte()
                                          Return Write(handle, sourceFile, BlockLen, senseEnabled, cancellationToken)
                                      End Function)
    End Function
    Public Shared Function Write(handle As IntPtr, sourceFile As String, ByVal BlockLen As Integer, ByVal senseEnabled As Boolean, Optional cancellationToken As CancellationToken = Nothing) As Byte()
        Dim sense(63) As Byte
        cancellationToken.ThrowIfCancellationRequested()
        BlockLen = Math.Min(BlockLen, GlobalBlockLimit)
        Dim DataBuffer(BlockLen - 1) As Byte
        Dim DataPtr As IntPtr = Marshal.AllocHGlobal(BlockLen)
        Dim fs As FileStream = Nothing
        Dim DataLen As Integer
        Dim succ As Boolean
        Dim ts As TapeImage = Nothing
        Try
            fs = New FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read)
            DataLen = fs.Read(DataBuffer, 0, BlockLen)
            Select Case DriverTypeSetting
                Case DriverType.TapeStream
                    TapeStreamMapping.MappingTable.TryGetValue(handle, ts)
                    If ts Is Nothing Then
                        sense = TapeImage.SenseData.NotPresent
                        Return sense
                    End If
            End Select
            While DataLen > 0
                cancellationToken.ThrowIfCancellationRequested()
                Select Case DriverTypeSetting
                    Case DriverType.TapeStream
                        ts.WriteBlock(DataBuffer, DataLen)
                        succ = True
                    Case Else
                        Do
                            cancellationToken.ThrowIfCancellationRequested()
                            Dim cdbData As Byte() = {&HA, 0, CByte((DataLen >> 16) And &HFF), CByte((DataLen >> 8) And &HFF), CByte(DataLen And &HFF), 0}
                            Marshal.Copy(DataBuffer, 0, DataPtr, DataLen)
                            succ = TapeSCSIIOCtlUnmanaged(handle, cdbData, DataPtr, CUInt(DataLen), 0, 60000, sense, cancellationToken)
                            If succ Then
                                Exit Do
                            Else
                                Dim ActiveFrm = ApplicationWheels.GetActiveWindow()
                                Dim dResult As DialogResult
                                ActiveFrm.Invoke(Sub() dResult = MessageBox.Show(ActiveFrm, $"写入出错：SCSI指令执行失败", "警告", MessageBoxButtons.AbortRetryIgnore))
                                Select Case dResult
                                    Case DialogResult.Abort
                                        Exit While
                                    Case DialogResult.Retry
                                    Case DialogResult.Ignore
                                        Exit Do
                                End Select
                            End If
                        Loop
                End Select
                DataLen = fs.Read(DataBuffer, 0, BlockLen)
            End While
            If Not succ Then
                Throw New SCSIFailureException("SCSI write", LastWin32Error, sense)
            End If
            Return {0, 0, 0}
        Finally
            If fs IsNot Nothing Then
                Try
                    fs.Close()
                Catch
                End Try
            End If
            Marshal.FreeHGlobal(DataPtr)
        End Try
    End Function
    Public Shared Function Flush(TapeDrive As String, Optional cancellationToken As CancellationToken = Nothing) As Byte()
        Return WriteFileMark(TapeDrive, 0, cancellationToken)
    End Function
    Public Shared Function Flush(handle As IntPtr, Optional cancellationToken As CancellationToken = Nothing) As Byte()
        Return WriteFileMark(handle, 0, cancellationToken)
    End Function
    Public Shared Function WriteFileMark(TapeDrive As String, Optional cancellationToken As CancellationToken = Nothing) As Byte()
        Return WriteFileMark(TapeDrive, 1, cancellationToken)
    End Function
    Public Shared Function WriteFileMark(TapeDrive As String, ByVal Number As UInteger, Optional cancellationToken As CancellationToken = Nothing) As Byte()
        Return RunOpenedTapeOperation(TapeDrive,
                                      Function(handle As IntPtr) As Byte()
                                          Return WriteFileMark(handle, Number, cancellationToken)
                                      End Function)
    End Function
    Public Shared Function WriteFileMark(handle As IntPtr, Optional cancellationToken As CancellationToken = Nothing) As Byte()
        Return WriteFileMark(handle, 1, cancellationToken)
    End Function
    Public Shared Function WriteFileMark(handle As IntPtr, ByVal Number As UInteger, Optional cancellationToken As CancellationToken = Nothing) As Byte()
        Dim sense(63) As Byte
        cancellationToken.ThrowIfCancellationRequested()
        Select Case DriverTypeSetting
            Case DriverType.TapeStream
                Dim ts As TapeImage = Nothing
                TapeStreamMapping.MappingTable.TryGetValue(handle, ts)
                If ts Is Nothing Then
                    sense = TapeImage.SenseData.NotPresent
                    Return sense
                Else
                    ts.WriteFilemark(CInt(Number))
                End If
            Case Else
                Dim commandResult As Boolean = SendSCSICommandUnmanaged(handle, {&H10, CByte(Math.Min(Number, 1)), CByte(Number >> 16 And &HFF), CByte(Number >> 8 And &HFF), CByte(Number And &HFF), 0}, {}, 1,
                                                                        Function(senseData As Byte()) As Boolean
                                                                            sense = senseData
                                                                            Return True
                                                                        End Function,
                                                                        cancellationToken:=cancellationToken)
                ThrowIfSCSIFailed(commandResult, "SCSI write file mark", sense)
        End Select
        Return sense
    End Function
    Public Shared Function GetMAMAttributeBytes(handle As IntPtr, PageCode_H As Byte, PageCode_L As Byte) As Byte()
        Return GetMAMAttributeBytes(handle, PageCode_H, PageCode_L, 0)
    End Function
    Public Shared Function GetMAMAttributeBytes(handle As IntPtr, PageCode_H As Byte, PageCode_L As Byte, ByVal PartitionNumber As Byte) As Byte()
        Dim DATA_LEN As Integer = 0
        Dim BCArray(DATA_LEN + 8) As Byte
        Dim cdbData As Byte() = {&H8C, 0, 0, 0, 0, 0, 0, PartitionNumber,
                    PageCode_H,
                    PageCode_L,
                    CByte(((DATA_LEN + 9) >> 24) And &HFF),
                    CByte(((DATA_LEN + 9) >> 16) And &HFF),
                    CByte(((DATA_LEN + 9) >> 8) And &HFF),
                    CByte((DATA_LEN + 9) And &HFF), 0, 0}
        Dim Result As Byte() = {}
        Select Case DriverTypeSetting
            Case DriverType.TapeStream
                Dim ts As TapeImage = Nothing
                TapeStreamMapping.MappingTable.TryGetValue(handle, ts)
                If ts IsNot Nothing Then
                    Dim sense() As Byte = {}
                    Dim headerResult As Boolean = ts.HandleSCSICommand(cdbData, Nothing, 1, 9, BCArray, sense)
                    If Not headerResult Then Throw New SCSIFailureException("SCSI read MAM header", 31, sense)
                    DATA_LEN = CInt(BCArray(7)) << 8 Or BCArray(8)
                    If DATA_LEN > 0 Then
                        Dim BCArray2(DATA_LEN + 8) As Byte
                        cdbData = {&H8C, 0, 0, 0, 0, 0, 0, PartitionNumber,
                                PageCode_H,
                                PageCode_L,
                                CByte(((DATA_LEN + 9) >> 24) And &HFF),
                                CByte(((DATA_LEN + 9) >> 16) And &HFF),
                                CByte(((DATA_LEN + 9) >> 8) And &HFF),
                                CByte((DATA_LEN + 9) And &HFF), 0, 0}
                        Dim dataResult As Boolean = ts.HandleSCSICommand(cdbData, Nothing, 1, DATA_LEN + 9, BCArray2, sense)
                        If Not dataResult Then Throw New SCSIFailureException("SCSI read MAM data", 31, sense)
                        Result = BCArray2.Skip(9).ToArray()
                        Return Result
                    Else
                        Return {}
                    End If
                Else
                    Return {}
                End If
            Case Else
                Dim sense(63) As Byte
                Dim dataBuffer As IntPtr = Marshal.AllocHGlobal(DATA_LEN + 9)
                Try
                    Marshal.Copy(BCArray, 0, dataBuffer, 9)
                    SyncLock SCSILockManager.GetLock(handle)
                        Dim headerResult As Boolean = TapeSCSIIOCtlUnmanaged(handle, cdbData, dataBuffer, CUInt(DATA_LEN + 9), 1, 60000, sense)
                        ThrowIfSCSIFailed(headerResult, "SCSI read MAM header", sense)
                        Marshal.Copy(dataBuffer, BCArray, 0, DATA_LEN + 9)

                        DATA_LEN = CInt(BCArray(7)) << 8 Or BCArray(8)
                        If DATA_LEN > 0 Then
                            Dim dataBuffer2 As IntPtr = Marshal.AllocHGlobal(DATA_LEN + 9)
                            Try
                                Dim BCArray2(DATA_LEN + 8) As Byte
                                Marshal.Copy(BCArray2, 0, dataBuffer2, DATA_LEN + 9)
                                cdbData = {&H8C, 0, 0, 0, 0, 0, 0, PartitionNumber,
                                    PageCode_H,
                                    PageCode_L,
                                    CByte(((DATA_LEN + 9) >> 24) And &HFF),
                                    CByte(((DATA_LEN + 9) >> 16) And &HFF),
                                    CByte(((DATA_LEN + 9) >> 8) And &HFF),
                                    CByte((DATA_LEN + 9) And &HFF), 0, 0}
                                Dim dataResult As Boolean = TapeSCSIIOCtlUnmanaged(handle, cdbData, dataBuffer2, CUInt(DATA_LEN + 9), 1, 60000, sense)
                                ThrowIfSCSIFailed(dataResult, "SCSI read MAM data", sense)
                                Marshal.Copy(dataBuffer2, BCArray2, 0, DATA_LEN + 9)
                                Result = BCArray2.Skip(9).ToArray()
                            Finally
                                Marshal.FreeHGlobal(dataBuffer2)
                            End Try
                        End If
                    End SyncLock
                    Return Result
                Finally
                    Marshal.FreeHGlobal(dataBuffer)
                End Try
        End Select
    End Function
    Public Shared Function GetMAMAttributeBytes(TapeDrive As String, PageCode_H As Byte, PageCode_L As Byte) As Byte()
        Return GetMAMAttributeBytes(TapeDrive, PageCode_H, PageCode_L, 0)
    End Function
    Public Shared Function GetMAMAttributeBytes(TapeDrive As String, PageCode_H As Byte, PageCode_L As Byte, ByVal PartitionNumber As Byte) As Byte()
        Return RunOpenedTapeOperation(TapeDrive,
                                      Function(handle As IntPtr) As Byte()
                                          Return GetMAMAttributeBytes(handle, PageCode_H, PageCode_L, PartitionNumber)
                                      End Function)

    End Function
    Public Shared Function Byte2Hex(bytes As Byte(), Optional ByVal ReadablePrint As Boolean = False) As String
        If ReadablePrint Then Return ByteArrayToString(bytes)
        If bytes Is Nothing Then Return ""
        If bytes.Length = 0 Then Return ""
        Dim sb As New StringBuilder
        For i As Integer = 0 To bytes.Length - 1
            sb.Append(Convert.ToString((bytes(i) And &HFF) + &H100, 16).Substring(1).ToUpper)
            sb.Append(" ")
            If i Mod 16 = 15 Then
                sb.Append(vbCrLf)
            End If
        Next
        Return sb.ToString()
    End Function
    Public Shared Function RichByte2Hex(bytes As Byte(), Optional ByVal TextShow As Boolean = False) As String
        Const HalfWidthChars As String = "`~!@#$%^&*()_+-=|\ <>?,./:;""''{}[]0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz"
        If bytes Is Nothing Then Return ""
        If bytes.Length = 0 Then Return ""
        Dim sb As New StringBuilder
        Dim tb As String = ""
        Dim ln As New StringBuilder
        For i As Integer = 0 To bytes.Length - 1
            If i Mod 16 = 0 And TextShow Then
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
    Public Shared Function ByteArrayToString(bytesArray As Byte(), Optional ByVal TextOnly As Boolean = False) As String
        Dim strBuilder As New StringBuilder()
        Dim rowSize As Integer = 16
        Dim numRows As Integer = CInt(Math.Ceiling(bytesArray.Length / rowSize))

        For row As Integer = 0 To numRows - 1
            Dim rowStart As Integer = row * rowSize
            Dim rowEnd As Integer = Math.Min((row + 1) * rowSize, bytesArray.Length)
            Dim rowBytes As Byte() = bytesArray.Skip(rowStart).Take(rowEnd - rowStart).ToArray()

            If Not TextOnly Then
                ' Append the hex values for this row
                strBuilder.Append($"{rowStart:X8}h: ")
                For Each b As Byte In rowBytes
                    strBuilder.Append($"{b:X2} ")
                Next
                strBuilder.Append(" "c, (rowSize - rowBytes.Length) * 3)
                strBuilder.Append("  ")
            End If
            ' Append the ASCII characters for this row
            For Each b As Byte In rowBytes
                strBuilder.Append(If(b >= 32 AndAlso b <= 126, ChrW(b), "."c))
            Next
            If Not TextOnly Then
                ' Append a newline character for the next row
                strBuilder.AppendLine()
            End If
        Next

        Return strBuilder.ToString()
    End Function

    <Serializable>
    Public Class MAMAttributeList
        Public Property Content As New List(Of MAMAttribute)
        Public Function GetSerializedText() As String
            Dim writer As New XmlSerializer(GetType(MAMAttributeList))
            Dim tmpf As String = Application.StartupPath & "\" & Now.ToString("MAM_yyyyMMdd_HHmmss.fffffff.tmp")
            While File.Exists(tmpf)
                tmpf = Application.StartupPath & "\" & Now.ToString("MAM_yyyyMMdd_HHmmss.fffffff.tmp")
            End While
            Dim ms As New FileStream(tmpf, FileMode.Create)
            Dim t As TextWriter = New StreamWriter(ms, New UTF8Encoding(False))
            writer.Serialize(t, Me)
            ms.Close()
            Dim soutp As New StreamReader(tmpf)
            Dim sout As New StringBuilder
            While Not soutp.EndOfStream
                sout.AppendLine(soutp.ReadLine)
            End While
            soutp.Close()
            File.Delete(tmpf)
            Return sout.ToString()
        End Function
        Public Sub SaveSerializedText(ByVal FileName As String)
            Dim writer As New XmlSerializer(GetType(MAMAttributeList))
            Dim ms As New FileStream(FileName, FileMode.Create)
            Dim t As TextWriter = New StreamWriter(ms, New UTF8Encoding(False))
            writer.Serialize(t, Me)
            ms.Close()
        End Sub
        Public Shared Function FromXML(s As String) As MAMAttributeList
            Dim reader As New XmlSerializer(GetType(MAMAttributeList))
            Dim t As TextReader = New StringReader(s)
            Return CType(reader.Deserialize(t), MAMAttributeList)
        End Function
    End Class
    <Serializable>
    Public Class MAMAttribute
        Public Property ID As UInt16
        Public Property ID_HexValue As String
            Get
                Return Byte2Hex({ID_MSB, ID_LSB})
            End Get
            Set(value As String)

            End Set
        End Property
        <XmlIgnore>
        Public ReadOnly Property ID_MSB As Byte
            Get
                Return CByte((ID >> 8) And &HFF)
            End Get
        End Property
        <XmlIgnore>
        Public ReadOnly Property ID_LSB As Byte
            Get
                Return CByte(ID And &HFF)
            End Get
        End Property
        Public Property RawData As Byte() = Array.Empty(Of Byte)()
        Public Property Length As Integer
            Get
                Return RawData.Length
            End Get
            Set(value As Integer)

            End Set
        End Property
        Public Property AsString As String
            Get
                Try
                    Return Encoding.UTF8.GetString(RawData)
                Catch ex As Exception
                    Return ""
                End Try
            End Get
            Set(value As String)

            End Set
        End Property
        Public Property AsNumeric As Int64
            Get
                If RawData.Length <> 1 And RawData.Length <> 2 And RawData.Length <> 4 And RawData.Length <> 8 Then Return 0
                Dim result As Int64 = 0
                For i As Integer = 0 To Math.Min(7, RawData.Length - 1)
                    result <<= 8
                    result = result Or RawData(i)
                Next
                Return result
            End Get
            Set(value As Int64)

            End Set
        End Property
        Public Property AsHexText As String
            Get
                Return Byte2Hex(RawData)
            End Get
            Set(value As String)

            End Set
        End Property
        Public Shared Function FromTapeDrive(driveHandle As IntPtr, PageCode_H As Byte, PageCode_L As Byte, Optional ByVal PartitionNumber As Byte = 0) As MAMAttribute
            Dim RawData As Byte() = GetMAMAttributeBytes(driveHandle, PageCode_H, PageCode_L, PartitionNumber)
            If RawData.Length = 0 Then Return Nothing
            Return New MAMAttribute With {.ID = (CUShort(PageCode_H) << 8) Or PageCode_L, .RawData = RawData}
        End Function
        Public Shared Function FromTapeDrive(TapeDrive As String, PageCode_H As Byte, PageCode_L As Byte, Optional ByVal PartitionNumber As Byte = 0) As MAMAttribute
            Dim RawData As Byte() = GetMAMAttributeBytes(TapeDrive, PageCode_H, PageCode_L, PartitionNumber)
            If RawData.Length = 0 Then Return Nothing
            Return New MAMAttribute With {.ID = (CUShort(PageCode_H) << 8) Or PageCode_L, .RawData = RawData}
        End Function
        Public Shared Function FromTapeDrive(TapeDrive As String, PageCode As UInt16, Optional ByVal PartitionNumber As Byte = 0) As MAMAttribute
            Return FromTapeDrive(TapeDrive:=TapeDrive, PageCode_H:=CByte((PageCode >> 8) And &HFF), PageCode_L:=CByte(PageCode And &HFF), PartitionNumber:=PartitionNumber)
        End Function

        Public Function GetSerializedText(Optional ByVal ReduceSize As Boolean = True) As String
            Dim writer As New XmlSerializer(GetType(MAMAttribute))
            Dim sb As New StringBuilder()
            Dim t As TextWriter = New StringWriter(sb)
            writer.Serialize(t, Me)
            t.Close()
            Return sb.ToString
        End Function
    End Class

    Private Shared Function SendTapeImageCommand(handle As IntPtr,
                                                 cdbData As Byte(),
                                                 ByRef Data As Byte(),
                                                 dataLen As Integer,
                                                 dataIn As Byte,
                                                 senseReport As Func(Of Byte(), Boolean),
                                                 cancellationToken As CancellationToken) As Boolean
        Dim tapeImage As TapeImage = Nothing
        TapeStreamMapping.MappingTable.TryGetValue(handle, tapeImage)
        If tapeImage Is Nothing Then
            Dim notPresentSense As Byte() = TapeImage.SenseData.NotPresent
            SetLastNativeError(1167)
            If senseReport IsNot Nothing Then senseReport(notPresentSense)
            Return False
        End If

        SyncLock SCSILockManager.GetLock(handle)
            cancellationToken.ThrowIfCancellationRequested()

            Dim parameter As Byte() = Array.Empty(Of Byte)()
            If Data IsNot Nothing AndAlso dataLen > 0 Then
                If dataLen = Data.Length Then
                    parameter = Data
                Else
                    ReDim parameter(dataLen - 1)
                    Array.Copy(Data, parameter, Math.Min(dataLen, Data.Length))
                End If
            End If

            Dim response As Byte() = Array.Empty(Of Byte)()
            Dim sense As Byte() = Array.Empty(Of Byte)()
            Dim commandResult As Boolean = tapeImage.HandleSCSICommand(cdbData,
                                                                        parameter,
                                                                        dataIn,
                                                                        Math.Max(0, dataLen),
                                                                        response,
                                                                        sense)
            sense = NormalizeSCSISense(sense, Not commandResult)

            If commandResult AndAlso Data IsNot Nothing AndAlso dataIn <> 1 Then
                Dim copySource As Byte() = If(dataIn = 0, parameter, response)
                If copySource IsNot Nothing AndAlso copySource.Length > 0 Then
                    Array.Copy(copySource, Data, Math.Min(dataLen, Math.Min(Data.Length, copySource.Length)))
                End If
            End If

            If senseReport IsNot Nothing Then senseReport(sense)

            Dim senseKey As Integer = If(sense.Length > 2, sense(2) And &HF, 0)
            If Not commandResult OrElse senseKey <> 0 Then
                SetLastNativeError(31)
                Return False
            End If
            Return True
        End SyncLock
    End Function

    Public Shared Function SendSCSICommand(handle As IntPtr, cdbData As Byte(), Optional ByRef Data As Byte() = Nothing, Optional DataIn As Byte = 2, Optional ByVal senseReport As Func(Of Byte(), Boolean) = Nothing, Optional ByVal TimeOut As Integer = 60000, Optional cancellationToken As CancellationToken = Nothing) As Boolean
        If DriverTypeSetting = DriverType.TapeStream Then
            Return SendTapeImageCommand(handle,
                                         cdbData,
                                         Data,
                                         If(Data Is Nothing, 0, Data.Length),
                                         DataIn,
                                         senseReport,
                                         cancellationToken)
        End If

        Dim dataBufferPtr As IntPtr
        Dim dataLen As Integer = 0
        If Data IsNot Nothing Then
            dataLen = Data.Length
            dataBufferPtr = Marshal.AllocHGlobal(Data.Length)
            Marshal.Copy(Data, 0, dataBufferPtr, Data.Length)
        Else
            dataBufferPtr = Marshal.AllocHGlobal(128)
        End If
        Dim senseBuffer(63) As Byte
        Try
            Dim succ As Boolean = TapeSCSIIOCtlUnmanaged(handle,
                                                          cdbData,
                                                          dataBufferPtr,
                                                          CUInt(dataLen),
                                                          DataIn,
                                                          CUInt(TimeOut),
                                                          senseBuffer,
                                                          cancellationToken)
            If succ AndAlso Data IsNot Nothing AndAlso DataIn <> 1 Then Marshal.Copy(dataBufferPtr, Data, 0, Data.Length)
            If senseReport IsNot Nothing Then
                senseReport(senseBuffer)
            End If
            Return succ
        Finally
            Marshal.FreeHGlobal(dataBufferPtr)
        End Try
    End Function
    Public Shared Function SendSCSICommand(handle As IntPtr, cdbData As Byte(), ByRef Data As Byte(), ByVal DataLen As Integer, Optional DataIn As Byte = 2, Optional ByVal senseReport As Func(Of Byte(), Boolean) = Nothing, Optional ByVal TimeOut As Integer = 60000, Optional cancellationToken As CancellationToken = Nothing) As Boolean
        If DriverTypeSetting = DriverType.TapeStream Then
            Return SendTapeImageCommand(handle, cdbData, Data, DataLen, DataIn, senseReport, cancellationToken)
        End If

        Dim dataBufferPtr As IntPtr
        If Data IsNot Nothing Then
            dataBufferPtr = Marshal.AllocHGlobal(DataLen)
            Marshal.Copy(Data, 0, dataBufferPtr, DataLen)
        Else
            dataBufferPtr = Marshal.AllocHGlobal(128)
        End If
        Dim senseBuffer(63) As Byte
        Try
            Dim succ As Boolean = TapeSCSIIOCtlUnmanaged(handle,
                                                          cdbData,
                                                          dataBufferPtr,
                                                          CUInt(DataLen),
                                                          DataIn,
                                                          CUInt(TimeOut),
                                                          senseBuffer,
                                                          cancellationToken)
            If succ AndAlso Data IsNot Nothing AndAlso DataIn <> 1 Then Marshal.Copy(dataBufferPtr, Data, 0, DataLen)
            If senseReport IsNot Nothing Then
                senseReport(senseBuffer)
            End If
            Return succ
        Finally
            Marshal.FreeHGlobal(dataBufferPtr)
        End Try
    End Function
    Public Shared Function SendSCSICommand(TapeDrive As String, cdbData As Byte(), Optional ByRef Data As Byte() = Nothing, Optional DataIn As Byte = 2, Optional ByVal senseReport As Func(Of Byte(), Boolean) = Nothing, Optional ByVal TimeOut As Integer = 60000, Optional cancellationToken As CancellationToken = Nothing) As Boolean
        SyncLock SCSILockManager.GetLock(TapeDrive)
            Dim handle As IntPtr
            If Not OpenTapeDrive(TapeDrive, handle) Then Throw New Exception($"Cannot open {TapeDrive}")
            Dim result As Boolean
            Try
                result = SendSCSICommand(handle, cdbData, Data, DataIn, senseReport, TimeOut, cancellationToken)
            Catch
                Try
                    CloseTapeDrive(handle)
                Catch
                End Try
                Throw
            End Try
            If Not CloseTapeDrive(handle) Then Throw New Exception($"Cannot close {TapeDrive}")
            Return result
        End SyncLock
    End Function
    Public Shared Function SendSCSICommandUnmanaged(handle As IntPtr, cdbData As Byte(), Optional ByRef Data As Byte() = Nothing, Optional DataIn As Byte = 2, Optional ByVal senseReport As Func(Of Byte(), Boolean) = Nothing, Optional ByVal TimeOut As Integer = 60000, Optional cancellationToken As CancellationToken = Nothing) As Boolean
        If DriverTypeSetting = DriverType.TapeStream Then
            Return SendTapeImageCommand(handle,
                                         cdbData,
                                         Data,
                                         If(Data Is Nothing, 0, Data.Length),
                                         DataIn,
                                         senseReport,
                                         cancellationToken)
        End If

        Dim dataBufferPtr As IntPtr
        Dim dataLen As Integer = 0
        If Data IsNot Nothing Then
            dataLen = Data.Length
            dataBufferPtr = Marshal.AllocHGlobal(Data.Length)
            Marshal.Copy(Data, 0, dataBufferPtr, Data.Length)
        Else
            dataBufferPtr = Marshal.AllocHGlobal(128)
        End If

        Dim senseBuffer(63) As Byte
        Try
            Dim succ As Boolean = TapeSCSIIOCtlUnmanaged(handle,
                                                          cdbData,
                                                          dataBufferPtr,
                                                          CUInt(dataLen),
                                                          DataIn,
                                                          CUInt(TimeOut),
                                                          senseBuffer,
                                                          cancellationToken)
            If succ AndAlso Data IsNot Nothing Then Marshal.Copy(dataBufferPtr, Data, 0, Data.Length)
            If senseReport IsNot Nothing Then
                senseReport(senseBuffer)
            End If
            Return succ
        Finally
            Marshal.FreeHGlobal(dataBufferPtr)
        End Try
    End Function

    Public Const DEFAULT_LOG_DIR As String = "C:\ProgramData\HPE\LTFS"
    Public Const DEFAULT_WORK_DIR As String = "C:\tmp\LTFS"
    Public Shared Function GetTapeDriveList() As List(Of BlockDevice)
        Dim LDrive As New List(Of BlockDevice)
        Dim obj As List(Of NativeDevice)
        Try
            obj = NativeMethods.EnumerateDevices("SCSI").ToList()
            obj.AddRange(NativeMethods.EnumerateDevices("USBSTOR").ToList())
            obj.AddRange(NativeMethods.EnumerateDevices("MPIO").ToList())
        Catch ex As Exception
            obj = New List(Of NativeDevice)
        End Try
        Dim tapeobj As New List(Of NativeDevice)
        For Each dev As NativeDevice In obj
            If dev.Present Then
                If dev.ClassName.ToLower = "tapedrive" OrElse dev.ClassName.ToLower = "unknown" Then
                    tapeobj.Add(dev)
                End If
            End If
        Next
        For Each dev As NativeDevice In tapeobj
            If dev.PDOName = "" Then Continue For
            Dim devicePath As String = $"\\.\Globalroot{dev.PDOName}"
            Dim deviceNumberResult As NativeStorageDeviceNumberResult = NativeMethods.QueryStorageDeviceNumber(devicePath)
            SetLastNativeError(deviceNumberResult.Win32Error)
            Dim result As Boolean = deviceNumberResult.Succeeded
            Dim drv As BlockDevice = Inquiry(devicePath)
            If drv Is Nothing Then drv = New BlockDevice()
            drv.DevicePath = devicePath
            If result Then
                drv.DevIndex = CStr(deviceNumberResult.DeviceNumber)
                drv.DevicePath = $"\\.\TAPE{drv.DevIndex}"
            End If
            LDrive.Add(drv)
        Next
        Dim s() As String

        LDrive.Sort(New Comparison(Of BlockDevice)(
                        Function(A As BlockDevice, B As BlockDevice) As Integer
                            Try
                                Return Integer.Parse(A.DevIndex).CompareTo(Integer.Parse(B.DevIndex))
                            Catch ex As Exception
                                Return A.DevIndex.CompareTo(B.DevIndex)
                            End Try
                        End Function))
        s = GetDriveMappings().Split({vbCr, vbLf}, StringSplitOptions.RemoveEmptyEntries)
        For Each t As String In s
            Dim q() As String = t.Split({"|"}, StringSplitOptions.None)
            If q.Length = 3 Then
                For Each Drv As BlockDevice In LDrive
                    If "TAPE" & Drv.DevIndex = q(1) And Drv.SerialNumber = q(2) Then
                        Drv.DriveLetter = q(0)
                    End If
                Next
            End If
        Next


        If Directory.Exists(My.Settings.customDevicePath) Then
            For Each f As FileInfo In (New DirectoryInfo(My.Settings.customDevicePath).GetFiles)
                If f.Extension.ToLower() = ".xml" Then
                    Dim fcnt As String = File.ReadAllText(f.FullName)
                    Try
                        Dim blkdev As BlockDevice = BlockDevice.FromXML(fcnt)
                        LDrive.Add(blkdev)
                    Catch ex As Exception

                    End Try
                End If
            Next
        End If
        Return LDrive
    End Function
    Public Shared Function GetDiskDriveList() As List(Of BlockDevice)
        Dim LDrive As New List(Of BlockDevice)
        Dim obj As List(Of NativeDevice) = NativeMethods.EnumerateDevices("SCSI").ToList()
        Dim diskobj As New List(Of NativeDevice)
        For Each dev As NativeDevice In obj
            If dev.Present Then
                If dev.ClassName.ToLower.Contains("disk") Then
                    diskobj.Add(dev)
                End If
            End If
        Next
        Try
            obj = NativeMethods.EnumerateDevices("MPIO").ToList()
        Catch ex As Exception
            obj = New List(Of NativeDevice)
        End Try
        For Each dev As NativeDevice In obj
            If dev.Present Then
                If dev.ClassName.ToLower.Contains("disk") Then
                    diskobj.Add(dev)
                End If
            End If
        Next
        For Each dev As NativeDevice In diskobj

            Dim devicePath As String = $"\\.\Globalroot{dev.PDOName}"
            Dim deviceNumberResult As NativeStorageDeviceNumberResult = NativeMethods.QueryStorageDeviceNumber(devicePath)
            SetLastNativeError(deviceNumberResult.Win32Error)
            Dim result As Boolean = deviceNumberResult.Succeeded
            Dim drv As BlockDevice
            If Not result Then
                drv = Inquiry(devicePath)
            Else
                drv = Inquiry($"\\.\PhysicalDrive{deviceNumberResult.DeviceNumber}")
            End If
            If drv Is Nothing Then Continue For
            drv.DeviceType = "PhysicalDrive"
            If result Then
                drv.DevIndex = CStr(deviceNumberResult.DeviceNumber)
                drv.DevicePath = $"\\.\PhysicalDrive{drv.DevIndex}"
            Else
                drv.DevicePath = devicePath
            End If
            LDrive.Add(drv)
        Next

        Dim s() As String

        LDrive.Sort(New Comparison(Of BlockDevice)(
                        Function(A As BlockDevice, B As BlockDevice) As Integer
                            Try
                                Return Integer.Parse(A.DevIndex).CompareTo(Integer.Parse(B.DevIndex))
                            Catch ex As Exception
                                Return A.DevIndex.CompareTo(B.DevIndex)
                            End Try
                        End Function))
        s = GetDriveMappings().Split({vbCr, vbLf}, StringSplitOptions.RemoveEmptyEntries)
        For Each t As String In s
            Dim q() As String = t.Split({"|"}, StringSplitOptions.None)
            If q.Length = 3 Then
                For Each Drv As BlockDevice In LDrive
                    If "PhysicalDrive" & Drv.DevIndex = q(1) And Drv.SerialNumber = q(2) Then
                        Drv.DriveLetter = q(0)
                    End If
                Next
            End If
        Next
        Return LDrive
    End Function
    Public Shared Function GetMediumChangerList() As List(Of MediumChanger)
        Dim LChanger As New List(Of MediumChanger)
        Dim obj As List(Of NativeDevice) = NativeMethods.EnumerateDevices("SCSI").ToList()
        obj.AddRange(NativeMethods.EnumerateDevices("MPIO").ToList())
        Dim tapeobj As New List(Of NativeDevice)
        For Each dev As NativeDevice In obj
            If dev.Present Then
                If dev.ClassName.ToLower = "mediumchanger" Then
                    tapeobj.Add(dev)
                End If
            End If
        Next
        For Each dev As NativeDevice In tapeobj
            Dim devicePath As String = $"\\.\Globalroot{dev.PDOName}"
            Dim deviceNumberResult As NativeStorageDeviceNumberResult = NativeMethods.QueryStorageDeviceNumber(devicePath)
            SetLastNativeError(deviceNumberResult.Win32Error)
            Dim result As Boolean = deviceNumberResult.Succeeded
            Dim drv As BlockDevice = Inquiry(devicePath)
            If drv Is Nothing Then Continue For
            drv.DeviceType = "CHANGER"
            drv.DevicePath = devicePath
            If result Then
                drv.DevIndex = CStr(deviceNumberResult.DeviceNumber)
                drv.DevicePath = $"\\.\CHANGER{drv.DevIndex}"
            End If
            Dim nc As New MediumChanger(drv.DevIndex, drv.SerialNumber, drv.VendorId, drv.ProductId)
            nc.device = drv
            LChanger.Add(nc)
        Next

        LChanger.Sort(New Comparison(Of MediumChanger)(
                        Function(A As MediumChanger, B As MediumChanger) As Integer
                            Try
                                Return Integer.Parse(A.DevIndex).CompareTo(Integer.Parse(B.DevIndex))
                            Catch ex As Exception
                                Return A.DevIndex.CompareTo(B.DevIndex)
                            End Try
                        End Function))
        Return LChanger
    End Function
    Public Shared Function GetHBAList() As List(Of BlockDevice)
        Dim LAdapter As New List(Of BlockDevice)
        Dim obj As List(Of NativeDevice) = NativeMethods.EnumerateDevices("PCI").ToList()
        Dim devobj As New List(Of NativeDevice)
        For Each dev As NativeDevice In obj
            If dev.Present Then
                If dev.ClassName.ToLower = "scsiadapter" Then
                    devobj.Add(dev)
                End If
            End If
        Next
        For Each dev As NativeDevice In devobj
            Dim drv As New BlockDevice
            drv.DeviceType = "SCSIAdapter"
            drv.DevicePath = $"\\.\Globalroot{dev.PDOName}"
            drv.ProductId = dev.Name
            drv.SerialNumber = dev.PDOName.Substring(8)
            'drv.VendorId = dev.Manufacturer
            LAdapter.Add(drv)
        Next
        Return LAdapter
    End Function
    Private Shared Function NativeCommandText(result As NativeTextResult) As String
        If result Is Nothing Then
            SetLastNativeError(31)
            Return String.Empty
        End If

        SetLastNativeError(result.Win32Error)
        Return result.Text
    End Function

    Public Shared Function GetDriveMappings() As String
        Return NativeCommandText(NativeLtfsCommands.GetDriveMappings())
    End Function

    Private Shared Function ResolveMappedTapeDrive(driveLetter As Char) As String
        Try
            Dim mappings As String = GetDriveMappings()
            If String.IsNullOrWhiteSpace(mappings) Then Return Nothing

            For Each mapping As String In mappings.Split({vbCr, vbLf}, StringSplitOptions.RemoveEmptyEntries)
                Dim fields As String() = mapping.Split({"|"}, StringSplitOptions.None)
                If fields.Length < 2 Then Continue For

                Dim mappedLetter As String = fields(0).Trim().TrimEnd(":"c)
                If Not String.Equals(mappedLetter, driveLetter.ToString(), StringComparison.OrdinalIgnoreCase) Then Continue For

                Dim deviceName As String = fields(1).Trim()
                If deviceName.StartsWith("\\.", StringComparison.OrdinalIgnoreCase) Then Return deviceName
                If deviceName.StartsWith("TAPE", StringComparison.OrdinalIgnoreCase) Then Return "\\." & deviceName
            Next
        Catch
        End Try
        Return Nothing
    End Function

    Private Shared Function EnterMappedTapeOperation(driveLetter As Char) As IDisposable
        Dim devicePath As String = ResolveMappedTapeDrive(driveLetter)
        If String.IsNullOrWhiteSpace(devicePath) Then
            devicePath = $"drive-letter:{Char.ToUpperInvariant(driveLetter)}"
        End If
        Return SCSILockManager.TryEnterOperation(devicePath)
    End Function

    Private Shared Function RunMappedTapeCommand(driveLetter As Char,
                                                  command As Func(Of NativeTextResult)) As String
        Dim operationScope As IDisposable = EnterMappedTapeOperation(driveLetter)
        If operationScope Is Nothing Then
            SetLastNativeError(170)
            Return "Device is busy in another process."
        End If

        Using operationScope
            Return NativeCommandText(command())
        End Using
    End Function

    Public Shared Function StartLtfsService() As String
        Return NativeCommandText(NativeLtfsCommands.StartLtfsService())
    End Function
    Public Shared Function StopLtfsService() As String
        Return NativeCommandText(NativeLtfsCommands.StopLtfsService())
    End Function
    Public Shared Function RemapTapeDrives() As String
        Return NativeCommandText(NativeLtfsCommands.RemapTapeDrives())
    End Function
    Public Shared Function MapTapeDrive(driveLetter As Char, TapeDrive As String, Optional ByVal logDir As String = DEFAULT_LOG_DIR, Optional ByVal workDir As String = DEFAULT_WORK_DIR, Optional ByVal showOffline As Boolean = False) As String
        Dim tapeIndex As Byte = Byte.Parse(TapeDrive.Substring(4))
        Dim operationScope As IDisposable = SCSILockManager.TryEnterOperation(TapeDrive)
        If operationScope Is Nothing Then
            SetLastNativeError(170)
            Return "Device is busy in another process."
        End If

        Using operationScope
            Return NativeCommandText(NativeLtfsCommands.MapTapeDrive(driveLetter.ToString(),
                                                                       TapeDrive,
                                                                       tapeIndex,
                                                                       logDir,
                                                                       workDir,
                                                                       showOffline))
        End Using
    End Function
    Public Shared Function UnMapTapeDrive(driveLetter As Char) As String
        Return RunMappedTapeCommand(driveLetter,
                                     Function() NativeLtfsCommands.UnmapTapeDrive(driveLetter.ToString()))
    End Function
    Public Shared Function LoadTapeDrive(driveLetter As Char, mount As Boolean) As String
        Return RunMappedTapeCommand(driveLetter,
                                     Function() NativeLtfsCommands.LoadTapeDrive(driveLetter.ToString(), mount))
    End Function
    Public Shared Function EjectTapeDrive(driveLetter As Char) As String
        Return RunMappedTapeCommand(driveLetter,
                                     Function() NativeLtfsCommands.EjectTapeDrive(driveLetter.ToString()))
    End Function
    Public Shared Function SetEncryption(handle As IntPtr, Optional ByVal EncryptionKey As Byte() = Nothing, Optional ByVal SenseReport As Func(Of Byte(), Boolean) = Nothing) As Boolean
        Dim result As Boolean = False
        Select Case DriverTypeSetting
            Case DriverType.TapeStream
                ' TapeImage has no hardware encryption command; treating this
                ' as a successful no-op keeps the common setup path uniform.
                result = True

            Case Else
                Dim param As New List(Of Byte)
                If EncryptionKey IsNot Nothing AndAlso EncryptionKey.Length = 32 Then
                    param.AddRange({&H0, &H10, &H0, &H30, &H40, &H34, &H2, &H3, &H1, &H0, &H0, &H0, &H0, &H0, &H0, &H0, &H0, &H0, &H0, &H20})
                    param.AddRange(EncryptionKey.ToList())
                    result = SendSCSICommand(handle, {&HB5, &H20, &H0, &H10, &H0, &H0, &H0, &H0, &H0, &H34, &H0, &H0},
                                    param.ToArray(), 0, SenseReport, 10)
                Else
                    Dim emptyValue(32) As Byte
                    param.AddRange({&H0, &H10, &H0, &H30, &H40, 0, 0, 0, &H1, &H0, &H0, &H0, &H0, &H0, &H0, &H0, &H0, &H0, &H0, &H20})
                    param.AddRange(emptyValue.ToList())
                    result = SendSCSICommand(handle, {&HB5, &H20, &H0, &H10, &H0, &H0, &H0, &H0, &H0, &H34, &H0, &H0},
                                    param.ToArray(), 0, SenseReport, 10)
                End If
        End Select

        Return result
    End Function
    Public Shared Function SetEncryption(TapeDrive As String, Optional ByVal EncryptionKey As Byte() = Nothing, Optional ByVal SenseReport As Func(Of Byte(), Boolean) = Nothing) As Boolean
        Return RunOpenedTapeOperation(TapeDrive,
                                      Function(handle As IntPtr) As Boolean
                                          Return SetEncryption(handle, EncryptionKey, SenseReport)
                                      End Function)
    End Function
    Public Enum LoadOption As Byte
        LoadThreaded = 1
        LoadUnthreaded = 9
        Unthread = &HA
        Eject = 0
    End Enum
    Public Shared Function LoadEject(handle As IntPtr, LoadOption As LoadOption, Optional ByVal EncryptionKey As Byte() = Nothing, Optional ByVal senseReport As Func(Of Byte(), Boolean) = Nothing, Optional ByVal TimeOut As Integer = 600) As Boolean
        Dim sensedata As Byte() = Array.Empty(Of Byte)()
        Dim result As Boolean = False
        Select Case DriverTypeSetting
            Case DriverType.TapeStream
                Dim ts As TapeImage = Nothing
                TapeStreamMapping.MappingTable.TryGetValue(handle, ts)
                If ts Is Nothing Then
                    If senseReport IsNot Nothing Then senseReport(TapeImage.SenseData.NotPresent)
                    Return False
                Else
                    Select Case LoadOption
                        Case LoadOption.Eject, LoadOption.Unthread
                            ts.CloseFile()
                            sensedata = TapeImage.SenseData.NoSense
                        Case LoadOption.LoadThreaded
                            ts.ReOpen()
                            ts.LocateByBlock(0, sensedata)
                    End Select
                    If senseReport IsNot Nothing Then senseReport(sensedata)
                    Return True
                End If
            Case Else
                Dim retrycount As Integer = 1
                While True
                    Dim sensereceived As Boolean = False
                    Dim commandResult As Boolean = SendSCSICommand(handle:=handle, cdbData:=New Byte() {&H1B, 0, 0, 0, LoadOption, 0}, DataIn:=1,
                                                                    senseReport:=Function(sense As Byte()) As Boolean
                                                                                     sensedata = sense
                                                                                     If sense.Length > 0 Then
                                                                                         If sense(0) <> 0 Then
                                                                                             result = False
                                                                                         Else
                                                                                             result = True
                                                                                         End If
                                                                                     Else
                                                                                         result = True
                                                                                     End If
                                                                                     sensereceived = True

                                                                                     Return True
                                                                                 End Function, TimeOut:=TimeOut)
                    If Not commandResult Then result = False
                    While Not sensereceived
                        Threading.Thread.Sleep(100)
                    End While
                    If result Then Exit While
                    If retrycount > 0 Then
                        retrycount -= 1
                        Continue While
                    End If
                    Dim ActiveFrm = ApplicationWheels.GetActiveWindow()
                    Dim dResult As DialogResult
                    ActiveFrm.Invoke(Sub() dResult = MessageBox.Show(ActiveFrm, $"{My.Resources.ResText_WErrSCSI }{vbCrLf}{ParseSenseData(sensedata)}", My.Resources.ResText_Warning, MessageBoxButtons.AbortRetryIgnore))
                    Select Case dResult
                        Case DialogResult.Abort
                            Return False
                        Case DialogResult.Retry

                        Case DialogResult.Ignore
                            Exit While
                    End Select
                End While


                If result AndAlso LoadOption = LoadOption.LoadThreaded AndAlso EncryptionKey IsNot Nothing Then
                    result = SetEncryption(handle:=handle, EncryptionKey:=EncryptionKey)
                End If
                If senseReport IsNot Nothing Then
                    senseReport(sensedata)
                End If
        End Select

        Return result
    End Function
    Public Shared Function LoadEject(TapeDrive As String, LoadOption As LoadOption, Optional ByVal EncryptionKey As Byte() = Nothing, Optional ByVal senseReport As Func(Of Byte(), Boolean) = Nothing, Optional ByVal TimeOut As Integer = 600) As Boolean
        Return RunOpenedTapeOperation(TapeDrive,
                                      Function(handle As IntPtr) As Boolean
                                          Return LoadEject(handle, LoadOption, EncryptionKey, senseReport, TimeOut)
                                      End Function)
    End Function
    Public Shared Function MountTapeDrive(driveLetter As Char) As String
        Return RunMappedTapeCommand(driveLetter,
                                     Function() NativeLtfsCommands.MountTapeDrive(driveLetter.ToString()))
    End Function
    Public Shared Function CheckTapeMedia(driveLetter As Char) As String
        Return RunMappedTapeCommand(driveLetter,
                                     Function() NativeLtfsCommands.CheckTapeMedia(driveLetter.ToString()))
    End Function
    <Category("Options")>
    <TypeConverter(GetType(ExpandableObjectConverter))>
    Public Class MKLTFS_Param
        Private _Barcode As String = ""
        <Category("Common")>
        <LocalizedDescription("PropertyDescription_mkltfs_Barcode")>
        Public Property Barcode As String
            Get
                Return _Barcode
            End Get
            Set(value As String)
                Dim asciiString As New StringBuilder()
                For Each ch As Char In value
                    If Asc(ch) >= 0 AndAlso Asc(ch) <= 127 Then
                        asciiString.Append(ch)
                    End If
                Next
                value = asciiString.ToString()
                If value.Length > 20 Then value = value.Substring(0, 20)
                _Barcode = value
            End Set
        End Property
        <Category("Common")>
        <LocalizedDescription("PropertyDescription_mkltfs_VolumeLabel")>
        Public Property VolumeLabel As String = ""
        <Category("Expert")>
        <LocalizedDescription("PropertyDescription_mkltfs_MaxExtraPartitionAllowed")>
        Public ReadOnly Property MaxExtraPartitionAllowed As Byte
        Private Property _ExtraPartitionCount As Byte = 1
        <Category("Expert")>
        <LocalizedDescription("PropertyDescription_mkltfs_ExtraPartitionCount")>
        Public Property ExtraPartitionCount As Byte
            Get
                Return _ExtraPartitionCount
            End Get
            Set(value As Byte)
                _ExtraPartitionCount = CByte(Math.Min(1, value))
                _ExtraPartitionCount = Math.Min(_ExtraPartitionCount, MaxExtraPartitionAllowed)
            End Set
        End Property
        Private _BlockLen As Long = 524288
        <Category("Expert")>
        <LocalizedDescription("PropertyDescription_mkltfs_BlockLen")>
        Public Property BlockLen As Long
            Get
                Return _BlockLen
            End Get
            Set(value As Long)
                _BlockLen = Math.Max(1, Math.Min(value, 2097152))
            End Set
        End Property
        <Category("Expert")>
        <LocalizedDescription("PropertyDescription_mkltfs_ImmediateMode")>
        Public Property ImmediateMode As Boolean = True
        <Category("Expert")>
        <LocalizedDescription("PropertyDescription_mkltfs_WORMMode")>
        Public Property WORMMode As Boolean = False
        <Category("Expert")>
        <LocalizedDescription("PropertyDescription_mkltfs_Capacity")>
        Public Property Capacity As UInt16 = &HFFFF
        Private _P0Size As UInt16 = 1
        <Category("Expert")>
        <LocalizedDescription("PropertyDescription_mkltfs_P0Size")>
        Public Property P0Size As UInt16
            Get
                Return _P0Size
            End Get
            Set(value As UInt16)
                _P0Size = value
                If value < &HFFFF Then
                    _P1Size = &HFFFF
                Else
                    _P1Size = 1
                End If
            End Set
        End Property
        Private Property _P1Size As UInt16 = &HFFFF
        <Category("Expert")>
        <LocalizedDescription("PropertyDescription_mkltfs_P1Size")>
        Public Property P1Size As UInt16
            Get
                Return _P1Size
            End Get
            Set(value As UInt16)
                _P1Size = value
                If value < &HFFFF Then
                    _P0Size = &HFFFF
                Else
                    _P0Size = 1
                End If
            End Set
        End Property
        <Category("Expert")>
        <LocalizedDescription("PropertyDescription_mkltfs_EncryptionKey")>
        Public Property EncryptionKey As Byte() = Nothing
        Public Sub New(MaxExtraPartitionAllowed As Byte)
            Me.MaxExtraPartitionAllowed = MaxExtraPartitionAllowed
            _ExtraPartitionCount = Math.Min(_ExtraPartitionCount, Me.MaxExtraPartitionAllowed)
        End Sub
    End Class
    Public Shared Function mkltfs(TapeDrive As String,
                                  Optional ByVal Barcode As String = "",
                                  Optional ByVal VolumeName As String = "",
                                  Optional ByVal ExtraPartitionCount As Byte = 1,
                                  Optional ByVal BlockLen As Long = 524288,
                                  Optional ByVal ImmediateMode As Boolean = True,
                                  Optional ByVal ProgressReport As Action(Of String) = Nothing,
                                  Optional ByVal OnFinish As Action(Of String) = Nothing,
                                  Optional ByVal OnError As Action(Of String) = Nothing,
                                  Optional ByVal Capacity As UInt16 = &HFFFF,
                                  Optional ByVal P0Size As UInt16 = 1,
                                  Optional ByVal P1Size As UInt16 = &HFFFF,
                                  Optional ByVal EncryptionKey As Byte() = Nothing) As Boolean
        Dim handle As IntPtr
        Dim result As Boolean
        If ImmediateMode Then
            SyncLock SCSILockManager.GetLock(TapeDrive)
                If Not OpenTapeDrive(TapeDrive, handle) Then Throw New Exception($"Cannot open {TapeDrive}")
                result = mkltfs(handle, Barcode, VolumeName, ExtraPartitionCount, BlockLen, True, ProgressReport, OnFinish, OnError, Capacity, P0Size, P1Size, EncryptionKey)
                If Not CloseTapeDrive(handle) Then Throw New Exception($"Cannot close {TapeDrive}")
                Return result
            End SyncLock
        Else
            Dim th As New Threading.Thread(
                Sub()
                    SyncLock SCSILockManager.GetLock(TapeDrive)
                        If Not OpenTapeDrive(TapeDrive, handle) Then Throw New Exception($"Cannot open {TapeDrive}")
                        mkltfs(handle, Barcode, VolumeName, ExtraPartitionCount, BlockLen, True, ProgressReport, OnFinish, OnError, Capacity, P0Size, P1Size, EncryptionKey)
                        If Not CloseTapeDrive(handle) Then Throw New Exception($"Cannot close {TapeDrive}")
                    End SyncLock
                End Sub)
            th.Start()
            Return True
        End If

    End Function
    Public Shared Function mkltfs(handle As IntPtr,
                                  Optional ByVal Barcode As String = "",
                                  Optional ByVal VolumeName As String = "",
                                  Optional ByVal ExtraPartitionCount As Byte = 1,
                                  Optional ByVal BlockLen As Long = 524288,
                                  Optional ByVal ImmediateMode As Boolean = True,
                                  Optional ByVal ProgressReport As Action(Of String) = Nothing,
                                  Optional ByVal OnFinish As Action(Of String) = Nothing,
                                  Optional ByVal OnError As Action(Of String) = Nothing,
                                  Optional ByVal Capacity As UInt16 = &HFFFF,
                                  Optional ByVal P0Size As UInt16 = 1,
                                  Optional ByVal P1Size As UInt16 = &HFFFF,
                                  Optional ByVal EncryptionKey As Byte() = Nothing,
                                  Optional ByVal WORM As Boolean = False) As Boolean
        GlobalBlockLimit = CInt(ReadBlockLimits(handle).MaximumBlockLength)
        FromFile(My.Settings.driveSettingFile)
        BlockLen = Math.Min(BlockLen, GlobalBlockLimit)
        Dim mkltfs_op As Func(Of Boolean) =
            Function()
                Try
                    Dim senseReportFunc As Func(Of Byte(), Boolean) = Function(sense As Byte()) As Boolean
                                                                          If (sense(2) And &HF) = 0 Then Return True
                                                                          ProgressReport($"{ParseSenseData(sense)}")
                                                                          Return False
                                                                      End Function

                    If DriverTypeSetting = DriverType.TapeStream Then
                        TapeStreamMapping.MappingTable(handle).ResetPartitionNumber(ExtraPartitionCount + 1)
                    End If
                    'Load and Thread
                    ProgressReport("Loading..")

                    If LoadEject(handle, LoadOption.LoadThreaded, senseReport:=senseReportFunc) Then
                        ProgressReport("Load OK" & vbCrLf)
                    Else
                        OnError("Load Fail" & vbCrLf)
                        Return False
                    End If
                    ProgressReport("Mode Sense..")
                    Dim ModeData As Byte() = ModeSense(handle, &H11, senseReport:=senseReportFunc)
                    ProgressReport(Byte2Hex(ModeData))
                    ReDim Preserve ModeData(11)
                    Dim MaxExtraPartitionAllowed As Byte = ModeData(2)
                    If DriverTypeSetting = DriverType.TapeStream Then
                        MaxExtraPartitionAllowed = 1
                    End If
                    ExtraPartitionCount = Math.Min(MaxExtraPartitionAllowed, ExtraPartitionCount)
                    ProgressReport($"Extra partitions: {ExtraPartitionCount}")
                    If Not AllowPartition Then ExtraPartitionCount = 0
                    If ExtraPartitionCount > 1 Then ExtraPartitionCount = 1

                    If Not WORM Then
                        'Set Capacity
                        ProgressReport("Set Capacity..")
                        If SetCapacity(handle:=handle, Capacity:=Capacity, senseReport:=senseReportFunc) Then
                            ProgressReport("Set Capacity OK" & vbCrLf)
                        Else
                            OnError("Set Capacity Fail" & vbCrLf)
                            Return False
                        End If
                    End If


                    'Format
                    ProgressReport("Initializing tape..")
                    Dim DisableFormat As Boolean = False
                    Try
                        Dim cmdata As New CMParser(handle)
                        DisableFormat = cmdata.CartridgeMfgData.IsLTO9Plus OrElse (Not cmdata.CartridgeMfgData.IsLTO3Plus) OrElse WORM
                    Catch ex As Exception
                        ProgressReport("CMData parse failed")
                    End Try
                    If DisableFormat Then
                        ProgressReport("Format disabled, skip initialization" & vbCrLf)
                    ElseIf DriverTypeSetting = DriverType.TapeStream Then
                        ProgressReport("Incompatible drive detected, skip initialization" & vbCrLf)
                    Else
                        If SendSCSICommand(handle, {4, 0, 0, 0, 0, 0}, senseReport:=senseReportFunc) Then
                            ProgressReport("Initialization OK" & vbCrLf)
                        Else
                            OnError("Initialization Fail" & vbCrLf)
                            Return False
                        End If
                    End If

                    If ExtraPartitionCount > 0 Then
                        ProgressReport("MODE SELECT - Partition mode page..")
                        If DriverTypeSetting <> DriverType.TapeStream Then
                            'Mode Select:1st Partition to Minimum 
                            If SendSCSICommand(handle:=handle, cdbData:=New Byte() {&H15, &H10, 0, 0, &H10, 0}, Data:=New Byte() {0, 0, &H10, 0, &H11, &HA, CByte(MaxExtraPartitionAllowed), 1, ModeData(4), ModeData(5), 9, ModeData(7), CByte((P0Size >> 8) And &HFF), CByte(P0Size And &HFF), CByte((P1Size >> 8) And &HFF), CByte(P1Size And &HFF)}, DataIn:=0, senseReport:=senseReportFunc) Then
                                ProgressReport("MODE SELECT 11h OK" & vbCrLf)
                            Else
                                OnError("MODE SELECT 11h Fail" & vbCrLf)
                                Return False
                            End If

                        End If
                        'Format
                        ProgressReport("Partitioning..")
                        Select Case DriverTypeSetting
                            Case DriverType.T10K
                                If SendSCSICommand(handle, {4, 0, 2, 0, 0, 0}, Nothing, 0, senseReport:=senseReportFunc) Then
                                    ProgressReport("     OK" & vbCrLf)
                                Else
                                    OnError("     Fail" & vbCrLf)
                                    Return False
                                End If
                            Case DriverType.TapeStream

                            Case Else
                                If SendSCSICommand(handle, {4, 0, 1, 0, 0, 0}, Nothing, 0, senseReport:=senseReportFunc) Then
                                    ProgressReport("     OK" & vbCrLf)
                                Else
                                    OnError("     Fail" & vbCrLf)
                                    Return False
                                End If
                        End Select

                    End If
                    'Set Vendor
                    ProgressReport($"WRITE ATTRIBUTE: Vendor=OPEN..")
                    If SetMAMAttribute(handle, &H800, "OPEN".PadRight(8), SenseReport:=senseReportFunc) Then
                        ProgressReport("WRITE ATTRIBUTE: 0800 OK" & vbCrLf)
                    Else
                        OnError("WRITE ATTRIBUTE: 0800 Fail" & vbCrLf)
                        Return False
                    End If
                    'Set AppName
                    ProgressReport($"WRITE ATTRIBUTE: Application Name = LTFSCopyGUI..")
                    If SetMAMAttribute(handle, &H801, "LTFSCopyGUI".PadRight(32), SenseReport:=senseReportFunc) Then
                        ProgressReport("WRITE ATTRIBUTE: 0801 OK" & vbCrLf)
                    Else
                        OnError("WRITE ATTRIBUTE: 0801 Fail" & vbCrLf)
                        Return False
                    End If
                    'Set Version
                    ProgressReport($"WRITE ATTRIBUTE: Application Version={My.Application.Info.Version.ToString(3)}..")
                    If SetMAMAttribute(handle, &H802, My.Application.Info.Version.ToString(3).PadRight(8), SenseReport:=senseReportFunc) Then
                        ProgressReport("WRITE ATTRIBUTE: 0802 OK" & vbCrLf)
                    Else
                        OnError("WRITE ATTRIBUTE: 0802 Fail" & vbCrLf)
                        Return False
                    End If
                    'Set TextLabel
                    ProgressReport($"WRITE ATTRIBUTE: TextLabel= ..")
                    If SetMAMAttribute(handle, &H803, "".PadRight(160), AttributeFormat.Text, SenseReport:=senseReportFunc) Then
                        ProgressReport("WRITE ATTRIBUTE: 0803 OK" & vbCrLf)
                    Else
                        OnError("WRITE ATTRIBUTE: 0803 Fail" & vbCrLf)
                        Return False
                    End If
                    'Set TLI
                    ProgressReport($"WRITE ATTRIBUTE: Localization Identifier = 0..")
                    If SetMAMAttribute(handle, &H805, {0}, AttributeFormat.Binary, SenseReport:=senseReportFunc) Then
                        ProgressReport("WRITE ATTRIBUTE:0805 OK" & vbCrLf)
                    Else
                        OnError("WRITE ATTRIBUTE:0805 Fail" & vbCrLf)
                        Return False
                    End If
                    'Set Barcode
                    Barcode = Barcode.PadRight(32).Substring(0, 32)
                    ProgressReport($"WRITE ATTRIBUTE: Barcode={Barcode}..")
                    If SetBarcode(handle, Barcode, senseReport:=senseReportFunc) Then
                        ProgressReport("WRITE ATTRIBUTE: 0806 OK" & vbCrLf)
                    Else
                        OnError("WRITE ATTRIBUTE: 0806 Fail" & vbCrLf)
                        Return False
                    End If
                    'Set Version
                    Dim LTFSVersion As String = "2.4.0"
                    If ExtraPartitionCount = 0 Then LTFSVersion = "2.4.1"
                    ProgressReport($"WRITE ATTRIBUTE: Format Version={LTFSVersion}..")
                    If SetMAMAttribute(handle, &H80B, LTFSVersion.PadRight(16), SenseReport:=senseReportFunc) Then
                        ProgressReport("WRITE ATTRIBUTE: 080B OK" & vbCrLf)
                    Else
                        OnError("WRITE ATTRIBUTE: 080B Fail" & vbCrLf)
                        Return False
                    End If
                    'Mode Select:Block Length
                    ProgressReport($"MODE SELECT - Block Size {BlockLen}..")
                    Dim blkSenseData As Byte() = SetBlockSize(handle:=handle, BlockSize:=0)
                    senseReportFunc(blkSenseData)
                    If blkSenseData.Length > 0 Then
                        ProgressReport($"MODE SELECT - Block Size {BlockLen} OK" & vbCrLf)
                    Else
                        OnError($"MODE SELECT - Block Size {BlockLen} Fail" & vbCrLf)
                        Return False
                    End If
                    'Locate
                    ProgressReport("Locate to data partition..")
                    Dim LocateAddCode As UShort = Locate(handle, 0, ExtraPartitionCount)
                    If LocateAddCode = 0 Then
                        ProgressReport($"Locate P{ExtraPartitionCount}B0 OK" & vbCrLf)
                    Else
                        OnError($"Locate P{ExtraPartitionCount}B0 Fail{vbCrLf}{ParseAdditionalSenseCode(LocateAddCode)}" & vbCrLf)
                        Return False
                    End If
                    'Set Encryption key
                    If Not SetEncryption(handle, EncryptionKey, SenseReport:=senseReportFunc) Then
                        OnError("Set Encryption key Fail" & vbCrLf)
                        Return False
                    End If
                    'Write VOL1Label
                    ProgressReport("Write VOL1Label..")
                    If Write(handle, New Vol1Label().GenerateRawData(Barcode)).Length > 0 Then
                        ProgressReport("Write VOL1Label OK" & vbCrLf)
                    Else
                        OnError("Write VOL1Label Fail" & vbCrLf)
                        Return False
                    End If

                    'Write FileMark
                    ProgressReport("Write FileMark..")
                    If WriteFileMark(handle).Length > 0 Then
                        ProgressReport("Write FileMark OK" & vbCrLf)
                    Else
                        OnError("Write FileMark Fail" & vbCrLf)
                        Return False
                    End If

                    Dim plabel As New ltfslabel()
                    plabel.volumeuuid = Guid.NewGuid()
                    plabel.location.partition = ltfslabel.PartitionLabel.b
                    plabel.partitions.index = ltfslabel.PartitionLabel.a
                    plabel.partitions.data = ltfslabel.PartitionLabel.b
                    plabel.blocksize = CInt(BlockLen)

                    'Write ltfslabel
                    ProgressReport("Write ltfslabel..")
                    Dim WriteSenseData As Byte() = Write(handle, Encoding.UTF8.GetBytes(plabel.GetSerializedText()))
                    senseReportFunc(WriteSenseData)
                    If WriteSenseData.Length > 0 Then
                        ProgressReport("Write ltfslabel OK" & vbCrLf)
                    Else
                        OnError("Write ltfslabel Fail" & vbCrLf)
                        Return False
                    End If

                    'Write FileMark
                    ProgressReport("Write FileMark..")
                    WriteSenseData = WriteFileMark(handle, 2)
                    senseReportFunc(WriteSenseData)
                    If WriteSenseData.Length > 0 Then
                        ProgressReport("Write 2FileMark OK" & vbCrLf)
                    Else
                        OnError("Write 2FileMark Fail" & vbCrLf)
                        Return False
                    End If

                    Dim pindex As New ltfsindex
                    pindex.volumeuuid = plabel.volumeuuid
                    pindex.generationnumber = 1
                    pindex.creator = plabel.creator
                    pindex.updatetime = plabel.formattime
                    pindex.location.partition = CType(ltfslabel.PartitionLabel.b, ltfsindex.PartitionLabel)
                    pindex.location.startblock = ReadPosition(handle).BlockNumber
                    pindex.previousgenerationlocation = Nothing
                    pindex.highestfileuid = 1
                    Dim block1 As ULong = pindex.location.startblock
                    pindex._directory = New List(Of ltfsindex.directory)
                    pindex._directory.Add(New ltfsindex.directory With {.name = VolumeName, .readonly = False,
                                                  .creationtime = plabel.formattime, .changetime = .creationtime,
                                                  .accesstime = .creationtime, .modifytime = .creationtime, .backuptime = .creationtime, .fileuid = 1, .contents = New ltfsindex.contentsDef()})

                    'Write ltfsindex
                    ProgressReport("Write ltfsindex..")
                    WriteSenseData = Write(handle, Encoding.UTF8.GetBytes(pindex.GetSerializedText()))
                    senseReportFunc(WriteSenseData)
                    If WriteSenseData.Length > 0 Then
                        ProgressReport("Write ltfsindex OK" & vbCrLf)
                    Else
                        OnError("Write ltfsindex Fail" & vbCrLf)
                        Return False
                    End If

                    'Write FileMark
                    ProgressReport("Write FileMark..")
                    WriteSenseData = WriteFileMark(handle)
                    senseReportFunc(WriteSenseData)
                    If WriteSenseData.Length > 0 Then
                        ProgressReport("Write FileMark OK" & vbCrLf)
                    Else
                        OnError("Write FileMark Fail" & vbCrLf)
                        Return False
                    End If
                    Dim block0 As ULong
                    If ExtraPartitionCount > 0 Then
                        'Locate
                        ProgressReport("Locate to index partition..")
                        LocateAddCode = Locate(handle, 0, 0)
                        If LocateAddCode = 0 Then
                            ProgressReport("Locate P0B0 OK" & vbCrLf)
                        Else
                            OnError($"Locate P0B0 Fail{vbCrLf}{ParseAdditionalSenseCode(LocateAddCode)}" & vbCrLf)
                            Return False
                        End If
                        'Write VOL1Label
                        ProgressReport("Write VOL1Label..")
                        WriteSenseData = Write(handle, New Vol1Label().GenerateRawData(Barcode))
                        senseReportFunc(WriteSenseData)
                        If WriteSenseData.Length > 0 Then
                            ProgressReport("Write VOL1Label OK" & vbCrLf)
                        Else
                            OnError("Write VOL1Label Fail" & vbCrLf)
                            Return False
                        End If
                        'Write FileMark
                        ProgressReport("Write FileMark..")
                        WriteSenseData = WriteFileMark(handle)
                        senseReportFunc(WriteSenseData)
                        If WriteSenseData.Length > 0 Then
                            ProgressReport("Write FileMark OK" & vbCrLf)
                        Else
                            OnError("Write FileMark Fail" & vbCrLf)
                            Return False
                        End If
                        'Write ltfslabel
                        plabel.location.partition = ltfslabel.PartitionLabel.a
                        ProgressReport("Write ltfslabel..")
                        WriteSenseData = Write(handle, Encoding.UTF8.GetBytes(plabel.GetSerializedText()))
                        senseReportFunc(WriteSenseData)
                        If WriteSenseData.Length > 0 Then
                            ProgressReport("Write ltfslabel OK" & vbCrLf)
                        Else
                            OnError("Write ltfslabel Fail" & vbCrLf)
                            Return False
                        End If
                        'Write FileMark
                        ProgressReport("Write FileMark..")
                        WriteSenseData = WriteFileMark(handle, 1)
                        senseReportFunc(WriteSenseData)
                        If WriteSenseData.Length > 0 Then
                            ProgressReport("Write FileMark OK" & vbCrLf)
                        Else
                            OnError("Write FileMark Fail" & vbCrLf)
                            Return False
                        End If
                        If Not WORM Then
                            'Write FileMark
                            ProgressReport("Write FileMark..")
                            WriteSenseData = WriteFileMark(handle, 1)
                            senseReportFunc(WriteSenseData)
                            If WriteSenseData.Length > 0 Then
                                ProgressReport("Write FileMark OK" & vbCrLf)
                            Else
                                OnError("Write FileMark Fail" & vbCrLf)
                                Return False
                            End If
                            'Write ltfsindex
                            pindex.previousgenerationlocation = New ltfsindex.LocationDef()
                            pindex.previousgenerationlocation.partition = pindex.location.partition
                            pindex.previousgenerationlocation.startblock = pindex.location.startblock
                            pindex.location.partition = ltfsindex.PartitionLabel.a
                            pindex.location.startblock = ReadPosition(handle).BlockNumber
                            block0 = pindex.location.startblock
                            ProgressReport("Write ltfsindex..")
                            WriteSenseData = Write(handle, Encoding.UTF8.GetBytes(pindex.GetSerializedText()))
                            senseReportFunc(WriteSenseData)
                            If WriteSenseData.Length > 0 Then
                                ProgressReport("Write ltfsindex OK" & vbCrLf)
                            Else
                                OnError("Write ltfsindex Fail" & vbCrLf)
                                Return False
                            End If
                            'Write FileMark
                            ProgressReport("Write FileMark..")
                            WriteSenseData = WriteFileMark(handle)
                            senseReportFunc(WriteSenseData)
                            If WriteSenseData.Length > 0 Then
                                ProgressReport("Write FileMark OK" & vbCrLf)
                            Else
                                OnError("Write FileMark Fail" & vbCrLf)
                                Return False
                            End If
                        End If

                    End If
                    'Set DateTime
                    Dim CurrentTime As String = Now.ToUniversalTime.ToString("yyyyMMddhhmm")
                    ProgressReport($"WRITE ATTRIBUTE: Written time={CurrentTime}..")
                    If SetMAMAttribute(handle, &H804, CurrentTime.PadRight(12), SenseReport:=senseReportFunc) Then
                        ProgressReport("WRITE ATTRIBUTE: 0804 OK" & vbCrLf)
                    Else
                        OnError("WRITE ATTRIBUTE: 0804 Fail" & vbCrLf)
                        Return False
                    End If
                    'Set VCI
                    ProgressReport($"WRITE ATTRIBUTE: VCI..")
                    If WriteVCI(handle:=handle, Generation:=pindex.generationnumber, block0:=block0, block1:=block1, UUID:=pindex.volumeuuid.ToString(), ExtraPartitionCount:=ExtraPartitionCount, SenseReport:=senseReportFunc) Then
                        ProgressReport("WRITE VCI OK" & vbCrLf)
                    Else
                        ProgressReport("WRITE VCI Fail" & vbCrLf)
                    End If
                    OnFinish("Format finished.")
                    Return True
                Catch ex As Exception
                    OnError(ex.ToString())
                    Return False
                End Try

            End Function
        If ImmediateMode Then
            SyncLock SCSILockManager.GetLock(handle)
                Return mkltfs_op()
            End SyncLock
        Else
            Dim th As New Threading.Thread(
                Sub()
                    SyncLock SCSILockManager.GetLock(handle)
                        mkltfs_op()
                    End SyncLock
                End Sub)
            th.Start()
            Return True
        End If
    End Function
    Public Shared Function RawDump(TapeDrive As String, OutputFile As String, BlockAddress As Long, ByteOffset As Long, FileOffset As Long, Partition As Long, TotalBytes As Long, ByRef StopFlag As Boolean, Optional ByVal BlockSize As Long = 524288, Optional ByVal ProgressReport As Func(Of Long, Boolean) = Nothing, Optional ByVal CreateNew As Boolean = True, Optional LockDrive As Boolean = True) As Boolean
        SyncLock SCSILockManager.GetLock(TapeDrive)
            Dim handle As IntPtr
            If Not OpenTapeDrive(TapeDrive, handle) Then Throw New Exception($"Cannot open {TapeDrive}")
            Dim result As Boolean
            Try
                result = RawDump(handle, OutputFile, BlockAddress, ByteOffset, FileOffset, Partition, TotalBytes, StopFlag, BlockSize, ProgressReport, CreateNew, LockDrive)
            Catch
                Try
                    CloseTapeDrive(handle)
                Catch
                End Try
                Throw
            End Try
            If Not CloseTapeDrive(handle) Then Throw New Exception($"Cannot close {TapeDrive}")
            Return result
        End SyncLock
    End Function
    Public Shared Function RawDump(handle As IntPtr, OutputFile As String, BlockAddress As Long, ByteOffset As Long, FileOffset As Long, Partition As Long, TotalBytes As Long, ByRef StopFlag As Boolean, Optional ByVal BlockSize As Long = 524288, Optional ByVal ProgressReport As Func(Of Long, Boolean) = Nothing, Optional ByVal CreateNew As Boolean = True, Optional LockDrive As Boolean = True) As Boolean
        SyncLock SCSILockManager.GetLock(handle)
            If LockDrive AndAlso Not ReserveUnit(handle) Then Return False
            BlockSize = Math.Min(BlockSize, GlobalBlockLimit)
            If LockDrive AndAlso Not PreventMediaRemoval(handle) Then
                ReleaseUnit(handle)
                Return False
            End If
            If Locate(handle:=handle, BlockAddress:=CULng(BlockAddress), Partition:=CByte(Partition), DestType:=LocateDestType.Block) <> 0 Then
                If LockDrive Then
                    AllowMediumRemoval(handle)
                    ReleaseUnit(handle)
                End If
                Return False
            End If
            Try
                If CreateNew Then File.WriteAllBytes(OutputFile, {})
            Catch ex As Exception
                If LockDrive Then
                    AllowMediumRemoval(handle)
                    ReleaseUnit(handle)
                End If
                Return False
            End Try
            Dim fs As FileStream
            Try
                fs = New FileStream(OutputFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 8388608, FileOptions.SequentialScan)
            Catch ex As Exception
                If LockDrive Then
                    AllowMediumRemoval(handle)
                    ReleaseUnit(handle)
                End If
                Return False
            End Try
            Try
                fs.Seek(FileOffset, SeekOrigin.Begin)
                Dim ReadedSize As Long = 0
                While (ReadedSize < TotalBytes + ByteOffset) And Not StopFlag
                    Dim len As Integer = CInt(Math.Min(BlockSize, TotalBytes + ByteOffset - ReadedSize))
                    Dim Data As Byte() = ReadBlock(handle:=handle, BlockSizeLimit:=CUInt(len))
                    If Data.Length <> len OrElse len = 0 Then
                        If LockDrive Then
                            AllowMediumRemoval(handle)
                            ReleaseUnit(handle)
                        End If
                        Return False
                    End If
                    ReadedSize += len
                    fs.Write(Data, CInt(ByteOffset), CInt(len - ByteOffset))
                    If ProgressReport IsNot Nothing Then StopFlag = ProgressReport(len - ByteOffset)
                    ByteOffset = 0
                End While
                If LockDrive Then
                    AllowMediumRemoval(handle)
                    ReleaseUnit(handle)
                End If
                If StopFlag Then
                    fs.Close()
                    File.Delete(OutputFile)
                    Return True
                End If
            Catch ex As Exception
                Dim ActiveFrm = ApplicationWheels.GetActiveWindow()
                ActiveFrm.Invoke(Sub() MessageBox.Show(ActiveFrm, ex.ToString))
                fs.Close()
                File.Delete(OutputFile)
                If LockDrive Then
                    AllowMediumRemoval(handle)
                    ReleaseUnit(handle)
                End If
                Return False
            End Try
            fs.Close()
            Return True
        End SyncLock
    End Function
    Public Shared Function ParseTimeStamp(t As String) As Date
        If t Is Nothing OrElse t = "" Then Return Nothing
        'yyyy-MM-ddTHH:mm:ss.fffffff00Z
        Try
            Return Date.ParseExact(t, "yyyy-MM-ddTHH:mm:ss.fffffff00Z", Globalization.CultureInfo.InvariantCulture)
        Catch ex As Exception
            Return Nothing
        End Try
    End Function
    <Serializable>
    Public Class BlockDevice
        Public Property DevIndex As String = ""
        Public Property SerialNumber As String = ""
        Public Property VendorId As String = ""
        Public Property ProductId As String = ""
        Public Property DriveLetter As String = ""
        Public Property DeviceType As String = "TAPE"
        Public Property DevicePath As String = ""
        Public Sub New()

        End Sub
        Public Sub New(DevIndex As String, SerialNumber As String, VendorId As String, ProductId As String, Optional ByVal DriveLetter As String = "")
            Me.DevIndex = DevIndex.TrimEnd(" "c)
            Me.SerialNumber = SerialNumber.TrimEnd(" "c)
            Me.VendorId = VendorId.TrimEnd(" "c)
            Me.ProductId = ProductId.TrimEnd(" "c)
            Me.DriveLetter = DriveLetter.TrimEnd(" "c)
        End Sub
        Public Overrides Function ToString() As String
            Dim o As String = DeviceType & DevIndex & ":"
            If DriveLetter <> "" Then o &= " (" & DriveLetter & ":)"
            o &= " [" & SerialNumber & "] " & VendorId & " " & ProductId
            Return o
        End Function
        Public Function GetSerializedText(Optional ByVal ReduceSize As Boolean = True) As String
            Dim writer As New XmlSerializer(GetType(BlockDevice))
            Dim sb As New StringBuilder
            Dim t As New StringWriter(sb)
            Dim ns As New XmlSerializerNamespaces({New Xml.XmlQualifiedName("v", "1")})
            writer.Serialize(t, Me, ns)
            Return sb.ToString()
        End Function
        Public Shared Function FromXML(s As String) As BlockDevice
            Dim reader As New XmlSerializer(GetType(BlockDevice))
            Dim t As TextReader = New StringReader(s)
            Return CType(reader.Deserialize(t), BlockDevice)
        End Function
    End Class
    <TypeConverter(GetType(ExpandableObjectConverter))>
    <Serializable>
    Public Class MediumChanger
        Public Property device As BlockDevice
        Public Property DevIndex As String
        Public Property SerialNumber As String
        Public Property VendorId As String
        Public Property ProductId As String
        Public Property RawElementData As Byte()
        <TypeConverter(GetType(ListTypeDescriptor(Of List(Of Element), Element)))>
        Public Property Elements As New List(Of Element)
        ''' <summary>
        ''' The lowest element address found for the specified Element Type Code that is greater than or equal to the Starting Element Address.
        ''' </summary>
        Public Property FirstElementAddressReported As UInt16
        ''' <summary>
        ''' The number of elements found for the specified Element Type Code that are greater than or equal to the Starting Element Address. This number is always less than or equal the Number of Elements specified in the CBD.
        ''' </summary>
        Public Property NumberofElementsAvailable As UInt16
        ''' <summary>
        ''' The number of bytes of element status data available. This count does not include the Element Status Data header bytes. The count is not adjusted to match the allocation length you specified in the Read Element Status command.
        ''' </summary>
        Public Property ByteCountofReportAvailable As UInt16
        Public Sub New()

        End Sub
        Public Sub New(DevIndex As String, SerialNumber As String, VendorId As String, ProductId As String)
            Me.DevIndex = DevIndex.TrimEnd(" "c)
            Me.SerialNumber = SerialNumber.TrimEnd(" "c)
            Me.VendorId = VendorId.TrimEnd(" "c)
            Me.ProductId = ProductId.TrimEnd(" "c)
        End Sub
        Public Overrides Function ToString() As String
            Dim o As String = "CHANGER" & DevIndex & ":"
            o &= " [" & SerialNumber & "] " & VendorId & " " & ProductId
            Return o
        End Function
        Public Shared Function SCSIReportLUNs(Changer As String, Optional ByRef sense As Byte() = Nothing) As List(Of Byte)
            Dim senseTarget As Byte() = sense
            Dim datalen As Byte() = SCSIReadParam(Changer,
                                                   {&HA0, 0, 0, 0, 0, 0, 0, 0, 0, &H10, 0, 0},
                                                   16,
                                                   Function(s As Byte()) As Boolean
                                                       If senseTarget Is Nothing Then
                                                           senseTarget = NormalizeSCSISense(s, False)
                                                       Else
                                                           Array.Copy(s, senseTarget, Math.Min(s.Length, senseTarget.Length))
                                                       End If
                                                       Return True
                                                   End Function)
            sense = senseTarget
            Dim rdLen As Integer = datalen(0)
            rdLen <<= 8
            rdLen = rdLen Or datalen(1)
            rdLen <<= 8
            rdLen = rdLen Or datalen(2)
            rdLen <<= 8
            rdLen = rdLen Or datalen(3)
            rdLen += 8
            datalen(0) = CByte(rdLen >> 24 And &HFF)
            datalen(1) = CByte(rdLen >> 16 And &HFF)
            datalen(2) = CByte(rdLen >> 8 And &HFF)
            datalen(3) = CByte(rdLen And &HFF)

            Dim rawData As Byte() = SCSIReadParam(Changer,
                                                  {&HA0, 0, 0, 0, 0, 0, datalen(0), datalen(1), datalen(2), datalen(3), 0, 0},
                                                  rdLen,
                                                  Function(s As Byte()) As Boolean
                                                      If senseTarget Is Nothing Then
                                                          senseTarget = NormalizeSCSISense(s, False)
                                                      Else
                                                          Array.Copy(s, senseTarget, Math.Min(s.Length, senseTarget.Length))
                                                      End If
                                                      Return True
                                                  End Function)
            sense = senseTarget
            Dim valueCount As Integer = CInt((rdLen - 8) / 8)
            Dim result As New List(Of Byte)
            For i As Integer = 0 To valueCount - 1
                result.Add(rawData(i * 8 + 8 + 1))
            Next
            Return result
        End Function

        Public Shared Function SCSIReadElementStatus(Changer As String, Optional ByRef sense As Byte() = Nothing, Optional ByVal dSize As Integer = 8, Optional ByVal LUN As Byte = 0) As Byte()
            Dim cdbBytes As Byte()
            Dim dataBuffer As IntPtr = IntPtr.Zero
            Try
                Dim succ As Boolean
                Dim scsiErrorCode As Integer = 0
                If dSize <= 8 Then
                    dSize = 8
                    cdbBytes = {&HB8, CByte(LUN << 5 Or &H10), 0, 0, &HFF, &HFF, 3, CByte(dSize >> 16 And &HFF), CByte(dSize >> 8 And &HFF), CByte(dSize And &HFF), 0, 0}
                    dataBuffer = Marshal.AllocHGlobal(dSize)
                    SyncLock SCSILockManager.GetLock(Changer)
                        Dim handle As IntPtr
                        If Not OpenTapeDrive(Changer, handle) Then
                            Throw New Win32Exception(LastWin32Error, $"Unable to open {Changer}.")
                        End If
                        Try
                            succ = TapeSCSIIOCtlUnmanaged(handle, cdbBytes, dataBuffer, CUInt(dSize), 1, 60, sense)
                            scsiErrorCode = LastWin32Error
                        Finally
                            If Not CloseTapeDrive(handle) Then
                                Throw New Win32Exception(LastWin32Error, $"Unable to close {Changer}.")
                            End If
                        End Try
                    End SyncLock
                    If Not succ Then Throw New SCSIFailureException("SCSI read element status header", scsiErrorCode, sense)

                    Dim data0(dSize - 1) As Byte
                    Marshal.Copy(dataBuffer, data0, 0, dSize)
                    dSize = data0(5)
                    dSize <<= 8
                    dSize = dSize Or data0(6)
                    dSize <<= 8
                    dSize = dSize Or data0(7)
                    dSize = Math.Max(8, dSize + 8)
                    Marshal.FreeHGlobal(dataBuffer)
                    dataBuffer = IntPtr.Zero
                End If

                cdbBytes = {&HB8, &H10, 0, 0, &HFF, &HFF, 3, CByte(dSize >> 16 And &HFF), CByte(dSize >> 8 And &HFF), CByte(dSize And &HFF), 0, 0}
                dataBuffer = Marshal.AllocHGlobal(dSize)
                SyncLock SCSILockManager.GetLock(Changer)
                    Dim handle As IntPtr
                    If Not OpenTapeDrive(Changer, handle) Then
                        Throw New Win32Exception(LastWin32Error, $"Unable to open {Changer}.")
                    End If
                    Try
                        succ = TapeSCSIIOCtlUnmanaged(handle, cdbBytes, dataBuffer, CUInt(dSize), 1, 60, sense)
                        scsiErrorCode = LastWin32Error
                    Finally
                        If Not CloseTapeDrive(handle) Then
                            Throw New Win32Exception(LastWin32Error, $"Unable to close {Changer}.")
                        End If
                    End Try
                End SyncLock
                If Not succ Then Throw New SCSIFailureException("SCSI read element status", scsiErrorCode, sense)

                Dim data1(dSize - 1) As Byte
                Marshal.Copy(dataBuffer, data1, 0, dSize)
                Return data1
            Finally
                If dataBuffer <> IntPtr.Zero Then Marshal.FreeHGlobal(dataBuffer)
            End Try
        End Function
        Public Shared Sub MoveMedium(Changer As String, src As UInt32, dest As UInt32, Optional ByVal sense As Byte() = Nothing, Optional ByVal LUN As Byte = 0)
            SCSIReadParam(TapeDrive:=Changer, cdbData:=New Byte() {&HA5, CByte(LUN << 5), 0, 0, CByte(src >> 8 And &HFFUI), CByte(src And &HFFUI), CByte(dest >> 8 And &HFFUI), CByte(dest And &HFFUI), 0, 0, 0, 0}, paramLen:=12,
                senseReport:=Function(s As Byte()) As Boolean
                                 If sense IsNot Nothing AndAlso sense.Length >= 64 Then
                                     Array.Copy(s, sense, Math.Min(64, s.Length))
                                 End If
                                 Return True
                             End Function)
        End Sub
        Public Sub RefreshElementStatus(Optional ByVal IgnoreLUN0 As Boolean = True)
            Dim devicePath As String = $"\\.\CHANGER{DevIndex}"
            If device IsNot Nothing Then devicePath = device.DevicePath
            Dim LUNList As List(Of Byte) = SCSIReportLUNs(devicePath)
            If LUNList.Count = 0 Then LUNList.Add(1)
            If LUNList IsNot Nothing AndAlso LUNList.Count > 0 Then
                Elements = New List(Of Element)
                For Each LUN As Byte In LUNList
                    If IgnoreLUN0 AndAlso LUN = 0 Then Continue For
                    If RawElementData IsNot Nothing AndAlso RawElementData.Length > 8 Then
                        RawElementData = SCSIReadElementStatus(devicePath, dSize:=RawElementData.Length, LUN:=LUN)
                    Else
                        RawElementData = SCSIReadElementStatus(devicePath, LUN:=LUN)
                    End If
                    FirstElementAddressReported = CUShort(CInt(RawElementData(0)) << 8 Or RawElementData(1))
                    NumberofElementsAvailable = CUShort(CInt(RawElementData(2)) << 8 Or RawElementData(3))
                    ByteCountofReportAvailable = CUShort(CInt(RawElementData(5)) << 16 Or CInt(RawElementData(6)) << 8 Or RawElementData(7))
                    Dim offset As Integer = 8
                    While offset < RawElementData.Length - 8
                        Dim PageHeader(7) As Byte
                        Array.Copy(RawElementData, offset, PageHeader, 0, 8)
                        offset += 8
                        Dim dlen As UInt16 = CUShort(CInt(PageHeader(2)) << 8 Or PageHeader(3))
                        If dlen = 0 Then Continue For
                        Dim totallen As UInt32 = CUInt(CInt(PageHeader(5)) << 16 Or CInt(PageHeader(6)) << 8 Or PageHeader(7))
                        Dim dcount As Integer = CInt(totallen \ dlen)
                        Dim pagedata(dlen - 1) As Byte
                        For i As Integer = 0 To dcount - 1
                            Array.Copy(RawElementData, offset + dlen * i, pagedata, 0, dlen)
                            Dim e As Element = Element.FromRaw(pagedata, New Element With {
                                .LUN = LUN,
                                .ElementTypeCode = CType(PageHeader(0) And &HF, Element.ElementTypeCodes),
                                .PVolTag = (((PageHeader(1) >> 7) And 1) <> 0),
                                .AVolTag = (((PageHeader(1) >> 6) And 1) <> 0),
                                .ElementDescriptorLength = dlen,
                                .ByteCountofDescriptorDataAvailable = CInt(totallen)
                                })
                            Elements.Add(e)
                        Next
                        offset = CInt(offset + totallen)
                    End While
                Next

            End If





        End Sub
        <TypeConverter(GetType(ExpandableObjectConverter))>
        <Serializable>
        Public Class Element
            Public Property LUN As Byte = 0
            Public Property RawData As Byte()
            ''' <summary>
            ''' <para>1h = Medium Transport Element (robot hand)</para>
            ''' <para>2h = Storage Element (cartridge cells)</para>
            ''' <para>3h = Import/Export Element (CAP cells)</para>
            ''' <para>4h = Data Transfer Element (drives Or empty drive slots)</para>
            ''' </summary>
            Public Enum ElementTypeCodes
                MediumTransportElement = 1
                StorageElement = 2
                ImportExportElement = 3
                DataTransferElement = 4
            End Enum
            Public Property ElementTypeCode As ElementTypeCodes
            ''' <summary>
            ''' <para>0 = The library omits Primary Volume Tag information from the element descriptors.</para>
            ''' <para>1 = The library includes Primary Volume Tag information in the element descriptors.</para>
            ''' </summary>
            Public Property PVolTag As Boolean
            ''' <summary>
            ''' 0 = The library does not support Alternative Volume Tags.
            ''' </summary>
            Public Property AVolTag As Boolean
            ''' <summary>
            ''' The total number of bytes contained in a single element descriptor.
            ''' </summary>
            Public Property ElementDescriptorLength As Integer
            ''' <summary>
            ''' The number of bytes of element descriptor data available. This count does not include the Element Status Page header bytes. The count is not adjusted to match the allocation length you specified in the Read Element Status command.
            ''' </summary>
            Public Property ByteCountofDescriptorDataAvailable As Integer
            Public Property ElementAddress As UInt16
            ''' <summary>
            ''' <para>0 = No operator intervention required to make the CAP accessible</para>
            ''' <para>1 = Operator intervention required to make the CAP accessible</para>
            ''' </summary>
            Public Property OIR As Boolean
            ''' <summary>
            ''' 0 = The import/export element is a CAP. The cartridge will not leave the library when prevented by the Prevent/Allow Medium Removal (1Eh) command.
            ''' </summary>
            Public Property CMC As Boolean
            ''' <summary>
            ''' 1 = The CAP supports importing cartridges.
            ''' </summary>
            Public Property InEnab As Boolean
            ''' <summary>
            ''' 1 = The CAP supports exporting cartridges.
            ''' </summary>
            Public Property ExEnab As Boolean
            ''' <summary>
            ''' <para>0 = The robot cannot access the element. For Import/Export elements, this can occur when the CAP is open or a CAP magazine was removed. For Data transfer elements, this can occur when a cartridge is loaded in a drive.</para>
            ''' <para>1 = The robot can access the element</para>
            ''' </summary>
            Public Property Access As Boolean
            ''' <summary>
            ''' <para>0 = The element is in a normal state</para>
            ''' <para>1 = The element Is in an abnormal state. The Additional Sense Code (ASC) And the Additional Sense Code Qualifier (ASCQ) fields contain information regarding the abnormal state. Other fields in the descriptor might be invalid And should be ignored.</para>
            ''' </summary>
            Public Property Except As Boolean
            ''' <summary>
            ''' <para>0 = The robot placed the cartridge in the CAP for an export operation.</para>
            ''' <para>1 = An operator placed the cartridge in the CAP for an import operation.</para>
            ''' </summary>
            Public Property ImpExp As Boolean
            ''' <summary>
            ''' <para>0 = The element does not contain a cartridge</para>
            ''' <para>1 = The element contains a cartridge</para>
            ''' </summary>
            Public Property Full As Boolean
            ''' <summary>
            ''' Additional Sense Code
            ''' <para>This field is valid only if the Except bit is set. In the case of an exception, it contains an ASC as defined for Request Sense data.</para>
            ''' <para>Condition_____________________ASC Value___________ASCQ Value</para>
            ''' <para>CAP Open____________________3Ah___________________02h</para>
            ''' <para>Empty Drive Slot_____________3Bh___________________1Ah</para>
            ''' <para>Drive Hardware Error_______40h___________________02h</para>
            ''' </summary>
            Public Property ASC As Byte
            ''' <summary>
            ''' Additional Sense Code Qualifier
            ''' <para>This field is valid only if the Except bit is set. In the case of an exception, it contains an ASCQ as defined for Request Sense data.</para>
            ''' <para>Condition_____________________ASC Value___________ASCQ Value</para>
            ''' <para>CAP Open____________________3Ah___________________02h</para>
            ''' <para>Empty Drive Slot_____________3Bh___________________1Ah</para>
            ''' <para>Drive Hardware Error_______40h___________________02h</para>
            ''' </summary>
            Public Property ASCQ As Byte
            ''' <summary>
            ''' <para>0 = The Source Element Address and Invert fields are not valid.</para>
            ''' <para>1 = The Source Element Address and Invert fields are valid.</para>
            ''' </summary>
            Public Property SValid As Boolean
            ''' <summary>
            ''' 0 = The library does not support multi-sided media.
            ''' </summary>
            Public Property Invert As Boolean
            ''' <summary>
            ''' <para>0 = The element is enabled.</para>
            ''' <para>1 = The element is disabled (for example an open CAP, a drive hardware error, or empty drive slot).</para>
            ''' </summary>
            ''' <returns></returns>
            Public Property ED As Boolean
            ''' <summary>
            ''' <para>0h = Unspecified - the medium changer cannot determine the medium type.</para>
            ''' <para>1h = Data Medium</para>
            ''' <para>2h = Cleaning Medium</para>
            ''' </summary>
            Public Property MediumType As Byte
            ''' <summary>
            ''' This field is valid only if the SValid field is 1. This field provides the address of the last storage element this cartridge occupied. The element address value may or may not be the same as this element.
            ''' </summary>
            Public Property SourceStorageElementAddress As UInt16
            ''' <summary>
            ''' <para>When PVolTag is 1, the library returns volume tag information. When PVolTag is 0, the library omits volume tag information.</para>
            ''' <para>The Primary Volume Tag field contains the null-terminated ASCII barcode label on the tape cartridge. If the label on the cartridge tape is not readable or if the element is empty, the Primary Volume Tag field is filled with 36 bytes of zeros. The "Volume Label Format" controls the presentation of the volser in the Primary Volume Tag field. The library supports the following settings:</para>
            ''' <para>· Full Label</para>
            ''' <para>· No Type Checking</para>
            ''' <para>· Prepend Last Two Characters</para>
            ''' <para>· Trim Last Character</para>
            ''' <para>· Trim Last Two Characters</para>
            ''' <para>· Trim First Two Characters</para>
            ''' <para>· Trim First Character</para>
            ''' </summary>
            Public Property PrimaryVolumeTagInformation As String = ""
            ''' <summary>
            ''' <para>0h = Reserved (not supported) for the Medium Transport Element, Storage Element, Import/Export Element, or Data Transfer Element (DvcID = 0) descriptors.</para>
            ''' <para>2h = The identifier contains ASCII graphic codes (code values 20h through 7Eh) for Data Transfer Element (DvcID = 1) descriptor.</para>
            ''' </summary>
            Public Property CodeSet As Byte
            ''' <summary>
            ''' The format and assignment authority for the identifier.
            ''' <para>0h = The library returns vendor specific data.</para>
            ''' </summary>
            ''' <returns></returns>
            Public Property IdentifierType As Byte
            ''' <summary>
            ''' The combined length of the Identifier and the Identifier Pad.
            ''' <para>00h = The library returns 0 bytes of identifier data in the descriptors for Medium Transport Elements, Storage Elements, Import/Export Elements, or Data Transfer Elements (DvcID = 0).</para>
            ''' <para>20h = The library returns 32 bytes of identifier data for the Data Transfer Element (DvcID = 1).</para>
            ''' </summary>
            Public Property IdentifierLength As Byte
            ''' <summary>
            ''' for Data Transfer Element DvcID = 1 Only
            ''' <para>The ASCII Serial Number for the tape drive associated with this data transfer element.</para>
            ''' Identifier Pad (for Data Transfer Element DvcID = 1 Only)
            ''' <para>Contains ASCII blanks. The number of blanks depends on the length of the Identifier field. The combined length of the Identifier field and the Identifier Pad is 32 bytes.</para>
            ''' </summary>
            Public Property Identifier As String = ""
            ''' <summary>
            ''' <para>43h ('C') = The element contains a cleaning cartridge.</para>
            ''' <para>4Ch ('L') = The element contains an LTO cartridge.</para>
            ''' <para>54h ('T') = The element contains a T10000 cartridge.</para>
            ''' <para>FFh = The media domain cannot be determined or the element is empty.</para>
            ''' </summary>
            Public Property MediaDomain As Byte
            ''' <summary>
            ''' FFh = The media type cannot be determined or the element is empty.
            ''' <para>If the Media Domain is 43h (C):</para>
            ''' <para>C = The element contains a T10000 Version 2 cleaning cartridge.</para>
            ''' <para>L = The element contains a T10000 Universal cleaning cartridge.</para>
            ''' <para>T = The element contains a T10000 Version 1 cleaning cartridge.</para>
            ''' <para>U = The element contains a Universal LTO cleaning cartridge.</para>
            ''' <para>If the Media Domain is 4Ch (L):</para>
            ''' <para>3-8 = The element contains a Generation 3-8 LTO cartridge.</para>
            ''' <para>T-Y = The element contains a Generation 3-8 LTO WORM cartridge.</para>
            ''' <para>If the Media Domain is 54h (T):</para>
            ''' <para>1 = The element contains a T10000 Version 1 cartridge.</para>
            ''' <para>2 = The element contains a T10000 Version 2 cartridge.</para>
            ''' <para>S = The element contains a T10000 Version 1 Sport cartridge.</para>
            ''' <para>T = The element contains a T10000 Version 2 Sport cartridge.</para>
            ''' </summary>
            Public Property MediaType As Byte
            ''' <summary>
            ''' <para>4Ch (L) = The drive supports LTO cartridges.</para>
            ''' <para>54h (T) = The drive supports T10000 cartridges.</para>
            ''' <para>FFh = The element domain cannot be determined.</para>
            ''' </summary>
            Public Property TransportDomain As Byte
            ''' <summary>
            ''' <para>If the Transport Domain is 4Ch (L):</para>
            ''' <para>· 3Bh = HP Generation 5 LTO drive</para>
            ''' <para>· 3Ch = IBM Generation 5 LTO drive</para>
            ''' <para>· 3Dh = HP Generation 6 LTO drive.</para>
            ''' <para>· 3Eh = IBM Generation 6 LTO drive.</para>
            ''' <para>· 2Dh = IBM Generation 7 LTO drive.</para>
            ''' <para>· 2Eh = IBM Generation 8 LTO drive.</para>
            ''' <para>If the Transport Domain is 54h (T):</para>
            ''' <para>· 0Dh = StorageTek T10000A drive.</para>
            ''' <para>· 0Eh = StorageTek T10000A drive in 3590 emulation mode.</para>
            ''' <para>· 18h = StorageTek T10000A Encrypting drive.</para>
            ''' <para>· 19h = StorageTek T10000A Encrypting drive in 3590 emulation mode.</para>
            ''' <para>· 1Ah = StorageTek T10000B drive.</para>
            ''' <para>· 1Bh = StorageTek T10000B drive in 3590 emulation mode.</para>
            ''' <para>· 1Ch = StorageTek T10000B Encrypting drive.</para>
            ''' <para>· 1Dh = StorageTek T10000B Encrypting drive in 3590 emulation mode.</para>
            ''' <para>· 22h = StorageTek T10000C drive.</para>
            ''' <para>· 23h = StorageTek T10000C drive in 3590 emulation mode.</para>
            ''' <para>· 24h = StorageTek T10000C Encrypting drive.</para>
            ''' <para>· 25h = StorageTek T10000C Encrypting drive in 3590 emulation mode.</para>
            ''' <para>· 26h = StorageTek T10000D drive.</para>
            ''' <para>· 27h = StorageTek T10000D drive in 3590 emulation mode.</para>
            ''' <para>· 28h = StorageTek T10000D Encrypting drive.</para>
            ''' <para>· 29h = StorageTek T10000D Encrypting drive in 3590 emulation mode.</para>
            ''' <para>· 2Ah = StorageTek T10000D Fibre Channel over Ethernet. </para>
            ''' <para>· 2Bh = StorageTek T10000D Fibre Channel over Ethernet Encrypting drive.</para>
            ''' </summary>
            Public Property TransportType As Byte
            ''' <summary>
            ''' The 32-byte ASCII serial number for the drive.
            ''' <para>For drives with a serial number less than 32 bytes, the library left-justifies the value by returning ASCII blanks for the unused less-significant bytes. If the serial number is not available from a drive that should support an ASCII serial number, the library returns all ASCII blanks.</para>
            ''' </summary>
            Public Property TransportSerialNumber As String = ""
            Public Shared Function FromRaw(RawData As Byte(), Optional ByVal Preset As Element = Nothing) As Element
                If RawData Is Nothing Then Return Nothing
                Dim result As Element
                If Preset IsNot Nothing Then result = Preset Else result = New Element
                result.RawData = RawData
                With result
                    Try
                        Dim DOffset As Byte = 0
                        .ElementAddress = CUShort(CInt(RawData(DOffset + 0)) << 8 Or RawData(DOffset + 1))
                        .OIR = (((RawData(DOffset + 2) >> 7) And 1) <> 0)
                        .CMC = (((RawData(DOffset + 2) >> 6) And 1) <> 0)
                        .InEnab = (((RawData(DOffset + 2) >> 5) And 1) <> 0)
                        .ExEnab = (((RawData(DOffset + 2) >> 4) And 1) <> 0)
                        .Access = (((RawData(DOffset + 2) >> 3) And 1) <> 0)
                        .Except = (((RawData(DOffset + 2) >> 2) And 1) <> 0)
                        .ImpExp = (((RawData(DOffset + 2) >> 1) And 1) <> 0)
                        .Full = (((RawData(DOffset + 2) >> 0) And 1) <> 0)
                        .ASC = RawData(DOffset + 4)
                        .ASCQ = RawData(DOffset + 5)
                        .SValid = (((RawData(DOffset + 9) >> 7) And 1) <> 0)
                        .Invert = (((RawData(DOffset + 9) >> 6) And 1) <> 0)
                        .ED = (((RawData(DOffset + 9) >> 3) And 1) <> 0)
                        .MediumType = CByte(RawData(DOffset + 9) And &B111)
                        .SourceStorageElementAddress = CUShort(CInt((RawData(DOffset + 10)) << 8) Or RawData(DOffset + 11))
                        If .PVolTag Then
                            .PrimaryVolumeTagInformation = Encoding.ASCII.GetString(RawData, DOffset + 12, 36).TrimEnd(Chr(0)).TrimEnd(" "c)
                            DOffset = CByte(DOffset + 36)
                        End If
                        .CodeSet = CByte(RawData(DOffset + 12) And &HF)
                        .IdentifierType = CByte(RawData(DOffset + 13) And &HF)
                        .IdentifierLength = RawData(DOffset + 15)
                        If .IdentifierLength > 0 Then
                            .Identifier = Encoding.ASCII.GetString(RawData, DOffset + 16, .IdentifierLength).TrimEnd(Chr(0)).TrimEnd(" "c)
                            DOffset = CByte(DOffset + 32)
                        End If
                        .MediaDomain = RawData(DOffset + 16)
                        .MediaType = RawData(DOffset + 17)
                        .TransportDomain = RawData(DOffset + 18)
                        .TransportType = RawData(DOffset + 19)
                        If RawData.Length - 1 >= DOffset + 51 Then
                            .TransportSerialNumber = Encoding.ASCII.GetString(RawData, DOffset + 20, 32).TrimEnd(Chr(0)).TrimEnd(" "c)
                        End If
                    Catch ex As Exception

                    End Try
                End With
                Return result
            End Function
        End Class
        Public Function GetSerializedText(Optional ByVal ReduceSize As Boolean = True) As String
            Dim writer As New XmlSerializer(GetType(MediumChanger))
            Dim sb As New StringBuilder
            Dim t As New StringWriter(sb)
            Dim ns As New XmlSerializerNamespaces({New Xml.XmlQualifiedName("v", "1")})
            writer.Serialize(t, Me, ns)
            Return sb.ToString()
        End Function
    End Class
    <TypeConverter(GetType(ExpandableObjectConverter))>
    Public Class GX256
        Public Shared ExpTable(255) As Byte
        Public Shared LogTable(255) As Byte
        Public Shared Sub Initialization()
            ExpTable(0) = 1
            For i As Integer = 1 To 255
                Dim tempval As Integer = (CInt(ExpTable(i - 1)) << 1) 'Xor ExpTable(i - 1)
                If tempval > 255 Then tempval = tempval Xor &H11D
                ExpTable(i) = CByte(tempval And &HFF)
            Next
            For i As Integer = 0 To 254
                LogTable(ExpTable(i)) = CByte(i And &HFF)
            Next
        End Sub
        Public Shared Function Times(a As Byte, b As Byte) As Byte
            If ExpTable(0) = 0 Then Initialization()
            If a <> 0 AndAlso b <> 0 Then
                Return ExpTable(CInt((LogTable(a) + CUInt(LogTable(b))) Mod 255))
            Else
                Return 0
            End If
        End Function
        Public Shared Function CalcCRC(Data As Byte()) As Byte()
            If ExpTable(0) = 0 Then
                Initialization()
            End If
            Dim R0 As Byte = 0, R1 As Byte = 0, R2 As Byte = 0, R3 As Byte = 0
            Dim TmpVal As Byte = 0
            For i As Integer = 0 To Data.Length - 1
                TmpVal = R3 Xor Data(i)
                R3 = R2 Xor Times(ExpTable(201), TmpVal)
                R2 = R1 Xor Times(ExpTable(246), TmpVal)
                R1 = R0 Xor Times(ExpTable(201), TmpVal)
                R0 = TmpVal
            Next
            Return {R3, R2, R1, R0}
        End Function
    End Class

    <Serializable>
    <TypeConverter(GetType(ExpandableObjectConverter))>
    Public Class CMParser
        Const GUARD_WRAP_IDENTIFIER As Integer = &HFFFFFFFE
        Const UNUSED_WRAP_IDENTIFIER As Integer = &HFFFFFFFF
        <Category("Internal")>
        Public Property a_CMBuffer As Byte() = Array.Empty(Of Byte)()

        <Category("Internal")>
        <XmlIgnore> Public Property a_PageID As Integer
        <Category("Internal")>
        <XmlIgnore> Public Property a_Offset As Integer
        <Category("Internal")>
        Public Property a_UnProt As Integer = 0
        <Category("Internal")>
        <XmlIgnore> Public Property a_Key As Integer
        <XmlIgnore> Public a_Index As Integer
        <Category("Internal")>
        Public Property a_Err As Integer = 0
        <Category("Internal")>
        <XmlIgnore> Public Property a_Buffer As Byte()
        <Category("Internal")>
        <XmlIgnore> Public Property at_Offset As Byte()
        <Category("Internal")>
        <XmlIgnore> Public Property a_Length As Integer = 0
        <Category("Internal")>
        Public Property a_CleansRemaining As Integer = 0
        <Category("Internal")>
        Public Property a_CleanLength As Double
        <Category("Internal")>
        Public Property a_NWraps As Integer = 0
        <Category("Internal")>
        Public Property a_TapeDirLength As Integer = 16
        <Category("Internal")>
        Public Property a_SetsPerWrap As Integer = 0
        <Category("Internal")>
        <XmlIgnore> Public Property a_SetID As Integer = 0
        <Category("Internal")>
        <XmlIgnore> Public Property a_LastID As Integer = 0
        <Category("Internal")>
        Public Property a_Barcode As String
        <Category("Internal")>
        <XmlIgnore> Public Property a_AttributeID As Integer
        <Category("Internal")>
        <XmlIgnore> Public Property a_AttributeLength As Integer
        <Category("Internal")>
        Public Property a_HdrLength As Integer
        <Category("Internal")>
        Public Property a_DriveTypeIdentifier As Integer = 0
        <Category("Internal")>
        <XmlIgnore> Public Property a_OutputStr As String = ""
        <Category("Internal")>
        Public Property a_TapeWritePassPartition As Integer = 0
        <Category("Internal")>
        Public Property a_NumPartitions As New List(Of EOD)
        <XmlIgnore> Public a_PartitionKey As Integer
        <Category("Internal")>
        <XmlIgnore> Public Property a_set As Integer
        <Category("Internal")>
        Public Property g_ValidCM As Boolean = False
        <Category("Internal")>
        Public Property g_Channels As Integer = 8
        <Category("Internal")>
        Public Property g_LoadCount As Integer = 0
        <Category("Internal")>
        Public Property g_CartridgeSN As String = "          "
        <Category("Internal")>
        Public Property g_TPM As Integer = 0
        <Category("Internal")>
        Public Property g_Barcode As String = "        "
        <Category("Internal")>
        Public Property g_DHLTimeStamp As Integer = 0
        <Category("Internal")>
        Public Property g_FaultLogSize As Integer = 0
        <Category("Internal")>
        Public Property g_LtoPearlFlagEnable As Integer = 0
        <Category("Internal")>
        Public Property g_DHLPowerCount As Integer = 0
        <Category("Internal")>
        Public Property g_DHLPocCount As Integer = 0


        <Category("Pages")>
        <TypeConverter(GetType(ListTypeDescriptor(Of List(Of Page), Page)))>
        Public Property PageData As New List(Of Page)
        <Category("PageData")>
        Public Property CartridgeMfgData As New Cartridge_mfg
        <Category("PageData")>
        Public Property MediaMfgData As New Media_mfg
        <Category("PageData")>
        <TypeConverter(GetType(ListTypeDescriptor(Of List(Of UsagePage), UsagePage)))>
        Public Property a_UsageData As New List(Of UsagePage)
        <Category("PageData")>
        <TypeConverter(GetType(ListTypeDescriptor(Of List(Of Usage), Usage)))>
        Public Property UsageData As New List(Of Usage)
        <Category("PageData")>
        Public Property StatusData As New TapeStatus
        <Category("PageData")>
        Public Property InitialisationData As New Initialisation
        <Category("PageData")>
        <TypeConverter(GetType(ListTypeDescriptor(Of List(Of EOD), EOD)))>
        Public Property PartitionEOD As New List(Of EOD)
        <Category("PageData")>
        Public Property CartridgeContentData As New CartridgeContent
        <Category("PageData")>
        Public Property TapeDirectoryData As New TapeDirectory
        <Category("PageData")>
        Public Property SuspendWriteData As New SuspendWrite
        <Category("PageData")>
        Public Property ApplicationSpecificData As New ApplicationSpecific
        Public Function a_Usage(index As Integer, Optional ByVal CreateNew As Boolean = True) As UsagePage
            For Each up As UsagePage In a_UsageData
                If up.index = index Then Return up
            Next
            If CreateNew Then
                Dim upn As New UsagePage With {.index = index}
                a_UsageData.Add(upn)
                Return upn
            End If
            Return Nothing
        End Function
        <TypeConverter(GetType(ExpandableObjectConverter))>
        <Serializable> Public Class UsagePage
            Public Property index As Integer
            Public Property data0 As Byte()
            Public Property data1 As Integer
        End Class
        <TypeConverter(GetType(ExpandableObjectConverter))>
        <Serializable> Public Class Page
            Public Property a_Key As Integer
            Public Property Version As Integer
            Public Property Offset As Integer = -1
            Public Property Length As Integer = -1
            Public Property Type As TypeDef
            Public Enum TypeDef
                unprotected
                [protected]
            End Enum
        End Class
        <TypeConverter(GetType(ExpandableObjectConverter))>
        <Serializable> Public Class Cartridge_mfg
            Public Property TapeVendor As String
            Public Property CartridgeSN As String
            Public Property CartridgeType As Integer
            Public Property Format As String = ""
            Public ReadOnly Property IsLTO3Plus As Boolean
                Get
                    Dim fmt As String = Format
                    Return fmt IsNot Nothing AndAlso fmt.Contains("LTO-3") OrElse fmt.Contains("LTO-4") OrElse fmt.Contains("LTO-5") OrElse fmt.Contains("LTO-6") OrElse fmt.Contains("LTO-7") OrElse fmt.Contains("LTO-8") OrElse fmt.Contains("LTO-9")
                End Get
            End Property
            Public ReadOnly Property IsLTO4Plus As Boolean
                Get
                    Dim fmt As String = Format
                    Return fmt IsNot Nothing AndAlso fmt.Contains("LTO-4") OrElse fmt.Contains("LTO-5") OrElse fmt.Contains("LTO-6") OrElse fmt.Contains("LTO-7") OrElse fmt.Contains("LTO-8") OrElse fmt.Contains("LTO-9")
                End Get
            End Property
            Public ReadOnly Property IsLTO5Plus As Boolean
                Get
                    Dim fmt As String = Format
                    Return fmt IsNot Nothing AndAlso fmt.Contains("LTO-5") OrElse fmt.Contains("LTO-6") OrElse fmt.Contains("LTO-7") OrElse fmt.Contains("LTO-8") OrElse fmt.Contains("LTO-9")
                End Get
            End Property
            Public ReadOnly Property IsLTO6Plus As Boolean
                Get
                    Dim fmt As String = Format
                    Return fmt IsNot Nothing AndAlso fmt.Contains("LTO-6") OrElse fmt.Contains("LTO-7") OrElse fmt.Contains("LTO-8") OrElse fmt.Contains("LTO-9")
                End Get
            End Property
            Public ReadOnly Property IsLTO7Plus As Boolean
                Get
                    Dim fmt As String = Format
                    Return fmt IsNot Nothing AndAlso fmt.Contains("LTO-7") OrElse fmt.Contains("LTO-8") OrElse fmt.Contains("LTO-9")
                End Get
            End Property
            Public ReadOnly Property IsLTO8Plus As Boolean
                Get
                    Dim fmt As String = Format
                    Return fmt IsNot Nothing AndAlso fmt.Contains("LTO-8") OrElse fmt.Contains("LTO-9")
                End Get
            End Property
            Public ReadOnly Property IsLTO9Plus As Boolean
                Get
                    Dim fmt As String = Format
                    Return fmt IsNot Nothing AndAlso fmt.Contains("LTO-9")
                End Get
            End Property
            Public ReadOnly Property CartridgeTypeAbbr As String
                Get
                    If ((CartridgeType >> 15) And 1) = 1 Then Return "CU"
                    Select Case CartridgeType And &HFF
                        Case 1
                            Return "L1"
                        Case 2
                            Return "L2"
                        Case 4
                            Return "L3"
                        Case 8
                            Return "L4"
                        Case 16
                            Return "L5"
                        Case 32
                            Return "L6"
                        Case 64
                            If Format.Contains("Type M") Then
                                Return "M8"
                            Else
                                Return "L7"
                            End If
                        Case 128
                            Return "L8"
                        Case 129
                            Return "L9"
                        Case Else
                            Select Case CartridgeType
                                Case 5126
                                    Return "JA"
                                Case 13
                                    Return "JB"
                                Case 15
                                    Return "JC"
                                Case 17
                                    Return "JD"
                                Case 19
                                    Return "JE"
                                Case 21
                                    Return "JF"
                                Case 13318
                                    Return "JJ"
                                Case 8207
                                    Return "JK"
                                Case 8209
                                    Return "JL"
                                Case 8211
                                    Return "JM"
                                Case 8213
                                    Return "JN"
                            End Select
                    End Select
                    Return ""
                End Get
            End Property
            Public Shared Function GetCMLength(Abbr As String) As Integer
                Select Case Abbr.ToUpper
                    Case "CU"
                        Return 4096
                    Case "L1"
                        Return 4096
                    Case "L2"
                        Return 4096
                    Case "L3"
                        Return 4096
                    Case "L4"
                        Return 8160
                    Case "L5"
                        Return 8160
                    Case "L6"
                        Return 16352
                    Case "L7"
                        Return 16352
                    Case "M8"
                        Return 16352
                    Case "L8"
                        Return 16352
                    Case "L9"
                        Return 32736
                    Case Else
                        Return 0
                End Select
            End Function
            Public Property KB_PER_DATASET As Integer
                Get
                    Select Case CartridgeTypeAbbr
                        Case "L1"
                            Return 404
                        Case "L2"
                            Return 404
                        Case "L3"
                            Return 1617
                        Case "L4"
                            Return 1590
                        Case "L5"
                            Return 2473
                        Case "L6"
                            Return 2473
                        Case "L7"
                            Return 5032
                        Case "M8"
                            Return 5032
                        Case "L8"
                            Return 5032
                        Case "L9"
                            Return 9806
                    End Select
                    Return 0
                End Get
                Set(value As Integer)
                End Set
            End Property
            Public Property CCQ_PER_DATASET As Integer
                Get
                    Select Case CartridgeTypeAbbr
                        Case "L1"
                            Return 64
                        Case "L2"
                            Return 64
                        Case "L3"
                            Return 128
                        Case "L4"
                            Return 128
                        Case "L5"
                            Return 192
                        Case "L6"
                            Return 192
                        Case "L7"
                            Return 192
                        Case "M8"
                            Return 192
                        Case "L8"
                            Return 192
                        Case "L9"
                            Return 384
                    End Select
                    Return 0
                End Get
                Set(value As Integer)
                End Set
            End Property
            Public Property SETS_PER_WRAP As Integer
                Get
                    Select Case CartridgeTypeAbbr
                        Case "L1"
                            Return 5500
                        Case "L2"
                            Return 8200
                        Case "L3"
                            Return 6000
                        Case "L4"
                            Return 9500
                        Case "L5"
                            Return 7800
                        Case "L6"
                            Return 7805
                        Case "L7"
                            Return 10950
                        Case "M8"
                            Return 10950
                        Case "L8"
                            Return 11660
                        Case "L9"
                            Return 6770
                    End Select
                    Return 0
                End Get
                Set(value As Integer)
                End Set
            End Property
            Public Property MB_PER_WRAP_METRE As Double
                Get
                    Select Case CartridgeTypeAbbr
                        Case "L1"
                            Return 3.84
                        Case "L2"
                            Return 5.75
                        Case "L3"
                            Return 14.98
                        Case "L4"
                            Return 19.27
                        Case "L5"
                            Return 23.9
                        Case "L6"
                            Return 23.89
                        Case "L7"
                            Return 59.85
                        Case "M8"
                            Return 59.85
                        Case "L8"
                            Return 63.64
                        Case "L9"
                            Return 66.59
                    End Select
                    Return 0
                End Get
                Set(value As Double)
                End Set
            End Property
            Public Property TAPE_LU_LIFE As Integer
                Get
                    Select Case CartridgeTypeAbbr
                        Case "L1"
                            Return 20000
                        Case "L2"
                            Return 20000
                        Case "L3"
                            Return 20000
                        Case "L4"
                            Return 20000
                        Case "L5"
                            Return 20000
                        Case "L6"
                            Return 20000
                        Case "L7"
                            Return 20000
                        Case "M8"
                            Return 20000
                        Case "L8"
                            Return 20000
                        Case "L9"
                            Return 20000
                    End Select
                    Return 0
                End Get
                Set(value As Integer)
                End Set
            End Property
            Public Property TAPE_LIFE_IN_VOLS As Integer
                Get
                    Select Case CartridgeTypeAbbr
                        Case "L1"
                            Return 260
                        Case "L2"
                            Return 260
                        Case "L3"
                            Return 260
                        Case "L4"
                            Return 260
                        Case "L5"
                            Return 260
                        Case "L6"
                            Return 130
                        Case "L7"
                            Return 130
                        Case "M8"
                            Return 98
                        Case "L8"
                            Return 75
                        Case "L9"
                            Return 55
                    End Select
                    Return 0
                End Get
                Set(value As Integer)
                End Set
            End Property
            Public Property WRAP_LEN_IN_MTRS As Integer
                Get
                    Select Case CartridgeTypeAbbr
                        Case "L1"
                            Return 580
                        Case "L2"
                            Return 580
                        Case "L3"
                            Return 648
                        Case "L4"
                            Return 783
                        Case "L5"
                            Return 808
                        Case "L6"
                            Return 808
                        Case "L7"
                            Return 922
                        Case "M8"
                            Return 922
                        Case "L8"
                            Return 922
                        Case "L9"
                            Return 997
                    End Select
                    Return 0
                End Get
                Set(value As Integer)
                End Set
            End Property
            Public Property TAPE_LEN_IN_MTRS As Integer
                Get
                    Select Case CartridgeTypeAbbr
                        Case "L1"
                            Return 609
                        Case "L2"
                            Return 609
                        Case "L3"
                            Return 680
                        Case "L4"
                            Return 820
                        Case "L5"
                            Return 846
                        Case "L6"
                            Return 846
                        Case "L7"
                            Return 960
                        Case "M8"
                            Return 960
                        Case "L8"
                            Return 960
                        Case "L9"
                            Return 1034
                        Case "CU"
                            Return 319
                    End Select
                    Return 0
                End Get
                Set(value As Integer)
                End Set
            End Property
            Public Property NO_WRAPS_ON_TAPE As Integer
                Get
                    Select Case CartridgeTypeAbbr
                        Case "L1"
                            Return 48
                        Case "L2"
                            Return 64
                        Case "L3"
                            Return 44
                        Case "L4"
                            Return 56
                        Case "L5"
                            Return 80
                        Case "L6"
                            Return 136
                        Case "L7"
                            Return 112
                        Case "M8"
                            Return 168
                        Case "L8"
                            Return 208
                        Case "L9"
                            Return 280
                    End Select
                    Return 0
                End Get
                Set(value As Integer)
                End Set
            End Property
            Public Property MIN_DATASETS_FOR_ASSESSING_CAPACITY_LOSS As Integer
                Get
                    Select Case CartridgeTypeAbbr
                        Case "L1"
                            Return 11064
                        Case "L2"
                            Return 16600
                        Case "L3"
                            Return 12500
                        Case "L4"
                            Return 19500
                        Case "L5"
                            Return 15920
                        Case "L6"
                            Return 15620
                        Case "L7"
                            Return 11060
                        Case "M8"
                            Return 11060
                        Case "L8"
                            Return 12020
                        Case "L9"
                            Return 13540
                    End Select
                    Return 0
                End Get
                Set(value As Integer)
                End Set
            End Property
            Public Property DENSITY_CODE As Integer
                Get
                    Select Case CartridgeTypeAbbr
                        Case "L1"
                            Return &H40
                        Case "L2"
                            Return &H42
                        Case "L3"
                            Return &H44
                        Case "L4"
                            Return &H46
                        Case "L5"
                            Return &H58
                        Case "L6"
                            Return &H5A
                        Case "L7"
                            Return &H5C
                        Case "M8"
                            Return &H5D
                        Case "L8"
                            Return &H5E
                        Case "L9"
                            Return &H60
                    End Select
                    Return 0
                End Get
                Set(value As Integer)
                End Set
            End Property
            Public Property KB_PER_WRAP As Integer
                Get
                    Return KB_PER_DATASET * SETS_PER_WRAP
                End Get
                Set(value As Integer)
                End Set
            End Property

            Public Property MfgDate As String
            Public Property TapeLength As Integer = 0
            Public Property MediaCode As Integer
            Public Property ParticleType As particle
            Public Property SubstrateType As substrate
            Public Property Servo_Band_ID As svbid
            Public Enum particle
                MP
                BaFe
            End Enum
            Public Enum substrate
                PEN
                SPALTAN
            End Enum
            Public Enum svbid
                legacy_UDIM
                non_UDIM
            End Enum
        End Class
        <TypeConverter(GetType(ExpandableObjectConverter))>
        <Serializable> Public Class Usage
            Public Property Index As Integer

            Public Property PageID As Integer
            Public Property DrvSN As String
            Public Property ThreadCount As Integer
            Public Property SetsWritten As Long
            Public Property SetsRead As Long
            Public Property TotalSets As Long
            Public Property WriteRetries As Integer
            Public Property ReadRetries As Integer
            Public Property UnRecovWrites As Integer
            Public Property UnRecovReads As Integer
            Public Property SuspendedWrites As Integer
            Public Property FatalSusWrites As Integer
            Public Property SuspendedAppendWrites As Integer
            Public Property LP3Passes As Integer
            Public Property MidpointPasses As Integer
            Public Property MaxTapeTemp As Integer

            Public Property CCQWriteFails As Integer
            Public Property C2RecovErrors As Integer
            Public Property DirectionChanges As Integer
            Public Property TapePullingTime As Integer
            Public Property TapeMetresPulled As Integer
            Public Property Repositions As Integer
            Public Property TotalLoadUnloads As Integer
            Public Property StreamFails As Integer

            Public Property MaxDriveTemp As Double
            Public Property MinDriveTemp As Double

            Public Property LifeSetsWritten As Integer
            Public Property LifeSetsRead As Integer
            Public Property LifeWriteRetries As Integer
            Public Property LifeReadRetries As Integer
            Public Property LifeUnRecovWrites As Integer
            Public Property LifeUnRecovReads As Integer
            Public Property LifeSuspendedWrites As Integer
            Public Property LifeFatalSuspWrites As Integer
            Public Property LifeTapeMetresPulled As Integer

            Public Property LifeSuspAppendWrites As Integer
            Public Property LifeLP3Passes As Integer
            Public Property LifeMidpointPasses As Integer

        End Class
        <TypeConverter(GetType(ExpandableObjectConverter))>
        <Serializable> Public Class Media_mfg
            Public Property Version As Byte
            Public Property MediaMfgDate As String
            Public Property MediaVendor As String
            Public ReadOnly Property MediaVendor_Code As Integer
                Get
                    With MediaVendor.ToUpper
                        If .Contains("SONY") Then Return 1
                        If .Contains("TDK") Then Return 2
                        If .Contains("FUJI") Then Return 3
                        If .Contains("MAXELL") Then Return 4
                        If .Contains("IMATION") Then Return 5
                        If .Contains("EMTEC") Then Return 6
                        Return 0
                    End With
                End Get
            End Property
        End Class
        <TypeConverter(GetType(ExpandableObjectConverter))>
        <Serializable> Public Class TapeStatus
            Public Property ThreadCount As Integer
            Public Property EncryptedData As Boolean
            Public Property LastLocation As Integer = 0
        End Class
        <TypeConverter(GetType(ExpandableObjectConverter))>
        <Serializable> Public Class Initialisation
            Public Property LP1 As Integer
            Public Property LP3 As Integer
            Public Property LP5 As Integer
        End Class
        <TypeConverter(GetType(ExpandableObjectConverter))>
        <Serializable> Public Class EOD
            Public Property Partition As Integer
            Public Property Dataset As Integer
            Public Property WrapNumber As Integer
            Public Property Validity As Integer
            Public Property PhysicalPosition As Integer
        End Class
        <TypeConverter(GetType(ExpandableObjectConverter))>
        <Serializable> Public Class CartridgeContent
            Public Property Drive_Id As String
            Public Property Cartridge_Content As Integer
            Public Property PartitionedCartridge As Boolean
            Public Property Type_M_Cartridge As Boolean
            Public Property Drive_Firmware_Id As String
        End Class
        <TypeConverter(GetType(ExpandableObjectConverter))>
        <Serializable> Public Class TapeDirectory
            Public Property Version As Byte
            Public Property FID_Tape_Write_Pass_Partition_0 As Integer
            Public Property FID_Tape_Write_Pass_Partition_1 As Integer
            Public Property FID_Tape_Write_Pass_Partition_2 As Integer
            Public Property FID_Tape_Write_Pass_Partition_3 As Integer
            Public Property Wrap As String
            <TypeConverter(GetType(ListTypeDescriptor(Of List(Of WrapEntryItemSet), WrapEntryItemSet)))>
            Public Property WrapEntryInfo As New List(Of WrapEntryItemSet)
            <TypeConverter(GetType(ListTypeDescriptor(Of List(Of Double), Double)))>
            Public Property CapacityLoss As New List(Of Double)
            <TypeConverter(GetType(ListTypeDescriptor(Of List(Of Dataset), Dataset)))>
            Public Property DatasetsOnWrapData As New List(Of Dataset)
            <TypeConverter(GetType(ExpandableObjectConverter))>
            <Serializable> Public Class Dataset
                Public Property Index As Integer
                Public Property Data As Integer
            End Class
            <TypeConverter(GetType(ExpandableObjectConverter))>
            <Serializable> Public Class WrapEntryItemSet
                Public Property Index As Integer
                Public Property Content As String
                Public Property RawData As Integer()
                Public Property RecCount As Integer
                Public Property FileMarkCount As Integer
            End Class
            Public Function WrapEntry(Index As Integer, Optional ByVal CreateNew As Boolean = True) As WrapEntryItemSet
                For Each d As WrapEntryItemSet In WrapEntryInfo
                    If d.Index = Index Then Return d
                Next
                If CreateNew Then
                    Dim dn As New WrapEntryItemSet With {.Index = Index}
                    WrapEntryInfo.Add(dn)
                    Return dn
                End If
                Return Nothing
            End Function
            Public Function DatasetsOnWrap(Index As Integer, Optional ByVal CreateNew As Boolean = True) As Dataset
                For Each d As Dataset In DatasetsOnWrapData
                    If d.Index = Index Then Return d
                Next
                If CreateNew Then
                    Dim dn As New Dataset With {.Index = Index}
                    DatasetsOnWrapData.Add(dn)
                    Return dn
                End If
                Return Nothing
            End Function
        End Class
        <TypeConverter(GetType(ExpandableObjectConverter))>
        <Serializable> Public Class SuspendWrite
            Public Function DataSetID(Index As Integer, Optional ByVal CreateNew As Boolean = True) As DataInfo
                For Each di As DataInfo In DataSetList
                    If di.Index = Index Then Return di
                Next
                If CreateNew Then
                    Dim din As New DataInfo With {.Index = Index}
                    DataSetList.Add(din)
                    Return din
                End If
                Return Nothing
            End Function
            Public Function WTapePass(Index As Integer, Optional ByVal CreateNew As Boolean = True) As DataInfo
                For Each di As DataInfo In WTapePassList
                    If di.Index = Index Then Return di
                Next
                If CreateNew Then
                    Dim din As New DataInfo With {.Index = Index}
                    WTapePassList.Add(din)
                    Return din
                End If
                Return Nothing
            End Function
            <TypeConverter(GetType(ListTypeDescriptor(Of List(Of DataInfo), DataInfo)))>
            Public Property DataSetList As New List(Of DataInfo)
            <TypeConverter(GetType(ListTypeDescriptor(Of List(Of DataInfo), DataInfo)))>
            Public Property WTapePassList As New List(Of DataInfo)
            <TypeConverter(GetType(ExpandableObjectConverter))>
            <Serializable> Public Class DataInfo
                Public Property Index As Integer
                Public Property Value As Integer
            End Class
        End Class
        <TypeConverter(GetType(ExpandableObjectConverter))>
        <Serializable> Public Class ApplicationSpecific
            Public Property Barcode As String
            Public Property Application_vendor As String
            Public Property Application_name As String
            Public Property Application_version As String
        End Class
        Public Enum gtype
            page
            cartridge_mfg
            media_mfg
            usage
            status
            initialisation
            EOD
            cartridge_content
            tape_directory
            suspended_writes
            application_specific
        End Enum
        Public Function g_CM(gtype As gtype, Optional ByVal gKey As Integer = 0, Optional ByVal createNew As Boolean = True) As Object
            Select Case gtype
                Case gtype.page
                    For Each p As Page In PageData
                        If p.a_Key = gKey Then Return p
                    Next
                    If createNew Then
                        Dim pnew As New Page With {.a_Key = gKey}
                        PageData.Add(pnew)
                        Return pnew
                    Else
                        Return Nothing
                    End If
                Case gtype.cartridge_mfg
                    Return CartridgeMfgData
                Case gtype.media_mfg
                    Return MediaMfgData
                Case gtype.usage
                    For Each u As Usage In UsageData
                        If u.Index = gKey Then Return u
                    Next
                    If createNew Then
                        Dim unew As New Usage With {.Index = gKey}
                        UsageData.Add(unew)
                        Return unew
                    End If
                Case gtype.status
                    Return StatusData
                Case gtype.initialisation
                    Return InitialisationData
                Case gtype.EOD
                    For Each eod As EOD In PartitionEOD
                        If eod.Partition = gKey Then Return eod
                    Next
                    If createNew Then
                        Dim eodn As New EOD With {.Partition = gKey}
                        PartitionEOD.Add(eodn)
                        Return eodn
                    End If
                Case gtype.cartridge_content
                    Return CartridgeContentData
                Case gtype.tape_directory
                    Return TapeDirectoryData
                Case gtype.suspended_writes
                    Return SuspendWriteData
                Case gtype.application_specific
                    Return ApplicationSpecificData
            End Select
            Return Nothing
        End Function
        Public Function RunParse(Optional ByVal Warn As Action(Of String) = Nothing) As Boolean
            If Warn Is Nothing Then
                Warn = Sub(text As String)
                       End Sub
            End If
            ' verify the checksum byte 4 is the xor of the first 4 bytes, otherwise CM data is not valid so don't parse the dataset
            ' also check that byte 5 (the size of CM in 1K units) is 0x04 (LTO1/2/3) or 0x08 (LTO4/5)
            If a_CMBuffer.Length < &H1000 Then
                If a_CMBuffer.Length <> 0 Then
                    Warn($"invalid CM buffer size (0x{Hex(a_CMBuffer.Length)}){vbCrLf}")
                End If
                Return False
            ElseIf ((a_CMBuffer(0) Xor a_CMBuffer(1) Xor a_CMBuffer(2) Xor a_CMBuffer(3)) <> a_CMBuffer(4)) Then
                Warn($"invalid CM checksum byte with length {a_CMBuffer.Length}{vbCrLf}")
                Return False
            Else
                ' Check the CM size byte, located at offset 5.
                a_Length = a_CMBuffer(5)
                ' CR 11775 : Changes For LTO9
                ' --------------------------------------------------------------------
                ' LTO9 has a 32kB CM (length Byte 0x20) so allow For that As well.
                If (a_Length <> 4 AndAlso a_Length <> 8 AndAlso a_Length <> 16 AndAlso a_Length <> 32) Then
                    If a_Length <> 0 Then ' only warn if there's a strange non-zero size
                        Warn($"invalid CM size byte (0x{Hex(a_CMBuffer(5))}){vbCrLf}")
                    End If
                    Return False
                End If
            End If

            ' assume valid CM for now (still need to check page IDs)
            g_ValidCM = True

            ' get page info
            a_UnProt = 0
            a_Offset = 36 ' start of the protected table
            While a_Offset < 400
                a_PageID = g_GetWord(a_CMBuffer, a_Offset) And &HFFF
                If a_PageID = &HFFF Then
                    ' EOPT (end of page table)
                    If a_UnProt = 0 Then
                        ' end of protected page table, therefore the offset value is the start of the unprotected page table
                        a_UnProt = 1
                        a_Offset = g_GetWord(a_CMBuffer, a_Offset + 2)
                    Else
                        Exit While ' end of unprotected page table
                    End If
                ElseIf (a_PageID = &HFFC OrElse a_PageID = &HFFE) Then
                    ' Empty or Pad
                    a_Offset += 4
                Else
                    a_Key = a_PageID
                    With CType(g_CM(gtype.page, a_Key), Page)
                        .Version = (a_CMBuffer(a_Offset) >> 4) And &HF
                        .Offset = g_GetWord(a_CMBuffer, a_Offset + 2)
                        .Length = g_GetWord(a_CMBuffer, .Offset + 2)
                        If a_UnProt > 0 Then .Type = Page.TypeDef.unprotected Else .Type = Page.TypeDef.protected
                        ' check that the page headers contain the correct page ID
                        If g_GetWord(a_CMBuffer, .Offset) <> g_GetWord(a_CMBuffer, a_Offset) Then
                            Warn($"CM Page Header Error: Offset = { .Offset} expected {g_GetWord(a_CMBuffer, a_Offset)}")
                        End If
                    End With
                    a_Offset += 4
                End If
            End While
            '===================== Parse the cartridge manufacturers page =====================
            a_Key = 1
            If g_CM(gtype.page, a_Key, False) IsNot Nothing Then
                With CType(g_CM(gtype.page, a_Key, False), Page)
                    If .Offset >= 0 AndAlso .Length >= 0 Then
                        a_Buffer = substr(a_CMBuffer, .Offset, .Length)
                        With CType(g_CM(gtype.cartridge_mfg), Cartridge_mfg)
                            .TapeVendor = getstr(a_Buffer, 4, 8)
                            .CartridgeSN = getstr(a_Buffer, 12, 10)
                            .CartridgeType = g_GetWord(a_Buffer, 22)
                            .MfgDate = getstr(a_Buffer, 24, 8) ' format YYYYMMDD
                            .TapeLength = g_GetWord(a_Buffer, 32) ' in .25 metre increments
                            .MediaCode = g_GetWord(a_Buffer, 46)
                            Dim a_PageRevision As Byte = a_Buffer(0)
                            Dim a_Particles As Byte = a_Buffer(42)
                            If a_PageRevision >= &H40 Then
                                If ((a_Particles And &HF) <> 0) Then
                                    .ParticleType = Cartridge_mfg.particle.BaFe
                                Else
                                    .ParticleType = Cartridge_mfg.particle.MP
                                End If
                                If (a_Particles And &HF0) = &H10 Then
                                    .SubstrateType = Cartridge_mfg.substrate.SPALTAN
                                Else
                                    .SubstrateType = Cartridge_mfg.substrate.PEN
                                End If
                            Else
                                If ((a_Buffer(42)) <> 0) Then
                                    .ParticleType = Cartridge_mfg.particle.BaFe
                                Else
                                    .ParticleType = Cartridge_mfg.particle.MP
                                End If
                            End If
                            If (.CartridgeType >> 15 And &H1) = 1 Then
                                .Format = "Cleaning Tape"
                            ElseIf .CartridgeType = 1 Then
                                .Format = "LTO-1"
                                a_NWraps = 48
                                a_SetsPerWrap = 5500
                                a_TapeDirLength = 16
                            ElseIf .CartridgeType = 2 Then
                                .Format = "LTO-2"
                                a_NWraps = 64
                                a_SetsPerWrap = 8200
                                a_TapeDirLength = 28
                            Else 'LTO3+ supports WORM, so need to mask off the 'WORM' bit in the upper byte
                                Select Case .CartridgeType And &HFF
                                    Case 4
                                        .Format = "LTO-3"
                                        a_NWraps = 44
                                        a_SetsPerWrap = 6000
                                        a_TapeDirLength = 32
                                    Case 8
                                        .Format = "LTO-4"
                                        a_NWraps = 56
                                        a_SetsPerWrap = 9500
                                        a_TapeDirLength = 32
                                    Case 16
                                        .Format = "LTO-5"
                                        a_NWraps = 80
                                        a_SetsPerWrap = 7800
                                        a_TapeDirLength = 32
                                    Case 32
                                        .Format = "LTO-6"
                                        a_NWraps = 136
                                        a_SetsPerWrap = 7805
                                        a_TapeDirLength = 32
                                    Case 64
                                        .Format = "LTO-7"
                                        a_NWraps = 112
                                        a_SetsPerWrap = 10950
                                        a_TapeDirLength = 32
                                    Case 128
                                        .Format = "LTO-8"
                                        a_NWraps = 208
                                        a_SetsPerWrap = 11660
                                        a_TapeDirLength = 32
                                    Case 129
                                        .Format = "LTO-9"
                                        a_NWraps = 280
                                        a_SetsPerWrap = 6770
                                        a_TapeDirLength = 32
                                    Case Else
                                        Select Case .CartridgeType
                                            Case 5126
                                                .Format = "3592JA"
                                            Case 13
                                                .Format = "3592JB"
                                            Case 15
                                                .Format = "3592JC"
                                            Case 17
                                                .Format = "3592JD"
                                            Case 19
                                                .Format = "3592JE"
                                            Case 21
                                                .Format = "3592JF"
                                            Case 13318
                                                .Format = "3592JJ"
                                            Case 8207
                                                .Format = "3592JK"
                                            Case 8209
                                                .Format = "3592JL"
                                            Case 8211
                                                .Format = "3592JM"
                                            Case 8213
                                                .Format = "3592JN"

                                        End Select
                                End Select
                                If (.CartridgeType >> 13 And 1) = 1 Then
                                    .Format &= " WORM"
                                End If
                            End If
                        End With
                    End If
                End With
            End If

            '===================== Parse the media manufacturers page =====================
            a_Key = 2
            If g_CM(gtype.page, a_Key, False) IsNot Nothing Then
                With CType(g_CM(gtype.page, a_Key, False), Page)
                    If .Offset >= 0 AndAlso .Length >= 0 Then
                        a_Buffer = substr(a_CMBuffer, .Offset, .Length)
                        With CType(g_CM(gtype.media_mfg), Media_mfg)
                            .Version = a_Buffer(0) >> 4
                            Dim suboffset As Integer = 0
                            If .Version >= 8 Then suboffset = 2 'For 3592 CM
                            .MediaMfgDate = getstr(a_Buffer, 4 + suboffset, 8)
                            .MediaVendor = getstr(a_Buffer, 12 + suboffset, 8)
                            ' check the MediaMfgDate for servo band ID
                            Try
                                If CType(g_CM(gtype.cartridge_mfg), Cartridge_mfg).Format.Contains("LTO-8") Then
                                    If .MediaMfgDate.StartsWith("22") Then
                                        CType(g_CM(gtype.cartridge_mfg), Cartridge_mfg).Servo_Band_ID = Cartridge_mfg.svbid.legacy_UDIM
                                    ElseIf .MediaVendor.StartsWith(">>") Then
                                        CType(g_CM(gtype.cartridge_mfg), Cartridge_mfg).Servo_Band_ID = Cartridge_mfg.svbid.non_UDIM
                                    End If
                                End If
                            Catch ex As Exception
                            End Try
                        End With
                    End If
                End With
            End If

            '===================== Parse the usage pages =====================
            ' usage information page definition Is changed For LTO5
            ' -- And the drive serial number field grew longer, so we'll use
            '    a variable For the length rather than assuming 10 chars.
            Dim driveSNlength As Integer
            With CType(g_CM(gtype.cartridge_mfg), Cartridge_mfg)
                If .IsLTO5Plus Then
                    at_Offset = {32, 36, 44, 52, 56, 60, 62, 64, 66, 80}
                    driveSNlength = 16
                Else
                    at_Offset = {24, 28, 36, 44, 48, 52, 54, 56, 58}
                    driveSNlength = 10
                End If
            End With
            ' parse the 4 usage information pages and mechanism related sub-pages
            a_Length = &H40
            If g_CM(gtype.page, &H108, False) IsNot Nothing Then
                With CType(g_CM(gtype.page, &H108, False), Page)
                    If .Length >= 0 Then a_Length = .Length
                End With
            End If
            a_Err = 0
            ' The Mechanism Related Information (Page 106) is vendor specific so we
            ' need To determine which Vendor wrote it before decoding its contents.
            Dim MechRelatedInfoVendorID As String = ""
            If g_CM(gtype.page, &H106, False) IsNot Nothing Then
                With CType(g_CM(gtype.page, &H106), Page)
                    If .Offset >= 0 AndAlso .Length >= 0 Then
                        MechRelatedInfoVendorID = Encoding.ASCII.GetString(substr(a_CMBuffer, .Offset + 4, 8)).TrimEnd
                    End If
                End With
            End If
            For a_Index = 0 To 3
                ' usage info page ID's 0x108-0x10B correspond to usage info pages 1-4 and mechanism related sub-pages 1-4
                a_Key = a_Index + &H108
                If g_CM(gtype.page, a_Key, False) IsNot Nothing AndAlso g_CM(gtype.page, &H106, False) IsNot Nothing Then
                    Dim valid As Boolean = True
                    With CType(g_CM(gtype.page, a_Key, False), Page)
                        If .Offset < 0 OrElse .Length < 0 Then
                            valid = False
                        End If
                    End With
                    With CType(g_CM(gtype.page, &H106, False), Page)
                        If .Offset < 0 OrElse .Length < 0 Then
                            valid = False
                        End If
                    End With
                    If valid Then
                        ' get the usage info page
                        a_Buffer = substr(a_CMBuffer, CType(g_CM(gtype.page, a_Key, False), Page).Offset, a_Length)
                        ' append the corresponding mechanism related sub-page
                        Dim b_Buffer As Byte() = substr(a_CMBuffer, CType(g_CM(gtype.page, &H106, False), Page).Offset + 12 + 64 * a_Index, 64)
                        a_Buffer = a_Buffer.Concat(b_Buffer).ToArray()
                        With a_Usage(a_Index, True)
                            .data0 = a_Buffer
                            .data1 = g_GetDWord(a_Buffer, at_Offset(0))
                        End With
                    Else
                        a_Err = 1
                    End If
                Else
                    a_Err = 1
                End If
            Next

            If a_Err = 0 Then
                'reverse sort the pages by tape thread count
                a_UsageData.Sort(New Comparison(Of UsagePage)(Function(a As UsagePage, b As UsagePage) As Integer
                                                                  Return b.data1.CompareTo(a.data1)
                                                              End Function))
                For i As Integer = 0 To a_UsageData.Count - 1
                    a_UsageData(i).index = i
                Next
                ' parse the sorted data structure and populate the return array
                For a_Index = 0 To 2
                    With CType(g_CM(gtype.usage, a_Index), Usage)
                        .PageID = g_GetWord(a_Usage(a_Index, False).data0, 0)
                        ' parameters need valid data for the current load in order to calculate (previous load fields are set to 0, per CM spec, so difference calculations will be correct)
                        If g_GetWord(a_Usage(a_Index, False).data0, 12) <> 0 Then
                            'Remove trailing whitespace
                            .DrvSN = Encoding.ASCII.GetString(substr(a_Usage(a_Index, False).data0, 12, driveSNlength)).TrimEnd()
                            'IBM-based drives have two leading chars
                            If .DrvSN.Length > 10 Then
                                'Just the last ten chars
                                .DrvSN = .DrvSN.Substring(.DrvSN.Length - 10)
                            End If
                        Else
                            .DrvSN = ""
                        End If
                        .ThreadCount = g_GetDWord(a_Usage(a_Index, False).data0, at_Offset(0))
                        .SetsWritten = g_GetInt64(a_Usage(a_Index, False).data0, at_Offset(1)) - g_GetInt64(a_Usage(a_Index + 1, False).data0, at_Offset(1))
                        .SetsRead = g_GetInt64(a_Usage(a_Index, False).data0, at_Offset(2)) - g_GetInt64(a_Usage(a_Index + 1, False).data0, at_Offset(2))
                        .TotalSets = g_GetInt64(a_Usage(a_Index, False).data0, at_Offset(1)) + g_GetInt64(a_Usage(a_Index, False).data0, at_Offset(2))
                        .WriteRetries = g_GetDWord(a_Usage(a_Index, False).data0, at_Offset(3)) - g_GetDWord(a_Usage(a_Index + 1, False).data0, at_Offset(3))
                        .ReadRetries = g_GetDWord(a_Usage(a_Index, False).data0, at_Offset(4)) - g_GetDWord(a_Usage(a_Index + 1, False).data0, at_Offset(4))
                        .UnRecovWrites = g_GetWord(a_Usage(a_Index, False).data0, at_Offset(5)) - g_GetWord(a_Usage(a_Index + 1, False).data0, at_Offset(5))
                        .UnRecovReads = g_GetWord(a_Usage(a_Index, False).data0, at_Offset(6)) - g_GetWord(a_Usage(a_Index + 1, False).data0, at_Offset(6))
                        .SuspendedWrites = g_GetWord(a_Usage(a_Index, False).data0, at_Offset(7)) - g_GetWord(a_Usage(a_Index + 1, False).data0, at_Offset(7))
                        .FatalSusWrites = g_GetWord(a_Usage(a_Index, False).data0, at_Offset(8)) - g_GetWord(a_Usage(a_Index + 1, False).data0, at_Offset(8))

                        'LTO5 only (doesn't look like early LTO5 drives update this area, so check if there is valid temperature info first)
                        If CType(g_CM(gtype.cartridge_mfg), Cartridge_mfg).IsLTO5Plus AndAlso a_Usage(a_Index, False).data0(76) > 0 Then
                            .SuspendedAppendWrites = g_GetWord(a_Usage(a_Index, False).data0, 28) - g_GetWord(a_Usage(a_Index + 1, False).data0, 28)
                            .LP3Passes = g_GetDWord(a_Usage(a_Index, False).data0, 68) - g_GetDWord(a_Usage(a_Index + 1, False).data0, 68)
                            .MidpointPasses = g_GetDWord(a_Usage(a_Index, False).data0, 72) - g_GetDWord(a_Usage(a_Index + 1, False).data0, 72)
                            .MaxTapeTemp = a_Usage(a_Index, False).data0(76)
                        End If
                        ' Because the code above helpfully (?) appended the mech-related subpage to each usage page,
                        '  we can parse the mech-related data at the same time. But only If it's HP data..
                        If MechRelatedInfoVendorID.Contains("HP") Then
                            .CCQWriteFails = CInt(g_GetInt64(a_Usage(a_Index, False).data0, a_Length) - g_GetInt64(a_Usage(a_Index + 1, False).data0, a_Length))
                            .C2RecovErrors = g_GetDWord(a_Usage(a_Index, False).data0, a_Length + 8) - g_GetDWord(a_Usage(a_Index + 1, False).data0, a_Length + 8)
                            .DirectionChanges = g_GetDWord(a_Usage(a_Index, False).data0, a_Length + 24) - g_GetDWord(a_Usage(a_Index + 1, False).data0, a_Length + 24)
                            .TapePullingTime = g_GetDWord(a_Usage(a_Index, False).data0, a_Length + 28) - g_GetDWord(a_Usage(a_Index + 1, False).data0, a_Length + 28)
                            .TapeMetresPulled = g_GetDWord(a_Usage(a_Index, False).data0, a_Length + 32)
                            .Repositions = g_GetDWord(a_Usage(a_Index, False).data0, a_Length + 36) - g_GetDWord(a_Usage(a_Index + 1, False).data0, a_Length + 36)
                            .TotalLoadUnloads = g_GetDWord(a_Usage(a_Index, False).data0, a_Length + 40)
                            .StreamFails = g_GetDWord(a_Usage(a_Index, False).data0, a_Length + 44) - g_GetDWord(a_Usage(a_Index + 1, False).data0, a_Length + 44)

                            'for some reason, temperature doesn't always get recorded(??)
                            If g_GetWord(a_Usage(a_Index, False).data0, a_Length + 48) > 0 Then
                                .MaxDriveTemp = g_GetWord(a_Usage(a_Index, False).data0, a_Length + 48) / 256
                            End If
                            If g_GetWord(a_Usage(a_Index, False).data0, a_Length + 50) > 0 Then
                                .MinDriveTemp = g_GetWord(a_Usage(a_Index, False).data0, a_Length + 50) / 256
                            End If
                            If .CCQWriteFails < 0 Then .CCQWriteFails = 0
                            If .C2RecovErrors < 0 Then .C2RecovErrors = 0
                            If .DirectionChanges < 0 Then .DirectionChanges = 0
                            If .TapePullingTime < 0 Then .TapePullingTime = 0
                            If .TapeMetresPulled < 0 Then .TapeMetresPulled = 0
                            If .Repositions < 0 Then .Repositions = 0
                            If .StreamFails < 0 Then .StreamFails = 0
                        Else
                            .CCQWriteFails = 0
                            .C2RecovErrors = 0
                        End If
                        .LifeSetsWritten = CInt(g_GetInt64(a_Usage(a_Index, False).data0, at_Offset(1)))
                        .LifeSetsRead = CInt(g_GetInt64(a_Usage(a_Index, False).data0, at_Offset(2)))
                        .LifeWriteRetries = g_GetDWord(a_Usage(a_Index, False).data0, at_Offset(3))
                        .LifeReadRetries = g_GetDWord(a_Usage(a_Index, False).data0, at_Offset(4))
                        .LifeUnRecovWrites = g_GetWord(a_Usage(a_Index, False).data0, at_Offset(5))
                        .LifeUnRecovReads = g_GetWord(a_Usage(a_Index, False).data0, at_Offset(6))
                        .LifeSuspendedWrites = g_GetWord(a_Usage(a_Index, False).data0, at_Offset(7))
                        .LifeFatalSuspWrites = g_GetWord(a_Usage(a_Index, False).data0, at_Offset(8))
                        If at_Offset.Length >= 10 Then .LifeTapeMetresPulled = g_GetDWord(a_Usage(a_Index, False).data0, at_Offset(9))
                        If CType(g_CM(gtype.cartridge_mfg), Cartridge_mfg).IsLTO5Plus Then
                            .LifeSuspAppendWrites = g_GetWord(a_Usage(a_Index, False).data0, 28)
                            .LifeLP3Passes = g_GetDWord(a_Usage(a_Index, False).data0, 68)
                            .LifeMidpointPasses = g_GetDWord(a_Usage(a_Index, False).data0, 72)
                        End If
                        'the following should always be >= 0 (it's not always, as confirmed by looking at the CM buffer (drv fw issue?)
                        If .ThreadCount < 0 Then .ThreadCount = 0
                        If .SetsWritten < 0 Then .SetsWritten = 0
                        If .SetsRead < 0 Then .SetsRead = 0
                        If .WriteRetries < 0 Then .WriteRetries = 0
                        If .ReadRetries < 0 Then .ReadRetries = 0
                        If .UnRecovWrites < 0 Then .UnRecovWrites = 0
                        If .UnRecovReads < 0 Then .UnRecovReads = 0
                        If .SuspendedWrites < 0 Then .SuspendedWrites = 0
                        If .FatalSusWrites < 0 Then .FatalSusWrites = 0
                        If a_Index = 0 Then
                            g_TPM = .TapeMetresPulled
                        End If
                    End With
                Next
            End If

            '===================== Parse the "tape status and tape alert flags" page =====================
            a_Key = &H105
            If g_CM(gtype.page, a_Key, False) IsNot Nothing Then
                With CType(g_CM(gtype.page, a_Key, False), Page)
                    If .Offset >= 0 AndAlso .Length >= 0 Then
                        a_Buffer = substr(a_CMBuffer, .Offset, .Length)
                        With CType(g_CM(gtype.status), TapeStatus)
                            .ThreadCount = g_GetDWord(a_Buffer, 12)
                            'check if any encrypted data (LTO4+ only) by checking if the First Encrypted Logical Object field is all 1's
                            If CType(g_CM(gtype.cartridge_mfg, createNew:=False), Cartridge_mfg).IsLTO4Plus Then
                                If g_GetWord(a_Buffer, 22) = &HFFFF AndAlso g_GetWord(a_Buffer, 24) = &HFFFF AndAlso g_GetWord(a_Buffer, 26) = &HFFFF Then
                                    .EncryptedData = False
                                Else
                                    .EncryptedData = True
                                End If
                            End If

                            'for cleaning tapes, bytes 26-27 is the last location used on the tape in quarter-metres
                            If CType(g_CM(gtype.cartridge_mfg, createNew:=False), Cartridge_mfg).Format.Contains("Clean") Then
                                .LastLocation = g_GetWord(a_Buffer, 26)
                            End If
                        End With
                    End If


                End With
            End If
            'for cleaning tapes, check if expired
            If CType(g_CM(gtype.cartridge_mfg, createNew:=False), Cartridge_mfg).Format.Contains("Clean") Then
                'cleaning length depends on drive type
                If False Then
                    'LTO1
                    a_CleanLength = 18.5
                Else
                    a_CleanLength = 5.5
                End If
                If CType(g_CM(gtype.status, createNew:=False), TapeStatus).LastLocation >= 0 AndAlso CType(g_CM(gtype.cartridge_mfg, createNew:=False), Cartridge_mfg).TapeLength >= 0 Then
                    a_CleansRemaining = CInt(CType(g_CM(gtype.cartridge_mfg, createNew:=False), Cartridge_mfg).TapeLength / 4 - 11)
                    a_CleansRemaining = CInt(a_CleansRemaining - CType(g_CM(gtype.status, createNew:=False), TapeStatus).LastLocation / 4)
                    a_CleansRemaining = CInt(a_CleansRemaining / a_CleanLength)
                    If a_CleansRemaining <= 0 Then
                        CType(g_CM(gtype.cartridge_mfg, createNew:=False), Cartridge_mfg).Format &= " (expired)"
                    End If
                End If
            End If

            '===================== Parse the initialisation page =====================
            a_Key = &H101
            If g_CM(gtype.page, a_Key, False) IsNot Nothing Then
                With CType(g_CM(gtype.page, a_Key, False), Page)
                    If .Offset >= 0 AndAlso .Length >= 0 Then
                        a_Buffer = substr(a_CMBuffer, .Offset, .Length)
                        'initialisation page definition is changed for LTO5+
                        If CType(g_CM(gtype.cartridge_mfg, createNew:=False), Cartridge_mfg).IsLTO5Plus Then
                            at_Offset = {28, 32, 40, 48}
                        Else
                            at_Offset = {22, 24, 32, 40}
                        End If
                        With CType(g_CM(gtype.initialisation), Initialisation)
                            .LP1 = g_GetDWord(a_Buffer, at_Offset(1))
                            .LP3 = g_GetDWord(a_Buffer, at_Offset(2))
                            .LP5 = g_GetDWord(a_Buffer, at_Offset(3))
                        End With
                    End If
                End With
            End If

            '#===================== Parse the EOD page for Partition 0-3 =====================
            For pnum As Integer = 0 To 3
                a_Key = {&H104, &H10E, &H10F, &H110}(pnum)
                If g_CM(gtype.page, a_Key, False) IsNot Nothing Then
                    With CType(g_CM(gtype.page, a_Key, False), Page)
                        If .Offset >= 0 AndAlso .Length >= 0 Then
                            a_Buffer = substr(a_CMBuffer, .Offset, .Length)
                            With CType(g_CM(gtype.EOD, pnum), EOD)
                                .Dataset = g_GetDWord(a_Buffer, 24)
                                .WrapNumber = g_GetDWord(a_Buffer, 28)
                                .Validity = g_GetWord(a_Buffer, 32)
                                .PhysicalPosition = g_GetDWord(a_Buffer, 36)
                            End With
                        End If
                    End With
                End If
            Next

            '===================== Parse the cartridge content page (if it exists!) =====================
            If CType(g_CM(gtype.cartridge_mfg, createNew:=False), Cartridge_mfg).IsLTO5Plus Then
                a_Key = &H10D
                If g_CM(gtype.page, a_Key, False) IsNot Nothing Then
                    With CType(g_CM(gtype.page, a_Key, False), Page)
                        If .Offset >= 0 AndAlso .Length >= 0 Then
                            a_Buffer = substr(a_CMBuffer, .Offset, .Length)
                            With CType(g_CM(gtype.cartridge_content), CartridgeContent)
                                .Drive_Id = Encoding.ASCII.GetString(substr(a_Buffer, 12, 16)).TrimEnd
                                .Cartridge_Content = g_GetWord(a_Buffer, 28)
                                .PartitionedCartridge = (((a_Buffer(28) >> 3) And 1) <> 0)
                                If CType(g_CM(gtype.cartridge_mfg, createNew:=False), Cartridge_mfg).IsLTO7Plus Then
                                    .Type_M_Cartridge = ((a_Buffer(28) And 1) <> 0)
                                End If
                                If CType(g_CM(gtype.cartridge_mfg, createNew:=False), Cartridge_mfg).Format.Contains("LTO-5") Then
                                    .Drive_Firmware_Id = Encoding.ASCII.GetString(substr(a_Buffer, 48, 4))
                                Else
                                    .Drive_Firmware_Id = Encoding.ASCII.GetString(substr(a_Buffer, 52, 4))
                                End If
                                If CType(g_CM(gtype.cartridge_mfg, createNew:=False), Cartridge_mfg).Format.Contains("LTO-7") AndAlso .Type_M_Cartridge Then
                                    CType(g_CM(gtype.cartridge_mfg, createNew:=False), Cartridge_mfg).Format = "LTO-7 Type M"
                                    a_NWraps = 168
                                End If
                            End With
                        End If
                    End With
                End If
            End If

            '===================== Parse the Tape Directory page =====================
            a_Key = &H103
            If g_CM(gtype.page, a_Key, False) IsNot Nothing AndAlso g_CM(gtype.EOD, 0, False) IsNot Nothing Then
                With CType(g_CM(gtype.page, a_Key, False), Page)
                    If .Offset >= 0 AndAlso .Length >= 0 AndAlso ((CType(g_CM(gtype.EOD, 0, False), EOD).Validity) <> 0) Then
                        a_Buffer = substr(a_CMBuffer, .Offset, .Length)
                        With CType(g_CM(gtype.tape_directory, createNew:=True), TapeDirectory)
                            .Version = CByte((a_Buffer(0) >> 4) And &HF)
                            If CType(g_CM(gtype.cartridge_mfg, createNew:=False), Cartridge_mfg).IsLTO6Plus Then
                                a_HdrLength = 48
                                a_TapeWritePassPartition = g_GetDWord(a_Buffer, 4)
                                .FID_Tape_Write_Pass_Partition_0 = a_TapeWritePassPartition
                                a_TapeWritePassPartition = g_GetDWord(a_Buffer, 8)
                                .FID_Tape_Write_Pass_Partition_1 = a_TapeWritePassPartition
                                a_TapeWritePassPartition = g_GetDWord(a_Buffer, 12)
                                .FID_Tape_Write_Pass_Partition_2 = a_TapeWritePassPartition
                                a_TapeWritePassPartition = g_GetDWord(a_Buffer, 16)
                                .FID_Tape_Write_Pass_Partition_3 = a_TapeWritePassPartition
                                a_OutputStr = $"{"WritePass".PadRight(12) _
                                    }{"DatasetID".PadRight(14) _
                                    }{"HOW RecCnt".PadRight(14) _
                                    }{"EOW RecCnt".PadRight(14) _
                                    }{"HOW FMCnt".PadRight(14) _
                                    }{"EOW FMCnt".PadRight(14) _
                                    }{"FM Map".PadRight(14) _
                                    }{"CRC".PadRight(14)}"
                                .Wrap = a_OutputStr
                            ElseIf CType(g_CM(gtype.cartridge_mfg, createNew:=False), Cartridge_mfg).IsLTO4Plus Then
                                a_HdrLength = 16
                                a_TapeWritePassPartition = g_GetDWord(a_Buffer, 4)
                                .FID_Tape_Write_Pass_Partition_0 = a_TapeWritePassPartition
                                a_TapeWritePassPartition = g_GetDWord(a_Buffer, 8)
                                .FID_Tape_Write_Pass_Partition_1 = a_TapeWritePassPartition
                                a_OutputStr = $"{"WritePass".PadRight(12) _
                                    }{"DatasetID".PadRight(14) _
                                    }{"HOW RecCnt".PadRight(14) _
                                    }{"EOW RecCnt".PadRight(14) _
                                    }{"HOW FMCnt".PadRight(14) _
                                    }{"EOW FMCnt".PadRight(14) _
                                    }{"FM Map".PadRight(14) _
                                    }{"CRC".PadRight(14)}"
                                .Wrap = a_OutputStr
                            ElseIf CType(g_CM(gtype.cartridge_mfg, createNew:=False), Cartridge_mfg).Format.Contains("LTO-3") Then
                                a_HdrLength = 16
                                a_OutputStr = $"{"WritePass".PadRight(12) _
                                    }{"DatasetID".PadRight(14) _
                                    }{"HOW RecCnt".PadRight(14) _
                                    }{"EOW RecCnt".PadRight(14) _
                                    }{"HOW FMCnt".PadRight(14) _
                                    }{"EOW FMCnt".PadRight(14) _
                                    }{"FM Map".PadRight(14) _
                                    }{"CRC".PadRight(14)}"
                                .Wrap = a_OutputStr
                            ElseIf CType(g_CM(gtype.cartridge_mfg, createNew:=False), Cartridge_mfg).Format.Contains("LTO-2") Then
                                a_HdrLength = 16
                                a_OutputStr = $"{"WritePass".PadRight(12) _
                                    }{"DatasetID".PadRight(14) _
                                    }{"HOW RecCnt".PadRight(14) _
                                    }{"EOW RecCnt".PadRight(14) _
                                    }{"HOW FMCnt".PadRight(14) _
                                    }{"EOW FMCnt".PadRight(14) _
                                    }{"CRC".PadRight(14)}"
                                .Wrap = a_OutputStr
                            ElseIf CType(g_CM(gtype.cartridge_mfg, createNew:=False), Cartridge_mfg).Format.Contains("LTO-1") Then
                                a_HdrLength = 16
                                a_OutputStr = $"{"WritePass".PadRight(12) _
                                    }{"DatasetID".PadRight(14) _
                                    }{"RecordCount".PadRight(14) _
                                    }{"FilemarkCount".PadRight(14) _
                                    }{"CRC".PadRight(14)}"
                                .Wrap = a_OutputStr
                            ElseIf .Version = 9 Then
                                a_HdrLength = 16
                                a_TapeWritePassPartition = g_GetDWord(a_Buffer, 4)
                                .FID_Tape_Write_Pass_Partition_0 = a_TapeWritePassPartition
                                a_TapeWritePassPartition = g_GetDWord(a_Buffer, 8)
                                .FID_Tape_Write_Pass_Partition_1 = a_TapeWritePassPartition
                                a_OutputStr = $"{"WritePass".PadRight(12) _
                                    }{"DatasetID".PadRight(14) _
                                    }{"HOW RecCnt".PadRight(14) _
                                    }{"EOW RecCnt".PadRight(14) _
                                    }{"HOW FMCnt".PadRight(14) _
                                    }{"EOW FMCnt".PadRight(14) _
                                    }{"FM Map".PadRight(14)}"
                                .Wrap = a_OutputStr
                            Else
                                a_HdrLength = 16
                            End If
                            PublishTapeDirectoryPage(a_Buffer)
                            a_LastID = 0

                            ' Get number of partitions.

                            a_NumPartitions = New List(Of EOD)
                            For Each eod As EOD In PartitionEOD
                                a_NumPartitions.Add(eod)
                            Next
                            .CapacityLoss = New List(Of Double)
                            For a_Index = 0 To a_NWraps - 1
                                a_SetID = g_GetDWord(a_Buffer, a_HdrLength + a_TapeDirLength * a_Index + 4)
                                If a_SetID = UNUSED_WRAP_IDENTIFIER Then
                                    .CapacityLoss.Add(-1)
                                    Continue For
                                ElseIf a_SetID = GUARD_WRAP_IDENTIFIER Then
                                    .CapacityLoss.Add(-3)
                                    Continue For
                                ElseIf a_SetID = 0 Then
                                    .CapacityLoss.Add(0)
                                    Continue For
                                Else
                                    a_set = 0
                                    For a_PartitionKey = 0 To a_NumPartitions.Count - 1
                                        If ((CType(g_CM(gtype.EOD, a_PartitionKey, False), EOD).Validity) <> 0) AndAlso CType(g_CM(gtype.EOD, a_PartitionKey, False), EOD).WrapNumber = a_Index Then
                                            .CapacityLoss.Add(-2)
                                            'partially written wrap (EOD Wrap).
                                            a_set = 1
                                            Exit For
                                        End If
                                    Next
                                    If a_set = 0 Then
                                        .CapacityLoss.Add(Math.Max(0, 100 * (1 - (a_SetID - a_LastID) / a_SetsPerWrap)))
                                        a_LastID = a_SetID
                                    End If
                                End If
                            Next
                            a_LastID = 0
                            Dim a As Integer
                            Dim DatasetsOnWrap_Index As Integer
                            Dim TapeDirectoryEntry As Byte()
                            For a_Index = 0 To a_NWraps - 1
                                DatasetsOnWrap_Index = a_Index
                                TapeDirectoryEntry = substr(a_Buffer, a_HdrLength + a_TapeDirLength * a_Index, a_TapeDirLength)
                                a = g_GetDWord(TapeDirectoryEntry, 4)
                                If a = UNUSED_WRAP_IDENTIFIER OrElse a = GUARD_WRAP_IDENTIFIER Then
                                    a = 0
                                    .DatasetsOnWrap(DatasetsOnWrap_Index).Data = a
                                Else
                                    .DatasetsOnWrap(DatasetsOnWrap_Index).Data = a - a_LastID
                                End If
                                a_LastID = a
                            Next
                        End With
                    End If
                End With
            End If

            'if partitioned cart and LTO5 or later
            If CType(g_CM(gtype.cartridge_content), CartridgeContent).PartitionedCartridge Then
                Dim pKeyList() As Integer = {}
                If CType(g_CM(gtype.cartridge_mfg), Cartridge_mfg).Format.Contains("LTO-5") Then
                    pKeyList = {&H10E}
                ElseIf CType(g_CM(gtype.cartridge_mfg), Cartridge_mfg).IsLTO6Plus Then
                    pKeyList = {&H10E, &H10F, &H110}
                End If
                For pNum As Integer = 1 To pKeyList.Count
                    a_Key = pKeyList(pNum - 1)
                    If g_CM(gtype.page, a_Key, False) IsNot Nothing Then
                        With CType(g_CM(gtype.page, a_Key, False), Page)
                            If .Length >= 0 AndAlso .Offset >= 0 Then
                                a_Buffer = substr(a_CMBuffer, .Offset, .Length)
                                With CType(g_CM(gtype.EOD, pNum), EOD)
                                    .Dataset = g_GetDWord(a_Buffer, 24)
                                    .WrapNumber = g_GetDWord(a_Buffer, 28)
                                    .Validity = g_GetWord(a_Buffer, 32)
                                    .PhysicalPosition = g_GetDWord(a_Buffer, 36)
                                End With
                            End If
                        End With
                    End If
                Next
            End If

            '===================== Parse the Suspended Append Writes page =====================
            a_Key = &H107
            Dim a_NoSlot As Integer
            a_Index = 0
            a_Offset = 0
            If g_CM(gtype.page, a_Key, False) IsNot Nothing Then
                With CType(g_CM(gtype.page, a_Key, False), Page)
                    If .Offset >= 0 AndAlso .Length >= 0 Then
                        a_Buffer = substr(a_CMBuffer, .Offset, .Length)
                        If Not CType(g_CM(gtype.cartridge_mfg, createNew:=False), Cartridge_mfg).IsLTO5Plus Then
                            ' LTO-1 through LTO-4 tapes have 14 slots (0-13)
                            a_NoSlot = 14
                        ElseIf CType(g_CM(gtype.cartridge_mfg, createNew:=False), Cartridge_mfg).Format.Contains("LTO-5") Then
                            'LTO-5 tape has 22 slots (0-21)
                            a_NoSlot = 22
                        ElseIf CType(g_CM(gtype.cartridge_mfg, createNew:=False), Cartridge_mfg).Format.Contains("LTO-6") Then
                            'LTO-6 tape has 38 slots (0-37)
                            a_NoSlot = 38
                        ElseIf CType(g_CM(gtype.cartridge_mfg, createNew:=False), Cartridge_mfg).Format.Contains("LTO-7") Then
                            'LTO-7 tape has 38 slots (0-37)
                            a_NoSlot = 38
                        ElseIf CType(g_CM(gtype.cartridge_mfg, createNew:=False), Cartridge_mfg).Format.Contains("LTO-8") Then
                            'LTO-8 tape has 38 slots (0-37), including Type M
                            a_NoSlot = 38
                        ElseIf CType(g_CM(gtype.cartridge_mfg, createNew:=False), Cartridge_mfg).Format.Contains("LTO-9") Then
                            'LTO-9 tape has 38 slots (0-37)
                            a_NoSlot = 38
                        End If

                        'Single partition, look for available slots, index 0 - 13
                        For a_Index = 0 To a_NoSlot - 1
                            With CType(g_CM(gtype.suspended_writes), SuspendWrite)
                                .DataSetID(a_Index).Value = g_GetDWord(a_Buffer, a_Offset + 8)
                                .WTapePass(a_Index).Value = g_GetDWord(a_Buffer, a_Offset + 12)
                                a_Offset += 8
                            End With
                        Next
                    End If
                End With
            End If

            '===================== Parse the Tape Control page (LTO9 Only)=====================
            'a_Key = &H183
            'tapectrl
            '    TDS Data Version           BYTE        a_Buffer(4)
            '    Temperature                BYTE        a_Buffer(5)
            '    Humidity                   BYTE        a_Buffer(6)
            '    Characterization needed    BYTE        a_Buffer(7)
            '    Timestamp                  DWORD       a_Buffer(16)
            '    DriveSN                    Byte(12)    a_Buffer(34)

            '===================== Parse the Application Specific page =====================
            ' See MAMAttribute
            a_Key = &H200
            If g_CM(gtype.page, a_Key, False) IsNot Nothing Then
                With CType(g_CM(gtype.page, a_Key, False), Page)
                    If .Offset >= 0 AndAlso .Length >= 0 Then
                        a_Buffer = substr(a_CMBuffer, .Offset, .Length)
                        With CType(g_CM(gtype.application_specific), ApplicationSpecific)
                            If getstr(a_Buffer, 4, 6).Equals("MAM001") OrElse getstr(a_Buffer, 4, 6).Equals("MAM002") Then
                                a_Index = 10
                                While a_Index < a_Buffer.Length
                                    a_AttributeID = g_GetWord(a_Buffer, a_Index)
                                    ' Attribute length definition changed from "MAM001" To "MAM002".  "MAM001" has 2 bytes For attribute length, whereas
                                    ' "MAM002" has 1.5 bytes.  Using 1.5 bytes For both cases, As the attribute length Is always small compared To what
                                    ' can be represented by 1.5 bytes (QXCR1001109840).
                                    a_AttributeLength = g_GetWord(a_Buffer, a_Index + 2) And &HFFF
                                    If a_AttributeID = &HFFF OrElse a_AttributeLength = 0 Then Exit While
                                    ' barcode
                                    If a_AttributeID = &H806 Then
                                        a_Barcode = getstr(a_Buffer, a_Index + 4, a_AttributeLength).TrimEnd()
                                        If a_Barcode <> "" Then
                                            .Barcode = a_Barcode
                                            g_Barcode = a_Barcode
                                        End If
                                    End If

                                    ' LTFS vendor
                                    If a_AttributeID = &H800 Then
                                        .Application_vendor = getstr(a_Buffer, a_Index + 4, a_AttributeLength).TrimEnd()
                                    End If

                                    ' LTFS formatted
                                    If a_AttributeID = &H801 Then
                                        .Application_name = getstr(a_Buffer, a_Index + 4, a_AttributeLength).TrimEnd()
                                    End If

                                    ' LTFS version
                                    If a_AttributeID = &H802 Then
                                        .Application_version = getstr(a_Buffer, a_Index + 4, a_AttributeLength).TrimEnd()
                                    End If

                                    a_Index += 4 + a_AttributeLength
                                End While
                            End If
                        End With
                    End If
                End With
            End If
            Return True
        End Function
        Public Sub PublishTapeDirectoryPage(ByRef Buffer As Byte())
            Dim a_WrapIndex As Integer = 0
            Dim a_WritePass As Integer = 0
            Dim a_DataSetID As Integer = 0
            Dim a_HOW_RecCnt As Integer = 0
            Dim a_EOW_RecCnt As Integer = 0
            Dim a_HOW_FMCnt As Integer = 0
            Dim a_EOW_FMCnt As Integer = 0
            Dim a_FM_MAP As Integer = 0
            Dim a_CRC As Integer = 0
            Dim a_WrapsInDrive As Integer = 0
            Dim a_RecordCount As Integer = 0
            Dim a_FilemarkCount As Integer = 0

            Dim a_EvenDataSetID As Integer = 0
            Dim a_EvenRecordCount As Integer = 0
            Dim a_EvenFileMarkCount As Integer = 0
            Dim a_EvenCRC As Integer = 0
            Dim a_OddDataSetID As Integer = 0
            Dim a_OddRecordCount As Integer = 0
            Dim a_OddFileMarkCount As Integer = 0
            Dim a_OddCRC As Integer = 0
            Dim a_HdrLength As Integer = 0
            Dim a_OutputStr As String = ""

            With CType(g_CM(gtype.cartridge_mfg, createNew:=False), Cartridge_mfg)
                If .Format.Contains("LTO-1") Then
                    a_WrapsInDrive = 48
                    a_HdrLength = 16
                ElseIf .Format.Contains("LTO-2") Then
                    a_WrapsInDrive = 64
                    a_HdrLength = 16
                ElseIf .Format.Contains("LTO-3") Then
                    a_WrapsInDrive = 44
                    a_HdrLength = 16
                ElseIf .Format.Contains("LTO-4") Then
                    a_WrapsInDrive = 56
                    a_HdrLength = 16
                ElseIf .Format.Contains("LTO-5") Then
                    a_WrapsInDrive = 80
                    a_HdrLength = 16
                ElseIf .Format.Contains("LTO-6") Then
                    a_WrapsInDrive = 136
                    a_HdrLength = 48
                ElseIf .Format.Contains("LTO-7") Then
                    If .Format.Contains("Type M") Then
                        a_WrapsInDrive = 168
                    Else
                        a_WrapsInDrive = 112
                    End If
                    a_HdrLength = 48
                ElseIf .Format.Contains("LTO-8") Then
                    a_WrapsInDrive = 208
                    a_HdrLength = 48
                ElseIf .Format.Contains("LTO-9") Then
                    a_WrapsInDrive = 280
                    a_HdrLength = 48
                ElseIf .Format.Contains("J") Then
                    a_HdrLength = 16
                    a_WrapsInDrive = ((Buffer.Length - a_HdrLength) \ (24 * 8 + 4)) * 8
                    a_NWraps = a_WrapsInDrive
                End If

                If .Format.Contains("LTO-2") Then
                    For a_WrapIndex = 0 To a_WrapsInDrive - 1
                        a_WritePass = g_GetDWord(Buffer, a_HdrLength)
                        a_HdrLength += 4
                        a_DataSetID = g_GetDWord(Buffer, a_HdrLength)
                        a_HdrLength += 4
                        a_HOW_RecCnt = g_GetDWord(Buffer, a_HdrLength)
                        a_HdrLength += 4
                        a_EOW_RecCnt = g_GetDWord(Buffer, a_HdrLength)
                        a_HdrLength += 4
                        a_HOW_FMCnt = g_GetDWord(Buffer, a_HdrLength)
                        a_HdrLength += 4
                        a_EOW_FMCnt = g_GetDWord(Buffer, a_HdrLength)
                        a_HdrLength += 4
                        a_CRC = g_GetDWord(Buffer, a_HdrLength)
                        a_HdrLength += 4
                        a_OutputStr = $"{a_WritePass.ToString().PadRight(12) _
                            }{a_DataSetID.ToString().PadRight(12) _
                            }{a_HOW_RecCnt.ToString().PadRight(12) _
                            }{a_EOW_RecCnt.ToString().PadRight(12) _
                            }{a_HOW_FMCnt.ToString().PadRight(12) _
                            }{a_EOW_FMCnt.ToString().PadRight(12) _
                            }{a_CRC.ToString().PadRight(12) _
                            }"

                        With CType(g_CM(gtype.tape_directory), TapeDirectory).WrapEntry(a_WrapIndex)
                            .Content = a_OutputStr
                            .RawData = {a_DataSetID, a_HOW_RecCnt, a_EOW_RecCnt, a_HOW_FMCnt, a_EOW_FMCnt, a_CRC}
                            .RecCount = a_HOW_RecCnt + a_EOW_RecCnt
                            .FileMarkCount = a_HOW_FMCnt + a_EOW_FMCnt
                        End With
                    Next
                ElseIf .IsLTO3Plus Then
                    For a_WrapIndex = 0 To a_WrapsInDrive - 1
                        a_WritePass = g_GetDWord(Buffer, a_HdrLength)
                        a_HdrLength += 4
                        a_DataSetID = g_GetDWord(Buffer, a_HdrLength)
                        a_HdrLength += 4
                        a_HOW_RecCnt = g_GetDWord(Buffer, a_HdrLength)
                        a_HdrLength += 4
                        a_EOW_RecCnt = g_GetDWord(Buffer, a_HdrLength)
                        a_HdrLength += 4
                        a_HOW_FMCnt = g_GetDWord(Buffer, a_HdrLength)
                        a_HdrLength += 4
                        a_EOW_FMCnt = g_GetDWord(Buffer, a_HdrLength)
                        a_HdrLength += 4
                        a_FM_MAP = g_GetDWord(Buffer, a_HdrLength)
                        a_HdrLength += 4
                        a_CRC = g_GetDWord(Buffer, a_HdrLength)
                        a_HdrLength += 4
                        a_OutputStr = $"{a_WritePass.ToString().PadRight(12) _
                            }{a_DataSetID.ToString().PadRight(12) _
                            }{a_HOW_RecCnt.ToString().PadRight(12) _
                            }{a_EOW_RecCnt.ToString().PadRight(12) _
                            }{a_HOW_FMCnt.ToString().PadRight(12) _
                            }{a_EOW_FMCnt.ToString().PadRight(12) _
                            }{a_FM_MAP.ToString().PadRight(12) _
                            }{a_CRC.ToString().PadRight(12) _
                            }"
                        With CType(g_CM(gtype.tape_directory), TapeDirectory).WrapEntry(a_WrapIndex)
                            .Content = a_OutputStr
                            .RawData = {a_DataSetID, a_HOW_RecCnt, a_EOW_RecCnt, a_HOW_FMCnt, a_EOW_FMCnt, a_FM_MAP, a_CRC}
                            .RecCount = a_HOW_RecCnt + a_EOW_RecCnt
                            .FileMarkCount = a_HOW_FMCnt + a_EOW_FMCnt
                        End With
                    Next
                ElseIf .Format.Contains("LTO-1") Then
                    a_HdrLength = 16
                    a_WrapsInDrive = 48
                    For a_WrapIndex = 0 To a_WrapsInDrive - 1
                        a_EvenDataSetID = g_GetDWord(Buffer, a_HdrLength)
                        a_HdrLength += 4
                        a_EvenRecordCount = g_GetDWord(Buffer, a_HdrLength)
                        a_HdrLength += 4
                        a_EvenFileMarkCount = g_GetDWord(Buffer, a_HdrLength)
                        a_HdrLength += 4
                        a_EvenCRC = g_GetDWord(Buffer, a_HdrLength)
                        a_HdrLength += 4
                        a_OddDataSetID = g_GetDWord(Buffer, a_HdrLength)
                        a_HdrLength += 4
                        a_OddRecordCount = g_GetDWord(Buffer, a_HdrLength)
                        a_HdrLength += 4
                        a_OddFileMarkCount = g_GetDWord(Buffer, a_HdrLength)
                        a_HdrLength += 4
                        a_OddCRC = g_GetDWord(Buffer, a_HdrLength)
                        a_HdrLength += 4

                        a_OutputStr = $"{a_EvenDataSetID.ToString().PadRight(12) _
                            }{a_EvenRecordCount.ToString().PadRight(12) _
                            }{a_EvenFileMarkCount.ToString().PadRight(12) _
                            }{a_EvenCRC.ToString().PadRight(12) _
                            }{a_OddDataSetID.ToString().PadRight(12) _
                            }{a_OddRecordCount.ToString().PadRight(12) _
                            }{a_OddFileMarkCount.ToString().PadRight(12) _
                            }{a_OddCRC.ToString().PadRight(12) _
                            }"

                        With CType(g_CM(gtype.tape_directory), TapeDirectory).WrapEntry(a_WrapIndex)
                            .Content = a_OutputStr
                            .RawData = {a_EvenDataSetID, a_EvenRecordCount, a_EvenFileMarkCount, a_EvenCRC, a_OddDataSetID, a_OddRecordCount, a_OddFileMarkCount, a_OddCRC}
                            .RecCount = a_EvenRecordCount + a_OddRecordCount
                            .FileMarkCount = a_EvenFileMarkCount + a_OddFileMarkCount
                        End With
                    Next
                ElseIf .Format.Contains("J") Then
                    For a_WrapIndex = 0 To a_WrapsInDrive - 1
                        a_WritePass = g_GetDWord(Buffer, a_HdrLength)
                        a_HdrLength += 4
                        a_DataSetID = g_GetDWord(Buffer, a_HdrLength)
                        a_HdrLength += 4
                        'a_HOW_RecCnt = g_GetDWord(Buffer, a_HdrLength)
                        'a_HdrLength += 4
                        a_EOW_RecCnt = g_GetDWord(Buffer, a_HdrLength)
                        a_HdrLength += 4
                        'a_HOW_FMCnt = g_GetDWord(Buffer, a_HdrLength)
                        'a_HdrLength += 4
                        a_EOW_FMCnt = g_GetDWord(Buffer, a_HdrLength)
                        a_HdrLength += 4
                        a_FM_MAP = g_GetDWord(Buffer, a_HdrLength)
                        a_HdrLength += 8
                        If a_WrapIndex Mod 8 = 7 Then
                            a_CRC = g_GetDWord(Buffer, a_HdrLength)
                            a_HdrLength += 4
                        End If
                        a_OutputStr = $"{a_WritePass.ToString().PadRight(12) _
                            }{a_DataSetID.ToString().PadRight(12) _
                            }{a_HOW_RecCnt.ToString().PadRight(12) _
                            }{a_EOW_RecCnt.ToString().PadRight(12) _
                            }{a_HOW_FMCnt.ToString().PadRight(12) _
                            }{a_EOW_FMCnt.ToString().PadRight(12) _
                            }{a_FM_MAP.ToString().PadRight(12) _
                            }"
                        With CType(g_CM(gtype.tape_directory), TapeDirectory).WrapEntry(a_WrapIndex)
                            .Content = a_OutputStr
                            .RawData = {a_DataSetID, a_HOW_RecCnt, a_EOW_RecCnt, a_HOW_FMCnt, a_EOW_FMCnt, a_FM_MAP, a_CRC}
                            .RecCount = a_HOW_RecCnt + a_EOW_RecCnt
                            .FileMarkCount = a_HOW_FMCnt + a_EOW_FMCnt
                        End With
                    Next
                End If
            End With

        End Sub
        Public Function g_GetWord(buffer As Byte(), offset As Integer) As Integer
            Return CInt(buffer(offset)) << 8 Or buffer(offset + 1)
        End Function
        Public Function g_GetDWord(buffer As Byte(), offset As Integer) As Integer
            Dim result As Integer
            For i As Integer = 0 To 3
                result <<= 8
                result = result Or buffer(offset + i)
            Next
            Return result
        End Function
        Public Function g_GetInt64(buffer As Byte(), offset As Integer) As Long
            Dim result As Long
            For i As Integer = 0 To 7
                result <<= 8
                result = result Or buffer(offset + i)
            Next
            Return result
        End Function
        Public Function substr(buffer As Byte(), offset As Long, length As Long) As Byte()
            Dim result(CInt(length - 1)) As Byte
            Array.Copy(buffer, offset, result, 0, length)
            Return result
        End Function
        Public Function getstr(buffer As Byte(), offset As Long, length As Long) As String
            Dim result(CInt(length - 1)) As Byte
            Array.Copy(buffer, offset, result, 0, length)
            Return Encoding.ASCII.GetString(result)
        End Function
        Public Sub New()

        End Sub
        Public Sub New(TapeDrive As String, Optional ByVal BufferID As Byte = &H10)
            Select Case DriverTypeSetting
                Case DriverType.LTO
                    a_CMBuffer = ReadBuffer(TapeDrive, BufferID)
                    If a_CMBuffer.Length = 0 Then
                        'IBM LTO
                        a_CMBuffer = ReadBuffer(TapeDrive, &H5)
                    End If
                Case DriverType.IBM3592
                    '3592
                    a_CMBuffer = ReadBuffer(TapeDrive, &H20)
            End Select
            If a_CMBuffer.Length <> 0 Then
                Try
                    RunParse()
                Catch
                End Try
            End If
        End Sub
        Public Sub New(handle As IntPtr, Optional ByVal BufferID As Byte = &H10)
            a_CMBuffer = ReadBuffer(handle, BufferID)
            If a_CMBuffer.Length = 0 Then a_CMBuffer = ReadBuffer(handle, &H5)
            RunParse()
        End Sub
        Public Sub New(ByVal BufferData As Byte(), Optional ByRef errorMsg As Exception = Nothing)
            a_CMBuffer = BufferData
            If a_CMBuffer.Length <> 0 Then
                Try
                    RunParse()
                Catch ex As Exception
                    errorMsg = ex
                End Try
            End If
        End Sub
        Public Shared Function FromTapeDrive(TapeDrive As String) As CMParser
            Return New CMParser(TapeDrive)
        End Function
        Public Function GetSerializedText() As String
            Dim writer As New XmlSerializer(GetType(CMParser))
            Dim sb As New StringBuilder
            Dim t As New StringWriter(sb)
            writer.Serialize(t, Me)
            Return sb.ToString()
        End Function
        Public Function GetReport() As String
            Dim Output As New StringBuilder
            Output.Append("+=========================== APPLICATION INFO ============================+" & vbCrLf)
            Try
                Dim BC As String = ApplicationSpecificData.Barcode
                'BC = TapeUtils.ReadBarcode(ConfTapeDrive)
                'BC = TapeUtils.ReadBarcode(ConfTapeDrive)
                Output.Append(("| Barcode: ".PadRight(28) & BC).PadRight(74) & "|" & vbCrLf)
            Catch ex As Exception
                Output.Append(("| Barcode: ".PadRight(28) & "Not available").PadRight(74) & "|" & vbCrLf)
            End Try
            Try
                Dim AppInfo As String = $"{ApplicationSpecificData.Application_vendor} {ApplicationSpecificData.Application_name} {ApplicationSpecificData.Application_version}" ' TapeUtils.ReadAppInfo(ConfTapeDrive)
                Output.Append(("| Application: ".PadRight(28) & AppInfo).PadRight(74) & "|" & vbCrLf)
            Catch ex As Exception
                Output.Append(("| Application: ".PadRight(28) & "Not available").PadRight(74) & "|" & vbCrLf)
            End Try
            Output.Append("+============================= MEDIUM USAGE ==============================+" & vbCrLf)
            If CartridgeMfgData.CartridgeTypeAbbr = "CU" Then
                Try
                    Dim LoadCount As Int64 = StatusData.ThreadCount ' TapeUtils.MAMAttribute.FromTapeDrive(ConfTapeDrive, 0, 3).AsNumeric
                    Output.Append(("| Cleans performed: ".PadRight(28) & LoadCount).PadRight(74) & "|" & vbCrLf)
                Catch ex As Exception
                    Output.Append(("| Cleans performed: ".PadRight(28) & "Not available").PadRight(74) & "|" & vbCrLf)
                End Try
                Try
                    Dim CleanRemain As Int64 = a_CleansRemaining ' TapeUtils.MAMAttribute.FromTapeDrive(ConfTapeDrive, 0, 3).AsNumeric
                    Output.Append(("| Cleans remain: ".PadRight(28) & CleanRemain).PadRight(74) & "|" & vbCrLf)
                Catch ex As Exception
                    Output.Append(("| Cleans remain: ".PadRight(28) & "Not available").PadRight(74) & "|" & vbCrLf)
                End Try
                Try
                    Dim TapeLen As Int64 = CartridgeMfgData.TapeLength ' TapeUtils.MAMAttribute.FromTapeDrive(ConfTapeDrive, 0, 3).AsNumeric
                    Dim LastLoc As Int64 = StatusData.LastLocation ' TapeUtils.MAMAttribute.FromTapeDrive(ConfTapeDrive, 0, 3).AsNumeric
                    Output.Append(("| Used length: ".PadRight(28) & $"{(LastLoc / 4).ToString("f2")} m / {((TapeLen / 4) - 11).ToString("f2")} m").PadRight(74) & "|" & vbCrLf)
                Catch ex As Exception
                    Output.Append(("| Used length: ".PadRight(28) & "Not available").PadRight(74) & "|" & vbCrLf)
                End Try

            Else
                Try
                    Dim LoadCount As Int64 = StatusData.ThreadCount ' TapeUtils.MAMAttribute.FromTapeDrive(ConfTapeDrive, 0, 3).AsNumeric
                    Output.Append(("| Load count: ".PadRight(28) & LoadCount).PadRight(74) & "|" & vbCrLf)
                Catch ex As Exception
                    Output.Append(("| Load count: ".PadRight(28) & "Not available").PadRight(74) & "|" & vbCrLf)
                End Try
                Try
                    Dim TotalWriteKBytes As Int64 = CartridgeMfgData.KB_PER_DATASET
                    TotalWriteKBytes *= UsageData(0).LifeSetsWritten
                    If CartridgeMfgData.KB_PER_DATASET > 0 Then
                        Output.Append(("| Total write: ".PadRight(28) & ReduceDataUnit(TotalWriteKBytes)).PadRight(74) & "|" & vbCrLf)
                    Else
                        Output.Append(("| Total write: ".PadRight(28) & $"{UsageData(0).LifeSetsWritten} Sets").PadRight(74) & "|" & vbCrLf)
                    End If
                Catch ex As Exception
                    Output.Append(("| Total write: ".PadRight(28) & "Not available").PadRight(74) & "|" & vbCrLf)
                End Try
                Try
                    Dim TotalReadKBytes As Int64 = CartridgeMfgData.KB_PER_DATASET
                    TotalReadKBytes *= UsageData(0).LifeSetsRead
                    If CartridgeMfgData.KB_PER_DATASET > 0 Then
                        Output.Append(("| Total read: ".PadRight(28) & ReduceDataUnit(TotalReadKBytes)).PadRight(74) & "|" & vbCrLf)
                    Else
                        Output.Append(("| Total read: ".PadRight(28) & $"{UsageData(0).LifeSetsRead} Sets").PadRight(74) & "|" & vbCrLf)
                    End If
                Catch ex As Exception
                    Output.Append(("| Total read: ".PadRight(28) & "Not available").PadRight(74) & "|" & vbCrLf)
                End Try
                Try
                    Dim fve As Double = (UsageData(0).LifeSetsRead + UsageData(0).LifeSetsWritten) / (a_SetsPerWrap * a_NWraps)
                    If a_SetsPerWrap > 0 Then
                        Output.Append(("| Full volume equivalents: ".PadRight(28) & fve.ToString("f2") & $" FVE ({(fve / CartridgeMfgData.TAPE_LIFE_IN_VOLS * 100).ToString("f2")}%)").PadRight(74) & "|" & vbCrLf)
                    Else
                        Output.Append(("| Full volume equivalents: ".PadRight(28) & $"Unknown").PadRight(74) & "|" & vbCrLf)
                    End If
                Catch ex As Exception
                    Output.Append(("| Full volume equivalents: ".PadRight(28) & "Not available").PadRight(74) & "|" & vbCrLf)
                End Try
                Try
                    Output.Append(("| Write retries: ".PadRight(28) & UsageData(0).LifeWriteRetries).PadRight(74) & "|" & vbCrLf)
                Catch ex As Exception
                    Output.Append(("| Write retries: ".PadRight(28) & "Not available").PadRight(74) & "|" & vbCrLf)
                End Try
                Try
                    Output.Append(("| Read retries: ".PadRight(28) & UsageData(0).LifeReadRetries).PadRight(74) & "|" & vbCrLf)
                Catch ex As Exception
                    Output.Append(("| Read retries: ".PadRight(28) & "Not available").PadRight(74) & "|" & vbCrLf)
                End Try
                Try
                    Output.Append(("| Unrecovered writes: ".PadRight(28) & UsageData(0).LifeUnRecovWrites).PadRight(74) & "|" & vbCrLf)
                Catch ex As Exception
                    Output.Append(("| Unrecovered writes: ".PadRight(28) & "Not available").PadRight(74) & "|" & vbCrLf)
                End Try
                Try
                    Output.Append(("| Unrecovered reads: ".PadRight(28) & UsageData(0).LifeUnRecovReads).PadRight(74) & "|" & vbCrLf)
                Catch ex As Exception
                    Output.Append(("| Unrecovered reads: ".PadRight(28) & "Not available").PadRight(74) & "|" & vbCrLf)
                End Try
                Try
                    Output.Append(("| Suspended writes: ".PadRight(28) & UsageData(0).LifeSuspendedWrites).PadRight(74) & "|" & vbCrLf)
                Catch ex As Exception
                    Output.Append(("| Suspended writes: ".PadRight(28) & "Not available").PadRight(74) & "|" & vbCrLf)
                End Try
                Try
                    Output.Append(("| Suspended append writes: ".PadRight(28) & UsageData(0).LifeSuspAppendWrites).PadRight(74) & "|" & vbCrLf)
                Catch ex As Exception
                    Output.Append(("| Suspended append writes: ".PadRight(28) & "Not available").PadRight(74) & "|" & vbCrLf)
                End Try
                Try
                    Output.Append(("| Fatal suspended writes: ".PadRight(28) & UsageData(0).LifeFatalSuspWrites).PadRight(74) & "|" & vbCrLf)
                Catch ex As Exception
                    Output.Append(("| Fatal suspended writes: ".PadRight(28) & "Not available").PadRight(74) & "|" & vbCrLf)
                End Try
            End If

            'Try
            '    Dim TapeMetresPulled As Int64 = Me.UsageData(0).LifeTapeMetresPulled
            '    If TapeMetresPulled <= 0 Then Throw New Exception("TMP Not available")
            '    TextBox8.Append(" Tape pulled: ".PadRight(28) & TapeMetresPulled & " m" & vbCrLf)
            'Catch ex As Exception
            '    TextBox8.Append(" Tape pulled: ".PadRight(28) & "Not available" & vbCrLf)
            'End Try
            Output.Append("+============================ MEDIUM IDENTITY ============================+" & vbCrLf)
            Try
                Dim Medium_Format As String = $"{CartridgeMfgData.Format} (MC 0x{CartridgeMfgData.MediaCode.ToString("X4")} DC 0x{CartridgeMfgData.DENSITY_CODE.ToString("X2")})"
                Output.Append(("| Format: ".PadRight(28) & Medium_Format).PadRight(74) & "|" & vbCrLf)
            Catch ex As Exception
                Output.Append(("| Format: ".PadRight(28) & "Not available").PadRight(74) & "|" & vbCrLf)
            End Try
            Try
                Dim Medium_SN As String = CartridgeMfgData.CartridgeSN ' TapeUtils.MAMAttribute.FromTapeDrive(ConfTapeDrive, 4, 1).AsString
                Output.Append(("| Serial number: ".PadRight(28) & Medium_SN).PadRight(74) & "|" & vbCrLf)
            Catch ex As Exception
                Output.Append(("| Serial number: ".PadRight(28) & "Not available").PadRight(74) & "|" & vbCrLf)
            End Try
            Try
                Dim Tape_Manufacturer As String = CartridgeMfgData.TapeVendor 'TapeUtils.MAMAttribute.FromTapeDrive(ConfTapeDrive, 4, 0).AsString
                Output.Append(("| Tape Vendor: ".PadRight(28) & Tape_Manufacturer).PadRight(74) & "|" & vbCrLf)
            Catch ex As Exception
                Output.Append(("| Tape Vendor: ".PadRight(28) & "Not available").PadRight(74) & "|" & vbCrLf)
            End Try
            Try
                Dim Tape_Man_Date As String = CartridgeMfgData.MfgDate 'TapeUtils.MAMAttribute.FromTapeDrive(ConfTapeDrive, 4, 6).AsString
                Output.Append(("| Tape mfg date: ".PadRight(28) & Tape_Man_Date).PadRight(74) & "|" & vbCrLf)
            Catch ex As Exception
                Output.Append(("| Tape mfg date: ".PadRight(28) & "Not available").PadRight(74) & "|" & vbCrLf)
            End Try
            Try
                Dim Medium_Manufacturer As String = MediaMfgData.MediaVendor 'TapeUtils.MAMAttribute.FromTapeDrive(ConfTapeDrive, 4, 0).AsString
                Output.Append(("| Media Vendor: ".PadRight(28) & Medium_Manufacturer).PadRight(74) & "|" & vbCrLf)
            Catch ex As Exception
                Output.Append(("| Media Vendor: ".PadRight(28) & "Not available").PadRight(74) & "|" & vbCrLf)
            End Try
            Try
                Dim Medium_Man_Date As String = MediaMfgData.MediaMfgDate 'TapeUtils.MAMAttribute.FromTapeDrive(ConfTapeDrive, 4, 6).AsString
                Output.Append(("| Media mfg date: ".PadRight(28) & Medium_Man_Date).PadRight(74) & "|" & vbCrLf)
            Catch ex As Exception
                Output.Append(("| Media mfg date: ".PadRight(28) & "Not available").PadRight(74) & "|" & vbCrLf)
            End Try
            Try
                Dim CMData As Byte() = a_CMBuffer
                Dim Medium_ParticleType As String = CartridgeMfgData.ParticleType.ToString()
                If CartridgeMfgData.CartridgeTypeAbbr = "CU" Then Medium_ParticleType = "Universal Clean Cartridge"
                Output.Append(("| Particle type: ".PadRight(28) & Medium_ParticleType).PadRight(74) & "|" & vbCrLf)
                Output.Append("+============================= DATA ON TAPE ==============================+" & vbCrLf)
                Dim wares As New StringBuilder
                Dim nLossDS As Long = 0
                Dim DataSize As New List(Of Long)
                Try
                    If CartridgeMfgData.CartridgeTypeAbbr = "CU" Then Exit Try
                    wares.AppendLine("+============================= WRAP ANALYSIS =============================+")
                    wares.AppendLine("| Wrap | Start Block |  End Block  | Filemark |      Set      | Capacity  |")
                    wares.AppendLine("|------+-------------+-------------+----------+---------------+-----------|")
                    Dim StartBlock As Integer = 0
                    Dim CurrSize As Long = 0
                    Dim gw As Boolean = False
                    For wn As Integer = 0 To a_NWraps - 1
                        Dim StartBlockStr As String = StartBlock.ToString()
                        If TapeDirectoryData.CapacityLoss(wn) = -1 Or TapeDirectoryData.CapacityLoss(wn) = -3 Then StartBlockStr = ""
                        Dim EndBlock As Integer = StartBlock + TapeDirectoryData.WrapEntryInfo(wn).RecCount + TapeDirectoryData.WrapEntryInfo(wn).FileMarkCount - 1
                        If TapeDirectoryData.CapacityLoss(wn) = -2 Then EndBlock += 1
                        wares.Append($"| {wn.ToString().PadLeft(3)}  |")
                        wares.Append($" {StartBlockStr.PadLeft(10)}  |")
                        If StartBlockStr <> "" Then
                            wares.Append($"  {EndBlock.ToString.PadLeft(10)} |")
                        Else
                            wares.Append($"  {"".PadLeft(10)} |")
                        End If
                        wares.Append($"  {TapeDirectoryData.WrapEntryInfo(wn).FileMarkCount.ToString().PadLeft(5)}   |")
                        wares.Append($" {TapeDirectoryData.DatasetsOnWrapData(wn).Data.ToString().PadLeft(5)} / {a_SetsPerWrap.ToString().PadRight(5)} |")
                        StartBlock += TapeDirectoryData.WrapEntryInfo(wn).RecCount + TapeDirectoryData.WrapEntryInfo(wn).FileMarkCount
                        If TapeDirectoryData.CapacityLoss(wn) >= 0 Then
                            nLossDS += Math.Max(0, a_SetsPerWrap - TapeDirectoryData.DatasetsOnWrapData(wn).Data)
                            CurrSize += TapeDirectoryData.DatasetsOnWrapData(wn).Data
                            'wares.Append($" { (100 - Me.TapeDirectoryData.CapacityLoss(wn)).ToString("f2").PadLeft(7)}%  |")
                            wares.Append($" { (TapeDirectoryData.DatasetsOnWrapData(wn).Data / a_SetsPerWrap * 100).ToString("f2").PadLeft(7)}%  |")
                        ElseIf TapeDirectoryData.CapacityLoss(wn) = -1 Then
                            StartBlock = 0
                            wares.Append($"           |")
                        ElseIf TapeDirectoryData.CapacityLoss(wn) = -2 Then
                            CurrSize += TapeDirectoryData.DatasetsOnWrapData(wn).Data
                            wares.Append($"  >>EOD<<  |")
                        ElseIf TapeDirectoryData.CapacityLoss(wn) = -3 Then
                            StartBlock = 0
                            If gw Then
                                DataSize.Add(CurrSize)
                                CurrSize = 0
                                gw = False
                            Else
                                gw = True
                            End If
                            wares.Append($"  *GUARD*  |")
                        End If
                        wares.AppendLine()
                    Next
                    DataSize.Add(CurrSize)
                Catch ex As Exception
                    wares.Append("| CM data parsing failed.".PadRight(74) & "|" & vbCrLf)
                End Try
                Try
                    Dim DataWrapList As New List(Of Integer)
                    Dim DataWrapNum As Integer = 0
                    For Each l As Double In TapeDirectoryData.CapacityLoss
                        If l = -3 Then
                            If DataWrapNum > 0 Then
                                DataWrapList.Add(DataWrapNum)
                                DataWrapNum = 0
                            End If
                        Else
                            DataWrapNum += 1
                        End If
                    Next
                    If DataWrapNum > 0 Then DataWrapList.Add(DataWrapNum)
                    Output.Append(("| Total partitions: ".PadRight(28) & DataWrapList.Count).PadRight(74) & "|" & vbCrLf)
                    For i As Integer = 0 To DataWrapList.Count - 1
                        Dim nWrap As Long = DataWrapList(i)
                        Dim len As Long = nWrap * CartridgeMfgData.KB_PER_WRAP

                        Dim WrittenSize As String = ""
                        If DataSize.Count = DataWrapList.Count Then
                            WrittenSize = $"{IOManager.FormatSize(DataSize(i) * CartridgeMfgData.KB_PER_DATASET * 1000, True)} / "
                        End If
                        Output.Append(($"| Partition {i} size: ".PadRight(28) & (WrittenSize & ReduceDataUnit(len)).PadRight(24) & $"[{nWrap.ToString().PadLeft(3)} wraps]").PadRight(74) & "|" & vbCrLf)
                    Next
                Catch ex As Exception
                    Output.Append("Partition page not available" & vbCrLf)
                End Try
                Output.Append(("| Estimated capacity loss: ".PadRight(28) & IOManager.FormatSize(nLossDS * CartridgeMfgData.KB_PER_DATASET * 1000)).PadRight(74) & "|" & vbCrLf)
                Output.Append(wares.ToString())

                Output.Append("+============================== CM RAW DATA ==============================+" & vbCrLf)
                Output.Append(("| Length: " & CMData.Length).PadRight(74) & "|" & vbCrLf)
                Output.Append(RichByte2Hex(CMData, True))
                Output.Append("+=========================================================================+")
                Output.Append(vbCrLf)
            Catch ex As Exception
                Output.Append("| CM data parsing failed.".PadRight(74) & "|" & vbCrLf & ex.ToString & vbCrLf)
            End Try
            Return Output.ToString().Replace(vbNullChar, " ")
        End Function
    End Class



    <TypeConverter(GetType(ExpandableObjectConverter))>
    <Serializable> Public Class PageData
        <TypeConverter(GetType(ExpandableObjectConverter))>
        <Serializable> Public Class DataItem
            Public Property Name As String
            Public Property StartByte As Integer
            Public Property BitOffset As Byte
            Public Property TotalBits As Integer
            Public Property DynamicParamCodeStartByte As Integer
            Public Property DynamicParamCodeBitOffset As Byte
            Public Property DynamicParamCodeTotalBits As Byte
            Public Property DynamicParamLenStartByte As Integer
            Public Property DynamicParamLenBitOffset As Byte
            Public Property DynamicParamLenTotalBits As Byte
            Public Property DynamicParamDataStartByte As Integer
            <TypeConverter(GetType(ExpandableObjectConverter))>
            <Serializable> Public Class DynamicParamPage
                Public ReadOnly Property Name As String
                    Get
                        Dim value As String = ""
                        Parent.EnumTranslator.TryGetValue(ParamCode, value)
                        If value = "" Then value = $"0x{Hex(ParamCode).Replace("-", "")}"
                        Return value
                    End Get
                End Property
                Public Property ParamCode As Integer
                Public Property Parent As DataItem
                Public Property Type As DataType
                Public Property EnumTranslator As SerializableDictionary(Of Long, String)
                <XmlIgnore>
                Public Property RawData As Byte()
                <XmlIgnore>
                Public ReadOnly Property LastByte As Byte
                    Get
                        If RawData Is Nothing OrElse RawData.Length = 0 Then Return Nothing
                        Return RawData.Last
                    End Get
                End Property

                <XmlIgnore>
                Public ReadOnly Property GetString As String
                    Get
                        If Parent Is Nothing Then Return ""
                        Dim rawdata As Byte() = Me.RawData()
                        If rawdata Is Nothing OrElse rawdata.Length = 0 Then Return ""
                        Select Case Type
                            Case DataType.Byte
                                Dim result As Byte
                                For i As Integer = 0 To rawdata.Length - 1
                                    result = result << 8
                                    result = result Or rawdata(i)
                                Next
                                Return result.ToString
                            Case DataType.Int16
                                Dim result As Int16
                                For i As Integer = 0 To rawdata.Length - 1
                                    result = result << 8
                                    result = result Or rawdata(i)
                                Next
                                Return result.ToString
                            Case DataType.Int32
                                Dim result As Integer
                                For i As Integer = 0 To rawdata.Length - 1
                                    result = result << 8
                                    result = result Or rawdata(i)
                                Next
                                Return result.ToString
                            Case DataType.Int64
                                Dim result As Long
                                For i As Integer = 0 To rawdata.Length - 1
                                    result = result << 8
                                    result = result Or rawdata(i)
                                Next
                                Return result.ToString
                            Case DataType.UInt64
                                Dim result As ULong
                                For i As Integer = 0 To rawdata.Length - 1
                                    result = result << 8
                                    result = result Or rawdata(i)
                                Next
                                Return result.ToString
                            Case DataType.Boolean
                                If rawdata.Last = 0 Then Return "False" Else Return "True"
                            Case DataType.Text
                                Return ByteArrayToString(rawdata, True)
                            Case DataType.Enum
                                Dim key As Long
                                For i As Integer = 0 To rawdata.Length - 1
                                    key = key << 8
                                    key = key Or rawdata(i)
                                Next
                                If EnumTranslator IsNot Nothing Then
                                    Dim result As String = ""
                                    If EnumTranslator.TryGetValue(key, result) Then
                                        Return $"0x{Hex(key)} {result}"
                                    Else
                                        Return key.ToString()
                                    End If
                                End If
                                Return key.ToString()
                            Case DataType.Binary
                                Return $"0x{BitConverter.ToString(rawdata).Replace("-", "").ToUpper}"
                            Case DataType.PageData
                                Dim pagedata As PageData = Nothing
                                If Parent.PageDataTemplate.TryGetValue(ParamCode, pagedata) Then
                                    pagedata.RawData = rawdata
                                    Return pagedata.GetSummary(False)
                                Else
                                    Return ""
                                End If
                            Case Else
                                If rawdata.Length > 0 Then Return Byte2Hex(rawdata, True) Else Return ""
                        End Select
                    End Get
                End Property
                <XmlIgnore>
                Public ReadOnly Property GetLong As Long
                    Get
                        Dim result As Long
                        For i As Integer = 0 To RawData.Length - 1
                            result <<= 8
                            result = result Or RawData(i)
                        Next
                        Return result
                    End Get
                End Property
                <XmlIgnore>
                Public ReadOnly Property GetPage As PageData
                    Get
                        If Type = DataType.PageData Then
                            Dim pagedata As PageData = Nothing
                            If Parent.PageDataTemplate.TryGetValue(ParamCode, pagedata) Then
                                pagedata.RawData = RawData
                                Return pagedata
                            End If
                        End If
                        Return Nothing
                    End Get
                End Property



                Public Shared Function [Next](ByVal PageData As DataItem, ByVal StartByte As Integer) As DynamicParamPage
                    Dim rawLen(CInt(Math.Ceiling(PageData.DynamicParamLenTotalBits / 8) - 1)) As Byte
                    For i As Integer = 0 To PageData.DynamicParamLenTotalBits - 1
                        Dim resultByteNum As Integer = rawLen.Length - 1 - i \ 8
                        Dim resultBitNum As Byte = CByte(i Mod 8) '76543210
                        Dim sourceByteNum As Integer = StartByte + PageData.DynamicParamLenStartByte + (PageData.DynamicParamLenBitOffset + PageData.DynamicParamLenTotalBits - i - 1) \ 8
                        Dim sourceBitNum As Integer = 7 - (PageData.DynamicParamLenBitOffset + PageData.DynamicParamLenTotalBits - i - 1) Mod 8
                        rawLen(resultByteNum) = CByte(rawLen(resultByteNum) Or ((PageData.RawData(sourceByteNum) And 1 << sourceBitNum) >> sourceBitNum) << resultBitNum)
                    Next
                    Dim LenValue As Integer
                    For i As Integer = 0 To rawLen.Length - 1
                        LenValue <<= 8
                        LenValue = LenValue Or rawLen(i)
                    Next
                    Dim rawPCode(CInt(Math.Ceiling(PageData.DynamicParamCodeTotalBits / 8) - 1)) As Byte
                    For i As Integer = 0 To PageData.DynamicParamCodeTotalBits - 1
                        Dim resultByteNum As Integer = rawPCode.Length - 1 - i \ 8
                        Dim resultBitNum As Byte = CByte(i Mod 8) '76543210
                        Dim sourceByteNum As Integer = StartByte + PageData.DynamicParamCodeStartByte + (PageData.DynamicParamCodeBitOffset + PageData.DynamicParamCodeTotalBits - i - 1) \ 8
                        Dim sourceBitNum As Integer = 7 - (PageData.DynamicParamCodeBitOffset + PageData.DynamicParamCodeTotalBits - i - 1) Mod 8
                        rawPCode(resultByteNum) = CByte(rawPCode(resultByteNum) Or ((PageData.RawData(sourceByteNum) And 1 << sourceBitNum) >> sourceBitNum) << resultBitNum)
                    Next
                    Dim PCode As Integer
                    For i As Integer = 0 To rawPCode.Length - 1
                        PCode <<= 8
                        PCode = PCode Or rawPCode(i)
                    Next
                    Dim resultData As Byte() = {}
                    LenValue = Math.Min(LenValue, PageData.RawData.Length - StartByte - PageData.DynamicParamDataStartByte)
                    If LenValue > 0 Then
                        ReDim resultData(LenValue - 1)
                        Array.Copy(PageData.RawData, StartByte + PageData.DynamicParamDataStartByte, resultData, 0, LenValue)
                    End If
                    Dim dataType As DataType
                    Try
                        PageData.DynamicParamType.TryGetValue(PCode, dataType)
                    Catch ex As Exception
                        Return Nothing
                    End Try
                    Return New DynamicParamPage With {.Parent = PageData, .ParamCode = PCode, .RawData = resultData, .Type = dataType}
                End Function
            End Class
            Public Enum DataType
                [Byte]
                Int16
                Int32
                Int64
                UInt64
                [Boolean]
                Text
                [Enum]
                Binary
                DynamicPage
                PageData
                RawData
            End Enum
            Public Property Type As DataType
            Public Property EnumTranslator As SerializableDictionary(Of Long, String)
            Public Property DynamicParamType As SerializableDictionary(Of Long, DataType)
            Public Property PageDataTemplate As SerializableDictionary(Of Long, PageData)
            Public ReadOnly Property RawData As Byte()
                Get
                    If Parent Is Nothing Then Return Nothing
                    If Type = DataType.DynamicPage Then
                        Return Parent.RawData.Skip(StartByte).ToArray()
                    End If
                    If (StartByte + (BitOffset + TotalBits - 1) \ 8) >= Parent.RawData.Length Then Return Nothing
                    Dim result(CInt(Math.Ceiling(TotalBits / 8) - 1)) As Byte
                    For i As Integer = 0 To TotalBits - 1
                        Dim resultByteNum As Integer = result.Length - 1 - i \ 8
                        Dim resultBitNum As Byte = CByte(i Mod 8) '76543210
                        Dim sourceByteNum As Integer = StartByte + (BitOffset + TotalBits - i - 1) \ 8
                        If sourceByteNum >= Parent.RawData.Length Then Return result
                        Dim sourceBitNum As Integer = 7 - (BitOffset + TotalBits - i - 1) Mod 8
                        result(resultByteNum) = CByte(result(resultByteNum) Or ((Parent.RawData(sourceByteNum) And 1 << sourceBitNum) >> sourceBitNum) << resultBitNum)
                    Next
                    Return result
                End Get
            End Property
            Public ReadOnly Property GetString As String
                Get
                    If Parent Is Nothing Then Return ""
                    Dim rawdata As Byte() = Me.RawData()
                    If rawdata Is Nothing OrElse rawdata.Length = 0 Then Return ""
                    Select Case Type
                        Case DataType.Byte
                            Dim result As Byte
                            For i As Integer = 0 To rawdata.Length - 1
                                result = result << 8
                                result = result Or rawdata(i)
                            Next
                            Return result.ToString
                        Case DataType.Int16
                            Dim result As Int16
                            For i As Integer = 0 To rawdata.Length - 1
                                result = result << 8
                                result = result Or rawdata(i)
                            Next
                            Return result.ToString
                        Case DataType.Int32
                            Dim result As Integer
                            For i As Integer = 0 To rawdata.Length - 1
                                result = result << 8
                                result = result Or rawdata(i)
                            Next
                            Return result.ToString
                        Case DataType.Int64
                            Dim result As Long
                            For i As Integer = 0 To rawdata.Length - 1
                                result = result << 8
                                result = result Or rawdata(i)
                            Next
                            Return result.ToString
                        Case DataType.UInt64
                            Dim result As ULong
                            For i As Integer = 0 To rawdata.Length - 1
                                result = result << 8
                                result = result Or rawdata(i)
                            Next
                            Return result.ToString
                        Case DataType.Boolean
                            If rawdata.Last = 0 Then Return "False" Else Return "True"
                        Case DataType.Text
                            Return ByteArrayToString(rawdata, True)
                        Case DataType.Enum
                            Dim key As Long
                            For i As Integer = 0 To rawdata.Length - 1
                                key = key << 8
                                key = key Or rawdata(i)
                            Next
                            If EnumTranslator IsNot Nothing Then
                                Dim result As String = ""
                                If EnumTranslator.TryGetValue(key, result) Then
                                    Return $"0x{Hex(key)} {result}"
                                Else
                                    Return key.ToString()
                                End If
                            End If
                            Return key.ToString()
                        Case DataType.DynamicPage
                            Dim i As Integer = 0
                            Dim sb As New StringBuilder
                            sb.Append($"↓")
                            While i < rawdata.Length - 1
                                Dim nextPage As DynamicParamPage = DynamicParamPage.Next(Me, i)
                                sb.Append($"{vbCrLf} * {nextPage.Name}")
                                Dim nPStr As String = nextPage.GetString()
                                If nPStr.Length > 0 Then
                                    For Each t As String In nPStr.Split({vbCrLf}, StringSplitOptions.RemoveEmptyEntries)
                                        sb.Append($"{vbCrLf}       {t}")
                                    Next
                                End If
                                i += nextPage.RawData.Length + DynamicParamDataStartByte
                            End While

                            Return sb.ToString()
                        Case DataType.Binary
                            Return $"0x{BitConverter.ToString(rawdata).Replace("-", "").ToUpper}"
                        Case Else
                            Return Byte2Hex(rawdata, True)
                    End Select
                End Get
            End Property
            <XmlIgnore> Public Property Parent As PageData
        End Class
        Public Property Name As String
        Public Property PageCode As Integer
        <TypeConverter(GetType(ListTypeDescriptor(Of List(Of DataItem), DataItem)))>
        Public Property Items As New List(Of DataItem)
        <XmlIgnore> Public Property RawData As Byte()
            Get
                Return _RawData
            End Get
            Set(value As Byte())
                _RawData = value
                _DynamicParamPages = Nothing
            End Set
        End Property
        <XmlIgnore> Private _RawData As Byte()
        Public Function GetSummary(Optional ByVal ShowTitle As Boolean = True) As String
            Dim sb As New StringBuilder
            If ShowTitle Then sb.AppendLine($"{Name}".PadLeft(Math.Max(0, 32 + Name.Length \ 2), "="c).PadRight(64, "="c))
            For Each it As DataItem In Items
                Dim result As String = it.GetString()
                If result IsNot Nothing AndAlso result.Length > 0 Then sb.AppendLine($"{it.Name} = {result}")
            Next
            If ShowTitle Then sb.AppendLine("".PadRight(64, "="c))
            Return sb.ToString()
        End Function

        <XmlIgnore>
        <TypeConverter(GetType(ListTypeDescriptor(Of List(Of DataItem.DynamicParamPage), DataItem.DynamicParamPage)))>
        Public Property DynamicParamPages As List(Of DataItem.DynamicParamPage)
            Get
                If _DynamicParamPages Is Nothing Then
                    _DynamicParamPages = New List(Of DataItem.DynamicParamPage)
                    For Each it As DataItem In Items
                        If it.EnumTranslator Is Nothing Then Continue For
                        Dim i As Integer = 0
                        While i < it.RawData.Length - 1
                            Dim nextPage As DataItem.DynamicParamPage = DataItem.DynamicParamPage.Next(it, i)
                            If nextPage Is Nothing Then Continue While
                            _DynamicParamPages.Add(nextPage)
                            i += nextPage.RawData.Length + it.DynamicParamDataStartByte
                        End While
                    Next
                End If
                Return _DynamicParamPages
            End Get
            Set(value As List(Of DataItem.DynamicParamPage))
                _DynamicParamPages = value
            End Set
        End Property
        <XmlIgnore> Private _DynamicParamPages As List(Of DataItem.DynamicParamPage)
        Public Function TryGetPage(ParamCode As Integer) As DataItem.DynamicParamPage
            Try
                For Each p As DataItem.DynamicParamPage In DynamicParamPages
                    If p.ParamCode = ParamCode Then Return p
                Next
            Catch ex As Exception
            End Try
            Return Nothing
        End Function
        Public Function TryGetPage(ParamName As String, Optional ByVal IgnoreCase As Boolean = True) As DataItem.DynamicParamPage
            Try
                For Each p As DataItem.DynamicParamPage In DynamicParamPages
                    If IgnoreCase Then
                        If p.Name.ToLower = ParamName.ToLower Then Return p
                    Else
                        If p.Name = ParamName Then Return p
                    End If
                Next
            Catch ex As Exception
            End Try
            Return Nothing
        End Function
        Public Function GetSerializedText(Optional ByVal ReduceSize As Boolean = True) As String
            Dim writer As New XmlSerializer(GetType(PageData))
            Dim sb As New StringBuilder()
            Dim t As TextWriter = New StringWriter(sb)
            writer.Serialize(t, Me)
            t.Close()
            Return sb.ToString
        End Function
        Public Shared Function FromXML(s As String) As PageData
            Dim reader As New XmlSerializer(GetType(PageData))
            Dim t As TextReader = New StringReader(s)
            Dim result As PageData = CType(reader.Deserialize(t), PageData)
            If result.Items IsNot Nothing Then
                Dim RemainingDataItem As New List(Of DataItem)
                RemainingDataItem = result.Items
                For Each it As DataItem In RemainingDataItem
                    it.Parent = result
                Next
                While RemainingDataItem.Count > 0
                    Dim newDI As New List(Of DataItem)
                    For Each it As DataItem In RemainingDataItem
                        If it.PageDataTemplate IsNot Nothing Then
                            For Each t2 As PageData In it.PageDataTemplate.Values
                                For Each i2 As DataItem In t2.Items
                                    i2.Parent = t2
                                    If i2.PageDataTemplate IsNot Nothing Then
                                        newDI.Add(i2)
                                    End If
                                Next
                            Next
                        End If
                    Next
                    RemainingDataItem = newDI
                End While

            End If
            Return result
        End Function
        Public Enum DefaultPages
            HPLTO6_SupportedLogPagesPage = &H0
            HPLTO6_WriteErrorCountersLogPage = &H2
            HPLTO6_ReadErrorCountersLogPage = &H3
            HPLTO6_SequentialAccessDeviceLogPage = &HC
            HPLTO6_TemperatureLogPage = &HD
            HPLTO6_DataTransferDeviceStatusLogPage = &H11
            HPLTO6_TapeAlertResponseLogPage = &H12
            HPLTO6_RequestedRecoveryLogPage = &H13
            HPLTO6_DeviceStatisticsLogPage = &H14
            HPLTO6_ServiceBuffersInformationLogPage = &H15
            HPLTO6_TapeDiagnosticLogPage = &H16
            HPLTO6_VolumeStatisticsLogPage = &H17
            HPLTO6_ProtocolSpecificPortLogPage = &H18
            HPLTO6_DataCompressionLogPage = &H1B
            HPLTO6_TapeAlertLogPage = &H2E
            HPLTO6_TapeUsageLogPage = &H30
            HPLTO6_TapeCapacityLogPage = &H31
            HPLTO6_DataCompressionHPLogPage = &H32
            HPLTO6_DeviceWellnessLogPage = &H33
            HPLTO6_PerformanceDataLogPage = &H34
            HPLTO6_DTDeviceErrorLogPage = &H35
            HPLTO6_DeviceStatusLogPage = &H3E
        End Enum
        Public Shared Function CreateDefault(PageTemplate As DefaultPages, ByVal logdata As Byte()) As PageData
            Dim pdata As PageData
            Dim DensityCodeTranslator As New SerializableDictionary(Of Long, String)
            Dim MediumTypeTranslator As New SerializableDictionary(Of Long, String)
            Dim SenseCodeTranslator As New SerializableDictionary(Of Long, String)
            With SenseCodeTranslator
                .Add(&H0, "NO SENSE")
                .Add(&H1, "RECOVERED ERROR")
                .Add(&H2, "NOT READY")
                .Add(&H3, "MEDIUM ERROR")
                .Add(&H4, "HARDWARE ERROR")
                .Add(&H5, "ILLEGAL REQUEST")
                .Add(&H6, "UNIT ATTENTION")
                .Add(&H7, "DATA PROTECT")
                .Add(&H8, "BLANK CHECK")
                .Add(&H9, "VENDOR SPECIFIC")
                .Add(&HA, "COPY ABORTED")
                .Add(&HB, "ABORTED COMMAND")
                .Add(&HC, "EQUAL")
                .Add(&HD, "VOLUME OVERFLOW")
                .Add(&HE, "MISCOMPARE")
                .Add(&HF, "RESERVED")
            End With
            Dim AdditionalSenseCodeTranslator As New SerializableDictionary(Of Long, String)
            With AdditionalSenseCodeTranslator
                .Add(&H0, "No addition sense")
                .Add(&H1, "Filemark detected")
                .Add(&H2, "End of Tape detected")
                .Add(&H4, "Beginning of Tape detected")
                .Add(&H5, "End of Data detected")
                .Add(&H16, "Operation in progress")
                .Add(&H18, "Erase operation in progress")
                .Add(&H19, "Locate operation in progress")
                .Add(&H1A, "Rewind operation in progress")
                .Add(&H400, "LUN not ready, cause not reportable")
                .Add(&H401, "LUN in process of becoming ready")
                .Add(&H402, "LUN not ready, Initializing command required")
                .Add(&H404, "LUN not ready, format in progress")
                .Add(&H407, "Command in progress")
                .Add(&H409, "LUN not ready, self-test in progress")
                .Add(&H40C, "LUN not accessible, port in unavailable state")
                .Add(&H412, "Logical unit offline")
                .Add(&H800, "Logical unit communication failure")
                .Add(&HB00, "Warning")
                .Add(&HB01, "Thermal limit exceeded")
                .Add(&HC00, "Write error")
                .Add(&HE01, "Information unit too short")
                .Add(&HE02, "Information unit too long")
                .Add(&HE03, "SK Illegal Request")
                .Add(&H1001, "Logical block guard check failed")
                .Add(&H1100, "Unrecovered read error")
                .Add(&H1112, "Media Auxiliary Memory read error")
                .Add(&H1400, "Recorded entity not found")
                .Add(&H1403, "End of Data not found")
                .Add(&H1A00, "Parameter list length error")
                .Add(&H2000, "Invalid command operation code")
                .Add(&H2400, "Invalid field in Command Descriptor Block")
                .Add(&H2500, "LUN not supported")
                .Add(&H2600, "Invalid field in parameter list")
                .Add(&H2601, "Parameter not supported")
                .Add(&H2602, "Parameter value invalid")
                .Add(&H2604, "Invalid release of persistent reservation")
                .Add(&H2610, "Data decryption key fail limit reached")
                .Add(&H2680, "Invalid CA certificate")
                .Add(&H2700, "Write-protected")
                .Add(&H2708, "Too many logical objects on partition to support operation")
                .Add(&H2800, "Not ready to ready transition, medium may have changed")
                .Add(&H2901, "Power-on reset")
                .Add(&H2902, "SCSI bus reset")
                .Add(&H2903, "Bus device reset")
                .Add(&H2904, "Internal firmware reboot")
                .Add(&H2907, "I_T nexus loss occurred")
                .Add(&H2A01, "Mode parameters changed")
                .Add(&H2A02, "Log parameters changed")
                .Add(&H2A03, "Reservations pre-empted")
                .Add(&H2A04, "Reservations released")
                .Add(&H2A05, "Registrations pre-empted")
                .Add(&H2A06, "Asymmetric access state changed")
                .Add(&H2A07, "Asymmetric access state transition failed")
                .Add(&H2A08, "Priority changed")
                .Add(&H2A0D, "Data encryption capabilities changed")
                .Add(&H2A10, "Timestamp changed")
                .Add(&H2A11, "Data encryption parameters changed by another initiator")
                .Add(&H2A12, "Data encryption parameters changed by a vendor-specific event")
                .Add(&H2A13, "Data Encryption Key Instance Counter has changed")
                .Add(&H2A14, "SA creation capabilities data has changed")
                .Add(&H2A15, "Medium removal prevention pre-empted")
                .Add(&H2A80, "Security configuration changed")
                .Add(&H2C00, "Command sequence invalid")
                .Add(&H2C07, "Previous busy status")
                .Add(&H2C08, "Previous task set full status")
                .Add(&H2C09, "Previous reservation conflict status")
                .Add(&H2C0B, "Not reserved")
                .Add(&H2F00, "Commands cleared by another initiator")
                .Add(&H3000, "Incompatible medium installed")
                .Add(&H3001, "Cannot read media, unknown format")
                .Add(&H3002, "Cannot read media: incompatible format")
                .Add(&H3003, "Cleaning cartridge installed")
                .Add(&H3004, "Cannot write medium")
                .Add(&H3005, "Cannot write medium, incompatible format")
                .Add(&H3006, "Cannot format, incompatible medium")
                .Add(&H3007, "Cleaning failure")
                .Add(&H300C, "WORM medium—overwrite attempted")
                .Add(&H300D, "WORM medium—integrity check failed")
                .Add(&H3100, "Medium format corrupted")
                .Add(&H3700, "Rounded parameter")
                .Add(&H3A00, "Medium not present")
                .Add(&H3A04, "Medium not present, Media Auxiliary Memory accessible")
                .Add(&H3B00, "Sequential positioning error")
                .Add(&H3B0C, "Position past BOM")
                .Add(&H3B1C, "Too many logical objects on partition to support operation.")
                .Add(&H3E00, "Logical unit has not self-configured yet")
                .Add(&H3F01, "Microcode has been changed")
                .Add(&H3F03, "Inquiry data has changed")
                .Add(&H3F05, "Device identifier changed")
                .Add(&H3F0E, "Reported LUNs data has changed")
                .Add(&H3F0F, "Echo buffer overwritten")
                .Add(&H4300, "Message error")
                .Add(&H4400, "Internal target failure")
                .Add(&H4500, "Selection/reselection failure")
                .Add(&H4700, "SCSI parity error")
                .Add(&H4800, "Initiator Detected Error message received")
                .Add(&H4900, "Invalid message")
                .Add(&H4B00, "Data phase error")
                .Add(&H4B02, "Too much write data")
                .Add(&H4B03, "ACK/NAK timeout")
                .Add(&H4B04, "NAK received")
                .Add(&H4B05, "Data offset error")
                .Add(&H4B06, "Initiator response timeout")
                .Add(&H4D00, "Tagged overlapped command")
                .Add(&H4E00, "Overlapped commands")
                .Add(&H5000, "Write append error")
                .Add(&H5200, "Cartridge fault")
                .Add(&H5300, "Media load or eject failed")
                .Add(&H5301, "Unload tape failure")
                .Add(&H5302, "Medium removal prevented")
                .Add(&H5303, "Insufficient resources")
                .Add(&H5304, "Medium thread or unthread failure")
                .Add(&H5504, "Insufficient registration resources")
                .Add(&H5506, "Media Auxiliary Memory full")
                .Add(&H5B01, "Threshold condition met")
                .Add(&H5D00, "Failure prediction threshold exceeded")
                .Add(&H5DFF, "Failure prediction threshold exceeded (false)")
                .Add(&H5E01, "Idle condition activated by timer")
                .Add(&H7400, "Security error")
                .Add(&H7401, "Unable to decrypt data")
                .Add(&H7402, "Unencrypted data encountered while decrypting")
                .Add(&H7403, "Incorrect data encryption key")
                .Add(&H7404, "Cryptographic integrity validation failed")
                .Add(&H7405, "Key-associated data descriptors changed.")
                .Add(&H7408, "Digital signature validation failure")
                .Add(&H7409, "Encryption mode mismatch on read")
                .Add(&H740A, "Encrypted block not RAW read-enabled")
                .Add(&H740B, "Incorrect encryption parameters")
                .Add(&H7421, "Data encryption configuration prevented")
                .Add(&H7440, "Authentication failed")
                .Add(&H7461, "External data encryption Key Manager access error")
                .Add(&H7462, "External data encryption Key Manager error")
                .Add(&H7463, "External data encryption management—key not found")
                .Add(&H7464, "External data encryption management—request not authorized")
                .Add(&H746E, "External data encryption control time-out")
                .Add(&H746F, "External data encryption control unknown error")
                .Add(&H7471, "Logical Unit access not authorized")
                .Add(&H7480, "KAD changed")
                .Add(&H7482, "Crypto KAD in CM failure")
                .Add(&H8282, "Drive requires cleaning")
                .Add(&H8283, "Bad microcode detected")
            End With
            With DensityCodeTranslator
                .Add(&H40, "L1")
                .Add(&H42, "L2")
                .Add(&H44, "L3")
                .Add(&H46, "L4")
                .Add(&H58, "L5")
                .Add(&H5A, "L6")
                .Add(&H5C, "L7")
                .Add(&H5D, "M8")
                .Add(&H5E, "L8")
                .Add(&H60, "L9")
            End With
            With MediumTypeTranslator
                .Add(0, "RW")
                .Add(1, "WORM")
            End With
            Dim TapeAlertFlag As New SerializableDictionary(Of Long, String)
            With TapeAlertFlag
                .Add(1, "Read")
                .Add(2, "Write")
                .Add(3, "Hard Error")
                .Add(4, "Medium")
                .Add(5, "Read Failure")
                .Add(6, "Write Failure")
                .Add(7, "Medium Life")
                .Add(8, "Not Data Grade")
                .Add(9, "Write-Protect")
                .Add(10, "Volume Removal Prevented")
                .Add(11, "Cleaning Volume")
                .Add(12, "Unsupported Format")
                .Add(13, "Recoverable Mechanical Cartridge Failure")
                .Add(14, "Unrecoverable Mechanical Cartridge Failure")
                .Add(15, "Memory Chip in Cartridge Failure")
                .Add(16, "Forced Eject")
                .Add(17, "Read-Only Format")
                .Add(18, "Tape Directory Corrupted on Load")
                .Add(19, "Nearing Medium Life")
                .Add(20, "Cleaning Required")
                .Add(21, "Cleaning Requested")
                .Add(22, "Expired Cleaning Volume")
                .Add(23, "Invalid Cleaning Volume")
                .Add(24, "Retension Requested")
                .Add(25, "Multi-port Interface Error on Primary Port")
                .Add(26, "Cooling Fan Failure")
                .Add(27, "Power Supply Failure")
                .Add(28, "Power Consumption")
                .Add(29, "Drive Preventative Maintenance Required")
                .Add(30, "Hardware A")
                .Add(31, "Hardware B")
                .Add(32, "Primary Interface")
                .Add(33, "Eject Media")
                .Add(34, "Microcode Update Failure")
                .Add(35, "Drive Humidity")
                .Add(36, "Drive Temperature")
                .Add(37, "Drive Voltage")
                .Add(38, "Predictive Failure")
                .Add(39, "Diagnostics Required")
                .Add(49, "Diminished Native Capacity")
                .Add(50, "Lost Statistics")
                .Add(51, "Tape Directory Invalid at Unload")
                .Add(52, "Tape System Area Write Failure")
                .Add(53, "Tape System Area Read Failure")
                .Add(54, "No Start of Data")
                .Add(55, "Loading or Threading Failure")
                .Add(56, "Unrecoverable Unload Failure")
                .Add(57, "Automation Interface Failure")
                .Add(58, "Microcode Failure")
                .Add(59, "WORM Medium — Integrity Check Failed")
                .Add(60, "WORM Medium — Overwrite Attempted")
                .Add(61, "Encryption Policy Violation")
            End With
            Select Case PageTemplate
                Case DefaultPages.HPLTO6_SupportedLogPagesPage
                    pdata = New PageData With {.Name = "Supported Log Pages page", .PageCode = &H0, .RawData = logdata}
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "Page Code",
                                    .StartByte = 0,
                                    .BitOffset = 2,
                                    .TotalBits = 6,
                                    .Type = DataItem.DataType.Binary})
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "Page Length",
                                    .StartByte = 2,
                                    .BitOffset = 0,
                                    .TotalBits = 16,
                                    .Type = DataItem.DataType.Int16})
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "Supported Page List",
                                    .StartByte = 4,
                                    .BitOffset = 0,
                                    .TotalBits = 0,
                                    .DynamicParamCodeBitOffset = 0,
                                    .DynamicParamCodeStartByte = 0,
                                    .DynamicParamCodeTotalBits = 8,
                                    .DynamicParamLenBitOffset = 0,
                                    .DynamicParamLenStartByte = 0,
                                    .DynamicParamLenTotalBits = 0,
                                    .DynamicParamDataStartByte = 1,
                                    .EnumTranslator = New SerializableDictionary(Of Long, String),
                                    .DynamicParamType = New SerializableDictionary(Of Long, DataItem.DataType),
                                    .Type = DataItem.DataType.DynamicPage})
                    With pdata.Items.Last.EnumTranslator
                        .Add(&H0, "Supported Pages (00h)")
                        .Add(&H2, "Write Error Counters (02h)")
                        .Add(&H3, "Read Error Counters (03h)")
                        .Add(&HC, "Sequential Access Device Log (0Ch)")
                        .Add(&HD, "Temperature Log (0Dh)")
                        .Add(&H11, "DTD Status Log (11h)")
                        .Add(&H12, "TapeAlert Response Log (12h)")
                        .Add(&H13, "Requested Recovery Log (13h)")
                        .Add(&H14, "Device Statistics Log (14h)")
                        .Add(&H15, "Service Buffers Information Log (15h)")
                        .Add(&H16, "Tape Diagnostics Data Log (16h)")
                        .Add(&H17, "Volume Statistics Log (17h)")
                        .Add(&H18, "SAS Port Log (18h)")
                        .Add(&H1B, "Data Compression Log (1Bh)")
                        .Add(&H2E, "TapeAlert Log (2Eh)")
                        .Add(&H30, "Tape Usage Log (30h)")
                        .Add(&H31, "Tape Capacity Log (31h)")
                        .Add(&H32, "Data Compression (HP-only) Log (32h)")
                        .Add(&H33, "Device Wellness Log (33h)")
                        .Add(&H34, "Performance Log (34h)")
                        .Add(&H35, "DT Device Error Log (35h)")
                        .Add(&H3E, "Device Status Log (3Eh)")
                    End With
                    With pdata.Items.Last.DynamicParamType
                        .Add(&H0, DataItem.DataType.Binary)
                        .Add(&H2, DataItem.DataType.Binary)
                        .Add(&H3, DataItem.DataType.Binary)
                        .Add(&HC, DataItem.DataType.Binary)
                        .Add(&HD, DataItem.DataType.Binary)
                        .Add(&H11, DataItem.DataType.Binary)
                        .Add(&H12, DataItem.DataType.Binary)
                        .Add(&H13, DataItem.DataType.Binary)
                        .Add(&H14, DataItem.DataType.Binary)
                        .Add(&H15, DataItem.DataType.Binary)
                        .Add(&H16, DataItem.DataType.Binary)
                        .Add(&H17, DataItem.DataType.Binary)
                        .Add(&H18, DataItem.DataType.Binary)
                        .Add(&H1B, DataItem.DataType.Binary)
                        .Add(&H2E, DataItem.DataType.Binary)
                        .Add(&H30, DataItem.DataType.Binary)
                        .Add(&H31, DataItem.DataType.Binary)
                        .Add(&H32, DataItem.DataType.Binary)
                        .Add(&H33, DataItem.DataType.Binary)
                        .Add(&H34, DataItem.DataType.Binary)
                        .Add(&H35, DataItem.DataType.Binary)
                        .Add(&H3E, DataItem.DataType.Binary)
                    End With
                Case DefaultPages.HPLTO6_WriteErrorCountersLogPage
                    pdata = New PageData With {.Name = "Write Error Counters log page", .PageCode = &H2, .RawData = logdata}
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "Page Code",
                                    .StartByte = 0,
                                    .BitOffset = 2,
                                    .TotalBits = 6,
                                    .Type = DataItem.DataType.Binary})
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "Page Length",
                                    .StartByte = 2,
                                    .BitOffset = 0,
                                    .TotalBits = 16,
                                    .Type = DataItem.DataType.Int16})
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "Write Error Counters",
                                    .StartByte = 4,
                                    .BitOffset = 0,
                                    .TotalBits = 0,
                                    .DynamicParamCodeBitOffset = 0,
                                    .DynamicParamCodeStartByte = 1,
                                    .DynamicParamCodeTotalBits = 8,
                                    .DynamicParamLenBitOffset = 0,
                                    .DynamicParamLenStartByte = 3,
                                    .DynamicParamLenTotalBits = 8,
                                    .DynamicParamDataStartByte = 4,
                                    .EnumTranslator = New SerializableDictionary(Of Long, String),
                                    .DynamicParamType = New SerializableDictionary(Of Long, DataItem.DataType),
                                    .Type = DataItem.DataType.DynamicPage})
                    With pdata.Items.Last.EnumTranslator
                        .Add(&H0, "Errors corrected without substantial delay (Total number of errors corrected without delay)")
                        .Add(&H1, "Errors corrected with possible delays (Total number of errors corrected using retries)")
                        .Add(&H2, "Total (Sum of parameters 3 and 6)")
                        .Add(&H3, "Total errors corrected (The number of data sets that needed to be rewritten)")
                        .Add(&H4, "Total times error correction processed (Number of CCQ sets rewritten)")
                        .Add(&H5, "Total data sets processed (The total number of data sets written)")
                        .Add(&H6, "Total uncorrected errors (The number of data sets that could not be written)")
                    End With
                    With pdata.Items.Last.DynamicParamType
                        .Add(&H0, DataItem.DataType.Int32)
                        .Add(&H1, DataItem.DataType.Int32)
                        .Add(&H2, DataItem.DataType.Int32)
                        .Add(&H3, DataItem.DataType.Int32)
                        .Add(&H4, DataItem.DataType.Int32)
                        .Add(&H5, DataItem.DataType.Int32)
                        .Add(&H6, DataItem.DataType.Int32)
                    End With
                Case DefaultPages.HPLTO6_ReadErrorCountersLogPage
                    pdata = New PageData With {.Name = "Read Error Counters log page", .PageCode = &H3, .RawData = logdata}
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "Page Code",
                                    .StartByte = 0,
                                    .BitOffset = 2,
                                    .TotalBits = 6,
                                    .Type = DataItem.DataType.Binary})
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "Page Length",
                                    .StartByte = 2,
                                    .BitOffset = 0,
                                    .TotalBits = 16,
                                    .Type = DataItem.DataType.Int16})
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "Read Error Counters",
                                    .StartByte = 4,
                                    .BitOffset = 0,
                                    .TotalBits = 0,
                                    .DynamicParamCodeBitOffset = 0,
                                    .DynamicParamCodeStartByte = 1,
                                    .DynamicParamCodeTotalBits = 8,
                                    .DynamicParamLenBitOffset = 0,
                                    .DynamicParamLenStartByte = 3,
                                    .DynamicParamLenTotalBits = 8,
                                    .DynamicParamDataStartByte = 4,
                                    .EnumTranslator = New SerializableDictionary(Of Long, String),
                                    .DynamicParamType = New SerializableDictionary(Of Long, DataItem.DataType),
                                    .Type = DataItem.DataType.DynamicPage})
                    With pdata.Items.Last.EnumTranslator
                        .Add(&H0, "Errors corrected without substantial delay (Total number of errors corrected without delay)")
                        .Add(&H1, "Errors corrected with possible delays (Total number of errors corrected using retries)")
                        .Add(&H2, "Total (Sum of parameters 3 and 6)")
                        .Add(&H3, "Total errors corrected (The number of data sets that were corrected after a read retry)")
                        .Add(&H4, "Total times error correction processed (Number of times C2 correction is invoked)")
                        .Add(&H5, "Total bytes processed (The total number of data sets read)")
                        .Add(&H6, "Total uncorrected errors (The number of data sets that could not be read after retries)")
                    End With
                    With pdata.Items.Last.DynamicParamType
                        .Add(&H0, DataItem.DataType.Int32)
                        .Add(&H1, DataItem.DataType.Int32)
                        .Add(&H2, DataItem.DataType.Int32)
                        .Add(&H3, DataItem.DataType.Int32)
                        .Add(&H4, DataItem.DataType.Int32)
                        .Add(&H5, DataItem.DataType.Int32)
                        .Add(&H6, DataItem.DataType.Int32)
                    End With
                Case DefaultPages.HPLTO6_SequentialAccessDeviceLogPage
                    pdata = New PageData With {.Name = "Sequential Access Device log page", .PageCode = &HC, .RawData = logdata}
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "Page Code",
                                    .StartByte = 0,
                                    .BitOffset = 2,
                                    .TotalBits = 6,
                                    .Type = DataItem.DataType.Binary})
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "Page Length",
                                    .StartByte = 2,
                                    .BitOffset = 0,
                                    .TotalBits = 16,
                                    .Type = DataItem.DataType.Int16})
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "Supported Page List",
                                    .StartByte = 4,
                                    .BitOffset = 0,
                                    .TotalBits = 0,
                                    .DynamicParamCodeBitOffset = 0,
                                    .DynamicParamCodeStartByte = 0,
                                    .DynamicParamCodeTotalBits = 16,
                                    .DynamicParamLenBitOffset = 0,
                                    .DynamicParamLenStartByte = 3,
                                    .DynamicParamLenTotalBits = 8,
                                    .DynamicParamDataStartByte = 4,
                                    .EnumTranslator = New SerializableDictionary(Of Long, String),
                                    .DynamicParamType = New SerializableDictionary(Of Long, DataItem.DataType),
                                    .Type = DataItem.DataType.DynamicPage})
                    With pdata.Items.Last.EnumTranslator
                        .Add(&H0, "Total channel write bytes. The number of data bytes received from application clients during write command operations. This is the number of bytes transferred over SCSI, before compression.")
                        .Add(&H1, "Total device write bytes. The number of data bytes written to the media as a result of write command operations, not counting the overhead from ECC and formatting. This is the number of data bytes transferred to media, after compression.")
                        .Add(&H2, "Total device read bytes. The number of data bytes read from the media during read command operations, not counting the overhead from ECC and formatting. This is the number of data bytes transferred from media with compression.")
                        .Add(&H3, "Total channel read bytes. The number of data bytes transferred to the initiator or initiators during read command operations. This is the number of bytes transferred over SCSI, after decompression.")
                        .Add(&H4, "The approximate native capacity from BOP to EOD, in megabytes. ")
                        .Add(&H5, "The approximate native capacity between BOP and EW in the current partition in megabytes.")
                        .Add(&H6, "The minimum native capacity from EW and EOP in the current partition, in megabytes.")
                        .Add(&H7, "The approximate native capacity from BOP to the current position of the medium in megabytes.")
                        .Add(&H8, "The maximum native capacity that is currently allowed to be in the device object buffer, in megabytes.")
                        .Add(&H100, "Cleaning requested; a non-volatile cleaning indication.")
                        .Add(&H8000, "Total megabytes processed since cleaning. The Number of megabytes processed to tape since last cleaning (written after compression/read before decompression)")
                        .Add(&H8001, "Lifetime load cycles. This is the number of times the drive has been loaded in its lifetime.")
                        .Add(&H8002, "Lifetime cleaning cycles. This is the number of times over its lifetime the drive has been cleaned using a cleaner cartridge.")
                        .Add(&H8003, "Lifetime Power-on time. This is the number of seconds the drive has been powered on over its lifetime.")
                    End With
                    With pdata.Items.Last.DynamicParamType
                        .Add(&H0, DataItem.DataType.Int64)
                        .Add(&H1, DataItem.DataType.Int64)
                        .Add(&H2, DataItem.DataType.Int64)
                        .Add(&H3, DataItem.DataType.Int64)
                        .Add(&H4, DataItem.DataType.Int32)
                        .Add(&H5, DataItem.DataType.Int32)
                        .Add(&H6, DataItem.DataType.Int32)
                        .Add(&H7, DataItem.DataType.Int32)
                        .Add(&H8, DataItem.DataType.Int32)
                        .Add(&H100, DataItem.DataType.Int64)
                        .Add(&H8000, DataItem.DataType.Int32)
                        .Add(&H8001, DataItem.DataType.Int32)
                        .Add(&H8002, DataItem.DataType.Int32)
                        .Add(&H8003, DataItem.DataType.Int32)
                    End With
                Case DefaultPages.HPLTO6_TemperatureLogPage
                    pdata = New PageData With {.Name = "Temperature log page", .PageCode = &HD, .RawData = logdata}
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "Page Code",
                                    .StartByte = 0,
                                    .BitOffset = 0,
                                    .TotalBits = 8,
                                    .Type = DataItem.DataType.Binary})
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "Page Length",
                                    .StartByte = 2,
                                    .BitOffset = 0,
                                    .TotalBits = 16,
                                    .Type = DataItem.DataType.Int16})
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "Temperature log parameter",
                                    .StartByte = 4,
                                    .BitOffset = 0,
                                    .TotalBits = 0,
                                    .DynamicParamCodeBitOffset = 0,
                                    .DynamicParamCodeStartByte = 0,
                                    .DynamicParamCodeTotalBits = 16,
                                    .DynamicParamLenBitOffset = 0,
                                    .DynamicParamLenStartByte = 3,
                                    .DynamicParamLenTotalBits = 8,
                                    .DynamicParamDataStartByte = 4,
                                    .EnumTranslator = New SerializableDictionary(Of Long, String),
                                    .DynamicParamType = New SerializableDictionary(Of Long, DataItem.DataType),
                                    .Type = DataItem.DataType.DynamicPage})
                    With pdata.Items.Last.EnumTranslator
                        .Add(&H0, "Temperature")
                        .Add(&H1, "Reference Temperature")
                    End With
                    With pdata.Items.Last.DynamicParamType
                        .Add(&H0, DataItem.DataType.Int16)
                        .Add(&H1, DataItem.DataType.Int16)
                    End With
                Case DefaultPages.HPLTO6_DataTransferDeviceStatusLogPage
                    pdata = New PageData With {.Name = "Data Transfer Device (DTD) Status log page", .PageCode = &H11, .RawData = logdata}
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "Page Code",
                                    .StartByte = 0,
                                    .BitOffset = 0,
                                    .TotalBits = 8,
                                    .Type = DataItem.DataType.Binary})
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "Page Length",
                                    .StartByte = 2,
                                    .BitOffset = 0,
                                    .TotalBits = 16,
                                    .Type = DataItem.DataType.Int16})
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "DTD Status log parameter",
                                    .StartByte = 4,
                                    .BitOffset = 0,
                                    .TotalBits = 0,
                                    .DynamicParamCodeBitOffset = 0,
                                    .DynamicParamCodeStartByte = 0,
                                    .DynamicParamCodeTotalBits = 16,
                                    .DynamicParamLenBitOffset = 0,
                                    .DynamicParamLenStartByte = 3,
                                    .DynamicParamLenTotalBits = 8,
                                    .DynamicParamDataStartByte = 4,
                                    .EnumTranslator = New SerializableDictionary(Of Long, String),
                                    .DynamicParamType = New SerializableDictionary(Of Long, DataItem.DataType),
                                    .PageDataTemplate = New SerializableDictionary(Of Long, PageData),
                                    .Type = DataItem.DataType.DynamicPage})

                    With pdata.Items.Last.EnumTranslator
                        .Add(&H0, "Very High Frequency data")
                        .Add(&H1, "Very High Frequency polling delay")
                        .Add(&H2, "DT device ADC data encryption control status")
                        .Add(&H3, "Key management error data")
                        .Add(&H101, "DTD primary status - SAS/FC Port A")
                        .Add(&H102, "DTD primary status - SAS/FC Port B")
                        .Add(&H103, "DTD primary status - Fibre Channel NPIV port A")
                        .Add(&H104, "DTD primary status - Fibre Channel NPIV port B")
                        .Add(&H8000, "VU Very High Frequency data")
                        .Add(&H8003, "VU key management error")
                        .Add(&H8010, "VU extended VHF data")
                        .Add(&H8020, "VU multi-initiator conflict warning")
                        .Add(&HA101, "VU Fibre Channel port A failover status")
                        .Add(&HA102, "VU Fibre Channel port B failover status")
                    End With
                    With pdata.Items.Last.DynamicParamType
                        .Add(&H0, DataItem.DataType.PageData)
                        .Add(&H1, DataItem.DataType.PageData)
                        .Add(&H2, DataItem.DataType.PageData)
                        .Add(&H3, DataItem.DataType.PageData)
                        .Add(&H101, DataItem.DataType.PageData)
                        .Add(&H102, DataItem.DataType.PageData)
                        .Add(&H103, DataItem.DataType.PageData)
                        .Add(&H104, DataItem.DataType.PageData)
                        .Add(&H8000, DataItem.DataType.PageData)
                        .Add(&H8003, DataItem.DataType.PageData)
                        .Add(&H8010, DataItem.DataType.PageData)
                        .Add(&H8020, DataItem.DataType.PageData)
                        .Add(&HA101, DataItem.DataType.PageData)
                        .Add(&HA102, DataItem.DataType.PageData)
                    End With
                    With pdata.Items.Last.PageDataTemplate
                        Dim subPage As PageData
                        subPage = New PageData With {.PageCode = 0, .Name = "Very High Frequency data"}
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Prevent/Allow Medium Removal bit", .StartByte = 0, .BitOffset = 0, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Host Initiated Unload bit", .StartByte = 0, .BitOffset = 1, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "MAM Accessible", .StartByte = 0, .BitOffset = 2, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Data Compression Enabled", .StartByte = 0, .BitOffset = 3, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Write Protect", .StartByte = 0, .BitOffset = 4, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Clean Requested", .StartByte = 0, .BitOffset = 5, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Cleaning Required", .StartByte = 0, .BitOffset = 6, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "DTD Initialized", .StartByte = 0, .BitOffset = 7, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "In Transition", .StartByte = 1, .BitOffset = 0, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Robotic Access Allowed", .StartByte = 1, .BitOffset = 2, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Media Present", .StartByte = 1, .BitOffset = 3, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Media Seated", .StartByte = 1, .BitOffset = 5, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Media Threaded", .StartByte = 1, .BitOffset = 6, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Data Accessible", .StartByte = 1, .BitOffset = 7, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "DT Device Activity", .StartByte = 2, .BitOffset = 0, .TotalBits = 8, .Type = DataItem.DataType.Enum})
                        subPage.Items.Last.EnumTranslator = New SerializableDictionary(Of Long, String)
                        With subPage.Items.Last.EnumTranslator
                            .Add(&H0, "No tape motion")
                            .Add(&H1, "Cleaning operation in progress")
                            .Add(&H2, "Tape being loaded")
                            .Add(&H3, "Tape being unloaded")
                            .Add(&H4, "Other tape activity")
                            .Add(&H5, "Reading")
                            .Add(&H6, "Writing")
                            .Add(&H7, "Locating")
                            .Add(&H8, "Rewinding")
                            .Add(&H9, "Erasing")
                            .Add(&HC, "Other DT device activity")
                            .Add(&HD, "Microcode update in progress")
                            .Add(&HE, "Reading encrypted data from tape")
                            .Add(&HF, "Writing encrypted data to tape")
                        End With
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "VU Extended VHF Data log parameter changed", .StartByte = 3, .BitOffset = 0, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Tape Diagnostic Data Entry Created", .StartByte = 3, .BitOffset = 2, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Encryption Parameters Present", .StartByte = 3, .BitOffset = 3, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Encryption Service Request", .StartByte = 3, .BitOffset = 4, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Recovery Requested", .StartByte = 3, .BitOffset = 5, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Interface Changed", .StartByte = 3, .BitOffset = 6, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "TapeAlert flag has changed", .StartByte = 3, .BitOffset = 7, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        .Add(&H0, subPage)
                        subPage = New PageData With {.PageCode = 1, .Name = "Very High Frequency polling delay"}
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "VHF Polling Delay in ms", .StartByte = 0, .BitOffset = 0, .TotalBits = 16, .Type = DataItem.DataType.Int16})
                        .Add(&H1, subPage)
                        subPage = New PageData With {.PageCode = 2, .Name = "DT device ADC data encryption control status"}
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Aborted", .StartByte = 1, .BitOffset = 3, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Key Management Error", .StartByte = 1, .BitOffset = 2, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Decryption Parameters Request", .StartByte = 1, .BitOffset = 1, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Encryption Parameters Request", .StartByte = 1, .BitOffset = 0, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Parameters Request Sequence Identifier", .StartByte = 2, .BitOffset = 0, .TotalBits = 32, .Type = DataItem.DataType.Binary})
                        .Add(&H2, subPage)
                        subPage = New PageData With {.PageCode = 3, .Name = "Key management error data"}
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Error Type", .StartByte = 0, .BitOffset = 0, .TotalBits = 4, .Type = DataItem.DataType.Enum})
                        subPage.Items.Last.EnumTranslator = New SerializableDictionary(Of Long, String)
                        subPage.Items.Last.EnumTranslator.Add(&H0, "No error")
                        subPage.Items.Last.EnumTranslator.Add(&H1, "Encryption parameters request error")
                        subPage.Items.Last.EnumTranslator.Add(&H2, "Decryption parameters request error")
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Key Timeout", .StartByte = 0, .BitOffset = 4, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Parameters Request Sequence Identifier", .StartByte = 2, .BitOffset = 0, .TotalBits = 32, .Type = DataItem.DataType.Binary})
                        .Add(&H3, subPage)
                        subPage = New PageData With {.PageCode = &H101, .Name = "DTD primary status - SAS/FC Port A"}
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "FC Current Topology", .StartByte = 0, .BitOffset = 0, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "FC Current Speed", .StartByte = 0, .BitOffset = 1, .TotalBits = 3, .Type = DataItem.DataType.Enum})
                        subPage.Items.Last.EnumTranslator = New SerializableDictionary(Of Long, String)
                        With subPage.Items.Last.EnumTranslator
                            .Add(&H0, "1 Gbps")
                            .Add(&H1, "2 Gbps")
                            .Add(&H2, "4 Gbps")
                            .Add(&H3, "8 Gbps")
                            .Add(&H4, "16 Gbps")
                            .Add(&H5, "32 Gbps")
                            .Add(&H6, "64 Gbps")
                            .Add(&H7, "128 Gbps")
                        End With
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "FC Login Complete", .StartByte = 0, .BitOffset = 4, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "FC AL_PA conflict", .StartByte = 0, .BitOffset = 5, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Signal", .StartByte = 0, .BitOffset = 6, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Port Initialization Complete", .StartByte = 0, .BitOffset = 7, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "FC Current N_Port ID", .StartByte = 1, .BitOffset = 0, .TotalBits = 24, .Type = DataItem.DataType.Binary})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "FC Current FC_AL Loop ID", .StartByte = 7, .BitOffset = 1, .TotalBits = 7, .Type = DataItem.DataType.Byte})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "FC Current Port Name", .StartByte = 8, .BitOffset = 0, .TotalBits = 32, .Type = DataItem.DataType.Binary})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "FC Current Node Name", .StartByte = 12, .BitOffset = 0, .TotalBits = 64, .Type = DataItem.DataType.Binary})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "SAS Negotiated Physical Link Rate", .StartByte = 0, .BitOffset = 0, .TotalBits = 4, .Type = DataItem.DataType.Byte})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "SAS Current Hashed Address", .StartByte = 1, .BitOffset = 0, .TotalBits = 24, .Type = DataItem.DataType.Binary})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "SAS Address", .StartByte = 4, .BitOffset = 0, .TotalBits = 64, .Type = DataItem.DataType.Binary})
                        .Add(&H101, subPage)
                        subPage = New PageData With {.PageCode = &H102, .Name = "DTD primary status - SAS/FC Port B"}
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "FC Current Topology", .StartByte = 0, .BitOffset = 0, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "FC Current Speed", .StartByte = 0, .BitOffset = 1, .TotalBits = 3, .Type = DataItem.DataType.Enum})
                        subPage.Items.Last.EnumTranslator = New SerializableDictionary(Of Long, String)
                        With subPage.Items.Last.EnumTranslator
                            .Add(&H0, "1 Gbps")
                            .Add(&H1, "2 Gbps")
                            .Add(&H2, "4 Gbps")
                            .Add(&H3, "8 Gbps")
                            .Add(&H4, "16 Gbps")
                            .Add(&H5, "32 Gbps")
                            .Add(&H6, "64 Gbps")
                            .Add(&H7, "128 Gbps")
                        End With
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "FC Login Complete", .StartByte = 0, .BitOffset = 4, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "FC AL_PA conflict", .StartByte = 0, .BitOffset = 5, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Signal", .StartByte = 0, .BitOffset = 6, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Port Initialization Complete", .StartByte = 0, .BitOffset = 7, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "FC Current N_Port ID", .StartByte = 1, .BitOffset = 0, .TotalBits = 24, .Type = DataItem.DataType.Binary})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "FC Current FC_AL Loop ID", .StartByte = 7, .BitOffset = 1, .TotalBits = 7, .Type = DataItem.DataType.Byte})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "FC Current Port Name", .StartByte = 8, .BitOffset = 0, .TotalBits = 32, .Type = DataItem.DataType.Binary})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "FC Current Node Name", .StartByte = 12, .BitOffset = 0, .TotalBits = 64, .Type = DataItem.DataType.Binary})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "SAS Negotiated Physical Link Rate", .StartByte = 0, .BitOffset = 0, .TotalBits = 4, .Type = DataItem.DataType.Byte})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "SAS Current Hashed Address", .StartByte = 1, .BitOffset = 0, .TotalBits = 24, .Type = DataItem.DataType.Binary})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "SAS Address", .StartByte = 4, .BitOffset = 0, .TotalBits = 64, .Type = DataItem.DataType.Binary})
                        .Add(&H102, subPage)
                        subPage = New PageData With {.PageCode = &H103, .Name = "DTD primary status - Fibre Channel NPIV port A"}
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "FC Current Topology", .StartByte = 0, .BitOffset = 0, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "FC Current Speed", .StartByte = 0, .BitOffset = 1, .TotalBits = 3, .Type = DataItem.DataType.Enum})
                        subPage.Items.Last.EnumTranslator = New SerializableDictionary(Of Long, String)
                        With subPage.Items.Last.EnumTranslator
                            .Add(&H0, "1 Gbps")
                            .Add(&H1, "2 Gbps")
                            .Add(&H2, "4 Gbps")
                            .Add(&H3, "8 Gbps")
                            .Add(&H4, "16 Gbps")
                            .Add(&H5, "32 Gbps")
                            .Add(&H6, "64 Gbps")
                            .Add(&H7, "128 Gbps")
                        End With
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "FC Login Complete", .StartByte = 0, .BitOffset = 4, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "FC AL_PA conflict", .StartByte = 0, .BitOffset = 5, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Signal", .StartByte = 0, .BitOffset = 6, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Port Initialization Complete", .StartByte = 0, .BitOffset = 7, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "FC Current N_Port ID", .StartByte = 1, .BitOffset = 0, .TotalBits = 24, .Type = DataItem.DataType.Binary})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "FC Current FC_AL Loop ID", .StartByte = 7, .BitOffset = 1, .TotalBits = 7, .Type = DataItem.DataType.Byte})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "FC Current Port Name", .StartByte = 8, .BitOffset = 0, .TotalBits = 32, .Type = DataItem.DataType.Binary})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "FC Current Node Name", .StartByte = 12, .BitOffset = 0, .TotalBits = 64, .Type = DataItem.DataType.Binary})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "SAS Negotiated Physical Link Rate", .StartByte = 0, .BitOffset = 0, .TotalBits = 4, .Type = DataItem.DataType.Byte})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "SAS Current Hashed Address", .StartByte = 1, .BitOffset = 0, .TotalBits = 24, .Type = DataItem.DataType.Binary})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "SAS Address", .StartByte = 4, .BitOffset = 0, .TotalBits = 64, .Type = DataItem.DataType.Binary})
                        .Add(&H103, subPage)
                        subPage = New PageData With {.PageCode = &H104, .Name = "DTD primary status - Fibre Channel NPIV port B"}
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "FC Current Topology", .StartByte = 0, .BitOffset = 0, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "FC Current Speed", .StartByte = 0, .BitOffset = 1, .TotalBits = 3, .Type = DataItem.DataType.Enum})
                        subPage.Items.Last.EnumTranslator = New SerializableDictionary(Of Long, String)
                        With subPage.Items.Last.EnumTranslator
                            .Add(&H0, "1 Gbps")
                            .Add(&H1, "2 Gbps")
                            .Add(&H2, "4 Gbps")
                            .Add(&H3, "8 Gbps")
                            .Add(&H4, "16 Gbps")
                            .Add(&H5, "32 Gbps")
                            .Add(&H6, "64 Gbps")
                            .Add(&H7, "128 Gbps")
                        End With
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "FC Login Complete", .StartByte = 0, .BitOffset = 4, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "FC AL_PA conflict", .StartByte = 0, .BitOffset = 5, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Signal", .StartByte = 0, .BitOffset = 6, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Port Initialization Complete", .StartByte = 0, .BitOffset = 7, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "FC Current N_Port ID", .StartByte = 1, .BitOffset = 0, .TotalBits = 24, .Type = DataItem.DataType.Binary})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "FC Current FC_AL Loop ID", .StartByte = 7, .BitOffset = 1, .TotalBits = 7, .Type = DataItem.DataType.Byte})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "FC Current Port Name", .StartByte = 8, .BitOffset = 0, .TotalBits = 32, .Type = DataItem.DataType.Binary})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "FC Current Node Name", .StartByte = 12, .BitOffset = 0, .TotalBits = 64, .Type = DataItem.DataType.Binary})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "SAS Negotiated Physical Link Rate", .StartByte = 0, .BitOffset = 0, .TotalBits = 4, .Type = DataItem.DataType.Byte})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "SAS Current Hashed Address", .StartByte = 1, .BitOffset = 0, .TotalBits = 24, .Type = DataItem.DataType.Binary})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "SAS Address", .StartByte = 4, .BitOffset = 0, .TotalBits = 64, .Type = DataItem.DataType.Binary})
                        .Add(&H104, subPage)
                        subPage = New PageData With {.PageCode = &H8000, .Name = "VU Very High Frequency data"}
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Prevent/Allow Medium Removal bit", .StartByte = 0, .BitOffset = 0, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Host Initiated Unload bit", .StartByte = 0, .BitOffset = 1, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "MAM Accessible", .StartByte = 0, .BitOffset = 2, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Data Compression Enabled", .StartByte = 0, .BitOffset = 3, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Write Protect", .StartByte = 0, .BitOffset = 4, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Clean Requested", .StartByte = 0, .BitOffset = 5, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Cleaning Required", .StartByte = 0, .BitOffset = 6, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "DTD Initialized", .StartByte = 0, .BitOffset = 7, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "In Transition", .StartByte = 1, .BitOffset = 0, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Robotic Access Allowed", .StartByte = 1, .BitOffset = 2, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Media Present", .StartByte = 1, .BitOffset = 3, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Media Seated", .StartByte = 1, .BitOffset = 5, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Media Threaded", .StartByte = 1, .BitOffset = 6, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Data Accessible", .StartByte = 1, .BitOffset = 7, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "DT Device Activity", .StartByte = 2, .BitOffset = 0, .TotalBits = 8, .Type = DataItem.DataType.Enum})
                        subPage.Items.Last.EnumTranslator = New SerializableDictionary(Of Long, String)
                        With subPage.Items.Last.EnumTranslator
                            .Add(&H0, "No tape motion")
                            .Add(&H1, "Cleaning operation in progress")
                            .Add(&H2, "Tape being loaded")
                            .Add(&H3, "Tape being unloaded")
                            .Add(&H4, "Other tape activity")
                            .Add(&H5, "Reading")
                            .Add(&H6, "Writing")
                            .Add(&H7, "Locating")
                            .Add(&H8, "Rewinding")
                            .Add(&H9, "Erasing")
                            .Add(&HC, "Other DT device activity")
                            .Add(&HD, "Microcode update in progress")
                            .Add(&HE, "Reading encrypted data from tape")
                            .Add(&HF, "Writing encrypted data to tape")
                        End With
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "VU Extended VHF Data log parameter changed", .StartByte = 3, .BitOffset = 0, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Tape Diagnostic Data Entry Created", .StartByte = 3, .BitOffset = 2, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Encryption Parameters Present", .StartByte = 3, .BitOffset = 3, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Encryption Service Request", .StartByte = 3, .BitOffset = 4, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Recovery Requested", .StartByte = 3, .BitOffset = 5, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Interface Changed", .StartByte = 3, .BitOffset = 6, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "TapeAlert flag has changed", .StartByte = 3, .BitOffset = 7, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Host Login", .StartByte = 4, .BitOffset = 0, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Hardware Error", .StartByte = 4, .BitOffset = 3, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Media Error", .StartByte = 4, .BitOffset = 4, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Upgrade Cartridge", .StartByte = 4, .BitOffset = 5, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Loading", .StartByte = 4, .BitOffset = 6, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Unloading", .StartByte = 4, .BitOffset = 7, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Snapshot", .StartByte = 5, .BitOffset = 5, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Load Complete", .StartByte = 5, .BitOffset = 6, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Unload Complete", .StartByte = 5, .BitOffset = 7, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        .Add(&H8000, subPage)
                        subPage = New PageData With {.PageCode = &H8003, .Name = "VU key management error data"}
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Key Timeout", .StartByte = 0, .BitOffset = 4, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Error Type", .StartByte = 0, .BitOffset = 5, .TotalBits = 3, .Type = DataItem.DataType.Byte})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Sense Key", .StartByte = 4, .BitOffset = 4, .TotalBits = 4, .Type = DataItem.DataType.Enum})
                        subPage.Items.Last.EnumTranslator = SenseCodeTranslator
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Additional Sense Code", .StartByte = 5, .BitOffset = 0, .TotalBits = 8, .Type = DataItem.DataType.Enum})
                        subPage.Items.Last.EnumTranslator = AdditionalSenseCodeTranslator
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Additional Sense Code Qualifier", .StartByte = 6, .BitOffset = 0, .TotalBits = 8, .Type = DataItem.DataType.Byte})
                        .Add(&H8003, subPage)
                        subPage = New PageData With {.PageCode = &H8010, .Name = "VU extended VHF data"}
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Multi-Initiator Conflict Warning", .StartByte = 0, .BitOffset = 6, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Snapshot", .StartByte = 0, .BitOffset = 7, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Hibernate Mode", .StartByte = 3, .BitOffset = 4, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Legacy Reservations Changed", .StartByte = 3, .BitOffset = 5, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Persistent Reservations Changed", .StartByte = 3, .BitOffset = 6, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Prevent Allow Medium Removal Changed", .StartByte = 3, .BitOffset = 7, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        .Add(&H8010, subPage)
                        subPage = New PageData With {.PageCode = &H8020, .Name = "VU multi-initiator conflict warning"}
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Host Name 1 (previous)", .StartByte = 0, .BitOffset = 0, .TotalBits = 64, .Type = DataItem.DataType.Text})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Host Name 2 (latest)", .StartByte = 8, .BitOffset = 0, .TotalBits = 64, .Type = DataItem.DataType.Text})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Operation Code", .StartByte = 16, .BitOffset = 0, .TotalBits = 8, .Type = DataItem.DataType.Byte})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Service Action", .StartByte = 17, .BitOffset = 3, .TotalBits = 5, .Type = DataItem.DataType.Byte})
                        .Add(&H8020, subPage)
                        subPage = New PageData With {.PageCode = &HA101, .Name = "VU Fibre Channel port A failover status"}
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Active", .StartByte = 0, .BitOffset = 7, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Failover Trigger", .StartByte = 1, .BitOffset = 4, .TotalBits = 4, .Type = DataItem.DataType.Enum})
                        subPage.Items.Last.EnumTranslator = New SerializableDictionary(Of Long, String)
                        With subPage.Items.Last.EnumTranslator
                            .Add(&H0, "No failover trigger has been detected.")
                            .Add(&H1, "A signal loss failover trigger was detected.")
                            .Add(&H2, "A link error threshold exceeded trigger was detected.")
                            .Add(&H3, "A command transport error threshold exceeded trigger was detected.")
                        End With
                        .Add(&HA101, subPage)
                        subPage = New PageData With {.PageCode = &HA102, .Name = "VU Fibre Channel port B failover status"}
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Active", .StartByte = 0, .BitOffset = 7, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Failover Trigger", .StartByte = 1, .BitOffset = 4, .TotalBits = 4, .Type = DataItem.DataType.Enum})
                        subPage.Items.Last.EnumTranslator = New SerializableDictionary(Of Long, String)
                        With subPage.Items.Last.EnumTranslator
                            .Add(&H0, "No failover trigger has been detected.")
                            .Add(&H1, "A signal loss failover trigger was detected.")
                            .Add(&H2, "A link error threshold exceeded trigger was detected.")
                            .Add(&H3, "A command transport error threshold exceeded trigger was detected.")
                        End With
                        .Add(&HA102, subPage)
                    End With
                Case DefaultPages.HPLTO6_TapeAlertResponseLogPage
                    pdata = New PageData With {.Name = "TapeAlert Response log page", .PageCode = &H12, .RawData = logdata}
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "Page Code",
                                    .StartByte = 0,
                                    .BitOffset = 0,
                                    .TotalBits = 8,
                                    .Type = DataItem.DataType.Binary})
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "Page Length",
                                    .StartByte = 2,
                                    .BitOffset = 0,
                                    .TotalBits = 16,
                                    .Type = DataItem.DataType.Int16})

                    For i As Integer = 0 To 7
                        For j As Integer = 0 To 7
                            Dim TAFValue As String = ""
                            If TapeAlertFlag.TryGetValue(i * 8 + j, TAFValue) Then TAFValue = $" {TAFValue}"
                            pdata.Items.Add(New DataItem With {
                                                    .Parent = pdata,
                                                    .Name = $"Flag {Hex(i * 8 + j).ToUpper().PadLeft(2, "0"c)}h{TAFValue}",
                                                    .StartByte = i + 8,
                                                    .BitOffset = CByte(j),
                                                    .TotalBits = 1,
                                                    .Type = DataItem.DataType.Boolean})
                            pdata.Items.Last.EnumTranslator = TapeAlertFlag
                        Next
                    Next
                Case DefaultPages.HPLTO6_RequestedRecoveryLogPage
                    pdata = New PageData With {.Name = "Requested Recovery log page", .PageCode = &H13, .RawData = logdata}
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "Page Code",
                                    .StartByte = 0,
                                    .BitOffset = 0,
                                    .TotalBits = 8,
                                    .Type = DataItem.DataType.Binary})
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "Page Length",
                                    .StartByte = 2,
                                    .BitOffset = 0,
                                    .TotalBits = 16,
                                    .Type = DataItem.DataType.Int16})
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "Recovery Procedure",
                                    .StartByte = 4,
                                    .BitOffset = 0,
                                    .TotalBits = 8,
                                    .Type = DataItem.DataType.Enum})
                    pdata.Items.Last.EnumTranslator = New SerializableDictionary(Of Long, String)
                    With pdata.Items.Last.EnumTranslator
                        .Add(&H0, "No recovery requested")
                        .Add(&HD, "Request creation of a DT device error log")
                        .Add(&HE, "Retrieve a DT device error log")
                        .Add(&HF, "Modify the configuration to allow microcode update")
                    End With
                Case DefaultPages.HPLTO6_DeviceStatisticsLogPage
                    pdata = New PageData With {.Name = "Device Statistics log page", .PageCode = &H14, .RawData = logdata}
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "DS",
                                    .StartByte = 0,
                                    .BitOffset = 0,
                                    .TotalBits = 1,
                                    .Type = DataItem.DataType.Boolean})
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "SPF",
                                    .StartByte = 0,
                                    .BitOffset = 1,
                                    .TotalBits = 1,
                                    .Type = DataItem.DataType.Boolean})
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "Page Code",
                                    .StartByte = 0,
                                    .BitOffset = 2,
                                    .TotalBits = 6,
                                    .Type = DataItem.DataType.Binary})
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "Subcode Page",
                                    .StartByte = 1,
                                    .BitOffset = 0,
                                    .TotalBits = 8,
                                    .Type = DataItem.DataType.Binary})
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "Page Length",
                                    .StartByte = 2,
                                    .BitOffset = 0,
                                    .TotalBits = 16,
                                    .Type = DataItem.DataType.Int16})
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "Device Statistics log parameter",
                                    .StartByte = 4,
                                    .BitOffset = 0,
                                    .TotalBits = 0,
                                    .DynamicParamCodeBitOffset = 0,
                                    .DynamicParamCodeStartByte = 0,
                                    .DynamicParamCodeTotalBits = 16,
                                    .DynamicParamLenBitOffset = 0,
                                    .DynamicParamLenStartByte = 3,
                                    .DynamicParamLenTotalBits = 8,
                                    .DynamicParamDataStartByte = 4,
                                    .EnumTranslator = New SerializableDictionary(Of Long, String),
                                    .DynamicParamType = New SerializableDictionary(Of Long, DataItem.DataType),
                                    .PageDataTemplate = New SerializableDictionary(Of Long, PageData),
                                    .Type = DataItem.DataType.DynamicPage})
                    With pdata.Items.Last.EnumTranslator
                        .Add(&H0, "Lifetime volume loads")
                        .Add(&H1, "Lifetime cleaning operations")
                        .Add(&H2, "Lifetime power-on hours")
                        .Add(&H3, "Lifetime media motion (head) hours")
                        .Add(&H4, "Lifetime meters of tape processed")
                        .Add(&H5, "Lifetime medium motion (head) hours when an incompatible volume was last loaded")
                        .Add(&H6, "Lifetime power-on hours when the last temperature condition occurred (TapeAlert code 24h)")
                        .Add(&H7, "Lifetime power-on hours when the last power consumption condition occurred (TapeAlert code 1Ch)")
                        .Add(&H8, "Medium motion (head) hours since the last successful cleaning operation")
                        .Add(&H9, "Medium motion (head) hours since the second to last successful cleaning operation")
                        .Add(&HA, "Medium motion (head) hours since the third to last successful cleaning operation")
                        .Add(&HB, "Lifetime power-on hours when the last operator-initiated forced reset or emergency eject occurred")
                        .Add(&HC, "Lifetime power cycles")
                        .Add(&HD, "Volume loads since last parameter reset")
                        .Add(&HE, "Hard write errors")
                        .Add(&HF, "Hard read errors")
                        .Add(&H10, "Duty cycle sample time (time in milliseconds since last hard reset)")
                        .Add(&H11, "Read duty cycle (percentage of duty cycle spent processing read-type commands)")
                        .Add(&H12, "Write duty cycle (percentage of duty cycle spent processing write-type commands)")
                        .Add(&H13, "Activity duty cycle (percentage of duty cycle spent processing commands that move the medium)")
                        .Add(&H14, "Volume not present duty cycle (percentage of duty cycle when there is no volume present)")
                        .Add(&H15, "Ready duty cycle (percentage of the duty cycle sample time when the drive is in the ready state)")
                        .Add(&H16, "MB transferred from the application client in the duty cycle sample time")
                        .Add(&H17, "MB transferred to the application client in the duty cycle sample time")
                        .Add(&H40, "Drive manufacturer’s serial number")
                        .Add(&H41, "Drive serial number")
                        .Add(&H80, "Medium removal prevented")
                        .Add(&H81, "Max recommended mechanism temperature exceeded")
                        .Add(&H1000, "Medium motion (head) hours for each medium type")
                    End With
                    With pdata.Items.Last.DynamicParamType
                        .Add(&H0, DataItem.DataType.Int64)
                        .Add(&H1, DataItem.DataType.Int64)
                        .Add(&H2, DataItem.DataType.Int64)
                        .Add(&H3, DataItem.DataType.Int64)
                        .Add(&H4, DataItem.DataType.Int64)
                        .Add(&H5, DataItem.DataType.Int64)
                        .Add(&H6, DataItem.DataType.Int64)
                        .Add(&H7, DataItem.DataType.Int64)
                        .Add(&H8, DataItem.DataType.Int64)
                        .Add(&H9, DataItem.DataType.Int64)
                        .Add(&HA, DataItem.DataType.Int64)
                        .Add(&HB, DataItem.DataType.Int64)
                        .Add(&HC, DataItem.DataType.Int64)
                        .Add(&HD, DataItem.DataType.Int64)
                        .Add(&HE, DataItem.DataType.Int64)
                        .Add(&HF, DataItem.DataType.Int64)
                        .Add(&H10, DataItem.DataType.Int64)
                        .Add(&H11, DataItem.DataType.Int64)
                        .Add(&H12, DataItem.DataType.Int64)
                        .Add(&H13, DataItem.DataType.Int64)
                        .Add(&H14, DataItem.DataType.Int64)
                        .Add(&H15, DataItem.DataType.Int64)
                        .Add(&H16, DataItem.DataType.Int64)
                        .Add(&H17, DataItem.DataType.Int64)
                        .Add(&H40, DataItem.DataType.Text)
                        .Add(&H41, DataItem.DataType.Text)
                        .Add(&H80, DataItem.DataType.Int64)
                        .Add(&H81, DataItem.DataType.Int64)
                        .Add(&H1000, DataItem.DataType.PageData)
                    End With

                    With pdata.Items.Last.PageDataTemplate
                        Dim subPage As PageData
                        subPage = New PageData With {.PageCode = 0, .Name = "Device statistics medium type log parameter"}
                        For i As Integer = 0 To 19
                            subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = $"Density Code", .StartByte = 2 + 8 * i, .BitOffset = 0, .TotalBits = 8, .Type = DataItem.DataType.Enum})
                            subPage.Items.Last.EnumTranslator = DensityCodeTranslator
                            subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = $"Medium Type", .StartByte = 3 + 8 * i, .BitOffset = 0, .TotalBits = 8, .Type = DataItem.DataType.Enum})
                            subPage.Items.Last.EnumTranslator = MediumTypeTranslator
                            subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = $"Medium Motion Hours", .StartByte = 4 + 8 * i, .BitOffset = 0, .TotalBits = 32, .Type = DataItem.DataType.Int32})
                        Next
                        .Add(&H1000, subPage)
                    End With
                Case DefaultPages.HPLTO6_ServiceBuffersInformationLogPage
                    pdata = New PageData With {.Name = "Service Buffers Information Log page", .PageCode = &H15, .RawData = logdata}
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "Page Code",
                                    .StartByte = 0,
                                    .BitOffset = 0,
                                    .TotalBits = 8,
                                    .Type = DataItem.DataType.Binary})
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "Page Length",
                                    .StartByte = 2,
                                    .BitOffset = 0,
                                    .TotalBits = 16,
                                    .Type = DataItem.DataType.Int16})
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "Service Buffers",
                                    .StartByte = 4,
                                    .BitOffset = 0,
                                    .TotalBits = 0,
                                    .DynamicParamCodeBitOffset = 0,
                                    .DynamicParamCodeStartByte = 0,
                                    .DynamicParamCodeTotalBits = 16,
                                    .DynamicParamLenBitOffset = 0,
                                    .DynamicParamLenStartByte = 3,
                                    .DynamicParamLenTotalBits = 8,
                                    .DynamicParamDataStartByte = 4,
                                    .EnumTranslator = New SerializableDictionary(Of Long, String),
                                    .DynamicParamType = New SerializableDictionary(Of Long, DataItem.DataType),
                                    .PageDataTemplate = New SerializableDictionary(Of Long, PageData),
                                    .Type = DataItem.DataType.DynamicPage})
                    With pdata.Items.Last.EnumTranslator
                        .Add(0, "DT Device Error log")
                        .Add(3, "Health and Error log")
                    End With
                    With pdata.Items.Last.DynamicParamType
                        .Add(&H0, DataItem.DataType.PageData)
                        .Add(&H3, DataItem.DataType.PageData)
                    End With
                    With pdata.Items.Last.PageDataTemplate
                        Dim subPage As PageData
                        subPage = New PageData With {.PageCode = 0, .Name = "DT Device Error Log service buffer"}
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Temporarily Unavailable", .StartByte = 1, .BitOffset = 3, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "DT Device Error Log Service Buffer", .StartByte = 4, .BitOffset = 0, .TotalBits = 160, .Type = DataItem.DataType.Text})
                        .Add(&H0, subPage)
                        subPage = New PageData With {.PageCode = 3, .Name = "Health and Error Log service buffer"}
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Temporarily Unavailable", .StartByte = 1, .BitOffset = 3, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Health & Error Log Service Buffer", .StartByte = 4, .BitOffset = 0, .TotalBits = 160, .Type = DataItem.DataType.Text})
                        .Add(&H3, subPage)
                    End With
                Case DefaultPages.HPLTO6_TapeDiagnosticLogPage
                    pdata = New PageData With {.Name = "Tape Diagnostic log page", .PageCode = &H16, .RawData = logdata}
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "Page Code",
                                    .StartByte = 0,
                                    .BitOffset = 0,
                                    .TotalBits = 8,
                                    .Type = DataItem.DataType.Binary})
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "Page Length",
                                    .StartByte = 2,
                                    .BitOffset = 0,
                                    .TotalBits = 16,
                                    .Type = DataItem.DataType.Int16})
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "Tape diagnostic data log parameters",
                                    .StartByte = 4,
                                    .BitOffset = 0,
                                    .TotalBits = 0,
                                    .DynamicParamCodeBitOffset = 0,
                                    .DynamicParamCodeStartByte = 0,
                                    .DynamicParamCodeTotalBits = 16,
                                    .DynamicParamLenBitOffset = 0,
                                    .DynamicParamLenStartByte = 3,
                                    .DynamicParamLenTotalBits = 8,
                                    .DynamicParamDataStartByte = 4,
                                    .EnumTranslator = New SerializableDictionary(Of Long, String),
                                    .DynamicParamType = New SerializableDictionary(Of Long, DataItem.DataType),
                                    .PageDataTemplate = New SerializableDictionary(Of Long, PageData),
                                    .Type = DataItem.DataType.DynamicPage})
                    For i As Integer = 0 To 59
                        With pdata.Items.Last.EnumTranslator
                            .Add(i, $"Parameter Code {Hex(i).ToUpper().PadLeft(4, "0"c)}h")
                        End With
                        With pdata.Items.Last.DynamicParamType
                            .Add(i, DataItem.DataType.PageData)
                        End With
                        With pdata.Items.Last.PageDataTemplate
                            Dim subPage As PageData
                            subPage = New PageData With {.PageCode = i, .Name = $"Parameter Code {Hex(i).ToUpper().PadLeft(4, "0"c)}h"}
                            subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Density Code", .StartByte = 2, .BitOffset = 0, .TotalBits = 8, .Type = DataItem.DataType.Byte})
                            subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Density Code Representation", .StartByte = 2, .BitOffset = 0, .TotalBits = 8, .Type = DataItem.DataType.Enum})
                            subPage.Items.Last.EnumTranslator = DensityCodeTranslator
                            subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Medium Type", .StartByte = 3, .BitOffset = 0, .TotalBits = 8, .Type = DataItem.DataType.Byte})
                            subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Medium Type Representation", .StartByte = 3, .BitOffset = 0, .TotalBits = 8, .Type = DataItem.DataType.Enum})
                            subPage.Items.Last.EnumTranslator = MediumTypeTranslator
                            subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Lifetime Medium Motion Hours", .StartByte = 4, .BitOffset = 0, .TotalBits = 32, .Type = DataItem.DataType.Int32})
                            subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Additional Sense Code", .StartByte = 10, .BitOffset = 0, .TotalBits = 8, .Type = DataItem.DataType.Binary})
                            subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Additional Sense Code Representation", .StartByte = 10, .BitOffset = 0, .TotalBits = 8, .Type = DataItem.DataType.Enum})
                            subPage.Items.Last.EnumTranslator = AdditionalSenseCodeTranslator
                            subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Additional Sense Code Qualifier", .StartByte = 11, .BitOffset = 0, .TotalBits = 8, .Type = DataItem.DataType.Binary})
                            subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Drive Error Code", .StartByte = 12, .BitOffset = 0, .TotalBits = 32, .Type = DataItem.DataType.Int32})
                            subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Product Revision Level", .StartByte = 16, .BitOffset = 0, .TotalBits = 32, .Type = DataItem.DataType.Int32})
                            subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Hours Since Last Clean", .StartByte = 20, .BitOffset = 0, .TotalBits = 32, .Type = DataItem.DataType.Int32})
                            subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Opcode", .StartByte = 24, .BitOffset = 0, .TotalBits = 8, .Type = DataItem.DataType.Byte})
                            subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Service Action", .StartByte = 25, .BitOffset = 3, .TotalBits = 5, .Type = DataItem.DataType.Byte})
                            subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Medium Identifier", .StartByte = 28, .BitOffset = 0, .TotalBits = 256, .Type = DataItem.DataType.RawData})
                            subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = "Medium Identifier", .StartByte = 62, .BitOffset = 0, .TotalBits = 48, .Type = DataItem.DataType.Int64})

                            .Add(i, subPage)
                        End With
                    Next
                Case DefaultPages.HPLTO6_VolumeStatisticsLogPage
                    pdata = New PageData With {.Name = "Volume Statistics Log page", .PageCode = &H17, .RawData = logdata}
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "Page Code",
                                    .StartByte = 0,
                                    .BitOffset = 0,
                                    .TotalBits = 8,
                                    .Type = DataItem.DataType.Binary})
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "Page Length",
                                    .StartByte = 2,
                                    .BitOffset = 0,
                                    .TotalBits = 16,
                                    .Type = DataItem.DataType.Int16})
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "Volume Statistics log parameters",
                                    .StartByte = 4,
                                    .BitOffset = 0,
                                    .TotalBits = 0,
                                    .DynamicParamCodeBitOffset = 0,
                                    .DynamicParamCodeStartByte = 0,
                                    .DynamicParamCodeTotalBits = 16,
                                    .DynamicParamLenBitOffset = 0,
                                    .DynamicParamLenStartByte = 3,
                                    .DynamicParamLenTotalBits = 8,
                                    .DynamicParamDataStartByte = 4,
                                    .EnumTranslator = New SerializableDictionary(Of Long, String),
                                    .DynamicParamType = New SerializableDictionary(Of Long, DataItem.DataType),
                                    .PageDataTemplate = New SerializableDictionary(Of Long, PageData),
                                    .Type = DataItem.DataType.DynamicPage})
                    With pdata.Items.Last.EnumTranslator
                        .Add(&H0, "Page valid")
                        .Add(&H1, "Thread count")
                        .Add(&H2, "Total data sets written")
                        .Add(&H3, "Total write retries")
                        .Add(&H4, "Total unrecovered write errors")
                        .Add(&H5, "Total suspended writes")
                        .Add(&H6, "Total fatal suspended writes")
                        .Add(&H7, "Total datasets read")
                        .Add(&H8, "Total read retries")
                        .Add(&H9, "Total unrecovered read errors")
                        .Add(&HC, "Last mount unrecovered write errors")
                        .Add(&HD, "Last mount unrecovered read errors")
                        .Add(&HE, "Last mount megabytes written")
                        .Add(&HF, "Last mount megabytes read")
                        .Add(&H10, "Lifetime megabytes written")
                        .Add(&H11, "Lifetime megabytes read")
                        .Add(&H12, "Last load write compression ratio")
                        .Add(&H13, "Last load read compression ratio")
                        .Add(&H14, "Medium mount time")
                        .Add(&H15, "Medium ready time")
                        .Add(&H16, "Total native capacity")
                        .Add(&H17, "Total used native capacity")
                        .Add(&H40, "Volume serial number")
                        .Add(&H41, "Tape lot identifier")
                        .Add(&H42, "Volume barcode")
                        .Add(&H43, "Volume manufacturer")
                        .Add(&H44, "Volume license code")
                        .Add(&H45, "Volume personality")
                        .Add(&H46, "Volume manufacture date")
                        .Add(&H80, "Write protect")
                        .Add(&H81, "Volume is WORM")
                        .Add(&H82, "Maximum recommended tape path temperature exceeded")
                        .Add(&H101, "Beginning of medium passes")
                        .Add(&H102, "Middle of medium passes")
                        .Add(&H200, "First encrypted logical object identifiers")
                        .Add(&H201, "First unencrypted logical object on the EOP side of the first encrypted logical object identifiers")
                        .Add(&H202, "Approximate native capacity of partitions")
                        .Add(&H203, "Approximate used native capacity of partitions")
                        .Add(&H300, "Mount history")
                        .Add(&HF000, "Version number (vendor-unique)")
                    End With
                    With pdata.Items.Last.DynamicParamType
                        .Add(&H0, DataItem.DataType.Int16)
                        .Add(&H1, DataItem.DataType.Int32)
                        .Add(&H2, DataItem.DataType.Int64)
                        .Add(&H3, DataItem.DataType.Int32)
                        .Add(&H4, DataItem.DataType.Int16)
                        .Add(&H5, DataItem.DataType.Int16)
                        .Add(&H6, DataItem.DataType.Int16)
                        .Add(&H7, DataItem.DataType.Int64)
                        .Add(&H8, DataItem.DataType.Int32)
                        .Add(&H9, DataItem.DataType.Int16)
                        .Add(&HC, DataItem.DataType.Int16)
                        .Add(&HD, DataItem.DataType.Int16)
                        .Add(&HE, DataItem.DataType.Int32)
                        .Add(&HF, DataItem.DataType.Int32)
                        .Add(&H10, DataItem.DataType.Int32)
                        .Add(&H11, DataItem.DataType.Int32)
                        .Add(&H12, DataItem.DataType.Int16)
                        .Add(&H13, DataItem.DataType.Int16)
                        .Add(&H14, DataItem.DataType.Int64)
                        .Add(&H15, DataItem.DataType.Int64)
                        .Add(&H16, DataItem.DataType.Int32)
                        .Add(&H17, DataItem.DataType.Int32)
                        .Add(&H40, DataItem.DataType.Text)
                        .Add(&H41, DataItem.DataType.Text)
                        .Add(&H42, DataItem.DataType.Text)
                        .Add(&H43, DataItem.DataType.Text)
                        .Add(&H44, DataItem.DataType.Text)
                        .Add(&H45, DataItem.DataType.Text)
                        .Add(&H46, DataItem.DataType.Text)
                        .Add(&H80, DataItem.DataType.Int16)
                        .Add(&H81, DataItem.DataType.Int16)
                        .Add(&H82, DataItem.DataType.Int16)
                        .Add(&H101, DataItem.DataType.Int32)
                        .Add(&H102, DataItem.DataType.Int32)
                        .Add(&H200, DataItem.DataType.PageData)
                        .Add(&H201, DataItem.DataType.PageData)
                        .Add(&H202, DataItem.DataType.PageData)
                        .Add(&H203, DataItem.DataType.PageData)
                        .Add(&H300, DataItem.DataType.PageData)
                        .Add(&HF000, DataItem.DataType.Int16)
                    End With
                    With pdata.Items.Last.PageDataTemplate
                        Dim subPage As PageData
                        subPage = New PageData With {.PageCode = &H200, .Name = "First encrypted logical object identifiers"}
                        For i As Integer = 0 To 7
                            subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = $"Partition Number", .StartByte = 2 + 12 * i, .BitOffset = 0, .TotalBits = 16, .Type = DataItem.DataType.Int16})
                            subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = $"Partition Record Data Counter", .StartByte = 4 + 12 * i, .BitOffset = 0, .TotalBits = 64, .Type = DataItem.DataType.Int64})
                        Next
                        .Add(&H200, subPage)
                        subPage = New PageData With {.PageCode = &H201, .Name = "First unencrypted logical object on the EOP side of the first encrypted logical object identifiers"}
                        For i As Integer = 0 To 7
                            subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = $"Partition Number", .StartByte = 2 + 12 * i, .BitOffset = 0, .TotalBits = 16, .Type = DataItem.DataType.Int16})
                            subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = $"Partition Record Data Counter", .StartByte = 4 + 12 * i, .BitOffset = 0, .TotalBits = 64, .Type = DataItem.DataType.Int64})
                        Next
                        .Add(&H201, subPage)
                        subPage = New PageData With {.PageCode = &H202, .Name = "Approximate native capacity of partitions"}
                        For i As Integer = 0 To 7
                            subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = $"Partition Number", .StartByte = 2 + 8 * i, .BitOffset = 0, .TotalBits = 16, .Type = DataItem.DataType.Int16})
                            subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = $"Partition Record Data Counter", .StartByte = 4 + 8 * i, .BitOffset = 0, .TotalBits = 32, .Type = DataItem.DataType.Int32})
                        Next
                        .Add(&H202, subPage)
                        subPage = New PageData With {.PageCode = &H203, .Name = "Approximate used native capacity of partitions"}
                        For i As Integer = 0 To 7
                            subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = $"Partition Number", .StartByte = 2 + 8 * i, .BitOffset = 0, .TotalBits = 16, .Type = DataItem.DataType.Int16})
                            subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = $"Partition Record Data Counter", .StartByte = 4 + 8 * i, .BitOffset = 0, .TotalBits = 32, .Type = DataItem.DataType.Int32})
                        Next
                        .Add(&H203, subPage)
                        subPage = New PageData With {.PageCode = &H300, .Name = "Mount history"}
                        For i As Integer = 0 To 3
                            subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = $"Mount History Index", .StartByte = 2 + &H2C * i, .BitOffset = 0, .TotalBits = 16, .Type = DataItem.DataType.Int16})
                            subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = $"Mount History Vendor ID", .StartByte = 4 + &H2C * i, .BitOffset = 0, .TotalBits = 64, .Type = DataItem.DataType.Text})
                            subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = $"Mount History Unit Serial Number", .StartByte = 12 + &H2C * i, .BitOffset = 0, .TotalBits = 256, .Type = DataItem.DataType.Text})
                        Next
                        .Add(&H300, subPage)
                    End With
                Case DefaultPages.HPLTO6_ProtocolSpecificPortLogPage
                    pdata = New PageData With {.Name = "Protocol-Specific Port Log page (SAS drives only)", .PageCode = &H18, .RawData = logdata}
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "Page Code",
                                    .StartByte = 0,
                                    .BitOffset = 0,
                                    .TotalBits = 8,
                                    .Type = DataItem.DataType.Binary})
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "Page Length",
                                    .StartByte = 2,
                                    .BitOffset = 0,
                                    .TotalBits = 16,
                                    .Type = DataItem.DataType.Int16})
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "Protocol-Specific Log Parameter",
                                    .StartByte = 4,
                                    .BitOffset = 0,
                                    .TotalBits = 0,
                                    .DynamicParamCodeBitOffset = 0,
                                    .DynamicParamCodeStartByte = 0,
                                    .DynamicParamCodeTotalBits = 16,
                                    .DynamicParamLenBitOffset = 0,
                                    .DynamicParamLenStartByte = 3,
                                    .DynamicParamLenTotalBits = 8,
                                    .DynamicParamDataStartByte = 4,
                                    .EnumTranslator = New SerializableDictionary(Of Long, String),
                                    .DynamicParamType = New SerializableDictionary(Of Long, DataItem.DataType),
                                    .PageDataTemplate = New SerializableDictionary(Of Long, PageData),
                                    .Type = DataItem.DataType.DynamicPage})
                    With pdata.Items.Last.EnumTranslator
                        .Add(0, "Protocol-Specific Log Parameter 0")
                        .Add(1, "Protocol-Specific Log Parameter 1")
                    End With
                    With pdata.Items.Last.DynamicParamType
                        .Add(0, DataItem.DataType.PageData)
                        .Add(1, DataItem.DataType.PageData)
                    End With
                    With pdata.Items.Last.PageDataTemplate
                        Dim subPage As PageData
                        subPage = New PageData With {.PageCode = 0, .Name = "Protocol-specific log parameters"}
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = $"Generation Code", .StartByte = 2, .BitOffset = 0, .TotalBits = 8, .Type = DataItem.DataType.Byte})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = $"PHY Identifier", .StartByte = 5, .BitOffset = 0, .TotalBits = 8, .Type = DataItem.DataType.Byte})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = $"Attached Device Type", .StartByte = 8, .BitOffset = 1, .TotalBits = 3, .Type = DataItem.DataType.Byte})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = $"Attached Reason", .StartByte = 8, .BitOffset = 4, .TotalBits = 4, .Type = DataItem.DataType.Byte})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = $"Negotiated Physical Link Rate", .StartByte = 9, .BitOffset = 4, .TotalBits = 4, .Type = DataItem.DataType.Byte})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = $"Attached SSP Initiator Port", .StartByte = 10, .BitOffset = 4, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = $"Attached STP Initiator Port", .StartByte = 10, .BitOffset = 5, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = $"Attached SMP Initiator Port", .StartByte = 10, .BitOffset = 6, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = $"Attached SSP Target Port", .StartByte = 11, .BitOffset = 4, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = $"Attached STP Target Port", .StartByte = 11, .BitOffset = 5, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = $"Attached SMP Target Port", .StartByte = 11, .BitOffset = 6, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = $"SAS Address", .StartByte = 12, .BitOffset = 0, .TotalBits = 64, .Type = DataItem.DataType.Binary})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = $"Attached SAS Address", .StartByte = 20, .BitOffset = 0, .TotalBits = 64, .Type = DataItem.DataType.Binary})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = $"Attached PHY Identifier", .StartByte = 28, .BitOffset = 0, .TotalBits = 8, .Type = DataItem.DataType.Byte})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = $"Invalid DWORD Count", .StartByte = 36, .BitOffset = 0, .TotalBits = 32, .Type = DataItem.DataType.Int32})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = $"Running Disparity Error Count", .StartByte = 40, .BitOffset = 0, .TotalBits = 32, .Type = DataItem.DataType.Int32})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = $"Loss of DWORD synchronization", .StartByte = 44, .BitOffset = 0, .TotalBits = 32, .Type = DataItem.DataType.Int32})
                        subPage.Items.Add(New DataItem With {.Parent = subPage, .Name = $"PHY Reset Problem Count", .StartByte = 48, .BitOffset = 0, .TotalBits = 32, .Type = DataItem.DataType.Int32})
                        .Add(0, subPage)
                        .Add(1, subPage)
                    End With
                Case DefaultPages.HPLTO6_DataCompressionLogPage
                    pdata = New PageData With {.Name = "Data Compression log page", .PageCode = &H1B, .RawData = logdata}
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "Page Code",
                                    .StartByte = 0,
                                    .BitOffset = 0,
                                    .TotalBits = 8,
                                    .Type = DataItem.DataType.Binary})
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "Page Length",
                                    .StartByte = 2,
                                    .BitOffset = 0,
                                    .TotalBits = 16,
                                    .Type = DataItem.DataType.Int16})
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "Data Compression Parameter",
                                    .StartByte = 4,
                                    .BitOffset = 0,
                                    .TotalBits = 0,
                                    .DynamicParamCodeBitOffset = 0,
                                    .DynamicParamCodeStartByte = 0,
                                    .DynamicParamCodeTotalBits = 16,
                                    .DynamicParamLenBitOffset = 0,
                                    .DynamicParamLenStartByte = 3,
                                    .DynamicParamLenTotalBits = 8,
                                    .DynamicParamDataStartByte = 4,
                                    .EnumTranslator = New SerializableDictionary(Of Long, String),
                                    .DynamicParamType = New SerializableDictionary(Of Long, DataItem.DataType),
                                    .Type = DataItem.DataType.DynamicPage})
                    With pdata.Items.Last.EnumTranslator
                        .Add(0, "Read compression ratio")
                        .Add(1, "Write compression ratio")
                        .Add(2, "Megabytes transferred to host")
                        .Add(3, "Bytes transferred to host")
                        .Add(4, "Megabytes read from tape")
                        .Add(5, "Bytes read from tape")
                        .Add(6, "Megabytes transferred from host")
                        .Add(7, "Bytes transferred from host")
                        .Add(8, "Megabytes written to tape")
                        .Add(9, "Bytes written to tape")
                        .Add(&H100, "Dara compression enabled")
                    End With
                    With pdata.Items.Last.DynamicParamType
                        .Add(0, DataItem.DataType.Int16)
                        .Add(1, DataItem.DataType.Int16)
                        .Add(2, DataItem.DataType.Int32)
                        .Add(3, DataItem.DataType.Int32)
                        .Add(4, DataItem.DataType.Int32)
                        .Add(5, DataItem.DataType.Int32)
                        .Add(6, DataItem.DataType.Int32)
                        .Add(7, DataItem.DataType.Int32)
                        .Add(8, DataItem.DataType.Int32)
                        .Add(9, DataItem.DataType.Int32)
                        .Add(&H100, DataItem.DataType.Boolean)
                    End With
                Case DefaultPages.HPLTO6_TapeAlertLogPage
                    pdata = New PageData With {.Name = "TapeAlert log page", .PageCode = &H2E, .RawData = logdata}
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "Page Code",
                                    .StartByte = 0,
                                    .BitOffset = 0,
                                    .TotalBits = 8,
                                    .Type = DataItem.DataType.Binary})
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "Page Length",
                                    .StartByte = 2,
                                    .BitOffset = 0,
                                    .TotalBits = 16,
                                    .Type = DataItem.DataType.Int16})
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "Data Compression Parameter",
                                    .StartByte = 4,
                                    .BitOffset = 0,
                                    .TotalBits = 0,
                                    .DynamicParamCodeBitOffset = 0,
                                    .DynamicParamCodeStartByte = 0,
                                    .DynamicParamCodeTotalBits = 16,
                                    .DynamicParamLenBitOffset = 0,
                                    .DynamicParamLenStartByte = 3,
                                    .DynamicParamLenTotalBits = 8,
                                    .DynamicParamDataStartByte = 4,
                                    .EnumTranslator = TapeAlertFlag,
                                    .DynamicParamType = New SerializableDictionary(Of Long, DataItem.DataType),
                                    .Type = DataItem.DataType.DynamicPage})
                    With pdata.Items.Last.DynamicParamType
                        .Add(1, DataItem.DataType.Boolean)
                        .Add(2, DataItem.DataType.Boolean)
                        .Add(3, DataItem.DataType.Boolean)
                        .Add(4, DataItem.DataType.Boolean)
                        .Add(5, DataItem.DataType.Boolean)
                        .Add(6, DataItem.DataType.Boolean)
                        .Add(7, DataItem.DataType.Boolean)
                        .Add(8, DataItem.DataType.Boolean)
                        .Add(9, DataItem.DataType.Boolean)
                        .Add(10, DataItem.DataType.Boolean)
                        .Add(11, DataItem.DataType.Boolean)
                        .Add(12, DataItem.DataType.Boolean)
                        .Add(13, DataItem.DataType.Boolean)
                        .Add(14, DataItem.DataType.Boolean)
                        .Add(15, DataItem.DataType.Boolean)
                        .Add(16, DataItem.DataType.Boolean)
                        .Add(17, DataItem.DataType.Boolean)
                        .Add(18, DataItem.DataType.Boolean)
                        .Add(19, DataItem.DataType.Boolean)
                        .Add(20, DataItem.DataType.Boolean)
                        .Add(21, DataItem.DataType.Boolean)
                        .Add(22, DataItem.DataType.Boolean)
                        .Add(23, DataItem.DataType.Boolean)
                        .Add(24, DataItem.DataType.Boolean)
                        .Add(25, DataItem.DataType.Boolean)
                        .Add(26, DataItem.DataType.Boolean)
                        .Add(27, DataItem.DataType.Boolean)
                        .Add(28, DataItem.DataType.Boolean)
                        .Add(29, DataItem.DataType.Boolean)
                        .Add(30, DataItem.DataType.Boolean)
                        .Add(31, DataItem.DataType.Boolean)
                        .Add(32, DataItem.DataType.Boolean)
                        .Add(33, DataItem.DataType.Boolean)
                        .Add(34, DataItem.DataType.Boolean)
                        .Add(35, DataItem.DataType.Boolean)
                        .Add(36, DataItem.DataType.Boolean)
                        .Add(37, DataItem.DataType.Boolean)
                        .Add(38, DataItem.DataType.Boolean)
                        .Add(39, DataItem.DataType.Boolean)
                        .Add(49, DataItem.DataType.Boolean)
                        .Add(50, DataItem.DataType.Boolean)
                        .Add(51, DataItem.DataType.Boolean)
                        .Add(52, DataItem.DataType.Boolean)
                        .Add(53, DataItem.DataType.Boolean)
                        .Add(54, DataItem.DataType.Boolean)
                        .Add(55, DataItem.DataType.Boolean)
                        .Add(56, DataItem.DataType.Boolean)
                        .Add(57, DataItem.DataType.Boolean)
                        .Add(58, DataItem.DataType.Boolean)
                        .Add(59, DataItem.DataType.Boolean)
                        .Add(60, DataItem.DataType.Boolean)
                        .Add(61, DataItem.DataType.Boolean)
                    End With
                Case DefaultPages.HPLTO6_TapeUsageLogPage
                    pdata = New PageData With {.Name = "Tape Usage log page", .PageCode = &H30, .RawData = logdata}
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "Page Code",
                                    .StartByte = 0,
                                    .BitOffset = 0,
                                    .TotalBits = 8,
                                    .Type = DataItem.DataType.Binary})
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "Page Length",
                                    .StartByte = 2,
                                    .BitOffset = 0,
                                    .TotalBits = 16,
                                    .Type = DataItem.DataType.Int16})
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "Tape Usage Log Parameter",
                                    .StartByte = 4,
                                    .BitOffset = 0,
                                    .TotalBits = 0,
                                    .DynamicParamCodeBitOffset = 0,
                                    .DynamicParamCodeStartByte = 0,
                                    .DynamicParamCodeTotalBits = 16,
                                    .DynamicParamLenBitOffset = 0,
                                    .DynamicParamLenStartByte = 3,
                                    .DynamicParamLenTotalBits = 8,
                                    .DynamicParamDataStartByte = 4,
                                    .EnumTranslator = New SerializableDictionary(Of Long, String),
                                    .DynamicParamType = New SerializableDictionary(Of Long, DataItem.DataType),
                                    .Type = DataItem.DataType.DynamicPage})
                    With pdata.Items.Last.EnumTranslator
                        .Add(1, "Thread Count")
                        .Add(2, "Total Data Sets Written")
                        .Add(3, "Total Write Retries")
                        .Add(4, "Total Unrecovered Write Errors")
                        .Add(5, "Total Suspended Writes")
                        .Add(6, "Total Fatal Suspended Writes")
                        .Add(7, "Total Data Sets Read")
                        .Add(8, "Total Read Retries")
                        .Add(9, "Total Unrecovered Read Errors")
                    End With
                    With pdata.Items.Last.DynamicParamType
                        .Add(1, DataItem.DataType.Int32)
                        .Add(2, DataItem.DataType.Int64)
                        .Add(3, DataItem.DataType.Int32)
                        .Add(4, DataItem.DataType.Int16)
                        .Add(5, DataItem.DataType.Int16)
                        .Add(6, DataItem.DataType.Int16)
                        .Add(7, DataItem.DataType.Int64)
                        .Add(8, DataItem.DataType.Int32)
                        .Add(9, DataItem.DataType.Int16)
                    End With
                Case DefaultPages.HPLTO6_TapeCapacityLogPage
                    pdata = New PageData With {.Name = "Tape Capacity log page", .PageCode = &H31, .RawData = logdata}
                    pdata.Items.Add(New DataItem With {
                        .Parent = pdata,
                        .Name = "Page Code",
                        .StartByte = 0,
                        .BitOffset = 0,
                        .TotalBits = 8,
                        .Type = DataItem.DataType.Binary})
                    pdata.Items.Add(New DataItem With {
                        .Parent = pdata,
                        .Name = "Page Length",
                        .StartByte = 2,
                        .BitOffset = 0,
                        .TotalBits = 16,
                        .Type = DataItem.DataType.Int16})
                    pdata.Items.Add(New DataItem With {
                        .Parent = pdata,
                        .Name = "Tape Capacity Log Parameter",
                        .StartByte = 4,
                        .BitOffset = 0,
                        .TotalBits = 0,
                        .DynamicParamCodeBitOffset = 0,
                        .DynamicParamCodeStartByte = 0,
                        .DynamicParamCodeTotalBits = 16,
                        .DynamicParamLenBitOffset = 0,
                        .DynamicParamLenStartByte = 3,
                        .DynamicParamLenTotalBits = 8,
                        .DynamicParamDataStartByte = 4,
                        .EnumTranslator = New SerializableDictionary(Of Long, String),
                        .DynamicParamType = New SerializableDictionary(Of Long, DataItem.DataType),
                        .Type = DataItem.DataType.DynamicPage})
                    With pdata.Items.Last.EnumTranslator
                        .Add(1, "Partition 0 Remaining Capacity")
                        .Add(2, "Partition 1 Remaining Capacity")
                        .Add(3, "Partition 0 Maximum Capacity")
                        .Add(4, "Partition 1 Maximum Capacity")
                        .Add(5, "Partition 2 Remaining Capacity")
                        .Add(6, "Partition 3 Remaining Capacity")
                        .Add(7, "Partition 2 Maximum Capacity")
                        .Add(8, "Partition 3 Maximum Capacity")
                        .Add(9, "Partition 4 Remaining Capacity")
                        .Add(&HA, "Partition 5 Remaining Capacity")
                        .Add(&HB, "Partition 4 Maximum Capacity")
                        .Add(&HC, "Partition 5 Maximum Capacity")
                        .Add(&HD, "Partition 6 Remaining Capacity")
                        .Add(&HE, "Partition 7 Remaining Capacity")
                        .Add(&HF, "Partition 6 Maximum Capacity")
                        .Add(&H10, "Partition 7 Maximum Capacity")
                        .Add(&H11, "Partition 8 Remaining Capacity")
                        .Add(&H12, "Partition 9 Remaining Capacity")
                        .Add(&H13, "Partition 8 Maximum Capacity")
                        .Add(&H14, "Partition 9 Maximum Capacity")
                    End With
                    With pdata.Items.Last.DynamicParamType
                        .Add(1, DataItem.DataType.Int32)
                        .Add(2, DataItem.DataType.Int32)
                        .Add(3, DataItem.DataType.Int32)
                        .Add(4, DataItem.DataType.Int32)
                        .Add(5, DataItem.DataType.Int32)
                        .Add(6, DataItem.DataType.Int32)
                        .Add(7, DataItem.DataType.Int32)
                        .Add(8, DataItem.DataType.Int32)
                        .Add(9, DataItem.DataType.Int32)
                        .Add(&HA, DataItem.DataType.Int32)
                        .Add(&HB, DataItem.DataType.Int32)
                        .Add(&HC, DataItem.DataType.Int32)
                        .Add(&HD, DataItem.DataType.Int32)
                        .Add(&HE, DataItem.DataType.Int32)
                        .Add(&HF, DataItem.DataType.Int32)
                        .Add(&H10, DataItem.DataType.Int32)
                        .Add(&H11, DataItem.DataType.Int32)
                        .Add(&H12, DataItem.DataType.Int32)
                        .Add(&H13, DataItem.DataType.Int32)
                        .Add(&H14, DataItem.DataType.Int32)
                    End With
                Case DefaultPages.HPLTO6_DataCompressionHPLogPage
                    pdata = New PageData With {.Name = "Data Compression (HP-only) log page", .PageCode = &H32, .RawData = logdata}
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "Page Code",
                                    .StartByte = 0,
                                    .BitOffset = 0,
                                    .TotalBits = 8,
                                    .Type = DataItem.DataType.Binary})
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "Page Length",
                                    .StartByte = 2,
                                    .BitOffset = 0,
                                    .TotalBits = 16,
                                    .Type = DataItem.DataType.Int16})
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "Data Compression Parameter",
                                    .StartByte = 4,
                                    .BitOffset = 0,
                                    .TotalBits = 0,
                                    .DynamicParamCodeBitOffset = 0,
                                    .DynamicParamCodeStartByte = 0,
                                    .DynamicParamCodeTotalBits = 16,
                                    .DynamicParamLenBitOffset = 0,
                                    .DynamicParamLenStartByte = 3,
                                    .DynamicParamLenTotalBits = 8,
                                    .DynamicParamDataStartByte = 4,
                                    .EnumTranslator = New SerializableDictionary(Of Long, String),
                                    .DynamicParamType = New SerializableDictionary(Of Long, DataItem.DataType),
                                    .Type = DataItem.DataType.DynamicPage})
                    With pdata.Items.Last.EnumTranslator
                        .Add(0, "Read compression ratio x100")
                        .Add(1, "Write compression ratio x100")
                        .Add(2, "Megabytes transferred to host")
                        .Add(3, "Bytes transferred to host")
                        .Add(4, "Megabytes read from tape")
                        .Add(5, "Bytes read from tape")
                        .Add(6, "Megabytes transferred from host")
                        .Add(7, "Bytes transferred from host")
                        .Add(8, "Megabytes written to tape")
                        .Add(9, "Bytes written to tape")
                    End With
                    With pdata.Items.Last.DynamicParamType
                        .Add(0, DataItem.DataType.Int16)
                        .Add(1, DataItem.DataType.Int16)
                        .Add(2, DataItem.DataType.Int32)
                        .Add(3, DataItem.DataType.Int32)
                        .Add(4, DataItem.DataType.Int32)
                        .Add(5, DataItem.DataType.Int32)
                        .Add(6, DataItem.DataType.Int32)
                        .Add(7, DataItem.DataType.Int32)
                        .Add(8, DataItem.DataType.Int32)
                        .Add(9, DataItem.DataType.Int32)
                    End With
                Case DefaultPages.HPLTO6_DeviceWellnessLogPage
                    pdata = New PageData With {.Name = "Device Wellness Log page", .PageCode = &H33, .RawData = logdata}
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "Page Code",
                                    .StartByte = 0,
                                    .BitOffset = 0,
                                    .TotalBits = 8,
                                    .Type = DataItem.DataType.Binary})
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "Page Length",
                                    .StartByte = 2,
                                    .BitOffset = 0,
                                    .TotalBits = 16,
                                    .Type = DataItem.DataType.Int16})
                    For i As Integer = 0 To 15
                        pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = $"Parameter Code {i}",
                                    .StartByte = 4 + i * 16,
                                    .BitOffset = 0,
                                    .TotalBits = 16,
                                    .Type = DataItem.DataType.Int16})
                        pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = $"Time Stamp",
                                    .StartByte = 8 + i * 16,
                                    .BitOffset = 0,
                                    .TotalBits = 32,
                                    .Type = DataItem.DataType.Int32})
                        pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = $"Media Signature",
                                    .StartByte = 12 + i * 16,
                                    .BitOffset = 0,
                                    .TotalBits = 32,
                                    .Type = DataItem.DataType.Int32})
                        pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = $"Sense Key",
                                    .StartByte = 16 + i * 16,
                                    .BitOffset = 0,
                                    .TotalBits = 8,
                                    .EnumTranslator = SenseCodeTranslator,
                                    .Type = DataItem.DataType.Enum})
                        pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = $"Additional Sense Code",
                                    .StartByte = 17 + i * 16,
                                    .BitOffset = 0,
                                    .TotalBits = 8,
                                    .EnumTranslator = AdditionalSenseCodeTranslator,
                                    .Type = DataItem.DataType.Enum})
                        pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = $"Additional Sense Qualifier",
                                    .StartByte = 18 + i * 16,
                                    .BitOffset = 0,
                                    .TotalBits = 8,
                                    .Type = DataItem.DataType.Byte})

                        pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = $"Additional Error Information",
                                    .StartByte = 19 + i * 16,
                                    .BitOffset = 0,
                                    .TotalBits = 8,
                                    .Type = DataItem.DataType.Byte})
                    Next
                Case DefaultPages.HPLTO6_PerformanceDataLogPage
                    pdata = New PageData With {.Name = "Performance Data log page", .PageCode = &H34, .RawData = logdata}
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "Page Code",
                                    .StartByte = 0,
                                    .BitOffset = 0,
                                    .TotalBits = 8,
                                    .Type = DataItem.DataType.Binary})
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "Page Length",
                                    .StartByte = 2,
                                    .BitOffset = 0,
                                    .TotalBits = 16,
                                    .Type = DataItem.DataType.Int16})
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "Performance Data Log Parameter",
                                    .StartByte = 4,
                                    .BitOffset = 0,
                                    .TotalBits = 0,
                                    .DynamicParamCodeBitOffset = 0,
                                    .DynamicParamCodeStartByte = 0,
                                    .DynamicParamCodeTotalBits = 16,
                                    .DynamicParamLenBitOffset = 0,
                                    .DynamicParamLenStartByte = 3,
                                    .DynamicParamLenTotalBits = 8,
                                    .DynamicParamDataStartByte = 4,
                                    .EnumTranslator = New SerializableDictionary(Of Long, String),
                                    .DynamicParamType = New SerializableDictionary(Of Long, DataItem.DataType),
                                    .Type = DataItem.DataType.DynamicPage})
                    With pdata.Items.Last.EnumTranslator
                        .Add(0, "Repositions per 100 MB")
                        .Add(1, "Data rate into buffer")
                        .Add(2, "Maximum data rate")
                        .Add(3, "Current data rate")
                        .Add(4, "Native data rate")
                    End With
                    With pdata.Items.Last.DynamicParamType
                        .Add(0, DataItem.DataType.Int16)
                        .Add(1, DataItem.DataType.Int16)
                        .Add(2, DataItem.DataType.Int16)
                        .Add(3, DataItem.DataType.Int16)
                        .Add(4, DataItem.DataType.Int16)
                    End With
                Case DefaultPages.HPLTO6_DTDeviceErrorLogPage
                    pdata = New PageData With {.Name = "DT Device Error log page", .PageCode = &H35, .RawData = logdata}
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "Page Code",
                                    .StartByte = 0,
                                    .BitOffset = 0,
                                    .TotalBits = 8,
                                    .Type = DataItem.DataType.Binary})
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "Page Length",
                                    .StartByte = 2,
                                    .BitOffset = 0,
                                    .TotalBits = 16,
                                    .Type = DataItem.DataType.Int16})
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "DT Device Error Log Parameters",
                                    .StartByte = 4,
                                    .BitOffset = 0,
                                    .TotalBits = 0,
                                    .DynamicParamCodeBitOffset = 0,
                                    .DynamicParamCodeStartByte = 0,
                                    .DynamicParamCodeTotalBits = 16,
                                    .DynamicParamLenBitOffset = 0,
                                    .DynamicParamLenStartByte = 3,
                                    .DynamicParamLenTotalBits = 8,
                                    .DynamicParamDataStartByte = 4,
                                    .EnumTranslator = New SerializableDictionary(Of Long, String),
                                    .DynamicParamType = New SerializableDictionary(Of Long, DataItem.DataType),
                                    .PageDataTemplate = New SerializableDictionary(Of Long, PageData),
                                    .Type = DataItem.DataType.DynamicPage})
                    With pdata.Items.Last.EnumTranslator
                        .Add(0, "Hardware Error data")
                        .Add(1, "Media Error data")
                    End With
                    With pdata.Items.Last.DynamicParamType
                        .Add(0, DataItem.DataType.PageData)
                        .Add(1, DataItem.DataType.RawData)
                    End With
                    With pdata.Items.Last.PageDataTemplate
                        Dim subpage As PageData
                        subpage = New PageData With {.PageCode = 0, .Name = "Hardware Error data log parameter"}
                        subpage.Items.Add(New DataItem With {.Parent = subpage, .Name = "Sense Key", .StartByte = 0, .BitOffset = 4, .TotalBits = 4, .Type = DataItem.DataType.Enum})
                        subpage.Items.Last.EnumTranslator = SenseCodeTranslator
                        subpage.Items.Add(New DataItem With {.Parent = subpage, .Name = "Additional Sense Code", .StartByte = 1, .BitOffset = 0, .TotalBits = 8, .Type = DataItem.DataType.Enum})
                        subpage.Items.Last.EnumTranslator = AdditionalSenseCodeTranslator
                        subpage.Items.Add(New DataItem With {.Parent = subpage, .Name = "Additional Sense Code Qualifier", .StartByte = 2, .BitOffset = 0, .TotalBits = 8, .Type = DataItem.DataType.Byte})
                        subpage.Items.Add(New DataItem With {.Parent = subpage, .Name = "Hardware Error", .StartByte = 3, .BitOffset = 0, .TotalBits = 16, .Type = DataItem.DataType.Int16})
                        subpage.Items.Add(New DataItem With {.Parent = subpage, .Name = "Host Identification", .StartByte = 5, .BitOffset = 0, .TotalBits = 64, .Type = DataItem.DataType.Binary})
                        subpage.Items.Add(New DataItem With {.Parent = subpage, .Name = "Power-on Count", .StartByte = 13, .BitOffset = 0, .TotalBits = 16, .Type = DataItem.DataType.Int16})
                        subpage.Items.Add(New DataItem With {.Parent = subpage, .Name = "Power-on Time of Error (seconds)", .StartByte = 15, .BitOffset = 0, .TotalBits = 32, .Type = DataItem.DataType.Int32})
                        subpage.Items.Add(New DataItem With {.Parent = subpage, .Name = "Library Time of Error Hour", .StartByte = 19, .BitOffset = 0, .TotalBits = 8, .Type = DataItem.DataType.Byte})
                        subpage.Items.Add(New DataItem With {.Parent = subpage, .Name = "Library Time of Error Minutes", .StartByte = 20, .BitOffset = 0, .TotalBits = 8, .Type = DataItem.DataType.Byte})
                        subpage.Items.Add(New DataItem With {.Parent = subpage, .Name = "Library Time of Error Seconds", .StartByte = 21, .BitOffset = 0, .TotalBits = 8, .Type = DataItem.DataType.Byte})

                        .Add(0, subpage)
                    End With
                Case DefaultPages.HPLTO6_DeviceStatusLogPage
                    pdata = New PageData With {.Name = "Device Status log page", .PageCode = &H3E, .RawData = logdata}
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "Page Code",
                                    .StartByte = 0,
                                    .BitOffset = 0,
                                    .TotalBits = 8,
                                    .Type = DataItem.DataType.Binary})
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "Page Length",
                                    .StartByte = 2,
                                    .BitOffset = 0,
                                    .TotalBits = 16,
                                    .Type = DataItem.DataType.Int16})
                    pdata.Items.Add(New DataItem With {
                                    .Parent = pdata,
                                    .Name = "DT Device Status Log Parameters",
                                    .StartByte = 4,
                                    .BitOffset = 0,
                                    .TotalBits = 0,
                                    .DynamicParamCodeBitOffset = 0,
                                    .DynamicParamCodeStartByte = 0,
                                    .DynamicParamCodeTotalBits = 16,
                                    .DynamicParamLenBitOffset = 0,
                                    .DynamicParamLenStartByte = 3,
                                    .DynamicParamLenTotalBits = 8,
                                    .DynamicParamDataStartByte = 4,
                                    .EnumTranslator = New SerializableDictionary(Of Long, String),
                                    .DynamicParamType = New SerializableDictionary(Of Long, DataItem.DataType),
                                    .PageDataTemplate = New SerializableDictionary(Of Long, PageData),
                                    .Type = DataItem.DataType.DynamicPage})
                    With pdata.Items.Last.EnumTranslator
                        .Add(0, "Device Type")
                        .Add(1, "Device Status Bits")
                        .Add(2, "Total Number of Loads")
                        .Add(3, "Cleaning Cartridge Status")
                        .Add(4, "Product Number")
                    End With
                    With pdata.Items.Last.DynamicParamType
                        .Add(0, DataItem.DataType.RawData)
                        .Add(1, DataItem.DataType.PageData)
                        .Add(2, DataItem.DataType.Int32)
                        .Add(3, DataItem.DataType.Int32)
                        .Add(4, DataItem.DataType.PageData)
                    End With
                    With pdata.Items.Last.PageDataTemplate

                        Dim subpage As PageData
                        subpage = New PageData With {.PageCode = 1, .Name = "Device Status Bits"}
                        subpage.Items.Add(New DataItem With {.Parent = subpage, .Name = "Cleaning Required flag", .StartByte = 0, .BitOffset = 5, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subpage.Items.Add(New DataItem With {.Parent = subpage, .Name = "Cleaning Requested flag", .StartByte = 0, .BitOffset = 6, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subpage.Items.Add(New DataItem With {.Parent = subpage, .Name = "Exhausted Cleaning Tape flag", .StartByte = 0, .BitOffset = 7, .TotalBits = 1, .Type = DataItem.DataType.Boolean})
                        subpage.Items.Add(New DataItem With {.Parent = subpage, .Name = "Temperature", .StartByte = 1, .BitOffset = 4, .TotalBits = 2, .Type = DataItem.DataType.Enum})
                        subpage.Items.Last.EnumTranslator = New SerializableDictionary(Of Long, String)
                        With subpage.Items.Last.EnumTranslator
                            .Add(0, "Field not supported")
                            .Add(1, "Temperature OK")
                            .Add(2, "Temperature degraded")
                            .Add(3, "Temperature failed")
                        End With
                        subpage.Items.Add(New DataItem With {.Parent = subpage, .Name = "Device Status", .StartByte = 1, .BitOffset = 6, .TotalBits = 2, .Type = DataItem.DataType.Enum})
                        subpage.Items.Last.EnumTranslator = New SerializableDictionary(Of Long, String)
                        With subpage.Items.Last.EnumTranslator
                            .Add(0, "Field not supported")
                            .Add(1, "Device status OK")
                            .Add(2, "Device status degraded")
                            .Add(3, "Device status failed")
                        End With
                        subpage.Items.Add(New DataItem With {.Parent = subpage, .Name = "Medium Status", .StartByte = 2, .BitOffset = 6, .TotalBits = 2, .Type = DataItem.DataType.Enum})
                        subpage.Items.Last.EnumTranslator = New SerializableDictionary(Of Long, String)
                        With subpage.Items.Last.EnumTranslator
                            .Add(0, "Field not supported")
                            .Add(1, "Medium status OK")
                            .Add(2, "Medium status degraded")
                            .Add(3, "Medium status failed")
                        End With
                        .Add(1, subpage)
                        subpage = New PageData With {.PageCode = 4, .Name = "Product Number"}
                        subpage.Items.Add(New DataItem With {.Parent = subpage, .Name = "Product Number", .StartByte = 0, .BitOffset = 0, .TotalBits = 32, .Type = DataItem.DataType.Enum})
                        subpage.Items.Last.EnumTranslator = New SerializableDictionary(Of Long, String)
                        With subpage.Items.Last.EnumTranslator
                            .Add(&H109022C, "LTO-6 full-height FC standalone")
                            .Add(&H109022D, "LTO-6 full-height FC automation")
                            .Add(&H109022E, "LTO-6 full-height SAS standalone")
                            .Add(&H109022F, "LTO-6 full-height SAS automation")
                            .Add(&H1090230, "LTO-6 half-height FC standalone")
                            .Add(&H1090231, "LTO-6 half-height FC automation")
                            .Add(&H1090232, "LTO-6 half-height SAS standalone")
                            .Add(&H1090233, "LTO-6 half-height SAS automation")
                        End With
                        .Add(4, subpage)
                    End With
                Case Else
                    pdata = Nothing
            End Select
            Return pdata
        End Function
        Public Shared Function GetAllPagesFromDrive(handle As IntPtr) As List(Of PageData)
            Dim result As New List(Of PageData)
            For Each pagecode As Byte In [Enum].GetValues(GetType(DefaultPages))
                Dim logdata As Byte() = LogSense(handle, pagecode, 0)
                result.Add(CreateDefault(CType(pagecode, DefaultPages), logdata))
            Next
            Return result
        End Function
    End Class
    Public Shared Function ReduceDataUnit(KBytes As Int64) As String
        Dim Result As Decimal = KBytes * 1000
        Dim ResultUnit As Integer = -6
        While Result >= 1000
            If My.Settings.Application_UseDecimalUnit Then
                Result /= 1000
            Else
                Result /= 1024
            End If
            ResultUnit += 3
        End While
        Dim ResultString As String = CStr(Math.Round(Result, 2))
        If My.Settings.Application_UseDecimalUnit Then
            Select Case ResultUnit
                Case 0
                    Return ResultString & " MB"
                Case 3
                    Return ResultString & " GB"
                Case 6
                    Return ResultString & " TB"
                Case 9
                    Return ResultString & " PB"
                Case 12
                    Return ResultString & " EB"
                Case 15
                    Return ResultString & " ZB"
                Case 18
                    Return ResultString & " YB"
                Case Else
                    Return ResultString & " << " & ResultUnit & "MB"
            End Select
        Else
            Select Case ResultUnit
                Case 0
                    Return ResultString & " MiB"
                Case 3
                    Return ResultString & " GiB"
                Case 6
                    Return ResultString & " TiB"
                Case 9
                    Return ResultString & " PiB"
                Case 12
                    Return ResultString & " EiB"
                Case 15
                    Return ResultString & " ZiB"
                Case 18
                    Return ResultString & " YiB"
                Case Else
                    Return ResultString & " << " & ResultUnit & "MiB"
            End Select
        End If
    End Function

    Public Shared Property TagDictionary As SerializableDictionary(Of String, String) = New SerializableDictionary(Of String, String)
    Public Property BlockLenLimit As Integer
        Get
            Return GlobalBlockLimit
        End Get
        Set(value As Integer)
            GlobalBlockLimit = value
        End Set
    End Property
    Public Property DriveAlias As SerializableDictionary(Of String, String)
        Get
            Return TagDictionary
        End Get
        Set(value As SerializableDictionary(Of String, String))
            TagDictionary = value
        End Set
    End Property
    Public Function GetSerializedText() As String
        Dim writer As New XmlSerializer(GetType(TapeUtils))
        Dim sb As New StringBuilder
        Dim t As New StringWriter(sb)
        writer.Serialize(t, Me)
        Return sb.ToString()
    End Function
    Public Shared Function FromFile(fileName As String) As TapeUtils
        If Not File.Exists(fileName) Then Return Nothing
        Return FromXML(File.ReadAllText(fileName))
    End Function
    Public Shared Function FromXML(s As String) As TapeUtils
        Dim reader As New XmlSerializer(GetType(TapeUtils))
        Dim t As TextReader = New StringReader(s)
        Return CType(reader.Deserialize(t), TapeUtils)
    End Function

End Class
