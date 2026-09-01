Imports System.Buffers
Imports System.ComponentModel
Imports System.Diagnostics
Imports System.IO
Imports System.IO.Pipelines
Imports System.Runtime
Imports System.Runtime.InteropServices
Imports System.Runtime.CompilerServices
Imports System.Security.Cryptography
Imports System.Text
Imports System.Threading
Imports Fsp.Interop
Imports Microsoft.WindowsAPICodePack.Dialogs
Imports Microsoft.Extensions.FileSystemGlobbing
Imports Serilog
Imports Serilog.Context
Imports ZstdSharp

Public Class LTFSWriter
    Private Const WriterTreePageSize As Integer = 1024
    Private _lastSelectedFolder As String = String.Empty
    Private _deviceLease As SCSIDeviceLockManager.WriterLease

    Public Sub New(processSessionId As String)
        Me.New()
        If Not String.IsNullOrWhiteSpace(processSessionId) Then
            _logSessionId = processSessionId
        End If
    End Sub

    Private Function SelectWriterFolder(Optional initialDirectory As String = Nothing) As String
        Dim startDirectory As String = initialDirectory
        If String.IsNullOrWhiteSpace(startDirectory) Then startDirectory = _lastSelectedFolder

        Dim selectedPath As String = FileDialogHelper.SelectFolder(startDirectory)
        If Not String.IsNullOrEmpty(selectedPath) Then _lastSelectedFolder = selectedPath
        Return selectedPath
    End Function

    <Category("LTFSWriter")>
    Public Property TapeDrive As String = ""
    <Category("LTFSWriter")>
    Public Property schema As ltfsindex
    <Category("LTFSWriter")>
    Public Property plabel As New ltfslabel With {.blocksize = 524288}
    <Category("LTFSWriter")>
    Public Property Modified As Boolean = False
    <Category("LTFSWriter")>
    Public Property OfflineMode As Boolean = False
    <Category("LTFSWriter")>
    Public Property IndexPartition As Byte = 0
    <Category("LTFSWriter")>
    Public Property DataPartition As Byte = 1
    Private _EncryptionKey As Byte()
    <Category("LTFSWriter")>
    Public Property EncryptionKey As Byte()
        Get
            Return _EncryptionKey
        End Get
        Set(value As Byte())
            _EncryptionKey = value
            If value IsNot Nothing AndAlso value.Length = 32 Then
                IO.File.WriteAllText(My.Settings.encKeyFile, BitConverter.ToString(value).Replace("-", "").ToUpper)
            Else
                If IO.File.Exists(My.Settings.encKeyFile) Then
                    IO.File.Delete(My.Settings.encKeyFile)
                End If
            End If
        End Set
    End Property
    Public Function GetPartitionNumber(partition As ltfslabel.PartitionLabel) As Byte
        If plabel Is Nothing Then Return partition
        If partition = plabel.partitions.index Then
            Return IndexPartition
        Else
            Return DataPartition
        End If
    End Function

    <Category("LTFSWriter")>
    Public Property IndexWriteInterval As Long
        Get
            Return My.Settings.LTFSWriter_IndexWriteInterval
        End Get
        Set(value As Long)
            value = Math.Max(0, value)
            My.Settings.LTFSWriter_IndexWriteInterval = value
            If value = 0 Then
                索引间隔36GiBToolStripMenuItem.Text = My.Resources.ResText_NoIndex
            Else
                索引间隔36GiBToolStripMenuItem.Text = $"{My.Resources.ResText_IndexInterval}{IOManager.FormatSize(value)}"
            End If
            My.Settings.Save()
        End Set
    End Property

    Private _TotalBytesUnindexed As Long
    <Category("LTFSWriter")>
    Public Property IndexLastUpdateTime As Date
    <Category("LTFSWriter")>
    Public Property TotalBytesUnindexed As Long
        Set(value As Long)
            _TotalBytesUnindexed = value
            If Not 更新数据区索引ToolStripMenuItem.Enabled AndAlso
                value <> 0 AndAlso schema IsNot Nothing AndAlso
                schema.location.partition = ltfsindex.PartitionLabel.b Then Invoke(Sub() 更新数据区索引ToolStripMenuItem.Enabled = True)
        End Set
        Get
            Return _TotalBytesUnindexed
        End Get
    End Property
    <Category("LTFSWriter")>
    Public Property TotalBytesProcessed As Long = 0
    <Category("LTFSWriter")>
    Public Property TotalFilesProcessed As Long = 0
    <Category("LTFSWriter")>
    Public Property CurrentBytesProcessed As Long = 0
    <Category("LTFSWriter")>
    Public Property CurrentFilesProcessed As Long = 0
    <Category("LTFSWriter")>
    Public Property CurrentHeight As Long = 0
    <Category("LTFSWriter")>
    Public ReadOnly Property GetPos As TapeUtils.PositionData
        Get
            Return TapeUtils.ReadPosition(driveHandle)
        End Get
    End Property
    <Category("LTFSWriter")>
    Public Property ExtraPartitionCount As Byte = 0
    <Category("LTFSWriter")>
    Public Property CapReduceCount As Long = 0
    <Category("LTFSWriter")>
    Public Property CapacityRefreshInterval As Integer
        Get
            Return My.Settings.LTFSWriter_CapacityRefreshInterval
        End Get
        Set(value As Integer)
            value = Math.Max(0, value)
            My.Settings.LTFSWriter_CapacityRefreshInterval = value
            If value = 0 Then
                容量刷新间隔30sToolStripMenuItem.Text = My.Resources.ResText_CRDisabled
            Else
                容量刷新间隔30sToolStripMenuItem.Text = $"{My.Resources.ResText_CRIntv}{value}s"
            End If
        End Set
    End Property
    <Category("LTFSWriter")>
    Public Property SpeedLimit As Integer
        Set(value As Integer)
            value = Math.Max(0, value)
            My.Settings.LTFSWriter_SpeedLimit = value
            If My.Settings.LTFSWriter_SpeedLimit = 0 Then
                限速不限制ToolStripMenuItem.Text = My.Resources.ResText_NoSLim
            Else
                限速不限制ToolStripMenuItem.Text = $"{My.Resources.ResText_SLim}{My.Settings.LTFSWriter_SpeedLimit} MiB/s"
            End If
        End Set
        Get
            Return My.Settings.LTFSWriter_SpeedLimit
        End Get
    End Property
    <Category("LTFSWriter")>
    Public Property SpeedLimitLastTriggerTime As Date = Now
    <Category("LTFSWriter")>
    Public Property CheckCount As Integer = 0
    <Category("LTFSWriter")>
    Public Property CheckCycle As Integer = 10
    <Category("LTFSWriter")>
    Public Property CleanCycle As Integer
        Set(value As Integer)
            value = Math.Max(0, value)
            If value = 0 Then
                重装带前清洁次数3ToolStripMenuItem.Text = My.Resources.ResText_RBCoff
            Else
                重装带前清洁次数3ToolStripMenuItem.Text = $"{My.Resources.ResText_RBC}{value}"
            End If
            My.Settings.LTFSWriter_CleanCycle = value
            My.Settings.Save()
        End Set
        Get
            Return My.Settings.LTFSWriter_CleanCycle
        End Get
    End Property
    <Category("LTFSWriter")>
    Public Property HashOnWrite As Boolean
        Get
            Return 计算校验ToolStripMenuItem.Checked
        End Get
        Set(value As Boolean)
            计算校验ToolStripMenuItem.Checked = value

        End Set
    End Property

    <Category("LTFSWriter")>
    Public Property AllowOperation As Boolean = True
    Public OperationLock As New Object
    <Category("LTFSWriter")>
    Public Property Barcode As String = ""
    <Category("LTFSWriter")>
    Public Property StopFlag As Boolean
        Get
            Return Volatile.Read(_stopFlagValue) <> 0
        End Get
        Set(value As Boolean)
            Dim previous = Interlocked.Exchange(_stopFlagValue, If(value, 1, 0))
            If value AndAlso previous = 0 Then
                Dim fastProviderSnapshot = Volatile.Read(_activeFastReaderProvider)
                If fastProviderSnapshot IsNot Nothing Then
                    Try
                        fastProviderSnapshot.Cancel()
                    Catch
                    End Try
                End If
            End If
        End Set
    End Property
    <Category("LTFSWriter")>
    Public Property Pause As Boolean = False
    <Category("LTFSWriter")>
    Public Property Flush As Boolean
        Get
            Return Threading.Volatile.Read(_flushState) <> 0
        End Get
        Set(value As Boolean)
            Threading.Interlocked.Exchange(_flushState, If(value, 1, 0))
        End Set
    End Property
    Private _flushState As Integer
    <Category("LTFSWriter")>
    Public Property ForceFlush As Boolean = False
    <Category("LTFSWriter")>
    Public Property Clean As Boolean
        Get
            Return Threading.Volatile.Read(_cleanState) <> 0
        End Get
        Set(value As Boolean)
            Threading.Interlocked.Exchange(_cleanState, If(value, 1, 0))
        End Set
    End Property
    Private _cleanState As Integer
    <Category("LTFSWriter")>
    Public Property Clean_last As Date = Now
    <Category("LTFSWriter")>
    Public Property DisablePartition As Boolean
        Get
            Return My.Settings.LTFSWriter_DisablePartition
        End Get
        Set(value As Boolean)
            My.Settings.LTFSWriter_DisablePartition = value
            My.Settings.Save()
            TapeUtils.AllowPartition = Not DisablePartition
        End Set
    End Property
    <Category("LTFSWriter")>
    Public Property Session_Start_Time As Date = Now
    Private _logSessionId As String = $"writer-{Guid.NewGuid().ToString("N").Substring(0, 8)}"
    Private ReadOnly _messageStateLock As New Object()
    Private ReadOnly _pendingQueueLock As New Object()
    Private ReadOnly _pendingByDirectory As New Dictionary(Of ltfsindex.directory, Dictionary(Of String, FileRecord))(DirectoryRecordComparer.Instance)
    Private _pendingTreeCountByDirectory As New Dictionary(Of ltfsindex.directory, Long)(DirectoryRecordComparer.Instance)
    Private _pendingSize As Long
    Private _activeAddFileLookup As Dictionary(Of ltfsindex.directory, Dictionary(Of String, List(Of ltfsindex.file)))
    Private _activeAddFileLookupCalls As Dictionary(Of ltfsindex.directory, Integer)
    Private _lastMessage As String = String.Empty
    Private Const SearchProgressMaximum As Integer = 10000
    Private _searchProgressActive As Integer
    Private Const DirectorySortProgressMaximum As Integer = 10000
    Private _directorySortProgressActive As Integer
    Private _listDisplayRefreshPending As Integer
    Private _listDisplayRefreshVersion As Long
    Private ReadOnly _writeCompletionLock As New Object()
    Private ReadOnly _completedWriteFiles As New HashSet(Of ltfsindex.file)
    Private ReadOnly _validationColorCacheLock As New Object()
    Private ReadOnly _validationColorCache As New Dictionary(Of Long, Dictionary(Of String, Color))
    Private _validationColorCacheSchema As ltfsindex
    <Category("LTFSWriter")>
    Public Property SilentMode As Boolean = False
    <Category("LTFSWriter")>
    Public Property SilentAutoEject As Boolean = False
    <Category("LTFSWriter")>
    Public Property BufferedBytes As Long = 0
    Private ddelta, fdelta, rwhdelta, rwtdelta As Long
    <Category("LTFSWriter")>
    Public Property SMaxNum As Integer = 600
    <Category("LTFSWriter")>
    Public Property PMaxNum As Integer = 3600 * 6
    <Category("LTFSWriter")>
    Public Property SpeedHistory As List(Of Double) = New Double(PMaxNum) {}.ToList()
    <Category("LTFSWriter")>
    Public Property ErrRateLog As List(Of Double) = New Double(PMaxNum) {}.ToList()
    <Category("LTFSWriter")>
    Public Property FileRateHistory As List(Of Double) = New Double(PMaxNum) {}.ToList()

    Public FileDroper As FileDropHandler
    Public Event LTFSLoaded()
    Public Event WriteFinished()
    Public Event TapeEjected()

    <Category("LTFSWriter")>
    Public Property MyClipBoard As New LTFSClipBoard With {.ContentChanged =
        Sub()
            Me.Invoke(Sub()
                          粘贴选中ToolStripMenuItem.Visible = Not MyClipBoard.IsEmpty
                          粘贴选中ToolStripMenuItem1.Visible = Not MyClipBoard.IsEmpty
                          If 粘贴选中ToolStripMenuItem.Visible Then TriggerTreeView1Event()
                      End Sub)
        End Sub}
    <TypeConverter(GetType(ExpandableObjectConverter))>
    Public Class LTFSClipBoard
        <TypeConverter(GetType(ListTypeDescriptor(Of List(Of ltfsindex.directory), ltfsindex.directory)))>
        Public Property Directory As New List(Of ltfsindex.directory)
        <TypeConverter(GetType(ListTypeDescriptor(Of List(Of ltfsindex.file), ltfsindex.file)))>
        Public Property File As New List(Of ltfsindex.file)
        Public ContentChanged As Action
        Public ReadOnly Property IsEmpty As Boolean
            Get
                Return Directory.Count + File.Count = 0
            End Get
        End Property
        Public Sub Add(content As ltfsindex.file)
            File.Add(content)
            ContentChanged()
        End Sub
        Public Sub Add(content As IEnumerable(Of ltfsindex.file))
            File.AddRange(content)
            ContentChanged()
        End Sub
        Public Sub Add(content As ltfsindex.directory)
            Directory.Add(content)
            ContentChanged()
        End Sub
        Public Sub Add(content As IEnumerable(Of ltfsindex.directory))
            Directory.AddRange(content)
            ContentChanged()
        End Sub
        Public Sub Clear()
            Directory.Clear()
            File.Clear()
            ContentChanged()
        End Sub
    End Class

    Public Sub Load_Settings()

        覆盖已有文件ToolStripMenuItem.Checked = My.Settings.LTFSWriter_OverwriteExist
        跳过符号链接ToolStripMenuItem.Checked = My.Settings.LTFSWriter_SkipSymlink
        显示文件数ToolStripMenuItem.Checked = My.Settings.LTFSWriter_ShowFileCount
        增强Tar元数据ToolStripMenuItem.Checked = My.Settings.LTFSWriter_EnrichTarMetadata
        Select Case My.Settings.LTFSWriter_OnWriteFinished
            Case 0
                WA0ToolStripMenuItem.Checked = True
                WA1ToolStripMenuItem.Checked = False
                WA2ToolStripMenuItem.Checked = False
                WA3ToolStripMenuItem.Checked = False
            Case 1
                WA0ToolStripMenuItem.Checked = False
                WA1ToolStripMenuItem.Checked = True
                WA2ToolStripMenuItem.Checked = False
                WA3ToolStripMenuItem.Checked = False
            Case 2
                WA0ToolStripMenuItem.Checked = False
                WA1ToolStripMenuItem.Checked = False
                WA2ToolStripMenuItem.Checked = True
                WA3ToolStripMenuItem.Checked = False
            Case 3
                WA0ToolStripMenuItem.Checked = False
                WA1ToolStripMenuItem.Checked = False
                WA2ToolStripMenuItem.Checked = False
                WA3ToolStripMenuItem.Checked = True
        End Select
        APToolStripMenuItem.Checked = My.Settings.LTFSWriter_AutoFlush
        启用日志记录ToolStripMenuItem.Checked = My.Settings.LTFSWriter_LogEnabled
        总是更新数据区索引ToolStripMenuItem.Checked = My.Settings.LTFSWriter_ForceIndex
        计算校验ToolStripMenuItem.Checked = My.Settings.LTFSWriter_HashOnWriting
        异步校验CPU占用高ToolStripMenuItem.Checked = My.Settings.LTFSWriter_HashAsync
        预读文件数5ToolStripMenuItem.Text = $"{My.Resources.ResText_PFC}{My.Settings.LTFSWriter_PreLoadFileCount}"
        文件缓存32MiBToolStripMenuItem.Text = $"{My.Resources.ResText_FB}{IOManager.FormatSize(My.Settings.LTFSWriter_PreLoadBytes)}"
        禁用分区ToolStripMenuItem.Checked = DisablePartition
        速度下限ToolStripMenuItem.Text = $"{My.Resources.ResText_SMin}{My.Settings.LTFSWriter_AutoCleanDownLim} MiB/s"
        速度上限ToolStripMenuItem.Text = $"{My.Resources.ResText_SMax}{My.Settings.LTFSWriter_AutoCleanUpperLim} MiB/s"
        持续时间ToolStripMenuItem.Text = $"{My.Resources.ResText_STime}{My.Settings.LTFSWriter_AutoCleanTimeThreashould}s"
        错误率ToolStripMenuItem.Text = $"{My.Resources.ResText_ErrRateLog}{My.Settings.LTFSWriter_AutoCleanErrRateLogThreashould}"
        去重ToolStripMenuItem.Checked = My.Settings.LTFSWriter_DeDupe
        右下角显示容量损失ToolStripMenuItem.Checked = My.Settings.LTFSWriter_ShowLoss
        Select Case My.Settings.LTFSWriter_PowerPolicyOnWriteBegin
            Case Guid.Empty
                无更改ToolStripMenuItem.Checked = True
            Case New Guid("381b4222-f694-41f0-9685-ff5bb260df2e")
                平衡ToolStripMenuItem.Checked = True
            Case New Guid("a1841308-3541-4fab-bc81-f71556f20b4a")
                节能ToolStripMenuItem.Checked = True
            Case New Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c")
                高性能ToolStripMenuItem.Checked = True
            Case Else
                其他ToolStripMenuItem.Checked = True
                其他ToolStripMenuItem.Text = $"{My.Resources.ResText_Other}: {My.Settings.LTFSWriter_PowerPolicyOnWriteBegin.ToString()}"
        End Select
        Select Case My.Settings.LTFSWriter_PowerPolicyOnWriteEnd
            Case Guid.Empty
                无更改ToolStripMenuItem1.Checked = True
            Case New Guid("381b4222-f694-41f0-9685-ff5bb260df2e")
                平衡ToolStripMenuItem1.Checked = True
            Case New Guid("a1841308-3541-4fab-bc81-f71556f20b4a")
                节能ToolStripMenuItem1.Checked = True
            Case New Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c")
                高性能ToolStripMenuItem1.Checked = True
            Case Else
                其他ToolStripMenuItem1.Checked = True
                其他ToolStripMenuItem1.Text = $"{My.Resources.ResText_Other}: {My.Settings.LTFSWriter_PowerPolicyOnWriteEnd.ToString()}"
        End Select
        Chart1.PrimaryAxisTitle = If(My.Settings.Application_UseDecimalUnit, My.Resources.ResText_SpeedBTD, My.Resources.ResText_SpeedBT)
        Chart1.SecondaryAxisTitle = My.Resources.ResText_FileRateBT
        Chart1.XAxisTitle = Min10ToolStripMenuItem.Text
        Chart1.VisibleSamples = SMaxNum
        TapeUtils.AllowPartition = Not DisablePartition
        CleanCycle = CleanCycle
        IndexWriteInterval = IndexWriteInterval
        CapacityRefreshInterval = CapacityRefreshInterval
        SpeedLimit = SpeedLimit
        If My.Settings.Application_License.ToLower().Contains("dev") Then TestToolStripMenuItem.Visible = True
    End Sub
    Public Sub Save_Settings()
        SaveListViewColumnOrder()
        My.Settings.LTFSWriter_OverwriteExist = 覆盖已有文件ToolStripMenuItem.Checked
        My.Settings.LTFSWriter_SkipSymlink = 跳过符号链接ToolStripMenuItem.Checked
        ApplyWAStatus()
        My.Settings.LTFSWriter_AutoFlush = APToolStripMenuItem.Checked
        My.Settings.LTFSWriter_LogEnabled = 启用日志记录ToolStripMenuItem.Checked
        AppLogging.SetInformationEnabled(My.Settings.LTFSWriter_LogEnabled)
        My.Settings.LTFSWriter_ForceIndex = 总是更新数据区索引ToolStripMenuItem.Checked
        My.Settings.LTFSWriter_HashOnWriting = 计算校验ToolStripMenuItem.Checked
        My.Settings.LTFSWriter_HashAsync = 异步校验CPU占用高ToolStripMenuItem.Checked
        If 无更改ToolStripMenuItem.Checked Then
            My.Settings.LTFSWriter_PowerPolicyOnWriteBegin = Guid.Empty
        ElseIf 平衡ToolStripMenuItem.Checked Then
            My.Settings.LTFSWriter_PowerPolicyOnWriteBegin = New Guid("381b4222-f694-41f0-9685-ff5bb260df2e")
        ElseIf 节能ToolStripMenuItem.Checked Then
            My.Settings.LTFSWriter_PowerPolicyOnWriteBegin = New Guid("a1841308-3541-4fab-bc81-f71556f20b4a")
        ElseIf 高性能ToolStripMenuItem.Checked Then
            My.Settings.LTFSWriter_PowerPolicyOnWriteBegin = New Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c")
        End If
        If 无更改ToolStripMenuItem1.Checked Then
            My.Settings.LTFSWriter_PowerPolicyOnWriteEnd = Guid.Empty
        ElseIf 平衡ToolStripMenuItem1.Checked Then
            My.Settings.LTFSWriter_PowerPolicyOnWriteEnd = New Guid("381b4222-f694-41f0-9685-ff5bb260df2e")
        ElseIf 节能ToolStripMenuItem1.Checked Then
            My.Settings.LTFSWriter_PowerPolicyOnWriteEnd = New Guid("a1841308-3541-4fab-bc81-f71556f20b4a")
        ElseIf 高性能ToolStripMenuItem1.Checked Then
            My.Settings.LTFSWriter_PowerPolicyOnWriteEnd = New Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c")
        End If
        My.Settings.Save()
    End Sub
    Private Text3 As String = "", Text5 As String = ""
    Private TextT3 As String = "", TextT5 As String = ""
    Private Sub Timer2_Tick(sender As Object, e As EventArgs) Handles Timer2.Tick
        Static blinkcycle As Integer = 0
        Const blinkticks As Integer = 1
        blinkcycle += 1
        blinkcycle = blinkcycle Mod (2 * blinkticks)

        Dim statusText3, statusText5, statusTooltip3, statusTooltip5 As String
        SyncLock _messageStateLock
            statusText3 = Text3
            statusTooltip3 = TextT3
            statusText5 = Text5
            statusTooltip5 = TextT5
        End SyncLock
        ToolStripStatusLabel3.Text = statusText3
        ToolStripStatusLabel3.ToolTipText = statusTooltip3
        ToolStripStatusLabel5.Text = statusText5
        ToolStripStatusLabel5.ToolTipText = statusTooltip5
        If TapeUtils.IsOpened(driveHandle) Then
            If IOCtlNum > 0 Then
                If blinkcycle < blinkticks Then
                    ToolStripStatusLabelS6.ForeColor = Color.Transparent
                Else
                    ToolStripStatusLabelS6.ForeColor = Color.LimeGreen
                End If
            Else
                ToolStripStatusLabelS6.ForeColor = Color.Green
            End If
        Else
            ToolStripStatusLabelS6.ForeColor = Color.Gray
        End If
        If blinkcycle < blinkticks Then
            If ToolStripStatusLabelS3.ForeColor <> Color.Gray Then
                ToolStripStatusLabelS3.ForeColor = Color.Transparent
            End If
            If ToolStripStatusLabelS4.ForeColor <> Color.Gray Then
                ToolStripStatusLabelS4.ForeColor = Color.Transparent
            End If
        Else
            If ToolStripStatusLabelS3.ForeColor <> Color.Gray Then
                ToolStripStatusLabelS3.ForeColor = Color.Orange
            End If
            If ToolStripStatusLabelS4.ForeColor <> Color.Gray Then
                ToolStripStatusLabelS4.ForeColor = Color.Orange
            End If
        End If
        If schema IsNot Nothing AndAlso
            My.Settings.LTFSWriter_ShowDragIcon AndAlso
            schema._directory IsNot Nothing AndAlso
            schema._directory.Count > 0 AndAlso
            schema._directory(0) IsNot Nothing AndAlso (
            GetListViewRowCount() = 0) Then
            Try
                Dim img As Image = IOManager.FitImage(My.Resources.dragdrop, ListView1.Size)
                ListView1.CreateGraphics().DrawImage(img, 0, 0)
            Catch ex As Exception

            End Try
        End If
    End Sub
    Public Enum LWStatus
        NotReady
        Idle
        Busy
        Succ
        Err
    End Enum
    Public Sub SetStatusLight(status As LWStatus)
        Invoke(Sub()
                   Select Case status
                       Case LWStatus.NotReady
                           ToolStripStatusLabelS1.ForeColor = Color.Gray
                           ToolStripStatusLabelS1.ToolTipText = My.Resources.ResText_NotReady
                       Case LWStatus.Idle
                           ToolStripStatusLabelS1.ForeColor = Color.Blue
                           ToolStripStatusLabelS1.ToolTipText = My.Resources.ResText_Idle
                       Case LWStatus.Busy
                           ToolStripStatusLabelS1.ForeColor = Color.Orange
                           ToolStripStatusLabelS1.ToolTipText = My.Resources.ResText_Busy
                       Case LWStatus.Succ
                           ToolStripStatusLabelS1.ForeColor = Color.Green
                           ToolStripStatusLabelS1.ToolTipText = My.Resources.ResText_Succ
                       Case LWStatus.Err
                           ToolStripStatusLabelS1.ForeColor = Color.Red
                           ToolStripStatusLabelS1.ToolTipText = My.Resources.ResText_Error
                   End Select
               End Sub)

    End Sub
    Public Sub PrintMsg(s As String, Optional ByVal Warning As Boolean = False,
                        Optional ByVal TooltipText As String = "",
                        Optional ByVal LogOnly As Boolean = False,
                        Optional ByVal ForceLog As Boolean = False,
                        Optional ByVal DeDupe As Boolean = False,
                        Optional ByVal Category As String = "LTFSWriter")
        If s Is Nothing Then s = String.Empty
        SyncLock _messageStateLock
            If DeDupe AndAlso s = _lastMessage Then Return
            _lastMessage = s
            If Not LogOnly Then
                Dim effectiveTooltip = TooltipText
                If effectiveTooltip IsNot Nothing AndAlso effectiveTooltip = String.Empty Then effectiveTooltip = s
                If Not Warning Then
                    Text3 = s
                    If effectiveTooltip IsNot Nothing Then TextT3 = effectiveTooltip
                Else
                    Text5 = s
                    If effectiveTooltip IsNot Nothing Then TextT5 = effectiveTooltip
                End If
            End If
        End SyncLock

        Dim logCategory = If(String.IsNullOrWhiteSpace(Category), NameOf(LTFSWriter), Category.Trim())
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(LTFSWriter))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", logCategory)
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "WriterStatus")
                        If Warning Then
                            Log.Warning(If(ForceLog, "Writer status update forced.", "Writer warning status update."))
                        ElseIf ForceLog Then
                            Log.Warning("Writer status update forced.")
                        Else
                            Log.Information(If(LogOnly, "Writer diagnostic status update.", "Writer status update."))
                        End If
                    End Using
                End Using
            End Using
        End Using
    End Sub
    <Category("LTFSWriter")>
    Public Property DataCompressionLogPage As TapeUtils.PageData
    <Category("UI")>
    Public Property ShowXAttr_Barcode As Boolean
    <Category("UI")>
    <TypeConverter(GetType(ListTypeDescriptor(Of List(Of Object), Object)))>
    Public ReadOnly Property ControlList As List(Of Object)
        Get
            Dim result As New List(Of Object)
            Dim q As New List(Of Control)
            For Each c As Control In Controls
                q.Add(c)
            Next
            While q.Count > 0
                Dim q2 As New List(Of Control)
                For Each c As Control In q
                    result.Add(c)
                    If TypeOf c Is SplitContainer Then
                        For Each d As Control In CType(c, SplitContainer).Panel1.Controls
                            q2.Add(d)
                        Next
                        For Each d As Control In CType(c, SplitContainer).Panel2.Controls
                            q2.Add(d)
                        Next
                    ElseIf TypeOf c Is Panel Then
                        For Each d As Control In CType(c, Panel).Controls
                            q2.Add(d)
                        Next
                    ElseIf TypeOf c Is TabControl Then
                        For Each d As TabPage In CType(c, TabControl).TabPages
                            For Each e As Control In d.Controls
                                q2.Add(e)
                            Next
                        Next
                    End If
                Next
                q = q2
            End While
            Return result
        End Get
    End Property
    <Category("UI")>
    <TypeConverter(GetType(ListTypeDescriptor(Of List(Of NamedObject), NamedObject)))>
    Public ReadOnly Property Field As List(Of NamedObject)
        Get
            Dim result As New List(Of NamedObject)
            For Each f As Reflection.FieldInfo In Me.GetType().GetFields(
                Reflection.BindingFlags.Public Or
                Reflection.BindingFlags.NonPublic Or
                Reflection.BindingFlags.Instance Or
                Reflection.BindingFlags.Static)
                Dim o As Object = f.GetValue(Me)
                If o IsNot Nothing Then result.Add(New NamedObject(f.Name, f, Me))
            Next
            Return result
        End Get
    End Property
    <TypeConverter(GetType(ExpandableObjectConverter))>
    Public Class MyThreadInfo
        Public Property Address As ULong
        Public Property ManagedThreadId As Integer
        Public Property Stack As String
        Public Property IsThreadpoolCompletionPort As Boolean
        Public Property IsThreadpoolWorker As Boolean
        Public Property IsThreadpoolWait As Boolean
        Public Property OSThreadId As UInteger
        Public Property IsAborted As Boolean
        Public Property IsAlive As Boolean
        Public Property IsBackground As Boolean
        Public Property IsGC As Boolean
        Public Property IsThreadpoolGate As Boolean
        Public Property IsThreadpoolTimer As Boolean
    End Class
    <TypeConverter(GetType(ExpandableObjectConverter))>
    Public Class StackTraceResult
        Public Property Message As String
        Public Property ThreadCount As Integer
        Public Property ThreadInfo As New List(Of MyThreadInfo)
        Public Property StackSummary As Dictionary(Of String, Integer)
    End Class
    <Category("Application")>
    Public ReadOnly Property StackTraces As StackTraceResult
        Get
            Try
                Dim threadLogDic As New Dictionary(Of String, Integer)()
                Dim threads = New List(Of MyThreadInfo)()

                Using target As Microsoft.Diagnostics.Runtime.DataTarget = Microsoft.Diagnostics.Runtime.DataTarget.CreateSnapshotAndAttach(Process.GetCurrentProcess().Id)
                    Dim runtime As Microsoft.Diagnostics.Runtime.ClrRuntime = target.ClrVersions.First().CreateRuntime()
                    ' We can't get the thread name from the ClrThead objects, so we'll look for
                    ' Thread instances on the heap and get the names from those.    
                    For Each thread As Microsoft.Diagnostics.Runtime.ClrThread In runtime.Threads
                        Dim t As Microsoft.Diagnostics.Runtime.ClrThread = thread

                        Dim stack As String = ""
                        For Each clrStackFrame In thread.EnumerateStackTrace()
                            stack += $"{clrStackFrame.Method}" & vbLf
                        Next
                        threads.Add(New MyThreadInfo() With {
                            .Address = t.Address,
                            .ManagedThreadId = t.ManagedThreadId,
                            .OSThreadId = t.OSThreadId,
                            .IsAborted = Nothing,
                            .IsAlive = t.IsAlive,
                            .IsBackground = Nothing,
                            .IsGC = t.IsGc,
                            .IsThreadpoolGate = Nothing,
                            .IsThreadpoolTimer = Nothing,
                            .IsThreadpoolWait = Nothing,
                            .IsThreadpoolWorker = Nothing,
                            .IsThreadpoolCompletionPort = Nothing,
                            .Stack = stack
                        })
                    Next
                End Using

                Dim stackDic = threads.GroupBy(Function(t) t.Stack).ToDictionary(Function(t) t.Key, Function(t) t.Count)

                Dim output As New StackTraceResult
                output.ThreadCount = threads.Count
                output.ThreadInfo = threads
                output.StackSummary = stackDic
                Return output
            Catch e As Exception
                Console.WriteLine(e)
                Return New StackTraceResult With {.Message = e.Message & e.StackTrace}
            End Try

            Return New StackTraceResult With {.Message = "err"}
        End Get

    End Property
    <Category("Application")>
    <TypeConverter(GetType(ListTypeDescriptor(Of List(Of NamedObject), NamedObject)))>
    Public ReadOnly Property App As List(Of NamedObject)
        Get
            Dim result As New List(Of NamedObject)
            Dim AppInstance As Application = CType(Activator.CreateInstance(GetType(Application), True), Application)
            For Each f As Reflection.FieldInfo In GetType(Application).GetFields(
                Reflection.BindingFlags.Public Or
                Reflection.BindingFlags.NonPublic Or
                Reflection.BindingFlags.Instance Or
                Reflection.BindingFlags.Static)
                Dim o As Object = f.GetValue(AppInstance)
                If o IsNot Nothing Then result.Add(New NamedObject(f.Name, f, AppInstance))
            Next
            Return result
        End Get
    End Property
    <Category("Application")>
    <TypeConverter(GetType(ExpandableObjectConverter))>
    Public ReadOnly Property Computer As Object
        Get
            Return My.Computer
        End Get
    End Property
    <TypeConverter(GetType(ExpandableObjectConverter))>
    <Category("Application")>
    Public ReadOnly Property Forms As Object
        Get
            Return My.Forms
        End Get
    End Property
    <TypeConverter(GetType(ExpandableObjectConverter))>
    <Category("Application")>
    Public ReadOnly Property Settings As Object
        Get
            Return My.Settings
        End Get
    End Property
    '<TypeConverter(GetType(ExpandableObjectConverter))>
    '<Category("Application")>
    'Public ReadOnly Property Resources As List(Of NamedObject)
    '    Get
    '        Dim result As New List(Of NamedObject)
    '        Dim asm As Reflection.Assembly = Reflection.Assembly.GetExecutingAssembly()
    '        Dim tRes As Type = GetType(My.Resources.Resources)
    '        For Each f As Reflection.FieldInfo In tRes.GetFields(
    '            Reflection.BindingFlags.Public Or
    '            Reflection.BindingFlags.NonPublic Or
    '            Reflection.BindingFlags.Instance Or
    '            Reflection.BindingFlags.Static)
    '            Dim o As Object = f.GetValue(Nothing)
    '            If o IsNot Nothing Then result.Add(New NamedObject(f.Name, f, Nothing))
    '        Next
    '        Return result
    '    End Get
    'End Property
    <TypeConverter(GetType(ExpandableObjectConverter))>
    <Category("Application")>
    Public ReadOnly Property User As Object
        Get
            Return My.User
        End Get
    End Property
    <Category("Application")>
    Public ReadOnly Property WebServices As Object
        Get
            Return My.WebServices
        End Get
    End Property
    <Category("TapeUtils")>
    <TypeConverter(GetType(ListTypeDescriptor(Of List(Of TapeUtils.PageData), TapeUtils.PageData)))>
    Public ReadOnly Property CurrentLogPages As List(Of TapeUtils.PageData)
        Get
            Return TapeUtils.PageData.GetAllPagesFromDrive(Handle)
        End Get
    End Property
    Public LastNoCCPs(31) As Integer
    Public LastC1Err(31) As Integer
    Public ChanErrLogRateHistory As New List(Of Double)
    Private _ErrLogRateHistory As Double
    Public ErrLRRefreshed As New AutoResetEvent(False)
    Public Property ErrLogRateHistory As Double
        Set(value As Double)
            _ErrLogRateHistory = value
            BeginInvoke(Sub()
                            ToolStripStatusLabelErrLog.Text = $"{value.ToString("f2")}"
                            ToolStripStatusLabelErrLog.ForeColor = ErrRateHelper.GetColor(ToolStripStatusLabelErrLog.Text, 0.6)
                            ErrLRRefreshed.Set()
                        End Sub)
            BeginInvoke(Sub()
                            Dim result As New StringBuilder
                            result.Append($"Max Error Rate Log: {value.ToString("f2")}")
                            SyncLock ChanErrLogRateHistory
                                For i As Integer = 0 To ChanErrLogRateHistory.Count - 1
                                    If ChanErrLogRateHistory(i) < 0 Then
                                        result.Append($"{vbCrLf}Channel {i}: {ChanErrLogRateHistory(i).ToString("f2")}")
                                    Else
                                        result.Append($"{vbCrLf}Channel {i}: N/A")
                                    End If

                                Next
                            End SyncLock
                            SetCapLossChannelInfo(result.ToString())
                        End Sub)
        End Set
        Get
            Return _ErrLogRateHistory
        End Get
    End Property
    <Category("LTFSWriter")>
    Public Property PageValid As Boolean = True
    Public Function ReadChanLRInfo(Optional ByVal TimeOut As Integer = 200) As Double
        Dim result As Double = Double.NegativeInfinity
        Dim debuginfo As New StringBuilder
        Dim WERLHeader As Byte()
        Dim WERLPage As Byte() = Nothing
        If Threading.Monitor.TryEnter(TapeUtils.GetSCSIOperationLock(driveHandle), TimeOut) Then
            Try
                If Not PageValid Then
                    Threading.Monitor.Exit(TapeUtils.GetSCSIOperationLock(driveHandle))
                    Return 0
                End If
                Dim pos As New TapeUtils.PositionData(driveHandle)
                Dim TapeCapLogPage As TapeUtils.PageData = TapeUtils.PageData.CreateDefault(TapeUtils.PageData.DefaultPages.HPLTO6_TapeCapacityLogPage, TapeUtils.LogSense(handle:=driveHandle, PageCode:=CByte(TapeUtils.PageData.DefaultPages.HPLTO6_TapeCapacityLogPage), SubPageCode:=0))
                Dim RemainCapacity As Integer = CInt(TapeCapLogPage.TryGetPage(pos.PartitionNumber + 1).GetLong)
                Dim TapeUsageLogPage As TapeUtils.PageData = TapeUtils.PageData.CreateDefault(TapeUtils.PageData.DefaultPages.HPLTO6_TapeUsageLogPage, TapeUtils.LogSense(handle:=driveHandle, PageCode:=CByte(TapeUtils.PageData.DefaultPages.HPLTO6_TapeUsageLogPage), SubPageCode:=0))
                Dim TotalDataSetW As Integer = CInt(TapeUsageLogPage.TryGetPage(2).GetLong)
                debuginfo.Append($"[ERRLOGRATE] P={pos.PartitionNumber} B={pos.BlockNumber} RemainCapacity={RemainCapacity} TotalDatasetWritten={TotalDataSetW}{vbTab}")

                WERLHeader = TapeUtils.SCSIReadParam(driveHandle, {&H1C, &H1, &H88, &H0, &H4, &H0}, 4,
                                                        Function(senseData As Byte()) As Boolean
                                                            PrintMsg(TapeUtils.ParseSenseData(senseData), LogOnly:=True)
                                                            Return True
                                                        End Function, 1)
                If WERLHeader.Length <> 4 Then
                    Threading.Monitor.Exit(TapeUtils.GetSCSIOperationLock(driveHandle))
                    PrintMsg("Invalid page. Skip Errrate Check", LogOnly:=True)
                    PageValid = False
                    Return 0
                End If
                Dim WERLPageLen As Integer = WERLHeader(2)
                WERLPageLen <<= 8
                WERLPageLen = WERLPageLen Or WERLHeader(3)
                If WERLPageLen = 0 Then
                    Threading.Monitor.Exit(TapeUtils.GetSCSIOperationLock(driveHandle))
                    PrintMsg("Page is empty. Skip Errrate Check", LogOnly:=True)
                    PageValid = False
                    Return 0
                End If
                WERLPageLen += 4
                WERLPage = TapeUtils.SCSIReadParam(handle:=driveHandle, cdbData:=New Byte() {&H1C, &H1, &H88, CByte((WERLPageLen >> 8) And &HFF), CByte(WERLPageLen And &HFF), &H0}, paramLen:=WERLPageLen)
            Catch ex As Exception
                PrintMsg(ex.ToString(), Warning:=True, LogOnly:=True)
            End Try
            Threading.Monitor.Exit(TapeUtils.GetSCSIOperationLock(driveHandle))
        Else
            PrintMsg("Device is busy. Skip Errrate Check", LogOnly:=True)
            Return 0
        End If
        Try
            Dim WERLData As String() = System.Text.Encoding.ASCII.GetString(WERLPage, 4, WERLPage.Length - 4).Split({vbCr, vbLf, vbTab}, StringSplitOptions.RemoveEmptyEntries)
            Dim AllResults As New List(Of Double)
            Dim DataValid As Boolean = False
            Dim HasNullChannel As Boolean = False
            For ch As Integer = 4 To WERLData.Length - 5 Step 5
                Dim chan As Integer = (ch - 4) \ 5
                Dim C1err As Integer = Integer.Parse(WERLData(ch + 0), Globalization.NumberStyles.HexNumber)
                'Dim C1cwerr As Integer = Integer.Parse(WERLData(ch + 1), Globalization.NumberStyles.HexNumber)
                'Dim Headerrr As Integer = Integer.Parse(WERLData(ch + 2), Globalization.NumberStyles.HexNumber)
                'Dim WrPasserr As Integer = Integer.Parse(WERLData(ch + 3), Globalization.NumberStyles.HexNumber)
                Dim NoCCPs As Integer = Integer.Parse(WERLData(ch + 4), Globalization.NumberStyles.HexNumber)
                debuginfo.Append($"CH={chan} CCP={NoCCPs} C1={C1err}")
                If NoCCPs - LastNoCCPs(chan) > 0 Then
                    DataValid = True
                    Dim errRateLogValue As Double = 0
                    Try
                        errRateLogValue = Math.Log10((C1err - LastC1Err(chan)) / (NoCCPs - LastNoCCPs(chan)) / 2 / 1920)
                    Catch ex As Exception
                    End Try
                    AllResults.Add(errRateLogValue)
                    If errRateLogValue < 0 Then
                        result = Math.Max(result, errRateLogValue)
                    End If
                    debuginfo.Append($" LR={errRateLogValue}{vbTab}")
                Else
                    HasNullChannel = True
                    Dim errRateLogValue As Double = -2.98
                    AllResults.Add(errRateLogValue)
                    debuginfo.Append($" LR={errRateLogValue}{vbTab}")
                End If
                LastC1Err(chan) = C1err
                LastNoCCPs(chan) = NoCCPs
            Next
            If DataValid AndAlso HasNullChannel Then
                result = Math.Max(result, -2.98)
            End If
            If DataValid Then ChanErrLogRateHistory = AllResults
        Catch
        End Try
        If result < -10 Then result = 0
        If result < 0 Then ErrLogRateHistory = result
        debuginfo.Append($" Result={result}")
        PrintMsg(debuginfo.ToString(), LogOnly:=True)
        If result <> 0 Then LRHistory = result
        Return result
    End Function


    Public d_last As Long = 0
    Public t_last As Long = 0
    Public TickCount As Long = 0
    Private Sub Timer1_Tick(sender As Object, e As EventArgs) Handles Timer1.Tick
        Try
            Dim pnow As Long = TotalBytesProcessed
            If pnow = 0 Then d_last = 0
            If pnow >= d_last Then
                ddelta = pnow - d_last
                d_last = pnow
            End If
            Dim tval As Long = TotalFilesProcessed
            If tval = 0 Then t_last = 0
            If tval >= t_last Then
                fdelta = tval - t_last
                t_last = tval
            End If


            SpeedHistory.Add(ddelta / 1048576)
            FileRateHistory.Add(fdelta)

            While SpeedHistory.Count > PMaxNum
                SpeedHistory.RemoveAt(0)
            End While
            While FileRateHistory.Count > PMaxNum
                FileRateHistory.RemoveAt(0)
            End While

            If My.Settings.LTFSWriter_AutoFlush AndAlso (fdelta = 0 OrElse LRHistory <> 0) Then
                Dim AutoFlushTriggered As Boolean = True
                For j As Integer = 1 To My.Settings.LTFSWriter_AutoCleanTimeThreashould
                    Dim n As Double = SpeedHistory(SpeedHistory.Count - j)
                    If (Not IsWriting) OrElse n < My.Settings.LTFSWriter_AutoCleanDownLim Or n > My.Settings.LTFSWriter_AutoCleanUpperLim Then
                        AutoFlushTriggered = False
                        Exit For
                    End If
                Next
                If CapReduceCount > 0 Then
                    ToolStripDropDownButton3.ToolTipText = $"{My.Resources.ResText_C0}{vbCrLf}{My.Resources.ResText_C1}{CapReduceCount}{vbCrLf}"
                    If CapReduceCount >= CleanCycle Then ToolStripDropDownButton3.ToolTipText &= $"{My.Resources.ResText_C2}{Clean_last.ToString("yyyy/MM/dd HH:mm:ss")}"
                End If
                Flush = AutoFlushTriggered
                If AutoFlushTriggered Then
                    If CleanCycle > 0 AndAlso CapReduceCount > 0 AndAlso (CapReduceCount Mod CleanCycle = 0) Then
                        Flush = False
                        Clean = True
                    End If
                End If
            End If

            If Threading.Monitor.TryEnter(OperationLock) Then
                Try
                    If AllowOperation And Clean Then
                        AllowOperation = False
                        Task.Run(Sub()
                                     CheckClean()
                                     AllowOperation = True
                                 End Sub)
                    End If
                Catch ex As Exception
                    PrintMsg(ex.ToString(), Warning:=True, LogOnly:=True)
                End Try
                Threading.Monitor.Exit(OperationLock)
            End If

            Chart1.AppendSample(ddelta / 1048576, fdelta)
            Dim USize As Long = CLng(UnwrittenSize)
            Dim UFile As Long = CLng(UnwrittenCount)
            Dim fastProviderSnapshot = _activeFastReaderProvider
            If fastProviderSnapshot IsNot Nothing Then
                Try
                    PipeBufferLength = fastProviderSnapshot.BufferedBytes
                Catch
                End Try
            End If
            ToolStripStatusLabel4.Text = " "
            ToolStripStatusLabel4.Text &= $"{My.Resources.ResText_S0}{IOManager.FormatSize(ddelta)}/s"
            ToolStripStatusLabel4.Text &= $"  {My.Resources.ResText_S1}{IOManager.FormatSize(TotalBytesProcessed)}"
            If CurrentBytesProcessed > 0 Then ToolStripStatusLabel4.Text &= $"({IOManager.FormatSize(CurrentBytesProcessed)})"
            ToolStripStatusLabel4.Text &= $"|{TotalFilesProcessed}"
            If CurrentFilesProcessed > 0 Then ToolStripStatusLabel4.Text &= $"({CurrentFilesProcessed})"
            ToolStripStatusLabel4.Text &= $"  {My.Resources.ResText_S2}"
            If UFile > 0 AndAlso UFile >= CurrentFilesProcessed Then ToolStripStatusLabel4.Text &= $"[{UFile - CurrentFilesProcessed}/{UFile}]"
            ToolStripStatusLabel4.Text &= $"{ IOManager.FormatSize(Math.Max(0, USize - CurrentBytesProcessed))}/{IOManager.FormatSize(USize)}"
            ToolStripStatusLabel4.Text &= $"  {My.Resources.ResText_S3}{IOManager.FormatSize(TotalBytesUnindexed)}"
            ToolStripStatusLabel4.Text &= $"  {My.Resources.ResText_S5}{IOManager.FormatSize(PipeBufferLength)}"
            ToolStripStatusLabel4.ToolTipText = ToolStripStatusLabel4.Text
            If Threading.Volatile.Read(_searchProgressActive) = 0 AndAlso
               Threading.Volatile.Read(_directorySortProgressActive) = 0 Then
                ToolStripStatusLabel6.Text = ""
                If USize > 0 AndAlso CurrentBytesProcessed >= 0 AndAlso CurrentBytesProcessed <= USize Then
                    ToolStripProgressBar1.Value = CInt(CurrentBytesProcessed / USize * 10000)
                    With Microsoft.WindowsAPICodePack.Taskbar.TaskbarManager.Instance
                        If ToolStripProgressBar1.Value = 0 OrElse ToolStripProgressBar1.Value = ToolStripProgressBar1.Maximum Then
                            .SetProgressState(Microsoft.WindowsAPICodePack.Taskbar.TaskbarProgressBarState.NoProgress)
                            .SetProgressValue(0, ToolStripProgressBar1.Maximum)
                        Else
                            If Not Pause Then
                                .SetProgressState(Microsoft.WindowsAPICodePack.Taskbar.TaskbarProgressBarState.Normal)
                            End If
                            .SetProgressValue(ToolStripProgressBar1.Value, ToolStripProgressBar1.Maximum)
                        End If
                    End With
                    ToolStripProgressBar1.ToolTipText = $"{My.Resources.ResText_S4}{IOManager.FormatSize(CurrentBytesProcessed)}/{IOManager.FormatSize(USize)}"
                    Dim CurrentTime As Date = Now
                    Dim totalTimeCost As Long = (CurrentTime - StartTime).Ticks
                    If totalTimeCost > 0 AndAlso CurrentBytesProcessed > 0 Then
                        Dim eteTotalCost As Double = totalTimeCost / CurrentBytesProcessed * USize
                        Dim RemainTicks As Long = CLng(Math.Min(Long.MaxValue, eteTotalCost) - totalTimeCost)
                        Dim remainTime As New TimeSpan(RemainTicks)
                        ToolStripStatusLabel6.Text = $"{My.Resources.ResText_Remaining} {Math.Truncate(remainTime.TotalHours).ToString().PadLeft(2, "0"c)}:{remainTime.Minutes.ToString().PadLeft(2, "0"c)}:{remainTime.Seconds.ToString().PadLeft(2, "0"c)}"
                        ToolStripProgressBar1.ToolTipText &= vbCrLf & ToolStripStatusLabel6.Text
                    End If
                End If
                ToolStripStatusLabel6.ToolTipText = ToolStripStatusLabel6.Text
            End If
            Task.Run(Sub()
                         Try
                             Dim Loc As String = GetLocInfo()
                             PrintMsg(Loc, LogOnly:=True, DeDupe:=True)
                             Invoke(Sub() Text = Loc)
                         Catch ex As Exception
                         End Try
                     End Sub)
            Static GCCollectCounter As Integer
            GCCollectCounter += 1
            If GCCollectCounter >= My.Settings.LTFSWriter_GCInterval Then
                If My.Settings.LTFSWriter_GCInterval > 0 AndAlso IsWriting Then GC.Collect()
                GCCollectCounter = 0
            End If
        Catch ex As Exception
            PrintMsg(ex.ToString)
            SetStatusLight(LWStatus.Err)
        End Try
        If TickCount < Long.MaxValue Then
            Threading.Interlocked.Increment(TickCount)
        Else
            TickCount = 0
        End If


    End Sub
    <Category("LTFSWriter")>
    Public Property FRTemplate As New FileRecord
    <TypeConverter(GetType(ExpandableObjectConverter))>
    Public Class FileRecord
        Public Property ParentDirectory As ltfsindex.directory
        Private _sourcePath As String
        Public Property SourcePath As String
            Get
                Return _sourcePath
            End Get
            Set(value As String)
                If String.IsNullOrWhiteSpace(value) Then
                    _sourcePath = value
                Else
                    ' Keep the path spelling returned by the filesystem when
                    ' the supplied path differs only by case.  This is needed
                    ' for case-sensitive NTFS directories and SMB shares.
                    _sourcePath = FileSystemPathResolver.ResolveExistingFilePath(value)
                End If
            End Set
        End Property
        Public Property File As ltfsindex.file
        Public Property FileOffset As Long
        Public Property SegmentLength As Long
        Public Property Buffer As Byte() = Nothing
        Public Property IsOpened As Boolean = False
        Private OperationLock As New Object
        Public Sub RemoveUnwritten()
            If ParentDirectory Is Nothing OrElse ParentDirectory.UnwrittenFiles Is Nothing Then Return
            SyncLock ParentDirectory.UnwrittenFiles
                ParentDirectory.UnwrittenFiles.Remove(File)
            End SyncLock
        End Sub
        Public Sub New()
            File = New ltfsindex.file
        End Sub
        Public Sub New(Path As String, ParentDir As ltfsindex.directory)
            Dim resolvedPath As String = FileSystemPathResolver.ResolveExistingFilePath(Path)
            If Not resolvedPath.StartsWith("\\") Then resolvedPath = $"\\?\{resolvedPath}"
            Dim xattrPath As String = Nothing
            Dim xattrCandidate As String = FileSystemPathResolver.ResolveExistingFilePath(resolvedPath & ".xattr")
            If IO.File.Exists(xattrCandidate) Then xattrPath = xattrCandidate
            Initialize(New IO.FileInfo(resolvedPath), ParentDir, xattrPath)
        End Sub
        Public Sub New(FileInfo As IO.FileInfo, ParentDir As ltfsindex.directory, XattrPath As String)
            Initialize(FileInfo, ParentDir, XattrPath)
        End Sub
        Public Sub New(FileInfo As IO.FileInfo, ParentDir As ltfsindex.directory)
            Dim resolvedPath As String = FileSystemPathResolver.ResolveExistingFilePath(FileInfo.FullName)
            Dim xattrPath As String = Nothing
            Dim xattrCandidate As String = FileSystemPathResolver.ResolveExistingFilePath(resolvedPath & ".xattr")
            If IO.File.Exists(xattrCandidate) Then xattrPath = xattrCandidate
            Initialize(New IO.FileInfo(resolvedPath), ParentDir, xattrPath)
        End Sub
        Private Sub Initialize(finf As IO.FileInfo, ParentDir As ltfsindex.directory, xattrPath As String)
            If finf Is Nothing Then Throw New ArgumentNullException(NameOf(finf))
            Dim resolvedPath As String = FileSystemPathResolver.ResolveExistingFilePath(finf.FullName)
            finf = New IO.FileInfo(resolvedPath)
            ParentDirectory = ParentDir
            SourcePath = finf.FullName
            If Not SourcePath.StartsWith("\\") Then SourcePath = $"\\?\{SourcePath}"
            File = New ltfsindex.file With {
                .name = finf.Name,
                .fileuid = -1,
                .length = finf.Length,
                .readonly = False,
                .openforwrite = False}
            FileOffset = 0
            SegmentLength = finf.Length
            With File
                Try
                    .creationtime = finf.CreationTimeUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffffff00Z")
                Catch ex As Exception
                    .creationtime = Now.ToUniversalTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff00Z")
                End Try
                Try
                    .modifytime = finf.LastWriteTimeUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffffff00Z")
                Catch ex As Exception
                    .modifytime = .creationtime
                End Try
                Try
                    .accesstime = finf.LastAccessTimeUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffffff00Z")
                Catch ex As Exception
                    .accesstime = .creationtime
                End Try
                .changetime = .modifytime
                .backuptime = Now.ToUniversalTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff00Z")
                Try
                    If xattrPath IsNot Nothing Then
                        xattrPath = FileSystemPathResolver.ResolveExistingFilePath(xattrPath)
                        If Not xattrPath.StartsWith("\\") Then xattrPath = $"\\?\{xattrPath}"
                        Dim x As String = IO.File.ReadAllText(xattrPath)
                        Dim xlist As List(Of ltfsindex.file.xattr) = ltfsindex.file.xattr.FromXMLList(x)
                        If .extendedattributes Is Nothing Then .extendedattributes = New List(Of ltfsindex.file.xattr)
                        .extendedattributes.AddRange(xlist)
                    End If
                Catch ex As Exception
                    Dim ActiveFrm = ApplicationWheels.GetActiveWindow()
                    ActiveFrm.Invoke(Sub() MessageBox.Show(New Form With {.TopMost = True}, $"{ex.ToString()}{vbCrLf}{ex.StackTrace}"))
                End Try
            End With
            SyncLock ParentDirectory.UnwrittenFiles
                ParentDirectory.UnwrittenFiles.Add(File)
            End SyncLock
        End Sub
        Public Property fs As IO.FileStream
        Public Property fsB As IO.BufferedStream
        'Public Property fsPreRead As IO.FileStream
        Public Property PreReadOffset As Long = 0
        Public Property PreReadByteCount As Long = 0
        Public PreReadOffsetLock As New Object
        Public Event PreReadFinished()
        'Public ReadOnly Property PreReadEnabled
        '    Get
        '        Return (My.Settings.LTFSWriter_PreLoadNum = 0)
        '    End Get
        'End Property
        Public Shared PreReadBufferSize As Long = 65536
        Public Shared PreReadBlockSize As Long = 4096
        Public PreReadBuffer As Byte() = Nothing

        Public Function EnsureSourcePathResolved() As String
            If String.IsNullOrWhiteSpace(_sourcePath) Then Return _sourcePath
            Dim resolvedPath As String = FileSystemPathResolver.ResolveExistingFilePath(_sourcePath)
            If Not String.Equals(_sourcePath, resolvedPath, StringComparison.Ordinal) Then
                _sourcePath = resolvedPath
            End If
            Return _sourcePath
        End Function

        Public Function Open(Optional BufferSize As Integer = 65536) As Integer
            SyncLock OperationLock
                While True
                    Try
                        If fs IsNot Nothing Then
                            IsOpened = True
                            Return 1
                        End If
                        fs = New IO.FileStream(EnsureSourcePathResolved(), IO.FileMode.Open, IO.FileAccess.Read, IO.FileShare.Read, BufferSize, True)
                        If FileOffset > 0 Then fs.Seek(FileOffset, IO.SeekOrigin.Begin)
                        IsOpened = True
                        Exit While
                    Catch ex As Exception
                        Dim ActiveFrm = ApplicationWheels.GetActiveWindow()
                        Dim dResult As DialogResult
                        ActiveFrm.Invoke(Sub() dResult = MessageBox.Show(New Form With {.TopMost = True}, $"{My.Resources.ResText_WErr }{vbCrLf}{ex.ToString}", My.Resources.ResText_Warning, MessageBoxButtons.AbortRetryIgnore))
                        Select Case dResult
                            Case DialogResult.Abort
                                IsOpened = False
                                Return 3
                            Case DialogResult.Retry

                            Case DialogResult.Ignore
                                IsOpened = False
                                Return 5
                        End Select
                    End Try
                End While
            End SyncLock
            Return 1
        End Function

        Public Function Read(array As Byte(), offset As Integer, count As Integer) As Integer
            'If PreReadEnabled Then
            SyncLock PreReadOffsetLock
                PreReadOffset = Math.Max(fs.Position, PreReadOffset)
            End SyncLock
            'Return fs.Read(array, offset, count)
            'Else
            Return fsB.Read(array, offset, count)
            'End If
        End Function
        Public IsClosed As Boolean = False
        Public Sub Close()
            SyncLock OperationLock
                Try
                    If fsB IsNot Nothing Then
                        fsB.Close()
                        fsB.Dispose()
                        fsB = Nothing
                    End If
                Catch ex As Exception
                End Try
                Try
                    If fs IsNot Nothing Then
                        fs.Close()
                        fs.Dispose()
                        fs = Nothing
                    End If
                Catch ex As Exception
                End Try
                IsClosed = True
            End SyncLock
        End Sub
        Public Sub CloseAsync()
            Task.Run(Sub()
                         SyncLock OperationLock
                             Try
                                 If fsB IsNot Nothing Then
                                     fsB.Close()
                                     fsB.Dispose()
                                     fsB = Nothing
                                 End If
                             Catch ex As Exception
                             End Try
                             Try
                                 fs.Close()
                                 fs.Dispose()
                                 fs = Nothing
                             Catch ex As Exception
                             End Try
                         End SyncLock
                     End Sub)
        End Sub
        Public Function ReadAllBytes() As Byte()
            If Buffer IsNot Nothing AndAlso Buffer.Length > 0 Then
                Dim result As Byte() = Buffer
                Buffer = Nothing
                Return result
            Else
                Return IO.File.ReadAllBytes(EnsureSourcePathResolved())
            End If
        End Function
    End Class

    Private NotInheritable Class DirectoryReferenceComparer
        Implements IEqualityComparer(Of ltfsindex.directory)

        Public Shared ReadOnly Instance As New DirectoryReferenceComparer

        Public Overloads Function Equals(left As ltfsindex.directory,
                                          right As ltfsindex.directory) As Boolean _
            Implements IEqualityComparer(Of ltfsindex.directory).Equals
            Return Object.ReferenceEquals(left, right)
        End Function

        Public Shadows Function GetHashCode(value As ltfsindex.directory) As Integer _
            Implements IEqualityComparer(Of ltfsindex.directory).GetHashCode
            If value Is Nothing Then Return 0
            Return RuntimeHelpers.GetHashCode(value)
        End Function
    End Class

    Private NotInheritable Class DirectoryRecordComparer
        Implements IEqualityComparer(Of ltfsindex.directory)

        Public Shared ReadOnly Instance As New DirectoryRecordComparer

        Public Overloads Function Equals(left As ltfsindex.directory,
                                          right As ltfsindex.directory) As Boolean _
            Implements IEqualityComparer(Of ltfsindex.directory).Equals
            If Object.ReferenceEquals(left, right) Then Return True
            If left Is Nothing OrElse right Is Nothing Then Return False
            Return left.HasLazyRecord AndAlso right.HasLazyRecord AndAlso
                   left.LazyRecordOffset = right.LazyRecordOffset AndAlso
                   Object.ReferenceEquals(left.LazyStoreReference, right.LazyStoreReference)
        End Function

        Public Shadows Function GetHashCode(value As ltfsindex.directory) As Integer _
            Implements IEqualityComparer(Of ltfsindex.directory).GetHashCode
            If value Is Nothing Then Return 0
            If Not value.HasLazyRecord Then Return RuntimeHelpers.GetHashCode(value)

            Dim storeHash As Integer = RuntimeHelpers.GetHashCode(value.LazyStoreReference)
            Return storeHash Xor value.LazyRecordOffset.GetHashCode()
        End Function
    End Class

    Private Function ResolvePendingDirectoryUnsafe(directory As ltfsindex.directory) As ltfsindex.directory
        If directory Is Nothing Then Return Nothing

        Dim byName As Dictionary(Of String, FileRecord) = Nothing
        If _pendingByDirectory.TryGetValue(directory, byName) Then
            For Each record As FileRecord In byName.Values
                If record IsNot Nothing AndAlso record.ParentDirectory IsNot Nothing Then
                    Return record.ParentDirectory
                End If
            Next
        End If
        Return directory
    End Function

    Private Function ResolvePendingDirectory(directory As ltfsindex.directory) As ltfsindex.directory
        SyncLock _pendingQueueLock
            Return ResolvePendingDirectoryUnsafe(directory)
        End SyncLock
    End Function

    Private Sub RefreshPendingTreeCounts()
        Dim counts As New Dictionary(Of ltfsindex.directory, Long)(DirectoryRecordComparer.Instance)
        SyncLock _pendingQueueLock
            For Each pendingDirectory As ltfsindex.directory In _pendingByDirectory.Keys
                Dim current As ltfsindex.directory = ResolvePendingDirectoryUnsafe(pendingDirectory)
                If current Is Nothing OrElse current.UnwrittenFiles Is Nothing Then Continue For
                Dim directCount As Long
                SyncLock current.UnwrittenFiles
                    directCount = current.UnwrittenFiles.Count
                End SyncLock
                If directCount = 0 Then Continue For

                Dim visited As New HashSet(Of ltfsindex.directory)(DirectoryRecordComparer.Instance)
                While current IsNot Nothing AndAlso visited.Add(current)
                    Dim currentCount As Long = 0
                    counts.TryGetValue(current, currentCount)
                    counts(current) = currentCount + directCount
                    current = current.LazyParent
                End While
            Next
        End SyncLock
        _pendingTreeCountByDirectory = counts
    End Sub

    Private Function GetPendingTreeFileCount(directory As ltfsindex.directory) As Long
        If directory Is Nothing Then Return 0
        Dim count As Long = 0
        If _pendingTreeCountByDirectory.TryGetValue(directory, count) Then Return count
        Return 0
    End Function

    Private Sub RegisterPendingRecordUnsafe(record As FileRecord)
        If record Is Nothing OrElse record.ParentDirectory Is Nothing OrElse record.File Is Nothing Then Return

        Dim byName As Dictionary(Of String, FileRecord) = Nothing
        If Not _pendingByDirectory.TryGetValue(record.ParentDirectory, byName) Then
            byName = New Dictionary(Of String, FileRecord)(StringComparer.Ordinal)
            _pendingByDirectory(record.ParentDirectory) = byName
        Else
            Dim canonicalDirectory As ltfsindex.directory = ResolvePendingDirectoryUnsafe(record.ParentDirectory)
            If canonicalDirectory IsNot Nothing AndAlso Not Object.ReferenceEquals(canonicalDirectory, record.ParentDirectory) Then
                Dim previousDirectory As ltfsindex.directory = record.ParentDirectory
                If previousDirectory.UnwrittenFiles IsNot Nothing Then
                    SyncLock previousDirectory.UnwrittenFiles
                        previousDirectory.UnwrittenFiles.Remove(record.File)
                    End SyncLock
                End If
                record.ParentDirectory = canonicalDirectory
                SyncLock canonicalDirectory.UnwrittenFiles
                    If Not canonicalDirectory.UnwrittenFiles.Contains(record.File) Then
                        canonicalDirectory.UnwrittenFiles.Add(record.File)
                    End If
                End SyncLock
            End If
        End If
        byName(If(record.File.name, String.Empty)) = record
    End Sub

    Private Function GetPendingDirectoryIndexUnsafe(directory As ltfsindex.directory) As Dictionary(Of String, FileRecord)
        Dim byName As Dictionary(Of String, FileRecord) = Nothing
        If _pendingByDirectory.TryGetValue(directory, byName) Then Return byName

        byName = New Dictionary(Of String, FileRecord)(StringComparer.Ordinal)
        If UnwrittenFiles IsNot Nothing Then
            For Each record As FileRecord In UnwrittenFiles
                If record IsNot Nothing AndAlso
                   DirectoryRecordComparer.Instance.Equals(record.ParentDirectory, directory) AndAlso
                   record.File IsNot Nothing Then
                    byName(If(record.File.name, String.Empty)) = record
                End If
            Next
        End If
        _pendingByDirectory(directory) = byName
        Return byName
    End Function

    Private Sub RemovePendingIndexUnsafe(record As FileRecord)
        If record Is Nothing OrElse record.ParentDirectory Is Nothing Then Return
        Dim byName As Dictionary(Of String, FileRecord) = Nothing
        If Not _pendingByDirectory.TryGetValue(record.ParentDirectory, byName) Then Return

        Dim staleKeys As New List(Of String)
        For Each pair As KeyValuePair(Of String, FileRecord) In byName
            If Object.ReferenceEquals(pair.Value, record) Then staleKeys.Add(pair.Key)
        Next
        For Each key As String In staleKeys
            byName.Remove(key)
        Next
        If byName.Count = 0 Then _pendingByDirectory.Remove(record.ParentDirectory)
    End Sub

    Private Sub AddPendingRecord(record As FileRecord)
        If record Is Nothing Then Return
        SyncLock _pendingQueueLock
            If UnwrittenFiles Is Nothing Then UnwrittenFiles = New List(Of FileRecord)
            UnwrittenFiles.Add(record)
            If record.File IsNot Nothing Then _pendingSize += record.File.length
            RegisterPendingRecordUnsafe(record)
        End SyncLock
    End Sub

    Private Function RemovePendingRecord(record As FileRecord) As Boolean
        If record Is Nothing Then Return False
        SyncLock _pendingQueueLock
            Dim removed As Boolean = False
            If record.ParentDirectory IsNot Nothing AndAlso record.ParentDirectory.UnwrittenFiles IsNot Nothing Then
                SyncLock record.ParentDirectory.UnwrittenFiles
                    removed = record.ParentDirectory.UnwrittenFiles.Remove(record.File) OrElse removed
                End SyncLock
            End If
            If UnwrittenFiles IsNot Nothing AndAlso UnwrittenFiles.Remove(record) Then
                If record.File IsNot Nothing Then _pendingSize -= record.File.length
                removed = True
            End If
            RemovePendingIndexUnsafe(record)
            Return removed
        End SyncLock
    End Function

    Private Function RemovePendingByFile(directory As ltfsindex.directory, file As ltfsindex.file) As Boolean
        If directory Is Nothing OrElse file Is Nothing Then Return False
        SyncLock _pendingQueueLock
            directory = ResolvePendingDirectoryUnsafe(directory)
            Dim removed As Boolean = False
            If directory.UnwrittenFiles IsNot Nothing Then
                SyncLock directory.UnwrittenFiles
                    removed = directory.UnwrittenFiles.Remove(file) OrElse removed
                End SyncLock
            End If
            If UnwrittenFiles IsNot Nothing Then
                For i As Integer = UnwrittenFiles.Count - 1 To 0 Step -1
                    Dim record As FileRecord = UnwrittenFiles(i)
                    If record IsNot Nothing AndAlso
                       DirectoryRecordComparer.Instance.Equals(record.ParentDirectory, directory) AndAlso
                       record.File Is file Then
                        UnwrittenFiles.RemoveAt(i)
                        If record.File IsNot Nothing Then _pendingSize -= record.File.length
                        RemovePendingIndexUnsafe(record)
                        removed = True
                    End If
                Next
            End If
            Return removed
        End SyncLock
    End Function

    Private Function RemovePendingByName(directory As ltfsindex.directory, fileName As String) As Boolean
        If directory Is Nothing OrElse fileName Is Nothing Then Return False
        SyncLock _pendingQueueLock
            directory = ResolvePendingDirectoryUnsafe(directory)
            Dim byName As Dictionary(Of String, FileRecord) = GetPendingDirectoryIndexUnsafe(directory)
            Dim record As FileRecord = Nothing
            If byName.TryGetValue(fileName, record) AndAlso record IsNot Nothing AndAlso record.File IsNot Nothing AndAlso
                   String.Equals(record.File.name, fileName, StringComparison.Ordinal) Then
                Return RemovePendingRecord(record)
            End If

            ' Direct list edits are still supported by the old public model.  The
            ' indexed path handles normal adds; this fallback keeps those edits
            ' correct without making every add scan the whole global queue.
            Dim removedFiles As New HashSet(Of ltfsindex.file)
            If directory.UnwrittenFiles IsNot Nothing Then
                SyncLock directory.UnwrittenFiles
                    For i As Integer = directory.UnwrittenFiles.Count - 1 To 0 Step -1
                        Dim pendingFile As ltfsindex.file = directory.UnwrittenFiles(i)
                        If pendingFile IsNot Nothing AndAlso String.Equals(pendingFile.name, fileName, StringComparison.Ordinal) Then
                            removedFiles.Add(pendingFile)
                            directory.UnwrittenFiles.RemoveAt(i)
                        End If
                    Next
                End SyncLock
            End If

            If UnwrittenFiles IsNot Nothing Then
                For i As Integer = UnwrittenFiles.Count - 1 To 0 Step -1
                    Dim pendingRecord As FileRecord = UnwrittenFiles(i)
                    If pendingRecord IsNot Nothing AndAlso
                       DirectoryRecordComparer.Instance.Equals(pendingRecord.ParentDirectory, directory) AndAlso
                       pendingRecord.File IsNot Nothing AndAlso
                       (removedFiles.Contains(pendingRecord.File) OrElse String.Equals(pendingRecord.File.name, fileName, StringComparison.Ordinal)) Then
                        UnwrittenFiles.RemoveAt(i)
                        _pendingSize -= pendingRecord.File.length
                        RemovePendingIndexUnsafe(pendingRecord)
                        removedFiles.Add(pendingRecord.File)
                    End If
                Next
            End If
            _pendingByDirectory.Remove(directory)
            Return removedFiles.Count > 0
        End SyncLock
    End Function

    Private Sub RemovePendingRecords(records As IList(Of FileRecord))
        If records Is Nothing OrElse records.Count = 0 Then Return
        SyncLock _pendingQueueLock
            Dim recordSet As New HashSet(Of FileRecord)
            Dim filesByDirectory As New Dictionary(Of ltfsindex.directory, HashSet(Of ltfsindex.file))(DirectoryReferenceComparer.Instance)
            For Each record As FileRecord In records
                If record Is Nothing Then Continue For
                recordSet.Add(record)
                If record.ParentDirectory Is Nothing OrElse record.File Is Nothing Then Continue For
                Dim files As HashSet(Of ltfsindex.file) = Nothing
                If Not filesByDirectory.TryGetValue(record.ParentDirectory, files) Then
                    files = New HashSet(Of ltfsindex.file)
                    filesByDirectory(record.ParentDirectory) = files
                End If
                files.Add(record.File)
            Next

            For Each pair As KeyValuePair(Of ltfsindex.directory, HashSet(Of ltfsindex.file)) In filesByDirectory
                If pair.Key.UnwrittenFiles Is Nothing Then Continue For
                SyncLock pair.Key.UnwrittenFiles
                    pair.Key.UnwrittenFiles.RemoveAll(Function(value As ltfsindex.file) pair.Value.Contains(value))
                End SyncLock
            Next

            If UnwrittenFiles IsNot Nothing Then
                For i As Integer = UnwrittenFiles.Count - 1 To 0 Step -1
                    Dim value As FileRecord = UnwrittenFiles(i)
                    If recordSet.Contains(value) Then
                        UnwrittenFiles.RemoveAt(i)
                        If value IsNot Nothing AndAlso value.File IsNot Nothing Then _pendingSize -= value.File.length
                    End If
                Next
            End If
            For Each record As FileRecord In recordSet
                RemovePendingIndexUnsafe(record)
            Next
        End SyncLock
    End Sub

    Private Sub BeginAddFileLookup()
        _activeAddFileLookup = New Dictionary(Of ltfsindex.directory, Dictionary(Of String, List(Of ltfsindex.file)))(DirectoryReferenceComparer.Instance)
        _activeAddFileLookupCalls = New Dictionary(Of ltfsindex.directory, Integer)(DirectoryReferenceComparer.Instance)
    End Sub

    Private Sub EndAddFileLookup()
        _activeAddFileLookup = Nothing
        _activeAddFileLookupCalls = Nothing
    End Sub

    Private Function GetExistingFilesForAdd(directory As ltfsindex.directory, fileName As String) As List(Of ltfsindex.file)
        If directory Is Nothing Then Return New List(Of ltfsindex.file)
        If _activeAddFileLookup Is Nothing Then Return directory.FindFilesByName(fileName)

        Dim byName As Dictionary(Of String, List(Of ltfsindex.file)) = Nothing
        If Not _activeAddFileLookup.TryGetValue(directory, byName) Then
            Dim lookupCalls As Integer = 0
            If _activeAddFileLookupCalls IsNot Nothing Then _activeAddFileLookupCalls.TryGetValue(directory, lookupCalls)
            If lookupCalls = 0 Then
                _activeAddFileLookupCalls(directory) = 1
                'A one-file add should retain the old early-exit behavior.  If
                'the same directory receives another file, the next call builds
                'one reusable name map instead of rescanning it for every file.
                Return directory.FindFilesByName(fileName)
            End If

            byName = New Dictionary(Of String, List(Of ltfsindex.file))(StringComparer.Ordinal)
            For Each existing As ltfsindex.file In directory.EnumerateLazyFiles()
                If existing Is Nothing Then Continue For
                Dim key As String = If(existing.name, String.Empty)
                Dim sameName As List(Of ltfsindex.file) = Nothing
                If Not byName.TryGetValue(key, sameName) Then
                    sameName = New List(Of ltfsindex.file)
                    byName(key) = sameName
                End If
                sameName.Add(existing)
            Next
            _activeAddFileLookup(directory) = byName
        End If

        Dim result As List(Of ltfsindex.file) = Nothing
        If byName.TryGetValue(If(fileName, String.Empty), result) Then Return New List(Of ltfsindex.file)(result)
        Return New List(Of ltfsindex.file)
    End Function

    Private Sub RemoveExistingFileFromAddLookup(directory As ltfsindex.directory, file As ltfsindex.file)
        If _activeAddFileLookup Is Nothing OrElse directory Is Nothing OrElse file Is Nothing Then Return
        Dim byName As Dictionary(Of String, List(Of ltfsindex.file)) = Nothing
        If Not _activeAddFileLookup.TryGetValue(directory, byName) Then Return
        Dim sameName As List(Of ltfsindex.file) = Nothing
        If Not byName.TryGetValue(If(file.name, String.Empty), sameName) Then Return
        sameName.Remove(file)
        If sameName.Count = 0 Then byName.Remove(If(file.name, String.Empty))
    End Sub

    Private Sub ReindexPendingFile(directory As ltfsindex.directory, file As ltfsindex.file)
        If directory Is Nothing OrElse file Is Nothing Then Return
        SyncLock _pendingQueueLock
            directory = ResolvePendingDirectoryUnsafe(directory)
            Dim byName As Dictionary(Of String, FileRecord) = Nothing
            If Not _pendingByDirectory.TryGetValue(directory, byName) Then Return
            Dim staleKeys As New List(Of String)
            For Each pair As KeyValuePair(Of String, FileRecord) In byName
                If pair.Value IsNot Nothing AndAlso pair.Value.File Is file Then staleKeys.Add(pair.Key)
            Next
            For Each key As String In staleKeys
                byName.Remove(key)
            Next
            For Each record As FileRecord In UnwrittenFiles
                If record IsNot Nothing AndAlso
                   DirectoryRecordComparer.Instance.Equals(record.ParentDirectory, directory) AndAlso
                   record.File Is file Then
                    byName(If(file.name, String.Empty)) = record
                    Exit For
                End If
            Next
        End SyncLock
    End Sub

    Private Sub ClearWriteCompletionState()
        SyncLock _writeCompletionLock
            _completedWriteFiles.Clear()
        End SyncLock
    End Sub

    Private Sub MarkWriteFileCompleted(file As ltfsindex.file)
        If file Is Nothing Then Return
        SyncLock _writeCompletionLock
            _completedWriteFiles.Add(file)
        End SyncLock
    End Sub

    Private Function IsWriteFileCompleted(file As ltfsindex.file) As Boolean
        If file Is Nothing Then Return False
        SyncLock _writeCompletionLock
            Return _completedWriteFiles.Contains(file)
        End SyncLock
    End Function

    Private Sub CommitWrittenFiles(records As IList(Of FileRecord))
        If records Is Nothing OrElse records.Count = 0 Then Return

        Dim filesByDirectory As New Dictionary(Of ltfsindex.directory, List(Of ltfsindex.file))(DirectoryReferenceComparer.Instance)
        Dim committedRecords As New List(Of FileRecord)
        Dim seenFiles As New HashSet(Of ltfsindex.file)
        For Each record As FileRecord In records
            If record Is Nothing OrElse record.ParentDirectory Is Nothing OrElse record.File Is Nothing Then Continue For
            If Not seenFiles.Add(record.File) Then Continue For
            MarkWriteFileCompleted(record.File)
            Dim files As List(Of ltfsindex.file) = Nothing
            If Not filesByDirectory.TryGetValue(record.ParentDirectory, files) Then
                files = New List(Of ltfsindex.file)
                filesByDirectory(record.ParentDirectory) = files
            End If
            files.Add(record.File)
            committedRecords.Add(record)
        Next

        For Each pair As KeyValuePair(Of ltfsindex.directory, List(Of ltfsindex.file)) In filesByDirectory
            pair.Key.AddFiles(pair.Value)
        Next
        RemovePendingRecords(committedRecords)
        RequestListDisplayRefresh()
    End Sub

    <TypeConverter(GetType(ExpandableObjectConverter))>
    Public Class IntLock
        Public Property Value As Integer = 0
        Public Sub Inc()
            SyncLock Me
                Threading.Interlocked.Increment(Value)
            End SyncLock
        End Sub
        Public Sub Dec()
            SyncLock Me
                Threading.Interlocked.Decrement(Value)
            End SyncLock
        End Sub
        Public Shared Widening Operator CType(n As Integer) As IntLock
            Return New IntLock With {.Value = n}
        End Operator
        Public Shared Operator >(a As IntLock, b As Integer) As Boolean
            Return a.Value > b
        End Operator
        Public Shared Operator <(a As IntLock, b As Integer) As Boolean
            Return a.Value < b
        End Operator
    End Class
    Public UFReadCount As IntLock = 0
    <Category("LTFSWriter")>
    <TypeConverter(GetType(ListTypeDescriptor(Of List(Of FileRecord), FileRecord)))>
    Public Property UnwrittenFiles As New List(Of FileRecord)
    <Category("LTFSWriter")>
    Public Property UnwrittenSizeOverrideValue As ULong = 0
    <Category("LTFSWriter")>
    Public ReadOnly Property UnwrittenSize As ULong
        Get
            If UnwrittenSizeOverrideValue > 0 Then Return UnwrittenSizeOverrideValue
            If UnwrittenFiles Is Nothing Then Return 0
            SyncLock _pendingQueueLock
                Return CULng(Math.Max(0L, _pendingSize))
            End SyncLock
        End Get
    End Property
    <Category("LTFSWriter")>
    Public Property UnwrittenCountOverrideValue As ULong = 0
    <Category("LTFSWriter")>
    Public ReadOnly Property UnwrittenCount As ULong
        Get
            If UnwrittenCountOverrideValue > 0 Then Return UnwrittenCountOverrideValue
            SyncLock _pendingQueueLock
                Return CULng(If(UnwrittenFiles Is Nothing, 0, UnwrittenFiles.Count))
            End SyncLock
        End Get
    End Property

    Private Sub ClearPendingWriteQueues(Optional extraDirectory As ltfsindex.directory = Nothing)
        ' Do not walk schema._directory here.  The global write queue already
        ' identifies every directory touched by this write; clearing only those
        ' queues keeps abort/cleanup O(pending writes), even for a multi-million
        ' file lazy schema.  extraDirectory covers the disk-image writer, whose
        ' temporary FileRecord is kept only in the selected directory queue.
        Dim touchedDirectories As New HashSet(Of ltfsindex.directory)(DirectoryReferenceComparer.Instance)
        SyncLock _pendingQueueLock
            If UnwrittenFiles IsNot Nothing Then
                For Each fr As FileRecord In UnwrittenFiles
                    If fr IsNot Nothing AndAlso fr.ParentDirectory IsNot Nothing Then
                        touchedDirectories.Add(fr.ParentDirectory)
                    End If
                Next
                UnwrittenFiles.Clear()
            End If
            _pendingSize = 0
            _pendingByDirectory.Clear()
            If extraDirectory IsNot Nothing Then touchedDirectories.Add(extraDirectory)

            For Each directory As ltfsindex.directory In touchedDirectories
                If directory.UnwrittenFiles IsNot Nothing Then
                    SyncLock directory.UnwrittenFiles
                        directory.UnwrittenFiles.Clear()
                    End SyncLock
                End If
            Next
        End SyncLock
    End Sub

    Private Sub ClearGlobalPendingQueue()
        SyncLock _pendingQueueLock
            If UnwrittenFiles IsNot Nothing Then UnwrittenFiles.Clear()
            _pendingSize = 0
            _pendingByDirectory.Clear()
        End SyncLock
    End Sub

    <Category("LTFSWriter")>
    Public Property LastRefresh As Date = Now
    <Category("LTFSWriter")>
    Public Property driveHandle As IntPtr
    <Category("TapeUtils")>
    Public Property IOCtlNum As Integer
    <Category("LTFSWriter")>
    Public Property LoadComplete As Boolean = False
    Private Sub LTFSWriter_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        Dim scrH As Integer = Screen.GetWorkingArea(Me).Height
        If scrH - Top - Height <= 0 Then
            Height += scrH - Top - Height
        End If
        FileDroper = New FileDropHandler(ListView1)
        Load_Settings()
        If OfflineMode Then
            LoadComplete = True
            Exit Sub
        End If

        _deviceLease = TapeUtils.SCSILockManager.AcquireWriterLease(TapeDrive, _logSessionId, 10000)
        If _deviceLease Is Nothing Then
            PrintMsg("Device is busy in another process.", Warning:=True, LogOnly:=False, ForceLog:=True)
            SetStatusLight(LWStatus.Err)
            LoadComplete = True
            BeginInvoke(Sub()
                            If Not IsDisposed AndAlso Not Disposing Then Close()
                        End Sub)
            Exit Sub
        End If

        Dim driveOpened As Boolean = False
        Try
            ' The parent may still be unwinding a debug-panel operation and
            ' therefore hold an OS handle even though this process already
            ' owns the writer lease.  Keep the lease, let the parent finish,
            ' and retry the open instead of releasing the reservation.
            For openAttempt As Integer = 1 To 40
                If TapeUtils.OpenTapeDrive(TapeDrive, driveHandle) Then
                    driveOpened = True
                    Exit For
                End If
                If openAttempt < 40 Then Thread.Sleep(250)
            Next

            If Not driveOpened Then
                Throw New IOException($"Cannot open {TapeDrive}")
            End If

        Catch ex As Exception
            PrintMsg(My.Resources.ResText_ErrP)
            SetStatusLight(LWStatus.Err)
            If _deviceLease IsNot Nothing Then
                _deviceLease.Dispose()
                _deviceLease = Nothing
            End If
            LoadComplete = True
            BeginInvoke(Sub()
                            If Not IsDisposed AndAlso Not Disposing Then Close()
                        End Sub)
            Exit Sub
        End Try
        AddHandler TapeUtils.IOCtlStart, Sub() Threading.Interlocked.Increment(_IOCtlNum)
        AddHandler TapeUtils.IOCtlFinished, Sub() Threading.Interlocked.Decrement(_IOCtlNum)
        Task.Run(Sub()
                     Dim LastTick As Long = 0
                     Dim UIHangCount As Integer = 0
                     Dim DebugPanelAutoShowup As Boolean = True
                     Dim ToolTipChanErrLogShown As Boolean = False
                     Dim ToolTipChanErrLogShownLock As New Object
                     Dim thF12Lock As New Object
                     While True AndAlso Me IsNot Nothing AndAlso Me.Visible
                         Try
                             Threading.Thread.Sleep(Timer1.Interval)
                             Dim NowTick As Long = TickCount
                             If LastTick = NowTick Then
                                 UIHangCount += 1
                             Else
                                 UIHangCount = 0
                             End If
                             LastTick = NowTick
                             If UIHangCount > 20 AndAlso DebugPanelAutoShowup Then
                                 Dim th As New Threading.Thread(
                                    Sub()
                                        If Threading.Monitor.TryEnter(thF12Lock) Then
                                            If MessageBox.Show("UI hang detected. Show debug panel?", "Debug", MessageBoxButtons.OKCancel) = DialogResult.OK Then
                                                DebugPanelAutoShowup = False
                                                Dim SP1 As New SettingPanel
                                                SP1.Text = Text
                                                SP1.SelectedObject = Me
                                                SP1.ShowDialog()
                                                DebugPanelAutoShowup = True
                                            End If
                                            Threading.Monitor.Exit(thF12Lock)
                                        End If
                                    End Sub)
                                 If Threading.Monitor.TryEnter(thF12Lock) Then
                                     Threading.Monitor.Exit(thF12Lock)
                                     th.Start()
                                 End If

                             End If
                             If driveHandle <> CType(-1, IntPtr) AndAlso TapeDrive.Length > 0 Then
                                 If Threading.Monitor.TryEnter(TapeUtils.GetSCSIOperationLock(driveHandle), 200) Then
                                     RefreshDriveLEDIndicator()
                                     Threading.Monitor.Exit(TapeUtils.GetSCSIOperationLock(driveHandle))
                                 End If
                             End If
                             If ToolTipChanErrLogShowing Then
                                 If Not ToolTipChanErrLogShown Then
                                     BeginInvoke(Sub() ToolTipChanErrLog.Show(CapLossChannelInfo, StatusStrip2, New Point(ToolStripStatusLabelErrLog.Bounds.Right - 1, ToolStripStatusLabelErrLog.Bounds.Bottom - 1)))
                                     ToolTipChanErrLogShown = True
                                 End If
                             Else
                                 ToolTipChanErrLogShown = False
                             End If
                         Catch ex As Exception

                         End Try
                     End While
                 End Sub)
        LoadComplete = True
        If driveOpened Then
            TapeUtils.CheckSwitchConfig(driveHandle)
            BeginInvoke(Sub() 读取索引ToolStripMenuItem_Click(sender, e))
        End If
    End Sub
    Private Sub LTFSWriter_Closing(sender As Object, e As CancelEventArgs) Handles Me.Closing
        Static ForceCloseCount As Integer = 0
        e.Cancel = False
        If Not AllowOperation Then
            If ForceCloseCount < 3 Then
                MessageBox.Show(New Form With {.TopMost = True}, My.Resources.ResText_X0)
            Else
                If MessageBox.Show(New Form With {.TopMost = True}, My.Resources.ResText_X1, My.Resources.ResText_Warning, MessageBoxButtons.OKCancel) = DialogResult.OK Then
                    Save_Settings()
                    e.Cancel = False
                    Exit Sub
                End If
            End If
            ForceCloseCount += 1
            e.Cancel = True
            Exit Sub
        End If
        If TotalBytesUnindexed > 0 Then
            If MessageBox.Show(New Form With {.TopMost = True}, My.Resources.ResText_X2, My.Resources.ResText_Warning, MessageBoxButtons.YesNo) = DialogResult.No Then
                e.Cancel = True
                Exit Sub
            Else
                ErrLRRefreshed.Set()
                Save_Settings()
                e.Cancel = False
                UFReadCount.Value = 0
                Exit Sub
            End If
        End If
        If ExtraPartitionCount > 0 AndAlso Modified Then
            If MessageBox.Show(New Form With {.TopMost = True}, My.Resources.ResText_X3, My.Resources.ResText_Warning, MessageBoxButtons.YesNo) = DialogResult.No Then
                e.Cancel = True
            Else
                ErrLRRefreshed.Set()
                Save_Settings()
                e.Cancel = False
                UFReadCount.Value = 0
            End If
        End If
        Save_Settings()
    End Sub
    Dim CurrDrive As TapeUtils.BlockDevice
    Public Function GetLocInfo() As String
        Dim DriveInfo As String = ""
        Dim IsOpened As Boolean = TapeUtils.IsOpened(driveHandle)
        If IsOpened Then
            If CurrDrive Is Nothing AndAlso Threading.Monitor.TryEnter(TapeUtils.GetSCSIOperationLock(driveHandle)) Then
                Try
                    CurrDrive = TapeUtils.Inquiry(driveHandle)
                Catch ex As Exception
                    PrintMsg(ex.ToString(), Warning:=True, LogOnly:=True)
                End Try
                Threading.Monitor.Exit(TapeUtils.GetSCSIOperationLock(driveHandle))
            End If
        End If
        If CurrDrive IsNot Nothing Then
            If TapeUtils.TagDictionary.ContainsKey(CurrDrive.SerialNumber) Then
                DriveInfo = $" {TapeUtils.TagDictionary(CurrDrive.SerialNumber)}{If(Not IsOpened, $" ({My.Resources.ResText_NotOpened})", "")}"
            Else
                DriveInfo = $" {CurrDrive.SerialNumber} {CurrDrive.VendorId} {CurrDrive.ProductId}{If(Not IsOpened, $" ({My.Resources.ResText_NotOpened})", "")}"
            End If
        End If
        If schema Is Nothing Then Return $"{My.Resources.ResText_NIndex} [{TapeDrive}{DriveInfo}] - {ApplicationWheels.ApplicationInfo} ({TapeUtils.DriverTypeSetting})"
        Dim info As String = $"{Barcode.TrimEnd()} ".TrimStart()
        If TapeDrive <> "" Then info &= $"[{TapeDrive}{DriveInfo}] "
        Try
            SyncLock schema
                info &= $"{My.Resources.ResText_Index}{schema.generationnumber} - {My.Resources.ResText_Partition}{schema.location.partition} - {My.Resources.ResText_Block}{schema.location.startblock}"
                If schema.previousgenerationlocation IsNot Nothing Then
                    If schema.previousgenerationlocation.startblock > 0 Then info &= $" ({My.Resources.ResText_Previous}:{My.Resources.ResText_Partition}{schema.previousgenerationlocation.partition} - {My.Resources.ResText_Block}{schema.previousgenerationlocation.startblock})"
                End If
            End SyncLock
            If CurrentHeight > 0 Then info &= $" {My.Resources.ResText_WritePointer}{CurrentHeight}"
            If Modified Then info &= "*"
            info &= $" - {ApplicationWheels.ApplicationInfo} ({TapeUtils.DriverTypeSetting})"
        Catch ex As Exception
            PrintMsg(My.Resources.ResText_RPosErr)
            SetStatusLight(LWStatus.Err)
        End Try
        Return info
    End Function
    Public Function GetProgressImage(ByVal value As Integer, ByVal maximum As Integer, ByVal color As Color) As Bitmap
        If maximum = 0 Then Return Nothing
        Dim result As New Bitmap(100, 1)
        Dim bd As Imaging.BitmapData = result.LockBits(New Rectangle(0, 0, 100, 1), Imaging.ImageLockMode.ReadWrite, Imaging.PixelFormat.Format24bppRgb)
        Dim b(bd.Stride - 1) As Byte
        value = Math.Max(0, value)
        value = Math.Min(value, maximum)
        For i As Integer = 0 To b.Length - 1
            b(i) = 255
        Next
        For i As Integer = 0 To CInt(value / maximum * 99)
            b(i * 3 + 0) = color.B
            b(i * 3 + 1) = color.G
            b(i * 3 + 2) = color.R
        Next
        Marshal.Copy(b, 0, bd.Scan0, b.Length)
        result.UnlockBits(bd)
        Return result
    End Function
    <Category("LTFSWriter")>
    Public Property MaxCapacity As Long = 0
    <Category("LTFSWriter")>
    Public Property CapacityLogPage As TapeUtils.PageData
    <Category("LTFSWriter")>
    Public Property VolumeStatisticsLogPage As TapeUtils.PageData
    Public Property DeviceStatusLogPage As TapeUtils.PageData
    Public Property DTDStatusLogPage As TapeUtils.PageData
    Public Sub RefreshDriveLEDIndicator()
        Dim logdataDSLP As Byte()
        Dim logdataDTD As Byte()
        Task.Run(Sub()
                     If Threading.Monitor.TryEnter(TapeUtils.GetSCSIOperationLock(driveHandle), 500) Then
                         Try
                             logdataDSLP = TapeUtils.LogSense(driveHandle, &H3E, 0, PageControl:=1)
                             logdataDTD = TapeUtils.LogSense(driveHandle, &H11, 0, PageControl:=1)
                             Threading.Monitor.Exit(TapeUtils.GetSCSIOperationLock(driveHandle))
                         Catch ex As Exception
                             Threading.Monitor.Exit(TapeUtils.GetSCSIOperationLock(driveHandle))
                             PrintMsg(ex.ToString(), LogOnly:=True)
                             Exit Sub
                         End Try
                         Try
                             Invoke(Sub()
                                        Try
                                            DeviceStatusLogPage = TapeUtils.PageData.CreateDefault(TapeUtils.PageData.DefaultPages.HPLTO6_DeviceStatusLogPage, logdataDSLP)
                                            DTDStatusLogPage = TapeUtils.PageData.CreateDefault(TapeUtils.PageData.DefaultPages.HPLTO6_DataTransferDeviceStatusLogPage, logdataDTD)
                                            Dim Page1 As TapeUtils.PageData.DataItem.DynamicParamPage = DeviceStatusLogPage.TryGetPage(&H1)
                                            If Page1 Is Nothing Then Exit Sub
                                            Dim DevStatusBits As TapeUtils.PageData = Page1.GetPage
                                            Dim TapeFlag, DriveFlag, CleanFlag, EncryptionFlag As Boolean
                                            If DevStatusBits IsNot Nothing Then
                                                For Each item As TapeUtils.PageData.DataItem In DevStatusBits.Items
                                                    Select Case item.Name.ToLower
                                                        Case "cleaning required flag"
                                                            CleanFlag = CleanFlag Or (item.RawData(0) <> 0)
                                                        Case "cleaning requested flag"
                                                            CleanFlag = CleanFlag Or (item.RawData(0) <> 0)
                                                        Case "device status"
                                                            DriveFlag = (item.RawData(0) > 1)
                                                        Case "medium status"
                                                            TapeFlag = (item.RawData(0) > 1)
                                                    End Select
                                                Next
                                            End If
                                            Dim VHFData As TapeUtils.PageData = DTDStatusLogPage.TryGetPage(0).GetPage
                                            If VHFData IsNot Nothing Then
                                                For Each item As TapeUtils.PageData.DataItem In VHFData.Items
                                                    Select Case item.Name.ToLower
                                                        Case "encryption parameters present"
                                                            EncryptionFlag = (item.RawData(0) = 1)
                                                    End Select
                                                Next
                                            End If
                                            If EncryptionFlag Then
                                                ToolStripStatusLabelS2.ForeColor = Color.Blue
                                            Else
                                                ToolStripStatusLabelS2.ForeColor = Color.Gray
                                            End If
                                            If CleanFlag Then
                                                ToolStripStatusLabelS3.ForeColor = Color.Orange
                                            Else
                                                ToolStripStatusLabelS3.ForeColor = Color.Gray
                                            End If
                                            If TapeFlag Then
                                                ToolStripStatusLabelS4.ForeColor = Color.Orange
                                            Else
                                                ToolStripStatusLabelS4.ForeColor = Color.Gray
                                            End If
                                            If DriveFlag Then
                                                ToolStripStatusLabelS5.ForeColor = Color.Orange
                                            Else
                                                ToolStripStatusLabelS5.ForeColor = Color.Gray
                                            End If
                                        Catch ex As Exception
                                            PrintMsg(ex.ToString(), LogOnly:=True)
                                        End Try
                                    End Sub)
                         Catch
                         End Try

                     End If
                 End Sub)
    End Sub
    Public Property CapLossChannelInfo As String
    Public Sub SetCapLossChannelInfo(Text As String)
        CapLossChannelInfo = Text
    End Sub
    Public CMOnce As TapeUtils.CMParser
    Public GenAbbr As String = ""

    <Category("LTFSWriter")>
    Public Property LastRemainCapValue As Long

    Public Function RefreshCapacity() As Long()
        Dim result(3) As Long
        Dim logdataCap As Byte() = TapeUtils.LogSense(driveHandle, &H31, 0, PageControl:=1)
        Dim logdataVStat As Byte() = TapeUtils.LogSense(driveHandle, &H17, 0, PageControl:=1)
        Try
            CapacityLogPage = TapeUtils.PageData.CreateDefault(TapeUtils.PageData.DefaultPages.HPLTO6_TapeCapacityLogPage, logdataCap)
            Dim Gen As Integer, WORM As Boolean, WP As Boolean, GenStr As String = ""
            If logdataVStat Is Nothing OrElse logdataVStat.Length <= 4 Then
                If CMOnce Is Nothing Then CMOnce = New TapeUtils.CMParser(driveHandle)
                If CMOnce IsNot Nothing Then
                    GenStr = CMOnce.CartridgeMfgData.CartridgeTypeAbbr
                End If
            Else
                VolumeStatisticsLogPage = TapeUtils.PageData.CreateDefault(TapeUtils.PageData.DefaultPages.HPLTO6_VolumeStatisticsLogPage, logdataVStat)
                Dim GenPage As TapeUtils.PageData.DataItem.DynamicParamPage = VolumeStatisticsLogPage.TryGetPage(&H45)
                If GenPage IsNot Nothing Then
                    Dim GenStr0 As String = GenPage.GetString()
                    Integer.TryParse(IOManager.ExtractNumericString(GenStr0), Gen)
                    If Not GenStr0.ToUpper().Contains("T10K") Then
                        GenStr = $"L{Gen}"
                    Else
                        Gen = Integer.Parse(GenStr0.Last)
                        GenStr = $"T{Gen}"
                    End If
                    If Gen = 7 OrElse Gen = 8 Then
                        If CMOnce Is Nothing Then CMOnce = New TapeUtils.CMParser(driveHandle)
                        If CMOnce IsNot Nothing Then
                            GenStr = CMOnce.CartridgeMfgData.CartridgeTypeAbbr
                        End If
                    End If
                Else
                    If CMOnce Is Nothing Then CMOnce = New TapeUtils.CMParser(driveHandle)
                    If CMOnce IsNot Nothing Then
                        GenStr = CMOnce.CartridgeMfgData.CartridgeTypeAbbr
                    End If
                End If
                GenAbbr = GenStr
                Dim WORMPage As TapeUtils.PageData.DataItem.DynamicParamPage = VolumeStatisticsLogPage.TryGetPage(&H81)
                If WORMPage IsNot Nothing Then WORM = ((WORMPage.LastByte) <> 0)
                Dim WPPage As TapeUtils.PageData.DataItem.DynamicParamPage = VolumeStatisticsLogPage.TryGetPage(&H80)
                If WPPage IsNot Nothing Then WP = ((WPPage.LastByte) <> 0)
            End If
            Dim errRate As Double = 0
            If Gen > 0 Then errRate = ReadChanLRInfo()
            PrintMsg($"[RefreshCapacity] ErrRateLogValue: {errRate}", LogOnly:=True)
            RefreshDriveLEDIndicator()

            Dim MediaDescription As String = $"{GenStr}"
            If WORM Then MediaDescription &= " WORM"
            If WP Then MediaDescription &= " RO" Else MediaDescription &= " RW"

            Dim cap0, cap1, max0, max1 As Long
            Dim cp0 As TapeUtils.PageData.DataItem.DynamicParamPage = CapacityLogPage.TryGetPage(1)
            Dim cp1 As TapeUtils.PageData.DataItem.DynamicParamPage = CapacityLogPage.TryGetPage(2)
            Dim mp0 As TapeUtils.PageData.DataItem.DynamicParamPage = CapacityLogPage.TryGetPage(3)
            Dim mp1 As TapeUtils.PageData.DataItem.DynamicParamPage = CapacityLogPage.TryGetPage(4)
            If cp0 IsNot Nothing Then cap0 = cp0.GetLong
            If cp1 IsNot Nothing Then cap1 = cp1.GetLong
            If mp0 IsNot Nothing Then max0 = mp0.GetLong
            If mp1 IsNot Nothing Then max1 = mp1.GetLong
            If cp0 Is Nothing Then
                Select Case TapeUtils.DriverTypeSetting
                    Case TapeUtils.DriverType.IBM3592
                        Dim mp23h As Byte() = TapeUtils.ModeSense(driveHandle, &H23, SkipHeader:=False)
                        Select Case (CShort(mp23h(4 + 12)) << 8 Or mp23h(5 + 12))
                            Case 0
                            Case &H141
                                MediaDescription = "JA"
                            Case &H142
                                MediaDescription = "JB"
                            Case &H143
                                MediaDescription = "JC"
                            Case &H144
                                MediaDescription = "JD"
                            Case &H145
                                MediaDescription = "JE"
                            Case &H146
                                MediaDescription = "JF"
                            Case &H151
                                MediaDescription = "JJ"
                            Case &H152
                                MediaDescription = "JK"
                            Case &H153
                                MediaDescription = "JL"
                            Case &H154
                                MediaDescription = "JM"
                            Case &H241
                                MediaDescription = "JW"
                            Case &H242
                                MediaDescription = "JX"
                            Case &H243
                                MediaDescription = "JY"
                            Case &H244
                                MediaDescription = "JZ"
                            Case &H251
                                MediaDescription = "JR"
                        End Select
                        GenAbbr = MediaDescription
                        Select Case mp23h(6 + 12)
                            Case &H31
                                MediaDescription &= " A1"
                            Case &H32
                                MediaDescription &= " A2"
                            Case &H33
                                MediaDescription &= " A3"
                            Case &H34
                                MediaDescription &= " A4"
                            Case &H35
                                MediaDescription &= " A5"
                            Case &H36
                                MediaDescription &= " B5"
                            Case &H37
                                MediaDescription &= " A6"
                            Case &H39
                                MediaDescription &= " A7"
                            Case &H71
                                MediaDescription &= " A1"
                            Case &H72
                                MediaDescription &= " A2"
                            Case &H73
                                MediaDescription &= " A3"
                            Case &H74
                                MediaDescription &= " A4"
                            Case &H75
                                MediaDescription &= " A5"
                            Case &H76
                                MediaDescription &= " B5"
                            Case &H77
                                MediaDescription &= " A6"
                            Case &H79
                                MediaDescription &= " A7"
                        End Select
                        If (((mp23h(10 + 12) >> 7) And 1) = 1) Then
                            MediaDescription &= " RO(Phy)"
                            WP = True
                        ElseIf (((mp23h(10 + 12) >> 6) And 1) = 1) Then
                            MediaDescription &= " RO(Asc)"
                            WP = True
                        ElseIf (((mp23h(10 + 12) >> 4) And 1) = 1) Then
                            MediaDescription &= " RO(Pst)"
                            WP = True
                        ElseIf (((mp23h(10 + 12) >> 0) And 1) = 1) Then
                            MediaDescription &= " RO(Pmn)"
                            WP = True
                        Else
                            MediaDescription &= " RW"
                        End If
                        Dim mps As TapeUtils.PageData.DataItem.DynamicParamPage = VolumeStatisticsLogPage.TryGetPage(&H202)
                        If mps Is Nothing Then
                            mp0 = VolumeStatisticsLogPage.TryGetPage(&H16)
                            cp0 = VolumeStatisticsLogPage.TryGetPage(&H17)
                            If mp0 IsNot Nothing Then max0 = mp0.GetLong
                            If cp0 IsNot Nothing Then cap0 = max0 - cp0.GetLong
                        Else
                            Dim cps As TapeUtils.PageData.DataItem.DynamicParamPage = VolumeStatisticsLogPage.TryGetPage(&H203)
                            Dim cpsdata As Byte() = cps.RawData
                            Dim mpsdata As Byte() = mps.RawData
                            For i As Integer = If(cpsdata.Length Mod 8 = 0, 0, 4) To cpsdata.Length - 1 Step 8
                                If cpsdata(i + 2) = 0 Then
                                    If cpsdata(i + 3) = 0 Then
                                        cap0 = cpsdata(i + 4)
                                        cap0 <<= 8
                                        cap0 = cap0 Or cpsdata(i + 5)
                                        cap0 <<= 8
                                        cap0 = cap0 Or cpsdata(i + 6)
                                        cap0 <<= 8
                                        cap0 = cap0 Or cpsdata(i + 7)
                                    ElseIf cpsdata(i + 3) = 1 Then
                                        cap1 = cpsdata(i + 4)
                                        cap1 <<= 8
                                        cap1 = cap1 Or cpsdata(i + 5)
                                        cap1 <<= 8
                                        cap1 = cap1 Or cpsdata(i + 6)
                                        cap1 <<= 8
                                        cap1 = cap1 Or cpsdata(i + 7)
                                    End If
                                End If
                            Next
                            For i As Integer = If(cpsdata.Length Mod 8 = 0, 0, 4) To mpsdata.Length - 1 Step 8
                                If mpsdata(i + 2) = 0 Then
                                    If mpsdata(i + 3) = 0 Then
                                        max0 = mpsdata(i + 4)
                                        max0 <<= 8
                                        max0 = max0 Or mpsdata(i + 5)
                                        max0 <<= 8
                                        max0 = max0 Or mpsdata(i + 6)
                                        max0 <<= 8
                                        max0 = max0 Or mpsdata(i + 7)
                                    ElseIf mpsdata(i + 3) = 1 Then
                                        max1 = mpsdata(i + 4)
                                        max1 <<= 8
                                        max1 = max1 Or mpsdata(i + 5)
                                        max1 <<= 8
                                        max1 = max1 Or mpsdata(i + 6)
                                        max1 <<= 8
                                        max1 = max1 Or mpsdata(i + 7)
                                    End If
                                End If
                            Next
                            cap0 = max0 - cap0
                            cap1 = max1 - cap1
                        End If
                End Select
            End If


            'cap0 = TapeUtils.MAMAttribute.FromTapeDrive(TapeDrive, 0, 0, 0).AsNumeric
            Dim loss As Long
            If My.Settings.LTFSWriter_ShowLoss Then
                Dim CMInfo As TapeUtils.CMParser
                Try
                    Dim errormsg As Exception = Nothing
                    CMInfo = New TapeUtils.CMParser(TapeUtils.ReceiveDiagCM(driveHandle, TapeUtils.CMParser.Cartridge_mfg.GetCMLength($"L{Gen}")), errormsg)
                    If errormsg IsNot Nothing Then Throw errormsg
                Catch ex As Exception
                    CMInfo = New TapeUtils.CMParser(driveHandle)
                End Try
                Dim nLossDS As Long = 0
                Dim DataSize As New List(Of Long)
                If CMInfo.CartridgeMfgData.CartridgeTypeAbbr = "CU" Then Exit Try
                Dim StartBlock As Integer = 0
                Dim CurrSize As Long = 0
                Dim gw As Boolean = False
                For wn As Integer = 0 To CMInfo.a_NWraps - 1
                    Dim StartBlockStr As String = StartBlock.ToString()
                    If CMInfo.TapeDirectoryData.CapacityLoss(wn) = -1 Or CMInfo.TapeDirectoryData.CapacityLoss(wn) = -3 Then StartBlockStr = ""
                    Dim EndBlock As Integer = StartBlock + CMInfo.TapeDirectoryData.WrapEntryInfo(wn).RecCount + CMInfo.TapeDirectoryData.WrapEntryInfo(wn).FileMarkCount - 1
                    If CMInfo.TapeDirectoryData.CapacityLoss(wn) = -2 Then EndBlock += 1
                    StartBlock += CMInfo.TapeDirectoryData.WrapEntryInfo(wn).RecCount + CMInfo.TapeDirectoryData.WrapEntryInfo(wn).FileMarkCount
                    If CMInfo.TapeDirectoryData.CapacityLoss(wn) >= 0 Then
                        nLossDS += Math.Max(0, CMInfo.a_SetsPerWrap - CMInfo.TapeDirectoryData.DatasetsOnWrapData(wn).Data)
                        CurrSize += CMInfo.TapeDirectoryData.DatasetsOnWrapData(wn).Data
                    ElseIf CMInfo.TapeDirectoryData.CapacityLoss(wn) = -1 Then
                        StartBlock = 0
                    ElseIf CMInfo.TapeDirectoryData.CapacityLoss(wn) = -2 Then
                        CurrSize += CMInfo.TapeDirectoryData.DatasetsOnWrapData(wn).Data
                    ElseIf CMInfo.TapeDirectoryData.CapacityLoss(wn) = -3 Then
                        StartBlock = 0
                        If gw Then
                            DataSize.Add(CurrSize)
                            CurrSize = 0
                            gw = False
                        Else
                            gw = True
                        End If
                    End If
                Next
                loss = nLossDS * CMInfo.CartridgeMfgData.KB_PER_DATASET * 1000

            End If
            Dim lshbits As Byte = 20

            'DAT Unit in KB
            If Gen = 0 AndAlso max0 > 20 * 1024 * 1024 Then lshbits = 10
            If ExtraPartitionCount > 0 Then
                MaxCapacity = max1
                If MaxCapacity = 0 Then MaxCapacity = TapeUtils.MAMAttribute.FromTapeDrive(driveHandle, 0, 1, 1).AsNumeric
                LastRemainCapValue = cap1 << lshbits
                'cap1 = TapeUtils.MAMAttribute.FromTapeDrive(TapeDrive, 0, 0, 1).AsNumeric
                Invoke(Sub()
                           ToolStripStatusLabel2.Text = $"{MediaDescription} {My.Resources.ResText_CapRem} P0:{IOManager.FormatSize(cap0 << lshbits)} P1:{IOManager.FormatSize(cap1 << lshbits)}"
                           ToolStripStatusLabel2.ToolTipText = $"{MediaDescription} {My.Resources.ResText_CapRem} P0:{LTFSConfigurator.ReduceDataUnit(cap0 >> (20 - lshbits))}/{LTFSConfigurator.ReduceDataUnit(max0 >> (20 - lshbits))} P1:{LTFSConfigurator.ReduceDataUnit(cap1 >> (20 - lshbits))}/{LTFSConfigurator.ReduceDataUnit(max1 >> (20 - lshbits))}"
                           If WP Then
                               ToolStripStatusLabel2.BackgroundImage = GetProgressImage(CInt(MaxCapacity - cap1), CInt(MaxCapacity), Color.FromArgb(191, 191, 191))
                           Else
                               If cap1 >= 4096 Then
                                   ToolStripStatusLabel2.BackgroundImage = GetProgressImage(CInt(MaxCapacity - cap1), CInt(MaxCapacity), Color.FromArgb(121, 196, 232))
                               Else
                                   ToolStripStatusLabel2.BackgroundImage = GetProgressImage(CInt(MaxCapacity - cap1), CInt(MaxCapacity), Color.FromArgb(255, 127, 127))
                               End If
                           End If

                       End Sub)
                result(2) = max1 - cap1
                result(3) = max1
            Else
                MaxCapacity = max0
                LastRemainCapValue = cap0 << lshbits
                Try
                    If MaxCapacity = 0 Then
                        Dim MAMCap As TapeUtils.MAMAttribute = TapeUtils.MAMAttribute.FromTapeDrive(driveHandle, 0, 1, 0)
                        If MAMCap IsNot Nothing Then MaxCapacity = MAMCap.AsNumeric
                    End If
                Catch ex As Exception
                End Try
                Invoke(Sub()
                           'If cap0 < 0 Then Exit Sub
                           ToolStripStatusLabel2.Text = $"{MediaDescription} {My.Resources.ResText_CapRem} P0:{IOManager.FormatSize(cap0 << lshbits)}"
                           ToolStripStatusLabel2.ToolTipText = $"{MediaDescription} {My.Resources.ResText_CapRem} P0:{LTFSConfigurator.ReduceDataUnit(cap0 >> (20 - lshbits))}/{LTFSConfigurator.ReduceDataUnit(max0 >> (20 - lshbits))}"
                           If WP Then
                               ToolStripStatusLabel2.BackgroundImage = GetProgressImage(CInt(MaxCapacity - cap0), CInt(MaxCapacity), Color.FromArgb(191, 191, 191))
                           Else
                               If cap0 >= 4096 Then
                                   ToolStripStatusLabel2.BackgroundImage = GetProgressImage(CInt(MaxCapacity - cap0), CInt(MaxCapacity), Color.FromArgb(121, 196, 232))
                               Else
                                   ToolStripStatusLabel2.BackgroundImage = GetProgressImage(CInt(MaxCapacity - cap0), CInt(MaxCapacity), Color.FromArgb(255, 127, 127))
                               End If
                           End If

                       End Sub)

            End If
            result(0) = max0 - cap0
            result(1) = max0
            'If errRate < 0 Then
            '    Invoke(Sub()
            '               ToolStripStatusLabel2.Text &= $" Err:{errRate.ToString("f2")}"
            '               ChanInfo &= $" Err:{errRate.ToString("f2")}"
            '           End Sub)
            'End If

            If My.Settings.LTFSWriter_ShowLoss Then
                Invoke(Sub()
                           ToolStripStatusLabel2.Text &= $" Loss:{IOManager.FormatSize(loss)}"
                           ToolStripStatusLabel2.ToolTipText &= $" Loss:{IOManager.FormatSize(loss)}"
                       End Sub)
            End If
            LastRefresh = Now
            PrintMsg($"[RefreshCapacity] Finished {result(0)} {result(1)} {result(2)} {result(3)}", LogOnly:=True)
            GC.Collect()
        Catch ex As Exception
            PrintMsg(My.Resources.ResText_RCErr, TooltipText:=ex.ToString)
            SetStatusLight(LWStatus.Err)
        End Try
        Return result
    End Function
    <Category("LTFSWriter")>
    Public ReadOnly Property GetCapacityMegaBytes As Long
        Get
            If Threading.Monitor.TryEnter(TapeUtils.GetSCSIOperationLock(driveHandle)) Then
                Threading.Monitor.Exit(TapeUtils.GetSCSIOperationLock(driveHandle))
                If ExtraPartitionCount > 0 Then
                    Return TapeUtils.MAMAttribute.FromTapeDrive(driveHandle, 0, 0, 1).AsNumeric
                Else
                    Return TapeUtils.MAMAttribute.FromTapeDrive(driveHandle, 0, 0, 0).AsNumeric
                End If
            Else
                Return 0
            End If
        End Get
    End Property

    Private Function TryGetTarVirtualRoot(source As ltfsindex.file, ByRef root As TarVirtualDirectory) As Boolean
        root = Nothing
        If source Is Nothing Then Return False
        Dim encoded As String = source.GetXAttr(ltfsindex.file.xattr.ApplicationSpecific.TarMetadata, True)
        Dim metadata As TarMetadata = Nothing
        If Not TarMetadataCodec.TryDecode(encoded, metadata) Then Return False
        Return TarVirtualTreeBuilder.TryBuild(source, metadata, root)
    End Function

    Private NotInheritable Class WriterTreeNode
        Inherits TreeNode

        Public Property ChildrenLoaded As Boolean
        Public Property ChildrenComplete As Boolean
        Public Property NextDirectoryIndex As Integer
        Public Property NextFileIndex As Integer
        Public Property FileChildrenScanned As Boolean
        Public FileMetadataScanQueued As Integer
        Public Property IsPlaceholder As Boolean
    End Class

    Private Function CreateDirectoryTreeNode(directory As ltfsindex.directory, rootNode As Boolean) As WriterTreeNode
        If directory Is Nothing Then Return Nothing
        'Lazy directory wrappers are recreated while rebuilding the tree.  Keep
        'using the instance owned by the pending queue so its UnwrittenFiles
        'collection remains visible when an inner directory is selected again.
        directory = ResolvePendingDirectory(directory)
        Dim nodeText As String = If(directory.name, String.Empty)
        If Not rootNode AndAlso My.Settings.LTFSWriter_ShowFileCount Then
            Dim pendingFileCount As Long = GetPendingTreeFileCount(directory)
            If pendingFileCount = 0 Then
                nodeText = $"{directory.TotalFiles.ToString.PadRight(6)}| {directory.name}"
            Else
                nodeText = $"{$"{directory.TotalFiles.ToString}+{pendingFileCount.ToString}".PadRight(6)}| {directory.name}"
            End If
        End If
        Dim directoryCount As Integer = directory.GetLazyDirectDirectoryCount()
        Dim fileCount As Integer = directory.GetLazyDirectFileCount()
        'Regular files are displayed in the list view.  Their metadata still
        'has to be scanned on demand because archive/tarmeta files add virtual
        'tree children.
        Return New WriterTreeNode With {
            .Text = nodeText,
            .Tag = directory,
            .ImageIndex = If(rootNode, 0, 1),
            .SelectedImageIndex = If(rootNode, 0, 1),
            .StateImageIndex = If(rootNode, 0, 1),
            .ChildrenComplete = directoryCount = 0,
            .FileChildrenScanned = fileCount = 0}
    End Function

    Private Function CreateArchiveTreeNode(file As ltfsindex.file) As TreeNode
        If file Is Nothing Then Return Nothing
        Return New TreeNode With {
            .Text = $"*{file.name}",
            .Tag = file,
            .ImageIndex = 3,
            .SelectedImageIndex = 3,
            .StateImageIndex = 3}
    End Function

    Private Function CreateTarDirectoryTreeNode(directory As TarVirtualDirectory) As WriterTreeNode
        If directory Is Nothing Then Return Nothing
        Return New WriterTreeNode With {
            .Text = directory.Name,
            .Tag = directory,
            .ImageIndex = 1,
            .SelectedImageIndex = 1,
            .StateImageIndex = 1,
            .ForeColor = Color.Blue,
            .ChildrenComplete = directory.Directories Is Nothing OrElse directory.Directories.Count = 0}
    End Function

    Private Function HasPotentialTreeChildren(node As TreeNode) As Boolean
        If node Is Nothing OrElse node.Tag Is Nothing Then Return False
        If TypeOf node.Tag Is ltfsindex.directory Then
            Dim directory As ltfsindex.directory = DirectCast(node.Tag, ltfsindex.directory)
            'Only direct subdirectories are tree children.  Direct files are
            'handled by the list view and must not produce an ellipsis node.
            Return directory.GetLazyDirectDirectoryCount() > 0
        End If
        If TypeOf node.Tag Is TarVirtualDirectory Then
            Dim directory As TarVirtualDirectory = DirectCast(node.Tag, TarVirtualDirectory)
            Return directory.Directories IsNot Nothing AndAlso directory.Directories.Count > 0
        End If
        Return False
    End Function

    Private Sub AddUnloadedChildMarker(node As TreeNode)
        Dim writerNode As WriterTreeNode = TryCast(node, WriterTreeNode)
        If writerNode Is Nothing OrElse writerNode.ChildrenComplete OrElse HasUnloadedChildMarker(node) Then Return
        If Not HasPotentialTreeChildren(node) Then Return
        Dim marker As New WriterTreeNode With {
            .Text = "...",
            .IsPlaceholder = True,
            .ChildrenComplete = True}
        'Keep the normal expand glyph so the user can request the next page.
        marker.Nodes.Add(New TreeNode())
        node.Nodes.Add(marker)
    End Sub

    Private Shared Function HasUnloadedChildMarker(node As TreeNode) As Boolean
        If node Is Nothing Then Return False
        For Each child As TreeNode In node.Nodes
            Dim writerChild As WriterTreeNode = TryCast(child, WriterTreeNode)
            If writerChild IsNot Nothing AndAlso writerChild.IsPlaceholder Then Return True
        Next
        Return False
    End Function

    Private Sub RemoveUnloadedChildMarker(node As TreeNode)
        If node Is Nothing Then Return
        For i As Integer = node.Nodes.Count - 1 To 0 Step -1
            Dim child As WriterTreeNode = TryCast(node.Nodes(i), WriterTreeNode)
            If child IsNot Nothing AndAlso child.IsPlaceholder Then node.Nodes.RemoveAt(i)
        Next
    End Sub

    Private Sub AddTarVirtualChildren(directory As TarVirtualDirectory, node As TreeNode)
        If directory Is Nothing OrElse node Is Nothing Then Return
        Dim writerNode As WriterTreeNode = TryCast(node, WriterTreeNode)
        If writerNode Is Nothing OrElse writerNode.ChildrenComplete Then Return

        Dim directoryCount As Integer = If(directory.Directories Is Nothing, 0, directory.Directories.Count)
        Dim remaining As Integer = WriterTreePageSize
        While remaining > 0 AndAlso writerNode.NextDirectoryIndex < directoryCount
            Dim child As TarVirtualDirectory = directory.Directories(writerNode.NextDirectoryIndex)
            writerNode.NextDirectoryIndex += 1
            If child Is Nothing Then Continue While
            Dim childNode As WriterTreeNode = CreateTarDirectoryTreeNode(child)
            If childNode IsNot Nothing Then
                node.Nodes.Add(childNode)
                AddUnloadedChildMarker(childNode)
            End If
            remaining -= 1
        End While

        writerNode.ChildrenLoaded = True
        writerNode.ChildrenComplete = writerNode.NextDirectoryIndex >= directoryCount
        If Not writerNode.ChildrenComplete Then AddUnloadedChildMarker(node)
    End Sub

    Private Sub QueueDirectoryFileMetadataScan(node As TreeNode)
        Dim writerNode As WriterTreeNode = TryCast(node, WriterTreeNode)
        Dim directory As ltfsindex.directory = If(node Is Nothing, Nothing, TryCast(node.Tag, ltfsindex.directory))
        If writerNode Is Nothing OrElse directory Is Nothing OrElse writerNode.FileChildrenScanned Then Return
        If Threading.Interlocked.Exchange(writerNode.FileMetadataScanQueued, 1) <> 0 Then Return

        Try
            BeginInvoke(
                Sub()
                    Threading.Interlocked.Exchange(writerNode.FileMetadataScanQueued, 0)
                    If IsDisposed OrElse Not Object.ReferenceEquals(node.TreeView, TreeView1) Then Return
                    If Not Object.ReferenceEquals(TreeView1.SelectedNode, node) AndAlso Not node.IsExpanded Then Return
                    LoadTreeNodeChildren(node, scanFilesOnly:=True)
                End Sub)
        Catch ex As ObjectDisposedException
            Threading.Interlocked.Exchange(writerNode.FileMetadataScanQueued, 0)
        Catch ex As InvalidOperationException
            Threading.Interlocked.Exchange(writerNode.FileMetadataScanQueued, 0)
        End Try
    End Sub

    Private Sub LoadTreeNodeChildren(node As TreeNode, Optional scanFilesOnly As Boolean = False)
        Dim writerNode As WriterTreeNode = TryCast(node, WriterTreeNode)
        If writerNode Is Nothing OrElse writerNode.IsPlaceholder Then Return
        If writerNode.ChildrenComplete AndAlso
           (Not TypeOf node.Tag Is ltfsindex.directory OrElse writerNode.FileChildrenScanned) Then Return

        writerNode.ChildrenLoaded = True
        Dim manageUpdate As Boolean = Not _treeRefreshInProgress
        If manageUpdate Then TreeView1.BeginUpdate()
        Try
            RemoveUnloadedChildMarker(node)
            If TypeOf node.Tag Is ltfsindex.directory Then
                Dim directory As ltfsindex.directory = DirectCast(node.Tag, ltfsindex.directory)
                Dim directoryCount As Integer = directory.GetLazyDirectDirectoryCount()
                Dim fileCount As Integer = directory.GetLazyDirectFileCount()
                'Directory rows remain user-paged.  Archive/tarmeta metadata has
                'an independent file page so it is not delayed by a directory
                'that also contains more than 1024 subdirectories.
                Dim remainingDirectories As Integer = If(scanFilesOnly, 0, WriterTreePageSize)

                While remainingDirectories > 0 AndAlso writerNode.NextDirectoryIndex < directoryCount
                    Dim childDirectory As ltfsindex.directory = directory.GetLazyDirectoryAt(writerNode.NextDirectoryIndex)
                    writerNode.NextDirectoryIndex += 1
                    If childDirectory Is Nothing Then Continue While

                    Dim alreadyLoaded As Boolean = False
                    For Each existingNode As TreeNode In node.Nodes
                        Dim existingDirectory As ltfsindex.directory = TryCast(existingNode.Tag, ltfsindex.directory)
                        If SameDirectoryRecord(existingDirectory, childDirectory) Then
                            alreadyLoaded = True
                            Exit For
                        End If
                    Next
                    If Not alreadyLoaded Then
                        Dim childNode As WriterTreeNode = CreateDirectoryTreeNode(childDirectory, False)
                        If childNode IsNot Nothing Then
                            node.Nodes.Add(childNode)
                            AddUnloadedChildMarker(childNode)
                        End If
                    End If
                    remainingDirectories -= 1
                End While

                Dim remainingFiles As Integer = WriterTreePageSize
                While remainingFiles > 0 AndAlso writerNode.NextFileIndex < fileCount
                    Dim file As ltfsindex.file = directory.GetLazyFileAt(writerNode.NextFileIndex)
                    writerNode.NextFileIndex += 1
                    remainingFiles -= 1
                    If file Is Nothing Then Continue While

                    Try
                        Dim archive As String = file.GetXAttr(ltfsindex.file.xattr.ApplicationSpecific.Archive)
                        If String.Equals(archive, "true", StringComparison.OrdinalIgnoreCase) Then
                            Dim archiveNode As TreeNode = CreateArchiveTreeNode(file)
                            If archiveNode IsNot Nothing Then node.Nodes.Add(archiveNode)
                        End If

                        Dim tarRoot As TarVirtualDirectory = Nothing
                        If TryGetTarVirtualRoot(file, tarRoot) Then
                            Dim tarNode As WriterTreeNode = CreateTarDirectoryTreeNode(tarRoot)
                            If tarNode IsNot Nothing Then
                                node.Nodes.Add(tarNode)
                                AddUnloadedChildMarker(tarNode)
                            End If
                        End If
                    Catch
                        'One malformed metadata record must not prevent later
                        'files in a large directory from being scanned.
                    End Try
                End While

                writerNode.FileChildrenScanned = writerNode.NextFileIndex >= fileCount
                Dim directoryChildrenComplete As Boolean = writerNode.NextDirectoryIndex >= directoryCount
                writerNode.ChildrenComplete = directoryChildrenComplete AndAlso
                                               writerNode.FileChildrenScanned
                If Not directoryChildrenComplete Then
                    AddUnloadedChildMarker(node)
                End If
                If Not writerNode.FileChildrenScanned Then
                    QueueDirectoryFileMetadataScan(node)
                End If
            ElseIf TypeOf node.Tag Is TarVirtualDirectory Then
                AddTarVirtualChildren(DirectCast(node.Tag, TarVirtualDirectory), node)
            End If
        Catch
            writerNode.ChildrenLoaded = False
            RemoveUnloadedChildMarker(node)
            AddUnloadedChildMarker(node)
        Finally
            If manageUpdate Then TreeView1.EndUpdate()
        End Try
    End Sub

    Private Function GetTreeNodeName(node As TreeNode) As String
        If node Is Nothing OrElse node.Tag Is Nothing Then Return Nothing
        If TypeOf node.Tag Is ltfsindex.directory Then Return DirectCast(node.Tag, ltfsindex.directory).name
        If TypeOf node.Tag Is TarVirtualDirectory Then Return DirectCast(node.Tag, TarVirtualDirectory).Name
        Return Nothing
    End Function

    Private Shared Function SameDirectoryRecord(left As ltfsindex.directory, right As ltfsindex.directory) As Boolean
        Return DirectoryRecordComparer.Instance.Equals(left, right)
    End Function

    Private Shared Function SameListViewTag(left As Object, right As Object) As Boolean
        If Object.ReferenceEquals(left, right) Then Return True
        Dim leftDirectory As ltfsindex.directory = TryCast(left, ltfsindex.directory)
        Dim rightDirectory As ltfsindex.directory = TryCast(right, ltfsindex.directory)
        Return leftDirectory IsNot Nothing AndAlso rightDirectory IsNot Nothing AndAlso
               SameDirectoryRecord(leftDirectory, rightDirectory)
    End Function

    Private Function FindOrCreateDirectoryTreeChild(parent As TreeNode, childName As String) As TreeNode
        If parent Is Nothing OrElse String.IsNullOrEmpty(childName) Then Return Nothing

        For Each child As TreeNode In parent.Nodes
            If String.Equals(GetTreeNodeName(child), childName, StringComparison.Ordinal) Then Return child
        Next

        Dim parentDirectory As ltfsindex.directory = TryCast(parent.Tag, ltfsindex.directory)
        If parentDirectory Is Nothing Then Return Nothing
        Dim writerParent As WriterTreeNode = TryCast(parent, WriterTreeNode)
        If writerParent IsNot Nothing Then
            While Not writerParent.ChildrenComplete
                Dim previousDirectoryIndex As Integer = writerParent.NextDirectoryIndex
                Dim previousFileIndex As Integer = writerParent.NextFileIndex
                LoadTreeNodeChildren(parent)
                For Each child As TreeNode In parent.Nodes
                    If String.Equals(GetTreeNodeName(child), childName, StringComparison.Ordinal) Then Return child
                Next
                If Not writerParent.ChildrenComplete AndAlso
                   writerParent.NextDirectoryIndex = previousDirectoryIndex AndAlso
                   writerParent.NextFileIndex = previousFileIndex Then
                    Exit While
                End If
            End While
        End If

        Return Nothing
    End Function

    Private Function FindTreeNodeByPath(path As String) As TreeNode
        If String.IsNullOrWhiteSpace(path) Then Return Nothing
        Dim parts() As String = path.Split({"\"c, "/"c}, StringSplitOptions.RemoveEmptyEntries)
        If parts.Length = 0 Then Return Nothing

        Dim current As TreeNode = Nothing
        For Each root As TreeNode In TreeView1.Nodes
            If String.Equals(GetTreeNodeName(root), parts(0), StringComparison.Ordinal) Then
                current = root
                Exit For
            End If
        Next
        If current Is Nothing Then Return Nothing

        For i As Integer = 1 To parts.Length - 1
            Dim nextNode As TreeNode = Nothing
            For Each child As TreeNode In current.Nodes
                If String.Equals(GetTreeNodeName(child), parts(i), StringComparison.Ordinal) Then
                    nextNode = child
                    Exit For
                End If
            Next
            If nextNode Is Nothing Then nextNode = FindOrCreateDirectoryTreeChild(current, parts(i))
            If nextNode Is Nothing Then Return Nothing
            current = nextNode
        Next
        Return current
    End Function

    Public Sub RefreshDisplay()
        If schema Is Nothing Then Exit Sub
        Invoke(
            Sub()
                _lastListRefreshTag = Nothing
                RefreshPendingTreeCounts()
                If My.Settings.LTFSWriter_ShowFileCount AndAlso schema._directory IsNot Nothing AndAlso schema._directory.Count > 0 Then schema._directory(0).DeepRefreshCount()
                Dim oldSelectPath As String = String.Empty
                If TreeView1.SelectedNode IsNot Nothing AndAlso TreeView1.SelectedNode.Tag IsNot Nothing Then
                    oldSelectPath = GetPath(TreeView1.SelectedNode)
                End If
                If String.IsNullOrEmpty(oldSelectPath) AndAlso TypeOf ListView1.Tag Is ltfsindex.directory AndAlso TreeView1.TopNode IsNot Nothing Then
                    oldSelectPath = GetPath(TreeView1.TopNode)
                End If

                _treeRefreshInProgress = True
                TreeView1.BeginUpdate()
                Try
                    LastSelectedNode = Nothing
                    TreeView1.Nodes.Clear()
                    SyncLock schema._directory
                        If schema._directory IsNot Nothing Then
                            For Each directory As ltfsindex.directory In schema._directory
                                Dim rootNode As WriterTreeNode = CreateDirectoryTreeNode(directory, True)
                                TreeView1.Nodes.Add(rootNode)
                                AddUnloadedChildMarker(rootNode)
                            Next
                        End If
                    End SyncLock

                    If TreeView1.Nodes.Count > 0 Then
                        Dim selectedNode As TreeNode = FindTreeNodeByPath(oldSelectPath)
                        If selectedNode Is Nothing Then selectedNode = TreeView1.TopNode
                        TreeView1.SelectedNode = selectedNode
                        selectedNode.Expand()
                        selectedNode.EnsureVisible()
                    End If
                Catch
                Finally
                    TreeView1.EndUpdate()
                    _treeRefreshInProgress = False
                End Try
                If TreeView1.SelectedNode IsNot Nothing AndAlso TreeView1.SelectedNode.Tag IsNot Nothing Then
                    TriggerTreeView1Event(True)
                End If
                Try
                    Text = GetLocInfo()
                    ToolStripStatusLabel4.Text = $"{My.Resources.ResText_DNW} {IOManager.FormatSize(CLng(UnwrittenSize))}"
                    ToolStripStatusLabel4.ToolTipText = ToolStripStatusLabel4.Text
                Catch ex As Exception
                    PrintMsg(My.Resources.ResText_RDErr)
                    SetStatusLight(LWStatus.Err)
                End Try

            End Sub)
    End Sub

    Private Sub RequestListDisplayRefresh()
        If IsDisposed OrElse Not IsHandleCreated Then Return
        Threading.Interlocked.Increment(_listDisplayRefreshVersion)
        QueueListDisplayRefresh()
    End Sub

    Private Sub QueueListDisplayRefresh()
        If IsDisposed OrElse Not IsHandleCreated Then Return
        If Threading.Interlocked.Exchange(_listDisplayRefreshPending, 1) <> 0 Then Return

        Try
            BeginInvoke(
                Sub()
                    Dim renderedVersion As Long = 0
                    Try
                        Do
                            renderedVersion = Threading.Volatile.Read(_listDisplayRefreshVersion)
                            'Virtual list items cache both values and colors.  Drop
                            'the cache before invalidating so completed work is
                            'visible immediately.
                            _listItemCache.Clear()
                            _listItemCacheOrder.Clear()
                            RefreshLazyListRowCounts()
                            Try
                                If Not IsDisposed AndAlso ListView1.IsHandleCreated Then
                                    If ListView1.VirtualListSize > 0 Then
                                        'RedrawItems causes the native virtual list to
                                        'request fresh items for the invalidated range.
                                        ListView1.RedrawItems(0, ListView1.VirtualListSize - 1, False)
                                    Else
                                        ListView1.Invalidate()
                                        ListView1.Update()
                                    End If
                                End If
                            Catch ex As ObjectDisposedException
                            Catch ex As InvalidOperationException
                            Catch ex As ArgumentException
                            End Try
                        Loop While renderedVersion <> Threading.Volatile.Read(_listDisplayRefreshVersion)
                    Finally
                        Threading.Interlocked.Exchange(_listDisplayRefreshPending, 0)
                        If renderedVersion <> Threading.Volatile.Read(_listDisplayRefreshVersion) Then
                            QueueListDisplayRefresh()
                        End If
                    End Try
                End Sub)
        Catch ex As ObjectDisposedException
            Threading.Interlocked.Exchange(_listDisplayRefreshPending, 0)
        Catch ex As InvalidOperationException
            Threading.Interlocked.Exchange(_listDisplayRefreshPending, 0)
        End Try
    End Sub

    Private Sub ToolStripStatusLabel2_Click(sender As Object, e As EventArgs) Handles ToolStripStatusLabel2.Click
        Try
            If True OrElse AllowOperation Then
                Task.Run(Sub()
                             If Threading.Monitor.TryEnter(TapeUtils.GetSCSIOperationLock(driveHandle)) Then
                                 Threading.Monitor.Exit(TapeUtils.GetSCSIOperationLock(driveHandle))
                                 RefreshCapacity()
                                 PrintMsg(My.Resources.ResText_CRef, TooltipText:=Nothing)
                             End If
                         End Sub)
            Else
                LastRefresh = Now - New TimeSpan(0, 0, CapacityRefreshInterval)
            End If
        Catch ex As Exception
            PrintMsg(My.Resources.ResText_CRefErr)
            SetStatusLight(LWStatus.Err)
        End Try

    End Sub
    Public Sub LockGUI(Optional ByVal Lock As Boolean = True)
        Invoke(Sub()
                   SyncLock OperationLock
                       AllowOperation = Not Lock
                       'MenuStrip1.Enabled = AllowOperation
                       ContextMenuStrip1.Enabled = AllowOperation
                       For Each Items As ToolStripMenuItem In MenuStrip1.Items
                           For Each SubItem In Items.DropDownItems
                               If TypeOf (SubItem) Is ToolStripDropDownItem Then
                                   CType(SubItem, ToolStripDropDownItem).Enabled = AllowOperation
                               End If
                           Next
                       Next
                       自动化ToolStripMenuItem1.Enabled = True
                       CommandBar.Enabled = AllowOperation
                       ContextMenuStrip3.Enabled = AllowOperation
                   End SyncLock
               End Sub)
    End Sub
    Public Function GetPath(ByVal n As TreeNode) As String
        Dim l As New List(Of String)
        Dim n0 As TreeNode = n
        While n0 IsNot Nothing
            If TypeOf n0.Tag Is ltfsindex.directory Then
                l.Add(DirectCast(n0.Tag, ltfsindex.directory).name)
            ElseIf TypeOf n0.Tag Is TarVirtualDirectory Then
                l.Add(DirectCast(n0.Tag, TarVirtualDirectory).Name)
            Else
                Exit While
            End If
            n0 = n0.Parent
        End While
        Dim sb As New StringBuilder
        For i As Integer = l.Count - 1 To 0 Step -1
            sb.Append("\")
            sb.Append(l(i))
        Next
        Return sb.ToString()
    End Function
    Public LastSelectedNode As TreeNode = Nothing

    Private Enum WriterListRowKind
        File
        UnwrittenFile
        TarDirectory
        TarFile
    End Enum

    Private NotInheritable Class WriterListRow
        Public Sub New(kind As WriterListRowKind, value As Object)
            Me.Kind = kind
            Me.Value = value
        End Sub

        Public ReadOnly Property Kind As WriterListRowKind
        Public ReadOnly Property Value As Object

        Public Function GetItem(owner As LTFSWriter) As ListViewItem
            Return owner.CreateListViewItem(Me)
        End Function
    End Class

    Private ReadOnly _listRows As New List(Of WriterListRow)
    Private ReadOnly _listItemCache As New Dictionary(Of Integer, ListViewItem)
    Private ReadOnly _listItemCacheOrder As New Queue(Of Integer)
    Private Const ListItemCacheCapacity As Integer = 512
    Private _lazyListDirectory As ltfsindex.directory
    Private _lazyListFileCount As Integer
    Private _lazyListPendingDirectory As ltfsindex.directory
    Private _lazyListPendingFileCount As Integer
    Private _lastListRefreshTag As Object = Nothing
    Private _treeRefreshInProgress As Boolean
    Private _listViewColumnSignature As String = Nothing
    Private _listViewTarMode As Boolean

    Private Function CreateListViewItem(row As WriterListRow) As ListViewItem
        If row Is Nothing OrElse row.Value Is Nothing Then Return New ListViewItem()

        Select Case row.Kind
            Case WriterListRowKind.File
                Return CreateFileListViewItem(DirectCast(row.Value, ltfsindex.file), False)
            Case WriterListRowKind.UnwrittenFile
                Return CreateFileListViewItem(DirectCast(row.Value, ltfsindex.file), True)
            Case WriterListRowKind.TarDirectory
                Dim directory As TarVirtualDirectory = DirectCast(row.Value, TarVirtualDirectory)
                Dim directoryItem As New ListViewItem With {
                    .Tag = directory,
                    .Text = directory.Name,
                    .ImageIndex = 1,
                    .StateImageIndex = 1,
                    .ForeColor = Color.Blue}
                directoryItem.SubItems.Add("<DIR>")
                Return directoryItem
            Case WriterListRowKind.TarFile
                Dim file As TarVirtualFile = DirectCast(row.Value, TarVirtualFile)
                Dim item As New ListViewItem With {
                    .Tag = file,
                    .Text = file.Name,
                    .ImageIndex = 2,
                    .StateImageIndex = 2}
                item.SubItems.Add(file.Entry.Length.ToString())
                item.SubItems.Add(file.Entry.Xxh3128)
                Return item
        End Select

        Return New ListViewItem()
    End Function

    Private Function CreateFileListViewItem(file As ltfsindex.file, unwritten As Boolean) As ListViewItem
        Dim item As New ListViewItem With {
            .Tag = file,
            .Text = If(file.name, String.Empty),
            .ImageIndex = If(unwritten, -1, 2),
            .StateImageIndex = If(unwritten, -1, 2)}
        Dim values As New List(Of String)

        Dim populate As Action =
            Sub()
                If ShowXAttr_Barcode Then values.Add(file.GetXAttr("Barcode", True))
                values.Add(CStr(file.length))
                values.Add(If(file.creationtime, String.Empty))
                If My.Settings.LTFSWriter_ChecksumEnabled_SHA1 Then values.Add(file.GetXAttr(ltfsindex.file.xattr.HashType.SHA1, True))
                If My.Settings.LTFSWriter_ChecksumEnabled_SHA256 Then values.Add(file.GetXAttr(ltfsindex.file.xattr.HashType.SHA256, True))
                If My.Settings.LTFSWriter_ChecksumEnabled_SHA512 Then values.Add(file.GetXAttr(ltfsindex.file.xattr.HashType.SHA512, True))
                If My.Settings.LTFSWriter_ChecksumEnabled_CRC32 Then values.Add(file.GetXAttr(ltfsindex.file.xattr.HashType.CRC32, True))
                If My.Settings.LTFSWriter_ChecksumEnabled_MD5 Then values.Add(file.GetXAttr(ltfsindex.file.xattr.HashType.MD5, True))
                If My.Settings.LTFSWriter_ChecksumEnabled_BLAKE3 Then values.Add(file.GetXAttr(ltfsindex.file.xattr.HashType.BLAKE3, True))
                If My.Settings.LTFSWriter_ChecksumEnabled_XxHash3 Then values.Add(file.GetXAttr(ltfsindex.file.xattr.HashType.XxHash3, True))
                If My.Settings.LTFSWriter_ChecksumEnabled_XxHash128 Then values.Add(file.GetXAttr(ltfsindex.file.xattr.HashType.XxHash128, True))
                values.Add(CStr(file.fileuid))
                values.Add(CStr(file.openforwrite))
                values.Add(CStr(file.readonly))
                values.Add(If(file.changetime, String.Empty))
                values.Add(If(file.modifytime, String.Empty))
                values.Add(If(file.accesstime, String.Empty))
                values.Add(If(file.backuptime, String.Empty))
                values.Add(If(file.tag, String.Empty))
                If file.extentinfo IsNot Nothing AndAlso file.extentinfo.Count > 0 Then
                    Try
                        values.Add(file.extentinfo(0).startblock.ToString())
                    Catch
                        values.Add("-")
                    End Try
                    Try
                        values.Add(file.extentinfo(0).partition.ToString())
                    Catch
                        values.Add("-")
                    End Try
                Else
                    values.Add("-")
                    values.Add("-")
                End If
                values.Add(ByteFormatter.FormatBytes(file.length, style:=If(My.Settings.Application_UseDecimalUnit, Style.SI, Style.IEC)))
                If file.WrittenBytes > 0 Then
                    values.Add(ByteFormatter.FormatBytes(file.WrittenBytes, style:=If(My.Settings.Application_UseDecimalUnit, Style.SI, Style.IEC)))
                Else
                    values.Add("-")
                End If
            End Sub

        If unwritten Then
            SyncLock file
                populate()
            End SyncLock
        Else
            populate()
        End If

        For Each value As String In values
            item.SubItems.Add(If(value, String.Empty))
        Next
        Dim completedWrite As Boolean = unwritten AndAlso IsWriteFileCompleted(file)
        item.ForeColor = If(unwritten AndAlso Not completedWrite, Color.Gray, file.ItemForeColor)

        If Not unwritten Then
            Dim columnIndex As Integer = 3 + If(ShowXAttr_Barcode, 1, 0)
            If My.Settings.LTFSWriter_ChecksumEnabled_SHA1 Then
                ApplyChecksumColor(item, columnIndex, GetChecksumColor(file, "SHA1"))
                columnIndex += 1
            End If
            If My.Settings.LTFSWriter_ChecksumEnabled_SHA256 Then
                ApplyChecksumColor(item, columnIndex, GetChecksumColor(file, "SHA256"))
                columnIndex += 1
            End If
            If My.Settings.LTFSWriter_ChecksumEnabled_SHA512 Then
                ApplyChecksumColor(item, columnIndex, GetChecksumColor(file, "SHA512"))
                columnIndex += 1
            End If
            If My.Settings.LTFSWriter_ChecksumEnabled_CRC32 Then
                ApplyChecksumColor(item, columnIndex, GetChecksumColor(file, "CRC32"))
                columnIndex += 1
            End If
            If My.Settings.LTFSWriter_ChecksumEnabled_MD5 Then
                ApplyChecksumColor(item, columnIndex, GetChecksumColor(file, "MD5"))
                columnIndex += 1
            End If
            If My.Settings.LTFSWriter_ChecksumEnabled_BLAKE3 Then
                ApplyChecksumColor(item, columnIndex, GetChecksumColor(file, "BLAKE3"))
                columnIndex += 1
            End If
            If My.Settings.LTFSWriter_ChecksumEnabled_XxHash3 Then
                ApplyChecksumColor(item, columnIndex, GetChecksumColor(file, "XxHash3"))
                columnIndex += 1
            End If
            If My.Settings.LTFSWriter_ChecksumEnabled_XxHash128 Then
                ApplyChecksumColor(item, columnIndex, GetChecksumColor(file, "XxHash128"))
            End If
        End If

        Return item
    End Function

    Private Function EnsureVirtualListViewItemColumns(item As ListViewItem) As ListViewItem
        If item Is Nothing Then item = New ListViewItem()

        ' ListView requires exactly one subitem for every column when
        ' RetrieveVirtualItem supplies an item. TAR virtual entries only have
        ' a subset of the normal LTFS file fields, so fill their unused cells.
        Dim columnCount As Integer = ListView1.Columns.Count
        While item.SubItems.Count < columnCount
            item.SubItems.Add(String.Empty)
        End While
        While item.SubItems.Count > columnCount AndAlso item.SubItems.Count > 1
            item.SubItems.RemoveAt(item.SubItems.Count - 1)
        End While

        Return item
    End Function

    Private Shared Sub ApplyChecksumColor(item As ListViewItem, columnIndex As Integer, color As Color)
        If color.Equals(Color.Black) OrElse columnIndex >= item.SubItems.Count Then Return
        item.UseItemStyleForSubItems = False
        item.SubItems(columnIndex).ForeColor = color
    End Sub

    Private Sub ClearListViewRows()
        ListView1.SelectedIndices.Clear()
        _listRows.Clear()
        _listItemCache.Clear()
        _listItemCacheOrder.Clear()
        _lazyListDirectory = Nothing
        _lazyListFileCount = 0
        _lazyListPendingDirectory = Nothing
        _lazyListPendingFileCount = 0
    End Sub

    Private Sub SetListViewRows(rows As List(Of WriterListRow),
                                Optional lazyDirectory As ltfsindex.directory = Nothing,
                                Optional includePendingFiles As Boolean = False)
        ClearListViewRows()
        If rows IsNot Nothing AndAlso rows.Count > 0 Then _listRows.AddRange(rows)
        If lazyDirectory IsNot Nothing Then
            _lazyListDirectory = lazyDirectory
            _lazyListFileCount = lazyDirectory.GetLazyDirectFileCount()
            If includePendingFiles Then
                _lazyListPendingDirectory = lazyDirectory
                SyncLock lazyDirectory.UnwrittenFiles
                    _lazyListPendingFileCount = lazyDirectory.UnwrittenFiles.Count
                End SyncLock
            End If
        End If
        UpdateListViewVirtualSize()
    End Sub

    Private Sub RefreshLazyListRowCounts()
        If _lazyListDirectory IsNot Nothing Then
            _lazyListFileCount = _lazyListDirectory.GetLazyDirectFileCount()
        End If
        If _lazyListPendingDirectory IsNot Nothing Then
            SyncLock _lazyListPendingDirectory.UnwrittenFiles
                _lazyListPendingFileCount = _lazyListPendingDirectory.UnwrittenFiles.Count
            End SyncLock
        End If
        UpdateListViewVirtualSize()
    End Sub

    Private Sub UpdateListViewVirtualSize()
        Dim rowCount As Long = _listRows.Count
        rowCount += _lazyListPendingFileCount
        If _lazyListDirectory IsNot Nothing Then rowCount += _lazyListFileCount
        Dim virtualCount As Integer = CInt(Math.Min(Integer.MaxValue, rowCount))
        If ListView1.VirtualListSize <> virtualCount Then
            ListView1.VirtualListSize = virtualCount
        Else
            ListView1.Invalidate()
        End If
    End Sub

    Private Function GetListViewRowCount() As Integer
        Dim rowCount As Long = CLng(_listRows.Count) + _lazyListPendingFileCount + _lazyListFileCount
        Return CInt(Math.Min(Integer.MaxValue, rowCount))
    End Function

    Private Function GetListViewItem(index As Integer) As ListViewItem
        If index < 0 OrElse index >= GetListViewRowCount() Then Return Nothing
        Dim item As ListViewItem = Nothing
        If _listItemCache.TryGetValue(index, item) Then
            Return EnsureVirtualListViewItemColumns(item)
        End If

        If index < _listRows.Count Then
            item = EnsureVirtualListViewItemColumns(_listRows(index).GetItem(Me))
        ElseIf _lazyListDirectory IsNot Nothing Then
            Dim lazyIndex As Integer = index - _listRows.Count
            If lazyIndex < _lazyListFileCount Then
                Dim file As ltfsindex.file = _lazyListDirectory.GetLazyFileAt(lazyIndex)
                item = EnsureVirtualListViewItemColumns(New WriterListRow(WriterListRowKind.File, file).GetItem(Me))
            ElseIf _lazyListPendingDirectory IsNot Nothing Then
                Dim pendingIndex As Integer = lazyIndex - _lazyListFileCount
                If pendingIndex < 0 OrElse pendingIndex >= _lazyListPendingFileCount Then Return Nothing
                Dim pendingFile As ltfsindex.file = Nothing
                SyncLock _lazyListPendingDirectory.UnwrittenFiles
                    If pendingIndex < _lazyListPendingDirectory.UnwrittenFiles.Count Then
                        pendingFile = _lazyListPendingDirectory.UnwrittenFiles(pendingIndex)
                    End If
                End SyncLock
                If pendingFile Is Nothing Then Return Nothing
                item = EnsureVirtualListViewItemColumns(New WriterListRow(WriterListRowKind.UnwrittenFile, pendingFile).GetItem(Me))
            Else
                Return Nothing
            End If
        Else
            Return Nothing
        End If
        _listItemCache(index) = item
        _listItemCacheOrder.Enqueue(index)
        While _listItemCache.Count > ListItemCacheCapacity
            Dim oldestIndex As Integer = _listItemCacheOrder.Dequeue()
            If _listItemCache.ContainsKey(oldestIndex) Then
                _listItemCache.Remove(oldestIndex)
            End If
        End While
        Return item
    End Function

    Private Function GetSelectedListViewItems() As List(Of ListViewItem)
        Dim result As New List(Of ListViewItem)
        For i As Integer = 0 To ListView1.SelectedIndices.Count - 1
            Dim index As Integer = ListView1.SelectedIndices(i)
            Dim item As ListViewItem = GetListViewItem(index)
            If item IsNot Nothing Then result.Add(item)
        Next
        Return result
    End Function

    Private Function GetSelectedListViewFile() As ltfsindex.file
        If ListView1.SelectedIndices.Count = 0 Then Return Nothing

        Dim index As Integer = ListView1.SelectedIndices(0)
        If index < 0 OrElse index >= GetListViewRowCount() Then Return Nothing

        'Search only needs the record identity.  Do not call
        'GetListViewItem here because rendering all columns can load every
        'xattr/checksum for a large lazy file while the user presses F3.
        If index < _listRows.Count Then
            Return TryCast(_listRows(index).Value, ltfsindex.file)
        End If

        If _lazyListDirectory Is Nothing Then Return Nothing
        Dim lazyIndex As Integer = index - _listRows.Count
        If lazyIndex < _lazyListFileCount Then
            Return _lazyListDirectory.GetLazyFileAt(lazyIndex)
        End If

        If _lazyListPendingDirectory Is Nothing Then Return Nothing
        Dim pendingIndex As Integer = lazyIndex - _lazyListFileCount
        If pendingIndex < 0 OrElse pendingIndex >= _lazyListPendingFileCount Then Return Nothing
        SyncLock _lazyListPendingDirectory.UnwrittenFiles
            If pendingIndex < _lazyListPendingDirectory.UnwrittenFiles.Count Then
                Return _lazyListPendingDirectory.UnwrittenFiles(pendingIndex)
            End If
        End SyncLock
        Return Nothing
    End Function

    Private Sub SelectListViewIndex(index As Integer)
        If index < 0 OrElse index >= GetListViewRowCount() Then Return
        ListView1.SelectedIndices.Clear()
        ListView1.SelectedIndices.Add(index)
        Dim item As ListViewItem = GetListViewItem(index)
        If item IsNot Nothing Then
            Try
                ListView1.FocusedItem = item
            Catch
            End Try
        End If
        ListView1.Focus()
        ListView1.EnsureVisible(index)
    End Sub

    Private Sub ListView1_RetrieveVirtualItem(sender As Object, e As RetrieveVirtualItemEventArgs) Handles ListView1.RetrieveVirtualItem
        Dim item As ListViewItem = GetListViewItem(e.ItemIndex)
        e.Item = EnsureVirtualListViewItemColumns(item)
    End Sub

    Private Function GetListViewColumnSignature() As String
        Return If(ShowXAttr_Barcode, "1", "0") &
               If(My.Settings.LTFSWriter_ChecksumEnabled_SHA1, "1", "0") &
               If(My.Settings.LTFSWriter_ChecksumEnabled_SHA256, "1", "0") &
               If(My.Settings.LTFSWriter_ChecksumEnabled_SHA512, "1", "0") &
               If(My.Settings.LTFSWriter_ChecksumEnabled_CRC32, "1", "0") &
               If(My.Settings.LTFSWriter_ChecksumEnabled_MD5, "1", "0") &
               If(My.Settings.LTFSWriter_ChecksumEnabled_BLAKE3, "1", "0") &
               If(My.Settings.LTFSWriter_ChecksumEnabled_XxHash3, "1", "0") &
               If(My.Settings.LTFSWriter_ChecksumEnabled_XxHash128, "1", "0")
    End Function

    Private Shared Function IsDynamicListViewColumn(name As String) As Boolean
        Select Case name
            Case "Column_Barcode", "Column_sha1", "Column_sha256", "Column_sha512", "Column_crc32",
                 "Column_md5", "Column_blake3", "Column_XxHash3", "Column_XxHash128"
                Return True
        End Select
        Return False
    End Function

    Private Sub SaveListViewColumnOrder()
        If _listViewTarMode OrElse Not ColumnReordered Then Return

        Dim columnIndex As New SerializableDictionary(Of String, Integer)
        For i As Integer = 0 To ListView1.Columns.Count - 1
            columnIndex.Add(ListView1.Columns(i).Name, ListView1.Columns(i).DisplayIndex)
        Next
        My.Settings.LTFSWriter_ColumnIndex = columnIndex
        My.Settings.Save()
        ColumnReordered = False
    End Sub

    Private Sub RestoreNormalListViewColumns()
        If Not _listViewTarMode Then Return

        ' Do not let the native ListView request an old virtual item while its
        ' column collection is being replaced.
        ListView1.VirtualListSize = 0
        ListView1.Columns.Clear()
        ListView1.Columns.AddRange(New ColumnHeader() {
                                  Column_name,
                                  Column_length,
                                  Column_creationtime,
                                  Column_fileuid,
                                  Column_openforwrite,
                                  Column_readonly,
                                  Column_changetime,
                                  Column_modifytime,
                                  Column_accesstime,
                                  Column_backuptime,
                                  Column_tag,
                                  Column_StartBlock,
                                  Column_Partition,
                                  Column_FriendlyLen,
                                  Column_writtenBytes})
        ListView1.AllowColumnReorder = True
        ColumnReordered = False
        _listViewTarMode = False
        _listViewColumnSignature = Nothing
    End Sub

    Private Sub EnsureTarListViewColumns()
        If _listViewTarMode Then Return

        SaveListViewColumnOrder()
        ListView1.VirtualListSize = 0
        ListView1.Columns.Clear()

        Dim tarHashColumn As New ColumnHeader With {
            .Name = "Column_tarHash",
            .Text = "XxHash3",
            .Width = ListView1.LogicalToDeviceUnits(260)}
        ListView1.Columns.AddRange(New ColumnHeader() {Column_name, Column_length, tarHashColumn})
        For i As Integer = 0 To ListView1.Columns.Count - 1
            ListView1.Columns(i).DisplayIndex = i
        Next

        ListView1.AllowColumnReorder = False
        ColumnReordered = False
        _listViewTarMode = True
    End Sub

    Private Sub EnsureListViewColumns()
        RestoreNormalListViewColumns()
        SaveListViewColumnOrder()

        Dim signature As String = GetListViewColumnSignature()
        If String.Equals(_listViewColumnSignature, signature, StringComparison.Ordinal) Then Return

        For i As Integer = ListView1.Columns.Count - 1 To 0 Step -1
            If IsDynamicListViewColumn(ListView1.Columns(i).Name) Then ListView1.Columns.RemoveAt(i)
        Next

        Dim colIndex As Integer = 3
        If My.Settings.LTFSWriter_ChecksumEnabled_SHA1 Then
            ListView1.Columns.Insert(colIndex, New ColumnHeader With {.Name = "Column_sha1", .Width = ListView1.LogicalToDeviceUnits(12 * (ltfsindex.file.xattr.HashLengthBytes.SHA1 + 1)), .Text = "SHA1", .DisplayIndex = colIndex})
            colIndex += 1
        End If
        If My.Settings.LTFSWriter_ChecksumEnabled_SHA256 Then
            ListView1.Columns.Insert(colIndex, New ColumnHeader With {.Name = "Column_sha256", .Width = ListView1.LogicalToDeviceUnits(12 * (ltfsindex.file.xattr.HashLengthBytes.SHA256 + 1)), .Text = "SHA256", .DisplayIndex = colIndex})
            colIndex += 1
        End If
        If My.Settings.LTFSWriter_ChecksumEnabled_SHA512 Then
            ListView1.Columns.Insert(colIndex, New ColumnHeader With {.Name = "Column_sha512", .Width = ListView1.LogicalToDeviceUnits(12 * (ltfsindex.file.xattr.HashLengthBytes.SHA512 + 1)), .Text = "SHA512", .DisplayIndex = colIndex})
            colIndex += 1
        End If
        If My.Settings.LTFSWriter_ChecksumEnabled_CRC32 Then
            ListView1.Columns.Insert(colIndex, New ColumnHeader With {.Name = "Column_crc32", .Width = ListView1.LogicalToDeviceUnits(12 * (ltfsindex.file.xattr.HashLengthBytes.CRC32 + 1)), .Text = "CRC32", .DisplayIndex = colIndex})
            colIndex += 1
        End If
        If My.Settings.LTFSWriter_ChecksumEnabled_MD5 Then
            ListView1.Columns.Insert(colIndex, New ColumnHeader With {.Name = "Column_md5", .Width = ListView1.LogicalToDeviceUnits(12 * (ltfsindex.file.xattr.HashLengthBytes.MD5 + 1)), .Text = "MD5", .DisplayIndex = colIndex})
            colIndex += 1
        End If
        If My.Settings.LTFSWriter_ChecksumEnabled_BLAKE3 Then
            ListView1.Columns.Insert(colIndex, New ColumnHeader With {.Name = "Column_blake3", .Width = ListView1.LogicalToDeviceUnits(12 * (ltfsindex.file.xattr.HashLengthBytes.BLAKE3 + 1)), .Text = "BLAKE3", .DisplayIndex = colIndex})
            colIndex += 1
        End If
        If My.Settings.LTFSWriter_ChecksumEnabled_XxHash3 Then
            ListView1.Columns.Insert(colIndex, New ColumnHeader With {.Name = "Column_XxHash3", .Width = ListView1.LogicalToDeviceUnits(12 * (ltfsindex.file.xattr.HashLengthBytes.XxHash3 + 1)), .Text = "XxHash3", .DisplayIndex = colIndex})
            colIndex += 1
        End If
        If My.Settings.LTFSWriter_ChecksumEnabled_XxHash128 Then
            ListView1.Columns.Insert(colIndex, New ColumnHeader With {.Name = "Column_XxHash128", .Width = ListView1.LogicalToDeviceUnits(12 * (ltfsindex.file.xattr.HashLengthBytes.XxHash128 + 1)), .Text = "XxHash128", .DisplayIndex = colIndex})
            colIndex += 1
        End If
        If ShowXAttr_Barcode Then
            ListView1.Columns.Insert(1, New ColumnHeader With {.Name = "Column_Barcode", .Width = ListView1.LogicalToDeviceUnits(60), .Text = "Barcode", .DisplayIndex = 1})
        End If

        If My.Settings.LTFSWriter_ColumnIndex IsNot Nothing Then
            Try
                Dim cl As New List(Of KeyValuePair(Of ColumnHeader, Integer))
                For i As Integer = 0 To ListView1.Columns.Count - 1
                    Dim cid As Integer = ListView1.Columns(i).DisplayIndex + ListView1.Columns.Count + 100
                    My.Settings.LTFSWriter_ColumnIndex.TryGetValue(ListView1.Columns(i).Name, cid)
                    cl.Add(New KeyValuePair(Of ColumnHeader, Integer)(ListView1.Columns(i), cid))
                Next
                cl.Sort(New Comparison(Of KeyValuePair(Of ColumnHeader, Integer))(Function(a As KeyValuePair(Of ColumnHeader, Integer), b As KeyValuePair(Of ColumnHeader, Integer))
                                                                                      Return a.Value.CompareTo(b.Value)
                                                                                  End Function))
                Dim d2 As New SerializableDictionary(Of String, Integer)
                For i As Integer = 0 To cl.Count - 1
                    cl(i) = New KeyValuePair(Of ColumnHeader, Integer)(cl(i).Key, i)
                    d2.Add(cl(i).Key.Name, i)
                Next
                My.Settings.LTFSWriter_ColumnIndex = d2
                For i As Integer = cl.Count - 1 To 0 Step -1
                    cl(i).Key.DisplayIndex = i
                Next
            Catch ex As Exception
                My.Settings.LTFSWriter_ColumnIndex = Nothing
            End Try
        Else
            For i As Integer = ListView1.Columns.Count - 1 To 0 Step -1
                ListView1.Columns(i).DisplayIndex = i
            Next
        End If

        _listViewColumnSignature = signature
    End Sub

    Public Sub TriggerTreeView1Event(Optional ByVal ForceRefresh As Boolean = False)
        If TreeView1.SelectedNode IsNot Nothing AndAlso TreeView1.SelectedNode.Tag IsNot Nothing Then
            Dim selectedTag As Object = TreeView1.SelectedNode.Tag
            If Not ForceRefresh AndAlso Object.ReferenceEquals(_lastListRefreshTag, selectedTag) Then Return
            _lastListRefreshTag = selectedTag

            If TypeOf selectedTag Is ltfsindex.directory Then
                'A directory containing only files has no tree placeholder, so
                'load its file-backed virtual children when it is selected.
                LoadTreeNodeChildren(TreeView1.SelectedNode)
            End If

            ListView1.BeginUpdate()
            Dim old_select_index As Integer, old_node As Object = ListView1.Tag
            If ListView1.SelectedIndices.Count > 0 Then old_select_index = ListView1.SelectedIndices(0) Else old_select_index = -1
            Dim pendingRows As New List(Of WriterListRow)
            Try

                If TypeOf selectedTag Is TarVirtualDirectory Then
                    EnsureTarListViewColumns()
                Else
                    EnsureListViewColumns()
                End If

                If TypeOf (TreeView1.SelectedNode.Tag) Is ltfsindex.directory Then
                    If TreeView1.SelectedNode.Parent IsNot Nothing Then
                        压缩索引ToolStripMenuItem.Enabled = True
                        剪切目录ToolStripMenuItem.Enabled = True
                        删除ToolStripMenuItem.Enabled = True
                        删除ToolStripMenuItem.Visible = True
                    Else
                        压缩索引ToolStripMenuItem.Enabled = False
                        剪切目录ToolStripMenuItem.Enabled = False
                        删除ToolStripMenuItem.Enabled = False
                        删除ToolStripMenuItem.Visible = True
                    End If
                    压缩索引ToolStripMenuItem.Visible = True
                    剪切目录ToolStripMenuItem.Enabled = True
                    剪切目录ToolStripMenuItem.Visible = True
                    解压索引ToolStripMenuItem.Visible = False
                    提取ToolStripMenuItem1.Enabled = True
                    校验ToolStripMenuItem1.Enabled = True
                    校验ToolStripMenuItem1.Visible = True
                    校验ToolStripMenuItem.Visible = True
                    校验ToolStripMenuItem.Enabled = True
                    重命名ToolStripMenuItem.Enabled = True
                    重命名ToolStripMenuItem.Visible = True
                    统计ToolStripMenuItem.Enabled = True
                    统计ToolStripMenuItem.Visible = True
                    详情ToolStripMenuItem.Visible = True
                    详情ToolStripMenuItem.Enabled = True
                    粘贴选中ToolStripMenuItem.Visible = Not MyClipBoard.IsEmpty
                    粘贴选中ToolStripMenuItem1.Visible = Not MyClipBoard.IsEmpty
                    TextBoxSelectedPath.Text = GetPath(TreeView1.SelectedNode)

                    移动到索引区ToolStripMenuItem.Enabled = True
                    定位到起始块ToolStripMenuItem.Enabled = True
                    剪切文件ToolStripMenuItem.Enabled = True
                    重命名文件ToolStripMenuItem.Enabled = True
                    重命名目录ToolStripMenuItem.Enabled = True
                    合并文件ToolStripMenuItem.Enabled = True
                    导入文件ToolStripMenuItem.Enabled = True
                    添加文件ToolStripMenuItem.Enabled = True
                    添加目录ToolStripMenuItem.Enabled = True
                    新建目录ToolStripMenuItem.Enabled = True
                    删除文件ToolStripMenuItem.Enabled = True
                    删除目录ToolStripMenuItem.Enabled = True
                    生成标签ToolStripMenuItem.Enabled = True
                    设置标签ToolStripMenuItem.Enabled = True
                    复制信息到剪贴板ToolStripMenuItem.Enabled = True
                    文件详情ToolStripMenuItem.Enabled = True
                    Dim d As ltfsindex.directory = DirectCast(TreeView1.SelectedNode.Tag, ltfsindex.directory)
                    ListView1.Tag = d

                    If d.HasUnmaterializedLazyContents Then
                        'Keep both collections virtual.  Enumerating d.UnwrittenFiles
                        'into WriterListRow objects here duplicates every pending
                        'file in a large add operation.
                        SetListViewRows(Nothing, d, includePendingFiles:=True)
                    Else
                        For Each f As ltfsindex.file In d.EnumerateLazyFiles()
                            pendingRows.Add(New WriterListRow(WriterListRowKind.File, f))
                        Next
                        SyncLock d.UnwrittenFiles
                            For Each f As ltfsindex.file In d.UnwrittenFiles
                                pendingRows.Add(New WriterListRow(WriterListRowKind.UnwrittenFile, f))
                            Next
                        End SyncLock
                        SetListViewRows(pendingRows)
                    End If
                ElseIf TypeOf (TreeView1.SelectedNode.Tag) Is TarVirtualDirectory Then
                    Dim tarDirectory As TarVirtualDirectory = DirectCast(TreeView1.SelectedNode.Tag, TarVirtualDirectory)
                    压缩索引ToolStripMenuItem.Visible = False
                    解压索引ToolStripMenuItem.Visible = False
                    压缩索引ToolStripMenuItem.Enabled = False
                    剪切目录ToolStripMenuItem.Enabled = False
                    剪切目录ToolStripMenuItem.Visible = False
                    剪切文件ToolStripMenuItem.Enabled = False
                    提取ToolStripMenuItem.Enabled = True
                    提取ToolStripMenuItem1.Enabled = True
                    校验ToolStripMenuItem.Enabled = False
                    校验ToolStripMenuItem.Visible = False
                    校验ToolStripMenuItem1.Enabled = False
                    校验ToolStripMenuItem1.Visible = False
                    重命名ToolStripMenuItem.Enabled = False
                    重命名ToolStripMenuItem.Visible = False
                    详情ToolStripMenuItem.Visible = False
                    删除ToolStripMenuItem.Enabled = False
                    删除ToolStripMenuItem.Visible = False
                    统计ToolStripMenuItem.Enabled = False
                    统计ToolStripMenuItem.Visible = False

                    移动到索引区ToolStripMenuItem.Enabled = False
                    定位到起始块ToolStripMenuItem.Enabled = False
                    剪切文件ToolStripMenuItem.Enabled = False
                    重命名文件ToolStripMenuItem.Enabled = False
                    重命名目录ToolStripMenuItem.Enabled = False
                    合并文件ToolStripMenuItem.Enabled = False
                    导入文件ToolStripMenuItem.Enabled = False
                    添加文件ToolStripMenuItem.Enabled = False
                    添加目录ToolStripMenuItem.Enabled = False
                    新建目录ToolStripMenuItem.Enabled = False
                    删除文件ToolStripMenuItem.Enabled = False
                    删除目录ToolStripMenuItem.Enabled = False
                    生成标签ToolStripMenuItem.Enabled = False
                    设置标签ToolStripMenuItem.Enabled = False
                    复制信息到剪贴板ToolStripMenuItem.Enabled = False
                    文件详情ToolStripMenuItem.Enabled = False

                    粘贴选中ToolStripMenuItem.Enabled = False
                    粘贴选中ToolStripMenuItem1.Enabled = False
                    新建压缩文件ToolStripMenuItem.Enabled = False
                    ListView1.Tag = tarDirectory
                    TextBoxSelectedPath.Text = GetPath(TreeView1.SelectedNode)
                    For Each child As TarVirtualDirectory In tarDirectory.Directories
                        pendingRows.Add(New WriterListRow(WriterListRowKind.TarDirectory, child))
                    Next
                    For Each child As TarVirtualFile In tarDirectory.Files
                        pendingRows.Add(New WriterListRow(WriterListRowKind.TarFile, child))
                    Next
                ElseIf TypeOf (TreeView1.SelectedNode.Tag) Is ltfsindex.file Then
                    Dim f As ltfsindex.file = DirectCast(TreeView1.SelectedNode.Tag, ltfsindex.file)
                    Dim t As String = f.GetXAttr(ltfsindex.file.xattr.ApplicationSpecific.Archive)
                    If t IsNot Nothing AndAlso t.ToLower = "true" Then
                        压缩索引ToolStripMenuItem.Visible = False
                        剪切目录ToolStripMenuItem.Enabled = False
                        粘贴选中ToolStripMenuItem.Enabled = False
                        粘贴选中ToolStripMenuItem1.Enabled = False
                        解压索引ToolStripMenuItem.Visible = True
                        提取ToolStripMenuItem1.Enabled = False
                        校验ToolStripMenuItem1.Enabled = False
                        重命名ToolStripMenuItem.Enabled = False
                        删除ToolStripMenuItem.Enabled = False
                        统计ToolStripMenuItem.Enabled = False
                    End If
                End If
                If pendingRows.Count = 0 AndAlso schema IsNot Nothing Then
                    'ListView1.BackgroundImage = IOManager.FitImage(My.Resources.dragdrop, ListView1.Size)
                Else
                    'ListView1.BackgroundImage = Nothing
                End If

                If Not TypeOf TreeView1.SelectedNode.Tag Is ltfsindex.directory Then
                    SetListViewRows(pendingRows)
                End If

            Catch ex As Exception
                _lastListRefreshTag = Nothing
                PrintMsg(My.Resources.ResText_NavErr)
                SetStatusLight(LWStatus.Err)
            End Try
            ListView1.EndUpdate()
            If old_node IsNot Nothing AndAlso ListView1.Tag IsNot Nothing AndAlso GetListViewRowCount() > 0 AndAlso
               SameListViewTag(old_node, ListView1.Tag) AndAlso old_select_index >= 0 Then
                SelectListViewIndex(Math.Min(old_select_index, GetListViewRowCount() - 1))
            End If
        End If
    End Sub
    Private Sub TreeView1_BeforeExpand(sender As Object, e As TreeViewCancelEventArgs) Handles TreeView1.BeforeExpand
        If _treeRefreshInProgress Then Return
        Dim placeholder As WriterTreeNode = TryCast(e.Node, WriterTreeNode)
        If placeholder IsNot Nothing AndAlso placeholder.IsPlaceholder Then
            e.Cancel = True
            If e.Node.Parent IsNot Nothing Then LoadTreeNodeChildren(e.Node.Parent)
            Return
        End If
        LoadTreeNodeChildren(e.Node)
    End Sub
    Private Sub TreeView1_BeforeSelect(sender As Object, e As TreeViewCancelEventArgs) Handles TreeView1.BeforeSelect
        Dim writerNode As WriterTreeNode = TryCast(e.Node, WriterTreeNode)
        If writerNode IsNot Nothing AndAlso writerNode.IsPlaceholder Then
            e.Cancel = True
            Return
        End If
    End Sub
    Private Sub TreeView1_AfterSelect(sender As Object, e As TreeViewEventArgs) Handles TreeView1.AfterSelect
        If _treeRefreshInProgress OrElse e.Node Is Nothing OrElse e.Node.Tag Is Nothing Then Return
        TriggerTreeView1Event()
    End Sub
    Private Sub TreeView1_KeyUp(sender As Object, e As KeyEventArgs) Handles TreeView1.KeyUp
        Select Case e.KeyCode
            Case Keys.ControlKey, Keys.LControlKey, Keys.RControlKey
                TreeView1.CheckBoxes = Not TreeView1.CheckBoxes
        End Select
    End Sub
    Private Sub TreeView1_NodeMouseClick(sender As Object, e As TreeNodeMouseClickEventArgs) Handles TreeView1.NodeMouseClick
        If e.Button = MouseButtons.Right Then
            If Not Object.ReferenceEquals(TreeView1.SelectedNode, e.Node) Then TreeView1.SelectedNode = e.Node
        End If
    End Sub
    <Category("UI")>
    Public ReadOnly Property SelectedNodes As List(Of TreeNode)
        Get
            Dim Nodes As New List(Of TreeNode)
            If Not TreeView1.CheckBoxes Then
                If TreeView1.SelectedNode IsNot Nothing Then Nodes.Add(TreeView1.SelectedNode)
            Else
                Nodes.AddRange(GetSelectedNodes(TreeView1))
                If Nodes.Count = 0 Then
                    If TreeView1.SelectedNode IsNot Nothing Then Nodes.Add(TreeView1.SelectedNode)
                End If
            End If
            Return Nodes
        End Get
    End Property

    Public Function GetSelectedNodes(ByVal treeView As TreeView) As List(Of TreeNode)
        Dim resultNodes As New List(Of TreeNode)()
        Dim processedStatus As New Dictionary(Of TreeNode, Boolean)() ' Maps node to "all descendants checked" status
        Dim hasCheckedChildren As New Dictionary(Of TreeNode, Boolean)() ' Maps node to "has any checked children" status

        ' First pass: Determine node statuses, bottom-up
        Dim nodeStack As New Stack(Of TreeNode)()
        Dim visitedNodes As New HashSet(Of TreeNode)()

        ' Push all nodes for processing
        For i As Integer = treeView.Nodes.Count - 1 To 0 Step -1
            nodeStack.Push(treeView.Nodes(i))
        Next

        While nodeStack.Count > 0
            Dim currentNode As TreeNode = nodeStack.Peek() ' Just peek to check if we've processed children

            ' Check if we've processed all children first
            Dim allChildrenProcessed As Boolean = True
            For Each childNode As TreeNode In currentNode.Nodes
                If Not visitedNodes.Contains(childNode) Then
                    allChildrenProcessed = False
                    nodeStack.Push(childNode)
                End If
            Next

            If Not allChildrenProcessed Then
                Continue While ' Process children first
            End If

            ' We've processed all children, now process this node
            nodeStack.Pop()
            visitedNodes.Add(currentNode)

            ' Calculate statuses
            If currentNode.Nodes.Count = 0 Then
                ' Leaf node
                processedStatus(currentNode) = currentNode.Checked
                hasCheckedChildren(currentNode) = False ' No children at all
            Else
                ' Non-leaf node
                Dim allDescendantsChecked As Boolean = True
                Dim anyChildChecked As Boolean = False

                For Each childNode As TreeNode In currentNode.Nodes
                    If childNode.Checked Then
                        anyChildChecked = True
                    Else
                        allDescendantsChecked = False
                    End If

                    ' Check descendants' status
                    If hasCheckedChildren(childNode) Then
                        anyChildChecked = True
                    End If

                    If Not processedStatus(childNode) Then
                        allDescendantsChecked = False
                    End If
                Next

                processedStatus(currentNode) = allDescendantsChecked And currentNode.Checked
                hasCheckedChildren(currentNode) = anyChildChecked
            End If
        End While

        ' Second pass: Apply the selection rules using the processed statuses
        nodeStack.Clear()
        visitedNodes.Clear()

        ' Push all nodes again
        For i As Integer = treeView.Nodes.Count - 1 To 0 Step -1
            nodeStack.Push(treeView.Nodes(i))
        Next

        While nodeStack.Count > 0
            Dim currentNode As TreeNode = nodeStack.Pop()

            ' Add children to stack
            For i As Integer = currentNode.Nodes.Count - 1 To 0 Step -1
                nodeStack.Push(currentNode.Nodes(i))
            Next

            ' Skip unchecked nodes
            If Not currentNode.Checked Then
                Continue While
            End If

            ' For checked nodes, apply the rules
            If currentNode.Nodes.Count = 0 Then
                ' Leaf node - always include if checked
                resultNodes.Add(currentNode)
            Else
                ' Rule 1: If all descendants are checked, include only parent
                If processedStatus(currentNode) Then
                    resultNodes.Add(currentNode)
                    ' Rule 2: If parent is checked but no children are checked, include parent
                ElseIf Not hasCheckedChildren(currentNode) Then
                    resultNodes.Add(currentNode)
                    ' Rule 3: If parent is checked but only some children are checked, don't include parent
                Else
                    ' Don't include parent - individual checked children will be included
                End If
            End If
        End While

        Return resultNodes
    End Function
    Private Function IsUnindexedDataLimitReached(forceFlush As Boolean) As Boolean
        Return (((IndexWriteInterval > 0) AndAlso (TotalBytesUnindexed >= IndexWriteInterval)) OrElse
                (My.Settings.LTFSWriter_IndexWriteIntervalSeconds > 0 AndAlso
                 (Now - IndexLastUpdateTime).TotalSeconds >= My.Settings.LTFSWriter_IndexWriteIntervalSeconds) OrElse
                forceFlush)
    End Function

    Public Function CheckUnindexedDataLimit(Optional ByVal ForceFlush As Boolean = False, Optional ByVal CheckOnly As Boolean = False) As Boolean
        If CheckOnly Then Return IsUnindexedDataLimitReached(ForceFlush)
        If IsUnindexedDataLimitReached(ForceFlush) Then
            WriteCurrentIndex(False, False)
            IndexLastUpdateTime = Now
            TotalBytesUnindexed = 0
            Try
                Dim Loc As String = GetLocInfo()
                Invoke(Sub() Text = Loc)
            Catch ex As Exception
                PrintMsg($"Error: CheckUnindexedDataSizeLimit {ex.ToString()}", Warning:=True, LogOnly:=True)
            End Try
            Return True
        End If
        Return False
    End Function

    Public Function TryExecute(ByVal command As Func(Of Byte())) As Boolean
        Dim succ As Boolean = False
        While Not succ
            Dim sense() As Byte
            Try
                sense = command()
            Catch ex As Exception
                Select Case MessageBox.Show(New Form With {.TopMost = True}, $"{My.Resources.ResText_RErrSCSI}{vbCrLf}{ex.ToString}", My.Resources.ResText_Warning, MessageBoxButtons.AbortRetryIgnore)
                    Case DialogResult.Abort
                        Throw ex
                    Case DialogResult.Retry
                        succ = False
                    Case DialogResult.Ignore
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
                PrintMsg($"sense err {TapeUtils.Byte2Hex(sense, True)}", Warning:=True, LogOnly:=True)
                Try
                    Throw New Exception("SCSI sense error")
                Catch ex As Exception
                    Select Case MessageBox.Show(New Form With {.TopMost = True}, $"{My.Resources.ResText_RestoreErr}{vbCrLf}{TapeUtils.ParseSenseData(sense)}{vbCrLf}{vbCrLf}sense{vbCrLf}{TapeUtils.Byte2Hex(sense, True)}{vbCrLf}{ex.StackTrace}", My.Resources.ResText_Warning, MessageBoxButtons.AbortRetryIgnore)
                        Case DialogResult.Abort
                            Throw New Exception(TapeUtils.ParseSenseData(sense))
                        Case DialogResult.Retry
                            succ = False
                        Case DialogResult.Ignore
                            succ = True
                            Exit While
                    End Select
                End Try
            Else
                succ = True
            End If
        End While
        Return succ
    End Function
    Public Sub WriteCurrentIndex(Optional ByVal GotoEOD As Boolean = True, Optional ByVal ClearCurrentStat As Boolean = True)
        SetStatusLight(LWStatus.Busy)
        PrintMsg($"Position = {GetPos.ToString()}", LogOnly:=True)
        If GotoEOD Then TapeUtils.Locate(driveHandle, 0UL, DataPartition, TapeUtils.LocateDestType.EOD)
        Dim CurrentPos As TapeUtils.PositionData = GetPos
        PrintMsg($"Position = {CurrentPos.ToString()}", LogOnly:=True)
        If ExtraPartitionCount > 0 AndAlso schema IsNot Nothing AndAlso schema.location.partition <> CurrentPos.PartitionNumber Then
            Throw New Exception($"{My.Resources.ResText_CurPos}p{CurrentPos.PartitionNumber}b{CurrentPos.BlockNumber}{My.Resources.ResText_IndexNAllowed}")
            Exit Sub
        End If
        If ExtraPartitionCount > 0 AndAlso schema IsNot Nothing AndAlso schema.location.startblock >= CurrentPos.BlockNumber Then
            Throw New Exception($"{My.Resources.ResText_CurPos}p{CurrentPos.PartitionNumber}b{CurrentPos.BlockNumber}{My.Resources.ResText_IndexNAllowed}")
            Exit Sub
        End If
        Dim originalGeneration As ULong = schema.generationnumber
        Dim originalUpdateTime As String = schema.updatetime
        Dim originalLocationPartition As ltfsindex.PartitionLabel = schema.location.partition
        Dim originalLocationStartBlock As ULong = schema.location.startblock
        Dim originalPreviousPartition As ltfsindex.PartitionLabel = schema.previousgenerationlocation.partition
        Dim originalPreviousStartBlock As ULong = schema.previousgenerationlocation.startblock
        Dim tmpf As String = Nothing
        Try
            TryExecute(Function() As Byte()
                           Return TapeUtils.WriteFileMark(driveHandle)
                       End Function)
            schema.generationnumber = CULng(schema.generationnumber + 1)
            schema.updatetime = Now.ToUniversalTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff00Z")
            schema.location.partition = ltfsindex.PartitionLabel.b
            schema.previousgenerationlocation = New ltfsindex.LocationDef With {.partition = schema.location.partition, .startblock = schema.location.startblock}
            CurrentPos = GetPos
            PrintMsg($"Position = {CurrentPos.ToString()}", LogOnly:=True)
            schema.location.startblock = CurrentPos.BlockNumber
            PrintMsg(My.Resources.ResText_GI)
            tmpf = LazySchemaStore.CreateTempFilePath("index-write")
            If Not schema.SaveFile(tmpf) Then Throw New IO.IOException("无法保存当前索引到临时文件。")
            'Dim sdata As Byte() = Encoding.UTF8.GetBytes(schema.GetSerializedText())
            PrintMsg(My.Resources.ResText_WI)
            'TapeUtils.Write(TapeDrive, sdata, plabel.blocksize)
            TapeUtils.Write(driveHandle, tmpf, plabel.blocksize, False)
            'While sdata.Length > 0
            '    Dim wdata As Byte() = sdata.Take(Math.Min(plabel.blocksize, sdata.Length)).ToArray
            '    sdata = sdata.Skip(Math.Min(plabel.blocksize, sdata.Length)).ToArray()
            '    TapeUtils.Write(TapeDrive, wdata)
            '    If sdata.Length = 0 Then Exit While
            'End While
            TotalBytesUnindexed = 0
            If ClearCurrentStat Then
                CurrentBytesProcessed = 0
                CurrentFilesProcessed = 0
            End If
            TapeUtils.WriteFileMark(driveHandle)
            PrintMsg(My.Resources.ResText_WIF)
            CurrentPos = GetPos
            CurrentHeight = CLng(CurrentPos.BlockNumber)
            PrintMsg($"Position = {CurrentPos.ToString()}", LogOnly:=True)
            Modified = ExtraPartitionCount > 0
            SetStatusLight(LWStatus.Succ)
        Catch
            SetStatusLight(LWStatus.Err)
            schema.generationnumber = originalGeneration
            schema.updatetime = originalUpdateTime
            schema.location = New ltfsindex.LocationDef With {.partition = originalLocationPartition, .startblock = originalLocationStartBlock}
            schema.previousgenerationlocation = New ltfsindex.LocationDef With {.partition = originalPreviousPartition, .startblock = originalPreviousStartBlock}
            Throw
        Finally
            Try
                If Not String.IsNullOrEmpty(tmpf) AndAlso IO.File.Exists(tmpf) Then IO.File.Delete(tmpf)
            Catch
            End Try
        End Try
    End Sub
    Public Sub RefreshIndexPartition()
        SetStatusLight(LWStatus.Busy)
        Dim originalLocationPartition As ltfsindex.PartitionLabel = schema.location.partition
        Dim originalLocationStartBlock As ULong = schema.location.startblock
        Dim originalPreviousPartition As ltfsindex.PartitionLabel = schema.previousgenerationlocation.partition
        Dim originalPreviousStartBlock As ULong = schema.previousgenerationlocation.startblock
        Dim block1 As ULong = schema.location.startblock
        Dim tmpf As String = Nothing
        Try
            If schema.location.partition = ltfsindex.PartitionLabel.a Then
                block1 = schema.previousgenerationlocation.startblock
            End If
            If ExtraPartitionCount > 0 Then
                PrintMsg($"Position = {GetPos.ToString()}", LogOnly:=True)
                PrintMsg(My.Resources.ResText_Locating)
                TapeUtils.Locate(driveHandle, 3UL, IndexPartition, TapeUtils.LocateDestType.FileMark)
                Dim p As TapeUtils.PositionData = GetPos
                PrintMsg($"Position = {p.ToString()}", LogOnly:=True)
                TapeUtils.WriteFileMark(driveHandle)
                PrintMsg($"Filemark Written", LogOnly:=True)
                If schema.location.partition = ltfsindex.PartitionLabel.b Then
                    schema.previousgenerationlocation = New ltfsindex.LocationDef With {.partition = schema.location.partition, .startblock = schema.location.startblock}
                End If

                schema.location.startblock = CULng(p.BlockNumber + 1)
                schema.location.partition = ltfsindex.PartitionLabel.a
                p = GetPos
                PrintMsg($"Position = {p.ToString()}", LogOnly:=True)
            End If
            Dim block0 As ULong = schema.location.startblock
            If ExtraPartitionCount > 0 Then
                PrintMsg(My.Resources.ResText_GI)
                tmpf = LazySchemaStore.CreateTempFilePath("index-write")
                If Not schema.SaveFile(tmpf) Then Throw New IO.IOException("无法保存索引到临时文件。")
                PrintMsg(My.Resources.ResText_WI)
                TapeUtils.Write(driveHandle, tmpf, plabel.blocksize, False)
                TapeUtils.WriteFileMark(driveHandle)
                PrintMsg(My.Resources.ResText_WIF)
                PrintMsg($"Position = {GetPos.ToString()}", LogOnly:=True)
            End If
            TapeUtils.WriteVCI(driveHandle, schema.generationnumber, block0, block1, schema.volumeuuid.ToString(), ExtraPartitionCount)
            Modified = False
            SetStatusLight(LWStatus.Succ)
        Catch
            SetStatusLight(LWStatus.Err)
            schema.location = New ltfsindex.LocationDef With {.partition = originalLocationPartition, .startblock = originalLocationStartBlock}
            schema.previousgenerationlocation = New ltfsindex.LocationDef With {.partition = originalPreviousPartition, .startblock = originalPreviousStartBlock}
            Throw
        Finally
            Try
                If Not String.IsNullOrEmpty(tmpf) AndAlso IO.File.Exists(tmpf) Then IO.File.Delete(tmpf)
            Catch
            End Try
        End Try
    End Sub
    ''' <summary>
    ''' 
    ''' </summary>
    ''' <param name="Data"></param>
    ''' <param name="RetainPosisiton"></param>
    ''' <returns>
    ''' Data start position
    ''' </returns>
    Public Function DumpDataToIndexPartition(ByVal Data As IO.Stream, Optional ByVal RetainPosisiton As Boolean = True, Optional ByVal IsFirstFile As Boolean = True, Optional ByVal IsLastFile As Boolean = True, Optional ByVal DataRead As Action(Of Byte(), Integer, Integer) = Nothing) As Long
        Try
            If ExtraPartitionCount = 0 Then Return -1
            Static tmpf As String = ""
            'record previous position
            Dim pPrevious As New TapeUtils.PositionData(driveHandle)
            'locate
            Dim pFMIndex As New TapeUtils.PositionData(driveHandle)
            If IsFirstFile Then
                TapeUtils.Locate(driveHandle, 3UL, IndexPartition, TapeUtils.LocateDestType.FileMark)
                pFMIndex = New TapeUtils.PositionData(driveHandle)
            End If
            Dim pStartBlock As Long = CLng(pFMIndex.BlockNumber)
            If IsFirstFile Then
                'Dump old index
                If Not TapeUtils.ReadFileMark(driveHandle) Then Return -1
                tmpf = LazySchemaStore.CreateTempFilePath("index-read")
                TapeUtils.ReadToFileMark(driveHandle, tmpf, plabel.blocksize)
            End If
            'Write data
            TapeUtils.Locate(driveHandle, pFMIndex.BlockNumber, pFMIndex.PartitionNumber)
            CurrentBytesProcessed = 0
            TapeUtils.Write(handle:=driveHandle, Data:=Data, BlockSize:=plabel.blocksize, senseEnabled:=False,
                            ProgressReport:=Sub(progress As Long)
                                                Threading.Interlocked.Add(TotalBytesProcessed, progress)
                                                Threading.Interlocked.Add(CurrentBytesProcessed, progress)
                                            End Sub,
                            DataRead:=DataRead)
            If IsLastFile Then
                'Recover old index
                TapeUtils.WriteFileMark(driveHandle)
                TapeUtils.Write(driveHandle, tmpf, plabel.blocksize, False)
                IO.File.Delete(tmpf)
                TapeUtils.WriteFileMark(driveHandle)
                'Recover position
                If RetainPosisiton Then TapeUtils.Locate(driveHandle, pPrevious.BlockNumber, pPrevious.PartitionNumber)
            End If
            Return pStartBlock
        Catch ex As Exception
            MessageBox.Show(New Form With {.TopMost = True}, $"{ex.ToString()}{vbCrLf}{ex.StackTrace}")
        End Try
        Return -1
    End Function
    Public Sub MoveToIndexPartition(ByVal f As ltfsindex.file, Optional ByVal tmpfile As String = "")
        Try
            If ExtraPartitionCount = 0 Then Exit Sub
            If f Is Nothing Then Exit Sub
            If f.extentinfo Is Nothing OrElse f.extentinfo.Count = 0 Then Exit Sub
            If f.extentinfo(0).partition = ltfsindex.PartitionLabel.a Then Exit Sub
            Dim tmpf As String
            If tmpfile IsNot Nothing AndAlso IO.File.Exists(tmpfile) Then
                tmpf = tmpfile
            Else
                tmpf = LazySchemaStore.CreateTempFilePath("file-transfer")
                RestoreFile(tmpf, f)
            End If
            Dim fs As New IO.FileStream(tmpf, IO.FileMode.Open)
            Dim len As Long = fs.Length
            Dim startblock As Integer = CInt(DumpDataToIndexPartition(fs))
            f.extentinfo = {New ltfsindex.file.extent With {.startblock = startblock, .bytecount = len, .byteoffset = 0, .fileoffset = 0, .partition = ltfsindex.PartitionLabel.a}}.ToList()
            IO.File.Delete(tmpf)
        Catch ex As Exception
            MessageBox.Show(New Form With {.TopMost = True}, $"{ex.ToString}")
        End Try

    End Sub
    Public Function DumpDataToIndexPartition(ByVal Data As Byte(), Optional ByVal RetainPosisiton As Boolean = True) As Long
        Dim s As New IO.MemoryStream(Data)
        Return DumpDataToIndexPartition(s, RetainPosisiton)
    End Function
    Public Sub UpdataAllIndex()
        SetStatusLight(LWStatus.Busy)
        If (My.Settings.LTFSWriter_ForceIndex OrElse (TotalBytesUnindexed <> 0)) AndAlso schema IsNot Nothing AndAlso schema.location.partition = ltfsindex.PartitionLabel.b Then
            PrintMsg(My.Resources.ResText_UDI)
            WriteCurrentIndex(False)
        End If
        PrintMsg(My.Resources.ResText_UI)
        RefreshIndexPartition()
        AutoDump()
        TapeUtils.ReleaseUnit(driveHandle)
        TapeUtils.AllowMediumRemoval(driveHandle)
        PrintMsg(My.Resources.ResText_IUd)
        If schema IsNot Nothing AndAlso schema.location.partition = ltfsindex.PartitionLabel.a Then Me.Invoke(Sub() 更新数据区索引ToolStripMenuItem.Enabled = False)
        If SilentMode Then
            If SilentAutoEject Then
                TapeUtils.LoadEject(driveHandle, TapeUtils.LoadOption.Eject)
                RaiseEvent TapeEjected()
            End If
        Else
            Dim DoEject As Boolean = False
            Invoke(Sub()
                       DoEject = WA3ToolStripMenuItem.Checked OrElse MessageBox.Show(New Form With {.TopMost = True}, My.Resources.ResText_PEj, My.Resources.ResText_Hint, MessageBoxButtons.OKCancel) = DialogResult.OK
                   End Sub)
            If DoEject Then
                TapeUtils.LoadEject(driveHandle, TapeUtils.LoadOption.Eject)
                PrintMsg(My.Resources.ResText_Ejd)
                RaiseEvent TapeEjected()
            End If
        End If
        Invoke(Sub()
                   LockGUI(False)
                   RefreshDisplay()
               End Sub)

        SetStatusLight(LWStatus.Succ)
    End Sub
    Public Sub OnWriteFinished()
        If WA0ToolStripMenuItem.Checked Then Exit Sub
        If WA1ToolStripMenuItem.Checked Then
            Dim SilentBefore As Boolean = SilentMode
            SilentMode = True
            Try
                If (My.Settings.LTFSWriter_ForceIndex OrElse TotalBytesUnindexed <> 0) AndAlso schema IsNot Nothing AndAlso schema.location.partition = ltfsindex.PartitionLabel.b Then
                    WriteCurrentIndex(False)
                    TapeUtils.Flush(driveHandle)
                End If
            Catch ex As Exception
                PrintMsg($"{ex.ToString()}{vbCrLf}{ex.StackTrace}")
                SetStatusLight(LWStatus.Err)
            End Try
            SilentMode = SilentBefore
            Exit Sub
        End If
        If WA2ToolStripMenuItem.Checked Then
            Dim SilentBefore As Boolean = SilentMode
            SilentMode = True
            SilentAutoEject = False
            UpdataAllIndex()
            SilentMode = SilentBefore
            Exit Sub
        End If
        If WA3ToolStripMenuItem.Checked Then
            SilentMode = True
            SilentAutoEject = True
            UpdataAllIndex()
            Exit Sub
        End If
    End Sub
    <Category("LTFSWriter")>
    Public Property ParallelAdd As Boolean = False
    Public ExplorerComparer As New ExplorerUtils()
    Public Function IsSameFile(f As IO.FileInfo, f0 As ltfsindex.file) As Boolean
        Dim Result As Boolean = True
        If f.Name <> f0.name Then
            Result = False
        ElseIf f.Length <> f0.length Then
            Result = False
        End If
        Dim validtime As String = ""
        Try
            validtime = f.LastWriteTimeUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffffff00Z")
            If validtime <> f0.modifytime Then
                Result = False
            End If
        Catch ex As Exception
            validtime = f.CreationTimeUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffffff00Z")
            If validtime <> f0.modifytime Then
                Result = False
            End If
        End Try
        If Not Result Then
            PrintMsg($"Different File: {f0.name}|{f0.length}|{f0.modifytime}->{f.Name}|{f.Length}|{validtime}", LogOnly:=True)
        End If
        Return Result
    End Function
    Public Sub AddFile(f As IO.FileInfo, d As ltfsindex.directory, Optional ByVal OverWrite As Boolean = False,
                       Optional ByVal XattrPath As String = Nothing)
        Try
            If StopFlag Then Exit Sub
            If f.Extension.Equals(".xattr", StringComparison.OrdinalIgnoreCase) Then Exit Sub
            'symlink
            If My.Settings.LTFSWriter_SkipSymlink AndAlso ((f.Attributes And IO.FileAttributes.ReparsePoint) <> 0) Then
                Exit Sub
            End If
            Dim FileExist As Boolean = False
            Dim SameFile As Boolean = False
            '检查磁带已有文件
            For Each oldf As ltfsindex.file In GetExistingFilesForAdd(d, f.Name)
                If String.Equals(oldf.name, f.Name, StringComparison.Ordinal) Then
                    SameFile = IsSameFile(f, oldf)
                    If OverWrite AndAlso Not SameFile Then
                        d.RemoveFile(oldf)
                        RemoveExistingFileFromAddLookup(d, oldf)
                    End If
                    FileExist = True
                End If
            Next
            If FileExist And (SameFile OrElse Not OverWrite) Then Exit Sub
            '检查写入队列
            If Not FileExist Then
                FileExist = RemovePendingByName(d, f.Name)
            End If
            '添加到队列
            Dim xattrPathToUse As String = XattrPath
            If XattrPath Is Nothing Then
                Dim candidateXattrPath = f.FullName & ".xattr"
                If IO.File.Exists(candidateXattrPath) Then xattrPathToUse = candidateXattrPath
            End If
            If String.IsNullOrEmpty(xattrPathToUse) Then xattrPathToUse = Nothing
            Dim frnew As New FileRecord(f, d, xattrPathToUse)
            AddPendingRecord(frnew)
        Catch ex As Exception
            Invoke(Sub() MessageBox.Show(New Form With {.TopMost = True}, $"{ex.ToString()}{vbCrLf}{ex.StackTrace}"))
        End Try
    End Sub
    Private Sub AddDirectorySinglePass(dnew1 As IO.DirectoryInfo,
                                       d1 As ltfsindex.directory,
                                       OverWrite As Boolean,
                                       exceptExtention As String())
        If My.Settings.LTFSWriter_SkipSymlink AndAlso ((dnew1.Attributes And IO.FileAttributes.ReparsePoint) <> 0) Then
            Exit Sub
        End If
        If StopFlag Then Exit Sub
        AddScannedDirectory(dnew1, d1, OverWrite, exceptExtention)
    End Sub

    Public Sub AddDirectry(dnew1 As IO.DirectoryInfo, d1 As ltfsindex.directory, Optional ByVal OverWrite As Boolean = False, Optional ByVal exceptExtention As String() = Nothing)
        Dim ownsLookup As Boolean = (_activeAddFileLookup Is Nothing)
        If ownsLookup Then BeginAddFileLookup()
        Try
            If My.Settings.LTFSWriter_SkipSymlink AndAlso ((dnew1.Attributes And IO.FileAttributes.ReparsePoint) <> 0) Then
                Exit Sub
            End If
            If StopFlag Then Exit Sub
            AddScannedDirectory(dnew1, d1, OverWrite, exceptExtention)
        Finally
            If ownsLookup Then EndAddFileLookup()
        End Try
    End Sub

    Private Function GetOrCreateTargetDirectory(sourceDirectory As IO.DirectoryInfo,
                                                 parent As ltfsindex.directory) As ltfsindex.directory
        Dim target As ltfsindex.directory = parent.FindDirectoryByName(sourceDirectory.Name)
        If target IsNot Nothing Then Return target

        target = New ltfsindex.directory With {
            .name = sourceDirectory.Name,
            .creationtime = sourceDirectory.CreationTimeUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffffff00Z"),
            .fileuid = schema.highestfileuid + 1,
            .accesstime = sourceDirectory.LastAccessTimeUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffffff00Z"),
            .modifytime = sourceDirectory.LastWriteTimeUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffffff00Z"),
            .changetime = .modifytime,
            .backuptime = Now.ToUniversalTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff00Z"),
            .readonly = False}
        parent.AddDirectory(target)
        Threading.Interlocked.Increment(schema.highestfileuid)
        Return target
    End Function

    Private Sub AddSourceFile(file As IO.FileInfo,
                              targetDirectory As ltfsindex.directory,
                              OverWrite As Boolean,
                              exceptExtention As String())
        Try
            If StopFlag OrElse file Is Nothing OrElse file.Extension.Equals(".xattr", StringComparison.OrdinalIgnoreCase) Then Exit Sub
            If exceptExtention IsNot Nothing Then
                For Each ext As String In exceptExtention
                    If file.FullName.EndsWith(ext, StringComparison.OrdinalIgnoreCase) Then Exit Sub
                Next
            End If
            AddFile(file, targetDirectory, OverWrite)
        Catch ex As Exception
            Invoke(Sub() MessageBox.Show(New Form With {.TopMost = True}, $"{ex}{vbCrLf}{ex.StackTrace}"))
        End Try
    End Sub

    Private Sub AddScannedDirectory(sourceDirectory As IO.DirectoryInfo,
                                    parent As ltfsindex.directory,
                                    OverWrite As Boolean,
                                    exceptExtention As String())
        If StopFlag Then Exit Sub
        If My.Settings.LTFSWriter_SkipSymlink AndAlso ((sourceDirectory.Attributes And IO.FileAttributes.ReparsePoint) <> 0) Then Exit Sub

        Dim targetDirectory As ltfsindex.directory = GetOrCreateTargetDirectory(sourceDirectory, parent)
        For Each entry As IO.FileSystemInfo In sourceDirectory.EnumerateFileSystemInfos()
            If StopFlag Then Exit For
            Dim sourceFile As IO.FileInfo = TryCast(entry, IO.FileInfo)
            If sourceFile IsNot Nothing Then
                AddSourceFile(sourceFile, targetDirectory, OverWrite, exceptExtention)
                Continue For
            End If

            Dim childDirectory As IO.DirectoryInfo = TryCast(entry, IO.DirectoryInfo)
            If childDirectory IsNot Nothing Then
                AddScannedDirectory(childDirectory, targetDirectory, OverWrite, exceptExtention)
            End If
        Next
    End Sub

    Public Sub ConcatDirectory(dnew1 As IO.DirectoryInfo, d1 As ltfsindex.directory, Optional ByVal OverWrite As Boolean = False)
        Dim ownsLookup As Boolean = (_activeAddFileLookup Is Nothing)
        If ownsLookup Then BeginAddFileLookup()
        Try
            For Each f As IO.FileInfo In dnew1.EnumerateFiles()
                Dim FileExist As Boolean = False
                '检查磁带已有文件
                For Each fe As ltfsindex.file In GetExistingFilesForAdd(d1, f.Name)
                    FileExist = True
                    If OverWrite Then
                        d1.RemoveFile(fe)
                        RemoveExistingFileFromAddLookup(d1, fe)
                    End If
                Next
                If (Not OverWrite) And FileExist Then Continue For
                '检查写入队列
                If Not FileExist Then
                    FileExist = RemovePendingByName(d1, f.Name)
                End If
                '添加到队列
                AddPendingRecord(New FileRecord(f.FullName, d1))
            Next
            For Each dn As IO.DirectoryInfo In dnew1.EnumerateDirectories()
                Dim dT As ltfsindex.directory = d1.FindDirectoryByName(dn.Name)
                If dT Is Nothing Then
                    dT = New ltfsindex.directory With {
                                            .name = dn.Name,
                                            .creationtime = dn.CreationTimeUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffffff00Z"),
                                            .fileuid = schema.highestfileuid + 1,
                                            .accesstime = dn.LastAccessTimeUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffffff00Z"),
                                            .modifytime = dn.LastWriteTimeUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffffff00Z"),
                                            .changetime = .modifytime,
                                            .backuptime = Now.ToUniversalTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff00Z"),
                                            .readonly = False
                                            }
                    d1.AddDirectory(dT)
                    schema.highestfileuid += 1
                End If
                ConcatDirectory(dn, dT, OverWrite)
            Next
        Finally
            If ownsLookup Then EndAddFileLookup()
        End Try
    End Sub

    Private Sub RemoveUnwrittenForDirectoryTree(root As ltfsindex.directory)
        If root Is Nothing Then Return

        'Collect the subtree once, then remove from the global queue in one
        'linear pass.  The old implementation scanned the complete global
        'queue for every pending file in the subtree (O(pending * queue)).
        Dim pendingDirectories As New HashSet(Of ltfsindex.directory)(DirectoryReferenceComparer.Instance)
        Dim lazyStore As Object = Nothing
        Dim lazyDirectoryOffsets As New HashSet(Of Long)
        Dim pending As New Stack(Of ltfsindex.directory)
        pending.Push(root)
        While pending.Count > 0
            Dim current As ltfsindex.directory = pending.Pop()
            If current Is Nothing OrElse Not pendingDirectories.Add(current) Then Continue While
            If current.HasLazyRecord Then
                If lazyStore Is Nothing Then lazyStore = current.LazyStoreReference
                If ReferenceEquals(lazyStore, current.LazyStoreReference) Then lazyDirectoryOffsets.Add(current.LazyRecordOffset)
            End If
            For Each child As ltfsindex.directory In current.EnumerateLazyDirectories()
                If child IsNot Nothing Then pending.Push(child)
            Next
        End While

        SyncLock _pendingQueueLock
            Dim directoriesToClear As New HashSet(Of ltfsindex.directory)(DirectoryReferenceComparer.Instance)
            For Each directory As ltfsindex.directory In pendingDirectories
                directoriesToClear.Add(directory)
            Next
            If UnwrittenFiles IsNot Nothing Then
                For i As Integer = UnwrittenFiles.Count - 1 To 0 Step -1
                    Dim record As FileRecord = UnwrittenFiles(i)
                    If record Is Nothing OrElse record.ParentDirectory Is Nothing Then Continue For
                    Dim inSubtree As Boolean = pendingDirectories.Contains(record.ParentDirectory)
                    If Not inSubtree AndAlso record.ParentDirectory.HasLazyRecord AndAlso lazyStore IsNot Nothing Then
                        inSubtree = ReferenceEquals(record.ParentDirectory.LazyStoreReference, lazyStore) AndAlso
                                   lazyDirectoryOffsets.Contains(record.ParentDirectory.LazyRecordOffset)
                    End If
                    If inSubtree Then
                        directoriesToClear.Add(record.ParentDirectory)
                        UnwrittenFiles.RemoveAt(i)
                        If record.File IsNot Nothing Then _pendingSize -= record.File.length
                    End If
                Next
            End If

            For Each directory As ltfsindex.directory In directoriesToClear
                If directory.UnwrittenFiles IsNot Nothing Then
                    SyncLock directory.UnwrittenFiles
                        directory.UnwrittenFiles.Clear()
                    End SyncLock
                End If
                _pendingByDirectory.Remove(directory)
            Next
        End SyncLock
    End Sub

    Private Function HasUnwrittenFilesInDirectoryTree(root As ltfsindex.directory) As Boolean
        If root Is Nothing Then Return False

        Dim directories As New HashSet(Of ltfsindex.directory)(DirectoryRecordComparer.Instance)
        Dim pending As New Stack(Of ltfsindex.directory)
        pending.Push(root)
        While pending.Count > 0
            Dim current As ltfsindex.directory = pending.Pop()
            If current Is Nothing OrElse Not directories.Add(current) Then Continue While
            For Each child As ltfsindex.directory In current.EnumerateLazyDirectories()
                If child IsNot Nothing Then pending.Push(child)
            Next
        End While

        SyncLock _pendingQueueLock
            If UnwrittenFiles IsNot Nothing Then
                For Each record As FileRecord In UnwrittenFiles
                    If record IsNot Nothing AndAlso directories.Contains(record.ParentDirectory) Then Return True
                Next
            End If
            For Each directory As ltfsindex.directory In directories
                If directory.UnwrittenFiles Is Nothing Then Continue For
                SyncLock directory.UnwrittenFiles
                    If directory.UnwrittenFiles.Count > 0 Then Return True
                End SyncLock
            Next
        End SyncLock
        Return False
    End Function

    Public Sub DeleteDir()
        Dim Nodes As List(Of TreeNode) = SelectedNodes
        If Nodes.Count = 0 Then Exit Sub
        If Nodes.Count > 1 Then
            If Not MessageBox.Show(New Form With {.TopMost = True}, $"{My.Resources.ResText_DelConfrm}{Nodes.Count}{My.Resources.ResText_Directories_C}", My.Resources.ResText_Confirm, MessageBoxButtons.OKCancel) = DialogResult.OK Then Exit Sub
        Else
            If Nodes(0).Parent Is Nothing Then Exit Sub
            If Not MessageBox.Show(New Form With {.TopMost = True}, $"{My.Resources.ResText_DelConfrm}{CType(Nodes(0).Tag, ltfsindex.directory).name}", My.Resources.ResText_Confirm, MessageBoxButtons.OKCancel) = DialogResult.OK Then Exit Sub
        End If
        For Each node As TreeNode In Nodes
            Dim d As ltfsindex.directory = DirectCast(node.Tag, ltfsindex.directory)
            If node.Parent IsNot Nothing Then
                Dim pd As ltfsindex.directory = DirectCast(node.Parent.Tag, ltfsindex.directory)
                pd.RemoveDirectory(d)
                If TotalBytesUnindexed = 0 Then TotalBytesUnindexed = 1
                RemoveUnwrittenForDirectoryTree(d)
                If TreeView1.SelectedNode IsNot Nothing AndAlso TreeView1.SelectedNode.Parent IsNot Nothing AndAlso TreeView1.SelectedNode.Parent.Tag IsNot Nothing AndAlso TypeOf (TreeView1.SelectedNode.Parent.Tag) Is ltfsindex.directory Then
                    TreeView1.SelectedNode = TreeView1.SelectedNode.Parent
                End If
                If TotalBytesUnindexed = 0 Then TotalBytesUnindexed = 1
            End If
        Next
        UnwrittenCountOverrideValue = 0
        UnwrittenSizeOverrideValue = 0
        RefreshDisplay()

    End Sub
    Public Sub RenameDir()
        If TreeView1.SelectedNode IsNot Nothing Then
            Dim d As ltfsindex.directory = DirectCast(TreeView1.SelectedNode.Tag, ltfsindex.directory)
            Dim s As String = d.name
            If DisplayHelper.ShowInputDialog(My.Resources.ResText_DirName, My.Resources.ResText_RenameDir, s) <> DialogResult.OK Then Exit Sub
            If s <> "" Then
                If s = d.name Then Exit Sub
                If (s.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0) Then
                    MessageBox.Show(New Form With {.TopMost = True}, My.Resources.ResText_DirNIllegal)
                    Exit Sub
                End If
                If TreeView1.SelectedNode.Parent IsNot Nothing Then
                    Dim pd As ltfsindex.directory = DirectCast(TreeView1.SelectedNode.Parent.Tag, ltfsindex.directory)
                    For Each d2 As ltfsindex.directory In pd.EnumerateDirectoriesByName(s)
                        If d2 IsNot d Then
                            MessageBox.Show(New Form With {.TopMost = True}, My.Resources.ResText_DirNExist)
                            Exit Sub
                        End If
                    Next
                End If
                d.name = s
                If TotalBytesUnindexed = 0 Then TotalBytesUnindexed = 1
                RefreshDisplay()
            End If
        End If

    End Sub
    Private Sub 重命名ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 重命名文件ToolStripMenuItem.Click
        Dim selectedItems As List(Of ListViewItem) = GetSelectedListViewItems()
        If ListView1.Tag IsNot Nothing AndAlso
        selectedItems.Count > 0 AndAlso
        selectedItems.Item(0).Tag IsNot Nothing AndAlso
        TypeOf (selectedItems.Item(0).Tag) Is ltfsindex.file Then
            Dim f As ltfsindex.file = DirectCast(selectedItems.Item(0).Tag, ltfsindex.file)
            Dim d As ltfsindex.directory = DirectCast(ListView1.Tag, ltfsindex.directory)
            Dim newname As String = f.name
            If (DisplayHelper.ShowInputDialog(My.Resources.ResText_NFName, My.Resources.ResText_Rename, newname) <> DialogResult.OK) Then Exit Sub
            If newname = f.name Then Exit Sub
            If newname = "" Then Exit Sub
            If (newname.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0) Then
                MessageBox.Show(New Form With {.TopMost = True}, My.Resources.ResText_FNIllegal)
                Exit Sub
            End If
            For Each allf As ltfsindex.file In d.EnumerateFilesByName(newname)
                If allf IsNot f AndAlso Not (f.HasLazyRecord AndAlso allf.HasLazyRecord AndAlso f.LazyRecordOffset = allf.LazyRecordOffset) Then
                    MessageBox.Show(New Form With {.TopMost = True}, My.Resources.ResText_FNExist)
                    Exit Sub
                End If
            Next
            f.name = newname
            ReindexPendingFile(d, f)
            If TotalBytesUnindexed = 0 Then TotalBytesUnindexed = 1
            RefreshDisplay()
        End If
    End Sub
    Private Sub 删除文件ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 删除文件ToolStripMenuItem.Click
        Dim selectedItems As List(Of ListViewItem) = GetSelectedListViewItems()
        If ListView1.Tag IsNot Nothing AndAlso
        selectedItems.Count > 0 AndAlso
        MessageBox.Show(New Form With {.TopMost = True}, $"{My.Resources.ResText_DelConfrm}{selectedItems.Count}{My.Resources.ResText_Files_C}", My.Resources.ResText_Warning, MessageBoxButtons.OKCancel) = DialogResult.OK Then
            Dim filesToRemove As New List(Of ltfsindex.file)
            Dim d As ltfsindex.directory = DirectCast(ListView1.Tag, ltfsindex.directory)
            For Each ItemSelected As ListViewItem In selectedItems
                If ItemSelected.Tag IsNot Nothing AndAlso TypeOf (ItemSelected.Tag) Is ltfsindex.file Then
                    Dim f As ltfsindex.file = DirectCast(ItemSelected.Tag, ltfsindex.file)
                    RemovePendingByFile(d, f)
                    filesToRemove.Add(f)
                End If
            Next
            If filesToRemove.Count > 0 AndAlso d.RemoveFiles(filesToRemove) > 0 AndAlso TotalBytesUnindexed = 0 Then TotalBytesUnindexed = 1
            UnwrittenCountOverrideValue = 0
            UnwrittenSizeOverrideValue = 0
            RefreshDisplay()
        End If
    End Sub
    Private Sub 添加文件ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 添加文件ToolStripMenuItem.Click
        If ListView1.Tag IsNot Nothing AndAlso OpenFileDialog1.ShowDialog = DialogResult.OK Then
            Dim d As ltfsindex.directory = DirectCast(ListView1.Tag, ltfsindex.directory)
            Dim overwrite As Boolean = 覆盖已有文件ToolStripMenuItem.Checked
            If My.Settings.LTFSWriter_MatchPattern IsNot Nothing AndAlso My.Settings.LTFSWriter_MatchPattern.Length > 0 Then
                AddFileOrDir(d, OpenFileDialog1.FileNames, My.Settings.LTFSWriter_MatchPattern, overwrite)
            Else
                AddFileOrDir(d, OpenFileDialog1.FileNames, overwrite)
            End If
            'For Each fpath As String In OpenFileDialog1.FileNames
            '    Dim f As IO.FileInfo = New IO.FileInfo(fpath)
            '    Try
            '        AddFile(f, d, 覆盖已有文件ToolStripMenuItem.Checked)
            '        PrintMsg("文件添加成功")
            '    Catch ex As Exception
            '        PrintMsg("文件添加失败")
            '        MessageBox.Show(New Form With {.TopMost = True}, ex.ToString())
            '    End Try
            'Next
            'RefreshDisplay()
        End If
    End Sub
    Private Function KeepPlannedSourceFile(candidate As GlobHelper.AddFile,
                                           reparseCache As Dictionary(Of String, Boolean)) As Boolean
        Try
            Dim fi As IO.FileInfo = candidate.SourceInfo
            If fi Is Nothing Then fi = New IO.FileInfo(candidate.SourceFullPath)
            fi.Refresh()
            Dim keepFile As Boolean = Not fi.Attributes.HasFlag(IO.FileAttributes.ReparsePoint)

            Dim dir As IO.DirectoryInfo = fi.Directory
            While keepFile AndAlso dir IsNot Nothing
                Dim dirPath = dir.FullName
                Dim isReparse As Boolean = False
                If Not reparseCache.TryGetValue(dirPath, isReparse) Then
                    isReparse = dir.Attributes.HasFlag(IO.FileAttributes.ReparsePoint)
                    reparseCache(dirPath) = isReparse
                End If
                keepFile = Not isReparse
                dir = dir.Parent
            End While
            Return keepFile
        Catch
            Return False
        End Try
    End Function

    Private Sub AddFilePlanOnWorker(d As ltfsindex.directory,
                                    fileSeq As IEnumerable(Of GlobHelper.AddFile),
                                    overwrite As Boolean,
                                    itemCount As Integer)
        Dim succeeded As Boolean = False
        Try
            BeginAddFileLookup()
            StopFlag = False
            If fileSeq Is Nothing Then fileSeq = Enumerable.Empty(Of GlobHelper.AddFile)()
            If itemCount >= 0 Then
                PrintMsg($"{My.Resources.ResText_Adding}{itemCount}{My.Resources.ResText_Items_x}")
            Else
                PrintMsg(My.Resources.ResText_Adding)
            End If

            Dim helper As New GlobHelper() With {
                .schema = schema,
                .OnStopFlagInquiry = Function() As Boolean
                                         Return StopFlag
                                     End Function
            }

            Dim sequence As IEnumerable(Of GlobHelper.AddFile) = fileSeq
            If My.Settings.LTFSWriter_SkipSymlink Then
                Dim reparseCache As New Dictionary(Of String, Boolean)(StringComparer.Ordinal)
                sequence = sequence.Where(Function(candidate) KeepPlannedSourceFile(candidate, reparseCache))
            End If

            For Each af In sequence
                If StopFlag Then Exit For

                Dim parentRel = IO.Path.GetDirectoryName(af.RelativePath)
                Dim targetDir As ltfsindex.directory = d
                If Not String.IsNullOrEmpty(parentRel) Then
                    targetDir = helper.EnsureDirectoryChain(d, "", parentRel)
                End If

                Try
                    Dim fi As IO.FileInfo = af.SourceInfo
                    If fi Is Nothing Then fi = New IO.FileInfo(af.SourceFullPath)
                    fi.Refresh()
                    AddFile(fi, targetDir, overwrite, af.SourceXattrPath)
                Catch
                End Try
            Next

            succeeded = Not StopFlag
        Catch ex As Exception
            PrintMsg($"Add failed: {ex.Message}")
            SetStatusLight(LWStatus.Err)
            Try
                Invoke(Sub() MessageBox.Show(New Form With {.TopMost = True}, $"{ex}{vbCrLf}{ex.StackTrace}"))
            Catch
            End Try
        Finally
            EndAddFileLookup()
            StopFlag = False
            UnwrittenSizeOverrideValue = 0
            UnwrittenCountOverrideValue = 0
            RefreshDisplay()
            If succeeded Then
                PrintMsg(My.Resources.ResText_AddFin)
                SetStatusLight(LWStatus.Succ)
            End If
            LockGUI(False)
        End Try
    End Sub

    Private Shared Function GetEnumerableCount(Of T)(values As IEnumerable(Of T)) As Integer
        If values Is Nothing Then Return -1
        Dim genericCollection As ICollection(Of T) = TryCast(values, ICollection(Of T))
        If genericCollection IsNot Nothing Then Return genericCollection.Count
        Dim collection As System.Collections.ICollection = TryCast(values, System.Collections.ICollection)
        If collection IsNot Nothing Then Return collection.Count
        Return -1
    End Function

    Public Sub AddFileOrDir(d As ltfsindex.directory, Paths As IEnumerable(Of String), ByVal match As String, Optional ByVal overwrite As Boolean = False)
        If Paths Is Nothing Then Exit Sub
        Dim pathCount As Integer = GetEnumerableCount(Paths)
        SetStatusLight(LWStatus.Busy)
        Dim th As New Threading.Thread(
            Sub()
                Try
                    Dim matcher = GlobCollector.BuildMatcherFromString(match)
                    Dim files = GlobCollector.EnumerateAdd_ByFullPathInputs(Paths, matcher)
                    AddFilePlanOnWorker(d, files, overwrite, pathCount)
                Catch ex As Exception
                    PrintMsg($"Add failed: {ex.Message}")
                    SetStatusLight(LWStatus.Err)
                    Try
                        Invoke(Sub() MessageBox.Show(New Form With {.TopMost = True}, $"{ex}{vbCrLf}{ex.StackTrace}"))
                    Catch
                    End Try
                    LockGUI(False)
                End Try
            End Sub)
        LockGUI()
        th.Start()
    End Sub

    Public Sub AddStableFilePlan(d As ltfsindex.directory,
                                 fileSeq As IEnumerable(Of GlobHelper.AddFile),
                                 Optional ByVal overwrite As Boolean = False)
        If fileSeq Is Nothing Then Exit Sub
        Dim itemCount As Integer = GetEnumerableCount(fileSeq)

        SetStatusLight(LWStatus.Busy)
        Dim th As New Threading.Thread(
            Sub()
                AddFilePlanOnWorker(d, fileSeq, overwrite, itemCount)
            End Sub)
        LockGUI()
        th.Start()
    End Sub
    Public Sub AddFileOrDir(d As ltfsindex.directory, Paths As IEnumerable(Of String), Optional ByVal overwrite As Boolean = False,
                            Optional ByVal exceptExtension As String() = Nothing)
        If Paths Is Nothing Then Exit Sub
        Dim pathCount As Integer = GetEnumerableCount(Paths)
        SetStatusLight(LWStatus.Busy)
        Dim th As New Threading.Thread(
                Sub()
                    BeginAddFileLookup()
                    Try
                        StopFlag = False
                        If pathCount >= 0 Then
                            PrintMsg($"{My.Resources.ResText_Adding}{pathCount}{My.Resources.ResText_Items_x}")
                        Else
                            PrintMsg(My.Resources.ResText_Adding)
                        End If
                        Dim numi As Integer = 0
                        For Each path As String In Paths
                            If String.IsNullOrWhiteSpace(path) Then Continue For
                            path = FileSystemPathResolver.ResolveExistingPath(path)
                            If Not path.StartsWith("\\") Then path = $"\\?\{path}"
                            Dim i As Integer = Threading.Interlocked.Increment(numi)
                            If StopFlag Then Exit For
                            Try
                                If IO.File.Exists(path) Then
                                    Dim f As IO.FileInfo = New IO.FileInfo(path)
                                    Dim skip As Boolean = False
                                    If exceptExtension IsNot Nothing AndAlso exceptExtension.Count > 0 Then
                                        For Each ext As String In exceptExtension
                                            If path.EndsWith(ext, StringComparison.OrdinalIgnoreCase) Then
                                                skip = True
                                                Exit For
                                            End If
                                        Next
                                    End If
                                    If Not skip Then
                                        PrintMsg($"{My.Resources.ResText_Adding} [{i}/{If(pathCount >= 0, pathCount.ToString(), "?")}] {f.Name}")
                                        AddFile(f, d, overwrite)
                                    Else
                                        PrintMsg($"{My.Resources.ResText_Skip} [{i}/{If(pathCount >= 0, pathCount.ToString(), "?")}] {f.Name}")
                                    End If
                                ElseIf IO.Directory.Exists(path) Then
                                    Dim f As IO.DirectoryInfo = New IO.DirectoryInfo(path)
                                    PrintMsg($"{My.Resources.ResText_Adding} [{i}/{If(pathCount >= 0, pathCount.ToString(), "?")}] {f.Name}")
                                    AddDirectry(f, d, overwrite, exceptExtension)
                                End If
                            Catch ex As Exception
                                Invoke(Sub() MessageBox.Show(New Form With {.TopMost = True}, $"{ex.ToString()}{vbCrLf}{ex.StackTrace}"))
                                SetStatusLight(LWStatus.Err)
                            End Try
                        Next

                        If ParallelAdd Then
                            SyncLock _pendingQueueLock
                                UnwrittenFiles.Sort(New Comparison(Of FileRecord)(Function(a As FileRecord, b As FileRecord) As Integer
                                                                                      Return ExplorerComparer.Compare(a.SourcePath, b.SourcePath)
                                                                                  End Function))
                            End SyncLock
                        End If
                        StopFlag = False
                        UnwrittenSizeOverrideValue = 0
                        UnwrittenCountOverrideValue = 0
                        RefreshDisplay()
                        PrintMsg(My.Resources.ResText_AddFin)
                        SetStatusLight(LWStatus.Succ)
                        LockGUI(False)
                    Finally
                        EndAddFileLookup()
                    End Try
                End Sub)
        LockGUI()
        th.Start()
    End Sub

    Private Shared Iterator Function EnumerateTopLevelAddPaths(sourceDirectory As IO.DirectoryInfo) As IEnumerable(Of String)
        If sourceDirectory Is Nothing Then Exit Function
        For Each entry As IO.FileSystemInfo In sourceDirectory.EnumerateFileSystemInfos()
            If entry IsNot Nothing Then Yield entry.FullName
        Next
    End Function

    Private Sub ListView1_DragEnter(sender As Object, e As DragEventArgs) Handles ListView1.DragEnter
        If Not AllowOperation OrElse Not MenuStrip1.Enabled Then
            PrintMsg(My.Resources.ResText_DragNA)
            Exit Sub
        End If
        If ListView1.Tag IsNot Nothing AndAlso TypeOf ListView1.Tag Is ltfsindex.directory Then
            Dim Paths As String() = DirectCast(e.Data.GetData(GetType(String())), String())
            Dim d As ltfsindex.directory = DirectCast(ListView1.Tag, ltfsindex.directory)
            Dim overwrite As Boolean = 覆盖已有文件ToolStripMenuItem.Checked
            If My.Settings.LTFSWriter_MatchPattern IsNot Nothing AndAlso My.Settings.LTFSWriter_MatchPattern.Length > 0 Then
                AddFileOrDir(d, Paths, My.Settings.LTFSWriter_MatchPattern, overwrite)
            Else
                AddFileOrDir(d, Paths, overwrite)
            End If
        End If
    End Sub
    Private Sub 导入文件ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 导入文件ToolStripMenuItem.Click
        If ListView1.Tag IsNot Nothing Then
            Dim sourceDirectory As String = SelectWriterFolder()
            If String.IsNullOrEmpty(sourceDirectory) Then Exit Sub
            Dim dnew As IO.DirectoryInfo = New IO.DirectoryInfo(sourceDirectory)
            Dim d As ltfsindex.directory = DirectCast(ListView1.Tag, ltfsindex.directory)
            If My.Settings.LTFSWriter_MatchPattern IsNot Nothing AndAlso My.Settings.LTFSWriter_MatchPattern.Length > 0 Then
                AddFileOrDir(d, EnumerateTopLevelAddPaths(dnew), My.Settings.LTFSWriter_MatchPattern, 覆盖已有文件ToolStripMenuItem.Checked)
            Else
                AddFileOrDir(d, EnumerateTopLevelAddPaths(dnew), 覆盖已有文件ToolStripMenuItem.Checked)
            End If
            'Try
            '    ConcatDirectory(dnew, d, 覆盖已有文件ToolStripMenuItem.Checked)
            '    PrintMsg("导入成功")
            'Catch ex As Exception
            '    PrintMsg("导入失败")
            '    MessageBox.Show(New Form With {.TopMost = True}, ex.ToString())
            'End Try
            'RefreshDisplay()
        End If
    End Sub
    Private Sub 添加目录ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 添加目录ToolStripMenuItem.Click
        If ListView1.Tag IsNot Nothing Then
            Dim COFD As New CommonOpenFileDialog
            COFD.Multiselect = True
            COFD.IsFolderPicker = True
            If COFD.ShowDialog = CommonFileDialogResult.Ok Then
                Dim d As ltfsindex.directory = DirectCast(ListView1.Tag, ltfsindex.directory)
                Dim dirs As New List(Of String)
                For Each fn As String In COFD.FileNames
                    dirs.Add(fn)
                Next
                If My.Settings.LTFSWriter_MatchPattern IsNot Nothing AndAlso My.Settings.LTFSWriter_MatchPattern.Length > 0 Then
                    AddFileOrDir(DirectCast(ListView1.Tag, ltfsindex.directory), dirs.ToArray(), My.Settings.LTFSWriter_MatchPattern, 覆盖已有文件ToolStripMenuItem.Checked)
                Else
                    AddFileOrDir(DirectCast(ListView1.Tag, ltfsindex.directory), dirs.ToArray(), 覆盖已有文件ToolStripMenuItem.Checked)
                End If

            End If
        End If
    End Sub
    Private Sub 新建目录ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 新建目录ToolStripMenuItem.Click
        If ListView1.Tag IsNot Nothing Then
            Dim s As String = ""
            If (DisplayHelper.ShowInputDialog(My.Resources.ResText_DirName, My.Resources.ResText_NewDir, s) <> DialogResult.OK) Then Exit Sub
            If s <> "" Then
                If s.Length >= 3 AndAlso s(1) = ":" AndAlso s(2) = "\" Then s = s.Substring(2)
                If (s.Replace("\", "").Replace("/", "").IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0) Then
                    MessageBox.Show(New Form With {.TopMost = True}, My.Resources.ResText_DirNIllegal)
                    Exit Sub
                End If
                Dim dirList As String() = s.Split({"\", "/"}, StringSplitOptions.RemoveEmptyEntries)
                Dim d As ltfsindex.directory = DirectCast(ListView1.Tag, ltfsindex.directory)
                Dim path As String = GetPath(TreeView1.SelectedNode)
                For Each newdirName As String In dirList

                    If d.FindDirectoryByName(newdirName) IsNot Nothing Then
                        MessageBox.Show(New Form With {.TopMost = True}, My.Resources.ResText_DirNExist)
                        Exit Sub
                    End If

                    Dim newdir As New ltfsindex.directory With {
                        .name = newdirName,
                        .creationtime = Now.ToUniversalTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff00Z"),
                        .fileuid = schema.highestfileuid + 1,
                        .backuptime = .creationtime,
                        .accesstime = .creationtime,
                        .changetime = .creationtime,
                        .modifytime = .creationtime,
                        .readonly = False
                        }
                    schema.highestfileuid += 1
                    d.AddDirectory(newdir)
                    d = newdir
                    path &= $"\{newdirName}"
                Next
                RefreshDisplay()
                ChangeDirectory(path)
            End If
        End If
    End Sub
    Private Sub 删除目录ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 删除目录ToolStripMenuItem.Click
        DeleteDir()
    End Sub
    Private Sub 重命名目录ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 重命名目录ToolStripMenuItem.Click
        RenameDir()
    End Sub
    <Category("LTFSWriter")>
    Public Property RestorePosition As TapeUtils.PositionData
    Public Sub RestoreFile(FileName As String, FileIndex As ltfsindex.file)
        If Not FileName.StartsWith("\\") Then FileName = $"\\?\{FileName}"
        Dim FileExist As Boolean = True
        If Not IO.File.Exists(FileName) Then
            FileExist = False
        Else
            Dim finfo As New IO.FileInfo(FileName)
            If finfo.Length <> FileIndex.length Then
                FileExist = False
            ElseIf finfo.CreationTimeUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffffff00Z") <> FileIndex.creationtime Then
                FileExist = False
                'ElseIf finfo.LastAccessTimeUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffffff00Z") <> FileIndex.accesstime Then
                '    FileExist = False
            ElseIf finfo.LastWriteTimeUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffffff00Z") <> FileIndex.modifytime Then
                FileExist = False
            End If
            If Not FileExist Then PrintMsg($"{My.Resources.ResText_OverwritingDF}{FileName} {finfo.Length}->{FileIndex.length}{vbCrLf _
                                                  }ct{finfo.CreationTimeUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffffff00Z")}->{FileIndex.creationtime}{vbCrLf _
                                                  }at{finfo.LastAccessTimeUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffffff00Z")}->{FileIndex.accesstime}{vbCrLf _
                                                  }wt{finfo.LastWriteTimeUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffffff00Z")}->{FileIndex.modifytime}{vbCrLf _
                                                  }", LogOnly:=True)
        End If
        If FileExist AndAlso Not (FileIndex.GetXAttr(ltfsindex.file.xattr.ApplicationSpecific.Fragment, True).ToLower() = "true") Then
            Threading.Interlocked.Increment(CurrentFilesProcessed)
            Threading.Interlocked.Increment(TotalFilesProcessed)
            Exit Sub
        End If
        IsWriting = False
        If IO.File.Exists(FileName) Then
            Dim fi As New IO.FileInfo(FileName)
            fi.Attributes = fi.Attributes And Not IO.FileAttributes.ReadOnly
            If My.Settings.LTFSWriter_ClearBeforeOverwrite Then
                IO.File.Delete(FileName)
                IO.File.WriteAllBytes(FileName, {})
            End If
        Else
            IO.File.WriteAllBytes(FileName, {})
        End If
        If FileIndex.length > 0 Then
            Dim reffile As String = ""
            If FileIndex.TempObj IsNot Nothing AndAlso TypeOf FileIndex.TempObj Is ltfsindex.file.refFile Then reffile = CType(FileIndex.TempObj, ltfsindex.file.refFile).FileName.ToString()
            If reffile <> "" AndAlso IO.File.Exists(reffile) Then
                IO.File.Copy(reffile, FileName, True)
                Dim finfo As New IO.FileInfo(FileName)
                finfo.CreationTimeUtc = TapeUtils.ParseTimeStamp(FileIndex.creationtime)
                finfo.LastWriteTimeUtc = TapeUtils.ParseTimeStamp(FileIndex.modifytime)
                finfo.IsReadOnly = FileIndex.readonly
                finfo.LastAccessTimeUtc = TapeUtils.ParseTimeStamp(FileIndex.accesstime)
                Threading.Interlocked.Add(TotalBytesProcessed, FileIndex.length)
                Threading.Interlocked.Add(CurrentBytesProcessed, FileIndex.length)
            Else
                If FileIndex.TempObj Is Nothing OrElse TypeOf FileIndex.TempObj IsNot ltfsindex.file.refFile Then FileIndex.TempObj = New ltfsindex.file.refFile()
                CType(FileIndex.TempObj, ltfsindex.file.refFile).FileName = FileName
                Dim fs As New IO.FileStream(FileName, IO.FileMode.OpenOrCreate, IO.FileAccess.ReadWrite, IO.FileShare.Read, 8388608, IO.FileOptions.None)
                Try
                    FileIndex.extentinfo.Sort(New Comparison(Of ltfsindex.file.extent)(Function(a As ltfsindex.file.extent, b As ltfsindex.file.extent)
                                                                                           If a.startblock <> b.startblock Then Return a.startblock.CompareTo(b.startblock)
                                                                                           Return a.fileoffset.CompareTo(b.fileoffset)
                                                                                       End Function))
                    For Each fe As ltfsindex.file.extent In FileIndex.extentinfo
                        Dim succ As Boolean = False
                        If FileIndex.extentinfo.Count > 1 Then RestorePosition = New TapeUtils.PositionData(driveHandle)
                        PrintMsg($"Extent {FileIndex.extentinfo.IndexOf(fe)}:P={GetPartitionNumber(CType(fe.partition, ltfslabel.PartitionLabel))} B={fe.startblock} BO={fe.byteoffset} BC={fe.bytecount} FO={fe.fileoffset}", LogOnly:=True)
                        Do
                            Dim BlockAddress As ULong = CULng(fe.startblock)
                            Dim ByteOffset As Long = fe.byteoffset
                            Dim FileOffset As Long = fe.fileoffset
                            Dim Partition As Long = fe.partition
                            Dim TotalBytes As Long = fe.bytecount
                            'Dim p As New TapeUtils.PositionData(TapeDrive)
                            If RestorePosition Is Nothing OrElse RestorePosition.BlockNumber <> BlockAddress OrElse RestorePosition.PartitionNumber <> Partition Then
                                PrintMsg($"LOCATE to P{GetPartitionNumber(CType(Partition, ltfslabel.PartitionLabel))} B{BlockAddress}", LogOnly:=True)
                                TapeUtils.Locate(driveHandle, BlockAddress, GetPartitionNumber(CType(Partition, ltfslabel.PartitionLabel)), TapeUtils.LocateDestType.Block)
                                RestorePosition = New TapeUtils.PositionData(driveHandle)
                            End If
                            PrintMsg($"Position: P{RestorePosition.PartitionNumber} B{RestorePosition.BlockNumber}", LogOnly:=True)
                            fs.Seek(FileOffset, IO.SeekOrigin.Begin)
                            Dim ReadedSize As Long = 0
                            While (ReadedSize < TotalBytes + ByteOffset) And Not StopFlag
                                Dim CurrentBlockLen As UInteger = CUInt(Math.Min(plabel.blocksize, TotalBytes + ByteOffset - ReadedSize))
                                Dim Data As Byte() = Nothing
                                Dim readsucc As Boolean = False
                                Dim ignored As Boolean = False
                                While Not readsucc
                                    Dim sense() As Byte = {}
                                    Try
                                        Data = TapeUtils.ReadBlock(driveHandle, sense, CurrentBlockLen, True)

                                    Catch ex As Exception
                                        Dim dResult As DialogResult
                                        Invoke(Sub() dResult = MessageBox.Show(Me, $"{My.Resources.ResText_RErrSCSI}{vbCrLf}{ex.ToString}", My.Resources.ResText_Warning, MessageBoxButtons.AbortRetryIgnore))
                                        Select Case dResult
                                            Case DialogResult.Abort
                                                StopFlag = True
                                                Throw ex
                                            Case DialogResult.Retry
                                                readsucc = False
                                            Case DialogResult.Ignore
                                                readsucc = True
                                                Exit While
                                        End Select
                                        Continue While
                                    End Try
                                    If ((sense(2) >> 6) And &H1) = 1 Then
                                        If (sense(2) And &HF) = 13 Then
                                            readsucc = True
                                        Else
                                            PrintMsg(My.Resources.ResText_EWEOM, True, DeDupe:=True)
                                            readsucc = True
                                            Exit While
                                        End If
                                    ElseIf (sense(2) And &HF) <> 0 Then
                                        PrintMsg($"sense err {TapeUtils.Byte2Hex(sense, True)}", Warning:=True, LogOnly:=True)
                                        Dim AutoIgnore As Boolean = False
                                        If My.Settings.LTFSWriter_IgnoreILI Then
                                            Dim DriveCode As UShort = CUShort(sense(16)) << 8 Or sense(17)
                                            If DriveCode = &H2C72 OrElse DriveCode = &H2C73 Then
                                                AutoIgnore = True
                                            ElseIf DriveCode = &H3021 Then 'Filemark
                                                'Skip index
                                                While TapeUtils.ReadBlock(driveHandle, Nothing, CurrentBlockLen).Length > 0
                                                End While
                                                AutoIgnore = True
                                            End If
                                        End If
                                        If Not AutoIgnore Then
                                            Try
                                                Throw New Exception("SCSI sense error")
                                            Catch ex As Exception
                                                Dim dResult As DialogResult
                                                Invoke(Sub() dResult = MessageBox.Show(New Form With {.TopMost = True}, $"{My.Resources.ResText_RestoreErr}{vbCrLf}{TapeUtils.ParseSenseData(sense)}{vbCrLf}{vbCrLf}sense{vbCrLf}{TapeUtils.Byte2Hex(sense, True)}{vbCrLf}{ex.StackTrace}", My.Resources.ResText_Warning, MessageBoxButtons.AbortRetryIgnore))
                                                Select Case dResult
                                                    Case DialogResult.Abort
                                                        fs.Close()
                                                        StopFlag = True
                                                        Throw New Exception(TapeUtils.ParseSenseData(sense))
                                                    Case DialogResult.Retry
                                                        readsucc = False
                                                    Case DialogResult.Ignore
                                                        readsucc = True
                                                        ignored = True
                                                        Exit While
                                                End Select
                                            End Try
                                        End If

                                    Else
                                        readsucc = True
                                    End If
                                End While
                                SyncLock RestorePosition
                                    RestorePosition.BlockNumber = CULng(RestorePosition.BlockNumber + 1)
                                End SyncLock
                                If Data.Length <> CurrentBlockLen OrElse CurrentBlockLen = 0 Then
                                    Dim errmsg As String = $"Error reading at p{RestorePosition.PartitionNumber}b{RestorePosition.BlockNumber}: readed length {Data.Length} should be {CurrentBlockLen}"
                                    PrintMsg(errmsg, LogOnly:=True, ForceLog:=True)
                                    If (Not ignored) AndAlso (Not My.Settings.LTFSWriter_IgnoreILI) Then
                                        While True
                                            Dim dResult As DialogResult
                                            Invoke(Sub() dResult = MessageBox.Show(New Form With {.TopMost = True}, errmsg, My.Resources.ResText_Warning, MessageBoxButtons.AbortRetryIgnore))
                                            Select Case dResult
                                                Case DialogResult.Abort
                                                    fs.Close()
                                                    StopFlag = True
                                                    Throw New Exception($"{My.Resources.ResText_OpCancelled}")
                                                Case DialogResult.Retry
                                                Case DialogResult.Ignore
                                                    ignored = True
                                                    Exit While
                                            End Select
                                        End While
                                    End If
                                    If (Not ignored) AndAlso (Not My.Settings.LTFSWriter_IgnoreILI) Then
                                        succ = False
                                        SetStatusLight(LWStatus.Err)
                                        Exit Do
                                    Else
                                        CurrentBlockLen = CUInt(Math.Min(Data.Length, CurrentBlockLen))
                                    End If
                                End If
                                ReadedSize += CurrentBlockLen - ByteOffset
                                fs.Write(Data, CInt(ByteOffset), CInt(CurrentBlockLen - ByteOffset))
                                Threading.Interlocked.Add(TotalBytesProcessed, CurrentBlockLen - ByteOffset)
                                Threading.Interlocked.Add(CurrentBytesProcessed, CurrentBlockLen - ByteOffset)
                                ByteOffset = 0
                                If Pause Then
                                    Invoke(Sub()
                                               With Microsoft.WindowsAPICodePack.Taskbar.TaskbarManager.Instance
                                                   .SetProgressState(Microsoft.WindowsAPICodePack.Taskbar.TaskbarProgressBarState.Paused)
                                               End With
                                           End Sub)
                                End If
                                While Pause
                                    Threading.Thread.Sleep(10)
                                    If Not Pause Then
                                        Invoke(Sub()
                                                   With Microsoft.WindowsAPICodePack.Taskbar.TaskbarManager.Instance
                                                       .SetProgressState(Microsoft.WindowsAPICodePack.Taskbar.TaskbarProgressBarState.Normal)
                                                   End With
                                               End Sub)
                                    End If
                                End While
                            End While
                            If StopFlag Then
                                fs.Close()
                                IO.File.Delete(FileName)
                                succ = True
                                Exit Do
                            End If
                            succ = True
                            Exit Do
                        Loop

                        If Not succ Then
                            PrintMsg($"{FileIndex.name}{My.Resources.ResText_RestoreErr}", ForceLog:=True)
                            SetStatusLight(LWStatus.Err)
                            Exit For
                        End If
                        If StopFlag Then Exit Sub
                    Next
                Catch ex As Exception
                    PrintMsg($"{FileIndex.name}{My.Resources.ResText_RestoreErr}{ex.ToString}", ForceLog:=True)
                    Invoke(Sub() MessageBox.Show($"{FileIndex.name}{My.Resources.ResText_RestoreErr}{ex.ToString}"))
                    SetStatusLight(LWStatus.Err)
                End Try
                Try
                    fs.Flush()
                    fs.Close()
                Catch ex As Exception

                End Try
                Dim finfo As New IO.FileInfo(FileName)
                Try
                    finfo.CreationTimeUtc = TapeUtils.ParseTimeStamp(FileIndex.creationtime)
                    finfo.LastWriteTimeUtc = TapeUtils.ParseTimeStamp(FileIndex.modifytime)
                    finfo.IsReadOnly = FileIndex.readonly
                    finfo.LastAccessTimeUtc = TapeUtils.ParseTimeStamp(FileIndex.accesstime)
                Catch ex As Exception

                End Try
            End If
        Else
            Dim finfo As New IO.FileInfo(FileName)
            Try
                finfo.CreationTimeUtc = TapeUtils.ParseTimeStamp(FileIndex.creationtime)
                finfo.LastWriteTimeUtc = TapeUtils.ParseTimeStamp(FileIndex.modifytime)
                finfo.IsReadOnly = FileIndex.readonly
                finfo.LastAccessTimeUtc = TapeUtils.ParseTimeStamp(FileIndex.accesstime)
            Catch ex As Exception

            End Try
        End If
        Threading.Interlocked.Increment(CurrentFilesProcessed)
        Threading.Interlocked.Increment(TotalFilesProcessed)
    End Sub
    Private Class TarTapeRangeReader
        Private ReadOnly _writer As LTFSWriter
        Private ReadOnly _source As ltfsindex.file
        Private ReadOnly _extents As New List(Of ltfsindex.file.extent)
        Private _extentIndex As Integer = -1
        Private _extentRemaining As Long
        Private _block As Byte() = Nothing
        Private _blockOffset As Integer
        Private _firstBlock As Boolean
        Private _position As Long

        Public Sub New(writer As LTFSWriter, source As ltfsindex.file)
            _writer = writer
            _source = source
            If source Is Nothing OrElse source.extentinfo Is Nothing OrElse source.extentinfo.Count = 0 Then
                Throw New IO.InvalidDataException("tar source has no extents")
            End If
            For Each extent As ltfsindex.file.extent In source.extentinfo
                _extents.Add(extent)
            Next
            _extents.Sort(New Comparison(Of ltfsindex.file.extent)(Function(a As ltfsindex.file.extent, b As ltfsindex.file.extent)
                                                                       Return a.fileoffset.CompareTo(b.fileoffset)
                                                                   End Function))
        End Sub

        Public Sub CopyRange(entry As TarMetadataEntry, outputPath As String)
            If entry Is Nothing OrElse entry.Length < 0 OrElse entry.DataOffset < 0 OrElse
                entry.DataOffset > _source.length OrElse entry.Length > _source.length - entry.DataOffset Then
                Throw New IO.InvalidDataException("tar member range is outside the source file")
            End If
            If entry.DataOffset < _position Then
                Throw New IO.InvalidDataException("tar member ranges are not in forward order")
            End If
            Skip(entry.DataOffset - _position)
            Dim parentPath As String = IO.Path.GetDirectoryName(outputPath)
            If Not String.IsNullOrEmpty(parentPath) Then IO.Directory.CreateDirectory(parentPath)
            If IO.File.Exists(outputPath) Then
                Dim existingInfo As New IO.FileInfo(outputPath)
                If existingInfo.IsReadOnly Then existingInfo.IsReadOnly = False
            End If
            Dim hasher As New System.IO.Hashing.XxHash128()
            Using output As New IO.FileStream(outputPath, IO.FileMode.Create, IO.FileAccess.Write, IO.FileShare.None, 1048576, IO.FileOptions.SequentialScan)
                Dim remaining As Long = entry.Length
                While remaining > 0
                    Dim chunk As Byte() = ReadChunk(remaining)
                    If chunk Is Nothing OrElse chunk.Length = 0 Then Throw New IO.EndOfStreamException("tar member ended before its declared length")
                    output.Write(chunk, 0, chunk.Length)
                    hasher.Append(chunk)
                    remaining -= chunk.Length
                    Threading.Interlocked.Add(_writer.TotalBytesProcessed, chunk.Length)
                    Threading.Interlocked.Add(_writer.CurrentBytesProcessed, chunk.Length)
                End While
            End Using
            Dim actualHash As String = BitConverter.ToString(hasher.GetHashAndReset()).Replace("-", "").ToUpperInvariant()
            If Not String.Equals(actualHash, entry.Xxh3128, StringComparison.OrdinalIgnoreCase) Then
                Throw New IO.InvalidDataException($"tar member hash mismatch: {entry.Path}")
            End If
        End Sub

        Private Sub Skip(count As Long)
            Dim remaining As Long = count
            While remaining > 0
                Dim chunk As Byte() = ReadChunk(remaining)
                If chunk Is Nothing OrElse chunk.Length = 0 Then Throw New IO.EndOfStreamException("tar source ended before member offset")
                remaining -= chunk.Length
            End While
        End Sub

        Private Function ReadChunk(maxCount As Long) As Byte()
            EnsureData()
            If _block Is Nothing OrElse _blockOffset >= _block.Length OrElse _extentRemaining <= 0 Then Return Nothing
            Dim count As Integer = CInt(Math.Min(Math.Min(maxCount, CLng(_block.Length - _blockOffset)), _extentRemaining))
            If count <= 0 Then Return Nothing
            Dim result(count - 1) As Byte
            System.Buffer.BlockCopy(_block, _blockOffset, result, 0, count)
            _blockOffset += count
            _extentRemaining -= count
            _position += count
            Return result
        End Function

        Private Sub EnsureData()
            While _extentRemaining <= 0
                StartNextExtent()
            End While
            If _block IsNot Nothing AndAlso _blockOffset < _block.Length Then Return
            Dim extent As ltfsindex.file.extent = _extents(_extentIndex)
            Dim physicalLimit As Long = _extentRemaining + If(_firstBlock, CLng(extent.byteoffset), 0L)
            Dim blockLimit As UInteger = CUInt(Math.Min(CLng(Math.Max(1, _writer.plabel.blocksize)), physicalLimit))
            Dim sense As Byte() = Nothing
            _block = TapeUtils.ReadBlock(_writer.driveHandle, sense, blockLimit, True)
            If sense IsNot Nothing AndAlso sense.Length > 2 AndAlso (sense(2) And &HF) <> 0 Then
                Throw New IO.IOException(TapeUtils.ParseSenseData(sense))
            End If
            If _block Is Nothing OrElse _block.Length = 0 Then Throw New IO.EndOfStreamException("empty tape block while reading tar")
            If _firstBlock Then
                If _block.Length <= extent.byteoffset Then Throw New IO.InvalidDataException("tar extent byte offset exceeds tape block")
                _blockOffset = CInt(extent.byteoffset)
                _firstBlock = False
            Else
                _blockOffset = 0
            End If
        End Sub

        Private Sub StartNextExtent()
            If _extentIndex >= 0 AndAlso _extentRemaining > 0 Then Return
            _extentIndex += 1
            If _extentIndex >= _extents.Count Then Throw New IO.EndOfStreamException("tar source has no more extents")
            Dim extent As ltfsindex.file.extent = _extents(_extentIndex)
            If extent.fileoffset <> _position Then
                Throw New IO.InvalidDataException("tar source extents are not contiguous")
            End If
            TapeUtils.Locate(_writer.driveHandle,
                             CULng(extent.startblock),
                             _writer.GetPartitionNumber(CType(extent.partition, ltfslabel.PartitionLabel)),
                             TapeUtils.LocateDestType.Block)
            _writer.RestorePosition = New TapeUtils.PositionData(_writer.driveHandle)
            _extentRemaining = extent.bytecount
            _block = Nothing
            _blockOffset = 0
            _firstBlock = True
            If _extentRemaining <= 0 Then StartNextExtent()
        End Sub
    End Class

    Private Sub CollectTarVirtualFiles(directory As TarVirtualDirectory, files As List(Of TarVirtualFile))
        If directory Is Nothing Then Return
        For Each file As TarVirtualFile In directory.Files
            Dim exists As Boolean = False
            For Each oldFile As TarVirtualFile In files
                If oldFile.SourceFile Is file.SourceFile AndAlso String.Equals(oldFile.Entry.Path, file.Entry.Path, StringComparison.OrdinalIgnoreCase) Then
                    exists = True
                    Exit For
                End If
            Next
            If Not exists Then files.Add(file)
        Next
        For Each child As TarVirtualDirectory In directory.Directories
            CollectTarVirtualFiles(child, files)
        Next
    End Sub

    Private Sub CreateTarVirtualDirectories(directory As TarVirtualDirectory, archiveRoot As String)
        If directory Is Nothing Then Return
        Dim target As String = archiveRoot
        If Not String.IsNullOrEmpty(directory.RelativePath) Then
            target = IO.Path.Combine(archiveRoot, directory.RelativePath.Replace("/"c, IO.Path.DirectorySeparatorChar))
        End If
        IO.Directory.CreateDirectory(target)
        For Each child As TarVirtualDirectory In directory.Directories
            CreateTarVirtualDirectories(child, archiveRoot)
        Next
    End Sub

    Private Shared Function ToLongPath(path As String) As String
        If path Is Nothing OrElse path.StartsWith("\\", StringComparison.Ordinal) Then Return path
        Return $"\\?\{path}"
    End Function

    Private Sub StartTarExtraction(basePath As String,
                                   selectedFiles As List(Of TarVirtualFile),
                                   selectedDirectories As List(Of TarVirtualDirectory))
        Dim files As New List(Of TarVirtualFile)
        If selectedFiles IsNot Nothing Then files.AddRange(selectedFiles)
        If selectedDirectories IsNot Nothing Then
            For Each directory As TarVirtualDirectory In selectedDirectories
                CollectTarVirtualFiles(directory, files)
            Next
        End If
        If files.Count = 0 AndAlso (selectedDirectories Is Nothing OrElse selectedDirectories.Count = 0) Then Return

        Dim th As New Threading.Thread(
            Sub()
                Dim reserved As Boolean = False
                Try
                    CurrentFilesProcessed = 0
                    CurrentBytesProcessed = 0
                    UnwrittenSizeOverrideValue = 0
                    UnwrittenCountOverrideValue = CULng(files.Count)
                    For Each file As TarVirtualFile In files
                        UnwrittenSizeOverrideValue = CULng(UnwrittenSizeOverrideValue + file.Entry.Length)
                    Next
                    StartTime = Now
                    StopFlag = False
                    SetStatusLight(LWStatus.Busy)
                    PrintMsg(My.Resources.ResText_Restoring)
                    TapeUtils.ReserveUnit(driveHandle)
                    reserved = True
                    TapeUtils.PreventMediaRemoval(driveHandle)
                    RestorePosition = New TapeUtils.PositionData(driveHandle)

                    Dim sources As New List(Of ltfsindex.file)
                    For Each file As TarVirtualFile In files
                        If Not sources.Contains(file.SourceFile) Then sources.Add(file.SourceFile)
                    Next
                    If selectedDirectories IsNot Nothing Then
                        For Each directory As TarVirtualDirectory In selectedDirectories
                            If Not sources.Contains(directory.SourceFile) Then sources.Add(directory.SourceFile)
                            Dim archiveRoot As String = ToLongPath(IO.Path.Combine(basePath, IO.Path.GetFileName(directory.SourceFile.name)))
                            CreateTarVirtualDirectories(directory, archiveRoot)
                        Next
                    End If

                    Dim groups As New Dictionary(Of ltfsindex.file, List(Of TarVirtualFile))
                    For Each file As TarVirtualFile In files
                        Dim group As List(Of TarVirtualFile) = Nothing
                        If Not groups.TryGetValue(file.SourceFile, group) Then
                            group = New List(Of TarVirtualFile)
                            groups(file.SourceFile) = group
                        End If
                        If Not group.Contains(file) Then group.Add(file)
                    Next

                    For Each source As ltfsindex.file In sources
                        Dim group As List(Of TarVirtualFile) = Nothing
                        If Not groups.TryGetValue(source, group) Then Continue For
                        group.Sort(New Comparison(Of TarVirtualFile)(Function(a As TarVirtualFile, b As TarVirtualFile)
                                                                         Return a.Entry.DataOffset.CompareTo(b.Entry.DataOffset)
                                                                     End Function))
                        Dim archiveRoot As String = ToLongPath(IO.Path.Combine(basePath, IO.Path.GetFileName(source.name)))
                        IO.Directory.CreateDirectory(archiveRoot)
                        Dim reader As New TarTapeRangeReader(Me, source)
                        For Each file As TarVirtualFile In group
                            If StopFlag Then Exit For
                            Dim relative As String = file.Entry.Path.Replace("/"c, IO.Path.DirectorySeparatorChar)
                            Dim outputPath As String = ToLongPath(IO.Path.Combine(archiveRoot, relative))
                            reader.CopyRange(file.Entry, outputPath)
                            Threading.Interlocked.Increment(CurrentFilesProcessed)
                            Threading.Interlocked.Increment(TotalFilesProcessed)
                        Next
                        If StopFlag Then Exit For
                    Next
                    If StopFlag Then
                        PrintMsg(My.Resources.ResText_OpCancelled)
                        SetStatusLight(LWStatus.Idle)
                    Else
                        PrintMsg(My.Resources.ResText_RestFin)
                        SetStatusLight(LWStatus.Succ)
                    End If
                Catch ex As Exception
                    PrintMsg($"{My.Resources.ResText_RestoreErr}{ex}", ForceLog:=True)
                    SetStatusLight(LWStatus.Err)
                Finally
                    If reserved Then
                        Try
                            TapeUtils.AllowMediumRemoval(driveHandle)
                        Catch
                        End Try
                        Try
                            TapeUtils.ReleaseUnit(driveHandle)
                        Catch
                        End Try
                    End If
                    StopFlag = False
                    UnwrittenSizeOverrideValue = 0
                    UnwrittenCountOverrideValue = 0
                    LockGUI(False)
                End Try
            End Sub)
        th.IsBackground = True
        LockGUI()
        th.Start()
    End Sub

    Private Sub 提取ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 提取ToolStripMenuItem.Click
        Dim selectedItems As List(Of ListViewItem) = GetSelectedListViewItems()
        If selectedItems.Count = 0 Then Exit Sub
        Dim tarFiles As New List(Of TarVirtualFile)
        Dim tarDirectories As New List(Of TarVirtualDirectory)
        For Each item As ListViewItem In selectedItems
            If TypeOf item.Tag Is TarVirtualFile Then
                tarFiles.Add(DirectCast(item.Tag, TarVirtualFile))
            ElseIf TypeOf item.Tag Is TarVirtualDirectory Then
                tarDirectories.Add(DirectCast(item.Tag, TarVirtualDirectory))
            End If
        Next
        Dim selectedPath As String = SelectWriterFolder()
        If String.IsNullOrEmpty(selectedPath) Then Exit Sub
        If tarFiles.Count > 0 OrElse tarDirectories.Count > 0 Then
            StartTarExtraction(selectedPath, tarFiles, tarDirectories)
            Exit Sub
        End If
        Dim BasePath As String = selectedPath
        LockGUI()
        Dim flist As New List(Of ltfsindex.file)
        For Each SI As ListViewItem In selectedItems
            If TypeOf SI.Tag Is ltfsindex.file Then
                flist.Add(DirectCast(SI.Tag, ltfsindex.file))
            End If
        Next

        Dim th As New Threading.Thread(
                Sub()
                    Try
                        CurrentFilesProcessed = 0
                        CurrentBytesProcessed = 0
                        UnwrittenSizeOverrideValue = 0
                        UnwrittenCountOverrideValue = CULng(flist.Count)
                        StartTime = Now
                        For Each FI As ltfsindex.file In flist
                            UnwrittenSizeOverrideValue = CULng(UnwrittenSizeOverrideValue + FI.length)
                            FI.TempObj = Nothing
                        Next
                        SetStatusLight(LWStatus.Busy)
                        PrintMsg(My.Resources.ResText_Restoring)
                        StopFlag = False
                        TapeUtils.ReserveUnit(driveHandle)
                        TapeUtils.PreventMediaRemoval(driveHandle)
                        RestorePosition = New TapeUtils.PositionData(driveHandle)
                        For Each FileIndex As ltfsindex.file In flist
                            Dim FileName As String = IO.Path.Combine(BasePath, FileIndex.name)
                            RestoreFile(FileName, FileIndex)
                            If StopFlag Then
                                PrintMsg(My.Resources.ResText_OpCancelled)
                                SetStatusLight(LWStatus.Idle)
                                LockGUI(False)
                                Exit Sub
                            End If
                        Next
                    Catch ex As Exception
                        PrintMsg($"{My.Resources.ResText_RestoreErr}{ex.ToString}", ForceLog:=True)
                        SetStatusLight(LWStatus.Err)
                    End Try
                    TapeUtils.AllowMediumRemoval(driveHandle)
                    TapeUtils.ReleaseUnit(driveHandle)
                    StopFlag = False
                    UnwrittenSizeOverrideValue = 0
                    UnwrittenCountOverrideValue = 0
                    LockGUI(False)
                    PrintMsg(My.Resources.ResText_RestFin)
                    SetStatusLight(LWStatus.Succ)
                    Invoke(Sub() MessageBox.Show(New Form With {.TopMost = True}, My.Resources.ResText_RestFin))
                End Sub)
        th.Start()
    End Sub
    Private Sub 提取ToolStripMenuItem1_Click(sender As Object, e As EventArgs) Handles 提取ToolStripMenuItem1.Click
        Dim Nodes As List(Of TreeNode) = SelectedNodes
        If Nodes.Count = 0 Then Exit Sub
        Dim tarDirectories As New List(Of TarVirtualDirectory)
        For Each node As TreeNode In Nodes
            If TypeOf node.Tag Is TarVirtualDirectory Then tarDirectories.Add(DirectCast(node.Tag, TarVirtualDirectory))
        Next
        Dim selectedPath As String = SelectWriterFolder()
        If String.IsNullOrEmpty(selectedPath) Then Exit Sub
        If tarDirectories.Count > 0 Then
            StartTarExtraction(selectedPath, New List(Of TarVirtualFile), tarDirectories)
            Exit Sub
        End If
        Dim totalFiles As Long = 0
        For Each node As TreeNode In Nodes
            Dim selectedDir As ltfsindex.directory = TryCast(node.Tag, ltfsindex.directory)
            If selectedDir IsNot Nothing Then totalFiles += selectedDir.TotalFiles
        Next
        Dim th As New Threading.Thread(
                Sub()
                    Dim succeeded As Boolean = False
                    PrintMsg(My.Resources.ResText_Restoring)
                    SetStatusLight(LWStatus.Busy)
                    Try
                        StopFlag = False
                        PrintMsg(My.Resources.ResText_PrepFile)
                        CurrentFilesProcessed = 0
                        CurrentBytesProcessed = 0
                        UnwrittenSizeOverrideValue = 0
                        UnwrittenCountOverrideValue = CULng(Math.Max(0, totalFiles))
                        StartTime = Now
                        PrintMsg(My.Resources.ResText_RestFile)
                        Dim c As Long = 0
                        TapeUtils.ReserveUnit(driveHandle)
                        TapeUtils.PreventMediaRemoval(driveHandle)
                        RestorePosition = New TapeUtils.PositionData(driveHandle)
                        For Each node As TreeNode In Nodes
                            If StopFlag Then Exit For
                            Dim selectedDir As ltfsindex.directory = TryCast(node.Tag, ltfsindex.directory)
                            If selectedDir Is Nothing Then Continue For
                            Dim outputPath As String = IO.Path.Combine(selectedPath, If(selectedDir.name, String.Empty))
                            If Not outputPath.StartsWith("\\") Then outputPath = $"\\?\{outputPath}"
                            If Not IO.Directory.Exists(outputPath) Then IO.Directory.CreateDirectory(outputPath)
                            Dim outputDirectory As New IO.DirectoryInfo(outputPath)
                            TraverseRestoreFiles(selectedDir, outputDirectory,
                                Sub(fr As FileRecord)
                                    If StopFlag Then Return
                                    c += 1
                                    PrintMsg($"{My.Resources.ResText_Restoring} [{c}/{totalFiles}] {fr.File.name}", False, $"{My.Resources.ResText_Restoring} [{c}/{totalFiles}] {fr.SourcePath}")
                                    RestoreFile(fr.SourcePath, fr.File)
                                End Sub)
                        Next
                        If StopFlag Then
                            PrintMsg(My.Resources.ResText_OpCancelled)
                            SetStatusLight(LWStatus.Idle)
                        Else
                            succeeded = True
                            PrintMsg(My.Resources.ResText_RestFin)
                            SetStatusLight(LWStatus.Succ)
                        End If
                    Catch ex As Exception
                        Invoke(Sub() MessageBox.Show(New Form With {.TopMost = True}, $"{ex.ToString}"))
                        PrintMsg($"{My.Resources.ResText_RestoreErr}{ex.ToString}", ForceLog:=True)
                        SetStatusLight(LWStatus.Err)
                    End Try
                    SyncLock OperationLock
                        SyncLock TapeUtils.GetSCSIOperationLock(driveHandle)
                            TapeUtils.AllowMediumRemoval(driveHandle)
                            TapeUtils.ReleaseUnit(driveHandle)
                        End SyncLock
                    End SyncLock

                    UnwrittenSizeOverrideValue = 0
                    UnwrittenCountOverrideValue = 0
                    LockGUI(False)
                    If Not succeeded AndAlso Not StopFlag Then SetStatusLight(LWStatus.Err)
                End Sub)
        LockGUI()
        th.Start()
    End Sub
    Private Sub 删除ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 删除ToolStripMenuItem.Click
        DeleteDir()
    End Sub
    Private Sub 重命名ToolStripMenuItem_Click_1(sender As Object, e As EventArgs) Handles 重命名ToolStripMenuItem.Click
        RenameDir()
    End Sub
    Private Sub 更新索引ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 更新全部索引ToolStripMenuItem.Click
        Dim th As New Threading.Thread(
                Sub()
                    Try
                        UpdataAllIndex()
                    Catch ex As Exception
                        PrintMsg(My.Resources.ResText_IUErr, False, $"{My.Resources.ResText_IUErr}: {ex.ToString}")
                        SetStatusLight(LWStatus.Err)
                        LockGUI(False)
                    End Try
                End Sub)
        LockGUI(True)
        th.Start()
    End Sub
    Public Function LocateToWritePosition() As Boolean
        If schema.location.partition = ltfsindex.PartitionLabel.a Then
            Dim p As TapeUtils.PositionData = Nothing
            While True
                Dim add_code As UShort = TapeUtils.Locate(driveHandle, schema.previousgenerationlocation.startblock, DataPartition, TapeUtils.LocateDestType.Block)
                p = GetPos
                If p.PartitionNumber <> CByte(schema.previousgenerationlocation.partition) OrElse (p.BlockNumber <> schema.previousgenerationlocation.startblock) Then
                    Dim result As DialogResult
                    Invoke(Sub() result = MessageBox.Show(New Form With {.TopMost = True}, $"Current: P{p.PartitionNumber} B{p.BlockNumber}{vbCrLf}Expected: P{schema.previousgenerationlocation.partition} B{schema.previousgenerationlocation.startblock}{vbCrLf}Additional sense code: 0x{Hex(add_code).ToUpper.PadLeft(4, "0"c)} {TapeUtils.ParseAdditionalSenseCode(add_code)}", My.Resources.ResText_Warning, MessageBoxButtons.AbortRetryIgnore))
                    Select Case result
                        Case DialogResult.Ignore
                            Exit While
                        Case DialogResult.Abort
                            LockGUI(False)
                            Return False
                        Case DialogResult.Retry

                    End Select
                Else
                    Exit While
                End If
            End While

            schema.location.startblock = schema.previousgenerationlocation.startblock
            schema.location.partition = schema.previousgenerationlocation.partition
            PrintMsg($"Position = {p.ToString()}", LogOnly:=True)
            PrintMsg(My.Resources.ResText_RI)
            Dim tmpf As String = LazySchemaStore.CreateTempFilePath("schema-read")
            TapeUtils.ReadToFileMark(driveHandle, tmpf, plabel.blocksize)
            PrintMsg(My.Resources.ResText_AI)
            'Dim sch2 As ltfsindex = ltfsindex.FromSchemaText(Encoding.UTF8.GetString(schraw))
            Dim sch2 As ltfsindex = ltfsindex.FromSchFile(tmpf)
            IO.File.Delete(tmpf)
            PrintMsg(My.Resources.ResText_AISucc)
            schema.previousgenerationlocation = sch2.previousgenerationlocation
            p = GetPos
            PrintMsg($"Position = {p.ToString()}", LogOnly:=True)
            CurrentHeight = CLng(p.BlockNumber)
            Dim Loc As String = GetLocInfo()
            Invoke(Sub() Text = Loc)
        ElseIf CurrentHeight > 0 Then
            Dim p As TapeUtils.PositionData = GetPos
            PrintMsg($"Position = {p.ToString()}", LogOnly:=True)
            If p.BlockNumber <> CurrentHeight Then
                While True
                    Dim add_code As UShort = TapeUtils.Locate(driveHandle, CULng(CurrentHeight), DataPartition, TapeUtils.LocateDestType.Block)
                    p = GetPos
                    If p.PartitionNumber <> DataPartition OrElse p.BlockNumber <> CULng(CurrentHeight) Then
                        Dim result As DialogResult
                        Invoke(Sub() result = MessageBox.Show(New Form With {.TopMost = True}, $"Current: P{p.PartitionNumber} B{p.BlockNumber}{vbCrLf}Expected: P{DataPartition} B{CULng(CurrentHeight)}{vbCrLf}Additional sense code: 0x{Hex(add_code).ToUpper.PadLeft(4, "0"c)} {TapeUtils.ParseAdditionalSenseCode(add_code)}", My.Resources.ResText_Warning, MessageBoxButtons.AbortRetryIgnore))
                        Select Case result
                            Case DialogResult.Ignore
                                Exit While
                            Case DialogResult.Abort
                                LockGUI(False)
                                Return False
                            Case DialogResult.Retry

                        End Select
                    Else
                        Exit While
                    End If
                End While
                PrintMsg($"Position = {p.ToString()}", LogOnly:=True)
            End If
        Else
            Dim p As TapeUtils.PositionData = GetPos
            Dim result As DialogResult
            Invoke(Sub() result = MessageBox.Show(New Form With {.TopMost = True}, $"{My.Resources.ResText_CurPos}P{p.PartitionNumber} B{p.BlockNumber}{My.Resources.ResText_NHWrn}", My.Resources.ResText_WriteWarning, MessageBoxButtons.OKCancel))
            If result = DialogResult.OK Then

            Else
                LockGUI(False)
                Return False
            End If
        End If
        Return True
    End Function
    Public StartTime As Date
    Public Property IsWriting As Boolean = False
    Private _PipeBufferLength As Long = 0
    Private _stopFlagValue As Integer = 0
    Private _activeFastReaderProvider As RustFastReaderProvider = Nothing
    Public Property PipeBufferLength As Long
        Get
            Return _PipeBufferLength
        End Get
        Set(value As Long)
            If value >= 0 Then _PipeBufferLength = value
        End Set
    End Property
    Public Property PipePause As Boolean = False
    Private Function PipeGetLength(reader As System.IO.Pipelines.PipeReader) As Long
        Dim result As ReadResult
        If Threading.Monitor.TryEnter(PipeLock, 0) Then
            Try
                If Not reader.TryRead(result) Then
                    Threading.Monitor.Exit(PipeLock)
                    ' 没有可读数据
                    Return 0
                End If

                ' 获取当前缓冲区的数据总长度
                Dim length As Long = result.Buffer.Length

                ' 不推进读取位置，保留数据以便后续读取
                reader.AdvanceTo(result.Buffer.Start, result.Buffer.Start)
                Threading.Monitor.Exit(PipeLock)
                If My.Settings.LTFSWriter_WaitOnBufferEmpty AndAlso length <= My.Settings.LTFSWriter_MinimumSegmentSize * 2 Then PipePause = True
                Return length
            Catch ex As Exception
                PrintMsg(ex.ToString, LogOnly:=True, ForceLog:=True)
                Threading.Monitor.Exit(PipeLock)
                Return -1
            End Try
            Threading.Monitor.Exit(PipeLock)
        Else
            Return -1
        End If

    End Function

    Private Function PipeGetLength(rb As SpscRingBuffer) As Long
        If rb Is Nothing Then Return -1
        Try
            Dim n As Integer = rb.AvailableToRead()
            Return CLng(n)
        Catch ex As Exception
            PrintMsg(ex.ToString, LogOnly:=True, ForceLog:=True)
            Return -1
        End Try
    End Function
    Private Function CopySequenceToArray(seq As ReadOnlySequence(Of Byte),
                                         dest As Byte(),
                                         destOffset As Integer,
                                         maxBytes As Integer) As Integer
        Dim copied As Integer = 0
        Dim e = seq.GetEnumerator()
        While e.MoveNext() AndAlso copied < maxBytes
            Dim rom As ReadOnlyMemory(Of Byte) = e.Current
            Dim segLen As Integer = Math.Min(rom.Length, maxBytes - copied)
            If segLen <= 0 Then Continue While

            Dim seg As ArraySegment(Of Byte) = Nothing
            If MemoryMarshal.TryGetArray(rom, seg) AndAlso seg.Array IsNot Nothing Then
                Buffer.BlockCopy(seg.Array, seg.Offset, dest, destOffset + copied, segLen)
                copied += segLen
            Else
                Exit While
            End If
        End While
        Return copied
    End Function
    Public Property PipeLock As New Object
    Public Property RingBufferEnabled As Boolean
    Private Function PipeReadExactly(reader As System.IO.Pipelines.PipeReader,
                                     dest As Byte(),
                                     count As Integer,
                                     Optional ct As Threading.CancellationToken = Nothing) As Integer
        If dest Is Nothing Then Throw New ArgumentNullException(NameOf(dest))
        If count < 0 OrElse count > dest.Length Then Throw New ArgumentOutOfRangeException(NameOf(count))
        Dim filled As Long = 0

        SyncLock PipeLock
            While filled < count
                Dim rr As ReadResult = reader.ReadAsync(ct).AsTask().GetAwaiter().GetResult()
                Dim buf As ReadOnlySequence(Of Byte) = rr.Buffer
                If rr.IsCanceled Then
                    reader.AdvanceTo(buf.End, buf.End)
                    Throw New OperationCanceledException("Pipe read was canceled.", ct)
                End If
                If buf.Length = 0 Then
                    reader.AdvanceTo(buf.Start, buf.End)
                    If rr.IsCompleted Then
                        Throw New EndOfStreamException($"Pipe ended before {count} bytes were read; received {filled}.")
                    End If
                    Continue While
                End If
                PipeBufferLength = buf.Length
                If My.Settings.LTFSWriter_WaitOnBufferEmpty AndAlso buf.Length <= My.Settings.LTFSWriter_MinimumSegmentSize * 2 Then PipePause = True
                Dim need As Long = count - filled
                Dim toCopy As Long = Math.Min(need, CLng(buf.Length))

                Dim copied As Long = 0
                If toCopy > 0 Then
                    copied = CopySequenceToArray(buf, dest, CInt(filled), CInt(toCopy))
                End If
                If copied <> toCopy Then
                    reader.AdvanceTo(buf.Start, buf.End)
                    Throw New InvalidDataException("The pipe returned a non-array segment while reading file data.")
                End If
                filled += copied
                Dim consumed As SequencePosition = buf.GetPosition(copied)
                reader.AdvanceTo(consumed, consumed)
            End While
        End SyncLock
        Return CInt(filled)
    End Function

    Private Function PipeReadExactly(rb As SpscRingBuffer,
                                     dest As Byte(),
                                     count As Integer,
                                     Optional ct As Threading.CancellationToken = Nothing) As Integer
        If rb Is Nothing Then Throw New ArgumentNullException(NameOf(rb))
        If dest Is Nothing Then Throw New ArgumentNullException(NameOf(dest))
        If count < 0 OrElse count > dest.Length Then Throw New ArgumentOutOfRangeException(NameOf(count))

        Dim filled As Integer = 0

        While filled < count
            ct.ThrowIfCancellationRequested()
            Dim seg As ArraySegment(Of Byte) = rb.GetReadSegment(1, ct)

            If seg.Count = 0 Then
                Throw New EndOfStreamException($"Ring buffer ended before {count} bytes were read; received {filled}.")
            End If
            PipeBufferLength = rb.AvailableToRead()

            Dim need As Integer = count - filled
            Dim toCopy As Integer = Math.Min(need, seg.Count)

            Buffer.BlockCopy(seg.Array, seg.Offset, dest, filled, toCopy)
            filled += toCopy

            rb.AdvanceRead(toCopy)
        End While

        Return filled
    End Function
    Private Sub PipeDrain(reader As System.IO.Pipelines.PipeReader,
       bytesToDrain As Long,
                          Optional ct As Threading.CancellationToken = Nothing)
        Dim remain = bytesToDrain
        While remain > 0
            SyncLock PipeLock
                Dim result = reader.ReadAsync(ct).AsTask().GetAwaiter().GetResult()
                Dim buffer = result.Buffer
                PipeBufferLength = buffer.Length
                If result.IsCanceled Then
                    reader.AdvanceTo(buffer.End, buffer.End)
                    Throw New OperationCanceledException("Pipe drain was canceled.", ct)
                End If
                If buffer.Length = 0 Then
                    reader.AdvanceTo(buffer.Start, buffer.End)
                    If result.IsCompleted Then Throw New EndOfStreamException($"Pipe ended before {bytesToDrain} bytes were drained; remaining {remain}.")
                    Continue While
                End If
                Dim take As Long = Math.Min(remain, buffer.Length)
                Dim consumed = buffer.GetPosition(take)
                reader.AdvanceTo(consumed, consumed)
                remain -= take
            End SyncLock
        End While
    End Sub

    Private Sub PipeDrain(rb As SpscRingBuffer,
                          bytesToDrain As Long,
                          Optional ct As Threading.CancellationToken = Nothing)

        If rb Is Nothing Then Throw New ArgumentNullException(NameOf(rb))
        If bytesToDrain <= 0 Then Return

        Dim remain As Long = bytesToDrain

        While remain > 0
            ct.ThrowIfCancellationRequested()

            Dim seg As ArraySegment(Of Byte) = rb.GetReadSegment(1, ct)

            If seg.Count = 0 Then
                Throw New EndOfStreamException($"Ring buffer ended before {bytesToDrain} bytes were drained; remaining {remain}.")
            End If

            PipeBufferLength = rb.AvailableToRead()

            Dim take As Integer = CInt(Math.Min(remain, CLng(seg.Count)))
            rb.AdvanceRead(take)
            remain -= take
        End While
    End Sub

    Private Shared Sub ObserveTask(task As Task)
        If task Is Nothing Then Return
        task.GetAwaiter().GetResult()
    End Sub

    Private Shared Sub WaitForTasks(tasks As List(Of Task))
        If tasks Is Nothing OrElse tasks.Count = 0 Then Return
        Dim pending As Task() = tasks.ToArray()
        tasks.Clear()
        Task.WhenAll(pending).GetAwaiter().GetResult()
    End Sub

    Private Shared Sub WaitForHashTasks(tasks As List(Of Task))
        WaitForTasks(tasks)
    End Sub

    Private Sub ApplyFastReaderHashes(fr As FileRecord, hashes As Dictionary(Of String, String))
        If fr Is Nothing OrElse fr.File Is Nothing OrElse hashes Is Nothing Then Return
        Dim v As String = Nothing
        If My.Settings.LTFSWriter_ChecksumEnabled_SHA1 AndAlso hashes.TryGetValue("SHA1", v) Then fr.File.SetXattr(ltfsindex.file.xattr.HashType.SHA1, v)
        If My.Settings.LTFSWriter_ChecksumEnabled_SHA256 AndAlso hashes.TryGetValue("SHA256", v) Then fr.File.SetXattr(ltfsindex.file.xattr.HashType.SHA256, v)
        If My.Settings.LTFSWriter_ChecksumEnabled_SHA512 AndAlso hashes.TryGetValue("SHA512", v) Then fr.File.SetXattr(ltfsindex.file.xattr.HashType.SHA512, v)
        If My.Settings.LTFSWriter_ChecksumEnabled_CRC32 AndAlso hashes.TryGetValue("CRC32", v) Then fr.File.SetXattr(ltfsindex.file.xattr.HashType.CRC32, v)
        If My.Settings.LTFSWriter_ChecksumEnabled_MD5 AndAlso hashes.TryGetValue("MD5", v) Then fr.File.SetXattr(ltfsindex.file.xattr.HashType.MD5, v)
        If My.Settings.LTFSWriter_ChecksumEnabled_BLAKE3 AndAlso hashes.TryGetValue("BLAKE3", v) Then fr.File.SetXattr(ltfsindex.file.xattr.HashType.BLAKE3, v)
        If My.Settings.LTFSWriter_ChecksumEnabled_XxHash3 AndAlso hashes.TryGetValue("XxHash3", v) Then fr.File.SetXattr(ltfsindex.file.xattr.HashType.XxHash3, v)
        If My.Settings.LTFSWriter_ChecksumEnabled_XxHash128 AndAlso hashes.TryGetValue("XxHash128", v) Then fr.File.SetXattr(ltfsindex.file.xattr.HashType.XxHash128, v)
    End Sub

    Private Function GetFastReaderDedupeHash(hashes As Dictionary(Of String, String)) As String
        If hashes Is Nothing Then Return ""
        Dim key As String
        Select Case My.Settings.LTFSWriter_DedupeAlgorithm
            Case ltfsindex.file.xattr.HashType.Available.SHA256
                key = "SHA256"
            Case ltfsindex.file.xattr.HashType.Available.SHA512
                key = "SHA512"
            Case ltfsindex.file.xattr.HashType.Available.CRC32
                key = "CRC32"
            Case ltfsindex.file.xattr.HashType.Available.MD5
                key = "MD5"
            Case ltfsindex.file.xattr.HashType.Available.BLAKE3
                key = "BLAKE3"
            Case ltfsindex.file.xattr.HashType.Available.XxHash3
                key = "XxHash3"
            Case ltfsindex.file.xattr.HashType.Available.XxHash128
                key = "XxHash128"
            Case Else
                key = "SHA1"
        End Select
        Dim value As String = Nothing
        If hashes.TryGetValue(key, value) Then Return value
        Return ""
    End Function

    Private Enum PlannedWriteKind
        ZeroLength
        Duplicate
        IndexPartitionMaterial
        DataPartitionMaterial
    End Enum

    Private Class PlannedWrite
        Public Kind As PlannedWriteKind
        Public ExistingReference As ltfsindex.file
        Public ExistingReferenceStore As LazySchemaStore
        Public ExistingReferenceOffset As Long = -1
        Public ExistingReferenceLength As Long = -1
        Public NewReferenceIndex As Integer = -1
        Public ExpectedDedupeHash As String = ""
        Public SourceLength As Long = -1
        Public SourceLastWriteUtcTicks As Long = -1
    End Class

    Private Class DedupeCanonical
        Public ExistingFile As ltfsindex.file
        Public ExistingStore As LazySchemaStore
        Public ExistingRecordOffset As Long = -1
        Public ExistingRecordLength As Long = -1
        Public NewFileIndex As Integer = -1
    End Class

    Private Function OpenPlannedExistingReference(plan As PlannedWrite) As ltfsindex.file
        If plan Is Nothing Then Return Nothing
        If plan.ExistingReferenceStore IsNot Nothing AndAlso
           plan.ExistingReferenceOffset >= 0 AndAlso plan.ExistingReferenceLength > 0 Then
            Dim result As New ltfsindex.file
            result.AttachLazyRecord(plan.ExistingReferenceStore,
                                    plan.ExistingReferenceOffset,
                                    plan.ExistingReferenceLength)
            Return result
        End If
        Return plan.ExistingReference
    End Function

    Private Function GetFileDedupeHash(file As ltfsindex.file) As String
        If file Is Nothing Then Return ""
        Select Case My.Settings.LTFSWriter_DedupeAlgorithm
            Case ltfsindex.file.xattr.HashType.Available.SHA256
                Return file.GetXAttr(ltfsindex.file.xattr.HashType.SHA256, True)
            Case ltfsindex.file.xattr.HashType.Available.SHA512
                Return file.GetXAttr(ltfsindex.file.xattr.HashType.SHA512, True)
            Case ltfsindex.file.xattr.HashType.Available.CRC32
                Return file.GetXAttr(ltfsindex.file.xattr.HashType.CRC32, True)
            Case ltfsindex.file.xattr.HashType.Available.MD5
                Return file.GetXAttr(ltfsindex.file.xattr.HashType.MD5, True)
            Case ltfsindex.file.xattr.HashType.Available.BLAKE3
                Return file.GetXAttr(ltfsindex.file.xattr.HashType.BLAKE3, True)
            Case ltfsindex.file.xattr.HashType.Available.XxHash3
                Return file.GetXAttr(ltfsindex.file.xattr.HashType.XxHash3, True)
            Case ltfsindex.file.xattr.HashType.Available.XxHash128
                Return file.GetXAttr(ltfsindex.file.xattr.HashType.XxHash128, True)
            Case Else
                Return file.GetXAttr(ltfsindex.file.xattr.HashType.SHA1, True)
        End Select
    End Function

    Private Sub SetFileDedupeHash(file As ltfsindex.file, value As String)
        If file Is Nothing OrElse String.IsNullOrEmpty(value) Then Return
        Select Case My.Settings.LTFSWriter_DedupeAlgorithm
            Case ltfsindex.file.xattr.HashType.Available.SHA256
                file.SetXattr(ltfsindex.file.xattr.HashType.SHA256, value)
            Case ltfsindex.file.xattr.HashType.Available.SHA512
                file.SetXattr(ltfsindex.file.xattr.HashType.SHA512, value)
            Case ltfsindex.file.xattr.HashType.Available.CRC32
                file.SetXattr(ltfsindex.file.xattr.HashType.CRC32, value)
            Case ltfsindex.file.xattr.HashType.Available.MD5
                file.SetXattr(ltfsindex.file.xattr.HashType.MD5, value)
            Case ltfsindex.file.xattr.HashType.Available.BLAKE3
                file.SetXattr(ltfsindex.file.xattr.HashType.BLAKE3, value)
            Case ltfsindex.file.xattr.HashType.Available.XxHash3
                file.SetXattr(ltfsindex.file.xattr.HashType.XxHash3, value)
            Case ltfsindex.file.xattr.HashType.Available.XxHash128
                file.SetXattr(ltfsindex.file.xattr.HashType.XxHash128, value)
            Case Else
                file.SetXattr(ltfsindex.file.xattr.HashType.SHA1, value)
        End Select
    End Sub

    Private Function ComputeDedupeHash(path As String) As String
        Select Case My.Settings.LTFSWriter_DedupeAlgorithm
            Case ltfsindex.file.xattr.HashType.Available.SHA256
                Return IOManager.GetSHA256(path)
            Case ltfsindex.file.xattr.HashType.Available.SHA512
                Return IOManager.GetSHA512(path)
            Case ltfsindex.file.xattr.HashType.Available.CRC32
                Return IOManager.GetCRC32(path)
            Case ltfsindex.file.xattr.HashType.Available.MD5
                Return IOManager.GetMD5(path)
            Case ltfsindex.file.xattr.HashType.Available.BLAKE3
                Return IOManager.GetBlake3(path)
            Case ltfsindex.file.xattr.HashType.Available.XxHash3
                Return IOManager.GetXxHash3(path)
            Case ltfsindex.file.xattr.HashType.Available.XxHash128
                Return IOManager.GetXxHash128(path)
            Case Else
                Return IOManager.SHA1(path)
        End Select
    End Function

    Private Function IsPlannedIndexPartition(fr As FileRecord) As Boolean
        Return fr IsNot Nothing AndAlso fr.File IsNot Nothing AndAlso IndexPartition <> DataPartition AndAlso
               fr.File.extentinfo IsNot Nothing AndAlso fr.File.extentinfo.Count > 0 AndAlso
               fr.File.extentinfo(0).partition = IndexPartition
    End Function

    Private Sub SnapshotPlannedSource(fr As FileRecord, plan As PlannedWrite)
        Dim info As New IO.FileInfo(fr.EnsureSourcePathResolved())
        plan.SourceLength = info.Length
        plan.SourceLastWriteUtcTicks = info.LastWriteTimeUtc.Ticks
    End Sub

    Private Sub ValidatePlannedSource(fr As FileRecord, plan As PlannedWrite)
        If plan Is Nothing OrElse plan.SourceLength < 0 Then Return
        Dim info As New IO.FileInfo(fr.EnsureSourcePathResolved())
        If info.Length <> plan.SourceLength OrElse info.LastWriteTimeUtc.Ticks <> plan.SourceLastWriteUtcTicks Then
            Throw New IO.IOException($"Source file changed after dedupe pre-scan: {fr.SourcePath}")
        End If
    End Sub

    Private Sub TraverseRestoreFiles(directory As ltfsindex.directory,
                                     outputDirectory As IO.DirectoryInfo,
                                     onFile As Action(Of FileRecord))
        If directory Is Nothing OrElse outputDirectory Is Nothing OrElse onFile Is Nothing Then Return
        For Each file As ltfsindex.file In directory.EnumerateLazyFiles()
            If StopFlag Then Exit Sub
            file.TempObj = New ltfsindex.file.refFile() With {.FileName = ""}
            onFile(New FileRecord With {
                       .File = file,
                       .SourcePath = IO.Path.Combine(outputDirectory.FullName, If(file.name, String.Empty))})
        Next
        For Each childDirectory As ltfsindex.directory In directory.EnumerateLazyDirectories()
            If StopFlag Then Exit Sub
            Dim childPath As String = IO.Path.Combine(outputDirectory.FullName, If(childDirectory.name, String.Empty))
            Dim restoreTimeStamp As Boolean = Not IO.Directory.Exists(childPath)
            If restoreTimeStamp Then IO.Directory.CreateDirectory(childPath)
            Dim childOutput As New IO.DirectoryInfo(childPath)
            TraverseRestoreFiles(childDirectory, childOutput, onFile)
            If restoreTimeStamp Then
                Try
                    childOutput.CreationTimeUtc = TapeUtils.ParseTimeStamp(childDirectory.creationtime)
                    childOutput.LastWriteTimeUtc = TapeUtils.ParseTimeStamp(childDirectory.modifytime)
                    childOutput.LastAccessTimeUtc = TapeUtils.ParseTimeStamp(childDirectory.accesstime)
                Catch
                End Try
            End If
        Next
    End Sub

    Private Sub CopyDedupeMetadata(target As ltfsindex.file, source As ltfsindex.file)
        target.SetXattr(ltfsindex.file.xattr.HashType.SHA1, source.GetXAttr(ltfsindex.file.xattr.HashType.SHA1, True), True)
        target.SetXattr(ltfsindex.file.xattr.HashType.SHA256, source.GetXAttr(ltfsindex.file.xattr.HashType.SHA256, True), True)
        target.SetXattr(ltfsindex.file.xattr.HashType.SHA512, source.GetXAttr(ltfsindex.file.xattr.HashType.SHA512, True), True)
        target.SetXattr(ltfsindex.file.xattr.HashType.CRC32, source.GetXAttr(ltfsindex.file.xattr.HashType.CRC32, True), True)
        target.SetXattr(ltfsindex.file.xattr.HashType.MD5, source.GetXAttr(ltfsindex.file.xattr.HashType.MD5, True), True)
        target.SetXattr(ltfsindex.file.xattr.HashType.BLAKE3, source.GetXAttr(ltfsindex.file.xattr.HashType.BLAKE3, True), True)
        target.SetXattr(ltfsindex.file.xattr.HashType.XxHash3, source.GetXAttr(ltfsindex.file.xattr.HashType.XxHash3, True), True)
        target.SetXattr(ltfsindex.file.xattr.HashType.XxHash128, source.GetXAttr(ltfsindex.file.xattr.HashType.XxHash128, True), True)
        target.RemoveXattr(ltfsindex.file.xattr.ApplicationSpecific.TarMetadata)
        Dim tarMetadataText As String = source.GetXAttr(ltfsindex.file.xattr.ApplicationSpecific.TarMetadata, True)
        Dim tarMetadata As TarMetadata = Nothing
        Dim virtualRoot As TarVirtualDirectory = Nothing
        If TarMetadataCodec.TryDecode(tarMetadataText, tarMetadata) AndAlso
            TarVirtualTreeBuilder.TryBuild(target, tarMetadata, virtualRoot) Then
            target.SetXattr(ltfsindex.file.xattr.ApplicationSpecific.TarMetadata, tarMetadataText)
        End If
    End Sub

    Private Function IsTarMetadataCandidate(fr As FileRecord) As Boolean
        If Not My.Settings.LTFSWriter_EnrichTarMetadata OrElse fr Is Nothing OrElse fr.File Is Nothing Then Return False
        If fr.File.length <= 0 OrElse fr.FileOffset <> 0 OrElse fr.SegmentLength <> fr.File.length Then Return False
        Return fr.File.name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)
    End Function

    Private Function IsTarFile(fr As FileRecord) As Boolean
        Return My.Settings.LTFSWriter_EnrichTarMetadata AndAlso fr IsNot Nothing AndAlso fr.File IsNot Nothing AndAlso
               fr.File.name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)
    End Function

    Private Sub CommitTarMetadata(fr As FileRecord, scanner As TarMetadataScanner)
        If fr Is Nothing OrElse fr.File Is Nothing OrElse scanner Is Nothing Then Return
        Dim metadata As TarMetadata = Nothing
        Try
            If scanner.TryComplete(fr.File.length, metadata) Then
                fr.File.SetXattr(ltfsindex.file.xattr.ApplicationSpecific.TarMetadata, TarMetadataCodec.Encode(metadata))
                Return
            End If
        Catch ex As Exception
            PrintMsg($"tar metadata encode skipped: {fr.SourcePath}; {ex.Message}", LogOnly:=True, ForceLog:=True)
        End Try
        fr.File.RemoveXattr(ltfsindex.file.xattr.ApplicationSpecific.TarMetadata)
        If scanner.Failed Then
            PrintMsg($"tar metadata skipped: {fr.SourcePath}; {scanner.ErrorMessage}", LogOnly:=True, ForceLog:=True)
        End If
    End Sub

    Private Iterator Function EnumerateSchemaFiles() As IEnumerable(Of ltfsindex.file)
        If schema Is Nothing Then Exit Function

        If schema._file IsNot Nothing Then
            For Each rootFile As ltfsindex.file In schema._file
                Yield rootFile
            Next
        End If

        Dim pending As New Stack(Of ltfsindex.directory)
        If schema._directory IsNot Nothing Then
            For i As Integer = schema._directory.Count - 1 To 0 Step -1
                pending.Push(schema._directory(i))
            Next
        End If

        While pending.Count > 0
            Dim current As ltfsindex.directory = pending.Pop()
            For Each currentFile As ltfsindex.file In current.EnumerateLazyFiles()
                Yield currentFile
            Next
            For Each child As ltfsindex.directory In current.EnumerateLazyDirectories()
                pending.Push(child)
            Next
        End While
    End Function

    Private Function BuildWritePlan(writeList As List(Of FileRecord),
                                    existingFiles As IEnumerable(Of ltfsindex.file),
                                    useFastReader As Boolean,
                                    fastProvider As RustFastReaderProvider) As List(Of PlannedWrite)
        Dim plans = Enumerable.Range(0, writeList.Count).Select(Function(index)
                                                                    Dim fr = writeList(index)
                                                                    Dim plan As New PlannedWrite()
                                                                    If fr Is Nothing OrElse fr.File Is Nothing OrElse fr.File.length = 0 Then
                                                                        plan.Kind = PlannedWriteKind.ZeroLength
                                                                    ElseIf IsPlannedIndexPartition(fr) Then
                                                                        plan.Kind = PlannedWriteKind.IndexPartitionMaterial
                                                                    Else
                                                                        plan.Kind = PlannedWriteKind.DataPartitionMaterial
                                                                    End If
                                                                    Return plan
                                                                End Function).ToList()
        If Not My.Settings.LTFSWriter_DeDupe Then Return plans

        Dim newSizeCounts As New Dictionary(Of Long, Integer)
        For Each fr In writeList
            If fr IsNot Nothing AndAlso fr.File IsNot Nothing AndAlso fr.File.length > 0 Then
                Dim count As Integer = 0
                newSizeCounts.TryGetValue(fr.File.length, count)
                newSizeCounts(fr.File.length) = count + 1
            End If
        Next

        Dim catalog As New Dictionary(Of Long, Dictionary(Of String, DedupeCanonical))
        For Each existing In existingFiles
            If existing Is Nothing OrElse existing.length <= 0 Then Continue For
            If Not newSizeCounts.ContainsKey(existing.length) Then Continue For
            Dim hash = GetFileDedupeHash(existing)
            If String.IsNullOrEmpty(hash) Then Continue For
            Dim byHash As Dictionary(Of String, DedupeCanonical) = Nothing
            If Not catalog.TryGetValue(existing.length, byHash) Then
                byHash = New Dictionary(Of String, DedupeCanonical)(StringComparer.OrdinalIgnoreCase)
                catalog(existing.length) = byHash
            End If
            If Not byHash.ContainsKey(hash) Then
                Dim canonical As New DedupeCanonical
                If existing.HasLazyRecord Then
                    canonical.ExistingStore = TryCast(existing.LazyStoreReference, LazySchemaStore)
                    canonical.ExistingRecordOffset = existing.LazyRecordOffset
                    canonical.ExistingRecordLength = existing.LazyRecordLength
                End If
                If canonical.ExistingStore Is Nothing OrElse canonical.ExistingRecordOffset < 0 OrElse canonical.ExistingRecordLength <= 0 Then
                    'Small/eager schemas do not have a backing record.  Keep
                    'the compatibility object only for that branch; large
                    'schemas stay represented by offset/length pairs.
                    canonical.ExistingFile = existing.GetCopy(existing.fileuid)
                End If
                byHash(hash) = canonical
            End If
        Next

        Dim candidates As New List(Of Long)
        For index = 0 To writeList.Count - 1
            Dim fr = writeList(index)
            If fr Is Nothing OrElse fr.File Is Nothing OrElse fr.File.length <= 0 Then Continue For
            Dim count As Integer = 0
            newSizeCounts.TryGetValue(fr.File.length, count)
            If count > 1 OrElse catalog.ContainsKey(fr.File.length) Then candidates.Add(index)
        Next

        For Each longIndex In candidates
            SnapshotPlannedSource(writeList(CInt(longIndex)), plans(CInt(longIndex)))
        Next

        Dim fastHashes As Dictionary(Of Long, Dictionary(Of String, String)) = Nothing
        If useFastReader AndAlso candidates.Count > 0 Then
            fastHashes = fastProvider.HashFiles(
                candidates,
                Timeout.Infinite,
                Sub(fileIndex)
                    Dim fr = writeList(CInt(fileIndex))
                    PrintMsg($"{My.Resources.ResText_CHashing} {My.Settings.LTFSWriter_DedupeAlgorithm}: {fr.File.name}  {My.Resources.ResText_Size} {IOManager.FormatSize(fr.File.length)}")
                End Sub)
        End If

        For Each longIndex In candidates
            Dim index = CInt(longIndex)
            Dim fr = writeList(index)
            If Not useFastReader Then
                PrintMsg($"{My.Resources.ResText_CHashing} {My.Settings.LTFSWriter_DedupeAlgorithm}: {fr.File.name}  {My.Resources.ResText_Size} {IOManager.FormatSize(fr.File.length)}")
            End If
            Dim hash = If(useFastReader, GetFastReaderDedupeHash(fastHashes(longIndex)), ComputeDedupeHash(fr.SourcePath))
            If String.IsNullOrEmpty(hash) Then Throw New IO.IOException($"Unable to calculate dedupe hash: {fr.SourcePath}")
            ValidatePlannedSource(fr, plans(index))
            plans(index).ExpectedDedupeHash = hash

            Dim byHash As Dictionary(Of String, DedupeCanonical) = Nothing
            If Not catalog.TryGetValue(fr.File.length, byHash) Then
                byHash = New Dictionary(Of String, DedupeCanonical)(StringComparer.OrdinalIgnoreCase)
                catalog(fr.File.length) = byHash
            End If
            Dim canonical As DedupeCanonical = Nothing
            If byHash.TryGetValue(hash, canonical) Then
                plans(index).Kind = PlannedWriteKind.Duplicate
                plans(index).ExistingReference = canonical.ExistingFile
                plans(index).ExistingReferenceStore = canonical.ExistingStore
                plans(index).ExistingReferenceOffset = canonical.ExistingRecordOffset
                plans(index).ExistingReferenceLength = canonical.ExistingRecordLength
                plans(index).NewReferenceIndex = canonical.NewFileIndex
            Else
                byHash(hash) = New DedupeCanonical With {.NewFileIndex = index}
            End If
        Next
        Return plans
    End Function

    Private Sub ReleaseManagedWriteRecord(writeList As List(Of FileRecord),
                                          index As Integer,
                                          dedupeReferenceUseCount As Dictionary(Of Integer, Integer))
        If writeList Is Nothing OrElse index < 0 OrElse index >= writeList.Count Then Return

        Dim remainingReferences As Integer = 0
        If dedupeReferenceUseCount IsNot Nothing AndAlso
           dedupeReferenceUseCount.TryGetValue(index, remainingReferences) AndAlso
           remainingReferences > 0 Then Return

        writeList(index) = Nothing
    End Sub

    Private Sub ReleaseManagedDedupeReference(writeList As List(Of FileRecord),
                                              index As Integer,
                                              dedupeReferenceUseCount As Dictionary(Of Integer, Integer))
        If dedupeReferenceUseCount Is Nothing OrElse index < 0 Then Return

        Dim remainingReferences As Integer = 0
        If Not dedupeReferenceUseCount.TryGetValue(index, remainingReferences) Then Return
        If remainingReferences <= 1 Then
            dedupeReferenceUseCount.Remove(index)
            ReleaseManagedWriteRecord(writeList, index, dedupeReferenceUseCount)
        Else
            dedupeReferenceUseCount(index) = remainingReferences - 1
        End If
    End Sub

    Private Sub WaitForFastReaderRefill(fastProvider As RustFastReaderProvider)
        If Not My.Settings.LTFSWriter_WaitOnBufferEmpty OrElse StopFlag Then
            PipePause = False
            Return
        End If

        Dim buffered = fastProvider.BufferedBytes
        PipeBufferLength = buffered
        Const lowWaterFraction As Double = 0.15
        Const fillFraction As Double = 0.75
        Dim lowWater = CLng(Math.Ceiling(fastProvider.BufferCapacityBytes * lowWaterFraction))
        If Not PipePause AndAlso buffered > lowWater Then Return

        PipePause = True
        Try
            Dim before = fastProvider.GetPerformanceStats()
            Dim timer = Stopwatch.StartNew()
            fastProvider.WaitForStreamFillFraction(fillFraction, Threading.CancellationToken.None)
            timer.Stop()
            LogFastReaderFillStats("refill", before, fastProvider.GetPerformanceStats(), timer.Elapsed)
        Finally
            PipeBufferLength = fastProvider.BufferedBytes
            PipePause = False
        End Try
    End Sub

    Private Sub WaitForInitialFastReaderFill(fastProvider As RustFastReaderProvider)
        If Not My.Settings.LTFSWriter_WaitOnBufferEmpty OrElse StopFlag Then Return
        PipePause = True
        Try
            Dim before = fastProvider.GetPerformanceStats()
            Dim timer = Stopwatch.StartNew()
            fastProvider.WaitForStreamFillFraction(0.75, Threading.CancellationToken.None)
            timer.Stop()
            LogFastReaderFillStats("initial fill", before, fastProvider.GetPerformanceStats(), timer.Elapsed)
        Finally
            PipeBufferLength = fastProvider.BufferedBytes
            PipePause = False
        End Try
    End Sub

    Private Sub LogFastReaderFillStats(operation As String,
                                       before As RustFastReaderProvider.PerformanceStats,
                                       after As RustFastReaderProvider.PerformanceStats,
                                       elapsed As TimeSpan)
        Dim readBytes = If(after.BytesRead >= before.BytesRead, after.BytesRead - before.BytesRead, 0UL)
        Dim publishedBytes = If(after.BytesPublished >= before.BytesPublished, after.BytesPublished - before.BytesPublished, 0UL)
        Dim seconds = Math.Max(0.000001, elapsed.TotalSeconds)
        Dim readMiBs = readBytes / 1048576.0 / seconds
        Dim publishMiBs = publishedBytes / 1048576.0 / seconds
        Dim ioWaitMs = If(after.ReadWaitNanoseconds >= before.ReadWaitNanoseconds,
                          (after.ReadWaitNanoseconds - before.ReadWaitNanoseconds) / 1000000.0,
                          0.0)
        Dim hashMs = If(after.HashNanoseconds >= before.HashNanoseconds,
                        (after.HashNanoseconds - before.HashNanoseconds) / 1000000.0,
                        0.0)
        Dim publishWaitMs = If(after.PublishWaitNanoseconds >= before.PublishWaitNanoseconds,
                               (after.PublishWaitNanoseconds - before.PublishWaitNanoseconds) / 1000000.0,
                               0.0)
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(LTFSWriter))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FastReader")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "BufferStats")
                        Log.Information("Fast reader buffer operation completed. Operation={Operation} ElapsedSeconds={ElapsedSeconds} BytesRead={BytesRead} BytesPublished={BytesPublished} ReadMiBs={ReadMiBs} PublishedMiBs={PublishedMiBs} IoWaitMilliseconds={IoWaitMilliseconds} HashMilliseconds={HashMilliseconds} PublishWaitMilliseconds={PublishWaitMilliseconds} BufferedBytes={BufferedBytes} OccupiedSlots={OccupiedSlots}.",
                                        operation,
                                        elapsed.TotalSeconds,
                                        readBytes,
                                        publishedBytes,
                                        readMiBs,
                                        publishMiBs,
                                        ioWaitMs,
                                        hashMs,
                                        publishWaitMs,
                                        after.BufferedBytes,
                                        after.OccupiedSlots)
                    End Using
                End Using
            End Using
        End Using
        PrintMsg($"fastreader {operation}: elapsed={elapsed.TotalSeconds:F3}s read={readMiBs:F1}MiB/s published={publishMiBs:F1}MiB/s io_wait={ioWaitMs:F1}ms hash={hashMs:F1}ms publish_wait={publishWaitMs:F1}ms buffered={IOManager.FormatSize(CLng(after.BufferedBytes))} slots={after.OccupiedSlots}",
                 LogOnly:=True,
                 ForceLog:=True)
    End Sub

    Private Function WriteFileFromFastReader(fastProvider As RustFastReaderProvider,
                                             fileIndex As Integer,
                                             fr As FileRecord,
                                             driveHandle As IntPtr,
                                             p As TapeUtils.PositionData,
                                             Optional expectedDedupeHash As String = "",
                                             Optional tarScanner As TarMetadataScanner = Nothing) As Boolean
        Dim remainingInFile As Long = fr.File.length
        Dim eofSeen As Boolean = False
        Dim writeCallCount As Long = 0
        Dim writeByteCount As Long = 0
        Dim writeElapsedSeconds As Double = 0
        Dim slowestWriteMiBs As Double = Double.PositiveInfinity
        Dim minimumBufferedBytes As Long = Long.MaxValue
        fastProvider.QueueFile(fileIndex)
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(LTFSWriter))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FastReader")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "FileSwitch")
                        Log.Information("Fast reader switched to file. FileIndex={FileIndex} FileName={FileName} SourcePath={SourcePath} Length={Length}.",
                                        fileIndex,
                                        fr.File.name,
                                        fr.SourcePath,
                                        fr.File.length)
                    End Using
                End Using
            End Using
        End Using
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(LTFSWriter))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FastReader")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "FileRead")
                        Log.Information("Fast reader starting file read. FileIndex={FileIndex} FileName={FileName} SourcePath={SourcePath} Length={Length}.",
                                        fileIndex,
                                        fr.File.name,
                                        fr.SourcePath,
                                        fr.File.length)
                    End Using
                End Using
            End Using
        End Using

        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(LTFSWriter))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FastReader")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "TapeWrite")
                        Log.Information("Fast reader tape write started. FileIndex={FileIndex} FileName={FileName} Length={Length} BufferedBytes={BufferedBytes} OccupiedSlots={OccupiedSlots} FastReaderRemainingBytes={FastReaderRemainingBytes}.",
                                        fileIndex,
                                        fr.File.name,
                                        fr.File.length,
                                        fastProvider.BufferedBytes,
                                        fastProvider.OccupiedSlotCount,
                                        fastProvider.RemainingBytes)
                    End Using
                End Using
            End Using
        End Using

        While Not StopFlag AndAlso remainingInFile > 0
            WaitForFastReaderRefill(fastProvider)

            Dim slot As RustFastReaderProvider.Slot = fastProvider.ReadSlot(fileIndex, Threading.CancellationToken.None)
            Dim expectedOffset = fr.File.length - remainingInFile
            If slot.FileIndex <> fileIndex OrElse slot.FileOffset <> expectedOffset Then
                fastProvider.AdvanceSlot(slot)
                Throw New IO.InvalidDataException($"Native fast reader slot order mismatch for {fr.SourcePath}: expected file={fileIndex} offset={expectedOffset}, actual file={slot.FileIndex} offset={slot.FileOffset}")
            End If
            If (slot.Flags And 1) <> 0 Then
                fastProvider.AdvanceSlot(slot)
                If slot.Flags <> 1 OrElse slot.Length <> 0 OrElse remainingInFile <> 0 Then
                    Throw New IO.EndOfStreamException($"Native fast reader published early EOF for {fr.SourcePath}: remaining={remainingInFile} length={slot.Length}")
                End If
                eofSeen = True
                Exit While
            End If
            If slot.Flags <> 0 Then
                fastProvider.AdvanceSlot(slot)
                Throw New IO.InvalidDataException($"Native fast reader published unsupported slot flags for {fr.SourcePath}: flags={slot.Flags}")
            End If
            If slot.Length <= 0 Then
                fastProvider.AdvanceSlot(slot)
                Throw New IO.InvalidDataException($"Native fast reader published an empty data slot for {fr.SourcePath} at offset {slot.FileOffset}")
            End If
            If slot.DataPtr = IntPtr.Zero Then
                fastProvider.AdvanceSlot(slot)
                Throw New IO.InvalidDataException($"Native fast reader published a null data pointer for {fr.SourcePath} at offset {slot.FileOffset}")
            End If
            If CLng(slot.Length) > remainingInFile Then
                fastProvider.AdvanceSlot(slot)
                Throw New IO.InvalidDataException($"Native fast reader slot exceeds the declared length for {fr.SourcePath}: remaining={remainingInFile} length={slot.Length}")
            End If
            PipeBufferLength = fastProvider.BufferedBytes
            minimumBufferedBytes = Math.Min(minimumBufferedBytes, PipeBufferLength)

            Dim bytesReaded As Integer = slot.Length
            Dim writeStarted = Stopwatch.GetTimestamp()
            Try
                If tarScanner IsNot Nothing Then tarScanner.ProcessUnmanaged(slot.DataPtr, slot.Length)
                CheckCount += 1
                If CheckCount >= CheckCycle Then CheckCount = 0
                If SpeedLimit > 0 AndAlso CheckCount = 0 Then
                    Dim ts As Double = (Now - SpeedLimitLastTriggerTime).TotalSeconds
                    While SpeedLimit > 0 AndAlso ts > 0 AndAlso ((plabel.blocksize * CheckCycle / 1048576) / ts) > SpeedLimit
                        Threading.Thread.Sleep(0)
                        ts = (Now - SpeedLimitLastTriggerTime).TotalSeconds
                    End While
                    SpeedLimitLastTriggerTime = Now
                End If

                Dim succ As Boolean = False
                While Not succ
                    Dim sense As Byte()
                    Dim writeCallNumber = writeCallCount + 1
                    Dim tapeWriteTimer = Stopwatch.StartNew()
                    Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(LTFSWriter))
                        Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FastReader")
                            Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                                Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "TapeWriteCall")
                                    Log.Information("Fast reader tape write call started. FileIndex={FileIndex} FileName={FileName} WriteCall={WriteCall} FileOffset={FileOffset} SlotLength={SlotLength} FileRemainingBytes={FileRemainingBytes} BufferedBytes={BufferedBytes} OccupiedSlots={OccupiedSlots} FastReaderRemainingBytes={FastReaderRemainingBytes}.",
                                                    fileIndex,
                                                    fr.File.name,
                                                    writeCallNumber,
                                                    slot.FileOffset,
                                                    slot.Length,
                                                    remainingInFile,
                                                    fastProvider.BufferedBytes,
                                                    fastProvider.OccupiedSlotCount,
                                                    fastProvider.RemainingBytes)
                                End Using
                            End Using
                        End Using
                    End Using
                    Try
                        writeCallCount = writeCallNumber
                        sense = TapeUtils.Write(driveHandle, slot.DataPtr, CUInt(slot.Length), True)
                        tapeWriteTimer.Stop()
                        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(LTFSWriter))
                            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FastReader")
                                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "TapeWriteCall")
                                        Log.Information("Fast reader tape write call completed. FileIndex={FileIndex} FileName={FileName} WriteCall={WriteCall} FileOffset={FileOffset} SlotLength={SlotLength} ElapsedMilliseconds={ElapsedMilliseconds} SenseByte2={SenseByte2} BufferedBytes={BufferedBytes} OccupiedSlots={OccupiedSlots} FastReaderRemainingBytes={FastReaderRemainingBytes}.",
                                                        fileIndex,
                                                        fr.File.name,
                                                        writeCallNumber,
                                                        slot.FileOffset,
                                                        slot.Length,
                                                        tapeWriteTimer.Elapsed.TotalMilliseconds,
                                                        If(sense Is Nothing OrElse sense.Length <= 2, -1, CInt(sense(2))),
                                                        fastProvider.BufferedBytes,
                                                        fastProvider.OccupiedSlotCount,
                                                        fastProvider.RemainingBytes)
                                    End Using
                                End Using
                            End Using
                        End Using
                        SyncLock p
                            p.BlockNumber = CULng(p.BlockNumber + 1)
                        End SyncLock
                    Catch ex As Exception
                        tapeWriteTimer.Stop()
                        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(LTFSWriter))
                            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FastReader")
                                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Error")
                                        Log.Error(ex, "Fast reader tape write call failed. FileIndex={FileIndex} FileName={FileName} WriteCall={WriteCall} FileOffset={FileOffset} SlotLength={SlotLength} ElapsedMilliseconds={ElapsedMilliseconds} FileRemainingBytes={FileRemainingBytes} BufferedBytes={BufferedBytes} OccupiedSlots={OccupiedSlots} FastReaderRemainingBytes={FastReaderRemainingBytes}.",
                                                  fileIndex,
                                                  fr.File.name,
                                                  writeCallNumber,
                                                  slot.FileOffset,
                                                  slot.Length,
                                                  tapeWriteTimer.Elapsed.TotalMilliseconds,
                                                  remainingInFile,
                                                  fastProvider.BufferedBytes,
                                                  fastProvider.OccupiedSlotCount,
                                                  fastProvider.RemainingBytes)
                                    End Using
                                End Using
                            End Using
                        End Using
                        Dim dResult As DialogResult
                        Invoke(Sub() dResult = MessageBox.Show(New Form With {.TopMost = True}, $"{My.Resources.ResText_WErrSCSI}{vbCrLf}{ex.ToString}", My.Resources.ResText_Warning, MessageBoxButtons.AbortRetryIgnore))
                        Select Case dResult
                            Case DialogResult.Abort
                                StopFlag = True
                                Throw
                            Case DialogResult.Retry
                                succ = False
                            Case DialogResult.Ignore
                                succ = True
                                Exit While
                        End Select
                        p = New TapeUtils.PositionData(driveHandle)
                        Continue While
                    End Try
                    If (((sense(2) >> 6) And &H1) = 1) Then
                        If ((sense(2) And &HF) = 13) AndAlso (Not My.Settings.LTFSWriter_IgnoreVolumeOverflow) Then
                            PrintMsg(My.Resources.ResText_VOF)
                            Invoke(Sub() MessageBox.Show(New Form With {.TopMost = True}, My.Resources.ResText_VOF))
                            StopFlag = True
                            Exit While
                        Else
                            PrintMsg(If(((sense(2) And &HF) = 13), My.Resources.ResText_VOF, My.Resources.ResText_EWEOM), True, DeDupe:=True)
                            succ = True
                            Exit While
                        End If
                    ElseIf (sense(2) And &HF) <> 0 Then
                        Dim dResult As DialogResult
                        Invoke(Sub() dResult = MessageBox.Show(New Form With {.TopMost = True}, $"{My.Resources.ResText_WErr}{vbCrLf}{TapeUtils.ParseSenseData(sense)}{vbCrLf}{vbCrLf}sense{vbCrLf}{TapeUtils.Byte2Hex(sense, True)}", My.Resources.ResText_Warning, MessageBoxButtons.AbortRetryIgnore))
                        Select Case dResult
                            Case DialogResult.Abort
                                StopFlag = True
                                Throw New Exception(TapeUtils.ParseSenseData(sense))
                            Case DialogResult.Retry
                                succ = False
                            Case DialogResult.Ignore
                                succ = True
                                Exit While
                        End Select
                        p = New TapeUtils.PositionData(driveHandle)
                    Else
                        succ = True
                        Exit While
                    End If
                End While

                If Not StopFlag Then
                    fr.File.WrittenBytes += slot.Length
                    TotalBytesProcessed += slot.Length
                    CurrentBytesProcessed += slot.Length
                    TotalBytesUnindexed += slot.Length
                End If
            Finally
                fastProvider.AdvanceSlot(slot)
            End Try
            Dim slotWriteSeconds = (Stopwatch.GetTimestamp() - writeStarted) / CDbl(Stopwatch.Frequency)
            writeByteCount += bytesReaded
            writeElapsedSeconds += slotWriteSeconds
            If slotWriteSeconds > 0 Then
                slowestWriteMiBs = Math.Min(slowestWriteMiBs, bytesReaded / 1048576.0 / slotWriteSeconds)
            End If
            remainingInFile -= bytesReaded
        End While

        If writeCallCount > 0 Then
            Dim averageWriteMiBs = If(writeElapsedSeconds > 0,
                                      writeByteCount / 1048576.0 / writeElapsedSeconds,
                                      0.0)
            Dim minimumBuffer = If(minimumBufferedBytes = Long.MaxValue, 0, minimumBufferedBytes)
            Dim slowestWrite = If(Double.IsPositiveInfinity(slowestWriteMiBs), 0.0, slowestWriteMiBs)
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(LTFSWriter))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FastReader")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                        Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "TapeWrite")
                            Log.Information("Fast reader tape write completed. FileIndex={FileIndex} FileName={FileName} Calls={Calls} Bytes={Bytes} ElapsedSeconds={ElapsedSeconds} AverageMiBs={AverageMiBs} SlowestMiBs={SlowestMiBs} MinimumBufferedBytes={MinimumBufferedBytes} FileRemainingBytes={FileRemainingBytes} FastReaderRemainingBytes={FastReaderRemainingBytes}.",
                                            fileIndex,
                                            fr.File.name,
                                            writeCallCount,
                                            writeByteCount,
                                            writeElapsedSeconds,
                                            averageWriteMiBs,
                                            slowestWrite,
                                            minimumBuffer,
                                            remainingInFile,
                                            fastProvider.RemainingBytes)
                        End Using
                    End Using
                End Using
            End Using
            PrintMsg($"fastreader tape write: calls={writeCallCount} bytes={IOManager.FormatSize(writeByteCount)} elapsed={writeElapsedSeconds:F3}s average={averageWriteMiBs:F1}MiB/s slowest={slowestWrite:F1}MiB/s min_buffered={IOManager.FormatSize(minimumBuffer)}",
                     LogOnly:=True,
                     ForceLog:=True)
        Else
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(LTFSWriter))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FastReader")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                        Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Error")
                            Log.Warning("Fast reader tape write loop ended without a tape write call. FileIndex={FileIndex} FileName={FileName} Length={Length} FileRemainingBytes={FileRemainingBytes} StopFlag={StopFlag}.",
                                        fileIndex,
                                        fr.File.name,
                                        fr.File.length,
                                        remainingInFile,
                                        StopFlag)
                        End Using
                    End Using
                End Using
            End Using
        End If

        If StopFlag Then Return False
        If remainingInFile <> 0 Then
            Throw New IO.EndOfStreamException($"Native fast reader ended before all bytes were written for {fr.SourcePath}: remaining={remainingInFile}")
        End If
        If Not eofSeen Then
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(LTFSWriter))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FastReader")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                        Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Eof")
                            Log.Information("Fast reader waiting for file EOF marker. FileIndex={FileIndex} FileName={FileName} RemainingBytes={RemainingBytes}.",
                                            fileIndex,
                                            fr.File.name,
                                            remainingInFile)
                        End Using
                    End Using
                End Using
            End Using
            Dim eofSlot = fastProvider.ReadSlot(fileIndex, Threading.CancellationToken.None)
            Dim validEof = eofSlot.FileIndex = fileIndex AndAlso
                           eofSlot.FileOffset = fr.File.length AndAlso
                           eofSlot.Length = 0 AndAlso
                           eofSlot.Flags = 1
            fastProvider.AdvanceSlot(eofSlot)
            If Not validEof Then
                Throw New IO.InvalidDataException($"Native fast reader did not publish the expected EOF for {fr.SourcePath}: file={eofSlot.FileIndex} offset={eofSlot.FileOffset} length={eofSlot.Length} flags={eofSlot.Flags}")
            End If
            eofSeen = True
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(LTFSWriter))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FastReader")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                        Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Eof")
                            Log.Information("Fast reader file EOF marker received. FileIndex={FileIndex} FileName={FileName} FileOffset={FileOffset}.",
                                            fileIndex,
                                            fr.File.name,
                                            eofSlot.FileOffset)
                        End Using
                    End Using
                End Using
            End Using
        End If
        If Not StopFlag Then
            Dim hashes = fastProvider.GetCompletedFileHashes(fileIndex)
            If Not String.IsNullOrEmpty(expectedDedupeHash) Then
                Dim actualDedupeHash = GetFastReaderDedupeHash(hashes)
                If Not actualDedupeHash.Equals(expectedDedupeHash, StringComparison.OrdinalIgnoreCase) Then
                    Throw New IO.IOException($"Source file changed after dedupe pre-scan: {fr.SourcePath}")
                End If
                SetFileDedupeHash(fr.File, actualDedupeHash)
            End If
            If HashOnWrite Then ApplyFastReaderHashes(fr, hashes)
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(LTFSWriter))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FastReader")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                        Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "FileCompleted")
                            Log.Information("Fast reader completed file processing. FileIndex={FileIndex} FileName={FileName} Length={Length} HashCount={HashCount} BufferedBytes={BufferedBytes} OccupiedSlots={OccupiedSlots} FastReaderRemainingBytes={FastReaderRemainingBytes}.",
                                            fileIndex,
                                            fr.File.name,
                                            fr.File.length,
                                            hashes.Count,
                                            fastProvider.BufferedBytes,
                                            fastProvider.OccupiedSlotCount,
                                            fastProvider.RemainingBytes)
                        End Using
                    End Using
                End Using
            End Using
        End If
        TotalFilesProcessed += 1
        CurrentFilesProcessed += 1
        Return Not StopFlag
    End Function


    Private Sub 写入数据ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 写入数据ToolStripMenuItem.Click
        Dim th As New Threading.Thread(
            Sub()
                Dim OnWriteFinishMessage As String = ""
                Dim provider As FileDataProvider = Nothing
                Dim fastProvider As RustFastReaderProvider = Nothing
                Dim WriteList As New List(Of FileRecord)
                Dim wBufferPtr As IntPtr = IntPtr.Zero
                Dim hashTasks As New List(Of Task)
                Dim writeTasks As New List(Of Task)
                Dim writeWaitHandles As New List(Of AutoResetEvent)
                Dim LTE As AutoResetEvent = Nothing
                Dim locateTask As Task = Nothing
                Dim tapeUnitReserved As Boolean = False
                Dim mediumRemovalPrevented As Boolean = False
                Dim ufReadCountIncremented As Boolean = False
                Try
                    ClearWriteCompletionState()
                    SetStatusLight(LWStatus.Busy)
                    StartTime = Now
                    PipePause = False
                    PrintMsg("", True)
                    PrintMsg($"Position = {GetPos.ToString()}", LogOnly:=True)
                    PrintMsg(My.Resources.ResText_PrepW)
                    TapeUtils.ReserveUnit(driveHandle)
                    tapeUnitReserved = True
                    TapeUtils.PreventMediaRemoval(driveHandle)
                    mediumRemovalPrevented = True
                    Dim locateResult As Boolean = False
                    LTE = New AutoResetEvent(False)
                    locateTask = Task.Run(Sub()
                                              Try
                                                  locateResult = LocateToWritePosition()
                                              Finally
                                                  Try
                                                      LTE.Set()
                                                  Catch
                                                  End Try
                                              End Try
                                          End Sub)
                    IsWriting = True
                    My.Settings.LTFSWriter_PreLoadBytes = My.Settings.LTFSWriter_PreLoadBytes
                    RingBufferEnabled = My.Settings.LTFSWriter_RingBufferEnabled
                    UFReadCount.Inc()
                    ufReadCountIncremented = True
                    Dim useFastReader As Boolean = False
                    If UnwrittenCount > 0 Then
                        CurrentFilesProcessed = 0
                        CurrentBytesProcessed = 0
                        UnwrittenSizeOverrideValue = 0
                        UnwrittenCountOverrideValue = 0
                        SyncLock _pendingQueueLock
                            If UnwrittenFiles IsNot Nothing Then WriteList.AddRange(UnwrittenFiles)
                        End SyncLock
                        UnwrittenCountOverrideValue = UnwrittenCount
                        UnwrittenSizeOverrideValue = UnwrittenSize
                        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency
                        If My.Settings.LTFSWriter_ExternalReaderEnabled Then
                            Try
                                fastProvider = New RustFastReaderProvider(WriteList, CInt(plabel.blocksize), My.Settings.LTFSWriter_PreLoadBytes)
                                fastProvider.Start()
                                useFastReader = True
                                _activeFastReaderProvider = fastProvider
                                Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(LTFSWriter))
                                    Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FastReader")
                                        Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                                            Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Lifecycle")
                                                Log.Information("Fast reader enabled for writer. DriverType={DriverType} FileCount={FileCount} CapacityBytes={CapacityBytes}.",
                                                                TapeUtils.DriverTypeSetting,
                                                                WriteList.Count,
                                                                My.Settings.LTFSWriter_PreLoadBytes)
                                            End Using
                                        End Using
                                    End Using
                                End Using
                                PrintMsg($"fastreader enabled: driver={TapeUtils.DriverTypeSetting} buffer={IOManager.FormatSize(My.Settings.LTFSWriter_PreLoadBytes)}", LogOnly:=True, ForceLog:=True)
                                For Each fr As FileRecord In WriteList
                                    If fr IsNot Nothing Then fr.IsOpened = True
                                Next
                            Catch ex As Exception
                                Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(LTFSWriter))
                                    Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FastReader")
                                        Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                                            Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Error")
                                                Log.Error(ex, "Fast reader could not be initialized for the writer. Managed fallback is disabled.")
                                            End Using
                                        End Using
                                    End Using
                                End Using
                                PrintMsg($"native fastreader unavailable: {ex.Message}", LogOnly:=True, ForceLog:=True)
                                If ReferenceEquals(_activeFastReaderProvider, fastProvider) Then _activeFastReaderProvider = Nothing
                                If fastProvider IsNot Nothing Then fastProvider.Dispose()
                                fastProvider = Nothing
                                Throw New IO.IOException("External Reader is enabled, but the native fastreader could not be initialized. Managed fallback is disabled.", ex)
                            End Try
                        End If
                        If Not useFastReader Then
                            provider = New FileDataProvider(WriteList,
                                                                 smallThresholdBytes:=16 * 1024,
                                                                 smallCacheCapacity:=My.Settings.LTFSWriter_PreLoadFileCount,
                                                                 pipeBufferBytes:=My.Settings.LTFSWriter_PreLoadBytes)
                            provider.Start()
                        End If

                    End If

                    Dim lcounter As Integer = 0
                    While Not (locateTask.IsCompleted OrElse locateTask.IsCanceled OrElse locateTask.IsFaulted)
                        LTE.WaitOne(10)
                        lcounter += 1
                        If lcounter >= 100 Then
                            lcounter = 0
                            If useFastReader AndAlso fastProvider IsNot Nothing Then
                                PipeBufferLength = fastProvider.BufferedBytes
                            ElseIf provider IsNot Nothing Then
                                PipeBufferLength = If(RingBufferEnabled, PipeGetLength(provider.RingBuffer), PipeGetLength(provider.Reader))
                            End If
                        End If
                    End While
                    If locateTask.IsFaulted Then ObserveTask(locateTask)
                    If Not locateResult Then
                        Exit Sub
                    End If
                    Invoke(Sub() 更新数据区索引ToolStripMenuItem.Enabled = True)
                    If My.Settings.LTFSWriter_PowerPolicyOnWriteBegin <> Guid.Empty Then
                        Process.Start(New ProcessStartInfo With {.FileName = "powercfg",
                                      .Arguments = $"/s {My.Settings.LTFSWriter_PowerPolicyOnWriteBegin.ToString()}",
                                      .WindowStyle = ProcessWindowStyle.Hidden})
                    End If
                    If WriteList.Count > 0 Then
                        wBufferPtr = Marshal.AllocHGlobal(CInt(plabel.blocksize))

                        'Dim PNum As Integer = My.Settings.LTFSWriter_PreLoadFileCount
                        'If PNum > 0 Then
                        '    For j As Integer = 0 To PNum
                        '        If j < WriteList.Count Then WriteList(j).BeginOpen(BlockSize:=plabel.blocksize)
                        '    Next
                        'End If
                        Dim ExitForFlag As Boolean = False
                        Dim completedWriteRecords As New List(Of FileRecord)
                        'DeDupe

                        Dim existingFiles As IEnumerable(Of ltfsindex.file) = Enumerable.Empty(Of ltfsindex.file)()
                        If My.Settings.LTFSWriter_DeDupe Then existingFiles = EnumerateSchemaFiles()
                        Dim writePlan = BuildWritePlan(WriteList, existingFiles, useFastReader, fastProvider)
                        Dim dedupeReferenceUseCount As New Dictionary(Of Integer, Integer)
                        If Not useFastReader Then
                            For planIndex As Integer = 0 To writePlan.Count - 1
                                Dim referenceIndex As Integer = writePlan(planIndex).NewReferenceIndex
                                If referenceIndex >= 0 Then
                                    Dim referenceCount As Integer = 0
                                    dedupeReferenceUseCount.TryGetValue(referenceIndex, referenceCount)
                                    dedupeReferenceUseCount(referenceIndex) = referenceCount + 1
                                End If
                            Next
                        End If
                        If useFastReader Then
                            Dim orderedDataFiles = Enumerable.Range(0, writePlan.Count).
                                Where(Function(index) writePlan(index).Kind = PlannedWriteKind.DataPartitionMaterial).
                                Select(Function(index) CLng(index)).ToList()
                            fastProvider.ConfigureOrderedFiles(orderedDataFiles)
                            WaitForInitialFastReaderFill(fastProvider)
                        End If
                        IndexLastUpdateTime = Now
                        Dim p As New TapeUtils.PositionData(driveHandle)

                        Dim lastpos As New TapeUtils.PositionData(driveHandle)
                        TapeUtils.SetBlockSize(driveHandle, plabel.blocksize)
                        For i As Integer = 0 To WriteList.Count - 1
                            'If i < WriteList.Count - 1 Then
                            '    Dim CFNum As Integer = i
                            '    Dim dl As New LTFSWriter.FileRecord.PreReadFinishedEventHandler(
                            '        Sub()
                            '            WriteList(CFNum + 1).BeginOpen()
                            '        End Sub)
                            '    AddHandler WriteList(CFNum).PreReadFinished, dl
                            'End If
                            If ExitForFlag Then Exit For
                            'PNum = My.Settings.LTFSWriter_PreLoadFileCount
                            'If PNum > 0 AndAlso i + PNum < WriteList.Count Then
                            '    WriteList(i + PNum).BeginOpen(BlockSize:=plabel.blocksize)
                            'End If
                            Dim fr As FileRecord = WriteList(i)
                            Try
                                If fr Is Nothing OrElse fr.File Is Nothing Then
                                    Throw New IO.InvalidDataException($"Write list contains an invalid FileRecord at index {i}.")
                                End If
                                fr.File.fileuid = schema.highestfileuid + 1
                                schema.highestfileuid += 1
                                Dim currentPlan = writePlan(i)
                                ValidatePlannedSource(fr, currentPlan)
                                Dim IsIndexPartition As Boolean = currentPlan.Kind = PlannedWriteKind.IndexPartitionMaterial
                                Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(LTFSWriter))
                                    Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Writer")
                                        Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                                            Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "FileSwitch")
                                                Log.Information("Writer switching to file. FileIndex={FileIndex} FileName={FileName} SourcePath={SourcePath} Length={Length} PlanKind={PlanKind} IndexPartition={IndexPartition} FastReader={FastReader}.",
                                                                i,
                                                                fr.File.name,
                                                                fr.SourcePath,
                                                                fr.File.length,
                                                                currentPlan.Kind.ToString(),
                                                                IsIndexPartition,
                                                                useFastReader)
                                            End Using
                                        End Using
                                    End Using
                                End Using
                                Dim tarScanner As TarMetadataScanner = Nothing
                                If currentPlan.Kind <> PlannedWriteKind.Duplicate AndAlso IsTarFile(fr) Then
                                    fr.File.RemoveXattr(ltfsindex.file.xattr.ApplicationSpecific.TarMetadata)
                                    If IsTarMetadataCandidate(fr) Then tarScanner = New TarMetadataScanner()
                                End If
                                If fr.File.length > 0 Then
                                    'p = New TapeUtils.PositionData(TapeDrive)
                                    'If p.EOP Then PrintMsg(My.Resources.ResText_EWEOM.Text, True)
                                    Dim dupe As Boolean = currentPlan.Kind = PlannedWriteKind.Duplicate
                                    If dupe Then
                                        'A lazy existing-file match is kept as a
                                        'record location in the plan.  Open just
                                        'this reference now so dedupe metadata
                                        'and extents do not retain the entire
                                        'schema in memory.
                                        Dim dupeFile As ltfsindex.file = OpenPlannedExistingReference(currentPlan)
                                        If dupeFile Is Nothing AndAlso currentPlan.NewReferenceIndex >= 0 Then
                                            Dim referenceRecord As FileRecord = Nothing
                                            If currentPlan.NewReferenceIndex < WriteList.Count Then
                                                referenceRecord = WriteList(currentPlan.NewReferenceIndex)
                                            End If
                                            If referenceRecord IsNot Nothing Then dupeFile = referenceRecord.File
                                            If dupeFile Is Nothing OrElse dupeFile.extentinfo Is Nothing OrElse dupeFile.extentinfo.Count = 0 Then
                                                Throw New IO.IOException($"Dedupe reference was not written successfully: {fr.SourcePath}")
                                            End If
                                        End If
                                        If dupeFile Is Nothing Then Throw New IO.IOException($"Missing dedupe reference: {fr.SourcePath}")
                                        If dupeFile.extentinfo Is Nothing OrElse dupeFile.extentinfo.Count = 0 Then
                                            Throw New IO.IOException($"Dedupe reference has no extent: {fr.SourcePath}")
                                        End If
                                        CopyDedupeMetadata(fr.File, dupeFile)
                                        SetFileDedupeHash(fr.File, currentPlan.ExpectedDedupeHash)
                                        fr.File.extentinfo.Clear()
                                        For Each ext As ltfsindex.file.extent In dupeFile.extentinfo
                                            fr.File.extentinfo.Add(ext)
                                        Next
                                        fr.File.MarkLazyRecordDirty()
                                        If currentPlan.NewReferenceIndex >= 0 Then
                                            ReleaseManagedDedupeReference(WriteList, currentPlan.NewReferenceIndex, dedupeReferenceUseCount)
                                        End If
                                        If fr.fs IsNot Nothing Then fr.Close()
                                        PrintMsg($"{My.Resources.ResText_Skip} {fr.File.name}  {My.Resources.ResText_Size} {IOManager.FormatSize(fr.File.length)}", False,
                                                 $"{My.Resources.ResText_Skip}: {fr.SourcePath}{vbCrLf}{My.Resources.ResText_Size}: {IOManager.FormatSize(fr.File.length)}{vbCrLf _
                                                 }{My.Resources.ResText_WrittenTotal}: {IOManager.FormatSize(TotalBytesProcessed) _
                                                 } {My.Resources.ResText_Remaining}: {IOManager.FormatSize(CLng(Math.Max(0, UnwrittenSize - CurrentBytesProcessed))) _
                                                 } -> {IOManager.FormatSize(CLng(Math.Max(0, UnwrittenSize - CurrentBytesProcessed - fr.File.length)))}")
                                        TotalBytesProcessed += fr.File.length
                                        CurrentBytesProcessed += fr.File.length
                                        TotalFilesProcessed += 1
                                        CurrentFilesProcessed += 1
                                        If useFastReader Then
                                            'HASH pre-scan does not enqueue shared-memory data, so there is nothing to drain.
                                        ElseIf RingBufferEnabled Then
                                            PipeDrain(provider.RingBuffer, fr.File.length)
                                        Else
                                            PipeDrain(provider.Reader, fr.File.length)
                                        End If
                                    Else
                                        Dim fileextent As New ltfsindex.file.extent With
                                            {.partition = CType(DataPartition, ltfsindex.PartitionLabel),
                                            .startblock = CLng(p.BlockNumber),
                                            .bytecount = If(Not useFastReader, fr.SegmentLength, fr.File.length),
                                            .byteoffset = 0,
                                            .fileoffset = If(Not useFastReader, fr.FileOffset, 0)}
                                        If IsIndexPartition Then
                                            fr.File.extentinfo.Clear()
                                            Dim tarDataRead As Action(Of Byte(), Integer, Integer) = Nothing
                                            If tarScanner IsNot Nothing Then
                                                tarDataRead = Sub(data As Byte(), dataOffset As Integer, dataCount As Integer)
                                                                  tarScanner.Process(data, dataOffset, dataCount)
                                                              End Sub
                                            End If
                                            Select Case fr.Open()
                                                Case DialogResult.Ignore
                                                    PrintMsg($"Cannot open file {fr.SourcePath}", LogOnly:=True, ForceLog:=True)
                                                    StopFlag = True
                                                    Throw New IO.IOException($"Cannot open file {fr.SourcePath}")
                                                Case DialogResult.Abort
                                                    StopFlag = True
                                                    Throw New Exception(My.Resources.ResText_FileOpenError)
                                            End Select
                                            fileextent.partition = CType(IndexPartition, ltfsindex.PartitionLabel)
                                            Dim IsFirstFile As Boolean = (i <= 0 OrElse p.PartitionNumber = DataPartition)
                                            Dim nextRecord As FileRecord = Nothing
                                            If i < WriteList.Count - 1 Then nextRecord = WriteList(i + 1)
                                            Dim IsLastFile As Boolean = (nextRecord Is Nothing OrElse nextRecord.File Is Nothing OrElse
                                                                         nextRecord.File.extentinfo Is Nothing OrElse
                                                                         nextRecord.File.extentinfo.Count = 0 OrElse
                                                                         nextRecord.File.extentinfo(0).partition = DataPartition)
                                            fileextent.startblock = DumpDataToIndexPartition(fr.fs, False, IsFirstFile, IsLastFile, tarDataRead)
                                            fr.Close()
                                            If IsLastFile Then TapeUtils.Locate(driveHandle, lastpos.BlockNumber, lastpos.PartitionNumber)
                                        Else
                                            If p.PartitionNumber <> lastpos.PartitionNumber OrElse p.BlockNumber <> lastpos.BlockNumber Then
                                                TapeUtils.Locate(driveHandle, lastpos.BlockNumber, lastpos.PartitionNumber)
                                                p = New TapeUtils.PositionData(driveHandle)
                                                fileextent.startblock = CLng(p.BlockNumber)
                                            End If
                                        End If
                                        fr.File.extentinfo.Add(fileextent)
                                        fr.File.MarkLazyRecordDirty()
                                        PrintMsg($"{My.Resources.ResText_Writing} {fr.File.name}  {My.Resources.ResText_Size} {IOManager.FormatSize(fr.File.length)}", False,
                                             $"{My.Resources.ResText_Writing}: {fr.SourcePath}{vbCrLf}{My.Resources.ResText_Size}: {IOManager.FormatSize(fr.File.length)}{vbCrLf _
                                             }{My.Resources.ResText_WrittenTotal}: {IOManager.FormatSize(TotalBytesProcessed) _
                                             } {My.Resources.ResText_Remaining}: {IOManager.FormatSize(CLng(Math.Max(0, UnwrittenSize - CurrentBytesProcessed))) _
                                             } -> {IOManager.FormatSize(CLng(Math.Max(0, UnwrittenSize - CurrentBytesProcessed - fr.File.length)))}")
                                        'write to tape
                                        Dim LastWriteTask As Task = Nothing
                                        If fr.File Is Nothing Then Continue For
                                        While (Not useFastReader) AndAlso Not fr.IsOpened AndAlso Not StopFlag
                                            If fr.File Is Nothing Then Continue For
                                            If provider IsNot Nothing AndAlso provider.ProducerCompleted Then
                                                Throw New IO.EndOfStreamException($"Managed file provider completed before opening {fr.SourcePath}.")
                                            End If
                                            Threading.Thread.Sleep(1)
                                        End While
                                        If StopFlag Then Exit For
                                        If useFastReader AndAlso IsIndexPartition Then
                                            fr.File.WrittenBytes += fr.File.length
                                            TotalBytesProcessed += fr.File.length
                                            CurrentBytesProcessed += fr.File.length
                                            TotalFilesProcessed += 1
                                            CurrentFilesProcessed += 1
                                            TotalBytesUnindexed += fr.File.length
                                        ElseIf useFastReader AndAlso Not IsIndexPartition Then
                                            If Not WriteFileFromFastReader(fastProvider, i, fr, driveHandle, p, currentPlan.ExpectedDedupeHash, tarScanner) Then Exit For
                                        ElseIf (fr.File.length <= plabel.blocksize) AndAlso (fr.FileOffset = 0) AndAlso (fr.File.length = fr.SegmentLength) Then
                                            Dim succ As Boolean = False
                                            Dim FileData(CInt(fr.File.length - 1)) As Byte
                                            While True
                                                Try
                                                    'FileData = fr.ReadAllBytes()
                                                    Dim PipeReader As System.IO.Pipelines.PipeReader
                                                    Dim RingBufferReader As SpscRingBuffer
                                                    If RingBufferEnabled Then
                                                        RingBufferReader = provider.RingBuffer
                                                        PipeReadExactly(RingBufferReader, FileData, CInt(fr.File.length))
                                                    Else
                                                        PipeReader = provider.Reader
                                                        PipeReadExactly(PipeReader, FileData, CInt(fr.File.length))
                                                    End If
                                                    If tarScanner IsNot Nothing AndAlso Not IsIndexPartition Then
                                                        tarScanner.Process(FileData, 0, FileData.Length)
                                                    End If
                                                    If IsIndexPartition Then succ = True
                                                    Exit While
                                                Catch ex As Exception
                                                    Dim dResult As DialogResult
                                                    Invoke(Sub() dResult = MessageBox.Show(New Form With {.TopMost = True}, $"{My.Resources.ResText_WErr }{vbCrLf}{ex.ToString}", My.Resources.ResText_Warning, MessageBoxButtons.AbortRetryIgnore))
                                                    Select Case dResult
                                                        Case DialogResult.Abort
                                                            StopFlag = True
                                                            fr.Close()
                                                            Throw ex
                                                        Case DialogResult.Retry

                                                        Case DialogResult.Ignore
                                                            PrintMsg($"Cannot read file {fr.SourcePath}", LogOnly:=True, ForceLog:=True)
                                                            StopFlag = True
                                                            Exit For
                                                    End Select
                                                End Try
                                            End While
                                            'If i < WriteList.Count - 1 Then WriteList(i + 1).BeginOpen()
                                            While ((Not succ) AndAlso (Not IsIndexPartition))
                                                Dim sense As Byte()
                                                Try
                                                    sense = TapeUtils.Write(driveHandle, FileData)
                                                    SyncLock p
                                                        p.BlockNumber = CULng(p.BlockNumber + 1)
                                                    End SyncLock
                                                Catch ex As Exception
                                                    Dim dResult As DialogResult
                                                    Invoke(Sub() dResult = MessageBox.Show(New Form With {.TopMost = True}, $"{My.Resources.ResText_WErrSCSI}{vbCrLf}{ex.ToString}", My.Resources.ResText_Warning, MessageBoxButtons.AbortRetryIgnore))
                                                    Select Case dResult
                                                        Case DialogResult.Abort
                                                            StopFlag = True
                                                            fr.Close()
                                                            Throw ex
                                                        Case DialogResult.Retry
                                                            succ = False
                                                        Case DialogResult.Ignore
                                                            succ = True
                                                            Exit While
                                                    End Select
                                                    p = New TapeUtils.PositionData(driveHandle)
                                                    Continue While
                                                End Try
                                                If ((sense(2) >> 6) And &H1) = 1 Then
                                                    If ((sense(2) And &HF) = 13) AndAlso (Not My.Settings.LTFSWriter_IgnoreVolumeOverflow) Then
                                                        PrintMsg(My.Resources.ResText_VOF)
                                                        Invoke(Sub() MessageBox.Show(New Form With {.TopMost = True}, My.Resources.ResText_VOF))
                                                        StopFlag = True
                                                        Exit For
                                                    Else
                                                        PrintMsg(If(((sense(2) And &HF) = 13), My.Resources.ResText_VOF, My.Resources.ResText_EWEOM), True, DeDupe:=True)
                                                        succ = True
                                                        Exit While
                                                    End If
                                                ElseIf (sense(2) And &HF) <> 0 Then
                                                    PrintMsg($"sense err {TapeUtils.Byte2Hex(sense, True)}", Warning:=True, LogOnly:=True)
                                                    Try
                                                        Throw New Exception("SCSI sense error")
                                                    Catch ex As Exception
                                                        Dim dResult As DialogResult
                                                        Invoke(Sub() dResult = MessageBox.Show(New Form With {.TopMost = True}, $"{My.Resources.ResText_WErr}{vbCrLf}{TapeUtils.ParseSenseData(sense)}{vbCrLf}{vbCrLf}sense{vbCrLf}{TapeUtils.Byte2Hex(sense, True)}{vbCrLf}{ex.StackTrace}", My.Resources.ResText_Warning, MessageBoxButtons.AbortRetryIgnore))
                                                        Select Case dResult
                                                            Case DialogResult.Abort
                                                                fr.Close()
                                                                StopFlag = True
                                                                Throw New Exception(TapeUtils.ParseSenseData(sense))
                                                            Case DialogResult.Retry
                                                                succ = False
                                                            Case DialogResult.Ignore
                                                                succ = True
                                                                Exit While
                                                        End Select
                                                    End Try

                                                    p = New TapeUtils.PositionData(driveHandle)
                                                Else
                                                    succ = True
                                                End If
                                            End While
                                            If succ AndAlso HashOnWrite Then
                                                Dim hashTask As Task = Task.Factory.StartNew(
                                                      Sub(state As Object)
                                                          Dim hashState = DirectCast(state, Tuple(Of FileRecord, Byte()))
                                                          Dim hashRecord As FileRecord = hashState.Item1
                                                          Dim hashData As Byte() = hashState.Item2
                                                          Dim sh As New IOManager.CheckSumBlockwiseCalculator
                                                          Try
                                                              sh.Propagate(hashData)
                                                              sh.ProcessFinalBlock()
                                                              If My.Settings.LTFSWriter_ChecksumEnabled_SHA1 Then hashRecord.File.SetXattr(ltfsindex.file.xattr.HashType.SHA1, sh.SHA1Value)
                                                              If My.Settings.LTFSWriter_ChecksumEnabled_SHA256 Then hashRecord.File.SetXattr(ltfsindex.file.xattr.HashType.SHA256, sh.SHA256Value)
                                                              If My.Settings.LTFSWriter_ChecksumEnabled_SHA512 Then hashRecord.File.SetXattr(ltfsindex.file.xattr.HashType.SHA512, sh.SHA512Value)
                                                              If My.Settings.LTFSWriter_ChecksumEnabled_CRC32 Then hashRecord.File.SetXattr(ltfsindex.file.xattr.HashType.CRC32, sh.CRC32Value)
                                                              If My.Settings.LTFSWriter_ChecksumEnabled_MD5 Then hashRecord.File.SetXattr(ltfsindex.file.xattr.HashType.MD5, sh.MD5Value)
                                                              If sh.BlakeValue IsNot Nothing AndAlso My.Settings.LTFSWriter_ChecksumEnabled_BLAKE3 Then
                                                                  hashRecord.File.SetXattr(ltfsindex.file.xattr.HashType.BLAKE3, sh.BlakeValue)
                                                              End If
                                                              If sh.XXHash3Value IsNot Nothing AndAlso My.Settings.LTFSWriter_ChecksumEnabled_XxHash3 Then
                                                                  hashRecord.File.SetXattr(ltfsindex.file.xattr.HashType.XxHash3, sh.XXHash3Value)
                                                              End If
                                                              If sh.XXHash128Value IsNot Nothing AndAlso My.Settings.LTFSWriter_ChecksumEnabled_XxHash128 Then
                                                                  hashRecord.File.SetXattr(ltfsindex.file.xattr.HashType.XxHash128, sh.XXHash128Value)
                                                              End If
                                                          Finally
                                                              sh.StopFlag = True
                                                          End Try
                                                      End Sub, Tuple.Create(fr, FileData))
                                                hashTasks.Add(hashTask)
                                            End If
                                            If fr.fs IsNot Nothing Then fr.Close()
                                            If Flush Then
                                                If CheckFlush() Then
                                                    If My.Settings.LTFSWriter_PowerPolicyOnWriteBegin <> Guid.Empty Then
                                                        Process.Start(New ProcessStartInfo With {.FileName = "powercfg",
                                                                      .Arguments = $"/s {My.Settings.LTFSWriter_PowerPolicyOnWriteBegin.ToString()}",
                                                                      .WindowStyle = ProcessWindowStyle.Hidden})
                                                    End If
                                                End If
                                            End If
                                            fr.File.WrittenBytes += fr.File.length
                                            TotalBytesProcessed += fr.File.length
                                            CurrentBytesProcessed += fr.File.length
                                            TotalFilesProcessed += 1
                                            CurrentFilesProcessed += 1
                                            TotalBytesUnindexed += fr.File.length
                                        Else
                                            'Select Case fr.Open()
                                            '    Case DialogResult.Ignore
                                            '        PrintMsg($"Cannot open file {fr.SourcePath}", LogOnly:=True, ForceLog:=True)
                                            '        Continue For
                                            '    Case DialogResult.Abort
                                            '        StopFlag = True
                                            '        Throw New Exception(My.Resources.ResText_FileOpenError)
                                            'End Select
                                            'PrintMsg($"File Opened:{fr.SourcePath}", LogOnly:=True)
                                            Dim sh As IOManager.CheckSumBlockwiseCalculator = Nothing
                                            If HashOnWrite Then sh = New IOManager.CheckSumBlockwiseCalculator
                                            Dim ExitWhileFlag As Boolean = False
                                            'Dim tstart As Date = Now
                                            'Dim tsub As Double = 0
                                            Dim RingBufferReader As SpscRingBuffer = Nothing
                                            Dim PipeReader As System.IO.Pipelines.PipeReader = Nothing
                                            If RingBufferEnabled Then
                                                RingBufferReader = provider.RingBuffer
                                            Else
                                                PipeReader = provider.Reader
                                            End If
                                            Dim remainingInFile As Long = fr.SegmentLength
                                            Dim LWTE As New AutoResetEvent(False)
                                            writeWaitHandles.Add(LWTE)
                                            While Not StopFlag AndAlso remainingInFile > 0
                                                Dim toRead As Integer = CInt(Math.Min(plabel.blocksize, remainingInFile))
                                                Dim buffer As Byte() = IOManager.PublicArrayPool.Rent(plabel.blocksize)
                                                Dim BytesReaded As Integer = 0

                                                ' 从 Pipe 读取当前文件需要的字节
                                                Try
                                                    If RingBufferEnabled Then
                                                        BytesReaded = PipeReadExactly(RingBufferReader, buffer, toRead)
                                                    Else
                                                        Dim pCounter1 As Integer = 0
                                                        Dim pCounter2 As Integer = 0
                                                        Dim lastlen As Long = PipeBufferLength
                                                        While PipePause AndAlso Not StopFlag
                                                            Threading.Thread.Sleep(10)
                                                            Threading.Interlocked.Increment(pCounter1)
                                                            pCounter1 += 1
                                                            pCounter2 += 1
                                                            If pCounter1 >= 100 Then
                                                                pCounter1 = 0
                                                                If RingBufferEnabled Then
                                                                    PipeBufferLength = PipeGetLength(provider.RingBuffer)
                                                                Else
                                                                    PipeBufferLength = PipeGetLength(provider.Reader)
                                                                End If
                                                                If pCounter2 >= 1000 Then
                                                                    If PipeBufferLength = lastlen OrElse PipeBufferLength >= My.Settings.LTFSWriter_PreLoadBytes * 0.75 Then
                                                                        PipePause = False
                                                                    End If
                                                                    lastlen = PipeBufferLength
                                                                    pCounter2 = 0
                                                                Else
                                                                    If PipeBufferLength >= My.Settings.LTFSWriter_PreLoadBytes * 0.75 Then
                                                                        PipePause = False
                                                                    End If
                                                                End If
                                                            End If
                                                        End While
                                                        If StopFlag Then
                                                            IOManager.PublicArrayPool.Return(buffer)
                                                            ExitWhileFlag = True
                                                            Exit While
                                                        End If
                                                        BytesReaded = PipeReadExactly(PipeReader, buffer, toRead)

                                                    End If
                                                    If BytesReaded = 0 Then
                                                        IOManager.PublicArrayPool.Return(buffer)
                                                        ExitWhileFlag = True
                                                        Exit While
                                                    End If
                                                Catch ex As Exception
                                                    Try
                                                        IOManager.PublicArrayPool.Return(buffer)
                                                    Catch
                                                    End Try
                                                    Dim dResult As DialogResult
                                                    Invoke(Sub() dResult = MessageBox.Show(New Form With {.TopMost = True}, $"{My.Resources.ResText_WErr }{vbCrLf}{ex.ToString}", My.Resources.ResText_Warning, MessageBoxButtons.AbortRetryIgnore))
                                                    Select Case dResult
                                                        Case DialogResult.Abort
                                                            StopFlag = True
                                                            Throw
                                                        Case DialogResult.Retry
                                                            Continue While
                                                        Case DialogResult.Ignore
                                                            PrintMsg($"Cannot read file {fr.SourcePath}", LogOnly:=True, ForceLog:=True)
                                                            SetStatusLight(LWStatus.Err)
                                                            StopFlag = True
                                                            Exit For
                                                    End Select
                                                End Try

                                                If tarScanner IsNot Nothing AndAlso Not IsIndexPartition AndAlso BytesReaded > 0 Then
                                                    tarScanner.Process(buffer, 0, BytesReaded)
                                                End If

                                                If LastWriteTask IsNot Nothing Then
                                                    Dim lwcounter As Integer = 0
                                                    While Not (LastWriteTask.IsCompleted OrElse LastWriteTask.IsCanceled OrElse LastWriteTask.IsFaulted)
                                                        LWTE.WaitOne(10)
                                                        lwcounter += 1
                                                        If lwcounter >= 100 Then
                                                            lwcounter = 0
                                                            If RingBufferEnabled Then
                                                                PipeBufferLength = PipeGetLength(RingBufferReader)
                                                            Else
                                                                PipeBufferLength = PipeGetLength(PipeReader)
                                                            End If
                                                        End If
                                                    End While
                                                    Try
                                                        ObserveTask(LastWriteTask)
                                                    Catch
                                                        IOManager.PublicArrayPool.Return(buffer)
                                                        Throw
                                                    End Try
                                                    writeTasks.Remove(LastWriteTask)
                                                End If
                                                If ExitWhileFlag Then Exit While

                                                LastWriteTask = Task.Factory.StartNew(
                                                 Sub(state)
                                                     Dim writeState = DirectCast(state, Tuple(Of Byte(), Integer))
                                                     Dim buf As Byte() = writeState.Item1
                                                     Dim bytesToWrite As Integer = writeState.Item2
                                                     Dim bufferReturned As Boolean = False
                                                     Try
                                                         If bytesToWrite > 0 Then
                                                             ' 限速（保留原逻辑）
                                                             CheckCount += 1
                                                             If CheckCount >= CheckCycle Then CheckCount = 0
                                                             If SpeedLimit > 0 AndAlso CheckCount = 0 Then
                                                                 Dim ts As Double = (Now - SpeedLimitLastTriggerTime).TotalSeconds
                                                                 While SpeedLimit > 0 AndAlso ts > 0 AndAlso ((plabel.blocksize * CheckCycle / 1048576) / ts) > SpeedLimit
                                                                     Threading.Thread.Sleep(0)
                                                                     ts = (Now - SpeedLimitLastTriggerTime).TotalSeconds
                                                                 End While
                                                                 SpeedLimitLastTriggerTime = Now
                                                             End If

                                                             ' 写带（保留原 TapeUtils.Write + sense 处理）
                                                             Marshal.Copy(buf, 0, wBufferPtr, bytesToWrite)
                                                             Dim succ As Boolean = False Or IsIndexPartition
                                                             While ((Not succ) AndAlso (Not IsIndexPartition))
                                                                 Dim sense As Byte()
                                                                 Try
                                                                     sense = TapeUtils.Write(driveHandle, wBufferPtr, CUInt(bytesToWrite), True)
                                                                     SyncLock p
                                                                         p.BlockNumber = CULng(p.BlockNumber + 1)
                                                                     End SyncLock
                                                                 Catch ex As Exception
                                                                     Dim dResult As DialogResult
                                                                     Invoke(Sub() dResult = MessageBox.Show(New Form With {.TopMost = True}, $"{My.Resources.ResText_WErrSCSI}{vbCrLf}{ex.ToString}", My.Resources.ResText_Warning, MessageBoxButtons.AbortRetryIgnore))
                                                                     Select Case dResult
                                                                         Case DialogResult.Abort
                                                                             StopFlag = True
                                                                             Throw
                                                                         Case DialogResult.Retry
                                                                             succ = False
                                                                         Case DialogResult.Ignore
                                                                             succ = True
                                                                             Exit While
                                                                     End Select
                                                                     p = New TapeUtils.PositionData(driveHandle)
                                                                     Continue While
                                                                 End Try
                                                                 If (((sense(2) >> 6) And &H1) = 1) Then
                                                                     If ((sense(2) And &HF) = 13) AndAlso (Not My.Settings.LTFSWriter_IgnoreVolumeOverflow) Then
                                                                         PrintMsg(My.Resources.ResText_VOF)
                                                                         Invoke(Sub() MessageBox.Show(New Form With {.TopMost = True}, My.Resources.ResText_VOF))
                                                                         StopFlag = True
                                                                         Try
                                                                             PipeBufferLength = 0
                                                                             provider.Cancel()
                                                                             provider.CompleteAsync().GetAwaiter().GetResult()
                                                                         Catch
                                                                             PrintMsg("pipe complete failed", LogOnly:=True)
                                                                         End Try
                                                                         Exit Sub
                                                                     Else
                                                                         PrintMsg(If(((sense(2) And &HF) = 13), My.Resources.ResText_VOF, My.Resources.ResText_EWEOM), True, DeDupe:=True)
                                                                         succ = True
                                                                         Exit While
                                                                     End If
                                                                 ElseIf (sense(2) And &HF) <> 0 Then
                                                                     Try
                                                                         Throw New Exception("SCSI sense error")
                                                                     Catch ex As Exception
                                                                         Dim dResult As DialogResult
                                                                         Invoke(Sub() dResult = MessageBox.Show(New Form With {.TopMost = True}, $"{My.Resources.ResText_WErr}{vbCrLf}{TapeUtils.ParseSenseData(sense)}{vbCrLf}{vbCrLf}sense{vbCrLf}{TapeUtils.Byte2Hex(sense, True)}{vbCrLf}{ex.StackTrace}", My.Resources.ResText_Warning, MessageBoxButtons.AbortRetryIgnore))
                                                                         Select Case dResult
                                                                             Case DialogResult.Abort
                                                                                 StopFlag = True
                                                                                 Throw New Exception(TapeUtils.ParseSenseData(sense))
                                                                             Case DialogResult.Retry
                                                                                 succ = False
                                                                             Case DialogResult.Ignore
                                                                                 succ = True
                                                                                 Exit While
                                                                         End Select
                                                                     End Try
                                                                     p = New TapeUtils.PositionData(driveHandle)
                                                                 Else
                                                                     succ = True
                                                                     Exit While
                                                                 End If
                                                             End While
                                                             If sh IsNot Nothing AndAlso succ Then
                                                                 If 异步校验CPU占用高ToolStripMenuItem.Checked Then
                                                                     sh.PropagateAsync(buf, bytesToWrite, Sub(qb As Byte())
                                                                                                              IOManager.PublicArrayPool.Return(qb)
                                                                                                          End Sub)
                                                                 Else
                                                                     sh.Propagate(buf, bytesToWrite, Sub(qb As Byte())
                                                                                                         IOManager.PublicArrayPool.Return(qb)
                                                                                                     End Sub)
                                                                 End If
                                                                 bufferReturned = True
                                                             Else
                                                                 IOManager.PublicArrayPool.Return(buf)
                                                                 bufferReturned = True
                                                             End If
                                                             If Flush Then
                                                                 Dim flushResult As Boolean = False
                                                                 Dim tFE As New AutoResetEvent(False)
                                                                 Dim tFlush As Task = Task.Run(Sub()
                                                                                                   flushResult = CheckFlush()
                                                                                                   tFE.Set()
                                                                                               End Sub)
                                                                 Dim counter As Integer = 0
                                                                 While Not (tFlush.IsCompleted OrElse tFlush.IsCanceled OrElse tFlush.IsFaulted)
                                                                     tFE.WaitOne(10)
                                                                     counter += 1
                                                                     If counter >= 100 Then
                                                                         counter = 0
                                                                         If RingBufferEnabled Then
                                                                             PipeBufferLength = PipeGetLength(RingBufferReader)
                                                                         Else
                                                                             PipeBufferLength = PipeGetLength(PipeReader)
                                                                         End If
                                                                     End If
                                                                 End While
                                                                 Try
                                                                     ObserveTask(tFlush)
                                                                 Finally
                                                                     tFE.Dispose()
                                                                 End Try
                                                                 If flushResult Then
                                                                     If My.Settings.LTFSWriter_PowerPolicyOnWriteBegin <> Guid.Empty Then
                                                                         Process.Start(New ProcessStartInfo With {.FileName = "powercfg",
                                                                        .Arguments = $"/s {My.Settings.LTFSWriter_PowerPolicyOnWriteBegin.ToString()}",
                                                                        .WindowStyle = ProcessWindowStyle.Hidden})
                                                                     End If
                                                                 End If
                                                             End If
                                                             If Clean Then
                                                                 Dim tCE As New AutoResetEvent(False)
                                                                 Dim tClean As Task = Task.Run(Sub()
                                                                                                   CheckClean(True)
                                                                                                   tCE.Set()
                                                                                               End Sub)
                                                                 Dim counter As Integer = 0
                                                                 While Not (tClean.IsCompleted OrElse tClean.IsCanceled OrElse tClean.IsFaulted)
                                                                     tCE.WaitOne(10)
                                                                     counter += 1
                                                                     If counter >= 100 Then
                                                                         counter = 0
                                                                         If RingBufferEnabled Then
                                                                             PipeBufferLength = PipeGetLength(RingBufferReader)
                                                                         Else
                                                                             PipeBufferLength = PipeGetLength(PipeReader)
                                                                         End If
                                                                     End If
                                                                 End While
                                                                 Try
                                                                     ObserveTask(tClean)
                                                                 Finally
                                                                     tCE.Dispose()
                                                                 End Try
                                                             End If
                                                             fr.File.WrittenBytes += bytesToWrite
                                                             TotalBytesProcessed += bytesToWrite
                                                             CurrentBytesProcessed += bytesToWrite
                                                             TotalBytesUnindexed += bytesToWrite
                                                         Else
                                                             ExitWhileFlag = True
                                                         End If
                                                     Finally
                                                         If Not bufferReturned Then
                                                             IOManager.PublicArrayPool.Return(buf)
                                                         End If
                                                         Try
                                                             LWTE.Set()
                                                         Catch
                                                         End Try
                                                     End Try
                                                 End Sub, Tuple.Create(buffer, BytesReaded))
                                                writeTasks.Add(LastWriteTask)
                                                remainingInFile -= BytesReaded
                                            End While

                                            'If i < WriteList.Count - 1 Then WriteList(i + 1).BeginOpen()
                                            If LastWriteTask IsNot Nothing Then
                                                Dim lwcounter As Integer = 0
                                                While Not (LastWriteTask.IsCompleted OrElse LastWriteTask.IsCanceled OrElse LastWriteTask.IsFaulted)
                                                    LWTE.WaitOne(10)
                                                    lwcounter += 1
                                                    If lwcounter >= 100 Then
                                                        lwcounter = 0
                                                        If RingBufferEnabled Then
                                                            PipeBufferLength = PipeGetLength(RingBufferReader)
                                                        Else
                                                            PipeBufferLength = PipeGetLength(PipeReader)
                                                        End If
                                                    End If
                                                End While
                                                ObserveTask(LastWriteTask)
                                                writeTasks.Remove(LastWriteTask)
                                            End If
                                            fr.Close()
                                            If HashOnWrite AndAlso sh IsNot Nothing AndAlso Not StopFlag Then
                                                Dim hashTask As Task = Task.Factory.StartNew(
                                                      Sub(state As Object)
                                                          Dim hashState = DirectCast(state, Tuple(Of FileRecord, IOManager.CheckSumBlockwiseCalculator))
                                                          Dim hashRecord As FileRecord = hashState.Item1
                                                          Dim hashCalculator As IOManager.CheckSumBlockwiseCalculator = hashState.Item2
                                                          Try
                                                              hashCalculator.ProcessFinalBlock()
                                                              If My.Settings.LTFSWriter_ChecksumEnabled_SHA1 Then hashRecord.File.SetXattr(ltfsindex.file.xattr.HashType.SHA1, hashCalculator.SHA1Value)
                                                              If My.Settings.LTFSWriter_ChecksumEnabled_SHA256 Then hashRecord.File.SetXattr(ltfsindex.file.xattr.HashType.SHA256, hashCalculator.SHA256Value)
                                                              If My.Settings.LTFSWriter_ChecksumEnabled_SHA512 Then hashRecord.File.SetXattr(ltfsindex.file.xattr.HashType.SHA512, hashCalculator.SHA512Value)
                                                              If My.Settings.LTFSWriter_ChecksumEnabled_CRC32 Then hashRecord.File.SetXattr(ltfsindex.file.xattr.HashType.CRC32, hashCalculator.CRC32Value)
                                                              If My.Settings.LTFSWriter_ChecksumEnabled_MD5 Then hashRecord.File.SetXattr(ltfsindex.file.xattr.HashType.MD5, hashCalculator.MD5Value)
                                                              If hashCalculator.BlakeValue IsNot Nothing AndAlso My.Settings.LTFSWriter_ChecksumEnabled_BLAKE3 Then
                                                                  hashRecord.File.SetXattr(ltfsindex.file.xattr.HashType.BLAKE3, hashCalculator.BlakeValue)
                                                              End If
                                                              If hashCalculator.XXHash3Value IsNot Nothing AndAlso My.Settings.LTFSWriter_ChecksumEnabled_XxHash3 Then
                                                                  hashRecord.File.SetXattr(ltfsindex.file.xattr.HashType.XxHash3, hashCalculator.XXHash3Value)
                                                              End If
                                                              If hashCalculator.XXHash128Value IsNot Nothing AndAlso My.Settings.LTFSWriter_ChecksumEnabled_XxHash128 Then
                                                                  hashRecord.File.SetXattr(ltfsindex.file.xattr.HashType.XxHash128, hashCalculator.XXHash128Value)
                                                              End If
                                                          Finally
                                                              hashCalculator.StopFlag = True
                                                          End Try
                                                      End Sub, Tuple.Create(fr, sh))
                                                hashTasks.Add(hashTask)
                                                If CheckUnindexedDataLimit(CheckOnly:=True) Then
                                                    WaitForHashTasks(hashTasks)
                                                    SetStatusLight(LWStatus.Busy)
                                                End If
                                            ElseIf sh IsNot Nothing Then
                                                sh.StopFlag = True
                                            End If
                                            TotalFilesProcessed += 1
                                            CurrentFilesProcessed += 1
                                        End If
                                        If Not String.IsNullOrEmpty(currentPlan.ExpectedDedupeHash) Then
                                            SetFileDedupeHash(fr.File, currentPlan.ExpectedDedupeHash)
                                        End If
                                        p = New TapeUtils.PositionData(driveHandle)
                                        If Not IsIndexPartition Then
                                            lastpos = New TapeUtils.PositionData(driveHandle)
                                            CurrentHeight = CLng(p.BlockNumber)
                                        End If
                                        If p.EOP Then PrintMsg(My.Resources.ResText_EWEOM, True, DeDupe:=True)
                                        PrintMsg($"Position = {p.ToString()}", LogOnly:=True)
                                    End If
                                Else
                                    fr.File.SetXattr(ltfsindex.file.xattr.HashType.CRC32, "00000000")
                                    fr.File.SetXattr(ltfsindex.file.xattr.HashType.SHA1, "DA39A3EE5E6B4B0D3255BFEF95601890AFD80709")
                                    fr.File.SetXattr(ltfsindex.file.xattr.HashType.SHA256, "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855")
                                    fr.File.SetXattr(ltfsindex.file.xattr.HashType.SHA512, "CF83E1357EEFB8BDF1542850D66D8007D620E4050B5715DC83F4A921D36CE9CE47D0D13C5D85F2B0FF8318D2877EEC2F63B931BD47417A81A538327AF927DA3E")
                                    fr.File.SetXattr(ltfsindex.file.xattr.HashType.MD5, "D41D8CD98F00B204E9800998ECF8427E")
                                    fr.File.SetXattr(ltfsindex.file.xattr.HashType.BLAKE3, "AF1349B9F5F9A1A6A0404DEA36DCC9499BCB25C9ADC112B7CC9A93CAE41F3262")
                                    fr.File.SetXattr(ltfsindex.file.xattr.HashType.XxHash3, "2D06800538D394C2")
                                    fr.File.SetXattr(ltfsindex.file.xattr.HashType.XxHash128, IOManager.Byte2Hex(IO.Hashing.XxHash128.Hash({})).Replace(" ", "").Replace("|", "").ToUpper())
                                    TotalBytesUnindexed += 1
                                    TotalFilesProcessed += 1
                                    CurrentFilesProcessed += 1
                                End If
                                If tarScanner IsNot Nothing AndAlso Not StopFlag Then
                                    CommitTarMetadata(fr, tarScanner)
                                End If
                                'Commit several completed files together.  The
                                'lazy schema store can then update one mutation
                                'and propagate one aggregate count per parent.
                                MarkWriteFileCompleted(fr.File)
                                completedWriteRecords.Add(fr)
                                If Not useFastReader Then
                                    ReleaseManagedWriteRecord(WriteList, i, dedupeReferenceUseCount)
                                End If
                                RequestListDisplayRefresh()
                                Dim extentCount As Integer = If(fr.File.extentinfo Is Nothing, 0, fr.File.extentinfo.Count)
                                Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(LTFSWriter))
                                    Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Writer")
                                        Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                                            Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "FileCompleted")
                                                Log.Information("Writer completed file. FileIndex={FileIndex} FileName={FileName} SourcePath={SourcePath} Length={Length} WrittenBytes={WrittenBytes} ExtentCount={ExtentCount} PlanKind={PlanKind} IndexPartition={IndexPartition} FastReader={FastReader}.",
                                                                i,
                                                                fr.File.name,
                                                                fr.SourcePath,
                                                                fr.File.length,
                                                                fr.File.WrittenBytes,
                                                                extentCount,
                                                                currentPlan.Kind.ToString(),
                                                                IsIndexPartition,
                                                                useFastReader)
                                            End Using
                                        End Using
                                    End Using
                                End Using
                                If TotalBytesUnindexed = 0 Then TotalBytesUnindexed = 1
                                If (Not IsIndexPartition) AndAlso IsUnindexedDataLimitReached(False) Then
                                    'The checkpoint serializer only sees files that
                                    'are already attached to the schema.  Finish
                                    'hash workers and commit this batch before the
                                    'index is emitted, otherwise a crash/reload can
                                    'lose files that are already on tape.
                                    WaitForHashTasks(hashTasks)
                                    CommitWrittenFiles(completedWriteRecords)
                                    completedWriteRecords.Clear()
                                    If CheckUnindexedDataLimit() Then
                                        p = New TapeUtils.PositionData(driveHandle)
                                        lastpos = New TapeUtils.PositionData(driveHandle)
                                        CurrentHeight = CLng(p.BlockNumber)
                                        SetStatusLight(LWStatus.Busy)
                                    End If
                                End If
                                If CapacityRefreshInterval > 0 AndAlso (Now - LastRefresh).TotalSeconds > CapacityRefreshInterval Then
                                    p = New TapeUtils.PositionData(driveHandle)
                                    Dim capValue As Long() = RefreshCapacity()
                                    fr.File.SetXattr(ltfsindex.file.xattr.ApplicationSpecific.CapacityRemain, CStr(capValue(p.PartitionNumber * 2)))
                                    Dim p2 As New TapeUtils.PositionData(driveHandle)
                                    If p2.BlockNumber <> p.BlockNumber OrElse p2.PartitionNumber <> p.PartitionNumber Then
                                        Invoke(Sub()
                                                   While True
                                                       Select Case MessageBox.Show(New Form With {.TopMost = True}, $"Position changed! {p.BlockNumber} -> {p2.BlockNumber}", "Warning", MessageBoxButtons.AbortRetryIgnore)
                                                           Case DialogResult.Abort
                                                               StopFlag = True
                                                           Case DialogResult.Retry
                                                               TapeUtils.Locate(driveHandle, p.BlockNumber, p.PartitionNumber)
                                                               p2 = New TapeUtils.PositionData(driveHandle)
                                                               If p2.BlockNumber = p.BlockNumber AndAlso p2.PartitionNumber = p.PartitionNumber Then Exit While
                                                           Case DialogResult.Ignore
                                                               Exit While
                                                       End Select
                                                   End While

                                               End Sub)
                                    End If
                                End If
                            Catch ex As OperationCanceledException When StopFlag
                                Exit For
                            Catch ex As Exception
                                Invoke(Sub() MessageBox.Show(New Form With {.TopMost = True}, $"{My.Resources.ResText_WErr}{vbCrLf}{ex.ToString}"))
                                PrintMsg($"{My.Resources.ResText_WErr}{ex.Message}{vbCrLf}{ex.StackTrace}")
                                SetStatusLight(LWStatus.Err)
                                StopFlag = True
                                Exit For
                            End Try
                            If Pause Then
                                Invoke(Sub()
                                           With Microsoft.WindowsAPICodePack.Taskbar.TaskbarManager.Instance
                                               .SetProgressState(Microsoft.WindowsAPICodePack.Taskbar.TaskbarProgressBarState.Paused)
                                           End With
                                       End Sub)
                            End If
                            Dim pCounter As Integer = 0
                            While Pause AndAlso Not StopFlag
                                Threading.Thread.Sleep(10)
                                Threading.Interlocked.Increment(pCounter)
                                pCounter += 1
                                If pCounter >= 100 Then
                                    pCounter = 0
                                    If useFastReader AndAlso fastProvider IsNot Nothing Then
                                        PipeBufferLength = fastProvider.BufferedBytes
                                    ElseIf RingBufferEnabled Then
                                        PipeBufferLength = PipeGetLength(provider.RingBuffer)
                                    Else
                                        PipeBufferLength = PipeGetLength(provider.Reader)
                                    End If
                                End If
                                If Not Pause Then
                                    Invoke(Sub()
                                               With Microsoft.WindowsAPICodePack.Taskbar.TaskbarManager.Instance
                                                   .SetProgressState(Microsoft.WindowsAPICodePack.Taskbar.TaskbarProgressBarState.Normal)
                                               End With
                                           End Sub)
                                End If
                            End While
                            If StopFlag Then
                                Exit For
                            End If
                        Next
                        WaitForHashTasks(hashTasks)
                        CommitWrittenFiles(completedWriteRecords)
                    End If
                    If ufReadCountIncremented Then
                        UFReadCount.Dec()
                        ufReadCountIncremented = False
                    End If
                    Me.Invoke(Sub() Timer1_Tick(sender, e))
                    Dim TotalBytesWritten As Long = CLng(UnwrittenSizeOverrideValue)
                    While True
                        Threading.Thread.Sleep(0)
                        SyncLock UFReadCount
                            If UFReadCount > 0 Then Continue While
                            UnwrittenSizeOverrideValue = 0
                            UnwrittenCountOverrideValue = 0
                            If Not My.Settings.LTFSWriter_KeepUnwrittenFilesOnAbort Then
                                ClearPendingWriteQueues()
                            Else
                                UnwrittenSizeOverrideValue = UnwrittenSize
                                UnwrittenCountOverrideValue = UnwrittenCount
                            End If
                            CurrentFilesProcessed = 0
                            CurrentBytesProcessed = 0
                            Exit While
                        End SyncLock
                    End While
                    Modified = True
                    If Not StopFlag Then
                        Dim TimeCost As TimeSpan = Now - StartTime
                        OnWriteFinishMessage = ($"{My.Resources.ResText_WFTime}{(Math.Floor(TimeCost.TotalHours)).ToString().PadLeft(2, "0"c)}:{TimeCost.Minutes.ToString().PadLeft(2, "0"c)}:{TimeCost.Seconds.ToString().PadLeft(2, "0"c)} {My.Resources.ResText_AvgS}{IOManager.FormatSize(TotalBytesWritten \ CLng(Math.Max(1, TimeCost.TotalSeconds)))}/s")
                        OnWriteFinished()
                    Else
                        OnWriteFinishMessage = (My.Resources.ResText_WCnd)
                    End If
                Catch ex As OperationCanceledException When StopFlag
                    Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(LTFSWriter))
                        Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FastReader")
                            Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                                Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Cancellation")
                                    Log.Information(ex, "Writer operation canceled while the fast reader was active. StopFlag={StopFlag}.", StopFlag)
                                End Using
                            End Using
                        End Using
                    End Using
                Catch ex As Exception
                    Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(LTFSWriter))
                        Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FastReader")
                            Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                                Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Error")
                                    Log.Error(ex, "Writer operation failed while the fast reader was active.")
                                End Using
                            End Using
                        End Using
                    End Using
                    Invoke(Sub() MessageBox.Show(New Form With {.TopMost = True}, $"{My.Resources.ResText_WErr}{vbCrLf}{ex.ToString}"))
                    PrintMsg($"{My.Resources.ResText_WErr}{ex.Message}")
                    SetStatusLight(LWStatus.Err)
                Finally
                    If locateTask IsNot Nothing Then
                        Try
                            ObserveTask(locateTask)
                        Catch cleanupEx As Exception
                            Log.Error(cleanupEx, "Writer locate task failed during cleanup.")
                            StopFlag = True
                        Finally
                            locateTask = Nothing
                        End Try
                    End If
                    Try
                        WaitForTasks(writeTasks)
                    Catch cleanupEx As Exception
                        Log.Error(cleanupEx, "Writer write tasks failed during cleanup.")
                        StopFlag = True
                    End Try
                    Try
                        WaitForHashTasks(hashTasks)
                    Catch cleanupEx As Exception
                        Log.Error(cleanupEx, "Writer hash tasks failed during cleanup.")
                        StopFlag = True
                    End Try
                    If wBufferPtr <> IntPtr.Zero Then
                        Try
                            Marshal.FreeHGlobal(wBufferPtr)
                        Catch cleanupEx As Exception
                            Log.Error(cleanupEx, "Writer buffer cleanup failed.")
                        Finally
                            wBufferPtr = IntPtr.Zero
                        End Try
                    End If
                    Try
                        If fastProvider IsNot Nothing Then
                            If ReferenceEquals(_activeFastReaderProvider, fastProvider) Then _activeFastReaderProvider = Nothing
                            fastProvider.Dispose()
                            fastProvider = Nothing
                        End If
                    Catch cleanupEx As Exception
                        Log.Error(cleanupEx, "Fast reader cleanup failed.")
                    End Try
                    Try
                        If provider IsNot Nothing Then
                            PipeBufferLength = 0
                            provider.Cancel()
                            provider.CompleteAsync().GetAwaiter().GetResult()
                            provider = Nothing
                        End If
                    Catch cleanupEx As Exception
                        Log.Error(cleanupEx, "Managed reader cleanup failed.")
                    End Try
                    For Each record As FileRecord In WriteList
                        Try
                            If record IsNot Nothing Then record.Close()
                        Catch cleanupEx As Exception
                            Log.Error(cleanupEx, "File record cleanup failed. SourcePath={SourcePath}.", If(record Is Nothing, String.Empty, record.SourcePath))
                        End Try
                    Next
                    For Each waitHandle As AutoResetEvent In writeWaitHandles
                        Try
                            waitHandle.Dispose()
                        Catch cleanupEx As Exception
                            Log.Error(cleanupEx, "Writer wait handle cleanup failed.")
                        End Try
                    Next
                    writeWaitHandles.Clear()
                    If ufReadCountIncremented Then
                        UFReadCount.Dec()
                        ufReadCountIncremented = False
                    End If
                    If LTE IsNot Nothing Then
                        Try
                            LTE.Dispose()
                        Catch
                        End Try
                        LTE = Nothing
                    End If
                    If tapeUnitReserved Then
                        Try
                            TapeUtils.Flush(driveHandle)
                        Catch cleanupEx As Exception
                            Log.Error(cleanupEx, "Tape flush failed during writer cleanup.")
                        End Try
                    End If
                    If mediumRemovalPrevented Then
                        Try
                            TapeUtils.AllowMediumRemoval(driveHandle)
                        Catch cleanupEx As Exception
                            Log.Error(cleanupEx, "Allow-medium-removal failed during writer cleanup.")
                        End Try
                    End If
                    If tapeUnitReserved Then
                        Try
                            TapeUtils.ReleaseUnit(driveHandle)
                        Catch cleanupEx As Exception
                            Log.Error(cleanupEx, "Tape unit release failed during writer cleanup.")
                        End Try
                    End If
                    If My.Settings.LTFSWriter_PowerPolicyOnWriteEnd <> Guid.Empty Then
                        Try
                            Process.Start(New ProcessStartInfo With {.FileName = "powercfg",
                                          .Arguments = $"/s {My.Settings.LTFSWriter_PowerPolicyOnWriteEnd.ToString()}",
                                          .WindowStyle = ProcessWindowStyle.Hidden})
                        Catch cleanupEx As Exception
                            Log.Error(cleanupEx, "Power policy restore failed during writer cleanup.")
                        End Try
                    End If
                    Try
                        LockGUI(False)
                    Catch cleanupEx As Exception
                        Log.Error(cleanupEx, "Writer GUI unlock failed.")
                    End Try
                    IsWriting = False
                End Try
                RefreshDisplay()
                ClearWriteCompletionState()
                RefreshCapacity()
                Invoke(Sub()
                           If Not StopFlag AndAlso WA0ToolStripMenuItem.Checked AndAlso MessageBox.Show(New Form With {.TopMost = True}, My.Resources.ResText_WFUp, My.Resources.ResText_OpSucc, MessageBoxButtons.OKCancel) = DialogResult.OK Then
                               更新数据区索引ToolStripMenuItem_Click(sender, e)
                           End If
                           PrintMsg(OnWriteFinishMessage)
                           SetStatusLight(If(StopFlag, LWStatus.Err, LWStatus.Succ))
                           RaiseEvent WriteFinished()
                       End Sub)
                IsWriting = False
            End Sub)
        StopFlag = False
        LockGUI()
        th.Start()
    End Sub
    Private Sub 清除当前索引后数据ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 清除当前索引后数据ToolStripMenuItem.Click

        If MessageBox.Show(New Form With {.TopMost = True}, My.Resources.ResText_X2, My.Resources.ResText_Warning, MessageBoxButtons.OKCancel) = DialogResult.Cancel Then
            Exit Sub
        End If
        Dim th As New Threading.Thread(
            Sub()
                Try
                    SetStatusLight(LWStatus.Busy)
                    PrintMsg($"Position = {GetPos.ToString()}", LogOnly:=True)
                    PrintMsg(My.Resources.ResText_Locating)
                    TapeUtils.Locate(handle:=driveHandle, BlockAddress:=schema.location.startblock, Partition:=CByte(schema.location.partition), DestType:=TapeUtils.LocateDestType.Block)
                    PrintMsg($"Position = {GetPos.ToString()}", LogOnly:=True)
                    PrintMsg(My.Resources.ResText_RI)
                    Dim outputfile As String = "LTFSIndex_SetEOD_" & Now.ToString("yyyyMMdd_HHmmss.fffffff") & ".schema"
                    outputfile = IO.Path.Combine(My.Settings.schemaPath, outputfile)
                    TapeUtils.ReadToFileMark(driveHandle, outputfile, plabel.blocksize)
                    Dim CurrentPos As TapeUtils.PositionData = GetPos
                    PrintMsg($"Position = {CurrentPos.ToString()}", LogOnly:=True)
                    If CurrentPos.PartitionNumber < ExtraPartitionCount Then
                        Invoke(Sub() MessageBox.Show(New Form With {.TopMost = True}, My.Resources.ResText_IPCanc))
                        Exit Try
                    End If
                    TapeUtils.Locate(driveHandle, CULng(CurrentPos.BlockNumber - 1), CurrentPos.PartitionNumber, TapeUtils.LocateDestType.Block)
                    PrintMsg($"Position = {GetPos.ToString()}", LogOnly:=True)
                    TapeUtils.WriteFileMark(driveHandle)
                    PrintMsg($"FileMark written", LogOnly:=True)
                    PrintMsg($"Position = {GetPos.ToString()}", LogOnly:=True)
                    PrintMsg(My.Resources.ResText_AI)
                    schema = ltfsindex.FromSchFile(outputfile)
                    PrintMsg(My.Resources.ResText_AISucc)
                    Modified = False
                    Dim p As TapeUtils.PositionData = GetPos
                    PrintMsg($"Position = {p.ToString()}", LogOnly:=True)
                    CurrentHeight = CLng(p.BlockNumber)
                    If ExtraPartitionCount = 0 Then
                        TapeUtils.SendSCSICommand(driveHandle, {19, 0, 0, 0, 0, 0})
                        TapeUtils.Flush(driveHandle)
                        PrintMsg($"Byte written", LogOnly:=True)
                        PrintMsg($"Position = {GetPos.ToString()}", LogOnly:=True)
                    End If
                    While True
                        Threading.Thread.Sleep(0)
                        SyncLock UFReadCount
                            If UFReadCount > 0 Then Continue While
                            ClearGlobalPendingQueue()
                            CurrentFilesProcessed = 0
                            CurrentBytesProcessed = 0
                            Exit While
                        End SyncLock
                    End While
                    RefreshDisplay()
                    RefreshCapacity()
                Catch ex As Exception
                    PrintMsg(My.Resources.ResText_RFailed)
                    SetStatusLight(LWStatus.Err)
                End Try
                Modified = False
                PrintMsg(My.Resources.ResText_RollBacked)
                SetStatusLight(LWStatus.Succ)
                Try
                    Dim Loc As String = GetLocInfo()
                    Invoke(Sub() Text = Loc)
                Catch ex As Exception
                End Try
                Me.Invoke(Sub() LockGUI(False))
            End Sub)
        LockGUI()
        th.Start()
    End Sub
    Private Sub 启用日志记录ToolStripMenuItem_CheckedChanged(sender As Object, e As EventArgs) Handles 启用日志记录ToolStripMenuItem.CheckedChanged
        My.Settings.LTFSWriter_LogEnabled = 启用日志记录ToolStripMenuItem.Checked
        AppLogging.SetInformationEnabled(My.Settings.LTFSWriter_LogEnabled)
    End Sub
    Private Sub 总是更新数据区索引ToolStripMenuItem_CheckedChanged(sender As Object, e As EventArgs) Handles 总是更新数据区索引ToolStripMenuItem.CheckedChanged
        My.Settings.LTFSWriter_ForceIndex = 总是更新数据区索引ToolStripMenuItem.Checked
    End Sub
    Private Sub 回滚ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 回滚ToolStripMenuItem.Click
        If MessageBox.Show(New Form With {.TopMost = True}, $"{My.Resources.ResText_RB1}{schema.generationnumber}{My.Resources.ResText_RB2} {My.Resources.ResText_Partition}{schema.location.partition} {My.Resources.ResText_Block}{schema.location.startblock}{vbCrLf _
                           }{My.Resources.ResText_RB3} {My.Resources.ResText_Partition}{schema.previousgenerationlocation.partition} {My.Resources.ResText_Block}{schema.previousgenerationlocation.startblock}{vbCrLf _
                           }{My.Resources.ResText_RB4}", My.Resources.ResText_Warning, MessageBoxButtons.OKCancel) = DialogResult.Cancel Then
            Exit Sub
        End If
        Dim th As New Threading.Thread(
            Sub()
                Try
                    SetStatusLight(LWStatus.Busy)
                    PrintMsg(My.Resources.ResText_RBing)
                    Dim genbefore As Integer = CInt(schema.generationnumber)
                    Dim prevpart As ltfsindex.PartitionLabel = schema.previousgenerationlocation.partition
                    Dim prevblk As Long = CLng(schema.previousgenerationlocation.startblock)
                    PrintMsg($"Position = {GetPos.ToString()}", LogOnly:=True)
                    TapeUtils.Locate(handle:=driveHandle, BlockAddress:=schema.previousgenerationlocation.startblock, Partition:=CByte(schema.previousgenerationlocation.partition), DestType:=TapeUtils.LocateDestType.Block)
                    PrintMsg($"Position = {GetPos.ToString()}", LogOnly:=True)
                    PrintMsg(My.Resources.ResText_RI)
                    Dim outputfile As String = "LTFSIndex_RollBack_" & Now.ToString("yyyyMMdd_HHmmss.fffffff") & ".schema"
                    outputfile = IO.Path.Combine(My.Settings.schemaPath, outputfile)
                    TapeUtils.ReadToFileMark(driveHandle, outputfile, plabel.blocksize)
                    PrintMsg($"Position = {GetPos.ToString()}", LogOnly:=True)
                    PrintMsg(My.Resources.ResText_AI)
                    schema = ltfsindex.FromSchFile(outputfile)
                    PrintMsg(My.Resources.ResText_AISucc)
                    Modified = False
                    Dim p As TapeUtils.PositionData = GetPos
                    PrintMsg($"Position = {p.ToString()}", LogOnly:=True)
                    CurrentHeight = CLng(p.BlockNumber)
                    While True
                        Threading.Thread.Sleep(0)
                        SyncLock UFReadCount
                            If UFReadCount > 0 Then Continue While
                            ClearGlobalPendingQueue()
                            CurrentFilesProcessed = 0
                            CurrentBytesProcessed = 0
                            Exit While
                        End SyncLock
                    End While
                    Me.Invoke(Sub()
                                  PrintMsg($"gen{genbefore}->{schema.generationnumber}: p{prevpart} block{prevblk}->p{schema.location.partition} block{schema.location.startblock}")
                              End Sub)
                    RefreshDisplay()
                    RefreshCapacity()
                Catch ex As Exception
                    PrintMsg(My.Resources.ResText_RFailed)
                    SetStatusLight(LWStatus.Err)
                End Try
                Try
                    Dim Loc As String = GetLocInfo()
                    Invoke(Sub() Text = Loc)
                Catch ex As Exception
                End Try
                Me.Invoke(Sub()
                              LockGUI(False)
                              PrintMsg(My.Resources.ResText_RBFin)
                          End Sub)
                SetStatusLight(LWStatus.Succ)
            End Sub)
        LockGUI()
        th.Start()
    End Sub
    Private Sub 读取索引ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 读取索引ToolStripMenuItem.Click
        Dim th As New Threading.Thread(
            Sub()
                Try
                    If TapeUtils.DriverTypeSetting = TapeUtils.DriverType.TapeStream Then
                        TapeUtils.CloseTapeDrive(driveHandle)
                        If Not IO.File.Exists(TapeDrive) Then
                            IO.File.Create(TapeDrive).Close()
                        End If
                    End If
                    If Not TapeUtils.IsOpened(driveHandle) Then
                        TapeUtils.OpenTapeDrive(TapeDrive, driveHandle)
                    End If
                    If TapeUtils.DriverTypeSetting = TapeUtils.DriverType.TapeStream Then
                        TapeStreamMapping.MappingTable(driveHandle).ReOpen()
                    End If
                    SetStatusLight(LWStatus.Busy)
                    RefreshCapacity()
                    Dim Sense0 As Byte() = {}
                    Dim retrycount As Integer = 5
                    While (retrycount > 0) AndAlso ((Sense0 Is Nothing) OrElse (Sense0.Length < 3) OrElse (Sense0(2) <> 0))
                        Sense0 = TapeUtils.TestUnitReady(driveHandle)
                        PrintMsg(TapeUtils.ParseSenseData(Sense0), LogOnly:=True)
                        Threading.Thread.Sleep(200)
                        retrycount -= 1
                    End While
                    PrintMsg($"Position = {TapeUtils.ReadPosition(driveHandle).ToString()}", LogOnly:=True)
                    PrintMsg(My.Resources.ResText_Locating)
                    Dim PModeData As Byte() = TapeUtils.ModeSense(driveHandle, &H11)
                    If PModeData.Length >= 4 Then ExtraPartitionCount = PModeData(3)
                    If TapeUtils.DriverTypeSetting = TapeUtils.DriverType.TapeStream Then
                        ExtraPartitionCount = CByte(TapeStreamMapping.MappingTable(driveHandle).PartitionCount - 1)
                    End If
                    TapeUtils.GlobalBlockLimit = CInt(TapeUtils.ReadBlockLimits(driveHandle).MaximumBlockLength)
                    TapeUtils.FromFile(My.Settings.driveSettingFile)
                    If IO.File.Exists(My.Settings.encKeyFile) Then
                        Dim key As String = (IO.File.ReadAllText(My.Settings.encKeyFile))
                        Dim newkey As Byte() = IOManager.HexStringToByteArray(key)
                        If newkey.Length <> 32 Then
                            EncryptionKey = Nothing
                        Else
                            Dim sum As Integer = 0
                            For i As Integer = 0 To newkey.Length - 1
                                sum += newkey(i)
                            Next
                            If sum = 0 Then
                                EncryptionKey = Nothing
                            Else
                                EncryptionKey = newkey
                            End If
                        End If
                        TapeUtils.SetEncryption(driveHandle, EncryptionKey)
                    End If
                    TapeUtils.Locate(driveHandle, 0UL, Math.Min(ExtraPartitionCount, IndexPartition), TapeUtils.LocateDestType.Block)
                    PrintMsg($"Position = {GetPos.ToString()}", LogOnly:=True)
                    Dim senseData As Byte() = Nothing
                    Dim header As String = Encoding.ASCII.GetString(TapeUtils.ReadBlock(driveHandle, senseData))
                    PrintMsg($"Position = {GetPos.ToString()}", LogOnly:=True)
                    Dim VOL1LabelLegal As Boolean = False
                    VOL1LabelLegal = (header.Length = 80)
                    If VOL1LabelLegal Then VOL1LabelLegal = header.StartsWith("VOL1")
                    If VOL1LabelLegal Then VOL1LabelLegal = (header.Substring(24, 4) = "LTFS")
                    If Not VOL1LabelLegal Then
                        Dim Add_Key As UInt16
                        If senseData.Length >= 14 Then Add_Key = CUShort(CInt(senseData(12)) << 8 Or senseData(13))
                        PrintMsg(My.Resources.ResText_NVOL1)
                        Invoke(Sub() MessageBox.Show(New Form With {.TopMost = True}, $"{My.Resources.ResText_NLTFS}{vbCrLf}{TapeUtils.ParseSenseData(senseData)}", My.Resources.ResText_Error))
                        LockGUI(False)
                        SetStatusLight(LWStatus.NotReady)
                        Exit Try
                    End If
                    TapeUtils.Locate(driveHandle, 1UL, Math.Min(ExtraPartitionCount, IndexPartition), TapeUtils.LocateDestType.FileMark)
                    PrintMsg(My.Resources.ResText_RLTFSInfo)
                    PrintMsg($"Position = {GetPos.ToString()}", LogOnly:=True)
                    TapeUtils.ReadFileMark(driveHandle)
                    PrintMsg($"Position = {GetPos.ToString()}", LogOnly:=True)
                    Dim pltext As String = Encoding.UTF8.GetString(TapeUtils.ReadToFileMark(driveHandle))
                    plabel = ltfslabel.FromXML(pltext)
                    TapeUtils.SetBlockSize(driveHandle, plabel.blocksize)
                    If plabel.location.partition = plabel.partitions.data Then
                        DataPartition = GetPos().PartitionNumber
                        IndexPartition = CByte((DataPartition + 1) Mod 2)
                        If ExtraPartitionCount > 0 Then
                            IndexPartition = 255
                            PrintMsg($"Data partition detected. Switching to index partition", LogOnly:=True)
                            TapeUtils.Locate(driveHandle, 1UL, IndexPartition, TapeUtils.LocateDestType.FileMark)
                            PrintMsg(My.Resources.ResText_RLTFSInfo)
                            PrintMsg($"Position = {GetPos.ToString()}", LogOnly:=True)
                            TapeUtils.ReadFileMark(driveHandle)
                            PrintMsg($"Position = {GetPos.ToString()}", LogOnly:=True)
                            pltext = Encoding.UTF8.GetString(TapeUtils.ReadToFileMark(driveHandle))
                            plabel = ltfslabel.FromXML(pltext)
                        End If
                    Else
                        IndexPartition = GetPos().PartitionNumber
                        DataPartition = CByte((IndexPartition + 1) Mod 2)
                    End If

                    Barcode = TapeUtils.ReadBarcode(driveHandle)
                    If Barcode Is Nothing OrElse Barcode = "" Then
                        Barcode = header.Substring(4, 6).TrimEnd(" "c) & GenAbbr
                    End If
                    PrintMsg($"Barcode = {Barcode}", LogOnly:=True)
                    PrintMsg(My.Resources.ResText_Locating)
                    If ExtraPartitionCount = 0 Then
                        IndexPartition = 0
                        TapeUtils.Locate(driveHandle, 0UL, CByte(0), TapeUtils.LocateDestType.EOD)
                        PrintMsg($"Position = {GetPos.ToString()}", LogOnly:=True)
                        PrintMsg(My.Resources.ResText_RI)
                        If DisablePartition Then
                            TapeUtils.Space6(driveHandle, -2, TapeUtils.LocateDestType.FileMark)
                        Else
                            Dim p As TapeUtils.PositionData = GetPos
                            Dim FM As ULong = p.FileNumber
                            PrintMsg($"Position = {p.ToString()}", LogOnly:=True)
                            If FM <= 1 Then
                                PrintMsg(My.Resources.ResText_IRFailed)
                                Invoke(Sub() MessageBox.Show(New Form With {.TopMost = True}, My.Resources.ResText_NLTFS, My.Resources.ResText_Error))
                                SetStatusLight(LWStatus.Err)
                                LockGUI(False)
                                Exit Try
                            End If
                            TapeUtils.Locate(driveHandle, CULng(FM - 1), CByte(0), TapeUtils.LocateDestType.FileMark)
                        End If
                        PrintMsg($"Position = {GetPos.ToString()}", LogOnly:=True)
                        TapeUtils.ReadFileMark(driveHandle)
                    Else
                        TapeUtils.Locate(driveHandle, 3UL, IndexPartition, TapeUtils.LocateDestType.FileMark)
                        PrintMsg($"Position = {GetPos.ToString()}", LogOnly:=True)
                        TapeUtils.ReadFileMark(driveHandle)
                    End If
                    PrintMsg(My.Resources.ResText_RI)
                    PrintMsg($"Position = {GetPos.ToString()}", LogOnly:=True)
                    Dim tmpf As String = LazySchemaStore.CreateTempFilePath("schema-read")

                    TapeUtils.ReadToFileMark(driveHandle, tmpf, plabel.blocksize)
                    PrintMsg($"Position = {GetPos.ToString()}", LogOnly:=True)
                    PrintMsg(My.Resources.ResText_AI)
                    schema = ltfsindex.FromSchFile(tmpf)
                    If ExtraPartitionCount = 0 Then
                        Dim p As TapeUtils.PositionData = GetPos
                        PrintMsg($"Position = {p.ToString()}", LogOnly:=True)
                        CurrentHeight = CLng(p.BlockNumber)
                    Else
                        CurrentHeight = -1
                    End If
                    PrintMsg(My.Resources.ResText_SvBak)
                    Dim baseBarcode As String = If(String.IsNullOrWhiteSpace(Barcode), "Unknown", Barcode.Trim())
                    Dim targetDir As String = IO.Path.Combine(My.Settings.schemaPath, baseBarcode, "Load")
                    If Not IO.Directory.Exists(targetDir) Then IO.Directory.CreateDirectory(targetDir)
                    Dim fileName As String = ""
                    If Barcode <> "" Then
                        fileName = Barcode
                    ElseIf schema IsNot Nothing Then
                        fileName = schema.volumeuuid.ToString()
                    End If
                    Dim latestSchemaPath As String = IO.Path.Combine(targetDir, $"LTFSIndex_Load_{fileName}_{Now:yyyyMMdd_HHmmss.fffffff}.schema")
                    IO.File.Move(tmpf, latestSchemaPath)
                    Try
                        For Each oldSchema In IO.Directory.GetFiles(targetDir, "*.schema", IO.SearchOption.TopDirectoryOnly)
                            If String.Equals(oldSchema, latestSchemaPath, StringComparison.OrdinalIgnoreCase) Then Continue For

                            Dim zstPath As String = oldSchema & ".zst"

                            Using inFs As New IO.FileStream(oldSchema, IO.FileMode.Open, IO.FileAccess.Read, IO.FileShare.Read)
                                Using outFs As New IO.FileStream(zstPath, IO.FileMode.Create, IO.FileAccess.Write, IO.FileShare.None)
                                    Using zs As New ZstdSharp.CompressionStream(outFs, 9)
                                        inFs.CopyTo(zs)
                                    End Using
                                End Using
                            End Using

                            IO.File.Delete(oldSchema)
                        Next
                    Catch
                        PrintMsg("ZSTD compress error", LogOnly:=True)
                    End Try
                    While True
                        Threading.Thread.Sleep(0)
                        SyncLock UFReadCount
                            If UFReadCount > 0 Then Continue While
                            ClearGlobalPendingQueue()
                            CurrentFilesProcessed = 0
                            CurrentBytesProcessed = 0
                            TotalBytesUnindexed = 0
                            Exit While
                        End SyncLock
                    End While
                    Modified = False
                    Try
                        Dim Loc As String = GetLocInfo()
                        Invoke(Sub() Text = Loc)
                    Catch ex As Exception
                    End Try
                    Me.Invoke(Sub()
                                  MaxCapacity = 0
                                  ToolStripStatusLabel1.Text = Barcode.TrimEnd(" "c)
                                  ToolStripStatusLabel1.ToolTipText = $"{My.Resources.ResText_Barcode}:{ToolStripStatusLabel1.Text}{vbCrLf}{My.Resources.ResText_BlkSize}:{plabel.blocksize}"
                              End Sub)
                    RefreshDisplay()
                    RefreshCapacity()

                    PrintMsg(My.Resources.ResText_IRSucc)
                    SetStatusLight(LWStatus.Succ)
                    LockGUI(False)
                    Invoke(Sub() RaiseEvent LTFSLoaded())
                Catch ex As Exception
                    PrintMsg(My.Resources.ResText_IRFailed)
                    PrintMsg($"{ex.ToString}", LogOnly:=True)
                    SetStatusLight(LWStatus.Err)
                    LockGUI(False)
                End Try
            End Sub)
        LockGUI()
        th.Start()
    End Sub
    Private Sub 读取数据区索引ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 读取数据区索引ToolStripMenuItem.Click
        If ExtraPartitionCount = 0 Then
            读取索引ToolStripMenuItem_Click(sender, e)
            Exit Sub
        End If
        Dim th As New Threading.Thread(
            Sub()
                Try
                    If Not TapeUtils.IsOpened(driveHandle) Then TapeUtils.OpenTapeDrive(TapeDrive, driveHandle)
                    SetStatusLight(LWStatus.Busy)
                    PrintMsg(My.Resources.ResText_Locating)
                    Dim currentPos As TapeUtils.PositionData = GetPos
                    PrintMsg($"Position = {currentPos.ToString()}", LogOnly:=True)
                    If currentPos.PartitionNumber <> 1 Then TapeUtils.Locate(driveHandle, 0UL, CByte(1), TapeUtils.LocateDestType.Block)
                    TapeUtils.Locate(driveHandle, 0UL, DataPartition, TapeUtils.LocateDestType.EOD)
                    PrintMsg(My.Resources.ResText_RI)
                    currentPos = GetPos
                    PrintMsg($"Position = {currentPos.ToString()}", LogOnly:=True)
                    If DisablePartition Then
                        TapeUtils.Space6(driveHandle, -2, TapeUtils.LocateDestType.FileMark)
                    Else
                        Dim FM As Long = CLng(currentPos.FileNumber)
                        If FM <= 1 Then
                            PrintMsg(My.Resources.ResText_IRFailed)
                            SetStatusLight(LWStatus.Err)
                            Invoke(Sub() MessageBox.Show(New Form With {.TopMost = True}, My.Resources.ResText_NLTFS, My.Resources.ResText_Error))
                            LockGUI(False)
                            Exit Try
                        End If
                        TapeUtils.Locate(handle:=driveHandle, BlockAddress:=CULng(FM - 1), Partition:=DataPartition, DestType:=TapeUtils.LocateDestType.FileMark)
                    End If

                    TapeUtils.ReadFileMark(driveHandle)
                    PrintMsg(My.Resources.ResText_RI)
                    currentPos = GetPos
                    Dim outDir As String = IO.Path.Combine(My.Settings.schemaPath, "LoadDP")
                    If Not IO.Directory.Exists(outDir) Then IO.Directory.CreateDirectory(outDir)
                    Dim outputfile As String = IO.Path.Combine(outDir, $"LTFSIndex_LoadDPIndex_p{currentPos.PartitionNumber}_b{currentPos.BlockNumber}_{Now:yyyyMMdd_HHmmss.fffffff}.schema")
                    TapeUtils.ReadToFileMark(driveHandle, outputfile, plabel.blocksize)
                    PrintMsg(My.Resources.ResText_AI)
                    schema = ltfsindex.FromSchFile(outputfile)
                    PrintMsg(My.Resources.ResText_AISucc)

                    Try
                        Dim targetDir As String = outDir
                        Dim allSchemas = IO.Directory.GetFiles(targetDir, "*.schema", IO.SearchOption.TopDirectoryOnly)
                        If allSchemas IsNot Nothing AndAlso allSchemas.Length > 0 Then
                            Dim latestSchemaPath As String =
                                    allSchemas.OrderByDescending(Function(p) IO.File.GetLastWriteTimeUtc(p)).First()

                            For Each oldSchema In allSchemas
                                If String.Equals(oldSchema, latestSchemaPath, StringComparison.OrdinalIgnoreCase) Then Continue For

                                Dim zstPath As String = oldSchema & ".zst"

                                If Not IO.File.Exists(zstPath) Then
                                    Using inFs As New IO.FileStream(oldSchema, IO.FileMode.Open, IO.FileAccess.Read, IO.FileShare.Read)
                                        Using outFs As New IO.FileStream(zstPath, IO.FileMode.Create, IO.FileAccess.Write, IO.FileShare.None)
                                            Using zs As New ZstdSharp.CompressionStream(outFs, 9)
                                                inFs.CopyTo(zs)
                                            End Using
                                        End Using
                                    End Using
                                End If

                                Try : IO.File.Delete(oldSchema) : Catch : End Try
                            Next
                        End If
                    Catch
                    End Try

                    While True
                        Threading.Thread.Sleep(0)
                        SyncLock UFReadCount
                            If UFReadCount > 0 Then Continue While
                            ClearGlobalPendingQueue()
                            CurrentFilesProcessed = 0
                            CurrentBytesProcessed = 0
                            TotalBytesUnindexed = 0
                            Exit While
                        End SyncLock
                    End While
                    Modified = False
                    Me.Invoke(Sub()
                                  MaxCapacity = 0
                                  ToolStripStatusLabel1.ToolTipText = $"{My.Resources.ResText_Barcode}:{ToolStripStatusLabel1.Text}{vbCrLf}{My.Resources.ResText_BlkSize}:{plabel.blocksize}"
                              End Sub)
                    RefreshDisplay()
                    RefreshCapacity()
                    CurrentHeight = -1
                    PrintMsg(My.Resources.ResText_IRSucc)
                    SetStatusLight(LWStatus.Succ)
                Catch ex As Exception
                    PrintMsg(My.Resources.ResText_IRFailed)
                    SetStatusLight(LWStatus.Err)
                End Try
                LockGUI(False)
            End Sub)
        LockGUI()
        th.Start()
    End Sub
    Private Sub 更新数据区索引ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 更新数据区索引ToolStripMenuItem.Click
        Dim th As New Threading.Thread(
        Sub()
            Try
                SetStatusLight(LWStatus.Busy)
                If (My.Settings.LTFSWriter_ForceIndex OrElse TotalBytesUnindexed <> 0) AndAlso schema IsNot Nothing AndAlso schema.location.partition = ltfsindex.PartitionLabel.b Then
                    WriteCurrentIndex(False)
                    TapeUtils.Flush(driveHandle)
                    PrintMsg(My.Resources.ResText_DPIWritten)
                    SetStatusLight(LWStatus.Succ)
                End If
            Catch ex As Exception
                PrintMsg(My.Resources.ResText_DPIWFailed, False, $"{My.Resources.ResText_DPIWFailed}: {ex.ToString}")
                Invoke(Sub() MessageBox.Show(New Form With {.TopMost = True}, $"{ex.ToString()}{vbCrLf}{ex.StackTrace}"))
                SetStatusLight(LWStatus.Err)
            End Try
            Invoke(Sub()
                       LockGUI(False)
                       RefreshDisplay()
                       If Not SilentMode Then MessageBox.Show(New Form With {.TopMost = True}, My.Resources.ResText_DPIUed)
                   End Sub)
        End Sub)
        LockGUI()
        th.Start()
    End Sub
    Public Sub LoadIndexFile(FileName As String, Optional ByVal Silent As Boolean = False)
        Try
            PrintMsg(My.Resources.ResText_RI)
            PrintMsg(My.Resources.ResText_AI)
            Dim sch2 As ltfsindex = ltfsindex.FromSchFile(FileName)
            PrintMsg(My.Resources.ResText_AISucc)
            If sch2 IsNot Nothing Then
                schema = sch2
            Else
                Throw New Exception
            End If
            While True
                Threading.Thread.Sleep(0)
                SyncLock UFReadCount
                    If UFReadCount > 0 Then Continue While
                    ClearGlobalPendingQueue()
                    CurrentFilesProcessed = 0
                    CurrentBytesProcessed = 0
                    Exit While
                End SyncLock
            End While
            ExtraPartitionCount = CByte(schema.location.partition)
            Dim stopflag As Boolean = False
            ShowXAttr_Barcode = False
            ltfsindex.WSort(schema._directory, Sub(f As ltfsindex.file)
                                                   If f.GetXAttr("Barcode", True).Length > 0 Then
                                                       stopflag = True
                                                       ShowXAttr_Barcode = True
                                                   End If
                                               End Sub, Nothing, stopflag)
            RefreshDisplay()
            Modified = False
            Dim MAM080C As TapeUtils.MAMAttribute = TapeUtils.MAMAttribute.FromTapeDrive(driveHandle, 8, 12, 0)
            Dim VCI As Byte() = {}
            If MAM080C IsNot Nothing Then
                VCI = MAM080C.RawData
            End If
            If Not Silent Then Invoke(Sub() MessageBox.Show(New Form With {.TopMost = True}, $"{My.Resources.ResText_ILdedP}{vbCrLf}{vbCrLf}{My.Resources.ResText_VCID}{vbCrLf}{TapeUtils.Byte2Hex(VCI, True)}"))
        Catch ex As Exception
            Invoke(Sub() MessageBox.Show(New Form With {.TopMost = True}, $"{My.Resources.ResText_IAErrp}{ex.Message}"))
            SetStatusLight(LWStatus.Err)
        End Try
    End Sub
    Private Sub 加载外部索引ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 加载外部索引ToolStripMenuItem.Click
        If OpenFileDialog1.ShowDialog = DialogResult.OK Then
            LoadIndexFile(OpenFileDialog1.FileName)
        End If
    End Sub

    Public Function AutoDump() As String
        SetStatusLight(LWStatus.Busy)
        Dim id As String = If(String.IsNullOrWhiteSpace(Barcode), schema.volumeuuid.ToString(), Barcode)

        Dim invalid = IO.Path.GetInvalidFileNameChars()
        Dim sbName As New StringBuilder(id.Length)
        For Each ch In id
            If Array.IndexOf(invalid, ch) >= 0 Then
                sbName.Append("_"c)
            Else
                sbName.Append(ch)
            End If
        Next
        Dim safeId As String = sbName.ToString()

        Dim subDir As String = IO.Path.Combine(My.Settings.schemaPath, safeId)
        If Not IO.Directory.Exists(subDir) Then
            IO.Directory.CreateDirectory(subDir)
        End If

        Dim stamp As String = Now.ToString("yyyyMMdd_HHmmss.fffffff")
        Dim stem As String = $"LTFSIndex_Autosave_{safeId}_GEN{schema.generationnumber}_P{schema.location.partition}_B{schema.location.startblock}_{stamp}"

        Dim schemaTxtPath As String = IO.Path.Combine(subDir, stem & ".schema")
        Dim cmPath As String = IO.Path.Combine(subDir, stem & ".cm")
        Dim dumpFailed As Boolean = False

        PrintMsg(My.Resources.ResText_Exporting)
        If Not schema.SaveFile(schemaTxtPath) Then
            SetStatusLight(LWStatus.Err)
            PrintMsg("索引自动备份失败：无法保存索引文件。", Warning:=True, ForceLog:=True)
            Return Nothing
        End If

        Try
            Dim cmData As New TapeUtils.CMParser(driveHandle)
            Dim CMReport As String = cmData.GetReport()
            If CMReport.Length > 0 Then
                IO.File.WriteAllText(cmPath, CMReport, New UTF8Encoding(False))
            End If
        Catch ex As Exception
            dumpFailed = True
            SetStatusLight(LWStatus.Err)
            PrintMsg($"保存容量报告失败：{ex.Message}", Warning:=True, ForceLog:=True)
        End Try

        Try
            Dim allSchemas = IO.Directory.EnumerateFiles(subDir, "*.schema", IO.SearchOption.TopDirectoryOnly) _
                                          .OrderByDescending(Function(p) New IO.FileInfo(p).LastWriteTimeUtc) _
                                          .ToList()

            Dim newest As String = Nothing
            If allSchemas.Count > 0 Then
                newest = allSchemas(0)
            End If

            Const zstdLevel As Integer = 6
            Const copyBuf As Integer = 1 << 20

            For Each oldSchema In allSchemas
                If String.Equals(oldSchema, newest, StringComparison.OrdinalIgnoreCase) Then
                    Continue For
                End If

                Dim zstPath As String = oldSchema & ".zst" ' *.schema.zst
                Using fin As New IO.FileStream(oldSchema, IO.FileMode.Open, IO.FileAccess.Read, IO.FileShare.Read, copyBuf, IO.FileOptions.SequentialScan)
                    Using fout As New IO.FileStream(zstPath, IO.FileMode.Create, IO.FileAccess.Write, IO.FileShare.None, copyBuf, IO.FileOptions.SequentialScan)
                        Using zs As New CompressionStream(fout, zstdLevel)
                            fin.CopyTo(zs, copyBuf)
                        End Using
                    End Using
                End Using

                Try
                    IO.File.Delete(oldSchema)
                Catch
                End Try
            Next
        Catch ex As Exception
            dumpFailed = True
            SetStatusLight(LWStatus.Err)
            PrintMsg(My.Resources.ResText_Error, True, "ZSTD compress error")
        End Try

        If dumpFailed Then
            PrintMsg($"索引备份已生成，但部分附加数据失败：{schemaTxtPath}", Warning:=True, ForceLog:=True)
            SetStatusLight(LWStatus.Err)
        Else
            PrintMsg(My.Resources.ResText_IndexBaked, False, $"{My.Resources.ResText_IndexBak2}{vbCrLf}{schemaTxtPath}")
            SetStatusLight(LWStatus.Succ)
        End If

        Return schemaTxtPath
    End Function

    Private Sub 备份当前索引ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 备份当前索引ToolStripMenuItem.Click
        Dim th As New Threading.Thread(
            Sub()
                Try
                    SetStatusLight(LWStatus.Busy)
                    Dim outputfile As String = AutoDump()
                    If Not String.IsNullOrEmpty(outputfile) Then
                        Invoke(Sub() MessageBox.Show(New Form With {.TopMost = True}, $"{My.Resources.ResText_IndexBak2}{vbCrLf}{outputfile}"))
                    End If
                Catch ex As Exception
                    PrintMsg(My.Resources.ResText_IndexBakF)
                    SetStatusLight(LWStatus.Err)
                End Try
                LockGUI(False)
            End Sub)
        LockGUI()
        th.Start()
    End Sub
    Private Sub 格式化ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 格式化ToolStripMenuItem.Click
        If MessageBox.Show(New Form With {.TopMost = True}, My.Resources.ResText_DataLossWarning, My.Resources.ResText_Warning, MessageBoxButtons.OKCancel) = DialogResult.OK Then
            While True
                Threading.Thread.Sleep(0)
                SyncLock UFReadCount
                    If UFReadCount > 0 Then Continue While
                    ClearGlobalPendingQueue()
                    CurrentFilesProcessed = 0
                    CurrentBytesProcessed = 0
                    Exit While
                End SyncLock
            End While
            LockGUI()
            Task.Run(
                Sub()
                    'nop
                    If Not TapeUtils.IsOpened(driveHandle) Then TapeUtils.OpenTapeDrive(TapeDrive, driveHandle)
                    TapeUtils.ReadPosition(driveHandle)
                    Dim modedata As Byte() = TapeUtils.ModeSense(driveHandle, &H11)
                    Dim MaxExtraPartitionAllowed As Byte = 0
                    If modedata IsNot Nothing AndAlso modedata.Length >= 3 Then MaxExtraPartitionAllowed = modedata(2)
                    If TapeUtils.DriverTypeSetting = TapeUtils.DriverType.TapeStream Then
                        MaxExtraPartitionAllowed = 1
                    End If
                    If MaxExtraPartitionAllowed > 1 Then MaxExtraPartitionAllowed = 1
                    Dim param As New TapeUtils.MKLTFS_Param(MaxExtraPartitionAllowed)
                    If My.Settings.LTFSWriter_DisablePartition Then
                        param.ExtraPartitionCount = 0
                        TapeUtils.AllowPartition = False
                    Else
                        TapeUtils.AllowPartition = True
                    End If
                    If param.MaxExtraPartitionAllowed = 0 Then
                        If My.Settings.TapeUtils_DriverType = TapeUtils.DriverType.LTO Then
                            param.BlockLen = 65536
                        End If
                    End If
                    param.BlockLen = Math.Min(param.BlockLen, TapeUtils.GlobalBlockLimit)
                    param.Barcode = TapeUtils.ReadBarcode(driveHandle)
                    param.EncryptionKey = EncryptionKey
                    Dim Confirm As Boolean = False
                    Invoke(Sub()
                               Dim msDialog As New SettingPanel With {.SelectedObject = param, .StartPosition = FormStartPosition.Manual, .TopMost = True, .Text = $"{格式化ToolStripMenuItem.Text} - {My.Resources.ResText_Setting}"}
                               msDialog.Top = CInt(Me.Top + Me.Height / 2 - msDialog.Height / 2)
                               msDialog.Left = CInt(Me.Left + Me.Width / 2 - msDialog.Width / 2)
                               While Not Confirm
                                   If param.VolumeLabel = "" Then param.VolumeLabel = param.Barcode
                                   If msDialog.ShowDialog() = DialogResult.Cancel Then
                                       LockGUI(False)
                                       Exit Sub
                                   End If
                                   Select Case MessageBox.Show(New Form With {.TopMost = True}, $"{My.Resources.ResText_Barcode2}{param.Barcode}{vbCrLf}{My.Resources.ResText_LTFSVolumeN2}{param.VolumeLabel}", My.Resources.ResText_Confirm, MessageBoxButtons.YesNoCancel)
                                       Case DialogResult.Yes
                                           Confirm = True
                                           Exit While
                                       Case DialogResult.No
                                           Confirm = False
                                       Case DialogResult.Cancel
                                           LockGUI(False)
                                           Exit Sub
                                   End Select
                               End While
                               SetStatusLight(LWStatus.Busy)
                               TapeUtils.mkltfs(driveHandle, param.Barcode, param.VolumeLabel, param.ExtraPartitionCount, param.BlockLen, False,
                                        Sub(Message As String)
                                            'ProgressReport
                                            PrintMsg(Message)
                                            If Message.Contains("error") Then Invoke(Sub() MessageBox.Show(New Form With {.TopMost = True}, Message))
                                        End Sub,
                                        Sub(Message As String)
                                            'OnFinished
                                            PrintMsg(My.Resources.ResText_FmtFin)
                                            SetStatusLight(LWStatus.Succ)
                                            LockGUI(False)
                                            Me.Invoke(Sub()
                                                          MessageBox.Show(New Form With {.TopMost = True}, $"{My.Resources.ResText_FmtFin}")
                                                          读取索引ToolStripMenuItem_Click(sender, e)
                                                      End Sub)
                                        End Sub,
                                        Sub(Message As String)
                                            'OnError
                                            PrintMsg(Message)
                                            SetStatusLight(LWStatus.Err)
                                            LockGUI(False)
                                            Me.Invoke(Sub() MessageBox.Show(New Form With {.TopMost = True}, $"{My.Resources.ResText_FmtFail}{vbCrLf}{Message}"))
                                        End Sub, param.Capacity, param.P0Size, param.P1Size, param.EncryptionKey, param.WORMMode)
                           End Sub)
                End Sub)
        End If
    End Sub
    Public Function ImportSHA1(schhash As ltfsindex, Overwrite As Boolean) As String
        SetStatusLight(LWStatus.Busy)
        Dim fprocessed As Integer = 0, fhash As Integer = 0
        Dim q As New Stack(Of IOManager.IndexedLHashDirectory)
        q.Push(New IOManager.IndexedLHashDirectory(schema._directory(0), schhash._directory(0)))
        While q.Count > 0
            Dim d As IOManager.IndexedLHashDirectory = q.Pop()
            For Each f As ltfsindex.file In d.LTFSIndexDir.EnumerateLazyFiles()
                Try
                    Dim crc32value0 As String = f.GetXAttr(ltfsindex.file.xattr.HashType.CRC32)
                    Dim sha1value0 As String = f.GetXAttr(ltfsindex.file.xattr.HashType.SHA1)
                    Dim sha256value0 As String = f.GetXAttr(ltfsindex.file.xattr.HashType.SHA256)
                    Dim sha512value0 As String = f.GetXAttr(ltfsindex.file.xattr.HashType.SHA512)
                    Dim md5value0 As String = f.GetXAttr(ltfsindex.file.xattr.HashType.MD5)
                    Dim blake3value0 As String = f.GetXAttr(ltfsindex.file.xattr.HashType.BLAKE3)
                    Dim xxhash3value0 As String = f.GetXAttr(ltfsindex.file.xattr.HashType.XxHash3)
                    Dim xxhash128value0 As String = f.GetXAttr(ltfsindex.file.xattr.HashType.XxHash128)
                    For Each flookup As ltfsindex.file In d.LHash_Dir.EnumerateLazyFiles()
                        If flookup.name = f.name And flookup.length = f.length Then
                            If Not Overwrite Then
                                If Not (crc32value0 IsNot Nothing AndAlso crc32value0 <> "" AndAlso crc32value0.Length = ltfsindex.file.xattr.HashLengthBytes.CRC32 * 2) Then
                                    PrintMsg($"{f.name}", False, $"[CRC32]{f.name}    {crc32value0} -> { flookup.GetXAttr(ltfsindex.file.xattr.HashType.CRC32)}")
                                    f.SetXattr(ltfsindex.file.xattr.HashType.CRC32, flookup.GetXAttr(ltfsindex.file.xattr.HashType.CRC32))
                                End If
                                If Not (sha1value0 IsNot Nothing AndAlso sha1value0 <> "" AndAlso sha1value0.Length = ltfsindex.file.xattr.HashLengthBytes.SHA1 * 2) Then
                                    PrintMsg($"{f.name}", False, $"[SHA1]{f.name}    {sha1value0} -> { flookup.GetXAttr(ltfsindex.file.xattr.HashType.SHA1)}")
                                    f.SetXattr(ltfsindex.file.xattr.HashType.SHA1, flookup.GetXAttr(ltfsindex.file.xattr.HashType.SHA1))
                                End If
                                If Not (sha256value0 IsNot Nothing AndAlso sha256value0 <> "" AndAlso sha256value0.Length = ltfsindex.file.xattr.HashLengthBytes.SHA256 * 2) Then
                                    PrintMsg($"{f.name}", False, $"[SHA256]{f.name}    {sha256value0} -> { flookup.GetXAttr(ltfsindex.file.xattr.HashType.SHA256)}")
                                    f.SetXattr(ltfsindex.file.xattr.HashType.SHA256, flookup.GetXAttr(ltfsindex.file.xattr.HashType.SHA256))
                                End If
                                If Not (sha512value0 IsNot Nothing AndAlso sha512value0 <> "" AndAlso sha512value0.Length = ltfsindex.file.xattr.HashLengthBytes.SHA512 * 2) Then
                                    PrintMsg($"{f.name}", False, $"[SHA512]{f.name}    {sha512value0} -> { flookup.GetXAttr(ltfsindex.file.xattr.HashType.SHA512)}")
                                    f.SetXattr(ltfsindex.file.xattr.HashType.SHA512, flookup.GetXAttr(ltfsindex.file.xattr.HashType.SHA512))
                                End If
                                If Not (md5value0 IsNot Nothing AndAlso md5value0 <> "" AndAlso md5value0.Length = ltfsindex.file.xattr.HashLengthBytes.MD5 * 2) Then
                                    PrintMsg($"{f.name}", False, $"[MD5]{f.name}    {md5value0} -> { flookup.GetXAttr(ltfsindex.file.xattr.HashType.MD5)}")
                                    f.SetXattr(ltfsindex.file.xattr.HashType.MD5, flookup.GetXAttr(ltfsindex.file.xattr.HashType.MD5))
                                End If
                                If Not (blake3value0 IsNot Nothing AndAlso blake3value0 <> "" AndAlso blake3value0.Length = ltfsindex.file.xattr.HashLengthBytes.BLAKE3 * 2) Then
                                    PrintMsg($"{f.name}", False, $"[Blake3]{f.name}    {blake3value0} -> { flookup.GetXAttr(ltfsindex.file.xattr.HashType.BLAKE3)}")
                                    f.SetXattr(ltfsindex.file.xattr.HashType.BLAKE3, flookup.GetXAttr(ltfsindex.file.xattr.HashType.BLAKE3))
                                End If
                                If Not (xxhash3value0 IsNot Nothing AndAlso xxhash3value0 <> "" AndAlso xxhash3value0.Length = ltfsindex.file.xattr.HashLengthBytes.XxHash3 * 2) Then
                                    PrintMsg($"{f.name}", False, $"[XxHash3]{f.name}    {xxhash3value0} -> { flookup.GetXAttr(ltfsindex.file.xattr.HashType.XxHash3)}")
                                    f.SetXattr(ltfsindex.file.xattr.HashType.XxHash3, flookup.GetXAttr(ltfsindex.file.xattr.HashType.XxHash3))
                                End If
                                If Not (xxhash128value0 IsNot Nothing AndAlso xxhash128value0 <> "" AndAlso xxhash128value0.Length = ltfsindex.file.xattr.HashLengthBytes.XxHash128 * 2) Then
                                    PrintMsg($"{f.name}", False, $"[XxHash128]{f.name}    {xxhash128value0} -> { flookup.GetXAttr(ltfsindex.file.xattr.HashType.XxHash128)}")
                                    f.SetXattr(ltfsindex.file.xattr.HashType.XxHash128, flookup.GetXAttr(ltfsindex.file.xattr.HashType.XxHash128))
                                End If
                                For Each xt As ltfsindex.file.xattr In flookup.extendedattributes
                                    If xt.key.StartsWith("ltfs.hash.") Then Continue For
                                    Dim value As String = f.GetXAttr(xt.key)
                                    If value IsNot Nothing OrElse value = "" Then
                                        f.SetXattr(xt.key, xt.value)
                                    End If
                                Next
                            Else
                                If flookup.GetXAttr(ltfsindex.file.xattr.HashType.SHA1) IsNot Nothing AndAlso flookup.GetXAttr(ltfsindex.file.xattr.HashType.SHA1) <> "" And flookup.GetXAttr(ltfsindex.file.xattr.HashType.SHA1).Length = 2 * ltfsindex.file.xattr.HashLengthBytes.SHA1 Then
                                    PrintMsg($"{f.name}", False, $"[SHA1]{f.name}    {sha1value0} -> { flookup.GetXAttr(ltfsindex.file.xattr.HashType.SHA1)}")
                                    f.SetXattr(ltfsindex.file.xattr.HashType.SHA1, flookup.GetXAttr(ltfsindex.file.xattr.HashType.SHA1))
                                End If
                                If flookup.GetXAttr(ltfsindex.file.xattr.HashType.SHA256) IsNot Nothing AndAlso flookup.GetXAttr(ltfsindex.file.xattr.HashType.SHA256) <> "" And flookup.GetXAttr(ltfsindex.file.xattr.HashType.SHA256).Length = 2 * ltfsindex.file.xattr.HashLengthBytes.SHA256 Then
                                    PrintMsg($"{f.name}", False, $"[SHA256]{f.name}    {sha256value0} -> { flookup.GetXAttr(ltfsindex.file.xattr.HashType.SHA256)}")
                                    f.SetXattr(ltfsindex.file.xattr.HashType.SHA256, flookup.GetXAttr(ltfsindex.file.xattr.HashType.SHA256))
                                End If
                                If flookup.GetXAttr(ltfsindex.file.xattr.HashType.SHA512) IsNot Nothing AndAlso flookup.GetXAttr(ltfsindex.file.xattr.HashType.SHA512) <> "" And flookup.GetXAttr(ltfsindex.file.xattr.HashType.SHA512).Length = 2 * ltfsindex.file.xattr.HashLengthBytes.SHA512 Then
                                    PrintMsg($"{f.name}", False, $"[SHA512]{f.name}    {sha512value0} -> { flookup.GetXAttr(ltfsindex.file.xattr.HashType.SHA512)}")
                                    f.SetXattr(ltfsindex.file.xattr.HashType.SHA512, flookup.GetXAttr(ltfsindex.file.xattr.HashType.SHA512))
                                End If
                                If flookup.GetXAttr(ltfsindex.file.xattr.HashType.CRC32) IsNot Nothing AndAlso flookup.GetXAttr(ltfsindex.file.xattr.HashType.CRC32) <> "" And flookup.GetXAttr(ltfsindex.file.xattr.HashType.CRC32).Length = 2 * ltfsindex.file.xattr.HashLengthBytes.CRC32 Then
                                    PrintMsg($"{f.name}", False, $"[CRC32]{f.name}    {crc32value0} -> { flookup.GetXAttr(ltfsindex.file.xattr.HashType.CRC32)}")
                                    f.SetXattr(ltfsindex.file.xattr.HashType.CRC32, flookup.GetXAttr(ltfsindex.file.xattr.HashType.CRC32))
                                End If
                                If flookup.GetXAttr(ltfsindex.file.xattr.HashType.MD5) IsNot Nothing AndAlso flookup.GetXAttr(ltfsindex.file.xattr.HashType.MD5) <> "" And flookup.GetXAttr(ltfsindex.file.xattr.HashType.MD5).Length = 2 * ltfsindex.file.xattr.HashLengthBytes.MD5 Then
                                    PrintMsg($"{f.name}", False, $"[MD5]{f.name}    {md5value0} -> { flookup.GetXAttr(ltfsindex.file.xattr.HashType.MD5)}")
                                    f.SetXattr(ltfsindex.file.xattr.HashType.MD5, flookup.GetXAttr(ltfsindex.file.xattr.HashType.MD5))
                                End If
                                If flookup.GetXAttr(ltfsindex.file.xattr.HashType.BLAKE3) IsNot Nothing AndAlso flookup.GetXAttr(ltfsindex.file.xattr.HashType.BLAKE3) <> "" And flookup.GetXAttr(ltfsindex.file.xattr.HashType.BLAKE3).Length = 2 * ltfsindex.file.xattr.HashLengthBytes.BLAKE3 Then
                                    PrintMsg($"{f.name}", False, $"[Blake3]{f.name}    {blake3value0} -> { flookup.GetXAttr(ltfsindex.file.xattr.HashType.BLAKE3)}")
                                    f.SetXattr(ltfsindex.file.xattr.HashType.BLAKE3, flookup.GetXAttr(ltfsindex.file.xattr.HashType.BLAKE3))
                                End If
                                If flookup.GetXAttr(ltfsindex.file.xattr.HashType.XxHash3) IsNot Nothing AndAlso flookup.GetXAttr(ltfsindex.file.xattr.HashType.XxHash3) <> "" And flookup.GetXAttr(ltfsindex.file.xattr.HashType.XxHash3).Length = 2 * ltfsindex.file.xattr.HashLengthBytes.XxHash3 Then
                                    PrintMsg($"{f.name}", False, $"[XxHash3]{f.name}    {xxhash3value0} -> { flookup.GetXAttr(ltfsindex.file.xattr.HashType.XxHash3)}")
                                    f.SetXattr(ltfsindex.file.xattr.HashType.XxHash3, flookup.GetXAttr(ltfsindex.file.xattr.HashType.XxHash3))
                                End If
                                If flookup.GetXAttr(ltfsindex.file.xattr.HashType.XxHash128) IsNot Nothing AndAlso flookup.GetXAttr(ltfsindex.file.xattr.HashType.XxHash128) <> "" And flookup.GetXAttr(ltfsindex.file.xattr.HashType.XxHash128).Length = 2 * ltfsindex.file.xattr.HashLengthBytes.XxHash128 Then
                                    PrintMsg($"{f.name}", False, $"[XxHash128]{f.name}    {xxhash128value0} -> { flookup.GetXAttr(ltfsindex.file.xattr.HashType.XxHash128)}")
                                    f.SetXattr(ltfsindex.file.xattr.HashType.XxHash128, flookup.GetXAttr(ltfsindex.file.xattr.HashType.XxHash128))
                                End If
                                For Each xt As ltfsindex.file.xattr In flookup.extendedattributes
                                    If xt.key.StartsWith("ltfs.hash.") Then Continue For
                                    f.SetXattr(xt.key, xt.value)
                                Next
                            End If

                            Exit For
                        End If
                    Next
                    f.openforwrite = False
                    Threading.Interlocked.Increment(fprocessed)
                    Dim checksumlegal As Boolean = True
                    Dim illegalinfo As New StringBuilder
                    Dim crc32value1 As String = f.GetXAttr(ltfsindex.file.xattr.HashType.CRC32)
                    Dim sha1value1 As String = f.GetXAttr(ltfsindex.file.xattr.HashType.SHA1)
                    Dim sha256value1 As String = f.GetXAttr(ltfsindex.file.xattr.HashType.SHA256)
                    Dim sha512value1 As String = f.GetXAttr(ltfsindex.file.xattr.HashType.SHA512)
                    Dim md5value1 As String = f.GetXAttr(ltfsindex.file.xattr.HashType.MD5)
                    Dim blake3value1 As String = f.GetXAttr(ltfsindex.file.xattr.HashType.BLAKE3)
                    Dim xxhash3value1 As String = f.GetXAttr(ltfsindex.file.xattr.HashType.XxHash3)
                    Dim xxhash128value1 As String = f.GetXAttr(ltfsindex.file.xattr.HashType.XxHash128)
                    If crc32value1 IsNot Nothing AndAlso crc32value1.Length <> 2 * ltfsindex.file.xattr.HashLengthBytes.CRC32 AndAlso crc32value1.Length > 0 Then
                        checksumlegal = False
                    Else
                        illegalinfo.AppendLine($"{f.fileuid} CRC32:{d.LTFSIndexDir.name}\{f.name} {crc32value1}")
                    End If
                    If sha1value1 IsNot Nothing AndAlso sha1value1.Length <> 2 * ltfsindex.file.xattr.HashLengthBytes.SHA1 AndAlso sha1value1.Length > 0 Then
                        checksumlegal = False
                    Else
                        illegalinfo.AppendLine($"{f.fileuid} SHA1:{d.LTFSIndexDir.name}\{f.name} {sha1value1}")
                    End If
                    If sha256value1 IsNot Nothing AndAlso sha256value1.Length <> 2 * ltfsindex.file.xattr.HashLengthBytes.SHA256 AndAlso sha256value1.Length > 0 Then
                        checksumlegal = False
                    Else
                        illegalinfo.AppendLine($"{f.fileuid} SHA256:{d.LTFSIndexDir.name}\{f.name} {sha256value1}")
                    End If
                    If sha512value1 IsNot Nothing AndAlso sha512value1.Length <> 2 * ltfsindex.file.xattr.HashLengthBytes.SHA512 AndAlso sha512value1.Length > 0 Then
                        checksumlegal = False
                    Else
                        illegalinfo.AppendLine($"{f.fileuid} SHA512:{d.LTFSIndexDir.name}\{f.name} {sha512value1}")
                    End If
                    If md5value1 IsNot Nothing AndAlso md5value1.Length <> 2 * ltfsindex.file.xattr.HashLengthBytes.MD5 AndAlso md5value1.Length > 0 Then
                        checksumlegal = False
                    Else
                        illegalinfo.AppendLine($"{f.fileuid} MD5:{d.LTFSIndexDir.name}\{f.name} {md5value1}")
                    End If
                    If blake3value1 IsNot Nothing AndAlso blake3value1.Length <> 2 * ltfsindex.file.xattr.HashLengthBytes.BLAKE3 AndAlso blake3value1.Length > 0 Then
                        checksumlegal = False
                    Else
                        illegalinfo.AppendLine($"{f.fileuid} Blake3:{d.LTFSIndexDir.name}\{f.name} {blake3value1}")
                    End If
                    If xxhash3value1 IsNot Nothing AndAlso xxhash3value1.Length <> 2 * ltfsindex.file.xattr.HashLengthBytes.XxHash3 AndAlso xxhash3value1.Length > 0 Then
                        checksumlegal = False
                    Else
                        illegalinfo.AppendLine($"{f.fileuid} XxHash3:{d.LTFSIndexDir.name}\{f.name} {xxhash3value1}")
                    End If
                    If xxhash128value1 IsNot Nothing AndAlso xxhash128value1.Length <> 2 * ltfsindex.file.xattr.HashLengthBytes.XxHash128 AndAlso xxhash128value1.Length > 0 Then
                        checksumlegal = False
                    Else
                        illegalinfo.AppendLine($"{f.fileuid} XxHash128:{d.LTFSIndexDir.name}\{f.name} {xxhash128value1}")
                    End If
                    If checksumlegal Then
                        Threading.Interlocked.Increment(fhash)
                    ElseIf fprocessed - fhash <= 5 Then
                        Invoke(Sub() MessageBox.Show(New Form With {.TopMost = True}, illegalinfo.ToString()))
                    End If
                Catch ex As Exception
                    SetStatusLight(LWStatus.Err)
                    PrintMsg(ex.ToString)
                End Try
            Next
            For Each sd As ltfsindex.directory In d.LTFSIndexDir.EnumerateLazyDirectories()
                Dim dlookup As ltfsindex.directory = d.LHash_Dir.FindDirectoryByName(sd.name)
                If dlookup IsNot Nothing Then
                    q.Push(New IOManager.IndexedLHashDirectory(sd, dlookup))
                End If
            Next
        End While
        If TotalBytesUnindexed = 0 Then TotalBytesUnindexed = 1
        Return ($"{fhash}/{fprocessed}")
    End Function
    Private Sub 合并SHA1ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 合并SHA1ToolStripMenuItem.Click
        If OpenFileDialog1.ShowDialog = DialogResult.OK Then
            SetStatusLight(LWStatus.Busy)
            Try
                Dim schhash As ltfsindex
                PrintMsg(My.Resources.ResText_RI)
                schhash = ltfsindex.FromSchemaFile(OpenFileDialog1.FileName)
                Dim dr As DialogResult = MessageBox.Show(New Form With {.TopMost = True}, My.Resources.ResText_SHA1Overw, My.Resources.ResText_Hint, MessageBoxButtons.YesNoCancel)
                PrintMsg(My.Resources.ResText_Importing)
                Dim result As String = ""
                If dr = DialogResult.Yes Then
                    result = ImportSHA1(schhash, True)
                ElseIf dr = DialogResult.No Then
                    result = ImportSHA1(schhash, False)
                Else
                    PrintMsg(My.Resources.ResText_OpCancelled)
                    Exit Try
                End If
                RefreshDisplay()
                PrintMsg($"{My.Resources.ResText_Imported} {result}")
            Catch ex As Exception
                PrintMsg(ex.ToString)
                SetStatusLight(LWStatus.Err)
            End Try
            SetStatusLight(LWStatus.Succ)
        End If
    End Sub
    Private Sub 设置高度ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 设置高度ToolStripMenuItem.Click
        Dim p As TapeUtils.PositionData = GetPos
        PrintMsg($"Position = {p.ToString()}", LogOnly:=True)
        Dim Pos As Long = CLng(p.BlockNumber)
        If MessageBox.Show(New Form With {.TopMost = True}, $"{My.Resources.ResText_SetH1}{Pos}{My.Resources.ResText_SetH2}{vbCrLf}{My.Resources.ResText_SetH3}", My.Resources.ResText_Confirm, MessageBoxButtons.OKCancel) = DialogResult.OK Then
            CurrentHeight = Pos
            SetStatusLight(LWStatus.Idle)
        End If
    End Sub
    Private Sub 定位到起始块ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 定位到起始块ToolStripMenuItem.Click
        Dim selectedItems As List(Of ListViewItem) = GetSelectedListViewItems()
        If selectedItems.Count > 0 AndAlso
        selectedItems(0).Tag IsNot Nothing AndAlso
            TypeOf (selectedItems(0).Tag) Is ltfsindex.file Then

            Dim f As ltfsindex.file = DirectCast(selectedItems(0).Tag, ltfsindex.file)
            If f.extentinfo IsNot Nothing AndAlso f.extentinfo.Count > 0 Then
                Dim ext As ltfsindex.file.extent = f.extentinfo(0)
                Dim th As New Threading.Thread(
                        Sub()
                            PrintMsg($"Position = {GetPos.ToString()}", LogOnly:=True)
                            TapeUtils.Locate(handle:=driveHandle, BlockAddress:=CULng(ext.startblock), Partition:=GetPartitionNumber(CType(ext.partition, ltfslabel.PartitionLabel)), DestType:=TapeUtils.LocateDestType.Block)
                            PrintMsg($"Position = {GetPos.ToString()}", LogOnly:=True)
                            LockGUI(False)
                            Invoke(Sub() MessageBox.Show(New Form With {.TopMost = True}, $"{My.Resources.ResText_Located}{ext.startblock}"))
                            PrintMsg($"{My.Resources.ResText_Located}{ext.startblock}")
                            SetStatusLight(LWStatus.Idle)
                        End Sub)
                LockGUI()
                SetStatusLight(LWStatus.Busy)
                PrintMsg(My.Resources.ResText_Locating)
                th.Start()
            End If
        End If
    End Sub
    Private Sub S60ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles S60ToolStripMenuItem.Click
        SMaxNum = 60
        Chart1.XAxisTitle = S60ToolStripMenuItem.Text
        Chart1.VisibleSamples = SMaxNum
    End Sub
    Private Sub Min5ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles Min5ToolStripMenuItem.Click
        SMaxNum = 300
        Chart1.XAxisTitle = Min5ToolStripMenuItem.Text
        Chart1.VisibleSamples = SMaxNum
    End Sub
    Private Sub Min10ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles Min10ToolStripMenuItem.Click
        SMaxNum = 600
        Chart1.XAxisTitle = Min10ToolStripMenuItem.Text
        Chart1.VisibleSamples = SMaxNum
    End Sub
    Private Sub Min30ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles Min30ToolStripMenuItem.Click
        SMaxNum = 1800
        Chart1.XAxisTitle = Min30ToolStripMenuItem.Text
        Chart1.VisibleSamples = SMaxNum
    End Sub
    Private Sub H1ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles H1ToolStripMenuItem.Click
        SMaxNum = 3600
        Chart1.XAxisTitle = H1ToolStripMenuItem.Text
        Chart1.VisibleSamples = SMaxNum
    End Sub
    Private Sub H3ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles H3ToolStripMenuItem.Click
        SMaxNum = 3600 * 3
        Chart1.XAxisTitle = H3ToolStripMenuItem.Text
        Chart1.VisibleSamples = SMaxNum
    End Sub
    Private Sub H6ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles H6ToolStripMenuItem.Click
        SMaxNum = 3600 * 6
        Chart1.XAxisTitle = H6ToolStripMenuItem.Text
        Chart1.VisibleSamples = SMaxNum
    End Sub
    Public LRHistory As Double
    Public Function CheckFlush() As Boolean
        If Threading.Interlocked.Exchange(_flushState, 0) <> 0 Then
            PrintMsg("Flush Triggered", LogOnly:=True)
            Dim Loc As TapeUtils.PositionData = GetPos
            If Loc.EOP Then PrintMsg(My.Resources.ResText_EWEOM, True, DeDupe:=True)
            PrintMsg($"Position = {Loc.ToString()}", LogOnly:=True)
            Dim ChanLRValue As Double = ReadChanLRInfo(10000)
            PrintMsg($"ErrRateLogValue: {ChanLRValue}", LogOnly:=True)
            If ChanLRValue <> 0 Then LRHistory = ChanLRValue
            If (Not ForceFlush) AndAlso (ChanLRValue < My.Settings.LTFSWriter_AutoCleanErrRateLogThreashould) Then
                PrintMsg("Error rate log OK, ignore", LogOnly:=True)
                Return False
            ElseIf (LRHistory = 0) OrElse (ChanLRValue <> 0) Then
                Threading.Interlocked.Increment(CapReduceCount)
                TapeUtils.Flush(driveHandle)
                If Not ForceFlush Then
                    Threading.Thread.Sleep(My.Settings.LTFSWriter_AutoFlushCooldownMilliseconds)
                End If
                ForceFlush = False
                RefreshCapacity()
                Return True
            Else
                Return False
            End If
        Else
            Return False
        End If
    End Function
    Public Sub CheckClean(Optional ByVal LockVolume As Boolean = False)
        If Threading.Interlocked.Exchange(_cleanState, 0) <> 0 Then
            If (Now - Clean_last).TotalSeconds < 300 Then Exit Sub
            PrintMsg("Clean Triggered", LogOnly:=True)
            Clean_last = Now
            Dim Loc As TapeUtils.PositionData = GetPos
            If Loc.EOP Then PrintMsg(My.Resources.ResText_EWEOM, True, DeDupe:=True)
            PrintMsg($"Position = {Loc.ToString()}", LogOnly:=True)
            If Not Loc.EOP Then
                TapeUtils.DoReload(driveHandle, LockVolume, EncryptionKey)
            End If
            RefreshCapacity()
        End If
    End Sub
    Private Sub LinearToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles LinearToolStripMenuItem.Click
        Chart1.IsLogarithmic = False
        LinearToolStripMenuItem.Checked = True
        LogarithmicToolStripMenuItem.Checked = False
    End Sub
    Private Sub LogrithmToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles LogarithmicToolStripMenuItem.Click
        Chart1.IsLogarithmic = True
        LinearToolStripMenuItem.Checked = False
        LogarithmicToolStripMenuItem.Checked = True
    End Sub
    Private Sub ToolStripDropDownButton1_Click(sender As Object, e As EventArgs) Handles ToolStripDropDownButton1.Click
        Pause = True
        If MessageBox.Show(New Form With {.TopMost = True}, My.Resources.ResText_CancelConfirm, My.Resources.ResText_Warning, MessageBoxButtons.OKCancel) = DialogResult.OK Then
            StopFlag = True
        End If
        Pause = False
    End Sub
    Private Sub ToolStripDropDownButton2_Click(sender As Object, e As EventArgs) Handles ToolStripDropDownButton2.Click
        ForceFlush = True
        Flush = True
    End Sub
    Private Sub ToolStripDropDownButton3_Click(sender As Object, e As EventArgs) Handles ToolStripDropDownButton3.Click
        If Not AllowOperation Then
            Clean = True
        Else
            ToolStripDropDownButton3.Enabled = False
            Task.Run(Sub()
                         TapeUtils.DoReload(driveHandle, Not AllowOperation, EncryptionKey)
                         Invoke(Sub() ToolStripDropDownButton3.Enabled = True)
                     End Sub)
        End If


    End Sub
    Private Sub ToolStripStatusLabel4_Click(sender As Object, e As EventArgs) Handles ToolStripStatusLabel4.Click
        If MessageBox.Show(New Form With {.TopMost = True}, My.Resources.ResText_ClearWC, My.Resources.ResText_Confirm, MessageBoxButtons.OKCancel) = DialogResult.OK Then
            TotalBytesProcessed = 0
            TotalFilesProcessed = 0
            t_last = 0
            d_last = 0
        End If
    End Sub
    Private Sub WA0ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles WA0ToolStripMenuItem.Click
        WA0ToolStripMenuItem.Checked = True
        WA1ToolStripMenuItem.Checked = False
        WA2ToolStripMenuItem.Checked = False
        WA3ToolStripMenuItem.Checked = False
        ApplyWAStatus()
    End Sub
    Private Sub WA1ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles WA1ToolStripMenuItem.Click
        WA0ToolStripMenuItem.Checked = False
        WA1ToolStripMenuItem.Checked = True
        WA2ToolStripMenuItem.Checked = False
        WA3ToolStripMenuItem.Checked = False
        ApplyWAStatus()
    End Sub
    Private Sub WA2ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles WA2ToolStripMenuItem.Click
        WA0ToolStripMenuItem.Checked = False
        WA1ToolStripMenuItem.Checked = False
        WA2ToolStripMenuItem.Checked = True
        WA3ToolStripMenuItem.Checked = False
        ApplyWAStatus()
    End Sub
    Private Sub WA3ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles WA3ToolStripMenuItem.Click
        WA0ToolStripMenuItem.Checked = False
        WA1ToolStripMenuItem.Checked = False
        WA2ToolStripMenuItem.Checked = False
        WA3ToolStripMenuItem.Checked = True
        ApplyWAStatus()
    End Sub
    Public Sub ApplyWAStatus()
        If WA0ToolStripMenuItem.Checked Then
            My.Settings.LTFSWriter_OnWriteFinished = 0
        ElseIf WA1ToolStripMenuItem.Checked Then
            My.Settings.LTFSWriter_OnWriteFinished = 1
        ElseIf WA2ToolStripMenuItem.Checked Then
            My.Settings.LTFSWriter_OnWriteFinished = 2
        ElseIf WA3ToolStripMenuItem.Checked Then
            My.Settings.LTFSWriter_OnWriteFinished = 3
        End If

    End Sub
    Private Const ValidationSpoolBudgetBytes As Long = 256L * 1024L * 1024L

    Private Class ValidationWork
        Public Representative As FileRecord
        Public Members As New List(Of FileRecord)
        Public Extents As New List(Of ValidationExtent)
        Public SpoolSlices As New List(Of ValidationSpoolSlice)
        Public Calculator As IOManager.CheckSumBlockwiseCalculator
        Public Result As Dictionary(Of String, String)
        Public HashedBytes As Long
        Public ProgressBytes As Long
    End Class

    Private Class ValidationProgressContext
        Public TotalFiles As Long
        Public TotalBytes As Long
        Public ProcessedFiles As Long
        Public ErrorCount As Long
        Public Started As Boolean
    End Class

    Private Class ValidationExtent
        Public Work As ValidationWork
        Public Partition As Byte
        Public StartBlock As Long
        Public EndBlockExclusive As Long
        Public ByteOffset As Long
        Public ByteCount As Long
        Public FileOffset As Long
        Public CopiedBytes As Long
    End Class

    Private Class ValidationSpoolSlice
        Public FileOffset As Long
        Public Length As Integer
        Public SpoolOffset As Long
    End Class

    Private Class ValidationSpoolBatch
        Public Works As New List(Of ValidationWork)
        Public ReservedBytes As Long
    End Class

    Private Function GetValidationBlockSize() As Long
        Dim blockSize As Long = If(plabel Is Nothing, 0, CLng(plabel.blocksize))
        If blockSize <= 0 Then blockSize = 524288
        Dim tapeLimit As Long = CLng(TapeUtils.GlobalBlockLimit)
        If tapeLimit <= 0 Then tapeLimit = 1048576
        If blockSize > tapeLimit Then
            Throw New IO.InvalidDataException($"LTFS block size {blockSize} exceeds the drive read limit {tapeLimit}")
        End If
        Return blockSize
    End Function

    Private Function CloneValidationExtent(source As ltfsindex.file.extent) As ltfsindex.file.extent
        Return New ltfsindex.file.extent With {
            .partition = source.partition,
            .startblock = source.startblock,
            .byteoffset = source.byteoffset,
            .bytecount = source.bytecount,
            .fileoffset = source.fileoffset}
    End Function

    Private Function GetValidationExtents(file As ltfsindex.file) As List(Of ltfsindex.file.extent)
        Dim result As New List(Of ltfsindex.file.extent)
        If file Is Nothing OrElse file.extentinfo Is Nothing Then Return result

        For Each source As ltfsindex.file.extent In file.extentinfo
            If source IsNot Nothing Then result.Add(CloneValidationExtent(source))
        Next

        ' LTFS 1.0 did not store fileoffset.  Preserve the recorded order only
        ' for that legacy shape; modern indexes may store extents in any order.
        If result.Count > 1 AndAlso result.All(Function(ext As ltfsindex.file.extent) ext.fileoffset = 0) Then
            Dim fileOffset As Long = 0
            For Each ext As ltfsindex.file.extent In result
                ext.fileoffset = fileOffset
                If ext.bytecount > 0 Then fileOffset += ext.bytecount
            Next
        Else
            result.Sort(New Comparison(Of ltfsindex.file.extent)(
                Function(a As ltfsindex.file.extent, b As ltfsindex.file.extent) As Integer
                    Dim comparison = a.fileoffset.CompareTo(b.fileoffset)
                    If comparison <> 0 Then Return comparison
                    comparison = a.partition.CompareTo(b.partition)
                    If comparison <> 0 Then Return comparison
                    comparison = a.startblock.CompareTo(b.startblock)
                    If comparison <> 0 Then Return comparison
                    comparison = a.byteoffset.CompareTo(b.byteoffset)
                    If comparison <> 0 Then Return comparison
                    Return a.bytecount.CompareTo(b.bytecount)
                End Function))
        End If
        Return result
    End Function

    Private Function CanMergeValidationReferences(previous As ltfsindex.file.extent,
                                                   current As ltfsindex.file.extent,
                                                   blockSize As Long) As Boolean
        If previous Is Nothing OrElse current Is Nothing OrElse
            previous.bytecount <= 0 OrElse current.bytecount <= 0 OrElse
            previous.partition <> current.partition Then Return False
        If previous.fileoffset > Long.MaxValue - previous.bytecount OrElse
            previous.fileoffset + previous.bytecount <> current.fileoffset Then Return False
        If previous.byteoffset > Long.MaxValue - previous.bytecount Then Return False

        Dim physicalLength = previous.byteoffset + previous.bytecount
        Dim blockDelta = physicalLength \ blockSize
        If previous.startblock > Long.MaxValue - blockDelta Then Return False
        Return previous.startblock + blockDelta = current.startblock AndAlso
            physicalLength Mod blockSize = current.byteoffset
    End Function

    Private Function GetValidationReferenceKey(file As ltfsindex.file,
                                                extents As List(Of ltfsindex.file.extent),
                                                blockSize As Long) As String
        Dim builder As New StringBuilder
        builder.Append(file.length).Append("|")
        If file.symlink Is Nothing Then
            builder.Append("no-symlink")
        Else
            builder.Append("symlink:").Append(file.symlink.Length).Append(":"c).Append(file.symlink)
        End If
        builder.Append("|")

        ' Extent-list order and segmentation are not significant in LTFS.  Merge
        ' logically and physically adjacent entries for the identity key so a
        ' deduplicated file is hashed once even if one index describes it as one
        ' extent and another index describes the same bytes as several extents.
        Dim canonical As New List(Of ltfsindex.file.extent)
        For Each source As ltfsindex.file.extent In extents
            If source.bytecount <= 0 Then Continue For
            Dim ext = CloneValidationExtent(source)
            If canonical.Count > 0 AndAlso CanMergeValidationReferences(canonical(canonical.Count - 1), ext, blockSize) Then
                Dim previous = canonical(canonical.Count - 1)
                If previous.bytecount <= Long.MaxValue - ext.bytecount Then
                    previous.bytecount += ext.bytecount
                    Continue For
                End If
            End If
            canonical.Add(ext)
        Next

        For Each ext As ltfsindex.file.extent In canonical
            builder.Append(CInt(ext.partition)).Append(":"c).
                Append(ext.startblock).Append(":"c).
                Append(ext.byteoffset).Append(":"c).
                Append(ext.bytecount).Append(":"c).
                Append(ext.fileoffset).Append(";"c)
        Next
        Return builder.ToString()
    End Function

    Private Function GetEnabledChecksumNames() As List(Of String)
        Dim result As New List(Of String)
        If My.Settings.LTFSWriter_ChecksumEnabled_SHA1 Then result.Add("SHA1")
        If My.Settings.LTFSWriter_ChecksumEnabled_SHA256 Then result.Add("SHA256")
        If My.Settings.LTFSWriter_ChecksumEnabled_SHA512 Then result.Add("SHA512")
        If My.Settings.LTFSWriter_ChecksumEnabled_CRC32 Then result.Add("CRC32")
        If My.Settings.LTFSWriter_ChecksumEnabled_MD5 Then result.Add("MD5")
        If My.Settings.LTFSWriter_ChecksumEnabled_BLAKE3 Then result.Add("BLAKE3")
        If My.Settings.LTFSWriter_ChecksumEnabled_XxHash3 Then result.Add("XxHash3")
        If My.Settings.LTFSWriter_ChecksumEnabled_XxHash128 Then result.Add("XxHash128")
        Return result
    End Function

    Private Function GetChecksumAttributeName(name As String) As String
        Select Case name
            Case "SHA1"
                Return ltfsindex.file.xattr.HashType.SHA1
            Case "SHA256"
                Return ltfsindex.file.xattr.HashType.SHA256
            Case "SHA512"
                Return ltfsindex.file.xattr.HashType.SHA512
            Case "CRC32"
                Return ltfsindex.file.xattr.HashType.CRC32
            Case "MD5"
                Return ltfsindex.file.xattr.HashType.MD5
            Case "BLAKE3"
                Return ltfsindex.file.xattr.HashType.BLAKE3
            Case "XxHash3"
                Return ltfsindex.file.xattr.HashType.XxHash3
            Case "XxHash128"
                Return ltfsindex.file.xattr.HashType.XxHash128
            Case Else
                Return Nothing
        End Select
    End Function

    Private Function GetChecksumValue(file As ltfsindex.file, name As String) As String
        Dim attributeName = GetChecksumAttributeName(name)
        If String.IsNullOrEmpty(attributeName) Then Return ""
        Return file.GetXAttr(attributeName, True)
    End Function

    Private Sub StoreValidationColor(file As ltfsindex.file, name As String, color As Color)
        If file Is Nothing OrElse String.IsNullOrEmpty(name) OrElse schema Is Nothing Then Return

        Dim fileId As Long = file.fileuid
        Dim currentSchema As ltfsindex = schema
        SyncLock _validationColorCacheLock
            If Not Object.ReferenceEquals(_validationColorCacheSchema, currentSchema) Then
                _validationColorCache.Clear()
                _validationColorCacheSchema = currentSchema
            End If

            Dim colors As Dictionary(Of String, Color) = Nothing
            If Not _validationColorCache.TryGetValue(fileId, colors) Then
                colors = New Dictionary(Of String, Color)(StringComparer.Ordinal)
                _validationColorCache.Add(fileId, colors)
            End If
            colors(name) = color
        End SyncLock
    End Sub

    Private Function TryGetValidationColor(file As ltfsindex.file,
                                           name As String,
                                           ByRef color As Color) As Boolean
        color = Color.Black
        If file Is Nothing OrElse String.IsNullOrEmpty(name) OrElse schema Is Nothing Then Return False

        Dim fileId As Long = file.fileuid
        Dim currentSchema As ltfsindex = schema
        SyncLock _validationColorCacheLock
            If Not Object.ReferenceEquals(_validationColorCacheSchema, currentSchema) Then
                _validationColorCache.Clear()
                _validationColorCacheSchema = currentSchema
            End If

            Dim colors As Dictionary(Of String, Color) = Nothing
            If _validationColorCache.TryGetValue(fileId, colors) Then
                Return colors.TryGetValue(name, color)
            End If
        End SyncLock
        Return False
    End Function

    Private Function GetChecksumColor(file As ltfsindex.file, name As String) As Color
        Dim color As Color
        If TryGetValidationColor(file, name, color) Then Return color

        Select Case name
            Case "SHA1"
                Return file.SHA1ForeColor
            Case "SHA256"
                Return file.SHA256ForeColor
            Case "SHA512"
                Return file.SHA512ForeColor
            Case "CRC32"
                Return file.CRC32ForeColor
            Case "MD5"
                Return file.MD5ForeColor
            Case "BLAKE3"
                Return file.BLAKE3ForeColor
            Case "XxHash3"
                Return file.XxHash3ForeColor
            Case "XxHash128"
                Return file.XxHash128ForeColor
            Case Else
                Return Color.Black
        End Select
    End Function

    Private Sub SetChecksumValue(file As ltfsindex.file, name As String, value As String)
        Dim attributeName = GetChecksumAttributeName(name)
        If Not String.IsNullOrEmpty(attributeName) AndAlso value IsNot Nothing Then
            file.SetXattr(attributeName, value)
        End If
    End Sub

    Private Sub SetChecksumColor(file As ltfsindex.file, name As String, color As Color)
        Select Case name
            Case "SHA1"
                file.SHA1ForeColor = color
            Case "SHA256"
                file.SHA256ForeColor = color
            Case "SHA512"
                file.SHA512ForeColor = color
            Case "CRC32"
                file.CRC32ForeColor = color
            Case "MD5"
                file.MD5ForeColor = color
            Case "BLAKE3"
                file.BLAKE3ForeColor = color
            Case "XxHash3"
                file.XxHash3ForeColor = color
            Case "XxHash128"
                file.XxHash128ForeColor = color
        End Select
        StoreValidationColor(file, name, color)
    End Sub

    Private Function HasMissingChecksum(file As ltfsindex.file) As Boolean
        For Each name As String In GetEnabledChecksumNames()
            If String.IsNullOrEmpty(GetChecksumValue(file, name)) Then Return True
        Next
        Return False
    End Function

    Private Function HasValidatedChecksums(file As ltfsindex.file) As Boolean
        For Each name As String In GetEnabledChecksumNames()
            Dim value = GetChecksumValue(file, name)
            If String.IsNullOrEmpty(value) Then Return False
            Dim color As Color = GetChecksumColor(file, name)
            If color.Equals(Color.Black) OrElse color.Equals(Color.Red) Then Return False
        Next
        Return True
    End Function

    Private Function ValidationNeedsCalculation(file As ltfsindex.file,
                                                overwrite As Boolean,
                                                validateOnly As Boolean) As Boolean
        If file Is Nothing Then Return False
        If GetEnabledChecksumNames().Count = 0 Then Return False
        If overwrite Then Return True
        If validateOnly Then Return Not HasValidatedChecksums(file)
        Return HasMissingChecksum(file)
    End Function

    Private Function BuildValidationPlan(fileList As List(Of FileRecord),
                                         overwrite As Boolean,
                                         validateOnly As Boolean,
                                         workByRecord As Dictionary(Of FileRecord, ValidationWork)) As List(Of ValidationWork)
        Dim result As New List(Of ValidationWork)
        Dim byReference As New Dictionary(Of String, ValidationWork)(StringComparer.Ordinal)
        Dim blockSize As Long = GetValidationBlockSize()

        For Each fr As FileRecord In fileList
            If fr Is Nothing OrElse fr.File Is Nothing OrElse
                Not ValidationNeedsCalculation(fr.File, overwrite, validateOnly) Then Continue For
            If fr.File.length < 0 Then Throw New IO.InvalidDataException($"Negative file length for {fr.File.name}")

            Dim normalizedExtents = GetValidationExtents(fr.File)
            Dim referenceKey = GetValidationReferenceKey(fr.File, normalizedExtents, blockSize)
            Dim work As ValidationWork = Nothing
            If Not byReference.TryGetValue(referenceKey, work) Then
                work = New ValidationWork With {.Representative = fr,
                                                 .Calculator = New IOManager.CheckSumBlockwiseCalculator}
                byReference.Add(referenceKey, work)
                result.Add(work)

                Dim logicalEnd As Long = 0
                For Each ext As ltfsindex.file.extent In normalizedExtents
                    If ext.bytecount < 0 Then Throw New IO.InvalidDataException($"Negative extent bytecount for {fr.File.name}")
                    If ext.partition <> ltfsindex.PartitionLabel.a AndAlso ext.partition <> ltfsindex.PartitionLabel.b Then
                        Throw New IO.InvalidDataException($"Invalid extent partition for {fr.File.name}: {ext.partition}")
                    End If
                    If ext.byteoffset < 0 OrElse ext.byteoffset >= blockSize Then
                        Throw New IO.InvalidDataException($"Invalid extent byteoffset for {fr.File.name}: {ext.byteoffset}")
                    End If
                    If ext.fileoffset < 0 OrElse ext.fileoffset > fr.File.length Then
                        Throw New IO.InvalidDataException($"Invalid extent fileoffset for {fr.File.name}: {ext.fileoffset}")
                    End If
                    If ext.startblock < 0 Then
                        Throw New IO.InvalidDataException($"Negative extent startblock for {fr.File.name}")
                    End If
                    If ext.bytecount = 0 Then Continue For
                    If ext.fileoffset > fr.File.length - ext.bytecount Then
                        Throw New IO.InvalidDataException($"Extent exceeds file length for {fr.File.name}")
                    End If
                    If ext.fileoffset < logicalEnd Then
                        Throw New IO.InvalidDataException($"Overlapping extents for {fr.File.name}")
                    End If
                    logicalEnd = ext.fileoffset + ext.bytecount

                    If ext.byteoffset > Long.MaxValue - ext.bytecount Then
                        Throw New IO.InvalidDataException($"Invalid physical extent length for {fr.File.name}")
                    End If
                    Dim physicalByteCount As Long = ext.byteoffset + ext.bytecount
                    If physicalByteCount > Long.MaxValue - (blockSize - 1) Then
                        Throw New IO.InvalidDataException($"Invalid physical extent block range for {fr.File.name}")
                    End If
                    Dim blockCount As Long = (physicalByteCount + blockSize - 1) \ blockSize
                    If blockCount <= 0 OrElse ext.startblock > Long.MaxValue - blockCount Then
                        Throw New IO.InvalidDataException($"Invalid extent block range for {fr.File.name}")
                    End If
                    work.Extents.Add(New ValidationExtent With {
                        .Work = work,
                        .Partition = GetPartitionNumber(CType(ext.partition, ltfslabel.PartitionLabel)),
                        .StartBlock = ext.startblock,
                        .EndBlockExclusive = ext.startblock + blockCount,
                        .ByteOffset = ext.byteoffset,
                        .ByteCount = ext.bytecount,
                        .FileOffset = ext.fileoffset})
                Next
            End If

            work.Members.Add(fr)
            workByRecord(fr) = work
        Next
        Return result
    End Function

    Private Function ReadValidationBlock(partition As Byte, block As Long) As Byte()
        If block < 0 Then Throw New IO.InvalidDataException("Negative validation block")
        If block = Long.MaxValue Then Throw New IO.InvalidDataException("Validation block number overflow")
        If RestorePosition Is Nothing Then RestorePosition = New TapeUtils.PositionData(driveHandle)

        If RestorePosition.PartitionNumber <> partition OrElse RestorePosition.BlockNumber <> CULng(block) Then
            Dim locateCode = TapeUtils.Locate(handle:=driveHandle,
                                              BlockAddress:=CULng(block),
                                              Partition:=partition,
                                              DestType:=TapeUtils.LocateDestType.Block)
            RestorePosition = New TapeUtils.PositionData(driveHandle)
            If RestorePosition.PartitionNumber <> partition OrElse RestorePosition.BlockNumber <> CULng(block) Then
                Throw New IO.IOException($"Unable to locate validation block P{partition} B{block}; current position is {RestorePosition}; sense=0x{Hex(locateCode).ToUpper.PadLeft(4, "0"c)}")
            End If
        End If

        While Not StopFlag
            Dim sense As Byte() = {}
            Dim data = TapeUtils.ReadBlock(handle:=driveHandle,
                                           sense:=sense,
                                           BlockSizeLimit:=CUInt(GetValidationBlockSize()))
            Dim senseKey As Integer = If(sense IsNot Nothing AndAlso sense.Length > 2, sense(2) And &HF, 0)
            Dim eom As Boolean = sense IsNot Nothing AndAlso sense.Length > 2 AndAlso ((sense(2) >> 6) And &H1) = 1
            If data IsNot Nothing AndAlso data.Length > 0 AndAlso (senseKey = 0 OrElse eom) Then
                If data.LongLength > GetValidationBlockSize() Then
                    Throw New IO.InvalidDataException($"Tape block P{partition} B{block} is {data.LongLength} bytes; LTFS label block size is {GetValidationBlockSize()}")
                End If
                'TotalBytesProcessed is also the source for the live speed
                'history.  Count the successful tape read here, while the
                'operation is still in progress, instead of waiting for the
                'whole validation plan to finish.
                Threading.Interlocked.Add(TotalBytesProcessed, data.LongLength)
                RestorePosition.PartitionNumber = partition
                RestorePosition.BlockNumber = CULng(block + 1)
                Return data
            End If

            If senseKey = 0 AndAlso (data Is Nothing OrElse data.Length = 0) Then
                Throw New IO.EndOfStreamException($"Empty tape block while validating P{partition} B{block}")
            End If

            PrintMsg($"sense err {TapeUtils.Byte2Hex(sense, True)}", Warning:=True, LogOnly:=True)
            Dim decision As DialogResult = DialogResult.Abort
            Invoke(Sub() decision = MessageBox.Show(New Form With {.TopMost = True},
                                                     $"{My.Resources.ResText_RErrSCSI}{vbCrLf}{TapeUtils.ParseSenseData(sense)}{vbCrLf}{vbCrLf}sense{vbCrLf}{TapeUtils.Byte2Hex(sense, True)}",
                                                     My.Resources.ResText_Warning,
                                                     MessageBoxButtons.AbortRetryIgnore))
            Select Case decision
                Case DialogResult.Retry
                    Continue While
                Case DialogResult.Ignore
                    If data IsNot Nothing AndAlso data.Length > 0 Then
                        Threading.Interlocked.Add(TotalBytesProcessed, data.LongLength)
                        RestorePosition.PartitionNumber = partition
                        RestorePosition.BlockNumber = CULng(block + 1)
                        Return data
                    End If
                    Throw New IO.EndOfStreamException($"No data returned for ignored tape error at P{partition} B{block}")
                Case Else
                    StopFlag = True
                    Throw New IO.IOException(TapeUtils.ParseSenseData(sense))
            End Select
        End While
        Throw New OperationCanceledException()
    End Function

    Private Sub SpoolValidationBlock(spool As IO.FileStream,
                                     blockNumber As Long,
                                     block As Byte(),
                                     active As List(Of ValidationExtent),
                                     blockSize As Long)
        Dim blockSpoolOffset As Long = spool.Length
        spool.Position = blockSpoolOffset
        spool.Write(block, 0, block.Length)
        For Each ext As ValidationExtent In active
            Dim blockDelta As Long = blockNumber - ext.StartBlock
            If blockDelta < 0 OrElse blockDelta > Long.MaxValue \ blockSize Then
                Throw New IO.InvalidDataException("Validation block offset overflow")
            End If
            Dim blockRelativeStart As Long = blockDelta * blockSize
            Dim validStart As Long = Math.Max(ext.ByteOffset, blockRelativeStart)
            Dim validEnd As Long = Math.Min(ext.ByteOffset + ext.ByteCount, blockRelativeStart + block.Length)
            If validEnd <= validStart Then Continue For

            Dim dataOffset As Integer = CInt(validStart - blockRelativeStart)
            Dim count As Integer = CInt(validEnd - validStart)
            Dim slice As New ValidationSpoolSlice With {
                .FileOffset = ext.FileOffset + validStart - ext.ByteOffset,
                .Length = count,
                .SpoolOffset = blockSpoolOffset + dataOffset}
            ext.CopiedBytes += count
            ext.Work.SpoolSlices.Add(slice)
            ReportValidationWorkBytes(ext.Work, count)
        Next
    End Sub

    Private Sub SpoolValidationPartition(spool As IO.FileStream,
                                          extents As List(Of ValidationExtent),
                                          blockSize As Long)
        extents.Sort(New Comparison(Of ValidationExtent)(
            Function(a As ValidationExtent, b As ValidationExtent) As Integer
                Dim comparison = a.StartBlock.CompareTo(b.StartBlock)
                If comparison <> 0 Then Return comparison
                comparison = a.ByteOffset.CompareTo(b.ByteOffset)
                If comparison <> 0 Then Return comparison
                comparison = a.EndBlockExclusive.CompareTo(b.EndBlockExclusive)
                If comparison <> 0 Then Return comparison
                Return a.FileOffset.CompareTo(b.FileOffset)
            End Function))

        Dim active As New List(Of ValidationExtent)
        Dim nextExtent As Integer = 0
        Dim currentBlock As Long = -1
        While nextExtent < extents.Count OrElse active.Count > 0
            If active.Count = 0 Then currentBlock = extents(nextExtent).StartBlock
            active.RemoveAll(Function(ext As ValidationExtent) currentBlock >= ext.EndBlockExclusive)
            While nextExtent < extents.Count AndAlso extents(nextExtent).StartBlock <= currentBlock
                active.Add(extents(nextExtent))
                nextExtent += 1
            End While
            If active.Count = 0 Then Continue While

            Dim block = ReadValidationBlock(active(0).Partition, currentBlock)
            SpoolValidationBlock(spool, currentBlock, block, active, blockSize)
            If StopFlag Then Throw New OperationCanceledException()
            If currentBlock = Long.MaxValue Then Throw New IO.InvalidDataException("Validation block number overflow")
            currentBlock += 1
            active.RemoveAll(Function(ext As ValidationExtent) currentBlock >= ext.EndBlockExclusive)
        End While

        For Each ext As ValidationExtent In extents
            If ext.CopiedBytes <> ext.ByteCount Then
                Throw New IO.EndOfStreamException($"Extent P{ext.Partition} B{ext.StartBlock} returned {ext.CopiedBytes} of {ext.ByteCount} bytes")
            End If
        Next
    End Sub

    Private Function GetValidationPartitionOrder(works As IEnumerable(Of ValidationWork)) As List(Of Byte)
        Dim partitions As New HashSet(Of Byte)
        For Each work As ValidationWork In works
            For Each ext As ValidationExtent In work.Extents
                partitions.Add(ext.Partition)
            Next
        Next

        Dim result As New List(Of Byte)
        If partitions.Count = 0 Then Return result
        RestorePosition = New TapeUtils.PositionData(driveHandle)
        If partitions.Contains(RestorePosition.PartitionNumber) Then
            result.Add(RestorePosition.PartitionNumber)
        End If
        For Each partition As Byte In partitions.OrderBy(Function(value As Byte) value)
            If Not result.Contains(partition) Then result.Add(partition)
        Next
        Return result
    End Function

    Private Function CompareValidationPosition(blockA As Long,
                                                offsetA As Long,
                                                blockB As Long,
                                                offsetB As Long) As Integer
        Dim comparison = blockA.CompareTo(blockB)
        If comparison <> 0 Then Return comparison
        Return offsetA.CompareTo(offsetB)
    End Function

    Private Function IsForwardValidationWork(work As ValidationWork,
                                             partitionOrder As List(Of Byte),
                                             blockSize As Long) As Boolean
        Dim rank As New Dictionary(Of Byte, Integer)
        For i As Integer = 0 To partitionOrder.Count - 1
            rank(partitionOrder(i)) = i
        Next

        Dim previousRank As Integer = -1
        Dim previousEndBlock As Long = 0
        Dim previousEndOffset As Long = 0
        For Each ext As ValidationExtent In work.Extents.OrderBy(Function(value As ValidationExtent) value.FileOffset)
            Dim currentRank As Integer = -1
            If Not rank.TryGetValue(ext.Partition, currentRank) OrElse currentRank < previousRank Then Return False
            If currentRank = previousRank AndAlso
                CompareValidationPosition(ext.StartBlock, ext.ByteOffset, previousEndBlock, previousEndOffset) < 0 Then Return False

            Dim physicalLength = ext.ByteOffset + ext.ByteCount
            Dim blockDelta = physicalLength \ blockSize
            If ext.StartBlock > Long.MaxValue - blockDelta Then Return False
            previousRank = currentRank
            previousEndBlock = ext.StartBlock + blockDelta
            previousEndOffset = physicalLength Mod blockSize
        Next
        Return True
    End Function

    Private Function OptimizeValidationPartitionOrder(works As List(Of ValidationWork),
                                                       preferred As List(Of Byte),
                                                       blockSize As Long) As List(Of Byte)
        If preferred.Count <> 2 Then Return preferred
        Dim alternate As New List(Of Byte) From {preferred(1), preferred(0)}
        Dim preferredBytes As Decimal = 0D
        Dim alternateBytes As Decimal = 0D
        Dim preferredCount As Integer = 0
        Dim alternateCount As Integer = 0
        For Each work As ValidationWork In works
            Dim length As Decimal = Math.Max(0, work.Representative.File.length)
            If IsForwardValidationWork(work, preferred, blockSize) Then
                preferredBytes += length
                preferredCount += 1
            End If
            If IsForwardValidationWork(work, alternate, blockSize) Then
                alternateBytes += length
                alternateCount += 1
            End If
        Next
        If alternateBytes > preferredBytes OrElse
            (alternateBytes = preferredBytes AndAlso alternateCount > preferredCount) Then Return alternate
        Return preferred
    End Function

    Private Sub StreamValidationBlock(blockNumber As Long,
                                      block As Byte(),
                                      active As List(Of ValidationExtent),
                                      blockSize As Long,
                                      zeroBuffer As Byte())
        For Each ext As ValidationExtent In active
            Dim blockDelta As Long = blockNumber - ext.StartBlock
            If blockDelta < 0 OrElse blockDelta > Long.MaxValue \ blockSize Then
                Throw New IO.InvalidDataException("Validation block offset overflow")
            End If
            Dim blockRelativeStart As Long = blockDelta * blockSize
            Dim validStart As Long = Math.Max(ext.ByteOffset, blockRelativeStart)
            Dim validEnd As Long = Math.Min(ext.ByteOffset + ext.ByteCount, blockRelativeStart + block.Length)
            If validEnd <= validStart Then Continue For

            Dim dataOffset As Integer = CInt(validStart - blockRelativeStart)
            Dim count As Integer = CInt(validEnd - validStart)
            Dim fileOffset As Long = ext.FileOffset + validStart - ext.ByteOffset
            If fileOffset < ext.Work.HashedBytes Then
                Throw New IO.InvalidDataException($"Physical extent order conflicts with logical order for {ext.Work.Representative.File.name}")
            End If
            If fileOffset > ext.Work.HashedBytes Then
                PropagateValidationZeros(ext.Work.Calculator, fileOffset - ext.Work.HashedBytes, zeroBuffer)
                ext.Work.HashedBytes = fileOffset
            End If
            ext.Work.Calculator.PropagateRange(block, dataOffset, count)
            ext.Work.HashedBytes += count
            ext.CopiedBytes += count
            ReportValidationWorkBytes(ext.Work, count)
        Next
    End Sub

    Private Sub StreamValidationPartition(extents As List(Of ValidationExtent),
                                          blockSize As Long,
                                          zeroBuffer As Byte(),
                                          Optional workCompleted As Action(Of ValidationWork) = Nothing)
        extents.Sort(New Comparison(Of ValidationExtent)(
            Function(a As ValidationExtent, b As ValidationExtent) As Integer
                Dim comparison = a.StartBlock.CompareTo(b.StartBlock)
                If comparison <> 0 Then Return comparison
                comparison = a.ByteOffset.CompareTo(b.ByteOffset)
                If comparison <> 0 Then Return comparison
                comparison = a.EndBlockExclusive.CompareTo(b.EndBlockExclusive)
                If comparison <> 0 Then Return comparison
                Return a.FileOffset.CompareTo(b.FileOffset)
            End Function))

        Dim active As New List(Of ValidationExtent)
        Dim nextExtent As Integer = 0
        Dim currentBlock As Long = -1
        While nextExtent < extents.Count OrElse active.Count > 0
            If active.Count = 0 Then currentBlock = extents(nextExtent).StartBlock
            active.RemoveAll(Function(ext As ValidationExtent) currentBlock >= ext.EndBlockExclusive)
            While nextExtent < extents.Count AndAlso extents(nextExtent).StartBlock <= currentBlock
                active.Add(extents(nextExtent))
                nextExtent += 1
            End While
            If active.Count = 0 Then Continue While

            Dim block = ReadValidationBlock(active(0).Partition, currentBlock)
            StreamValidationBlock(currentBlock, block, active, blockSize, zeroBuffer)
            If workCompleted IsNot Nothing Then
                Dim activeWorks As New HashSet(Of ValidationWork)
                For Each ext As ValidationExtent In active
                    activeWorks.Add(ext.Work)
                Next
                For Each work As ValidationWork In activeWorks
                    workCompleted(work)
                Next
            End If
            If StopFlag Then Throw New OperationCanceledException()
            If currentBlock = Long.MaxValue Then Throw New IO.InvalidDataException("Validation block number overflow")
            currentBlock += 1
            active.RemoveAll(Function(ext As ValidationExtent) currentBlock >= ext.EndBlockExclusive)
        End While

        For Each ext As ValidationExtent In extents
            If ext.CopiedBytes <> ext.ByteCount Then
                Throw New IO.EndOfStreamException($"Extent P{ext.Partition} B{ext.StartBlock} returned {ext.CopiedBytes} of {ext.ByteCount} bytes")
            End If
        Next
    End Sub

    Private Sub ReportValidationWorkBytes(work As ValidationWork, bytes As Long)
        If work Is Nothing OrElse bytes <= 0 OrElse work.Representative Is Nothing OrElse
           work.Representative.File Is Nothing Then Return

        Dim remaining = work.Representative.File.length - work.ProgressBytes
        If remaining <= 0 Then Return
        Dim reported = Math.Min(bytes, remaining)
        work.ProgressBytes += reported

        'A deduplicated reference is read once but represents every member in
        'the validation request.  Scale the operation progress by that member
        'count so the progress bar still reaches the requested total.
        Dim memberCount As Long = Math.Max(1, work.Members.Count)
        Dim increment As Decimal = CDec(reported) * CDec(memberCount)
        If increment > Long.MaxValue Then increment = Long.MaxValue
        Threading.Interlocked.Add(CurrentBytesProcessed, CLng(increment))
    End Sub

    Private Sub FinishStreamedValidationWork(work As ValidationWork, zeroBuffer As Byte())
        If work.HashedBytes < work.Representative.File.length Then
            PropagateValidationZeros(work.Calculator, work.Representative.File.length - work.HashedBytes, zeroBuffer)
            work.HashedBytes = work.Representative.File.length
        ElseIf work.HashedBytes > work.Representative.File.length Then
            Throw New IO.InvalidDataException($"Validation extents exceed file length for {work.Representative.File.name}")
        End If
        If work.ProgressBytes < work.Representative.File.length Then
            ReportValidationWorkBytes(work, work.Representative.File.length - work.ProgressBytes)
        End If
        work.Calculator.ProcessFinalBlock()
        work.Result = GetValidationChecksumResult(work.Calculator)
    End Sub

    Private Sub CalculateForwardValidationWorks(works As List(Of ValidationWork),
                                                partitionOrder As List(Of Byte),
                                                blockSize As Long,
                                                Optional workCompleted As Action(Of ValidationWork) = Nothing)
        If works.Count = 0 Then Return
        Dim zeroBuffer(Math.Min(CInt(blockSize), 1024 * 1024) - 1) As Byte
        Dim byPartition As New Dictionary(Of Byte, List(Of ValidationExtent))
        For Each work As ValidationWork In works
            work.HashedBytes = 0
            work.ProgressBytes = 0
            For Each ext As ValidationExtent In work.Extents
                ext.CopiedBytes = 0
                Dim partitionExtents As List(Of ValidationExtent) = Nothing
                If Not byPartition.TryGetValue(ext.Partition, partitionExtents) Then
                    partitionExtents = New List(Of ValidationExtent)
                    byPartition.Add(ext.Partition, partitionExtents)
                End If
                partitionExtents.Add(ext)
            Next
        Next

        Dim completedWorks As New HashSet(Of ValidationWork)
        Dim finishCompletedWork As Action(Of ValidationWork) =
            Sub(work As ValidationWork)
                If work Is Nothing OrElse completedWorks.Contains(work) OrElse
                   work.Extents.Any(Function(ext As ValidationExtent) ext.CopiedBytes <> ext.ByteCount) Then Return
                FinishStreamedValidationWork(work, zeroBuffer)
                completedWorks.Add(work)
                If workCompleted IsNot Nothing Then workCompleted(work)
            End Sub
        For Each partition As Byte In partitionOrder
            If byPartition.ContainsKey(partition) Then
                StreamValidationPartition(byPartition(partition), blockSize, zeroBuffer, finishCompletedWork)
            End If
        Next
        For Each work As ValidationWork In works
            If Not completedWorks.Contains(work) Then
                FinishStreamedValidationWork(work, zeroBuffer)
                completedWorks.Add(work)
                If workCompleted IsNot Nothing Then workCompleted(work)
            End If
        Next
    End Sub

    Private Function GetValidationSpoolReservation(work As ValidationWork, blockSize As Long) As Long
        Dim result As Long = 0
        For Each ext As ValidationExtent In work.Extents
            Dim blockCount = ext.EndBlockExclusive - ext.StartBlock
            If blockCount <= 0 Then Continue For
            If blockCount > ValidationSpoolBudgetBytes \ blockSize Then Return ValidationSpoolBudgetBytes + 1
            Dim extentBytes = blockCount * blockSize
            If result > ValidationSpoolBudgetBytes - extentBytes Then Return ValidationSpoolBudgetBytes + 1
            result += extentBytes
        Next
        Return result
    End Function

    Private Function BuildValidationSpoolBatches(works As List(Of ValidationWork),
                                                 blockSize As Long,
                                                 directWorks As List(Of ValidationWork)) As List(Of ValidationSpoolBatch)
        Dim candidates As New List(Of Tuple(Of ValidationWork, Long))
        For Each work As ValidationWork In works
            Dim reservation = GetValidationSpoolReservation(work, blockSize)
            If reservation > ValidationSpoolBudgetBytes Then
                directWorks.Add(work)
            Else
                candidates.Add(Tuple.Create(work, reservation))
            End If
        Next
        candidates.Sort(New Comparison(Of Tuple(Of ValidationWork, Long))(
            Function(a As Tuple(Of ValidationWork, Long), b As Tuple(Of ValidationWork, Long)) As Integer
                Return b.Item2.CompareTo(a.Item2)
            End Function))

        Dim result As New List(Of ValidationSpoolBatch)
        For Each candidate As Tuple(Of ValidationWork, Long) In candidates
            Dim best As ValidationSpoolBatch = Nothing
            Dim bestRemaining As Long = Long.MaxValue
            For Each batch As ValidationSpoolBatch In result
                If batch.ReservedBytes <= ValidationSpoolBudgetBytes - candidate.Item2 Then
                    Dim remaining = ValidationSpoolBudgetBytes - batch.ReservedBytes - candidate.Item2
                    If remaining < bestRemaining Then
                        best = batch
                        bestRemaining = remaining
                    End If
                End If
            Next
            If best Is Nothing Then
                best = New ValidationSpoolBatch
                result.Add(best)
            End If
            best.Works.Add(candidate.Item1)
            best.ReservedBytes += candidate.Item2
        Next
        Return result
    End Function

    Private Sub CalculateValidationWorkDirect(work As ValidationWork, blockSize As Long)
        Dim zeroBuffer(Math.Min(CInt(blockSize), 1024 * 1024) - 1) As Byte
        work.HashedBytes = 0
        work.ProgressBytes = 0
        For Each ext As ValidationExtent In work.Extents.OrderBy(Function(value As ValidationExtent) value.FileOffset)
            ext.CopiedBytes = 0
            If ext.FileOffset < work.HashedBytes Then
                Throw New IO.InvalidDataException($"Overlapping extents in validation file {work.Representative.File.name}")
            End If
            If ext.FileOffset > work.HashedBytes Then
                PropagateValidationZeros(work.Calculator, ext.FileOffset - work.HashedBytes, zeroBuffer)
                work.HashedBytes = ext.FileOffset
            End If

            Dim currentBlock = ext.StartBlock
            While currentBlock < ext.EndBlockExclusive
                Dim block = ReadValidationBlock(ext.Partition, currentBlock)
                Dim blockDelta = currentBlock - ext.StartBlock
                Dim blockRelativeStart = blockDelta * blockSize
                Dim validStart = Math.Max(ext.ByteOffset, blockRelativeStart)
                Dim validEnd = Math.Min(ext.ByteOffset + ext.ByteCount, blockRelativeStart + block.Length)
                If validEnd > validStart Then
                    Dim dataOffset = CInt(validStart - blockRelativeStart)
                    Dim count = CInt(validEnd - validStart)
                    work.Calculator.PropagateRange(block, dataOffset, count)
                    work.HashedBytes += count
                    ext.CopiedBytes += count
                    ReportValidationWorkBytes(work, count)
                End If
                currentBlock += 1
            End While
            If ext.CopiedBytes <> ext.ByteCount Then
                Throw New IO.EndOfStreamException($"Extent P{ext.Partition} B{ext.StartBlock} returned {ext.CopiedBytes} of {ext.ByteCount} bytes")
            End If
        Next
        FinishStreamedValidationWork(work, zeroBuffer)
    End Sub

    Private Sub PropagateValidationZeros(calculator As IOManager.CheckSumBlockwiseCalculator,
                                         count As Long,
                                         zeroBuffer As Byte())
        While count > 0
            If StopFlag Then Throw New OperationCanceledException()
            Dim take As Integer = CInt(Math.Min(count, CLng(zeroBuffer.Length)))
            calculator.Propagate(zeroBuffer, take)
            count -= take
        End While
    End Sub

    Private Sub ReadValidationSpoolSlice(spool As IO.FileStream,
                                         slice As ValidationSpoolSlice,
                                         calculator As IOManager.CheckSumBlockwiseCalculator,
                                         buffer As Byte())
        spool.Position = slice.SpoolOffset
        Dim remaining As Integer = slice.Length
        While remaining > 0
            If StopFlag Then Throw New OperationCanceledException()
            Dim readCount = spool.Read(buffer, 0, Math.Min(remaining, buffer.Length))
            If readCount <= 0 Then Throw New IO.EndOfStreamException("Validation spool ended unexpectedly")
            calculator.Propagate(buffer, readCount)
            remaining -= readCount
        End While
    End Sub

    Private Function GetValidationChecksumResult(calculator As IOManager.CheckSumBlockwiseCalculator) As Dictionary(Of String, String)
        Dim result As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        For Each name As String In GetEnabledChecksumNames()
            Dim value As String = Nothing
            Select Case name
                Case "SHA1"
                    value = calculator.SHA1Value
                Case "SHA256"
                    value = calculator.SHA256Value
                Case "SHA512"
                    value = calculator.SHA512Value
                Case "CRC32"
                    value = calculator.CRC32Value
                Case "MD5"
                    value = calculator.MD5Value
                Case "BLAKE3"
                    value = calculator.BlakeValue
                Case "XxHash3"
                    value = calculator.XXHash3Value
                Case "XxHash128"
                    value = calculator.XXHash128Value
            End Select
            If value IsNot Nothing Then result(name) = value
        Next
        Return result
    End Function

    Private Sub FinalizeValidationWork(work As ValidationWork,
                                       spool As IO.FileStream,
                                       blockSize As Long)
        Dim zeroBuffer(Math.Min(CInt(blockSize), 1024 * 1024) - 1) As Byte
        Dim readBuffer(Math.Min(CInt(blockSize), 1024 * 1024) - 1) As Byte
        Dim logicalOffset As Long = 0
        work.SpoolSlices.Sort(New Comparison(Of ValidationSpoolSlice)(
            Function(a As ValidationSpoolSlice, b As ValidationSpoolSlice) As Integer
                Return a.FileOffset.CompareTo(b.FileOffset)
            End Function))

        For Each slice As ValidationSpoolSlice In work.SpoolSlices
            If StopFlag Then Throw New OperationCanceledException()
            If slice.FileOffset < logicalOffset Then
                Throw New IO.InvalidDataException($"Overlapping extents in validation file {work.Representative.File.name}")
            End If
            If slice.FileOffset > logicalOffset Then
                PropagateValidationZeros(work.Calculator, slice.FileOffset - logicalOffset, zeroBuffer)
                logicalOffset = slice.FileOffset
            End If
            ReadValidationSpoolSlice(spool, slice, work.Calculator, readBuffer)
            logicalOffset += slice.Length
        Next
        If logicalOffset < work.Representative.File.length Then
            PropagateValidationZeros(work.Calculator, work.Representative.File.length - logicalOffset, zeroBuffer)
        ElseIf logicalOffset > work.Representative.File.length Then
            Throw New IO.InvalidDataException($"Validation extents exceed file length for {work.Representative.File.name}")
        End If
        If work.ProgressBytes < work.Representative.File.length Then
            ReportValidationWorkBytes(work, work.Representative.File.length - work.ProgressBytes)
        End If
        work.Calculator.ProcessFinalBlock()
        work.Result = GetValidationChecksumResult(work.Calculator)
    End Sub

    Private Sub CalculateChecksumsWithSpool(works As List(Of ValidationWork),
                                            Optional workCompleted As Action(Of ValidationWork) = Nothing)
        Dim spoolPath As String = LazySchemaStore.CreateTempFilePath("validation-spool")
        Dim blockSize As Long = GetValidationBlockSize()
        Try
            Using spool As New IO.FileStream(spoolPath,
                                             IO.FileMode.Create,
                                             IO.FileAccess.ReadWrite,
                                             IO.FileShare.None,
                                             1024 * 1024,
                                             IO.FileOptions.RandomAccess Or IO.FileOptions.DeleteOnClose)
                Dim byPartition As New Dictionary(Of Byte, List(Of ValidationExtent))
                For Each work As ValidationWork In works
                    work.SpoolSlices.Clear()
                    work.ProgressBytes = 0
                    For Each ext As ValidationExtent In work.Extents
                        ext.CopiedBytes = 0
                        Dim partitionExtents As List(Of ValidationExtent) = Nothing
                        If Not byPartition.TryGetValue(ext.Partition, partitionExtents) Then
                            partitionExtents = New List(Of ValidationExtent)
                            byPartition.Add(ext.Partition, partitionExtents)
                        End If
                        partitionExtents.Add(ext)
                    Next
                Next

                Dim partitionOrder = GetValidationPartitionOrder(works)
                For Each partition As Byte In partitionOrder
                    SpoolValidationPartition(spool, byPartition(partition), blockSize)
                    If spool.Length > ValidationSpoolBudgetBytes Then
                        Throw New IO.IOException($"Validation spool exceeded its {ValidationSpoolBudgetBytes} byte limit")
                    End If
                Next

                spool.Flush()
                For Each work As ValidationWork In works
                    If StopFlag Then Throw New OperationCanceledException()
                    FinalizeValidationWork(work, spool, blockSize)
                    If workCompleted IsNot Nothing Then workCompleted(work)
                Next
            End Using
        Finally
            Try
                If IO.File.Exists(spoolPath) Then IO.File.Delete(spoolPath)
            Catch
            End Try
        End Try
    End Sub

    Private Sub CalculateChecksumsWithPlan(works As List(Of ValidationWork),
                                           Optional workCompleted As Action(Of ValidationWork) = Nothing)
        If works.Count = 0 Then Return
        Dim blockSize = GetValidationBlockSize()
        Dim partitionOrder = GetValidationPartitionOrder(works)
        partitionOrder = OptimizeValidationPartitionOrder(works, partitionOrder, blockSize)
        Dim forwardWorks As New List(Of ValidationWork)
        Dim deferredWorks As New List(Of ValidationWork)
        For Each work As ValidationWork In works
            If IsForwardValidationWork(work, partitionOrder, blockSize) Then
                forwardWorks.Add(work)
            Else
                deferredWorks.Add(work)
            End If
        Next

        CalculateForwardValidationWorks(forwardWorks, partitionOrder, blockSize, workCompleted)

        Dim directWorks As New List(Of ValidationWork)
        Dim spoolBatches = BuildValidationSpoolBatches(deferredWorks, blockSize, directWorks)
        PrintMsg($"Validation schedule: forward={forwardWorks.Count}, bounded spool batches={spoolBatches.Count}, direct fallback={directWorks.Count}, spool limit={ValidationSpoolBudgetBytes}", LogOnly:=True)
        For Each batch As ValidationSpoolBatch In spoolBatches
            If StopFlag Then Throw New OperationCanceledException()
            CalculateChecksumsWithSpool(batch.Works, workCompleted)
        Next

        directWorks.Sort(New Comparison(Of ValidationWork)(
            Function(a As ValidationWork, b As ValidationWork) As Integer
                If a.Extents.Count = 0 Then Return If(b.Extents.Count = 0, 0, -1)
                If b.Extents.Count = 0 Then Return 1
                Dim comparison = a.Extents(0).Partition.CompareTo(b.Extents(0).Partition)
                If comparison <> 0 Then Return comparison
                Return a.Extents(0).StartBlock.CompareTo(b.Extents(0).StartBlock)
            End Function))
        For Each work As ValidationWork In directWorks
            If StopFlag Then Throw New OperationCanceledException()
            CalculateValidationWorkDirect(work, blockSize)
            If workCompleted IsNot Nothing Then workCompleted(work)
        Next
    End Sub

    Private Function ApplyValidationResult(file As ltfsindex.file,
                                           result As Dictionary(Of String, String),
                                           overwrite As Boolean,
                                           validateOnly As Boolean,
                                           ByRef errorCount As Long,
                                           darkGreen As Boolean) As Boolean
        If result Is Nothing Then Return False
        Dim updateHashes As Boolean = overwrite OrElse (Not validateOnly AndAlso HasMissingChecksum(file))
        For Each name As String In GetEnabledChecksumNames()
            Dim expected As String = Nothing
            If Not result.TryGetValue(name, expected) OrElse String.IsNullOrEmpty(expected) Then Continue For
            Dim actual = GetChecksumValue(file, name)
            If validateOnly Then
                If String.IsNullOrEmpty(actual) Then Continue For
                If String.Equals(actual, expected, StringComparison.OrdinalIgnoreCase) Then
                    SetChecksumColor(file, name, If(darkGreen, Color.DarkGreen, Color.Green))
                Else
                    SetChecksumColor(file, name, Color.Red)
                    Threading.Interlocked.Increment(errorCount)
                    PrintMsg($"{name} mismatch at fileuid={file.fileuid} filename={file.name} logged={actual} calculated={expected}", ForceLog:=True)
                End If
            ElseIf updateHashes Then
                If Not String.Equals(actual, expected, StringComparison.OrdinalIgnoreCase) Then
                    SetChecksumValue(file, name, expected)
                    If actual = "" Then
                        SetChecksumColor(file, name, Color.Blue)
                    Else
                        SetChecksumColor(file, name, Color.OrangeRed)
                        Threading.Interlocked.Increment(errorCount)
                        PrintMsg($"{name} changed at fileuid={file.fileuid} filename={file.name} logged={actual} calculated={expected}", ForceLog:=True)
                    End If
                Else
                    SetChecksumColor(file, name, Color.Green)
                End If
            End If
        Next
        Return True
    End Function

    Private Sub MarkValidationProgress(file As ltfsindex.file,
                                       includeTotal As Boolean,
                                       Optional bytesAlreadyReported As Boolean = False)
        If file Is Nothing Then Return
        If Not bytesAlreadyReported Then
            Threading.Interlocked.Add(CurrentBytesProcessed, Math.Max(0, file.length))
        End If
        If includeTotal Then
            Threading.Interlocked.Increment(TotalFilesProcessed)
        End If
        Threading.Interlocked.Increment(CurrentFilesProcessed)
    End Sub

    Private Function GetValidationDisplayPath(fr As FileRecord) As String
        If fr Is Nothing Then Return ""
        If Not String.IsNullOrEmpty(fr.SourcePath) Then Return fr.SourcePath
        If fr.File IsNot Nothing Then Return fr.File.fullpath
        Return ""
    End Function

    Private Sub AddValidationTotal(ByRef total As Long, value As Long)
        If value <= 0 Then Return
        If total > Long.MaxValue - value Then
            total = Long.MaxValue
        Else
            total += value
        End If
    End Sub

    Private Sub GetValidationDirectoryTotals(root As ltfsindex.directory,
                                             ByRef fileCount As Long,
                                             ByRef byteCount As Long)
        fileCount = 0
        byteCount = 0
        If root Is Nothing Then Return

        Dim pending As New Stack(Of ltfsindex.directory)
        pending.Push(root)
        While pending.Count > 0
            Dim current As ltfsindex.directory = pending.Pop()
            If current Is Nothing Then Continue While

            AddValidationTotal(fileCount, current.GetLazyDirectFileCount())
            AddValidationTotal(byteCount, current.GetLazyDirectFileByteCount())
            For Each child As ltfsindex.directory In current.EnumerateLazyDirectories()
                If child IsNot Nothing Then pending.Push(child)
            Next
        End While
    End Sub

    Private Sub ExecuteHashPlan(fileList As List(Of FileRecord),
                                overwrite As Boolean,
                                validateOnly As Boolean,
                                darkGreen As Boolean,
                                Optional resetStopFlag As Boolean = True,
                                Optional clearStopFlag As Boolean = True,
                                Optional releaseGui As Boolean = True,
                                Optional progressContext As ValidationProgressContext = Nothing)
        Dim displayTotalFiles As Long = If(progressContext Is Nothing,
                                           If(fileList Is Nothing, 0L, fileList.Count),
                                           Math.Max(0L, progressContext.TotalFiles))
        Dim fileCount As Long = If(progressContext Is Nothing, 0L, progressContext.ProcessedFiles)
        Dim errorCount As Long = If(progressContext Is Nothing, 0L, progressContext.ErrorCount)
        Try
            If progressContext Is Nothing OrElse Not progressContext.Started Then
                StartTime = Now
                CurrentBytesProcessed = 0
                CurrentFilesProcessed = 0
                If progressContext IsNot Nothing Then progressContext.Started = True
            End If
            SetStatusLight(LWStatus.Busy)
            PrintMsg(My.Resources.ResText_Hashing)
            If resetStopFlag Then StopFlag = False
            If progressContext Is Nothing Then
                UnwrittenSizeOverrideValue = 0
                UnwrittenCountOverrideValue = CULng(If(fileList Is Nothing, 0, fileList.Count))
                For Each fr As FileRecord In fileList
                    If fr IsNot Nothing AndAlso fr.File IsNot Nothing Then
                        UnwrittenSizeOverrideValue = CULng(UnwrittenSizeOverrideValue + Math.Max(0, fr.File.length))
                    End If
                Next
            Else
                UnwrittenCountOverrideValue = CULng(Math.Max(0L, progressContext.TotalFiles))
                UnwrittenSizeOverrideValue = CULng(Math.Max(0L, progressContext.TotalBytes))
            End If

            Dim workByRecord As New Dictionary(Of FileRecord, ValidationWork)
            Dim works = BuildValidationPlan(fileList, overwrite, validateOnly, workByRecord)
            Dim physicalExtentCount As Integer = works.Sum(Function(work As ValidationWork) work.Extents.Count)
            If progressContext Is Nothing Then
                PrintMsg($"Validation plan: files={fileList.Count}, unique references={works.Count}, physical extents={physicalExtentCount}", LogOnly:=True)
            Else
                PrintMsg($"Validation plan: files={displayTotalFiles} (batch={fileList.Count}), unique references={works.Count}, physical extents={physicalExtentCount}", LogOnly:=True)
            End If

            Dim completedRecords As New HashSet(Of FileRecord)
            Dim reportWorkCompleted As Action(Of ValidationWork) =
                Sub(work As ValidationWork)
                    If work Is Nothing OrElse work.Result Is Nothing Then Return
                    For Each member As FileRecord In work.Members
                        If StopFlag Then Exit For
                        If member Is Nothing OrElse member.File Is Nothing OrElse completedRecords.Contains(member) Then Continue For

                        fileCount += 1
                        If progressContext IsNot Nothing Then progressContext.ProcessedFiles = fileCount
                        PrintMsg($"{My.Resources.ResText_Hashing} [{fileCount}/{displayTotalFiles}] {member.File.name} {My.Resources.ResText_Size}:{IOManager.FormatSize(member.File.length)}",
                                 False,
                                 $"{My.Resources.ResText_Hashing} [{fileCount}/{displayTotalFiles}] {GetValidationDisplayPath(member)} {My.Resources.ResText_Size}:{member.File.length}")
                        ApplyValidationResult(member.File, work.Result, overwrite, validateOnly, errorCount, darkGreen)
                        If progressContext IsNot Nothing Then progressContext.ErrorCount = errorCount
                        'The data bytes were counted while the tape block was
                        'read.  Only the file counter is left for this step.
                        MarkValidationProgress(member.File, True, bytesAlreadyReported:=True)
                        completedRecords.Add(member)
                    Next
                    RequestListDisplayRefresh()
                End Sub
            CalculateChecksumsWithPlan(works, reportWorkCompleted)

            For Each fr As FileRecord In fileList
                If StopFlag Then Exit For
                If fr Is Nothing OrElse fr.File Is Nothing Then Continue For
                If completedRecords.Contains(fr) Then Continue For
                fileCount += 1
                If progressContext IsNot Nothing Then progressContext.ProcessedFiles = fileCount
                Dim work As ValidationWork = Nothing
                If workByRecord.TryGetValue(fr, work) AndAlso work.Result IsNot Nothing Then
                    PrintMsg($"{My.Resources.ResText_Hashing} [{fileCount}/{displayTotalFiles}] {fr.File.name} {My.Resources.ResText_Size}:{IOManager.FormatSize(fr.File.length)}",
                             False,
                             $"{My.Resources.ResText_Hashing} [{fileCount}/{displayTotalFiles}] {GetValidationDisplayPath(fr)} {My.Resources.ResText_Size}:{fr.File.length}")
                    ApplyValidationResult(fr.File, work.Result, overwrite, validateOnly, errorCount, darkGreen)
                    If progressContext IsNot Nothing Then progressContext.ErrorCount = errorCount
                    MarkValidationProgress(fr.File, True, bytesAlreadyReported:=True)
                    completedRecords.Add(fr)
                    RequestListDisplayRefresh()
                Else
                    MarkValidationProgress(fr.File, False)
                End If
            Next
            If StopFlag Then
                PrintMsg(My.Resources.ResText_OpCancelled)
            End If
        Catch ex As OperationCanceledException When StopFlag
            PrintMsg(My.Resources.ResText_OpCancelled)
        Catch ex As Exception
            SetStatusLight(LWStatus.Err)
            Try
                Invoke(Sub() MessageBox.Show(New Form With {.TopMost = True}, ex.ToString))
            Catch
            End Try
            PrintMsg(My.Resources.ResText_HErr)
        Finally
            If progressContext Is Nothing Then
                UnwrittenSizeOverrideValue = 0
                UnwrittenCountOverrideValue = 0
            Else
                progressContext.ProcessedFiles = fileCount
                progressContext.ErrorCount = errorCount
                RequestListDisplayRefresh()
            End If
            If clearStopFlag Then StopFlag = False
            If progressContext Is Nothing Then SetStatusLight(LWStatus.Idle)
            If releaseGui Then LockGUI(False)
            If progressContext Is Nothing Then RefreshDisplay()
            PrintMsg($"{My.Resources.ResText_HFin} {fileCount - errorCount}/{displayTotalFiles} | {errorCount} {My.Resources.ResText_Error}")
        End Try
    End Sub

    Public Function CalculateChecksum(FileIndex As ltfsindex.file, Optional ByVal blk0 As Byte() = Nothing) As Dictionary(Of String, String)
        If FileIndex Is Nothing Then Return Nothing

        ' blk0 belonged to the former extent-reconstruction path.  The unified
        ' planner always follows the authoritative LTFS extent map.
        Dim fileRecord As New FileRecord With {
            .File = FileIndex,
            .SourcePath = FileIndex.fullpath}
        Dim workByRecord As New Dictionary(Of FileRecord, ValidationWork)
        Dim works = BuildValidationPlan(New List(Of FileRecord) From {fileRecord}, True, False, workByRecord)
        If works.Count = 0 Then Return Nothing

        CalculateChecksumsWithPlan(works)
        Return works(0).Result
    End Function
    Private Sub 生成标签ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 生成标签ToolStripMenuItem.Click
        If My.Settings.LTFSWriter_FileLabel = "" Then
            设置标签ToolStripMenuItem_Click(sender, e)
            If My.Settings.LTFSWriter_FileLabel = "" Then Exit Sub
        End If
        If ListView1.Tag IsNot Nothing Then
            Dim d As ltfsindex.directory = DirectCast(ListView1.Tag, ltfsindex.directory)
            For Each dir As ltfsindex.directory In d.EnumerateLazyDirectories()
                If My.Settings.LTFSWriter_FileLabel = " " OrElse CInt(Val(dir.name)).ToString = dir.name Then
                    Dim fl As String = $".{My.Settings.LTFSWriter_FileLabel}"
                    If fl = ". " Then fl = ""
                    Dim fExist As Boolean = False
                    For Each f As ltfsindex.file In d.EnumerateLazyFiles()
                        If f.name = $"{dir.name}{fl}" Then
                            fExist = True
                            Exit For
                        End If
                    Next
                    If Not fExist Then
                        Dim emptyfile As String = IO.Path.Combine(Application.StartupPath, "empty.file")
                        IO.File.WriteAllBytes(emptyfile, {})
                        Dim fnew As New FileRecord(emptyfile, d)
                        With fnew.File
                            .name = $"{dir.name}{fl}"
                            .backuptime = Now.ToUniversalTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff00Z")
                            .creationtime = .backuptime
                            .modifytime = .backuptime
                            .accesstime = .backuptime
                            .changetime = .modifytime
                        End With
                        AddPendingRecord(fnew)
                    End If
                End If
            Next
            PrintMsg(My.Resources.ResText_OpSucc)
            SetStatusLight(LWStatus.Idle)
            RefreshDisplay()
        End If
    End Sub

    Private Sub 设置标签ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 设置标签ToolStripMenuItem.Click
        If (DisplayHelper.ShowInputDialog(My.Resources.ResText_DLS, My.Resources.ResText_DLT, My.Settings.LTFSWriter_FileLabel) <> DialogResult.OK) Then Exit Sub
        PrintMsg($"{My.Resources.ResText_DLFin} .{My.Settings.LTFSWriter_FileLabel}")
    End Sub

    Private Sub ImportFilesButton_Click(sender As Object, e As EventArgs) Handles ImportFilesButton.Click
        导入文件ToolStripMenuItem_Click(sender, e)
    End Sub

    Private Sub WriteTapeButton_Click(sender As Object, e As EventArgs) Handles WriteTapeButton.Click
        写入数据ToolStripMenuItem_Click(sender, e)
    End Sub

    Private Sub BackupIndexButton_Click(sender As Object, e As EventArgs) Handles BackupIndexButton.Click
        备份当前索引ToolStripMenuItem_Click(sender, e)
    End Sub

    Private Sub SafeEjectButton_Click(sender As Object, e As EventArgs) Handles SafeEjectButton.Click
        If MessageBox.Show(New Form With {.TopMost = True}, My.Resources.ResText_UIE, My.Resources.ResText_Confirm, MessageBoxButtons.OKCancel) = DialogResult.Cancel Then Exit Sub
        Dim th As New Threading.Thread(
                Sub()
                    Try
                        SetStatusLight(LWStatus.Busy)
                        If (My.Settings.LTFSWriter_ForceIndex OrElse TotalBytesUnindexed <> 0) AndAlso schema IsNot Nothing AndAlso schema.location.partition = ltfsindex.PartitionLabel.b Then
                            PrintMsg(My.Resources.ResText_UDI)
                            If LocateToWritePosition() Then WriteCurrentIndex(False)
                            TapeUtils.Flush(driveHandle)
                        End If
                        AutoDump()
                        PrintMsg(My.Resources.ResText_UI)
                        RefreshIndexPartition()
                        TapeUtils.ReleaseUnit(driveHandle)
                        TapeUtils.AllowMediumRemoval(driveHandle)
                        PrintMsg(My.Resources.ResText_IUd)
                        If schema IsNot Nothing AndAlso schema.location.partition = ltfsindex.PartitionLabel.a Then Invoke(Sub() 更新数据区索引ToolStripMenuItem.Enabled = False)
                        SetStatusLight(LWStatus.Busy)
                        TapeUtils.LoadEject(driveHandle, TapeUtils.LoadOption.Eject)
                        PrintMsg(My.Resources.ResText_Ejd)
                        Invoke(Sub()
                                   SetStatusLight(LWStatus.Succ)
                                   LockGUI(False)
                                   RefreshDisplay()
                                   RaiseEvent TapeEjected()
                               End Sub)
                    Catch ex As Exception
                        PrintMsg(My.Resources.ResText_IUErr, TooltipText:=$"{My.Resources.ResText_IUErr}{vbCrLf}{ex.ToString()}")
                        SetStatusLight(LWStatus.Err)
                        LockGUI(False)
                    End Try
                End Sub)
        LockGUI(True)
        th.Start()

    End Sub

    Private Sub MergeHashButton_Click(sender As Object, e As EventArgs) Handles MergeHashButton.Click
        合并SHA1ToolStripMenuItem_Click(sender, e)
    End Sub

    Private Sub 校验源文件ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 校验源文件ToolStripMenuItem.Click
        Dim baseDirectory As String = SelectWriterFolder()
        If String.IsNullOrEmpty(baseDirectory) Then Exit Sub
        SetStatusLight(LWStatus.Busy)
        Dim hw As New HashTaskWindow With {.schema = schema, .BaseDirectory = baseDirectory, .TargetDirectory = "", .DisableSkipInfo = True}
        Dim p As String = ""
        If OpenFileDialog1.FileName <> "" Then p = New IO.FileInfo(OpenFileDialog1.FileName).DirectoryName
        hw.schPath = Barcode & ".schema"
        If IO.Directory.Exists(p) Then
            hw.schPath = IO.Path.Combine(p, hw.schPath)
        End If
        hw.CheckBox2.Visible = False
        hw.CheckBox3.Visible = False
        hw.Button3.Visible = False
        hw.Button4.Visible = False
        Dim errCount As Integer = 0
        AddHandler hw.SHA1Changed, Sub(f As ltfsindex.file, msg As String)
                                       PrintMsg($"SHA1 mismatch:[FID {f.fileuid}] {msg} {f.fullpath}", ForceLog:=True, LogOnly:=True)
                                       Threading.Interlocked.Increment(errCount)
                                   End Sub
        hw.ShowDialog()
        Dim q As New Stack(Of ltfsindex.directory)
        Dim hcount As Integer = 0, fcount As Integer = 0
        For Each d As ltfsindex.directory In schema._directory
            q.Push(d)
        Next
        For Each f As ltfsindex.file In schema._file
            Threading.Interlocked.Increment(fcount)
            If f.sha1 IsNot Nothing AndAlso f.sha1.Length = 40 Then Threading.Interlocked.Increment(hcount)
        Next
        While q.Count > 0
            Dim d As ltfsindex.directory = q.Pop()
            For Each f1 As ltfsindex.file In d.EnumerateLazyFiles()
                Threading.Interlocked.Increment(fcount)
                If f1.sha1 IsNot Nothing AndAlso f1.sha1.Length = 40 Then Threading.Interlocked.Increment(hcount)
            Next
            For Each d1 As ltfsindex.directory In d.EnumerateLazyDirectories()
                q.Push(d1)
            Next
        End While
        PrintMsg($"{hcount}/{fcount}{If(errCount > 0, $"({errCount} mismatch)", "")}")
        SetStatusLight(LWStatus.Idle)
        If TotalBytesUnindexed = 0 Then TotalBytesUnindexed = 1
        RefreshDisplay()
    End Sub

    Private Sub VerifySourceButton_Click(sender As Object, e As EventArgs) Handles VerifySourceButton.Click
        校验源文件ToolStripMenuItem_Click(sender, e)
    End Sub

    Private Sub 限速不限制ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 限速不限制ToolStripMenuItem.Click
        If DisplayHelper.ShowInputDialog(My.Resources.ResText_WLimS, My.Resources.ResText_Setting, SpeedLimit) <> DialogResult.OK Then Exit Sub
    End Sub

    Private Sub 重装带前清洁次数3ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 重装带前清洁次数3ToolStripMenuItem.Click
        DisplayHelper.ShowInputDialog(My.Resources.ResText_CLNCS, My.Resources.ResText_Setting, CleanCycle)
    End Sub
    Public Sub HashSelectedFiles(Overwrite As Boolean, ValidOnly As Boolean)
        Dim selectedItems As List(Of ListViewItem) = GetSelectedListViewItems()
        If selectedItems.Count = 0 Then Return

        Dim selectedFiles As New List(Of FileRecord)
        For Each item As ListViewItem In selectedItems
            If TypeOf item.Tag Is ltfsindex.file Then
                Dim selectedFile As ltfsindex.file = DirectCast(item.Tag, ltfsindex.file)
                selectedFiles.Add(New FileRecord With {
                    .File = selectedFile,
                    .SourcePath = IO.Path.Combine(If(_lastSelectedFolder, ""), If(selectedFile.name, ""))
                })
            End If
        Next
        If selectedFiles.Count = 0 Then Return

        LockGUI()
        Dim validationThread As New Threading.Thread(
            Sub() ExecuteHashPlan(selectedFiles, Overwrite, ValidOnly, True))
        validationThread.Start()
    End Sub

    Private Sub 计算并更新ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 计算并更新ToolStripMenuItem.Click
        HashSelectedFiles(True, False)
    End Sub

    Private Sub 计算并跳过已有校验ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 计算并跳过已有校验ToolStripMenuItem.Click
        HashSelectedFiles(False, False)
    End Sub
    Private Sub 仅验证ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 仅验证ToolStripMenuItem.Click
        HashSelectedFiles(False, True)
    End Sub

    Private Iterator Function EnumerateDirectoryFileRecords(directory As ltfsindex.directory,
                                                             outputPath As String) As IEnumerable(Of FileRecord)
        If directory Is Nothing Then Exit Function
        Dim pending As New Stack(Of Tuple(Of ltfsindex.directory, String))
        pending.Push(Tuple.Create(directory, If(outputPath, String.Empty)))
        While pending.Count > 0
            Dim current As Tuple(Of ltfsindex.directory, String) = pending.Pop()
            Dim currentPath As String = current.Item2
            For Each file As ltfsindex.file In current.Item1.EnumerateLazyFiles()
                Yield New FileRecord With {
                    .File = file,
                    .SourcePath = IO.Path.Combine(currentPath, If(file.name, String.Empty))}
            Next
            For Each childDirectory As ltfsindex.directory In current.Item1.EnumerateLazyDirectories()
                For Each nestedRecord As FileRecord In EnumerateDirectoryFileRecords(childDirectory,
                                                                                      IO.Path.Combine(currentPath, If(childDirectory.name, String.Empty)))
                    Yield nestedRecord
                Next
            Next
        End While
    End Function

    Private Sub ExecuteHashPlanBatched(selectedDir As ltfsindex.directory,
                                       overwrite As Boolean,
                                       validateOnly As Boolean)
        Const BatchSize As Integer = 1024
        Dim batch As New List(Of FileRecord)(BatchSize)
        Dim progressContext As New ValidationProgressContext
        StopFlag = False
        Try
            GetValidationDirectoryTotals(selectedDir,
                                         progressContext.TotalFiles,
                                         progressContext.TotalBytes)
            For Each record As FileRecord In EnumerateDirectoryFileRecords(selectedDir, String.Empty)
                If StopFlag Then Exit For
                batch.Add(record)
                If batch.Count >= BatchSize Then
                    ExecuteHashPlan(batch, overwrite, validateOnly, False,
                                    resetStopFlag:=False,
                                    clearStopFlag:=False,
                                    releaseGui:=False,
                                    progressContext:=progressContext)
                    batch.Clear()
                End If
            Next
            If Not StopFlag AndAlso batch.Count > 0 Then
                ExecuteHashPlan(batch, overwrite, validateOnly, False,
                                resetStopFlag:=False,
                                clearStopFlag:=False,
                                releaseGui:=False,
                                progressContext:=progressContext)
            End If
        Finally
            batch.Clear()
            UnwrittenSizeOverrideValue = 0
            UnwrittenCountOverrideValue = 0
            StopFlag = False
            SetStatusLight(LWStatus.Idle)
            LockGUI(False)
            RefreshDisplay()
        End Try
    End Sub

    Public Sub HashSelectedDir(selectedDir As ltfsindex.directory, Overwrite As Boolean, ValidateOnly As Boolean)
        If selectedDir Is Nothing Then Return

        PrintMsg(My.Resources.ResText_PrepFile)
        Dim validationThread As New Threading.Thread(
            Sub() ExecuteHashPlanBatched(selectedDir, Overwrite, ValidateOnly))
        LockGUI()
        validationThread.Start()
    End Sub

    Private Sub 计算并更新ToolStripMenuItem1_Click(sender As Object, e As EventArgs) Handles 计算并更新ToolStripMenuItem1.Click
        Dim nodes As List(Of TreeNode) = SelectedNodes
        If nodes.Count > 0 Then
            Dim selectedDir As New ltfsindex.directory
            For Each n As TreeNode In nodes
                selectedDir.AddDirectory(DirectCast(n.Tag, ltfsindex.directory))
            Next
            HashSelectedDir(selectedDir, True, False)
        End If
    End Sub

    Private Sub 跳过已有校验ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 跳过已有校验ToolStripMenuItem.Click
        Dim nodes As List(Of TreeNode) = SelectedNodes
        If nodes.Count > 0 Then
            Dim selectedDir As New ltfsindex.directory
            For Each n As TreeNode In nodes
                selectedDir.AddDirectory(DirectCast(n.Tag, ltfsindex.directory))
            Next
            HashSelectedDir(selectedDir, False, False)
        End If
    End Sub

    Private Sub 仅验证ToolStripMenuItem1_Click(sender As Object, e As EventArgs) Handles 仅验证ToolStripMenuItem1.Click
        Dim nodes As List(Of TreeNode) = SelectedNodes
        If nodes.Count > 0 Then
            Dim selectedDir As New ltfsindex.directory
            For Each n As TreeNode In nodes
                selectedDir.AddDirectory(DirectCast(n.Tag, ltfsindex.directory))
            Next
            HashSelectedDir(selectedDir, False, True)
        End If
    End Sub

    Private Sub 复制选中信息ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 复制选中信息ToolStripMenuItem.Click
        Dim result As New StringBuilder
        Dim selectedItems As List(Of ListViewItem) = GetSelectedListViewItems()
        If ListView1.Tag IsNot Nothing AndAlso
        selectedItems.Count > 0 Then
            For Each ItemSelected As ListViewItem In selectedItems
                If ItemSelected.Tag IsNot Nothing AndAlso TypeOf (ItemSelected.Tag) Is ltfsindex.file Then
                    Dim f As ltfsindex.file = DirectCast(ItemSelected.Tag, ltfsindex.file)
                    result.AppendLine(f.name)
                End If
            Next
        End If
        Clipboard.SetText(result.ToString)
    End Sub

    Private Sub 统计ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 统计ToolStripMenuItem.Click
        Dim Nodes As List(Of TreeNode) = SelectedNodes
        If Nodes.Count = 0 Then Exit Sub

        Dim directories As New List(Of ltfsindex.directory)
        Dim names As New List(Of String)
        For Each n As TreeNode In Nodes
            If TypeOf n.Tag IsNot ltfsindex.directory Then Exit For
            Dim d As ltfsindex.directory = DirectCast(n.Tag, ltfsindex.directory)
            directories.Add(d)
            If names.Count = 0 OrElse String.Join("; ", names).Length <= 40 Then names.Add(If(d.name, String.Empty))
        Next
        If directories.Count = 0 Then Exit Sub

        Dim dirNames As String = String.Join("; ", names)
        Dim maxlen As Integer = 40
        If dirNames.Length > maxlen Then
            dirNames = dirNames.Substring(0, maxlen - 3) & "..."
        End If

        'Statistics used to run synchronously on the UI thread and parsed
        'every file record (including xattrs and extents).  The lazy store can
        'sum the persisted file lengths natively, so only the directory chain
        'is walked here.  Keep the UI responsive while that first calculation
        'warms the backing-store caches.
        LockGUI(True)
        Task.Run(
            Sub()
                Try
                    Dim fnum As Long = 0
                    Dim fbytes As Long = 0
                    For Each d As ltfsindex.directory In directories
                        Dim q As New Stack(Of ltfsindex.directory)
                        q.Push(d)
                        While q.Count > 0
                            Dim qd As ltfsindex.directory = q.Pop()
                            fnum += qd.GetLazyDirectFileCount()
                            fbytes += qd.GetLazyDirectFileByteCount()
                            For Each childDirectory As ltfsindex.directory In qd.EnumerateLazyDirectories()
                                q.Push(childDirectory)
                            Next
                        End While
                    Next

                    Invoke(
                        Sub()
                            Try
                                MessageBox.Show(New Form With {.TopMost = True}, $"{dirNames}{vbCrLf}{My.Resources.ResText_FCountP}{fnum}{vbCrLf}{My.Resources.ResText_FSizeP}{fbytes} {My.Resources.ResText_Byte} ({IOManager.FormatSize(fbytes)})")
                            Finally
                                LockGUI(False)
                            End Try
                        End Sub)
                Catch ex As Exception
                    Try
                        Invoke(
                            Sub()
                                Try
                                    PrintMsg($"Statistics failed: {ex}", ForceLog:=True)
                                Finally
                                    LockGUI(False)
                                End Try
                            End Sub)
                    Catch
                    End Try
                End Try
            End Sub)
    End Sub

    Private Sub 预读文件数5ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 预读文件数5ToolStripMenuItem.Click
        If DisplayHelper.ShowInputDialog(My.Resources.ResText_SPreR, My.Resources.ResText_Setting, My.Settings.LTFSWriter_PreLoadFileCount) <> DialogResult.OK Then Exit Sub
        预读文件数5ToolStripMenuItem.Text = $"{My.Resources.ResText_PFC}{My.Settings.LTFSWriter_PreLoadFileCount}"
    End Sub

    Private Sub 文件缓存32MiBToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 文件缓存32MiBToolStripMenuItem.Click
        If DisplayHelper.ShowInputDialog("设置文件缓存", My.Resources.ResText_Setting, My.Settings.LTFSWriter_PreLoadBytes) <> DialogResult.OK Then Exit Sub
        If My.Settings.LTFSWriter_PreLoadBytes <= (16 << 20) Then My.Settings.LTFSWriter_PreLoadBytes = (16 << 20)
        文件缓存32MiBToolStripMenuItem.Text = $"文件缓存：{IOManager.FormatSize(My.Settings.LTFSWriter_PreLoadBytes)}"
    End Sub

    Private Shared Sub ApplyFileDetailsEdit(target As ltfsindex.file, edited As ltfsindex.file)
        If target Is Nothing OrElse edited Is Nothing Then Return

        'PropertyGrid edits nested xattr/extent objects in place.  Use a second
        'copy here so Cancel never mutates the live lazy record, then replace the
        'collections as a whole when the user accepts the dialog.
        Dim snapshot As ltfsindex.file = edited.GetCopy(edited.fileuid)
        target.fullpath = snapshot.fullpath
        target.tag = snapshot.tag
        target.name = snapshot.name
        target.length = snapshot.length
        target.readonly = snapshot.readonly
        target.openforwrite = snapshot.openforwrite
        target.creationtime = snapshot.creationtime
        target.changetime = snapshot.changetime
        target.modifytime = snapshot.modifytime
        target.accesstime = snapshot.accesstime
        target.backuptime = snapshot.backuptime
        target.fileuid = snapshot.fileuid
        target.symlink = snapshot.symlink
        target.extendedattributes = snapshot.extendedattributes
        target.extentinfo = snapshot.extentinfo
        target.MarkLazyRecordDirty()
    End Sub

    Private Shared Function CreateDirectoryDetailsEdit(source As ltfsindex.directory) As ltfsindex.directory
        If source Is Nothing Then Return Nothing
        Return New ltfsindex.directory With {
            .name = source.name,
            .readonly = source.readonly,
            .creationtime = source.creationtime,
            .changetime = source.changetime,
            .modifytime = source.modifytime,
            .accesstime = source.accesstime,
            .backuptime = source.backuptime,
            .fileuid = source.fileuid}
    End Function

    Private Shared Sub ApplyDirectoryDetailsEdit(target As ltfsindex.directory, edited As ltfsindex.directory)
        If target Is Nothing OrElse edited Is Nothing Then Return
        target.name = edited.name
        target.readonly = edited.readonly
        target.creationtime = edited.creationtime
        target.changetime = edited.changetime
        target.modifytime = edited.modifytime
        target.accesstime = edited.accesstime
        target.backuptime = edited.backuptime
        target.fileuid = edited.fileuid
    End Sub

    Private Function IsPendingFile(directory As ltfsindex.directory, file As ltfsindex.file) As Boolean
        If directory Is Nothing OrElse file Is Nothing Then Return False
        SyncLock _pendingQueueLock
            directory = ResolvePendingDirectoryUnsafe(directory)
            If UnwrittenFiles IsNot Nothing Then
                For Each record As FileRecord In UnwrittenFiles
                    If record IsNot Nothing AndAlso
                       DirectoryRecordComparer.Instance.Equals(record.ParentDirectory, directory) AndAlso
                       record.File Is file Then Return True
                Next
            End If
            If directory.UnwrittenFiles IsNot Nothing Then
                SyncLock directory.UnwrittenFiles
                    Return directory.UnwrittenFiles.Contains(file)
                End SyncLock
            End If
        End SyncLock
        Return False
    End Function

    Private Sub SynchronizePendingFileEdit(directory As ltfsindex.directory,
                                            file As ltfsindex.file,
                                            previousLength As Long)
        If directory Is Nothing OrElse file Is Nothing Then Return
        SyncLock _pendingQueueLock
            directory = ResolvePendingDirectoryUnsafe(directory)
            If UnwrittenFiles Is Nothing Then Return
            For Each record As FileRecord In UnwrittenFiles
                If record IsNot Nothing AndAlso
                   DirectoryRecordComparer.Instance.Equals(record.ParentDirectory, directory) AndAlso
                   record.File Is file Then
                    If record.FileOffset < 0 OrElse record.FileOffset > file.length Then record.FileOffset = 0
                    record.SegmentLength = Math.Max(0L, file.length - record.FileOffset)
                    _pendingSize += file.length - previousLength
                    RemovePendingIndexUnsafe(record)
                    RegisterPendingRecordUnsafe(record)
                    Exit For
                End If
            Next
        End SyncLock
    End Sub

    Private Sub 文件详情ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 文件详情ToolStripMenuItem.Click
        Dim result As New StringBuilder
        Dim selectedItems As List(Of ListViewItem) = GetSelectedListViewItems()
        If ListView1.Tag IsNot Nothing Then
            If selectedItems.Count > 0 Then
                If selectedItems.Count > 1 Then
                    For Each ItemSelected As ListViewItem In selectedItems
                        If ItemSelected.Tag IsNot Nothing AndAlso TypeOf (ItemSelected.Tag) Is ltfsindex.file Then
                            Dim f As ltfsindex.file = DirectCast(ItemSelected.Tag, ltfsindex.file)
                            result.AppendLine(f.GetSerializedText())
                        End If
                    Next
                    MessageBox.Show(New Form With {.TopMost = True}, result.ToString)
                Else
                    Dim selectedFile As ltfsindex.file = TryCast(selectedItems(0).Tag, ltfsindex.file)
                    If selectedFile Is Nothing Then Exit Sub
                    Dim selectedDirectory As ltfsindex.directory = TryCast(ListView1.Tag, ltfsindex.directory)
                    Dim previousLength As Long = selectedFile.length
                    Dim wasPending As Boolean = IsPendingFile(selectedDirectory, selectedFile)
                    Dim editableFile As ltfsindex.file = selectedFile.GetCopy(selectedFile.fileuid)
                    Dim PG1 As New SettingPanel
                    PG1.PropertyGrid1.SelectedObject = editableFile
                    PG1.Text = $"{TextBoxSelectedPath.Text}\{selectedFile.name}"
                    If PG1.ShowDialog() = DialogResult.OK Then
                        ApplyFileDetailsEdit(selectedFile, editableFile)
                        If wasPending Then SynchronizePendingFileEdit(selectedDirectory, selectedFile, previousLength)
                        Modified = True
                        If TotalBytesUnindexed = 0 Then TotalBytesUnindexed = 1
                        RequestListDisplayRefresh()
                    End If
                End If
            Else
                Dim selectedDirectory As ltfsindex.directory = TryCast(ListView1.Tag, ltfsindex.directory)
                If selectedDirectory Is Nothing Then Exit Sub
                Dim editableDirectory As ltfsindex.directory = CreateDirectoryDetailsEdit(selectedDirectory)
                Dim PG1 As New SettingPanel
                PG1.PropertyGrid1.SelectedObject = editableDirectory
                PG1.Text = $"{TextBoxSelectedPath.Text}\{selectedDirectory.name}"
                If PG1.ShowDialog() = DialogResult.OK Then
                    ApplyDirectoryDetailsEdit(selectedDirectory, editableDirectory)
                    Modified = True
                    If TotalBytesUnindexed = 0 Then TotalBytesUnindexed = 1
                    RefreshDisplay()
                End If
            End If
        End If

    End Sub

    Private Sub 禁用分区ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 禁用分区ToolStripMenuItem.Click
        DisablePartition = 禁用分区ToolStripMenuItem.Checked
    End Sub
    Private Sub 速度下限ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 速度下限ToolStripMenuItem.Click
        Dim s As String = My.Settings.LTFSWriter_AutoCleanDownLim.ToString
        If DisplayHelper.ShowInputDialog(My.Resources.ResText_SSMin, My.Resources.ResText_Setting, s) <> DialogResult.OK Then Exit Sub
        If s = "" Then Exit Sub
        If Not Double.TryParse(s, My.Settings.LTFSWriter_AutoCleanDownLim) Then Exit Sub
        My.Settings.Save()
        速度下限ToolStripMenuItem.Text = $"{My.Resources.ResText_SMin}{My.Settings.LTFSWriter_AutoCleanDownLim} MiB/s"
    End Sub

    Private Sub 速度上限ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 速度上限ToolStripMenuItem.Click
        Dim s As String = My.Settings.LTFSWriter_AutoCleanUpperLim.ToString()
        If DisplayHelper.ShowInputDialog(My.Resources.ResText_SSMax, My.Resources.ResText_Setting, s) <> DialogResult.OK Then Exit Sub
        If s = "" Then Exit Sub
        If Not Double.TryParse(s, My.Settings.LTFSWriter_AutoCleanUpperLim) Then Exit Sub
        My.Settings.Save()
        速度上限ToolStripMenuItem.Text = $"{My.Resources.ResText_SMax}{My.Settings.LTFSWriter_AutoCleanUpperLim} MiB/s"
    End Sub

    Private Sub 持续时间ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 持续时间ToolStripMenuItem.Click
        Dim s As String = My.Settings.LTFSWriter_AutoCleanTimeThreashould.ToString()
        If DisplayHelper.ShowInputDialog(My.Resources.ResText_SSTime, My.Resources.ResText_Setting, s) <> DialogResult.OK Then Exit Sub
        If s = "" Then Exit Sub
        If Not Integer.TryParse(s, My.Settings.LTFSWriter_AutoCleanTimeThreashould) Then Exit Sub
        My.Settings.Save()
        持续时间ToolStripMenuItem.Text = $"{My.Resources.ResText_STime}{My.Settings.LTFSWriter_AutoCleanTimeThreashould}s"
    End Sub

    Private Sub 去重SHA1ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 去重ToolStripMenuItem.Click
        My.Settings.LTFSWriter_DeDupe = Not My.Settings.LTFSWriter_DeDupe
        去重ToolStripMenuItem.Checked = My.Settings.LTFSWriter_DeDupe
        My.Settings.Save()
    End Sub
    <TypeConverter(GetType(ExpandableObjectConverter))>
    Public Class LTFSMountFSBase
        Inherits Fsp.FileSystemBase
        Public LW As LTFSWriter
        Public VolumeLabel As String
        Public TapeDrive As String
        Public Const ALLOCATION_UNIT As Integer = 4096

        Protected Shared Sub ThrowIoExceptionWithHResult(ByVal HResult As Int32)
            Throw New IO.IOException(Nothing, HResult)
        End Sub

        Protected Shared Sub ThrowIoExceptionWithWin32(ByVal [Error] As Int32)
            ThrowIoExceptionWithHResult(CType((2147942400 Or [Error]), Int32))
            'TODO: checked/unchecked is not supported at this time
        End Sub
        Protected Shared Sub ThrowIoExceptionWithNtStatus(ByVal Status As Int32)
            ThrowIoExceptionWithWin32(CType(Win32FromNtStatus(Status), Int32))
        End Sub
        Public Overrides Function ExceptionHandler(ByVal ex As Exception) As Int32
            Dim HResult As Int32 = ex.HResult
            If (2147942400 _
                    = (HResult And 4294901760)) Then
                Return NtStatusFromWin32(CUInt((CType(HResult, UInt32) And 65535)))
            End If
            Return STATUS_UNEXPECTED_IO_ERROR
        End Function
        <TypeConverter(GetType(ExpandableObjectConverter))>
        Class FileDesc
            Public IsDirectory As Boolean
            Public LTFSFile As ltfsindex.file
            Public LTFSDirectory As ltfsindex.directory
            Public Parent As ltfsindex.directory

            ' Directory enumeration is intentionally kept as an iterator.  A
            ' SortedList of every child made a single FUSE listing allocate
            ' one object for every file in the directory.
            Public DirectoryEnumerator As IEnumerator(Of MountedDirectoryEntry)
            Public DirectoryEnumerationPattern As String
            Public DirectoryEnumerationIndex As Long


            Public Enum dwFilAttributesValue As UInteger
                FILE_ATTRIBUTE_ARCHIVE = &H20
                FILE_ATTRIBUTE_COMPRESSED = &H800
                FILE_ATTRIBUTE_DIRECTORY = &H10
                FILE_ATTRIBUTE_ENCRYPTED = &H4000
                FILE_ATTRIBUTE_HIDDEN = &H2
                FILE_ATTRIBUTE_NORMAL = &H80
                FILE_ATTRIBUTE_OFFLINE = &H1000
                FILE_ATTRIBUTE_READONLY = &H1
                FILE_ATTRIBUTE_REPARSE_POINT = &H400
                FILE_ATTRIBUTE_SPARSE_FILE = &H200
                FILE_ATTRIBUTE_SYSTEM = &H4
                FILE_ATTRIBUTE_TEMPORARY = &H100
                FILE_ATTRIBUTE_VIRTUAL = &H10000
            End Enum

            Public Function GetFileInfo(ByRef FileInfo As Fsp.Interop.FileInfo) As Int32
                If (Not IsDirectory) Then
                    FileInfo.FileAttributes = dwFilAttributesValue.FILE_ATTRIBUTE_OFFLINE Or dwFilAttributesValue.FILE_ATTRIBUTE_ARCHIVE
                    If LTFSFile.readonly Then FileInfo.FileAttributes = FileInfo.FileAttributes Or dwFilAttributesValue.FILE_ATTRIBUTE_READONLY
                    FileInfo.ReparseTag = 0
                    FileInfo.FileSize = CULng(LTFSFile.length)
                    FileInfo.AllocationSize = CULng((FileInfo.FileSize + ALLOCATION_UNIT - 1) / ALLOCATION_UNIT * ALLOCATION_UNIT)
                    FileInfo.CreationTime = CULng(TapeUtils.ParseTimeStamp(LTFSFile.creationtime).ToFileTimeUtc)
                    FileInfo.LastAccessTime = CULng(TapeUtils.ParseTimeStamp(LTFSFile.accesstime).ToFileTimeUtc)
                    FileInfo.LastWriteTime = CULng(TapeUtils.ParseTimeStamp(LTFSFile.changetime).ToFileTimeUtc)
                    FileInfo.ChangeTime = CULng(TapeUtils.ParseTimeStamp(LTFSFile.changetime).ToFileTimeUtc)
                    FileInfo.IndexNumber = 0
                    FileInfo.HardLinks = 0
                Else
                    FileInfo.FileAttributes = dwFilAttributesValue.FILE_ATTRIBUTE_OFFLINE Or dwFilAttributesValue.FILE_ATTRIBUTE_DIRECTORY
                    FileInfo.ReparseTag = 0
                    FileInfo.FileSize = 0
                    FileInfo.AllocationSize = 0
                    FileInfo.CreationTime = CULng(TapeUtils.ParseTimeStamp(LTFSDirectory.creationtime).ToFileTimeUtc)
                    FileInfo.LastAccessTime = CULng(TapeUtils.ParseTimeStamp(LTFSDirectory.accesstime).ToFileTimeUtc)
                    FileInfo.LastWriteTime = CULng(TapeUtils.ParseTimeStamp(LTFSDirectory.changetime).ToFileTimeUtc)
                    FileInfo.ChangeTime = CULng(TapeUtils.ParseTimeStamp(LTFSDirectory.changetime).ToFileTimeUtc)
                    FileInfo.IndexNumber = 0
                    FileInfo.HardLinks = 0
                End If
                Return STATUS_SUCCESS
            End Function

            Public Function GetFileAttributes() As UInt32
                Dim FileInfo As Fsp.Interop.FileInfo
                Me.GetFileInfo(FileInfo)
                Return FileInfo.FileAttributes
            End Function

        End Class

        Public NotInheritable Class MountedDirectoryEntry
            Public Property Name As String
            Public Property Value As Object
        End Class

        Public Overrides Function Init(Host0 As Object) As Integer
            Dim Host As Fsp.FileSystemHost = CType(Host0, Fsp.FileSystemHost)
            Try
                Host.FileInfoTimeout = 10 * 1000
                Host.FileSystemName = "LTFS"
                Host.SectorSize = 4096
                Host.SectorsPerAllocationUnit = CUShort(LW.plabel.blocksize \ Host.SectorSize)
                Host.VolumeCreationTime = CULng(TapeUtils.ParseTimeStamp(LW.plabel.formattime).ToFileTimeUtc())
                Host.VolumeSerialNumber = 0
                Host.CaseSensitiveSearch = False
                Host.CasePreservedNames = True
                Host.UnicodeOnDisk = True
                Host.PersistentAcls = False
                Host.ReparsePoints = False
                Host.ReparsePointsAccessCheck = False
                Host.NamedStreams = False
                Host.PostCleanupWhenModifiedOnly = True
                Host.FlushAndPurgeOnCleanup = True
                Host.PassQueryDirectoryPattern = True
                Host.MaxComponentLength = 4096

            Catch ex As Exception
                Dim ActiveFrm = ApplicationWheels.GetActiveWindow()
                ActiveFrm.Invoke(Sub() MessageBox.Show(New Form With {.TopMost = True}, $"{ex.ToString}"))
            End Try
            Return STATUS_SUCCESS
        End Function
        Public Sub New(path0 As String)
            TapeDrive = path0
        End Sub
        Private fbytes As Long = 0

        Public Overrides Function GetVolumeInfo(<Out> ByRef VolumeInfo As VolumeInfo) As Int32
            VolumeInfo = New VolumeInfo()
            VolumeLabel = LW.schema._directory(0).name
            Try
                VolumeInfo.TotalSize = CULng(TapeUtils.MAMAttribute.FromTapeDrive(LW.TapeDrive, 0, 1, LW.ExtraPartitionCount).AsNumeric << 20)
                VolumeInfo.FreeSize = CULng(TapeUtils.MAMAttribute.FromTapeDrive(LW.TapeDrive, 0, 0, LW.ExtraPartitionCount).AsNumeric << 20)
                'VolumeInfo.SetVolumeLabel(VolumeLabel)
            Catch ex As Exception
                LW.PrintMsg(ex.ToString(), LogOnly:=True)
                If fbytes = 0 Then
                    Dim q As New Stack(Of ltfsindex.directory)
                    q.Push(LW.schema._directory(0))
                    While q.Count > 0
                        Dim qd As ltfsindex.directory = q.Pop()
                        For Each qf As ltfsindex.file In qd.EnumerateLazyFiles()
                            Threading.Interlocked.Add(fbytes, qf.length)
                        Next
                        For Each childDirectory As ltfsindex.directory In qd.EnumerateLazyDirectories()
                            q.Push(childDirectory)
                        Next
                    End While
                End If
                VolumeInfo.TotalSize = CULng(fbytes)
                VolumeInfo.FreeSize = 0
            End Try
            Return STATUS_SUCCESS
        End Function

        Public Overrides Function GetSecurityByName(FileName As String, ByRef FileAttributes As UInteger, ByRef SecurityDescriptor() As Byte) As Integer
            If LW.schema._directory.Count = 0 Then Throw New Exception("Not LTFS formatted")
            Dim path As String() = FileName.Split({"\"}, StringSplitOptions.RemoveEmptyEntries)
            Dim filedesc As New FileDesc
            Dim FileInfo As New Fsp.Interop.FileInfo
            If path.Length = 0 Then
                filedesc = New FileDesc With {.IsDirectory = True, .LTFSDirectory = LW.schema._directory(0)}
                filedesc.GetFileInfo(FileInfo)
                FileAttributes = FileInfo.FileAttributes
                Return STATUS_SUCCESS
            End If
            Dim FileExist As Boolean = False

            Dim LTFSDir As ltfsindex.directory = LW.schema._directory(0)
            For i As Integer = 0 To path.Length - 2
                Dim childDirectory As ltfsindex.directory = LTFSDir.FindDirectoryByName(path(i))
                If childDirectory Is Nothing Then Return STATUS_NOT_FOUND
                LTFSDir = childDirectory
            Next
            Dim foundDirectory As ltfsindex.directory = LTFSDir.FindDirectoryByName(path(path.Length - 1))
            If foundDirectory IsNot Nothing Then
                FileExist = True
                filedesc = New FileDesc With {.IsDirectory = True, .LTFSDirectory = foundDirectory}
            End If
            If Not FileExist Then
                Dim foundFile As ltfsindex.file = LTFSDir.FindFileByName(path(path.Length - 1))
                If foundFile IsNot Nothing Then
                    FileExist = True
                    filedesc = New FileDesc With {.IsDirectory = False, .LTFSFile = foundFile, .Parent = LTFSDir}
                End If
            End If
            If FileExist Then
                filedesc.GetFileInfo(FileInfo)
            End If
            FileAttributes = FileInfo.FileAttributes
            Return STATUS_SUCCESS
        End Function
        Public Overrides Function Open(FileName As String,
                                       CreateOptions As UInteger,
                                       GrantedAccess As UInteger,
                                       ByRef FileNode As Object,
                                       ByRef FileDesc As Object,
                                       ByRef FileInfo As Fsp.Interop.FileInfo,
                                       ByRef NormalizedName As String) As Integer
            Try
                'FileNode = New Object()
                NormalizedName = ""
                If LW.schema._directory.Count = 0 Then Throw New Exception("Not LTFS formatted")
                Dim path As String() = FileName.Split({"\"}, StringSplitOptions.RemoveEmptyEntries)
                If path.Length = 0 Then
                    FileDesc = New FileDesc With {.IsDirectory = True, .LTFSDirectory = LW.schema._directory(0)}
                    Dim status As Integer = CType(FileDesc, FileDesc).GetFileInfo(FileInfo)
                    Return status
                End If
                Dim FileExist As Boolean = False

                Dim LTFSDir As ltfsindex.directory = LW.schema._directory(0)
                For i As Integer = 0 To path.Length - 2
                    Dim childDirectory As ltfsindex.directory = LTFSDir.FindDirectoryByName(path(i))
                    If childDirectory Is Nothing Then Return STATUS_NOT_FOUND
                    LTFSDir = childDirectory
                Next
                Dim foundDirectory As ltfsindex.directory = LTFSDir.FindDirectoryByName(path(path.Length - 1))
                If foundDirectory IsNot Nothing Then
                    FileExist = True
                    FileDesc = New FileDesc With {.IsDirectory = True, .LTFSDirectory = foundDirectory}
                End If
                If Not FileExist Then
                    Dim foundFile As ltfsindex.file = LTFSDir.FindFileByName(path(path.Length - 1))
                    If foundFile IsNot Nothing Then
                        FileExist = True
                        FileDesc = New FileDesc With {.IsDirectory = False, .LTFSFile = foundFile, .Parent = LTFSDir}
                    End If
                End If
                If FileExist Then
                    Dim status As Integer = CType(FileDesc, FileDesc).GetFileInfo(FileInfo)
                    FileInfo = FileInfo
                    Return status
                End If
            Catch ex As Exception
                Throw
            End Try
            Return STATUS_NOT_FOUND
        End Function
        Public Overrides Sub Close(FileNode As Object, FileDesc As Object)

        End Sub
        Public Overrides Function Read(FileNode As Object,
                                       FileDesc As Object,
                                       Buffer As IntPtr,
                                       Offset As ULong,
                                       Length As UInteger,
                                       ByRef BytesTransferred As UInteger) As Integer
            If FileDesc Is Nothing OrElse TypeOf FileDesc IsNot FileDesc Then Return STATUS_NOT_FOUND
            Try
                With CType(FileDesc, FileDesc)
                    If .IsDirectory Then Return STATUS_NOT_FOUND
                    If .LTFSFile Is Nothing Then Return STATUS_NOT_FOUND
                    If Offset >= .LTFSFile.length Then ThrowIoExceptionWithNtStatus(STATUS_END_OF_FILE)
                    .LTFSFile.extentinfo.Sort(New Comparison(Of ltfsindex.file.extent)(Function(a As ltfsindex.file.extent, b As ltfsindex.file.extent) As Integer
                                                                                           Return (a.fileoffset).CompareTo(b.fileoffset)
                                                                                       End Function))
                    Dim BufferOffset As Long = CLng(Offset)
                    For ei As Integer = 0 To .LTFSFile.extentinfo.Count - 1
                        With .LTFSFile.extentinfo(ei)
                            If Offset >= .fileoffset + .bytecount Then Continue For
                            Dim CurrentFileOffset As Long = .fileoffset

                            TapeUtils.Locate(TapeDrive:=TapeDrive, BlockAddress:=CULng(.startblock), Partition:=LW.GetPartitionNumber(CType(.partition, ltfslabel.PartitionLabel)))

                            Dim blkBuffer As Byte() = TapeUtils.ReadBlock(TapeDrive)
                            CurrentFileOffset += blkBuffer.Length - .byteoffset
                            While CurrentFileOffset <= Offset
                                blkBuffer = TapeUtils.ReadBlock(TapeDrive)
                                CurrentFileOffset += blkBuffer.Length
                            End While
                            Dim FirstBlockByteOffset As Integer = CInt(blkBuffer.Length - (CurrentFileOffset - Offset))
                            Marshal.Copy(blkBuffer, FirstBlockByteOffset, Buffer, CInt(Math.Min(Length, blkBuffer.Length - FirstBlockByteOffset)))
                            BufferOffset += Math.Min(Length, blkBuffer.Length - FirstBlockByteOffset)
                            BytesTransferred = CUInt(BytesTransferred + Math.Min(Length, blkBuffer.Length - FirstBlockByteOffset))
                            While BufferOffset < .bytecount AndAlso BufferOffset < Length
                                blkBuffer = TapeUtils.ReadBlock(TapeDrive)
                                Marshal.Copy(blkBuffer, 0, New IntPtr(Buffer.ToInt64 + BufferOffset), CInt(Math.Min(Length - BufferOffset, Math.Min(blkBuffer.Length, .bytecount - BufferOffset))))
                                BufferOffset += Math.Min(blkBuffer.Length, .bytecount - BufferOffset)
                            End While
                        End With
                    Next
                    Return STATUS_SUCCESS
                End With
            Catch ex As Exception
                Return STATUS_FILE_CORRUPT_ERROR
            End Try
        End Function
        Public Overrides Function GetFileInfo(FileNode As Object, FileDesc As Object, ByRef FileInfo As Fsp.Interop.FileInfo) As Integer
            Dim result As Integer = CType(FileDesc, FileDesc).GetFileInfo(FileInfo)
            Return result
        End Function

        Private Iterator Function EnumerateMountedDirectoryEntries(directory As ltfsindex.directory,
                                                                    parent As ltfsindex.directory,
                                                                    pattern As String) As IEnumerable(Of MountedDirectoryEntry)
            If directory Is Nothing Then Exit Function

            If parent IsNot Nothing Then
                Yield New MountedDirectoryEntry With {.Name = ".", .Value = directory}
                Yield New MountedDirectoryEntry With {.Name = "..", .Value = parent}
            End If

            Dim matchPattern As String = If(String.IsNullOrEmpty(pattern), "*", pattern).ToLowerInvariant()
            For Each childDirectory As ltfsindex.directory In directory.EnumerateLazyDirectories()
                Dim childName As String = If(childDirectory.name, String.Empty)
                If childName.ToLowerInvariant() Like matchPattern Then
                    Yield New MountedDirectoryEntry With {.Name = childName, .Value = childDirectory}
                End If
            Next
            For Each childFile As ltfsindex.file In directory.EnumerateLazyFiles()
                Dim childName As String = If(childFile.name, String.Empty)
                If childName.ToLowerInvariant() Like matchPattern Then
                    Yield New MountedDirectoryEntry With {.Name = childName, .Value = childFile}
                End If
            Next
        End Function

        Private Function TryGetNextMountedDirectoryEntry(fileDesc As FileDesc,
                                                          pattern As String,
                                                          marker As String,
                                                          ByRef context As Object,
                                                          ByRef result As MountedDirectoryEntry) As Boolean
            If fileDesc Is Nothing OrElse fileDesc.LTFSDirectory Is Nothing Then Return False

            Dim matchPattern As String = If(String.IsNullOrEmpty(pattern), "*", pattern)
            If context Is Nothing Then
                If fileDesc.DirectoryEnumerator IsNot Nothing Then
                    fileDesc.DirectoryEnumerator.Dispose()
                End If
                fileDesc.DirectoryEnumerationPattern = matchPattern
                fileDesc.DirectoryEnumerationIndex = 0
                fileDesc.DirectoryEnumerator = EnumerateMountedDirectoryEntries(fileDesc.LTFSDirectory,
                                                                                 fileDesc.Parent,
                                                                                 matchPattern).GetEnumerator()
            ElseIf fileDesc.DirectoryEnumerator Is Nothing OrElse
                   Not String.Equals(fileDesc.DirectoryEnumerationPattern, matchPattern, StringComparison.Ordinal) Then
                ' Context is normally paired with the same FileDesc.  Rebuild
                ' defensively if the filesystem provider resumes with a stale
                ' context, while still consuming only one entry at a time.
                If fileDesc.DirectoryEnumerator IsNot Nothing Then
                    fileDesc.DirectoryEnumerator.Dispose()
                End If
                fileDesc.DirectoryEnumerationPattern = matchPattern
                fileDesc.DirectoryEnumerationIndex = 0
                fileDesc.DirectoryEnumerator = EnumerateMountedDirectoryEntries(fileDesc.LTFSDirectory,
                                                                                 fileDesc.Parent,
                                                                                 matchPattern).GetEnumerator()
                Dim resumeIndex As Long = 0
                If TypeOf context Is Long Then resumeIndex = CLng(context)
                While resumeIndex > 0 AndAlso fileDesc.DirectoryEnumerator.MoveNext()
                    resumeIndex -= 1
                End While
                fileDesc.DirectoryEnumerationIndex = CLng(context)
            End If

            Dim markerText As String = If(marker, String.Empty)
            While fileDesc.DirectoryEnumerator.MoveNext()
                Dim candidate As MountedDirectoryEntry = fileDesc.DirectoryEnumerator.Current
                If context Is Nothing AndAlso markerText.Length > 0 AndAlso
                   String.Compare(candidate.Name, markerText, StringComparison.Ordinal) <= 0 Then
                    Continue While
                End If
                fileDesc.DirectoryEnumerationIndex += 1
                context = fileDesc.DirectoryEnumerationIndex
                result = candidate
                Return True
            End While

            result = Nothing
            Return False
        End Function

        Public Overrides Function ReadDirectoryEntry(FileNode As Object, FileDesc0 As Object, Pattern As String, Marker As String, ByRef Context As Object, <Out> ByRef FileName As String, <Out> ByRef FileInfo As Fsp.Interop.FileInfo) As Boolean
            Dim FileDesc As FileDesc = CType(FileDesc0, FileDesc)
            Pattern = If(Pattern, "*").Replace("<", "*").Replace(">", "?").Replace("""", ".")
            Dim entry As MountedDirectoryEntry = Nothing
            If TryGetNextMountedDirectoryEntry(FileDesc, Pattern, Marker, Context, entry) Then
                FileName = entry.Name
                FileInfo = New Fsp.Interop.FileInfo()
                If TypeOf entry.Value Is ltfsindex.directory Then
                    With CType(entry.Value, ltfsindex.directory)
                        FileInfo.FileAttributes = FileDesc.dwFilAttributesValue.FILE_ATTRIBUTE_OFFLINE Or FileDesc.dwFilAttributesValue.FILE_ATTRIBUTE_DIRECTORY
                        FileInfo.ReparseTag = 0
                        FileInfo.FileSize = 0
                        FileInfo.AllocationSize = 0
                        FileInfo.CreationTime = CULng(TapeUtils.ParseTimeStamp(.creationtime).ToFileTimeUtc)
                        FileInfo.LastAccessTime = CULng(TapeUtils.ParseTimeStamp(.accesstime).ToFileTimeUtc)
                        FileInfo.LastWriteTime = CULng(TapeUtils.ParseTimeStamp(.changetime).ToFileTimeUtc)
                        FileInfo.ChangeTime = CULng(TapeUtils.ParseTimeStamp(.changetime).ToFileTimeUtc)
                        FileInfo.IndexNumber = 0
                        FileInfo.HardLinks = 0
                    End With
                ElseIf TypeOf entry.Value Is ltfsindex.file Then
                    With CType(entry.Value, ltfsindex.file)
                        FileInfo.FileAttributes = FileDesc.dwFilAttributesValue.FILE_ATTRIBUTE_OFFLINE Or FileDesc.dwFilAttributesValue.FILE_ATTRIBUTE_ARCHIVE
                        If .readonly Then FileInfo.FileAttributes = FileInfo.FileAttributes Or FileDesc.dwFilAttributesValue.FILE_ATTRIBUTE_READONLY
                        FileInfo.ReparseTag = 0
                        FileInfo.FileSize = CULng(.length)
                        FileInfo.CreationTime = CULng(TapeUtils.ParseTimeStamp(.creationtime).ToFileTimeUtc)
                        FileInfo.LastAccessTime = CULng(TapeUtils.ParseTimeStamp(.accesstime).ToFileTimeUtc)
                        FileInfo.LastWriteTime = CULng(TapeUtils.ParseTimeStamp(.changetime).ToFileTimeUtc)
                        FileInfo.ChangeTime = CULng(TapeUtils.ParseTimeStamp(.changetime).ToFileTimeUtc)
                        FileInfo.IndexNumber = 0
                        FileInfo.HardLinks = 0
                    End With
                End If
                Return True
            End If

            FileName = ""
            FileInfo = New Fsp.Interop.FileInfo()
            Return False
        End Function
    End Class

    <TypeConverter(GetType(ExpandableObjectConverter))>
    Public Class LTFSMountFuseSvc
        Inherits Fsp.Service
        Public LW As LTFSWriter
        Public _Host As Fsp.FileSystemHost
        Public TapeDrive As String = ""
        Public ReadOnly Property MountPath As String
            Get
                Return TapeDrive.Split({"\"}, StringSplitOptions.RemoveEmptyEntries).Last
            End Get
        End Property
        Public Sub New()
            MyBase.New("LTFSMountFuseServie")
        End Sub

        Protected Overrides Sub OnStart(Args As String())
            Dim Host As New Fsp.FileSystemHost(New LTFSMountFSBase(TapeDrive) With {.LW = LW})
            Host.Prefix = $"\ltfs\{MountPath}"
            Host.FileSystemName = "LTFS"
            Dim Code As Integer = Host.Mount("L:", Nothing, True, 0)
            _Host = Host
        End Sub
        Protected Overrides Sub OnStop()
            _Host.Unmount()
            _Host = Nothing
        End Sub
    End Class
    Private Sub 挂载盘符只读ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 挂载盘符只读ToolStripMenuItem.Click
        '挂载
        Dim DriveLoc As String = TapeDrive
        If DriveLoc = "" Then DriveLoc = "\\.\TAPE0"
        Dim MountPath As String = DriveLoc.Split({"\"}, StringSplitOptions.RemoveEmptyEntries).ToList.Last
        Static svc As New LTFSMountFuseSvc()
        svc.LW = Me
        svc.TapeDrive = DriveLoc

        Task.Run(
            Sub()
                svc.Run()
            End Sub)

        MessageBox.Show(New Form With {.TopMost = True}, $"Mounted as \\ltfs\{svc.MountPath}{vbCrLf}Press OK to unmount")

        '卸载
        svc.Stop()
        MessageBox.Show(New Form With {.TopMost = True}, $"Unmounted. Code={svc.ExitCode}")
    End Sub

    Private Sub 子目录列表ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 子目录列表ToolStripMenuItem.Click
        Dim result As New StringBuilder
        If ListView1.Tag IsNot Nothing AndAlso TypeOf (ListView1.Tag) Is ltfsindex.directory Then
            For Each Dir As ltfsindex.directory In CType(ListView1.Tag, ltfsindex.directory).EnumerateLazyDirectories()
                result.AppendLine(Dir.name)
            Next
        End If
        Clipboard.SetText(result.ToString)
    End Sub

    Private Sub 文件详情ToolStripMenuItem1_Click(sender As Object, e As EventArgs) Handles 文件详情ToolStripMenuItem1.Click
        Dim result As New StringBuilder
        Dim selectedItems As List(Of ListViewItem) = GetSelectedListViewItems()
        If ListView1.Tag IsNot Nothing AndAlso
        selectedItems.Count > 0 Then
            For Each ItemSelected As ListViewItem In selectedItems
                If ItemSelected.Tag IsNot Nothing AndAlso TypeOf (ItemSelected.Tag) Is ltfsindex.file Then
                    Dim f As ltfsindex.file = DirectCast(ItemSelected.Tag, ltfsindex.file)
                    result.AppendLine(f.GetSerializedText())
                End If
            Next
        End If
        Clipboard.SetText(result.ToString)
    End Sub

    Private Sub XAttrToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles XAttrToolStripMenuItem.Click
        Dim result As New StringBuilder
        Dim selectedItems As List(Of ListViewItem) = GetSelectedListViewItems()
        If ListView1.Tag IsNot Nothing AndAlso
        selectedItems.Count > 0 Then
            For Each ItemSelected As ListViewItem In selectedItems
                If ItemSelected.Tag IsNot Nothing AndAlso TypeOf (ItemSelected.Tag) Is ltfsindex.file Then
                    Dim f As ltfsindex.file = DirectCast(ItemSelected.Tag, ltfsindex.file)
                    result.AppendLine(f.GetXAttrText())
                End If
            Next
        End If
        Clipboard.SetText(result.ToString)
    End Sub

    Private Async Sub 启动FTP服务只读ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 启动FTP服务只读ToolStripMenuItem.Click
        Dim svc As New FTPService()
        SetStatusLight(LWStatus.Busy)
        AddHandler svc.LogPrint, Sub(s As String)
                                     PrintMsg($"FTPSVC> {s}", Category:="FTP")
                                 End Sub
        svc.port = 8021
        If DisplayHelper.ShowInputDialog("Port", "FTP Service", svc.port) <> DialogResult.OK Then
            SetStatusLight(LWStatus.Idle)
            Exit Sub
        End If

        Dim username As String = String.Empty
        If DisplayHelper.ShowInputDialog("Username (blank for anonymous)", "FTP Service", username) <> DialogResult.OK Then
            SetStatusLight(LWStatus.Idle)
            Exit Sub
        End If

        svc.Username = username.Trim()
        If String.IsNullOrEmpty(svc.Username) Then
            svc.Password = String.Empty
            svc.AllowAnonymous = True
        Else
            Dim password As String = String.Empty
            If DisplayHelper.ShowInputDialog("Password", "FTP Service", password, MaskInput:=True) <> DialogResult.OK Then
                SetStatusLight(LWStatus.Idle)
                Exit Sub
            End If
            svc.Password = password
            svc.AllowAnonymous = False
        End If

        svc.schema = schema
        svc.TapeDrive = TapeDrive
        svc.BlockSize = plabel.blocksize
        svc.ExtraPartitionCount = ExtraPartitionCount
        Try
            svc.StartService()
            ShowFtpRunningDialog(svc.port)
            Await svc.StopServiceAsync()
            MessageBox.Show(New Form With {.TopMost = True}, "Service stopped.")
        Finally
            SetStatusLight(LWStatus.Idle)
        End Try
    End Sub

    Private Sub ShowFtpRunningDialog(servicePort As Integer)
        Using runningDialog As New Form()
            runningDialog.StartPosition = FormStartPosition.CenterParent
            runningDialog.FormBorderStyle = FormBorderStyle.FixedDialog
            runningDialog.MinimizeBox = False
            runningDialog.MaximizeBox = False
            runningDialog.ClientSize = New Size(300, 120)
            runningDialog.Text = "FTP Service"
            runningDialog.TopMost = True

            Dim statusLabel As New Label With {
                .AutoSize = True,
                .Text = $"Service running on port {servicePort}.",
                .Location = New Point(12, 15)
            }
            runningDialog.Controls.Add(statusLabel)

            Dim stopButton As New Button With {
                .Name = "stopButton",
                .Text = "Stop",
                .DialogResult = DialogResult.OK,
                .Size = New Size(80, 25),
                .Location = New Point(208, 78),
                .Anchor = AnchorStyles.Right Or AnchorStyles.Bottom
            }
            runningDialog.Controls.Add(stopButton)
            runningDialog.AcceptButton = stopButton
            runningDialog.CancelButton = stopButton
            DisplayHelper.ApplyDynamicFormLayout(runningDialog)
            runningDialog.ShowDialog(Me)
        End Using
    End Sub

    Private Sub 右下角显示容量损失ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 右下角显示容量损失ToolStripMenuItem.Click
        If Not My.Settings.LTFSWriter_ShowLoss Then
            If MessageBox.Show(New Form With {.TopMost = True}, My.Resources.ResText_CapLossPerfWarning, My.Resources.ResText_Warning, MessageBoxButtons.OKCancel) = DialogResult.Cancel Then
                右下角显示容量损失ToolStripMenuItem.Checked = My.Settings.LTFSWriter_ShowLoss
                Exit Sub
            End If
        End If
        My.Settings.LTFSWriter_ShowLoss = Not My.Settings.LTFSWriter_ShowLoss
        右下角显示容量损失ToolStripMenuItem.Checked = My.Settings.LTFSWriter_ShowLoss
        My.Settings.Save()
    End Sub

    Private Sub 压缩索引ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 压缩索引ToolStripMenuItem.Click
        If TreeView1.SelectedNode IsNot Nothing AndAlso
           TypeOf TreeView1.SelectedNode.Tag Is ltfsindex.directory AndAlso
           TreeView1.SelectedNode.Parent IsNot Nothing AndAlso
           TypeOf TreeView1.SelectedNode.Parent.Tag Is ltfsindex.directory Then
            Dim d As ltfsindex.directory = DirectCast(TreeView1.SelectedNode.Tag, ltfsindex.directory)
            Dim p As ltfsindex.directory = DirectCast(TreeView1.SelectedNode.Parent.Tag, ltfsindex.directory)
            If HasUnwrittenFilesInDirectoryTree(d) Then
                MessageBox.Show(New Form With {.TopMost = True},
                                "当前目录或子目录存在尚未写入的文件，请先完成写入后再压缩索引。",
                                My.Resources.ResText_Warning)
                Exit Sub
            End If

            LockGUI(True)
            Task.Run(Sub()
                         Dim tmpf As String = Nothing
                         Dim ms As IO.FileStream = Nothing
                         Dim wBufferPtr As IntPtr = IntPtr.Zero
                         Dim unitReserved As Boolean = False
                         Dim mediumRemovalPrevented As Boolean = False
                         Try
                             tmpf = LazySchemaStore.CreateTempFilePath("index-compress")
                             If Not d.SaveFile(tmpf) Then Throw New InvalidOperationException("无法生成压缩索引临时文件。")
                             ms = New IO.FileStream(tmpf, IO.FileMode.Open, IO.FileAccess.Read, IO.FileShare.Read)
                             TapeUtils.ReserveUnit(driveHandle)
                             unitReserved = True
                             TapeUtils.PreventMediaRemoval(driveHandle)
                             mediumRemovalPrevented = True
                             If Not LocateToWritePosition() Then Exit Sub
                             Dim pos As New TapeUtils.PositionData(driveHandle)
                             Dim fadd As New ltfsindex.file With {.name = d.name,
                                 .accesstime = d.accesstime,
                                 .backuptime = d.backuptime,
                                 .changetime = d.changetime,
                                 .creationtime = d.creationtime,
                                 .modifytime = d.modifytime,
                                 .extendedattributes = {New ltfsindex.file.xattr With {.key = ltfsindex.file.xattr.ApplicationSpecific.Archive, .value = "True"}}.ToList(),
                                 .fileuid = schema.highestfileuid,
                                 .length = ms.Length,
                                 .extentinfo = {New ltfsindex.file.extent With {
                                 .bytecount = ms.Length,
                                 .startblock = CLng(pos.BlockNumber),
                                 .byteoffset = 0,
                                  .fileoffset = 0,
                                  .partition = CType(pos.PartitionNumber, ltfsindex.PartitionLabel)}
                                  }.ToList()}

                             Dim LastWriteTask As Task = Nothing
                             Dim ExitWhileFlag As Boolean = False
                             wBufferPtr = Marshal.AllocHGlobal(plabel.blocksize)
                             Dim sh As New IOManager.CheckSumBlockwiseCalculator
                             While Not StopFlag
                                 Dim buffer(plabel.blocksize - 1) As Byte
                                 Dim BytesReaded As Integer = ms.Read(buffer, 0, plabel.blocksize)
                                 sh.Propagate(buffer, BytesReaded)
                                 If ExitWhileFlag Then Exit While
                                 If BytesReaded > 0 Then
                                     CheckCount += 1
                                     If CheckCount >= CheckCycle Then CheckCount = 0
                                     If SpeedLimit > 0 AndAlso CheckCount = 0 Then
                                         Dim ts As Double = (Now - SpeedLimitLastTriggerTime).TotalSeconds
                                         While SpeedLimit > 0 AndAlso ts > 0 AndAlso ((plabel.blocksize * CheckCycle / 1048576) / ts) > SpeedLimit
                                             Threading.Thread.Sleep(0)
                                             ts = (Now - SpeedLimitLastTriggerTime).TotalSeconds
                                         End While
                                         SpeedLimitLastTriggerTime = Now
                                     End If
                                     Marshal.Copy(buffer, 0, wBufferPtr, BytesReaded)
                                     Dim succ As Boolean = False
                                     While Not succ
                                         Dim sense As Byte()
                                         Try
                                             sense = TapeUtils.Write(handle:=driveHandle, Data:=wBufferPtr, Length:=CUInt(BytesReaded), senseEnabled:=True)
                                             SyncLock pos
                                                 pos.BlockNumber = CULng(pos.BlockNumber + 1)
                                             End SyncLock
                                         Catch ex As Exception
                                             Dim dResult As DialogResult
                                             Invoke(Sub() dResult = MessageBox.Show(New Form With {.TopMost = True}, $"{ My.Resources.ResText_WErrSCSI}{vbCrLf}{ex.StackTrace}", My.Resources.ResText_Warning, MessageBoxButtons.AbortRetryIgnore))
                                             Select Case dResult
                                                 Case DialogResult.Abort
                                                     SetStatusLight(LWStatus.Err)
                                                     Throw ex
                                                 Case DialogResult.Retry
                                                     succ = False
                                                 Case DialogResult.Ignore
                                                     succ = True
                                                     Exit While
                                             End Select
                                             pos = New TapeUtils.PositionData(driveHandle)
                                             Continue While
                                         End Try
                                         If (((sense(2) >> 6) And &H1) = 1) Then
                                             If ((sense(2) And &HF) = 13) Then
                                                 PrintMsg(My.Resources.ResText_VOF)
                                                 Invoke(Sub() MessageBox.Show(New Form With {.TopMost = True}, My.Resources.ResText_VOF))
                                                 StopFlag = True
                                                 ms.Close()
                                                 SetStatusLight(LWStatus.Err)
                                                 Exit Sub
                                             Else
                                                 PrintMsg(My.Resources.ResText_EWEOM, True, DeDupe:=True)
                                                 succ = True
                                                 Exit While
                                             End If
                                         ElseIf (sense(2) And &HF) <> 0 Then
                                             Try
                                                 Throw New Exception("SCSI sense error")
                                             Catch ex As Exception
                                                 Dim dResult As DialogResult
                                                 Invoke(Sub() dResult = MessageBox.Show(New Form With {.TopMost = True}, $"{My.Resources.ResText_WErr}{vbCrLf}{TapeUtils.ParseSenseData(sense)}{vbCrLf}{vbCrLf}sense{vbCrLf}{TapeUtils.Byte2Hex(sense, True)}{vbCrLf}{ex.StackTrace}", My.Resources.ResText_Warning, MessageBoxButtons.AbortRetryIgnore))
                                                 Select Case dResult
                                                     Case DialogResult.Abort
                                                         SetStatusLight(LWStatus.Err)
                                                         Throw New Exception(TapeUtils.ParseSenseData(sense))
                                                     Case DialogResult.Retry
                                                         succ = False
                                                     Case DialogResult.Ignore
                                                         succ = True
                                                         Exit While
                                                 End Select
                                             End Try

                                             pos = New TapeUtils.PositionData(driveHandle)
                                         Else
                                             succ = True
                                             Exit While
                                         End If
                                     End While
                                     If Flush Then CheckFlush()
                                     If Clean Then CheckClean(True)
                                     TotalBytesProcessed += BytesReaded
                                     CurrentBytesProcessed += BytesReaded
                                     TotalBytesUnindexed += BytesReaded
                                 Else
                                     ExitWhileFlag = True
                                 End If
                             End While
                             If StopFlag Then Exit Sub
                             sh.ProcessFinalBlock()
                             If sh.SHA1Value IsNot Nothing Then fadd.SetXattr(ltfsindex.file.xattr.HashType.SHA1, sh.SHA1Value)
                             If sh.MD5Value IsNot Nothing Then fadd.SetXattr(ltfsindex.file.xattr.HashType.MD5, sh.MD5Value)
                             If sh.BlakeValue IsNot Nothing Then fadd.SetXattr(ltfsindex.file.xattr.HashType.BLAKE3, sh.BlakeValue)
                             If sh.XXHash3Value IsNot Nothing Then fadd.SetXattr(ltfsindex.file.xattr.HashType.XxHash3, sh.XXHash3Value)
                             If sh.XXHash128Value IsNot Nothing Then fadd.SetXattr(ltfsindex.file.xattr.HashType.XxHash128, sh.XXHash128Value)
                             If LastWriteTask IsNot Nothing Then ObserveTask(LastWriteTask)
                             'Do not expose the archive in the schema until every
                             'byte has been written successfully.  If the write
                             'fails, the original directory remains reachable.
                             p.AddFile(fadd)
                             schema.highestfileuid += 1
                             p.RemoveDirectory(d)
                             TotalFilesProcessed += 1
                             CurrentFilesProcessed += 1
                             If TotalBytesUnindexed = 0 Then TotalBytesUnindexed = 1
                             pos = GetPos
                             If pos.EOP Then PrintMsg(My.Resources.ResText_EWEOM, True, DeDupe:=True)
                             PrintMsg($"Position = {p.ToString()}", LogOnly:=True)
                             CurrentHeight = CLng(pos.BlockNumber)
                             Invoke(Sub() 更新数据区索引ToolStripMenuItem.Enabled = True)
                             SetStatusLight(LWStatus.Succ)
                             TapeUtils.Flush(driveHandle)
                             Invoke(Sub() TreeView1.SelectedNode = TreeView1.SelectedNode.Parent)
                             RefreshDisplay()
                             RefreshCapacity()
                             Modified = True
                         Catch ex As Exception
                             SetStatusLight(LWStatus.Err)
                             PrintMsg($"压缩索引出错：{ex.Message}{vbCrLf}{ex.StackTrace}", ForceLog:=True)
                             Try
                                 Invoke(Sub() MessageBox.Show(New Form With {.TopMost = True}, $"压缩索引出错：{ex}", My.Resources.ResText_Warning))
                             Catch
                             End Try
                         Finally
                             If wBufferPtr <> IntPtr.Zero Then
                                 Try
                                     Marshal.FreeHGlobal(wBufferPtr)
                                 Catch
                                 End Try
                             End If
                             If ms IsNot Nothing Then
                                 Try
                                     ms.Dispose()
                                 Catch
                                 End Try
                             End If
                             If Not String.IsNullOrEmpty(tmpf) Then
                                 Try
                                     If IO.File.Exists(tmpf) Then IO.File.Delete(tmpf)
                                 Catch
                                 End Try
                             End If
                             If mediumRemovalPrevented Then
                                 Try
                                     TapeUtils.AllowMediumRemoval(driveHandle)
                                 Catch
                                 End Try
                             End If
                             If unitReserved Then
                                 Try
                                     TapeUtils.ReleaseUnit(driveHandle)
                                 Catch
                                 End Try
                             End If
                             LockGUI(False)
                         End Try
                     End Sub)
        End If
    End Sub

    Private Sub 解压索引ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 解压索引ToolStripMenuItem.Click
        If TreeView1.SelectedNode IsNot Nothing AndAlso TypeOf TreeView1.SelectedNode.Tag Is ltfsindex.file Then
            Dim f As ltfsindex.file = DirectCast(TreeView1.SelectedNode.Tag, ltfsindex.file)
            Dim d As ltfsindex.directory = DirectCast(TreeView1.SelectedNode.Parent.Tag, ltfsindex.directory)
            If f.GetXAttr(ltfsindex.file.xattr.ApplicationSpecific.Archive).ToLower = "true" Then
                LockGUI(True)
                Task.Run(Sub()
                             SetStatusLight(LWStatus.Busy)
                             Try
                                 Dim tmpf As String = LazySchemaStore.CreateTempFilePath("index-decompress")
                                 RestorePosition = New TapeUtils.PositionData(driveHandle)
                                 RestoreFile(tmpf, f)
                                 Dim dindex As ltfsindex.directory = ltfsindex.directory.FromFile(tmpf)
                                 d.RemoveFile(f)
                                 d.AddDirectory(dindex)
                                 IO.File.Delete(tmpf)
                                 SetStatusLight(LWStatus.Idle)
                             Catch ex As Exception
                                 SetStatusLight(LWStatus.Err)
                                 PrintMsg($"解压索引出错：{ex.ToString}", ForceLog:=True)
                             End Try

                             RefreshDisplay()
                             LockGUI(False)
                         End Sub)

            End If
        End If
    End Sub

    Private Sub 跳过符号链接ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 跳过符号链接ToolStripMenuItem.Click
        My.Settings.LTFSWriter_SkipSymlink = 跳过符号链接ToolStripMenuItem.Checked
    End Sub

    Private Sub 覆盖已有文件ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 覆盖已有文件ToolStripMenuItem.Click
        My.Settings.LTFSWriter_OverwriteExist = 覆盖已有文件ToolStripMenuItem.Checked
    End Sub

    Private Sub 显示文件数ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 显示文件数ToolStripMenuItem.Click
        My.Settings.LTFSWriter_ShowFileCount = 显示文件数ToolStripMenuItem.Checked
        RefreshDisplay()
    End Sub

    Private Sub 增强Tar元数据ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 增强Tar元数据ToolStripMenuItem.Click
        My.Settings.LTFSWriter_EnrichTarMetadata = 增强Tar元数据ToolStripMenuItem.Checked
        My.Settings.Save()
    End Sub

    Private Sub 移动到索引区ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 移动到索引区ToolStripMenuItem.Click
        Dim selectedItems As List(Of ListViewItem) = GetSelectedListViewItems()
        If selectedItems.Count > 0 Then
            LockGUI()
            Dim flist As New List(Of ltfsindex.file)
            For Each SI As ListViewItem In selectedItems
                If TypeOf SI.Tag Is ltfsindex.file Then
                    flist.Add(DirectCast(SI.Tag, ltfsindex.file))
                End If
            Next
            Dim th As New Threading.Thread(
                    Sub()
                        Try
                            SetStatusLight(LWStatus.Busy)
                            CurrentFilesProcessed = 0
                            CurrentBytesProcessed = 0
                            UnwrittenSizeOverrideValue = 0
                            UnwrittenCountOverrideValue = CULng(flist.Count)
                            For Each FI As ltfsindex.file In flist
                                UnwrittenSizeOverrideValue = CULng(UnwrittenSizeOverrideValue + FI.length)
                            Next
                            PrintMsg(My.Resources.ResText_Writing)
                            StopFlag = False
                            TapeUtils.ReserveUnit(driveHandle)
                            TapeUtils.PreventMediaRemoval(driveHandle)
                            RestorePosition = New TapeUtils.PositionData(driveHandle)

                            For Each FileIndex As ltfsindex.file In flist
                                Dim tmpf As String = LazySchemaStore.CreateTempFilePath("file-transfer")
                                If DirectCast(ListView1.Tag, ltfsindex.directory).UnwrittenFiles.Contains(FileIndex) Then
                                    FileIndex.extentinfo.Clear()
                                    FileIndex.extentinfo.Add(New ltfsindex.file.extent With {.partition = CType(IndexPartition, ltfsindex.PartitionLabel)})
                                    FileIndex.MarkLazyRecordDirty()
                                Else
                                    RestoreFile(tmpf, FileIndex)
                                    FileIndex.TempObj = tmpf
                                End If
                            Next
                            For Each FileIndex As ltfsindex.file In flist
                                If Not DirectCast(ListView1.Tag, ltfsindex.directory).UnwrittenFiles.Contains(FileIndex) Then
                                    MoveToIndexPartition(FileIndex, CStr(FileIndex.TempObj))
                                End If
                                If StopFlag Then
                                    PrintMsg(My.Resources.ResText_OpCancelled)
                                    SetStatusLight(LWStatus.Idle)
                                    Exit Sub
                                End If
                            Next
                        Catch ex As Exception
                            SetStatusLight(LWStatus.Err)
                            PrintMsg(My.Resources.ResText_RestoreErr)
                        End Try
                        TapeUtils.AllowMediumRemoval(driveHandle)
                        TapeUtils.ReleaseUnit(driveHandle)
                        StopFlag = False
                        UnwrittenSizeOverrideValue = 0
                        UnwrittenCountOverrideValue = 0
                        LockGUI(False)
                        PrintMsg(My.Resources.ResText_AddFin)
                        SetStatusLight(LWStatus.Succ)
                        Invoke(Sub()
                                   RefreshDisplay()
                                   MessageBox.Show(New Form With {.TopMost = True}, My.Resources.ResText_AddFin)
                               End Sub)
                    End Sub)
            th.Start()
        End If
    End Sub


    Private Sub 索引间隔36GiBToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 索引间隔36GiBToolStripMenuItem.Click
        If DisplayHelper.ShowInputDialog(My.Resources.ResText_SIIntv, My.Resources.ResText_Setting, IndexWriteInterval) <> DialogResult.OK Then Exit Sub
    End Sub

    Private Sub DebugToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles DebugToolStripMenuItem.Click
        ApplicationNavigation.ShowConfigurator()
    End Sub

    Private Sub 设置密钥ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 设置密钥ToolStripMenuItem.Click
        Dim key As String = ""
        If EncryptionKey IsNot Nothing AndAlso EncryptionKey.Length = 32 Then
            key = BitConverter.ToString(EncryptionKey).Replace("-", "").ToUpper
        End If
        If DisplayHelper.ShowInputDialog(设置密钥ToolStripMenuItem.Text, "LTFSWriter", key) <> DialogResult.OK Then Exit Sub
        Dim newkey As Byte() = IOManager.HexStringToByteArray(key)
        If newkey.Length <> 32 Then
            EncryptionKey = Nothing
        Else
            Dim sum As Integer = 0
            For i As Integer = 0 To newkey.Length - 1
                sum += newkey(i)
            Next
            If sum = 0 Then
                EncryptionKey = Nothing
            Else
                EncryptionKey = newkey
            End If
        End If
        TapeUtils.SetEncryption(driveHandle, EncryptionKey)
    End Sub

    Private Sub 设置密码ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 设置密码ToolStripMenuItem.Click
        Dim key As String = ""
        If DisplayHelper.ShowInputDialog(设置密码ToolStripMenuItem.Text, "LTFSWriter", key) <> DialogResult.OK Then Exit Sub
        If key.Length = 0 Then
            EncryptionKey = Nothing
        Else
            Dim newkey As Byte()
            Dim sha256 As System.Security.Cryptography.SHA256
            sha256 = System.Security.Cryptography.SHA256.Create()
            Dim strData As Byte() = Encoding.UTF8.GetBytes(key)
            sha256.TransformFinalBlock(strData, 0, strData.Length)
            newkey = sha256.Hash()
            EncryptionKey = newkey
        End If
        TapeUtils.SetEncryption(driveHandle, EncryptionKey)
    End Sub

    Private Sub 剪切文件ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 剪切文件ToolStripMenuItem.Click
        Dim selectedItems As List(Of ListViewItem) = GetSelectedListViewItems()
        If selectedItems.Count > 0 Then
            Dim flist As New List(Of ltfsindex.file)
            Dim filesToRemove As New List(Of ltfsindex.file)
            Dim d As ltfsindex.directory = DirectCast(ListView1.Tag, ltfsindex.directory)
            For Each SI As ListViewItem In selectedItems
                If TypeOf SI.Tag Is ltfsindex.file Then
                    Dim f As ltfsindex.file = CType(SI.Tag, ltfsindex.file)
                    If Not d.UnwrittenFiles.Contains(f) Then
                        flist.Add(f)
                        filesToRemove.Add(f)
                    End If
                End If
            Next
            If d.RemoveFiles(filesToRemove) > 0 AndAlso TotalBytesUnindexed = 0 Then TotalBytesUnindexed = 1
            MyClipBoard.Add(flist)
            RefreshDisplay()
        End If
    End Sub

    Private Sub 剪切目录ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 剪切目录ToolStripMenuItem.Click
        Dim Nodes As List(Of TreeNode) = SelectedNodes
        If Nodes.Count = 0 Then Exit Sub
        For Each n As TreeNode In Nodes
            If n IsNot Nothing AndAlso n.Parent IsNot Nothing Then
                Dim d As ltfsindex.directory = DirectCast(n.Tag, ltfsindex.directory)
                Dim dp As ltfsindex.directory = DirectCast(n.Parent.Tag, ltfsindex.directory)
                MyClipBoard.Add(d)
                dp.RemoveDirectory(d)
            End If
        Next
        If Nodes.Count = 1 AndAlso TreeView1.SelectedNode.Parent IsNot Nothing AndAlso TreeView1.SelectedNode.Parent.Tag IsNot Nothing AndAlso TypeOf (TreeView1.SelectedNode.Parent.Tag) Is ltfsindex.directory Then
            TreeView1.SelectedNode = TreeView1.SelectedNode.Parent
        End If
        RefreshDisplay()
    End Sub

    Private Sub 粘贴选中ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 粘贴选中ToolStripMenuItem.Click
        If TreeView1.SelectedNode IsNot Nothing Then
            Dim droot As ltfsindex.directory = DirectCast(TreeView1.SelectedNode.Tag, ltfsindex.directory)
            Dim filesToPaste As New List(Of ltfsindex.file)
            For Each f As ltfsindex.file In MyClipBoard.File
                schema.highestfileuid += 1
                filesToPaste.Add(f)
            Next
            droot.AddFiles(filesToPaste)
            Dim directoriesToPaste As New List(Of ltfsindex.directory)
            For Each d As ltfsindex.directory In MyClipBoard.Directory
                schema.highestfileuid += 1
                directoriesToPaste.Add(d)
            Next
            droot.AddDirectories(directoriesToPaste)
            MyClipBoard.Clear()
            If TotalBytesUnindexed = 0 Then TotalBytesUnindexed = 1
            RefreshDisplay()
        End If
    End Sub

    Private Sub ToolStripStatusLabel1_Click(sender As Object, e As EventArgs) Handles ToolStripStatusLabel1.Click
        If MyClipBoard.IsEmpty Then Exit Sub
        Dim fMsg As New Form With {.Width = 800, .Height = 600, .TopMost = True,
            .Text = My.Resources.ResText_ClipBoard, .Font = DisplayHelper.DisplayFont}
        Dim ostr As New StringBuilder
        For Each d As ltfsindex.directory In MyClipBoard.Directory
            ostr.AppendLine($"DIR   {d.name}")
        Next
        For Each f As ltfsindex.file In MyClipBoard.File
            ostr.AppendLine($"FILE  {f.name}")
        Next
        fMsg.Controls.Add(New TextBox With {.Parent = fMsg, .Dock = DockStyle.Fill, .WordWrap = False,
                          .Multiline = True, .ScrollBars = ScrollBars.Both, .ReadOnly = True, .Text = ostr.ToString()})
        DisplayHelper.ApplyDynamicFormLayout(fMsg)
        fMsg.ShowDialog()
    End Sub

    Private Sub 粘贴选中ToolStripMenuItem1_Click(sender As Object, e As EventArgs) Handles 粘贴选中ToolStripMenuItem1.Click
        粘贴选中ToolStripMenuItem_Click(sender, e)
    End Sub

    Private Sub 查找指定位置前的索引ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 查找指定位置前的索引ToolStripMenuItem.Click
        Dim blocknum As ULong = 0
        If DisplayHelper.ShowInputDialog("Block number", "Index search", blocknum) <> DialogResult.OK Then Exit Sub
        If blocknum <= 0 Then Exit Sub
        Dim th As New Threading.Thread(
            Sub()
                Try
                    If Not TapeUtils.IsOpened(driveHandle) Then TapeUtils.OpenTapeDrive(TapeDrive, driveHandle)
                    SetStatusLight(LWStatus.Busy)
                    PrintMsg(My.Resources.ResText_Locating)
                    Dim currentPos As TapeUtils.PositionData = GetPos
                    PrintMsg($"Position = {currentPos.ToString()}", LogOnly:=True)
                    If ExtraPartitionCount = 0 Then
                        TapeUtils.Locate(driveHandle, blocknum, 0)
                    Else
                        If currentPos.PartitionNumber <> 1 Then TapeUtils.Locate(driveHandle, 0UL, CByte(1), TapeUtils.LocateDestType.Block)
                        TapeUtils.Locate(driveHandle, blocknum, DataPartition)
                    End If
                    PrintMsg(My.Resources.ResText_RI)
                    currentPos = GetPos
                    PrintMsg($"Position = {currentPos.ToString()}", LogOnly:=True)
                    If DisablePartition Then
                        TapeUtils.Space6(driveHandle, -2, TapeUtils.LocateDestType.FileMark)
                    Else
                        Dim FM As Long = CLng(currentPos.FileNumber)
                        If FM <= 1 Then
                            PrintMsg(My.Resources.ResText_IRFailed)
                            SetStatusLight(LWStatus.Err)
                            Invoke(Sub() MessageBox.Show(New Form With {.TopMost = True}, My.Resources.ResText_NLTFS, My.Resources.ResText_Error))
                            LockGUI(False)
                            Exit Try
                        End If
                        TapeUtils.Locate(driveHandle, CULng(FM - 1), DataPartition, TapeUtils.LocateDestType.FileMark)
                    End If

                    TapeUtils.ReadFileMark(driveHandle)
                    currentPos = GetPos
                    PrintMsg(My.Resources.ResText_RI)
                    Dim outDir As String = IO.Path.Combine(My.Settings.schemaPath, "LoadDP")
                    If Not IO.Directory.Exists(outDir) Then IO.Directory.CreateDirectory(outDir)
                    Dim outputfile As String = IO.Path.Combine(outDir, $"LTFSIndex_LoadDPIndex_p{currentPos.PartitionNumber}_b{currentPos.BlockNumber}_{Now:yyyyMMdd_HHmmss.fffffff}.schema")
                    TapeUtils.ReadToFileMark(driveHandle, outputfile)
                    PrintMsg(My.Resources.ResText_AI)
                    schema = ltfsindex.FromSchFile(outputfile)
                    PrintMsg(My.Resources.ResText_AISucc)

                    Try
                        Dim targetDir As String = outDir
                        Dim allSchemas = IO.Directory.GetFiles(targetDir, "*.schema", IO.SearchOption.TopDirectoryOnly)

                        If allSchemas IsNot Nothing AndAlso allSchemas.Length > 0 Then
                            Dim latestSchemaPath As String =
                                    allSchemas.OrderByDescending(Function(p) IO.File.GetLastWriteTimeUtc(p)).First()

                            For Each oldSchema In allSchemas
                                If String.Equals(oldSchema, latestSchemaPath, StringComparison.OrdinalIgnoreCase) Then Continue For

                                Dim zstPath As String = oldSchema & ".zst"

                                If Not IO.File.Exists(zstPath) Then
                                    Using inFs As New IO.FileStream(oldSchema, IO.FileMode.Open, IO.FileAccess.Read, IO.FileShare.Read)
                                        Using outFs As New IO.FileStream(zstPath, IO.FileMode.Create, IO.FileAccess.Write, IO.FileShare.None)
                                            Using zs As New ZstdSharp.CompressionStream(outFs, 9)
                                                inFs.CopyTo(zs)
                                            End Using
                                        End Using
                                    End Using
                                End If

                                Try : IO.File.Delete(oldSchema) : Catch : End Try
                            Next
                        End If
                    Catch
                    End Try

                    While True
                        Threading.Thread.Sleep(0)
                        SyncLock UFReadCount
                            If UFReadCount > 0 Then Continue While
                            ClearGlobalPendingQueue()
                            CurrentFilesProcessed = 0
                            CurrentBytesProcessed = 0
                            TotalBytesUnindexed = 0
                            Exit While
                        End SyncLock
                    End While
                    Modified = False
                    Me.Invoke(Sub()
                                  ToolStripStatusLabel1.ToolTipText = $"{My.Resources.ResText_Barcode}:{ToolStripStatusLabel1.Text}{vbCrLf}{My.Resources.ResText_BlkSize}:{plabel.blocksize}"
                                  MaxCapacity = 0
                              End Sub)
                    RefreshDisplay()
                    RefreshCapacity()
                    CurrentHeight = -1
                    PrintMsg(My.Resources.ResText_IRSucc)
                    SetStatusLight(LWStatus.Idle)
                Catch ex As Exception
                    SetStatusLight(LWStatus.Err)
                    PrintMsg(My.Resources.ResText_IRFailed)
                End Try
                LockGUI(False)
            End Sub)
        LockGUI()
        th.Start()
    End Sub
    <Category("TapeUtils")>
    Public ReadOnly Property TapeUtils_DriveOpenCount As SerializableDictionary(Of String, Integer)
        Get
            Return TapeUtils.DriveOpenCount
        End Get
    End Property
    <Category("TapeUtils")>
    Public ReadOnly Property TapeUtils_DriveHandle As SerializableDictionary(Of String, IntPtr)
        Get
            Return TapeUtils.DriveHandle
        End Get
    End Property


    Private Sub 加锁ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 加锁ToolStripMenuItem.Click
        TapeUtils.OpenTapeDrive(TapeDrive, driveHandle)
        If TapeUtils.IsOpened(driveHandle) Then SetStatusLight(LWStatus.Idle)
        MessageBox.Show(New Form With {.TopMost = True}, $"Lock: {TapeUtils.DriveOpenCount(TapeDrive)}")
    End Sub

    Private Sub 新建压缩文件ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 新建压缩文件ToolStripMenuItem.Click
        If ListView1.Tag IsNot Nothing Then
            Dim th As New Threading.Thread(
            Sub()
                Dim OnWriteFinishMessage As String = ""
                Dim wBufferPtr As IntPtr = IntPtr.Zero
                Dim hashTasks As New List(Of Task)
                Dim writeTasks As New List(Of Task)
                Dim tapeUnitReserved As Boolean = False
                Dim mediumRemovalPrevented As Boolean = False
                Dim ufReadCountIncremented As Boolean = False
                Dim activeFile As FileRecord = Nothing
                Try
                    Dim StartTime As Date = Now
                    PrintMsg("", True)
                    PrintMsg($"Position = {GetPos.ToString()}", LogOnly:=True)
                    PrintMsg(My.Resources.ResText_PrepW)
                    TapeUtils.ReserveUnit(driveHandle)
                    tapeUnitReserved = True
                    TapeUtils.PreventMediaRemoval(driveHandle)
                    mediumRemovalPrevented = True
                    If Not LocateToWritePosition() Then Exit Sub
                    Invoke(Sub() 更新数据区索引ToolStripMenuItem.Enabled = True)
                    UFReadCount.Inc()
                    ufReadCountIncremented = True
                    CurrentFilesProcessed = 0
                    CurrentBytesProcessed = 0
                    UnwrittenSizeOverrideValue = 0
                    UnwrittenCountOverrideValue = 0
                    If UnwrittenCount > 0 Then
                        UFReadCount.Inc()
                        UFReadCount.Dec()
                        UnwrittenCountOverrideValue = UnwrittenCount
                        UnwrittenSizeOverrideValue = UnwrittenSize
                        wBufferPtr = Marshal.AllocHGlobal(CInt(plabel.blocksize))

                        Dim p As New TapeUtils.PositionData(driveHandle)
                        TapeUtils.SetBlockSize(driveHandle, plabel.blocksize)
                        Try
                            Dim fr As FileRecord = Nothing
                            SyncLock _pendingQueueLock
                                If UnwrittenFiles IsNot Nothing Then
                                    For Each candidate As FileRecord In UnwrittenFiles
                                        If candidate IsNot Nothing AndAlso candidate.File IsNot Nothing AndAlso
                                           Not String.IsNullOrWhiteSpace(candidate.SourcePath) Then
                                            fr = candidate
                                            Exit For
                                        End If
                                    Next
                                End If
                            End SyncLock
                            If fr Is Nothing Then Throw New InvalidOperationException("找不到有效的待写文件记录。")
                            activeFile = fr
                            fr.File.fileuid = schema.highestfileuid + 1
                            schema.highestfileuid += 1
                            Dim fileextent As New ltfsindex.file.extent With
                                            {.partition = ltfsindex.PartitionLabel.b,
                                            .startblock = CLng(p.BlockNumber),
                                            .bytecount = 0,
                                            .byteoffset = 0,
                                            .fileoffset = 0}
                            fr.File.extentinfo.Add(fileextent)
                            PrintMsg($"{My.Resources.ResText_Writing} {fr.File.name}  {My.Resources.ResText_Size} {IOManager.FormatSize(fr.File.length)}", False,
                                 $"{My.Resources.ResText_Writing}: {fr.SourcePath}{vbCrLf}{My.Resources.ResText_Size}: {IOManager.FormatSize(fr.File.length)}{vbCrLf _
                                 }{My.Resources.ResText_WrittenTotal}: {IOManager.FormatSize(TotalBytesProcessed) _
                                 } {My.Resources.ResText_Remaining}: {IOManager.FormatSize(CLng(Math.Max(0, UnwrittenSize - CurrentBytesProcessed))) _
                                 } -> {IOManager.FormatSize(CLng(Math.Max(0, UnwrittenSize - CurrentBytesProcessed - fr.File.length)))}")
                            'write to tape

                            Select Case fr.Open()
                                Case DialogResult.Ignore
                                    PrintMsg($"Cannot open file {fr.SourcePath}", LogOnly:=True, ForceLog:=True)
                                    StopFlag = True
                                    Throw New IO.IOException($"Cannot open file {fr.SourcePath}")
                                Case DialogResult.Abort
                                    StopFlag = True
                                    Throw New Exception(My.Resources.ResText_FileOpenError)
                            End Select
                            'PrintMsg($"File Opened:{fr.SourcePath}", LogOnly:=True)
                            Dim sh As IOManager.CheckSumBlockwiseCalculator = Nothing
                            If HashOnWrite Then sh = New IOManager.CheckSumBlockwiseCalculator
                            Dim LastWriteTask As Task = Nothing
                            Dim ExitWhileFlag As Boolean = False
                            'Dim tstart As Date = Now
                            'Dim tsub As Double = 0
                            While Not StopFlag
                                Dim buffer(plabel.blocksize - 1) As Byte
                                Dim BytesReaded As UInteger
                                While True
                                    Try
                                        BytesReaded = CUInt(fr.Read(buffer, 0, plabel.blocksize))
                                        Exit While
                                    Catch ex As Exception
                                        Dim dResult As DialogResult
                                        Invoke(Sub() dResult = MessageBox.Show(New Form With {.TopMost = True}, $"{My.Resources.ResText_WErr }{vbCrLf}{ex.ToString}", My.Resources.ResText_Warning, MessageBoxButtons.AbortRetryIgnore))
                                        Select Case dResult
                                            Case DialogResult.Abort
                                                StopFlag = True
                                                fr.Close()
                                                Throw ex
                                            Case DialogResult.Retry

                                            Case DialogResult.Ignore
                                                PrintMsg($"Cannot read file {fr.SourcePath}", LogOnly:=True, ForceLog:=True)
                                                StopFlag = True
                                                Throw New IO.IOException($"Cannot read file {fr.SourcePath}")
                                        End Select
                                    End Try
                                End While

                                If LastWriteTask IsNot Nothing Then
                                    ObserveTask(LastWriteTask)
                                    writeTasks.Remove(LastWriteTask)
                                End If
                                If ExitWhileFlag Then Exit While
                                LastWriteTask = Task.Factory.StartNew(
                                Sub(state As Object)
                                    Dim writeState = DirectCast(state, Tuple(Of Byte(), UInteger))
                                    Dim writeBuffer As Byte() = writeState.Item1
                                    Dim bytesToWrite As UInteger = writeState.Item2
                                    If bytesToWrite > 0 Then
                                        CheckCount += 1
                                        If CheckCount >= CheckCycle Then CheckCount = 0
                                        If SpeedLimit > 0 AndAlso CheckCount = 0 Then
                                            Dim ts As Double = (Now - SpeedLimitLastTriggerTime).TotalSeconds
                                            While SpeedLimit > 0 AndAlso ts > 0 AndAlso ((plabel.blocksize * CheckCycle / 1048576) / ts) > SpeedLimit
                                                Threading.Thread.Sleep(0)
                                                ts = (Now - SpeedLimitLastTriggerTime).TotalSeconds
                                            End While
                                            SpeedLimitLastTriggerTime = Now
                                        End If
                                        Marshal.Copy(writeBuffer, 0, wBufferPtr, CInt(bytesToWrite))
                                        Dim succ As Boolean = False
                                        While Not succ
                                            Dim sense As Byte()
                                            Try
                                                'Dim t0 As Date = Now
                                                sense = TapeUtils.Write(driveHandle, wBufferPtr, bytesToWrite, True)
                                                'tsub += (Now - t0).TotalMilliseconds
                                                'Invoke(Sub() Text = tsub / (Now - tstart).TotalMilliseconds)
                                                SyncLock p
                                                    p.BlockNumber = CULng(p.BlockNumber + 1)
                                                End SyncLock
                                            Catch ex As Exception
                                                Dim dResult As DialogResult
                                                Invoke(Sub() dResult = MessageBox.Show(New Form With {.TopMost = True}, $"{My.Resources.ResText_WErrSCSI}{vbCrLf}{ex.ToString}", My.Resources.ResText_Warning, MessageBoxButtons.AbortRetryIgnore))
                                                Select Case dResult
                                                    Case DialogResult.Abort
                                                        fr.Close()
                                                        StopFlag = True
                                                        Throw ex
                                                    Case DialogResult.Retry
                                                        succ = False
                                                    Case DialogResult.Ignore
                                                        succ = True
                                                        Exit While
                                                End Select
                                                p = New TapeUtils.PositionData(driveHandle)
                                                Continue While
                                            End Try
                                            If (((sense(2) >> 6) And &H1) = 1) Then
                                                If ((sense(2) And &HF) = 13) Then
                                                    PrintMsg(My.Resources.ResText_VOF)
                                                    Invoke(Sub() MessageBox.Show(New Form With {.TopMost = True}, My.Resources.ResText_VOF))
                                                    StopFlag = True
                                                    fr.Close()
                                                    Exit Sub
                                                Else
                                                    PrintMsg(My.Resources.ResText_EWEOM, True, DeDupe:=True)
                                                    succ = True
                                                    Exit While
                                                End If
                                            ElseIf (sense(2) And &HF) <> 0 Then
                                                Try
                                                    Throw New Exception("SCSI sense error")
                                                Catch ex As Exception
                                                    Dim dResult As DialogResult
                                                    Invoke(Sub() dResult = MessageBox.Show(New Form With {.TopMost = True}, $"{My.Resources.ResText_WErr}{vbCrLf}{TapeUtils.ParseSenseData(sense)}{vbCrLf}{vbCrLf}sense{vbCrLf}{TapeUtils.Byte2Hex(sense, True)}{vbCrLf}{ex.StackTrace}", My.Resources.ResText_Warning, MessageBoxButtons.AbortRetryIgnore))
                                                    Select Case dResult
                                                        Case DialogResult.Abort
                                                            fr.Close()
                                                            StopFlag = True
                                                            Throw New Exception(TapeUtils.ParseSenseData(sense))
                                                        Case DialogResult.Retry
                                                            succ = False
                                                        Case DialogResult.Ignore
                                                            succ = True
                                                            Exit While
                                                    End Select
                                                End Try

                                                p = New TapeUtils.PositionData(driveHandle)
                                            Else
                                                succ = True
                                                Exit While
                                            End If
                                        End While
                                        If sh IsNot Nothing AndAlso succ Then
                                            If 异步校验CPU占用高ToolStripMenuItem.Checked Then
                                                sh.PropagateAsync(writeBuffer, CInt(bytesToWrite))
                                            Else
                                                sh.Propagate(writeBuffer, CInt(bytesToWrite))
                                            End If
                                        End If
                                        If Flush Then CheckFlush()
                                        If Clean Then CheckClean(True)
                                        fr.File.WrittenBytes += bytesToWrite
                                        TotalBytesProcessed += bytesToWrite
                                        CurrentBytesProcessed += bytesToWrite
                                        TotalBytesUnindexed += bytesToWrite
                                    Else
                                        ExitWhileFlag = True
                                    End If
                                End Sub, Tuple.Create(buffer, BytesReaded))
                                writeTasks.Add(LastWriteTask)
                            End While
                            If LastWriteTask IsNot Nothing Then
                                ObserveTask(LastWriteTask)
                                writeTasks.Remove(LastWriteTask)
                            End If
                            fr.Close()
                            If HashOnWrite AndAlso sh IsNot Nothing AndAlso Not StopFlag Then
                                Dim hashTask As Task = Task.Run(Sub()
                                                                    Try
                                                                        sh.ProcessFinalBlock()
                                                                        If sh.SHA1Value IsNot Nothing Then fr.File.SetXattr(ltfsindex.file.xattr.HashType.SHA1, sh.SHA1Value)
                                                                        If sh.MD5Value IsNot Nothing Then fr.File.SetXattr(ltfsindex.file.xattr.HashType.MD5, sh.MD5Value)
                                                                        If sh.BlakeValue IsNot Nothing Then fr.File.SetXattr(ltfsindex.file.xattr.HashType.BLAKE3, sh.BlakeValue)
                                                                        If sh.XXHash3Value IsNot Nothing Then fr.File.SetXattr(ltfsindex.file.xattr.HashType.XxHash3, sh.XXHash3Value)
                                                                    Finally
                                                                        sh.StopFlag = True
                                                                    End Try
                                                                End Sub)
                                hashTasks.Add(hashTask)
                                WaitForHashTasks(hashTasks)
                            ElseIf sh IsNot Nothing Then
                                sh.StopFlag = True
                            End If
                            TotalFilesProcessed += 1
                            CurrentFilesProcessed += 1
                            p = GetPos
                            If p.EOP Then PrintMsg(My.Resources.ResText_EWEOM, True, DeDupe:=True)
                            PrintMsg($"Position = {p.ToString()}", LogOnly:=True)
                            CurrentHeight = CLng(p.BlockNumber)
                            'mark as written
                            CommitWrittenFiles(New List(Of FileRecord) From {fr})
                            If TotalBytesUnindexed = 0 Then TotalBytesUnindexed = 1
                            If CheckUnindexedDataLimit() Then p = New TapeUtils.PositionData(driveHandle)
                            If CapacityRefreshInterval > 0 AndAlso (Now - LastRefresh).TotalSeconds > CapacityRefreshInterval Then
                                p = New TapeUtils.PositionData(driveHandle)
                                RefreshCapacity()
                                Dim p2 As New TapeUtils.PositionData(driveHandle)
                                If p2.BlockNumber <> p.BlockNumber OrElse p2.PartitionNumber <> p.PartitionNumber Then
                                    Invoke(Sub()
                                               If MessageBox.Show(New Form With {.TopMost = True}, $"Position changed! {p.BlockNumber} -> {p2.BlockNumber}", "Warning", MessageBoxButtons.OKCancel) = DialogResult.Cancel Then
                                                   StopFlag = True
                                               End If
                                           End Sub)
                                End If
                            End If
                        Catch ex As Exception
                            Invoke(Sub() MessageBox.Show(New Form With {.TopMost = True}, $"{My.Resources.ResText_WErr}{vbCrLf}{ex.ToString}"))
                            PrintMsg($"{My.Resources.ResText_WErr}{ex.Message}{vbCrLf}{ex.StackTrace}")
                            SetStatusLight(LWStatus.Err)
                            StopFlag = True
                        End Try
                        If Pause Then
                            Invoke(Sub()
                                       With Microsoft.WindowsAPICodePack.Taskbar.TaskbarManager.Instance
                                           .SetProgressState(Microsoft.WindowsAPICodePack.Taskbar.TaskbarProgressBarState.Paused)
                                       End With
                                   End Sub)
                        End If
                        While Pause AndAlso Not StopFlag
                            Threading.Thread.Sleep(10)
                            If Not Pause Then
                                Invoke(Sub()
                                           With Microsoft.WindowsAPICodePack.Taskbar.TaskbarManager.Instance
                                               .SetProgressState(Microsoft.WindowsAPICodePack.Taskbar.TaskbarProgressBarState.Normal)
                                           End With
                                       End Sub)
                            End If
                        End While
                    End If
                    If ufReadCountIncremented Then
                        UFReadCount.Dec()
                        ufReadCountIncremented = False
                    End If
                    Me.Invoke(Sub() Timer1_Tick(sender, e))
                    Dim TotalBytesWritten As Long = CLng(UnwrittenSizeOverrideValue)
                    While True
                        Threading.Thread.Sleep(0)
                        SyncLock UFReadCount
                            If UFReadCount > 0 Then Continue While
                            UnwrittenSizeOverrideValue = 0
                            UnwrittenCountOverrideValue = 0
                            CurrentFilesProcessed = 0
                            CurrentBytesProcessed = 0
                            Exit While
                        End SyncLock
                    End While
                    Modified = True
                    If Not StopFlag Then
                        Dim TimeCost As TimeSpan = Now - StartTime
                        OnWriteFinishMessage = ($"{My.Resources.ResText_WFTime}{(Math.Floor(TimeCost.TotalHours)).ToString().PadLeft(2, "0"c)}:{TimeCost.Minutes.ToString().PadLeft(2, "0"c)}:{TimeCost.Seconds.ToString().PadLeft(2, "0"c)} {My.Resources.ResText_AvgS}{IOManager.FormatSize(TotalBytesWritten \ CLng(Math.Max(1, TimeCost.TotalSeconds)))}/s")
                        OnWriteFinished()
                    Else
                        OnWriteFinishMessage = (My.Resources.ResText_WCnd)
                    End If
                Catch ex As Exception
                    Invoke(Sub() MessageBox.Show(New Form With {.TopMost = True}, $"{My.Resources.ResText_WErr}{vbCrLf}{ex.ToString}"))
                    PrintMsg($"{My.Resources.ResText_WErr}{ex.Message}{vbCrLf}{ex.StackTrace}")
                    SetStatusLight(LWStatus.Err)
                    StopFlag = True
                Finally
                    Try
                        WaitForTasks(writeTasks)
                    Catch cleanupEx As Exception
                        Log.Error(cleanupEx, "Compressed writer write tasks failed during cleanup.")
                        StopFlag = True
                    End Try
                    Try
                        WaitForHashTasks(hashTasks)
                    Catch cleanupEx As Exception
                        Log.Error(cleanupEx, "Compressed writer hash tasks failed during cleanup.")
                        StopFlag = True
                    End Try
                    If activeFile IsNot Nothing Then
                        Try
                            activeFile.Close()
                        Catch cleanupEx As Exception
                            Log.Error(cleanupEx, "Compressed writer file cleanup failed. SourcePath={SourcePath}.", activeFile.SourcePath)
                        Finally
                            activeFile = Nothing
                        End Try
                    End If
                    If wBufferPtr <> IntPtr.Zero Then
                        Try
                            Marshal.FreeHGlobal(wBufferPtr)
                        Catch cleanupEx As Exception
                            Log.Error(cleanupEx, "Compressed writer buffer cleanup failed.")
                        Finally
                            wBufferPtr = IntPtr.Zero
                        End Try
                    End If
                    If ufReadCountIncremented Then
                        UFReadCount.Dec()
                        ufReadCountIncremented = False
                    End If
                    If mediumRemovalPrevented Then
                        Try
                            TapeUtils.AllowMediumRemoval(driveHandle)
                        Catch cleanupEx As Exception
                            Log.Error(cleanupEx, "Allow-medium-removal failed during compressed writer cleanup.")
                        End Try
                    End If
                    If tapeUnitReserved Then
                        Try
                            TapeUtils.Flush(driveHandle)
                        Catch cleanupEx As Exception
                            Log.Error(cleanupEx, "Tape flush failed during compressed writer cleanup.")
                        End Try
                        Try
                            TapeUtils.ReleaseUnit(driveHandle)
                        Catch cleanupEx As Exception
                            Log.Error(cleanupEx, "Tape unit release failed during compressed writer cleanup.")
                        End Try
                    End If
                    If My.Settings.LTFSWriter_PowerPolicyOnWriteEnd <> Guid.Empty Then
                        Try
                            Process.Start(New ProcessStartInfo With {.FileName = "powercfg",
                                          .Arguments = $"/s {My.Settings.LTFSWriter_PowerPolicyOnWriteEnd.ToString()}",
                                          .WindowStyle = ProcessWindowStyle.Hidden})
                        Catch cleanupEx As Exception
                            Log.Error(cleanupEx, "Power policy restore failed during compressed writer cleanup.")
                        End Try
                    End If
                    Try
                        LockGUI(False)
                    Catch cleanupEx As Exception
                        Log.Error(cleanupEx, "Compressed writer GUI unlock failed.")
                    End Try
                End Try
                RefreshDisplay()
                RefreshCapacity()
                Invoke(Sub()
                           If Not StopFlag AndAlso WA0ToolStripMenuItem.Checked AndAlso MessageBox.Show(New Form With {.TopMost = True}, My.Resources.ResText_WFUp, My.Resources.ResText_OpSucc, MessageBoxButtons.OKCancel) = DialogResult.OK Then
                               更新数据区索引ToolStripMenuItem_Click(sender, e)
                           End If
                           PrintMsg(OnWriteFinishMessage)
                           SetStatusLight(If(StopFlag, LWStatus.Err, LWStatus.Succ))
                           RaiseEvent WriteFinished()
                       End Sub)
            End Sub)
            StopFlag = False
            LockGUI()
            th.Start()
        End If
    End Sub
    Public Sub ResetPowerPolicyUI0()
        无更改ToolStripMenuItem.Checked = False
        平衡ToolStripMenuItem.Checked = False
        节能ToolStripMenuItem.Checked = False
        高性能ToolStripMenuItem.Checked = False
        其他ToolStripMenuItem.Checked = False
        其他ToolStripMenuItem.Text = My.Resources.ResText_Other
    End Sub
    Public Sub ResetPowerPolicyUI1()
        无更改ToolStripMenuItem1.Checked = False
        平衡ToolStripMenuItem1.Checked = False
        节能ToolStripMenuItem1.Checked = False
        高性能ToolStripMenuItem1.Checked = False
        其他ToolStripMenuItem1.Checked = False
        其他ToolStripMenuItem1.Text = My.Resources.ResText_Other
    End Sub
    Private Sub 无更改ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 无更改ToolStripMenuItem.Click
        ResetPowerPolicyUI0()
        无更改ToolStripMenuItem.Checked = True
        My.Settings.LTFSWriter_PowerPolicyOnWriteBegin = Guid.Empty
    End Sub

    Private Sub 平衡ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 平衡ToolStripMenuItem.Click
        ResetPowerPolicyUI0()
        平衡ToolStripMenuItem.Checked = True
        My.Settings.LTFSWriter_PowerPolicyOnWriteBegin = New Guid("381b4222-f694-41f0-9685-ff5bb260df2e")
    End Sub

    Private Sub 节能ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 节能ToolStripMenuItem.Click
        ResetPowerPolicyUI0()
        节能ToolStripMenuItem.Checked = True
        My.Settings.LTFSWriter_PowerPolicyOnWriteBegin = New Guid("a1841308-3541-4fab-bc81-f71556f20b4a")
    End Sub

    Private Sub 高性能ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 高性能ToolStripMenuItem.Click
        ResetPowerPolicyUI0()
        高性能ToolStripMenuItem.Checked = True
        My.Settings.LTFSWriter_PowerPolicyOnWriteBegin = New Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c")
    End Sub

    Private Sub 其他ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 其他ToolStripMenuItem.Click
        Try
            Dim s As String = ""
            If DisplayHelper.ShowInputDialog("Power policy GUID", "Power policy on write begin", s) <> DialogResult.OK Then Exit Sub
            My.Settings.LTFSWriter_PowerPolicyOnWriteBegin = New Guid(s)
            ResetPowerPolicyUI0()
            其他ToolStripMenuItem.Checked = True
            其他ToolStripMenuItem.Text = $"{My.Resources.ResText_Other}: {My.Settings.LTFSWriter_PowerPolicyOnWriteBegin.ToString()}"
        Catch ex As Exception
            MessageBox.Show(New Form With {.TopMost = True}, ex.ToString())
        End Try
    End Sub

    Private Sub 无更改ToolStripMenuItem1_Click(sender As Object, e As EventArgs) Handles 无更改ToolStripMenuItem1.Click
        ResetPowerPolicyUI1()
        无更改ToolStripMenuItem1.Checked = True
        My.Settings.LTFSWriter_PowerPolicyOnWriteEnd = Guid.Empty
    End Sub

    Private Sub 平衡ToolStripMenuItem1_Click(sender As Object, e As EventArgs) Handles 平衡ToolStripMenuItem1.Click
        ResetPowerPolicyUI1()
        平衡ToolStripMenuItem1.Checked = True
        My.Settings.LTFSWriter_PowerPolicyOnWriteEnd = New Guid("381b4222-f694-41f0-9685-ff5bb260df2e")
    End Sub

    Private Sub 节能ToolStripMenuItem1_Click(sender As Object, e As EventArgs) Handles 节能ToolStripMenuItem1.Click
        ResetPowerPolicyUI1()
        节能ToolStripMenuItem1.Checked = True
        My.Settings.LTFSWriter_PowerPolicyOnWriteEnd = New Guid("a1841308-3541-4fab-bc81-f71556f20b4a")
    End Sub

    Private Sub 高性能ToolStripMenuItem1_Click(sender As Object, e As EventArgs) Handles 高性能ToolStripMenuItem1.Click
        ResetPowerPolicyUI1()
        高性能ToolStripMenuItem1.Checked = True
        My.Settings.LTFSWriter_PowerPolicyOnWriteEnd = New Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c")
    End Sub

    Private Sub 其他ToolStripMenuItem1_Click(sender As Object, e As EventArgs) Handles 其他ToolStripMenuItem1.Click
        Try
            Dim s As String = ""
            If DisplayHelper.ShowInputDialog("Power policy GUID", "Power policy on write end", s) <> DialogResult.OK Then Exit Sub
            My.Settings.LTFSWriter_PowerPolicyOnWriteEnd = New Guid(s)
            ResetPowerPolicyUI1()
            其他ToolStripMenuItem1.Checked = True
            其他ToolStripMenuItem1.Text = $"{My.Resources.ResText_Other}: {My.Settings.LTFSWriter_PowerPolicyOnWriteEnd.ToString()}"
        Catch ex As Exception
            MessageBox.Show(New Form With {.TopMost = True}, ex.ToString())
        End Try
    End Sub

    Private Sub 详情ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 详情ToolStripMenuItem.Click
        If TreeView1.SelectedNode IsNot Nothing Then
            If TypeOf TreeView1.SelectedNode.Tag IsNot ltfsindex.directory Then Exit Sub
            Dim d As ltfsindex.directory = DirectCast(TreeView1.SelectedNode.Tag, ltfsindex.directory)
            Dim editableDirectory As ltfsindex.directory = CreateDirectoryDetailsEdit(d)
            Dim PG1 As New SettingPanel
            PG1.PropertyGrid1.SelectedObject = editableDirectory
            PG1.Text = TextBoxSelectedPath.Text
            If PG1.ShowDialog() = DialogResult.OK Then
                ApplyDirectoryDetailsEdit(d, editableDirectory)
                If TotalBytesUnindexed = 0 Then TotalBytesUnindexed = 1
                RefreshDisplay()
            End If
        End If
    End Sub

    Private Sub 解锁ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 解锁ToolStripMenuItem.Click
        TapeUtils.CloseTapeDrive(driveHandle)
        If Not TapeUtils.IsOpened(driveHandle) Then SetStatusLight(LWStatus.NotReady)
        MessageBox.Show(New Form With {.TopMost = True}, $"Lock: {TapeUtils.DriveOpenCount(TapeDrive)}")
    End Sub

    Private Sub 错误率ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 错误率ToolStripMenuItem.Click
        If DisplayHelper.ShowInputDialog(My.Resources.ResText_ErrRateLog, My.Resources.ResText_Setting, My.Settings.LTFSWriter_AutoCleanErrRateLogThreashould) <> DialogResult.OK Then Exit Sub
        My.Settings.Save()
        错误率ToolStripMenuItem.Text = $"{My.Resources.ResText_ErrRateLog}{My.Settings.LTFSWriter_AutoCleanErrRateLogThreashould}s"
    End Sub

    Private Sub 容量刷新间隔30sToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 容量刷新间隔30sToolStripMenuItem.Click
        If DisplayHelper.ShowInputDialog(My.Resources.ResText_SCIntv, My.Resources.ResText_Setting, CapacityRefreshInterval) <> DialogResult.OK Then Exit Sub
    End Sub



    <Category("LTFSWriter")>
    Public Property LastSearchKW As String = ""
    Private Const SearchMatchDirectory As UInteger = 1UI
    Private Const SearchMatchFile As UInteger = 2UI
    Private _searchSessionActive As Boolean
    Private _searchScope As ltfsindex.directory
    Private _searchScopePath As String = String.Empty
    Private _searchLastHit As SearchEntry

    Public Function GetSearchInput() As String
        DisplayHelper.ShowInputDialog("Keyword", "Search", LastSearchKW)
        Return LastSearchKW
    End Function

    Private NotInheritable Class SearchEntry
        Public Property Directory As ltfsindex.directory
        Public Property File As ltfsindex.file
        Public Property DirectoryPath As String
        Public Property Path As String
        Public Property FileIndex As Integer
        Public Property MatchKind As UInteger
        Public Property NativeRecordOffset As Long = -1L
    End Class

    Private Shared Function AppendSearchPath(parentPath As String, childName As String) As String
        If String.IsNullOrEmpty(parentPath) Then Return If(childName, String.Empty)
        If String.IsNullOrEmpty(childName) Then Return parentPath
        Return parentPath & "\" & childName
    End Function

    Private Iterator Function EnumerateSearchEntries(directory As ltfsindex.directory,
                                                      directoryPath As String) As IEnumerable(Of SearchEntry)
        If directory Is Nothing Then Exit Function

        'Search intentionally follows only the committed LTFS directory/file
        'chain.  Pending files live in UnwrittenFiles and are not search hits.

        Yield New SearchEntry With {
            .Directory = directory,
            .DirectoryPath = directoryPath,
            .Path = directoryPath,
            .FileIndex = -1,
            .MatchKind = SearchMatchDirectory,
            .NativeRecordOffset = If(directory.HasLazyRecord, directory.LazyRecordOffset, -1L)}

        Dim fileIndex As Integer = 0
        For Each childFile As ltfsindex.file In directory.EnumerateLazyFiles()
            Yield New SearchEntry With {
                .Directory = directory,
                .File = childFile,
                .DirectoryPath = directoryPath,
                .Path = AppendSearchPath(directoryPath, childFile.name),
                .FileIndex = fileIndex,
                .MatchKind = SearchMatchFile,
                .NativeRecordOffset = If(childFile.HasLazyRecord, childFile.LazyRecordOffset, -1L)}
            fileIndex += 1
        Next

        For Each childDirectory As ltfsindex.directory In directory.EnumerateLazyDirectories()
            Dim childPath As String = AppendSearchPath(directoryPath, childDirectory.name)
            For Each nestedEntry As SearchEntry In EnumerateSearchEntries(childDirectory, childPath)
                Yield nestedEntry
            Next
        Next
    End Function

    Private Shared Function SameFileRecord(left As ltfsindex.file, right As ltfsindex.file) As Boolean
        If left Is Nothing OrElse right Is Nothing Then Return False
        If Object.ReferenceEquals(left, right) Then Return True
        Return left.HasLazyRecord AndAlso right.HasLazyRecord AndAlso
               left.LazyRecordOffset = right.LazyRecordOffset AndAlso
               Object.ReferenceEquals(left.LazyStoreReference, right.LazyStoreReference)
    End Function

    Private Shared Function SameSearchEntry(left As SearchEntry, right As SearchEntry) As Boolean
        If left Is Nothing OrElse right Is Nothing Then Return False

        If left.MatchKind <> 0UI AndAlso right.MatchKind <> 0UI AndAlso
           left.NativeRecordOffset >= 0 AndAlso right.NativeRecordOffset >= 0 Then
            Return left.MatchKind = right.MatchKind AndAlso
                   left.NativeRecordOffset = right.NativeRecordOffset
        End If

        If left.File IsNot Nothing OrElse right.File IsNot Nothing Then
            Return left.File IsNot Nothing AndAlso right.File IsNot Nothing AndAlso
                   SameFileRecord(left.File, right.File)
        End If

        Return SameDirectoryRecord(left.Directory, right.Directory)
    End Function

    Private Shared Function TryGetNativeSearchCursor(entry As SearchEntry,
                                                     ByRef matchKind As UInteger,
                                                     ByRef recordOffset As Long) As Boolean
        matchKind = 0UI
        recordOffset = -1L
        If entry Is Nothing Then Return False

        If (entry.MatchKind = SearchMatchDirectory OrElse entry.MatchKind = SearchMatchFile) AndAlso
           entry.NativeRecordOffset >= 0 Then
            matchKind = entry.MatchKind
            recordOffset = entry.NativeRecordOffset
            Return True
        End If

        If entry.File IsNot Nothing AndAlso entry.File.HasLazyRecord Then
            matchKind = SearchMatchFile
            recordOffset = entry.File.LazyRecordOffset
            Return True
        End If
        If entry.Directory IsNot Nothing AndAlso entry.Directory.HasLazyRecord Then
            matchKind = SearchMatchDirectory
            recordOffset = entry.Directory.LazyRecordOffset
            Return True
        End If
        Return False
    End Function

    Private Shared Function CreateNativeSearchEntry(value As NativeStoreSearchResultData) As SearchEntry
        If value Is Nothing OrElse Not value.Found Then Return Nothing
        Dim fileIndex As Integer = -1
        If value.MatchKind = SearchMatchFile AndAlso value.FileIndex >= 0 AndAlso
           value.FileIndex <= Integer.MaxValue Then
            fileIndex = CInt(value.FileIndex)
        End If
        Return New SearchEntry With {
            .DirectoryPath = If(value.DirectoryPath, String.Empty),
            .Path = If(value.Path, String.Empty),
            .FileIndex = fileIndex,
            .MatchKind = value.MatchKind,
            .NativeRecordOffset = value.RecordOffset}
    End Function

    Private Shared Function SearchContains(value As String, keyword As String, caseSensitive As Boolean) As Boolean
        If String.IsNullOrEmpty(keyword) Then Return True
        If value Is Nothing Then Return False
        Return value.IndexOf(keyword,
                             If(caseSensitive, StringComparison.Ordinal, StringComparison.OrdinalIgnoreCase)) >= 0
    End Function

    Private Shared Function MatchesSearchEntry(entry As SearchEntry,
                                               keyword As String,
                                               fidMode As Boolean,
                                               fid As String,
                                               caseSensitive As Boolean) As Boolean
        If entry Is Nothing Then Return False
        If entry.File Is Nothing Then
            If fidMode AndAlso entry.Directory IsNot Nothing AndAlso entry.Directory.fileuid.ToString() = fid Then Return True
            Return SearchContains(If(entry.Directory Is Nothing, Nothing, entry.Directory.name), keyword, caseSensitive)
        End If

        If fidMode AndAlso entry.File.fileuid.ToString() = fid Then Return True
        Return SearchContains(entry.File.name, keyword, caseSensitive)
    End Function

    Private Function FindFirstSearchEntry(directory As ltfsindex.directory,
                                          directoryPath As String,
                                          keyword As String,
                                          fidMode As Boolean,
                                          fid As String,
                                          caseSensitive As Boolean,
                                          resumeEntry As SearchEntry,
                                          ByRef resumeEntryFound As Boolean,
                                          progressCallback As Action) As SearchEntry
        resumeEntryFound = resumeEntry Is Nothing
        For Each entry As SearchEntry In EnumerateSearchEntries(directory, directoryPath)
            If progressCallback IsNot Nothing Then progressCallback()
            If StopFlag Then
                StopFlag = False
                Exit For
            End If
            'The first yielded entry is the search root itself.  The root is
            'the current location, so search its children without returning
            'the same directory on every F3 press.
            If entry.File Is Nothing AndAlso entry.Directory Is directory Then Continue For

            'F3 continues after the last hit.  The hit may be a directory or
            'a file, so compare the complete search entry instead of relying
            'on the current ListView directory after navigation.
            If Not resumeEntryFound Then
                If SameSearchEntry(entry, resumeEntry) Then resumeEntryFound = True
                Continue For
            End If

            If MatchesSearchEntry(entry, keyword, fidMode, fid, caseSensitive) Then Return entry
        Next
        Return Nothing
    End Function

    Private Function FindListViewFileIndex(directory As ltfsindex.directory,
                                           target As ltfsindex.file,
                                           Optional directFileIndex As Integer = -1) As Integer
        If directory Is Nothing OrElse target Is Nothing Then Return -1

        For i As Integer = 0 To _listRows.Count - 1
            Dim candidate As ltfsindex.file = TryCast(_listRows(i).Value, ltfsindex.file)
            If SameFileRecord(candidate, target) Then Return i
        Next

        If _lazyListDirectory Is Nothing OrElse Not SameDirectoryRecord(_lazyListDirectory, directory) Then Return -1
        'Lazy rows put persisted files before unwritten files.  The search
        'entry already knows the persisted file's direct index, so avoid
        'walking the whole directory on the UI thread and account for the
        'persisted rows when selecting the virtual row.
        If directFileIndex >= 0 AndAlso directFileIndex < _lazyListFileCount Then
            Return _listRows.Count + directFileIndex
        End If

        Dim baseIndex As Integer = _listRows.Count
        Dim directIndex As Integer = 0
        For Each candidate As ltfsindex.file In directory.EnumerateLazyFiles()
            If SameFileRecord(candidate, target) Then Return baseIndex + directIndex
            directIndex += 1
        Next
        Return -1
    End Function

    Private Shared Function GetSearchProgressValue(processed As Long, total As Long) As Integer
        If total <= 0 Then Return 0
        Dim scaled As Double = processed / CDbl(total) * SearchProgressMaximum
        Return CInt(Math.Max(0.0, Math.Min(CDbl(SearchProgressMaximum), scaled)))
    End Function

    Private Sub BeginSearchProgress(total As Long)
        Threading.Interlocked.Exchange(_searchProgressActive, 1)
        Dim update As Action =
            Sub()
                If IsDisposed Then Return
                ToolStripProgressBar1.Minimum = 0
                ToolStripProgressBar1.Maximum = SearchProgressMaximum
                ToolStripProgressBar1.Value = 0
                ToolStripProgressBar1.ToolTipText = $"Search 0/{total}"
                ToolStripStatusLabel6.Text = $"Search 0/{total}"
                ToolStripStatusLabel6.ToolTipText = ToolStripStatusLabel6.Text
            End Sub
        If IsHandleCreated AndAlso InvokeRequired Then
            Invoke(update)
        Else
            update()
        End If
    End Sub

    Private Sub ReportSearchProgress(processed As Long, total As Long, Optional ForcedRetryOnly As Boolean = False)
        Static _lastReportSearchProgress As (processed As Long, total As Long)
        _lastReportSearchProgress = (processed, total)
        If ForcedRetryOnly Then
            processed = _lastReportSearchProgress.processed
            total = _lastReportSearchProgress.total
        End If
        If (Not ForcedRetryOnly) AndAlso Threading.Volatile.Read(_searchProgressActive) = 0 Then Return
        If processed > 1 AndAlso processed < total AndAlso processed Mod 256 <> 0 Then Return
        Static LastReport As Date
        If (Not ForcedRetryOnly) AndAlso (Now - LastReport).TotalMilliseconds < 100 Then Return
        LastReport = Now
        Dim value As Integer = GetSearchProgressValue(processed, total)
        Dim update As Action =
            Sub()
                If (Not ForcedRetryOnly) AndAlso (Threading.Volatile.Read(_searchProgressActive) = 0 OrElse IsDisposed) Then Return
                ToolStripProgressBar1.Value = value
                ToolStripProgressBar1.ToolTipText = $"Search {processed}/{total}"
                ToolStripStatusLabel6.Text = $"Search {processed}/{total}"
                ToolStripStatusLabel6.ToolTipText = ToolStripStatusLabel6.Text
            End Sub
        Try
            If IsHandleCreated AndAlso InvokeRequired Then
                BeginInvoke(update)
            Else
                update()
            End If
        Catch
        End Try
    End Sub

    Private Sub FinishSearchProgress(success As Boolean)
        Dim update As Action =
            Sub()
                If Not IsDisposed Then
                    ToolStripProgressBar1.Value = If(success, SearchProgressMaximum, 0)
                    ToolStripProgressBar1.ToolTipText = If(success, "Search complete", "Search failed")
                    ToolStripStatusLabel6.Text = If(success, "Search complete", "Search failed")
                    ToolStripStatusLabel6.ToolTipText = ToolStripStatusLabel6.Text
                End If
                Threading.Interlocked.Exchange(_searchProgressActive, 0)
                LockGUI(False)
            End Sub
        Try
            If IsHandleCreated AndAlso InvokeRequired Then
                Invoke(update)
            Else
                update()
            End If
        Catch
            Threading.Interlocked.Exchange(_searchProgressActive, 0)
        End Try
    End Sub

    Private Shared Function GetDirectorySortProgressValue(processed As Long, total As Long) As Integer
        If total <= 0 Then Return 0
        Dim scaled As Double = processed / CDbl(total) * DirectorySortProgressMaximum
        Return CInt(Math.Max(0.0, Math.Min(CDbl(DirectorySortProgressMaximum), scaled)))
    End Function

    Private Sub BeginDirectorySortProgress(total As Long)
        Threading.Interlocked.Exchange(_directorySortProgressActive, 1)
        Dim update As Action =
            Sub()
                If IsDisposed Then Return
                ToolStripProgressBar1.Minimum = 0
                ToolStripProgressBar1.Maximum = DirectorySortProgressMaximum
                ToolStripProgressBar1.Value = 0
                ToolStripProgressBar1.ToolTipText = $"Sort 0/{total}"
                ToolStripStatusLabel6.Text = $"Sort 0/{total}"
                ToolStripStatusLabel6.ToolTipText = ToolStripStatusLabel6.Text
            End Sub
        If IsHandleCreated AndAlso InvokeRequired Then
            Invoke(update)
        Else
            update()
        End If
    End Sub

    Private Sub ReportDirectorySortProgress(processed As ULong, total As ULong)
        If Threading.Volatile.Read(_directorySortProgressActive) = 0 Then Return
        Dim processedValue As Long = If(processed > CULng(Long.MaxValue), Long.MaxValue, CLng(processed))
        Dim totalValue As Long = If(total > CULng(Long.MaxValue), Long.MaxValue, CLng(total))
        Dim value As Integer = GetDirectorySortProgressValue(processedValue, totalValue)
        Dim update As Action =
            Sub()
                If Threading.Volatile.Read(_directorySortProgressActive) = 0 OrElse IsDisposed Then Return
                ToolStripProgressBar1.Value = value
                ToolStripProgressBar1.ToolTipText = $"Sort {processedValue}/{totalValue}"
                ToolStripStatusLabel6.Text = $"Sort {processedValue}/{totalValue}"
                ToolStripStatusLabel6.ToolTipText = ToolStripStatusLabel6.Text
            End Sub
        Try
            If IsHandleCreated AndAlso InvokeRequired Then
                BeginInvoke(update)
            Else
                update()
            End If
        Catch
        End Try
    End Sub

    Private Sub FinishDirectorySortProgress(success As Boolean)
        Dim update As Action =
            Sub()
                If Not IsDisposed Then
                    ToolStripProgressBar1.Value = If(success, DirectorySortProgressMaximum, 0)
                    ToolStripProgressBar1.ToolTipText = If(success, "Sort complete", "Sort failed")
                    ToolStripStatusLabel6.Text = If(success, "Sort complete", "Sort failed")
                    ToolStripStatusLabel6.ToolTipText = ToolStripStatusLabel6.Text
                End If
                Threading.Interlocked.Exchange(_directorySortProgressActive, 0)
                LockGUI(False)
            End Sub
        Try
            If IsHandleCreated AndAlso InvokeRequired Then
                Invoke(update)
            Else
                update()
            End If
        Catch
            Threading.Interlocked.Exchange(_directorySortProgressActive, 0)
            Try
                LockGUI(False)
            Catch
            End Try
        End Try
    End Sub

    Private Sub SortCurrentDirectory(directory As ltfsindex.directory, logicalSort As Boolean)
        If directory Is Nothing OrElse Not AllowOperation Then Exit Sub
        Dim localeName As String = Globalization.CultureInfo.CurrentCulture.Name
        Dim total As Long
        Try
            total = Math.Max(0L, directory.GetDirectSortChildCount())
        Catch
            total = 0L
        End Try

        LockGUI(True)
        BeginDirectorySortProgress(total)
        Task.Run(
            Sub()
                Dim nativeProgressCallback As NativeDirectorySortProgressCallback =
                    Sub(processed As ULong, nativeTotal As ULong, userData As IntPtr)
                        ReportDirectorySortProgress(processed, nativeTotal)
                    End Sub
                Try
                    Dim nativeResult As NativeStoreDirectorySortResultData = Nothing
                    Dim nativeUsed As Boolean = False
                    Try
                        nativeUsed = directory.TrySortChildrenNative(logicalSort,
                                                                      localeName,
                                                                      nativeProgressCallback,
                                                                      nativeResult)
                    Catch ex As EntryPointNotFoundException
                        'An older native DLL can still use the managed sort
                        'path until the application is rebuilt.
                        nativeUsed = False
                    End Try

                    If Not nativeUsed Then
                        If logicalSort Then
                            Dim explorerComparer As New ExplorerUtils
                            directory.SortChildrenByName(
                                Function(left As String, right As String) As Integer
                                    Return explorerComparer.Compare(left, right)
                                End Function,
                                Function(left As String, right As String) As Integer
                                    Return explorerComparer.Compare(left, right)
                                End Function)
                        Else
                            directory.SortChildrenByName(
                                Function(left As String, right As String) As Integer
                                    Return String.Compare(If(left, String.Empty), If(right, String.Empty), StringComparison.CurrentCulture)
                                End Function,
                                Function(left As String, right As String) As Integer
                                    Return String.Compare(If(left, String.Empty), If(right, String.Empty), StringComparison.CurrentCulture)
                                End Function)
                        End If
                        If total > 0 Then ReportDirectorySortProgress(CULng(total), CULng(total))
                    End If

                    RefreshDisplay()
                    FinishDirectorySortProgress(True)
                Catch ex As Exception
                    PrintMsg($"Directory sort failed: {ex}", Warning:=True, ForceLog:=True)
                    FinishDirectorySortProgress(False)
                Finally
                    GC.KeepAlive(nativeProgressCallback)
                End Try
            End Sub)
    End Sub

    Public Sub Search(Optional ByVal KW As String = Nothing)
        Dim continueSearch As Boolean = KW Is Nothing
        Dim searchKeyword As String = If(continueSearch, LastSearchKW, KW)
        Dim searchRoot As ltfsindex.directory = Nothing
        Dim searchRootNode As TreeNode = Nothing
        Dim rootPath As String = String.Empty

        'Keep F3 inside the scope where the search started.  Selecting a
        'result changes the visible tree node, but must not change the search
        'scope to that result's child directory.
        If continueSearch AndAlso _searchSessionActive AndAlso _searchScope IsNot Nothing Then
            searchRoot = _searchScope
            rootPath = _searchScopePath
        Else
            Dim selectedTreeNode As TreeNode = TreeView1.SelectedNode
            'If selectedTreeNode IsNot Nothing Then
            '    searchRoot = TryCast(selectedTreeNode.Tag, ltfsindex.directory)
            '    If searchRoot IsNot Nothing Then
            '        searchRootNode = selectedTreeNode
            '    Else
            '        'Archive/file nodes can be selected in the tree.  Search from
            '        'their nearest LTFS directory instead of silently jumping to
            '        'the first root directory.
            '        Dim ancestor As TreeNode = selectedTreeNode.Parent
            '        While ancestor IsNot Nothing
            '            searchRoot = TryCast(ancestor.Tag, ltfsindex.directory)
            '            If searchRoot IsNot Nothing Then
            '                searchRootNode = ancestor
            '                Exit While
            '            End If
            '            ancestor = ancestor.Parent
            '        End While
            '    End If
            'End If
            If searchRoot Is Nothing AndAlso schema IsNot Nothing AndAlso
               schema._directory IsNot Nothing AndAlso schema._directory.Count > 0 Then
                searchRoot = schema._directory(0)
            End If

            If searchRoot IsNot Nothing Then
                If searchRootNode IsNot Nothing Then
                    rootPath = GetPath(searchRootNode).TrimStart("\"c)
                End If
                If String.IsNullOrEmpty(rootPath) Then rootPath = If(searchRoot.name, String.Empty)

                _searchScope = searchRoot
                _searchScopePath = rootPath
                _searchLastHit = Nothing
                _searchSessionActive = True
            End If
        End If
        If searchRoot Is Nothing Then Return

        Dim resumeEntry As SearchEntry = Nothing
        If continueSearch AndAlso _searchSessionActive Then resumeEntry = _searchLastHit

        'For the first search (or after a session was not established), keep
        'the old behavior of starting after the currently selected file.
        If resumeEntry Is Nothing AndAlso TypeOf ListView1.Tag Is ltfsindex.directory Then
            Dim selectedDirectory As ltfsindex.directory = DirectCast(ListView1.Tag, ltfsindex.directory)
            Dim selectedFile As ltfsindex.file = GetSelectedListViewFile()
            If selectedFile IsNot Nothing AndAlso Not IsPendingFile(selectedDirectory, selectedFile) Then
                resumeEntry = New SearchEntry With {
                    .Directory = selectedDirectory,
                    .File = selectedFile,
                    .MatchKind = SearchMatchFile,
                    .NativeRecordOffset = If(selectedFile.HasLazyRecord, selectedFile.LazyRecordOffset, -1L)}
            End If
        End If

        searchKeyword = If(searchKeyword, String.Empty)
        Dim fidMode As Boolean = searchKeyword.StartsWith("fid:", StringComparison.OrdinalIgnoreCase)
        Dim fid As String = If(fidMode, searchKeyword.Substring(4).Trim(), String.Empty)
        Dim caseSensitive As Boolean = My.Settings.Application_CaseSensitiveSearch
        Dim nativeStore As LazySchemaStore = Nothing
        Dim useNativeSearch As Boolean = False
        If Not fidMode AndAlso searchRoot.HasLazyRecord Then
            nativeStore = TryCast(searchRoot.LazyStoreReference, LazySchemaStore)
            useNativeSearch = nativeStore IsNot Nothing AndAlso nativeStore.CanUseNativeSearch()
            'Pending files are deliberately outside the search scope, so they
            'do not disable the native committed-index search fast path.
        End If

        Dim nativeResumeKind As UInteger = 0UI
        Dim nativeResumeOffset As Long = -1L
        If useNativeSearch AndAlso resumeEntry IsNot Nothing AndAlso
           Not TryGetNativeSearchCursor(resumeEntry, nativeResumeKind, nativeResumeOffset) Then
            useNativeSearch = False
        End If

        LockGUI(True)
        Dim searchTotal As Long = 1L
        Try
            searchTotal = Math.Max(1L, 1L + searchRoot.TotalFiles + searchRoot.TotalDirectories)
        Catch
        End Try
        If resumeEntry IsNot Nothing Then
            If searchTotal > Long.MaxValue \ 2L Then
                searchTotal = Long.MaxValue
            Else
                searchTotal *= 2L
            End If
        End If
        BeginSearchProgress(searchTotal)
        Task.Run(Sub()
                     Dim hit As SearchEntry = Nothing
                     Dim processedEntries As Long = 0
                     Dim reportProgress As Action =
                         Sub()
                             Dim processed As Long = Threading.Interlocked.Increment(processedEntries)
                             ReportSearchProgress(processed, searchTotal)
                         End Sub
                     Try
                         If useNativeSearch Then
                             Dim nativeProgressCallback As NativeSearchProgressCallback =
                                 Sub(processed As ULong, total As ULong, userData As IntPtr)
                                     Dim progress As Long = If(processed > CULng(Long.MaxValue), Long.MaxValue, CLng(processed))
                                     ReportSearchProgress(progress, searchTotal)
                                 End Sub
                             Dim nativeResult As NativeStoreSearchResultData = nativeStore.SearchNative(
                                 searchRoot.LazyRecordOffset,
                                 rootPath,
                                 searchKeyword,
                                 caseSensitive,
                                 nativeResumeKind,
                                 nativeResumeOffset,
                                 nativeProgressCallback)
                             hit = CreateNativeSearchEntry(nativeResult)
                         Else
                             Dim resumeEntryFound As Boolean = False
                             hit = FindFirstSearchEntry(searchRoot,
                                                        rootPath,
                                                        searchKeyword,
                                                        fidMode,
                                                        fid,
                                                        caseSensitive,
                                                        resumeEntry,
                                                        resumeEntryFound,
                                                        reportProgress)

                             'If the resume entry was the last item, or is an
                             'unwritten file that is not in the lazy chain, wrap
                             'around and search from the beginning of the same
                             'scope.  This also makes F3 cycle through every hit.
                             If hit Is Nothing AndAlso resumeEntry IsNot Nothing Then
                                 Dim ignored As Boolean = False
                                 hit = FindFirstSearchEntry(searchRoot,
                                                            rootPath,
                                                            searchKeyword,
                                                            fidMode,
                                                            fid,
                                                            caseSensitive,
                                                            Nothing,
                                                            ignored,
                                                            reportProgress)
                             End If
                         End If

                         If hit IsNot Nothing Then
                             ReportSearchProgress(0, 0, True)
                             Invoke(Sub()
                                        Try
                                            _searchLastHit = hit
                                            _searchSessionActive = True
                                            Dim isFileHit As Boolean = hit.MatchKind = SearchMatchFile OrElse hit.File IsNot Nothing
                                            Dim directoryPath As String = If(isFileHit, hit.DirectoryPath, hit.Path)
                                            Dim node As TreeNode = FindTreeNodeByPath(directoryPath)
                                            If node IsNot Nothing Then
                                                If Not Object.ReferenceEquals(node, TreeView1.SelectedNode) Then
                                                    TreeView1.SelectedNode = node
                                                    RefreshDisplay()
                                                End If
                                                If isFileHit Then
                                                    If hit.File Is Nothing Then
                                                        Dim hitDirectory As ltfsindex.directory = TryCast(node.Tag, ltfsindex.directory)
                                                        If hitDirectory IsNot Nothing AndAlso hit.FileIndex >= 0 Then
                                                            hit.Directory = hitDirectory
                                                            hit.File = hitDirectory.GetLazyFileAt(hit.FileIndex)
                                                        End If
                                                    End If
                                                    If hit.File Is Nothing Then
                                                        PrintMsg($"Search found but file navigation failed: \{hit.Path}", ForceLog:=True)
                                                    Else
                                                        Dim rowIndex As Integer = FindListViewFileIndex(hit.Directory, hit.File, hit.FileIndex)
                                                        If rowIndex >= 0 Then SelectListViewIndex(rowIndex)
                                                        PrintMsg($"\{hit.Path}")
                                                    End If
                                                Else
                                                    hit.Directory = TryCast(node.Tag, ltfsindex.directory)
                                                    PrintMsg($"\{hit.Path}")
                                                    SelectListViewIndex(0)
                                                End If
                                            Else
                                                PrintMsg($"Search found but navigation failed: \{hit.Path}", ForceLog:=True)
                                            End If
                                        Finally
                                            FinishSearchProgress(True)
                                        End Try
                                    End Sub)
                             Return
                         End If

                         Invoke(Sub()
                                    FinishSearchProgress(True)
                                    MessageBox.Show(New Form With {.TopMost = True}, $"""{searchKeyword}"" not found.")
                                End Sub)
                     Catch ex As Exception
                         PrintMsg($"Search failed: {ex}", ForceLog:=True)
                         Try
                             FinishSearchProgress(False)
                         Catch
                         End Try
                     End Try
                 End Sub)
    End Sub
    Private Sub LTFSWriter_KeyDown(sender As Object, e As KeyEventArgs) Handles Me.KeyDown
        Select Case e.KeyCode
            Case Keys.V
                If Not AllowOperation Then Exit Sub
                If ListView1.Tag Is Nothing Then Exit Sub
                If e.Control Then
                    Dim Paths As String() = Nothing
                    If Clipboard.ContainsText Then
                        Paths = Clipboard.GetText().Split({vbCr, vbLf}, StringSplitOptions.RemoveEmptyEntries)
                    ElseIf Clipboard.ContainsFileDropList Then
                        Dim sc As Specialized.StringCollection = Clipboard.GetFileDropList()
                        Dim plist As New List(Of String)
                        For Each str As String In sc
                            plist.Add(str)
                        Next
                        Paths = plist.ToArray()
                    End If
                    If Paths IsNot Nothing AndAlso Paths.Count > 0 Then
                        Dim d As ltfsindex.directory = DirectCast(ListView1.Tag, ltfsindex.directory)
                        Dim overwrite As Boolean = 覆盖已有文件ToolStripMenuItem.Checked
                        If My.Settings.LTFSWriter_MatchPattern IsNot Nothing AndAlso My.Settings.LTFSWriter_MatchPattern.Length > 0 Then
                            AddFileOrDir(d, Paths, My.Settings.LTFSWriter_MatchPattern, overwrite)
                        Else
                            AddFileOrDir(d, Paths, overwrite)
                        End If
                    End If
                    粘贴选中ToolStripMenuItem_Click(sender, e)
                End If
            Case Keys.X
                If e.Control Then
                    If CurrentFocus Is TreeView1 Then
                        剪切目录ToolStripMenuItem_Click(sender, e)
                    ElseIf CurrentFocus Is ListView1 Then
                        剪切文件ToolStripMenuItem_Click(sender, e)
                    End If
                End If
            Case Keys.Delete
                If CurrentFocus Is TreeView1 Then
                    删除ToolStripMenuItem_Click(sender, e)
                ElseIf CurrentFocus Is ListView1 Then
                    删除文件ToolStripMenuItem_Click(sender, e)
                End If
            Case Keys.F
                If Not AllowOperation Then Exit Sub
                If Not e.Control Then Exit Select
                If e.Alt Then Exit Select
                If e.Shift Then Exit Select
                Search(GetSearchInput())
            Case Keys.N
                If Not AllowOperation Then Exit Sub
                If Not e.Control Then Exit Select
                If e.Alt Then Exit Select
                If e.Shift Then Exit Select
                新建目录ToolStripMenuItem_Click(sender, e)
            Case Keys.P
                If Not e.Control Then Exit Select
                If e.Alt Then
                    PipePause = Not PipePause
                End If
                If e.Shift Then Exit Select
                Pause = Not Pause
            Case Keys.F3
                If Not AllowOperation Then Exit Sub
                If LastSearchKW = "" Then GetSearchInput()
                Search()
            Case Keys.F5
                If e.Control Then
                    e.SuppressKeyPress = True
                    e.Handled = True
                    If Not AllowOperation Then Exit Select
                    Dim dir As ltfsindex.directory = TryCast(ListView1.Tag, ltfsindex.directory)
                    If dir IsNot Nothing Then SortCurrentDirectory(dir, Not e.Alt)
                    Exit Select
                End If
                RefreshDisplay()
            Case Keys.F8
                LockGUI(AllowOperation)
            Case Keys.F12
                If Not e.Control Then
                    Task.Run(Sub()
                                 Dim SP1 As New SettingPanel
                                 SP1.Text = Text
                                 SP1.SelectedObject = Me
                                 SP1.ShowDialog()
                             End Sub)

                Else
                    Task.Run(Sub()
                                 Dim SP1 As New SettingPanel
                                 SP1.Text = Text
                                 SP1.SelectedObject = StackTraces
                                 SP1.ShowDialog()
                             End Sub)
                End If
            Case Keys.T
                If e.Control Then
                    TestToolStripMenuItem.Visible = True
                End If
        End Select
    End Sub

    Private Sub ToolStripStatusLabel1_MouseUp(sender As Object, e As MouseEventArgs) Handles ToolStripStatusLabel1.MouseUp
        If e.Button = MouseButtons.Right Then
            ContextMenuStrip4.Show(ToolStripStatusLabel1.GetCurrentParent, e.Location + CType(ToolStripStatusLabel1.Bounds.Location, Size))
        End If
    End Sub

    Private Sub LTFSWriter_Closed(sender As Object, e As EventArgs) Handles Me.Closed
        Dim handleToClose As IntPtr = driveHandle
        Dim leaseToDispose As SCSIDeviceLockManager.WriterLease = _deviceLease
        _deviceLease = Nothing
        Dim closed As Boolean = False

        Try
            ' Do not let a close event wait behind an in-flight SCSI command.
            closed = TapeUtils.CloseTapeDrive(handleToClose, 0)
        Catch
        Finally
            If leaseToDispose IsNot Nothing Then
                leaseToDispose.Dispose()
            End If
        End Try

        If Not closed Then
            ' The command still owns the device lock.  Finish the close after
            ' it releases, without keeping the form close handler blocked.
            Threading.ThreadPool.QueueUserWorkItem(
                Sub(state As Object)
                    Try
                        TapeUtils.CloseTapeDrive(handleToClose)
                    Catch
                    End Try
                End Sub)
        End If
    End Sub

    Private Sub ToolTipChanErrLog_Popup(sender As Object, e As PopupEventArgs) Handles ToolTipChanErrLog.Popup
        Task.Run(Sub()
                     Threading.Thread.Sleep(3000)
                     ToolTipChanErrLogShowing = False
                     BeginInvoke(Sub() ToolTipChanErrLog.Hide(StatusStrip2))
                 End Sub)
    End Sub

    Private Sub SettingToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles SettingToolStripMenuItem.Click
        Form1.Button16_Click(sender, e)
    End Sub

    Private Sub 合并文件ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 合并文件ToolStripMenuItem.Click
        Dim selectedItems As List(Of ListViewItem) = GetSelectedListViewItems()
        If selectedItems.Count > 0 Then
            Dim flist As New List(Of ltfsindex.file)
            Dim d As ltfsindex.directory = DirectCast(ListView1.Tag, ltfsindex.directory)
            For Each SI As ListViewItem In selectedItems
                If TypeOf SI.Tag Is ltfsindex.file Then
                    Dim f As ltfsindex.file = CType(SI.Tag, ltfsindex.file)
                    If Not d.UnwrittenFiles.Contains(f) Then
                        flist.Add(f)
                    End If
                End If
            Next
            Dim newFile As New ltfsindex.file
            With newFile
                .backuptime = Now.ToUniversalTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff00Z")
                .accesstime = .backuptime
                .changetime = .backuptime
                .creationtime = .backuptime
                .modifytime = .backuptime
                .name = $"merge_{ .backuptime}"
                For i As Integer = 0 To flist.Count - 1
                    For j As Integer = 0 To flist(i).extentinfo.Count - 1
                        If flist(i).extentinfo(j).fileoffset >= flist(i).length Then Continue For
                        .extentinfo.Add(New ltfsindex.file.extent With {
                                        .bytecount = Math.Min(flist(i).extentinfo(j).bytecount, flist(i).length - flist(i).extentinfo(j).fileoffset),
                                        .byteoffset = flist(i).extentinfo(j).byteoffset,
                                        .fileoffset = flist(i).extentinfo(j).fileoffset + newFile.length,
                                        .partition = flist(i).extentinfo(j).partition,
                                        .startblock = flist(i).extentinfo(j).startblock})
                    Next
                    .length += flist(i).length
                Next
                .fileuid = schema.highestfileuid + 1
            End With
            d.AddFile(newFile)
            schema.highestfileuid += 1
            RefreshDisplay()
        End If
    End Sub

    Private ToolTipChanErrLogShowingChanged As Boolean = False
    Private _ToolTipChanErrLogShowing As Boolean
    Public Property ToolTipChanErrLogShowing As Boolean
        Get
            Return _ToolTipChanErrLogShowing
        End Get
        Set(value As Boolean)
            _ToolTipChanErrLogShowing = value
            ToolTipChanErrLogShowingChanged = True
        End Set
    End Property
    Private Sub ToolStripStatusLabelErrLog_MouseHover(sender As Object, e As EventArgs) Handles ToolStripStatusLabelErrLog.MouseHover
        ToolTipChanErrLogShowing = True
    End Sub

    Private Sub ToolStripStatusLabelErrLog_MouseLeave(sender As Object, e As EventArgs) Handles ToolStripStatusLabelErrLog.MouseLeave
        ToolTipChanErrLogShowing = False
    End Sub

    Public Property ColumnReordered As Boolean = False
    Private Sub ListView1_ColumnReordered(sender As Object, e As ColumnReorderedEventArgs) Handles ListView1.ColumnReordered
        If LoadComplete Then
            ColumnReordered = True
        End If
    End Sub

    Private Sub TextBoxSelectedPath_Leave(sender As Object, e As EventArgs) Handles TextBoxSelectedPath.Leave
        If Not LoadComplete Then Exit Sub
        ChangeDirectory(TextBoxSelectedPath.Text)
    End Sub

    Private Sub TextBoxSelectedPath_KeyUp(sender As Object, e As KeyEventArgs) Handles TextBoxSelectedPath.KeyUp
        If Not LoadComplete Then Exit Sub
        If e.KeyCode = Keys.Enter Then
            ChangeDirectory(TextBoxSelectedPath.Text)
        End If
    End Sub
    Public Function GetNode(path As String) As TreeNode
        Return FindTreeNodeByPath(path)
    End Function
    Public Sub ChangeDirectory(path As String)
        Dim targetdir As TreeNode = GetNode(path)
        If targetdir IsNot Nothing Then
            Dim selectionUnchanged As Boolean = Object.ReferenceEquals(TreeView1.SelectedNode, targetdir)
            TreeView1.SelectedNode = targetdir
            targetdir.Expand()
            If selectionUnchanged Then TriggerTreeView1Event(True)
        End If
    End Sub

    Private Sub 启动iSCSI服务ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 启动iSCSI服务ToolStripMenuItem.Click
        Dim svc As New iSCSIService()
        SetStatusLight(LWStatus.Busy)
        AddHandler svc.LogPrint, Sub(s As String)
                                     PrintMsg($"iSCSISVC> {s}", Category:="iSCSI")
                                 End Sub
        svc.port = 3262
        If DisplayHelper.ShowInputDialog("Port", "iSCSI Service", svc.port) <> DialogResult.OK Then Exit Sub
        svc.driveHandle = driveHandle
        svc.BlockSize = plabel.blocksize
        svc.ExtraPartitionCount = ExtraPartitionCount
        If My.Settings.LTFSWriter_LogEnabled Then svc.LogCommand = True
        SyncLock TapeUtils.GetSCSIOperationLock(driveHandle)
            svc.StartService($"iqn.2019-01.com.ltfscopygui:ltfswriter{If(CurrDrive IsNot Nothing, $":{CurrDrive.SerialNumber}", "")}")
            MessageBox.Show(New Form With {.TopMost = True}, $"Service running on port {svc.port}.")
            svc.StopService()
        End SyncLock
        MessageBox.Show(New Form With {.TopMost = True}, "Service stopped.")
        SetStatusLight(LWStatus.Idle)
    End Sub

    Private Sub APToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles APToolStripMenuItem.Click
        My.Settings.LTFSWriter_AutoFlush = APToolStripMenuItem.Checked
    End Sub

    Public Function DirectoryExists(path As String) As Boolean
        If Not path.EndsWith("\") AndAlso Not path.EndsWith("/") Then path &= "\"
        Dim DIR() As String = path.Split({"\", "/"}, StringSplitOptions.None)
        ReDim Preserve DIR(DIR.Length - 2)
        Dim targetdir As TreeNode = TreeView1.TopNode
        If DIR.Length > 1 AndAlso DIR(1) = CType(targetdir.Tag, ltfsindex.directory).name Then
            Dim found As Boolean = False
            For i As Integer = 2 To DIR.Length - 1
                For j As Integer = 0 To targetdir.Nodes.Count - 1
                    If CType(targetdir.Nodes(j).Tag, ltfsindex.directory).name = DIR(i) Then
                        targetdir = targetdir.Nodes(j)
                        If i = DIR.Length - 1 Then
                            Return True
                        End If
                        Exit For
                    End If
                Next
            Next
        End If
        Return False
    End Function

    Private Sub 计算校验ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 计算校验ToolStripMenuItem.Click
        My.Settings.LTFSWriter_HashOnWriting = 计算校验ToolStripMenuItem.Checked
    End Sub

    Private Sub 异步校验CPU占用高ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 异步校验CPU占用高ToolStripMenuItem.Click
        My.Settings.LTFSWriter_HashAsync = 异步校验CPU占用高ToolStripMenuItem.Checked
    End Sub

    Private Sub 启用日志记录ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 启用日志记录ToolStripMenuItem.Click
        My.Settings.LTFSWriter_LogEnabled = 启用日志记录ToolStripMenuItem.Checked
        AppLogging.SetInformationEnabled(My.Settings.LTFSWriter_LogEnabled)
    End Sub

    Private Sub 总是更新数据区索引ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 总是更新数据区索引ToolStripMenuItem.Click
        My.Settings.LTFSWriter_ForceIndex = 总是更新数据区索引ToolStripMenuItem.Checked
    End Sub

    Private Sub 选中二级目录ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 选中二级目录ToolStripMenuItem.Click
        AllowOperation = False
        For Each n As TreeNode In TreeView1.TopNode.Nodes
            For Each n2 As TreeNode In n.Nodes
                n2.Checked = True
            Next
        Next
        AllowOperation = True
    End Sub

    Private Sub 导出待写列表ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 导出待写列表ToolStripMenuItem.Click
        If UnwrittenCount > 0 Then
            Dim sfd1 As New SaveFileDialog With {.Filter = "txt|*.txt"}
            If sfd1.ShowDialog = DialogResult.OK Then
                Dim sb As New StringBuilder
                SyncLock _pendingQueueLock
                    If UnwrittenFiles IsNot Nothing Then
                        For Each record As FileRecord In UnwrittenFiles
                            If record IsNot Nothing Then sb.AppendLine(record.SourcePath)
                        Next
                    End If
                End SyncLock
                IO.File.WriteAllText(sfd1.FileName, sb.ToString())
                MessageBox.Show(My.Resources.ResText_Succ)
            End If
        End If
    End Sub

    Private Class MonitorFileState
        Public Property Length As Long
        Public Property LastWriteTimeUtc As DateTime
        Public Property StableSinceUtc As DateTime
        Public Property Queued As Boolean
    End Class

    Private Shared ReadOnly DefaultMonitorFilter As String = String.Join(vbCrLf, New String() {
        "**/*",
        "!**/*.!qb",
        "!**/*.bc!",
        "!**/*.!ut",
        "!**/*.part",
        "!**/*.tmp",
        "!**/*.temp",
        "!**/*.download",
        "!**/*.downloading",
        "!**/*.downloading.cfg",
        "!**/*.crdownload",
        "!**/*.filepart",
        "!**/*.opdownload",
        "!**/*.partial",
        "!**/*.aria2",
        "!**/~*",
        "!**/.~lock.*"})

    Private Shared Function NormalizeMonitorPath(path As String) As String
        If String.IsNullOrWhiteSpace(path) Then Return String.Empty

        Dim normalized As String = path.Trim()
        If normalized.StartsWith("\\?\UNC\", StringComparison.OrdinalIgnoreCase) Then
            normalized = "\\" & normalized.Substring(8)
        ElseIf normalized.StartsWith("\\?\", StringComparison.OrdinalIgnoreCase) Then
            normalized = normalized.Substring(4)
        End If

        Try
            normalized = IO.Path.GetFullPath(normalized)
            Dim root As String = IO.Path.GetPathRoot(normalized)
            If Not String.IsNullOrEmpty(root) AndAlso
                Not normalized.Equals(root, StringComparison.OrdinalIgnoreCase) Then
                normalized = normalized.TrimEnd(IO.Path.DirectorySeparatorChar, IO.Path.AltDirectorySeparatorChar)
            End If
        Catch
        End Try
        Return normalized
    End Function

    Private Shared Function TryReadMonitorFileInfo(candidate As GlobHelper.AddFile,
                                                    ByRef fileInfo As IO.FileInfo) As Boolean
        fileInfo = Nothing
        If candidate Is Nothing OrElse String.IsNullOrWhiteSpace(candidate.SourceFullPath) Then Return False
        If candidate.SourceFullPath.EndsWith(".xattr", StringComparison.OrdinalIgnoreCase) Then Return False

        Try
            If Not IO.File.Exists(candidate.SourceFullPath) Then Return False
            fileInfo = New IO.FileInfo(candidate.SourceFullPath)
            fileInfo.Refresh()
            Return fileInfo.Exists
        Catch
            fileInfo = Nothing
            Return False
        End Try
    End Function

    Private Shared Function CollectStableMonitorFiles(sourcePaths As String(),
                                                       matcher As Matcher,
                                                       states As Dictionary(Of String, MonitorFileState),
                                                       nowUtc As DateTime,
        token As Threading.CancellationToken,
                                                        ByRef matchedCount As Integer) As List(Of GlobHelper.AddFile)
        matchedCount = 0
        Dim ready As New List(Of GlobHelper.AddFile)
        Const MaxReadyFiles As Integer = 4096

        For Each candidate As GlobHelper.AddFile In GlobCollector.EnumerateAdd_ByFullPathInputs(sourcePaths, matcher)
            token.ThrowIfCancellationRequested()
            Dim info As IO.FileInfo = Nothing
            If Not TryReadMonitorFileInfo(candidate, info) Then Continue For

            Dim key As String = NormalizeMonitorPath(info.FullName)
            If String.IsNullOrEmpty(key) Then Continue For
            matchedCount += 1

            Dim state As MonitorFileState = Nothing
            Dim lastWrite As DateTime = info.LastWriteTimeUtc
            If Not states.TryGetValue(key, state) OrElse
               state.Length <> info.Length OrElse
               state.LastWriteTimeUtc <> lastWrite Then
                state = New MonitorFileState With {
                    .Length = info.Length,
                    .LastWriteTimeUtc = lastWrite,
                    .StableSinceUtc = nowUtc,
                    .Queued = False}
                states(key) = state
                Continue For
            End If

            If state.Queued Then Continue For
            If nowUtc.Subtract(state.StableSinceUtc).TotalSeconds < 10.0 Then Continue For

            If ready.Count < MaxReadyFiles Then
                ready.Add(New GlobHelper.AddFile With {
                    .SourceFullPath = info.FullName,
                    .RelativePath = candidate.RelativePath,
                    .SourceInfo = info,
                    .SourceXattrPath = candidate.SourceXattrPath})
            End If
        Next

        Return ready
    End Function

    Private Function WaitForMonitorOperation(token As Threading.CancellationToken) As Boolean
        While AllowOperation
            If token.IsCancellationRequested Then Return False
            Threading.Thread.Sleep(100)
        End While
        While Not AllowOperation
            If token.IsCancellationRequested Then Return False
            Threading.Thread.Sleep(100)
        End While
        Return True
    End Function

    Private Sub 监控写入ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 监控写入ToolStripMenuItem.Click
        Dim frm As New Form With {
            .Width = 780,
            .Height = 560,
            .Font = DisplayHelper.DisplayFont,
            .FormBorderStyle = FormBorderStyle.FixedDialog,
            .MaximizeBox = False,
            .MinimizeBox = False,
            .StartPosition = FormStartPosition.CenterParent,
            .Text = "文件监控写入"}
        frm.ClientSize = New Size(780, 560)

        Dim lblTarget As New Label With {.Parent = frm, .Left = 7, .Top = 8,
            .Width = frm.ClientSize.Width - 14, .Height = 22,
            .Text = "目标 LTFS 目录：" & If(ListView1.Tag Is Nothing, "未选择", TryCast(ListView1.Tag, ltfsindex.directory)?.name)}
        Dim lblSources As New Label With {.Parent = frm, .Left = 7, .Top = 34,
            .Width = 300, .Height = 22, .Text = "监控源（文件或目录）"}

        Dim sourceList As New ListView With {
            .Parent = frm,
            .Left = 7,
            .Top = 58,
            .Width = 630,
            .Height = 250,
            .View = View.Details,
            .FullRowSelect = True,
            .GridLines = True,
            .HideSelection = False,
            .MultiSelect = True,
            .HeaderStyle = ColumnHeaderStyle.Nonclickable}
        sourceList.Columns.Add("路径", sourceList.Width - 5)

        Dim buttonAddFolder As New Button With {.Parent = frm, .Text = "添加目录"}
        Dim buttonAddFile As New Button With {.Parent = frm, .Text = "添加文件"}
        Dim buttonEdit As New Button With {.Parent = frm, .Text = "编辑"}
        Dim buttonRemove As New Button With {.Parent = frm, .Text = "删除"}
        buttonAddFolder.SetBounds(647, 58, 118, 30)
        buttonAddFile.SetBounds(647, 94, 118, 30)
        buttonEdit.SetBounds(647, 130, 118, 30)
        buttonRemove.SetBounds(647, 166, 118, 30)

        Dim lblFilter As New Label With {.Parent = frm, .Left = 7, .Top = 316,
            .Width = 300, .Height = 22, .Text = "Glob 过滤规则（每行一条，! 表示排除）"}
        Dim txtFilter As New TextBox With {
            .Parent = frm,
            .Left = 7,
            .Top = 340,
            .Width = frm.ClientSize.Width - 14,
            .Height = 145,
            .Multiline = True,
            .AcceptsReturn = True,
            .ScrollBars = ScrollBars.Both,
            .WordWrap = False,
            .Text = DefaultMonitorFilter}

        Dim chkAutoDelete As New CheckBox With {.Parent = frm, .Left = 7, .Top = 493,
            .Width = 220, .Height = 24, .Text = "写入成功后静默删除源文件", .Checked = True}
        Dim buttonStart As New Button With {.Parent = frm, .Text = "开始"}
        buttonStart.SetBounds(CInt(frm.ClientSize.Width / 2 - 45), 523, 90, 28)

        Dim addSource As Action(Of String) =
            Sub(path As String)
                If String.IsNullOrWhiteSpace(path) Then Exit Sub
                Dim value As String = path.Trim()
                For Each item As ListViewItem In sourceList.Items
                    If String.Equals(item.Text, value, StringComparison.OrdinalIgnoreCase) Then Exit Sub
                Next
                sourceList.Items.Add(New ListViewItem(value))
            End Sub

        If IO.Directory.Exists("F:\DLTEMP\test") Then addSource("F:\DLTEMP\test")

        AddHandler buttonAddFolder.Click,
            Sub()
                Dim selectedPath As String = FileDialogHelper.SelectFolder()
                If Not String.IsNullOrWhiteSpace(selectedPath) Then addSource(selectedPath)
            End Sub
        AddHandler buttonAddFile.Click,
            Sub()
                Using dialog As New OpenFileDialog With {
                    .Multiselect = True,
                    .CheckFileExists = True,
                    .Filter = "所有文件|*.*"}
                    If dialog.ShowDialog(frm) = DialogResult.OK Then
                        For Each path As String In dialog.FileNames
                            addSource(path)
                        Next
                    End If
                End Using
            End Sub
        AddHandler buttonEdit.Click,
            Sub()
                If sourceList.SelectedItems.Count <> 1 Then
                    MessageBox.Show(frm, "请选择一个源项目。", "监控源")
                    Exit Sub
                End If
                Dim item As ListViewItem = sourceList.SelectedItems(0)
                Dim value As String = item.Text
                If DisplayHelper.ShowInputDialog("文件或目录路径", "编辑监控源", value) = DialogResult.OK AndAlso
                    Not String.IsNullOrWhiteSpace(value) Then
                    item.Text = value.Trim()
                End If
            End Sub
        AddHandler buttonRemove.Click,
            Sub()
                Dim indexes As List(Of Integer) = sourceList.SelectedIndices.Cast(Of Integer)().OrderByDescending(Function(i) i).ToList()
                For Each index As Integer In indexes
                    sourceList.Items.RemoveAt(index)
                Next
            End Sub
        AddHandler sourceList.DoubleClick,
            Sub() buttonEdit.PerformClick()
        AddHandler sourceList.KeyDown,
            Sub(keySender As Object, keyEvent As KeyEventArgs)
                If keyEvent.KeyCode = Keys.Delete Then
                    buttonRemove.PerformClick()
                    keyEvent.Handled = True
                End If
            End Sub

        Dim monitorCts As Threading.CancellationTokenSource = Nothing
        Dim closing As Boolean = False
        Dim editorControls As New List(Of Control) From {
            sourceList, buttonAddFolder, buttonAddFile, buttonEdit, buttonRemove, txtFilter, chkAutoDelete}

        Dim updateStatus As Action(Of String) =
            Sub(status As String)
                If closing OrElse frm.IsDisposed OrElse Not frm.IsHandleCreated Then Exit Sub
                Try
                    Dim update As Action =
                        Sub()
                            If Not frm.IsDisposed Then frm.Text = status
                        End Sub
                    If frm.InvokeRequired Then frm.BeginInvoke(update) Else update()
                Catch
                End Try
            End Sub

        Dim setRunningUi As Action(Of Boolean) =
            Sub(running As Boolean)
                If closing OrElse frm.IsDisposed OrElse Not frm.IsHandleCreated Then Exit Sub
                Try
                    Dim update As Action =
                        Sub()
                            If frm.IsDisposed Then Exit Sub
                            For Each control As Control In editorControls
                                control.Enabled = Not running
                            Next
                            buttonStart.Text = If(running, "停止", "开始")
                            buttonStart.Enabled = True
                        End Sub
                    If frm.InvokeRequired Then frm.BeginInvoke(update) Else update()
                Catch
                End Try
            End Sub

        AddHandler frm.FormClosing,
            Sub()
                closing = True
                If monitorCts IsNot Nothing Then monitorCts.Cancel()
            End Sub

        AddHandler buttonStart.Click,
            Sub()
                If monitorCts IsNot Nothing Then
                    monitorCts.Cancel()
                    buttonStart.Enabled = False
                    frm.Text = "正在停止监控..."
                    Exit Sub
                End If

                Dim targetDirectory As ltfsindex.directory = TryCast(ListView1.Tag, ltfsindex.directory)
                If targetDirectory Is Nothing Then
                    MessageBox.Show(frm, "请先在 LTFS 窗口中选择目标目录。", "文件监控写入")
                    Exit Sub
                End If

                Dim sourcePaths As String() = sourceList.Items.Cast(Of ListViewItem)().
                    Select(Function(item) item.Text.Trim()).
                    Where(Function(path) Not String.IsNullOrWhiteSpace(path)).
                    ToArray()
                If sourcePaths.Length = 0 Then
                    MessageBox.Show(frm, "请至少添加一个文件或目录作为监控源。", "文件监控写入")
                    Exit Sub
                End If

                Dim filterText As String = txtFilter.Text
                If String.IsNullOrWhiteSpace(filterText) Then filterText = DefaultMonitorFilter
                Dim autoDelete As Boolean = chkAutoDelete.Checked
                Dim overwrite As Boolean = 覆盖已有文件ToolStripMenuItem.Checked
                Dim currentCts As New Threading.CancellationTokenSource()
                monitorCts = currentCts
                setRunningUi(True)

                Threading.Tasks.Task.Run(
                    Sub()
                        Dim token As Threading.CancellationToken = currentCts.Token
                        Dim states As New Dictionary(Of String, MonitorFileState)(StringComparer.OrdinalIgnoreCase)
                        Try
                            Dim matcher As Matcher = GlobCollector.BuildMatcherFromString(filterText)
                            While Not token.IsCancellationRequested
                                If Not AllowOperation Then
                                    updateStatus("等待当前 LTFS 操作完成...")
                                    token.WaitHandle.WaitOne(500)
                                    Continue While
                                End If

                                Dim matchedCount As Integer = 0
                                Dim ready As List(Of GlobHelper.AddFile) = Nothing
                                Try
                                    ready = CollectStableMonitorFiles(sourcePaths, matcher, states, DateTime.UtcNow, token, matchedCount)
                                Catch ex As OperationCanceledException
                                    Exit While
                                Catch ex As Exception
                                    updateStatus($"监控扫描失败：{ex.Message}")
                                    token.WaitHandle.WaitOne(1000)
                                    Continue While
                                End Try

                                If ready.Count = 0 Then
                                    updateStatus($"监控中：匹配 {matchedCount} 个，等待大小和修改时间稳定 10 秒")
                                    token.WaitHandle.WaitOne(1000)
                                    Continue While
                                End If

                                Dim remainingCapacity As Long
                                Try
                                    RefreshCapacity()
                                    remainingCapacity = LastRemainCapValue
                                Catch ex As Exception
                                    updateStatus($"读取磁带容量失败：{ex.Message}")
                                    token.WaitHandle.WaitOne(1000)
                                    Continue While
                                End Try

                                Const ReserveBytes As Long = 4294967296L
                                Dim available As Long = remainingCapacity - ReserveBytes
                                Dim batch As New List(Of GlobHelper.AddFile)
                                If available > 0 Then
                                    For Each fileItem As GlobHelper.AddFile In ready
                                        token.ThrowIfCancellationRequested()
                                        Dim length As Long = fileItem.SourceInfo.Length
                                        If length <= available Then
                                            batch.Add(fileItem)
                                            available -= length
                                        End If
                                    Next
                                End If

                                If batch.Count = 0 Then
                                    updateStatus($"等待容量：可用 {IOManager.FormatSize(Math.Max(0, remainingCapacity))}，稳定文件 {ready.Count} 个")
                                    token.WaitHandle.WaitOne(1000)
                                    Continue While
                                End If

                                updateStatus($"正在加入队列：{batch.Count} 个稳定文件")
                                Me.Invoke(Sub() AddStableFilePlan(targetDirectory, batch, overwrite))
                                If Not WaitForMonitorOperation(token) Then Exit While

                                Dim batchKeys As New HashSet(Of String)(
                                    batch.Select(Function(fileItem) NormalizeMonitorPath(fileItem.SourceFullPath)),
                                    StringComparer.OrdinalIgnoreCase)
                                For Each key As String In batchKeys
                                    Dim state As MonitorFileState = Nothing
                                    If states.TryGetValue(key, state) Then state.Queued = True
                                Next

                                Dim deleteList As New List(Of String)
                                SyncLock _pendingQueueLock
                                    For Each fileRecord As FileRecord In UnwrittenFiles
                                        If batchKeys.Contains(NormalizeMonitorPath(fileRecord.SourcePath)) Then
                                            deleteList.Add(fileRecord.SourcePath)
                                        End If
                                    Next
                                End SyncLock

                                If UnwrittenCount > 0 Then
                                    updateStatus($"正在写入：{batch.Count} 个稳定文件")
                                    Me.Invoke(Sub() 写入数据ToolStripMenuItem_Click(sender, e))
                                    If Not WaitForMonitorOperation(token) Then Exit While
                                End If

                                If autoDelete AndAlso deleteList.Count > 0 AndAlso Not token.IsCancellationRequested Then
                                    For Each sourcePath As String In deleteList
                                        Try
                                            If IO.File.Exists(sourcePath) Then IO.File.Delete(sourcePath)
                                        Catch ex As Exception
                                            updateStatus($"删除失败：{ex.Message}")
                                        End Try
                                    Next
                                End If
                            End While
                        Catch ex As OperationCanceledException
                        Catch ex As Exception
                            updateStatus($"监控已停止（错误）：{ex.Message}")
                        Finally
                            If ReferenceEquals(monitorCts, currentCts) Then
                                monitorCts = Nothing
                                setRunningUi(False)
                                If token.IsCancellationRequested Then
                                    updateStatus("监控已停止")
                                Else
                                    updateStatus("监控已结束")
                                End If
                            End If
                            currentCts.Dispose()
                        End Try
                    End Sub,
                    currentCts.Token)
            End Sub

        DisplayHelper.ApplyDynamicFormLayout(frm)
        frm.Show(Me)
    End Sub

    Private Sub 停止等待缓存ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 停止等待缓存ToolStripMenuItem.Click
        PipePause = False
    End Sub

    Private Sub 自适应限速ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 自适应限速ToolStripMenuItem.Click
        自适应限速ToolStripMenuItem.Checked = Not 自适应限速ToolStripMenuItem.Checked
        ErrLRRefreshed.Set()
        If 自适应限速ToolStripMenuItem.Checked Then
            SpeedLimit = 160
            Task.Run(Sub()
                         While 自适应限速ToolStripMenuItem.Checked
                             ErrLRRefreshed.WaitOne()
                             Invoke(Sub()
                                        If ErrLogRateHistory < -4 Then
                                            SpeedLimit = Math.Min(160, SpeedLimit + 5)
                                        ElseIf ErrLogRateHistory < -3.9 Then
                                            SpeedLimit = Math.Min(160, SpeedLimit + 2)
                                        ElseIf ErrLogRateHistory < -3.8 Then
                                            SpeedLimit = Math.Min(160, SpeedLimit + 1)
                                        ElseIf ErrLogRateHistory >= -3.3 Then
                                            SpeedLimit -= 10
                                        ElseIf ErrLogRateHistory >= -3.4 Then
                                            SpeedLimit -= 5
                                        ElseIf ErrLogRateHistory >= -3.5 Then
                                            SpeedLimit -= 2
                                        ElseIf ErrLogRateHistory >= -3.6 Then
                                            SpeedLimit -= 1
                                        End If
                                        ToolStripStatusLabelErrLog.Text = $"{ErrLogRateHistory.ToString("f2")}|{SpeedLimit}"
                                    End Sub)
                         End While
                     End Sub)
        End If
    End Sub

    Private Sub 从文件添加ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 从文件添加ToolStripMenuItem.Click
        Dim ofd As New OpenFileDialog With {.FileName = ""}
        Dim overwrite As Boolean = 覆盖已有文件ToolStripMenuItem.Checked
        If ofd.ShowDialog = DialogResult.OK Then
            Dim s() As String = IO.File.ReadAllLines(ofd.FileName)
            Dim currdir As ltfsindex.directory = DirectCast(TreeView1.SelectedNode.Tag, ltfsindex.directory)
            LockGUI(True)
            Task.Run(Sub()
                         Dim currpath As String = ""
                         If currdir Is Nothing Then currdir = schema._directory(0)
                         For i As Integer = 0 To s.Length - 1
                             Dim t() As String = s(i).Split({vbTab}, StringSplitOptions.None)
                             Dim toadd As String = ""
                             If t.Count >= 2 Then
                                 If currpath <> t(0) Then
                                     currpath = t(0)
                                     currdir = schema.GetDirectory(currpath)
                                     toadd = t(1)
                                 End If
                             Else
                                 toadd = t(0)
                             End If
                             If IO.File.Exists(toadd) Then
                                 AddFile(New IO.FileInfo(toadd), currdir, overwrite)
                             ElseIf IO.Directory.Exists(toadd) Then
                                 AddDirectry(New IO.DirectoryInfo(toadd), currdir, overwrite)
                             End If
                         Next
                         RefreshDisplay()
                         PrintMsg(My.Resources.ResText_AddFin)
                         SetStatusLight(LWStatus.Succ)
                         LockGUI(False)
                     End Sub)

        End If
    End Sub

    Private Sub 提取为空文件ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 提取为空文件ToolStripMenuItem.Click
        Dim Nodes As List(Of TreeNode) = SelectedNodes
        If Nodes.Count = 0 Then Exit Sub
        Dim outputDirectory As String = SelectWriterFolder()
        If String.IsNullOrEmpty(outputDirectory) Then Exit Sub
        Dim totalFiles As Long = 0
        For Each node As TreeNode In Nodes
            Dim selectedDir As ltfsindex.directory = TryCast(node.Tag, ltfsindex.directory)
            If selectedDir IsNot Nothing Then totalFiles += selectedDir.TotalFiles
        Next
        Dim th As New Threading.Thread(
                Sub()
                    Dim succeeded As Boolean = False
                    PrintMsg(My.Resources.ResText_Restoring)
                    SetStatusLight(LWStatus.Busy)
                    Try
                        StopFlag = False
                        PrintMsg(My.Resources.ResText_PrepFile)
                        CurrentFilesProcessed = 0
                        CurrentBytesProcessed = 0
                        UnwrittenSizeOverrideValue = 0
                        UnwrittenCountOverrideValue = CULng(Math.Max(0, totalFiles))
                        StartTime = Now
                        PrintMsg(My.Resources.ResText_RestFile)
                        Dim c As Long = 0
                        TapeUtils.ReserveUnit(driveHandle)
                        TapeUtils.PreventMediaRemoval(driveHandle)
                        RestorePosition = New TapeUtils.PositionData(driveHandle)
                        For Each node As TreeNode In Nodes
                            If StopFlag Then Exit For
                            Dim selectedDir As ltfsindex.directory = TryCast(node.Tag, ltfsindex.directory)
                            If selectedDir Is Nothing Then Continue For
                            Dim outputPath As String = IO.Path.Combine(outputDirectory, If(selectedDir.name, String.Empty))
                            If Not outputPath.StartsWith("\\") Then outputPath = $"\\?\{outputPath}"
                            If Not IO.Directory.Exists(outputPath) Then IO.Directory.CreateDirectory(outputPath)
                            Dim output As New IO.DirectoryInfo(outputPath)
                            TraverseRestoreFiles(selectedDir, output,
                                Sub(fr As FileRecord)
                                    If StopFlag Then Return
                                    c += 1
                                    PrintMsg($"{My.Resources.ResText_Restoring} [{c}/{totalFiles}] {fr.File.name}", False, $"{My.Resources.ResText_Restoring} [{c}/{totalFiles}] {fr.SourcePath}")
                                    IOManager.CreateSparceFile(fr.SourcePath, fr.File.length)
                                End Sub)
                        Next
                        If StopFlag Then
                            PrintMsg(My.Resources.ResText_OpCancelled)
                            SetStatusLight(LWStatus.Idle)
                        Else
                            succeeded = True
                            PrintMsg(My.Resources.ResText_RestFin)
                            SetStatusLight(LWStatus.Succ)
                        End If
                    Catch ex As Exception
                        Invoke(Sub() MessageBox.Show(New Form With {.TopMost = True}, $"{ex.ToString}"))
                        PrintMsg($"{My.Resources.ResText_RestoreErr}{ex.ToString}", ForceLog:=True)
                        SetStatusLight(LWStatus.Err)
                    End Try
                    SyncLock OperationLock
                        SyncLock TapeUtils.GetSCSIOperationLock(driveHandle)
                            TapeUtils.AllowMediumRemoval(driveHandle)
                            TapeUtils.ReleaseUnit(driveHandle)
                        End SyncLock
                    End SyncLock

                    UnwrittenSizeOverrideValue = 0
                    UnwrittenCountOverrideValue = 0
                    StopFlag = False
                    LockGUI(False)
                    If Not succeeded Then SetStatusLight(LWStatus.Err)
                End Sub)
        LockGUI()
        th.Start()
    End Sub

    Private Sub 按索引删除硬盘文件ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 按索引删除硬盘文件ToolStripMenuItem.Click
        Dim Nodes As List(Of TreeNode) = SelectedNodes
        If Nodes.Count = 0 Then Exit Sub
        Dim outputDirectory As String = SelectWriterFolder()
        If String.IsNullOrEmpty(outputDirectory) Then Exit Sub
        Dim totalFiles As Long = 0
        For Each node As TreeNode In Nodes
            Dim selectedDir As ltfsindex.directory = TryCast(node.Tag, ltfsindex.directory)
            If selectedDir IsNot Nothing Then totalFiles += selectedDir.TotalFiles
        Next
        Dim th As New Threading.Thread(
                Sub()
                    Dim succeeded As Boolean = False
                    PrintMsg(My.Resources.ResText_Deleting)
                    SetStatusLight(LWStatus.Busy)
                    Try
                        StopFlag = False
                        PrintMsg(My.Resources.ResText_PrepFile)
                        CurrentFilesProcessed = 0
                        CurrentBytesProcessed = 0
                        UnwrittenSizeOverrideValue = 0
                        UnwrittenCountOverrideValue = CULng(Math.Max(0, totalFiles))
                        StartTime = Now
                        PrintMsg(My.Resources.ResText_Deleting)
                        Dim c As Long = 0
                        TapeUtils.ReserveUnit(driveHandle)
                        TapeUtils.PreventMediaRemoval(driveHandle)
                        RestorePosition = New TapeUtils.PositionData(driveHandle)
                        For Each node As TreeNode In Nodes
                            If StopFlag Then Exit For
                            Dim selectedDir As ltfsindex.directory = TryCast(node.Tag, ltfsindex.directory)
                            If selectedDir Is Nothing Then Continue For
                            Dim outputPath As String = IO.Path.Combine(outputDirectory, If(selectedDir.name, String.Empty))
                            If Not outputPath.StartsWith("\\") Then outputPath = $"\\?\{outputPath}"
                            If Not IO.Directory.Exists(outputPath) Then IO.Directory.CreateDirectory(outputPath)
                            TraverseRestoreFiles(selectedDir, New IO.DirectoryInfo(outputPath),
                                Sub(fr As FileRecord)
                                    If StopFlag Then Return
                                    c += 1
                                    PrintMsg($"{My.Resources.ResText_Deleting} [{c}/{totalFiles}] {fr.File.name}", False, $"{My.Resources.ResText_Deleting} [{c}/{totalFiles}] {fr.SourcePath}")
                                    Try
                                        If IO.File.Exists(fr.SourcePath) AndAlso
                                           (New IO.FileInfo(fr.SourcePath)).Length = fr.File.length Then
                                            IO.File.Delete(fr.SourcePath)
                                        End If
                                    Catch ex As Exception
                                        PrintMsg($"[ERROR]{fr.SourcePath}>{ex.ToString()}", LogOnly:=True, ForceLog:=True)
                                    End Try
                                End Sub)
                        Next
                        If StopFlag Then
                            PrintMsg(My.Resources.ResText_OpCancelled)
                            SetStatusLight(LWStatus.Idle)
                        Else
                            succeeded = True
                            PrintMsg(My.Resources.ResText_DeleteFin)
                            SetStatusLight(LWStatus.Succ)
                        End If
                    Catch ex As Exception
                        Invoke(Sub() MessageBox.Show(New Form With {.TopMost = True}, $"{ex.ToString}"))
                        PrintMsg($"{My.Resources.ResText_Error}{ex.ToString}", ForceLog:=True)
                        SetStatusLight(LWStatus.Err)
                    End Try
                    SyncLock OperationLock
                        SyncLock TapeUtils.GetSCSIOperationLock(driveHandle)
                            TapeUtils.AllowMediumRemoval(driveHandle)
                            TapeUtils.ReleaseUnit(driveHandle)
                        End SyncLock
                    End SyncLock

                    UnwrittenSizeOverrideValue = 0
                    UnwrittenCountOverrideValue = 0
                    StopFlag = False
                    LockGUI(False)
                    If Not succeeded Then SetStatusLight(LWStatus.Err)
                End Sub)
        LockGUI()
        th.Start()
    End Sub

    Private Sub ForceEjectButton_Click(sender As Object, e As EventArgs) Handles ForceEjectButton.Click
        If MessageBox.Show(New Form With {.TopMost = True}, My.Resources.ResText_ForceEjectConfirm, My.Resources.ResText_Confirm, MessageBoxButtons.OKCancel) = DialogResult.Cancel Then Exit Sub
        LockGUI(True)
        Task.Run(Sub()
                     SetStatusLight(LWStatus.Busy)
                     TapeUtils.ReleaseUnit(driveHandle)
                     TapeUtils.AllowMediumRemoval(driveHandle)
                     TapeUtils.LoadEject(driveHandle, TapeUtils.LoadOption.Eject)
                     PrintMsg(My.Resources.ResText_Ejd)
                     Invoke(Sub()
                                SetStatusLight(LWStatus.Succ)
                                LockGUI(False)
                                RefreshDisplay()
                                RaiseEvent TapeEjected()
                            End Sub)
                 End Sub)
    End Sub

    Private Sub 创建磁盘镜像ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 创建磁盘镜像ToolStripMenuItem.Click
        If Not AllowOperation Then Exit Sub
        Dim cfg As New HardDriveDataProvider.Config With {.DrivePath = "\\.\PhysicalDrive1", .StartLBA = 0, .SectorCount = -1}
        Dim sp As New SettingPanel With {.SelectedObject = CType(cfg, Object)}
        If sp.ShowDialog() <> DialogResult.OK Then Exit Sub
        StopFlag = False
        LockGUI(True)
        Dim d As ltfsindex.directory = DirectCast(TreeView1.SelectedNode.Tag, ltfsindex.directory)
        Task.Run(Sub()
                     Dim provider As HardDriveDataProvider = Nothing
                     Dim wBufferPtr As IntPtr = IntPtr.Zero
                     Dim hashTasks As New List(Of Task)
                     Dim writeTasks As New List(Of Task)
                     Dim LTE As AutoResetEvent = Nothing
                     Dim LWTE As AutoResetEvent = Nothing
                     Dim locateTask As Task = Nothing
                     Dim tapeUnitReserved As Boolean = False
                     Dim mediumRemovalPrevented As Boolean = False
                     Try
                         ClearWriteCompletionState()
                         SetStatusLight(LWStatus.Busy)
                         StartTime = Now
                         PipePause = False
                         PrintMsg($"Position = {GetPos.ToString()}", LogOnly:=True)
                         PrintMsg(My.Resources.ResText_PrepW)
                         TapeUtils.ReserveUnit(driveHandle)
                         tapeUnitReserved = True
                         TapeUtils.PreventMediaRemoval(driveHandle)
                         mediumRemovalPrevented = True
                         Dim locateResult As Boolean = False
                         LTE = New AutoResetEvent(False)
                         locateTask = Task.Run(Sub()
                                                   Try
                                                       locateResult = LocateToWritePosition()
                                                   Finally
                                                       Try
                                                           LTE.Set()
                                                       Catch
                                                       End Try
                                                   End Try
                                               End Sub)
                         IsWriting = True
                         My.Settings.LTFSWriter_PreLoadBytes = My.Settings.LTFSWriter_PreLoadBytes
                         RingBufferEnabled = My.Settings.LTFSWriter_RingBufferEnabled
                         provider = New HardDriveDataProvider(cfg.DrivePath, cfg.StartLBA, cfg.SectorCount, pipeBufferBytes:=My.Settings.LTFSWriter_PreLoadBytes)
                         GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency
                         provider.Start()
                         Dim lcounter As Integer = 0
                         While Not (locateTask.IsCompleted OrElse locateTask.IsCanceled OrElse locateTask.IsFaulted)
                             LTE.WaitOne(10)
                             lcounter += 1
                             If lcounter >= 100 Then
                                 lcounter = 0
                                 If provider IsNot Nothing Then PipeBufferLength = If(RingBufferEnabled, PipeGetLength(provider.RingBuffer), PipeGetLength(provider.Reader))
                             End If
                         End While
                         If locateTask.IsFaulted Then ObserveTask(locateTask)
                         If Not locateResult Then
                             Exit Sub
                         End If
                         Invoke(Sub() 更新数据区索引ToolStripMenuItem.Enabled = True)
                         If My.Settings.LTFSWriter_PowerPolicyOnWriteBegin <> Guid.Empty Then
                             Process.Start(New ProcessStartInfo With {.FileName = "powercfg",
                                  .Arguments = $"/s {My.Settings.LTFSWriter_PowerPolicyOnWriteBegin.ToString()}",
                                  .WindowStyle = ProcessWindowStyle.Hidden})
                         End If
                         wBufferPtr = Marshal.AllocHGlobal(CInt(plabel.blocksize))

                         Dim ExitForFlag As Boolean = False
                         IndexLastUpdateTime = Now
                         Dim p As New TapeUtils.PositionData(driveHandle)

                         Dim lastpos As New TapeUtils.PositionData(driveHandle)
                         TapeUtils.SetBlockSize(driveHandle, plabel.blocksize)
                         Try
                             While Not provider.SectorLenUpdated AndAlso Not StopFlag
                                 If provider.ProducerCompleted Then
                                     Throw New IO.EndOfStreamException($"Hard-drive data provider completed before device geometry was available: {cfg.DrivePath}")
                                 End If
                                 Threading.Thread.Sleep(1)
                             End While
                             If StopFlag Then Exit Sub
                             Dim newfile As New ltfsindex.file
                             d.UnwrittenFiles.Add(newfile)
                             newfile.fileuid = schema.highestfileuid + 1
                             schema.highestfileuid += 1
                             newfile.length = provider.SectorCount * provider.SectorLength
                             UnwrittenSizeOverrideValue = CULng(newfile.length)
                             newfile.name = cfg.DrivePath.Replace("\", "").Replace(".", "") & StartTime.ToString("_yyyyMMdd_HHmmss_fffffff") & ".img"
                             Dim fileextent As New ltfsindex.file.extent With
                                {.partition = CType(DataPartition, ltfsindex.PartitionLabel),
                                .startblock = CLng(p.BlockNumber),
                                .bytecount = newfile.length,
                                .byteoffset = 0,
                                .fileoffset = 0}
                             newfile.extentinfo.Add(fileextent)
                             PrintMsg($"{My.Resources.ResText_Writing} {newfile.name}  {My.Resources.ResText_Size} {IOManager.FormatSize(newfile.length)}", False,
                                     $"{My.Resources.ResText_Writing}: {cfg.DrivePath}{vbCrLf}{My.Resources.ResText_Size}: {IOManager.FormatSize(newfile.length)}{vbCrLf _
                                     }{My.Resources.ResText_WrittenTotal}: {IOManager.FormatSize(TotalBytesProcessed) _
                                     } {My.Resources.ResText_Remaining}: {IOManager.FormatSize(CLng(Math.Max(0, UnwrittenSize - CurrentBytesProcessed))) _
                                     } -> {IOManager.FormatSize(CLng(Math.Max(0, UnwrittenSize - CurrentBytesProcessed - newfile.length)))}")
                             'write to tape
                             Dim LastWriteTask As Task = Nothing
                             Dim sh As IOManager.CheckSumBlockwiseCalculator = Nothing
                             If HashOnWrite Then sh = New IOManager.CheckSumBlockwiseCalculator
                             Dim ExitWhileFlag As Boolean = False
                             Dim RingBufferReader As SpscRingBuffer = Nothing
                             Dim PipeReader As System.IO.Pipelines.PipeReader = Nothing
                             If RingBufferEnabled Then
                                 RingBufferReader = provider.RingBuffer
                             Else
                                 PipeReader = provider.Reader
                             End If
                             Dim remainingInFile As Long = newfile.length
                             LWTE = New AutoResetEvent(False)
                             While Not StopFlag AndAlso remainingInFile > 0
                                 Dim toRead As Integer = CInt(Math.Min(plabel.blocksize, remainingInFile))
                                 Dim buffer As Byte() = IOManager.PublicArrayPool.Rent(plabel.blocksize)
                                 Dim BytesReaded As Integer = 0

                                 ' 从 Pipe 读取当前文件需要的字节
                                 Try
                                     If RingBufferEnabled Then
                                         BytesReaded = PipeReadExactly(RingBufferReader, buffer, toRead)
                                     Else
                                         Dim pCounter1 As Integer = 0
                                         Dim pCounter2 As Integer = 0
                                         Dim lastlen As Long = PipeBufferLength
                                         While PipePause AndAlso Not StopFlag
                                             Threading.Thread.Sleep(10)
                                             Threading.Interlocked.Increment(pCounter1)
                                             pCounter1 += 1
                                             pCounter2 += 1
                                             If pCounter1 >= 100 Then
                                                 pCounter1 = 0
                                                 If RingBufferEnabled Then
                                                     PipeBufferLength = PipeGetLength(provider.RingBuffer)
                                                 Else
                                                     PipeBufferLength = PipeGetLength(provider.Reader)
                                                 End If
                                                 If pCounter2 >= 1000 Then
                                                     If PipeBufferLength = lastlen OrElse PipeBufferLength >= My.Settings.LTFSWriter_PreLoadBytes * 0.75 Then
                                                         PipePause = False
                                                     End If
                                                     lastlen = PipeBufferLength
                                                     pCounter2 = 0
                                                 Else
                                                     If PipeBufferLength >= My.Settings.LTFSWriter_PreLoadBytes * 0.75 Then
                                                         PipePause = False
                                                     End If
                                                 End If
                                             End If
                                         End While
                                         If StopFlag Then
                                             IOManager.PublicArrayPool.Return(buffer)
                                             ExitWhileFlag = True
                                             Exit While
                                         End If
                                         BytesReaded = PipeReadExactly(PipeReader, buffer, toRead)

                                     End If
                                     If BytesReaded = 0 Then
                                         IOManager.PublicArrayPool.Return(buffer)
                                         ExitWhileFlag = True
                                         Exit While
                                     End If
                                 Catch ex As Exception
                                     Try
                                         IOManager.PublicArrayPool.Return(buffer)
                                     Catch
                                     End Try
                                     Dim dResult As DialogResult
                                     Invoke(Sub() dResult = MessageBox.Show(New Form With {.TopMost = True}, $"{My.Resources.ResText_WErr }{vbCrLf}{ex.ToString}", My.Resources.ResText_Warning, MessageBoxButtons.AbortRetryIgnore))
                                     Select Case dResult
                                         Case DialogResult.Abort
                                             StopFlag = True
                                             Throw
                                         Case DialogResult.Retry
                                             Continue While
                                         Case DialogResult.Ignore
                                             StopFlag = True
                                             Throw
                                     End Select
                                 End Try

                                 If LastWriteTask IsNot Nothing Then
                                     Dim lwcounter As Integer = 0
                                     While Not (LastWriteTask.IsCompleted OrElse LastWriteTask.IsCanceled OrElse LastWriteTask.IsFaulted)
                                         LWTE.WaitOne(10)
                                         lwcounter += 1
                                         If lwcounter >= 100 Then
                                             lwcounter = 0
                                             If RingBufferEnabled Then
                                                 PipeBufferLength = PipeGetLength(RingBufferReader)
                                             Else
                                                 PipeBufferLength = PipeGetLength(PipeReader)
                                             End If
                                         End If
                                     End While
                                     Try
                                         ObserveTask(LastWriteTask)
                                     Catch
                                         IOManager.PublicArrayPool.Return(buffer)
                                         Throw
                                     End Try
                                     writeTasks.Remove(LastWriteTask)
                                 End If
                                 If ExitWhileFlag Then Exit While

                                 LastWriteTask = Task.Factory.StartNew(
                                  Sub(state)
                                      Dim writeState = DirectCast(state, Tuple(Of Byte(), Integer))
                                      Dim buf As Byte() = writeState.Item1
                                      Dim bytesToWrite As Integer = writeState.Item2
                                      Dim bufferReturned As Boolean = False
                                      Try
                                          If bytesToWrite > 0 Then
                                              ' 限速（保留原逻辑）
                                              CheckCount += 1
                                              If CheckCount >= CheckCycle Then CheckCount = 0
                                              If SpeedLimit > 0 AndAlso CheckCount = 0 Then
                                                  Dim ts As Double = (Now - SpeedLimitLastTriggerTime).TotalSeconds
                                                  While SpeedLimit > 0 AndAlso ts > 0 AndAlso ((plabel.blocksize * CheckCycle / 1048576) / ts) > SpeedLimit
                                                      Threading.Thread.Sleep(0)
                                                      ts = (Now - SpeedLimitLastTriggerTime).TotalSeconds
                                                  End While
                                                  SpeedLimitLastTriggerTime = Now
                                              End If

                                              ' 写带（保留原 TapeUtils.Write + sense 处理）
                                              Marshal.Copy(buf, 0, wBufferPtr, bytesToWrite)
                                              Dim succ As Boolean = False
                                              While Not succ
                                                  Dim sense As Byte()
                                                  Try
                                                      sense = TapeUtils.Write(driveHandle, wBufferPtr, CUInt(bytesToWrite), True)
                                                      SyncLock p
                                                          p.BlockNumber = CULng(p.BlockNumber + 1)
                                                      End SyncLock
                                                  Catch ex As Exception
                                                      Dim dResult As DialogResult
                                                      Invoke(Sub() dResult = MessageBox.Show(New Form With {.TopMost = True}, $"{My.Resources.ResText_WErrSCSI}{vbCrLf}{ex.ToString}", My.Resources.ResText_Warning, MessageBoxButtons.AbortRetryIgnore))
                                                      Select Case dResult
                                                          Case DialogResult.Abort
                                                              StopFlag = True
                                                              Throw
                                                          Case DialogResult.Retry
                                                              succ = False
                                                          Case DialogResult.Ignore
                                                              succ = True
                                                              Exit While
                                                      End Select
                                                      p = New TapeUtils.PositionData(driveHandle)
                                                      Continue While
                                                  End Try
                                                  If (((sense(2) >> 6) And &H1) = 1) Then
                                                      If ((sense(2) And &HF) = 13) AndAlso (Not My.Settings.LTFSWriter_IgnoreVolumeOverflow) Then
                                                          PrintMsg(My.Resources.ResText_VOF)
                                                          Invoke(Sub() MessageBox.Show(New Form With {.TopMost = True}, My.Resources.ResText_VOF))
                                                          StopFlag = True
                                                          Try
                                                              PipeBufferLength = 0
                                                              provider.Cancel()
                                                              provider.CompleteAsync().GetAwaiter().GetResult()
                                                          Catch
                                                              PrintMsg("pipe complete failed", LogOnly:=True)
                                                          End Try
                                                          Exit Sub
                                                      Else
                                                          PrintMsg(If(((sense(2) And &HF) = 13), My.Resources.ResText_VOF, My.Resources.ResText_EWEOM), True, DeDupe:=True)
                                                          succ = True
                                                          Exit While
                                                      End If
                                                  ElseIf (sense(2) And &HF) <> 0 Then
                                                      Try
                                                          Throw New Exception("SCSI sense error")
                                                      Catch ex As Exception
                                                          Dim dResult As DialogResult
                                                          Invoke(Sub() dResult = MessageBox.Show(New Form With {.TopMost = True}, $"{My.Resources.ResText_WErr}{vbCrLf}{TapeUtils.ParseSenseData(sense)}{vbCrLf}{vbCrLf}sense{vbCrLf}{TapeUtils.Byte2Hex(sense, True)}{vbCrLf}{ex.StackTrace}", My.Resources.ResText_Warning, MessageBoxButtons.AbortRetryIgnore))
                                                          Select Case dResult
                                                              Case DialogResult.Abort
                                                                  StopFlag = True
                                                                  Throw New Exception(TapeUtils.ParseSenseData(sense))
                                                              Case DialogResult.Retry
                                                                  succ = False
                                                              Case DialogResult.Ignore
                                                                  succ = True
                                                                  Exit While
                                                          End Select
                                                      End Try
                                                      p = New TapeUtils.PositionData(driveHandle)
                                                  Else
                                                      succ = True
                                                      Exit While
                                                  End If
                                              End While
                                              If sh IsNot Nothing AndAlso succ Then
                                                  If 异步校验CPU占用高ToolStripMenuItem.Checked Then
                                                      sh.PropagateAsync(buf, bytesToWrite, Sub(qb As Byte())
                                                                                               IOManager.PublicArrayPool.Return(qb)
                                                                                           End Sub)
                                                  Else
                                                      sh.Propagate(buf, bytesToWrite, Sub(qb As Byte())
                                                                                          IOManager.PublicArrayPool.Return(qb)
                                                                                      End Sub)
                                                  End If
                                                  bufferReturned = True
                                              Else
                                                  IOManager.PublicArrayPool.Return(buf)
                                                  bufferReturned = True
                                              End If
                                              If Flush Then
                                                  Dim flushResult As Boolean = False
                                                  Dim tFE As New AutoResetEvent(False)
                                                  Dim tFlush As Task = Task.Run(Sub()
                                                                                    flushResult = CheckFlush()
                                                                                    tFE.Set()
                                                                                End Sub)
                                                  Dim counter As Integer = 0
                                                  While Not (tFlush.IsCompleted OrElse tFlush.IsCanceled OrElse tFlush.IsFaulted)
                                                      tFE.WaitOne(10)
                                                      counter += 1
                                                      If counter >= 100 Then
                                                          counter = 0
                                                          If RingBufferEnabled Then
                                                              PipeBufferLength = PipeGetLength(RingBufferReader)
                                                          Else
                                                              PipeBufferLength = PipeGetLength(PipeReader)
                                                          End If
                                                      End If
                                                  End While
                                                  Try
                                                      ObserveTask(tFlush)
                                                  Finally
                                                      tFE.Dispose()
                                                  End Try
                                                  If flushResult Then
                                                      If My.Settings.LTFSWriter_PowerPolicyOnWriteBegin <> Guid.Empty Then
                                                          Process.Start(New ProcessStartInfo With {.FileName = "powercfg",
                                                                .Arguments = $"/s {My.Settings.LTFSWriter_PowerPolicyOnWriteBegin.ToString()}",
                                                                .WindowStyle = ProcessWindowStyle.Hidden})
                                                      End If
                                                  End If
                                              End If
                                              If Clean Then
                                                  Dim tCE As New AutoResetEvent(False)
                                                  Dim tClean As Task = Task.Run(Sub()
                                                                                    CheckClean(True)
                                                                                    tCE.Set()
                                                                                End Sub)
                                                  Dim counter As Integer = 0
                                                  While Not (tClean.IsCompleted OrElse tClean.IsCanceled OrElse tClean.IsFaulted)
                                                      tCE.WaitOne(10)
                                                      counter += 1
                                                      If counter >= 100 Then
                                                          counter = 0
                                                          If RingBufferEnabled Then
                                                              PipeBufferLength = PipeGetLength(RingBufferReader)
                                                          Else
                                                              PipeBufferLength = PipeGetLength(PipeReader)
                                                          End If
                                                      End If
                                                  End While
                                                  Try
                                                      ObserveTask(tClean)
                                                  Finally
                                                      tCE.Dispose()
                                                  End Try
                                              End If
                                              newfile.WrittenBytes += bytesToWrite
                                              TotalBytesProcessed += bytesToWrite
                                              CurrentBytesProcessed += bytesToWrite
                                              TotalBytesUnindexed += bytesToWrite
                                          Else
                                              ExitWhileFlag = True
                                          End If
                                      Finally
                                          If Not bufferReturned Then
                                              IOManager.PublicArrayPool.Return(buf)
                                          End If
                                          Try
                                              LWTE.Set()
                                          Catch
                                          End Try
                                      End Try
                                  End Sub, Tuple.Create(buffer, BytesReaded))
                                 writeTasks.Add(LastWriteTask)
                                 remainingInFile -= BytesReaded
                             End While

                             'If i < WriteList.Count - 1 Then WriteList(i + 1).BeginOpen()
                             If LastWriteTask IsNot Nothing Then
                                 Dim lwcounter As Integer = 0
                                 While Not (LastWriteTask.IsCompleted OrElse LastWriteTask.IsCanceled OrElse LastWriteTask.IsFaulted)
                                     LWTE.WaitOne(10)
                                     lwcounter += 1
                                     If lwcounter >= 100 Then
                                         lwcounter = 0
                                         If RingBufferEnabled Then
                                             PipeBufferLength = PipeGetLength(RingBufferReader)
                                         Else
                                             PipeBufferLength = PipeGetLength(PipeReader)
                                         End If
                                     End If
                                 End While
                                 ObserveTask(LastWriteTask)
                                 writeTasks.Remove(LastWriteTask)
                             End If
                             If HashOnWrite AndAlso sh IsNot Nothing AndAlso Not StopFlag Then
                                 Dim hashTask As Task = Task.Run(Sub()
                                                                     Try
                                                                         sh.ProcessFinalBlock()
                                                                         If My.Settings.LTFSWriter_ChecksumEnabled_SHA1 Then newfile.SetXattr(ltfsindex.file.xattr.HashType.SHA1, sh.SHA1Value)
                                                                         If My.Settings.LTFSWriter_ChecksumEnabled_SHA256 Then newfile.SetXattr(ltfsindex.file.xattr.HashType.SHA256, sh.SHA256Value)
                                                                         If My.Settings.LTFSWriter_ChecksumEnabled_SHA512 Then newfile.SetXattr(ltfsindex.file.xattr.HashType.SHA512, sh.SHA512Value)
                                                                         If My.Settings.LTFSWriter_ChecksumEnabled_CRC32 Then newfile.SetXattr(ltfsindex.file.xattr.HashType.CRC32, sh.CRC32Value)
                                                                         If My.Settings.LTFSWriter_ChecksumEnabled_MD5 Then newfile.SetXattr(ltfsindex.file.xattr.HashType.MD5, sh.MD5Value)
                                                                         If sh.BlakeValue IsNot Nothing AndAlso My.Settings.LTFSWriter_ChecksumEnabled_BLAKE3 Then
                                                                             newfile.SetXattr(ltfsindex.file.xattr.HashType.BLAKE3, sh.BlakeValue)
                                                                         End If
                                                                         If sh.XXHash3Value IsNot Nothing AndAlso My.Settings.LTFSWriter_ChecksumEnabled_XxHash3 Then
                                                                             newfile.SetXattr(ltfsindex.file.xattr.HashType.XxHash3, sh.XXHash3Value)
                                                                         End If
                                                                         If sh.XXHash128Value IsNot Nothing AndAlso My.Settings.LTFSWriter_ChecksumEnabled_XxHash128 Then
                                                                             newfile.SetXattr(ltfsindex.file.xattr.HashType.XxHash128, sh.XXHash128Value)
                                                                         End If
                                                                     Finally
                                                                         sh.StopFlag = True
                                                                     End Try
                                                                 End Sub)
                                 hashTasks.Add(hashTask)
                                 WaitForHashTasks(hashTasks)
                                 SetStatusLight(LWStatus.Busy)
                             ElseIf sh IsNot Nothing Then
                                 sh.StopFlag = True
                             End If
                             TotalFilesProcessed += 1
                             CurrentFilesProcessed += 1
                             p = New TapeUtils.PositionData(driveHandle)
                             lastpos = New TapeUtils.PositionData(driveHandle)
                             CurrentHeight = CLng(p.BlockNumber)
                             If p.EOP Then PrintMsg(My.Resources.ResText_EWEOM, True, DeDupe:=True)
                             PrintMsg($"Position = {p.ToString()}", LogOnly:=True)


                             'mark as written
                             MarkWriteFileCompleted(newfile)
                             RequestListDisplayRefresh()
                             d.AddFile(newfile)
                             d.UnwrittenFiles.Remove(newfile)
                             If TotalBytesUnindexed = 0 Then TotalBytesUnindexed = 1
                             If CheckUnindexedDataLimit() Then
                                 p = New TapeUtils.PositionData(driveHandle)
                                 lastpos = New TapeUtils.PositionData(driveHandle)
                                 CurrentHeight = CLng(p.BlockNumber)
                                 SetStatusLight(LWStatus.Busy)
                             End If
                             If CapacityRefreshInterval > 0 AndAlso (Now - LastRefresh).TotalSeconds > CapacityRefreshInterval Then
                                 p = New TapeUtils.PositionData(driveHandle)
                                 Dim capValue As Long() = RefreshCapacity()
                                 newfile.SetXattr(ltfsindex.file.xattr.ApplicationSpecific.CapacityRemain, CStr(capValue(p.PartitionNumber * 2)))
                                 Dim p2 As New TapeUtils.PositionData(driveHandle)
                                 If p2.BlockNumber <> p.BlockNumber OrElse p2.PartitionNumber <> p.PartitionNumber Then
                                     Invoke(Sub()
                                                While True
                                                    Select Case MessageBox.Show(New Form With {.TopMost = True}, $"Position changed! {p.BlockNumber} -> {p2.BlockNumber}", "Warning", MessageBoxButtons.AbortRetryIgnore)
                                                        Case DialogResult.Abort
                                                            StopFlag = True
                                                        Case DialogResult.Retry
                                                            TapeUtils.Locate(driveHandle, p.BlockNumber, p.PartitionNumber)
                                                            p2 = New TapeUtils.PositionData(driveHandle)
                                                            If p2.BlockNumber = p.BlockNumber AndAlso p2.PartitionNumber = p.PartitionNumber Then Exit While
                                                        Case DialogResult.Ignore
                                                            Exit While
                                                    End Select
                                                End While

                                            End Sub)
                                 End If
                             End If
                         Catch ex As Exception
                             Invoke(Sub() MessageBox.Show(New Form With {.TopMost = True}, $"{My.Resources.ResText_WErr}{vbCrLf}{ex.ToString}"))
                             PrintMsg($"{My.Resources.ResText_WErr}{ex.Message}{vbCrLf}{ex.StackTrace}")
                             SetStatusLight(LWStatus.Err)
                             StopFlag = True
                         End Try
                         Me.Invoke(Sub() Timer1_Tick(sender, e))
                         Dim TotalBytesWritten As Long = CLng(UnwrittenSizeOverrideValue)
                         While True
                             Threading.Thread.Sleep(0)
                             SyncLock UFReadCount
                                 If UFReadCount > 0 Then Continue While
                                 UnwrittenSizeOverrideValue = 0
                                 UnwrittenCountOverrideValue = 0
                                 If Not My.Settings.LTFSWriter_KeepUnwrittenFilesOnAbort Then
                                     ClearPendingWriteQueues(d)
                                 Else
                                     UnwrittenSizeOverrideValue = UnwrittenSize
                                     UnwrittenCountOverrideValue = UnwrittenCount
                                 End If
                                 CurrentFilesProcessed = 0
                                 CurrentBytesProcessed = 0
                                 Exit While
                             End SyncLock
                         End While
                         Modified = True
                         Dim OnWriteFinishMessage As String = ""
                         If Not StopFlag Then
                             Dim TimeCost As TimeSpan = Now - StartTime
                             OnWriteFinishMessage = ($"{My.Resources.ResText_WFTime}{(Math.Floor(TimeCost.TotalHours)).ToString().PadLeft(2, "0"c)}:{TimeCost.Minutes.ToString().PadLeft(2, "0"c)}:{TimeCost.Seconds.ToString().PadLeft(2, "0"c)} {My.Resources.ResText_AvgS}{IOManager.FormatSize(TotalBytesWritten \ CLng(Math.Max(1, TimeCost.TotalSeconds)))}/s")
                             OnWriteFinished()
                         Else
                             OnWriteFinishMessage = (My.Resources.ResText_WCnd)
                         End If
                         RefreshDisplay()
                         ClearWriteCompletionState()
                         RefreshCapacity()
                         Invoke(Sub()
                                    If Not StopFlag AndAlso WA0ToolStripMenuItem.Checked AndAlso MessageBox.Show(New Form With {.TopMost = True}, My.Resources.ResText_WFUp, My.Resources.ResText_OpSucc, MessageBoxButtons.OKCancel) = DialogResult.OK Then
                                        更新数据区索引ToolStripMenuItem_Click(sender, e)
                                    End If
                                    PrintMsg(OnWriteFinishMessage)
                                    SetStatusLight(If(StopFlag, LWStatus.Err, LWStatus.Succ))
                                    RaiseEvent WriteFinished()
                                End Sub)
                     Catch ex As Exception
                         Invoke(Sub() MessageBox.Show(New Form With {.TopMost = True}, $"{My.Resources.ResText_WErr}{vbCrLf}{ex.ToString}"))
                         PrintMsg($"{My.Resources.ResText_WErr}{ex.Message}{vbCrLf}{ex.StackTrace}")
                         SetStatusLight(LWStatus.Err)
                         StopFlag = True
                     Finally
                         If locateTask IsNot Nothing Then
                             Try
                                 ObserveTask(locateTask)
                             Catch cleanupEx As Exception
                                 Log.Error(cleanupEx, "Hard-drive writer locate task failed during cleanup.")
                                 StopFlag = True
                             Finally
                                 locateTask = Nothing
                             End Try
                         End If
                         Try
                             WaitForTasks(writeTasks)
                         Catch cleanupEx As Exception
                             Log.Error(cleanupEx, "Hard-drive writer write tasks failed during cleanup.")
                             StopFlag = True
                         End Try
                         Try
                             WaitForHashTasks(hashTasks)
                         Catch cleanupEx As Exception
                             Log.Error(cleanupEx, "Hard-drive writer hash tasks failed during cleanup.")
                             StopFlag = True
                         End Try
                         If wBufferPtr <> IntPtr.Zero Then
                             Try
                                 Marshal.FreeHGlobal(wBufferPtr)
                             Catch cleanupEx As Exception
                                 Log.Error(cleanupEx, "Hard-drive writer buffer cleanup failed.")
                             Finally
                                 wBufferPtr = IntPtr.Zero
                             End Try
                         End If
                         If LWTE IsNot Nothing Then
                             Try
                                 LWTE.Dispose()
                             Catch cleanupEx As Exception
                                 Log.Error(cleanupEx, "Hard-drive writer wait handle cleanup failed.")
                             Finally
                                 LWTE = Nothing
                             End Try
                         End If
                         Try
                             If provider IsNot Nothing Then
                                 PipeBufferLength = 0
                                 provider.Cancel()
                                 provider.CompleteAsync().GetAwaiter().GetResult()
                                 provider = Nothing
                             End If
                         Catch cleanupEx As Exception
                             Log.Error(cleanupEx, "Hard-drive reader cleanup failed.")
                         End Try
                         If LTE IsNot Nothing Then
                             Try
                                 LTE.Dispose()
                             Catch cleanupEx As Exception
                                 Log.Error(cleanupEx, "Hard-drive locate wait handle cleanup failed.")
                             Finally
                                 LTE = Nothing
                             End Try
                         End If
                         If tapeUnitReserved Then
                             Try
                                 TapeUtils.Flush(driveHandle)
                             Catch cleanupEx As Exception
                                 Log.Error(cleanupEx, "Tape flush failed during hard-drive writer cleanup.")
                             End Try
                         End If
                         If mediumRemovalPrevented Then
                             Try
                                 TapeUtils.AllowMediumRemoval(driveHandle)
                             Catch cleanupEx As Exception
                                 Log.Error(cleanupEx, "Allow-medium-removal failed during hard-drive writer cleanup.")
                             End Try
                         End If
                         If tapeUnitReserved Then
                             Try
                                 TapeUtils.ReleaseUnit(driveHandle)
                             Catch cleanupEx As Exception
                                 Log.Error(cleanupEx, "Tape unit release failed during hard-drive writer cleanup.")
                             End Try
                         End If
                         If My.Settings.LTFSWriter_PowerPolicyOnWriteEnd <> Guid.Empty Then
                             Try
                                 Process.Start(New ProcessStartInfo With {.FileName = "powercfg",
                                               .Arguments = $"/s {My.Settings.LTFSWriter_PowerPolicyOnWriteEnd.ToString()}",
                                               .WindowStyle = ProcessWindowStyle.Hidden})
                             Catch cleanupEx As Exception
                                 Log.Error(cleanupEx, "Power policy restore failed during hard-drive writer cleanup.")
                             End Try
                         End If
                         Try
                             LockGUI(False)
                         Catch cleanupEx As Exception
                             Log.Error(cleanupEx, "Hard-drive writer GUI unlock failed.")
                         End Try
                         IsWriting = False
                     End Try
                 End Sub)
    End Sub

    Private Sub 清除指定大小范围的待写文件ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 清除指定大小范围的待写文件ToolStripMenuItem.Click
        If UnwrittenCount > 0 Then
            Static range As String = "268435456-10000000000000"
            If DisplayHelper.ShowInputDialog("Range (bytes)", "Input", range) = DialogResult.OK Then
                Dim rangelim() As String = range.Split({",", "-", " ", vbTab}, StringSplitOptions.RemoveEmptyEntries)
                If rangelim.Count < 2 Then Exit Sub
                Dim dlim As Long = Long.Parse(rangelim(0))
                Dim ulim As Long = Long.Parse(rangelim(1))
                Dim recordsToRemove As New List(Of FileRecord)
                SyncLock _pendingQueueLock
                    If UnwrittenFiles IsNot Nothing Then
                        For i As Integer = UnwrittenFiles.Count - 1 To 0 Step -1
                            Dim record As FileRecord = UnwrittenFiles(i)
                            If record Is Nothing OrElse record.File Is Nothing OrElse
                               record.File.length < dlim OrElse record.File.length > ulim Then Continue For
                            recordsToRemove.Add(record)
                        Next
                    End If
                End SyncLock
                RemovePendingRecords(recordsToRemove)
                MessageBox.Show(My.Resources.ResText_Succ)
            End If
        End If
    End Sub

    Public Function FileExists(path As String) As Boolean
        If schema Is Nothing OrElse String.IsNullOrWhiteSpace(path) Then Return False
        Dim normalizedPath As String = path.Trim("\"c, "/"c)
        If normalizedPath.Length = 0 Then Return False
        Return schema.GetFile(normalizedPath) IsNot Nothing
    End Function
    Public CurrentFocus As Object
    Private Sub TreeView1_GotFocus(sender As Object, e As EventArgs) Handles TreeView1.GotFocus
        CurrentFocus = TreeView1
    End Sub

    Private Sub ListView1_GotFocus(sender As Object, e As EventArgs) Handles ListView1.GotFocus
        CurrentFocus = ListView1
    End Sub

    Private Sub 自动化ToolStripMenuItem1_DropDownOpening(sender As Object, e As EventArgs) Handles 自动化ToolStripMenuItem1.DropDownOpening
        Load_Settings()
    End Sub

    Private Sub TreeView1_SizeChanged(sender As Object, e As EventArgs) Handles TreeView1.SizeChanged
        Try
            Dim targetSize As New Size(TreeView1.ItemHeight, TreeView1.ItemHeight)
            If TreeView1.ImageList IsNot Nothing AndAlso TreeView1.ImageList.ImageSize = targetSize Then Return

            Dim il = New ImageList With {.ImageSize = targetSize}
            For Each img As Image In ImageList1.Images
                il.Images.Add(img)
            Next
            ImageList1 = il
            If TreeView1 IsNot Nothing AndAlso TreeView1.ImageList IsNot Nothing Then TreeView1.ImageList = il
        Catch ex As Exception

        End Try
    End Sub
End Class

