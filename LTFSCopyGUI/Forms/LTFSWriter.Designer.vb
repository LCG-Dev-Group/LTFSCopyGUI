<Global.Microsoft.VisualBasic.CompilerServices.DesignerGenerated()>
Partial Class LTFSWriter
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
        Dim resources As System.ComponentModel.ComponentResourceManager = New System.ComponentModel.ComponentResourceManager(GetType(LTFSWriter))
        Me.SplitContainer1 = New System.Windows.Forms.SplitContainer()
        Me.TreeView1 = New LTFSCopyGUI.DpiAwareTreeView()
        Me.ContextMenuStrip3 = New System.Windows.Forms.ContextMenuStrip(Me.components)
        Me.提取ToolStripMenuItem1 = New System.Windows.Forms.ToolStripMenuItem()
        Me.校验ToolStripMenuItem1 = New System.Windows.Forms.ToolStripMenuItem()
        Me.计算并更新ToolStripMenuItem1 = New System.Windows.Forms.ToolStripMenuItem()
        Me.跳过已有校验ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.仅验证ToolStripMenuItem1 = New System.Windows.Forms.ToolStripMenuItem()
        Me.剪切目录ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.粘贴选中ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.重命名ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.删除ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.统计ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.详情ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.压缩索引ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.解压索引ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.复制到另一磁带ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.ImageList1 = New System.Windows.Forms.ImageList(Me.components)
        Me.SplitContainer2 = New System.Windows.Forms.SplitContainer()
        Me.ListView1 = New LTFSCopyGUI.DpiAwareListView()
        Me.Column_name = CType(New System.Windows.Forms.ColumnHeader(),System.Windows.Forms.ColumnHeader)
        Me.Column_length = CType(New System.Windows.Forms.ColumnHeader(),System.Windows.Forms.ColumnHeader)
        Me.Column_creationtime = CType(New System.Windows.Forms.ColumnHeader(),System.Windows.Forms.ColumnHeader)
        Me.Column_fileuid = CType(New System.Windows.Forms.ColumnHeader(),System.Windows.Forms.ColumnHeader)
        Me.Column_openforwrite = CType(New System.Windows.Forms.ColumnHeader(),System.Windows.Forms.ColumnHeader)
        Me.Column_readonly = CType(New System.Windows.Forms.ColumnHeader(),System.Windows.Forms.ColumnHeader)
        Me.Column_changetime = CType(New System.Windows.Forms.ColumnHeader(),System.Windows.Forms.ColumnHeader)
        Me.Column_modifytime = CType(New System.Windows.Forms.ColumnHeader(),System.Windows.Forms.ColumnHeader)
        Me.Column_accesstime = CType(New System.Windows.Forms.ColumnHeader(),System.Windows.Forms.ColumnHeader)
        Me.Column_backuptime = CType(New System.Windows.Forms.ColumnHeader(),System.Windows.Forms.ColumnHeader)
        Me.Column_tag = CType(New System.Windows.Forms.ColumnHeader(),System.Windows.Forms.ColumnHeader)
        Me.Column_StartBlock = CType(New System.Windows.Forms.ColumnHeader(),System.Windows.Forms.ColumnHeader)
        Me.Column_Partition = CType(New System.Windows.Forms.ColumnHeader(),System.Windows.Forms.ColumnHeader)
        Me.Column_FriendlyLen = CType(New System.Windows.Forms.ColumnHeader(),System.Windows.Forms.ColumnHeader)
        Me.Column_writtenBytes = CType(New System.Windows.Forms.ColumnHeader(),System.Windows.Forms.ColumnHeader)
        Me.ContextMenuStrip1 = New System.Windows.Forms.ContextMenuStrip(Me.components)
        Me.提取ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.校验ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.计算并更新ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.计算并跳过已有校验ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.仅验证ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.移动到索引区ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.定位到起始块ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.ToolStripSeparator2 = New System.Windows.Forms.ToolStripSeparator()
        Me.剪切文件ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.粘贴选中ToolStripMenuItem1 = New System.Windows.Forms.ToolStripMenuItem()
        Me.重命名文件ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.重命名目录ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.合并文件ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.ToolStripSeparator3 = New System.Windows.Forms.ToolStripSeparator()
        Me.导入文件ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.添加文件ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.添加目录ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.新建目录ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.新建压缩文件ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.选项ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.覆盖已有文件ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.跳过符号链接ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.ToolStripSeparator4 = New System.Windows.Forms.ToolStripSeparator()
        Me.删除文件ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.删除目录ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.ToolStripSeparator8 = New System.Windows.Forms.ToolStripSeparator()
        Me.生成标签ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.设置标签ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.文件详情ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.复制信息到剪贴板ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.复制选中信息ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.子目录列表ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.文件详情ToolStripMenuItem1 = New System.Windows.Forms.ToolStripMenuItem()
        Me.XAttrToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.复制到另一磁带ToolStripMenuItem1 = New System.Windows.Forms.ToolStripMenuItem()
        Me.Chart1 = New LTFSCopyGUI.Direct2DChartControl()
        Me.ContextMenuStrip2 = New System.Windows.Forms.ContextMenuStrip(Me.components)
        Me.S60ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.Min5ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.Min10ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.Min30ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.H1ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.H3ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.H6ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.ToolStripSeparator1 = New System.Windows.Forms.ToolStripSeparator()
        Me.LinearToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.LogarithmicToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.MenuStrip1 = New System.Windows.Forms.MenuStrip()
        Me.磁带ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.索引ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.读取索引ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.读取数据区索引ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.加载外部索引ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.备份当前索引ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.数据操作ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.写入数据ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.更新数据区索引ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.更新全部索引ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.ToolStripSeparator5 = New System.Windows.Forms.ToolStripSeparator()
        Me.自动化ToolStripMenuItem1 = New System.Windows.Forms.ToolStripMenuItem()
        Me.写入完成后标题ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.WA0ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.WA1ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.WA2ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.WA3ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.电源选项ToolStripMenuItem1 = New System.Windows.Forms.ToolStripMenuItem()
        Me.无更改ToolStripMenuItem1 = New System.Windows.Forms.ToolStripMenuItem()
        Me.平衡ToolStripMenuItem1 = New System.Windows.Forms.ToolStripMenuItem()
        Me.节能ToolStripMenuItem1 = New System.Windows.Forms.ToolStripMenuItem()
        Me.高性能ToolStripMenuItem1 = New System.Windows.Forms.ToolStripMenuItem()
        Me.其他ToolStripMenuItem1 = New System.Windows.Forms.ToolStripMenuItem()
        Me.写入时标题ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.计算校验ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.异步校验CPU占用高ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.去重ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.索引间隔36GiBToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.容量刷新间隔30sToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.右下角显示容量损失ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.限速不限制ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.预读文件数5ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.文件缓存32MiBToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.APToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.容量缺失检测参数ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.速度下限ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.速度上限ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.持续时间ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.错误率ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.重装带前清洁次数3ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.电源选项ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.无更改ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.平衡ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.节能ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.高性能ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.其他ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.ToolStripSeparator7 = New System.Windows.Forms.ToolStripSeparator()
        Me.启用日志记录ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.总是更新数据区索引ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.禁用分区ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.显示文件数ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.增强Tar元数据ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.ToolStripSeparator6 = New System.Windows.Forms.ToolStripSeparator()
        Me.回退ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.清除当前索引后数据ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.回滚ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.查找指定位置前的索引ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.数据ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.格式化ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.设置高度ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.加密ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.设置密钥ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.设置密码ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.合并SHA1ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.校验源文件ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.挂载盘符只读ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.启动FTP服务只读ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.启动iSCSI服务ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.驱动器锁定状态ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.加锁ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.解锁ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.StatusStrip1 = New System.Windows.Forms.StatusStrip()
        Me.ToolStripDropDownButton1 = New System.Windows.Forms.ToolStripDropDownButton()
        Me.ToolStripDropDownButton2 = New System.Windows.Forms.ToolStripDropDownButton()
        Me.ToolStripDropDownButton3 = New System.Windows.Forms.ToolStripDropDownButton()
        Me.ToolStripStatusLabel1 = New System.Windows.Forms.ToolStripStatusLabel()
        Me.ToolStripStatusLabel4 = New System.Windows.Forms.ToolStripStatusLabel()
        Me.ToolStripProgressBar1 = New System.Windows.Forms.ToolStripProgressBar()
        Me.ToolStripStatusLabel5 = New System.Windows.Forms.ToolStripStatusLabel()
        Me.ToolStripStatusLabel2 = New System.Windows.Forms.ToolStripStatusLabel()
        Me.Timer1 = New System.Windows.Forms.Timer(Me.components)
        Me.OpenFileDialog1 = New System.Windows.Forms.OpenFileDialog()
        Me.CommandBar = New LTFSCopyGUI.ModernCommandBar()
        Me.ImportFilesButton = New LTFSCopyGUI.ModernCommandButton()
        Me.WriteTapeButton = New LTFSCopyGUI.ModernCommandButton()
        Me.BackupIndexButton = New LTFSCopyGUI.ModernCommandButton()
        Me.MergeHashButton = New LTFSCopyGUI.ModernCommandButton()
        Me.VerifySourceButton = New LTFSCopyGUI.ModernCommandButton()
        Me.SafeEjectButton = New LTFSCopyGUI.ModernCommandButton()
        Me.ForceEjectButton = New LTFSCopyGUI.ModernCommandButton()
        Me.Timer2 = New System.Windows.Forms.Timer(Me.components)
        Me.StatusStrip2 = New System.Windows.Forms.StatusStrip()
        Me.ToolStripStatusLabel6 = New System.Windows.Forms.ToolStripStatusLabel()
        Me.ToolStripStatusLabel3 = New System.Windows.Forms.ToolStripStatusLabel()
        Me.ToolStripStatusLabelS1 = New System.Windows.Forms.ToolStripStatusLabel()
        Me.ToolStripStatusLabelS2 = New System.Windows.Forms.ToolStripStatusLabel()
        Me.ToolStripStatusLabelS3 = New System.Windows.Forms.ToolStripStatusLabel()
        Me.ToolStripStatusLabelS4 = New System.Windows.Forms.ToolStripStatusLabel()
        Me.ToolStripStatusLabelS5 = New System.Windows.Forms.ToolStripStatusLabel()
        Me.ToolStripStatusLabelS6 = New System.Windows.Forms.ToolStripStatusLabel()
        Me.ToolStripStatusLabelErrLog = New System.Windows.Forms.ToolStripStatusLabel()
        Me.TextBoxSelectedPath = New System.Windows.Forms.TextBox()
        Me.ContextMenuStrip4 = New System.Windows.Forms.ContextMenuStrip(Me.components)
        Me.DebugToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.SettingToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.TestToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.停止等待缓存ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.选中二级目录ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.导出待写列表ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.监控写入ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.自适应限速ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.从文件添加ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.提取为空文件ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.按索引删除硬盘文件ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.创建磁盘镜像ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.清除指定大小范围的待写文件ToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.ToolTipChanErrLog = New System.Windows.Forms.ToolTip(Me.components)
        CType(Me.SplitContainer1,System.ComponentModel.ISupportInitialize).BeginInit
        Me.SplitContainer1.Panel1.SuspendLayout
        Me.SplitContainer1.Panel2.SuspendLayout
        Me.SplitContainer1.SuspendLayout
        Me.ContextMenuStrip3.SuspendLayout
        CType(Me.SplitContainer2,System.ComponentModel.ISupportInitialize).BeginInit
        Me.SplitContainer2.Panel1.SuspendLayout
        Me.SplitContainer2.Panel2.SuspendLayout
        Me.SplitContainer2.SuspendLayout
        Me.ContextMenuStrip1.SuspendLayout
        Me.ContextMenuStrip2.SuspendLayout
        Me.MenuStrip1.SuspendLayout
        Me.StatusStrip1.SuspendLayout
        Me.CommandBar.SuspendLayout
        Me.StatusStrip2.SuspendLayout
        Me.ContextMenuStrip4.SuspendLayout
        Me.SuspendLayout
        '
        'SplitContainer1
        '
        resources.ApplyResources(Me.SplitContainer1, "SplitContainer1")
        Me.SplitContainer1.Name = "SplitContainer1"
        '
        'SplitContainer1.Panel1
        '
        Me.SplitContainer1.Panel1.Controls.Add(Me.TreeView1)
        '
        'SplitContainer1.Panel2
        '
        Me.SplitContainer1.Panel2.Controls.Add(Me.SplitContainer2)
        '
        'TreeView1
        '
        Me.TreeView1.ContextMenuStrip = Me.ContextMenuStrip3
        resources.ApplyResources(Me.TreeView1, "TreeView1")
        Me.TreeView1.HideSelection = false
        Me.TreeView1.ImageList = Me.ImageList1
        Me.TreeView1.Name = "TreeView1"
        '
        'ContextMenuStrip3
        '
        Me.ContextMenuStrip3.Items.AddRange(New System.Windows.Forms.ToolStripItem() {Me.提取ToolStripMenuItem1, Me.校验ToolStripMenuItem1, Me.剪切目录ToolStripMenuItem, Me.粘贴选中ToolStripMenuItem, Me.重命名ToolStripMenuItem, Me.删除ToolStripMenuItem, Me.统计ToolStripMenuItem, Me.详情ToolStripMenuItem, Me.压缩索引ToolStripMenuItem, Me.解压索引ToolStripMenuItem, Me.复制到另一磁带ToolStripMenuItem})
        Me.ContextMenuStrip3.Name = "ContextMenuStrip3"
        resources.ApplyResources(Me.ContextMenuStrip3, "ContextMenuStrip3")
        '
        '提取ToolStripMenuItem1
        '
        resources.ApplyResources(Me.提取ToolStripMenuItem1, "提取ToolStripMenuItem1")
        Me.提取ToolStripMenuItem1.Name = "提取ToolStripMenuItem1"
        '
        '校验ToolStripMenuItem1
        '
        Me.校验ToolStripMenuItem1.DropDownItems.AddRange(New System.Windows.Forms.ToolStripItem() {Me.计算并更新ToolStripMenuItem1, Me.跳过已有校验ToolStripMenuItem, Me.仅验证ToolStripMenuItem1})
        Me.校验ToolStripMenuItem1.Name = "校验ToolStripMenuItem1"
        resources.ApplyResources(Me.校验ToolStripMenuItem1, "校验ToolStripMenuItem1")
        '
        '计算并更新ToolStripMenuItem1
        '
        Me.计算并更新ToolStripMenuItem1.Name = "计算并更新ToolStripMenuItem1"
        resources.ApplyResources(Me.计算并更新ToolStripMenuItem1, "计算并更新ToolStripMenuItem1")
        '
        '跳过已有校验ToolStripMenuItem
        '
        Me.跳过已有校验ToolStripMenuItem.Name = "跳过已有校验ToolStripMenuItem"
        resources.ApplyResources(Me.跳过已有校验ToolStripMenuItem, "跳过已有校验ToolStripMenuItem")
        '
        '仅验证ToolStripMenuItem1
        '
        Me.仅验证ToolStripMenuItem1.Name = "仅验证ToolStripMenuItem1"
        resources.ApplyResources(Me.仅验证ToolStripMenuItem1, "仅验证ToolStripMenuItem1")
        '
        '剪切目录ToolStripMenuItem
        '
        Me.剪切目录ToolStripMenuItem.Name = "剪切目录ToolStripMenuItem"
        resources.ApplyResources(Me.剪切目录ToolStripMenuItem, "剪切目录ToolStripMenuItem")
        '
        '粘贴选中ToolStripMenuItem
        '
        Me.粘贴选中ToolStripMenuItem.Name = "粘贴选中ToolStripMenuItem"
        resources.ApplyResources(Me.粘贴选中ToolStripMenuItem, "粘贴选中ToolStripMenuItem")
        '
        '重命名ToolStripMenuItem
        '
        Me.重命名ToolStripMenuItem.Name = "重命名ToolStripMenuItem"
        resources.ApplyResources(Me.重命名ToolStripMenuItem, "重命名ToolStripMenuItem")
        '
        '删除ToolStripMenuItem
        '
        Me.删除ToolStripMenuItem.Name = "删除ToolStripMenuItem"
        resources.ApplyResources(Me.删除ToolStripMenuItem, "删除ToolStripMenuItem")
        '
        '统计ToolStripMenuItem
        '
        Me.统计ToolStripMenuItem.Name = "统计ToolStripMenuItem"
        resources.ApplyResources(Me.统计ToolStripMenuItem, "统计ToolStripMenuItem")
        '
        '详情ToolStripMenuItem
        '
        Me.详情ToolStripMenuItem.Name = "详情ToolStripMenuItem"
        resources.ApplyResources(Me.详情ToolStripMenuItem, "详情ToolStripMenuItem")
        '
        '压缩索引ToolStripMenuItem
        '
        Me.压缩索引ToolStripMenuItem.Name = "压缩索引ToolStripMenuItem"
        resources.ApplyResources(Me.压缩索引ToolStripMenuItem, "压缩索引ToolStripMenuItem")
        '
        '解压索引ToolStripMenuItem
        '
        Me.解压索引ToolStripMenuItem.Name = "解压索引ToolStripMenuItem"
        resources.ApplyResources(Me.解压索引ToolStripMenuItem, "解压索引ToolStripMenuItem")
        '
        '复制到另一磁带ToolStripMenuItem
        '
        Me.复制到另一磁带ToolStripMenuItem.Name = "复制到另一磁带ToolStripMenuItem"
        resources.ApplyResources(Me.复制到另一磁带ToolStripMenuItem, "复制到另一磁带ToolStripMenuItem")
        '
        'ImageList1
        '
        Me.ImageList1.ImageStream = CType(resources.GetObject("ImageList1.ImageStream"),System.Windows.Forms.ImageListStreamer)
        Me.ImageList1.TransparentColor = System.Drawing.Color.Transparent
        Me.ImageList1.Images.SetKeyName(0, "DirRoot.png")
        Me.ImageList1.Images.SetKeyName(1, "Dir.png")
        Me.ImageList1.Images.SetKeyName(2, "File.png")
        Me.ImageList1.Images.SetKeyName(3, "Archive.png")
        '
        'SplitContainer2
        '
        resources.ApplyResources(Me.SplitContainer2, "SplitContainer2")
        Me.SplitContainer2.Name = "SplitContainer2"
        '
        'SplitContainer2.Panel1
        '
        Me.SplitContainer2.Panel1.Controls.Add(Me.ListView1)
        '
        'SplitContainer2.Panel2
        '
        Me.SplitContainer2.Panel2.Controls.Add(Me.Chart1)
        '
        'ListView1
        '
        Me.ListView1.AllowColumnReorder = true
        Me.ListView1.Columns.AddRange(New System.Windows.Forms.ColumnHeader() {Me.Column_name, Me.Column_length, Me.Column_creationtime, Me.Column_fileuid, Me.Column_openforwrite, Me.Column_readonly, Me.Column_changetime, Me.Column_modifytime, Me.Column_accesstime, Me.Column_backuptime, Me.Column_tag, Me.Column_StartBlock, Me.Column_Partition, Me.Column_FriendlyLen, Me.Column_writtenBytes})
        Me.ListView1.ContextMenuStrip = Me.ContextMenuStrip1
        resources.ApplyResources(Me.ListView1, "ListView1")
        Me.ListView1.HideSelection = false
        Me.ListView1.Name = "ListView1"
        Me.ListView1.ShowGroups = false
        Me.ListView1.ShowItemToolTips = true
        Me.ListView1.SmallImageList = Me.ImageList1
        Me.ListView1.UseCompatibleStateImageBehavior = false
        Me.ListView1.View = System.Windows.Forms.View.Details
        Me.ListView1.VirtualMode = true
        '
        'Column_name
        '
        Me.Column_name.Name = "Column_name"
        resources.ApplyResources(Me.Column_name, "Column_name")
        '
        'Column_length
        '
        resources.ApplyResources(Me.Column_length, "Column_length")
        Me.Column_length.Name = "Column_length"
        '
        'Column_creationtime
        '
        resources.ApplyResources(Me.Column_creationtime, "Column_creationtime")
        Me.Column_creationtime.Name = "Column_creationtime"
        '
        'Column_fileuid
        '
        Me.Column_fileuid.Name = "Column_fileuid"
        resources.ApplyResources(Me.Column_fileuid, "Column_fileuid")
        '
        'Column_openforwrite
        '
        resources.ApplyResources(Me.Column_openforwrite, "Column_openforwrite")
        Me.Column_openforwrite.Name = "Column_openforwrite"
        '
        'Column_readonly
        '
        resources.ApplyResources(Me.Column_readonly, "Column_readonly")
        Me.Column_readonly.Name = "Column_readonly"
        '
        'Column_changetime
        '
        resources.ApplyResources(Me.Column_changetime, "Column_changetime")
        Me.Column_changetime.Name = "Column_changetime"
        '
        'Column_modifytime
        '
        resources.ApplyResources(Me.Column_modifytime, "Column_modifytime")
        Me.Column_modifytime.Name = "Column_modifytime"
        '
        'Column_accesstime
        '
        resources.ApplyResources(Me.Column_accesstime, "Column_accesstime")
        Me.Column_accesstime.Name = "Column_accesstime"
        '
        'Column_backuptime
        '
        resources.ApplyResources(Me.Column_backuptime, "Column_backuptime")
        Me.Column_backuptime.Name = "Column_backuptime"
        '
        'Column_tag
        '
        resources.ApplyResources(Me.Column_tag, "Column_tag")
        Me.Column_tag.Name = "Column_tag"
        '
        'Column_StartBlock
        '
        resources.ApplyResources(Me.Column_StartBlock, "Column_StartBlock")
        Me.Column_StartBlock.Name = "Column_StartBlock"
        '
        'Column_Partition
        '
        Me.Column_Partition.Name = "Column_Partition"
        resources.ApplyResources(Me.Column_Partition, "Column_Partition")
        '
        'Column_FriendlyLen
        '
        resources.ApplyResources(Me.Column_FriendlyLen, "Column_FriendlyLen")
        Me.Column_FriendlyLen.Name = "Column_FriendlyLen"
        '
        'Column_writtenBytes
        '
        resources.ApplyResources(Me.Column_writtenBytes, "Column_writtenBytes")
        Me.Column_writtenBytes.Name = "Column_writtenBytes"
        '
        'ContextMenuStrip1
        '
        Me.ContextMenuStrip1.Items.AddRange(New System.Windows.Forms.ToolStripItem() {Me.提取ToolStripMenuItem, Me.校验ToolStripMenuItem, Me.移动到索引区ToolStripMenuItem, Me.定位到起始块ToolStripMenuItem, Me.ToolStripSeparator2, Me.剪切文件ToolStripMenuItem, Me.粘贴选中ToolStripMenuItem1, Me.重命名文件ToolStripMenuItem, Me.重命名目录ToolStripMenuItem, Me.合并文件ToolStripMenuItem, Me.ToolStripSeparator3, Me.导入文件ToolStripMenuItem, Me.添加文件ToolStripMenuItem, Me.添加目录ToolStripMenuItem, Me.新建目录ToolStripMenuItem, Me.新建压缩文件ToolStripMenuItem, Me.选项ToolStripMenuItem, Me.ToolStripSeparator4, Me.删除文件ToolStripMenuItem, Me.删除目录ToolStripMenuItem, Me.ToolStripSeparator8, Me.生成标签ToolStripMenuItem, Me.设置标签ToolStripMenuItem, Me.文件详情ToolStripMenuItem, Me.复制信息到剪贴板ToolStripMenuItem, Me.复制到另一磁带ToolStripMenuItem1})
        Me.ContextMenuStrip1.Name = "ContextMenuStrip1"
        resources.ApplyResources(Me.ContextMenuStrip1, "ContextMenuStrip1")
        '
        '提取ToolStripMenuItem
        '
        Me.提取ToolStripMenuItem.Name = "提取ToolStripMenuItem"
        resources.ApplyResources(Me.提取ToolStripMenuItem, "提取ToolStripMenuItem")
        '
        '校验ToolStripMenuItem
        '
        Me.校验ToolStripMenuItem.DropDownItems.AddRange(New System.Windows.Forms.ToolStripItem() {Me.计算并更新ToolStripMenuItem, Me.计算并跳过已有校验ToolStripMenuItem, Me.仅验证ToolStripMenuItem})
        Me.校验ToolStripMenuItem.Name = "校验ToolStripMenuItem"
        resources.ApplyResources(Me.校验ToolStripMenuItem, "校验ToolStripMenuItem")
        '
        '计算并更新ToolStripMenuItem
        '
        Me.计算并更新ToolStripMenuItem.Name = "计算并更新ToolStripMenuItem"
        resources.ApplyResources(Me.计算并更新ToolStripMenuItem, "计算并更新ToolStripMenuItem")
        '
        '计算并跳过已有校验ToolStripMenuItem
        '
        Me.计算并跳过已有校验ToolStripMenuItem.Name = "计算并跳过已有校验ToolStripMenuItem"
        resources.ApplyResources(Me.计算并跳过已有校验ToolStripMenuItem, "计算并跳过已有校验ToolStripMenuItem")
        '
        '仅验证ToolStripMenuItem
        '
        Me.仅验证ToolStripMenuItem.Name = "仅验证ToolStripMenuItem"
        resources.ApplyResources(Me.仅验证ToolStripMenuItem, "仅验证ToolStripMenuItem")
        '
        '移动到索引区ToolStripMenuItem
        '
        Me.移动到索引区ToolStripMenuItem.Name = "移动到索引区ToolStripMenuItem"
        resources.ApplyResources(Me.移动到索引区ToolStripMenuItem, "移动到索引区ToolStripMenuItem")
        '
        '定位到起始块ToolStripMenuItem
        '
        Me.定位到起始块ToolStripMenuItem.Name = "定位到起始块ToolStripMenuItem"
        resources.ApplyResources(Me.定位到起始块ToolStripMenuItem, "定位到起始块ToolStripMenuItem")
        '
        'ToolStripSeparator2
        '
        Me.ToolStripSeparator2.Name = "ToolStripSeparator2"
        resources.ApplyResources(Me.ToolStripSeparator2, "ToolStripSeparator2")
        '
        '剪切文件ToolStripMenuItem
        '
        Me.剪切文件ToolStripMenuItem.Name = "剪切文件ToolStripMenuItem"
        resources.ApplyResources(Me.剪切文件ToolStripMenuItem, "剪切文件ToolStripMenuItem")
        '
        '粘贴选中ToolStripMenuItem1
        '
        Me.粘贴选中ToolStripMenuItem1.Name = "粘贴选中ToolStripMenuItem1"
        resources.ApplyResources(Me.粘贴选中ToolStripMenuItem1, "粘贴选中ToolStripMenuItem1")
        '
        '重命名文件ToolStripMenuItem
        '
        Me.重命名文件ToolStripMenuItem.Name = "重命名文件ToolStripMenuItem"
        resources.ApplyResources(Me.重命名文件ToolStripMenuItem, "重命名文件ToolStripMenuItem")
        '
        '重命名目录ToolStripMenuItem
        '
        Me.重命名目录ToolStripMenuItem.Name = "重命名目录ToolStripMenuItem"
        resources.ApplyResources(Me.重命名目录ToolStripMenuItem, "重命名目录ToolStripMenuItem")
        '
        '合并文件ToolStripMenuItem
        '
        Me.合并文件ToolStripMenuItem.Name = "合并文件ToolStripMenuItem"
        resources.ApplyResources(Me.合并文件ToolStripMenuItem, "合并文件ToolStripMenuItem")
        '
        'ToolStripSeparator3
        '
        Me.ToolStripSeparator3.Name = "ToolStripSeparator3"
        resources.ApplyResources(Me.ToolStripSeparator3, "ToolStripSeparator3")
        '
        '导入文件ToolStripMenuItem
        '
        resources.ApplyResources(Me.导入文件ToolStripMenuItem, "导入文件ToolStripMenuItem")
        Me.导入文件ToolStripMenuItem.Name = "导入文件ToolStripMenuItem"
        '
        '添加文件ToolStripMenuItem
        '
        Me.添加文件ToolStripMenuItem.Name = "添加文件ToolStripMenuItem"
        resources.ApplyResources(Me.添加文件ToolStripMenuItem, "添加文件ToolStripMenuItem")
        '
        '添加目录ToolStripMenuItem
        '
        Me.添加目录ToolStripMenuItem.Name = "添加目录ToolStripMenuItem"
        resources.ApplyResources(Me.添加目录ToolStripMenuItem, "添加目录ToolStripMenuItem")
        '
        '新建目录ToolStripMenuItem
        '
        Me.新建目录ToolStripMenuItem.Name = "新建目录ToolStripMenuItem"
        resources.ApplyResources(Me.新建目录ToolStripMenuItem, "新建目录ToolStripMenuItem")
        '
        '新建压缩文件ToolStripMenuItem
        '
        Me.新建压缩文件ToolStripMenuItem.Name = "新建压缩文件ToolStripMenuItem"
        resources.ApplyResources(Me.新建压缩文件ToolStripMenuItem, "新建压缩文件ToolStripMenuItem")
        '
        '选项ToolStripMenuItem
        '
        Me.选项ToolStripMenuItem.DropDownItems.AddRange(New System.Windows.Forms.ToolStripItem() {Me.覆盖已有文件ToolStripMenuItem, Me.跳过符号链接ToolStripMenuItem})
        Me.选项ToolStripMenuItem.Name = "选项ToolStripMenuItem"
        resources.ApplyResources(Me.选项ToolStripMenuItem, "选项ToolStripMenuItem")
        '
        '覆盖已有文件ToolStripMenuItem
        '
        Me.覆盖已有文件ToolStripMenuItem.CheckOnClick = true
        Me.覆盖已有文件ToolStripMenuItem.Name = "覆盖已有文件ToolStripMenuItem"
        resources.ApplyResources(Me.覆盖已有文件ToolStripMenuItem, "覆盖已有文件ToolStripMenuItem")
        '
        '跳过符号链接ToolStripMenuItem
        '
        Me.跳过符号链接ToolStripMenuItem.Checked = true
        Me.跳过符号链接ToolStripMenuItem.CheckOnClick = true
        Me.跳过符号链接ToolStripMenuItem.CheckState = System.Windows.Forms.CheckState.Checked
        Me.跳过符号链接ToolStripMenuItem.Name = "跳过符号链接ToolStripMenuItem"
        resources.ApplyResources(Me.跳过符号链接ToolStripMenuItem, "跳过符号链接ToolStripMenuItem")
        '
        'ToolStripSeparator4
        '
        Me.ToolStripSeparator4.Name = "ToolStripSeparator4"
        resources.ApplyResources(Me.ToolStripSeparator4, "ToolStripSeparator4")
        '
        '删除文件ToolStripMenuItem
        '
        Me.删除文件ToolStripMenuItem.Name = "删除文件ToolStripMenuItem"
        resources.ApplyResources(Me.删除文件ToolStripMenuItem, "删除文件ToolStripMenuItem")
        '
        '删除目录ToolStripMenuItem
        '
        Me.删除目录ToolStripMenuItem.Name = "删除目录ToolStripMenuItem"
        resources.ApplyResources(Me.删除目录ToolStripMenuItem, "删除目录ToolStripMenuItem")
        '
        'ToolStripSeparator8
        '
        Me.ToolStripSeparator8.Name = "ToolStripSeparator8"
        resources.ApplyResources(Me.ToolStripSeparator8, "ToolStripSeparator8")
        '
        '生成标签ToolStripMenuItem
        '
        Me.生成标签ToolStripMenuItem.Name = "生成标签ToolStripMenuItem"
        resources.ApplyResources(Me.生成标签ToolStripMenuItem, "生成标签ToolStripMenuItem")
        '
        '设置标签ToolStripMenuItem
        '
        Me.设置标签ToolStripMenuItem.Name = "设置标签ToolStripMenuItem"
        resources.ApplyResources(Me.设置标签ToolStripMenuItem, "设置标签ToolStripMenuItem")
        '
        '文件详情ToolStripMenuItem
        '
        Me.文件详情ToolStripMenuItem.Name = "文件详情ToolStripMenuItem"
        resources.ApplyResources(Me.文件详情ToolStripMenuItem, "文件详情ToolStripMenuItem")
        '
        '复制信息到剪贴板ToolStripMenuItem
        '
        Me.复制信息到剪贴板ToolStripMenuItem.DropDownItems.AddRange(New System.Windows.Forms.ToolStripItem() {Me.复制选中信息ToolStripMenuItem, Me.子目录列表ToolStripMenuItem, Me.文件详情ToolStripMenuItem1, Me.XAttrToolStripMenuItem})
        Me.复制信息到剪贴板ToolStripMenuItem.Name = "复制信息到剪贴板ToolStripMenuItem"
        resources.ApplyResources(Me.复制信息到剪贴板ToolStripMenuItem, "复制信息到剪贴板ToolStripMenuItem")
        '
        '复制选中信息ToolStripMenuItem
        '
        Me.复制选中信息ToolStripMenuItem.Name = "复制选中信息ToolStripMenuItem"
        resources.ApplyResources(Me.复制选中信息ToolStripMenuItem, "复制选中信息ToolStripMenuItem")
        '
        '子目录列表ToolStripMenuItem
        '
        Me.子目录列表ToolStripMenuItem.Name = "子目录列表ToolStripMenuItem"
        resources.ApplyResources(Me.子目录列表ToolStripMenuItem, "子目录列表ToolStripMenuItem")
        '
        '文件详情ToolStripMenuItem1
        '
        Me.文件详情ToolStripMenuItem1.Name = "文件详情ToolStripMenuItem1"
        resources.ApplyResources(Me.文件详情ToolStripMenuItem1, "文件详情ToolStripMenuItem1")
        '
        'XAttrToolStripMenuItem
        '
        Me.XAttrToolStripMenuItem.Name = "XAttrToolStripMenuItem"
        resources.ApplyResources(Me.XAttrToolStripMenuItem, "XAttrToolStripMenuItem")
        '
        '复制到另一磁带ToolStripMenuItem1
        '
        Me.复制到另一磁带ToolStripMenuItem1.Name = "复制到另一磁带ToolStripMenuItem1"
        resources.ApplyResources(Me.复制到另一磁带ToolStripMenuItem1, "复制到另一磁带ToolStripMenuItem1")
        '
        'Chart1
        '
        resources.ApplyResources(Me.Chart1, "Chart1")
        Me.Chart1.AxisColor = System.Drawing.Color.FromArgb(CType(CType(110,Byte),Integer), CType(CType(110,Byte),Integer), CType(CType(130,Byte),Integer))
        Me.Chart1.BackColor = System.Drawing.SystemColors.Window
        Me.Chart1.ChartTitle = ""
        Me.Chart1.ContextMenuStrip = Me.ContextMenuStrip2
        Me.Chart1.ForeColor = System.Drawing.SystemColors.ControlText
        Me.Chart1.GridColor = System.Drawing.Color.FromArgb(CType(CType(205,Byte),Integer), CType(CType(205,Byte),Integer), CType(CType(225,Byte),Integer))
        Me.Chart1.Name = "Chart1"
        Me.Chart1.PrimaryColor = System.Drawing.Color.FromArgb(CType(CType(128,Byte),Integer), CType(CType(128,Byte),Integer), CType(CType(255,Byte),Integer))
        Me.Chart1.SecondaryColor = System.Drawing.Color.FromArgb(CType(CType(255,Byte),Integer), CType(CType(128,Byte),Integer), CType(CType(0,Byte),Integer))
        Me.Chart1.TabStop = false
        Me.Chart1.ThirdColor = System.Drawing.Color.Red
        Me.Chart1.XAxisTitle = "10分钟"
        '
        'ContextMenuStrip2
        '
        Me.ContextMenuStrip2.Items.AddRange(New System.Windows.Forms.ToolStripItem() {Me.S60ToolStripMenuItem, Me.Min5ToolStripMenuItem, Me.Min10ToolStripMenuItem, Me.Min30ToolStripMenuItem, Me.H1ToolStripMenuItem, Me.H3ToolStripMenuItem, Me.H6ToolStripMenuItem, Me.ToolStripSeparator1, Me.LinearToolStripMenuItem, Me.LogarithmicToolStripMenuItem})
        Me.ContextMenuStrip2.Name = "ContextMenuStrip1"
        resources.ApplyResources(Me.ContextMenuStrip2, "ContextMenuStrip2")
        '
        'S60ToolStripMenuItem
        '
        resources.ApplyResources(Me.S60ToolStripMenuItem, "S60ToolStripMenuItem")
        Me.S60ToolStripMenuItem.Name = "S60ToolStripMenuItem"
        '
        'Min5ToolStripMenuItem
        '
        Me.Min5ToolStripMenuItem.Name = "Min5ToolStripMenuItem"
        resources.ApplyResources(Me.Min5ToolStripMenuItem, "Min5ToolStripMenuItem")
        '
        'Min10ToolStripMenuItem
        '
        Me.Min10ToolStripMenuItem.Name = "Min10ToolStripMenuItem"
        resources.ApplyResources(Me.Min10ToolStripMenuItem, "Min10ToolStripMenuItem")
        '
        'Min30ToolStripMenuItem
        '
        Me.Min30ToolStripMenuItem.Name = "Min30ToolStripMenuItem"
        resources.ApplyResources(Me.Min30ToolStripMenuItem, "Min30ToolStripMenuItem")
        '
        'H1ToolStripMenuItem
        '
        Me.H1ToolStripMenuItem.Name = "H1ToolStripMenuItem"
        resources.ApplyResources(Me.H1ToolStripMenuItem, "H1ToolStripMenuItem")
        '
        'H3ToolStripMenuItem
        '
        Me.H3ToolStripMenuItem.Name = "H3ToolStripMenuItem"
        resources.ApplyResources(Me.H3ToolStripMenuItem, "H3ToolStripMenuItem")
        '
        'H6ToolStripMenuItem
        '
        Me.H6ToolStripMenuItem.Name = "H6ToolStripMenuItem"
        resources.ApplyResources(Me.H6ToolStripMenuItem, "H6ToolStripMenuItem")
        '
        'ToolStripSeparator1
        '
        Me.ToolStripSeparator1.Name = "ToolStripSeparator1"
        resources.ApplyResources(Me.ToolStripSeparator1, "ToolStripSeparator1")
        '
        'LinearToolStripMenuItem
        '
        Me.LinearToolStripMenuItem.CheckOnClick = true
        Me.LinearToolStripMenuItem.Name = "LinearToolStripMenuItem"
        resources.ApplyResources(Me.LinearToolStripMenuItem, "LinearToolStripMenuItem")
        '
        'LogarithmicToolStripMenuItem
        '
        Me.LogarithmicToolStripMenuItem.CheckOnClick = true
        Me.LogarithmicToolStripMenuItem.Name = "LogarithmicToolStripMenuItem"
        resources.ApplyResources(Me.LogarithmicToolStripMenuItem, "LogarithmicToolStripMenuItem")
        '
        'MenuStrip1
        '
        Me.MenuStrip1.Items.AddRange(New System.Windows.Forms.ToolStripItem() {Me.磁带ToolStripMenuItem, Me.数据ToolStripMenuItem})
        resources.ApplyResources(Me.MenuStrip1, "MenuStrip1")
        Me.MenuStrip1.Name = "MenuStrip1"
        '
        '磁带ToolStripMenuItem
        '
        Me.磁带ToolStripMenuItem.DropDownItems.AddRange(New System.Windows.Forms.ToolStripItem() {Me.索引ToolStripMenuItem, Me.数据操作ToolStripMenuItem, Me.ToolStripSeparator5, Me.自动化ToolStripMenuItem1, Me.ToolStripSeparator6, Me.回退ToolStripMenuItem})
        Me.磁带ToolStripMenuItem.Name = "磁带ToolStripMenuItem"
        resources.ApplyResources(Me.磁带ToolStripMenuItem, "磁带ToolStripMenuItem")
        '
        '索引ToolStripMenuItem
        '
        Me.索引ToolStripMenuItem.DropDownItems.AddRange(New System.Windows.Forms.ToolStripItem() {Me.读取索引ToolStripMenuItem, Me.读取数据区索引ToolStripMenuItem, Me.加载外部索引ToolStripMenuItem, Me.备份当前索引ToolStripMenuItem})
        resources.ApplyResources(Me.索引ToolStripMenuItem, "索引ToolStripMenuItem")
        Me.索引ToolStripMenuItem.Name = "索引ToolStripMenuItem"
        '
        '读取索引ToolStripMenuItem
        '
        resources.ApplyResources(Me.读取索引ToolStripMenuItem, "读取索引ToolStripMenuItem")
        Me.读取索引ToolStripMenuItem.Name = "读取索引ToolStripMenuItem"
        '
        '读取数据区索引ToolStripMenuItem
        '
        Me.读取数据区索引ToolStripMenuItem.Name = "读取数据区索引ToolStripMenuItem"
        resources.ApplyResources(Me.读取数据区索引ToolStripMenuItem, "读取数据区索引ToolStripMenuItem")
        '
        '加载外部索引ToolStripMenuItem
        '
        Me.加载外部索引ToolStripMenuItem.Name = "加载外部索引ToolStripMenuItem"
        resources.ApplyResources(Me.加载外部索引ToolStripMenuItem, "加载外部索引ToolStripMenuItem")
        '
        '备份当前索引ToolStripMenuItem
        '
        resources.ApplyResources(Me.备份当前索引ToolStripMenuItem, "备份当前索引ToolStripMenuItem")
        Me.备份当前索引ToolStripMenuItem.Name = "备份当前索引ToolStripMenuItem"
        '
        '数据操作ToolStripMenuItem
        '
        Me.数据操作ToolStripMenuItem.DropDownItems.AddRange(New System.Windows.Forms.ToolStripItem() {Me.写入数据ToolStripMenuItem, Me.更新数据区索引ToolStripMenuItem, Me.更新全部索引ToolStripMenuItem})
        Me.数据操作ToolStripMenuItem.Name = "数据操作ToolStripMenuItem"
        resources.ApplyResources(Me.数据操作ToolStripMenuItem, "数据操作ToolStripMenuItem")
        '
        '写入数据ToolStripMenuItem
        '
        resources.ApplyResources(Me.写入数据ToolStripMenuItem, "写入数据ToolStripMenuItem")
        Me.写入数据ToolStripMenuItem.Name = "写入数据ToolStripMenuItem"
        '
        '更新数据区索引ToolStripMenuItem
        '
        resources.ApplyResources(Me.更新数据区索引ToolStripMenuItem, "更新数据区索引ToolStripMenuItem")
        Me.更新数据区索引ToolStripMenuItem.Name = "更新数据区索引ToolStripMenuItem"
        '
        '更新全部索引ToolStripMenuItem
        '
        resources.ApplyResources(Me.更新全部索引ToolStripMenuItem, "更新全部索引ToolStripMenuItem")
        Me.更新全部索引ToolStripMenuItem.Name = "更新全部索引ToolStripMenuItem"
        '
        'ToolStripSeparator5
        '
        Me.ToolStripSeparator5.Name = "ToolStripSeparator5"
        resources.ApplyResources(Me.ToolStripSeparator5, "ToolStripSeparator5")
        '
        '自动化ToolStripMenuItem1
        '
        Me.自动化ToolStripMenuItem1.DropDownItems.AddRange(New System.Windows.Forms.ToolStripItem() {Me.写入完成后标题ToolStripMenuItem, Me.WA0ToolStripMenuItem, Me.WA1ToolStripMenuItem, Me.WA2ToolStripMenuItem, Me.WA3ToolStripMenuItem, Me.电源选项ToolStripMenuItem1, Me.写入时标题ToolStripMenuItem, Me.计算校验ToolStripMenuItem, Me.异步校验CPU占用高ToolStripMenuItem, Me.去重ToolStripMenuItem, Me.索引间隔36GiBToolStripMenuItem, Me.容量刷新间隔30sToolStripMenuItem, Me.右下角显示容量损失ToolStripMenuItem, Me.限速不限制ToolStripMenuItem, Me.预读文件数5ToolStripMenuItem, Me.文件缓存32MiBToolStripMenuItem, Me.APToolStripMenuItem, Me.容量缺失检测参数ToolStripMenuItem, Me.重装带前清洁次数3ToolStripMenuItem, Me.电源选项ToolStripMenuItem, Me.ToolStripSeparator7, Me.启用日志记录ToolStripMenuItem, Me.总是更新数据区索引ToolStripMenuItem, Me.禁用分区ToolStripMenuItem, Me.显示文件数ToolStripMenuItem, Me.增强Tar元数据ToolStripMenuItem})
        Me.自动化ToolStripMenuItem1.Name = "自动化ToolStripMenuItem1"
        resources.ApplyResources(Me.自动化ToolStripMenuItem1, "自动化ToolStripMenuItem1")
        '
        '写入完成后标题ToolStripMenuItem
        '
        resources.ApplyResources(Me.写入完成后标题ToolStripMenuItem, "写入完成后标题ToolStripMenuItem")
        Me.写入完成后标题ToolStripMenuItem.Name = "写入完成后标题ToolStripMenuItem"
        '
        'WA0ToolStripMenuItem
        '
        Me.WA0ToolStripMenuItem.CheckOnClick = true
        resources.ApplyResources(Me.WA0ToolStripMenuItem, "WA0ToolStripMenuItem")
        Me.WA0ToolStripMenuItem.Name = "WA0ToolStripMenuItem"
        '
        'WA1ToolStripMenuItem
        '
        Me.WA1ToolStripMenuItem.Checked = true
        Me.WA1ToolStripMenuItem.CheckOnClick = true
        Me.WA1ToolStripMenuItem.CheckState = System.Windows.Forms.CheckState.Checked
        Me.WA1ToolStripMenuItem.Name = "WA1ToolStripMenuItem"
        resources.ApplyResources(Me.WA1ToolStripMenuItem, "WA1ToolStripMenuItem")
        '
        'WA2ToolStripMenuItem
        '
        Me.WA2ToolStripMenuItem.CheckOnClick = true
        Me.WA2ToolStripMenuItem.Name = "WA2ToolStripMenuItem"
        resources.ApplyResources(Me.WA2ToolStripMenuItem, "WA2ToolStripMenuItem")
        '
        'WA3ToolStripMenuItem
        '
        Me.WA3ToolStripMenuItem.CheckOnClick = true
        Me.WA3ToolStripMenuItem.Name = "WA3ToolStripMenuItem"
        resources.ApplyResources(Me.WA3ToolStripMenuItem, "WA3ToolStripMenuItem")
        '
        '电源选项ToolStripMenuItem1
        '
        Me.电源选项ToolStripMenuItem1.DropDownItems.AddRange(New System.Windows.Forms.ToolStripItem() {Me.无更改ToolStripMenuItem1, Me.平衡ToolStripMenuItem1, Me.节能ToolStripMenuItem1, Me.高性能ToolStripMenuItem1, Me.其他ToolStripMenuItem1})
        Me.电源选项ToolStripMenuItem1.Name = "电源选项ToolStripMenuItem1"
        resources.ApplyResources(Me.电源选项ToolStripMenuItem1, "电源选项ToolStripMenuItem1")
        '
        '无更改ToolStripMenuItem1
        '
        resources.ApplyResources(Me.无更改ToolStripMenuItem1, "无更改ToolStripMenuItem1")
        Me.无更改ToolStripMenuItem1.Name = "无更改ToolStripMenuItem1"
        '
        '平衡ToolStripMenuItem1
        '
        Me.平衡ToolStripMenuItem1.Name = "平衡ToolStripMenuItem1"
        resources.ApplyResources(Me.平衡ToolStripMenuItem1, "平衡ToolStripMenuItem1")
        '
        '节能ToolStripMenuItem1
        '
        Me.节能ToolStripMenuItem1.Name = "节能ToolStripMenuItem1"
        resources.ApplyResources(Me.节能ToolStripMenuItem1, "节能ToolStripMenuItem1")
        '
        '高性能ToolStripMenuItem1
        '
        Me.高性能ToolStripMenuItem1.Name = "高性能ToolStripMenuItem1"
        resources.ApplyResources(Me.高性能ToolStripMenuItem1, "高性能ToolStripMenuItem1")
        '
        '其他ToolStripMenuItem1
        '
        Me.其他ToolStripMenuItem1.Name = "其他ToolStripMenuItem1"
        resources.ApplyResources(Me.其他ToolStripMenuItem1, "其他ToolStripMenuItem1")
        '
        '写入时标题ToolStripMenuItem
        '
        resources.ApplyResources(Me.写入时标题ToolStripMenuItem, "写入时标题ToolStripMenuItem")
        Me.写入时标题ToolStripMenuItem.Name = "写入时标题ToolStripMenuItem"
        '
        '计算校验ToolStripMenuItem
        '
        Me.计算校验ToolStripMenuItem.CheckOnClick = true
        Me.计算校验ToolStripMenuItem.Name = "计算校验ToolStripMenuItem"
        resources.ApplyResources(Me.计算校验ToolStripMenuItem, "计算校验ToolStripMenuItem")
        '
        '异步校验CPU占用高ToolStripMenuItem
        '
        Me.异步校验CPU占用高ToolStripMenuItem.CheckOnClick = true
        Me.异步校验CPU占用高ToolStripMenuItem.Name = "异步校验CPU占用高ToolStripMenuItem"
        resources.ApplyResources(Me.异步校验CPU占用高ToolStripMenuItem, "异步校验CPU占用高ToolStripMenuItem")
        '
        '去重ToolStripMenuItem
        '
        Me.去重ToolStripMenuItem.Name = "去重ToolStripMenuItem"
        resources.ApplyResources(Me.去重ToolStripMenuItem, "去重ToolStripMenuItem")
        '
        '索引间隔36GiBToolStripMenuItem
        '
        Me.索引间隔36GiBToolStripMenuItem.Name = "索引间隔36GiBToolStripMenuItem"
        resources.ApplyResources(Me.索引间隔36GiBToolStripMenuItem, "索引间隔36GiBToolStripMenuItem")
        '
        '容量刷新间隔30sToolStripMenuItem
        '
        Me.容量刷新间隔30sToolStripMenuItem.Name = "容量刷新间隔30sToolStripMenuItem"
        resources.ApplyResources(Me.容量刷新间隔30sToolStripMenuItem, "容量刷新间隔30sToolStripMenuItem")
        '
        '右下角显示容量损失ToolStripMenuItem
        '
        Me.右下角显示容量损失ToolStripMenuItem.CheckOnClick = true
        Me.右下角显示容量损失ToolStripMenuItem.Name = "右下角显示容量损失ToolStripMenuItem"
        resources.ApplyResources(Me.右下角显示容量损失ToolStripMenuItem, "右下角显示容量损失ToolStripMenuItem")
        '
        '限速不限制ToolStripMenuItem
        '
        Me.限速不限制ToolStripMenuItem.Name = "限速不限制ToolStripMenuItem"
        resources.ApplyResources(Me.限速不限制ToolStripMenuItem, "限速不限制ToolStripMenuItem")
        '
        '预读文件数5ToolStripMenuItem
        '
        Me.预读文件数5ToolStripMenuItem.Name = "预读文件数5ToolStripMenuItem"
        resources.ApplyResources(Me.预读文件数5ToolStripMenuItem, "预读文件数5ToolStripMenuItem")
        '
        '文件缓存32MiBToolStripMenuItem
        '
        Me.文件缓存32MiBToolStripMenuItem.Name = "文件缓存32MiBToolStripMenuItem"
        resources.ApplyResources(Me.文件缓存32MiBToolStripMenuItem, "文件缓存32MiBToolStripMenuItem")
        '
        'APToolStripMenuItem
        '
        Me.APToolStripMenuItem.CheckOnClick = true
        Me.APToolStripMenuItem.Name = "APToolStripMenuItem"
        resources.ApplyResources(Me.APToolStripMenuItem, "APToolStripMenuItem")
        '
        '容量缺失检测参数ToolStripMenuItem
        '
        Me.容量缺失检测参数ToolStripMenuItem.DropDownItems.AddRange(New System.Windows.Forms.ToolStripItem() {Me.速度下限ToolStripMenuItem, Me.速度上限ToolStripMenuItem, Me.持续时间ToolStripMenuItem, Me.错误率ToolStripMenuItem})
        Me.容量缺失检测参数ToolStripMenuItem.Name = "容量缺失检测参数ToolStripMenuItem"
        resources.ApplyResources(Me.容量缺失检测参数ToolStripMenuItem, "容量缺失检测参数ToolStripMenuItem")
        '
        '速度下限ToolStripMenuItem
        '
        resources.ApplyResources(Me.速度下限ToolStripMenuItem, "速度下限ToolStripMenuItem")
        Me.速度下限ToolStripMenuItem.Name = "速度下限ToolStripMenuItem"
        '
        '速度上限ToolStripMenuItem
        '
        Me.速度上限ToolStripMenuItem.Name = "速度上限ToolStripMenuItem"
        resources.ApplyResources(Me.速度上限ToolStripMenuItem, "速度上限ToolStripMenuItem")
        '
        '持续时间ToolStripMenuItem
        '
        Me.持续时间ToolStripMenuItem.Name = "持续时间ToolStripMenuItem"
        resources.ApplyResources(Me.持续时间ToolStripMenuItem, "持续时间ToolStripMenuItem")
        '
        '错误率ToolStripMenuItem
        '
        Me.错误率ToolStripMenuItem.Name = "错误率ToolStripMenuItem"
        resources.ApplyResources(Me.错误率ToolStripMenuItem, "错误率ToolStripMenuItem")
        '
        '重装带前清洁次数3ToolStripMenuItem
        '
        Me.重装带前清洁次数3ToolStripMenuItem.Name = "重装带前清洁次数3ToolStripMenuItem"
        resources.ApplyResources(Me.重装带前清洁次数3ToolStripMenuItem, "重装带前清洁次数3ToolStripMenuItem")
        '
        '电源选项ToolStripMenuItem
        '
        Me.电源选项ToolStripMenuItem.DropDownItems.AddRange(New System.Windows.Forms.ToolStripItem() {Me.无更改ToolStripMenuItem, Me.平衡ToolStripMenuItem, Me.节能ToolStripMenuItem, Me.高性能ToolStripMenuItem, Me.其他ToolStripMenuItem})
        Me.电源选项ToolStripMenuItem.Name = "电源选项ToolStripMenuItem"
        resources.ApplyResources(Me.电源选项ToolStripMenuItem, "电源选项ToolStripMenuItem")
        '
        '无更改ToolStripMenuItem
        '
        resources.ApplyResources(Me.无更改ToolStripMenuItem, "无更改ToolStripMenuItem")
        Me.无更改ToolStripMenuItem.Name = "无更改ToolStripMenuItem"
        '
        '平衡ToolStripMenuItem
        '
        Me.平衡ToolStripMenuItem.Name = "平衡ToolStripMenuItem"
        resources.ApplyResources(Me.平衡ToolStripMenuItem, "平衡ToolStripMenuItem")
        '
        '节能ToolStripMenuItem
        '
        Me.节能ToolStripMenuItem.Name = "节能ToolStripMenuItem"
        resources.ApplyResources(Me.节能ToolStripMenuItem, "节能ToolStripMenuItem")
        '
        '高性能ToolStripMenuItem
        '
        Me.高性能ToolStripMenuItem.Name = "高性能ToolStripMenuItem"
        resources.ApplyResources(Me.高性能ToolStripMenuItem, "高性能ToolStripMenuItem")
        '
        '其他ToolStripMenuItem
        '
        Me.其他ToolStripMenuItem.Name = "其他ToolStripMenuItem"
        resources.ApplyResources(Me.其他ToolStripMenuItem, "其他ToolStripMenuItem")
        '
        'ToolStripSeparator7
        '
        Me.ToolStripSeparator7.Name = "ToolStripSeparator7"
        resources.ApplyResources(Me.ToolStripSeparator7, "ToolStripSeparator7")
        '
        '启用日志记录ToolStripMenuItem
        '
        Me.启用日志记录ToolStripMenuItem.CheckOnClick = true
        Me.启用日志记录ToolStripMenuItem.Name = "启用日志记录ToolStripMenuItem"
        resources.ApplyResources(Me.启用日志记录ToolStripMenuItem, "启用日志记录ToolStripMenuItem")
        '
        '总是更新数据区索引ToolStripMenuItem
        '
        Me.总是更新数据区索引ToolStripMenuItem.CheckOnClick = true
        Me.总是更新数据区索引ToolStripMenuItem.Name = "总是更新数据区索引ToolStripMenuItem"
        resources.ApplyResources(Me.总是更新数据区索引ToolStripMenuItem, "总是更新数据区索引ToolStripMenuItem")
        '
        '禁用分区ToolStripMenuItem
        '
        Me.禁用分区ToolStripMenuItem.CheckOnClick = true
        Me.禁用分区ToolStripMenuItem.Name = "禁用分区ToolStripMenuItem"
        resources.ApplyResources(Me.禁用分区ToolStripMenuItem, "禁用分区ToolStripMenuItem")
        '
        '显示文件数ToolStripMenuItem
        '
        Me.显示文件数ToolStripMenuItem.CheckOnClick = true
        Me.显示文件数ToolStripMenuItem.Name = "显示文件数ToolStripMenuItem"
        resources.ApplyResources(Me.显示文件数ToolStripMenuItem, "显示文件数ToolStripMenuItem")
        '
        '增强Tar元数据ToolStripMenuItem
        '
        Me.增强Tar元数据ToolStripMenuItem.CheckOnClick = true
        Me.增强Tar元数据ToolStripMenuItem.Name = "增强Tar元数据ToolStripMenuItem"
        resources.ApplyResources(Me.增强Tar元数据ToolStripMenuItem, "增强Tar元数据ToolStripMenuItem")
        '
        'ToolStripSeparator6
        '
        Me.ToolStripSeparator6.Name = "ToolStripSeparator6"
        resources.ApplyResources(Me.ToolStripSeparator6, "ToolStripSeparator6")
        '
        '回退ToolStripMenuItem
        '
        Me.回退ToolStripMenuItem.DropDownItems.AddRange(New System.Windows.Forms.ToolStripItem() {Me.清除当前索引后数据ToolStripMenuItem, Me.回滚ToolStripMenuItem, Me.查找指定位置前的索引ToolStripMenuItem})
        Me.回退ToolStripMenuItem.Name = "回退ToolStripMenuItem"
        resources.ApplyResources(Me.回退ToolStripMenuItem, "回退ToolStripMenuItem")
        '
        '清除当前索引后数据ToolStripMenuItem
        '
        resources.ApplyResources(Me.清除当前索引后数据ToolStripMenuItem, "清除当前索引后数据ToolStripMenuItem")
        Me.清除当前索引后数据ToolStripMenuItem.Name = "清除当前索引后数据ToolStripMenuItem"
        '
        '回滚ToolStripMenuItem
        '
        resources.ApplyResources(Me.回滚ToolStripMenuItem, "回滚ToolStripMenuItem")
        Me.回滚ToolStripMenuItem.Name = "回滚ToolStripMenuItem"
        '
        '查找指定位置前的索引ToolStripMenuItem
        '
        resources.ApplyResources(Me.查找指定位置前的索引ToolStripMenuItem, "查找指定位置前的索引ToolStripMenuItem")
        Me.查找指定位置前的索引ToolStripMenuItem.Name = "查找指定位置前的索引ToolStripMenuItem"
        '
        '数据ToolStripMenuItem
        '
        Me.数据ToolStripMenuItem.DropDownItems.AddRange(New System.Windows.Forms.ToolStripItem() {Me.格式化ToolStripMenuItem, Me.设置高度ToolStripMenuItem, Me.加密ToolStripMenuItem, Me.合并SHA1ToolStripMenuItem, Me.校验源文件ToolStripMenuItem, Me.挂载盘符只读ToolStripMenuItem, Me.启动FTP服务只读ToolStripMenuItem, Me.启动iSCSI服务ToolStripMenuItem, Me.驱动器锁定状态ToolStripMenuItem})
        Me.数据ToolStripMenuItem.Name = "数据ToolStripMenuItem"
        resources.ApplyResources(Me.数据ToolStripMenuItem, "数据ToolStripMenuItem")
        '
        '格式化ToolStripMenuItem
        '
        Me.格式化ToolStripMenuItem.Name = "格式化ToolStripMenuItem"
        resources.ApplyResources(Me.格式化ToolStripMenuItem, "格式化ToolStripMenuItem")
        '
        '设置高度ToolStripMenuItem
        '
        Me.设置高度ToolStripMenuItem.Name = "设置高度ToolStripMenuItem"
        resources.ApplyResources(Me.设置高度ToolStripMenuItem, "设置高度ToolStripMenuItem")
        '
        '加密ToolStripMenuItem
        '
        Me.加密ToolStripMenuItem.DropDownItems.AddRange(New System.Windows.Forms.ToolStripItem() {Me.设置密钥ToolStripMenuItem, Me.设置密码ToolStripMenuItem})
        Me.加密ToolStripMenuItem.Name = "加密ToolStripMenuItem"
        resources.ApplyResources(Me.加密ToolStripMenuItem, "加密ToolStripMenuItem")
        '
        '设置密钥ToolStripMenuItem
        '
        resources.ApplyResources(Me.设置密钥ToolStripMenuItem, "设置密钥ToolStripMenuItem")
        Me.设置密钥ToolStripMenuItem.Name = "设置密钥ToolStripMenuItem"
        '
        '设置密码ToolStripMenuItem
        '
        Me.设置密码ToolStripMenuItem.Name = "设置密码ToolStripMenuItem"
        resources.ApplyResources(Me.设置密码ToolStripMenuItem, "设置密码ToolStripMenuItem")
        '
        '合并SHA1ToolStripMenuItem
        '
        resources.ApplyResources(Me.合并SHA1ToolStripMenuItem, "合并SHA1ToolStripMenuItem")
        Me.合并SHA1ToolStripMenuItem.Name = "合并SHA1ToolStripMenuItem"
        '
        '校验源文件ToolStripMenuItem
        '
        resources.ApplyResources(Me.校验源文件ToolStripMenuItem, "校验源文件ToolStripMenuItem")
        Me.校验源文件ToolStripMenuItem.Name = "校验源文件ToolStripMenuItem"
        '
        '挂载盘符只读ToolStripMenuItem
        '
        Me.挂载盘符只读ToolStripMenuItem.Name = "挂载盘符只读ToolStripMenuItem"
        resources.ApplyResources(Me.挂载盘符只读ToolStripMenuItem, "挂载盘符只读ToolStripMenuItem")
        '
        '启动FTP服务只读ToolStripMenuItem
        '
        Me.启动FTP服务只读ToolStripMenuItem.Name = "启动FTP服务只读ToolStripMenuItem"
        resources.ApplyResources(Me.启动FTP服务只读ToolStripMenuItem, "启动FTP服务只读ToolStripMenuItem")
        '
        '启动iSCSI服务ToolStripMenuItem
        '
        Me.启动iSCSI服务ToolStripMenuItem.Name = "启动iSCSI服务ToolStripMenuItem"
        resources.ApplyResources(Me.启动iSCSI服务ToolStripMenuItem, "启动iSCSI服务ToolStripMenuItem")
        '
        '驱动器锁定状态ToolStripMenuItem
        '
        Me.驱动器锁定状态ToolStripMenuItem.DropDownItems.AddRange(New System.Windows.Forms.ToolStripItem() {Me.加锁ToolStripMenuItem, Me.解锁ToolStripMenuItem})
        Me.驱动器锁定状态ToolStripMenuItem.Name = "驱动器锁定状态ToolStripMenuItem"
        resources.ApplyResources(Me.驱动器锁定状态ToolStripMenuItem, "驱动器锁定状态ToolStripMenuItem")
        '
        '加锁ToolStripMenuItem
        '
        resources.ApplyResources(Me.加锁ToolStripMenuItem, "加锁ToolStripMenuItem")
        Me.加锁ToolStripMenuItem.Name = "加锁ToolStripMenuItem"
        '
        '解锁ToolStripMenuItem
        '
        Me.解锁ToolStripMenuItem.Name = "解锁ToolStripMenuItem"
        resources.ApplyResources(Me.解锁ToolStripMenuItem, "解锁ToolStripMenuItem")
        '
        'StatusStrip1
        '
        resources.ApplyResources(Me.StatusStrip1, "StatusStrip1")
        Me.StatusStrip1.ImageScalingSize = New System.Drawing.Size(24, 24)
        Me.StatusStrip1.Items.AddRange(New System.Windows.Forms.ToolStripItem() {Me.ToolStripDropDownButton1, Me.ToolStripDropDownButton2, Me.ToolStripDropDownButton3, Me.ToolStripStatusLabel1, Me.ToolStripStatusLabel4, Me.ToolStripProgressBar1, Me.ToolStripStatusLabel5, Me.ToolStripStatusLabel2})
        Me.StatusStrip1.Name = "StatusStrip1"
        Me.StatusStrip1.ShowItemToolTips = true
        Me.StatusStrip1.SizingGrip = false
        '
        'ToolStripDropDownButton1
        '
        Me.ToolStripDropDownButton1.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Text
        Me.ToolStripDropDownButton1.ForeColor = System.Drawing.Color.Red
        resources.ApplyResources(Me.ToolStripDropDownButton1, "ToolStripDropDownButton1")
        Me.ToolStripDropDownButton1.Name = "ToolStripDropDownButton1"
        Me.ToolStripDropDownButton1.ShowDropDownArrow = false
        '
        'ToolStripDropDownButton2
        '
        Me.ToolStripDropDownButton2.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Text
        Me.ToolStripDropDownButton2.ForeColor = System.Drawing.Color.Blue
        resources.ApplyResources(Me.ToolStripDropDownButton2, "ToolStripDropDownButton2")
        Me.ToolStripDropDownButton2.Name = "ToolStripDropDownButton2"
        Me.ToolStripDropDownButton2.ShowDropDownArrow = false
        '
        'ToolStripDropDownButton3
        '
        Me.ToolStripDropDownButton3.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Text
        Me.ToolStripDropDownButton3.ForeColor = System.Drawing.Color.FromArgb(CType(CType(255,Byte),Integer), CType(CType(128,Byte),Integer), CType(CType(0,Byte),Integer))
        resources.ApplyResources(Me.ToolStripDropDownButton3, "ToolStripDropDownButton3")
        Me.ToolStripDropDownButton3.Name = "ToolStripDropDownButton3"
        Me.ToolStripDropDownButton3.ShowDropDownArrow = false
        '
        'ToolStripStatusLabel1
        '
        Me.ToolStripStatusLabel1.BorderSides = System.Windows.Forms.ToolStripStatusLabelBorderSides.Left
        Me.ToolStripStatusLabel1.Name = "ToolStripStatusLabel1"
        resources.ApplyResources(Me.ToolStripStatusLabel1, "ToolStripStatusLabel1")
        '
        'ToolStripStatusLabel4
        '
        Me.ToolStripStatusLabel4.BorderSides = System.Windows.Forms.ToolStripStatusLabelBorderSides.Left
        Me.ToolStripStatusLabel4.Name = "ToolStripStatusLabel4"
        resources.ApplyResources(Me.ToolStripStatusLabel4, "ToolStripStatusLabel4")
        Me.ToolStripStatusLabel4.Spring = true
        '
        'ToolStripProgressBar1
        '
        Me.ToolStripProgressBar1.AccessibleRole = System.Windows.Forms.AccessibleRole.ProgressBar
        Me.ToolStripProgressBar1.Maximum = 10000
        Me.ToolStripProgressBar1.Name = "ToolStripProgressBar1"
        resources.ApplyResources(Me.ToolStripProgressBar1, "ToolStripProgressBar1")
        '
        'ToolStripStatusLabel5
        '
        Me.ToolStripStatusLabel5.Name = "ToolStripStatusLabel5"
        resources.ApplyResources(Me.ToolStripStatusLabel5, "ToolStripStatusLabel5")
        '
        'ToolStripStatusLabel2
        '
        resources.ApplyResources(Me.ToolStripStatusLabel2, "ToolStripStatusLabel2")
        Me.ToolStripStatusLabel2.Name = "ToolStripStatusLabel2"
        '
        'Timer1
        '
        Me.Timer1.Enabled = true
        Me.Timer1.Interval = 1000
        '
        'OpenFileDialog1
        '
        Me.OpenFileDialog1.Multiselect = true
        '
        'CommandBar
        '
        Me.CommandBar.BackColor = System.Drawing.Color.Transparent
        Me.CommandBar.Controls.Add(Me.ImportFilesButton)
        Me.CommandBar.Controls.Add(Me.WriteTapeButton)
        Me.CommandBar.Controls.Add(Me.BackupIndexButton)
        Me.CommandBar.Controls.Add(Me.MergeHashButton)
        Me.CommandBar.Controls.Add(Me.VerifySourceButton)
        Me.CommandBar.Controls.Add(Me.SafeEjectButton)
        Me.CommandBar.Controls.Add(Me.ForceEjectButton)
        resources.ApplyResources(Me.CommandBar, "CommandBar")
        Me.CommandBar.Name = "CommandBar"
        '
        'ImportFilesButton
        '
        Me.ImportFilesButton.BackColor = System.Drawing.Color.Transparent
        Me.ImportFilesButton.CommandIcon = LTFSCopyGUI.ModernCommandIcon.ImportFiles
        resources.ApplyResources(Me.ImportFilesButton, "ImportFilesButton")
        Me.ImportFilesButton.Name = "ImportFilesButton"
        Me.ImportFilesButton.UseVisualStyleBackColor = false
        '
        'WriteTapeButton
        '
        Me.WriteTapeButton.BackColor = System.Drawing.Color.Transparent
        Me.WriteTapeButton.CommandIcon = LTFSCopyGUI.ModernCommandIcon.WriteTape
        resources.ApplyResources(Me.WriteTapeButton, "WriteTapeButton")
        Me.WriteTapeButton.Name = "WriteTapeButton"
        Me.WriteTapeButton.UseVisualStyleBackColor = false
        '
        'BackupIndexButton
        '
        Me.BackupIndexButton.BackColor = System.Drawing.Color.Transparent
        Me.BackupIndexButton.CommandIcon = LTFSCopyGUI.ModernCommandIcon.BackupIndex
        resources.ApplyResources(Me.BackupIndexButton, "BackupIndexButton")
        Me.BackupIndexButton.Name = "BackupIndexButton"
        Me.BackupIndexButton.UseVisualStyleBackColor = false
        '
        'MergeHashButton
        '
        Me.MergeHashButton.BackColor = System.Drawing.Color.Transparent
        Me.MergeHashButton.CommandIcon = LTFSCopyGUI.ModernCommandIcon.MergeHash
        resources.ApplyResources(Me.MergeHashButton, "MergeHashButton")
        Me.MergeHashButton.Name = "MergeHashButton"
        Me.MergeHashButton.UseVisualStyleBackColor = false
        '
        'VerifySourceButton
        '
        Me.VerifySourceButton.BackColor = System.Drawing.Color.Transparent
        Me.VerifySourceButton.CommandIcon = LTFSCopyGUI.ModernCommandIcon.VerifySource
        resources.ApplyResources(Me.VerifySourceButton, "VerifySourceButton")
        Me.VerifySourceButton.Name = "VerifySourceButton"
        Me.VerifySourceButton.UseVisualStyleBackColor = false
        '
        'SafeEjectButton
        '
        Me.SafeEjectButton.BackColor = System.Drawing.Color.Transparent
        Me.SafeEjectButton.CommandIcon = LTFSCopyGUI.ModernCommandIcon.SafeEject
        resources.ApplyResources(Me.SafeEjectButton, "SafeEjectButton")
        Me.SafeEjectButton.Name = "SafeEjectButton"
        Me.SafeEjectButton.UseVisualStyleBackColor = false
        '
        'ForceEjectButton
        '
        Me.ForceEjectButton.BackColor = System.Drawing.Color.Transparent
        Me.ForceEjectButton.CommandIcon = LTFSCopyGUI.ModernCommandIcon.ForceEject
        resources.ApplyResources(Me.ForceEjectButton, "ForceEjectButton")
        Me.ForceEjectButton.Name = "ForceEjectButton"
        Me.ForceEjectButton.UseVisualStyleBackColor = false
        '
        'Timer2
        '
        Me.Timer2.Enabled = true
        Me.Timer2.Interval = 200
        '
        'StatusStrip2
        '
        Me.StatusStrip2.ImageScalingSize = New System.Drawing.Size(24, 24)
        Me.StatusStrip2.Items.AddRange(New System.Windows.Forms.ToolStripItem() {Me.ToolStripStatusLabel6, Me.ToolStripStatusLabel3, Me.ToolStripStatusLabelS1, Me.ToolStripStatusLabelS2, Me.ToolStripStatusLabelS3, Me.ToolStripStatusLabelS4, Me.ToolStripStatusLabelS5, Me.ToolStripStatusLabelS6, Me.ToolStripStatusLabelErrLog})
        resources.ApplyResources(Me.StatusStrip2, "StatusStrip2")
        Me.StatusStrip2.Name = "StatusStrip2"
        Me.StatusStrip2.ShowItemToolTips = true
        Me.StatusStrip2.SizingGrip = false
        '
        'ToolStripStatusLabel6
        '
        Me.ToolStripStatusLabel6.Name = "ToolStripStatusLabel6"
        resources.ApplyResources(Me.ToolStripStatusLabel6, "ToolStripStatusLabel6")
        '
        'ToolStripStatusLabel3
        '
        Me.ToolStripStatusLabel3.BorderSides = System.Windows.Forms.ToolStripStatusLabelBorderSides.Right
        Me.ToolStripStatusLabel3.Name = "ToolStripStatusLabel3"
        resources.ApplyResources(Me.ToolStripStatusLabel3, "ToolStripStatusLabel3")
        Me.ToolStripStatusLabel3.Spring = true
        '
        'ToolStripStatusLabelS1
        '
        Me.ToolStripStatusLabelS1.BorderSides = System.Windows.Forms.ToolStripStatusLabelBorderSides.Right
        Me.ToolStripStatusLabelS1.ForeColor = System.Drawing.Color.Gray
        Me.ToolStripStatusLabelS1.Name = "ToolStripStatusLabelS1"
        resources.ApplyResources(Me.ToolStripStatusLabelS1, "ToolStripStatusLabelS1")
        '
        'ToolStripStatusLabelS2
        '
        Me.ToolStripStatusLabelS2.ForeColor = System.Drawing.Color.Gray
        Me.ToolStripStatusLabelS2.Name = "ToolStripStatusLabelS2"
        resources.ApplyResources(Me.ToolStripStatusLabelS2, "ToolStripStatusLabelS2")
        '
        'ToolStripStatusLabelS3
        '
        Me.ToolStripStatusLabelS3.ForeColor = System.Drawing.Color.Gray
        Me.ToolStripStatusLabelS3.Name = "ToolStripStatusLabelS3"
        resources.ApplyResources(Me.ToolStripStatusLabelS3, "ToolStripStatusLabelS3")
        '
        'ToolStripStatusLabelS4
        '
        Me.ToolStripStatusLabelS4.ForeColor = System.Drawing.Color.Gray
        Me.ToolStripStatusLabelS4.Name = "ToolStripStatusLabelS4"
        resources.ApplyResources(Me.ToolStripStatusLabelS4, "ToolStripStatusLabelS4")
        '
        'ToolStripStatusLabelS5
        '
        Me.ToolStripStatusLabelS5.ForeColor = System.Drawing.Color.Gray
        Me.ToolStripStatusLabelS5.Name = "ToolStripStatusLabelS5"
        resources.ApplyResources(Me.ToolStripStatusLabelS5, "ToolStripStatusLabelS5")
        '
        'ToolStripStatusLabelS6
        '
        Me.ToolStripStatusLabelS6.ForeColor = System.Drawing.Color.Gray
        Me.ToolStripStatusLabelS6.Name = "ToolStripStatusLabelS6"
        resources.ApplyResources(Me.ToolStripStatusLabelS6, "ToolStripStatusLabelS6")
        '
        'ToolStripStatusLabelErrLog
        '
        Me.ToolStripStatusLabelErrLog.BorderSides = System.Windows.Forms.ToolStripStatusLabelBorderSides.Left
        Me.ToolStripStatusLabelErrLog.Name = "ToolStripStatusLabelErrLog"
        resources.ApplyResources(Me.ToolStripStatusLabelErrLog, "ToolStripStatusLabelErrLog")
        '
        'TextBoxSelectedPath
        '
        resources.ApplyResources(Me.TextBoxSelectedPath, "TextBoxSelectedPath")
        Me.TextBoxSelectedPath.Name = "TextBoxSelectedPath"
        '
        'ContextMenuStrip4
        '
        Me.ContextMenuStrip4.Items.AddRange(New System.Windows.Forms.ToolStripItem() {Me.DebugToolStripMenuItem, Me.SettingToolStripMenuItem, Me.TestToolStripMenuItem})
        Me.ContextMenuStrip4.Name = "ContextMenuStrip4"
        resources.ApplyResources(Me.ContextMenuStrip4, "ContextMenuStrip4")
        '
        'DebugToolStripMenuItem
        '
        Me.DebugToolStripMenuItem.Name = "DebugToolStripMenuItem"
        resources.ApplyResources(Me.DebugToolStripMenuItem, "DebugToolStripMenuItem")
        '
        'SettingToolStripMenuItem
        '
        Me.SettingToolStripMenuItem.Name = "SettingToolStripMenuItem"
        resources.ApplyResources(Me.SettingToolStripMenuItem, "SettingToolStripMenuItem")
        '
        'TestToolStripMenuItem
        '
        Me.TestToolStripMenuItem.DropDownItems.AddRange(New System.Windows.Forms.ToolStripItem() {Me.停止等待缓存ToolStripMenuItem, Me.选中二级目录ToolStripMenuItem, Me.导出待写列表ToolStripMenuItem, Me.监控写入ToolStripMenuItem, Me.自适应限速ToolStripMenuItem, Me.从文件添加ToolStripMenuItem, Me.提取为空文件ToolStripMenuItem, Me.按索引删除硬盘文件ToolStripMenuItem, Me.创建磁盘镜像ToolStripMenuItem, Me.清除指定大小范围的待写文件ToolStripMenuItem})
        Me.TestToolStripMenuItem.Name = "TestToolStripMenuItem"
        resources.ApplyResources(Me.TestToolStripMenuItem, "TestToolStripMenuItem")
        '
        '停止等待缓存ToolStripMenuItem
        '
        Me.停止等待缓存ToolStripMenuItem.Name = "停止等待缓存ToolStripMenuItem"
        resources.ApplyResources(Me.停止等待缓存ToolStripMenuItem, "停止等待缓存ToolStripMenuItem")
        '
        '选中二级目录ToolStripMenuItem
        '
        Me.选中二级目录ToolStripMenuItem.Name = "选中二级目录ToolStripMenuItem"
        resources.ApplyResources(Me.选中二级目录ToolStripMenuItem, "选中二级目录ToolStripMenuItem")
        '
        '导出待写列表ToolStripMenuItem
        '
        Me.导出待写列表ToolStripMenuItem.Name = "导出待写列表ToolStripMenuItem"
        resources.ApplyResources(Me.导出待写列表ToolStripMenuItem, "导出待写列表ToolStripMenuItem")
        '
        '监控写入ToolStripMenuItem
        '
        Me.监控写入ToolStripMenuItem.Name = "监控写入ToolStripMenuItem"
        resources.ApplyResources(Me.监控写入ToolStripMenuItem, "监控写入ToolStripMenuItem")
        '
        '自适应限速ToolStripMenuItem
        '
        Me.自适应限速ToolStripMenuItem.Name = "自适应限速ToolStripMenuItem"
        resources.ApplyResources(Me.自适应限速ToolStripMenuItem, "自适应限速ToolStripMenuItem")
        '
        '从文件添加ToolStripMenuItem
        '
        Me.从文件添加ToolStripMenuItem.Name = "从文件添加ToolStripMenuItem"
        resources.ApplyResources(Me.从文件添加ToolStripMenuItem, "从文件添加ToolStripMenuItem")
        '
        '提取为空文件ToolStripMenuItem
        '
        Me.提取为空文件ToolStripMenuItem.Name = "提取为空文件ToolStripMenuItem"
        resources.ApplyResources(Me.提取为空文件ToolStripMenuItem, "提取为空文件ToolStripMenuItem")
        '
        '按索引删除硬盘文件ToolStripMenuItem
        '
        Me.按索引删除硬盘文件ToolStripMenuItem.Name = "按索引删除硬盘文件ToolStripMenuItem"
        resources.ApplyResources(Me.按索引删除硬盘文件ToolStripMenuItem, "按索引删除硬盘文件ToolStripMenuItem")
        '
        '创建磁盘镜像ToolStripMenuItem
        '
        Me.创建磁盘镜像ToolStripMenuItem.Name = "创建磁盘镜像ToolStripMenuItem"
        resources.ApplyResources(Me.创建磁盘镜像ToolStripMenuItem, "创建磁盘镜像ToolStripMenuItem")
        '
        '清除指定大小范围的待写文件ToolStripMenuItem
        '
        Me.清除指定大小范围的待写文件ToolStripMenuItem.Name = "清除指定大小范围的待写文件ToolStripMenuItem"
        resources.ApplyResources(Me.清除指定大小范围的待写文件ToolStripMenuItem, "清除指定大小范围的待写文件ToolStripMenuItem")
        '
        'ToolTipChanErrLog
        '
        Me.ToolTipChanErrLog.AutoPopDelay = 3600000
        Me.ToolTipChanErrLog.InitialDelay = 500
        Me.ToolTipChanErrLog.ReshowDelay = 100
        Me.ToolTipChanErrLog.ShowAlways = true
        Me.ToolTipChanErrLog.UseAnimation = false
        Me.ToolTipChanErrLog.UseFading = false
        '
        'LTFSWriter
        '
        resources.ApplyResources(Me, "$this")
        Me.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Dpi
        Me.Controls.Add(Me.TextBoxSelectedPath)
        Me.Controls.Add(Me.StatusStrip2)
        Me.Controls.Add(Me.CommandBar)
        Me.Controls.Add(Me.StatusStrip1)
        Me.Controls.Add(Me.SplitContainer1)
        Me.Controls.Add(Me.MenuStrip1)
        Me.DoubleBuffered = true
        Me.KeyPreview = true
        Me.MainMenuStrip = Me.MenuStrip1
        Me.Name = "LTFSWriter"
        Me.SplitContainer1.Panel1.ResumeLayout(false)
        Me.SplitContainer1.Panel2.ResumeLayout(false)
        CType(Me.SplitContainer1,System.ComponentModel.ISupportInitialize).EndInit
        Me.SplitContainer1.ResumeLayout(false)
        Me.ContextMenuStrip3.ResumeLayout(false)
        Me.SplitContainer2.Panel1.ResumeLayout(false)
        Me.SplitContainer2.Panel2.ResumeLayout(false)
        CType(Me.SplitContainer2,System.ComponentModel.ISupportInitialize).EndInit
        Me.SplitContainer2.ResumeLayout(false)
        Me.ContextMenuStrip1.ResumeLayout(false)
        Me.ContextMenuStrip2.ResumeLayout(false)
        Me.MenuStrip1.ResumeLayout(false)
        Me.MenuStrip1.PerformLayout
        Me.StatusStrip1.ResumeLayout(false)
        Me.StatusStrip1.PerformLayout
        Me.CommandBar.ResumeLayout(false)
        Me.StatusStrip2.ResumeLayout(false)
        Me.StatusStrip2.PerformLayout
        Me.ContextMenuStrip4.ResumeLayout(false)
        Me.ResumeLayout(false)
        Me.PerformLayout

End Sub

    Friend WithEvents ListView1 As DpiAwareListView
    Friend WithEvents Column_name As ColumnHeader
    Friend WithEvents Column_length As ColumnHeader
    Friend WithEvents Column_creationtime As ColumnHeader
    Friend WithEvents Column_changetime As ColumnHeader
    Friend WithEvents SplitContainer1 As SplitContainer
    Friend WithEvents TreeView1 As LTFSCopyGUI.DpiAwareTreeView
    Friend WithEvents Column_fileuid As ColumnHeader
    Friend WithEvents Column_openforwrite As ColumnHeader
    Friend WithEvents Column_readonly As ColumnHeader
    Friend WithEvents Column_modifytime As ColumnHeader
    Friend WithEvents Column_accesstime As ColumnHeader
    Friend WithEvents Column_backuptime As ColumnHeader
    Friend WithEvents Column_tag As ColumnHeader
    Friend WithEvents Chart1 As Direct2DChartControl
    Friend WithEvents MenuStrip1 As MenuStrip
    Friend WithEvents 磁带ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 读取索引ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 写入数据ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 更新全部索引ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents StatusStrip1 As StatusStrip
    Friend WithEvents ToolStripStatusLabel2 As ToolStripStatusLabel
    Friend WithEvents ContextMenuStrip1 As ContextMenuStrip
    Friend WithEvents 重命名文件ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 提取ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 添加文件ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents Timer1 As Timer
    Friend WithEvents 删除文件ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents OpenFileDialog1 As OpenFileDialog
    Friend WithEvents 添加目录ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 新建目录ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 删除目录ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 重命名目录ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 清除当前索引后数据ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 回滚ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 更新数据区索引ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents SplitContainer2 As SplitContainer
    Friend WithEvents ContextMenuStrip2 As ContextMenuStrip
    Friend WithEvents S60ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents Min5ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents Min10ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents Min30ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents H1ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents H3ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents H6ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents ToolStripSeparator1 As ToolStripSeparator
    Friend WithEvents LinearToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents LogarithmicToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents Column_StartBlock As ColumnHeader
    Friend WithEvents Column_Partition As ColumnHeader
    Friend WithEvents Column_FriendlyLen As ColumnHeader
    Friend WithEvents Column_writtenBytes As ColumnHeader
    Friend WithEvents 加载外部索引ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 导入文件ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 数据ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 设置高度ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 定位到起始块ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 校验ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents ContextMenuStrip3 As ContextMenuStrip
    Friend WithEvents 提取ToolStripMenuItem1 As ToolStripMenuItem
    Friend WithEvents 校验ToolStripMenuItem1 As ToolStripMenuItem
    Friend WithEvents 重命名ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 删除ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents ToolStripSeparator2 As ToolStripSeparator
    Friend WithEvents ToolStripSeparator3 As ToolStripSeparator
    Friend WithEvents ToolStripSeparator4 As ToolStripSeparator
    Friend WithEvents ToolStripSeparator5 As ToolStripSeparator
    Friend WithEvents ToolStripSeparator6 As ToolStripSeparator
    Friend WithEvents 格式化ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 选项ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 覆盖已有文件ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents ToolStripDropDownButton1 As ToolStripDropDownButton
    Friend WithEvents ToolStripDropDownButton2 As ToolStripDropDownButton
    Friend WithEvents ToolStripStatusLabel5 As ToolStripStatusLabel
    Friend WithEvents 读取数据区索引ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 合并SHA1ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 自动化ToolStripMenuItem1 As ToolStripMenuItem
    Friend WithEvents WA0ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents WA1ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents WA2ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents WA3ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents ToolStripSeparator7 As ToolStripSeparator
    Friend WithEvents 索引ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 数据操作ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 写入完成后标题ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 回退ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents ToolStripDropDownButton3 As ToolStripDropDownButton
    Friend WithEvents 备份当前索引ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents ToolStripSeparator8 As ToolStripSeparator
    Friend WithEvents 生成标签ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 设置标签ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 写入时标题ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents APToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 启用日志记录ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents CommandBar As ModernCommandBar
    Friend WithEvents ImportFilesButton As ModernCommandButton
    Friend WithEvents WriteTapeButton As ModernCommandButton
    Friend WithEvents SafeEjectButton As ModernCommandButton
    Friend WithEvents BackupIndexButton As ModernCommandButton
    Friend WithEvents ImageList1 As ImageList
    Friend WithEvents MergeHashButton As ModernCommandButton
    Friend WithEvents 校验源文件ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents VerifySourceButton As ModernCommandButton
    Friend WithEvents 总是更新数据区索引ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 限速不限制ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 重装带前清洁次数3ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 计算并更新ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 仅验证ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 计算并更新ToolStripMenuItem1 As ToolStripMenuItem
    Friend WithEvents 跳过已有校验ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 仅验证ToolStripMenuItem1 As ToolStripMenuItem
    Friend WithEvents 计算并跳过已有校验ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 计算校验ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 异步校验CPU占用高ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 索引间隔36GiBToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 容量刷新间隔30sToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents Timer2 As Timer
    Friend WithEvents 复制选中信息ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 统计ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 预读文件数5ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 文件缓存32MiBToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 文件详情ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 禁用分区ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 增强Tar元数据ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 容量缺失检测参数ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 速度下限ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 速度上限ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 持续时间ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 去重ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 挂载盘符只读ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 复制信息到剪贴板ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 子目录列表ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 文件详情ToolStripMenuItem1 As ToolStripMenuItem
    Friend WithEvents StatusStrip2 As StatusStrip
    Friend WithEvents ToolStripStatusLabel4 As ToolStripStatusLabel
    Friend WithEvents ToolStripStatusLabel3 As ToolStripStatusLabel
    Friend WithEvents ToolStripStatusLabel1 As ToolStripStatusLabel
    Friend WithEvents XAttrToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 启动FTP服务只读ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 右下角显示容量损失ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 压缩索引ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 解压索引ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 跳过符号链接ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 显示文件数ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 移动到索引区ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 查找指定位置前的索引ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents TextBoxSelectedPath As TextBox
    Friend WithEvents ContextMenuStrip4 As ContextMenuStrip
    Friend WithEvents DebugToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 加密ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 设置密钥ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 设置密码ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 剪切文件ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 剪切目录ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 粘贴选中ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 粘贴选中ToolStripMenuItem1 As ToolStripMenuItem
    Friend WithEvents 驱动器锁定状态ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 加锁ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 解锁ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 新建压缩文件ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 电源选项ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 无更改ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 平衡ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 节能ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 高性能ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 其他ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 电源选项ToolStripMenuItem1 As ToolStripMenuItem
    Friend WithEvents 无更改ToolStripMenuItem1 As ToolStripMenuItem
    Friend WithEvents 平衡ToolStripMenuItem1 As ToolStripMenuItem
    Friend WithEvents 节能ToolStripMenuItem1 As ToolStripMenuItem
    Friend WithEvents 高性能ToolStripMenuItem1 As ToolStripMenuItem
    Friend WithEvents 其他ToolStripMenuItem1 As ToolStripMenuItem
    Friend WithEvents 详情ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents ToolStripStatusLabelS1 As ToolStripStatusLabel
    Friend WithEvents ToolStripStatusLabelS2 As ToolStripStatusLabel
    Friend WithEvents ToolStripStatusLabelS3 As ToolStripStatusLabel
    Friend WithEvents ToolStripStatusLabelS4 As ToolStripStatusLabel
    Friend WithEvents ToolStripStatusLabelS5 As ToolStripStatusLabel
    Friend WithEvents ToolStripStatusLabelS6 As ToolStripStatusLabel
    Friend WithEvents 错误率ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents ToolStripStatusLabel6 As ToolStripStatusLabel
    Friend WithEvents ToolStripStatusLabelErrLog As ToolStripStatusLabel
    Friend WithEvents ToolTipChanErrLog As ToolTip
    Friend WithEvents ToolStripProgressBar1 As ToolStripProgressBar
    Friend WithEvents SettingToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 合并文件ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 启动iSCSI服务ToolStripMenuItem As ToolStripMenuItem

    Public Sub New()
        ' 此调用是设计器所必需的。
        InitializeComponent()
        ' 在 InitializeComponent() 调用之后添加任何初始化。
        DisplayHelper.ApplyApplicationFont(Me)
    End Sub

    Friend WithEvents TestToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 选中二级目录ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 导出待写列表ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 监控写入ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 停止等待缓存ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 自适应限速ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 从文件添加ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 提取为空文件ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 按索引删除硬盘文件ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents ForceEjectButton As ModernCommandButton
    Friend WithEvents 创建磁盘镜像ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 清除指定大小范围的待写文件ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 复制到另一磁带ToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents 复制到另一磁带ToolStripMenuItem1 As ToolStripMenuItem
End Class
