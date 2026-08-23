Imports System
Imports System.ComponentModel
Imports System.IO
Imports System.Reflection
Imports System.Runtime.Serialization
Imports System.Text
Imports System.Xml.Serialization
Imports Microsoft.Diagnostics.Runtime
Imports Microsoft.Extensions.FileSystemGlobbing
Imports Serilog
Imports Serilog.Context
Imports stdole
Imports LTFSCopyGUI.Native
Public Class SerializationHelper
    Public Shared Function GetSerializeString(ByVal c As Object) As String
        Dim s As New MemoryStream
        Dim b As Formatters.Binary.BinaryFormatter = New Formatters.Binary.BinaryFormatter
        b.Serialize(s, c)
        Return Convert.ToBase64String(s.ToArray())
    End Function
    Public Shared Function FromSerializeString(ByVal d As String) As Object
        Dim s As New MemoryStream(Convert.FromBase64String(d))
        Dim b As Formatters.Binary.BinaryFormatter = New Formatters.Binary.BinaryFormatter
        Return b.Deserialize(s)
    End Function
End Class
Public Class LocalizedDescriptionAttribute
    Inherits DescriptionAttribute
    Public Shared Function Localize(key As String) As String
        Return My.Resources.ResourceManager.GetString(key)
    End Function
    Public Sub New(ByVal key As String)
        MyBase.New(Localize(key))
    End Sub
End Class

<Serializable>
Public Class SettingImportExport
    Public Property MySettings As SerializableDictionary(Of String, String)
        Get
            Dim result As New SerializableDictionary(Of String, String)
            For Each setting As System.Configuration.SettingsPropertyValue In My.Settings.PropertyValues
                If setting.PropertyValue IsNot Nothing Then result.Add(setting.Name, SerializationHelper.GetSerializeString(setting.PropertyValue))
            Next
            Return result
        End Get
        Set(value As SerializableDictionary(Of String, String))
            For Each setting As String In value.Keys

                My.Settings.PropertyValues.Item(setting).PropertyValue = SerializationHelper.FromSerializeString(value(setting))
            Next
            My.Settings.Save()
        End Set
    End Property
    Public Shared Function GetSerializedText() As String
        Dim writer As New XmlSerializer(GetType(SettingImportExport))
        Dim sb As New StringBuilder
        Dim t As New StringWriter(sb)
        writer.Serialize(t, New SettingImportExport())
        Return sb.ToString()
    End Function
    Public Shared Sub LoadFromFile(FileName As String)
        Dim s As SettingImportExport = FromXML(File.ReadAllText(FileName))
    End Sub
    Public Shared Function FromXML(s As String) As SettingImportExport
        Dim reader As New XmlSerializer(GetType(SettingImportExport))
        Dim t As TextReader = New StringReader(s)
        Return CType(reader.Deserialize(t), SettingImportExport)
    End Function
End Class

''' <summary>   
''' 支持XML序列化的泛型   
''' </summary>   
''' <typeparam name="TKey"></typeparam>   
''' <typeparam name="TValue"></typeparam>   
<XmlRoot("SerializableDictionary"),
    Serializable>
Public Class SerializableDictionary(Of TKey, TValue)
    Inherits Dictionary(Of TKey, TValue)
    Implements IXmlSerializable
#Region "构造函数"
    Public Sub New()
        MyBase.New

    End Sub

    Public Sub New(ByVal dictionary As IDictionary(Of TKey, TValue))
        MyBase.New(dictionary)

    End Sub

    Public Sub New(ByVal comparer As IEqualityComparer(Of TKey))
        MyBase.New(comparer)

    End Sub

    Public Sub New(ByVal capacity As Integer)
        MyBase.New(capacity)

    End Sub

    Public Sub New(ByVal capacity As Integer, ByVal comparer As IEqualityComparer(Of TKey))
        MyBase.New(capacity, comparer)

    End Sub

    Protected Sub New(ByVal info As SerializationInfo, ByVal context As StreamingContext)
        MyBase.New(info, context)

    End Sub
#End Region
#Region "IXmlSerializable Members   "

    Public Function GetSchema() As Xml.Schema.XmlSchema Implements IXmlSerializable.GetSchema
        Return Nothing
    End Function

    ''' <summary>   
    ''' 从对象的 XML 表示形式生成该对象  
    ''' </summary>   
    ''' <param name="reader"></param>   
    Public Sub ReadXml(ByVal reader As Xml.XmlReader) Implements IXmlSerializable.ReadXml
        Dim keySerializer As XmlSerializer = New XmlSerializer(GetType(TKey))
        Dim valueSerializer As XmlSerializer = New XmlSerializer(GetType(TValue))
        Dim wasEmpty As Boolean = reader.IsEmptyElement
        reader.Read()

        If wasEmpty Then
            Return
        End If


        While (reader.NodeType <> Xml.XmlNodeType.EndElement)
            reader.ReadStartElement("item")
            reader.ReadStartElement("key")
            Dim key As TKey = CType(keySerializer.Deserialize(reader), TKey)
            reader.ReadEndElement()
            reader.ReadStartElement("value")
            Dim value As TValue = CType(valueSerializer.Deserialize(reader), TValue)
            reader.ReadEndElement()
            Add(key, value)
            reader.ReadEndElement()
            reader.MoveToContent()

        End While

        reader.ReadEndElement()
    End Sub

    Public Sub WriteXml(ByVal writer As Xml.XmlWriter) Implements IXmlSerializable.WriteXml
        Dim keySerializer As XmlSerializer = New XmlSerializer(GetType(TKey))
        Dim valueSerializer As XmlSerializer = New XmlSerializer(GetType(TValue))
        For Each key As TKey In Keys
            writer.WriteStartElement("item")
            writer.WriteStartElement("key")
            keySerializer.Serialize(writer, key)
            writer.WriteEndElement()
            writer.WriteStartElement("value")
            Dim value As TValue = Me(key)
            valueSerializer.Serialize(writer, value)
            writer.WriteEndElement()
            writer.WriteEndElement()
        Next
    End Sub
#End Region
End Class

<TypeConverter(GetType(ExpandableObjectConverter))>
Public Class NamedObject
    Public Property Name As String
    <Category("Edit")>
    Public ReadOnly Property Type As Type
        Get
            Return Value.GetType()
        End Get
    End Property
    <TypeConverter(GetType(ExpandableObjectConverter))>
    Public Property Value As Object
        Get
            Return FieldInfo.GetValue(Instance)
        End Get
        Set(value As Object)
            FieldInfo.SetValue(Instance, value)
        End Set
    End Property

    <Category("Edit")>
    Public Property AsString As String
        Get
            Dim result As Object = Value
            If TypeOf result Is String Then
                Return CStr(result)
            Else
                Return Nothing
            End If
        End Get
        Set(value As String)
            If TypeOf Me.Value IsNot String Then Exit Property
            Me.Value = value
        End Set
    End Property
    <Category("Edit")>
    Public Property AsBoolean As Boolean
        Get
            Dim result As Object = Value
            If TypeOf result Is Boolean Then
                Return CBool(result)
            Else
                Return Nothing
            End If
        End Get
        Set(value As Boolean)
            If TypeOf Me.Value IsNot Boolean Then Exit Property
            Me.Value = value
        End Set
    End Property
    <Category("Edit")>
    Public Property AsByte As Byte
        Get
            Dim result As Object = Value
            If TypeOf result Is Byte Then
                Return CByte(result)
            Else
                Return Nothing
            End If
        End Get
        Set(value As Byte)
            If TypeOf Me.Value IsNot Byte Then Exit Property
            Me.Value = value
        End Set
    End Property
    <Category("Edit")>
    Public Property AsShort As Short
        Get
            Dim result As Object = Value
            If TypeOf result Is Short Then
                Return CShort(result)
            Else
                Return Nothing
            End If
        End Get
        Set(value As Short)
            If TypeOf Me.Value IsNot Short Then Exit Property
            Me.Value = value
        End Set
    End Property
    <Category("Edit")>
    Public Property AsInteger As Integer
        Get
            Dim result As Object = Value
            If TypeOf result Is Integer Then
                Return CInt(result)
            Else
                Return Nothing
            End If
        End Get
        Set(value As Integer)
            If TypeOf Me.Value IsNot Integer Then Exit Property
            Me.Value = value
        End Set
    End Property
    <Category("Edit")>
    Public Property AsLong As Long
        Get
            Dim result As Object = Value
            If TypeOf result Is Long Then
                Return CLng(result)
            Else
                Return Nothing
            End If
        End Get
        Set(value As Long)
            If TypeOf Me.Value IsNot Long Then Exit Property
            Me.Value = value
        End Set
    End Property
    <Category("Edit")>
    Public Property AsUByte As SByte
        Get
            Dim result As Object = Value
            If TypeOf result Is SByte Then
                Return CSByte(result)
            Else
                Return Nothing
            End If
        End Get
        Set(value As SByte)
            If TypeOf Me.Value IsNot SByte Then Exit Property
            Me.Value = value
        End Set
    End Property
    <Category("Edit")>
    Public Property AsUShort As UShort
        Get
            Dim result As Object = Value
            If TypeOf result Is UShort Then
                Return CUShort(result)
            Else
                Return Nothing
            End If
        End Get
        Set(value As UShort)
            If TypeOf Me.Value IsNot UShort Then Exit Property
            Me.Value = value
        End Set
    End Property
    <Category("Edit")>
    Public Property AsUInteger As UInteger
        Get
            Dim result As Object = Value
            If TypeOf result Is UInteger Then
                Return CUInt(result)
            Else
                Return Nothing
            End If
        End Get
        Set(value As UInteger)
            If TypeOf Me.Value IsNot UInteger Then Exit Property
            Me.Value = value
        End Set
    End Property
    <Category("Edit")>
    Public Property AsULong As ULong
        Get
            Dim result As Object = Value
            If TypeOf result Is ULong Then
                Return CULng(result)
            Else
                Return Nothing
            End If
        End Get
        Set(value As ULong)
            If TypeOf Me.Value IsNot ULong Then Exit Property
            Me.Value = value
        End Set
    End Property
    <Category("Edit")>
    Public Property AsCollection As List(Of Object)
        Get
            Dim result As New List(Of Object)
            Dim collection As IEnumerable = TryCast(Value, IEnumerable)
            If collection Is Nothing Then Return result
            For Each o As Object In collection
                result.Add(o)
            Next
            Return result
        End Get
        Set(value As List(Of Object))
            Me.Value = value
        End Set
    End Property
    Public Property Instance As Object
    Public Property FieldInfo As FieldInfo
    Public Sub New(Name As String, ByVal f As FieldInfo, ByVal Instance As Object)
        Me.Name = Name
        FieldInfo = f
        Me.Instance = Instance
    End Sub
End Class

Public Class ListTypeDescriptor(Of TColl As IList, TItem)
    Inherits ExpandableObjectConverter

    Public Overrides Function GetProperties(context As ITypeDescriptorContext, value As Object, attributes() As Attribute) As PropertyDescriptorCollection
        Dim coll = DirectCast(value, TColl)
        Dim props(coll.Count - 1) As PropertyDescriptor
        Dim digits As Integer = (coll.Count - 1).ToString().Length
        For i = 0 To coll.Count - 1
            Dim Name As String = ""
            If i < coll.Count AndAlso coll(i) IsNot Nothing Then
                Dim obj As Object = coll(i)
                Dim myType As Type = obj.GetType()
                Dim myprops As New List(Of PropertyInfo)(myType.GetProperties)
                'If myprops.Count > 0 Then Name = $": {myprops(0).GetValue(obj)}"
            End If
            props(i) = New ListPropertyDescriptor(Of TColl, TItem)($"Item{CStr(i).PadLeft(digits, "0"c)}{Name}")
        Next
        Return New PropertyDescriptorCollection(props)
    End Function
    Public Overrides Function GetPropertiesSupported(context As ITypeDescriptorContext) As Boolean
        Return True
    End Function
End Class

Public Class ListPropertyDescriptor(Of TColl, TItem)
    Inherits PropertyDescriptor

    Private _index As Integer = 0

    Public Sub New(name As String)
        MyBase.New(name, Nothing)
        Dim indexStr = RegularExpressions.Regex.Match(name, "\d+$").Value
        _index = CInt(indexStr)
    End Sub

    Public Overrides Function CanResetValue(component As Object) As Boolean
        Return False
    End Function

    Public Overrides ReadOnly Property ComponentType As Type
        Get
            Return GetType(TColl)
        End Get
    End Property

    Public Overrides Function GetValue(component As Object) As Object
        Dim coll = DirectCast(component, IList)
        Return coll(_index)
    End Function

    Public Overrides ReadOnly Property IsReadOnly As Boolean
        Get
            Return True
        End Get
    End Property

    Public Overrides ReadOnly Property PropertyType As Type
        Get
            Return GetType(TItem)
        End Get
    End Property

    Public Overrides Sub ResetValue(component As Object)

    End Sub

    Public Overrides Sub SetValue(component As Object, value As Object)

    End Sub

    Public Overrides Function ShouldSerializeValue(component As Object) As Boolean
        Return False
    End Function

End Class

Public Class GlobHelper
    Public Property schema As ltfsindex
    Public Property OnStopFlagInquiry As Func(Of Boolean)
    Private ReadOnly _directoryCache As New Dictionary(Of String, ltfsindex.directory)(StringComparer.OrdinalIgnoreCase)
    Public ReadOnly Property StopFlag As Boolean
        Get
            If OnStopFlagInquiry IsNot Nothing Then
                Return OnStopFlagInquiry.Invoke()
            Else
                Return False
            End If
        End Get
    End Property
    Public Shared Function NormalizePath(p As String) As String
        If String.IsNullOrWhiteSpace(p) Then Return p
        Dim s = p.Replace("/"c, "\"c)
        If s.Length > 3 AndAlso s.EndsWith("\", StringComparison.Ordinal) Then
            s = s.TrimEnd("\"c)
        End If
        Return s
    End Function

    Public Shared Function PathIsUnder(p As String, root As String) As Boolean
        If String.IsNullOrEmpty(p) OrElse String.IsNullOrEmpty(root) Then Return False
        Dim pn = NormalizePath(p)
        Dim rn = NormalizePath(root)
        If pn.Equals(rn, StringComparison.OrdinalIgnoreCase) Then Return True
        If pn.Length <= rn.Length Then Return False
        If pn.StartsWith(rn, StringComparison.OrdinalIgnoreCase) Then
            Return pn(rn.Length) = "\"c
        End If
        Return False
    End Function

    Public Function FindBestRoot(path As String, candidateRoots As List(Of String)) As String
        If candidateRoots Is Nothing OrElse candidateRoots.Count = 0 Then Return Nothing
        Dim best As String = Nothing
        Dim bestLen As Integer = -1
        For Each r In candidateRoots
            If PathIsUnder(path, r) Then
                Dim l = NormalizePath(r).Length
                If l > bestLen Then
                    best = r
                    bestLen = l
                End If
            End If
        Next
        Return best
    End Function

    Public Shared Function GetRelativePathWin(basePath As String, fullPath As String) As String
        If String.IsNullOrWhiteSpace(basePath) OrElse String.IsNullOrWhiteSpace(fullPath) Then Return Nothing
        Dim b As String
        Dim f As String
        Try
            b = NormalizePath(Path.GetFullPath(basePath))
            f = NormalizePath(Path.GetFullPath(fullPath))
        Catch
            Return Nothing
        End Try
        If b.Equals(f, StringComparison.OrdinalIgnoreCase) Then Return String.Empty
        Dim bSeg = b.Split("\"c)
        Dim fSeg = f.Split("\"c)
        Dim i As Integer = 0
        Dim maxI = Math.Min(bSeg.Length, fSeg.Length)
        While i < maxI AndAlso bSeg(i).Equals(fSeg(i), StringComparison.OrdinalIgnoreCase)
            i += 1
        End While
        If i < bSeg.Length AndAlso Not b.EndsWith(":\", StringComparison.Ordinal) AndAlso Not b.StartsWith("\\") Then
            Return If(PathIsUnder(f, b), String.Join("\", fSeg, i, fSeg.Length - i), Nothing)
        End If
        Dim rel = String.Join("\", fSeg, i, fSeg.Length - i)
        Return rel
    End Function

    Public Function EnsureDirectoryChain(rootNode As ltfsindex.directory, baseAbs As String, relPath As String) As ltfsindex.directory
        Dim node = rootNode
        Dim currentAbs = NormalizePath(baseAbs)
        If String.IsNullOrEmpty(relPath) Then Return node

        Dim parts = relPath.Split(New Char() {"\"c, "/"c}, StringSplitOptions.RemoveEmptyEntries)
        For Each directoryName In parts
            If StopFlag Then Exit For
            currentAbs = If(currentAbs.EndsWith("\"), currentAbs & directoryName, currentAbs & "\" & directoryName)
            Dim cached As ltfsindex.directory = Nothing
            If _directoryCache.TryGetValue(currentAbs, cached) Then
                node = cached
            Else
                node = GetOrCreateSubdir(node, directoryName, currentAbs)
                _directoryCache(currentAbs) = node
            End If
        Next
        Return node
    End Function

    Private Function GetOrCreateSubdir(parent As ltfsindex.directory, childName As String, absPath As String) As ltfsindex.directory
        Dim found As ltfsindex.directory = Nothing

        SyncLock parent.contents._directory
            For Each fe As ltfsindex.directory In parent.contents._directory
                If fe.name.Equals(childName, StringComparison.OrdinalIgnoreCase) Then
                    found = fe
                    Exit For
                End If
            Next

            If found Is Nothing Then
                Dim info As DirectoryInfo = Nothing
                Try : info = New DirectoryInfo(absPath)
                Catch : info = Nothing
                End Try

                Dim ct = If(info IsNot Nothing, info.CreationTimeUtc, Now.ToUniversalTime)
                Dim at = If(info IsNot Nothing, info.LastAccessTimeUtc, Now.ToUniversalTime)
                Dim mt = If(info IsNot Nothing, info.LastWriteTimeUtc, Now.ToUniversalTime)
                Dim tsFmt As Func(Of Date, String) =
                    Function(dt) dt.ToString("yyyy-MM-ddTHH:mm:ss.fffffff00Z")

                found = New ltfsindex.directory With {
                    .name = childName,
                    .creationtime = tsFmt(ct),
                    .fileuid = schema.highestfileuid + 1,
                    .accesstime = tsFmt(at),
                    .modifytime = tsFmt(mt),
                    .changetime = tsFmt(mt),
                    .backuptime = tsFmt(Now.ToUniversalTime),
                    .readonly = False
                }
                parent.contents._directory.Add(found)
                Threading.Interlocked.Increment(schema.highestfileuid)
            End If
        End SyncLock

        Return found
    End Function
    Public Class AddFile
        Public Property SourceFullPath As String
        Public Property RelativePath As String
        Public Property SourceInfo As FileInfo
        ' Empty means the directory snapshot confirmed that no sidecar exists;
        ' Nothing means the caller did not have a snapshot and should probe.
        Public Property SourceXattrPath As String
    End Class

    Public Class AddPlan
        Public ReadOnly Dirs As New List(Of String)()
        Public ReadOnly Files As New List(Of AddFile)()
    End Class
End Class

Public Module GlobCollector
    Public Function BuildMatcherFromStrings(patterns As IEnumerable(Of String),
                                              Optional caseSensitive As Boolean = False) As Matcher
        If patterns Is Nothing Then Throw New ArgumentNullException(NameOf(patterns))

        Dim comparison = If(caseSensitive, StringComparison.Ordinal, StringComparison.OrdinalIgnoreCase)
        Dim matcher = New Matcher(comparison)

        Dim hasIncludeRules As Boolean = False
        Dim lineNumber As Integer = 0

        For Each rawLine In patterns
            lineNumber += 1
            If rawLine Is Nothing Then Continue For

            Dim line = rawLine.Trim()
            If line.Length = 0 Then Continue For
            If line.StartsWith("#"c) Then Continue For

            Try
                Dim isExclude As Boolean = False
                Dim pattern As String = line

                ' 处理转义：\!, \#, \\ 前缀
                If line.StartsWith("\"c) AndAlso line.Length > 1 Then
                    Dim nextChar = line(1)
                    If nextChar = "!"c OrElse nextChar = "#"c OrElse nextChar = "\"c Then
                        pattern = line.Substring(1)
                    End If
                ElseIf line.StartsWith("!"c) Then
                    isExclude = True
                    pattern = line.Substring(1).TrimStart()
                    If pattern.Length = 0 Then Continue For
                End If

                If Not IsValidPattern(pattern) Then Continue For

                If isExclude Then
                    matcher.AddExclude(pattern)
                Else
                    matcher.AddInclude(pattern)
                    hasIncludeRules = True
                End If

            Catch
                Continue For
            End Try
        Next

        If Not hasIncludeRules Then
            matcher.AddInclude("**/*")
        End If

        Return matcher
    End Function

    Public Function BuildMatcherFromFile(filePath As String,
                                         Optional encoding As Encoding = Nothing,
                                         Optional caseSensitive As Boolean = False) As Matcher
        If String.IsNullOrWhiteSpace(filePath) Then Throw New ArgumentNullException(NameOf(filePath))
        If Not File.Exists(filePath) Then Throw New FileNotFoundException("Pattern file not found.", filePath)

        Dim enc = If(encoding, Encoding.UTF8)
        Dim lines = File.ReadAllLines(filePath, enc)
        Return BuildMatcherFromStrings(lines, caseSensitive)
    End Function

    Public Function BuildMatcherFromString(patternText As String,
                                           Optional caseSensitive As Boolean = False) As Matcher
        If String.IsNullOrWhiteSpace(patternText) Then Throw New ArgumentNullException(NameOf(patternText))
        Dim lines = patternText.Split({vbCrLf, vbLf, vbCr}, StringSplitOptions.None)
        Return BuildMatcherFromStrings(lines, caseSensitive)
    End Function

    Private Function IsValidPattern(pattern As String) As Boolean
        If String.IsNullOrEmpty(pattern) Then Return False

        Dim invalidChars = {"<"c, ">"c, "|"c, """"c, vbNullChar}
        For Each ch In invalidChars
            If pattern.Contains(ch) Then Return False
        Next

        ' 方括号匹配
        Dim openBrackets = pattern.Count(Function(c) c = "["c)
        Dim closeBrackets = pattern.Count(Function(c) c = "]"c)
        If openBrackets <> closeBrackets Then Return False

        ' 连续 * 超过 2
        If pattern.Contains("***") Then Return False

        Return True
    End Function

    Private Class PendingGlobDirectory
        Public Sub New(directory As DirectoryInfo)
            Me.Directory = directory
        End Sub

        Public ReadOnly Property Directory As DirectoryInfo
    End Class

    Private Function IsNetworkPath(path As String) As Boolean
        If path.StartsWith("\\", StringComparison.Ordinal) AndAlso
           Not path.StartsWith("\\?\", StringComparison.Ordinal) Then
            Return True
        End If

        Try
            Dim root = IO.Path.GetPathRoot(path)
            Return Not String.IsNullOrEmpty(root) AndAlso New DriveInfo(root).DriveType = DriveType.Network
        Catch
            Return False
        End Try
    End Function

    Private Function EnumerateFiles(root As DirectoryInfo, skipSymlink As Boolean) As List(Of FileInfo)
        If skipSymlink Then
            Try
                If root.Attributes.HasFlag(FileAttributes.ReparsePoint) Then Return New List(Of FileInfo)
            Catch
            End Try
        End If

        If IsNetworkPath(root.FullName) Then Return EnumerateNetworkFiles(root, skipSymlink)

        Dim result As New List(Of FileInfo)
        Dim pending As New Stack(Of DirectoryInfo)
        pending.Push(root)
        While pending.Count > 0
            Dim directory = pending.Pop()
            For Each entry As FileSystemInfo In directory.GetFileSystemInfos()
                Dim file = TryCast(entry, FileInfo)
                If file IsNot Nothing Then
                    result.Add(file)
                Else
                    Dim childDirectory = DirectCast(entry, DirectoryInfo)
                    If skipSymlink Then
                        Try
                            If childDirectory.Attributes.HasFlag(FileAttributes.ReparsePoint) Then Continue For
                        Catch
                        End Try
                    End If
                    pending.Push(childDirectory)
                End If
            Next
        End While
        Return result
    End Function

    Private Function EnumerateNetworkFiles(root As DirectoryInfo, skipSymlink As Boolean) As List(Of FileInfo)
        Const WorkerCount As Integer = 16
        Dim queue As New Collections.Concurrent.ConcurrentQueue(Of PendingGlobDirectory)
        Dim files As New Collections.Concurrent.ConcurrentBag(Of FileInfo)
        Dim failures As New Collections.Concurrent.ConcurrentQueue(Of Exception)
        Dim pending As Integer = 1
        Dim finished As Integer = 0
        queue.Enqueue(New PendingGlobDirectory(root))

        Using wake As New Threading.AutoResetEvent(False)
            Dim workers(WorkerCount - 1) As Threading.Tasks.Task
            For i As Integer = 0 To workers.Length - 1
                workers(i) = Threading.Tasks.Task.Run(
                    Sub()
                        While Threading.Volatile.Read(finished) = 0
                            Dim current As PendingGlobDirectory = Nothing
                            If Not queue.TryDequeue(current) Then
                                wake.WaitOne(2)
                                Continue While
                            End If

                            Try
                                For Each entry As FileSystemInfo In current.Directory.GetFileSystemInfos()
                                    Dim file = TryCast(entry, FileInfo)
                                    If file IsNot Nothing Then
                                        files.Add(file)
                                    Else
                                        Dim childDirectory = DirectCast(entry, DirectoryInfo)
                                        Dim skipChild As Boolean = False
                                        If skipSymlink Then
                                            Try
                                                skipChild = childDirectory.Attributes.HasFlag(FileAttributes.ReparsePoint)
                                            Catch
                                            End Try
                                        End If
                                        If Not skipChild Then
                                            Threading.Interlocked.Increment(pending)
                                            queue.Enqueue(New PendingGlobDirectory(childDirectory))
                                            wake.Set()
                                        End If
                                    End If
                                Next
                            Catch ex As Exception
                                failures.Enqueue(ex)
                            Finally
                                If Threading.Interlocked.Decrement(pending) = 0 Then
                                    Threading.Interlocked.Exchange(finished, 1)
                                    wake.Set()
                                End If
                            End Try
                        End While
                    End Sub)
            Next
            Threading.Tasks.Task.WaitAll(workers)
        End Using

        If Not failures.IsEmpty Then Throw New AggregateException(failures)

        Dim result As New List(Of FileInfo)
        For Each file In files
            result.Add(file)
        Next
        Return result
    End Function

    Public Function PlanAdd_ByFullPathInputs(inputs As IEnumerable(Of String),
                                             matcher As Matcher) As GlobHelper.AddPlan
        If inputs Is Nothing Then Throw New ArgumentNullException(NameOf(inputs))
        If matcher Is Nothing Then Throw New ArgumentNullException(NameOf(matcher))

        Dim plan As New GlobHelper.AddPlan()

        Dim seenSource As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        Dim seenRelative As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        Dim dirSet As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        Dim parentDirSet As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)

        Dim normalized = inputs.
            Where(Function(p) Not String.IsNullOrWhiteSpace(p)).
            Select(Function(p) NormalizeFullPath(p)).
            ToList()

        For Each inputPath In normalized
            If Directory.Exists(inputPath) Then
                Dim baseDirForRel = Path.GetDirectoryName(TrailingTrimSeparators(inputPath))
                ' A UNC share root (for example \\server\share) has no parent
                ' according to Path.GetDirectoryName. Treat the selected root
                ' itself as the relative-path base in that case.
                If String.IsNullOrEmpty(baseDirForRel) Then baseDirForRel = inputPath

                Dim enumeratedFiles = EnumerateFiles(New DirectoryInfo(inputPath), My.Settings.LTFSWriter_SkipSymlink)
                Dim knownXattrPaths As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
                For Each candidate As FileInfo In enumeratedFiles
                    If candidate.Extension.Equals(".xattr", StringComparison.OrdinalIgnoreCase) Then
                        knownXattrPaths.Add(candidate.FullName)
                    End If
                Next

                For Each fileInfo As FileInfo In enumeratedFiles
                    Dim fileFull = fileInfo.FullName
                    Dim matchRel = NormalizeRelativeSeparators(GetRelativePathFast(inputPath, fileFull))
                    If String.IsNullOrEmpty(matchRel) OrElse Not matcher.Match(matchRel).HasMatches Then Continue For

                    Dim rel = GetRelativePathFast(baseDirForRel, fileFull)
                    rel = NormalizeRelativeSeparators(rel)

                    If rel.StartsWith(Path.DirectorySeparatorChar) OrElse rel.StartsWith(Path.AltDirectorySeparatorChar) Then
                        rel = rel.Substring(1)
                    End If
                    If String.IsNullOrEmpty(rel) Then Continue For

                    If Not seenRelative.Add(rel) Then Continue For
                    If Not seenSource.Add(fileFull) Then Continue For

                    Dim xattrPath As String = String.Empty
                    Dim xattrCandidate = fileFull & ".xattr"
                    If knownXattrPaths.Contains(xattrCandidate) Then xattrPath = xattrCandidate

                    plan.Files.Add(New GlobHelper.AddFile With {
                        .SourceFullPath = fileFull,
                        .RelativePath = rel,
                        .SourceInfo = fileInfo,
                        .SourceXattrPath = xattrPath
                    })

                    Dim parentRel = Path.GetDirectoryName(rel)
                    If Not String.IsNullOrEmpty(parentRel) Then parentDirSet.Add(parentRel)
                Next

            ElseIf File.Exists(inputPath) Then
                Dim parentDir = Path.GetDirectoryName(inputPath)
                If String.IsNullOrEmpty(parentDir) Then Continue For

                Dim fileInfo As New FileInfo(inputPath)
                Dim relForMatch = Path.GetFileName(inputPath)
                relForMatch = NormalizeRelativeSeparators(relForMatch)

                If matcher.Match(relForMatch).HasMatches Then
                    Dim relDisplay = Path.GetFileName(inputPath)
                    If String.IsNullOrEmpty(relDisplay) Then Continue For

                    If Not seenRelative.Add(relDisplay) Then Continue For
                    If Not seenSource.Add(inputPath) Then Continue For

                    plan.Files.Add(New GlobHelper.AddFile With {
                        .SourceFullPath = inputPath,
                        .RelativePath = relDisplay,
                        .SourceInfo = fileInfo,
                        .SourceXattrPath = Nothing
                    })
                End If
            Else
                Continue For
            End If
        Next

        For Each parentRel In parentDirSet
            AddParentDirsOfRelativePath(parentRel, dirSet)
        Next

        plan.Dirs.AddRange(dirSet.OrderBy(Function(s) s, StringComparer.OrdinalIgnoreCase))
        plan.Files.Sort(Function(a, b) StringComparer.OrdinalIgnoreCase.Compare(a.RelativePath, b.RelativePath))

        Return plan
    End Function

    Private Function NormalizeFullPath(p As String) As String
        Dim full = Path.GetFullPath(p)
        Return TrailingTrimSeparators(full)
    End Function

    Private Function TrailingTrimSeparators(p As String) As String
        If String.IsNullOrEmpty(p) Then Return p
        If Path.GetPathRoot(p).Equals(p, StringComparison.OrdinalIgnoreCase) Then
            Return p
        End If
        Return p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
    End Function

    Private Function GetRelativePathFast(baseDir As String, targetPath As String) As String
        If String.IsNullOrEmpty(baseDir) OrElse String.IsNullOrEmpty(targetPath) Then Return Nothing

        Dim baseFixed = TrailingTrimSeparators(NormalizeFullPath(baseDir))
        Dim targetFixed = TrailingTrimSeparators(NormalizeFullPath(targetPath))
        If baseFixed.Equals(targetFixed, StringComparison.OrdinalIgnoreCase) Then Return String.Empty

        Dim prefix = If(baseFixed.EndsWith("\", StringComparison.Ordinal), baseFixed, baseFixed & "\")
        If targetFixed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) Then
            Return targetFixed.Substring(prefix.Length)
        End If

        Return GetRelativePathWin(baseDir, targetPath)
    End Function

    Public Function GetRelativePathWin(baseDir As String, targetPath As String) As String
        If String.IsNullOrEmpty(baseDir) Then Throw New ArgumentNullException(NameOf(baseDir))
        If String.IsNullOrEmpty(targetPath) Then Throw New ArgumentNullException(NameOf(targetPath))

        Dim baseFixed = baseDir
        If Not baseFixed.EndsWith(Path.DirectorySeparatorChar) AndAlso Not baseFixed.EndsWith(Path.AltDirectorySeparatorChar) Then
            baseFixed &= Path.DirectorySeparatorChar
        End If

        Dim baseUri As New Uri(PathToUri(baseFixed))
        Dim targetUri As New Uri(PathToUri(targetPath))

        Dim relUri = baseUri.MakeRelativeUri(targetUri)
        Dim rel = Uri.UnescapeDataString(relUri.ToString())

        rel = rel.Replace("/"c, Path.DirectorySeparatorChar)
        Return rel
    End Function

    Private Function PathToUri(p As String) As String
        Dim u = New Uri(p, UriKind.Absolute)
        Return u.AbsoluteUri
    End Function

    Private Function NormalizeRelativeSeparators(rel As String) As String
        If String.IsNullOrEmpty(rel) Then Return rel
        Return rel.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
    End Function

    Private Sub AddParentDirsOfRelativePath(relativePath As String, dirSet As HashSet(Of String))
        If String.IsNullOrEmpty(relativePath) Then Exit Sub

        Dim parts = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).
                         Where(Function(s) Not String.IsNullOrEmpty(s)).
                         ToArray()
        If parts.Length = 0 Then Exit Sub

        Dim cur As String = parts(0)
        dirSet.Add(cur)
        For i = 1 To parts.Length - 1
            cur = cur & Path.DirectorySeparatorChar & parts(i)
            dirSet.Add(cur)
        Next
    End Sub
End Module

Public NotInheritable Class DisplayHelper
    Private Const LogicalDpi As Single = 96.0F
    Private Shared _font As Drawing.Font
    Public Shared Property DisplayFont As Drawing.Font
        Get
            If _font Is Nothing Then
                _font = New Drawing.Font("SimSun", 9.0F, FontStyle.Regular, GraphicsUnit.Point)
            End If
            Return _font
        End Get
        Set(value As Drawing.Font)
            If value Is Nothing Then Throw New ArgumentNullException(NameOf(value))

            Dim pointSize As Single = value.SizeInPoints
            If value.Unit = GraphicsUnit.Pixel Then
                ' Legacy configurations stored the 96-DPI design font as 12 px.
                ' Interpret that value as 9 pt on every monitor, rather than
                ' converting it through the DPI of the monitor used at startup.
                pointSize = value.Size * 72.0F / LogicalDpi
            End If

            Dim normalized = New Drawing.Font(value.FontFamily, pointSize, value.Style, GraphicsUnit.Point)
            If _font IsNot Nothing Then _font.Dispose()
            _font = normalized
        End Set
    End Property
    Public Shared Sub ApplyApplicationFont(frm As Form)
        If frm Is Nothing Then Throw New ArgumentNullException(NameOf(frm))
        ' Keep the user-selectable base font. DPI scaling itself is owned by
        ' WinForms' PerMonitorV2/AutoScaleMode pipeline.
        frm.Font = DisplayFont
    End Sub

    ''' <summary>
    ''' Applies the 96-DPI logical layout contract to a form built in code.
    ''' Call this after the form's child controls have been created.
    ''' </summary>
    Public Shared Sub ApplyDynamicFormLayout(frm As Form)
        If frm Is Nothing Then Throw New ArgumentNullException(NameOf(frm))
        frm.Font = DisplayFont
        frm.AutoScaleDimensions = New SizeF(LogicalDpi, LogicalDpi)

        ' Force WinForms to finish accessibility/handle initialization before
        ' AutoScaleMode.Set reads the current DPI state.  Removing this access
        ' can distort dynamically-created forms on some high-DPI systems.
        GC.KeepAlive(frm.AccessibilityObject)

        frm.AutoScaleMode = AutoScaleMode.Dpi
    End Sub

    Public Shared Function ShowInputDialog(Prompt As String, Title As String, ByRef Response As UShort) As DialogResult
        Dim resp As String = Response.ToString()
        Dim result As DialogResult = ShowInputDialog(Prompt, Title, resp)
        If Not UShort.TryParse(resp, Response) Then Return DialogResult.Cancel
        Return result
    End Function
    Public Shared Function ShowInputDialog(Prompt As String, Title As String, ByRef Response As Integer) As DialogResult
        Dim resp As String = Response.ToString()
        Dim result As DialogResult = ShowInputDialog(Prompt, Title, resp)
        If Not Integer.TryParse(resp, Response) Then Return DialogResult.Cancel
        Return result
    End Function
    Public Shared Function ShowInputDialog(Prompt As String, Title As String, ByRef Response As Long) As DialogResult
        Dim resp As String = Response.ToString()
        Dim result As DialogResult = ShowInputDialog(Prompt, Title, resp)
        If Not Long.TryParse(resp, Response) Then Return DialogResult.Cancel
        Return result
    End Function
    Public Shared Function ShowInputDialog(Prompt As String, Title As String, ByRef Response As ULong) As DialogResult
        Dim resp As String = Response.ToString()
        Dim result As DialogResult = ShowInputDialog(Prompt, Title, resp)
        If Not ULong.TryParse(resp, Response) Then Return DialogResult.Cancel
        Return result
    End Function
    Public Shared Function ShowInputDialog(Prompt As String, Title As String, ByRef Response As Double) As DialogResult
        Dim resp As String = Response.ToString()
        Dim result As DialogResult = ShowInputDialog(Prompt, Title, resp)
        If Not Double.TryParse(resp, Response) Then Return DialogResult.Cancel
        Return result
    End Function
    Public Shared Function ShowInputDialog(Prompt As String,
                                            Title As String,
                                            ByRef Response As String,
                                            Optional MaskInput As Boolean = False) As DialogResult
        Dim size As Size = New Size(200, 90)
        Dim inputDialog As Form = New Form()
        inputDialog.StartPosition = FormStartPosition.CenterParent
        inputDialog.Font = DisplayFont
        inputDialog.FormBorderStyle = FormBorderStyle.FixedDialog
        inputDialog.MinimizeBox = False
        inputDialog.MaximizeBox = False
        inputDialog.ClientSize = size
        inputDialog.Text = Title
        Dim promptLabel As Label = New Label()
        promptLabel.Location = New Point(5, 5)
        promptLabel.Text = Prompt
        promptLabel.AutoSize = True
        inputDialog.Controls.Add(promptLabel)
        Dim textBox As TextBox = New TextBox()
        textBox.Size = New Size(size.Width - 10, 23)
        textBox.Location = New Point(5, 25)
        textBox.Anchor = AnchorStyles.Top Or AnchorStyles.Left Or AnchorStyles.Right
        textBox.Text = Response
        textBox.UseSystemPasswordChar = MaskInput
        inputDialog.Controls.Add(textBox)
        Dim okButton As Button = New Button()
        okButton.DialogResult = DialogResult.OK
        okButton.Name = "okButton"
        okButton.Size = New Size(75, 23)
        okButton.Text = $"&OK"
        okButton.Location = New Point(size.Width - 80 - 80, 59)
        okButton.Anchor = AnchorStyles.Right Or AnchorStyles.Bottom
        inputDialog.Controls.Add(okButton)
        Dim cancelButton As Button = New Button()
        cancelButton.DialogResult = DialogResult.Cancel
        cancelButton.Name = "cancelButton"
        cancelButton.Size = New Size(75, 23)
        cancelButton.Text = $"&Cancel"
        cancelButton.Location = New Point(size.Width - 80, 59)
        cancelButton.Anchor = AnchorStyles.Right Or AnchorStyles.Bottom
        inputDialog.Controls.Add(cancelButton)
        inputDialog.AcceptButton = okButton
        inputDialog.CancelButton = cancelButton
        ApplyDynamicFormLayout(inputDialog)
        Dim result As DialogResult = inputDialog.ShowDialog()
        If result = DialogResult.OK Then Response = textBox.Text
        Return result
    End Function
End Class

''' <summary>
''' ListView with DPI-aware column widths.
'''
''' WinForms scales the control itself, but column widths are native ListView
''' state and are not part of the regular child-control scaling pass. The
''' framework supplies the scale factor here, so no process-wide DPI is read
''' or cached by the application.
''' </summary>
Public Class DpiAwareListView
    Inherits ListView

    Public Sub New()
        MyBase.New()
        DoubleBuffered = True
        VirtualMode = True
    End Sub

    Protected Overrides Sub ScaleControl(factor As SizeF,
                                         specified As BoundsSpecified)
        MyBase.ScaleControl(factor, specified)

        If factor.Width = 1.0F Then Return

        For Each column As ColumnHeader In Columns
            If column.Width > 0 Then
                column.Width = CInt(Math.Round(column.Width * factor.Width))
            End If
        Next
    End Sub
End Class

Public Class DpiAwareTreeView
    Inherits TreeViewEx

    Protected Overrides ReadOnly Property UsePartialCheckboxes As Boolean
        Get
            Return False
        End Get
    End Property

    Public Sub New()
        MyBase.New()
        HideSelection = False
    End Sub
End Class

Public Class ErrRateHelper
    Public Shared Function MixColor(col1 As Color, col2 As Color, val As Double, min As Double, max As Double) As Color
        If val <= min Then Return col1
        If val >= max Then Return col2
        Dim ratio As Double = (val - min) / (max - min)
        Return Color.FromArgb(CInt(col1.R * (1 - ratio) + col2.R * ratio), CInt(col1.G * (1 - ratio) + col2.G * ratio), CInt(col1.B * (1 - ratio) + col2.B * ratio))
    End Function
    Public Shared Function GetColor(segvalue As String, Optional ByVal colorCoeff As Double = 1) As Color
        Static col1 As Color = Color.FromArgb(CInt(101 * colorCoeff), CInt(225 * colorCoeff), CInt(111 * colorCoeff))
        Static col2 As Color = Color.FromArgb(CInt(237 * colorCoeff), CInt(208 * colorCoeff), CInt(120 * colorCoeff))
        Static col3 As Color = Color.FromArgb(CInt(251 * colorCoeff), CInt(145 * colorCoeff), CInt(85 * colorCoeff))
        Static col4 As Color = Color.FromArgb(CInt(255 * colorCoeff), CInt(71 * colorCoeff), CInt(73 * colorCoeff))

        If segvalue.Length > 0 Then
            Dim errratelog As Double = 0
            If segvalue.Length = 5 Then
                Double.TryParse(segvalue, errratelog)
            End If

            If segvalue = "-Inf" OrElse errratelog <= -6 Then
                Return col1
            ElseIf errratelog <= -5 Then
                Return MixColor(col1, col2, errratelog, -6, -5)
            ElseIf errratelog <= -4 Then
                Return MixColor(col2, col3, errratelog, -5, -4)
            ElseIf errratelog <= -3.5 Then
                Return MixColor(col3, col4, errratelog, -4, -3.5)
            ElseIf errratelog > -3.5 Then
                Return col4
            End If
        Else
        End If
    End Function
End Class

Partial Public Class ApplicationWheels
    Public Shared ReadOnly Property ApplicationInfo As String
        Get
            Return $"{My.Application.Info.ProductName} {My.Application.Info.Version.ToString(3)} Build {Build}{My.Settings.Application_License}"
        End Get
    End Property
    Public Shared Function TryExecute(ByVal command As Func(Of Byte()), Optional ByVal AutoRetryCount As Integer = 0) As Boolean
        Dim succ As Boolean = False
        Dim operationId As String = $"scsi-{Global.System.Guid.NewGuid().ToString("N").Substring(0, 8)}"
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(ApplicationWheels))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "SCSI")
                Using operationScope As IDisposable = LogContext.PushProperty("OperationId", operationId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Command")
                        Log.Debug("Retryable SCSI operation started. AutoRetryCount={AutoRetryCount}.", AutoRetryCount)
                    End Using
                End Using
            End Using
        End Using
        While Not succ
            Dim sense() As Byte
            Try
                sense = command()
            Catch ex As Exception
                Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(ApplicationWheels))
                    Using categoryScope As IDisposable = LogContext.PushProperty("Category", "SCSI")
                        Using operationScope As IDisposable = LogContext.PushProperty("OperationId", operationId)
                            Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Error")
                                Log.Error(ex, "Retryable SCSI operation threw an exception.")
                            End Using
                        End Using
                    End Using
                End Using
                Dim ActiveFrm = GetActiveWindow()
                Dim dResult As DialogResult
                ActiveFrm.Invoke(Sub() dResult = MessageBox.Show(ActiveFrm, $"{My.Resources.ResText_Error}{vbCrLf}{ex.ToString}", My.Resources.ResText_Warning, MessageBoxButtons.AbortRetryIgnore))
                Select Case dResult
                    Case DialogResult.Abort
                        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(ApplicationWheels))
                            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "SCSI")
                                Using operationScope As IDisposable = LogContext.PushProperty("OperationId", operationId)
                                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Cancellation")
                                        Log.Warning("Retryable SCSI operation aborted by the user.")
                                    End Using
                                End Using
                            End Using
                        End Using
                        Throw ex
                    Case DialogResult.Retry
                        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(ApplicationWheels))
                            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "SCSI")
                                Using operationScope As IDisposable = LogContext.PushProperty("OperationId", operationId)
                                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Retry")
                                        Log.Warning("Retryable SCSI operation will be retried after an exception.")
                                    End Using
                                End Using
                            End Using
                        End Using
                        succ = False
                    Case DialogResult.Ignore
                        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(ApplicationWheels))
                            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "SCSI")
                                Using operationScope As IDisposable = LogContext.PushProperty("OperationId", operationId)
                                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Retry")
                                        Log.Warning("Retryable SCSI operation ignored by the user.")
                                    End Using
                                End Using
                            End Using
                        End Using
                        succ = True
                        Exit While
                End Select
                Continue While
            End Try
            If ((sense(2) >> 6) And &H1) = 1 Then
                If (sense(2) And &HF) = 13 Then
                    succ = True
                Else
                    succ = True
                    Exit While
                End If
            ElseIf (sense(2) And &HF) <> 0 Then
                Try
                    Throw New Exception("SCSI sense error")
                Catch ex As Exception
                    If AutoRetryCount > 0 Then
                        AutoRetryCount -= 1
                        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(ApplicationWheels))
                            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "SCSI")
                                Using operationScope As IDisposable = LogContext.PushProperty("OperationId", operationId)
                                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Retry")
                                        Log.Warning("SCSI sense error will be retried. RemainingRetries={RemainingRetries}.", AutoRetryCount)
                                    End Using
                                End Using
                            End Using
                        End Using
                        succ = False
                    Else
                        Dim ActiveFrm = GetActiveWindow()
                        Dim dResult As DialogResult
                        ActiveFrm.Invoke(Sub() dResult = MessageBox.Show(ActiveFrm, $"{My.Resources.ResText_RestoreErr}{vbCrLf}{TapeUtils.ParseSenseData(sense)}{vbCrLf}{vbCrLf}sense{vbCrLf}{TapeUtils.Byte2Hex(sense, True)}{vbCrLf}{ex.StackTrace}", My.Resources.ResText_Warning, MessageBoxButtons.AbortRetryIgnore))
                        Select Case dResult
                            Case DialogResult.Abort
                                Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(ApplicationWheels))
                                    Using categoryScope As IDisposable = LogContext.PushProperty("Category", "SCSI")
                                        Using operationScope As IDisposable = LogContext.PushProperty("OperationId", operationId)
                                            Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Cancellation")
                                                Log.Warning("SCSI sense error operation aborted by the user.")
                                            End Using
                                        End Using
                                    End Using
                                End Using
                                Throw New Exception(TapeUtils.ParseSenseData(sense))
                            Case DialogResult.Retry
                                Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(ApplicationWheels))
                                    Using categoryScope As IDisposable = LogContext.PushProperty("Category", "SCSI")
                                        Using operationScope As IDisposable = LogContext.PushProperty("OperationId", operationId)
                                            Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Retry")
                                                Log.Warning("SCSI sense error operation will be retried after user confirmation.")
                                            End Using
                                        End Using
                                    End Using
                                End Using
                                succ = False
                            Case DialogResult.Ignore
                                Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(ApplicationWheels))
                                    Using categoryScope As IDisposable = LogContext.PushProperty("Category", "SCSI")
                                        Using operationScope As IDisposable = LogContext.PushProperty("OperationId", operationId)
                                            Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Retry")
                                                Log.Warning("SCSI sense error operation was ignored by the user.")
                                            End Using
                                        End Using
                                    End Using
                                End Using
                                succ = True
                                Exit While
                        End Select
                    End If
                End Try
            Else
                succ = True
            End If
        End While
        Return succ
    End Function
    Public Shared Function GetActiveWindow() As Form
        Dim ActiveFrm As Form = Nothing
        If Application.OpenForms.Count > 0 Then Application.OpenForms(0).Invoke(
            Sub()
                ActiveFrm = Application.OpenForms.Cast(Of Form)().FirstOrDefault(Function(f) f.Focused)
            End Sub)
        If ActiveFrm Is Nothing Then ActiveFrm = If(Application.OpenForms.Count > 0, Application.OpenForms(0), New Form With {.TopMost = True})
        Return ActiveFrm
    End Function
End Class

Public NotInheritable Class FileDropHandler
    Implements IMessageFilter, IDisposable


    Private Const WM_COPYGLOBALDATA As UInteger = 73

    Private Const WM_COPYDATA As UInteger = 74

    Private Const WM_DROPFILES As UInteger = 563

    Private _ContainerControl As Control

    Private _DisposeControl As Boolean

    Public ReadOnly Property ContainerControl As Control
        Get
            Return _ContainerControl
        End Get
    End Property

    Public Sub New(ByVal containerControl As Control)
        Me.New(containerControl, False)

    End Sub

    Public Sub New(ByVal containerControl As Control, ByVal releaseControl As Boolean)
        Try
            _ContainerControl = containerControl
        Catch ex As Exception
            Throw New ArgumentNullException("control", "control is null.")
        End Try
        If containerControl.IsDisposed Then
            Throw New ObjectDisposedException("control")
        End If

        _DisposeControl = releaseControl
        NativeMethods.ChangeWindowMessageFilterEx(containerControl.Handle, WM_DROPFILES, 1UI).ThrowIfFailed("Allow WM_DROPFILES failed.")
        NativeMethods.ChangeWindowMessageFilterEx(containerControl.Handle, WM_COPYGLOBALDATA, 1UI).ThrowIfFailed("Allow WM_COPYGLOBALDATA failed.")
        NativeMethods.ChangeWindowMessageFilterEx(containerControl.Handle, WM_COPYDATA, 1UI).ThrowIfFailed("Allow WM_COPYDATA failed.")

        NativeMethods.DragAcceptFiles(containerControl.Handle, True).ThrowIfFailed("Enable file drop failed.")
        Application.AddMessageFilter(Me)
    End Sub

    Public Function PreFilterMessage(ByRef m As Message) As Boolean Implements IMessageFilter.PreFilterMessage
        If ((_ContainerControl Is Nothing) OrElse _ContainerControl.IsDisposed) Then
            Return False
        End If

        If _ContainerControl.AllowDrop Then
            _ContainerControl.AllowDrop = False
            Return False
        End If
        If (m.Msg = WM_DROPFILES) Then
            Dim handle = m.WParam
            Dim fileNames() As String
            Try
                fileNames = NativeMethods.GetDroppedFiles(handle)
            Finally
                Dim finishResult As NativeCallResult = NativeMethods.DragFinish(handle)
                If Not finishResult.Succeeded Then
                    Log.Warning("Finishing dropped file handle failed. NativeError={NativeError}.", finishResult.Win32Error)
                End If
            End Try
            _ContainerControl.AllowDrop = True
            _ContainerControl.DoDragDrop(fileNames, DragDropEffects.All)
            _ContainerControl.AllowDrop = False
            Return True
        End If

        Return False
    End Function

    Public Sub Dispose() Implements IDisposable.Dispose
        If (_ContainerControl Is Nothing) Then
            If (_DisposeControl AndAlso Not _ContainerControl.IsDisposed) Then
                _ContainerControl.Dispose()
            End If

            Application.RemoveMessageFilter(Me)
            _ContainerControl = Nothing
        End If

    End Sub
End Class


