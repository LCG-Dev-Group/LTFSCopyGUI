Imports System.IO
Imports System.Text
Public Class TapeStreamMapping
    Public Shared MappingTable As New SerializableDictionary(Of IntPtr, TapeImage)
End Class

<Serializable>
Public Class TapeImage
    Implements IDisposable

    Public Class SenseData
        Public Shared BlankCheck As Byte() = {&HF0, 0, 8, 0, 0, 0, 0, &H10, 0, 0, 0, 0, 0, 5, 0, 0, &H50, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0}
        Public Shared MediumError As Byte() = {&H70, 0, 3, 0, 0, 0, 0, &H10, 0, 0, 0, 0, &H31, 0, 0, 0, &H50, &H89, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0}
        Public Shared NotPresent As Byte() = {&H70, 0, 2, 0, 0, 0, 0, &H10, 0, 0, 0, 0, &H3A, 0, 0, 0, &H94, &H50, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0}
        Public Shared NotFound As Byte() = {&H70, 0, 8, 0, 0, 0, 0, &H10, 0, 0, 0, 0, 0, 5, 0, 0, &H34, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0}
        Public Shared FileMark As Byte() = {&HF0, 0, &H80, 0, 8, 0, 0, &H10, 0, 0, 0, 0, 0, 1, 0, 0, &H30, &H21, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0}
        Public Shared EOD As Byte() = {&HF0, 0, 8, 0, 0, 0, 0, &H10, 0, 0, 0, 0, 0, 5, 0, 0, &H34, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0}
        Public Shared NoSense As Byte() = {0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0}
        Public Shared IllegalOpCode As Byte() = {&H70, 0, 5, 0, 0, 0, 0, &H10, 0, 0, 0, 0, &H20, 0, 0, 0, &H94, &H10, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0}
    End Class
    <Xml.Serialization.XmlIgnore>
    Public Property idxFile As FileInfo
    <Xml.Serialization.XmlIgnore>
    Public ReadOnly Property idxPath As String
        Get
            If idxFile Is Nothing Then
                Return Nothing
            Else
                Return idxFile.DirectoryName
            End If
        End Get
    End Property

    Private _Position As New TapeUtils.PositionData
    Public Property Position As TapeUtils.PositionData
        Get
            If _Position.BlockNumber = 0 Then _Position.BOP = True Else _Position.BOP = False
            Return _Position
        End Get
        Set(value As TapeUtils.PositionData)
            _Position = value
            If value.BlockNumber = 0 Then _Position.BOP = True Else _Position.BOP = False
        End Set
    End Property

    Public Property DatesetLength As Long = 2473000

    Public Property PartitionMappingFile As New SerializableDictionary(Of Integer, String)
    Public Property PartitionEOD As New SerializableDictionary(Of Integer, Long)
    Public Property ValidLength As New SerializableDictionary(Of Integer, Long)
    <Xml.Serialization.XmlIgnore>
    Public Property ValidLengthLookupTable As New SerializableDictionary(Of Stream, Integer)
    Public Property VCR As UInt32

    <Xml.Serialization.XmlIgnore>
    Public ReadOnly Property VolumeChangeReference As UInt32
        Get
            If VolumeChanged Then
                VolumeChanged = False
                VCR += 1UI
            End If
            Return VCR
        End Get
    End Property
    <Xml.Serialization.XmlIgnore>
    Public Property VolumeChanged As Boolean
    Public Property MediumSN As String = ""
    Public Property MediumMFDate As String = ""
    Public Property MAM0800 As String = ""
    Public Property MAM0801 As String = ""
    Public Property MAM0802 As String = ""
    Public Property MAM0803 As String = ""
    Public Property MAM0804 As String = ""
    Public Property MAM0805 As Byte = 0
    Public Property MAM0806 As String = ""
    Public Property MAM0807 As String = ""
    Public Property MAM0808 As String = ""
    Public Property MAM0809 As New SerializableDictionary(Of Integer, String)
    Public Property MAM080B As String = ""
    Public Property MAM080C As New SerializableDictionary(Of Integer, Byte())
    Public Property FilemarkBlockIndex As New SerializableDictionary(Of Integer, List(Of Long))
    <Xml.Serialization.XmlIgnore>
    Public ReadOnly Property PartitionCount As Integer
        Get
            Return PartitionMappingFile.Keys.Count
        End Get
    End Property
    <Xml.Serialization.XmlIgnore>
    Public Property PartitionCountOverrideValue As Integer = 0

    <Xml.Serialization.XmlIgnore>
    Public ReadOnly Property CurrentStream As Stream
        Get
            Return PartitionMappingStream(Position.PartitionNumber)
        End Get
    End Property
    <Xml.Serialization.XmlIgnore>
    Public ReadOnly Property CurrentStreamValidLength As Long
        Get
            Return ValidLength(Position.PartitionNumber)
        End Get
    End Property
    <Xml.Serialization.XmlIgnore>
    Public ReadOnly Property CurrentFileOffset As Long
        Get
            Return CLng(DatesetLength * Position.SetNumber + CurrentIntraSetBlockOffset)
        End Get
    End Property

    Public Property Compressed As Boolean = True
    Public Property WriteProtect As Boolean = False
    Private PartitionMappingStream As New Dictionary(Of Integer, Stream)
    Public CurrentDatasetID As Integer = 0
    Public CurrentIntraSetBlockOffset As Integer = 0
    Public Const BlockHeaderLen As Integer = 16
    Public Function GetAvailableDiskSpace(Partition As Integer) As Long
        If PartitionMappingFile.ContainsKey(Partition) Then
            Dim path As String = IO.Path.Combine(idxPath, PartitionMappingFile(Partition))
            Dim drive = IO.Path.GetPathRoot(path)
            Dim driveInfo As New DriveInfo(drive)
            If driveInfo.IsReady Then
                Return driveInfo.AvailableFreeSpace
            Else
                Return 0
            End If
        Else
            Return 0
        End If
    End Function
    Public Function GetTotalDiskSpace(Partition As Integer) As Long
        If PartitionMappingFile.ContainsKey(Partition) Then
            Dim path As String = IO.Path.Combine(idxPath, PartitionMappingFile(Partition))
            Dim drive = IO.Path.GetPathRoot(path)
            Dim driveInfo As New DriveInfo(drive)
            If driveInfo.IsReady Then
                Return driveInfo.TotalSize
            Else
                Return 0
            End If
        Else
            Return 0
        End If
    End Function
    Public ReadOnly Property CurrentSetResidueBytes As Integer
        Get
            Return CInt(DatesetLength - CurrentIntraSetBlockOffset)
        End Get
    End Property

    Public Function GetSerializedString() As String
        Dim writer As New Xml.Serialization.XmlSerializer(GetType(TapeImage))
        Dim sb As New StringBuilder
        Dim t As New StringWriter(sb)
        writer.Serialize(t, Me)
        Return sb.ToString()
    End Function
    Public Shared Function FromXML(s As String) As TapeImage
        Dim reader As New Xml.Serialization.XmlSerializer(GetType(TapeImage))
        Dim t As TextReader = New StringReader(s)
        Return CType(reader.Deserialize(t), TapeImage)
    End Function
    Public Sub New()

    End Sub
    Public Sub New(filename As String, Optional ByVal PartitionCount As Integer = 1, Optional ByVal Compressed As Boolean = False)
        If File.Exists(filename) AndAlso (New FileInfo(filename)).Length = 0 Then
            File.Delete(filename)
        End If
        If File.Exists(filename) Then
            OpenFile(filename)
        Else
            CreateNewFile(filename, PartitionCount, Compressed)
        End If
    End Sub
    Public Sub OpenFile(filename As String)
        Dim idx As TapeImage = FromXML(File.ReadAllText(filename))
        idxFile = New FileInfo(filename)
        With idx
            DatesetLength = .DatesetLength
            PartitionMappingFile = .PartitionMappingFile
            FilemarkBlockIndex = .FilemarkBlockIndex
            PartitionEOD = .PartitionEOD
            PartitionMappingStream = New Dictionary(Of Integer, Stream)
            ValidLengthLookupTable = New SerializableDictionary(Of Stream, Integer)
            ValidLength = .ValidLength
            If ValidLength Is Nothing OrElse ValidLength.Keys.Count <> PartitionMappingFile.Keys.Count Then
                ValidLength = New SerializableDictionary(Of Integer, Long)
                For Each id As Integer In PartitionMappingFile.Keys
                    ValidLength.Add(id, 0)
                Next
            End If
            Position = New TapeUtils.PositionData()
            MediumSN = .MediumSN
            MediumMFDate = .MediumMFDate
            MAM0800 = .MAM0800
            MAM0801 = .MAM0801
            MAM0802 = .MAM0802
            MAM0803 = .MAM0803
            MAM0804 = .MAM0804
            MAM0805 = .MAM0805
            MAM0806 = .MAM0806
            MAM0807 = .MAM0807
            MAM0808 = .MAM0808
            MAM0809 = .MAM0809
            MAM080B = .MAM080B
            MAM080C = .MAM080C
            WriteProtect = .WriteProtect
            VCR = .VCR
        End With
        For Each id As Integer In PartitionMappingFile.Keys
            Dim ToAdd As Stream
            If PartitionMappingFile(id).ToLower().EndsWith(".lcgimg.zst") Then
                Compressed = True
                ToAdd = New ZstdSharp.CompressionStream(New FileStream(Path.Combine(idxPath, PartitionMappingFile(id)), FileMode.Open), 9, leaveOpen:=False)
            Else
                Compressed = False
                ToAdd = New FileStream(Path.Combine(idxPath, PartitionMappingFile(id)), FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1024 * 1024, FileOptions.SequentialScan)
            End If
            PartitionMappingStream.Add(id, ToAdd)
            If ValidLength(id) = 0 Then ValidLength(id) = ToAdd.Length
            ValidLengthLookupTable.Add(ToAdd, id)
        Next
        If idx.Position.PartitionNumber <> 0 Then
            ChangePartition(idx.Position.PartitionNumber, Nothing)
            LocateByBlock(CLng(idx.Position.BlockNumber), Nothing)
        ElseIf Position.PartitionNumber <> 0 OrElse idx.Position.BlockNumber <> 0 Then
            LocateByBlock(CLng(idx.Position.BlockNumber), Nothing)
        End If
    End Sub
    Public Sub OpenStream(idx As TapeImage, partitions As List(Of Stream))
        With idx
            DatesetLength = .DatesetLength
            PartitionMappingFile = .PartitionMappingFile
            FilemarkBlockIndex = .FilemarkBlockIndex
            PartitionEOD = .PartitionEOD
            Position = .Position
            PartitionMappingStream = New Dictionary(Of Integer, Stream)
        End With
        For i As Integer = 0 To partitions.Count - 1
            PartitionMappingStream.Add(i, partitions(i))
        Next
        If Position.PartitionNumber <> 0 Then
            ChangePartition(Position.PartitionNumber, Nothing)
            LocateByBlock(CLng(Position.BlockNumber), Nothing)
        ElseIf Position.PartitionNumber <> 0 OrElse Position.BlockNumber <> 0 Then
            LocateByBlock(CLng(Position.BlockNumber), Nothing)
        End If
    End Sub
    Public Sub CreateNewFile(filename As String, Optional ByVal PartitionCount As Integer = 1, Optional ByVal Compressed As Boolean = False)
        idxFile = New FileInfo(filename)
        Dim name As String = idxFile.Name.Substring(0, idxFile.Name.Length - idxFile.Extension.Length)
        Me.Compressed = Compressed
        PartitionMappingFile = New SerializableDictionary(Of Integer, String)
        PartitionMappingStream = New Dictionary(Of Integer, Stream)
        FilemarkBlockIndex = New SerializableDictionary(Of Integer, List(Of Long))
        PartitionEOD = New SerializableDictionary(Of Integer, Long)
        MAM0809 = New SerializableDictionary(Of Integer, String)
        MAM080C = New SerializableDictionary(Of Integer, Byte())
        Position = New TapeUtils.PositionData()
        ValidLength = New SerializableDictionary(Of Integer, Long)
        ValidLengthLookupTable = New SerializableDictionary(Of Stream, Integer)
        For i As Integer = 0 To PartitionCount - 1
            Dim imgfilename As String = If(Me.Compressed, $"{name}.{i}.lcgimg.zst", $"{name}.{i}.lcgimg")
            PartitionMappingFile.Add(i, imgfilename)
            File.Create(Path.Combine(idxPath, imgfilename)).Close()
            Dim streamtoadd As Stream
            If Me.Compressed Then
                streamtoadd = New ZstdSharp.CompressionStream(New FileStream(Path.Combine(idxPath, imgfilename), FileMode.Open), 9, leaveOpen:=False)
            Else
                streamtoadd = New FileStream(Path.Combine(idxPath, imgfilename), FileMode.Open)
            End If
            PartitionMappingStream.Add(i, streamtoadd)
            ValidLength.Add(i, 0)
            ValidLengthLookupTable.Add(streamtoadd, i)
            FilemarkBlockIndex.Add(i, New List(Of Long))
            MAM0809.Add(i, "")
            MAM080C.Add(i, {})
            PartitionEOD.Add(i, 0)
        Next
        VCR = 0
        MediumSN = $"LV{(New Guid()).ToString().ToUpper().Substring(0, 8)}"
        MediumMFDate = Now.ToString("yyyyMMdd")
        File.WriteAllText(filename, GetSerializedString())
    End Sub

    Public Sub ResetPartitionNumber(PartitionCount As Integer)
        If WriteProtect Then
            Exit Sub
        End If
        CloseFile()
        Dim name As String = idxFile.Name.Substring(0, idxFile.Name.Length - idxFile.Extension.Length)
        PartitionMappingFile = New SerializableDictionary(Of Integer, String)
        PartitionMappingStream = New Dictionary(Of Integer, Stream)
        FilemarkBlockIndex = New SerializableDictionary(Of Integer, List(Of Long))
        PartitionEOD = New SerializableDictionary(Of Integer, Long)
        Position = New TapeUtils.PositionData()
        For i As Integer = 0 To PartitionCount - 1
            Dim imgfilename As String = If(Compressed, $"{name}.{i}.lcgimg.zst", $"{name}.{i}.lcgimg")
            PartitionMappingFile.Add(i, imgfilename)
            File.Create(Path.Combine(idxPath, imgfilename)).Close()
            If Compressed Then
                PartitionMappingStream.Add(i, New ZstdSharp.CompressionStream(New FileStream(Path.Combine(idxPath, imgfilename), FileMode.Open), 9, leaveOpen:=False))
            Else
                PartitionMappingStream.Add(i, New FileStream(Path.Combine(idxPath, imgfilename), FileMode.Open))
            End If
            FilemarkBlockIndex.Add(i, New List(Of Long))
            PartitionEOD.Add(i, 0)
        Next
        VolumeChanged = True
    End Sub
    Public Sub ReOpen()
        CloseFile()
        OpenFile(idxFile.FullName)
    End Sub
    Public Sub CloseFile()
        Try
            If VolumeChanged Then
                VolumeChanged = False
                VCR = CUInt(VCR + 1)
            End If
            File.WriteAllText(idxFile.FullName, GetSerializedString())
            For i As Integer = 0 To PartitionCount - 1
                PartitionMappingStream(i).Close()
            Next
        Catch ex As Exception

        End Try
    End Sub
    Public Function HandleSCSICommand(commandBytes As Byte(), Param As Byte(), dataIn As Byte, dataLen As Integer, ByRef Response As Byte(), ByRef sense As Byte()) As Boolean
        'Handle SCSI Command
        Select Case commandBytes(0)
            Case &H0 'TEST UNIT READY
                Response = {}
                sense = SenseData.NoSense
                Return True
            Case &H4  'FORMAT
                ResetPartitionNumber(If(PartitionCountOverrideValue > 0, PartitionCountOverrideValue, PartitionCount))
                sense = SenseData.NoSense
            Case &H8  'READ6
                Response = ReadBlock(sense)
                Dim diffbyte As Integer = dataLen - Response.Length
                If Response.Length > 0 AndAlso diffbyte <> 0 Then
                    Dim r2(dataLen - 1) As Byte
                    Array.Copy(Response, r2, Math.Min(Response.Length, dataLen))
                    Response = r2
                    'sense ILI
                    sense(0) = &HF0
                    sense(2) = &H20
                    sense(3) = CByte(&HFF And (diffbyte >> 24))
                    sense(4) = CByte(&HFF And (diffbyte >> 16))
                    sense(5) = CByte(&HFF And (diffbyte >> 8))
                    sense(6) = CByte(&HFF And diffbyte)
                    sense(7) = &H10
                    sense(16) = &H2C
                    If diffbyte > 0 Then sense(17) = &H73 Else sense(17) = &H72
                End If
                If Response.Length <> dataLen Then ReDim Preserve Response(dataLen)
            Case &HA  'WRITE6
                If Param.Length <> dataLen Then ReDim Preserve Param(dataLen - 1)
                If WriteProtect Then
                    sense = SenseData.MediumError
                Else
                    sense = SenseData.NoSense
                End If
                WriteBlock(Param, dataLen)
            Case &H10 'WRITE FILEMARKS
                Dim FMCount As Integer = 0
                For i As Integer = 2 To 4
                    FMCount <<= 8
                    FMCount = FMCount Or commandBytes(i)
                Next
                WriteFilemark(FMCount)
                If WriteProtect AndAlso FMCount > 0 Then
                    sense = SenseData.MediumError
                Else
                    sense = SenseData.NoSense
                End If
            Case &H12 'INQUIRY
                ReDim Response(dataLen - 1)
                Dim EVPD As Byte = CByte(&H1 And commandBytes(1))
                Dim PageCode As Byte = commandBytes(2)
                Dim AllocLen As Integer = commandBytes(3)
                AllocLen <<= 8
                AllocLen = AllocLen Or commandBytes(4)
                sense = SenseData.NoSense
                If EVPD = 0 Then
                    Select Case PageCode
                        Case 0
                            Response = {&H1, &H80, &H6, &H2, &H5B, &H11, &H10, &H2, &H48, &H50, &H20, &H20, &H20, &H20, &H20, &H20,
                                        &H55, &H6C, &H74, &H72, &H69, &H75, &H6D, &H20, &H36, &H2D, &H53, &H43, &H53, &H49, &H20, &H20,
                                        &H32, &H35, &H4D, &H57, &H0, &H0, &H0, &H0, &H1, &H5C, &HD, &H0, &H0, &H0, &H0, &H0,
                                        &H0, &H0, &H0, &H0, &H0, &H0, &H0, &H0, &H0, &H0, &H0, &H90, &HA, &H11, &HD, &H7D,
                                        &HD, &HBC, &H13, &H1C, &H13, &H3C, &H4, &H63, &H5, &H20, &H0, &H0, &H0, &H0, &H0, &H0,
                                        &H0, &H0, &H0, &H0, &H0, &H0, &H0, &H0, &H0, &H0, &H0, &H0, &H0, &H0, &H0, &H0}
                        Case Else
                            sense = {&H70, &H0, &H5, &H0, &H0, &H0, &H0, &H10, &H0, &H0, &H0, &H0, &H24, &H0, &H0, &HCF,
                                     &H0, &H2, &H0, &H0, &H0, &H0, &H0, &H0, &H0, &H0, &H0, &H0, &H0, &H0, &H0, &H0}
                    End Select
                Else
                    Select Case PageCode
                        Case &H0 'Supported Vital Product Pages page
                            Response = {&H1, &H0, &H0, &H15, &H0, &H80, &H83, &H85, &H86, &H87, &H88, &HB0, &HB1, &HB2, &HB3, &HB4,
                                        &HC0, &HC1, &HC2, &HC3, &HC4, &HC5, &HC8, &HCC, &HD0}
                        Case &H80 'Unit Serial Number page
                            Response = {&H1, &H80, 0, &HA}
                            Response = Response.Concat(Encoding.ASCII.GetBytes($"LT{ApplicationWheels.Build}")).ToArray()
                        Case &H83 'Device Identification page
                            Response = {&H1, &H83, &H0, &H5A, &H1, &H3, &H0, &H8, &H51, &H40, &H2E, &HC0, &H12, &H9, &H2D, &HF6,
                                        &H1, &H93, &H0, &H8, &H51, &H40, &H2E, &HC0, &H12, &H9, &H2D, &HF4, &H1, &H94, &H0, &H4,
                                        &H0, &H0, &H0, &H1, &H1, &H95, &H0, &H4, &H0, &H0, &H0, &H0, &H1, &HA3, &H0, &H8,
                                        &H51, &H40, &H2E, &HC0, &H12, &H9, &H2D, &HF6, &H2, &HA1, &H0, &H22, &H48, &H50, &H20, &H20,
                                        &H20, &H20, &H20, &H20, &H55, &H6C, &H74, &H72, &H69, &H75, &H6D, &H20, &H36, &H2D, &H53, &H43,
                                        &H53, &H49, &H20, &H20}
                            Response = Response.Concat(Encoding.ASCII.GetBytes($"LT{ApplicationWheels.Build}")).ToArray()
                        Case &H85 'Management Network Address page
                            Response = {&H1, &H85, 0, 0}
                        Case &H86 'Extended Inquiry Data page
                            Response = {&H1, &H86, 0, &H3C, &H88, 1, 0, 1}
                            ReDim Preserve Response(&H3C + 4 - 1)
                        Case &H87 'Mode Page Policy VPD page
                            Response = {&H1, &H87, &H0, &H14, &H3F, &HFF, &H0, &H0, &H2, &H0, &H80, &H0, &H18, &H0, &H80, &H0,
                                        &H19, &H0, &H80, &H0, &HA, &HF0, &H3, &H0}
                        Case &H88
                            Response = {&H1, &H88, &H0, &H30, &H0, &H0, &H0, &H1, &H0, &H0, &H0, &H0, &H0, &H0, &H0, &HC,
                                        &H1, &H93, &H0, &H8, &H51, &H40, &H2E, &HC0, &H12, &H9, &H2D, &HF4, &H0, &H0, &H0, &H2,
                                        &H0, &H0, &H0, &H0, &H0, &H0, &H0, &HC, &H1, &H93, &H0, &H8, &H51, &H40, &H2E, &HC0,
                                        &H12, &H9, &H2D, &HF5}
                        Case &HB0
                            Response = {&H1, &HB0, 0, 4, 1, 0, 0, 0}
                        Case &HB1
                            Response = {&H1, &HB1, 0, &HA}
                            Response = Response.Concat(Encoding.ASCII.GetBytes($"LT{ApplicationWheels.Build}")).ToArray()
                        Case &HB2
                            Response = {&H1, &HB2, 0, 8, &HFE, &HFF, &HFF, &HFF, &HFE, 0, &HFF, &HF0}
                        Case &HB3
                            Response = {&H1, &HB3, 0, &H10}
                            Response = Response.Concat(Encoding.ASCII.GetBytes($"LCG{ApplicationWheels.Build}")).ToArray()
                            ReDim Preserve Response(&H14 - 1)
                        Case &HB4
                            Response = {&H1, &HB4, 0, 4, &HFF, &HFF, &HFF, &HFF}
                        Case &HC0
                            Response = {&H1, &HC0, &H0, &H5C, &H43, &H6F, &H6D, &H70, &H6F, &H6E, &H65, &H6E, &H74, &H20, &H3D, &H20,
                                        &H46, &H69, &H72, &H6D, &H77, &H61, &H72, &H65, &H20, &H20, &H20, &H20, &H20, &H20, &H56, &H65,
                                        &H72, &H73, &H69, &H6F, &H6E, &H20, &H3D, &H20, &H30, &H31, &H39, &H2E, &H37, &H34, &H33, &H20,
                                        &H20, &H44, &H61, &H74, &H65, &H20, &H3D, &H20, &H32, &H30, &H31, &H36, &H2F, &H31, &H30, &H2F,
                                        &H31, &H33, &H2D, &H31, &H35, &H3A, &H34, &H32, &H3A, &H56, &H61, &H72, &H69, &H61, &H6E, &H74,
                                        &H20, &H3D, &H20, &H30, &H78, &H30, &H30, &H30, &H30, &H30, &H30, &H31, &H34, &H20, &H20, &H20}
                        Case &HC1
                            Response = {&H1, &HC1, &H0, &H5C, &H43, &H6F, &H6D, &H70, &H6F, &H6E, &H65, &H6E, &H74, &H20, &H3D, &H20,
                                        &H48, &H61, &H72, &H64, &H77, &H61, &H72, &H65, &H20, &H20, &H20, &H20, &H20, &H20, &H56, &H65,
                                        &H72, &H73, &H69, &H6F, &H6E, &H20, &H3D, &H20, &H48, &H2F, &H57, &H2E, &H52, &H45, &H56, &H20,
                                        &H20, &H44, &H61, &H74, &H65, &H20, &H3D, &H20, &H32, &H30, &H31, &H32, &H2F, &H30, &H31, &H2F,
                                        &H30, &H31, &H20, &H30, &H30, &H3A, &H30, &H31, &H20, &H56, &H61, &H72, &H69, &H61, &H6E, &H74,
                                        &H20, &H3D, &H20, &H30, &H78, &H38, &H30, &H30, &H32, &H30, &H35, &H30, &H30, &H20, &H20, &H20}
                        Case &HC2
                            Response = {&H1, &HC2, &H0, &H5C, &H43, &H6F, &H6D, &H70, &H6F, &H6E, &H65, &H6E, &H74, &H20, &H3D, &H20,
                                        &H50, &H43, &H41, &H20, &H20, &H20, &H20, &H20, &H20, &H20, &H20, &H20, &H20, &H20, &H56, &H65,
                                        &H72, &H73, &H69, &H6F, &H6E, &H20, &H3D, &H20, &H50, &H43, &H41, &H2E, &H4F, &H4E, &H45, &H20,
                                        &H20, &H44, &H61, &H74, &H65, &H20, &H3D, &H20, &H32, &H30, &H31, &H32, &H2F, &H30, &H31, &H2F,
                                        &H30, &H31, &H20, &H31, &H32, &H3A, &H31, &H32, &H20, &H56, &H61, &H72, &H69, &H61, &H6E, &H74,
                                        &H20, &H3D, &H20, &H50, &H43, &H41, &H20, &H56, &H61, &H72, &H69, &H61, &H6E, &H74, &H20, &H20}
                        Case &HC3
                            Response = {&H1, &HC3, &H0, &H5C, &H43, &H6F, &H6D, &H70, &H6F, &H6E, &H65, &H6E, &H74, &H20, &H3D, &H20,
                                        &H4D, &H65, &H63, &H68, &H61, &H6E, &H69, &H73, &H6D, &H20, &H20, &H20, &H20, &H20, &H56, &H65,
                                        &H72, &H73, &H69, &H6F, &H6E, &H20, &H3D, &H20, &H4D, &H43, &H48, &H2E, &H56, &H45, &H52, &H20,
                                        &H20, &H44, &H61, &H74, &H65, &H20, &H3D, &H20, &H32, &H30, &H31, &H32, &H2F, &H30, &H31, &H2F,
                                        &H30, &H31, &H20, &H31, &H32, &H3A, &H31, &H32, &H20, &H56, &H61, &H72, &H69, &H61, &H6E, &H74,
                                        &H20, &H3D, &H20, &H4D, &H65, &H63, &H68, &H20, &H56, &H61, &H72, &H69, &H61, &H6E, &H74, &H20}
                        Case &HC4
                            Response = {&H1, &HC4, &H0, &H5C, &H43, &H6F, &H6D, &H70, &H6F, &H6E, &H65, &H6E, &H74, &H20, &H3D, &H20,
                                        &H48, &H65, &H61, &H64, &H20, &H41, &H73, &H73, &H79, &H20, &H20, &H20, &H20, &H20, &H56, &H65,
                                        &H72, &H73, &H69, &H6F, &H6E, &H20, &H3D, &H20, &H48, &H45, &H41, &H2E, &H56, &H45, &H52, &H20,
                                        &H20, &H44, &H61, &H74, &H65, &H20, &H3D, &H20, &H32, &H30, &H31, &H32, &H2F, &H30, &H31, &H2F,
                                        &H30, &H31, &H20, &H31, &H32, &H3A, &H31, &H32, &H20, &H56, &H61, &H72, &H69, &H61, &H6E, &H74,
                                        &H20, &H3D, &H20, &H48, &H65, &H61, &H64, &H20, &H56, &H61, &H72, &H69, &H61, &H6E, &H74, &H20}
                        Case &HC5
                            Response = {&H1, &HC5, &H0, &H5C, &H43, &H6F, &H6D, &H70, &H6F, &H6E, &H65, &H6E, &H74, &H20, &H3D, &H20,
                                        &H41, &H43, &H49, &H20, &H20, &H20, &H20, &H20, &H20, &H20, &H20, &H20, &H20, &H20, &H56, &H65,
                                        &H72, &H73, &H69, &H6F, &H6E, &H20, &H3D, &H20, &H30, &H30, &H34, &H2E, &H34, &H30, &H30, &H20,
                                        &H20, &H44, &H61, &H74, &H65, &H20, &H3D, &H20, &H32, &H30, &H31, &H36, &H2F, &H31, &H30, &H2F,
                                        &H31, &H33, &H2D, &H31, &H35, &H3A, &H34, &H32, &H3A, &H56, &H61, &H72, &H69, &H61, &H6E, &H74,
                                        &H20, &H3D, &H20, &H30, &H78, &H30, &H30, &H30, &H30, &H30, &H30, &H31, &H34, &H20, &H20, &H20}
                        Case &HC8
                            Response = {&H1, &HC8, &H0, &H4, &H6, 0, 0, 0, 0, 0, 0}
                        Case &HCC
                            Response = {&H1, &HCC, &H0, &H20, &H0, &H0, &H0, &H2, &H48, &H50, &H20, &H20, &H20, &H20, &H20, &H20,
                                        &H55, &H4C, &H54, &H52, &H49, &H55, &H4D, &H36, &H32, &H35, &H30, &H20, &H44, &H52, &H56, &H20,
                                        &H32, &H35, &H4D, &H57}
                        Case &HD0
                            Response = {&H1, &HD0, &H0, &H10, &H0, &HF, &H82, &H0, &H9, &H55, &HFF, &HFF, &H0, &HB4, &H0, &H0,
                                        &H0, &H0, &H0, &H0}
                        Case Else
                            sense = {&H70, &H0, &H5, &H0, &H0, &H0, &H0, &H10, &H0, &H0, &H0, &H0, &H24, &H0, &H0, &HCF,
                                     &H0, &H2, &H0, &H0, &H0, &H0, &H0, &H0, &H0, &H0, &H0, &H0, &H0, &H0, &H0, &H0}
                    End Select
                End If
                ReDim Preserve Response(dataLen - 1)
            Case &H19 'ERASE
                [Erase]()
                If WriteProtect Then
                    sense = SenseData.MediumError
                Else
                    sense = SenseData.NoSense
                End If
            Case &H16, &H17, &H1E
                Response = {}
                sense = SenseData.NoSense
            Case &H15, &H55 'MODE SELECT
                Dim PF As Byte = CByte((commandBytes(1) >> 4) And 1)
                Dim ParamLen As Integer = 0

                If commandBytes(0) = &H15 Then
                    ParamLen = commandBytes(4)
                Else
                    ParamLen = commandBytes(7)
                    ParamLen <<= 8
                    ParamLen = ParamLen Or commandBytes(8)
                End If
                ReDim Preserve Param(ParamLen)
                If PF <> 0 Then
                    Dim BDL As Integer
                    If commandBytes(0) = &H15 Then
                        BDL = commandBytes(3)
                        Param = Param.Skip(4 + BDL).ToArray()
                    Else
                        BDL = commandBytes(6)
                        BDL <<= 8
                        BDL = BDL Or commandBytes(7)
                        Param = Param.Skip(8 + BDL).ToArray()
                    End If
                    Dim i As Integer = 0
                    While i < Param.Length - 1
                        Dim PageCode As Integer = Param(i)
                        Dim PageLen As Integer = Param(i + 1)
                        If i + PageLen + 1 > Param.Length - 1 Then
                            Exit While
                        End If
                        Dim PageData(PageLen + 1) As Byte
                        Array.Copy(Param, i, PageData, 0, PageLen + 2)
                        Select Case PageCode
                            Case &H1, &H2, &HA, &HF, &H10, &H18, &H19, &H23
                                sense = SenseData.NoSense
                            Case &H11
                                PartitionCountOverrideValue = PageData(3) + 1
                                sense = SenseData.NoSense
                            Case Else
                                sense = SenseData.IllegalOpCode
                        End Select
                        i += 2 + PageLen
                    End While
                End If
            Case &H1A, &H5A 'MODE SENSE
                Dim DBD As Byte = CByte((commandBytes(1) >> 3) And 1)
                Dim PC As Byte = CByte((commandBytes(2) >> 6) And &B11)
                Dim PageCode As Byte = CByte(commandBytes(2) And &B111111)
                Dim SubCode As Byte = commandBytes(3)
                Dim AllocLen As Integer = 0
                If commandBytes(0) = &H1A Then
                    AllocLen = commandBytes(4)
                Else
                    AllocLen = commandBytes(7)
                    AllocLen <<= 8
                    AllocLen = AllocLen Or commandBytes(8)
                End If
                Dim result As New List(Of Byte)
                result.AddRange({0, 0, &H10, 0})
                If DBD = 0 Then
                    result(3) = 8
                    result.AddRange({&H5A, 0, 0, 0, 0, 0, 0, 0})
                End If
                Select Case PageCode
                    Case &H11
                        Dim availLen As Integer = (255 - result.Count + 1 - 8) \ 2
                        availLen = 4
                        Dim pCount As Byte = CByte(Math.Min(availLen, PartitionCount))
                        Dim PSize(pCount - 1) As UInt16
                        result.AddRange({&H11, CByte((pCount * 2) + 6), CByte(availLen - 1), CByte(pCount - 1), &H3C, 3, 9, 0})
                        For i As Integer = 0 To pCount - 1
                            PSize(i) = CUShort(Math.Min(UInt16.MaxValue, GetTotalDiskSpace(i) \ 1000000000))
                            result.AddRange({CByte(PSize(i) >> 8 And &HFF), CByte(PSize(i) And &HFF)})
                        Next
                    Case &H1D
                        result.AddRange({&H1D, &H1E, 0, 0, 1, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                                         0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0})
                    Case Else
                        Response = {}
                        sense = {&H70, &H0, &H5, &H0, &H0, &H0, &H0, &H10, &H0, &H0, &H0, &H0, &H24, &H0, &H0, &HCD,
                                 &H0, &H2, &H0, &H0, &H0, &H0, &H0, &H0, &H0, &H0, &H0, &H0, &H0, &H0, &H0, &H0}
                        Return True
                End Select
                result(0) = CByte(result.Count - 1)
                Response = result.ToArray()
                ReDim Preserve Response(AllocLen - 1)
                sense = SenseData.NoSense
            Case &H1B 'LOEJ
                LocateByBlock(0, sense)
            Case &H34 'READ POSITION
                Dim currpos = ReadPosition()
                Dim ServiceAction As Byte = CByte(commandBytes(1) And &B11111)
                Dim AllocLen As Integer = commandBytes(7)
                AllocLen <<= 8
                AllocLen = AllocLen Or commandBytes(8)
                sense = SenseData.NoSense
                Dim currentpos = ReadPosition()
                Select Case ServiceAction
                    Case 0
                        Response = {CByte((CByte(currentpos.BOP) << 7) Or &B110000),
                            currentpos.PartitionNumber,
                            0, 0,
                            CByte(CLng((currentpos.BlockNumber >> 24)) And &HFF),
                            CByte(CLng((currentpos.BlockNumber >> 16)) And &HFF),
                            CByte(CLng((currentpos.BlockNumber >> 8)) And &HFF),
                            CByte(CLng(currentpos.BlockNumber) And &HFF),
                            CByte(CLng((currentpos.BlockNumber >> 24)) And &HFF),
                            CByte(CLng((currentpos.BlockNumber >> 16)) And &HFF),
                            CByte(CLng((currentpos.BlockNumber >> 8)) And &HFF),
                            CByte(CLng(currentpos.BlockNumber) And &HFF),
                            0, 0, 0, 0, 0, 0, 0, 0}
                    Case 6
                        Response = {CByte(CShort(currentpos.BOP) << 7),
                            0, 0, 0,
                            CByte((currentpos.PartitionNumber >> 24) And &HFF),
                            CByte((currentpos.PartitionNumber >> 16) And &HFF),
                            CByte((currentpos.PartitionNumber >> 8) And &HFF),
                            CByte(currentpos.PartitionNumber And &HFF),
                            CByte(CLng((currentpos.BlockNumber >> 56)) And &HFF),
                            CByte(CLng((currentpos.BlockNumber >> 48)) And &HFF),
                            CByte(CLng((currentpos.BlockNumber >> 40)) And &HFF),
                            CByte(CLng((currentpos.BlockNumber >> 32)) And &HFF),
                            CByte(CLng((currentpos.BlockNumber >> 24)) And &HFF),
                            CByte(CLng((currentpos.BlockNumber >> 16)) And &HFF),
                            CByte(CLng((currentpos.BlockNumber >> 8)) And &HFF),
                            CByte(CLng(currentpos.BlockNumber) And &HFF),
                            CByte(CLng((currentpos.FileNumber >> 56)) And &HFF),
                            CByte(CLng((currentpos.FileNumber >> 48)) And &HFF),
                            CByte(CLng((currentpos.FileNumber >> 40)) And &HFF),
                            CByte(CLng((currentpos.FileNumber >> 32)) And &HFF),
                            CByte(CLng((currentpos.FileNumber >> 24)) And &HFF),
                            CByte(CLng((currentpos.FileNumber >> 16)) And &HFF),
                            CByte(CLng((currentpos.FileNumber >> 8)) And &HFF),
                            CByte(CLng(currentpos.FileNumber) And &HFF),
                            0, 0, 0, 0, 0, 0, 0, 0}
                    Case 8
                        Response = {CByte((CShort(currentpos.BOP) << 7) Or &B11000),
                            currentpos.PartitionNumber, 0, &H1C,
                            0, 0, 0, 0,
                            CByte(CLng((currentpos.BlockNumber >> 56)) And &HFF),
                            CByte(CLng((currentpos.BlockNumber >> 48)) And &HFF),
                            CByte(CLng((currentpos.BlockNumber >> 40)) And &HFF),
                            CByte(CLng((currentpos.BlockNumber >> 32)) And &HFF),
                            CByte(CLng((currentpos.BlockNumber >> 24)) And &HFF),
                            CByte(CLng((currentpos.BlockNumber >> 16)) And &HFF),
                            CByte(CLng((currentpos.BlockNumber >> 8)) And &HFF),
                            CByte(CLng(currentpos.BlockNumber) And &HFF),
                            CByte(CLng((currentpos.BlockNumber >> 56)) And &HFF),
                            CByte(CLng((currentpos.BlockNumber >> 48)) And &HFF),
                            CByte(CLng((currentpos.BlockNumber >> 40)) And &HFF),
                            CByte(CLng((currentpos.BlockNumber >> 32)) And &HFF),
                            CByte(CLng((currentpos.BlockNumber >> 24)) And &HFF),
                            CByte(CLng((currentpos.BlockNumber >> 16)) And &HFF),
                            CByte(CLng((currentpos.BlockNumber >> 8)) And &HFF),
                            CByte(CLng(currentpos.BlockNumber) And &HFF),
                            0, 0, 0, 0, 0, 0, 0, 0}
                    Case Else
                        sense = SenseData.IllegalOpCode
                End Select
                ReDim Preserve Response(AllocLen - 1)
            Case &H2B, &H92 'LOCATE
                Response = {}
                sense = SenseData.NoSense
                Dim CP As Byte = CByte((commandBytes(1) >> 1) And 1)
                Dim DestType As Byte = 0
                Dim LI As Long = 0
                Dim Partition As Byte
                If commandBytes(0) = &H2B Then
                    For i As Integer = 3 To 6
                        LI <<= 8
                        LI = LI Or commandBytes(i)
                    Next
                    Partition = commandBytes(8)
                Else
                    DestType = CByte((commandBytes(1) >> 3) And &B111)
                    Partition = commandBytes(3)
                    For i As Integer = 4 To 11
                        LI <<= 8
                        LI = LI Or commandBytes(i)
                    Next
                End If
                Dim currpos = ReadPosition()
                If CP = 1 AndAlso currpos.PartitionNumber <> Partition Then
                    ChangePartition(Partition, sense)
                    If sense IsNot SenseData.NoSense Then
                        Return True
                    End If
                End If
                Select Case DestType
                    Case 0 'block
                        LocateByBlock(LI, sense)
                    Case 1 'filemark
                        LocateByFilemark(LI, sense)
                    Case &B11 'eod
                        LocateToEOD(sense)
                    Case Else
                        sense = SenseData.IllegalOpCode
                End Select
            Case &H1 'REWIND
                LocateByBlock(0, sense)
            Case &H11, &H91 'SPACE
                Response = {}
                sense = SenseData.NoSense
                Dim Code As Byte = CByte(commandBytes(1) And &B111)
                Dim Count As Long = 0
                If commandBytes(0) = &H11 Then
                    If (commandBytes(2) >> 7) = 1 Then Count = -1
                    For i As Integer = 2 To 4
                        Count <<= 8
                        Count = Count Or commandBytes(i)
                    Next
                Else
                    For i As Integer = 4 To 11
                        Count <<= 8
                        Count = Count Or commandBytes(i)
                    Next
                End If
                Select Case Code
                    Case 0 'block
                        If Count <> 0 Then LocateByBlock(CLng(ReadPosition().BlockNumber + Count), sense)
                    Case 1 'filemark
                        If Count <> 0 Then LocateByFilemark(CLng(ReadPosition().FileNumber + Count), sense)
                    Case 3 'eod
                        LocateToEOD(sense)
                    Case Else
                        sense = SenseData.IllegalOpCode
                End Select
            Case &H8C 'READ ATTRIBUTE
                ReDim Response(dataLen - 1)
                Dim ServiceAction As Byte = CByte(commandBytes(1) And &B11111)
                Dim Partition As Byte = commandBytes(7)
                If Partition >= PartitionCount Then
                    sense = SenseData.IllegalOpCode
                    Return True
                End If
                Dim FID As Integer = commandBytes(8)
                FID <<= 8
                FID = FID Or commandBytes(9)
                Dim allocLen As Integer = 0
                For i As Integer = 10 To 13
                    allocLen <<= 8
                    allocLen = allocLen Or commandBytes(i)
                Next

                Select Case ServiceAction
                    Case 0
                        Dim paramData As New List(Of Byte())
                        Dim remain As Long = GetAvailableDiskSpace(Partition) \ 1000000
                        paramData.Add({0, 0, &H80, 0, 8,
                                    CByte((remain >> 52) And &HFF),
                                    CByte((remain >> 48) And &HFF),
                                    CByte((remain >> 40) And &HFF),
                                    CByte((remain >> 32) And &HFF),
                                    CByte((remain >> 24) And &HFF),
                                    CByte((remain >> 16) And &HFF),
                                    CByte((remain >> 8) And &HFF),
                                    CByte(remain And &HFF)})
                        Dim cap As Long = GetTotalDiskSpace(Partition) \ 1000000
                        paramData.Add({0, 1, &H80, 0, 8,
                                    CByte((cap >> 52) And &HFF),
                                    CByte((cap >> 48) And &HFF),
                                    CByte((cap >> 40) And &HFF),
                                    CByte((cap >> 32) And &HFF),
                                    CByte((cap >> 24) And &HFF),
                                    CByte((cap >> 16) And &HFF),
                                    CByte((cap >> 8) And &HFF),
                                    CByte(cap And &HFF)})
                        paramData.Add({0, 2, &H80, 0, 8, 0, 0, 0, 0, 0, 0, &H80, 0})
                        paramData.Add({0, 3, &H80, 0, 8, 0, 0, 0, 0, 0, 0, 0, 0})
                        paramData.Add({0, 4, &H80, 0, 8, 0, 0, 0, 0, 0, 0, 0, 0})
                        paramData.Add({0, 5, &H80, 0, 8, &H4C, &H54, &H4F, &H2D, &H43, &H56, &H45, &H20})
                        paramData.Add({0, 6, &H80, 0, 1, &H5A})
                        paramData.Add({0, 7, &H80, 0, 2, 0, 1})
                        paramData.Add({0, 8, &H81, 0, 0})
                        paramData.Add({0, 9, &H80, 0, 4,
                                      CByte((VolumeChangeReference >> 24) And &HFF),
                                      CByte((VolumeChangeReference >> 16) And &HFF),
                                      CByte((VolumeChangeReference >> 8) And &HFF),
                                      CByte(VolumeChangeReference And &HFF)})
                        Dim DeviceSN As Byte() = Encoding.ASCII.GetBytes($"LT{ApplicationWheels.Build}".PadRight(&H28).Substring(0, &H28))
                        paramData.Add((New Byte() {&H2, &HA, &H81, 0, &H28}).Concat(DeviceSN).ToArray())
                        paramData.Add((New Byte() {&H2, &HB, &H81, 0, &H28}).Concat(DeviceSN).ToArray())
                        paramData.Add((New Byte() {&H2, &HC, &H81, 0, &H28}).Concat(DeviceSN).ToArray())
                        paramData.Add((New Byte() {&H2, &HD, &H81, 0, &H28}).Concat(DeviceSN).ToArray())
                        paramData.Add({2, &H20, &H80, 0, 8, 0, 0, 0, 0, 0, 0, 0, 0})
                        paramData.Add({2, &H21, &H80, 0, 8, 0, 0, 0, 0, 0, 0, 0, 0})
                        paramData.Add({2, &H22, &H80, 0, 8, 0, 0, 0, 0, 0, 0, 0, 0})
                        paramData.Add({2, &H23, &H80, 0, 8, 0, 0, 0, 0, 0, 0, 0, 0})
                        paramData.Add({2, &H24, &H80, 0, 8, &HFF, &HFF, &HFF, &HFF, &HFF, &HFF, &HFF, &HFF})
                        paramData.Add({2, &H25, &H80, 0, 8, &HFF, &HFF, &HFF, &HFF, &HFF, &HFF, &HFF, &HFF})
                        paramData.Add((New Byte() {4, 0, &H81, 0, 8}).Concat(Encoding.ASCII.GetBytes($"LCGVT".PadRight(8).Substring(0, 8))).ToArray())
                        paramData.Add((New Byte() {4, 1, &H81, 0, &H20}).Concat(Encoding.ASCII.GetBytes(MediumSN.PadRight(&H20).Substring(0, &H20))).ToArray())
                        paramData.Add({4, 2, &H80, 0, 4, 0, 0, 3, &H4E})
                        paramData.Add({4, 3, &H80, 0, 4, 0, 0, 0, &H7F})
                        paramData.Add({4, 4, &H81, 0, 8, &H4C, &H54, &H4F, &H2D, &H43, &H56, &H45, &H20})
                        paramData.Add({4, 5, &H80, 0, 1, &H5A})
                        paramData.Add((New Byte() {4, 6, &H81, 0, 8}).Concat(Encoding.ASCII.GetBytes(MediumMFDate.PadRight(8).Substring(0, 8))).ToArray())
                        paramData.Add({4, 7, &H80, 0, 8, 0, 0, 0, 0, 0, 0, &H20, 0})
                        paramData.Add({4, 8, &H80, 0, 1, 0})
                        paramData.Add({4, 9, &H80, 0, 2, 0, 0})
                        paramData.Add((New Byte() {8, 0, &H1, 0, 8}).Concat(Encoding.ASCII.GetBytes(MAM0800.PadRight(8).Substring(0, 8))).ToArray())
                        paramData.Add((New Byte() {8, 1, &H1, 0, 32}).Concat(Encoding.ASCII.GetBytes(MAM0801.PadRight(32).Substring(0, 32))).ToArray())
                        paramData.Add((New Byte() {8, 2, &H1, 0, 8}).Concat(Encoding.ASCII.GetBytes(MAM0802.PadRight(8).Substring(0, 8))).ToArray())
                        paramData.Add((New Byte() {8, 3, &H2, 0, 160}).Concat(Encoding.ASCII.GetBytes(MAM0803.PadRight(160).Substring(0, 160))).ToArray())
                        paramData.Add((New Byte() {8, 4, &H1, 0, 12}).Concat(Encoding.ASCII.GetBytes(MAM0804.PadRight(12).Substring(0, 12))).ToArray())
                        paramData.Add({8, 5, 0, 0, 1, MAM0805})
                        paramData.Add((New Byte() {8, 6, &H1, 0, 32}).Concat(Encoding.ASCII.GetBytes(MAM0806.PadRight(32).Substring(0, 32))).ToArray())
                        paramData.Add((New Byte() {8, 7, &H2, 0, 80}).Concat(Encoding.ASCII.GetBytes(MAM0807.PadRight(80).Substring(0, 80))).ToArray())
                        paramData.Add((New Byte() {8, 8, &H2, 0, 160}).Concat(Encoding.ASCII.GetBytes(MAM0808.PadRight(160).Substring(0, 160))).ToArray())
                        If Not MAM0809.ContainsKey(Partition) Then MAM0809.Add(Partition, "")
                        paramData.Add((New Byte() {8, 9, &H1, 0, 16}).Concat(Encoding.ASCII.GetBytes(MAM0809(Partition).PadRight(16).Substring(0, 16))).ToArray())
                        paramData.Add((New Byte() {8, &HB, &H1, 0, 16}).Concat(Encoding.ASCII.GetBytes(MAM080B.PadRight(16).Substring(0, 16))).ToArray())
                        If Not MAM080C.ContainsKey(Partition) Then MAM080C.Add(Partition, {})
                        paramData.Add((New Byte() {8, &HC, &H0, 0, CByte(MAM080C(Partition).Length And &HFF)}).Concat(MAM080C(Partition)).ToArray())
                        paramData.Add((New Byte() {&H10, 0, &H80, 0, &H1C}).Concat(
                                      Encoding.ASCII.GetBytes(MediumSN.PadRight(4).Substring(0, 4) & ApplicationWheels.Build.PadRight(8).Substring(0, 8) & "LCGVT   ")).Concat(
                                      {0, 0, 0, 0, 0, &H10, 0, 0}).ToArray())
                        paramData.Add((New Byte() {&H10, 1, &H80, 0, &H18}).Concat(
                                      Encoding.ASCII.GetBytes(MediumSN.PadRight(4).Substring(0, 4) & ApplicationWheels.Build.PadRight(8).Substring(0, 8) & MediumSN.PadRight(10).Substring(0, 10))).Concat(
                                      {0, &H10}).ToArray())
                        Dim respData As New List(Of Byte)
                        For i As Integer = 0 To paramData.Count - 1
                            If FID <= ((CInt(paramData(i)(0)) << 8) Or CInt(paramData(i)(1))) Then
                                respData.AddRange(paramData(i))
                            End If
                        Next
                        Response = {CByte((respData.Count << 24) And &HFF),
                            CByte((respData.Count << 16) And &HFF),
                            CByte((respData.Count << 8) And &HFF),
                            CByte(respData.Count And &HFF)}
                        Response = Response.Concat(respData).ToArray()
                        ReDim Preserve Response(dataLen - 1)
                    Case Else
                        sense = SenseData.IllegalOpCode
                End Select
            Case &H8D 'WRITE ATTRIBUTE
                Dim Partition As Byte = commandBytes(7)
                If Partition >= PartitionCount Then
                    sense = SenseData.IllegalOpCode
                    Return True
                End If
                Dim allocLen As Integer = 0
                For i As Integer = 10 To 13
                    allocLen <<= 8
                    allocLen = allocLen Or commandBytes(i)
                Next
                If allocLen >= 4 Then
                    ReDim Preserve Param(allocLen - 1)
                    Dim ParamLen As Integer = 0
                    For i As Integer = 0 To 3
                        ParamLen <<= 8
                        ParamLen = ParamLen Or Param(i)
                    Next
                    If allocLen - 4 >= ParamLen Then
                        Dim offset As Integer = 4
                        While offset + 4 < allocLen - 1
                            Dim attriblen As Integer = Param(offset + 3)
                            attriblen <<= 8
                            attriblen = attriblen Or Param(offset + 4)
                            Dim attribID As Integer = Param(offset)
                            attribID <<= 8
                            attribID = attribID Or Param(offset + 1)
                            If attriblen > 0 Then
                                If offset + 4 + attriblen > allocLen Then Exit While
                                Dim attribvalue(attriblen - 1) As Byte
                                Array.Copy(Param, offset + 5, attribvalue, 0, attriblen)
                                Select Case attribID
                                    Case &H800
                                        MAM0800 = Encoding.ASCII.GetString(attribvalue).PadRight(8).Substring(0, 8)
                                    Case &H801
                                        MAM0801 = Encoding.ASCII.GetString(attribvalue).PadRight(32).Substring(0, 32)
                                    Case &H802
                                        MAM0802 = Encoding.ASCII.GetString(attribvalue).PadRight(8).Substring(0, 8)
                                    Case &H803
                                        MAM0803 = Encoding.ASCII.GetString(attribvalue).PadRight(160).Substring(0, 160)
                                    Case &H804
                                        MAM0804 = Encoding.ASCII.GetString(attribvalue).PadRight(12).Substring(0, 12)
                                    Case &H805
                                        MAM0805 = attribvalue(0)
                                    Case &H806
                                        MAM0806 = Encoding.ASCII.GetString(attribvalue).PadRight(32).Substring(0, 32)
                                    Case &H807
                                        MAM0807 = Encoding.ASCII.GetString(attribvalue).PadRight(80).Substring(0, 80)
                                    Case &H808
                                        MAM0808 = Encoding.ASCII.GetString(attribvalue).PadRight(160).Substring(0, 160)
                                    Case &H809
                                        If Not MAM0809.ContainsKey(Partition) Then MAM0809.Add(Partition, "")
                                        MAM0809(Partition) = Encoding.ASCII.GetString(attribvalue).PadRight(16).Substring(0, 16)
                                    Case &H80C
                                        If Not MAM080C.ContainsKey(Partition) Then MAM080C.Add(Partition, {})
                                        MAM080C(Partition) = attribvalue
                                End Select
                            End If
                            offset += attriblen + 5
                        End While
                    Else
                        sense = SenseData.IllegalOpCode
                    End If
                End If
                sense = SenseData.NoSense
            Case &H3C 'READ BUFFER
                Response = {}
                sense = SenseData.NoSense
            Case &H4D 'LOG SENSE
                Dim PC As Byte = CByte((commandBytes(2) >> 5) And &B11)
                Dim PageCode As Byte = CByte(commandBytes(2) And &B111111)
                Dim ParamPointer As Integer = commandBytes(5)
                ParamPointer <<= 8
                ParamPointer = ParamPointer Or commandBytes(6)
                Dim allocLen As Integer = commandBytes(7)
                allocLen <<= 8
                allocLen = allocLen Or commandBytes(8)
                sense = SenseData.NoSense
                Select Case PageCode
                    Case &H17 'VolumeStat
                        Dim paramData As New List(Of Byte())
                        paramData.Add({&H0, &H0, &H3, &H2, &H0, &H1})
                        paramData.Add({&H0, &H1, &H3, &H4, &H0, &H0, &H0, &H0})
                        paramData.Add({&H0, &H2, &H3, &H8, &H0, &H0, &H0, &H0, &H0, &H0, &H0, &H0})
                        paramData.Add({&H0, &H3, &H3, &H4, &H0, &H0, &H0, &H0})
                        paramData.Add({&H0, &H4, &H3, &H2, &H0, &H0})
                        paramData.Add({&H0, &H5, &H3, &H2, &H0, &H0})
                        paramData.Add({&H0, &H6, &H3, &H2, &H0, &H0})
                        paramData.Add({&H0, &H7, &H3, &H8, &H0, &H0, &H0, &H0, &H0, &H0, &H0, &H0})
                        paramData.Add({&H0, &H8, &H3, &H4, &H0, &H0, &H0, &H0})
                        paramData.Add({&H0, &H9, &H3, &H2, &H0, &H0})
                        paramData.Add({&H0, &HC, &H3, &H2, &H0, &H0})
                        paramData.Add({&H0, &HD, &H3, &H2, &H0, &H0})
                        paramData.Add({&H0, &HE, &H3, &H4, &H0, &H0, &H0, &H0})
                        paramData.Add({&H0, &HF, &H3, &H4, &H0, &H0, &H0, &H0})
                        paramData.Add({&H0, &H10, &H3, &H8, &H0, &H0, &H0, &H0, &H0, &H0, &H0, &H0})
                        paramData.Add({&H0, &H11, &H3, &H8, &H0, &H0, &H0, &H0, &H0, &H0, &H0, &H0})
                        paramData.Add({&H0, &H12, &H3, &H2, &H0, &H0})
                        paramData.Add({&H0, &H13, &H3, &H2, &H0, &H0})
                        paramData.Add({&H0, &H14, &H3, &H8, &H0, &H0, &H0, &H0, &H0, &H0, &H0, &H0})
                        paramData.Add({&H0, &H15, &H3, &H8, &H0, &H0, &H0, &H0, &H0, &H0, &H0, &H0})
                        Dim PRemain(3) As UInteger
                        Dim PSize(3) As UInteger
                        Dim pCount As Byte = CByte(Math.Min(2, PartitionCount))
                        For i As Integer = 0 To pCount - 1
                            PRemain(i) = CUInt(Math.Min(UInteger.MaxValue, GetAvailableDiskSpace(i) \ 1000000))
                            PSize(i) = CUInt(Math.Min(UInteger.MaxValue, GetTotalDiskSpace(i) \ 1000000))
                        Next
                        paramData.Add({&H0, &H16, &H3, &H4,
                                      CByte((PSize(0) >> 24) And &HFF),
                                      CByte((PSize(0) >> 16) And &HFF),
                                      CByte((PSize(0) >> 8) And &HFF),
                                      CByte(PSize(0) And &HFF)})
                        paramData.Add({&H0, &H17, &H3, &H4,
                                      CByte(((PSize(0) - PRemain(0)) >> 24) And &HFF),
                                      CByte(((PSize(0) - PRemain(0)) >> 16) And &HFF),
                                      CByte(((PSize(0) - PRemain(0)) >> 8) And &HFF),
                                      CByte((PSize(0) - PRemain(0)) And &HFF)})
                        paramData.Add({&H0, &H40, &H1, &HA, &H30, &H30, &H30, &H30, &H30, &H30, &H30, &H30, &H30, &H30})
                        paramData.Add({&H0, &H41, &H1, &H8, &H30, &H30, &H30, &H30, &H30, &H30, &H30, &H30})
                        paramData.Add({&H0, &H42, &H1, &H20, &H56, &H54, &H30, &H30, &H30, &H31, &H4C, &H36, &H20, &H20, &H20, &H20, &H20, &H20, &H20, &H20, &H20, &H20, &H20, &H20, &H20, &H20, &H20, &H20, &H20, &H20, &H20, &H20, &H20, &H20, &H20, &H20})
                        paramData.Add({&H0, &H43, &H1, &H8, &H52, &H43, &H47, &H20, &H20, &H20, &H20, &H20})
                        paramData.Add({&H0, &H44, &H1, &H4, &H55, &H31, &H30, &H37})
                        paramData.Add({&H0, &H45, &H1, &H9, &H55, &H6C, &H74, &H72, &H69, &H75, &H6D, &H2D, &H36})
                        paramData.Add((New Byte() {&H0, &H46, &H1, &H8}).Concat(Encoding.ASCII.GetBytes($"{ApplicationWheels.Build.PadRight(8, "0"c).Substring(0, 8)}")).ToArray())
                        paramData.Add({&H0, &H80, &H3, &H2, &H0, &H0})
                        paramData.Add({&H0, &H81, &H3, &H2, &H0, &H0})
                        paramData.Add({&H0, &H82, &H3, &H2, &H0, &H0})
                        paramData.Add({&H1, &H1, &H3, &H4, &H0, &H0, &H0, &H0})
                        paramData.Add({&H1, &H2, &H3, &H4, &H0, &H0, &H0, &H0})
                        paramData.Add({&H2, &H0, &H3, &H18, &HB, &H0, &H0, &H0, &H0, &H0, &HFF, &HFF, &HFF, &HFF, &HFF, &HFF, &HB, &H0, &H0, &H1, &H0, &H0, &HFF, &HFF, &HFF, &HFF, &HFF, &HFF})
                        paramData.Add({&H2, &H1, &H3, &H18, &HB, &H0, &H0, &H0, &H0, &H0, &HFF, &HFF, &HFF, &HFF, &HFF, &HFF, &HB, &H0, &H0, &H1, &H0, &H0, &HFF, &HFF, &HFF, &HFF, &HFF, &HFF})
                        paramData.Add({&H2, &H2, &H3, &H10,
                                      &H7, &H0, &H0, &H0,
                                      CByte((PSize(0) >> 24) And &HFF),
                                      CByte((PSize(0) >> 16) And &HFF),
                                      CByte((PSize(0) >> 8) And &HFF),
                                      CByte(PSize(0) And &HFF),
                                      &H7, &H0, &H0, &H1,
                                      CByte((PSize(1) >> 24) And &HFF),
                                      CByte((PSize(1) >> 16) And &HFF),
                                      CByte((PSize(1) >> 8) And &HFF),
                                      CByte(PSize(1) And &HFF)})
                        paramData.Add({&H2, &H3, &H3, &H10,
                                      &H7, &H0, &H0, &H0,
                                      CByte(((PSize(0) - PRemain(0)) >> 24) And &HFF),
                                      CByte(((PSize(0) - PRemain(0)) >> 16) And &HFF),
                                      CByte(((PSize(0) - PRemain(0)) >> 8) And &HFF),
                                      CByte((PSize(0) - PRemain(0)) And &HFF),
                                      &H7, &H0, &H0, &H1,
                                      CByte(((PSize(1) - PRemain(1)) >> 24) And &HFF),
                                      CByte(((PSize(1) - PRemain(1)) >> 16) And &HFF),
                                      CByte(((PSize(1) - PRemain(1)) >> 8) And &HFF),
                                      CByte((PSize(1) - PRemain(1)) And &HFF)})
                        paramData.Add({&H3, &H0, &H3, &H0})
                        paramData.Add({&HF0, &H0, &H3, &H2, &H0, &H1})
                        Dim RespList As New List(Of Byte)
                        RespList.AddRange({&H17, 0, 0, 0})
                        For i As Integer = 0 To paramData.Count - 1
                            Dim Code As Integer = paramData(i)(0)
                            Code <<= 8
                            Code = Code Or paramData(i)(1)
                            If ParamPointer > Code Then Continue For
                            RespList.AddRange(paramData(i))
                        Next
                        RespList(2) = CByte(((RespList.Count - 4) >> 8) And &HFF)
                        RespList(3) = CByte((RespList.Count - 4) And &HFF)
                        Response = RespList.ToArray()
                    Case &H31 'Capacity
                        Dim PRemain(3) As UInteger
                        Dim PSize(3) As UInteger
                        Dim pCount As Byte = CByte(Math.Min(4, PartitionCount))
                        For i As Integer = 0 To pCount - 1
                            PRemain(i) = CUInt(Math.Min(UInteger.MaxValue, GetAvailableDiskSpace(i) \ 1048576))
                            PSize(i) = CUInt(Math.Min(UInteger.MaxValue, GetTotalDiskSpace(i) \ 1048576))
                        Next
                        Response = {&H31, 0, 0, &H40,
                            0, 1, &H60, 4,
                            CByte((PRemain(0) >> 24) And &HFF),
                            CByte((PRemain(0) >> 16) And &HFF),
                            CByte((PRemain(0) >> 8) And &HFF),
                             CByte(PRemain(0) And &HFF),
                            0, 2, &H60, 4,
                            CByte((PRemain(1) >> 24) And &HFF),
                            CByte((PRemain(1) >> 16) And &HFF),
                            CByte((PRemain(1) >> 8) And &HFF),
                             CByte(PRemain(1) And &HFF),
                            0, 3, &H60, 4,
                            CByte((PSize(0) >> 24) And &HFF),
                            CByte((PSize(0) >> 16) And &HFF),
                            CByte((PSize(0) >> 8) And &HFF),
                             CByte(PSize(0) And &HFF),
                            0, 4, &H60, 4,
                            CByte((PSize(1) >> 24) And &HFF),
                            CByte((PSize(1) >> 16) And &HFF),
                            CByte((PSize(1) >> 8) And &HFF),
                             CByte(PSize(1) And &HFF),
                            0, 5, &H60, 4,
                            CByte((PRemain(2) >> 24) And &HFF),
                            CByte((PRemain(2) >> 16) And &HFF),
                            CByte((PRemain(2) >> 8) And &HFF),
                             CByte(PRemain(2) And &HFF),
                            0, 6, &H60, 4,
                            CByte((PRemain(3) >> 24) And &HFF),
                            CByte((PRemain(3) >> 16) And &HFF),
                            CByte((PRemain(3) >> 8) And &HFF),
                             CByte(PRemain(3) And &HFF),
                            0, 7, &H60, 4,
                            CByte((PSize(2) >> 24) And &HFF),
                            CByte((PSize(2) >> 16) And &HFF),
                            CByte((PSize(2) >> 8) And &HFF),
                             CByte(PSize(2) And &HFF),
                            0, 8, &H60, 4,
                            CByte((PSize(3) >> 24) And &HFF),
                            CByte((PSize(3) >> 16) And &HFF),
                            CByte((PSize(3) >> 8) And &HFF),
                             CByte(PSize(3) And &HFF)}

                    Case Else
                        sense = SenseData.IllegalOpCode
                End Select
                ReDim Preserve Response(allocLen - 1)
            Case &H44 'REPORT DENSITY SUPPORT
                Dim Opt As Byte = CByte(commandBytes(1) And &B11)
                Dim allocLen As Integer = commandBytes(7)
                allocLen <<= 8
                allocLen = allocLen Or commandBytes(8)
                Select Case Opt
                    Case 0
                        Response = {&H0, &H9E, &H0, &H0, &H46, &H46, &H0, &H0, &H0, &H0, &H31, &HB5, &H0, &H7F, &H3, &H80,
                                    &H0, &HC, &H35, &H0, &H4C, &H54, &H4F, &H2D, &H43, &H56, &H45, &H20, &H55, &H2D, &H34, &H31,
                                    &H36, &H20, &H20, &H20, &H55, &H6C, &H74, &H72, &H69, &H75, &H6D, &H20, &H34, &H2F, &H31, &H36,
                                    &H54, &H20, &H20, &H20, &H20, &H20, &H20, &H20, &H58, &H58, &H80, &H0, &H0, &H0, &H3B, &H26,
                                    &H0, &H7F, &H5, &H0, &H0, &H16, &HE3, &H60, &H4C, &H54, &H4F, &H2D, &H43, &H56, &H45, &H20,
                                    &H55, &H2D, &H35, &H31, &H36, &H20, &H20, &H20, &H55, &H6C, &H74, &H72, &H69, &H75, &H6D, &H20,
                                    &H35, &H2F, &H31, &H36, &H54, &H20, &H20, &H20, &H20, &H20, &H20, &H20, &H5A, &H5A, &HA0, &H0,
                                    &H0, &H0, &H3B, &H26, &H0, &H7F, &H8, &H80, &H0, &H26, &H25, &HA0, &H4C, &H54, &H4F, &H2D,
                                    &H43, &H56, &H45, &H20, &H55, &H2D, &H36, &H31, &H36, &H20, &H20, &H20, &H55, &H6C, &H74, &H72,
                                    &H69, &H75, &H6D, &H20, &H36, &H2F, &H31, &H36, &H54, &H20, &H20, &H20, &H20, &H20, &H20, &H20}
                    Case 1
                        Response = {&H0, &H36, &H0, &H0, &H5A, &H5A, &HA0, &H0, &H0, &H0, &H3B, &H26, &H0, &H7F, &H8, &H80,
                                    &H0, &H26, &H25, &HA0, &H4C, &H54, &H4F, &H2D, &H43, &H56, &H45, &H20, &H55, &H2D, &H36, &H31,
                                    &H36, &H20, &H20, &H20, &H55, &H6C, &H74, &H72, &H69, &H75, &H6D, &H20, &H36, &H2F, &H31, &H36,
                                    &H54, &H20, &H20, &H20, &H20, &H20, &H20, &H20}
                    Case 2
                        Response = {&H1, &H52, &H0, &H0, &H0, &H0, &H0, &H34, &H1, &H46, &H0, &H0, &H0, &H0, &H0, &H0,
                                    &H0, &H0, &H0, &H7F, &H3, &H34, &H0, &H0, &H48, &H50, &H20, &H20, &H20, &H20, &H20, &H20,
                                    &H4C, &H54, &H4F, &H34, &H44, &H61, &H74, &H61, &H55, &H6C, &H74, &H72, &H69, &H75, &H6D, &H20,
                                    &H34, &H20, &H44, &H61, &H74, &H61, &H20, &H54, &H61, &H70, &H65, &H20, &H0, &H0, &H0, &H34,
                                    &H1, &H58, &H0, &H0, &H0, &H0, &H0, &H0, &H0, &H0, &H0, &H7F, &H3, &H4E, &H0, &H0,
                                    &H48, &H50, &H20, &H20, &H20, &H20, &H20, &H20, &H4C, &H54, &H4F, &H35, &H44, &H61, &H74, &H61,
                                    &H55, &H6C, &H74, &H72, &H69, &H75, &H6D, &H20, &H35, &H20, &H44, &H61, &H74, &H61, &H20, &H54,
                                    &H61, &H70, &H65, &H20, &H0, &H0, &H0, &H34, &H1, &H5A, &H0, &H0, &H0, &H0, &H0, &H0,
                                    &H0, &H0, &H0, &H7F, &H3, &H4E, &H0, &H0, &H48, &H50, &H20, &H20, &H20, &H20, &H20, &H20,
                                    &H4C, &H54, &H4F, &H36, &H44, &H61, &H74, &H61, &H55, &H6C, &H74, &H72, &H69, &H75, &H6D, &H20,
                                    &H36, &H20, &H44, &H61, &H74, &H61, &H20, &H54, &H61, &H70, &H65, &H20, &H1, &H0, &H0, &H34,
                                    &H1, &H46, &H0, &H0, &H0, &H0, &H0, &H0, &H0, &H0, &H0, &H7F, &H3, &H34, &H0, &H0,
                                    &H48, &H50, &H20, &H20, &H20, &H20, &H20, &H20, &H4C, &H54, &H4F, &H34, &H57, &H4F, &H52, &H4D,
                                    &H55, &H6C, &H74, &H72, &H69, &H75, &H6D, &H20, &H34, &H20, &H57, &H4F, &H52, &H4D, &H20, &H54,
                                    &H61, &H70, &H65, &H20, &H1, &H0, &H0, &H34, &H1, &H58, &H0, &H0, &H0, &H0, &H0, &H0,
                                    &H0, &H0, &H0, &H7F, &H3, &H4E, &H0, &H0, &H48, &H50, &H20, &H20, &H20, &H20, &H20, &H20,
                                    &H4C, &H54, &H4F, &H35, &H57, &H4F, &H52, &H4D, &H55, &H6C, &H74, &H72, &H69, &H75, &H6D, &H20,
                                    &H35, &H20, &H57, &H4F, &H52, &H4D, &H20, &H54, &H61, &H70, &H65, &H20, &H1, &H0, &H0, &H34,
                                    &H1, &H5A, &H0, &H0, &H0, &H0, &H0, &H0, &H0, &H0, &H0, &H7F, &H3, &H4E, &H0, &H0,
                                    &H48, &H50, &H20, &H20, &H20, &H20, &H20, &H20, &H4C, &H54, &H4F, &H36, &H57, &H4F, &H52, &H4D,
                                    &H55, &H6C, &H74, &H72, &H69, &H75, &H6D, &H20, &H36, &H20, &H57, &H4F, &H52, &H4D, &H20, &H54,
                                    &H61, &H70, &H65, &H20}
                    Case 3
                        Response = {&H0, &H3A, &H0, &H0, &H0, &H0, &H0, &H34, &H1, &H5A, &H0, &H0, &H0, &H0, &H0, &H0,
                                    &H0, &H0, &H0, &H7F, &H3, &H4E, &H0, &H0, &H48, &H50, &H20, &H20, &H20, &H20, &H20, &H20,
                                    &H4C, &H54, &H4F, &H36, &H44, &H61, &H74, &H61, &H55, &H6C, &H74, &H72, &H69, &H75, &H6D, &H20,
                                    &H36, &H20, &H44, &H61, &H74, &H61, &H20, &H54, &H61, &H70, &H65, &H20}
                End Select
                ReDim Preserve Response(allocLen - 1)
                sense = SenseData.NoSense
            Case &HA0 'REPORT LUNS
                Dim allocLen As Integer = 0
                For i As Integer = 6 To 9
                    allocLen <<= 8
                    allocLen = allocLen Or commandBytes(i)
                Next
                If allocLen >= 16 Then
                    ReDim Response(allocLen - 1)
                    Response(3) = 8
                    sense = SenseData.NoSense
                Else
                    sense = SenseData.IllegalOpCode
                End If
            Case Else
                'SI_ERR_UNSUPPORTED_OPCODE
                sense = SenseData.IllegalOpCode
        End Select
        Return True
    End Function
    Public Sub [Erase]()
        If WriteProtect Then Exit Sub
        PartitionEOD(Position.PartitionNumber) = CLng(Position.BlockNumber)
        CurrentStream.SetLength(CurrentFileOffset)
        ValidLength(ValidLengthLookupTable(CurrentStream)) = CurrentFileOffset
        VolumeChanged = True
    End Sub

    Public Sub SetLengthIfNeeded(st As Stream, TargetLength As Long)
        If ValidLength(ValidLengthLookupTable(st)) > TargetLength Then
            st.SetLength(TargetLength)
        End If
        ValidLength(ValidLengthLookupTable(st)) = TargetLength
    End Sub

    Public Sub CurrentStream_SeekIfNeeded(offset As Long)
        If CurrentStream.Position <> offset Then
            CurrentStream.Seek(offset, SeekOrigin.Begin)
        End If
    End Sub

    Public Sub WriteBlock(data As Byte(), Optional ByVal len As Integer = -1)
        If WriteProtect Then Exit Sub
        If len = -1 Then len = data.Length
        Dim blocklen As Integer = Math.Min(data.Length, len)
        If CurrentSetResidueBytes < BlockHeaderLen Then
            'SetLengthIfNeeded(CurrentStream, CurrentFileOffset + CurrentSetResidueBytes + DatesetLength)
            CurrentStream_SeekIfNeeded(CurrentFileOffset)
            If CurrentSetResidueBytes > 0 Then
                Dim paddingdata(CurrentSetResidueBytes - 1) As Byte
                CurrentStream.Write(paddingdata, 0, CurrentSetResidueBytes)
            End If
            Position.SetNumber = CULng(Position.SetNumber + 1)
            CurrentIntraSetBlockOffset = 0
        End If
        If blocklen = 0 Then
            CurrentStream_SeekIfNeeded(CurrentFileOffset)
            CurrentStream.Write(GetByteArray(Position.BlockNumber), 0, 8)
            CurrentStream.Write(GetByteArray(0I), 0, 4)
            CurrentStream.Write(GetByteArray(0I), 0, 4)
            'SetLengthIfNeeded(CurrentStream, CurrentFileOffset + BlockHeaderLen)
            While FilemarkBlockIndex(Position.PartitionNumber).Count > 0 AndAlso FilemarkBlockIndex(Position.PartitionNumber).Last >= Position.BlockNumber
                FilemarkBlockIndex(Position.PartitionNumber).RemoveAt(FilemarkBlockIndex(Position.PartitionNumber).Count - 1)
            End While
            FilemarkBlockIndex(Position.PartitionNumber).Add(CLng(Position.BlockNumber))
            Position.BlockNumber = CULng(Position.BlockNumber + 1)
            Position.FileNumber = CULng(Position.FileNumber + 1)
            CurrentIntraSetBlockOffset += BlockHeaderLen
        Else
            Dim residue As Integer = blocklen
            While residue > 0
                If CurrentSetResidueBytes < BlockHeaderLen Then
                    'SetLengthIfNeeded(CurrentStream, CurrentFileOffset + CurrentSetResidueBytes + DatesetLength)
                    CurrentStream_SeekIfNeeded(CurrentFileOffset)
                    If CurrentSetResidueBytes > 0 Then
                        Dim paddingdata(CurrentSetResidueBytes - 1) As Byte
                        CurrentStream.Write(paddingdata, 0, CurrentSetResidueBytes)
                    End If
                    Position.SetNumber = CULng(Position.SetNumber + 1)
                    CurrentIntraSetBlockOffset = 0
                End If
                Dim writelen As Integer = Math.Min(residue, CurrentSetResidueBytes - BlockHeaderLen)
                'SetLengthIfNeeded(CurrentStream, CurrentFileOffset + BlockHeaderLen + writelen)
                CurrentStream_SeekIfNeeded(CurrentFileOffset)
                CurrentStream.Write(GetByteArray(Position.BlockNumber), 0, 8)
                CurrentStream.Write(GetByteArray(blocklen), 0, 4)
                CurrentStream.Write(GetByteArray(writelen), 0, 4)
                CurrentIntraSetBlockOffset += writelen + BlockHeaderLen
                If writelen > 0 Then
                    CurrentStream.Write(data, blocklen - residue, writelen)
                End If
                residue -= writelen
            End While
            Position.BlockNumber = CULng(Position.BlockNumber + 1)
        End If
        SetLengthIfNeeded(CurrentStream, CurrentFileOffset)
        PartitionEOD(Position.PartitionNumber) = CLng(Position.BlockNumber)
        VolumeChanged = True
    End Sub
    Public Sub WriteFilemark(Optional ByVal count As Integer = 1)
        If count = 0 Then
            For Each s As Stream In PartitionMappingStream.Values
                s.Flush()
            Next
        Else
            If WriteProtect Then Exit Sub
            For i As Integer = 0 To count - 1
                WriteBlock({})
            Next
            VolumeChanged = True
        End If
    End Sub

    Public Function ReadBlock(ByRef sense As Byte()) As Byte()
        If CurrentFileOffset + BlockHeaderLen > CurrentStreamValidLength Then
            sense = SenseData.EOD
            Return {}
        End If
        If CurrentSetResidueBytes < BlockHeaderLen Then
            Position.SetNumber = CULng(Position.SetNumber + 1)
            CurrentIntraSetBlockOffset = 0
        End If
        If CurrentFileOffset <> CurrentStream.Position Then
            CurrentStream.Position = CurrentFileOffset
        End If
        Dim BlockHeader(BlockHeaderLen - 1) As Byte
        CurrentStream.Read(BlockHeader, 0, BlockHeaderLen)
        CurrentIntraSetBlockOffset += BlockHeaderLen
        Dim blocknum As ULong = GetULong(BlockHeader.Take(8).ToArray())
        Dim blocklen As Integer = GetInteger(BlockHeader.Skip(8).Take(4).ToArray())
        Dim blockfraglen As Integer = GetInteger(BlockHeader.Skip(12).Take(4).ToArray())
        If blocknum = 0 AndAlso blocklen = 0 Then
            If Position.SetNumber <> 0 OrElse CurrentIntraSetBlockOffset <> BlockHeaderLen Then
                sense = SenseData.EOD
                CurrentIntraSetBlockOffset -= BlockHeaderLen
                Return {}
            End If
        End If
        Position.BlockNumber = CULng(blocknum + 1)
        If blocklen = 0 Then
            sense = SenseData.FileMark
            Position.FileNumber = CULng(Position.FileNumber + 1)
            Return {}
        End If
        Dim result(blocklen - 1) As Byte
        Dim residue As Integer = blocklen
        Dim HeaderReaded As Boolean = True
        While residue > 0
            If HeaderReaded Then
                If CurrentSetResidueBytes = 0 Then
                    Position.SetNumber = CULng(Position.SetNumber + 1)
                    CurrentIntraSetBlockOffset = BlockHeaderLen
                    CurrentStream_SeekIfNeeded(CurrentFileOffset)
                End If
                HeaderReaded = False
            Else
                If CurrentSetResidueBytes <= BlockHeaderLen Then
                    Position.SetNumber = CULng(Position.SetNumber + 1)
                    CurrentIntraSetBlockOffset = BlockHeaderLen
                Else
                    CurrentIntraSetBlockOffset += BlockHeaderLen
                End If
                CurrentStream_SeekIfNeeded(CurrentFileOffset)
            End If
            Dim readlen As Integer = Math.Min(residue, CurrentSetResidueBytes)
            CurrentStream.Read(result, blocklen - residue, readlen)
            CurrentIntraSetBlockOffset += readlen
            residue -= readlen
        End While
        sense = SenseData.NoSense
        'Position.BlockNumber += 1
        Return result
    End Function
    Public Sub ChangePartition(partition As Byte, ByRef sense As Byte())
        If PartitionCount = 1 Then Exit Sub
        If partition >= PartitionCount Then
            sense = SenseData.BlankCheck
        Else
            sense = SenseData.NoSense
            Position.PartitionNumber = partition
            Position.BlockNumber = 0
            Position.FileNumber = 0
            Position.SetNumber = 0
            CurrentIntraSetBlockOffset = 0
        End If
    End Sub

    Public Sub LocateByBlock(blockIndex As Long, ByRef sense As Byte())
        If blockIndex = 0 Then
            Position.SetNumber = 0
            Position.BlockNumber = 0
            Position.FileNumber = 0
            CurrentIntraSetBlockOffset = 0
            sense = SenseData.NoSense
            Exit Sub
        End If
        If blockIndex > PartitionEOD(Position.PartitionNumber) Then
            sense = SenseData.NotFound
            Exit Sub
        End If
        Dim currset As Long = CLng(Position.SetNumber)
        Dim currblock As ULong = GetHeaderBlockNumber(currset)
        While currset > 0 AndAlso currblock = 0
            currset -= 1
            currblock = GetHeaderBlockNumber(currset)
        End While
        Dim nextset As Long = currset
        Dim nextblock As ULong = currblock
        Dim lastset As Long = CLng(Math.Ceiling(CurrentStreamValidLength / DatesetLength) - 1)
        Dim lastblock As ULong = CULng(PartitionEOD(Position.PartitionNumber))
        If blockIndex >= lastblock Then
            LocateToEOD(sense)
            Exit Sub
        End If
        Dim debuglogs As New StringBuilder '死循环检测
        Dim logcount As Integer = 0
        Dim DebugLog As New Action(Of String, Boolean)(Sub(info As String, ByVal ForceReport As Boolean)
                                                           debuglogs.AppendLine(info)
                                                           logcount += 1
                                                           If logcount >= 200 OrElse ForceReport Then
                                                               logcount = 0
                                                               Dim debuginfo = debuglogs.ToString()
                                                               MessageBox.Show($"已复制到剪贴板，请粘贴后回报{vbCrLf}{debuginfo}")
                                                               Application.OpenForms(0).Invoke(Sub() Clipboard.SetText(debuginfo))
                                                               debuglogs = New StringBuilder
                                                           End If
                                                       End Sub)
        While (currblock >= blockIndex) OrElse (nextblock < blockIndex AndAlso nextset < lastset) OrElse nextset - currset > 1
            While currblock > blockIndex
                Dim delta As Integer = CInt(currset - currset \ 2)
                If delta = 0 Then
                    currset = 0
                    currblock = 0
                    Exit While
                End If
                currset \= 2
                currblock = GetHeaderBlockNumber(currset)
                DebugLog($"L1340 currblock={currblock}, currset={currset}, blockindex={blockIndex}", False)
            End While
            While nextblock <= blockIndex
                Dim delta As Integer = CInt(nextset - Math.Ceiling((nextset + lastset) / 2))
                If delta = 0 Then
                    nextset = lastset
                    nextblock = GetHeaderBlockNumber(nextset)
                    Exit While
                End If
                nextset = CLng(Math.Ceiling((nextset + lastset) / 2))
                nextblock = GetHeaderBlockNumber(nextset)
                DebugLog($"L1351 currblock={currblock}, nextblock={nextblock}, currset={currset}, nextset={nextset}, lastset={lastset}", False)
            End While
            If nextset - currset < 5 Then
                For i As Integer = CInt(nextset) To CInt(currset + 1) Step -1
                    If GetHeaderBlockNumber(i) >= blockIndex Then
                        nextset = i
                    End If
                Next
            End If
            Dim midset As Long = (currset + nextset) \ 2
            Dim midblk As ULong = GetHeaderBlockNumber(midset)
            DebugLog($"L1362 currset={currset}, nextset={nextset}, midset={midset}, midblk={midblk}", False)
            If midblk < blockIndex Then
                currset = midset
            ElseIf midblk > blockIndex Then
                nextset = midset
            Else
                Dim BlockHeader() As Byte = GetHeaderBlock(midset)
                Dim blocknum As ULong = GetULong(BlockHeader.Take(8).ToArray())
                Dim blocklen As Integer = GetInteger(BlockHeader.Skip(8).Take(4).ToArray())
                Dim blockfraglen As Integer = GetInteger(BlockHeader.Skip(12).Take(4).ToArray())
                If blocklen = blockfraglen Then
                    currset = midset
                    currblock = GetHeaderBlockNumber(currset)
                Else
                    currset = midset - 1
                    currblock = GetHeaderBlockNumber(currset)
                    Exit While
                End If
            End If
            If currset < 0 Then
                currset = 0
                currblock = 0
                Exit While
            End If
            currblock = GetHeaderBlockNumber(currset)
            nextblock = GetHeaderBlockNumber(nextset)
            If currblock = blockIndex Then Exit While
            If nextset - currset <= 1 Then Exit While
        End While
        Dim iofs As Long = 0
        Position.SetNumber = CULng(currset)
        Position.FileNumber = 0
        While currblock <> blockIndex
            If iofs > DatesetLength - BlockHeaderLen Then
                Position.SetNumber = CULng(nextset)
                Position.BlockNumber = nextblock
                iofs = 0
            End If
            Dim header As Byte() = GetHeaderBlock(CLng(Position.SetNumber), iofs)
            currblock = GetULong(header.Take(8).ToArray())
            If currblock = 0 AndAlso (Position.SetNumber > 0 OrElse iofs > 0) Then
                sense = SenseData.NotFound
                Exit Sub
            End If
            If currblock = blockIndex Then Exit While
            Dim datalen As Integer = GetInteger(header.Skip(12).Take(4).ToArray())
            iofs += BlockHeaderLen + datalen
        End While
        CurrentIntraSetBlockOffset = CInt(iofs)
        Position.BlockNumber = CULng(blockIndex)
        For i As Integer = 0 To FilemarkBlockIndex(Position.PartitionNumber).Count - 1
            If FilemarkBlockIndex(Position.PartitionNumber)(i) < Position.BlockNumber Then
                Position.FileNumber = CULng(i + 1)
            End If
        Next
    End Sub
    Public Function GetHeaderBlockNumber(setnum As Long, Optional ByVal offset As Long = 0) As ULong
        Dim streampos As Long = CurrentStream.Position
        CurrentStream_SeekIfNeeded(setnum * DatesetLength + offset)
        Dim blkdata(7) As Byte
        CurrentStream.Read(blkdata, 0, 8)
        CurrentStream_SeekIfNeeded(streampos)
        Return GetULong(blkdata)
    End Function
    Public Function GetHeaderBlock(setnum As Long, Optional ByVal offset As Long = 0) As Byte()
        Dim streampos As Long = CurrentStream.Position
        CurrentStream_SeekIfNeeded(setnum * DatesetLength + offset)
        Dim blkdata(15) As Byte
        CurrentStream.Read(blkdata, 0, 16)
        CurrentStream_SeekIfNeeded(streampos)
        Return blkdata
    End Function

    Public Sub LocateByFilemark(filemarkIndex As Long, ByRef sense As Byte())
        If filemarkIndex = 0 Then
            Position.SetNumber = 0
            Position.BlockNumber = 0
            Position.FileNumber = 0
            CurrentIntraSetBlockOffset = 0
            sense = SenseData.NoSense
            Exit Sub
        End If
        If FilemarkBlockIndex(Position.PartitionNumber).Count > filemarkIndex Then
            LocateByBlock(FilemarkBlockIndex(Position.PartitionNumber)(CInt(filemarkIndex - 1)), sense)
        Else
            sense = SenseData.NotFound
        End If
    End Sub
    Public Sub LocateToEOD(ByRef sense As Byte())
        CurrentIntraSetBlockOffset = 0
        If CurrentStreamValidLength < BlockHeaderLen Then
            Position.BlockNumber = 0
            Position.SetNumber = 0
            Position.FileNumber = 0
            Exit Sub
        End If
        Position.SetNumber = CULng((CurrentStreamValidLength - BlockHeaderLen) \ DatesetLength)
        Dim BlockHeader(BlockHeaderLen - 1) As Byte
        Dim blocknum As ULong, blocklen As Integer, blockfraglen As Integer
        Dim setHeaderPosition = CurrentFileOffset
        CurrentStream_SeekIfNeeded(CurrentFileOffset)
        CurrentStream.Read(BlockHeader, 0, BlockHeaderLen)
        blocknum = GetULong(BlockHeader.Take(8).ToArray())
        blocklen = GetInteger(BlockHeader.Skip(8).Take(4).ToArray())
        blockfraglen = GetInteger(BlockHeader.Skip(12).Take(4).ToArray())
        If blocknum = 0 AndAlso blocklen = 0 AndAlso blockfraglen = 0 Then
            If Position.SetNumber <> 0 OrElse CurrentIntraSetBlockOffset <> BlockHeaderLen Then
                sense = SenseData.NoSense
                Position.BlockNumber = 0
                CurrentIntraSetBlockOffset -= BlockHeaderLen
                Exit Sub
            End If
        Else
            If blockfraglen < blocklen Then
                Position.BlockNumber = blocknum + 1UL
                CurrentIntraSetBlockOffset += blockfraglen + BlockHeaderLen
            Else
                Position.BlockNumber = blocknum
            End If
            ReadBlock(sense)
            blocknum = Position.BlockNumber
        End If
        While blocknum > 0
            If CurrentSetResidueBytes < BlockHeaderLen Then Exit Sub
            CurrentStream_SeekIfNeeded(CurrentFileOffset)
            If CurrentStream.Read(BlockHeader, 0, BlockHeaderLen) < BlockHeaderLen Then
                Exit While
            End If
            CurrentIntraSetBlockOffset += BlockHeaderLen
            blocknum = GetULong(BlockHeader.Take(8).ToArray())
            If blocknum = 0 Then
                CurrentIntraSetBlockOffset -= BlockHeaderLen
                CurrentStream_SeekIfNeeded(CurrentFileOffset)
                Exit While
            Else
                Position.BlockNumber = CULng(blocknum + 1)
            End If
            blocklen = GetInteger(BlockHeader.Skip(8).Take(4).ToArray())
            blockfraglen = GetInteger(BlockHeader.Skip(12).Take(4).ToArray())
            CurrentIntraSetBlockOffset += blockfraglen
        End While
        'Position.BlockNumber = CULng(Position.BlockNumber + 1)
        ReadBlock(sense)
        For i As Integer = 0 To FilemarkBlockIndex(Position.PartitionNumber).Count - 1
            If FilemarkBlockIndex(Position.PartitionNumber)(i) < Position.BlockNumber Then
                Position.FileNumber = CULng(i + 1)
            End If
        Next
    End Sub
    Public Function ReadPosition() As TapeUtils.PositionData
        Return Position
    End Function

    Public Function GetInteger(data As Byte()) As Integer
        Return BitConverter.ToInt32(data, 0)
    End Function
    Public Function GetULong(data As Byte()) As ULong
        Return BitConverter.ToUInt64(data, 0)
    End Function
    Public Function GetByteArray(data As Integer) As Byte()
        Return BitConverter.GetBytes(data)
    End Function
    Public Function GetByteArray(data As ULong) As Byte()
        Return BitConverter.GetBytes(data)
    End Function
    ' 释放
    Public Sub Dispose() Implements IDisposable.Dispose

    End Sub
End Class
