Imports System.ComponentModel
Imports System.Globalization
Imports System.Xml

<Serializable>
<TypeConverter(GetType(ExpandableObjectConverter))>
Public Class ltfsindex
    'Public Property version As String
    <Category("LTFSIndex")>
    Public Property creator As String = My.Application.Info.ProductName & " " & My.Application.Info.Version.ToString(3) & " - Windows - TapeUtils"
    <Category("LTFSIndex")>
    Public Property volumeuuid As Guid
    <Category("LTFSIndex")>
    Public Property generationnumber As ULong
    <Category("LTFSIndex")>
    Public Property updatetime As String
    Public Enum PartitionLabel
        a
        b
    End Enum
    <Serializable>
    <TypeConverter(GetType(ExpandableObjectConverter))>
    Public Class LocationDef

        Public Property partition As PartitionLabel = PartitionLabel.a
        Public Property startblock As ULong
    End Class

    <Category("LTFSIndex")>
    Public Property location As New LocationDef
    <Category("LTFSIndex")>
    Public Property previousgenerationlocation As New LocationDef
    <Category("LTFSIndex")>
    Public Property allowpolicyupdate As Boolean
    <Serializable>
    <TypeConverter(GetType(ExpandableObjectConverter))>
    Public Class policy
        Public Structure indexpartitioncriteria
            Public Property size As Long
        End Structure
    End Class
    <Category("LTFSIndex")>
    Public Property dataplacementpolicy As policy
    Public Enum volumelockstateValue
        unlocked
        locked
        permlocked
    End Enum
    <Category("LTFSIndex")>
    Public Property volumelockstate As volumelockstateValue = volumelockstateValue.unlocked
    <Category("LTFSIndex")>
    Public Property highestfileuid As Long
    <Serializable>
    <TypeConverter(GetType(ExpandableObjectConverter))>
    Public Class file
        Private _name As String
        Private _length As Long
        Private _readonly As Boolean = False
        Private _openforwrite As Boolean = True
        Private _creationtime As String
        Private _changetime As String
        Private _modifytime As String
        Private _accesstime As String
        Private _backuptime As String
        Private _fileuid As Long
        Private _extendedattributes As List(Of xattr) = New List(Of xattr)
        Private _symlink As String = Nothing
        Private _extentinfo As List(Of extent) = New List(Of extent)
        Private _lazyStore As LazySchemaStore
        Private _lazyRecordOffset As Long
        Private _lazyRecordLength As Long
        Private _lazyScalarsLoaded As Boolean = True
        Private _lazyExtendedAttributesLoaded As Boolean = True
        Private _lazyExtentInfoLoaded As Boolean = True

        Private Sub MarkLazyDirty()
            If _lazyStore IsNot Nothing AndAlso _lazyRecordOffset >= 0 Then
                _lazyStore.RegisterModifiedFile(_lazyRecordOffset, Me)
            End If
        End Sub

        Private Sub EnsureLazyScalars()
            If _lazyStore Is Nothing OrElse _lazyScalarsLoaded Then Exit Sub

            Dim values As LazyFileScalarData = _lazyStore.ReadFileScalars(_lazyRecordOffset, _lazyRecordLength)
            _name = values.Name
            _length = values.Length
            _readonly = values.ReadOnly
            _openforwrite = values.OpenForWrite
            _creationtime = values.CreationTime
            _changetime = values.ChangeTime
            _modifytime = values.ModifyTime
            _accesstime = values.AccessTime
            _backuptime = values.BackupTime
            _fileuid = values.FileUid
            _symlink = values.Symlink
            _lazyScalarsLoaded = True
        End Sub

        Private Sub EnsureLazyExtendedAttributes()
            If _lazyStore Is Nothing OrElse _lazyExtendedAttributesLoaded Then Exit Sub
            _extendedattributes = _lazyStore.ReadFileExtendedAttributes(_lazyRecordOffset, _lazyRecordLength)
            If _extendedattributes Is Nothing Then _extendedattributes = New List(Of xattr)
            _lazyExtendedAttributesLoaded = True
        End Sub

        Private Sub EnsureLazyExtentInfo()
            If _lazyStore Is Nothing OrElse _lazyExtentInfoLoaded Then Exit Sub
            _extentinfo = _lazyStore.ReadFileExtentInfo(_lazyRecordOffset, _lazyRecordLength)
            If _extentinfo Is Nothing Then _extentinfo = New List(Of extent)
            _lazyExtentInfoLoaded = True
        End Sub

        Friend Sub AttachLazyRecord(store As LazySchemaStore, recordOffset As Long, recordLength As Long)
            _lazyStore = store
            _lazyRecordOffset = recordOffset
            _lazyRecordLength = recordLength
            _lazyScalarsLoaded = False
            _lazyExtendedAttributesLoaded = False
            _lazyExtentInfoLoaded = False
            _extendedattributes = Nothing
        End Sub

        Friend ReadOnly Property HasLazyRecord As Boolean
            Get
                Return _lazyStore IsNot Nothing AndAlso _lazyRecordOffset >= 0 AndAlso _lazyRecordLength > 0
            End Get
        End Property

        Friend ReadOnly Property LazyRecordOffset As Long
            Get
                Return _lazyRecordOffset
            End Get
        End Property

        Friend ReadOnly Property LazyRecordLength As Long
            Get
                Return _lazyRecordLength
            End Get
        End Property

        Friend ReadOnly Property LazyStoreReference As Object
            Get
                Return _lazyStore
            End Get
        End Property

        <Category("LTFSIndex")>
        Public Property name As String
            Get
                EnsureLazyScalars()
                Return _name
            End Get
            Set(value As String)
                EnsureLazyScalars()
                _name = value
                MarkLazyDirty()
            End Set
        End Property
        <Category("LTFSIndex")>
        Public Property length As Long
            Get
                EnsureLazyScalars()
                Return _length
            End Get
            Set(value As Long)
                EnsureLazyScalars()
                _length = value
                MarkLazyDirty()
            End Set
        End Property
        <Category("LTFSIndex")>
        Public Property [readonly] As Boolean
            Get
                EnsureLazyScalars()
                Return _readonly
            End Get
            Set(value As Boolean)
                EnsureLazyScalars()
                _readonly = value
                MarkLazyDirty()
            End Set
        End Property
        <Category("LTFSIndex")>
        Public Property openforwrite As Boolean
            Get
                EnsureLazyScalars()
                Return _openforwrite
            End Get
            Set(value As Boolean)
                EnsureLazyScalars()
                _openforwrite = value
                MarkLazyDirty()
            End Set
        End Property
        <Category("LTFSIndex")>
        Public Property creationtime As String
            Get
                EnsureLazyScalars()
                Return _creationtime
            End Get
            Set(value As String)
                EnsureLazyScalars()
                _creationtime = value
                MarkLazyDirty()
            End Set
        End Property
        <Category("LTFSIndex")>
        Public Property changetime As String
            Get
                EnsureLazyScalars()
                Return _changetime
            End Get
            Set(value As String)
                EnsureLazyScalars()
                _changetime = value
                MarkLazyDirty()
            End Set
        End Property
        <Category("LTFSIndex")>
        Public Property modifytime As String
            Get
                EnsureLazyScalars()
                Return _modifytime
            End Get
            Set(value As String)
                EnsureLazyScalars()
                _modifytime = value
                MarkLazyDirty()
            End Set
        End Property
        <Category("LTFSIndex")>
        Public Property accesstime As String
            Get
                EnsureLazyScalars()
                Return _accesstime
            End Get
            Set(value As String)
                EnsureLazyScalars()
                _accesstime = value
                MarkLazyDirty()
            End Set
        End Property
        <Category("LTFSIndex")>
        Public Property backuptime As String
            Get
                EnsureLazyScalars()
                Return _backuptime
            End Get
            Set(value As String)
                EnsureLazyScalars()
                _backuptime = value
                MarkLazyDirty()
            End Set
        End Property
        <Category("LTFSIndex")>
        Public Property fileuid As Long
            Get
                EnsureLazyScalars()
                Return _fileuid
            End Get
            Set(value As Long)
                EnsureLazyScalars()
                _fileuid = value
                MarkLazyDirty()
            End Set
        End Property
        <Category("Deprecated")>
        <Xml.Serialization.XmlIgnore>
        Public Property sha1 As String
            Get
                If Searializing Then Return Nothing
                Dim result As String = GetXAttr(xattr.HashType.SHA1)
                If result Is Nothing Then Return ""
                Return result
            End Get
            Set(value As String)
                If value Is Nothing Then Exit Property
                If value.Length <> 40 Then Exit Property
                SetXattr(xattr.HashType.SHA1, value)
            End Set
        End Property
        <Category("Internal")>
        <Xml.Serialization.XmlIgnore>
        Public Property tag As String
        <TypeConverter(GetType(ExpandableObjectConverter))>
        Public Class refFile
            Public FileName As String
        End Class
        <Category("Internal")>
        <Xml.Serialization.XmlIgnore>
        Public Property fullpath As String
        <Category("Internal")>
        <Xml.Serialization.XmlIgnore>
        Public Property Selected As Boolean = True
        <Category("Internal")>
        <Xml.Serialization.XmlIgnore>
        Public Property WrittenBytes As Long = 0
        <Category("Internal")>
        <Xml.Serialization.XmlIgnore>
        Public Property TempObj As Object
        <Category("Internal")>
        <Xml.Serialization.XmlIgnore>
        Public Property SHA1ForeColor As Color = Color.Black
        <Category("Internal")>
        <Xml.Serialization.XmlIgnore>
        Public Property SHA256ForeColor As Color = Color.Black
        <Category("Internal")>
        <Xml.Serialization.XmlIgnore>
        Public Property SHA512ForeColor As Color = Color.Black
        <Category("Internal")>
        <Xml.Serialization.XmlIgnore>
        Public Property CRC32ForeColor As Color = Color.Black
        <Category("Internal")>
        <Xml.Serialization.XmlIgnore>
        Public Property MD5ForeColor As Color = Color.Black
        <Category("Internal")>
        <Xml.Serialization.XmlIgnore>
        Public Property BLAKE3ForeColor As Color = Color.Black
        <Category("Internal")>
        <Xml.Serialization.XmlIgnore>
        Public Property XxHash3ForeColor As Color = Color.Black
        <Category("Internal")>
        <Xml.Serialization.XmlIgnore>
        Public Property XxHash128ForeColor As Color = Color.Black
        <Category("Internal")>
        <Xml.Serialization.XmlIgnore>
        Public Property ItemForeColor As Color = Color.Black
        <Serializable>
        <TypeConverter(GetType(ExpandableObjectConverter))>
        Public Class xattr
            <Category("LTFSIndex")>
            Public Property key As String
            <Category("LTFSIndex")>
            Public Property value As String
            <TypeConverter(GetType(ExpandableObjectConverter))>
            <Serializable>
            Public Class HashType
                Public Shared ReadOnly Property CRC32 As String = "ltfs.hash.crc32sum"
                Public Shared ReadOnly Property MD5 As String = "ltfs.hash.md5sum"
                Public Shared ReadOnly Property SHA1 As String = "ltfs.hash.sha1sum"
                Public Shared ReadOnly Property SHA256 As String = "ltfs.hash.sha256sum"
                Public Shared ReadOnly Property SHA512 As String = "ltfs.hash.sha512sum"
                Public Shared ReadOnly Property BLAKE3 As String = "ltfs.hash.blake3sum"
                Public Shared ReadOnly Property XxHash3 As String = "ltfs.hash.xxhash3sum"
                Public Shared ReadOnly Property XxHash128 As String = "ltfs.hash.xxhash128sum"
                Public Enum Available
                    SHA1
                    SHA256
                    SHA512
                    CRC32
                    MD5
                    BLAKE3
                    XxHash3
                    XxHash128
                End Enum
            End Class
            Public Class ApplicationSpecific
                Public Shared ReadOnly Property TarMetadata As String = "ltfscopygui.tarmetadata"
                Public Shared ReadOnly Property CapacityRemain As String = "ltfscopygui.capacityremain"
                Public Shared ReadOnly Property Archive As String = "ltfscopygui.archive"
                Public Shared ReadOnly Property Fragment As String = "ltfscopygui.fragment"

            End Class
            Public Class HashLengthBytes
                Public Const CRC32 As Integer = 4
                Public Const MD5 As Integer = 16
                Public Const SHA1 As Integer = 20
                Public Const SHA256 As Integer = 32
                Public Const SHA512 As Integer = 64
                Public Const BLAKE3 As Integer = 32
                Public Const XxHash3 As Integer = 8
                Public Const XxHash128 As Integer = 16
            End Class
            Public Shared Function FromXMLList(s As String) As List(Of xattr)
                Dim reader As New Xml.Serialization.XmlSerializer(GetType(List(Of xattr)))
                Dim t As IO.TextReader = New IO.StringReader(s)
                Return CType(reader.Deserialize(t), List(Of xattr))
            End Function
        End Class
        <Category("LTFSIndex")>
        <TypeConverter(GetType(ListTypeDescriptor(Of List(Of xattr), xattr)))>
        Public Property extendedattributes As List(Of xattr)
            Get
                EnsureLazyExtendedAttributes()
                Return _extendedattributes
            End Get
            Set(value As List(Of xattr))
                _extendedattributes = value
                _lazyExtendedAttributesLoaded = True
                MarkLazyDirty()
            End Set
        End Property
        Public Function GetXAttrText() As String
            Dim writer As New Xml.Serialization.XmlSerializer(GetType(List(Of xattr)))
            Dim sb As New Text.StringBuilder
            Dim t As New IO.StringWriter(sb)
            writer.Serialize(t, extendedattributes)
            Return sb.ToString()
        End Function
        Public Function GetXAttr(key As String, Optional ByVal ReturnBlankIfNotFound As Boolean = False) As String
            For Each x As xattr In extendedattributes
                If x.key.ToLower = key.ToLower Then Return x.value
            Next
            If ReturnBlankIfNotFound Then
                Return ""
            Else
                Return Nothing
            End If
        End Function

        Public Sub SetXattr(key As String, value As String, Optional ByVal IgnoreBlank As Boolean = False)
            If IgnoreBlank AndAlso value.Length = 0 Then Exit Sub
            For Each x As xattr In extendedattributes
                If x.key.ToLower = key.ToLower Then
                    If x.value <> value Then
                        x.value = value
                        MarkLazyDirty()
                    End If
                    Exit Sub
                End If
            Next
            extendedattributes.Add(New xattr With {.key = key, .value = value})
            MarkLazyDirty()
        End Sub
        Public Sub RemoveXattr(key As String)
            If String.IsNullOrEmpty(key) OrElse extendedattributes Is Nothing Then Exit Sub
            For i As Integer = extendedattributes.Count - 1 To 0 Step -1
                If String.Equals(extendedattributes(i).key, key, StringComparison.OrdinalIgnoreCase) Then
                    extendedattributes.RemoveAt(i)
                    MarkLazyDirty()
                End If
            Next
        End Sub
        <Category("LTFSIndex")>
        Public Property symlink As String
            Get
                EnsureLazyScalars()
                Return _symlink
            End Get
            Set(value As String)
                EnsureLazyScalars()
                _symlink = value
                MarkLazyDirty()
                'If value IsNot Nothing Then extentinfo = Nothing
            End Set
        End Property

        <Serializable>
        <TypeConverter(GetType(ExpandableObjectConverter))>
        <Category("LTFSIndex")>
        Public Class extent
            <Category("LTFSIndex")>
            Public Property fileoffset As Long
            <Category("LTFSIndex")>
            Public Property partition As PartitionLabel
            <Category("LTFSIndex")>
            Public Property startblock As Long
            <Category("LTFSIndex")>
            Public Property byteoffset As Long
            <Category("LTFSIndex")>
            Public Property bytecount As Long
            <Xml.Serialization.XmlIgnore>
            <Category("Internal")>
            Public Property TempInfo As Object
            Public Shared Function AllEquals(a As List(Of extent), b As List(Of extent)) As Boolean
                If a Is Nothing OrElse b Is Nothing Then Return False
                If a.Count <> b.Count Then Return False
                For i As Integer = 0 To a.Count - 1
                    If a(i).startblock <> b(i).startblock Then Return False
                    If a(i).bytecount <> b(i).bytecount Then Return False
                    If a(i).byteoffset <> b(i).byteoffset Then Return False
                    If a(i).fileoffset <> b(i).fileoffset Then Return False
                Next
                Return True
            End Function
        End Class

        <Category("LTFSIndex")>
        <TypeConverter(GetType(ListTypeDescriptor(Of List(Of extent), extent)))>
        Public Property extentinfo As List(Of extent)
            Get
                EnsureLazyExtentInfo()
                Return _extentinfo
            End Get
            Set(value As List(Of extent))
                _extentinfo = value
                _lazyExtentInfoLoaded = True
                MarkLazyDirty()
            End Set
        End Property
        Public Function GetSerializedText(Optional ByVal ReduceSize As Boolean = True) As String
            Dim writer As New Xml.Serialization.XmlSerializer(GetType(file))
            Dim sb As New Text.StringBuilder
            Dim t As New IO.StringWriter(sb)
            Dim ns As New Xml.Serialization.XmlSerializerNamespaces({New Xml.XmlQualifiedName("v", "1")})
            writer.Serialize(t, Me, ns)
            sb.Remove(0, 41)
            Return sb.ToString().Replace("<file xmlns:v=""1""", "<file")
        End Function
        Public Function GetCopy(fileuid1 As Long) As file
            Dim result As New file With {.accesstime = accesstime, .backuptime = backuptime,
                .changetime = changetime, .creationtime = creationtime,
                .fileuid = fileuid1,
                .fullpath = fullpath, .length = length,
                .modifytime = modifytime, .name = name, .openforwrite = openforwrite, .readonly = [readonly],
                .tag = tag}
            result.extendedattributes = New List(Of xattr)
            For Each x As xattr In extendedattributes
                result.extendedattributes.Add(New xattr With {.key = x.key, .value = x.value})
            Next
            result.extentinfo = New List(Of extent)
            For Each xt As extent In extentinfo
                result.extentinfo.Add(New extent With {.bytecount = xt.bytecount, .byteoffset = xt.byteoffset, .fileoffset = xt.fileoffset, .partition = xt.partition, .startblock = xt.startblock})
            Next
            Return result
        End Function
    End Class
    <Serializable>
    <TypeConverter(GetType(ExpandableObjectConverter))>
    Public Class directory
        Private _name As String
        Private _readonly As Boolean = False
        Private _creationtime As String
        Private _changetime As String
        Private _modifytime As String
        Private _accesstime As String
        Private _backuptime As String
        Private _fileuid As Long
        Private _contents As contentsDef = New contentsDef
        Private _lazyStore As LazySchemaStore
        Private _lazyRecordOffset As Long
        Private _lazyMetadataLoaded As Boolean = True
        Private _lazyContentsLoaded As Boolean = True
        Private _lazyCountsLoaded As Boolean = True
        Private _lazyTotalFileCount As Long
        Private _lazyTotalDirectoryCount As Long
        Private _totalCountsDirty As Boolean
        Private _lazyFileCursorIndex As Integer = -1
        Private _lazyFileCursorOffset As Long = -1
        Private _lazyDirectoryCursorIndex As Integer = -1
        Private _lazyDirectoryCursorOffset As Long = -1
        Private _lazyParent As directory
        Private ReadOnly _lazyLoadLock As New Object

        Private Sub MarkLazyMetadataDirty()
            If _lazyStore IsNot Nothing AndAlso _lazyRecordOffset >= 0 Then
                _lazyStore.RegisterModifiedDirectory(_lazyRecordOffset, Me)
            End If
        End Sub

        Private Sub EnsureLazyMetadata()
            If _lazyStore Is Nothing OrElse _lazyMetadataLoaded Then Exit Sub

            Dim values As LazyDirectoryScalarData = _lazyStore.ReadDirectoryScalars(_lazyRecordOffset)
            _name = values.Name
            _readonly = values.ReadOnly
            _creationtime = values.CreationTime
            _changetime = values.ChangeTime
            _modifytime = values.ModifyTime
            _accesstime = values.AccessTime
            _backuptime = values.BackupTime
            _fileuid = values.FileUid
            _lazyMetadataLoaded = True
        End Sub

        Private Sub EnsureLazyContents()
            If _lazyStore Is Nothing OrElse _lazyContentsLoaded Then Exit Sub

            SyncLock _lazyLoadLock
                If _lazyContentsLoaded Then Exit Sub
                If _contents Is Nothing Then _contents = New contentsDef

                For Each childDirectory As directory In EnumerateLazyDirectories()
                    _contents._directory.Add(childDirectory)
                Next
                For Each childFile As file In EnumerateLazyFiles()
                    _contents._file.Add(childFile)
                Next

                _lazyContentsLoaded = True
            End SyncLock
        End Sub

        Friend Sub AttachLazyRecord(store As LazySchemaStore,
                                    recordOffset As Long,
                                    Optional parent As directory = Nothing)
            _lazyStore = store
            _lazyRecordOffset = recordOffset
            _lazyParent = parent
            _lazyMetadataLoaded = False
            _lazyContentsLoaded = False
            _lazyCountsLoaded = False
            _lazyFileCursorIndex = -1
            _lazyFileCursorOffset = -1
            _lazyDirectoryCursorIndex = -1
            _lazyDirectoryCursorOffset = -1
            _contents = New contentsDef
        End Sub

        Friend Sub AttachLazyParent(parent As directory)
            _lazyParent = parent
        End Sub

        Private Sub PropagateLazyTotalDelta(fileDelta As Long, directoryDelta As Long)
            If fileDelta = 0 AndAlso directoryDelta = 0 Then Exit Sub
            If _lazyContentsLoaded Then _totalCountsDirty = True
            If _lazyStore IsNot Nothing Then _lazyStore.ApplyDirectoryTotalDelta(_lazyRecordOffset, fileDelta, directoryDelta)
            If _lazyParent IsNot Nothing Then _lazyParent.PropagateLazyTotalDelta(fileDelta, directoryDelta)
        End Sub

        Private Function CreateLazyFile(child As LazySchemaChildData) As file
            Dim modified As file = _lazyStore.GetModifiedFile(child.RecordOffset)
            If modified IsNot Nothing Then Return modified

            Dim result As New file
            result.AttachLazyRecord(_lazyStore, child.RecordOffset, child.RecordLength)
            Return result
        End Function

        Private Function CreateLazyDirectory(child As LazySchemaChildData) As directory
            Dim modified As directory = _lazyStore.GetModifiedDirectory(child.RecordOffset)
            If modified IsNot Nothing Then
                modified.AttachLazyParent(Me)
                Return modified
            End If

            Dim result As New directory
            result.AttachLazyRecord(_lazyStore, child.RecordOffset, Me)
            Return result
        End Function

        Friend Function FindFileByName(fileName As String) As file
            If fileName Is Nothing Then Return Nothing
            For Each item As file In EnumerateLazyFiles()
                If String.Equals(item.name, fileName, StringComparison.OrdinalIgnoreCase) Then Return item
            Next
            Return Nothing
        End Function

        Friend Function FindFilesByName(fileName As String) As List(Of file)
            Dim result As New List(Of file)
            If fileName Is Nothing Then Return result
            For Each item As file In EnumerateLazyFiles()
                If String.Equals(item.name, fileName, StringComparison.OrdinalIgnoreCase) Then result.Add(item)
            Next
            Return result
        End Function

        Friend Function FindDirectoryByName(directoryName As String) As directory
            If directoryName Is Nothing Then Return Nothing
            For Each item As directory In EnumerateLazyDirectories()
                If String.Equals(item.name, directoryName, StringComparison.OrdinalIgnoreCase) Then Return item
            Next
            Return Nothing
        End Function

        Friend Function FindDirectoriesByName(directoryName As String) As List(Of directory)
            Dim result As New List(Of directory)
            If directoryName Is Nothing Then Return result
            For Each item As directory In EnumerateLazyDirectories()
                If String.Equals(item.name, directoryName, StringComparison.OrdinalIgnoreCase) Then result.Add(item)
            Next
            Return result
        End Function

        Friend Sub AddFile(value As file)
            If value Is Nothing Then Exit Sub
            If _lazyStore IsNot Nothing AndAlso Not _lazyContentsLoaded Then
                Dim delta As Long = _lazyStore.AddDirectoryFile(_lazyRecordOffset, value)
                PropagateLazyTotalDelta(delta, 0)
            Else
                If _contents Is Nothing Then _contents = New contentsDef
                _contents._file.Add(value)
                PropagateLazyTotalDelta(1, 0)
            End If
        End Sub

        Friend Function RemoveFile(value As file) As Boolean
            If value Is Nothing Then Return False
            If _lazyStore IsNot Nothing AndAlso Not _lazyContentsLoaded Then
                Dim delta As Long = _lazyStore.RemoveDirectoryFile(_lazyRecordOffset, value)
                PropagateLazyTotalDelta(delta, 0)
                Return delta <> 0
            End If
            If _contents Is Nothing OrElse _contents._file Is Nothing Then Return False
            Dim removed As Boolean = _contents._file.Remove(value)
            If removed Then PropagateLazyTotalDelta(-1, 0)
            Return removed
        End Function

        Friend Sub AddDirectory(value As directory)
            If value Is Nothing Then Exit Sub
            value.AttachLazyParent(Me)
            If _lazyStore IsNot Nothing AndAlso Not _lazyContentsLoaded Then
                Dim delta As LazyTotalDelta = _lazyStore.AddDirectoryDirectory(_lazyRecordOffset, value)
                PropagateLazyTotalDelta(delta.FileCount, delta.DirectoryCount)
            Else
                If _contents Is Nothing Then _contents = New contentsDef
                _contents._directory.Add(value)
                PropagateLazyTotalDelta(value.TotalFiles, 1L + value.TotalDirectories)
            End If
        End Sub

        Friend Function RemoveDirectory(value As directory) As Boolean
            If value Is Nothing Then Return False
            If _lazyStore IsNot Nothing AndAlso Not _lazyContentsLoaded Then
                Dim delta As LazyTotalDelta = _lazyStore.RemoveDirectoryDirectory(_lazyRecordOffset, value)
                PropagateLazyTotalDelta(delta.FileCount, delta.DirectoryCount)
                If delta.FileCount <> 0 OrElse delta.DirectoryCount <> 0 Then value.AttachLazyParent(Nothing)
                Return delta.FileCount <> 0 OrElse delta.DirectoryCount <> 0
            End If
            If _contents Is Nothing OrElse _contents._directory Is Nothing Then Return False
            Dim removed As Boolean = _contents._directory.Remove(value)
            If removed Then
                PropagateLazyTotalDelta(-value.TotalFiles, -(1L + value.TotalDirectories))
                value.AttachLazyParent(Nothing)
            End If
            Return removed
        End Function

        Private Sub EnsureLazyCounts()
            If _lazyStore Is Nothing OrElse _lazyCountsLoaded Then Exit Sub
            _lazyTotalFileCount = _lazyStore.GetDirectoryTotalFileCount(_lazyRecordOffset)
            _lazyTotalDirectoryCount = _lazyStore.GetDirectoryTotalDirectoryCount(_lazyRecordOffset)
            _lazyCountsLoaded = True
        End Sub

        Friend ReadOnly Property HasUnmaterializedLazyContents As Boolean
            Get
                Return _lazyStore IsNot Nothing AndAlso Not _lazyContentsLoaded
            End Get
        End Property

        Friend ReadOnly Property LazyRecordOffset As Long
            Get
                Return _lazyRecordOffset
            End Get
        End Property

        Friend ReadOnly Property HasLazyRecord As Boolean
            Get
                Return _lazyStore IsNot Nothing AndAlso _lazyRecordOffset >= 0
            End Get
        End Property

        Friend ReadOnly Property LazyStoreReference As Object
            Get
                Return _lazyStore
            End Get
        End Property

        Friend Function HasPotentialChildren() As Boolean
            If _lazyStore IsNot Nothing AndAlso Not _lazyContentsLoaded Then
                Return _lazyStore.GetDirectoryFileCount(_lazyRecordOffset) > 0 OrElse
                       _lazyStore.GetDirectoryDirectoryCount(_lazyRecordOffset) > 0
            End If
            Return _contents IsNot Nothing AndAlso
                   ((_contents._directory IsNot Nothing AndAlso _contents._directory.Count > 0) OrElse
                    (_contents._file IsNot Nothing AndAlso _contents._file.Count > 0))
        End Function

        Friend Function GetLazyDirectFileCount() As Integer
            If _lazyStore Is Nothing OrElse _lazyContentsLoaded Then Return If(_contents Is Nothing OrElse _contents._file Is Nothing, 0, _contents._file.Count)
            Return _lazyStore.GetDirectoryFileCount(_lazyRecordOffset)
        End Function

        Friend Function GetLazyDirectDirectoryCount() As Integer
            If _lazyStore Is Nothing OrElse _lazyContentsLoaded Then Return If(_contents Is Nothing OrElse _contents._directory Is Nothing, 0, _contents._directory.Count)
            Return _lazyStore.GetDirectoryDirectoryCount(_lazyRecordOffset)
        End Function

        Friend Function GetLazyFileAt(index As Integer) As file
            If _lazyStore Is Nothing OrElse _lazyContentsLoaded Then Return _contents._file(index)
            SyncLock _lazyLoadLock
                Dim logicalIndex As Integer = 0
                If _lazyStore.GetRemovedFileCount(_lazyRecordOffset) = 0 Then
                    Dim rawCount As Integer = _lazyStore.ReadDirectoryFileCount(_lazyRecordOffset)
                    If index < rawCount Then
                        Dim child As LazySchemaChildData = _lazyStore.ReadFileAt(_lazyRecordOffset, index, _lazyFileCursorIndex, _lazyFileCursorOffset)
                        Return CreateLazyFile(child)
                    End If
                    logicalIndex = rawCount
                Else
                    For Each child As LazySchemaChildData In _lazyStore.EnumerateFileReferences(_lazyRecordOffset)
                        If _lazyStore.IsFileRemoved(_lazyRecordOffset, child.RecordOffset) Then Continue For
                        If logicalIndex = index Then Return CreateLazyFile(child)
                        logicalIndex += 1
                    Next
                End If

                For Each added As file In _lazyStore.EnumerateAddedFiles(_lazyRecordOffset)
                    If logicalIndex = index Then Return added
                    logicalIndex += 1
                Next
            End SyncLock
            Throw New ArgumentOutOfRangeException(NameOf(index))
        End Function

        Friend Function GetLazyDirectoryAt(index As Integer) As directory
            If _lazyStore Is Nothing OrElse _lazyContentsLoaded Then Return _contents._directory(index)
            SyncLock _lazyLoadLock
                Dim logicalIndex As Integer = 0
                If _lazyStore.GetRemovedDirectoryCount(_lazyRecordOffset) = 0 Then
                    Dim rawCount As Integer = _lazyStore.ReadDirectoryDirectoryCount(_lazyRecordOffset)
                    If index < rawCount Then
                        Dim child As LazySchemaChildData = _lazyStore.ReadDirectoryAt(_lazyRecordOffset, index, _lazyDirectoryCursorIndex, _lazyDirectoryCursorOffset)
                        Return CreateLazyDirectory(child)
                    End If
                    logicalIndex = rawCount
                Else
                    For Each child As LazySchemaChildData In _lazyStore.EnumerateDirectoryReferences(_lazyRecordOffset)
                        If _lazyStore.IsDirectoryRemoved(_lazyRecordOffset, child.RecordOffset) Then Continue For
                        If logicalIndex = index Then Return CreateLazyDirectory(child)
                        logicalIndex += 1
                    Next
                End If

                For Each added As directory In _lazyStore.EnumerateAddedDirectories(_lazyRecordOffset)
                    If logicalIndex = index Then Return added
                    logicalIndex += 1
                Next
            End SyncLock
            Throw New ArgumentOutOfRangeException(NameOf(index))
        End Function

        Friend Iterator Function EnumerateLazyFiles() As IEnumerable(Of file)
            If _lazyStore Is Nothing OrElse _lazyContentsLoaded Then
                If _contents Is Nothing OrElse _contents._file Is Nothing Then Exit Function
                For Each item As file In _contents._file
                    Yield item
                Next
                Exit Function
            End If

            For Each child As LazySchemaChildData In _lazyStore.EnumerateFileReferences(_lazyRecordOffset)
                If _lazyStore.IsFileRemoved(_lazyRecordOffset, child.RecordOffset) Then Continue For
                Yield CreateLazyFile(child)
            Next
            For Each added As file In _lazyStore.EnumerateAddedFiles(_lazyRecordOffset)
                Yield added
            Next
        End Function

        Friend Iterator Function EnumerateLazyDirectories() As IEnumerable(Of directory)
            If _lazyStore Is Nothing OrElse _lazyContentsLoaded Then
                If _contents Is Nothing OrElse _contents._directory Is Nothing Then Exit Function
                For Each item As directory In _contents._directory
                    Yield item
                Next
                Exit Function
            End If

            For Each child As LazySchemaChildData In _lazyStore.EnumerateDirectoryReferences(_lazyRecordOffset)
                If _lazyStore.IsDirectoryRemoved(_lazyRecordOffset, child.RecordOffset) Then Continue For
                Yield CreateLazyDirectory(child)
            Next
            For Each added As directory In _lazyStore.EnumerateAddedDirectories(_lazyRecordOffset)
                Yield added
            Next
        End Function

        <Category("LTFSIndex")>
        Public Property name As String
            Get
                EnsureLazyMetadata()
                Return _name
            End Get
            Set(value As String)
                EnsureLazyMetadata()
                _name = value
                MarkLazyMetadataDirty()
            End Set
        End Property
        <Category("LTFSIndex")>
        Public Property [readonly] As Boolean
            Get
                EnsureLazyMetadata()
                Return _readonly
            End Get
            Set(value As Boolean)
                EnsureLazyMetadata()
                _readonly = value
                MarkLazyMetadataDirty()
            End Set
        End Property
        <Category("LTFSIndex")>
        Public Property creationtime As String
            Get
                EnsureLazyMetadata()
                Return _creationtime
            End Get
            Set(value As String)
                EnsureLazyMetadata()
                _creationtime = value
                MarkLazyMetadataDirty()
            End Set
        End Property
        <Category("LTFSIndex")>
        Public Property changetime As String
            Get
                EnsureLazyMetadata()
                Return _changetime
            End Get
            Set(value As String)
                EnsureLazyMetadata()
                _changetime = value
                MarkLazyMetadataDirty()
            End Set
        End Property
        <Category("LTFSIndex")>
        Public Property modifytime As String
            Get
                EnsureLazyMetadata()
                Return _modifytime
            End Get
            Set(value As String)
                EnsureLazyMetadata()
                _modifytime = value
                MarkLazyMetadataDirty()
            End Set
        End Property
        <Category("LTFSIndex")>
        Public Property accesstime As String
            Get
                EnsureLazyMetadata()
                Return _accesstime
            End Get
            Set(value As String)
                EnsureLazyMetadata()
                _accesstime = value
                MarkLazyMetadataDirty()
            End Set
        End Property
        <Category("LTFSIndex")>
        Public Property backuptime As String
            Get
                EnsureLazyMetadata()
                Return _backuptime
            End Get
            Set(value As String)
                EnsureLazyMetadata()
                _backuptime = value
                MarkLazyMetadataDirty()
            End Set
        End Property
        <Category("LTFSIndex")>
        Public Property fileuid As Long
            Get
                EnsureLazyMetadata()
                Return _fileuid
            End Get
            Set(value As Long)
                EnsureLazyMetadata()
                _fileuid = value
                MarkLazyMetadataDirty()
            End Set
        End Property
        <Xml.Serialization.XmlIgnore>
        <Category("Internal")>
        <TypeConverter(GetType(ListTypeDescriptor(Of List(Of file), file)))>
        Public Property UnwrittenFiles As New List(Of file)
        <Xml.Serialization.XmlIgnore>
        <Category("Internal")>
        Public Property LastUnwrittenFilesCount As Integer
        <Category("LTFSIndex")>
        Public Property contents As contentsDef
            Get
                EnsureLazyContents()
                Return _contents
            End Get
            Set(value As contentsDef)
                _contents = value
                _lazyContentsLoaded = True
                _totalCountsDirty = True
                MarkLazyMetadataDirty()
            End Set
        End Property

        Friend Function GetLazyScalarDataForWrite() As LazyDirectoryScalarData
            EnsureLazyMetadata()
            Return New LazyDirectoryScalarData With {
                .Name = _name,
                .ReadOnly = _readonly,
                .CreationTime = _creationtime,
                .ChangeTime = _changetime,
                .ModifyTime = _modifytime,
                .AccessTime = _accesstime,
                .BackupTime = _backuptime,
                .FileUid = _fileuid}
        End Function
        '<Xml.Serialization.XmlIgnore>
        '<Category("Internal")>
        'Public ReadOnly Property Files As List(Of file)
        '    Get
        '        Return contents._file
        '    End Get
        'End Property
        '<Xml.Serialization.XmlIgnore>
        '<Category("Internal")>
        'Public ReadOnly Property Directories As List(Of directory)
        '    Get
        '        Return contents._directory
        '    End Get
        'End Property
        <Xml.Serialization.XmlIgnore>
        <Category("Internal")>
        Public Property tag As String

        Private _TotalFiles, _TotalDirectories, _TotalFilesUnwritten As Long
        <Xml.Serialization.XmlIgnore>
        <Category("Internal")>
        Public ReadOnly Property TotalFiles As Long
            Get
                If _lazyStore IsNot Nothing AndAlso Not _lazyContentsLoaded Then
                    Return _lazyStore.GetDirectoryTotalFileCount(_lazyRecordOffset)
                End If
                If _totalCountsDirty Then RefreshCount()
                If _TotalDirectories = 0 AndAlso contents._directory IsNot Nothing AndAlso contents._directory.Count > 0 Then
                    RefreshCount()
                End If
                If _TotalFiles = 0 AndAlso contents._file IsNot Nothing AndAlso contents._file.Count > 0 Then
                    RefreshCount()
                End If
                Return _TotalFiles
            End Get
        End Property
        <Xml.Serialization.XmlIgnore>
        <Category("Internal")>
        Public ReadOnly Property TotalFilesUnwritten As Long
            Get
                If _lazyStore IsNot Nothing AndAlso Not _lazyContentsLoaded Then
                    Return If(UnwrittenFiles Is Nothing, 0L, UnwrittenFiles.Count)
                End If
                If _totalCountsDirty Then RefreshCount()
                If _TotalDirectories = 0 AndAlso contents._directory IsNot Nothing AndAlso contents._directory.Count > 0 Then
                    RefreshCount()
                End If
                If _TotalFiles = 0 AndAlso contents._file IsNot Nothing AndAlso contents._file.Count > 0 Then
                    RefreshCount()
                End If
                If _TotalFilesUnwritten = 0 AndAlso UnwrittenFiles IsNot Nothing AndAlso UnwrittenFiles.Count > 0 Then
                    RefreshCount()
                End If
                Return _TotalFilesUnwritten
            End Get
        End Property
        <Xml.Serialization.XmlIgnore>
        <Category("Internal")>
        Public ReadOnly Property TotalDirectories As Long
            Get
                If _lazyStore IsNot Nothing AndAlso Not _lazyContentsLoaded Then
                    Return _lazyStore.GetDirectoryTotalDirectoryCount(_lazyRecordOffset)
                End If
                If _totalCountsDirty Then RefreshCount()
                If _TotalDirectories = 0 AndAlso contents._directory IsNot Nothing AndAlso contents._directory.Count > 0 Then
                    RefreshCount()
                End If
                If _TotalFiles = 0 AndAlso contents._file IsNot Nothing AndAlso contents._file.Count > 0 Then
                    RefreshCount()
                End If
                Return _TotalDirectories
            End Get
        End Property
        Public Sub RefreshCount()
            If _lazyStore IsNot Nothing AndAlso Not _lazyContentsLoaded Then
                _TotalFiles = _lazyStore.GetDirectoryTotalFileCount(_lazyRecordOffset)
                _TotalDirectories = _lazyStore.GetDirectoryTotalDirectoryCount(_lazyRecordOffset)
                _TotalFilesUnwritten = If(UnwrittenFiles Is Nothing, 0L, UnwrittenFiles.Count)
                _totalCountsDirty = False
                Return
            End If
            If contents._directory Is Nothing OrElse contents._directory.Count = 0 Then
                If contents._file IsNot Nothing Then
                    _TotalFiles = contents._file.Count
                Else
                    _TotalFiles = 0
                End If
                If UnwrittenFiles IsNot Nothing Then
                    _TotalFilesUnwritten = UnwrittenFiles.Count
                Else
                    _TotalFilesUnwritten = 0
                End If
            Else
                If contents._file IsNot Nothing Then
                    _TotalFiles = contents._file.Count
                End If
                If UnwrittenFiles IsNot Nothing Then
                    _TotalFilesUnwritten = UnwrittenFiles.Count
                End If
                For Each d As directory In contents._directory
                    _TotalFiles += d.TotalFiles
                    _TotalFilesUnwritten += d.TotalFilesUnwritten
                Next
            End If
            If contents._directory IsNot Nothing Then
                _TotalDirectories = contents._directory.Count
                For Each d As directory In contents._directory
                    _TotalDirectories += d.TotalDirectories
                Next
            End If
            _totalCountsDirty = False
        End Sub
        Public Sub DeepRefreshCount()
            If _lazyStore IsNot Nothing AndAlso Not _lazyContentsLoaded Then
                _TotalFiles = _lazyStore.GetDirectoryTotalFileCount(_lazyRecordOffset)
                _TotalDirectories = _lazyStore.GetDirectoryTotalDirectoryCount(_lazyRecordOffset)
                _TotalFilesUnwritten = If(UnwrittenFiles Is Nothing, 0L, UnwrittenFiles.Count)
                _totalCountsDirty = False
                Return
            End If
            _TotalFiles = 0
            _TotalDirectories = 0
            For Each d As directory In contents._directory
                d.DeepRefreshCount()
            Next
            RefreshCount()
        End Sub


        <Xml.Serialization.XmlIgnore>
        <Category("Internal")>
        Public Property fullpath As String
        <Xml.Serialization.XmlIgnore>
        <Category("Internal")>
        Public Property Selected As Boolean = True

        Public Function GetSerializedText(Optional ByVal ReduceSize As Boolean = True) As String
            If _lazyStore IsNot Nothing Then
                Dim lazyText As New Text.StringBuilder(40960)
                Using lazyWriter As New IO.StringWriter(lazyText)
                    _lazyStore.WriteDirectory(Me, lazyWriter, useCollectionWrappers:=True)
                End Using
                Return lazyText.ToString()
            End If
            Dim writer As New Xml.Serialization.XmlSerializer(GetType(directory))
            Dim sb As New Text.StringBuilder
            Dim t As New IO.StringWriter(sb)
            writer.Serialize(t, Me)
            Return sb.ToString()
        End Function
        Public Function SaveFile(FileName As String) As Boolean
            If _lazyStore IsNot Nothing Then
                Try
                    Using lazyWriter As New IO.StreamWriter(FileName, append:=False, encoding:=New Text.UTF8Encoding(False), bufferSize:=1 << 16)
                        _lazyStore.WriteDirectory(Me, lazyWriter, useCollectionWrappers:=True)
                    End Using
                    Return True
                Catch
                    Return False
                End Try
            End If
            Dim writer As New Xml.Serialization.XmlSerializer(GetType(directory))
            Dim ms As New IO.FileStream(FileName, IO.FileMode.Create)
            Dim t As IO.TextWriter = New IO.StreamWriter(ms, New Text.UTF8Encoding(False))
            Dim ns As New Xml.Serialization.XmlSerializerNamespaces({New Xml.XmlQualifiedName("v", "2.4.0")})
            writer.Serialize(t, Me, ns)
            t.Close()
            ms.Close()
            Return True
        End Function
        Public Shared Function FromXML(s As String) As directory
            Dim reader As New Xml.Serialization.XmlSerializer(GetType(directory))
            Dim t As IO.TextReader = New IO.StringReader(s)
            Return CType(reader.Deserialize(t), directory)
        End Function
        Public Shared Function FromFile(FileName As String) As directory
            Dim result As directory
            Dim reader As New Xml.Serialization.XmlSerializer(GetType(directory))
            Dim t As IO.StreamReader = New IO.StreamReader(FileName)
            result = CType(reader.Deserialize(t), directory)
            t.Close()
            Return result
        End Function
    End Class
    <Serializable>
    <TypeConverter(GetType(ExpandableObjectConverter))>
    Public Class contentsDef
        <Category("LTFSIndex")>
        <TypeConverter(GetType(ListTypeDescriptor(Of List(Of file), file)))>
        Public Property _file As New List(Of file)
        <Category("LTFSIndex")>
        <TypeConverter(GetType(ListTypeDescriptor(Of List(Of directory), directory)))>
        Public Property _directory As New List(Of directory)
    End Class
    <Category("LTFSIndex")>
    <TypeConverter(GetType(ListTypeDescriptor(Of List(Of file), file)))>
    Public Property _file As New List(Of file)
    <Category("LTFSIndex")>
    <TypeConverter(GetType(ListTypeDescriptor(Of List(Of directory), directory)))>
    Public Property _directory As New List(Of directory)
    <Xml.Serialization.XmlIgnore>
    Public ReadOnly Property IsLazyLoaded As Boolean
        Get
            Return _lazyStore IsNot Nothing
        End Get
    End Property
    Private _lazyStore As LazySchemaStore

    Friend Sub AttachLazyStore(store As LazySchemaStore)
        _lazyStore = store
    End Sub

    <Xml.Serialization.XmlIgnore>
    <Category("Internal")>
    Public Shared Property Searializing As Boolean = False

    Public Sub Standarize()
        Exit Sub
        Dim q As New List(Of directory)
        For Each f As file In _file
            If f.sha1 IsNot Nothing Then
                If f.sha1.Length = 40 Then
                    f.SetXattr("ltfs.hash.sha1sum", f.sha1)
                End If
                f.sha1 = Nothing
            End If
        Next
        For Each d As directory In _directory
            q.Add(d)
        Next
        While q.Count > 0
            Dim qn As New List(Of directory)
            For Each d As directory In q
                For Each fn As file In d.contents._file
                    If fn.sha1 IsNot Nothing Then
                        If fn.sha1.Length = 40 Then
                            fn.SetXattr("ltfs.hash.sha1sum", fn.sha1)
                        End If
                        fn.sha1 = Nothing
                    End If
                Next
                For Each dn As directory In d.contents._directory
                    qn.Add(dn)
                Next
            Next
            q = qn
        End While
    End Sub
    Public Function GetDirectory(path As String) As directory
        Dim p() As String = path.Split({"\", "/"}, StringSplitOptions.None)
        If p.Count <= 0 Then Return Nothing
        If _directory Is Nothing OrElse _directory.Count = 0 Then Return Nothing
        Dim result As directory
        If p(0) = _directory(0).name Then
            result = _directory(0)
        Else
            Return Nothing
        End If
        For i As Integer = 1 To p.Length - 1
            Dim found As Boolean = False
            For Each childDirectory As directory In result.EnumerateLazyDirectories()
                If childDirectory.name = p(i) Then
                    result = childDirectory
                    found = True
                    Exit For
                End If
            Next
            If Not found Then Return Nothing
        Next
        Return result
    End Function
    Public Function GetFile(path As String) As file
        Dim p() As String = path.Split({"\", "/"}, StringSplitOptions.None)
        If p.Count <= 0 Then Return Nothing
        If _directory Is Nothing OrElse _directory.Count = 0 Then Return Nothing
        Dim result As directory
        If p(0) = _directory(0).name Then
            result = _directory(0)
        Else
            Return Nothing
        End If
        For i As Integer = 1 To p.Length - 2
            Dim found As Boolean = False
            For Each childDirectory As directory In result.EnumerateLazyDirectories()
                If childDirectory.name = p(i) Then
                    result = childDirectory
                    found = True
                    Exit For
                End If
            Next
            If Not found Then Return Nothing
        Next
        For Each childFile As file In result.EnumerateLazyFiles()
            If childFile.name = p.Last Then
                Return childFile
            End If
        Next
        Return Nothing
    End Function
    Public Function GetSerializedText(Optional ByVal ReduceSize As Boolean = True) As String
        Dim sb As New Text.StringBuilder(40960)
        Using sw As New IO.StringWriter(sb)
            WriteSerializedText(sw, ReduceSize)
        End Using
        Return sb.ToString()
    End Function

    Public Sub WriteSerializedText(output As IO.TextWriter, Optional ByVal reduceSize As Boolean = True)
        Searializing = True
        Standarize()

        If _lazyStore IsNot Nothing Then
            Try
                _lazyStore.WriteSchema(Me, output, reduceSize)
            Finally
                Searializing = False
            End Try
            Return
        End If

        Const buf As Integer = 1 << 16
        Dim serializer As New Xml.Serialization.XmlSerializer(GetType(ltfsindex))
        Dim ns As New Xml.Serialization.XmlSerializerNamespaces({New Xml.XmlQualifiedName("v", "2.4.0")})

        Try
            Dim tempFile As String = $"{Application.StartupPath}\LCG_{Now:yyyyMMdd_HHmmss.fffffff}.tmp"
            Using sw As New IO.StreamWriter(tempFile, append:=False, encoding:=New Text.UTF8Encoding(False), bufferSize:=buf)
                serializer.Serialize(sw, Me, ns)
            End Using

            Using r As New IO.StreamReader(tempFile, Text.Encoding.UTF8, detectEncodingFromByteOrderMarks:=True, bufferSize:=buf)
                Dim line As String = r.ReadLine()

                If line IsNot Nothing Then
                    If line.StartsWith("<?xml", StringComparison.Ordinal) AndAlso line.IndexOf("utf-8", StringComparison.Ordinal) >= 0 Then
                        line = line.Replace("utf-8", "UTF-8")
                    End If
                    If reduceSize AndAlso line.IndexOf("xmlns:v", StringComparison.Ordinal) >= 0 Then
                        line = line.Replace("xmlns:v", "version")
                    End If
                    If reduceSize Then line = line.Trim(" "c)
                    If line.Length > 0 Then output.WriteLine(line)
                End If

                Do
                    line = r.ReadLine()
                    If line Is Nothing Then Exit Do

                    If reduceSize Then
                        If line.IndexOf("xmlns:v", StringComparison.Ordinal) >= 0 Then
                            line = line.Replace("xmlns:v", "version")
                        End If
                        If line.IndexOf("_file", StringComparison.Ordinal) >= 0 Then
                            line = line.Replace("<_file />", "").Replace("<_file>", "").Replace("</_file>", "")
                        End If
                        If line.IndexOf("_directory", StringComparison.Ordinal) >= 0 Then
                            line = line.Replace("<_directory />", "").Replace("<_directory>", "").Replace("</_directory>", "")
                        End If
                        line = line.Trim(" "c)
                    End If

                    If line.Length > 0 Then output.WriteLine(line)
                Loop
            End Using
            Try
                IO.File.Delete(tempFile)
            Catch
            End Try

        Finally
            Searializing = False
        End Try
    End Sub

    Public Function SaveFile(fileName As String) As Boolean
        Searializing = True
        Standarize()

        If _lazyStore IsNot Nothing Then
            Try
                Using lazyWriter As New IO.StreamWriter(fileName, append:=False, encoding:=New Text.UTF8Encoding(False), bufferSize:=1 << 16)
                    _lazyStore.WriteSchema(Me, lazyWriter, True)
                End Using
                Return True
            Catch
                Return False
            Finally
                Searializing = False
            End Try
        End If

        Dim tempFile As String = $"{Application.StartupPath}\LCG_{Now:yyyyMMdd_HHmmss.fffffff}.tmp"
        Const buf As Integer = 1 << 16

        Try
            Using writer As New IO.StreamWriter(tempFile, append:=False, encoding:=New Text.UTF8Encoding(False), bufferSize:=buf)
                Dim serializer As New Xml.Serialization.XmlSerializer(GetType(ltfsindex))
                Dim ns As New Xml.Serialization.XmlSerializerNamespaces({New Xml.XmlQualifiedName("v", "2.4.0")})
                serializer.Serialize(writer, Me, ns)
            End Using

            Using r As New IO.StreamReader(tempFile, Text.Encoding.UTF8, detectEncodingFromByteOrderMarks:=True, bufferSize:=buf)
                Using w As New IO.StreamWriter(fileName, append:=False, encoding:=New Text.UTF8Encoding(False), bufferSize:=buf)
                    Dim sline As String = r.ReadLine()
                    If sline IsNot Nothing Then
                        If sline.StartsWith("<?xml", StringComparison.Ordinal) Then
                            If sline.IndexOf("utf-8", StringComparison.Ordinal) >= 0 Then
                                sline = sline.Replace("utf-8", "UTF-8")
                            End If
                        End If
                        If sline.IndexOf("xmlns:v", StringComparison.Ordinal) >= 0 Then
                            sline = sline.Replace("xmlns:v", "version")
                        End If
                        sline = sline.Trim(" "c)
                        If sline.Length > 0 Then w.WriteLine(sline)
                    End If

                    Do
                        sline = r.ReadLine()
                        If sline Is Nothing Then Exit Do
                        If sline.IndexOf("xmlns:v", StringComparison.Ordinal) >= 0 Then
                            sline = sline.Replace("xmlns:v", "version")
                        End If
                        If sline.IndexOf("_file", StringComparison.Ordinal) >= 0 Then
                            sline = sline.Replace("<_file />", "") _
                                         .Replace("<_file>", "") _
                                         .Replace("</_file>", "")
                        End If
                        If sline.IndexOf("_directory", StringComparison.Ordinal) >= 0 Then
                            sline = sline.Replace("<_directory />", "") _
                                         .Replace("<_directory>", "") _
                                         .Replace("</_directory>", "")
                        End If
                        sline = sline.Trim(" "c)
                        If sline.Length > 0 Then w.WriteLine(sline)
                    Loop
                End Using
            End Using

            Return True

        Catch
            Return False

        Finally
            Searializing = False
            Try
                If IO.File.Exists(tempFile) Then IO.File.Delete(tempFile)
            Catch
            End Try
        End Try
    End Function

    Public Shared Function FromXML(s As String) As ltfsindex
        Dim reader As New Xml.Serialization.XmlSerializer(GetType(ltfsindex))
        Dim t As IO.TextReader = New IO.StringReader(s)
        Return CType(reader.Deserialize(t), ltfsindex)
    End Function
    Public Shared Function FromSchemaText(s As String) As ltfsindex
        s = s.Replace("<directory>", "<_directory><directory>")
        s = s.Replace("</directory>", "</directory></_directory>")
        s = s.Replace("<file>", "<_file><file>")
        s = s.Replace("</file>", "</file></_file>")
        s = s.Replace("%25", "%")
        Dim reader As New Xml.Serialization.XmlSerializer(GetType(ltfsindex))
        Dim t As IO.TextReader = New IO.StringReader(s)
        Dim result As ltfsindex = CType(reader.Deserialize(t), ltfsindex)
        result.Standarize()
        Return result
    End Function
    Private Const LazyLoadThresholdBytes As Long = 4L * 1024L * 1024L

    Public Shared Function FromSchFile(FileName As String) As ltfsindex
        Try
            If IO.File.Exists(FileName) AndAlso New IO.FileInfo(FileName).Length >= LazyLoadThresholdBytes Then
                Return LazySchemaReader.Load(FileName)
            End If

            Return FromSchFileEager(FileName)
        Catch ex As Exception
            MessageBox.Show(New Form With {.TopMost = True}, ex.ToString)
            Return Nothing
        End Try
    End Function

    Private Shared Function FromSchFileEager(FileName As String) As ltfsindex
        Const BUF As Integer = 1 << 16 ' 64 KiB
        Dim tmpf As String = $"{Application.StartupPath}\LCX_{Now:yyyyMMdd_HHmmss.fffffff}_{Guid.NewGuid()}.tmp"
        Dim result As ltfsindex = Nothing

        Try
            Using sin As New IO.StreamReader(FileName, Text.Encoding.UTF8, detectEncodingFromByteOrderMarks:=True, bufferSize:=BUF)
                Using soutx As New IO.StreamWriter(tmpf, append:=False, encoding:=New Text.UTF8Encoding(False), bufferSize:=BUF)
                    Do
                        Dim s As String = sin.ReadLine()
                        If s Is Nothing Then Exit Do

                        If s.Length = 0 Then
                            Continue Do
                        End If

                        If s.IndexOf("<directory>", StringComparison.Ordinal) >= 0 Then
                            s = s.Replace("<directory>", "<_directory><directory>")
                        End If
                        If s.IndexOf("</directory>", StringComparison.Ordinal) >= 0 Then
                            s = s.Replace("</directory>", "</directory></_directory>")
                        End If
                        If s.IndexOf("<file>", StringComparison.Ordinal) >= 0 Then
                            s = s.Replace("<file>", "<_file><file>")
                        End If
                        If s.IndexOf("</file>", StringComparison.Ordinal) >= 0 Then
                            s = s.Replace("</file>", "</file></_file>")
                        End If
                        If s.IndexOf("%25", StringComparison.Ordinal) >= 0 Then
                            s = s.Replace("%25", "%")
                        End If

                        soutx.WriteLine(s)
                    Loop
                End Using
            End Using

            Dim reader As New Xml.Serialization.XmlSerializer(GetType(ltfsindex))
            Using t As New IO.StreamReader(tmpf, Text.Encoding.UTF8, detectEncodingFromByteOrderMarks:=True, bufferSize:=BUF)
                result = CType(reader.Deserialize(t), ltfsindex)
            End Using

            If result IsNot Nothing Then
                result.Standarize()
            End If

        Finally
            Try
                If IO.File.Exists(tmpf) Then IO.File.Delete(tmpf)
            Catch
            End Try
        End Try

        Return result
    End Function

    Public Function Clone() As ltfsindex
        Dim tmpf As String = $"{Application.StartupPath}\LWI_{Now.ToString("yyyyMMdd_HHmmss.fffffff")}.tmp"
        SaveFile(tmpf)
        Dim result As ltfsindex = FromSchFile(tmpf)
        IO.File.Delete(tmpf)
        Return result
    End Function
    Public Shared Sub WSort(d As List(Of directory), OnFileFound As Action(Of file), OnDirectoryFound As Action(Of directory), Optional ByRef StopFlag As Boolean = False)
        Dim q As List(Of directory) = d
        While (Not StopFlag) AndAlso q.Count > 0
            Dim q2 As New List(Of directory)
            For Each dq As directory In q
                If OnDirectoryFound IsNot Nothing Then OnDirectoryFound(dq)
                For Each fi As file In dq.EnumerateLazyFiles()
                    If OnFileFound IsNot Nothing Then OnFileFound(fi)
                Next
                For Each childDirectory As directory In dq.EnumerateLazyDirectories()
                    q2.Add(childDirectory)
                Next
            Next
            q = q2
        End While
    End Sub
End Class

Friend Enum LazySchemaChildKind As Byte
    FileRecord = 1
    DirectoryRecord = 2
End Enum

Friend NotInheritable Class LazySchemaChildData
    Public Property Kind As LazySchemaChildKind
    Public Property RecordOffset As Long
    Public Property RecordLength As Long
End Class

Friend NotInheritable Class LazyDirectoryScalarData
    Public Property Name As String
    Public Property [ReadOnly] As Boolean
    Public Property CreationTime As String
    Public Property ChangeTime As String
    Public Property ModifyTime As String
    Public Property AccessTime As String
    Public Property BackupTime As String
    Public Property FileUid As Long
End Class

Friend NotInheritable Class LazyFileScalarData
    Public Property Name As String
    Public Property Length As Long
    Public Property [ReadOnly] As Boolean
    Public Property OpenForWrite As Boolean = True
    Public Property CreationTime As String
    Public Property ChangeTime As String
    Public Property ModifyTime As String
    Public Property AccessTime As String
    Public Property BackupTime As String
    Public Property FileUid As Long
    Public Property Symlink As String
End Class

Friend Structure LazyDirectoryBuildState
    Public Offset As Long
    Public FirstFileIndexOffset As Long
    Public LastFileIndexOffset As Long
    Public FirstDirectoryIndexOffset As Long
    Public LastDirectoryIndexOffset As Long
    Public FileCount As Integer
    Public DirectoryCount As Integer
    Public TotalFileCount As Long
    Public TotalDirectoryCount As Long
End Structure

Friend Structure LazyDirectoryReference
    Public Offset As Long
    Public TotalFileCount As Long
    Public TotalDirectoryCount As Long
End Structure

Friend NotInheritable Class LazyDirectoryMutation
    Public ReadOnly AddedFiles As New List(Of ltfsindex.file)
    Public ReadOnly AddedDirectories As New List(Of ltfsindex.directory)
    Public ReadOnly RemovedFileOffsets As New HashSet(Of Long)
    Public ReadOnly RemovedDirectoryOffsets As New HashSet(Of Long)
End Class

Friend Structure LazyTotalDelta
    Public FileCount As Long
    Public DirectoryCount As Long
End Structure

Friend NotInheritable Class LazySchemaStore
    Private Const DirectoryMagic As Integer = &H4C534452 ' LSDR
    Private Const DirectoryVersion As Integer = 2
    Private Const DirectoryHeaderSize As Integer = 64
    Private Const FileIndexEntrySize As Integer = 24
    Private Const DirectoryIndexEntrySize As Integer = 16
    Private Const IoBufferSize As Integer = 1 << 16

    Private ReadOnly _fileRecordsPath As String
    Private ReadOnly _directoryRecordsPath As String
    Private ReadOnly _fileIndexPath As String
    Private ReadOnly _directoryIndexPath As String
    Private ReadOnly _buildLock As New Object

    Private _fileRecords As IO.FileStream
    Private _directoryRecords As IO.FileStream
    Private _fileIndex As IO.FileStream
    Private _directoryIndex As IO.FileStream
    Private _fileWriter As IO.BinaryWriter
    Private _directoryWriter As IO.BinaryWriter
    Private _fileIndexWriter As IO.BinaryWriter
    Private _directoryIndexWriter As IO.BinaryWriter
    Private _building As Boolean
    Private ReadOnly _mutationLock As New Object
    Private ReadOnly _directoryMutations As New Dictionary(Of Long, LazyDirectoryMutation)
    Private ReadOnly _modifiedFiles As New Dictionary(Of Long, ltfsindex.file)
    Private ReadOnly _modifiedDirectories As New Dictionary(Of Long, ltfsindex.directory)
    Private ReadOnly _fileTotalDeltas As New Dictionary(Of Long, Long)
    Private ReadOnly _directoryTotalDeltas As New Dictionary(Of Long, Long)

    Private Sub New(fileRecordsPath As String,
                    directoryRecordsPath As String,
                    fileIndexPath As String,
                    directoryIndexPath As String)
        _fileRecordsPath = fileRecordsPath
        _directoryRecordsPath = directoryRecordsPath
        _fileIndexPath = fileIndexPath
        _directoryIndexPath = directoryIndexPath

        _fileRecords = New IO.FileStream(_fileRecordsPath, IO.FileMode.Create, IO.FileAccess.ReadWrite, IO.FileShare.Read, IoBufferSize, IO.FileOptions.SequentialScan)
        _directoryRecords = New IO.FileStream(_directoryRecordsPath, IO.FileMode.Create, IO.FileAccess.ReadWrite, IO.FileShare.Read, IoBufferSize, IO.FileOptions.SequentialScan)
        _fileIndex = New IO.FileStream(_fileIndexPath, IO.FileMode.Create, IO.FileAccess.ReadWrite, IO.FileShare.Read, IoBufferSize, IO.FileOptions.SequentialScan)
        _directoryIndex = New IO.FileStream(_directoryIndexPath, IO.FileMode.Create, IO.FileAccess.ReadWrite, IO.FileShare.Read, IoBufferSize, IO.FileOptions.SequentialScan)
        Dim utf8 As New Text.UTF8Encoding(False, True)
        _fileWriter = New IO.BinaryWriter(_fileRecords, utf8, leaveOpen:=True)
        _directoryWriter = New IO.BinaryWriter(_directoryRecords, utf8, leaveOpen:=True)
        _fileIndexWriter = New IO.BinaryWriter(_fileIndex, utf8, leaveOpen:=True)
        _directoryIndexWriter = New IO.BinaryWriter(_directoryIndex, utf8, leaveOpen:=True)
        _building = True
    End Sub

    Friend Shared Function CreateForBuild() As LazySchemaStore
        Dim paths As New List(Of String)
        Try
            For Each suffix As String In New String() {"files", "directories", "file-index", "directory-index"}
                paths.Add(CreateTempFilePath(suffix))
            Next
            Return New LazySchemaStore(paths(0), paths(1), paths(2), paths(3))
        Catch
            For Each path As String In paths
                Try
                    If IO.File.Exists(path) Then IO.File.Delete(path)
                Catch
                End Try
            Next
            Throw
        End Try
    End Function

    Private Shared Function CreateTempFilePath(suffix As String) As String
        Dim directories As New List(Of String)
        Try
            directories.Add(System.Windows.Forms.Application.StartupPath)
        Catch
        End Try
        directories.Add(IO.Path.GetTempPath())

        For Each directory As String In directories.Distinct(StringComparer.OrdinalIgnoreCase)
            If String.IsNullOrWhiteSpace(directory) Then Continue For
            Try
                If Not IO.Directory.Exists(directory) Then IO.Directory.CreateDirectory(directory)
                For i As Integer = 0 To 7
                    Dim path As String = IO.Path.Combine(directory, $"LCG_SCHEMA_{Guid.NewGuid():N}_{suffix}.tmp")
                    Using stream As New IO.FileStream(path, IO.FileMode.CreateNew, IO.FileAccess.Write, IO.FileShare.None, 1, IO.FileOptions.SequentialScan)
                    End Using
                    Return path
                Next
            Catch
            End Try
        Next

        Throw New IO.IOException("Unable to create a temporary schema store.")
    End Function

    Friend Function BeginFileRecord() As Long
        EnsureBuilding()
        Return _fileRecords.Position
    End Function

    Friend Function CreateFileXmlWriter() As XmlWriter
        EnsureBuilding()
        Return XmlWriter.Create(_fileRecords, New XmlWriterSettings With {
            .Encoding = New Text.UTF8Encoding(False),
            .OmitXmlDeclaration = True,
            .Indent = False,
            .CloseOutput = False,
            .CheckCharacters = True})
    End Function

    Friend Function EndFileRecord(startOffset As Long) As Long
        EnsureBuilding()
        _fileRecords.Flush()
        Return _fileRecords.Position - startOffset
    End Function

    Friend Function BeginDirectoryRecord() As LazyDirectoryBuildState
        EnsureBuilding()
        Dim state As New LazyDirectoryBuildState With {
            .Offset = _directoryRecords.Position,
            .FirstFileIndexOffset = -1,
            .LastFileIndexOffset = -1,
            .FirstDirectoryIndexOffset = -1,
            .LastDirectoryIndexOffset = -1,
            .FileCount = 0,
            .DirectoryCount = 0,
            .TotalFileCount = 0,
            .TotalDirectoryCount = 0}
        _directoryWriter.Write(DirectoryMagic)
        _directoryWriter.Write(DirectoryVersion)
        _directoryWriter.Write(CLng(-1))
        _directoryWriter.Write(0)
        _directoryWriter.Write(0)
        _directoryWriter.Write(CLng(-1))
        _directoryWriter.Write(0)
        _directoryWriter.Write(CLng(-1))
        _directoryWriter.Write(0)
        _directoryWriter.Write(CLng(0))
        _directoryWriter.Write(CLng(0))
        Return state
    End Function

    Friend Sub AddChild(ByRef state As LazyDirectoryBuildState,
                         kind As LazySchemaChildKind,
                         recordOffset As Long,
                         recordLength As Long,
                         Optional childTotalFileCount As Long = 0,
                         Optional childTotalDirectoryCount As Long = 0)
        EnsureBuilding()
        If kind = LazySchemaChildKind.FileRecord Then
            Dim fileIndexOffset As Long = _fileIndex.Position
            _fileIndexWriter.Write(CLng(-1))
            _fileIndexWriter.Write(recordOffset)
            _fileIndexWriter.Write(recordLength)
            If state.FileCount = 0 Then
                state.FirstFileIndexOffset = fileIndexOffset
            Else
                Dim restorePosition As Long = _fileIndex.Position
                _fileIndex.Seek(state.LastFileIndexOffset, IO.SeekOrigin.Begin)
                _fileIndexWriter.Write(fileIndexOffset)
                _fileIndex.Seek(restorePosition, IO.SeekOrigin.Begin)
            End If
            state.LastFileIndexOffset = fileIndexOffset
            state.FileCount += 1
            state.TotalFileCount += 1
        ElseIf kind = LazySchemaChildKind.DirectoryRecord Then
            Dim directoryIndexOffset As Long = _directoryIndex.Position
            _directoryIndexWriter.Write(CLng(-1))
            _directoryIndexWriter.Write(recordOffset)
            If state.DirectoryCount = 0 Then
                state.FirstDirectoryIndexOffset = directoryIndexOffset
            Else
                Dim restorePosition As Long = _directoryIndex.Position
                _directoryIndex.Seek(state.LastDirectoryIndexOffset, IO.SeekOrigin.Begin)
                _directoryIndexWriter.Write(directoryIndexOffset)
                _directoryIndex.Seek(restorePosition, IO.SeekOrigin.Begin)
            End If
            state.LastDirectoryIndexOffset = directoryIndexOffset
            state.DirectoryCount += 1
            state.TotalFileCount += childTotalFileCount
            state.TotalDirectoryCount += 1 + childTotalDirectoryCount
        Else
            Throw New IO.InvalidDataException("Invalid lazy schema child type.")
        End If
    End Sub

    Friend Function FinishDirectoryRecord(ByRef state As LazyDirectoryBuildState,
                                           values As LazyDirectoryScalarData) As Long
        EnsureBuilding()
        Dim scalarOffset As Long = _directoryRecords.Position
        WriteDirectoryScalars(_directoryWriter, values)
        Dim scalarLength As Integer = CInt(_directoryRecords.Position - scalarOffset)
        Dim restorePosition As Long = _directoryRecords.Position

        _directoryRecords.Seek(state.Offset + 8, IO.SeekOrigin.Begin)
        _directoryWriter.Write(scalarOffset)
        _directoryWriter.Write(scalarLength)
        _directoryWriter.Write(0)
        _directoryWriter.Write(If(state.FileCount = 0, -1L, state.FirstFileIndexOffset))
        _directoryWriter.Write(state.FileCount)
        _directoryWriter.Write(If(state.DirectoryCount = 0, -1L, state.FirstDirectoryIndexOffset))
        _directoryWriter.Write(state.DirectoryCount)
        _directoryWriter.Write(state.TotalFileCount)
        _directoryWriter.Write(state.TotalDirectoryCount)
        _directoryRecords.Seek(restorePosition, IO.SeekOrigin.Begin)
        _directoryRecords.Flush()
        Return state.Offset
    End Function

    Friend Sub FinishBuild()
        SyncLock _buildLock
            If Not _building Then Exit Sub
            _building = False
            CloseBuildStreams()
        End SyncLock
    End Sub

    Friend Sub AbortBuild()
        SyncLock _buildLock
            _building = False
            CloseBuildStreams()
            DeleteBackingFiles()
        End SyncLock
    End Sub

    Private Sub EnsureBuilding()
        If Not _building Then Throw New ObjectDisposedException(NameOf(LazySchemaStore))
    End Sub

    Private Function GetDirectoryMutation(recordOffset As Long, create As Boolean) As LazyDirectoryMutation
        SyncLock _mutationLock
            Dim result As LazyDirectoryMutation = Nothing
            If Not _directoryMutations.TryGetValue(recordOffset, result) AndAlso create Then
                result = New LazyDirectoryMutation
                _directoryMutations(recordOffset) = result
            End If
            Return result
        End SyncLock
    End Function

    Friend Sub ApplyDirectoryTotalDelta(recordOffset As Long, fileDelta As Long, directoryDelta As Long)
        If recordOffset < 0 OrElse (fileDelta = 0 AndAlso directoryDelta = 0) Then Exit Sub
        SyncLock _mutationLock
            If fileDelta <> 0 Then
                Dim currentFileDelta As Long = 0
                _fileTotalDeltas.TryGetValue(recordOffset, currentFileDelta)
                _fileTotalDeltas(recordOffset) = currentFileDelta + fileDelta
            End If
            If directoryDelta <> 0 Then
                Dim currentDirectoryDelta As Long = 0
                _directoryTotalDeltas.TryGetValue(recordOffset, currentDirectoryDelta)
                _directoryTotalDeltas(recordOffset) = currentDirectoryDelta + directoryDelta
            End If
        End SyncLock
    End Sub

    Friend Sub RegisterModifiedFile(recordOffset As Long, value As ltfsindex.file)
        If value Is Nothing OrElse recordOffset < 0 Then Exit Sub
        SyncLock _mutationLock
            _modifiedFiles(recordOffset) = value
        End SyncLock
    End Sub

    Friend Function GetModifiedFile(recordOffset As Long) As ltfsindex.file
        SyncLock _mutationLock
            Dim result As ltfsindex.file = Nothing
            If _modifiedFiles.TryGetValue(recordOffset, result) Then Return result
            Return Nothing
        End SyncLock
    End Function

    Friend Sub RegisterModifiedDirectory(recordOffset As Long, value As ltfsindex.directory)
        If value Is Nothing OrElse recordOffset < 0 Then Exit Sub
        SyncLock _mutationLock
            _modifiedDirectories(recordOffset) = value
        End SyncLock
    End Sub

    Friend Function GetModifiedDirectory(recordOffset As Long) As ltfsindex.directory
        SyncLock _mutationLock
            Dim result As ltfsindex.directory = Nothing
            If _modifiedDirectories.TryGetValue(recordOffset, result) Then Return result
            Return Nothing
        End SyncLock
    End Function

    Friend Function AddDirectoryFile(parentOffset As Long, value As ltfsindex.file) As Long
        If value Is Nothing Then Return 0
        SyncLock _mutationLock
            Dim mutation As LazyDirectoryMutation = GetDirectoryMutation(parentOffset, True)
            If mutation.AddedFiles.Contains(value) Then Return 0

            If value.HasLazyRecord AndAlso ReferenceEquals(value.LazyStoreReference, Me) Then
                If mutation.RemovedFileOffsets.Remove(value.LazyRecordOffset) Then
                    Return 1
                End If
            End If

            mutation.AddedFiles.Add(value)
            Return 1
        End SyncLock
    End Function

    Friend Function RemoveDirectoryFile(parentOffset As Long, value As ltfsindex.file) As Long
        If value Is Nothing Then Return 0
        SyncLock _mutationLock
            Dim mutation As LazyDirectoryMutation = GetDirectoryMutation(parentOffset, True)
            For i As Integer = mutation.AddedFiles.Count - 1 To 0 Step -1
                If ReferenceEquals(mutation.AddedFiles(i), value) Then
                    mutation.AddedFiles.RemoveAt(i)
                    Return -1
                End If
            Next

            If value.HasLazyRecord AndAlso ReferenceEquals(value.LazyStoreReference, Me) Then
                Dim removed As Boolean = mutation.RemovedFileOffsets.Add(value.LazyRecordOffset)
                Return If(removed, -1L, 0L)
            End If
            Return 0
        End SyncLock
    End Function

    Friend Function AddDirectoryDirectory(parentOffset As Long, value As ltfsindex.directory) As LazyTotalDelta
        If value Is Nothing Then Return New LazyTotalDelta
        SyncLock _mutationLock
            Dim mutation As LazyDirectoryMutation = GetDirectoryMutation(parentOffset, True)
            If mutation.AddedDirectories.Contains(value) Then Return New LazyTotalDelta

            If value.HasLazyRecord AndAlso ReferenceEquals(value.LazyStoreReference, Me) Then
                If mutation.RemovedDirectoryOffsets.Remove(value.LazyRecordOffset) Then
                    Return New LazyTotalDelta With {
                        .FileCount = GetDirectoryTotalFileCount(value.LazyRecordOffset),
                        .DirectoryCount = 1L + GetDirectoryTotalDirectoryCount(value.LazyRecordOffset)}
                End If
            End If

            mutation.AddedDirectories.Add(value)
            Return New LazyTotalDelta With {
                .FileCount = value.TotalFiles,
                .DirectoryCount = 1L + value.TotalDirectories}
        End SyncLock
    End Function

    Friend Function RemoveDirectoryDirectory(parentOffset As Long, value As ltfsindex.directory) As LazyTotalDelta
        If value Is Nothing Then Return New LazyTotalDelta
        SyncLock _mutationLock
            Dim mutation As LazyDirectoryMutation = GetDirectoryMutation(parentOffset, True)
            For i As Integer = mutation.AddedDirectories.Count - 1 To 0 Step -1
                If ReferenceEquals(mutation.AddedDirectories(i), value) Then
                    mutation.AddedDirectories.RemoveAt(i)
                    Return New LazyTotalDelta With {
                        .FileCount = -value.TotalFiles,
                        .DirectoryCount = -(1L + value.TotalDirectories)}
                End If
            Next

            If value.HasLazyRecord AndAlso ReferenceEquals(value.LazyStoreReference, Me) Then
                Dim removed As Boolean = mutation.RemovedDirectoryOffsets.Add(value.LazyRecordOffset)
                If removed Then
                    Return New LazyTotalDelta With {
                        .FileCount = -GetDirectoryTotalFileCount(value.LazyRecordOffset),
                        .DirectoryCount = -(1L + GetDirectoryTotalDirectoryCount(value.LazyRecordOffset))}
                End If
            End If
            Return New LazyTotalDelta
        End SyncLock
    End Function

    Friend Function IsFileRemoved(parentOffset As Long, recordOffset As Long) As Boolean
        SyncLock _mutationLock
            Dim mutation As LazyDirectoryMutation = Nothing
            Return _directoryMutations.TryGetValue(parentOffset, mutation) AndAlso mutation.RemovedFileOffsets.Contains(recordOffset)
        End SyncLock
    End Function

    Friend Function IsDirectoryRemoved(parentOffset As Long, recordOffset As Long) As Boolean
        SyncLock _mutationLock
            Dim mutation As LazyDirectoryMutation = Nothing
            Return _directoryMutations.TryGetValue(parentOffset, mutation) AndAlso mutation.RemovedDirectoryOffsets.Contains(recordOffset)
        End SyncLock
    End Function

    Friend Function GetRemovedFileCount(parentOffset As Long) As Integer
        SyncLock _mutationLock
            Dim mutation As LazyDirectoryMutation = Nothing
            If _directoryMutations.TryGetValue(parentOffset, mutation) Then Return mutation.RemovedFileOffsets.Count
            Return 0
        End SyncLock
    End Function

    Friend Function GetRemovedDirectoryCount(parentOffset As Long) As Integer
        SyncLock _mutationLock
            Dim mutation As LazyDirectoryMutation = Nothing
            If _directoryMutations.TryGetValue(parentOffset, mutation) Then Return mutation.RemovedDirectoryOffsets.Count
            Return 0
        End SyncLock
    End Function

    Friend Function EnumerateAddedFiles(parentOffset As Long) As IEnumerable(Of ltfsindex.file)
        Dim snapshot As List(Of ltfsindex.file) = Nothing
        SyncLock _mutationLock
            Dim mutation As LazyDirectoryMutation = Nothing
            If _directoryMutations.TryGetValue(parentOffset, mutation) Then snapshot = mutation.AddedFiles.ToList()
        End SyncLock
        If snapshot Is Nothing Then Return Enumerable.Empty(Of ltfsindex.file)()
        Return snapshot
    End Function

    Friend Function EnumerateAddedDirectories(parentOffset As Long) As IEnumerable(Of ltfsindex.directory)
        Dim snapshot As List(Of ltfsindex.directory) = Nothing
        SyncLock _mutationLock
            Dim mutation As LazyDirectoryMutation = Nothing
            If _directoryMutations.TryGetValue(parentOffset, mutation) Then snapshot = mutation.AddedDirectories.ToList()
        End SyncLock
        If snapshot Is Nothing Then Return Enumerable.Empty(Of ltfsindex.directory)()
        Return snapshot
    End Function

    Private Sub CloseBuildStreams()
        If _fileWriter IsNot Nothing Then
            Try : _fileWriter.Flush() : Catch : End Try
            Try : _fileWriter.Dispose() : Catch : End Try
            _fileWriter = Nothing
        End If
        If _directoryWriter IsNot Nothing Then
            Try : _directoryWriter.Flush() : Catch : End Try
            Try : _directoryWriter.Dispose() : Catch : End Try
            _directoryWriter = Nothing
        End If
        If _fileIndexWriter IsNot Nothing Then
            Try : _fileIndexWriter.Flush() : Catch : End Try
            Try : _fileIndexWriter.Dispose() : Catch : End Try
            _fileIndexWriter = Nothing
        End If
        If _directoryIndexWriter IsNot Nothing Then
            Try : _directoryIndexWriter.Flush() : Catch : End Try
            Try : _directoryIndexWriter.Dispose() : Catch : End Try
            _directoryIndexWriter = Nothing
        End If
        If _fileRecords IsNot Nothing Then
            Try : _fileRecords.Dispose() : Catch : End Try
            _fileRecords = Nothing
        End If
        If _directoryRecords IsNot Nothing Then
            Try : _directoryRecords.Dispose() : Catch : End Try
            _directoryRecords = Nothing
        End If
        If _fileIndex IsNot Nothing Then
            Try : _fileIndex.Dispose() : Catch : End Try
            _fileIndex = Nothing
        End If
        If _directoryIndex IsNot Nothing Then
            Try : _directoryIndex.Dispose() : Catch : End Try
            _directoryIndex = Nothing
        End If
    End Sub

    Private Sub DeleteBackingFiles()
        For Each path As String In New String() {_fileRecordsPath, _directoryRecordsPath, _fileIndexPath, _directoryIndexPath}
            Try
                If IO.File.Exists(path) Then IO.File.Delete(path)
            Catch
            End Try
        Next
    End Sub

    Friend Function ReadDirectoryScalars(recordOffset As Long) As LazyDirectoryScalarData
        Dim header As LazyDirectoryHeader = ReadDirectoryHeader(recordOffset)
        Dim result As New LazyDirectoryScalarData
        Using stream As New IO.FileStream(_directoryRecordsPath, IO.FileMode.Open, IO.FileAccess.Read, IO.FileShare.Read, IoBufferSize, IO.FileOptions.RandomAccess)
            stream.Seek(header.ScalarOffset, IO.SeekOrigin.Begin)
            Using reader As New IO.BinaryReader(stream, New Text.UTF8Encoding(False, True), leaveOpen:=False)
                result.Name = ReadNullableString(reader)
                result.ReadOnly = reader.ReadBoolean()
                result.CreationTime = ReadNullableString(reader)
                result.ChangeTime = ReadNullableString(reader)
                result.ModifyTime = ReadNullableString(reader)
                result.AccessTime = ReadNullableString(reader)
                result.BackupTime = ReadNullableString(reader)
                result.FileUid = reader.ReadInt64()
            End Using
        End Using
        Return result
    End Function

    Friend Function ReadDirectoryChildren(recordOffset As Long) As List(Of LazySchemaChildData)
        Dim header As LazyDirectoryHeader = ReadDirectoryHeader(recordOffset)
        Dim result As New List(Of LazySchemaChildData)(Math.Max(0, header.FileCount + header.DirectoryCount))
        For Each directoryChild As LazySchemaChildData In EnumerateDirectoryReferences(recordOffset)
            result.Add(directoryChild)
        Next
        For Each fileChild As LazySchemaChildData In EnumerateFileReferences(recordOffset)
            result.Add(fileChild)
        Next
        Return result
    End Function

    Friend Function HasDirectoryChildren(recordOffset As Long) As Boolean
        Return GetDirectoryFileCount(recordOffset) > 0 OrElse GetDirectoryDirectoryCount(recordOffset) > 0
    End Function

    Friend Function GetDirectoryFileCount(recordOffset As Long) As Integer
        Dim header As LazyDirectoryHeader = ReadDirectoryHeader(recordOffset)
        Dim removedCount As Integer = 0
        Dim addedCount As Integer = 0
        SyncLock _mutationLock
            Dim mutation As LazyDirectoryMutation = Nothing
            If _directoryMutations.TryGetValue(recordOffset, mutation) Then
                removedCount = mutation.RemovedFileOffsets.Count
                addedCount = mutation.AddedFiles.Count
            End If
        End SyncLock
        Return Math.Max(0, header.FileCount - removedCount + addedCount)
    End Function

    Friend Function GetDirectoryDirectoryCount(recordOffset As Long) As Integer
        Dim header As LazyDirectoryHeader = ReadDirectoryHeader(recordOffset)
        Dim removedCount As Integer = 0
        Dim addedCount As Integer = 0
        SyncLock _mutationLock
            Dim mutation As LazyDirectoryMutation = Nothing
            If _directoryMutations.TryGetValue(recordOffset, mutation) Then
                removedCount = mutation.RemovedDirectoryOffsets.Count
                addedCount = mutation.AddedDirectories.Count
            End If
        End SyncLock
        Return Math.Max(0, header.DirectoryCount - removedCount + addedCount)
    End Function

    Friend Function ReadDirectoryFileCount(recordOffset As Long) As Integer
        Return ReadDirectoryHeader(recordOffset).FileCount
    End Function

    Friend Function ReadDirectoryDirectoryCount(recordOffset As Long) As Integer
        Return ReadDirectoryHeader(recordOffset).DirectoryCount
    End Function

    Friend Function ReadDirectoryTotalFileCount(recordOffset As Long) As Long
        Return ReadDirectoryHeader(recordOffset).TotalFileCount
    End Function

    Friend Function ReadDirectoryTotalDirectoryCount(recordOffset As Long) As Long
        Return ReadDirectoryHeader(recordOffset).TotalDirectoryCount
    End Function

    Friend Function GetDirectoryTotalFileCount(recordOffset As Long) As Long
        SyncLock _mutationLock
            Dim delta As Long = 0
            _fileTotalDeltas.TryGetValue(recordOffset, delta)
            Return Math.Max(0L, ReadDirectoryTotalFileCount(recordOffset) + delta)
        End SyncLock
    End Function

    Friend Function GetDirectoryTotalDirectoryCount(recordOffset As Long) As Long
        SyncLock _mutationLock
            Dim delta As Long = 0
            _directoryTotalDeltas.TryGetValue(recordOffset, delta)
            Return Math.Max(0L, ReadDirectoryTotalDirectoryCount(recordOffset) + delta)
        End SyncLock
    End Function

    Friend Function ReadFileAt(recordOffset As Long,
                               index As Integer,
                               ByRef cursorIndex As Integer,
                               ByRef cursorOffset As Long) As LazySchemaChildData
        Dim header As LazyDirectoryHeader = ReadDirectoryHeader(recordOffset)
        If index < 0 OrElse index >= header.FileCount Then Throw New ArgumentOutOfRangeException(NameOf(index))
        If header.FileIndexOffset < 0 Then Throw New IO.InvalidDataException("Invalid lazy schema file index.")

        Using stream As New IO.FileStream(_fileIndexPath, IO.FileMode.Open, IO.FileAccess.Read, IO.FileShare.Read, IoBufferSize, IO.FileOptions.RandomAccess)
            Dim entryOffset As Long = header.FileIndexOffset
            Using reader As New IO.BinaryReader(stream, New Text.UTF8Encoding(False, True), leaveOpen:=False)
                Dim currentIndex As Integer = 0
                If cursorIndex >= 0 AndAlso index >= cursorIndex Then
                    currentIndex = cursorIndex
                    entryOffset = cursorOffset
                End If
                For i As Integer = currentIndex To index - 1
                    If entryOffset < 0 OrElse entryOffset > stream.Length - FileIndexEntrySize Then Throw New IO.InvalidDataException("Invalid lazy schema file index chain.")
                    stream.Seek(entryOffset, IO.SeekOrigin.Begin)
                    entryOffset = reader.ReadInt64()
                Next
                If entryOffset < 0 OrElse entryOffset > stream.Length - FileIndexEntrySize Then Throw New IO.InvalidDataException("Lazy schema file index is outside the backing file.")
                stream.Seek(entryOffset, IO.SeekOrigin.Begin)
                reader.ReadInt64()
                Dim result As New LazySchemaChildData With {
                    .Kind = LazySchemaChildKind.FileRecord,
                    .RecordOffset = reader.ReadInt64(),
                    .RecordLength = reader.ReadInt64()}
                cursorIndex = index
                cursorOffset = entryOffset
                Return result
            End Using
        End Using
    End Function

    Friend Function ReadDirectoryAt(recordOffset As Long,
                                    index As Integer,
                                    ByRef cursorIndex As Integer,
                                    ByRef cursorOffset As Long) As LazySchemaChildData
        Dim header As LazyDirectoryHeader = ReadDirectoryHeader(recordOffset)
        If index < 0 OrElse index >= header.DirectoryCount Then Throw New ArgumentOutOfRangeException(NameOf(index))
        If header.DirectoryIndexOffset < 0 Then Throw New IO.InvalidDataException("Invalid lazy schema directory index.")

        Using stream As New IO.FileStream(_directoryIndexPath, IO.FileMode.Open, IO.FileAccess.Read, IO.FileShare.Read, IoBufferSize, IO.FileOptions.RandomAccess)
            Dim entryOffset As Long = header.DirectoryIndexOffset
            Using reader As New IO.BinaryReader(stream, New Text.UTF8Encoding(False, True), leaveOpen:=False)
                Dim currentIndex As Integer = 0
                If cursorIndex >= 0 AndAlso index >= cursorIndex Then
                    currentIndex = cursorIndex
                    entryOffset = cursorOffset
                End If
                For i As Integer = currentIndex To index - 1
                    If entryOffset < 0 OrElse entryOffset > stream.Length - DirectoryIndexEntrySize Then Throw New IO.InvalidDataException("Invalid lazy schema directory index chain.")
                    stream.Seek(entryOffset, IO.SeekOrigin.Begin)
                    entryOffset = reader.ReadInt64()
                Next
                If entryOffset < 0 OrElse entryOffset > stream.Length - DirectoryIndexEntrySize Then Throw New IO.InvalidDataException("Lazy schema directory index is outside the backing file.")
                stream.Seek(entryOffset, IO.SeekOrigin.Begin)
                reader.ReadInt64()
                Dim result As New LazySchemaChildData With {
                    .Kind = LazySchemaChildKind.DirectoryRecord,
                    .RecordOffset = reader.ReadInt64(),
                    .RecordLength = 0}
                cursorIndex = index
                cursorOffset = entryOffset
                Return result
            End Using
        End Using
    End Function

    Friend Iterator Function EnumerateFileReferences(recordOffset As Long) As IEnumerable(Of LazySchemaChildData)
        Dim header As LazyDirectoryHeader = ReadDirectoryHeader(recordOffset)
        If header.FileCount = 0 Then Exit Function
        If header.FileIndexOffset < 0 Then Throw New IO.InvalidDataException("Invalid lazy schema file index.")

        Using stream As New IO.FileStream(_fileIndexPath, IO.FileMode.Open, IO.FileAccess.Read, IO.FileShare.Read, IoBufferSize, IO.FileOptions.SequentialScan)
            Using reader As New IO.BinaryReader(stream, New Text.UTF8Encoding(False, True), leaveOpen:=False)
                Dim entryOffset As Long = header.FileIndexOffset
                For i As Integer = 0 To header.FileCount - 1
                    If entryOffset < 0 OrElse entryOffset > stream.Length - FileIndexEntrySize Then Throw New IO.InvalidDataException("Invalid lazy schema file index chain.")
                    stream.Seek(entryOffset, IO.SeekOrigin.Begin)
                    Dim nextOffset As Long = reader.ReadInt64()
                    Dim result As New LazySchemaChildData With {
                        .Kind = LazySchemaChildKind.FileRecord,
                        .RecordOffset = reader.ReadInt64(),
                        .RecordLength = reader.ReadInt64()}
                    Yield result
                    entryOffset = nextOffset
                Next
            End Using
        End Using
    End Function

    Friend Iterator Function EnumerateDirectoryReferences(recordOffset As Long) As IEnumerable(Of LazySchemaChildData)
        Dim header As LazyDirectoryHeader = ReadDirectoryHeader(recordOffset)
        If header.DirectoryCount = 0 Then Exit Function
        If header.DirectoryIndexOffset < 0 Then Throw New IO.InvalidDataException("Invalid lazy schema directory index.")

        Using stream As New IO.FileStream(_directoryIndexPath, IO.FileMode.Open, IO.FileAccess.Read, IO.FileShare.Read, IoBufferSize, IO.FileOptions.SequentialScan)
            Using reader As New IO.BinaryReader(stream, New Text.UTF8Encoding(False, True), leaveOpen:=False)
                Dim entryOffset As Long = header.DirectoryIndexOffset
                For i As Integer = 0 To header.DirectoryCount - 1
                    If entryOffset < 0 OrElse entryOffset > stream.Length - DirectoryIndexEntrySize Then Throw New IO.InvalidDataException("Invalid lazy schema directory index chain.")
                    stream.Seek(entryOffset, IO.SeekOrigin.Begin)
                    Dim nextOffset As Long = reader.ReadInt64()
                    Dim result As New LazySchemaChildData With {
                        .Kind = LazySchemaChildKind.DirectoryRecord,
                        .RecordOffset = reader.ReadInt64(),
                        .RecordLength = 0}
                    Yield result
                    entryOffset = nextOffset
                Next
            End Using
        End Using
    End Function

    Friend Function ReadFileScalars(recordOffset As Long, recordLength As Long) As LazyFileScalarData
        Dim result As New LazyFileScalarData
        Using reader As XmlReader = OpenFileRecordReader(recordOffset, recordLength)
            reader.MoveToContent()
            If reader.NodeType <> XmlNodeType.Element Then Return result
            Dim rootDepth As Integer = reader.Depth
            If reader.IsEmptyElement Then Return result

            While reader.Read()
                If reader.NodeType = XmlNodeType.EndElement AndAlso reader.Depth = rootDepth Then Exit While
                If reader.NodeType <> XmlNodeType.Element OrElse reader.Depth <> rootDepth + 1 Then Continue While

                Dim value As String
                Dim parsedLong As Long
                Dim parsedBoolean As Boolean
                Select Case reader.LocalName
                    Case "name"
                        result.Name = DecodeSchemaValue(ReadElementText(reader))
                    Case "length"
                        value = ReadElementText(reader)
                        If Long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, parsedLong) Then result.Length = parsedLong
                    Case "readonly"
                        value = ReadElementText(reader)
                        If Boolean.TryParse(value, parsedBoolean) Then result.ReadOnly = parsedBoolean
                    Case "openforwrite"
                        value = ReadElementText(reader)
                        If Boolean.TryParse(value, parsedBoolean) Then result.OpenForWrite = parsedBoolean
                    Case "creationtime"
                        result.CreationTime = DecodeSchemaValue(ReadElementText(reader))
                    Case "changetime"
                        result.ChangeTime = DecodeSchemaValue(ReadElementText(reader))
                    Case "modifytime"
                        result.ModifyTime = DecodeSchemaValue(ReadElementText(reader))
                    Case "accesstime"
                        result.AccessTime = DecodeSchemaValue(ReadElementText(reader))
                    Case "backuptime"
                        result.BackupTime = DecodeSchemaValue(ReadElementText(reader))
                    Case "fileuid"
                        value = ReadElementText(reader)
                        If Long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, parsedLong) Then result.FileUid = parsedLong
                    Case "symlink"
                        result.Symlink = DecodeSchemaValue(ReadElementText(reader))
                    Case Else
                        SkipElement(reader)
                End Select
            End While
        End Using
        Return result
    End Function

    Friend Function ReadFileExtendedAttributes(recordOffset As Long, recordLength As Long) As List(Of ltfsindex.file.xattr)
        Dim result As New List(Of ltfsindex.file.xattr)
        Using reader As XmlReader = OpenFileRecordReader(recordOffset, recordLength)
            reader.MoveToContent()
            If reader.NodeType <> XmlNodeType.Element Then Return result
            Dim rootDepth As Integer = reader.Depth
            If reader.IsEmptyElement Then Return result

            While reader.Read()
                If reader.NodeType = XmlNodeType.EndElement AndAlso reader.Depth = rootDepth Then Exit While
                If reader.NodeType = XmlNodeType.Element AndAlso reader.Depth = rootDepth + 1 AndAlso reader.LocalName = "extendedattributes" Then
                    ReadXattrContainer(reader, result)
                End If
            End While
        End Using
        Return result
    End Function

    Friend Function ReadFileExtentInfo(recordOffset As Long, recordLength As Long) As List(Of ltfsindex.file.extent)
        Dim result As New List(Of ltfsindex.file.extent)
        Using reader As XmlReader = OpenFileRecordReader(recordOffset, recordLength)
            reader.MoveToContent()
            If reader.NodeType <> XmlNodeType.Element Then Return result
            Dim rootDepth As Integer = reader.Depth
            If reader.IsEmptyElement Then Return result

            While reader.Read()
                If reader.NodeType = XmlNodeType.EndElement AndAlso reader.Depth = rootDepth Then Exit While
                If reader.NodeType = XmlNodeType.Element AndAlso reader.Depth = rootDepth + 1 AndAlso reader.LocalName = "extentinfo" Then
                    ReadExtentContainer(reader, result)
                End If
            End While
        End Using
        Return result
    End Function

    Private Function OpenFileRecordReader(recordOffset As Long, recordLength As Long) As XmlReader
        If recordOffset < 0 OrElse recordLength <= 0 Then Throw New IO.InvalidDataException("Invalid lazy schema file record.")
        Dim stream As New IO.FileStream(_fileRecordsPath, IO.FileMode.Open, IO.FileAccess.Read, IO.FileShare.Read, IoBufferSize, IO.FileOptions.RandomAccess)
        If recordOffset > stream.Length OrElse recordLength > stream.Length - recordOffset Then
            stream.Dispose()
            Throw New IO.InvalidDataException("Lazy schema file record is outside the backing file.")
        End If
        stream.Seek(recordOffset, IO.SeekOrigin.Begin)
        Dim limited As New LazySchemaLimitedStream(stream, recordLength)
        Dim settings As New XmlReaderSettings With {
            .IgnoreComments = True,
            .IgnoreWhitespace = True,
            .IgnoreProcessingInstructions = True,
            .DtdProcessing = DtdProcessing.Prohibit,
            .XmlResolver = Nothing,
            .CloseInput = True}
        Return XmlReader.Create(limited, settings)
    End Function

    Private Shared Sub ReadXattrContainer(reader As XmlReader, result As List(Of ltfsindex.file.xattr))
        Dim containerDepth As Integer = reader.Depth
        If reader.IsEmptyElement Then Exit Sub
        While reader.Read()
            If reader.NodeType = XmlNodeType.EndElement AndAlso reader.Depth = containerDepth Then Exit While
            If reader.NodeType = XmlNodeType.Element AndAlso reader.Depth = containerDepth + 1 AndAlso reader.LocalName = "xattr" Then
                result.Add(ReadXattr(reader))
            End If
        End While
    End Sub

    Private Shared Function ReadXattr(reader As XmlReader) As ltfsindex.file.xattr
        Dim result As New ltfsindex.file.xattr
        Dim itemDepth As Integer = reader.Depth
        If reader.IsEmptyElement Then Return result
        While reader.Read()
            If reader.NodeType = XmlNodeType.EndElement AndAlso reader.Depth = itemDepth Then Exit While
            If reader.NodeType <> XmlNodeType.Element OrElse reader.Depth <> itemDepth + 1 Then Continue While
            Select Case reader.LocalName
                Case "key"
                    result.key = DecodeSchemaValue(ReadElementText(reader))
                Case "value"
                    result.value = DecodeSchemaValue(ReadElementText(reader))
                Case Else
                    SkipElement(reader)
            End Select
        End While
        Return result
    End Function

    Private Shared Sub ReadExtentContainer(reader As XmlReader, result As List(Of ltfsindex.file.extent))
        Dim containerDepth As Integer = reader.Depth
        If reader.IsEmptyElement Then Exit Sub
        While reader.Read()
            If reader.NodeType = XmlNodeType.EndElement AndAlso reader.Depth = containerDepth Then Exit While
            If reader.NodeType = XmlNodeType.Element AndAlso reader.Depth = containerDepth + 1 AndAlso reader.LocalName = "extent" Then
                result.Add(ReadExtent(reader))
            End If
        End While
    End Sub

    Private Shared Function ReadExtent(reader As XmlReader) As ltfsindex.file.extent
        Dim result As New ltfsindex.file.extent
        Dim itemDepth As Integer = reader.Depth
        Dim parsedLong As Long
        Dim parsedPartition As ltfsindex.PartitionLabel
        If reader.IsEmptyElement Then Return result
        While reader.Read()
            If reader.NodeType = XmlNodeType.EndElement AndAlso reader.Depth = itemDepth Then Exit While
            If reader.NodeType <> XmlNodeType.Element OrElse reader.Depth <> itemDepth + 1 Then Continue While
            Dim fieldName As String = reader.LocalName
            Dim value As String = ReadElementText(reader)
            Select Case fieldName
                Case "fileoffset"
                    If Long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, parsedLong) Then result.fileoffset = parsedLong
                Case "partition"
                    If [Enum].TryParse(value, True, parsedPartition) Then result.partition = parsedPartition
                Case "startblock"
                    If Long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, parsedLong) Then result.startblock = parsedLong
                Case "byteoffset"
                    If Long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, parsedLong) Then result.byteoffset = parsedLong
                Case "bytecount"
                    If Long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, parsedLong) Then result.bytecount = parsedLong
            End Select
        End While
        Return result
    End Function

    Private Structure LazyDirectoryHeader
        Public ScalarOffset As Long
        Public ScalarLength As Integer
        Public FileIndexOffset As Long
        Public FileCount As Integer
        Public DirectoryIndexOffset As Long
        Public DirectoryCount As Integer
        Public TotalFileCount As Long
        Public TotalDirectoryCount As Long
    End Structure

    Private Function ReadDirectoryHeader(recordOffset As Long) As LazyDirectoryHeader
        If recordOffset < 0 Then Throw New IO.InvalidDataException("Invalid lazy schema directory record.")
        Using stream As New IO.FileStream(_directoryRecordsPath, IO.FileMode.Open, IO.FileAccess.Read, IO.FileShare.Read, IoBufferSize, IO.FileOptions.RandomAccess)
            If recordOffset > stream.Length - DirectoryHeaderSize Then Throw New IO.InvalidDataException("Lazy schema directory record is outside the backing file.")
            stream.Seek(recordOffset, IO.SeekOrigin.Begin)
            Using reader As New IO.BinaryReader(stream, New Text.UTF8Encoding(False, True), leaveOpen:=False)
                If reader.ReadInt32() <> DirectoryMagic OrElse reader.ReadInt32() <> DirectoryVersion Then Throw New IO.InvalidDataException("Invalid lazy schema directory header.")
                Dim result As New LazyDirectoryHeader With {
                    .ScalarOffset = reader.ReadInt64(),
                    .ScalarLength = reader.ReadInt32()
                    }
                reader.ReadInt32()
                result.FileIndexOffset = reader.ReadInt64()
                result.FileCount = reader.ReadInt32()
                result.DirectoryIndexOffset = reader.ReadInt64()
                result.DirectoryCount = reader.ReadInt32()
                result.TotalFileCount = reader.ReadInt64()
                result.TotalDirectoryCount = reader.ReadInt64()
                If result.ScalarOffset < recordOffset + DirectoryHeaderSize OrElse result.ScalarLength < 0 OrElse result.ScalarOffset > stream.Length - result.ScalarLength Then Throw New IO.InvalidDataException("Invalid lazy schema directory scalar record.")
                If result.FileCount < 0 OrElse result.DirectoryCount < 0 OrElse result.TotalFileCount < 0 OrElse result.TotalDirectoryCount < 0 Then Throw New IO.InvalidDataException("Invalid lazy schema directory counts.")
                Return result
            End Using
        End Using
    End Function

    Private Shared Sub WriteDirectoryScalars(writer As IO.BinaryWriter, values As LazyDirectoryScalarData)
        WriteNullableString(writer, values.Name)
        writer.Write(values.ReadOnly)
        WriteNullableString(writer, values.CreationTime)
        WriteNullableString(writer, values.ChangeTime)
        WriteNullableString(writer, values.ModifyTime)
        WriteNullableString(writer, values.AccessTime)
        WriteNullableString(writer, values.BackupTime)
        writer.Write(values.FileUid)
    End Sub

    Private Shared Sub WriteNullableString(writer As IO.BinaryWriter, value As String)
        If value Is Nothing Then
            writer.Write(-1)
            Return
        End If
        Dim bytes As Byte() = New Text.UTF8Encoding(False, True).GetBytes(value)
        writer.Write(bytes.Length)
        writer.Write(bytes)
    End Sub

    Private Shared Function ReadNullableString(reader As IO.BinaryReader) As String
        Dim length As Integer = reader.ReadInt32()
        If length = -1 Then Return Nothing
        If length < 0 OrElse length > 64 * 1024 * 1024 Then Throw New IO.InvalidDataException("Invalid lazy schema string length.")
        Dim bytes As Byte() = reader.ReadBytes(length)
        If bytes.Length <> length Then Throw New IO.EndOfStreamException()
        Return New Text.UTF8Encoding(False, True).GetString(bytes)
    End Function

    Friend Shared Function ReadElementText(reader As XmlReader) As String
        If reader.IsEmptyElement Then Return String.Empty
        Dim elementDepth As Integer = reader.Depth
        Dim result As New Text.StringBuilder
        While reader.Read()
            If reader.NodeType = XmlNodeType.EndElement AndAlso reader.Depth = elementDepth Then Exit While
            Select Case reader.NodeType
                Case XmlNodeType.Text, XmlNodeType.CDATA, XmlNodeType.Whitespace, XmlNodeType.SignificantWhitespace
                    result.Append(reader.Value)
                Case XmlNodeType.Element
                    SkipElement(reader)
            End Select
        End While
        Return result.ToString()
    End Function

    Friend Shared Function DecodeSchemaValue(value As String) As String
        If value Is Nothing Then Return Nothing
        Return value.Replace("%25", "%")
    End Function

    Friend Shared Sub SkipElement(reader As XmlReader)
        If reader.IsEmptyElement Then Exit Sub
        Dim elementDepth As Integer = reader.Depth
        While reader.Read()
            If reader.NodeType = XmlNodeType.EndElement AndAlso reader.Depth = elementDepth Then Exit While
        End While
    End Sub

    Friend Sub WriteSchema(index As ltfsindex, output As IO.TextWriter, reduceSize As Boolean)
        Dim settings As New XmlWriterSettings With {
            .Encoding = New Text.UTF8Encoding(False),
            .OmitXmlDeclaration = True,
            .Indent = False,
            .CloseOutput = False}
        Dim fileSerializer As New Xml.Serialization.XmlSerializer(GetType(ltfsindex.file))
        Dim fileNamespaces As New Xml.Serialization.XmlSerializerNamespaces
        fileNamespaces.Add(String.Empty, String.Empty)

        Using writer As XmlWriter = XmlWriter.Create(output, settings)
            writer.WriteStartElement("ltfsindex")
            writer.WriteAttributeString("version", "2.4.0")
            WriteElement(writer, "creator", index.creator)
            WriteElement(writer, "volumeuuid", index.volumeuuid.ToString())
            WriteElement(writer, "generationnumber", index.generationnumber.ToString(CultureInfo.InvariantCulture))
            WriteElement(writer, "updatetime", index.updatetime)
            WriteLocation(writer, "location", index.location)
            WriteLocation(writer, "previousgenerationlocation", index.previousgenerationlocation)
            WriteElement(writer, "allowpolicyupdate", index.allowpolicyupdate.ToString())
            If index.dataplacementpolicy IsNot Nothing Then
                writer.WriteStartElement("dataplacementpolicy")
                writer.WriteEndElement()
            End If
            WriteElement(writer, "volumelockstate", index.volumelockstate.ToString())
            WriteElement(writer, "highestfileuid", index.highestfileuid.ToString(CultureInfo.InvariantCulture))

            If index._file IsNot Nothing Then
                For Each rootFile As ltfsindex.file In index._file
                    WriteFileObject(writer, rootFile, fileSerializer, fileNamespaces)
                Next
            End If
            If index._directory IsNot Nothing Then
                For Each rootDirectory As ltfsindex.directory In index._directory
                    WriteDirectory(writer, rootDirectory, useCollectionWrappers:=False, fileSerializer, fileNamespaces)
                Next
            End If
            writer.WriteEndElement()
        End Using
        output.Flush()
    End Sub

    Friend Sub WriteDirectory(directory As ltfsindex.directory,
                               output As IO.TextWriter,
                               useCollectionWrappers As Boolean)
        Dim settings As New XmlWriterSettings With {
            .Encoding = New Text.UTF8Encoding(False),
            .OmitXmlDeclaration = True,
            .Indent = False,
            .CloseOutput = False}
        Dim fileSerializer As New Xml.Serialization.XmlSerializer(GetType(ltfsindex.file))
        Dim fileNamespaces As New Xml.Serialization.XmlSerializerNamespaces
        fileNamespaces.Add(String.Empty, String.Empty)

        Using writer As XmlWriter = XmlWriter.Create(output, settings)
            WriteDirectory(writer, directory, useCollectionWrappers, fileSerializer, fileNamespaces)
        End Using
        output.Flush()
    End Sub

    Private Sub WriteDirectory(writer As XmlWriter,
                                directory As ltfsindex.directory,
                                useCollectionWrappers As Boolean,
                                fileSerializer As Xml.Serialization.XmlSerializer,
                                fileNamespaces As Xml.Serialization.XmlSerializerNamespaces)
        If directory Is Nothing Then Exit Sub
        writer.WriteStartElement("directory")

        If directory.HasUnmaterializedLazyContents Then
            Dim modifiedDirectory As ltfsindex.directory = GetModifiedDirectory(directory.LazyRecordOffset)
            Dim values As LazyDirectoryScalarData = If(modifiedDirectory Is Nothing,
                                                        ReadDirectoryScalars(directory.LazyRecordOffset),
                                                        modifiedDirectory.GetLazyScalarDataForWrite())
            WriteDirectoryScalars(writer, values)
            writer.WriteStartElement("contents")
            If useCollectionWrappers Then writer.WriteStartElement("_file")
            Using fileStream As IO.FileStream = OpenFileRecordStream()
                For Each child As LazySchemaChildData In EnumerateFileReferences(directory.LazyRecordOffset)
                    If IsFileRemoved(directory.LazyRecordOffset, child.RecordOffset) Then Continue For
                    Dim modifiedFile As ltfsindex.file = GetModifiedFile(child.RecordOffset)
                    If modifiedFile Is Nothing Then
                        WriteFileRecord(writer, fileStream, child.RecordOffset, child.RecordLength)
                    Else
                        WriteFileObject(writer, modifiedFile, fileSerializer, fileNamespaces)
                    End If
                Next
            End Using
            For Each addedFile As ltfsindex.file In EnumerateAddedFiles(directory.LazyRecordOffset)
                If addedFile.HasLazyRecord AndAlso ReferenceEquals(addedFile.LazyStoreReference, Me) Then
                    Dim modifiedFile As ltfsindex.file = GetModifiedFile(addedFile.LazyRecordOffset)
                    If modifiedFile Is Nothing Then
                        Using fileStream As IO.FileStream = OpenFileRecordStream()
                            WriteFileRecord(writer, fileStream, addedFile.LazyRecordOffset, addedFile.LazyRecordLength)
                        End Using
                    Else
                        WriteFileObject(writer, modifiedFile, fileSerializer, fileNamespaces)
                    End If
                Else
                    WriteFileObject(writer, addedFile, fileSerializer, fileNamespaces)
                End If
                Next
            If useCollectionWrappers Then writer.WriteEndElement()
            If useCollectionWrappers Then writer.WriteStartElement("_directory")
            For Each child As LazySchemaChildData In EnumerateDirectoryReferences(directory.LazyRecordOffset)
                If IsDirectoryRemoved(directory.LazyRecordOffset, child.RecordOffset) Then Continue For
                Dim childDirectory As ltfsindex.directory = GetModifiedDirectory(child.RecordOffset)
                If childDirectory Is Nothing Then
                    childDirectory = New ltfsindex.directory
                    childDirectory.AttachLazyRecord(Me, child.RecordOffset)
                End If
                WriteDirectory(writer, childDirectory, useCollectionWrappers, fileSerializer, fileNamespaces)
            Next
            For Each addedDirectory As ltfsindex.directory In EnumerateAddedDirectories(directory.LazyRecordOffset)
                WriteDirectory(writer, addedDirectory, useCollectionWrappers, fileSerializer, fileNamespaces)
            Next
            If useCollectionWrappers Then writer.WriteEndElement()
            writer.WriteEndElement()
        Else
            WriteElement(writer, "name", directory.name)
            WriteElement(writer, "readonly", directory.readonly.ToString())
            WriteElement(writer, "creationtime", directory.creationtime)
            WriteElement(writer, "changetime", directory.changetime)
            WriteElement(writer, "modifytime", directory.modifytime)
            WriteElement(writer, "accesstime", directory.accesstime)
            WriteElement(writer, "backuptime", directory.backuptime)
            WriteElement(writer, "fileuid", directory.fileuid.ToString(CultureInfo.InvariantCulture))
            writer.WriteStartElement("contents")
            If useCollectionWrappers Then writer.WriteStartElement("_file")
            If directory.contents IsNot Nothing AndAlso directory.contents._file IsNot Nothing Then
                For Each childFile As ltfsindex.file In directory.contents._file
                    WriteFileObject(writer, childFile, fileSerializer, fileNamespaces)
                Next
            End If
            If useCollectionWrappers Then writer.WriteEndElement()
            If useCollectionWrappers Then writer.WriteStartElement("_directory")
            If directory.contents IsNot Nothing AndAlso directory.contents._directory IsNot Nothing Then
                For Each childDirectory As ltfsindex.directory In directory.contents._directory
                    WriteDirectory(writer, childDirectory, useCollectionWrappers, fileSerializer, fileNamespaces)
                Next
            End If
            If useCollectionWrappers Then writer.WriteEndElement()
            writer.WriteEndElement()
        End If

        writer.WriteEndElement()
    End Sub

    Private Sub WriteDirectoryScalars(writer As XmlWriter, values As LazyDirectoryScalarData)
        WriteElement(writer, "name", values.Name)
        WriteElement(writer, "readonly", values.ReadOnly.ToString())
        WriteElement(writer, "creationtime", values.CreationTime)
        WriteElement(writer, "changetime", values.ChangeTime)
        WriteElement(writer, "modifytime", values.ModifyTime)
        WriteElement(writer, "accesstime", values.AccessTime)
        WriteElement(writer, "backuptime", values.BackupTime)
        WriteElement(writer, "fileuid", values.FileUid.ToString(CultureInfo.InvariantCulture))
    End Sub

    Private Shared Sub WriteLocation(writer As XmlWriter, elementName As String, value As ltfsindex.LocationDef)
        If value Is Nothing Then Exit Sub
        writer.WriteStartElement(elementName)
        WriteElement(writer, "partition", value.partition.ToString())
        WriteElement(writer, "startblock", value.startblock.ToString(CultureInfo.InvariantCulture))
        writer.WriteEndElement()
    End Sub

    Private Shared Sub WriteElement(writer As XmlWriter, elementName As String, value As String)
        writer.WriteStartElement(elementName)
        If value IsNot Nothing Then writer.WriteString(value)
        writer.WriteEndElement()
    End Sub

    Private Shared Sub WriteFileObject(writer As XmlWriter,
                                       value As ltfsindex.file,
                                       serializer As Xml.Serialization.XmlSerializer,
                                       namespaces As Xml.Serialization.XmlSerializerNamespaces)
        If value Is Nothing Then Exit Sub
        serializer.Serialize(writer, value, namespaces)
    End Sub

    Private Function OpenFileRecordStream() As IO.FileStream
        Return New IO.FileStream(_fileRecordsPath, IO.FileMode.Open, IO.FileAccess.Read, IO.FileShare.Read, IoBufferSize, IO.FileOptions.SequentialScan)
    End Function

    Private Sub WriteFileRecord(writer As XmlWriter,
                                stream As IO.FileStream,
                                recordOffset As Long,
                                recordLength As Long)
        If recordOffset < 0 OrElse recordLength <= 0 OrElse recordOffset > stream.Length OrElse recordLength > stream.Length - recordOffset Then
            Throw New IO.InvalidDataException("Invalid lazy schema file record.")
        End If
        stream.Seek(recordOffset, IO.SeekOrigin.Begin)
        Using limited As New LazySchemaLimitedStream(stream, recordLength, leaveInnerOpen:=True)
            Dim settings As New XmlReaderSettings With {
                .IgnoreComments = True,
                .IgnoreWhitespace = True,
                .IgnoreProcessingInstructions = True,
                .DtdProcessing = DtdProcessing.Prohibit,
                .XmlResolver = Nothing,
                .CloseInput = False}
            Using reader As XmlReader = XmlReader.Create(limited, settings)
                reader.MoveToContent()
                If reader.NodeType <> XmlNodeType.Element Then Throw New IO.InvalidDataException("Invalid lazy schema file record.")
                writer.WriteNode(reader, True)
            End Using
        End Using
    End Sub

    Protected Overrides Sub Finalize()
        Try
            SyncLock _buildLock
                CloseBuildStreams()
                DeleteBackingFiles()
            End SyncLock
        Finally
            MyBase.Finalize()
        End Try
    End Sub
End Class

Friend NotInheritable Class LazySchemaLimitedStream
    Inherits IO.Stream

    Private ReadOnly _inner As IO.Stream
    Private ReadOnly _leaveInnerOpen As Boolean
    Private _remaining As Long
    Private _position As Long

    Public Sub New(inner As IO.Stream, length As Long, Optional leaveInnerOpen As Boolean = False)
        _inner = inner
        _leaveInnerOpen = leaveInnerOpen
        _remaining = length
    End Sub

    Public Overrides ReadOnly Property CanRead As Boolean
        Get
            Return True
        End Get
    End Property
    Public Overrides ReadOnly Property CanSeek As Boolean
        Get
            Return False
        End Get
    End Property
    Public Overrides ReadOnly Property CanWrite As Boolean
        Get
            Return False
        End Get
    End Property
    Public Overrides ReadOnly Property Length As Long
        Get
            Return _position + _remaining
        End Get
    End Property
    Public Overrides Property Position As Long
        Get
            Return _position
        End Get
        Set(value As Long)
            Throw New NotSupportedException()
        End Set
    End Property

    Public Overrides Sub Flush()
    End Sub

    Public Overrides Function Read(buffer() As Byte, offset As Integer, count As Integer) As Integer
        If _remaining <= 0 Then Return 0
        Dim readCount As Integer = CInt(Math.Min(CLng(count), _remaining))
        Dim bytesRead As Integer = _inner.Read(buffer, offset, readCount)
        If bytesRead > 0 Then
            _position += bytesRead
            _remaining -= bytesRead
        End If
        Return bytesRead
    End Function

    Public Overrides Function Seek(offset As Long, origin As IO.SeekOrigin) As Long
        Throw New NotSupportedException()
    End Function

    Public Overrides Sub SetLength(value As Long)
        Throw New NotSupportedException()
    End Sub

    Public Overrides Sub Write(buffer() As Byte, offset As Integer, count As Integer)
        Throw New NotSupportedException()
    End Sub

    Protected Overrides Sub Dispose(disposing As Boolean)
        If disposing AndAlso _inner IsNot Nothing AndAlso Not _leaveInnerOpen Then _inner.Dispose()
        MyBase.Dispose(disposing)
    End Sub
End Class

Friend NotInheritable Class LazySchemaReader
    Private Sub New()
    End Sub

    Friend Shared Function Load(fileName As String) As ltfsindex
        Dim store As LazySchemaStore = LazySchemaStore.CreateForBuild()
        Try
            Dim result As New ltfsindex
            Using input As New IO.FileStream(fileName, IO.FileMode.Open, IO.FileAccess.Read, IO.FileShare.Read, 1 << 16, IO.FileOptions.SequentialScan)
                Dim settings As New XmlReaderSettings With {
                    .IgnoreComments = True,
                    .IgnoreWhitespace = True,
                    .IgnoreProcessingInstructions = True,
                    .DtdProcessing = DtdProcessing.Prohibit,
                    .XmlResolver = Nothing,
                    .CloseInput = False}
                Using reader As XmlReader = XmlReader.Create(input, settings)
                    reader.MoveToContent()
                    ParseIndex(reader, result, store)
                End Using
            End Using
            store.FinishBuild()
            result.AttachLazyStore(store)
            Return result
        Catch
            store.AbortBuild()
            Throw
        End Try
    End Function

    Private Shared Sub ParseIndex(reader As XmlReader, result As ltfsindex, store As LazySchemaStore)
        If reader.NodeType <> XmlNodeType.Element Then Throw New IO.InvalidDataException("Schema root element was not found.")
        Dim rootDepth As Integer = reader.Depth
        If reader.IsEmptyElement Then Exit Sub
        Dim textValue As String
        Dim parsedBoolean As Boolean
        Dim parsedLong As Long
        Dim parsedLockState As ltfsindex.volumelockstateValue

        While reader.Read()
            If reader.NodeType = XmlNodeType.EndElement AndAlso reader.Depth = rootDepth Then Exit While
            If reader.NodeType <> XmlNodeType.Element OrElse reader.Depth <> rootDepth + 1 Then Continue While

            Select Case reader.LocalName
                Case "creator"
                    result.creator = LazySchemaStore.DecodeSchemaValue(LazySchemaStore.ReadElementText(reader))
                Case "volumeuuid"
                    Dim guidValue As Guid
                    If Guid.TryParse(LazySchemaStore.ReadElementText(reader), guidValue) Then result.volumeuuid = guidValue
                Case "generationnumber"
                    Dim ulongValue As ULong
                    If ULong.TryParse(LazySchemaStore.ReadElementText(reader), NumberStyles.Integer, CultureInfo.InvariantCulture, ulongValue) Then result.generationnumber = ulongValue
                Case "updatetime"
                    result.updatetime = LazySchemaStore.DecodeSchemaValue(LazySchemaStore.ReadElementText(reader))
                Case "location"
                    result.location = ParseLocation(reader)
                Case "previousgenerationlocation"
                    result.previousgenerationlocation = ParseLocation(reader)
                Case "allowpolicyupdate"
                    textValue = LazySchemaStore.ReadElementText(reader)
                    If Boolean.TryParse(textValue, parsedBoolean) Then result.allowpolicyupdate = parsedBoolean
                Case "volumelockstate"
                    textValue = LazySchemaStore.ReadElementText(reader)
                    If [Enum].TryParse(textValue, True, parsedLockState) Then result.volumelockstate = parsedLockState
                Case "highestfileuid"
                    textValue = LazySchemaStore.ReadElementText(reader)
                    If Long.TryParse(textValue, NumberStyles.Integer, CultureInfo.InvariantCulture, parsedLong) Then result.highestfileuid = parsedLong
                Case "directory"
                    result._directory.Add(CreateDirectory(ParseDirectory(reader, store), store))
                Case "file"
                    Dim fileReference As LazyFileRecordReference = ParseFile(reader, store)
                    Dim rootFile As New ltfsindex.file
                    rootFile.AttachLazyRecord(store, fileReference.Offset, fileReference.Length)
                    result._file.Add(rootFile)
                Case "_directory", "contents"
                    ParseRootContainer(reader, result, store)
                Case "_file"
                    ParseRootFileContainer(reader, result, store)
                Case Else
                    LazySchemaStore.SkipElement(reader)
            End Select
        End While
    End Sub

    Private Shared Sub ParseRootContainer(reader As XmlReader, result As ltfsindex, store As LazySchemaStore)
        Dim containerDepth As Integer = reader.Depth
        If reader.IsEmptyElement Then Exit Sub
        While reader.Read()
            If reader.NodeType = XmlNodeType.EndElement AndAlso reader.Depth = containerDepth Then Exit While
            If reader.NodeType <> XmlNodeType.Element OrElse reader.Depth <> containerDepth + 1 Then Continue While
            Select Case reader.LocalName
                Case "directory"
                    result._directory.Add(CreateDirectory(ParseDirectory(reader, store), store))
                Case "file"
                    Dim fileReference As LazyFileRecordReference = ParseFile(reader, store)
                    Dim rootFile As New ltfsindex.file
                    rootFile.AttachLazyRecord(store, fileReference.Offset, fileReference.Length)
                    result._file.Add(rootFile)
                Case "_directory", "contents"
                    ParseRootContainer(reader, result, store)
                Case "_file"
                    ParseRootFileContainer(reader, result, store)
                Case Else
                    LazySchemaStore.SkipElement(reader)
            End Select
        End While
    End Sub

    Private Shared Sub ParseRootFileContainer(reader As XmlReader, result As ltfsindex, store As LazySchemaStore)
        Dim containerDepth As Integer = reader.Depth
        If reader.IsEmptyElement Then Exit Sub
        While reader.Read()
            If reader.NodeType = XmlNodeType.EndElement AndAlso reader.Depth = containerDepth Then Exit While
            If reader.NodeType = XmlNodeType.Element AndAlso reader.Depth = containerDepth + 1 AndAlso reader.LocalName = "file" Then
                Dim fileReference As LazyFileRecordReference = ParseFile(reader, store)
                Dim rootFile As New ltfsindex.file
                rootFile.AttachLazyRecord(store, fileReference.Offset, fileReference.Length)
                result._file.Add(rootFile)
            End If
        End While
    End Sub

    Private Shared Function ParseDirectory(reader As XmlReader, store As LazySchemaStore) As LazyDirectoryReference
        Dim state As LazyDirectoryBuildState = store.BeginDirectoryRecord()
        Dim values As New LazyDirectoryScalarData
        Dim directoryDepth As Integer = reader.Depth
        Dim textValue As String
        Dim parsedBoolean As Boolean
        Dim parsedLong As Long

        If Not reader.IsEmptyElement Then
            While reader.Read()
                If reader.NodeType = XmlNodeType.EndElement AndAlso reader.Depth = directoryDepth Then Exit While
                If reader.NodeType <> XmlNodeType.Element OrElse reader.Depth <> directoryDepth + 1 Then Continue While

                Select Case reader.LocalName
                    Case "name"
                        values.Name = LazySchemaStore.DecodeSchemaValue(LazySchemaStore.ReadElementText(reader))
                    Case "readonly"
                        textValue = LazySchemaStore.ReadElementText(reader)
                        If Boolean.TryParse(textValue, parsedBoolean) Then values.ReadOnly = parsedBoolean
                    Case "creationtime"
                        values.CreationTime = LazySchemaStore.DecodeSchemaValue(LazySchemaStore.ReadElementText(reader))
                    Case "changetime"
                        values.ChangeTime = LazySchemaStore.DecodeSchemaValue(LazySchemaStore.ReadElementText(reader))
                    Case "modifytime"
                        values.ModifyTime = LazySchemaStore.DecodeSchemaValue(LazySchemaStore.ReadElementText(reader))
                    Case "accesstime"
                        values.AccessTime = LazySchemaStore.DecodeSchemaValue(LazySchemaStore.ReadElementText(reader))
                    Case "backuptime"
                        values.BackupTime = LazySchemaStore.DecodeSchemaValue(LazySchemaStore.ReadElementText(reader))
                    Case "fileuid"
                        textValue = LazySchemaStore.ReadElementText(reader)
                        If Long.TryParse(textValue, NumberStyles.Integer, CultureInfo.InvariantCulture, parsedLong) Then values.FileUid = parsedLong
                    Case "contents", "_directory", "_file"
                        ParseDirectoryContainer(reader, state, store)
                    Case "directory"
                        Dim childDirectory As LazyDirectoryReference = ParseDirectory(reader, store)
                        store.AddChild(state, LazySchemaChildKind.DirectoryRecord, childDirectory.Offset, 0,
                                       childDirectory.TotalFileCount, childDirectory.TotalDirectoryCount)
                    Case "file"
                        Dim fileReference As LazyFileRecordReference = ParseFile(reader, store)
                        store.AddChild(state, LazySchemaChildKind.FileRecord, fileReference.Offset, fileReference.Length)
                    Case Else
                        LazySchemaStore.SkipElement(reader)
                End Select
            End While
        End If

        Return New LazyDirectoryReference With {
            .Offset = store.FinishDirectoryRecord(state, values),
            .TotalFileCount = state.TotalFileCount,
            .TotalDirectoryCount = state.TotalDirectoryCount}
    End Function

    Private Shared Sub ParseDirectoryContainer(reader As XmlReader,
                                                ByRef parentState As LazyDirectoryBuildState,
                                                store As LazySchemaStore)
        Dim containerDepth As Integer = reader.Depth
        If reader.IsEmptyElement Then Exit Sub
        While reader.Read()
            If reader.NodeType = XmlNodeType.EndElement AndAlso reader.Depth = containerDepth Then Exit While
            If reader.NodeType <> XmlNodeType.Element OrElse reader.Depth <> containerDepth + 1 Then Continue While
            Select Case reader.LocalName
                Case "directory"
                    Dim childDirectory As LazyDirectoryReference = ParseDirectory(reader, store)
                    store.AddChild(parentState, LazySchemaChildKind.DirectoryRecord, childDirectory.Offset, 0,
                                   childDirectory.TotalFileCount, childDirectory.TotalDirectoryCount)
                Case "file"
                    Dim fileReference As LazyFileRecordReference = ParseFile(reader, store)
                    store.AddChild(parentState, LazySchemaChildKind.FileRecord, fileReference.Offset, fileReference.Length)
                Case "contents", "_directory", "_file"
                    ParseDirectoryContainer(reader, parentState, store)
                Case Else
                    LazySchemaStore.SkipElement(reader)
            End Select
        End While
    End Sub

    Private Structure LazyFileRecordReference
        Public Offset As Long
        Public Length As Long
    End Structure

    Private Shared Function ParseFile(reader As XmlReader, store As LazySchemaStore) As LazyFileRecordReference
        Dim result As New LazyFileRecordReference With {.Offset = store.BeginFileRecord()}
        Using subtree As XmlReader = reader.ReadSubtree()
            subtree.MoveToContent()
            Using writer As XmlWriter = store.CreateFileXmlWriter()
                writer.WriteNode(subtree, True)
            End Using
        End Using
        result.Length = store.EndFileRecord(result.Offset)
        Return result
    End Function

    Private Shared Function CreateDirectory(reference As LazyDirectoryReference, store As LazySchemaStore) As ltfsindex.directory
        Dim result As New ltfsindex.directory
        result.AttachLazyRecord(store, reference.Offset)
        Return result
    End Function

    Private Shared Function ParseLocation(reader As XmlReader) As ltfsindex.LocationDef
        Dim result As New ltfsindex.LocationDef
        Dim locationDepth As Integer = reader.Depth
        Dim textValue As String
        Dim parsedPartition As ltfsindex.PartitionLabel
        Dim parsedULong As ULong
        If reader.IsEmptyElement Then Return result
        While reader.Read()
            If reader.NodeType = XmlNodeType.EndElement AndAlso reader.Depth = locationDepth Then Exit While
            If reader.NodeType <> XmlNodeType.Element OrElse reader.Depth <> locationDepth + 1 Then Continue While
            Select Case reader.LocalName
                Case "partition"
                    textValue = LazySchemaStore.ReadElementText(reader)
                    If [Enum].TryParse(textValue, True, parsedPartition) Then result.partition = parsedPartition
                Case "startblock"
                    textValue = LazySchemaStore.ReadElementText(reader)
                    If ULong.TryParse(textValue, NumberStyles.Integer, CultureInfo.InvariantCulture, parsedULong) Then result.startblock = parsedULong
                Case Else
                    LazySchemaStore.SkipElement(reader)
            End Select
        End While
        Return result
    End Function
End Class

<Serializable>
<TypeConverter(GetType(ExpandableObjectConverter))>
<Category("LTFSIndex")>
Public Class ltfslabel
    <Category("LTFSIndex")>
    Public Property creator As String = My.Application.Info.ProductName & " " & My.Application.Info.Version.ToString(3) & " - Windows - TapeUtils"
    <Category("LTFSIndex")>
    Public Property formattime As String = Now.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffff00Z")
    <Category("LTFSIndex")>
    Public Property volumeuuid As Guid
    <Serializable>
    Public Enum PartitionLabel As Byte
        a = 0
        b = 1
    End Enum
    <TypeConverter(GetType(ExpandableObjectConverter))>
    Public Class PartitionLocation

        Public Property partition As PartitionLabel = PartitionLabel.a
    End Class
    <Category("LTFSIndex")>
    Public Property location As New PartitionLocation
    <Serializable>
    <TypeConverter(GetType(ExpandableObjectConverter))>
    Public Class PartitionInfo

        Public Property index As PartitionLabel = PartitionLabel.a
        Public Property data As PartitionLabel = PartitionLabel.b
    End Class
    <Category("LTFSIndex")>
    Public Property partitions As New PartitionInfo
    <Category("LTFSIndex")>
    Public Property blocksize As Integer = 524288
    <Category("LTFSIndex")>
    Public Property compression As Boolean = True
    Public Function GetSerializedText(Optional ByVal ReduceSize As Boolean = True) As String
        Dim writer As New Xml.Serialization.XmlSerializer(GetType(ltfslabel))
        Dim tmpf As String = $"{Application.StartupPath}\LCG_{Now.ToString("yyyyMMdd_HHmmss")}.tmp"
        Dim ms As New IO.FileStream(tmpf, IO.FileMode.Create)
        Dim t As IO.TextWriter = New IO.StreamWriter(ms, New Text.UTF8Encoding(False))
        Dim ns As New Xml.Serialization.XmlSerializerNamespaces({New Xml.XmlQualifiedName("v", "2.4.0")})
        writer.Serialize(t, Me, ns)

        ms.Close()
        Dim soutp As New IO.StreamReader(tmpf)

        Dim sout As New Text.StringBuilder
        Dim sline As String = soutp.ReadLine
        If sline.StartsWith("<?xml") Then
            sline = sline.Replace("utf-8", "UTF-8")
        End If
        If ReduceSize Then
            sline = sline.Replace("xmlns:v", "version")
        End If
        If sline.Length > 0 Then sout.AppendLine(sline)
        While Not soutp.EndOfStream
            sline = soutp.ReadLine
            If ReduceSize Then
                sline = sline.Replace("xmlns:v", "version")
                sline = sline.Replace("<_file />", "")
                sline = sline.Replace("<_directory />", "")
                sline = sline.Replace("<_file>", "")
                sline = sline.Replace("</_file>", "")
                sline = sline.Replace("<_directory>", "")
                sline = sline.Replace("</_directory>", "")
                sline = sline.TrimEnd(" "c)
            End If
            If sline.Length > 0 Then sout.AppendLine(sline)
        End While
        soutp.Close()
        IO.File.Delete(tmpf)
        Return sout.ToString()
    End Function
    Public Shared Function FromXML(s As String) As ltfslabel
        Dim reader As New Xml.Serialization.XmlSerializer(GetType(ltfslabel))
        Dim t As IO.TextReader = New IO.StringReader(s)
        Return CType(reader.Deserialize(t), ltfslabel)
    End Function
    Public Function Clone() As ltfslabel
        Return (FromXML(GetSerializedText(False)))
    End Function
End Class

<TypeConverter(GetType(ExpandableObjectConverter))>
Public Class Vol1Label
    Private _label_identifier As String = "VOL".PadRight(3)
    <Category("LTFSIndex")>
    Public Property label_identifier As String
        Set(value As String)
            _label_identifier = value.PadRight(3).Substring(0, 3)
        End Set
        Get
            Return _label_identifier
        End Get
    End Property
    <Category("LTFSIndex")>
    Public Property label_number As Char = "1"c
    Private _volume_identifier As String = "".PadRight(6)
    <Category("LTFSIndex")>
    Public Property volume_identifier As String
        Set(value As String)
            _volume_identifier = value.PadRight(6).Substring(0, 6)
        End Set
        Get
            Return _volume_identifier
        End Get
    End Property
    <Category("LTFSIndex")>
    Public Property volume_accessibility As Char = "L"c
    Private _implementation_identifier As String = "LTFS".PadRight(13)
    <Category("LTFSIndex")>
    Public Property implementation_identifier As String
        Set(value As String)
            _implementation_identifier = value.PadRight(13).Substring(0, 13)
        End Set
        Get
            Return _implementation_identifier
        End Get
    End Property
    Private _owner_identifier As String = "".PadRight(14).Substring(0, 14)
    <Category("LTFSIndex")>
    Public Property owner_identifier As String
        Set(value As String)
            _owner_identifier = value.PadRight(14)
        End Set
        Get
            Return _owner_identifier
        End Get
    End Property
    <Category("LTFSIndex")>
    Public Property label_standard_version As Char = "4"c

    Public Function GenerateRawData(Optional ByVal Barcode As String = "") As Byte()
        If Barcode <> "" Then volume_identifier = Barcode.ToUpper().Substring(0, Math.Min(6, Barcode.Length))
        Dim RawData(79) As Byte
        For i As Integer = 0 To 79
            RawData(i) = &H20
        Next
        For i As Integer = 0 To 2
            RawData(i + 0) = CByte(Asc(label_identifier(i)))
        Next
        RawData(3) = CByte(Asc(label_number))
        For i As Integer = 0 To 5
            RawData(i + 4) = CByte(Asc(volume_identifier(i)))
        Next
        RawData(10) = CByte(Asc(volume_accessibility))
        For i As Integer = 0 To 12
            RawData(i + 24) = CByte(Asc(implementation_identifier(i)))
        Next
        For i As Integer = 0 To 13
            RawData(i + 37) = CByte(Asc(owner_identifier(i)))
        Next
        RawData(79) = CByte(Asc(label_standard_version))
        Return RawData
    End Function
End Class
