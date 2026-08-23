Imports System.Runtime.InteropServices
Imports System.Threading
Imports ISCSI.Server
Imports SCSI
Imports Serilog
Imports Serilog.Context
Public Class iSCSIService
    Public driveHandle As IntPtr
    Public BlockSize As Integer = 524288
    Public ExtraPartitionCount As Integer = 1
    Public port As UShort = 3261
    Public Event LogPrint(s As String)
    Public svc As ISCSIServer
    Public target As ISCSITarget
    Public Property LogCommand As Boolean = False

    Public Class SCSIDirectInterface
        Implements SCSITargetInterface
        Public driveHandle As IntPtr
        Public Event OnStandardInquiry As EventHandler(Of StandardInquiryEventArgs) Implements SCSITargetInterface.OnStandardInquiry
        Public Event OnUnitSerialNumberInquiry As EventHandler(Of UnitSerialNumberInquiryEventArgs) Implements SCSITargetInterface.OnUnitSerialNumberInquiry
        Public Event OnDeviceIdentificationInquiry As EventHandler(Of DeviceIdentificationInquiryEventArgs) Implements SCSITargetInterface.OnDeviceIdentificationInquiry
        Public QueueTaskProcessor As Task
        Private Shared _DataDir As Dictionary(Of Byte(), Byte)
        Public Property LogCommand As Boolean
        Public Shared ReadOnly Property DataDir As Dictionary(Of Byte(), Byte)
            Get
                If _DataDir Is Nothing Then
                    _DataDir = New Dictionary(Of Byte(), Byte)
                    _DataDir.Add({&H0}, 1)
                    _DataDir.Add({&H1}, 1)
                    _DataDir.Add({&H3}, 1)
                    _DataDir.Add({&H4}, 1)
                    _DataDir.Add({&H5}, 1)
                    _DataDir.Add({&H8}, 1)
                    _DataDir.Add({&HA}, 0)
                    _DataDir.Add({&HB}, 1)
                    _DataDir.Add({&H10}, 1)
                    _DataDir.Add({&H11}, 1)
                    _DataDir.Add({&H12}, 1)
                    _DataDir.Add({&H13}, 1)
                    _DataDir.Add({&H15}, 0)
                    _DataDir.Add({&H16}, 1)
                    _DataDir.Add({&H17}, 1)
                    _DataDir.Add({&H19}, 1)
                    _DataDir.Add({&H1A}, 1)
                    _DataDir.Add({&H1B}, 1)
                    _DataDir.Add({&H1C}, 1)
                    _DataDir.Add({&H1D}, 0)
                    _DataDir.Add({&H1E}, 1)
                    _DataDir.Add({&H25}, 1)
                    _DataDir.Add({&H28}, 1)
                    _DataDir.Add({&H2B}, 1)
                    _DataDir.Add({&H34}, 1)
                    _DataDir.Add({&H3B}, 0)
                    _DataDir.Add({&H3C}, 1)
                    _DataDir.Add({&H43}, 1)
                    _DataDir.Add({&H44}, 1)
                    _DataDir.Add({&H4C}, 0)
                    _DataDir.Add({&H4D}, 1)
                    _DataDir.Add({&H55}, 0)
                    _DataDir.Add({&H56}, 1)
                    _DataDir.Add({&H57}, 1)
                    _DataDir.Add({&H5A}, 1)
                    _DataDir.Add({&H5E}, 1)
                    _DataDir.Add({&H5F}, 0)
                    _DataDir.Add({&H8C}, 1)
                    _DataDir.Add({&H8D}, 0)
                    _DataDir.Add({&H91}, 1)
                    _DataDir.Add({&H92}, 1)
                    _DataDir.Add({&HA0}, 1)
                    _DataDir.Add({&HA2}, 1)
                    _DataDir.Add({&HA3, &H5}, 1)
                    _DataDir.Add({&HA3, &HA}, 1)
                    _DataDir.Add({&HA3, &HC}, 1)
                    _DataDir.Add({&HA3, &HD}, 1)
                    _DataDir.Add({&HA3, &HF}, 1)
                    _DataDir.Add({&HA3, &H1F, &H0}, 0)
                    _DataDir.Add({&HA3, &H1F, &H1}, 1)
                    _DataDir.Add({&HA3, &H1F, &H5}, 1)
                    _DataDir.Add({&HA3, &H1F, &H7}, 1)
                    _DataDir.Add({&HA3, &H1F, &H8}, 1)
                    _DataDir.Add({&HA3, &H1F, &H9}, 1)
                    _DataDir.Add({&HA3, &H1F, &HA}, 1)
                    _DataDir.Add({&HA3, &H1F, &HB}, 1)
                    _DataDir.Add({&HA3, &H1F, &HD}, 1)
                    _DataDir.Add({&HA3, &H1F, &HE}, 1)
                    _DataDir.Add({&HA3, &H1F, &H10}, 1)
                    _DataDir.Add({&HA3, &H1F, &H12}, 1)
                    _DataDir.Add({&HA3, &H1F, &H15}, 1)
                    _DataDir.Add({&HA3, &H1F, &H18}, 1)
                    _DataDir.Add({&HA4, &H1F, &H5}, 1)
                    _DataDir.Add({&HA4, &H1F, &H6}, 1)
                    _DataDir.Add({&HA4, &H1F, &H7}, 1)
                    _DataDir.Add({&HA4, &H1F, &HC}, 1)
                    _DataDir.Add({&HA4, &H1F, &H14}, 1)
                    _DataDir.Add({&HA4, &H6}, 0)
                    _DataDir.Add({&HA4, &H1F, &HA}, 0)
                    _DataDir.Add({&HA4, &H1F, &HB}, 0)
                    _DataDir.Add({&HA4, &H1F, &HD}, 0)
                    _DataDir.Add({&HA4, &H1F, &HE}, 0)
                    _DataDir.Add({&HA4, &H1F, &H12}, 0)
                    _DataDir.Add({&HAB}, 1)
                    _DataDir.Add({&HB5}, 0)
                    _DataDir.Add({&HC2}, 1)
                End If
                Return _DataDir
            End Get
        End Property
        Public Function GetDataDir(data As Byte()) As Byte
            Dim dataabbr As String = BitConverter.ToString(data, 0, 3)
            For Each k As Byte() In DataDir.Keys
                If dataabbr.StartsWith(BitConverter.ToString(k)) Then
                    Return DataDir(k)
                End If
            Next
            Return 2
        End Function

        Public Sub StartProcessingQueue()
            _stopping = False
            QueueTaskProcessor = Task.Run(Sub()
                                              While Not _stopping OrElse _pendingTask IsNot Nothing
                                                  If _pendingTask Is Nothing Then
                                                      _commandLock.WaitOne()
                                                      If _stopping AndAlso _pendingTask Is Nothing Then Exit While
                                                  End If

                                                  Dim t As Task = Interlocked.Exchange(_pendingTask, Nothing)

                                                  If t IsNot Nothing AndAlso Not t.IsCompleted Then
                                                      t.Start()
                                                      t.Wait()
                                                  End If
                                              End While
                                          End Sub)
        End Sub
        Public Sub StopProcessingQueue()
            If QueueTaskProcessor Is Nothing Then Exit Sub
            _stopping = True
            _commandLock.Set()

            Dim tstop As Task = QueueTaskProcessor
            QueueTaskProcessor = Nothing
            tstop.Wait()
        End Sub
        Public Sub New(handle As IntPtr)
            AddHandler OnStandardInquiry,
                Sub(sender As Object, e As StandardInquiryEventArgs)
                    Dim devdata As TapeUtils.BlockDevice = TapeUtils.Inquiry(driveHandle)
                    If devdata Is Nothing Then devdata = New TapeUtils.BlockDevice
                    With e.Data
                        .DriveSerialNumber = CULng(devdata.SerialNumber)
                        .VendorIdentification = devdata.VendorId
                        .ProductIdentification = devdata.ProductId
                    End With
                End Sub
            AddHandler OnUnitSerialNumberInquiry,
                Sub(sender As Object, e As UnitSerialNumberInquiryEventArgs)
                    Dim devdata As TapeUtils.BlockDevice = TapeUtils.Inquiry(driveHandle)
                    If devdata Is Nothing Then devdata = New TapeUtils.BlockDevice
                    With e.Page
                        .ProductSerialNumber = devdata.SerialNumber
                    End With
                End Sub
            'AddHandler Me.OnDeviceIdentificationInquiry,
            '    Sub(sender As Object, e As DeviceIdentificationInquiryEventArgs)
            '    End Sub
            driveHandle = handle
            If QueueTaskProcessor IsNot Nothing Then StopProcessingQueue()
            StartProcessingQueue()
        End Sub

        Private _pendingTask As Task = Nothing
        Private _stopping As Boolean = False
        Private ReadOnly _commandLock As AutoResetEvent = New AutoResetEvent(False)

        Public Sub QueueCommand(commandBytes() As Byte, lun As LUNStructure, data() As Byte, task As Object, OnCommandCompleted As OnCommandCompleted) Implements SCSITargetInterface.QueueCommand
            Dim t = New Task(
                              Sub()
                                  Dim cmddir As Byte = GetDataDir(commandBytes)
                                  Dim datalen As Integer = data.Length
                                  Dim cdblen As Integer = 16
                                  If cmddir <> 0 Then
                                      datalen = 0
                                      Select Case commandBytes(0)
                                          Case &H0 'TEST UNIT READY
                                              cdblen = 6
                                          Case &H1 'REWIND
                                              cdblen = 6
                                          Case &H3 'REQUEST SENSE
                                              datalen = commandBytes(4)
                                              cdblen = 6
                                          Case &H4 'FORMAT
                                              cdblen = 6
                                          Case &H5 'READ BLOCK LIMITS
                                              datalen = 6
                                              cdblen = 6
                                          Case &H8 'READ
                                              datalen = CInt(BigEndianConverter.GetValue(commandBytes, 2, 4))
                                              cdblen = 6
                                          Case &HB 'SET CAPACITY
                                              cdblen = 6
                                          Case &H10 'WRITE FILEMARKS
                                              cdblen = 6
                                          Case &H11 'SPACE
                                              cdblen = 6
                                          Case &H12 'INQUIRY
                                              datalen = CInt(BigEndianConverter.GetValue(commandBytes, 3, 4))
                                              cdblen = 6
                                          Case &H13 'VERIFY
                                              cdblen = 6
                                          Case &H16 'RESERVE UNIT
                                              cdblen = 6
                                          Case &H17 'RELEASE UNIT
                                              cdblen = 6
                                          Case &H19 'ERASE
                                              cdblen = 6
                                          Case &H1A 'MODE SENSE
                                              datalen = commandBytes(4)
                                              cdblen = 6
                                          Case &H1B 'LOAD/UNLOAD
                                              cdblen = 6
                                          Case &H1C 'RECEIVE DIAGNOSTIC RESULTS
                                              datalen = CInt(BigEndianConverter.GetValue(commandBytes, 3, 4))
                                              cdblen = 6
                                          Case &H1E 'PREVENT/ALLOW MEDIUM REMOVAL 
                                              cdblen = 6
                                          Case &H25 'READ CAPACITY
                                              datalen = 8
                                              cdblen = 10
                                          Case &H28 'READ 10
                                              datalen = CInt(BigEndianConverter.GetValue(commandBytes, 7, 8))
                                              cdblen = 10
                                          Case &H2B 'LOCATE 10
                                              cdblen = 10
                                          Case &H34 'READ POSITION
                                              If commandBytes(1) = 0 Then
                                                  datalen = 20
                                              Else
                                                  datalen = 32
                                              End If
                                              cdblen = 10
                                          Case &H3C 'READ BUFFER
                                              datalen = CInt(BigEndianConverter.GetValue(commandBytes, 6, 8))
                                              cdblen = 10
                                          Case &H43 'READ TOC
                                              datalen = CInt(Math.Max(BigEndianConverter.GetValue(commandBytes, 7, 8), 20))
                                              cdblen = 10
                                          Case &H44 'REPORT DENSITY SUPPORT
                                              datalen = CInt(BigEndianConverter.GetValue(commandBytes, 7, 8))
                                              cdblen = 10
                                          Case &H4D 'LOG SENSE
                                              datalen = CInt(BigEndianConverter.GetValue(commandBytes, 7, 8))
                                              cdblen = 10
                                          Case &H56 'RESERVE UNIT
                                              cdblen = 10
                                          Case &H57 'RELEASE UNIT
                                              cdblen = 10
                                          Case &H5A 'MODE SENSE
                                              datalen = CInt(BigEndianConverter.GetValue(commandBytes, 7, 8))
                                              cdblen = 10
                                          Case &H5E 'PERSISTENT RESERVE IN
                                              datalen = CInt(BigEndianConverter.GetValue(commandBytes, 7, 8))
                                              cdblen = 10
                                          Case &H8C 'READ ATTRIBUTE
                                              datalen = CInt(BigEndianConverter.GetValue(commandBytes, 10, 13))
                                              cdblen = 16
                                          Case &H91 'SPACE 16
                                              cdblen = 16
                                          Case &H92 'LOCATE 16
                                              cdblen = 16
                                          Case &HA0 'REPORT LUNS
                                              datalen = CInt(Math.Min(32, BigEndianConverter.GetValue(commandBytes, 6, 9)))
                                              cdblen = 16
                                          Case &HA2 'SECURITY PROTOCOL IN
                                              datalen = CInt(BigEndianConverter.GetValue(commandBytes, 6, 9))
                                              cdblen = 16
                                          Case &HA3
                                              Select Case commandBytes(1)
                                                  Case &H5, &HA, &HC, &HD, &HF
                                                      datalen = CInt(BigEndianConverter.GetValue(commandBytes, 6, 9))
                                                  Case &H1F
                                                      Select Case commandBytes(2)
                                                          Case &H6, &H10, &H12, &H15
                                                              datalen = CInt(BigEndianConverter.GetValue(commandBytes, 6, 9))
                                                          Case &H7, &HA, &HB, &HD, &HE, &H18
                                                              datalen = CInt(BigEndianConverter.GetValue(commandBytes, 6, 7))
                                                          Case &H8, &H9
                                                              datalen = CInt(BigEndianConverter.GetValue(commandBytes, 6, 8))
                                                          Case &H14
                                                              datalen = commandBytes(9)
                                                      End Select
                                              End Select
                                              cdblen = 16
                                          Case &HAB
                                              datalen = CInt(BigEndianConverter.GetValue(commandBytes, 6, 9))
                                              cdblen = 16
                                      End Select
                                  Else
                                      Select Case commandBytes(0)
                                          Case &HA 'WRITE
                                              cdblen = 6
                                          Case &H15 'MODE SELECT
                                              cdblen = 6
                                          Case &H1D 'SEND DIAGNOSTIC
                                              cdblen = 6
                                          Case &H3B 'WRITE BUFFER
                                              cdblen = 10
                                          Case &H4C 'LOG SELECT
                                              cdblen = 10
                                          Case &H55 'MODE SELECT
                                              cdblen = 10
                                          Case &H5F 'PERSISTENT RESERVE OUT
                                              cdblen = 10
                                          Case &H8D 'WRITE ATTRIBUTE
                                              cdblen = 16
                                          Case &HA4
                                              cdblen = 16
                                          Case &HB5 'SECURITY PROTOCOL OUT
                                              cdblen = 16
                                      End Select
                                  End If
                                  ReDim Preserve commandBytes(cdblen - 1)
                                  Dim sense(63) As Byte
                                  Dim responsedata(datalen - 1) As Byte
                                  SyncLock TapeUtils.GetSCSIOperationLock(driveHandle)
                                      Select Case My.Settings.TapeUtils_DriverType
                                          Case TapeUtils.DriverType.TapeStream
                                              Dim vt As TapeImage = Nothing
                                              TapeStreamMapping.MappingTable.TryGetValue(driveHandle, vt)
                                              If vt IsNot Nothing Then
                                                  vt.HandleSCSICommand(commandBytes, data, cmddir, datalen, responsedata, sense)
                                              Else
                                                  sense = TapeImage.SenseData.NotPresent
                                                  responsedata = {}
                                              End If
                                          Case Else
                                              Dim databuffer As IntPtr = Marshal.AllocHGlobal(datalen)
                                              Try
                                                  If cmddir <> 1 Then
                                                      Marshal.Copy(data, 0, databuffer, datalen)
                                                  Else
                                                      Marshal.Copy(responsedata, 0, databuffer, datalen)
                                                  End If
                                                  TapeUtils.TapeSCSIIOCtlUnmanaged(driveHandle, commandBytes, databuffer, CUInt(datalen), cmddir, 24 * 3600, sense)
                                                  If cmddir <> 0 Then Marshal.Copy(databuffer, responsedata, 0, datalen)
                                              Finally
                                                  Marshal.FreeHGlobal(databuffer)
                                              End Try
                                      End Select
                                  End SyncLock


                                  Dim response As Byte()
                                  Dim status As SCSIStatusCodeName
                                  If sense(0) = 0 Then
                                      status = SCSIStatusCodeName.Good
                                      If cmddir <> 0 Then
                                          response = responsedata
                                      Else
                                          response = {}
                                      End If
                                  Else
                                      status = SCSIStatusCodeName.CheckCondition
                                      response = {CByte(sense.Length And &HFF), CByte((sense.Length >> 8) And &HFF)}
                                      response = response.Concat(sense).Concat(responsedata).ToArray()
                                  End If
                                  OnCommandCompleted(status, response, task)
                                  If LogCommand Then
                                      Dim parameterData = If(commandBytes.Length > 0 AndAlso
                                                              commandBytes(0) <> &H8 AndAlso
                                                              commandBytes(0) <> &HA,
                                                              IOManager.Byte2Hex(responsedata),
                                                              String.Empty)
                                      Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(SCSIDirectInterface))
                                          Using categoryScope As IDisposable = LogContext.PushProperty("Category", "iSCSI")
                                              Using logStreamScope As IDisposable = LogContext.PushProperty("LogStream", "scsi-console")
                                                  Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "ScsiCommand")
                                                      Using opcodeScope As IDisposable = LogContext.PushProperty("CommandOpcode", If(commandBytes.Length = 0, -1, commandBytes(0)))
                                                          Using directionScope As IDisposable = LogContext.PushProperty("DataDirection", cmddir)
                                                              Log.Information(
                                                                  "SCSI command completed with status {Status}. CDB={Cdb} PARAM={ParameterData} SENSE={SenseData}.",
                                                                  status.ToString(),
                                                                  IOManager.Byte2Hex(commandBytes),
                                                                  parameterData,
                                                                  IOManager.Byte2Hex(sense))
                                                          End Using
                                                      End Using
                                                  End Using
                                              End Using
                                          End Using
                                      End Using
                                  End If
                              End Sub)

            Do
                Dim placed = False

                SyncLock Me
                    If _pendingTask Is Nothing Then
                        _pendingTask = t
                        placed = True
                    End If
                End SyncLock
                If placed Then
                    _commandLock.Set()
                    Exit Do
                End If
                Thread.Sleep(1)
            Loop
        End Sub

        Public Function ExecuteCommand(commandBytes() As Byte, lun As LUNStructure, data() As Byte, ByRef response() As Byte) As SCSIStatusCodeName Implements SCSITargetInterface.ExecuteCommand
            Dim rdata() As Byte = Nothing, retstatus As SCSIStatusCodeName
            QueueCommand(commandBytes, lun, data, Nothing, Sub(status As SCSIStatusCodeName, resp() As Byte, task As Object)
                                                               rdata = resp
                                                               retstatus = status
                                                           End Sub)
            While _pendingTask IsNot Nothing
                Thread.Sleep(1)
            End While
            response = rdata
            Return retstatus
        End Function
    End Class
    Public Sub New()

    End Sub
    Public Sub StartService(Optional ByVal TargetName As String = "iqn.2019-01.com.ltfscopygui:target1")
        svc = New ISCSIServer()
        Try
            Dim directInterface As New SCSIDirectInterface(driveHandle) With {
                .LogCommand = LogCommand
            }
            target = New ISCSITarget(TargetName, directInterface)
            svc.AddTarget(target)
            svc.Start(port)
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(iSCSIService))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "iSCSI")
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "ServiceLifecycle")
                        Log.Information("iSCSI target started. Target={TargetName} Port={Port}.", TargetName, port)
                    End Using
                End Using
            End Using
            RaiseEvent LogPrint($"Started target={TargetName} port={port}")
        Catch ex As Exception
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(iSCSIService))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "iSCSI")
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "ServiceLifecycle")
                        Log.Error(ex, "Failed to start iSCSI service.")
                    End Using
                End Using
            End Using
            Throw
        End Try
    End Sub
    Public Sub StopService()
        If svc IsNot Nothing Then svc.Stop()
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(iSCSIService))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "iSCSI")
                Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "ServiceLifecycle")
                    Log.Information("iSCSI target stopped.")
                End Using
            End Using
        End Using
        RaiseEvent LogPrint("Stopped")
    End Sub

End Class
