<Global.Microsoft.VisualBasic.CompilerServices.DesignerGenerated()>
Partial Class LTFSConfigurator
    Inherits System.Windows.Forms.Form

    'Form 重写 Dispose，以清理组件列表。
    <System.Diagnostics.DebuggerNonUserCode()>
    Protected Overrides Sub Dispose(ByVal disposing As Boolean)
        Try
            If disposing AndAlso components IsNot Nothing Then
                components.Dispose()
            End If
        Finally
            MyBase.Dispose(disposing)
        End Try
    End Sub

    'Windows 窗体设计器所必需的
    Private components As System.ComponentModel.IContainer

    '注意: 以下过程是 Windows 窗体设计器所必需的
    '可以使用 Windows 窗体设计器修改它。  
    '不要使用代码编辑器修改它。
    <System.Diagnostics.DebuggerStepThrough()>
    Private Sub InitializeComponent()
        Me.components = New System.ComponentModel.Container()
        Dim resources As System.ComponentModel.ComponentResourceManager = New System.ComponentModel.ComponentResourceManager(GetType(LTFSConfigurator))
        Me.ButtonRefresh = New System.Windows.Forms.Button()
        Me.ContextMenuStripRefreshDeviceList = New System.Windows.Forms.ContextMenuStrip(Me.components)
        Me.DiskToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.SCSIAdapterToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.ManualAddToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.AddImageToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.BrowseToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.ListBox1 = New System.Windows.Forms.ListBox()
        Me.ButtonStartFUSESvc = New System.Windows.Forms.Button()
        Me.ButtonStopFUSESvc = New System.Windows.Forms.Button()
        Me.ButtonRemount = New System.Windows.Forms.Button()
        Me.ComboBoxDriveLetter = New System.Windows.Forms.ComboBox()
        Me.LabelDrive = New System.Windows.Forms.Label()
        Me.TextBoxDevInfo = New System.Windows.Forms.TextBox()
        Me.LabelLetter = New System.Windows.Forms.Label()
        Me.ButtonAssign = New System.Windows.Forms.Button()
        Me.ButtonRemove = New System.Windows.Forms.Button()
        Me.LabelInfo = New System.Windows.Forms.Label()
        Me.ButtonLoadThreaded = New System.Windows.Forms.Button()
        Me.ButtonEject = New System.Windows.Forms.Button()
        Me.ButtonMount = New System.Windows.Forms.Button()
        Me.TextBoxMsg = New System.Windows.Forms.TextBox()
        Me.Panel1 = New System.Windows.Forms.Panel()
        Me.ButtonChangerTool = New System.Windows.Forms.Button()
        Me.CheckBoxAutoRefresh = New System.Windows.Forms.CheckBox()
        Me.ButtonFileSorter = New System.Windows.Forms.Button()
        Me.ButtonLTFSWriter = New System.Windows.Forms.Button()
        Me.ContextMenuStripLTFSWriter = New System.Windows.Forms.ContextMenuStrip(Me.components)
        Me.在当前进程运行ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.不读取索引ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.ButtonUnthread = New System.Windows.Forms.Button()
        Me.ButtonLoadUnthreaded = New System.Windows.Forms.Button()
        Me.CheckBoxDebugPanel = New System.Windows.Forms.CheckBox()
        Me.Panel2 = New System.Windows.Forms.Panel()
        Me.TextBoxLUN = New System.Windows.Forms.TextBox()
        Me.LabelLUN = New System.Windows.Forms.Label()
        Me.TextBoxTargetID = New System.Windows.Forms.TextBox()
        Me.LabelTargetID = New System.Windows.Forms.Label()
        Me.TabControl1 = New System.Windows.Forms.TabControl()
        Me.TabPageCommand = New System.Windows.Forms.TabPage()
        Me.ButtonDebugRewind = New System.Windows.Forms.Button()
        Me.LabelRead = New System.Windows.Forms.Label()
        Me.LabelPartition = New System.Windows.Forms.Label()
        Me.NumericUpDownBlockNum = New System.Windows.Forms.NumericUpDown()
        Me.CheckBoxParseCMData = New System.Windows.Forms.CheckBox()
        Me.NumericUpDownPartitionNum = New System.Windows.Forms.NumericUpDown()
        Me.ButtonDebugFormat = New System.Windows.Forms.Button()
        Me.ButtonDebugAllowMediumRemoval = New System.Windows.Forms.Button()
        Me.ButtonDebugReadInfo = New System.Windows.Forms.Button()
        Me.ContextMenuStripCMReader = New System.Windows.Forms.ContextMenuStrip(Me.components)
        Me.BrowseBinaryFileToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.ReadThroughDiagnosticCommandToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.ButtonDebugLocate = New System.Windows.Forms.Button()
        Me.LabelPartialErase = New System.Windows.Forms.Label()
        Me.ButtonDebugReleaseUnit = New System.Windows.Forms.Button()
        Me.ComboBoxLocateType = New System.Windows.Forms.ComboBox()
        Me.NumericUpDownEraseCycle = New System.Windows.Forms.NumericUpDown()
        Me.ButtonDebugReadBlock = New System.Windows.Forms.Button()
        Me.ButtonDebugReadPosition = New System.Windows.Forms.Button()
        Me.ButtonDebugDumpIndex = New System.Windows.Forms.Button()
        Me.ButtonDebugErase = New System.Windows.Forms.Button()
        Me.ContextMenuStripErase = New System.Windows.Forms.ContextMenuStrip(Me.components)
        Me.ReInitializeToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.QuickEraseToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.NumericUpDownBlockLen = New System.Windows.Forms.NumericUpDown()
        Me.CheckBoxEnableDumpLog = New System.Windows.Forms.CheckBox()
        Me.LabelReadBlockLim = New System.Windows.Forms.Label()
        Me.ButtonStopRawDump = New System.Windows.Forms.Button()
        Me.ButtonDebugDumpTape = New System.Windows.Forms.Button()
        Me.ContextMenuStripRawDump = New System.Windows.Forms.ContextMenuStrip(Me.components)
        Me.WriteToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.PlayPCMToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.WritePCMToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.TabPageBuffer = New System.Windows.Forms.TabPage()
        Me.ButtonDebugWriteBuffer = New System.Windows.Forms.Button()
        Me.NumericUpDownRBMode = New System.Windows.Forms.NumericUpDown()
        Me.ButtonDebugReadBuffer = New System.Windows.Forms.Button()
        Me.ComboBoxBufferPage = New System.Windows.Forms.ComboBox()
        Me.LabelBufferID = New System.Windows.Forms.Label()
        Me.TabPageMAM = New System.Windows.Forms.TabPage()
        Me.NumericUpDownRAPartition = New System.Windows.Forms.NumericUpDown()
        Me.Label2 = New System.Windows.Forms.Label()
        Me.LabelBarcode = New System.Windows.Forms.Label()
        Me.TextBoxBarcode = New System.Windows.Forms.TextBox()
        Me.ButtonDebugWriteBarcode = New System.Windows.Forms.Button()
        Me.LabelMAMAttrib = New System.Windows.Forms.Label()
        Me.LabelMAMPageCode = New System.Windows.Forms.Label()
        Me.NumericUpDownPCHigh = New System.Windows.Forms.NumericUpDown()
        Me.NumericUpDownPCLow = New System.Windows.Forms.NumericUpDown()
        Me.ButtonDebugReadMAM = New System.Windows.Forms.Button()
        Me.ButtonDebugDumpMAM = New System.Windows.Forms.Button()
        Me.TabPageLog = New System.Windows.Forms.TabPage()
        Me.CheckBoxShowRawLogPageData = New System.Windows.Forms.CheckBox()
        Me.ComboBox5 = New System.Windows.Forms.ComboBox()
        Me.LabelLogSensePageCtrl = New System.Windows.Forms.Label()
        Me.ButtonRunLogSense = New System.Windows.Forms.Button()
        Me.LabelLogSense = New System.Windows.Forms.Label()
        Me.ComboBox4 = New System.Windows.Forms.ComboBox()
        Me.ButtonResetLogPage = New System.Windows.Forms.Button()
        Me.TabPageTest = New System.Windows.Forms.TabPage()
        Me.GroupBoxDiag = New System.Windows.Forms.GroupBox()
        Me.LabelTestSpeed = New System.Windows.Forms.Label()
        Me.LabelTestStartLen = New System.Windows.Forms.Label()
        Me.ButtonEOT = New System.Windows.Forms.Button()
        Me.NumericUpDownTestSpeed = New System.Windows.Forms.NumericUpDown()
        Me.ButtonBOT = New System.Windows.Forms.Button()
        Me.NumericUpDownTestStartLen = New System.Windows.Forms.NumericUpDown()
        Me.NumericUpDownTestSets = New System.Windows.Forms.NumericUpDown()
        Me.LabelTestWrap = New System.Windows.Forms.Label()
        Me.LabelTestSets = New System.Windows.Forms.Label()
        Me.NumericUpDownTestWrap = New System.Windows.Forms.NumericUpDown()
        Me.ButtonDiagTest = New System.Windows.Forms.Button()
        Me.GroupBoxRWTest = New System.Windows.Forms.GroupBox()
        Me.LabelTestBlocksize = New System.Windows.Forms.Label()
        Me.ButtonVerify = New System.Windows.Forms.Button()
        Me.NumericUpDownTestBlkSize = New System.Windows.Forms.NumericUpDown()
        Me.LabelTestBlockCount = New System.Windows.Forms.Label()
        Me.NumericUpDownTestBlkNum = New System.Windows.Forms.NumericUpDown()
        Me.RadioButtonTest1 = New System.Windows.Forms.RadioButton()
        Me.RadioButtonTest2 = New System.Windows.Forms.RadioButton()
        Me.ButtonTest = New System.Windows.Forms.Button()
        Me.ButtonRDErrRateLog = New System.Windows.Forms.Button()
        Me.TabPageZBC = New System.Windows.Forms.TabPage()
        Me.ButtonZBCiSCSISvc = New System.Windows.Forms.Button()
        Me.ButtonResetAllZones = New System.Windows.Forms.Button()
        Me.ButtonRestoreLBA = New System.Windows.Forms.Button()
        Me.ButtonDumpLBA = New System.Windows.Forms.Button()
        Me.Label1 = New System.Windows.Forms.Label()
        Me.ComboBoxReportOptions = New System.Windows.Forms.ComboBox()
        Me.NumericUpDownWriteLBACount = New System.Windows.Forms.NumericUpDown()
        Me.ButtonWriteRandom = New System.Windows.Forms.Button()
        Me.ButtonReportZone = New System.Windows.Forms.Button()
        Me.NumericUpDownSectorSize = New System.Windows.Forms.NumericUpDown()
        Me.ButtonReadLBA = New System.Windows.Forms.Button()
        Me.ButtonFinishZone = New System.Windows.Forms.Button()
        Me.ButtonResetWritePointer = New System.Windows.Forms.Button()
        Me.ButtonCloseZone = New System.Windows.Forms.Button()
        Me.ButtonOpenZone = New System.Windows.Forms.Button()
        Me.LabelLBA = New System.Windows.Forms.Label()
        Me.NumericUpDownLBA = New System.Windows.Forms.NumericUpDown()
        Me.ButtonCloseAllZones = New System.Windows.Forms.Button()
        Me.ButtonReportZones = New System.Windows.Forms.Button()
        Me.LabelTimeout = New System.Windows.Forms.Label()
        Me.TextBoxTimeoutValue = New System.Windows.Forms.TextBox()
        Me.TextBoxDataDir = New System.Windows.Forms.TextBox()
        Me.LabelDataDir = New System.Windows.Forms.Label()
        Me.LabelParam = New System.Windows.Forms.Label()
        Me.LabelCDB = New System.Windows.Forms.Label()
        Me.TextBoxDebugOutput = New System.Windows.Forms.RichTextBox()
        Me.TextBoxParamData = New System.Windows.Forms.TextBox()
        Me.ButtonDebugSendSCSICommand = New System.Windows.Forms.Button()
        Me.ContextMenuStripSend = New System.Windows.Forms.ContextMenuStrip(Me.components)
        Me.ToolStripSeparator1 = New System.Windows.Forms.ToolStripSeparator()
        Me.保存ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.删除ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.DebugToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.TextBoxCDBData = New System.Windows.Forms.TextBox()
        Me.TextBoxDevicePath = New System.Windows.Forms.TextBox()
        Me.LabelSCSIIOCtl = New System.Windows.Forms.Label()
        Me.LabelDebugPanel = New System.Windows.Forms.Label()
        Me.SaveFileDialog1 = New System.Windows.Forms.SaveFileDialog()
        Me.SaveFileDialog2 = New System.Windows.Forms.SaveFileDialog()
        Me.OpenFileDialog1 = New System.Windows.Forms.OpenFileDialog()
        Me.ContextMenuStripRefreshDeviceList.SuspendLayout()
        Me.Panel1.SuspendLayout()
        Me.ContextMenuStripLTFSWriter.SuspendLayout()
        Me.Panel2.SuspendLayout()
        Me.TabControl1.SuspendLayout()
        Me.TabPageCommand.SuspendLayout()
        CType(Me.NumericUpDownBlockNum, System.ComponentModel.ISupportInitialize).BeginInit()
        CType(Me.NumericUpDownPartitionNum, System.ComponentModel.ISupportInitialize).BeginInit()
        Me.ContextMenuStripCMReader.SuspendLayout()
        CType(Me.NumericUpDownEraseCycle, System.ComponentModel.ISupportInitialize).BeginInit()
        Me.ContextMenuStripErase.SuspendLayout()
        CType(Me.NumericUpDownBlockLen, System.ComponentModel.ISupportInitialize).BeginInit()
        Me.ContextMenuStripRawDump.SuspendLayout()
        Me.TabPageBuffer.SuspendLayout()
        CType(Me.NumericUpDownRBMode, System.ComponentModel.ISupportInitialize).BeginInit()
        Me.TabPageMAM.SuspendLayout()
        CType(Me.NumericUpDownRAPartition, System.ComponentModel.ISupportInitialize).BeginInit()
        CType(Me.NumericUpDownPCHigh, System.ComponentModel.ISupportInitialize).BeginInit()
        CType(Me.NumericUpDownPCLow, System.ComponentModel.ISupportInitialize).BeginInit()
        Me.TabPageLog.SuspendLayout()
        Me.TabPageTest.SuspendLayout()
        Me.GroupBoxDiag.SuspendLayout()
        CType(Me.NumericUpDownTestSpeed, System.ComponentModel.ISupportInitialize).BeginInit()
        CType(Me.NumericUpDownTestStartLen, System.ComponentModel.ISupportInitialize).BeginInit()
        CType(Me.NumericUpDownTestSets, System.ComponentModel.ISupportInitialize).BeginInit()
        CType(Me.NumericUpDownTestWrap, System.ComponentModel.ISupportInitialize).BeginInit()
        Me.GroupBoxRWTest.SuspendLayout()
        CType(Me.NumericUpDownTestBlkSize, System.ComponentModel.ISupportInitialize).BeginInit()
        CType(Me.NumericUpDownTestBlkNum, System.ComponentModel.ISupportInitialize).BeginInit()
        Me.TabPageZBC.SuspendLayout()
        CType(Me.NumericUpDownWriteLBACount, System.ComponentModel.ISupportInitialize).BeginInit()
        CType(Me.NumericUpDownSectorSize, System.ComponentModel.ISupportInitialize).BeginInit()
        CType(Me.NumericUpDownLBA, System.ComponentModel.ISupportInitialize).BeginInit()
        Me.ContextMenuStripSend.SuspendLayout()
        Me.SuspendLayout()
        '
        'ButtonRefresh
        '
        resources.ApplyResources(Me.ButtonRefresh, "ButtonRefresh")
        Me.ButtonRefresh.ContextMenuStrip = Me.ContextMenuStripRefreshDeviceList
        Me.ButtonRefresh.Name = "ButtonRefresh"
        Me.ButtonRefresh.UseVisualStyleBackColor = True
        '
        'ContextMenuStripRefreshDeviceList
        '
        Me.ContextMenuStripRefreshDeviceList.Items.AddRange(New System.Windows.Forms.ToolStripItem() {Me.DiskToolStripMenuItem, Me.SCSIAdapterToolStripMenuItem, Me.ManualAddToolStripMenuItem, Me.AddImageToolStripMenuItem, Me.BrowseToolStripMenuItem})
        Me.ContextMenuStripRefreshDeviceList.Name = "ContextMenuStrip3"
        resources.ApplyResources(Me.ContextMenuStripRefreshDeviceList, "ContextMenuStripRefreshDeviceList")
        '
        'DiskToolStripMenuItem
        '
        Me.DiskToolStripMenuItem.Name = "DiskToolStripMenuItem"
        resources.ApplyResources(Me.DiskToolStripMenuItem, "DiskToolStripMenuItem")
        '
        'SCSIAdapterToolStripMenuItem
        '
        Me.SCSIAdapterToolStripMenuItem.Name = "SCSIAdapterToolStripMenuItem"
        resources.ApplyResources(Me.SCSIAdapterToolStripMenuItem, "SCSIAdapterToolStripMenuItem")
        '
        'ManualAddToolStripMenuItem
        '
        Me.ManualAddToolStripMenuItem.Name = "ManualAddToolStripMenuItem"
        resources.ApplyResources(Me.ManualAddToolStripMenuItem, "ManualAddToolStripMenuItem")
        '
        'AddImageToolStripMenuItem
        '
        Me.AddImageToolStripMenuItem.Name = "AddImageToolStripMenuItem"
        resources.ApplyResources(Me.AddImageToolStripMenuItem, "AddImageToolStripMenuItem")
        '
        'BrowseToolStripMenuItem
        '
        Me.BrowseToolStripMenuItem.Name = "BrowseToolStripMenuItem"
        resources.ApplyResources(Me.BrowseToolStripMenuItem, "BrowseToolStripMenuItem")
        '
        'ListBox1
        '
        resources.ApplyResources(Me.ListBox1, "ListBox1")
        Me.ListBox1.FormattingEnabled = True
        Me.ListBox1.Name = "ListBox1"
        '
        'ButtonStartFUSESvc
        '
        resources.ApplyResources(Me.ButtonStartFUSESvc, "ButtonStartFUSESvc")
        Me.ButtonStartFUSESvc.Name = "ButtonStartFUSESvc"
        Me.ButtonStartFUSESvc.UseVisualStyleBackColor = True
        '
        'ButtonStopFUSESvc
        '
        resources.ApplyResources(Me.ButtonStopFUSESvc, "ButtonStopFUSESvc")
        Me.ButtonStopFUSESvc.Name = "ButtonStopFUSESvc"
        Me.ButtonStopFUSESvc.UseVisualStyleBackColor = True
        '
        'ButtonRemount
        '
        resources.ApplyResources(Me.ButtonRemount, "ButtonRemount")
        Me.ButtonRemount.Name = "ButtonRemount"
        Me.ButtonRemount.UseVisualStyleBackColor = True
        '
        'ComboBoxDriveLetter
        '
        resources.ApplyResources(Me.ComboBoxDriveLetter, "ComboBoxDriveLetter")
        Me.ComboBoxDriveLetter.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList
        Me.ComboBoxDriveLetter.FormattingEnabled = True
        Me.ComboBoxDriveLetter.Name = "ComboBoxDriveLetter"
        '
        'LabelDrive
        '
        resources.ApplyResources(Me.LabelDrive, "LabelDrive")
        Me.LabelDrive.Name = "LabelDrive"
        '
        'TextBoxDevInfo
        '
        resources.ApplyResources(Me.TextBoxDevInfo, "TextBoxDevInfo")
        Me.TextBoxDevInfo.Name = "TextBoxDevInfo"
        Me.TextBoxDevInfo.ReadOnly = True
        '
        'LabelLetter
        '
        resources.ApplyResources(Me.LabelLetter, "LabelLetter")
        Me.LabelLetter.Name = "LabelLetter"
        '
        'ButtonAssign
        '
        resources.ApplyResources(Me.ButtonAssign, "ButtonAssign")
        Me.ButtonAssign.Name = "ButtonAssign"
        Me.ButtonAssign.UseVisualStyleBackColor = True
        '
        'ButtonRemove
        '
        resources.ApplyResources(Me.ButtonRemove, "ButtonRemove")
        Me.ButtonRemove.Name = "ButtonRemove"
        Me.ButtonRemove.UseVisualStyleBackColor = True
        '
        'LabelInfo
        '
        resources.ApplyResources(Me.LabelInfo, "LabelInfo")
        Me.LabelInfo.Name = "LabelInfo"
        '
        'ButtonLoadThreaded
        '
        resources.ApplyResources(Me.ButtonLoadThreaded, "ButtonLoadThreaded")
        Me.ButtonLoadThreaded.Name = "ButtonLoadThreaded"
        Me.ButtonLoadThreaded.UseVisualStyleBackColor = True
        '
        'ButtonEject
        '
        resources.ApplyResources(Me.ButtonEject, "ButtonEject")
        Me.ButtonEject.Name = "ButtonEject"
        Me.ButtonEject.UseVisualStyleBackColor = True
        '
        'ButtonMount
        '
        resources.ApplyResources(Me.ButtonMount, "ButtonMount")
        Me.ButtonMount.Name = "ButtonMount"
        Me.ButtonMount.UseVisualStyleBackColor = True
        '
        'TextBoxMsg
        '
        resources.ApplyResources(Me.TextBoxMsg, "TextBoxMsg")
        Me.TextBoxMsg.Name = "TextBoxMsg"
        '
        'Panel1
        '
        Me.Panel1.Controls.Add(Me.ButtonChangerTool)
        Me.Panel1.Controls.Add(Me.CheckBoxAutoRefresh)
        Me.Panel1.Controls.Add(Me.ButtonFileSorter)
        Me.Panel1.Controls.Add(Me.ButtonLTFSWriter)
        Me.Panel1.Controls.Add(Me.ButtonUnthread)
        Me.Panel1.Controls.Add(Me.ButtonLoadUnthreaded)
        Me.Panel1.Controls.Add(Me.CheckBoxDebugPanel)
        Me.Panel1.Controls.Add(Me.Panel2)
        Me.Panel1.Controls.Add(Me.ButtonStartFUSESvc)
        Me.Panel1.Controls.Add(Me.TextBoxMsg)
        Me.Panel1.Controls.Add(Me.ButtonRefresh)
        Me.Panel1.Controls.Add(Me.ButtonMount)
        Me.Panel1.Controls.Add(Me.ListBox1)
        Me.Panel1.Controls.Add(Me.ButtonEject)
        Me.Panel1.Controls.Add(Me.ButtonStopFUSESvc)
        Me.Panel1.Controls.Add(Me.ButtonLoadThreaded)
        Me.Panel1.Controls.Add(Me.ButtonRemount)
        Me.Panel1.Controls.Add(Me.LabelInfo)
        Me.Panel1.Controls.Add(Me.ButtonRemove)
        Me.Panel1.Controls.Add(Me.ComboBoxDriveLetter)
        Me.Panel1.Controls.Add(Me.ButtonAssign)
        Me.Panel1.Controls.Add(Me.LabelDrive)
        Me.Panel1.Controls.Add(Me.LabelLetter)
        Me.Panel1.Controls.Add(Me.TextBoxDevInfo)
        resources.ApplyResources(Me.Panel1, "Panel1")
        Me.Panel1.Name = "Panel1"
        '
        'ButtonChangerTool
        '
        resources.ApplyResources(Me.ButtonChangerTool, "ButtonChangerTool")
        Me.ButtonChangerTool.Name = "ButtonChangerTool"
        Me.ButtonChangerTool.UseVisualStyleBackColor = True
        '
        'CheckBoxAutoRefresh
        '
        resources.ApplyResources(Me.CheckBoxAutoRefresh, "CheckBoxAutoRefresh")
        Me.CheckBoxAutoRefresh.Checked = True
        Me.CheckBoxAutoRefresh.CheckState = System.Windows.Forms.CheckState.Checked
        Me.CheckBoxAutoRefresh.Name = "CheckBoxAutoRefresh"
        Me.CheckBoxAutoRefresh.UseVisualStyleBackColor = True
        '
        'ButtonFileSorter
        '
        resources.ApplyResources(Me.ButtonFileSorter, "ButtonFileSorter")
        Me.ButtonFileSorter.Name = "ButtonFileSorter"
        Me.ButtonFileSorter.UseVisualStyleBackColor = True
        '
        'ButtonLTFSWriter
        '
        resources.ApplyResources(Me.ButtonLTFSWriter, "ButtonLTFSWriter")
        Me.ButtonLTFSWriter.ContextMenuStrip = Me.ContextMenuStripLTFSWriter
        Me.ButtonLTFSWriter.Name = "ButtonLTFSWriter"
        Me.ButtonLTFSWriter.UseVisualStyleBackColor = True
        '
        'ContextMenuStripLTFSWriter
        '
        Me.ContextMenuStripLTFSWriter.Items.AddRange(New System.Windows.Forms.ToolStripItem() {Me.在当前进程运行ToolStripMenuItem, Me.不读取索引ToolStripMenuItem})
        Me.ContextMenuStripLTFSWriter.Name = "ContextMenuStrip1"
        resources.ApplyResources(Me.ContextMenuStripLTFSWriter, "ContextMenuStripLTFSWriter")
        '
        '在当前进程运行ToolStripMenuItem
        '
        Me.在当前进程运行ToolStripMenuItem.Name = "在当前进程运行ToolStripMenuItem"
        resources.ApplyResources(Me.在当前进程运行ToolStripMenuItem, "在当前进程运行ToolStripMenuItem")
        '
        '不读取索引ToolStripMenuItem
        '
        Me.不读取索引ToolStripMenuItem.Name = "不读取索引ToolStripMenuItem"
        resources.ApplyResources(Me.不读取索引ToolStripMenuItem, "不读取索引ToolStripMenuItem")
        '
        'ButtonUnthread
        '
        resources.ApplyResources(Me.ButtonUnthread, "ButtonUnthread")
        Me.ButtonUnthread.Name = "ButtonUnthread"
        Me.ButtonUnthread.UseVisualStyleBackColor = True
        '
        'ButtonLoadUnthreaded
        '
        resources.ApplyResources(Me.ButtonLoadUnthreaded, "ButtonLoadUnthreaded")
        Me.ButtonLoadUnthreaded.Name = "ButtonLoadUnthreaded"
        Me.ButtonLoadUnthreaded.UseVisualStyleBackColor = True
        '
        'CheckBoxDebugPanel
        '
        resources.ApplyResources(Me.CheckBoxDebugPanel, "CheckBoxDebugPanel")
        Me.CheckBoxDebugPanel.Name = "CheckBoxDebugPanel"
        Me.CheckBoxDebugPanel.UseVisualStyleBackColor = True
        '
        'Panel2
        '
        resources.ApplyResources(Me.Panel2, "Panel2")
        Me.Panel2.BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle
        Me.Panel2.Controls.Add(Me.TextBoxLUN)
        Me.Panel2.Controls.Add(Me.LabelLUN)
        Me.Panel2.Controls.Add(Me.TextBoxTargetID)
        Me.Panel2.Controls.Add(Me.LabelTargetID)
        Me.Panel2.Controls.Add(Me.TabControl1)
        Me.Panel2.Controls.Add(Me.LabelTimeout)
        Me.Panel2.Controls.Add(Me.TextBoxTimeoutValue)
        Me.Panel2.Controls.Add(Me.TextBoxDataDir)
        Me.Panel2.Controls.Add(Me.LabelDataDir)
        Me.Panel2.Controls.Add(Me.LabelParam)
        Me.Panel2.Controls.Add(Me.LabelCDB)
        Me.Panel2.Controls.Add(Me.TextBoxDebugOutput)
        Me.Panel2.Controls.Add(Me.TextBoxParamData)
        Me.Panel2.Controls.Add(Me.ButtonDebugSendSCSICommand)
        Me.Panel2.Controls.Add(Me.TextBoxCDBData)
        Me.Panel2.Controls.Add(Me.TextBoxDevicePath)
        Me.Panel2.Controls.Add(Me.LabelSCSIIOCtl)
        Me.Panel2.Controls.Add(Me.LabelDebugPanel)
        Me.Panel2.Name = "Panel2"
        '
        'TextBoxLUN
        '
        resources.ApplyResources(Me.TextBoxLUN, "TextBoxLUN")
        Me.TextBoxLUN.Name = "TextBoxLUN"
        '
        'LabelLUN
        '
        resources.ApplyResources(Me.LabelLUN, "LabelLUN")
        Me.LabelLUN.Name = "LabelLUN"
        '
        'TextBoxTargetID
        '
        resources.ApplyResources(Me.TextBoxTargetID, "TextBoxTargetID")
        Me.TextBoxTargetID.Name = "TextBoxTargetID"
        '
        'LabelTargetID
        '
        resources.ApplyResources(Me.LabelTargetID, "LabelTargetID")
        Me.LabelTargetID.Name = "LabelTargetID"
        '
        'TabControl1
        '
        resources.ApplyResources(Me.TabControl1, "TabControl1")
        Me.TabControl1.Controls.Add(Me.TabPageCommand)
        Me.TabControl1.Controls.Add(Me.TabPageBuffer)
        Me.TabControl1.Controls.Add(Me.TabPageMAM)
        Me.TabControl1.Controls.Add(Me.TabPageLog)
        Me.TabControl1.Controls.Add(Me.TabPageTest)
        Me.TabControl1.Controls.Add(Me.TabPageZBC)
        Me.TabControl1.Name = "TabControl1"
        Me.TabControl1.SelectedIndex = 0
        '
        'TabPageCommand
        '
        Me.TabPageCommand.Controls.Add(Me.ButtonDebugRewind)
        Me.TabPageCommand.Controls.Add(Me.LabelRead)
        Me.TabPageCommand.Controls.Add(Me.LabelPartition)
        Me.TabPageCommand.Controls.Add(Me.NumericUpDownBlockNum)
        Me.TabPageCommand.Controls.Add(Me.CheckBoxParseCMData)
        Me.TabPageCommand.Controls.Add(Me.NumericUpDownPartitionNum)
        Me.TabPageCommand.Controls.Add(Me.ButtonDebugFormat)
        Me.TabPageCommand.Controls.Add(Me.ButtonDebugAllowMediumRemoval)
        Me.TabPageCommand.Controls.Add(Me.ButtonDebugReadInfo)
        Me.TabPageCommand.Controls.Add(Me.ButtonDebugLocate)
        Me.TabPageCommand.Controls.Add(Me.LabelPartialErase)
        Me.TabPageCommand.Controls.Add(Me.ButtonDebugReleaseUnit)
        Me.TabPageCommand.Controls.Add(Me.ComboBoxLocateType)
        Me.TabPageCommand.Controls.Add(Me.NumericUpDownEraseCycle)
        Me.TabPageCommand.Controls.Add(Me.ButtonDebugReadBlock)
        Me.TabPageCommand.Controls.Add(Me.ButtonDebugReadPosition)
        Me.TabPageCommand.Controls.Add(Me.ButtonDebugDumpIndex)
        Me.TabPageCommand.Controls.Add(Me.ButtonDebugErase)
        Me.TabPageCommand.Controls.Add(Me.NumericUpDownBlockLen)
        Me.TabPageCommand.Controls.Add(Me.CheckBoxEnableDumpLog)
        Me.TabPageCommand.Controls.Add(Me.LabelReadBlockLim)
        Me.TabPageCommand.Controls.Add(Me.ButtonStopRawDump)
        Me.TabPageCommand.Controls.Add(Me.ButtonDebugDumpTape)
        resources.ApplyResources(Me.TabPageCommand, "TabPageCommand")
        Me.TabPageCommand.Name = "TabPageCommand"
        Me.TabPageCommand.UseVisualStyleBackColor = True
        '
        'ButtonDebugRewind
        '
        resources.ApplyResources(Me.ButtonDebugRewind, "ButtonDebugRewind")
        Me.ButtonDebugRewind.Name = "ButtonDebugRewind"
        Me.ButtonDebugRewind.UseVisualStyleBackColor = True
        '
        'LabelRead
        '
        resources.ApplyResources(Me.LabelRead, "LabelRead")
        Me.LabelRead.Name = "LabelRead"
        '
        'LabelPartition
        '
        resources.ApplyResources(Me.LabelPartition, "LabelPartition")
        Me.LabelPartition.Name = "LabelPartition"
        '
        'NumericUpDownBlockNum
        '
        resources.ApplyResources(Me.NumericUpDownBlockNum, "NumericUpDownBlockNum")
        Me.NumericUpDownBlockNum.Maximum = New Decimal(New Integer() {-1, 0, 0, 0})
        Me.NumericUpDownBlockNum.Name = "NumericUpDownBlockNum"
        '
        'CheckBoxParseCMData
        '
        resources.ApplyResources(Me.CheckBoxParseCMData, "CheckBoxParseCMData")
        Me.CheckBoxParseCMData.Name = "CheckBoxParseCMData"
        Me.CheckBoxParseCMData.UseVisualStyleBackColor = True
        '
        'NumericUpDownPartitionNum
        '
        resources.ApplyResources(Me.NumericUpDownPartitionNum, "NumericUpDownPartitionNum")
        Me.NumericUpDownPartitionNum.Maximum = New Decimal(New Integer() {7, 0, 0, 0})
        Me.NumericUpDownPartitionNum.Name = "NumericUpDownPartitionNum"
        '
        'ButtonDebugFormat
        '
        resources.ApplyResources(Me.ButtonDebugFormat, "ButtonDebugFormat")
        Me.ButtonDebugFormat.Name = "ButtonDebugFormat"
        Me.ButtonDebugFormat.UseVisualStyleBackColor = True
        '
        'ButtonDebugAllowMediumRemoval
        '
        resources.ApplyResources(Me.ButtonDebugAllowMediumRemoval, "ButtonDebugAllowMediumRemoval")
        Me.ButtonDebugAllowMediumRemoval.Name = "ButtonDebugAllowMediumRemoval"
        Me.ButtonDebugAllowMediumRemoval.UseVisualStyleBackColor = True
        '
        'ButtonDebugReadInfo
        '
        resources.ApplyResources(Me.ButtonDebugReadInfo, "ButtonDebugReadInfo")
        Me.ButtonDebugReadInfo.ContextMenuStrip = Me.ContextMenuStripCMReader
        Me.ButtonDebugReadInfo.Name = "ButtonDebugReadInfo"
        Me.ButtonDebugReadInfo.UseVisualStyleBackColor = True
        '
        'ContextMenuStripCMReader
        '
        Me.ContextMenuStripCMReader.Items.AddRange(New System.Windows.Forms.ToolStripItem() {Me.BrowseBinaryFileToolStripMenuItem, Me.ReadThroughDiagnosticCommandToolStripMenuItem})
        Me.ContextMenuStripCMReader.Name = "ContextMenuStrip2"
        resources.ApplyResources(Me.ContextMenuStripCMReader, "ContextMenuStripCMReader")
        '
        'BrowseBinaryFileToolStripMenuItem
        '
        Me.BrowseBinaryFileToolStripMenuItem.Name = "BrowseBinaryFileToolStripMenuItem"
        resources.ApplyResources(Me.BrowseBinaryFileToolStripMenuItem, "BrowseBinaryFileToolStripMenuItem")
        '
        'ReadThroughDiagnosticCommandToolStripMenuItem
        '
        Me.ReadThroughDiagnosticCommandToolStripMenuItem.Name = "ReadThroughDiagnosticCommandToolStripMenuItem"
        resources.ApplyResources(Me.ReadThroughDiagnosticCommandToolStripMenuItem, "ReadThroughDiagnosticCommandToolStripMenuItem")
        '
        'ButtonDebugLocate
        '
        resources.ApplyResources(Me.ButtonDebugLocate, "ButtonDebugLocate")
        Me.ButtonDebugLocate.Name = "ButtonDebugLocate"
        Me.ButtonDebugLocate.UseVisualStyleBackColor = True
        '
        'LabelPartialErase
        '
        resources.ApplyResources(Me.LabelPartialErase, "LabelPartialErase")
        Me.LabelPartialErase.Name = "LabelPartialErase"
        '
        'ButtonDebugReleaseUnit
        '
        resources.ApplyResources(Me.ButtonDebugReleaseUnit, "ButtonDebugReleaseUnit")
        Me.ButtonDebugReleaseUnit.Name = "ButtonDebugReleaseUnit"
        Me.ButtonDebugReleaseUnit.UseVisualStyleBackColor = True
        '
        'ComboBoxLocateType
        '
        Me.ComboBoxLocateType.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList
        Me.ComboBoxLocateType.FormattingEnabled = True
        Me.ComboBoxLocateType.Items.AddRange(New Object() {resources.GetString("ComboBoxLocateType.Items"), resources.GetString("ComboBoxLocateType.Items1"), resources.GetString("ComboBoxLocateType.Items2")})
        resources.ApplyResources(Me.ComboBoxLocateType, "ComboBoxLocateType")
        Me.ComboBoxLocateType.Name = "ComboBoxLocateType"
        '
        'NumericUpDownEraseCycle
        '
        resources.ApplyResources(Me.NumericUpDownEraseCycle, "NumericUpDownEraseCycle")
        Me.NumericUpDownEraseCycle.Maximum = New Decimal(New Integer() {10, 0, 0, 0})
        Me.NumericUpDownEraseCycle.Minimum = New Decimal(New Integer() {1, 0, 0, 0})
        Me.NumericUpDownEraseCycle.Name = "NumericUpDownEraseCycle"
        Me.NumericUpDownEraseCycle.Value = New Decimal(New Integer() {1, 0, 0, 0})
        '
        'ButtonDebugReadBlock
        '
        resources.ApplyResources(Me.ButtonDebugReadBlock, "ButtonDebugReadBlock")
        Me.ButtonDebugReadBlock.Name = "ButtonDebugReadBlock"
        Me.ButtonDebugReadBlock.UseVisualStyleBackColor = True
        '
        'ButtonDebugReadPosition
        '
        resources.ApplyResources(Me.ButtonDebugReadPosition, "ButtonDebugReadPosition")
        Me.ButtonDebugReadPosition.Name = "ButtonDebugReadPosition"
        Me.ButtonDebugReadPosition.UseVisualStyleBackColor = True
        '
        'ButtonDebugDumpIndex
        '
        resources.ApplyResources(Me.ButtonDebugDumpIndex, "ButtonDebugDumpIndex")
        Me.ButtonDebugDumpIndex.Name = "ButtonDebugDumpIndex"
        Me.ButtonDebugDumpIndex.UseVisualStyleBackColor = True
        '
        'ButtonDebugErase
        '
        Me.ButtonDebugErase.ContextMenuStrip = Me.ContextMenuStripErase
        resources.ApplyResources(Me.ButtonDebugErase, "ButtonDebugErase")
        Me.ButtonDebugErase.Name = "ButtonDebugErase"
        Me.ButtonDebugErase.UseVisualStyleBackColor = True
        '
        'ContextMenuStripErase
        '
        Me.ContextMenuStripErase.Items.AddRange(New System.Windows.Forms.ToolStripItem() {Me.ReInitializeToolStripMenuItem, Me.QuickEraseToolStripMenuItem})
        Me.ContextMenuStripErase.Name = "ContextMenuStripErase"
        resources.ApplyResources(Me.ContextMenuStripErase, "ContextMenuStripErase")
        '
        'ReInitializeToolStripMenuItem
        '
        Me.ReInitializeToolStripMenuItem.Name = "ReInitializeToolStripMenuItem"
        resources.ApplyResources(Me.ReInitializeToolStripMenuItem, "ReInitializeToolStripMenuItem")
        '
        'QuickEraseToolStripMenuItem
        '
        Me.QuickEraseToolStripMenuItem.Name = "QuickEraseToolStripMenuItem"
        resources.ApplyResources(Me.QuickEraseToolStripMenuItem, "QuickEraseToolStripMenuItem")
        '
        'NumericUpDownBlockLen
        '
        resources.ApplyResources(Me.NumericUpDownBlockLen, "NumericUpDownBlockLen")
        Me.NumericUpDownBlockLen.Maximum = New Decimal(New Integer() {1048576, 0, 0, 0})
        Me.NumericUpDownBlockLen.Minimum = New Decimal(New Integer() {1, 0, 0, 0})
        Me.NumericUpDownBlockLen.Name = "NumericUpDownBlockLen"
        Me.NumericUpDownBlockLen.Value = New Decimal(New Integer() {524288, 0, 0, 0})
        '
        'CheckBoxEnableDumpLog
        '
        resources.ApplyResources(Me.CheckBoxEnableDumpLog, "CheckBoxEnableDumpLog")
        Me.CheckBoxEnableDumpLog.Name = "CheckBoxEnableDumpLog"
        Me.CheckBoxEnableDumpLog.UseVisualStyleBackColor = True
        '
        'LabelReadBlockLim
        '
        resources.ApplyResources(Me.LabelReadBlockLim, "LabelReadBlockLim")
        Me.LabelReadBlockLim.Name = "LabelReadBlockLim"
        '
        'ButtonStopRawDump
        '
        Me.ButtonStopRawDump.ForeColor = System.Drawing.Color.Red
        resources.ApplyResources(Me.ButtonStopRawDump, "ButtonStopRawDump")
        Me.ButtonStopRawDump.Name = "ButtonStopRawDump"
        Me.ButtonStopRawDump.UseVisualStyleBackColor = True
        '
        'ButtonDebugDumpTape
        '
        Me.ButtonDebugDumpTape.AllowDrop = True
        Me.ButtonDebugDumpTape.ContextMenuStrip = Me.ContextMenuStripRawDump
        resources.ApplyResources(Me.ButtonDebugDumpTape, "ButtonDebugDumpTape")
        Me.ButtonDebugDumpTape.Name = "ButtonDebugDumpTape"
        Me.ButtonDebugDumpTape.UseVisualStyleBackColor = True
        '
        'ContextMenuStripRawDump
        '
        Me.ContextMenuStripRawDump.Items.AddRange(New System.Windows.Forms.ToolStripItem() {Me.WriteToolStripMenuItem, Me.PlayPCMToolStripMenuItem, Me.WritePCMToolStripMenuItem})
        Me.ContextMenuStripRawDump.Name = "ContextMenuStripRawDump"
        resources.ApplyResources(Me.ContextMenuStripRawDump, "ContextMenuStripRawDump")
        '
        'WriteToolStripMenuItem
        '
        Me.WriteToolStripMenuItem.Name = "WriteToolStripMenuItem"
        resources.ApplyResources(Me.WriteToolStripMenuItem, "WriteToolStripMenuItem")
        '
        'PlayPCMToolStripMenuItem
        '
        Me.PlayPCMToolStripMenuItem.Name = "PlayPCMToolStripMenuItem"
        resources.ApplyResources(Me.PlayPCMToolStripMenuItem, "PlayPCMToolStripMenuItem")
        '
        'WritePCMToolStripMenuItem
        '
        Me.WritePCMToolStripMenuItem.Name = "WritePCMToolStripMenuItem"
        resources.ApplyResources(Me.WritePCMToolStripMenuItem, "WritePCMToolStripMenuItem")
        '
        'TabPageBuffer
        '
        Me.TabPageBuffer.Controls.Add(Me.ButtonDebugWriteBuffer)
        Me.TabPageBuffer.Controls.Add(Me.NumericUpDownRBMode)
        Me.TabPageBuffer.Controls.Add(Me.ButtonDebugReadBuffer)
        Me.TabPageBuffer.Controls.Add(Me.ComboBoxBufferPage)
        Me.TabPageBuffer.Controls.Add(Me.LabelBufferID)
        resources.ApplyResources(Me.TabPageBuffer, "TabPageBuffer")
        Me.TabPageBuffer.Name = "TabPageBuffer"
        Me.TabPageBuffer.UseVisualStyleBackColor = True
        '
        'ButtonDebugWriteBuffer
        '
        resources.ApplyResources(Me.ButtonDebugWriteBuffer, "ButtonDebugWriteBuffer")
        Me.ButtonDebugWriteBuffer.Name = "ButtonDebugWriteBuffer"
        Me.ButtonDebugWriteBuffer.UseVisualStyleBackColor = True
        '
        'NumericUpDownRBMode
        '
        resources.ApplyResources(Me.NumericUpDownRBMode, "NumericUpDownRBMode")
        Me.NumericUpDownRBMode.Maximum = New Decimal(New Integer() {255, 0, 0, 0})
        Me.NumericUpDownRBMode.Name = "NumericUpDownRBMode"
        Me.NumericUpDownRBMode.Value = New Decimal(New Integer() {2, 0, 0, 0})
        '
        'ButtonDebugReadBuffer
        '
        resources.ApplyResources(Me.ButtonDebugReadBuffer, "ButtonDebugReadBuffer")
        Me.ButtonDebugReadBuffer.Name = "ButtonDebugReadBuffer"
        Me.ButtonDebugReadBuffer.UseVisualStyleBackColor = True
        '
        'ComboBoxBufferPage
        '
        resources.ApplyResources(Me.ComboBoxBufferPage, "ComboBoxBufferPage")
        Me.ComboBoxBufferPage.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList
        Me.ComboBoxBufferPage.FormattingEnabled = True
        Me.ComboBoxBufferPage.Items.AddRange(New Object() {resources.GetString("ComboBoxBufferPage.Items"), resources.GetString("ComboBoxBufferPage.Items1"), resources.GetString("ComboBoxBufferPage.Items2"), resources.GetString("ComboBoxBufferPage.Items3"), resources.GetString("ComboBoxBufferPage.Items4"), resources.GetString("ComboBoxBufferPage.Items5"), resources.GetString("ComboBoxBufferPage.Items6"), resources.GetString("ComboBoxBufferPage.Items7"), resources.GetString("ComboBoxBufferPage.Items8"), resources.GetString("ComboBoxBufferPage.Items9"), resources.GetString("ComboBoxBufferPage.Items10"), resources.GetString("ComboBoxBufferPage.Items11"), resources.GetString("ComboBoxBufferPage.Items12"), resources.GetString("ComboBoxBufferPage.Items13"), resources.GetString("ComboBoxBufferPage.Items14"), resources.GetString("ComboBoxBufferPage.Items15"), resources.GetString("ComboBoxBufferPage.Items16"), resources.GetString("ComboBoxBufferPage.Items17"), resources.GetString("ComboBoxBufferPage.Items18"), resources.GetString("ComboBoxBufferPage.Items19"), resources.GetString("ComboBoxBufferPage.Items20"), resources.GetString("ComboBoxBufferPage.Items21"), resources.GetString("ComboBoxBufferPage.Items22"), resources.GetString("ComboBoxBufferPage.Items23"), resources.GetString("ComboBoxBufferPage.Items24"), resources.GetString("ComboBoxBufferPage.Items25"), resources.GetString("ComboBoxBufferPage.Items26"), resources.GetString("ComboBoxBufferPage.Items27"), resources.GetString("ComboBoxBufferPage.Items28"), resources.GetString("ComboBoxBufferPage.Items29"), resources.GetString("ComboBoxBufferPage.Items30"), resources.GetString("ComboBoxBufferPage.Items31"), resources.GetString("ComboBoxBufferPage.Items32"), resources.GetString("ComboBoxBufferPage.Items33"), resources.GetString("ComboBoxBufferPage.Items34"), resources.GetString("ComboBoxBufferPage.Items35"), resources.GetString("ComboBoxBufferPage.Items36"), resources.GetString("ComboBoxBufferPage.Items37"), resources.GetString("ComboBoxBufferPage.Items38"), resources.GetString("ComboBoxBufferPage.Items39"), resources.GetString("ComboBoxBufferPage.Items40"), resources.GetString("ComboBoxBufferPage.Items41"), resources.GetString("ComboBoxBufferPage.Items42"), resources.GetString("ComboBoxBufferPage.Items43"), resources.GetString("ComboBoxBufferPage.Items44"), resources.GetString("ComboBoxBufferPage.Items45"), resources.GetString("ComboBoxBufferPage.Items46"), resources.GetString("ComboBoxBufferPage.Items47"), resources.GetString("ComboBoxBufferPage.Items48"), resources.GetString("ComboBoxBufferPage.Items49"), resources.GetString("ComboBoxBufferPage.Items50"), resources.GetString("ComboBoxBufferPage.Items51"), resources.GetString("ComboBoxBufferPage.Items52"), resources.GetString("ComboBoxBufferPage.Items53"), resources.GetString("ComboBoxBufferPage.Items54"), resources.GetString("ComboBoxBufferPage.Items55"), resources.GetString("ComboBoxBufferPage.Items56"), resources.GetString("ComboBoxBufferPage.Items57"), resources.GetString("ComboBoxBufferPage.Items58"), resources.GetString("ComboBoxBufferPage.Items59"), resources.GetString("ComboBoxBufferPage.Items60"), resources.GetString("ComboBoxBufferPage.Items61"), resources.GetString("ComboBoxBufferPage.Items62"), resources.GetString("ComboBoxBufferPage.Items63"), resources.GetString("ComboBoxBufferPage.Items64"), resources.GetString("ComboBoxBufferPage.Items65"), resources.GetString("ComboBoxBufferPage.Items66"), resources.GetString("ComboBoxBufferPage.Items67"), resources.GetString("ComboBoxBufferPage.Items68"), resources.GetString("ComboBoxBufferPage.Items69"), resources.GetString("ComboBoxBufferPage.Items70"), resources.GetString("ComboBoxBufferPage.Items71"), resources.GetString("ComboBoxBufferPage.Items72"), resources.GetString("ComboBoxBufferPage.Items73"), resources.GetString("ComboBoxBufferPage.Items74"), resources.GetString("ComboBoxBufferPage.Items75"), resources.GetString("ComboBoxBufferPage.Items76"), resources.GetString("ComboBoxBufferPage.Items77"), resources.GetString("ComboBoxBufferPage.Items78"), resources.GetString("ComboBoxBufferPage.Items79"), resources.GetString("ComboBoxBufferPage.Items80"), resources.GetString("ComboBoxBufferPage.Items81"), resources.GetString("ComboBoxBufferPage.Items82"), resources.GetString("ComboBoxBufferPage.Items83"), resources.GetString("ComboBoxBufferPage.Items84"), resources.GetString("ComboBoxBufferPage.Items85"), resources.GetString("ComboBoxBufferPage.Items86"), resources.GetString("ComboBoxBufferPage.Items87"), resources.GetString("ComboBoxBufferPage.Items88"), resources.GetString("ComboBoxBufferPage.Items89"), resources.GetString("ComboBoxBufferPage.Items90"), resources.GetString("ComboBoxBufferPage.Items91"), resources.GetString("ComboBoxBufferPage.Items92"), resources.GetString("ComboBoxBufferPage.Items93"), resources.GetString("ComboBoxBufferPage.Items94"), resources.GetString("ComboBoxBufferPage.Items95"), resources.GetString("ComboBoxBufferPage.Items96"), resources.GetString("ComboBoxBufferPage.Items97"), resources.GetString("ComboBoxBufferPage.Items98"), resources.GetString("ComboBoxBufferPage.Items99"), resources.GetString("ComboBoxBufferPage.Items100"), resources.GetString("ComboBoxBufferPage.Items101"), resources.GetString("ComboBoxBufferPage.Items102"), resources.GetString("ComboBoxBufferPage.Items103")})
        Me.ComboBoxBufferPage.Name = "ComboBoxBufferPage"
        '
        'LabelBufferID
        '
        resources.ApplyResources(Me.LabelBufferID, "LabelBufferID")
        Me.LabelBufferID.Name = "LabelBufferID"
        '
        'TabPageMAM
        '
        Me.TabPageMAM.Controls.Add(Me.NumericUpDownRAPartition)
        Me.TabPageMAM.Controls.Add(Me.Label2)
        Me.TabPageMAM.Controls.Add(Me.LabelBarcode)
        Me.TabPageMAM.Controls.Add(Me.TextBoxBarcode)
        Me.TabPageMAM.Controls.Add(Me.ButtonDebugWriteBarcode)
        Me.TabPageMAM.Controls.Add(Me.LabelMAMAttrib)
        Me.TabPageMAM.Controls.Add(Me.LabelMAMPageCode)
        Me.TabPageMAM.Controls.Add(Me.NumericUpDownPCHigh)
        Me.TabPageMAM.Controls.Add(Me.NumericUpDownPCLow)
        Me.TabPageMAM.Controls.Add(Me.ButtonDebugReadMAM)
        Me.TabPageMAM.Controls.Add(Me.ButtonDebugDumpMAM)
        resources.ApplyResources(Me.TabPageMAM, "TabPageMAM")
        Me.TabPageMAM.Name = "TabPageMAM"
        Me.TabPageMAM.UseVisualStyleBackColor = True
        '
        'NumericUpDownRAPartition
        '
        resources.ApplyResources(Me.NumericUpDownRAPartition, "NumericUpDownRAPartition")
        Me.NumericUpDownRAPartition.Maximum = New Decimal(New Integer() {255, 0, 0, 0})
        Me.NumericUpDownRAPartition.Name = "NumericUpDownRAPartition"
        '
        'Label2
        '
        resources.ApplyResources(Me.Label2, "Label2")
        Me.Label2.Name = "Label2"
        '
        'LabelBarcode
        '
        resources.ApplyResources(Me.LabelBarcode, "LabelBarcode")
        Me.LabelBarcode.Name = "LabelBarcode"
        '
        'TextBoxBarcode
        '
        resources.ApplyResources(Me.TextBoxBarcode, "TextBoxBarcode")
        Me.TextBoxBarcode.Name = "TextBoxBarcode"
        '
        'ButtonDebugWriteBarcode
        '
        resources.ApplyResources(Me.ButtonDebugWriteBarcode, "ButtonDebugWriteBarcode")
        Me.ButtonDebugWriteBarcode.Name = "ButtonDebugWriteBarcode"
        Me.ButtonDebugWriteBarcode.UseVisualStyleBackColor = True
        '
        'LabelMAMAttrib
        '
        resources.ApplyResources(Me.LabelMAMAttrib, "LabelMAMAttrib")
        Me.LabelMAMAttrib.Name = "LabelMAMAttrib"
        '
        'LabelMAMPageCode
        '
        resources.ApplyResources(Me.LabelMAMPageCode, "LabelMAMPageCode")
        Me.LabelMAMPageCode.Name = "LabelMAMPageCode"
        '
        'NumericUpDownPCHigh
        '
        resources.ApplyResources(Me.NumericUpDownPCHigh, "NumericUpDownPCHigh")
        Me.NumericUpDownPCHigh.Maximum = New Decimal(New Integer() {255, 0, 0, 0})
        Me.NumericUpDownPCHigh.Name = "NumericUpDownPCHigh"
        Me.NumericUpDownPCHigh.Value = New Decimal(New Integer() {8, 0, 0, 0})
        '
        'NumericUpDownPCLow
        '
        resources.ApplyResources(Me.NumericUpDownPCLow, "NumericUpDownPCLow")
        Me.NumericUpDownPCLow.Maximum = New Decimal(New Integer() {255, 0, 0, 0})
        Me.NumericUpDownPCLow.Name = "NumericUpDownPCLow"
        Me.NumericUpDownPCLow.Value = New Decimal(New Integer() {6, 0, 0, 0})
        '
        'ButtonDebugReadMAM
        '
        resources.ApplyResources(Me.ButtonDebugReadMAM, "ButtonDebugReadMAM")
        Me.ButtonDebugReadMAM.Name = "ButtonDebugReadMAM"
        Me.ButtonDebugReadMAM.UseVisualStyleBackColor = True
        '
        'ButtonDebugDumpMAM
        '
        resources.ApplyResources(Me.ButtonDebugDumpMAM, "ButtonDebugDumpMAM")
        Me.ButtonDebugDumpMAM.Name = "ButtonDebugDumpMAM"
        Me.ButtonDebugDumpMAM.UseVisualStyleBackColor = True
        '
        'TabPageLog
        '
        Me.TabPageLog.Controls.Add(Me.CheckBoxShowRawLogPageData)
        Me.TabPageLog.Controls.Add(Me.ComboBox5)
        Me.TabPageLog.Controls.Add(Me.LabelLogSensePageCtrl)
        Me.TabPageLog.Controls.Add(Me.ButtonRunLogSense)
        Me.TabPageLog.Controls.Add(Me.LabelLogSense)
        Me.TabPageLog.Controls.Add(Me.ComboBox4)
        Me.TabPageLog.Controls.Add(Me.ButtonResetLogPage)
        resources.ApplyResources(Me.TabPageLog, "TabPageLog")
        Me.TabPageLog.Name = "TabPageLog"
        Me.TabPageLog.UseVisualStyleBackColor = True
        '
        'CheckBoxShowRawLogPageData
        '
        resources.ApplyResources(Me.CheckBoxShowRawLogPageData, "CheckBoxShowRawLogPageData")
        Me.CheckBoxShowRawLogPageData.Checked = True
        Me.CheckBoxShowRawLogPageData.CheckState = System.Windows.Forms.CheckState.Checked
        Me.CheckBoxShowRawLogPageData.Name = "CheckBoxShowRawLogPageData"
        Me.CheckBoxShowRawLogPageData.UseVisualStyleBackColor = True
        '
        'ComboBox5
        '
        resources.ApplyResources(Me.ComboBox5, "ComboBox5")
        Me.ComboBox5.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList
        Me.ComboBox5.DropDownWidth = 200
        Me.ComboBox5.FormattingEnabled = True
        Me.ComboBox5.Items.AddRange(New Object() {resources.GetString("ComboBox5.Items"), resources.GetString("ComboBox5.Items1"), resources.GetString("ComboBox5.Items2"), resources.GetString("ComboBox5.Items3")})
        Me.ComboBox5.Name = "ComboBox5"
        '
        'LabelLogSensePageCtrl
        '
        resources.ApplyResources(Me.LabelLogSensePageCtrl, "LabelLogSensePageCtrl")
        Me.LabelLogSensePageCtrl.Name = "LabelLogSensePageCtrl"
        '
        'ButtonRunLogSense
        '
        resources.ApplyResources(Me.ButtonRunLogSense, "ButtonRunLogSense")
        Me.ButtonRunLogSense.Name = "ButtonRunLogSense"
        Me.ButtonRunLogSense.UseVisualStyleBackColor = True
        '
        'LabelLogSense
        '
        resources.ApplyResources(Me.LabelLogSense, "LabelLogSense")
        Me.LabelLogSense.Name = "LabelLogSense"
        '
        'ComboBox4
        '
        resources.ApplyResources(Me.ComboBox4, "ComboBox4")
        Me.ComboBox4.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList
        Me.ComboBox4.FormattingEnabled = True
        Me.ComboBox4.Name = "ComboBox4"
        '
        'ButtonResetLogPage
        '
        resources.ApplyResources(Me.ButtonResetLogPage, "ButtonResetLogPage")
        Me.ButtonResetLogPage.Name = "ButtonResetLogPage"
        Me.ButtonResetLogPage.UseVisualStyleBackColor = True
        '
        'TabPageTest
        '
        Me.TabPageTest.Controls.Add(Me.GroupBoxDiag)
        Me.TabPageTest.Controls.Add(Me.GroupBoxRWTest)
        Me.TabPageTest.Controls.Add(Me.ButtonRDErrRateLog)
        resources.ApplyResources(Me.TabPageTest, "TabPageTest")
        Me.TabPageTest.Name = "TabPageTest"
        Me.TabPageTest.UseVisualStyleBackColor = True
        '
        'GroupBoxDiag
        '
        Me.GroupBoxDiag.Controls.Add(Me.LabelTestSpeed)
        Me.GroupBoxDiag.Controls.Add(Me.LabelTestStartLen)
        Me.GroupBoxDiag.Controls.Add(Me.ButtonEOT)
        Me.GroupBoxDiag.Controls.Add(Me.NumericUpDownTestSpeed)
        Me.GroupBoxDiag.Controls.Add(Me.ButtonBOT)
        Me.GroupBoxDiag.Controls.Add(Me.NumericUpDownTestStartLen)
        Me.GroupBoxDiag.Controls.Add(Me.NumericUpDownTestSets)
        Me.GroupBoxDiag.Controls.Add(Me.LabelTestWrap)
        Me.GroupBoxDiag.Controls.Add(Me.LabelTestSets)
        Me.GroupBoxDiag.Controls.Add(Me.NumericUpDownTestWrap)
        Me.GroupBoxDiag.Controls.Add(Me.ButtonDiagTest)
        resources.ApplyResources(Me.GroupBoxDiag, "GroupBoxDiag")
        Me.GroupBoxDiag.Name = "GroupBoxDiag"
        Me.GroupBoxDiag.TabStop = False
        '
        'LabelTestSpeed
        '
        resources.ApplyResources(Me.LabelTestSpeed, "LabelTestSpeed")
        Me.LabelTestSpeed.Name = "LabelTestSpeed"
        '
        'LabelTestStartLen
        '
        resources.ApplyResources(Me.LabelTestStartLen, "LabelTestStartLen")
        Me.LabelTestStartLen.Name = "LabelTestStartLen"
        '
        'ButtonEOT
        '
        resources.ApplyResources(Me.ButtonEOT, "ButtonEOT")
        Me.ButtonEOT.Name = "ButtonEOT"
        Me.ButtonEOT.UseVisualStyleBackColor = True
        '
        'NumericUpDownTestSpeed
        '
        resources.ApplyResources(Me.NumericUpDownTestSpeed, "NumericUpDownTestSpeed")
        Me.NumericUpDownTestSpeed.Maximum = New Decimal(New Integer() {-1, 0, 0, 0})
        Me.NumericUpDownTestSpeed.Name = "NumericUpDownTestSpeed"
        Me.NumericUpDownTestSpeed.Value = New Decimal(New Integer() {7120, 0, 0, 0})
        '
        'ButtonBOT
        '
        resources.ApplyResources(Me.ButtonBOT, "ButtonBOT")
        Me.ButtonBOT.Name = "ButtonBOT"
        Me.ButtonBOT.UseVisualStyleBackColor = True
        '
        'NumericUpDownTestStartLen
        '
        resources.ApplyResources(Me.NumericUpDownTestStartLen, "NumericUpDownTestStartLen")
        Me.NumericUpDownTestStartLen.Maximum = New Decimal(New Integer() {-1, 0, 0, 0})
        Me.NumericUpDownTestStartLen.Name = "NumericUpDownTestStartLen"
        Me.NumericUpDownTestStartLen.Value = New Decimal(New Integer() {832584, 0, 0, 0})
        '
        'NumericUpDownTestSets
        '
        resources.ApplyResources(Me.NumericUpDownTestSets, "NumericUpDownTestSets")
        Me.NumericUpDownTestSets.Maximum = New Decimal(New Integer() {-1, 0, 0, 0})
        Me.NumericUpDownTestSets.Name = "NumericUpDownTestSets"
        Me.NumericUpDownTestSets.Value = New Decimal(New Integer() {250, 0, 0, 0})
        '
        'LabelTestWrap
        '
        resources.ApplyResources(Me.LabelTestWrap, "LabelTestWrap")
        Me.LabelTestWrap.Name = "LabelTestWrap"
        '
        'LabelTestSets
        '
        resources.ApplyResources(Me.LabelTestSets, "LabelTestSets")
        Me.LabelTestSets.Name = "LabelTestSets"
        '
        'NumericUpDownTestWrap
        '
        resources.ApplyResources(Me.NumericUpDownTestWrap, "NumericUpDownTestWrap")
        Me.NumericUpDownTestWrap.Maximum = New Decimal(New Integer() {-1, 0, 0, 0})
        Me.NumericUpDownTestWrap.Name = "NumericUpDownTestWrap"
        Me.NumericUpDownTestWrap.Value = New Decimal(New Integer() {1, 0, 0, 0})
        '
        'ButtonDiagTest
        '
        resources.ApplyResources(Me.ButtonDiagTest, "ButtonDiagTest")
        Me.ButtonDiagTest.Name = "ButtonDiagTest"
        Me.ButtonDiagTest.UseVisualStyleBackColor = True
        '
        'GroupBoxRWTest
        '
        Me.GroupBoxRWTest.Controls.Add(Me.LabelTestBlocksize)
        Me.GroupBoxRWTest.Controls.Add(Me.ButtonVerify)
        Me.GroupBoxRWTest.Controls.Add(Me.NumericUpDownTestBlkSize)
        Me.GroupBoxRWTest.Controls.Add(Me.LabelTestBlockCount)
        Me.GroupBoxRWTest.Controls.Add(Me.NumericUpDownTestBlkNum)
        Me.GroupBoxRWTest.Controls.Add(Me.RadioButtonTest1)
        Me.GroupBoxRWTest.Controls.Add(Me.RadioButtonTest2)
        Me.GroupBoxRWTest.Controls.Add(Me.ButtonTest)
        resources.ApplyResources(Me.GroupBoxRWTest, "GroupBoxRWTest")
        Me.GroupBoxRWTest.Name = "GroupBoxRWTest"
        Me.GroupBoxRWTest.TabStop = False
        '
        'LabelTestBlocksize
        '
        resources.ApplyResources(Me.LabelTestBlocksize, "LabelTestBlocksize")
        Me.LabelTestBlocksize.Name = "LabelTestBlocksize"
        '
        'ButtonVerify
        '
        resources.ApplyResources(Me.ButtonVerify, "ButtonVerify")
        Me.ButtonVerify.Name = "ButtonVerify"
        Me.ButtonVerify.UseVisualStyleBackColor = True
        '
        'NumericUpDownTestBlkSize
        '
        resources.ApplyResources(Me.NumericUpDownTestBlkSize, "NumericUpDownTestBlkSize")
        Me.NumericUpDownTestBlkSize.Maximum = New Decimal(New Integer() {2097152, 0, 0, 0})
        Me.NumericUpDownTestBlkSize.Name = "NumericUpDownTestBlkSize"
        Me.NumericUpDownTestBlkSize.Value = New Decimal(New Integer() {524288, 0, 0, 0})
        '
        'LabelTestBlockCount
        '
        resources.ApplyResources(Me.LabelTestBlockCount, "LabelTestBlockCount")
        Me.LabelTestBlockCount.Name = "LabelTestBlockCount"
        '
        'NumericUpDownTestBlkNum
        '
        resources.ApplyResources(Me.NumericUpDownTestBlkNum, "NumericUpDownTestBlkNum")
        Me.NumericUpDownTestBlkNum.Maximum = New Decimal(New Integer() {2147483647, 0, 0, 0})
        Me.NumericUpDownTestBlkNum.Minimum = New Decimal(New Integer() {1, 0, 0, 0})
        Me.NumericUpDownTestBlkNum.Name = "NumericUpDownTestBlkNum"
        Me.NumericUpDownTestBlkNum.Value = New Decimal(New Integer() {1024, 0, 0, 0})
        '
        'RadioButtonTest1
        '
        resources.ApplyResources(Me.RadioButtonTest1, "RadioButtonTest1")
        Me.RadioButtonTest1.Checked = True
        Me.RadioButtonTest1.Name = "RadioButtonTest1"
        Me.RadioButtonTest1.TabStop = True
        Me.RadioButtonTest1.UseVisualStyleBackColor = True
        '
        'RadioButtonTest2
        '
        resources.ApplyResources(Me.RadioButtonTest2, "RadioButtonTest2")
        Me.RadioButtonTest2.Name = "RadioButtonTest2"
        Me.RadioButtonTest2.UseVisualStyleBackColor = True
        '
        'ButtonTest
        '
        resources.ApplyResources(Me.ButtonTest, "ButtonTest")
        Me.ButtonTest.Name = "ButtonTest"
        Me.ButtonTest.UseVisualStyleBackColor = True
        '
        'ButtonRDErrRateLog
        '
        resources.ApplyResources(Me.ButtonRDErrRateLog, "ButtonRDErrRateLog")
        Me.ButtonRDErrRateLog.Name = "ButtonRDErrRateLog"
        Me.ButtonRDErrRateLog.UseVisualStyleBackColor = True
        '
        'TabPageZBC
        '
        Me.TabPageZBC.Controls.Add(Me.ButtonZBCiSCSISvc)
        Me.TabPageZBC.Controls.Add(Me.ButtonResetAllZones)
        Me.TabPageZBC.Controls.Add(Me.ButtonRestoreLBA)
        Me.TabPageZBC.Controls.Add(Me.ButtonDumpLBA)
        Me.TabPageZBC.Controls.Add(Me.Label1)
        Me.TabPageZBC.Controls.Add(Me.ComboBoxReportOptions)
        Me.TabPageZBC.Controls.Add(Me.NumericUpDownWriteLBACount)
        Me.TabPageZBC.Controls.Add(Me.ButtonWriteRandom)
        Me.TabPageZBC.Controls.Add(Me.ButtonReportZone)
        Me.TabPageZBC.Controls.Add(Me.NumericUpDownSectorSize)
        Me.TabPageZBC.Controls.Add(Me.ButtonReadLBA)
        Me.TabPageZBC.Controls.Add(Me.ButtonFinishZone)
        Me.TabPageZBC.Controls.Add(Me.ButtonResetWritePointer)
        Me.TabPageZBC.Controls.Add(Me.ButtonCloseZone)
        Me.TabPageZBC.Controls.Add(Me.ButtonOpenZone)
        Me.TabPageZBC.Controls.Add(Me.LabelLBA)
        Me.TabPageZBC.Controls.Add(Me.NumericUpDownLBA)
        Me.TabPageZBC.Controls.Add(Me.ButtonCloseAllZones)
        Me.TabPageZBC.Controls.Add(Me.ButtonReportZones)
        resources.ApplyResources(Me.TabPageZBC, "TabPageZBC")
        Me.TabPageZBC.Name = "TabPageZBC"
        Me.TabPageZBC.UseVisualStyleBackColor = True
        '
        'ButtonZBCiSCSISvc
        '
        resources.ApplyResources(Me.ButtonZBCiSCSISvc, "ButtonZBCiSCSISvc")
        Me.ButtonZBCiSCSISvc.Name = "ButtonZBCiSCSISvc"
        Me.ButtonZBCiSCSISvc.UseVisualStyleBackColor = True
        '
        'ButtonResetAllZones
        '
        resources.ApplyResources(Me.ButtonResetAllZones, "ButtonResetAllZones")
        Me.ButtonResetAllZones.Name = "ButtonResetAllZones"
        Me.ButtonResetAllZones.UseVisualStyleBackColor = True
        '
        'ButtonRestoreLBA
        '
        resources.ApplyResources(Me.ButtonRestoreLBA, "ButtonRestoreLBA")
        Me.ButtonRestoreLBA.Name = "ButtonRestoreLBA"
        Me.ButtonRestoreLBA.UseVisualStyleBackColor = True
        '
        'ButtonDumpLBA
        '
        resources.ApplyResources(Me.ButtonDumpLBA, "ButtonDumpLBA")
        Me.ButtonDumpLBA.Name = "ButtonDumpLBA"
        Me.ButtonDumpLBA.UseVisualStyleBackColor = True
        '
        'Label1
        '
        resources.ApplyResources(Me.Label1, "Label1")
        Me.Label1.Name = "Label1"
        '
        'ComboBoxReportOptions
        '
        Me.ComboBoxReportOptions.DropDownWidth = 1000
        Me.ComboBoxReportOptions.FormattingEnabled = True
        Me.ComboBoxReportOptions.Items.AddRange(New Object() {resources.GetString("ComboBoxReportOptions.Items"), resources.GetString("ComboBoxReportOptions.Items1"), resources.GetString("ComboBoxReportOptions.Items2"), resources.GetString("ComboBoxReportOptions.Items3"), resources.GetString("ComboBoxReportOptions.Items4"), resources.GetString("ComboBoxReportOptions.Items5"), resources.GetString("ComboBoxReportOptions.Items6"), resources.GetString("ComboBoxReportOptions.Items7")})
        resources.ApplyResources(Me.ComboBoxReportOptions, "ComboBoxReportOptions")
        Me.ComboBoxReportOptions.Name = "ComboBoxReportOptions"
        '
        'NumericUpDownWriteLBACount
        '
        resources.ApplyResources(Me.NumericUpDownWriteLBACount, "NumericUpDownWriteLBACount")
        Me.NumericUpDownWriteLBACount.Maximum = New Decimal(New Integer() {2147483647, 0, 0, 0})
        Me.NumericUpDownWriteLBACount.Minimum = New Decimal(New Integer() {1, 0, 0, 0})
        Me.NumericUpDownWriteLBACount.Name = "NumericUpDownWriteLBACount"
        Me.NumericUpDownWriteLBACount.Value = New Decimal(New Integer() {1, 0, 0, 0})
        '
        'ButtonWriteRandom
        '
        resources.ApplyResources(Me.ButtonWriteRandom, "ButtonWriteRandom")
        Me.ButtonWriteRandom.Name = "ButtonWriteRandom"
        Me.ButtonWriteRandom.UseVisualStyleBackColor = True
        '
        'ButtonReportZone
        '
        resources.ApplyResources(Me.ButtonReportZone, "ButtonReportZone")
        Me.ButtonReportZone.Name = "ButtonReportZone"
        Me.ButtonReportZone.UseVisualStyleBackColor = True
        '
        'NumericUpDownSectorSize
        '
        resources.ApplyResources(Me.NumericUpDownSectorSize, "NumericUpDownSectorSize")
        Me.NumericUpDownSectorSize.Maximum = New Decimal(New Integer() {65535, 0, 0, 0})
        Me.NumericUpDownSectorSize.Name = "NumericUpDownSectorSize"
        Me.NumericUpDownSectorSize.Value = New Decimal(New Integer() {4096, 0, 0, 0})
        '
        'ButtonReadLBA
        '
        resources.ApplyResources(Me.ButtonReadLBA, "ButtonReadLBA")
        Me.ButtonReadLBA.Name = "ButtonReadLBA"
        Me.ButtonReadLBA.UseVisualStyleBackColor = True
        '
        'ButtonFinishZone
        '
        resources.ApplyResources(Me.ButtonFinishZone, "ButtonFinishZone")
        Me.ButtonFinishZone.Name = "ButtonFinishZone"
        Me.ButtonFinishZone.UseVisualStyleBackColor = True
        '
        'ButtonResetWritePointer
        '
        resources.ApplyResources(Me.ButtonResetWritePointer, "ButtonResetWritePointer")
        Me.ButtonResetWritePointer.Name = "ButtonResetWritePointer"
        Me.ButtonResetWritePointer.UseVisualStyleBackColor = True
        '
        'ButtonCloseZone
        '
        resources.ApplyResources(Me.ButtonCloseZone, "ButtonCloseZone")
        Me.ButtonCloseZone.Name = "ButtonCloseZone"
        Me.ButtonCloseZone.UseVisualStyleBackColor = True
        '
        'ButtonOpenZone
        '
        resources.ApplyResources(Me.ButtonOpenZone, "ButtonOpenZone")
        Me.ButtonOpenZone.Name = "ButtonOpenZone"
        Me.ButtonOpenZone.UseVisualStyleBackColor = True
        '
        'LabelLBA
        '
        resources.ApplyResources(Me.LabelLBA, "LabelLBA")
        Me.LabelLBA.Name = "LabelLBA"
        '
        'NumericUpDownLBA
        '
        resources.ApplyResources(Me.NumericUpDownLBA, "NumericUpDownLBA")
        Me.NumericUpDownLBA.Maximum = New Decimal(New Integer() {-1, -1, 0, 0})
        Me.NumericUpDownLBA.Name = "NumericUpDownLBA"
        '
        'ButtonCloseAllZones
        '
        resources.ApplyResources(Me.ButtonCloseAllZones, "ButtonCloseAllZones")
        Me.ButtonCloseAllZones.Name = "ButtonCloseAllZones"
        Me.ButtonCloseAllZones.UseVisualStyleBackColor = True
        '
        'ButtonReportZones
        '
        resources.ApplyResources(Me.ButtonReportZones, "ButtonReportZones")
        Me.ButtonReportZones.Name = "ButtonReportZones"
        Me.ButtonReportZones.UseVisualStyleBackColor = True
        '
        'LabelTimeout
        '
        resources.ApplyResources(Me.LabelTimeout, "LabelTimeout")
        Me.LabelTimeout.Name = "LabelTimeout"
        '
        'TextBoxTimeoutValue
        '
        resources.ApplyResources(Me.TextBoxTimeoutValue, "TextBoxTimeoutValue")
        Me.TextBoxTimeoutValue.Name = "TextBoxTimeoutValue"
        '
        'TextBoxDataDir
        '
        resources.ApplyResources(Me.TextBoxDataDir, "TextBoxDataDir")
        Me.TextBoxDataDir.Name = "TextBoxDataDir"
        '
        'LabelDataDir
        '
        resources.ApplyResources(Me.LabelDataDir, "LabelDataDir")
        Me.LabelDataDir.Name = "LabelDataDir"
        '
        'LabelParam
        '
        resources.ApplyResources(Me.LabelParam, "LabelParam")
        Me.LabelParam.ForeColor = System.Drawing.Color.Blue
        Me.LabelParam.Name = "LabelParam"
        '
        'LabelCDB
        '
        resources.ApplyResources(Me.LabelCDB, "LabelCDB")
        Me.LabelCDB.Name = "LabelCDB"
        '
        'TextBoxDebugOutput
        '
        resources.ApplyResources(Me.TextBoxDebugOutput, "TextBoxDebugOutput")
        Me.TextBoxDebugOutput.BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle
        Me.TextBoxDebugOutput.HideSelection = False
        Me.TextBoxDebugOutput.Name = "TextBoxDebugOutput"
        '
        'TextBoxParamData
        '
        resources.ApplyResources(Me.TextBoxParamData, "TextBoxParamData")
        Me.TextBoxParamData.Name = "TextBoxParamData"
        '
        'ButtonDebugSendSCSICommand
        '
        resources.ApplyResources(Me.ButtonDebugSendSCSICommand, "ButtonDebugSendSCSICommand")
        Me.ButtonDebugSendSCSICommand.ContextMenuStrip = Me.ContextMenuStripSend
        Me.ButtonDebugSendSCSICommand.Name = "ButtonDebugSendSCSICommand"
        Me.ButtonDebugSendSCSICommand.UseVisualStyleBackColor = True
        '
        'ContextMenuStripSend
        '
        Me.ContextMenuStripSend.Items.AddRange(New System.Windows.Forms.ToolStripItem() {Me.ToolStripSeparator1, Me.保存ToolStripMenuItem, Me.删除ToolStripMenuItem, Me.DebugToolStripMenuItem})
        Me.ContextMenuStripSend.Name = "ContextMenuStripSend"
        resources.ApplyResources(Me.ContextMenuStripSend, "ContextMenuStripSend")
        '
        'ToolStripSeparator1
        '
        Me.ToolStripSeparator1.Name = "ToolStripSeparator1"
        resources.ApplyResources(Me.ToolStripSeparator1, "ToolStripSeparator1")
        '
        '保存ToolStripMenuItem
        '
        Me.保存ToolStripMenuItem.Name = "保存ToolStripMenuItem"
        resources.ApplyResources(Me.保存ToolStripMenuItem, "保存ToolStripMenuItem")
        '
        '删除ToolStripMenuItem
        '
        Me.删除ToolStripMenuItem.Name = "删除ToolStripMenuItem"
        resources.ApplyResources(Me.删除ToolStripMenuItem, "删除ToolStripMenuItem")
        '
        'DebugToolStripMenuItem
        '
        Me.DebugToolStripMenuItem.Name = "DebugToolStripMenuItem"
        resources.ApplyResources(Me.DebugToolStripMenuItem, "DebugToolStripMenuItem")
        '
        'TextBoxCDBData
        '
        resources.ApplyResources(Me.TextBoxCDBData, "TextBoxCDBData")
        Me.TextBoxCDBData.Name = "TextBoxCDBData"
        '
        'TextBoxDevicePath
        '
        resources.ApplyResources(Me.TextBoxDevicePath, "TextBoxDevicePath")
        Me.TextBoxDevicePath.Name = "TextBoxDevicePath"
        '
        'LabelSCSIIOCtl
        '
        resources.ApplyResources(Me.LabelSCSIIOCtl, "LabelSCSIIOCtl")
        Me.LabelSCSIIOCtl.Name = "LabelSCSIIOCtl"
        '
        'LabelDebugPanel
        '
        resources.ApplyResources(Me.LabelDebugPanel, "LabelDebugPanel")
        Me.LabelDebugPanel.Name = "LabelDebugPanel"
        '
        'SaveFileDialog1
        '
        Me.SaveFileDialog1.FileName = "MAMAttrib.xml"
        resources.ApplyResources(Me.SaveFileDialog1, "SaveFileDialog1")
        '
        'SaveFileDialog2
        '
        Me.SaveFileDialog2.FileName = "Dump.bin"
        resources.ApplyResources(Me.SaveFileDialog2, "SaveFileDialog2")
        '
        'OpenFileDialog1
        '
        Me.OpenFileDialog1.FileName = "OpenFileDialog1"
        '
        'LTFSConfigurator
        '
        resources.ApplyResources(Me, "$this")
        Me.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Dpi
        Me.Controls.Add(Me.Panel1)
        Me.DoubleBuffered = True
        Me.KeyPreview = True
        Me.Name = "LTFSConfigurator"
        Me.ContextMenuStripRefreshDeviceList.ResumeLayout(False)
        Me.Panel1.ResumeLayout(False)
        Me.Panel1.PerformLayout()
        Me.ContextMenuStripLTFSWriter.ResumeLayout(False)
        Me.Panel2.ResumeLayout(False)
        Me.Panel2.PerformLayout()
        Me.TabControl1.ResumeLayout(False)
        Me.TabPageCommand.ResumeLayout(False)
        Me.TabPageCommand.PerformLayout()
        CType(Me.NumericUpDownBlockNum, System.ComponentModel.ISupportInitialize).EndInit()
        CType(Me.NumericUpDownPartitionNum, System.ComponentModel.ISupportInitialize).EndInit()
        Me.ContextMenuStripCMReader.ResumeLayout(False)
        CType(Me.NumericUpDownEraseCycle, System.ComponentModel.ISupportInitialize).EndInit()
        Me.ContextMenuStripErase.ResumeLayout(False)
        CType(Me.NumericUpDownBlockLen, System.ComponentModel.ISupportInitialize).EndInit()
        Me.ContextMenuStripRawDump.ResumeLayout(False)
        Me.TabPageBuffer.ResumeLayout(False)
        Me.TabPageBuffer.PerformLayout()
        CType(Me.NumericUpDownRBMode, System.ComponentModel.ISupportInitialize).EndInit()
        Me.TabPageMAM.ResumeLayout(False)
        Me.TabPageMAM.PerformLayout()
        CType(Me.NumericUpDownRAPartition, System.ComponentModel.ISupportInitialize).EndInit()
        CType(Me.NumericUpDownPCHigh, System.ComponentModel.ISupportInitialize).EndInit()
        CType(Me.NumericUpDownPCLow, System.ComponentModel.ISupportInitialize).EndInit()
        Me.TabPageLog.ResumeLayout(False)
        Me.TabPageLog.PerformLayout()
        Me.TabPageTest.ResumeLayout(False)
        Me.GroupBoxDiag.ResumeLayout(False)
        Me.GroupBoxDiag.PerformLayout()
        CType(Me.NumericUpDownTestSpeed, System.ComponentModel.ISupportInitialize).EndInit()
        CType(Me.NumericUpDownTestStartLen, System.ComponentModel.ISupportInitialize).EndInit()
        CType(Me.NumericUpDownTestSets, System.ComponentModel.ISupportInitialize).EndInit()
        CType(Me.NumericUpDownTestWrap, System.ComponentModel.ISupportInitialize).EndInit()
        Me.GroupBoxRWTest.ResumeLayout(False)
        Me.GroupBoxRWTest.PerformLayout()
        CType(Me.NumericUpDownTestBlkSize, System.ComponentModel.ISupportInitialize).EndInit()
        CType(Me.NumericUpDownTestBlkNum, System.ComponentModel.ISupportInitialize).EndInit()
        Me.TabPageZBC.ResumeLayout(False)
        Me.TabPageZBC.PerformLayout()
        CType(Me.NumericUpDownWriteLBACount, System.ComponentModel.ISupportInitialize).EndInit()
        CType(Me.NumericUpDownSectorSize, System.ComponentModel.ISupportInitialize).EndInit()
        CType(Me.NumericUpDownLBA, System.ComponentModel.ISupportInitialize).EndInit()
        Me.ContextMenuStripSend.ResumeLayout(False)
        Me.ResumeLayout(False)

    End Sub

    Friend WithEvents ButtonRefresh As Button
    Friend WithEvents ListBox1 As ListBox
    Friend WithEvents ButtonStartFUSESvc As Button
    Friend WithEvents ButtonStopFUSESvc As Button
    Friend WithEvents ButtonRemount As Button
    Friend WithEvents ComboBoxDriveLetter As ComboBox
    Friend WithEvents LabelDrive As Label
    Friend WithEvents TextBoxDevInfo As TextBox
    Friend WithEvents LabelLetter As Label
    Friend WithEvents ButtonAssign As Button
    Friend WithEvents ButtonRemove As Button
    Friend WithEvents LabelInfo As Label
    Friend WithEvents ButtonLoadThreaded As Button
    Friend WithEvents ButtonEject As Button
    Friend WithEvents ButtonMount As Button
    Friend WithEvents TextBoxMsg As TextBox
    Friend WithEvents Panel1 As Panel
    Friend WithEvents Panel2 As Panel
    Friend WithEvents CheckBoxDebugPanel As CheckBox
    Friend WithEvents LabelDebugPanel As Label
    Friend WithEvents LabelSCSIIOCtl As Label
    Friend WithEvents TextBoxCDBData As TextBox
    Friend WithEvents TextBoxDevicePath As TextBox
    Friend WithEvents ButtonDebugSendSCSICommand As Button
    Friend WithEvents ButtonLoadUnthreaded As Button
    Friend WithEvents ButtonUnthread As Button
    Friend WithEvents ButtonDebugErase As Button
    Friend WithEvents TextBoxParamData As TextBox
    Friend WithEvents TextBoxDebugOutput As RichTextBox
    Friend WithEvents NumericUpDownEraseCycle As NumericUpDown
    Friend WithEvents LabelPartialErase As Label
    Friend WithEvents LabelParam As Label
    Friend WithEvents LabelCDB As Label
    Friend WithEvents TextBoxDataDir As TextBox
    Friend WithEvents LabelDataDir As Label
    Friend WithEvents ButtonDebugReadMAM As Button
    Friend WithEvents NumericUpDownPCLow As NumericUpDown
    Friend WithEvents NumericUpDownPCHigh As NumericUpDown
    Friend WithEvents LabelMAMPageCode As Label
    Friend WithEvents LabelMAMAttrib As Label
    Friend WithEvents ButtonDebugReadInfo As Button
    Friend WithEvents ButtonDebugDumpMAM As Button
    Friend WithEvents SaveFileDialog1 As SaveFileDialog
    Friend WithEvents LabelBufferID As Label
    Friend WithEvents ButtonDebugRewind As Button
    Friend WithEvents SaveFileDialog2 As SaveFileDialog
    Friend WithEvents ButtonDebugReadBlock As Button
    Friend WithEvents NumericUpDownBlockLen As NumericUpDown
    Friend WithEvents ButtonDebugReadBuffer As Button
    Friend WithEvents ComboBoxBufferPage As ComboBox
    Friend WithEvents ButtonDebugLocate As Button
    Friend WithEvents LabelPartition As Label
    Friend WithEvents NumericUpDownPartitionNum As NumericUpDown
    Friend WithEvents NumericUpDownBlockNum As NumericUpDown
    Friend WithEvents LabelReadBlockLim As Label
    Friend WithEvents LabelRead As Label
    Friend WithEvents ButtonDebugReadPosition As Button
    Friend WithEvents ButtonDebugDumpTape As Button
    Friend WithEvents ButtonStopRawDump As Button
    Friend WithEvents CheckBoxEnableDumpLog As CheckBox
    Friend WithEvents ComboBoxLocateType As ComboBox
    Friend WithEvents ButtonDebugDumpIndex As Button
    Friend WithEvents ButtonDebugFormat As Button
    Friend WithEvents ButtonLTFSWriter As Button
    Friend WithEvents ButtonDebugReleaseUnit As Button
    Friend WithEvents ButtonDebugAllowMediumRemoval As Button
    Friend WithEvents ButtonFileSorter As Button
    Friend WithEvents CheckBoxAutoRefresh As CheckBox
    Friend WithEvents CheckBoxParseCMData As CheckBox
    Friend WithEvents OpenFileDialog1 As OpenFileDialog
    Friend WithEvents ButtonChangerTool As Button
    Friend WithEvents TextBoxTimeoutValue As TextBox
    Friend WithEvents LabelTimeout As Label
    Friend WithEvents ButtonResetLogPage As Button
    Friend WithEvents TabControl1 As TabControl
    Friend WithEvents TabPageCommand As TabPage
    Friend WithEvents TabPageBuffer As TabPage
    Friend WithEvents TabPageMAM As TabPage
    Friend WithEvents TabPageLog As TabPage
    Friend WithEvents ButtonRunLogSense As Button
    Friend WithEvents LabelLogSense As Label
    Friend WithEvents ComboBox4 As ComboBox
    Friend WithEvents ComboBox5 As ComboBox
    Friend WithEvents LabelLogSensePageCtrl As Label
    Friend WithEvents CheckBoxShowRawLogPageData As CheckBox
    Friend WithEvents TabPageTest As TabPage
    Friend WithEvents NumericUpDownTestBlkSize As NumericUpDown
    Friend WithEvents LabelTestBlocksize As Label
    Friend WithEvents NumericUpDownTestBlkNum As NumericUpDown
    Friend WithEvents LabelTestBlockCount As Label
    Friend WithEvents RadioButtonTest2 As RadioButton
    Friend WithEvents RadioButtonTest1 As RadioButton
    Friend WithEvents ButtonTest As Button
    Friend WithEvents ButtonRDErrRateLog As Button
    Friend WithEvents LabelBarcode As Label
    Friend WithEvents TextBoxBarcode As TextBox
    Friend WithEvents ButtonDebugWriteBarcode As Button
    Friend WithEvents ContextMenuStripLTFSWriter As ContextMenuStrip
    Friend WithEvents 在当前进程运行ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 不读取索引ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents ContextMenuStripCMReader As ContextMenuStrip
    Friend WithEvents BrowseBinaryFileToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents ReadThroughDiagnosticCommandToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents ContextMenuStripRefreshDeviceList As ContextMenuStrip
    Friend WithEvents DiskToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents ContextMenuStripErase As ContextMenuStrip
    Friend WithEvents ReInitializeToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents QuickEraseToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents ButtonDiagTest As Button
    Friend WithEvents NumericUpDownTestWrap As NumericUpDown
    Friend WithEvents LabelTestWrap As Label
    Friend WithEvents NumericUpDownTestStartLen As NumericUpDown
    Friend WithEvents NumericUpDownTestSpeed As NumericUpDown
    Friend WithEvents LabelTestStartLen As Label
    Friend WithEvents LabelTestSpeed As Label
    Friend WithEvents ContextMenuStripRawDump As ContextMenuStrip
    Friend WithEvents WriteToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents PlayPCMToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents ManualAddToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents BrowseToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents WritePCMToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents ContextMenuStripSend As ContextMenuStrip
    Friend WithEvents ToolStripSeparator1 As ToolStripSeparator
    Friend WithEvents 保存ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 删除ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents DebugToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents NumericUpDownTestSets As NumericUpDown
    Friend WithEvents LabelTestSets As Label

    Public Sub New()
        ' 此调用是设计器所必需的。
        InitializeComponent()
        ' 在 InitializeComponent() 调用之后添加任何初始化。
        DisplayHelper.ApplyApplicationFont(Me)
        TextBoxDebugOutput.Font = My.Settings.LTFSConf_SCSIConsoleFont
    End Sub

    Friend WithEvents AddImageToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents TabPageZBC As TabPage
    Friend WithEvents ButtonReportZones As Button
    Friend WithEvents ButtonCloseAllZones As Button
    Friend WithEvents LabelLBA As Label
    Friend WithEvents NumericUpDownLBA As NumericUpDown
    Friend WithEvents ButtonResetWritePointer As Button
    Friend WithEvents ButtonCloseZone As Button
    Friend WithEvents ButtonOpenZone As Button
    Friend WithEvents ButtonReadLBA As Button
    Friend WithEvents ButtonFinishZone As Button
    Friend WithEvents NumericUpDownSectorSize As NumericUpDown
    Friend WithEvents ButtonReportZone As Button
    Friend WithEvents ButtonWriteRandom As Button
    Friend WithEvents NumericUpDownWriteLBACount As NumericUpDown
    Friend WithEvents ComboBoxReportOptions As ComboBox
    Friend WithEvents ButtonDumpLBA As Button
    Friend WithEvents Label1 As Label
    Friend WithEvents ButtonRestoreLBA As Button
    Friend WithEvents ButtonBOT As Button
    Friend WithEvents ButtonEOT As Button
    Friend WithEvents NumericUpDownRBMode As NumericUpDown
    Friend WithEvents ButtonDebugWriteBuffer As Button
    Friend WithEvents SCSIAdapterToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents TextBoxTargetID As TextBox
    Friend WithEvents LabelTargetID As Label
    Friend WithEvents TextBoxLUN As TextBox
    Friend WithEvents LabelLUN As Label
    Friend WithEvents Label2 As Label
    Friend WithEvents NumericUpDownRAPartition As NumericUpDown
    Friend WithEvents ButtonVerify As Button
    Friend WithEvents GroupBoxDiag As GroupBox
    Friend WithEvents GroupBoxRWTest As GroupBox
    Friend WithEvents ButtonResetAllZones As Button
    Friend WithEvents ButtonZBCiSCSISvc As Button
End Class
