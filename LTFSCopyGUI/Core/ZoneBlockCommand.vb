Imports System.IO
Imports System.Runtime.CompilerServices
Imports System.Text
Imports System.Threading
Imports System.Xml.Serialization

Public Class ZBCDeviceHelper
    Public Property handle As IntPtr
    Public Property MaximumLBA As ULong
    Public Property SectorLength As UInt16 = 512
    Public Property CommandLengthLimit As Integer = 524288
    Public Property MaxZoneOpened As UInteger = &HFFFFFFFFUI
    Public Event StatusReport(info As String)
    Public Event ReportSCSICDB(cdb As Byte())
    Public Property CurrentOpenedZone As New List(Of Zone)
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
    <MethodImpl(MethodImplOptions.NoOptimization Or MethodImplOptions.NoInlining)>
    Public Sub InitDevice()
        My.Settings.TapeUtils_DriverType = TapeUtils.DriverType.ZBCDevice
        TapeUtils.LoadEject(handle, TapeUtils.LoadOption.LoadThreaded)
        Dim MP03 As Byte() = TapeUtils.ModeSense(handle, 3)
        SectorLength = CUShort(BigEndianConverter.ToUInt16(MP03, 12))
        ReportZones()
        RaiseEvent StatusReport($"SectorLEN={SectorLength} Zonecount={ZoneList.Count} OpenedZoneCount={CurrentOpenedZone.Count}/{MaxZoneOpened}")
    End Sub
    Public Sub ReportZones(Optional ByVal opt As Byte = 0)
        Dim data0 As Byte() = TapeUtils.SCSIReadParam(handle, {&H95, 0,
                                                      0, 0, 0, 0, 0, 0, 0, 0,
                                                      0, 0, 0, &H40,
                                                      0, 0}, 64)
        MaximumLBA = BigEndianConverter.ToUInt64(data0, 8)
        Dim vpddata As Byte() = TapeUtils.SCSIReadParam(handle, {&H12, 1, &HB6, 0, &H40, 0}, 64)
        Dim ZoneListLen As UInteger = BigEndianConverter.ToUInt32(data0, 0)
        MaxZoneOpened = BigEndianConverter.ToUInt32(vpddata, 16)
        Dim ZoneCount As UInteger = ZoneListLen \ 64UI
        Dim currLBA As ULong = 0
        ZoneList.Clear()
        ZoneLBAMap.Clear()
        CurrentOpenedZone.Clear()
        While True
            Dim cdb As Byte() = {&H95, 0, CByte(CLng((currLBA >> 56)) And &HFF),
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
                                          CByte(&H80 Or opt), 0}
            Dim data1 As Byte() = TapeUtils.SCSIReadParam(handle, cdb, CommandLengthLimit)
            RaiseEvent ReportSCSICDB(cdb)
            ZoneListLen = BigEndianConverter.ToUInt32(data1, 0)
            If ZoneListLen = 0 Then Exit While
            ZoneCount = ZoneListLen \ 64UI
            Dim readed As Zone = Nothing
            For i As Integer = 0 To CInt(ZoneCount - 1)
                readed = New Zone(data1, 64 + 64 * i)
                If readed.ZoneCondition = Zone.ZoneConditionDef.EXPLICIT_OPENED OrElse readed.ZoneCondition = Zone.ZoneConditionDef.IMPLICIT_OPENED Then
                    CurrentOpenedZone.Add(readed)
                End If
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
        Dim cdb As Byte() = {&H95, 0, CByte(CLng((ToRefresh.ZoneStartLBA >> 56)) And &HFF),
                                      CByte(CLng((ToRefresh.ZoneStartLBA >> 48)) And &HFF),
                                      CByte(CLng((ToRefresh.ZoneStartLBA >> 40)) And &HFF),
                                      CByte(CLng((ToRefresh.ZoneStartLBA >> 32)) And &HFF),
                                      CByte(CLng((ToRefresh.ZoneStartLBA >> 24)) And &HFF),
                                      CByte(CLng((ToRefresh.ZoneStartLBA >> 16)) And &HFF),
                                      CByte(CLng((ToRefresh.ZoneStartLBA >> 8)) And &HFF),
                                      CByte(CLng((ToRefresh.ZoneStartLBA >> 0)) And &HFF),
                                      0, 0, 0, 128, &H80, 0}
        Dim data1 As Byte() = TapeUtils.SCSIReadParam(handle, cdb, CommandLengthLimit)
        RaiseEvent ReportSCSICDB(cdb)
        RaiseEvent StatusReport($"ReportZone {ToRefresh.ZoneStartLBA}")
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
        RaiseEvent StatusReport($"SectorLEN={SectorLength} Zonecount={ZoneList.Count} OpenedZoneCount={CurrentOpenedZone.Count}/{MaxZoneOpened}")
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
                If ZoneList(SearchStart).ZoneStartLBA <= LBA AndAlso ZoneList(SearchStart).ZoneEndLBA >= LBA Then
                    idx = SearchStart
                    Exit While
                End If
                If ZoneList(SearchEnd).ZoneStartLBA <= LBA AndAlso ZoneList(SearchEnd).ZoneEndLBA >= LBA Then
                    idx = SearchEnd
                    Exit While
                End If
            End If
        End While
        Return ZoneList(idx)
    End Function
    Public Function CloseAllZones(Optional ByRef sense As Byte() = Nothing) As Boolean
        Dim senseFin As Boolean = False
        Dim senseresult As Byte() = Array.Empty(Of Byte)()
        Dim cdb As Byte() = {&H94, &H1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0}
        Dim result As Boolean = TapeUtils.SendSCSICommand(handle, cdb, Nothing, 1,
                                         Function(sdata As Byte())
                                             senseresult = sdata
                                             senseFin = True
                                             Return True
                                         End Function)
        RaiseEvent ReportSCSICDB(cdb)
        RaiseEvent StatusReport($"CloseAllZone")
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
        Dim cdb As Byte() = {&H94, &H4,
            CByte(CLng((LowestLBA >> 56)) And &HFF),
            CByte(CLng((LowestLBA >> 48)) And &HFF),
            CByte(CLng((LowestLBA >> 40)) And &HFF),
            CByte(CLng((LowestLBA >> 32)) And &HFF),
            CByte(CLng((LowestLBA >> 24)) And &HFF),
            CByte(CLng((LowestLBA >> 16)) And &HFF),
            CByte(CLng((LowestLBA >> 8)) And &HFF),
            CByte(CLng((LowestLBA >> 0)) And &HFF),
            0, 0, 0, 0, 0, 0}
        Dim result As Boolean = TapeUtils.SendSCSICommand(
            handle, cdb, Nothing, 1, Function(sdata As Byte())
                                         senseresult = sdata
                                         senseFin = True
                                         Return True
                                     End Function)
        RaiseEvent ReportSCSICDB(cdb)
        RaiseEvent StatusReport($"ResetWritePointer {LowestLBA}")
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
        Dim cdb As Byte() = {&H94, &H3,
            CByte(CLng((LowestLBA >> 56)) And &HFF),
            CByte(CLng((LowestLBA >> 48)) And &HFF),
            CByte(CLng((LowestLBA >> 40)) And &HFF),
            CByte(CLng((LowestLBA >> 32)) And &HFF),
            CByte(CLng((LowestLBA >> 24)) And &HFF),
            CByte(CLng((LowestLBA >> 16)) And &HFF),
            CByte(CLng((LowestLBA >> 8)) And &HFF),
            CByte(CLng((LowestLBA >> 0)) And &HFF),
            0, 0, 0, 0, 0, 0}
        Dim result As Boolean = TapeUtils.SendSCSICommand(
            handle, cdb, Nothing, 1, Function(sdata As Byte())
                                         senseresult = sdata
                                         senseFin = True
                                         Return True
                                     End Function)
        RaiseEvent ReportSCSICDB(cdb)
        RaiseEvent StatusReport($"OpenZone {LowestLBA}")
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
        Dim cdb As Byte() = {&H94, &H1,
            CByte(CLng((LowestLBA >> 56)) And &HFF),
            CByte(CLng((LowestLBA >> 48)) And &HFF),
            CByte(CLng((LowestLBA >> 40)) And &HFF),
            CByte(CLng((LowestLBA >> 32)) And &HFF),
            CByte(CLng((LowestLBA >> 24)) And &HFF),
            CByte(CLng((LowestLBA >> 16)) And &HFF),
            CByte(CLng((LowestLBA >> 8)) And &HFF),
            CByte(CLng((LowestLBA >> 0)) And &HFF),
            0, 0, 0, 0, 0, 0}
        Dim result As Boolean = TapeUtils.SendSCSICommand(
            handle, cdb, Nothing, 1, Function(sdata As Byte())
                                         senseresult = sdata
                                         senseFin = True
                                         Return True
                                     End Function)
        RaiseEvent ReportSCSICDB(cdb)
        RaiseEvent StatusReport($"CloseZone {LowestLBA}")
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
        Dim cdb As Byte() = {&H94, &H2,
            CByte(CLng((LowestLBA >> 56)) And &HFF),
            CByte(CLng((LowestLBA >> 48)) And &HFF),
            CByte(CLng((LowestLBA >> 40)) And &HFF),
            CByte(CLng((LowestLBA >> 32)) And &HFF),
            CByte(CLng((LowestLBA >> 24)) And &HFF),
            CByte(CLng((LowestLBA >> 16)) And &HFF),
            CByte(CLng((LowestLBA >> 8)) And &HFF),
            CByte(CLng((LowestLBA >> 0)) And &HFF),
            0, 0, 0, 0, 0, 0}
        Dim result As Boolean = TapeUtils.SendSCSICommand(
            handle, cdb, Nothing, 1,
                                         Function(sdata As Byte())
                                             senseresult = sdata
                                             senseFin = True
                                             Return True
                                         End Function)

        RaiseEvent ReportSCSICDB(cdb)
        RaiseEvent StatusReport($"FinishZone {LowestLBA}")
        For i As Integer = 0 To 10
            If senseFin Then Exit For
            Thread.Sleep(1)
        Next
        sense = senseresult
        Return result
    End Function
    Public Function ReadBytes(StartLBA As ULong, ByVal ByteOffset As UInt16, ReadLen As ULong) As Byte()
        Dim result As New List(Of Byte)
        Dim remain As Long = CLng(ReadLen)
        Dim oncereadsectorcount As Integer = CInt(Math.Truncate(CommandLengthLimit / SectorLength))
        Dim currentLBA As ULong = StartLBA
        While remain > 0
            Dim cdb As Byte() = {&H28, 0,
                CByte(CLng((currentLBA >> 24)) And &HFF),
                CByte(CLng((currentLBA >> 16)) And &HFF),
                CByte(CLng((currentLBA >> 8)) And &HFF),
                CByte(CLng((currentLBA >> 0)) And &HFF),
                0,
                CByte((oncereadsectorcount >> 8) And &HFF),
                CByte((oncereadsectorcount >> 0) And &HFF),
                 0}
            Dim data As Byte() = TapeUtils.SCSIReadParam(handle, cdb, oncereadsectorcount * SectorLength)
            RaiseEvent ReportSCSICDB(cdb)
            If currentLBA = StartLBA AndAlso ByteOffset > 0 Then
                data = data.Skip(ByteOffset).ToArray()
            End If
            If data.Length > remain Then data = data.Take(CInt(remain)).ToArray()
            result.AddRange(data)
            remain = remain - data.Length
            currentLBA = CULng(currentLBA + oncereadsectorcount)
        End While
        Return result.ToArray()
    End Function
    Public Function ReadBytes(StartLBA As ULong, ByVal ByteOffset As UInt16, ReadLen As ULong, ByVal Destination As Byte(), ByVal DestinationOffset As Integer) As Boolean
        If ReadLen = 0 Then Return True
        If ReadLen > CULng(Destination.Length - DestinationOffset) Then Return False

        Dim remain As ULong = ReadLen
        Dim currentLBA As ULong = StartLBA
        Dim currentOffset As Integer = ByteOffset
        Dim destOffset As Integer = DestinationOffset
        Dim maxSectorCount As Integer = CInt(Math.Truncate(CommandLengthLimit / SectorLength))

        While remain > 0
            Dim sectorCount As Integer = CInt(Math.Min(CULng(maxSectorCount), (CULng(currentOffset) + remain + CULng(SectorLength) - 1UL) \ CULng(SectorLength)))
            Dim cdb As Byte() = {&H28, 0,
            CByte((currentLBA >> 24) And &HFFUL),
            CByte((currentLBA >> 16) And &HFFUL),
            CByte((currentLBA >> 8) And &HFFUL),
            CByte(currentLBA And &HFFUL),
            0,
            CByte((sectorCount >> 8) And &HFF),
            CByte(sectorCount And &HFF),
            0}
            Dim data As Byte() = TapeUtils.SCSIReadParam(handle, cdb, sectorCount * SectorLength)
            RaiseEvent ReportSCSICDB(cdb)

            If data Is Nothing OrElse data.Length <= currentOffset Then Return False
            Dim copyLen As Integer = CInt(Math.Min(CULng(data.Length - currentOffset), remain))
            Buffer.BlockCopy(data, currentOffset, Destination, destOffset, copyLen)

            remain -= CULng(copyLen)
            destOffset += copyLen
            currentLBA += CULng(sectorCount)
            currentOffset = 0
        End While
        Return True
    End Function
    Public Function WriteBytesConventional(ByVal source As Byte(), StartLBA As ULong, Optional senseReport As Func(Of Byte(), Boolean) = Nothing) As Boolean
        If source Is Nothing OrElse source.Length = 0 Then Return True
        Dim sectorLen As Integer = SectorLength
        If sectorLen <= 0 Then Return False
        Dim maxSectorCount As Integer = Math.Min(&HFFFF, CommandLengthLimit \ sectorLen)
        If maxSectorCount <= 0 Then Return False
        Dim maxTransferBytes As Integer = maxSectorCount * sectorLen
        Dim remain As Integer = source.Length
        Dim sourceOffset As Integer = 0
        Dim currentLBA As ULong = StartLBA
        While remain > 0
            Dim sendlen As Integer = Math.Min(maxTransferBytes, remain)
            Dim sectorCount As Integer = (sendlen - 1) \ sectorLen + 1
            Dim transferLen As Integer = sectorCount * sectorLen
            Dim toSend(transferLen - 1) As Byte
            Array.Copy(source, sourceOffset, toSend, 0, sendlen)
            Dim cdb As Byte() = {
                &H2A, 0,
                CByte((currentLBA >> 24) And &HFFUL),
                CByte((currentLBA >> 16) And &HFFUL),
                CByte((currentLBA >> 8) And &HFFUL),
                CByte(currentLBA And &HFFUL),
                0,
                CByte((sectorCount >> 8) And &HFF),
                CByte(sectorCount And &HFF),
                0}
            If Not TapeUtils.SendSCSICommand(handle, cdb, toSend, 0, senseReport, 600) Then Return False
            RaiseEvent ReportSCSICDB(cdb)
            sourceOffset += sendlen
            remain -= sendlen
            currentLBA += CULng(sectorCount)
        End While
        Return True
    End Function
    Public Function WriteBytesConventional(ByVal source As Byte(), ByVal transferLength As Integer, StartLBA As ULong, Optional senseReport As Func(Of Byte(), Boolean) = Nothing) As Boolean
        If source Is Nothing OrElse transferLength = 0 Then Return True
        If transferLength < 0 OrElse transferLength > source.Length Then Return False
        Dim sectorLen As Integer = SectorLength
        If sectorLen <= 0 Then Return False
        Dim maxSectorCount As Integer = Math.Min(&HFFFF, CommandLengthLimit \ sectorLen)
        If maxSectorCount <= 0 Then Return False
        Dim maxTransferBytes As Integer = maxSectorCount * sectorLen
        Dim remain As Integer = transferLength
        Dim sourceOffset As Integer = 0
        Dim currentLBA As ULong = StartLBA
        Dim toSend() As Byte = {}
        While remain > 0
            Dim sendlen As Integer = Math.Min(maxTransferBytes, remain)
            Dim sectorCount As Integer = (sendlen - 1) \ sectorLen + 1
            Dim transferLen As Integer = sectorCount * sectorLen
            If toSend.Length <> transferLen Then ReDim toSend(transferLen - 1)
            Array.Copy(source, sourceOffset, toSend, 0, sendlen)
            Dim cdb As Byte() = {
                &H2A, 0,
                CByte((currentLBA >> 24) And &HFFUL),
                CByte((currentLBA >> 16) And &HFFUL),
                CByte((currentLBA >> 8) And &HFFUL),
                CByte(currentLBA And &HFFUL),
                0,
                CByte((sectorCount >> 8) And &HFF),
                CByte(sectorCount And &HFF),
                0}
            If Not TapeUtils.SendSCSICommand(handle, cdb, toSend, transferLen, 0, senseReport, 600) Then Return False
            RaiseEvent ReportSCSICDB(cdb)
            sourceOffset += sendlen
            remain -= sendlen
            currentLBA += CULng(sectorCount)
        End While
        Return True
    End Function
    Public ZoneBuffer As Byte()
    Public Function GetZoneBuffer(size As Integer) As Byte()
        If ZoneBuffer Is Nothing OrElse ZoneBuffer.Length < size Then
            ReDim ZoneBuffer(size - 1)
        End If
        Return ZoneBuffer
    End Function
    Public paddingBuffer As Byte()
    Public Function GetPaddingBuffer(size As Integer) As Byte()
        If paddingBuffer Is Nothing OrElse paddingBuffer.Length < size Then
            ReDim paddingBuffer(size - 1)
        End If
        Return paddingBuffer
    End Function
    Private _SCSICommandHandlerLock As New Object
    Public Function HandleSCSICommand(commandBytes As Byte(), Param As Byte(), dataIn As Byte, dataLen As Integer, ByRef Response As Byte(), ByRef sense As Byte(), Optional ByVal timeout As Integer = 600) As Boolean
        SyncLock _SCSICommandHandlerLock
            Select Case commandBytes(0)
                Case &H2A
                    Dim startLBA As ULong = 0
                    For i As Integer = 2 To 5
                        startLBA <<= 8
                        startLBA = startLBA Or commandBytes(i)
                    Next
                    Dim sectorCount As ULong = 0
                    For i As Integer = 7 To 8
                        sectorCount <<= 8
                        sectorCount = sectorCount Or commandBytes(i)
                    Next
                    If sectorCount = 0 Then
                        Return True
                    End If
                    RaiseEvent StatusReport($"SCSIOP 0x2A WRITE LBA={startLBA.ToString()} SECTOR={sectorCount}")
                    Dim startZone = ZoneList.IndexOf(GetZoneByLBA(startLBA))
                    Dim endZone = ZoneList.IndexOf(GetZoneByLBA(startLBA + sectorCount - 1UL))
                    Dim lastSense(63) As Byte
                    Dim senseCallback As Func(Of Byte(), Boolean) =
                    Function(sdata As Byte())
                        lastSense = sdata
                        Return True
                    End Function
                    Dim result As Boolean = True
                    For i As Integer = startZone To endZone
                        Dim currZone = ZoneList(i)
                        Dim currstartLBA = currZone.ZoneStartLBA
                        Dim currsectorCnt = currZone.ZoneLength
                        If i = startZone Then
                            currstartLBA = Math.Max(currstartLBA, startLBA)
                            currsectorCnt = Math.Min(currsectorCnt, currZone.ZoneEndLBA - currstartLBA + 1UL)
                            currsectorCnt = Math.Min(currsectorCnt, sectorCount)
                        ElseIf i = endZone Then
                            currsectorCnt = startLBA + sectorCount - currZone.ZoneStartLBA
                        End If
                        result = CommitZoneWrite(currZone, currstartLBA, currsectorCnt, Param, CInt((currstartLBA - startLBA) * SectorLength), senseCallback)
                        If Not result Then Exit For
                    Next
                    sense = lastSense
                    Return result
                Case &H28
                    Dim result = DirectRead(commandBytes, Param, dataIn, dataLen, Response, sense, timeout)
                    Return result
                Case Else
                    Dim senseFin As Boolean = False
                    Dim senseresult As Byte() = Array.Empty(Of Byte)()
                    If dataIn = 1 Then ReDim Param(dataLen - 1)
                    Dim result As Boolean = TapeUtils.SendSCSICommand(
                    handle, commandBytes, Param, dataIn,
                                                 Function(sdata As Byte())
                                                     senseresult = sdata
                                                     senseFin = True
                                                     Return True
                                                 End Function, timeout)
                    RaiseEvent ReportSCSICDB(commandBytes)
                    RaiseEvent StatusReport($"SCSIOP 0x{commandBytes(0).ToString("X")}")
                    If result Then
                        Response = Param
                        If commandBytes(0) = &H12 Then
                            'PERIPHERAL DEVICE TYPE change to normal HDD instead of 0x14h (host managed zoned block device)
                            Response(0) = 0
                        End If
                    End If
                    sense = senseresult
                    Return result
            End Select
        End SyncLock
    End Function
    Public Function DirectRead(commandBytes As Byte(), Param As Byte(), dataIn As Byte, dataLen As Integer, ByRef Response As Byte(), ByRef sense As Byte(), Optional ByVal timeout As Integer = 600) As Boolean
        Dim senseFin As Boolean = False
        Dim senseresult As Byte() = Array.Empty(Of Byte)()
        If dataIn = 1 Then ReDim Param(dataLen - 1)
        Dim LBA As ULong = commandBytes(2)
        LBA <<= 8
        LBA = LBA Or commandBytes(3)
        LBA <<= 8
        LBA = LBA Or commandBytes(4)
        LBA <<= 8
        LBA = LBA Or commandBytes(5)

        Dim Sector As Integer = commandBytes(7)
        Sector <<= 8
        Sector = Sector Or commandBytes(8)
        If Not ForceFlushZone(LBA, Sector) Then Return False
        Dim result As Boolean = TapeUtils.SendSCSICommand(handle, commandBytes, Param, dataIn,
                                                          Function(sdata As Byte())
                                                              senseresult = sdata
                                                              senseFin = True
                                                              Return True
                                                          End Function, timeout)
        If result Then
            Response = Param
            If commandBytes(0) = &H12 Then
                'PERIPHERAL DEVICE TYPE change to normal HDD instead of 0x14h (host managed zoned block device)
                Response(0) = 0
            End If
        End If
        sense = senseresult
        RaiseEvent StatusReport($"SCSIOP 0x28 READ LBA={LBA.ToString()} SECTOR={Sector}")
        RaiseEvent ReportSCSICDB(commandBytes)
        Return result
    End Function
    Public Function CommitZoneWrite(zoneToWrite As Zone, StartLBA As ULong, SectorCnt As ULong, Source As Byte(), SourceOffset As Integer, senseCallback As Func(Of Byte(), Boolean)) As Boolean
        Dim needDumpSegCount As Byte = 0 '需要先读多少段
        Dim Dump1StartLBA, Dump1SectorCount, Dump2StartLBA, Dump2SectorCount, ZoneHeaderPaddingSectorCount As ULong

        Dim needFillZoneHeader As Boolean = False '是否需要填充开头扇区
        If zoneToWrite.ZoneType = Zone.ZoneTypeDef.Conventional OrElse zoneToWrite Is LastWrittenZone Then
            needDumpSegCount = 0
            needFillZoneHeader = False
        Else
            RefreshZoneCondition(zoneToWrite)
            Select Case zoneToWrite.ZoneCondition
                Case Zone.ZoneConditionDef.EMPTY
                    '为空：不需要dmp，按需填零
                    needDumpSegCount = 0
                    If StartLBA <> zoneToWrite.ZoneStartLBA Then
                        needFillZoneHeader = True
                        ZoneHeaderPaddingSectorCount = StartLBA - zoneToWrite.ZoneStartLBA
                    Else
                        needFillZoneHeader = False
                    End If
                    CurrentOpenedZone.Add(zoneToWrite)
                Case Zone.ZoneConditionDef.FULL
                    '如果往开头写整个zone, 直接resetWP, 然后和empty一样处理。
                    If StartLBA = zoneToWrite.ZoneStartLBA AndAlso SectorCnt = zoneToWrite.ZoneEndLBA - zoneToWrite.ZoneStartLBA + 1UL Then
                        If Not ResetWritePointer(zoneToWrite.ZoneStartLBA) Then Return False
                        needFillZoneHeader = False
                        Exit Select
                    Else
                        '按需判断前后是否dump
                        If StartLBA > zoneToWrite.ZoneStartLBA Then
                            needDumpSegCount = 1
                            needFillZoneHeader = True
                            Dump1StartLBA = zoneToWrite.ZoneStartLBA
                            Dump1SectorCount = StartLBA - zoneToWrite.ZoneStartLBA
                        End If
                        If StartLBA + SectorCnt <= zoneToWrite.ZoneEndLBA Then
                            '需要dump后面
                            needDumpSegCount = needDumpSegCount + CByte(1)
                            If needDumpSegCount = 2 Then
                                Dump2StartLBA = StartLBA + SectorCnt
                                Dump2SectorCount = zoneToWrite.ZoneEndLBA - Dump2StartLBA + 1UL
                            Else
                                Dump1StartLBA = StartLBA + SectorCnt
                                Dump1SectorCount = zoneToWrite.ZoneEndLBA - Dump1StartLBA + 1UL
                            End If
                        End If
                    End If
                    CurrentOpenedZone.Add(zoneToWrite)
                Case Zone.ZoneConditionDef.CLOSED, Zone.ZoneConditionDef.IMPLICIT_OPENED, Zone.ZoneConditionDef.EXPLICIT_OPENED
                    If zoneToWrite.ZoneCondition = Zone.ZoneConditionDef.CLOSED Then
                        '如果往开头写整个zone, 直接resetWP, 然后和empty一样处理。
                        If StartLBA = zoneToWrite.ZoneStartLBA AndAlso SectorCnt = zoneToWrite.ZoneEndLBA - zoneToWrite.ZoneStartLBA + 1UL Then
                            If Not ResetWritePointer(zoneToWrite.ZoneStartLBA) Then Return False
                            needFillZoneHeader = False
                            Exit Select
                        Else
                            '打开Zone，后续和opened相同处理方式
                            If Not OpenZone(zoneToWrite.ZoneStartLBA) Then Return False
                            CurrentOpenedZone.Add(zoneToWrite)
                        End If
                    End If
                    If StartLBA < zoneToWrite.ZoneWritePointerLBA Then
                        'WP在写入位置后面, 按需dump前后
                        If StartLBA > zoneToWrite.ZoneStartLBA Then
                            needDumpSegCount = 1
                            needFillZoneHeader = True
                            Dump1StartLBA = zoneToWrite.ZoneStartLBA
                            Dump1SectorCount = StartLBA - zoneToWrite.ZoneStartLBA
                        Else
                            '从开头写，不需要dump前面
                            needDumpSegCount = 0
                            needFillZoneHeader = False
                        End If
                        If StartLBA + SectorCnt <= zoneToWrite.ZoneWritePointerLBA Then
                            '需要dump后面
                            needDumpSegCount = needDumpSegCount + CByte(1)
                            If needDumpSegCount = 2 Then
                                Dump2StartLBA = StartLBA + SectorCnt
                                Dump2SectorCount = zoneToWrite.ZoneWritePointerLBA - Dump2StartLBA
                            Else
                                Dump1StartLBA = StartLBA + SectorCnt
                                Dump1SectorCount = zoneToWrite.ZoneWritePointerLBA - Dump1StartLBA
                            End If
                        End If
                    ElseIf StartLBA = zoneToWrite.ZoneWritePointerLBA Then
                        'WP对齐写入位置，不需要dump
                        needDumpSegCount = 0
                        needFillZoneHeader = False
                    Else
                        'WP在写入位置前, 只要padding, 后面没数据
                        Dim paddingsectors As Integer = CInt(StartLBA - zoneToWrite.ZoneWritePointerLBA)
                        Dim maxPaddingSectors As Integer = Math.Max(1, CommandLengthLimit \ SectorLength)
                        Dim paddingBuffer = GetPaddingBuffer(maxPaddingSectors * SectorLength)
                        While paddingsectors > 0
                            Dim count As Integer = CInt(Math.Min(CULng(maxPaddingSectors), paddingsectors))
                            Dim paddingLen = count * SectorLength
                            If Not WriteBytesConventional(paddingBuffer, paddingLen, zoneToWrite.ZoneWritePointerLBA) Then Return False
                            zoneToWrite.ZoneWritePointerLBA += CULng(count)
                            paddingsectors -= count
                        End While
                        zoneToWrite.ZoneWritePointerLBA = StartLBA
                        needDumpSegCount = 0
                        needFillZoneHeader = False
                    End If
            End Select
        End If
        While CurrentOpenedZone.Count > MaxZoneOpened \ 2
            CloseZone(CurrentOpenedZone(0).ZoneStartLBA)
            RefreshZoneCondition(CurrentOpenedZone(0))
            If CurrentOpenedZone(0).ZoneCondition <> Zone.ZoneConditionDef.IMPLICIT_OPENED AndAlso CurrentOpenedZone(0).ZoneCondition <> Zone.ZoneConditionDef.EXPLICIT_OPENED Then
                CurrentOpenedZone.RemoveAt(0)
            End If
        End While
        Dim totalSectorsToWrite = SectorCnt
        Dim writeStartLBA = StartLBA
        If needFillZoneHeader Then
            writeStartLBA = zoneToWrite.ZoneStartLBA
            If ZoneHeaderPaddingSectorCount > 0 Then
                totalSectorsToWrite += ZoneHeaderPaddingSectorCount
            End If
        End If
        If needDumpSegCount > 0 Then
            totalSectorsToWrite += Dump1SectorCount
            If needDumpSegCount = 2 Then
                totalSectorsToWrite += Dump2SectorCount
            End If
        End If
        Dim totalBytesToWrite As Integer = CInt(totalSectorsToWrite * SectorLength)
        Dim toWrite() As Byte = GetZoneBuffer(totalBytesToWrite)
        If needDumpSegCount > 0 Then
            If Not ReadBytes(Dump1StartLBA, 0, Dump1SectorCount * SectorLength, toWrite, CInt((Dump1StartLBA - zoneToWrite.ZoneStartLBA) * SectorLength)) Then Return False
            If needDumpSegCount = 2 Then
                If Not ReadBytes(Dump2StartLBA, 0, Dump2SectorCount * SectorLength, toWrite, CInt((Dump2StartLBA - zoneToWrite.ZoneStartLBA) * SectorLength)) Then Return False
            End If
            If Not ResetWritePointer(zoneToWrite.ZoneStartLBA) Then Return False
        ElseIf needFillZoneHeader Then
            Array.Clear(toWrite, 0, CInt((StartLBA - zoneToWrite.ZoneStartLBA) * SectorLength))
        End If
        Dim destOffset As Integer = 0
        If needFillZoneHeader Then
            destOffset = CInt((StartLBA - zoneToWrite.ZoneStartLBA) * SectorLength)
        End If
        Array.Copy(Source, SourceOffset, toWrite,
                    destOffset, CInt(SectorCnt * SectorLength))
        If needDumpSegCount > 0 OrElse needFillZoneHeader Then
            RaiseEvent StatusReport($"Readout required. ZoneStart={zoneToWrite.ZoneStartLBA.ToString()} WriteAt={StartLBA.ToString()} WriteCount={SectorCnt} TotalWriteCount={totalSectorsToWrite}")
        End If
        Dim result As Boolean = True
        If zoneToWrite.ZoneType = Zone.ZoneTypeDef.Conventional Then
            result = WriteBytesConventional(toWrite, totalBytesToWrite, writeStartLBA, senseCallback)
        Else
            result = UpdateZoneBuffer(zoneToWrite, writeStartLBA, CInt(totalSectorsToWrite), toWrite, 0)
        End If
        If zoneToWrite.ZoneCondition = Zone.ZoneConditionDef.FULL Then
            CurrentOpenedZone.Remove(zoneToWrite)
        End If
        Return result
    End Function

    Public Function UpdateZoneBuffer(zoneToWrite As Zone, StartLBA As ULong, SectorCount As Integer, Source As Byte(), SourceOffset As Integer) As Boolean
        Dim CopyStart As Integer = CInt(StartLBA - zoneToWrite.ZoneStartLBA) * SectorLength
        Dim CopyLen As Integer = SectorCount * SectorLength
        SyncLock BufferLock
            If LastWrittenZone Is Nothing OrElse LastWrittenZone IsNot zoneToWrite Then
                If LastWrittenZone IsNot Nothing Then
                    If Not ForceFlushZone(LastWrittenZone.ZoneStartLBA, 1) Then Return False
                End If
                LastWrittenZone = zoneToWrite
                Dim ZoneLengthInBytes As Integer = CInt(zoneToWrite.ZoneEndLBA - zoneToWrite.ZoneStartLBA + 1) * SectorLength
                If LastWrittenZoneData Is Nothing OrElse LastWrittenZoneData.Length <> ZoneLengthInBytes Then
                    ReDim LastWrittenZoneData(ZoneLengthInBytes - 1)
                Else
                    If CopyStart > 0 Then Array.Clear(LastWrittenZoneData, 0, CopyStart)
                    If CopyStart + CopyLen < LastWrittenZoneData.Length Then Array.Clear(LastWrittenZoneData, CopyStart + CopyLen, (LastWrittenZoneData.Length - CopyStart - CopyLen))
                End If
            End If
            Array.Copy(Source, SourceOffset, LastWrittenZoneData, CopyStart, CopyLen)
            BufferWritten = False
            Interlocked.Exchange(LastBufferUpdateTimeStamp, Stopwatch.GetTimestamp())
            If UpdateTask Is Nothing Then
                UpdateCTS = New CancellationTokenSource()
                Dim token As CancellationToken = UpdateCTS.Token
                UpdateTask = Task.Run(Sub()
                                          Try
                                              While True
                                                  token.ThrowIfCancellationRequested()
                                                  Dim lastTimestamp = Interlocked.Read(LastBufferUpdateTimeStamp)
                                                  Dim elapsedSeconds = (Stopwatch.GetTimestamp() - lastTimestamp) / Stopwatch.Frequency
                                                  If elapsedSeconds >= AutoFlushIdleSeconds Then Exit While
                                                  If token.WaitHandle.WaitOne(100) Then token.ThrowIfCancellationRequested()
                                              End While
                                              token.ThrowIfCancellationRequested()
                                              '自动回写入口内部也会获取 _SCSICommandHandlerLock
                                              If Not ZoneAutoFlush(token) Then
                                                  Throw New Exception("Zone autoflush error")
                                              End If
                                          Catch ex As OperationCanceledException
                                              '正常取消，不需要报错
                                          End Try
                                      End Sub)
            End If
        End SyncLock

        Return True
    End Function
    Public Property LastWrittenZone As Zone
    Public Property BufferWritten As Boolean = False
    Public Property BufferLock As New Object
    Public Property LastWrittenZoneData As Byte()
    Public Property LastBufferUpdateTimeStamp As Long
    Public Property AutoFlushIdleSeconds As Double = 30
    Private UpdateTask As Task
    Private UpdateCTS As CancellationTokenSource
    Public Function ForceFlushZone(StartLBA As ULong, SectorCount As Integer) As Boolean
        SyncLock BufferLock
            If LastWrittenZone IsNot Nothing Then
                Dim Remaining As Long = SectorCount
                If StartLBA > LastWrittenZone.ZoneEndLBA Then Return True
                If StartLBA + SectorCount <= LastWrittenZone.ZoneStartLBA Then Return True
                Dim CurrentLBA As ULong = StartLBA
                While Remaining > 0
                    If LastWrittenZone.ZoneStartLBA <= CurrentLBA AndAlso CurrentLBA <= LastWrittenZone.ZoneEndLBA Then
                        If BufferWritten Then Exit While
                        If Not ResetWritePointer(LastWrittenZone.ZoneStartLBA) Then Return False
                        If Not WriteBytesConventional(LastWrittenZoneData, LastWrittenZoneData.Length, LastWrittenZone.ZoneStartLBA) Then Return False
                        '取消尚在等待中的自动回写任务
                        If UpdateCTS IsNot Nothing Then
                            UpdateCTS.Cancel()
                            UpdateCTS = Nothing
                            UpdateTask = Nothing
                        End If
                        RefreshZoneCondition(LastWrittenZone)
                        BufferWritten = True
                        Remaining -= (CInt(LastWrittenZone.ZoneEndLBA - CurrentLBA) + 1)
                        CurrentLBA = LastWrittenZone.ZoneEndLBA + 1UL
                    Else
                        Dim NextZone = GetZoneByLBA(CurrentLBA)
                        If NextZone Is Nothing Then Exit While
                        Dim NextLBA = NextZone.ZoneEndLBA + 1UL
                        Remaining -= CInt(NextLBA - CurrentLBA)
                        CurrentLBA = NextLBA
                    End If
                End While
            End If
        End SyncLock

        Return True
    End Function
    Public Function ZoneAutoFlush(token As CancellationToken) As Boolean
        SyncLock _SCSICommandHandlerLock
            If LastWrittenZone Is Nothing Then Return True
            If token.IsCancellationRequested Then Return True
            Dim result = ForceFlushZone(LastWrittenZone.ZoneStartLBA, 1)
            If Not result Then Return False
            UpdateTask = Nothing
            UpdateCTS = Nothing
            Return True
        End SyncLock
    End Function
    Public Function SafeEject() As Boolean
        SyncLock _SCSICommandHandlerLock
            If LastWrittenZone Is Nothing Then Return True
            Return ForceFlushZone(LastWrittenZone.ZoneStartLBA, 1)
        End SyncLock
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
        WriteBytesConventional(vol1, 0)
    End Sub
    <Serializable>
    Public Class ZBCDataHelper
        <XmlIgnore>
        Public Device As ZBCDeviceHelper
        Public Property CMRDataStartLBA As ULong
        Public Property CMRDataLength As ULong
        Public Sub WriteCMRData(toWrite As Byte())
            CMRDataLength = CULng(toWrite.Length)
            Device.WriteBytesConventional(toWrite, CMRDataStartLBA)
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
