Imports System
Imports System.Buffers
Imports System.ComponentModel
Imports System.IO
Imports System.Runtime.InteropServices
Imports System.Text
Imports LTFSCopyGUI.TapeImage
Imports LTFSCopyGUI.Native
Imports NAudio.Wave
Imports Serilog
Imports Serilog.Context

Public Class LTFSConfigurator
    Private ReadOnly _logSessionId As String = $"configurator-{Guid.NewGuid().ToString("N").Substring(0, 8)}"
    Private Const WM_SETREDRAW As Integer = &HB

    Private LoadComplete As Boolean = False
    Private _SelectedIndex As Integer
    Public ReadOnly Property DriveOpenCount As SerializableDictionary(Of String, Integer)
        Get
            Return TapeUtils.DriveOpenCount
        End Get
    End Property
    Public ReadOnly Property DriveHandle As SerializableDictionary(Of String, IntPtr)
        Get
            Return TapeUtils.DriveHandle
        End Get
    End Property
    Public Function GetCurDrive(Optional ByVal ForceDisableRefresh As Boolean = False) As TapeUtils.BlockDevice
        Dim dlist As List(Of TapeUtils.BlockDevice)
        If (Not ForceDisableRefresh) AndAlso (CheckBoxAutoRefresh.Checked OrElse LastDeviceList Is Nothing) Then
            dlist = DeviceList
        Else
            dlist = LastDeviceList
        End If
        If dlist.Count <> ListBox1.Items.Count Then
            RefreshUI()
        End If
        If dlist.Count = 0 Then Return Nothing
        Return dlist(SelectedIndex)
    End Function
    Public ReadOnly Property TapeDrive As String
        Get
            Dim result As String = ""
            Dim curDrive As TapeUtils.BlockDevice = GetCurDrive()
            If curDrive.DevicePath IsNot Nothing AndAlso curDrive.DevicePath.Length > 0 Then Return curDrive.DevicePath
            Try
                result = $"\\.\{ curDrive.DeviceType}{curDrive.DevIndex}"
            Catch ex As Exception
                If TextBoxDevicePath.Text <> "" Then
                    result = TextBoxDevicePath.Text
                Else
                    result = "\\.\TAPE0"
                End If

            End Try
            Return result
        End Get
    End Property
    Public Property ConfTapeDrive As String
        Get
            Return TextBoxDevicePath.Text
        End Get
        Set(value As String)
            TextBoxDevicePath.Text = value
        End Set
    End Property

    Public Property SelectedIndex As Integer
        Set(value As Integer)
            _SelectedIndex = Math.Max(0, value)
            If Not LoadComplete Then Exit Property
            Dim CurDrive As TapeUtils.BlockDevice = GetCurDrive(ForceDisableRefresh:=_deviceRefreshInProgress)
            If CurDrive Is Nothing Then
                ButtonAssign.Enabled = False
                ButtonLTFSWriter.Enabled = False
                ButtonRemove.Enabled = False
                ButtonLoadThreaded.Enabled = False
                ButtonLoadUnthreaded.Enabled = False
                ButtonUnthread.Enabled = False
                ButtonEject.Enabled = False
                ButtonMount.Enabled = False
                'Button27.Enabled = False
                Exit Property
            End If
            TextBoxDevInfo.Text = CurDrive.ToString()
            TapeUtils.CheckSwitchConfig(CurDrive)
            My.Settings.Save()
            If CurDrive.DriveLetter <> "" Then
                If Not ComboBoxDriveLetter.Items.Contains(CurDrive.DriveLetter) Then ComboBoxDriveLetter.Items.Add(CurDrive.DriveLetter)
                ComboBoxDriveLetter.SelectedItem = CurDrive.DriveLetter
                TextBoxMsg.AppendText(TapeUtils.CheckTapeMedia(CChar(CurDrive.DriveLetter)) & vbCrLf)
            End If
            ComboBoxDriveLetter.Enabled = (CurDrive.DriveLetter = "")
            ButtonAssign.Enabled = (CurDrive.DriveLetter = "")
            ButtonLTFSWriter.Enabled = (CurDrive.DriveLetter = "")
            ButtonRemove.Enabled = (CurDrive.DriveLetter <> "")
            TextBoxDevicePath.Text = TapeDrive
            ButtonLoadThreaded.Enabled = True
            ButtonLoadUnthreaded.Enabled = True
            ButtonUnthread.Enabled = True
            ButtonEject.Enabled = True
            ButtonMount.Enabled = (CurDrive.DriveLetter <> "")
        End Set
        Get
            Return _SelectedIndex
        End Get
    End Property
    <TypeConverter(GetType(ListTypeDescriptor(Of List(Of TapeUtils.BlockDevice), TapeUtils.BlockDevice)))>
    Public Property LastDeviceList As List(Of TapeUtils.BlockDevice)
    Public ReadOnly Property DeviceList As List(Of TapeUtils.BlockDevice)
        Get
            LastDeviceList = TapeUtils.GetTapeDriveList()
            Return LastDeviceList
        End Get
    End Property
    <TypeConverter(GetType(ListTypeDescriptor(Of List(Of Char), Char)))>
    Public ReadOnly Property AvailableDriveLetters As List(Of Char)
        Get
            Dim Result As New List(Of Char)
            Dim drv() As DriveInfo = DriveInfo.GetDrives()
            Dim UsedList As New List(Of Integer)
            For Each d As DriveInfo In drv
                UsedList.Add(Asc(d.Name(0)))
            Next
            For c As Integer = Asc("D") To Asc("Z")
                If Not UsedList.Contains(c) Then
                    Result.Add(Chr(c))
                End If
            Next
            Return Result
        End Get
    End Property
    Public UILock As New Object
    Private _deviceRefreshInProgress As Boolean
    Public Sub RefreshUI(Optional RefreshDevList As Boolean = True)
        If IsDisposed OrElse Disposing Then Exit Sub
        If InvokeRequired Then
            BeginInvoke(New Action(Sub() RefreshUI(RefreshDevList)))
            Exit Sub
        End If
        If Not LoadComplete OrElse _deviceRefreshInProgress Then Exit Sub

        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(LTFSConfigurator))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Configurator")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "DeviceRefresh")
                        Log.Debug("Configurator device refresh started. RefreshDeviceList={RefreshDeviceList}.", RefreshDevList)
                    End Using
                End Using
            End Using
        End Using
        _deviceRefreshInProgress = True
        LoadComplete = False
        ButtonRefresh.Enabled = False
        Dim selectedIndexBeforeRefresh As Integer = SelectedIndex
        Dim refreshSucceeded As Boolean = False

        Task.Run(Sub()
                     Try
                         If Threading.Monitor.TryEnter(UILock, 100) Then
                             Try
                                 Dim DevList As List(Of TapeUtils.BlockDevice)
                                 If RefreshDevList OrElse LastDeviceList Is Nothing Then
                                     DevList = DeviceList
                                 Else
                                     DevList = LastDeviceList
                                 End If
                                 Dim driveLetters As List(Of Char) = AvailableDriveLetters

                                 If IsDisposed OrElse Disposing Then Return
                                 Invoke(Sub()
                                            ListBox1.Items.Clear()
                                            For Each D As TapeUtils.BlockDevice In DevList
                                                If TapeUtils.TagDictionary.ContainsKey(D.SerialNumber) Then
                                                    ListBox1.Items.Add(TapeUtils.TagDictionary(D.SerialNumber))
                                                Else
                                                    ListBox1.Items.Add(D.ToString())
                                                End If
                                            Next
                                            ListBox1.SelectedIndex = Math.Min(selectedIndexBeforeRefresh, ListBox1.Items.Count - 1)
                                            Dim t As String = ComboBoxDriveLetter.Text
                                            ComboBoxDriveLetter.Items.Clear()
                                            ComboBoxDriveLetter.Text = ""
                                            For Each s As Char In driveLetters
                                                ComboBoxDriveLetter.Items.Add(s)
                                            Next
                                            If ComboBoxDriveLetter.Items.Count > 0 Then
                                                If Not ComboBoxDriveLetter.Items.Contains(t) Then
                                                    ComboBoxDriveLetter.SelectedIndex = 0
                                                Else
                                                    ComboBoxDriveLetter.Text = t
                                                End If
                                            End If
                                            If Not My.Settings.Application_License.ToLower().Contains("dev") Then TabControl1.TabPages.Remove(TabPageZBC)
                                            LoadComplete = True
                                             SelectedIndex = ListBox1.SelectedIndex
                                         End Sub)
                                  refreshSucceeded = True
                             Finally
                                 Threading.Monitor.Exit(UILock)
                             End Try
                         End If
                     Catch ex As Exception
                         Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(LTFSConfigurator))
                             Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Configurator")
                                 Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                                     Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Error")
                                         Log.Error(ex, "Configurator device refresh failed. RefreshDeviceList={RefreshDeviceList}.", RefreshDevList)
                                     End Using
                                 End Using
                             End Using
                         End Using
                         If Not IsDisposed AndAlso Not Disposing Then
                             Try
                                 Invoke(Sub() MessageBox.Show(New Form With {.TopMost = True}, ex.ToString(), My.Resources.ResText_Warning))
                             Catch
                             End Try
                         End If
                     Finally
                         If Not IsDisposed AndAlso Not Disposing Then
                             Try
                                 Invoke(Sub()
                                            _deviceRefreshInProgress = False
                                            LoadComplete = True
                                            ButtonRefresh.Enabled = True
                                        End Sub)
                             Catch
                             End Try
                         End If
                         Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(LTFSConfigurator))
                             Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Configurator")
                                 Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                                     Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "DeviceRefresh")
                                         Log.Debug("Configurator device refresh finished. Succeeded={Succeeded}.", refreshSucceeded)
                                     End Using
                                 End Using
                             End Using
                         End Using
                     End Try
                 End Sub)
    End Sub

    Private Sub ButtonRefresh_Click(sender As Object, e As EventArgs) Handles ButtonRefresh.Click
        RefreshUI()
    End Sub

    Private Sub LTFSConfigurator_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(LTFSConfigurator))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Configurator")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Lifecycle")
                        Log.Information("LTFS configurator window loading.")
                    End Using
                End Using
            End Using
        End Using
        CheckBoxAutoRefresh.Checked = My.Settings.LTFSConf_AutoRefresh
        ComboBoxBufferPage.SelectedIndex = 16
        ComboBoxLocateType.SelectedIndex = 0
        Text = $"LTFSConfigurator - {ApplicationWheels.ApplicationInfo}"
        LoadCMD()
        LoadComplete = True
        RefreshUI()
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(LTFSConfigurator))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Configurator")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Lifecycle")
                        Log.Information("LTFS configurator window loaded. AutoRefresh={AutoRefresh}.", CheckBoxAutoRefresh.Checked)
                    End Using
                End Using
            End Using
        End Using
    End Sub

    Private Sub Button2_Click(sender As Object, e As EventArgs) Handles ButtonStartFUSESvc.Click
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(LTFSConfigurator))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Configurator")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "ServiceOperation")
                        Log.Information("LTFS service start requested.")
                    End Using
                End Using
            End Using
        End Using
        Dim s As String = TapeUtils.StartLtfsService()
        If s = "" Then s = "OK"
        MessageBox.Show(New Form With {.TopMost = True}, s)
    End Sub

    Private Sub Button3_Click(sender As Object, e As EventArgs) Handles ButtonStopFUSESvc.Click
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(LTFSConfigurator))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Configurator")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "ServiceOperation")
                        Log.Information("LTFS service stop requested.")
                    End Using
                End Using
            End Using
        End Using
        Dim s As String = TapeUtils.StopLtfsService()
        If s = "" Then s = "OK"
        MessageBox.Show(New Form With {.TopMost = True}, s)
    End Sub

    Private Sub Button4_Click(sender As Object, e As EventArgs) Handles ButtonRemount.Click
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(LTFSConfigurator))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Configurator")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "ServiceOperation")
                        Log.Information("Tape drive remount requested.")
                    End Using
                End Using
            End Using
        End Using
        Dim s As String = TapeUtils.RemapTapeDrives()
        If s = "" Then s = "OK"
        MessageBox.Show(New Form With {.TopMost = True}, s)
    End Sub

    Private Sub ListBox1_SelectedIndexChanged(sender As Object, e As EventArgs) Handles ListBox1.SelectedIndexChanged
        If Not LoadComplete Then Exit Sub
        _SelectedIndex = ListBox1.SelectedIndex
        RefreshUI(CheckBoxAutoRefresh.Checked)
    End Sub

    Private Sub Button6_Click(sender As Object, e As EventArgs) Handles ButtonAssign.Click
        If Not LoadComplete Then Exit Sub
        If MessageBox.Show(New Form With {.TopMost = True}, $"{ButtonAssign.Text} {TextBoxDevInfo.Text} <=> {ComboBoxDriveLetter.Text}", My.Resources.ResText_Confirm, MessageBoxButtons.OKCancel) = DialogResult.Cancel Then Exit Sub
        Dim CurDrive As TapeUtils.BlockDevice = GetCurDrive()
        If CurDrive IsNot Nothing Then
            If CurDrive.DriveLetter = "" And ComboBoxDriveLetter.Text <> "" Then
                Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(LTFSConfigurator))
                    Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Configurator")
                        Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                            Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "DeviceMapping")
                                Log.Information("Tape drive mapping requested. DevicePath={DevicePath} DriveLetter={DriveLetter}.", CurDrive.DevicePath, ComboBoxDriveLetter.Text)
                            End Using
                        End Using
                    End Using
                End Using
                Dim result As String = TapeUtils.MapTapeDrive(CChar(ComboBoxDriveLetter.Text), CurDrive.DeviceType & CurDrive.DevIndex)
                If result = "" Then result = CurDrive.DeviceType & CurDrive.DevIndex & " <=> " & ComboBoxDriveLetter.Text & ":"
                result &= vbCrLf
                TextBoxMsg.AppendText(result)
                Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(LTFSConfigurator))
                    Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Configurator")
                        Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                            Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "DeviceMapping")
                                Log.Information("Tape drive mapping completed. DevicePath={DevicePath} DriveLetter={DriveLetter} Result={Result}.", CurDrive.DevicePath, ComboBoxDriveLetter.Text, result.Trim())
                            End Using
                        End Using
                    End Using
                End Using
            End If
        End If
        RefreshUI(False)
    End Sub

    Private Sub Button7_Click(sender As Object, e As EventArgs) Handles ButtonRemove.Click
        If Not LoadComplete Then Exit Sub
        Dim CurDrive As TapeUtils.BlockDevice = GetCurDrive()
        If CurDrive IsNot Nothing Then
            If CurDrive.DriveLetter <> "" Then
                Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(LTFSConfigurator))
                    Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Configurator")
                        Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                            Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "DeviceMapping")
                                Log.Information("Tape drive unmapping requested. DevicePath={DevicePath} DriveLetter={DriveLetter}.", CurDrive.DevicePath, CurDrive.DriveLetter)
                            End Using
                        End Using
                    End Using
                End Using
                Dim result As String = TapeUtils.UnMapTapeDrive(CChar(ComboBoxDriveLetter.Text))
                If result = "" Then result = CurDrive.DeviceType & CurDrive.DevIndex & " <=> ---" & ComboBoxDriveLetter.Text
                result &= vbCrLf
                TextBoxMsg.AppendText(result)
                Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(LTFSConfigurator))
                    Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Configurator")
                        Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                            Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "DeviceMapping")
                                Log.Information("Tape drive unmapping completed. DevicePath={DevicePath} DriveLetter={DriveLetter} Result={Result}.", CurDrive.DevicePath, CurDrive.DriveLetter, result.Trim())
                            End Using
                        End Using
                    End Using
                End Using
            End If
        End If
        RefreshUI(False)
    End Sub

    Private Sub Button8_Click(sender As Object, e As EventArgs) Handles ButtonLoadThreaded.Click
        If Not LoadComplete Then Exit Sub
        Dim path As String = TapeDrive
        Dim CurDrive As TapeUtils.BlockDevice = GetCurDrive(True)
        If CurDrive IsNot Nothing Then
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(LTFSConfigurator))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Configurator")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                        Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "TapeOperation")
                            Log.Information("Threaded tape load requested. TapeDrive={TapeDrive}.", path)
                        End Using
                    End Using
                End Using
            End Using
            Panel1.Enabled = False
            Dim dL As Char = CChar(ComboBoxDriveLetter.Text)
            Dim th As New Threading.Thread(
                Sub()
                    Dim result As String
                    Try
                        result = TapeUtils.LoadEject(path, TapeUtils.LoadOption.LoadThreaded, Nothing, Function(sense As Byte()) As Boolean
                                                                                                           PrintCommandResult({&H1B, 0, 0, 0, TapeUtils.LoadOption.LoadThreaded, 0}, {}, sense)
                                                                                                           Return True
                                                                                                       End Function).ToString()
                        result = result.Replace("True", "").Replace("False", "Failed")
                    Catch ex As Exception
                        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(LTFSConfigurator))
                            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Configurator")
                                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Error")
                                        Log.Error(ex, "Threaded tape load failed. TapeDrive={TapeDrive}.", path)
                                    End Using
                                End Using
                            End Using
                        End Using
                        result = ex.ToString()
                    End Try
                    Invoke(Sub()
                               Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(LTFSConfigurator))
                                   Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Configurator")
                                       Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                                           Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "TapeOperation")
                                               Log.Information("Threaded tape load finished. TapeDrive={TapeDrive} Result={Result}.", path, result.Trim())
                                           End Using
                                       End Using
                                   End Using
                               End Using
                               If result = "" Then result = CurDrive.DeviceType & CurDrive.DevIndex & " loaded"
                               result &= vbCrLf
                               TextBoxMsg.AppendText(result)
                           End Sub)
                    Invoke(Sub()
                               Panel1.Enabled = True
                               RefreshUI(False)
                           End Sub)
                End Sub)
            th.Start()
        End If
        If Panel1.Enabled Then RefreshUI(False)

    End Sub

    Private Sub Button9_Click(sender As Object, e As EventArgs) Handles ButtonEject.Click
        If Not LoadComplete Then Exit Sub
        Dim path As String = TapeDrive
        Dim CurDrive As TapeUtils.BlockDevice = GetCurDrive(True)
        If CurDrive IsNot Nothing Then
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(LTFSConfigurator))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Configurator")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                        Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "TapeOperation")
                            Log.Information("Tape eject requested. TapeDrive={TapeDrive}.", path)
                        End Using
                    End Using
                End Using
            End Using
            Panel1.Enabled = False
            Dim dL As Char = CChar(ComboBoxDriveLetter.Text)
            Dim th As New Threading.Thread(
                Sub()
                    Dim result As String
                    Try
                        result = TapeUtils.LoadEject(path, TapeUtils.LoadOption.Eject, Nothing, Function(sense As Byte()) As Boolean
                                                                                                    PrintCommandResult({&H1B, 0, 0, 0, TapeUtils.LoadOption.Eject, 0}, {}, sense)
                                                                                                    Return True
                                                                                                End Function).ToString()
                        result = result.Replace("True", "").Replace("False", "Failed")
                    Catch ex As Exception
                        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(LTFSConfigurator))
                            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Configurator")
                                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Error")
                                        Log.Error(ex, "Tape eject failed. TapeDrive={TapeDrive}.", path)
                                    End Using
                                End Using
                            End Using
                        End Using
                        result = ex.ToString()
                    End Try
                    Invoke(Sub()
                               Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(LTFSConfigurator))
                                   Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Configurator")
                                       Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                                           Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "TapeOperation")
                                               Log.Information("Tape eject finished. TapeDrive={TapeDrive} Result={Result}.", path, result.Trim())
                                           End Using
                                       End Using
                                   End Using
                               End Using
                               If result = "" Then result = CurDrive.DeviceType & CurDrive.DevIndex & " ejected"
                               result &= vbCrLf
                               TextBoxMsg.AppendText(result)
                               Panel1.Enabled = True
                               RefreshUI(False)
                           End Sub)
                End Sub)
            th.Start()
        End If
        If Panel1.Enabled Then RefreshUI(False)
    End Sub

    Private Sub Button10_Click(sender As Object, e As EventArgs) Handles ButtonMount.Click
        If Not LoadComplete Then Exit Sub
        Dim CurDrive As TapeUtils.BlockDevice = GetCurDrive()
        If CurDrive IsNot Nothing Then
            If CurDrive.DriveLetter <> "" And ComboBoxDriveLetter.Text <> "" Then
                Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(LTFSConfigurator))
                    Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Configurator")
                        Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                            Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "DeviceMapping")
                                Log.Information("Tape drive mount requested. DriveLetter={DriveLetter}.", ComboBoxDriveLetter.Text)
                            End Using
                        End Using
                    End Using
                End Using
                Dim result As String = TapeUtils.MountTapeDrive(CChar(ComboBoxDriveLetter.Text))
                If result = "" Then result = CurDrive.DeviceType & CurDrive.DevIndex & " mounted"
                result &= vbCrLf
                TextBoxMsg.AppendText(result)
                Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(LTFSConfigurator))
                    Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Configurator")
                        Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                            Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "DeviceMapping")
                                Log.Information("Tape drive mount completed. DriveLetter={DriveLetter} Result={Result}.", ComboBoxDriveLetter.Text, result.Trim())
                            End Using
                        End Using
                    End Using
                End Using
            End If
        End If
        RefreshUI(False)
    End Sub

    Private Sub CheckBox1_CheckedChanged(sender As Object, e As EventArgs) Handles CheckBoxDebugPanel.CheckedChanged
        Panel2.Visible = CheckBoxDebugPanel.Checked
        ButtonLTFSWriter.Enabled = True
    End Sub


    Private Sub ButtonDebugSendSCSICommand_Click(sender As Object, e As EventArgs) Handles ButtonDebugSendSCSICommand.Click
        Dim th As New Threading.Thread(
            Sub()
                Try
                    Dim cdb() As Byte = IOManager.HexStringToByteArray(TextBoxCDBData.Text)
                    Dim param() As Byte = {}
                    If TextBoxParamData.Text.Length >= 2 Then
                        param = IOManager.HexStringToByteArray(TextBoxParamData.Text)
                    End If

                    Dim sense(63) As Byte
                    Dim succ As Boolean, BytesReturned As UInteger
                    SyncLock TapeUtils.GetSCSIOperationLock(ConfTapeDrive)
                        Dim handle As IntPtr
                        If Not TapeUtils.OpenTapeDrive(ConfTapeDrive, handle) Then
                            Throw New Win32Exception(TapeUtils.LastWin32Error, $"Unable to open {ConfTapeDrive}.")
                        End If
                        Try
                            If TapeUtils.DriverTypeSetting = TapeUtils.DriverType.TapeStream Then
                                Dim ts As TapeImage = Nothing
                                TapeStreamMapping.MappingTable.TryGetValue(handle, ts)
                                If ts IsNot Nothing Then
                                    succ = ts.HandleSCSICommand(cdb, param, CByte(CInt(TextBoxDataDir.Text)), param.Length, param, sense)
                                End If
                            Else
                                Dim dataBufferPtr As IntPtr = IntPtr.Zero
                                Try
                                    If param.Length > 0 Then
                                        dataBufferPtr = Marshal.AllocHGlobal(param.Length)
                                        Marshal.Copy(param, 0, dataBufferPtr, param.Length)
                                    End If
                                    succ = TapeUtils.IOCtlDirect(handle, cdb, dataBufferPtr, CUInt(param.Length),
                                                           CByte(CInt(TextBoxDataDir.Text)), CUInt(CInt(TextBoxTimeoutValue.Text)), sense,
                                                           CByte(TextBoxTargetID.Text), CByte(TextBoxLUN.Text), BytesReturned)
                                    If param.Length > 0 Then Marshal.Copy(dataBufferPtr, param, 0, param.Length)
                                Finally
                                    If dataBufferPtr <> IntPtr.Zero Then Marshal.FreeHGlobal(dataBufferPtr)
                                End Try
                            End If
                        Finally
                            TapeUtils.CloseTapeDrive(handle)
                        End Try
                    End SyncLock
                    Invoke(Sub()
                               PrintCommandResult(cdb, param, sense)
                           End Sub)
                    If succ Then
                        Invoke(Sub() TextBoxDebugOutput.Text &= $"{vbCrLf}OK{vbCrLf}Bytesreturned = {BytesReturned}")
                    Else
                        Dim ErrCode As Integer = TapeUtils.LastWin32Error
                        Dim win32ex As New Win32Exception(ErrCode)
                        Invoke(Sub() TextBoxDebugOutput.Text &= $"{vbCrLf}FAIL{vbCrLf}ErrCode: 0x{ErrCode.ToString("X8")}h{vbCrLf}{win32ex.Message}")
                    End If
                Catch ex As Exception
                    MessageBox.Show(New Form With {.TopMost = True}, ex.ToString)
                End Try
                Invoke(Sub() Panel2.Enabled = True)
            End Sub)
        Panel2.Enabled = False
        th.Start()

    End Sub

    Private Sub Button13_Click(sender As Object, e As EventArgs) Handles ButtonLoadUnthreaded.Click
        If Not LoadComplete Then Exit Sub
        Dim path As String = TapeDrive
        Dim CurDrive As TapeUtils.BlockDevice = GetCurDrive(True)
        If CurDrive IsNot Nothing Then
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(LTFSConfigurator))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Configurator")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                        Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "TapeOperation")
                            Log.Information("Unthreaded tape load requested. TapeDrive={TapeDrive}.", path)
                        End Using
                    End Using
                End Using
            End Using
            Panel1.Enabled = False
            Dim dL As Char = CChar(ComboBoxDriveLetter.Text)
            Dim th As New Threading.Thread(
                Sub()
                    Dim result As String
                    Try
                        result = TapeUtils.LoadEject(path, TapeUtils.LoadOption.LoadUnthreaded, Nothing, Function(sense As Byte()) As Boolean
                                                                                                             PrintCommandResult({&H1B, 0, 0, 0, TapeUtils.LoadOption.LoadUnthreaded, 0}, {}, sense)
                                                                                                             Return True
                                                                                                         End Function).ToString()
                        result = result.Replace("True", "").Replace("False", "Failed")
                    Catch ex As Exception
                        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(LTFSConfigurator))
                            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Configurator")
                                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Error")
                                        Log.Error(ex, "Unthreaded tape load failed. TapeDrive={TapeDrive}.", path)
                                    End Using
                                End Using
                            End Using
                        End Using
                        result = ex.ToString()
                    End Try
                    Invoke(Sub()
                               Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(LTFSConfigurator))
                                   Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Configurator")
                                       Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                                           Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "TapeOperation")
                                               Log.Information("Unthreaded tape load finished. TapeDrive={TapeDrive} Result={Result}.", path, result.Trim())
                                           End Using
                                       End Using
                                   End Using
                               End Using
                               If result = "" Then result = CurDrive.DeviceType & CurDrive.DevIndex & " loaded (unthread)"
                               result &= vbCrLf
                               TextBoxMsg.AppendText(result)
                           End Sub)
                    Invoke(Sub()
                               Panel1.Enabled = True
                               RefreshUI(False)
                           End Sub)
                End Sub)
            th.Start()
        End If
        If Panel1.Enabled Then RefreshUI(False)
    End Sub

    Private Sub Button14_Click(sender As Object, e As EventArgs) Handles ButtonUnthread.Click
        If Not LoadComplete Then Exit Sub
        Dim path As String = TapeDrive
        Dim CurDrive As TapeUtils.BlockDevice = GetCurDrive(True)
        If CurDrive IsNot Nothing Then
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(LTFSConfigurator))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Configurator")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                        Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "TapeOperation")
                            Log.Information("Tape unthread requested. TapeDrive={TapeDrive}.", path)
                        End Using
                    End Using
                End Using
            End Using
            Panel1.Enabled = False
            Dim dL As Char = CChar(ComboBoxDriveLetter.Text)
            Dim th As New Threading.Thread(
                Sub()
                    Dim result As String
                    Try
                        result = TapeUtils.LoadEject(path, TapeUtils.LoadOption.Unthread, Nothing, Function(sense As Byte()) As Boolean
                                                                                                       PrintCommandResult({&H1B, 0, 0, 0, TapeUtils.LoadOption.Unthread, 0}, {}, sense)
                                                                                                       Return True
                                                                                                   End Function).ToString()
                        result = result.Replace("True", "").Replace("False", "Failed")
                    Catch ex As Exception
                        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(LTFSConfigurator))
                            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Configurator")
                                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Error")
                                        Log.Error(ex, "Tape unthread failed. TapeDrive={TapeDrive}.", path)
                                    End Using
                                End Using
                            End Using
                        End Using
                        result = ex.ToString()
                    End Try
                    Invoke(Sub()
                               Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(LTFSConfigurator))
                                   Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Configurator")
                                       Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                                           Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "TapeOperation")
                                               Log.Information("Tape unthread finished. TapeDrive={TapeDrive} Result={Result}.", path, result.Trim())
                                           End Using
                                       End Using
                                   End Using
                               End Using
                               If result = "" Then result = CurDrive.DeviceType & CurDrive.DevIndex & " unthreaded"
                               result &= vbCrLf
                               TextBoxMsg.AppendText(result)
                           End Sub)
                    Invoke(Sub()
                               Panel1.Enabled = True
                               RefreshUI(False)
                           End Sub)
                End Sub)
            th.Start()
        End If
        If Panel1.Enabled Then RefreshUI(False)
    End Sub

    Private Sub ButtonDebugErase_Click(sender As Object, e As EventArgs) Handles ButtonDebugErase.Click
        If Not LoadComplete Then Exit Sub
        If MessageBox.Show(New Form With {.TopMost = True}, "Data will be cleared on this tape. Continue?", "Warning", MessageBoxButtons.OKCancel) = DialogResult.Cancel Then Exit Sub
        Panel1.Enabled = False
        Dim dL As Char = CChar(ComboBoxDriveLetter.Text)
        Dim th As New Threading.Thread(
                Sub()
                    Invoke(Sub() TextBoxDebugOutput.Text = "Start erase ..." & vbCrLf)
                    Try
                        Select Case My.Settings.TapeUtils_DriverType
                            Case TapeUtils.DriverType.LTO
                                'result = TapeUtils.LoadTapeDrive(dL, True)
                                'Load and Thread
                                Invoke(Sub() TextBoxDebugOutput.AppendText("Loading.."))
                                If TapeUtils.SendSCSICommand(ConfTapeDrive, {&H1B, 0, 0, 0, 1, 0}) Then
                                    Invoke(Sub() TextBoxDebugOutput.AppendText("     OK" & vbCrLf))
                                Else
                                    Invoke(Sub() TextBoxDebugOutput.AppendText("     Fail" & vbCrLf))
                                    Exit Try
                                End If

                                'Mode Sense
                                Invoke(Sub() TextBoxDebugOutput.AppendText("MODE SENSE"))
                                Dim ModeData As Byte()
                                ModeData = TapeUtils.ModeSense(TapeDrive, &H11)
                                Invoke(Sub() TextBoxDebugOutput.AppendText($"     Mode Data: {IOManager.Byte2Hex(ModeData)}{vbCrLf}"))
                                ReDim Preserve ModeData(11)
                                'Mode Select:1st Partition to Minimum 
                                Invoke(Sub() TextBoxDebugOutput.AppendText("MODE SELECT - Partition mode page.."))
                                If TapeUtils.SendSCSICommand(ConfTapeDrive, {&H15, &H10, 0, 0, &H10, 0}, {0, 0, &H10, 0, &H11, &HA, ModeData(2), 1, ModeData(4), ModeData(5), ModeData(6), ModeData(7), 0, 1, &HFF, &HFF}, 0) Then
                                    Invoke(Sub() TextBoxDebugOutput.AppendText("     OK" & vbCrLf))
                                Else
                                    Invoke(Sub() TextBoxDebugOutput.AppendText("     Fail" & vbCrLf))
                                    Exit Try
                                End If

                                'Format
                                Invoke(Sub() TextBoxDebugOutput.AppendText("Partitioning.."))
                                If TapeUtils.SendSCSICommand(ConfTapeDrive, {4, 0, 1, 0, 0, 0}, Nothing, 0) Then
                                    Invoke(Sub() TextBoxDebugOutput.AppendText("     OK" & vbCrLf))
                                Else
                                    Invoke(Sub() TextBoxDebugOutput.AppendText("     Fail" & vbCrLf))
                                    Exit Try
                                End If
                                For i As Integer = 1 To CInt(NumericUpDownEraseCycle.Value)
                                    Dim currentCycle As Integer = i
                                    'Unthread
                                    Invoke(Sub() TextBoxDebugOutput.AppendText("Unthreading.."))
                                    If TapeUtils.SendSCSICommand(ConfTapeDrive, {&H1B, 0, 0, 0, &HA, 0}) Then
                                        Invoke(Sub() TextBoxDebugOutput.AppendText("     OK" & vbCrLf))
                                    Else
                                        Invoke(Sub() TextBoxDebugOutput.AppendText("     Fail" & vbCrLf))
                                        Exit Try
                                    End If
                                    'Thread
                                    Invoke(Sub() TextBoxDebugOutput.AppendText("Threading.."))
                                    If TapeUtils.SendSCSICommand(ConfTapeDrive, {&H1B, 0, 0, 0, 1, 0}) Then
                                        Invoke(Sub() TextBoxDebugOutput.AppendText("     OK" & vbCrLf))
                                    Else
                                        Invoke(Sub() TextBoxDebugOutput.AppendText("     Fail" & vbCrLf))
                                        Exit Try
                                    End If
                                    'Erase
                                    Invoke(Sub() TextBoxDebugOutput.AppendText("Erasing " & currentCycle & "/" & NumericUpDownEraseCycle.Value & ".."))
                                    If TapeUtils.SendSCSICommand(ConfTapeDrive, {&H19, 1, 0, 0, 0, 0}, TimeOut:=320) Then
                                        Invoke(Sub() TextBoxDebugOutput.AppendText("     OK" & vbCrLf))
                                    Else
                                        Invoke(Sub() TextBoxDebugOutput.AppendText("     Fail" & vbCrLf))
                                        Exit Try
                                    End If
                                Next
                                'Unthread
                                Invoke(Sub() TextBoxDebugOutput.AppendText("Unthreading.."))
                                If TapeUtils.SendSCSICommand(ConfTapeDrive, {&H1B, 0, 0, 0, &HA, 0}) Then
                                    Invoke(Sub() TextBoxDebugOutput.AppendText("     OK" & vbCrLf))
                                Else
                                    Invoke(Sub() TextBoxDebugOutput.AppendText("     Fail" & vbCrLf))
                                    Exit Try
                                End If
                                'Thread
                                Invoke(Sub() TextBoxDebugOutput.AppendText("Threading.."))
                                If TapeUtils.SendSCSICommand(ConfTapeDrive, {&H1B, 0, 0, 0, 1, 0}) Then
                                    Invoke(Sub() TextBoxDebugOutput.AppendText("     OK" & vbCrLf))
                                Else
                                    Invoke(Sub() TextBoxDebugOutput.AppendText("     Fail" & vbCrLf))
                                    Exit Try
                                End If
                                'Remove Partition
                                Invoke(Sub() TextBoxDebugOutput.AppendText("Reinitializing.."))
                                If TapeUtils.SendSCSICommand(ConfTapeDrive, {4, 0, 0, 0, 0, 0}) Then
                                    Invoke(Sub() TextBoxDebugOutput.AppendText("     OK" & vbCrLf))
                                Else
                                    Invoke(Sub() TextBoxDebugOutput.AppendText("     Fail" & vbCrLf))
                                    Exit Try
                                End If
                                'Unload
                                Invoke(Sub() TextBoxDebugOutput.AppendText("Unloading.."))
                                If TapeUtils.SendSCSICommand(ConfTapeDrive, {&H1B, 0, 0, 0, 0, 0}) Then
                                    Invoke(Sub() TextBoxDebugOutput.AppendText("     OK" & vbCrLf))
                                Else
                                    Invoke(Sub() TextBoxDebugOutput.AppendText("     Fail" & vbCrLf))
                                    Exit Try
                                End If

                            Case TapeUtils.DriverType.SLR3
                                Invoke(Sub() TextBoxDebugOutput.AppendText("Erasing.."))
                                If TapeUtils.SendSCSICommand(ConfTapeDrive, {&H19, 1, 0, 0, 0, 0}, TimeOut:=240) Then
                                    Invoke(Sub() TextBoxDebugOutput.AppendText("     OK" & vbCrLf))
                                Else
                                    Invoke(Sub() TextBoxDebugOutput.AppendText("     Fail" & vbCrLf))
                                    Exit Try
                                End If
                                Invoke(Sub() TextBoxDebugOutput.AppendText("Reloading.."))
                                If TapeUtils.SendSCSICommand(ConfTapeDrive, {&H1B, 0, 0, 0, 1, 0}) Then
                                    Invoke(Sub() TextBoxDebugOutput.AppendText("     OK" & vbCrLf))
                                Else
                                    Invoke(Sub() TextBoxDebugOutput.AppendText("     Fail" & vbCrLf))
                                    Exit Try
                                End If
                            Case TapeUtils.DriverType.SLR1
                                Invoke(Sub() TextBoxDebugOutput.AppendText("Erasing.."))
                                If TapeUtils.SendSCSICommand(ConfTapeDrive, {&H19, 1, 0, 0, 0, 0}, TimeOut:=240) Then
                                    Invoke(Sub() TextBoxDebugOutput.AppendText("     OK" & vbCrLf))
                                Else
                                    Invoke(Sub() TextBoxDebugOutput.AppendText("     Fail" & vbCrLf))
                                    Exit Try
                                End If
                                Invoke(Sub() TextBoxDebugOutput.AppendText("Reloading.."))
                                If TapeUtils.SendSCSICommand(ConfTapeDrive, {&H1B, 0, 0, 0, 1, 0}) Then
                                    Invoke(Sub() TextBoxDebugOutput.AppendText("     OK" & vbCrLf))
                                Else
                                    Invoke(Sub() TextBoxDebugOutput.AppendText("     Fail" & vbCrLf))
                                    Exit Try
                                End If
                        End Select
                    Catch ex As Exception
                        Invoke(Sub() TextBoxDebugOutput.AppendText(ex.ToString()))
                    End Try
                    Invoke(Sub() TextBoxDebugOutput.AppendText("Erase finished."))
                    Invoke(Sub()
                               Panel1.Enabled = True
                               RefreshUI(False)
                           End Sub)
                End Sub)
        th.Start()
        If Panel1.Enabled Then RefreshUI(False)
    End Sub

    Private Sub ButtonDebugWriteBarcode_Click(sender As Object, e As EventArgs) Handles ButtonDebugWriteBarcode.Click
        If Not LoadComplete Then Exit Sub
        Panel1.Enabled = False
        Dim barcode As String = TextBoxBarcode.Text
        Dim th As New Threading.Thread(
                New Threading.ThreadStart(
                Sub()
                    Dim result As String = ""
                    Dim sense(63) As Byte
                    Try
                        result &= TapeUtils.SetBarcode(ConfTapeDrive, barcode, Function(senseData As Byte()) As Boolean
                                                                                   sense = senseData
                                                                                   Return True
                                                                               End Function)
                        result = result.Replace("True", "").Replace("False", "Failed")

                    Catch ex As Exception
                        result = ex.ToString()
                    End Try
                    Invoke(Sub()
                               If result = "" Then result = ConfTapeDrive & " Barcode = " & barcode
                               result &= vbCrLf & vbCrLf & "SenseBuffer" & vbCrLf
                               result &= IOManager.Byte2Hex(sense) & vbCrLf
                               result &= TapeUtils.ParseSenseData(sense) & vbCrLf
                               TextBoxDebugOutput.Text = result
                           End Sub)
                    Invoke(Sub()
                               Panel1.Enabled = True
                               RefreshUI(False)
                           End Sub)
                End Sub))
        th.Start()
        If Panel1.Enabled Then RefreshUI(False)
    End Sub

    Private Sub Label11_Click(sender As Object, e As EventArgs)
    End Sub

    Private Sub ButtonDebugReadMAM_Click(sender As Object, e As EventArgs) Handles ButtonDebugReadMAM.Click
        If Not LoadComplete Then Exit Sub
        Panel1.Enabled = False
        Dim PCH As Byte = CByte(NumericUpDownPCHigh.Value)
        Dim PCL As Byte = CByte(NumericUpDownPCLow.Value)
        Dim PN As Byte = CByte(NumericUpDownRAPartition.Value)
        Task.Run(Sub()
                     Dim ResultB As Byte() = TapeUtils.GetMAMAttributeBytes(ConfTapeDrive, PCH, PCL, PN)
                     Dim Result As String = ""
                     If ResultB.Length = 0 Then

                     Else
                         Result = Encoding.UTF8.GetString(ResultB)
                     End If

                     Invoke(Sub()
                                TextBoxDebugOutput.Text = ("Result: " & vbCrLf & Result & vbCrLf & vbCrLf)
                                TextBoxDebugOutput.AppendText(IOManager.Byte2Hex(ResultB, True))
                                Panel1.Enabled = True
                            End Sub)
                 End Sub)


    End Sub

    Private Sub Label13_Click(sender As Object, e As EventArgs) Handles LabelMAMAttrib.Click
        MessageBox.Show(New Form With {.TopMost = True}, "/* Page code of Application Name */
#define TC_MAM_PAGE_APP_NAME       (0x0801) 
#define TC_MAM_PAGE_APP_NAME_SIZE  (0x20)

/* Page code for Application Vendor */
#define TC_MAM_APPLICATION_VENDOR    (0x0800) 
#define TC_MAM_APPLICATION_VENDOR_LEN  8

/* Page code for Application Name */
#define TC_MAM_APPLICATION_NAME      0x0801 
#define TC_MAM_APPLICATION_NAME_LEN    32

/* Page code for Application Version */
#define TC_MAM_APPLICATION_VERSION   0x0802 
#define TC_MAM_APPLICATION_VERSION_LEN 8

/* Page code for Format Version */
#define TC_MAM_APP_FORMAT_VERSION    0x080B 
#define TC_MAM_APP_FORMAT_VERSION_LEN 16

/* Page code for Custom Volume Name */
#define TC_MAM_USR_MED_TXT_LABEL     0x0803 
#define TC_MAM_USR_MED_TXT_LABEL_LEN 160

/* Page code for Custom Barcode Name */
#define TC_MAM_BARCODE     			 0x0806 
#define TC_MAM_BARCODE_LEN 			 32

/* Page code for Volume Lock State */
#define TC_MAM_VOL_LOCK_STATE		 0x1623 
#define TC_MAM_VOL_LOCK_STATE_LEN	 1
")
    End Sub
    Public Function ReduceDataUnit(MBytes As Int64) As String
        Dim sym As String = If(MBytes >= 0, "", "-")
        MBytes = Math.Abs(MBytes)
        Dim Result As Decimal = MBytes
        Dim ResultUnit As Integer = 0
        If My.Settings.Application_UseDecimalUnit Then
            While Result >= 1000
                Result /= 1000
                ResultUnit += 3
            End While
            Dim ResultString As String = sym & Math.Round(Result, 2)
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
            While Result >= 1000
                Result /= 1024
                ResultUnit += 3
            End While
            Dim ResultString As String = sym & Math.Round(Result, 2)
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
    Private Sub ButtonDebugReadInfo_Click(sender As Object, e As EventArgs) Handles ButtonDebugReadInfo.Click
        Enabled = False
        Select Case TapeUtils.DriverTypeSetting
            Case TapeUtils.DriverType.LTO, TapeUtils.DriverType.IBM3592
                Dim CMInfo As TapeUtils.CMParser = Nothing
                TextBoxDebugOutput.Text = ""
                Task.Run(Sub()
                             Try
                                 CMInfo = New TapeUtils.CMParser(ConfTapeDrive)
                             Catch ex As Exception
                                 Invoke(Sub() TextBoxDebugOutput.AppendText("CM Data Parsing Failed." & vbCrLf & ex.ToString & vbCrLf))
                             End Try
                             Invoke(Sub()
                                        Try
                                            TextBoxDebugOutput.AppendText(CMInfo.GetReport())
                                        Catch ex As Exception
                                            TextBoxDebugOutput.AppendText("Report generation failed.".PadRight(74) & vbCrLf & ex.ToString & vbCrLf)
                                        End Try
                                        Try
                                            If CheckBoxParseCMData.Checked AndAlso CMInfo IsNot Nothing Then
                                                TextBoxDebugOutput.AppendText(CMInfo.GetSerializedText())
                                                Dim PG1 As New SettingPanel
                                                PG1.SelectedObject = CMInfo
                                                PG1.Text = CMInfo.CartridgeMfgData.CartridgeSN
                                                PG1.Show()
                                                TextBoxDebugOutput.AppendText(vbCrLf)
                                            End If
                                        Catch ex As Exception
                                            TextBoxDebugOutput.AppendText("CM Data Parsing failed.".PadRight(74) & vbCrLf & ex.ToString & vbCrLf)
                                        End Try
                                        TextBoxDebugOutput.Select(0, 0)
                                        TextBoxDebugOutput.ScrollToCaret()
                                        If Directory.Exists(My.Application.Info.DirectoryPath & "\Info") Then
                                            Dim fn As String
                                            Try
                                                fn = CMInfo.ApplicationSpecificData.Barcode
                                                If fn Is Nothing OrElse fn.Length = 0 Then fn = CMInfo.CartridgeMfgData.CartridgeSN
                                                If fn Is Nothing Then fn = ""
                                                File.WriteAllText($"{My.Application.Info.DirectoryPath}\Info\{fn}.txt", TextBoxDebugOutput.Text)
                                            Catch ex As Exception

                                            End Try
                                        End If
                                        Enabled = True
                                    End Sub)
                         End Sub)
            Case TapeUtils.DriverType.SLR3
                TextBoxDebugOutput.Text = ""
                Task.Run(Sub()
                             Invoke(Sub() TextBoxDebugOutput.AppendText("SLR Tape is NOT supported with ReadInfo function.".PadRight(74) & vbCrLf))
                         End Sub)
                Enabled = True
        End Select

    End Sub

    Private Sub ButtonDebugDumpMAM_Click(sender As Object, e As EventArgs) Handles ButtonDebugDumpMAM.Click
        If SaveFileDialog1.ShowDialog = DialogResult.OK Then
            ButtonDebugDumpMAM.Enabled = False

            Dim th As New Threading.Thread(
                Sub()
                    Dim MAMData As New TapeUtils.MAMAttributeList
                    For i As UInt16 = &H0 To &HFFFF Step 1

                        Try
                            Dim Attr As TapeUtils.MAMAttribute = TapeUtils.MAMAttribute.FromTapeDrive(ConfTapeDrive, i, CByte(NumericUpDownPartitionNum.Value))
                            If Attr IsNot Nothing Then
                                Invoke(Sub()
                                           TextBoxDebugOutput.Text = IOManager.Byte2Hex({Attr.ID_MSB, Attr.ID_LSB}) & " LEN=" & Attr.RawData.Length & vbCrLf & vbCrLf
                                           TextBoxDebugOutput.AppendText(Attr.AsNumeric & vbCrLf & vbCrLf)
                                           TextBoxDebugOutput.AppendText(Attr.AsString & vbCrLf & vbCrLf)
                                           TextBoxDebugOutput.AppendText(IOManager.Byte2Hex(Attr.RawData) & vbCrLf)
                                       End Sub)
                                MAMData.Content.Add(Attr)
                            Else
                                If (i And &H7F) = 0 Then
                                    Dim i2 As UInt16 = i
                                    Invoke(Sub()
                                               TextBoxDebugOutput.Text = IOManager.Byte2Hex({CByte(i2 >> 8 And &HFF), CByte(i2 And &HFF)}) & " LEN=0"
                                           End Sub)
                                End If

                            End If
                        Catch ex As Exception
                            MessageBox.Show(New Form With {.TopMost = True}, i & vbCrLf & ex.ToString)
                        End Try
                        If i = &HFFFF Then Exit For
                    Next
                    MessageBox.Show(New Form With {.TopMost = True}, "Dump Complete")
                    MAMData.SaveSerializedText(SaveFileDialog1.FileName)
                    Invoke(Sub() ButtonDebugDumpMAM.Enabled = True)
                End Sub)
            th.Start()
        End If
    End Sub

    Private Sub ButtonDebugRewind_Click(sender As Object, e As EventArgs) Handles ButtonDebugRewind.Click
        Enabled = False
        Task.Run(Sub()
                     Dim senseData(63) As Byte
                     Dim cdb As Byte() = {1, 0, 0, 0, 0, 0}
                     If TapeUtils.DriverTypeSetting = TapeUtils.DriverType.TapeStream Then
                         TapeUtils.Locate(ConfTapeDrive, 0, 0, TapeUtils.LocateDestType.Block, senseData)
                     Else
                         Dim data As IntPtr = Marshal.AllocHGlobal(1)
                         Dim handle As IntPtr
                         SyncLock TapeUtils.GetSCSIOperationLock(ConfTapeDrive)
                             TapeUtils.OpenTapeDrive(ConfTapeDrive, handle)
                             TapeUtils.TapeSCSIIOCtlUnmanaged(handle, cdb, data, 0, 1, 60000, senseData)
                             TapeUtils.CloseTapeDrive(handle)
                         End SyncLock
                         Marshal.FreeHGlobal(data)
                     End If

                     PrintCommandResult(cdb, Nothing, senseData)
                     Invoke(Sub() Enabled = True)
                 End Sub)

    End Sub

    Private Sub ButtonDebugReadBlock_Click(sender As Object, e As EventArgs) Handles ButtonDebugReadBlock.Click
        Enabled = False
        Dim ReadLen As UInteger = CUInt(NumericUpDownBlockLen.Value)
        Task.Run(Sub()
                     Dim sense(63) As Byte
                     Dim readData As Byte()
                     Try
                         readData = TapeUtils.ReadBlock(ConfTapeDrive, sense, ReadLen)
                         Invoke(Sub()
                                    Dim DiffBytes As Int32
                                    For i As Integer = 3 To 6
                                        DiffBytes <<= 8
                                        DiffBytes = DiffBytes Or sense(i)
                                    Next
                                    Dim Add_Key As UInt16 = CUShort(CInt(sense(12)) << 8 Or sense(13))
                                    TextBoxDebugOutput.Text = TapeUtils.ParseAdditionalSenseCode(Add_Key) & vbCrLf & vbCrLf & "Raw data:" & vbCrLf
                                    TextBoxDebugOutput.Text &= "Length: " & readData.Length & vbCrLf
                                    If DiffBytes < 0 Then
                                        TextBoxDebugOutput.Text &= TapeUtils.ParseSenseData(sense) & vbCrLf
                                        TextBoxDebugOutput.Text &= "Excess data is discarded. Block length should be " & readData.Length - DiffBytes & vbCrLf & vbCrLf
                                    End If
                                    TextBoxDebugOutput.Text &= IOManager.Byte2Hex(readData, True) & vbCrLf
                                    TextBoxDebugOutput.Text &= TapeUtils.ParseSenseData(sense) & vbCrLf
                                    TextBoxDebugOutput.Text &= IOManager.Byte2Hex(sense, True) & vbCrLf

                                End Sub)
                     Catch ex As Exception

                     End Try

                     Invoke(Sub()
                                Enabled = True
                            End Sub)
                 End Sub)
    End Sub

    Private Sub ButtonDebugReadBuffer_Click(sender As Object, e As EventArgs) Handles ButtonDebugReadBuffer.Click
        Enabled = False
        Dim selectedBufferPage As String = Convert.ToString(ComboBoxBufferPage.SelectedItem)
        Dim BufferID As Byte = Convert.ToByte(selectedBufferPage.Substring(0, 2), 16)
        Dim Mode As Byte = CByte(NumericUpDownRBMode.Value)
        Task.Run(Sub()
                     Dim DumpData As Byte() = TapeUtils.ReadBuffer(ConfTapeDrive, BufferID, Mode)
                     Invoke(Sub()
                                TextBoxDebugOutput.Text = "Buffer len=" & DumpData.Length & vbCrLf
                                SaveFileDialog2.FileName = selectedBufferPage & ".bin"
                                If SaveFileDialog2.ShowDialog = DialogResult.OK Then
                                    File.WriteAllBytes(SaveFileDialog2.FileName, DumpData)
                                End If
                                TextBoxDebugOutput.AppendText(IOManager.Byte2Hex(DumpData, True))
                                Enabled = True
                            End Sub)
                 End Sub)
    End Sub

    Private Sub Label9_Click(sender As Object, e As EventArgs) Handles LabelParam.Click
        Dim bNum As Integer = 0
        If DisplayHelper.ShowInputDialog("Byte count?", "cdb SetBytes", bNum) <> DialogResult.OK Then Exit Sub
        If bNum > 0 Then
            TextBoxParamData.Text = ""
            Dim sb As New StringBuilder
            For i As Integer = 1 To bNum
                sb.Append("00 ")
            Next
            TextBoxParamData.Text = sb.ToString()
        End If
    End Sub

    Private Sub ButtonDebugLocate_Click(sender As Object, e As EventArgs) Handles ButtonDebugLocate.Click
        Enabled = False
        Dim blk As ULong = CULng(NumericUpDownBlockNum.Value)
        Dim partition As Byte = CByte(NumericUpDownPartitionNum.Value)
        Dim timeout As Integer = Integer.Parse(TextBoxTimeoutValue.Text)
        Dim dest As TapeUtils.LocateDestType = CType(System.Enum.Parse(GetType(TapeUtils.LocateDestType), CStr(ComboBoxLocateType.SelectedItem)), TapeUtils.LocateDestType)
        Task.Run(Sub()
                     Dim result As String = ""
                     Try
                         TapeUtils.AllowPartition = Not My.Settings.LTFSWriter_DisablePartition
                         Dim sense As Byte() = {}
                         Dim handle As IntPtr
                         If Not TapeUtils.OpenTapeDrive(ConfTapeDrive, handle) Then Throw New Exception($"Cannot open {ConfTapeDrive}")
                         TapeUtils.Locate(handle, blk, partition, dest, sense, timeout)
                         If TapeUtils.DriverTypeSetting = TapeUtils.DriverType.TapeStream Then
                             Dim ts As TapeImage = Nothing
                             TapeStreamMapping.MappingTable.TryGetValue(handle, ts)
                             If ts IsNot Nothing Then
                                 result = $"Dataset={ts.CurrentDatasetID}
FileOffset={ts.CurrentFileOffset} 0x{ts.CurrentFileOffset.ToString("X")}h
IntrasetOffset = {ts.CurrentIntraSetBlockOffset} 0x{ts.CurrentIntraSetBlockOffset.ToString("X")}h
DatasetResidue = {ts.CurrentSetResidueBytes}{vbCrLf}"
                             End If
                         End If
                         If Not TapeUtils.CloseTapeDrive(handle) Then Throw New Exception($"Cannot close {ConfTapeDrive}")

                         result &= TapeUtils.ParseSenseData(sense) & vbCrLf & IOManager.Byte2Hex(sense, True)
                     Catch ex As Exception

                     End Try

                     Invoke(Sub()
                                TextBoxDebugOutput.Text = result
                                Enabled = True
                            End Sub)
                 End Sub)



    End Sub

    Private Sub ButtonDebugReadPosition_Click(sender As Object, e As EventArgs) Handles ButtonDebugReadPosition.Click
        Panel1.Enabled = False
        Task.Run(Sub()
                     Try
                         TapeUtils.AllowPartition = Not My.Settings.LTFSWriter_DisablePartition
                         Dim pos As New TapeUtils.PositionData(ConfTapeDrive)
                         Invoke(Sub()
                                    TextBoxDebugOutput.Text = ""
                                    TextBoxDebugOutput.Text &= "Partition " & pos.PartitionNumber & vbCrLf
                                    TextBoxDebugOutput.Text &= "Block " & pos.BlockNumber & vbCrLf
                                    TextBoxDebugOutput.Text &= "FileMark " & pos.FileNumber & vbCrLf
                                    TextBoxDebugOutput.Text &= "Set " & pos.SetNumber & vbCrLf
                                    TextBoxDebugOutput.Text &= vbCrLf
                                    If pos.BOP Then TextBoxDebugOutput.Text &= "BOM - Beginning of media" & vbCrLf
                                    If pos.EOP Then TextBoxDebugOutput.Text &= "EW-EOM - Early warning" & vbCrLf
                                    If pos.EOD Then TextBoxDebugOutput.Text &= "End of Data detected" & vbCrLf
                                    Panel1.Enabled = True
                                End Sub)
                     Catch ex As Exception
                         Invoke(Sub()
                                    TextBoxDebugOutput.Text = ex.ToString()
                                    Panel1.Enabled = True
                                End Sub)
                     End Try

                 End Sub)
    End Sub
    Public Operation_Cancel_Flag As Boolean = False
    Private Sub ButtonDebugDumpTape_Click(sender As Object, e As EventArgs) Handles ButtonDebugDumpTape.Click
        Dim outputDirectory As String = SelectFolder()
        If String.IsNullOrEmpty(outputDirectory) Then Exit Sub
        If New DirectoryInfo(outputDirectory).GetFiles("*.bin", SearchOption.TopDirectoryOnly).Length > 0 Then
            MessageBox.Show(New Form With {.TopMost = True}, "File exist: *.bin; Cancelled.")
            Exit Sub
        End If

        For Each c As Control In Panel1.Controls
            c.Enabled = False
        Next
        Panel2.Enabled = True
        For Each c As Control In Panel2.Controls
            c.Enabled = False
        Next
        For Each c As Control In TabControl1.Controls
            c.Enabled = False
        Next
        For Each c As Control In TabPageCommand.Controls
            c.Enabled = False
        Next
        TabControl1.Enabled = True
        TabPageCommand.Enabled = True
        ButtonStopRawDump.Enabled = True
        TextBoxDebugOutput.Text = ""
        Dim log As Boolean = CheckBoxEnableDumpLog.Checked
        Dim thprog As New Threading.Thread(
            Sub()
                Dim ReadLen As UInteger = CUInt(NumericUpDownBlockLen.Value)
                Dim FileNum As Integer = 0

                'Position
                Dim pos As New TapeUtils.PositionData(ConfTapeDrive)

                Dim Partition As Byte = pos.PartitionNumber
                Dim Block As UInteger = CUInt(pos.BlockNumber)
                Dim BlkNum As Integer = CInt(Block)
                While True
                    Dim sense(63) As Byte
                    Dim readData As Byte() = TapeUtils.ReadBlock(ConfTapeDrive, sense, ReadLen)
                    Dim Add_Key As UInt16 = CUShort(CInt(sense(12)) << 8 Or sense(13))
                    If readData.Length > 0 Then
                        My.Computer.FileSystem.WriteAllBytes($"{outputDirectory}\FM{FileNum}_BLK{BlkNum}.bin", readData, True)
                        If Not readData.Length = ReadLen Then
                            BlkNum = CInt(Block)
                        End If
                    End If
                    If Add_Key <> 0 Then
                        FileNum += 1
                        BlkNum = CInt(Block)
                    End If
                    If (Add_Key > 1 And Add_Key <> 4) Or Operation_Cancel_Flag Then
                        MessageBox.Show(TapeUtils.ParseSenseData(sense))
                        Operation_Cancel_Flag = False
                        Exit While
                    End If
                    If log Then
                        Invoke(Sub()
                                   If TextBoxDebugOutput.Text.Length > 10000 Then TextBoxDebugOutput.Text = ""
                                   TextBoxDebugOutput.AppendText("Processing file " & FileNum.ToString.PadRight(10) & " (Block = " & Block & ") Sense:")
                                   TextBoxDebugOutput.AppendText(TapeUtils.ParseAdditionalSenseCode(Add_Key) & vbCrLf)
                               End Sub)
                    End If
                    Block = CUInt(Block + 1)
                End While
                Invoke(Sub()
                           For Each c As Control In Panel1.Controls
                               c.Enabled = True
                           Next
                           For Each c As Control In Panel2.Controls
                               c.Enabled = True
                           Next
                           For Each c As Control In TabControl1.Controls
                               c.Enabled = True
                           Next
                           For Each c As Control In TabPageCommand.Controls
                               c.Enabled = True
                           Next
                       End Sub)
            End Sub)
        thprog.Start()
    End Sub

    Private Sub Button24_Click(sender As Object, e As EventArgs) Handles ButtonStopRawDump.Click
        Operation_Cancel_Flag = True
    End Sub

    Private Sub ButtonDebugDumpIndex_Click(sender As Object, e As EventArgs) Handles ButtonDebugDumpIndex.Click
        Enabled = False
        Task.Run(Sub()
                     Try
                         TapeUtils.Locate(ConfTapeDrive, 3, 0, TapeUtils.LocateDestType.FileMark)
                         TapeUtils.ReadBlock(ConfTapeDrive)
                         Dim data As Byte() = TapeUtils.ReadToFileMark(ConfTapeDrive)
                         Dim outputfile As String = "LTFSIndex_" & Now.ToString("yyyyMMdd_HHmmss.fffffff") & ".schema"
                         If Not Directory.Exists(My.Settings.schemaPath) Then
                             Directory.CreateDirectory(My.Settings.schemaPath)
                         End If
                         outputfile = Path.Combine(My.Settings.schemaPath, outputfile)
                         File.WriteAllBytes(outputfile, data)
                         Form1.Invoke(Sub()
                                          Form1.TextBox1.Text = outputfile
                                          Form1.LoadSchemaFile()
                                      End Sub)
                     Catch ex As Exception

                     End Try
                     Invoke(Sub() Enabled = True)
                 End Sub)

    End Sub

    Private Sub ButtonDebugFormat_Click(sender As Object, e As EventArgs) Handles ButtonDebugFormat.Click
        If Not LoadComplete Then Exit Sub
        Dim driveHandle As IntPtr
        If MessageBox.Show(New Form With {.TopMost = True}, My.Resources.ResText_DataLossWarning, My.Resources.ResText_Warning, MessageBoxButtons.OKCancel) = DialogResult.OK Then
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(LTFSConfigurator))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Configurator")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                        Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Format")
                            Log.Information("Tape format requested. TapeDrive={TapeDrive}.", ConfTapeDrive)
                        End Using
                    End Using
                End Using
            End Using
            Try
                If Not TapeUtils.IsOpened(driveHandle) Then TapeUtils.OpenTapeDrive(ConfTapeDrive, driveHandle)
                TapeUtils.ReadPosition(driveHandle)
                Dim modedata As Byte() = TapeUtils.ModeSense(driveHandle, &H11)
                Dim MaxExtraPartitionAllowed As Byte = modedata(2)
                If MaxExtraPartitionAllowed > 1 Then MaxExtraPartitionAllowed = 1
                Dim param As New TapeUtils.MKLTFS_Param(MaxExtraPartitionAllowed)

                If param.MaxExtraPartitionAllowed = 0 Then param.BlockLen = 65536
                param.Barcode = TapeUtils.ReadBarcode(driveHandle)
                Dim Confirm As Boolean = False
                Dim msDialog As New SettingPanel With {.SelectedObject = param, .StartPosition = FormStartPosition.Manual, .TopMost = True, .Text = $"MKLTFS"}
                msDialog.Top = CInt(Top + Height / 2 - msDialog.Height / 2)
                msDialog.Left = CInt(Left + Width / 2 - msDialog.Width / 2)
                While Not Confirm
                    If param.VolumeLabel = "" Then param.VolumeLabel = param.Barcode
                    If msDialog.ShowDialog() = DialogResult.Cancel Then Exit Sub

                    Select Case MessageBox.Show(New Form With {.TopMost = True}, $"{My.Resources.ResText_Barcode2}{param.Barcode}{vbCrLf}{My.Resources.ResText_LTFSVolumeN2}{param.VolumeLabel}", My.Resources.ResText_Confirm, MessageBoxButtons.YesNoCancel)
                        Case DialogResult.Yes
                            Confirm = True
                            Exit While
                        Case DialogResult.No
                            Confirm = False
                        Case DialogResult.Cancel
                            Exit Sub
                    End Select
                End While
                Panel1.Enabled = False
                TapeUtils.mkltfs(driveHandle, param.Barcode, param.VolumeLabel, param.ExtraPartitionCount, param.BlockLen, False,
                    Sub(Message As String)
                        'ProgressReport
                        Invoke(Sub()
                                   TextBoxDebugOutput.AppendText(Message & vbCrLf)
                               End Sub)
                    End Sub,
                    Sub(Message As String)
                        'OnFinished
                        TapeUtils.CloseTapeDrive(driveHandle)
                        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(LTFSConfigurator))
                            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Configurator")
                                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Format")
                                        Log.Information("Tape format completed. TapeDrive={TapeDrive}.", ConfTapeDrive)
                                    End Using
                                End Using
                            End Using
                        End Using
                        Invoke(Sub()
                                   TextBoxDebugOutput.AppendText("Format finished.")
                                   Panel1.Enabled = True
                               End Sub)
                    End Sub,
                    Sub(Message As String)
                        'OnError
                        TapeUtils.CloseTapeDrive(driveHandle)
                        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(LTFSConfigurator))
                            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Configurator")
                                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Error")
                                        Log.Error("Tape format failed. TapeDrive={TapeDrive} Message={Message}.", ConfTapeDrive, Message)
                                    End Using
                                End Using
                            End Using
                        End Using
                        Invoke(Sub()
                                   TextBoxDebugOutput.AppendText(Message & vbCrLf)
                                   TextBoxDebugOutput.AppendText("Format failed.")
                                   Panel1.Enabled = True
                               End Sub)
                        Invoke(Sub() MessageBox.Show(New Form With {.TopMost = True}, $"{My.Resources.ResText_FmtFail}{vbCrLf}{Message}"))
                    End Sub, param.Capacity, param.P0Size, param.P1Size, param.EncryptionKey, param.WORMMode)
            Catch ex As Exception
                TapeUtils.CloseTapeDrive(driveHandle)
                Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(LTFSConfigurator))
                    Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Configurator")
                        Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                            Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Error")
                                Log.Error(ex, "Tape format failed before completion callback. TapeDrive={TapeDrive}.", ConfTapeDrive)
                            End Using
                        End Using
                    End Using
                End Using
                TextBoxDebugOutput.AppendText(ex.ToString())
                Panel1.Enabled = True
            End Try

        End If
    End Sub

    Private Sub Button27_Click(sender As Object, e As EventArgs) Handles ButtonLTFSWriter.Click
        ApplicationNavigation.ShowWriter(ConfTapeDrive)
    End Sub

    Private Sub ButtonDebugReleaseUnit_Click(sender As Object, e As EventArgs) Handles ButtonDebugReleaseUnit.Click
        Panel1.Enabled = False
        Task.Run(Sub()
                     TapeUtils.ReleaseUnit(ConfTapeDrive,
                                                   Function(sense As Byte()) As Boolean
                                                       Invoke(Sub()
                                                                  TextBoxDebugOutput.Text = "RELEASE UNIT" & vbCrLf
                                                                  TextBoxDebugOutput.AppendText(TapeUtils.ParseSenseData(sense))
                                                              End Sub)
                                                       Return True
                                                   End Function)
                     Invoke(Sub() Panel1.Enabled = True)
                 End Sub)
    End Sub

    Private Sub ButtonDebugAllowMediumRemoval_Click(sender As Object, e As EventArgs) Handles ButtonDebugAllowMediumRemoval.Click
        Panel1.Enabled = False
        Task.Run(Sub()
                     TapeUtils.AllowMediumRemoval(ConfTapeDrive,
                              Function(sense As Byte()) As Boolean
                                  Invoke(Sub()
                                             TextBoxDebugOutput.Text = "ALLOW MEDIUM REMOVAL" & vbCrLf
                                             TextBoxDebugOutput.AppendText(TapeUtils.ParseSenseData(sense))
                                         End Sub)
                                  Return True
                              End Function)
                     Invoke(Sub() Panel1.Enabled = True)
                 End Sub)
    End Sub

    Private Sub Button30_Click(sender As Object, e As EventArgs) Handles ButtonFileSorter.Click
        Form1.Show()
    End Sub

    Private Sub LTFSConfigurator_Closing(sender As Object, e As CancelEventArgs) Handles Me.Closing
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(LTFSConfigurator))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Configurator")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Lifecycle")
                        Log.Information("LTFS configurator window closing.")
                    End Using
                End Using
            End Using
        End Using
        Operation_Cancel_Flag = True
        My.Settings.LTFSConf_AutoRefresh = CheckBoxAutoRefresh.Checked
        My.Settings.Save()
    End Sub


    Private Sub CheckBox1_MouseUp(sender As Object, e As MouseEventArgs) Handles CheckBoxDebugPanel.MouseUp
        If e.Button = MouseButtons.Right Then
            ButtonStartFUSESvc.Visible = Not ButtonStartFUSESvc.Visible
            ButtonStopFUSESvc.Visible = Not ButtonStopFUSESvc.Visible
            ButtonRemount.Visible = Not ButtonRemount.Visible
            ButtonChangerTool.Visible = Not ButtonChangerTool.Visible
        End If
    End Sub

    Private Sub Button5_Click(sender As Object, e As EventArgs) Handles ButtonChangerTool.Click
        ApplicationNavigation.ShowChangerTool()
    End Sub

    Private Sub Button11_Click(sender As Object, e As EventArgs) Handles ButtonResetLogPage.Click
        Panel1.Enabled = False
        Task.Run(Sub()
                     TextBoxDebugOutput.Clear()
                     Dim logdata As Byte()
                     Dim pdata As TapeUtils.PageData
#Region "0x00"
                     logdata = TapeUtils.LogSense(ConfTapeDrive, &H0, 0, PageControl:=1)
                     pdata = TapeUtils.PageData.CreateDefault(TapeUtils.PageData.DefaultPages.HPLTO6_SupportedLogPagesPage, logdata)
                     Invoke(Sub() TextBoxDebugOutput.AppendText(pdata.GetSummary()))
                     If Not Directory.Exists(My.Settings.logPagesPath) Then Directory.CreateDirectory(My.Settings.logPagesPath)
                     File.WriteAllText(Path.Combine(My.Settings.logPagesPath, "0x00.xml"), pdata.GetSerializedText())
#End Region
#Region "0x02"
                     logdata = TapeUtils.LogSense(ConfTapeDrive, &H2, 0, PageControl:=1)
                     pdata = TapeUtils.PageData.CreateDefault(TapeUtils.PageData.DefaultPages.HPLTO6_WriteErrorCountersLogPage, logdata)
                     Invoke(Sub() TextBoxDebugOutput.AppendText(pdata.GetSummary()))
                     If Not Directory.Exists(My.Settings.logPagesPath) Then Directory.CreateDirectory(My.Settings.logPagesPath)
                     File.WriteAllText(Path.Combine(My.Settings.logPagesPath, "0x02.xml"), pdata.GetSerializedText())
#End Region
#Region "0x03"
                     logdata = TapeUtils.LogSense(ConfTapeDrive, &H3, 0, PageControl:=1)
                     pdata = TapeUtils.PageData.CreateDefault(TapeUtils.PageData.DefaultPages.HPLTO6_ReadErrorCountersLogPage, logdata)
                     Invoke(Sub() TextBoxDebugOutput.AppendText(pdata.GetSummary()))
                     If Not Directory.Exists(My.Settings.logPagesPath) Then Directory.CreateDirectory(My.Settings.logPagesPath)
                     File.WriteAllText(Path.Combine(My.Settings.logPagesPath, "0x03.xml"), pdata.GetSerializedText())
#End Region
#Region "0x0C"
                     logdata = TapeUtils.LogSense(ConfTapeDrive, &HC, 0, PageControl:=1)
                     pdata = TapeUtils.PageData.CreateDefault(TapeUtils.PageData.DefaultPages.HPLTO6_SequentialAccessDeviceLogPage, logdata)
                     Invoke(Sub() TextBoxDebugOutput.AppendText(pdata.GetSummary()))
                     If Not Directory.Exists(My.Settings.logPagesPath) Then Directory.CreateDirectory(My.Settings.logPagesPath)
                     File.WriteAllText(Path.Combine(My.Settings.logPagesPath, "0x0C.xml"), pdata.GetSerializedText())
#End Region
#Region "0x0D"
                     logdata = TapeUtils.LogSense(ConfTapeDrive, &HD, 0, PageControl:=1)
                     pdata = TapeUtils.PageData.CreateDefault(TapeUtils.PageData.DefaultPages.HPLTO6_TemperatureLogPage, logdata)
                     Invoke(Sub() TextBoxDebugOutput.AppendText(pdata.GetSummary()))
                     If Not Directory.Exists(My.Settings.logPagesPath) Then Directory.CreateDirectory(My.Settings.logPagesPath)
                     File.WriteAllText(Path.Combine(My.Settings.logPagesPath, "0x0D.xml"), pdata.GetSerializedText())
#End Region
#Region "0x11"
                     logdata = TapeUtils.LogSense(ConfTapeDrive, &H11, 0, PageControl:=1)
                     pdata = TapeUtils.PageData.CreateDefault(TapeUtils.PageData.DefaultPages.HPLTO6_DataTransferDeviceStatusLogPage, logdata)
                     Invoke(Sub() TextBoxDebugOutput.AppendText(pdata.GetSummary()))
                     If Not Directory.Exists(My.Settings.logPagesPath) Then Directory.CreateDirectory(My.Settings.logPagesPath)
                     File.WriteAllText(Path.Combine(My.Settings.logPagesPath, "0x11.xml"), pdata.GetSerializedText())
#End Region
#Region "0x12"
                     logdata = TapeUtils.LogSense(ConfTapeDrive, &H12, 0, PageControl:=1)
                     pdata = TapeUtils.PageData.CreateDefault(TapeUtils.PageData.DefaultPages.HPLTO6_TapeAlertResponseLogPage, logdata)
                     Invoke(Sub() TextBoxDebugOutput.AppendText(pdata.GetSummary()))
                     If Not Directory.Exists(My.Settings.logPagesPath) Then Directory.CreateDirectory(My.Settings.logPagesPath)
                     File.WriteAllText(Path.Combine(My.Settings.logPagesPath, "0x12.xml"), pdata.GetSerializedText())
#End Region
#Region "0x13"
                     logdata = TapeUtils.LogSense(ConfTapeDrive, &H13, 0, PageControl:=1)
                     pdata = TapeUtils.PageData.CreateDefault(TapeUtils.PageData.DefaultPages.HPLTO6_RequestedRecoveryLogPage, logdata)
                     Invoke(Sub() TextBoxDebugOutput.AppendText(pdata.GetSummary()))
                     If Not Directory.Exists(My.Settings.logPagesPath) Then Directory.CreateDirectory(My.Settings.logPagesPath)
                     File.WriteAllText(Path.Combine(My.Settings.logPagesPath, "0x13.xml"), pdata.GetSerializedText())
#End Region
#Region "0x14"
                     logdata = TapeUtils.LogSense(ConfTapeDrive, &H14, 0, PageControl:=1)
                     pdata = TapeUtils.PageData.CreateDefault(TapeUtils.PageData.DefaultPages.HPLTO6_DeviceStatisticsLogPage, logdata)
                     Invoke(Sub() TextBoxDebugOutput.AppendText(pdata.GetSummary()))
                     If Not Directory.Exists(My.Settings.logPagesPath) Then Directory.CreateDirectory(My.Settings.logPagesPath)
                     File.WriteAllText(Path.Combine(My.Settings.logPagesPath, "0x14.xml"), pdata.GetSerializedText())
#End Region
#Region "0x15"
                     logdata = TapeUtils.LogSense(ConfTapeDrive, &H15, 0, PageControl:=1)
                     pdata = TapeUtils.PageData.CreateDefault(TapeUtils.PageData.DefaultPages.HPLTO6_ServiceBuffersInformationLogPage, logdata)
                     Invoke(Sub() TextBoxDebugOutput.AppendText(pdata.GetSummary()))
                     If Not Directory.Exists(My.Settings.logPagesPath) Then Directory.CreateDirectory(My.Settings.logPagesPath)
                     File.WriteAllText(Path.Combine(My.Settings.logPagesPath, "0x15.xml"), pdata.GetSerializedText())
#End Region
#Region "0x16"
                     logdata = TapeUtils.LogSense(ConfTapeDrive, &H16, 0, PageControl:=1)
                     pdata = TapeUtils.PageData.CreateDefault(TapeUtils.PageData.DefaultPages.HPLTO6_TapeDiagnosticLogPage, logdata)
                     Invoke(Sub() TextBoxDebugOutput.AppendText(pdata.GetSummary()))
                     If Not Directory.Exists(My.Settings.logPagesPath) Then Directory.CreateDirectory(My.Settings.logPagesPath)
                     File.WriteAllText(Path.Combine(My.Settings.logPagesPath, "0x16.xml"), pdata.GetSerializedText())
#End Region
#Region "0x17"
                     logdata = TapeUtils.LogSense(ConfTapeDrive, &H17, 0, PageControl:=1)
                     pdata = TapeUtils.PageData.CreateDefault(TapeUtils.PageData.DefaultPages.HPLTO6_VolumeStatisticsLogPage, logdata)
                     Invoke(Sub() TextBoxDebugOutput.AppendText(pdata.GetSummary()))
                     If Not Directory.Exists(My.Settings.logPagesPath) Then Directory.CreateDirectory(My.Settings.logPagesPath)
                     File.WriteAllText(Path.Combine(My.Settings.logPagesPath, "0x17.xml"), pdata.GetSerializedText())
#End Region
#Region "0x18"
                     logdata = TapeUtils.LogSense(ConfTapeDrive, &H18, 0, PageControl:=1)
                     pdata = TapeUtils.PageData.CreateDefault(TapeUtils.PageData.DefaultPages.HPLTO6_ProtocolSpecificPortLogPage, logdata)
                     Invoke(Sub() TextBoxDebugOutput.AppendText(pdata.GetSummary()))
                     If Not Directory.Exists(My.Settings.logPagesPath) Then Directory.CreateDirectory(My.Settings.logPagesPath)
                     File.WriteAllText(Path.Combine(My.Settings.logPagesPath, "0x18.xml"), pdata.GetSerializedText())
#End Region
#Region "0x1B"
                     logdata = TapeUtils.LogSense(ConfTapeDrive, &H1B, 0, PageControl:=1)
                     pdata = TapeUtils.PageData.CreateDefault(TapeUtils.PageData.DefaultPages.HPLTO6_DataCompressionLogPage, logdata)
                     Invoke(Sub() TextBoxDebugOutput.AppendText(pdata.GetSummary()))
                     If Not Directory.Exists(My.Settings.logPagesPath) Then Directory.CreateDirectory(My.Settings.logPagesPath)
                     File.WriteAllText(Path.Combine(My.Settings.logPagesPath, "0x1B.xml"), pdata.GetSerializedText())
#End Region
#Region "0x2E"
                     logdata = TapeUtils.LogSense(ConfTapeDrive, &H2E, 0, PageControl:=1)
                     pdata = TapeUtils.PageData.CreateDefault(TapeUtils.PageData.DefaultPages.HPLTO6_TapeAlertLogPage, logdata)
                     Invoke(Sub() TextBoxDebugOutput.AppendText(pdata.GetSummary()))
                     If Not Directory.Exists(My.Settings.logPagesPath) Then Directory.CreateDirectory(My.Settings.logPagesPath)
                     File.WriteAllText(Path.Combine(My.Settings.logPagesPath, "0x2E.xml"), pdata.GetSerializedText())
#End Region
#Region "0x30"
                     logdata = TapeUtils.LogSense(ConfTapeDrive, &H30, 0, PageControl:=1)
                     pdata = TapeUtils.PageData.CreateDefault(TapeUtils.PageData.DefaultPages.HPLTO6_TapeUsageLogPage, logdata)
                     Invoke(Sub() TextBoxDebugOutput.AppendText(pdata.GetSummary()))
                     If Not Directory.Exists(My.Settings.logPagesPath) Then Directory.CreateDirectory(My.Settings.logPagesPath)
                     File.WriteAllText(Path.Combine(My.Settings.logPagesPath, "0x30.xml"), pdata.GetSerializedText())
#End Region
#Region "0x31"
                     logdata = TapeUtils.LogSense(ConfTapeDrive, &H31, 0, PageControl:=1)
                     pdata = TapeUtils.PageData.CreateDefault(TapeUtils.PageData.DefaultPages.HPLTO6_TapeCapacityLogPage, logdata)
                     Invoke(Sub() TextBoxDebugOutput.AppendText(pdata.GetSummary()))
                     If Not Directory.Exists(My.Settings.logPagesPath) Then Directory.CreateDirectory(My.Settings.logPagesPath)
                     File.WriteAllText(Path.Combine(My.Settings.logPagesPath, "0x31.xml"), pdata.GetSerializedText())
#End Region
#Region "0x32"
                     logdata = TapeUtils.LogSense(ConfTapeDrive, &H32, 0, PageControl:=1)
                     pdata = TapeUtils.PageData.CreateDefault(TapeUtils.PageData.DefaultPages.HPLTO6_DataCompressionHPLogPage, logdata)
                     Invoke(Sub() TextBoxDebugOutput.AppendText(pdata.GetSummary()))
                     If Not Directory.Exists(My.Settings.logPagesPath) Then Directory.CreateDirectory(My.Settings.logPagesPath)
                     File.WriteAllText(Path.Combine(My.Settings.logPagesPath, "0x32.xml"), pdata.GetSerializedText())
#End Region
#Region "0x33"
                     logdata = TapeUtils.LogSense(ConfTapeDrive, &H33, 0, PageControl:=1)
                     pdata = TapeUtils.PageData.CreateDefault(TapeUtils.PageData.DefaultPages.HPLTO6_DeviceWellnessLogPage, logdata)
                     Invoke(Sub() TextBoxDebugOutput.AppendText(pdata.GetSummary()))
                     If Not Directory.Exists(My.Settings.logPagesPath) Then Directory.CreateDirectory(My.Settings.logPagesPath)
                     File.WriteAllText(Path.Combine(My.Settings.logPagesPath, "0x33.xml"), pdata.GetSerializedText())
#End Region
#Region "0x34"
                     logdata = TapeUtils.LogSense(ConfTapeDrive, &H34, 0, PageControl:=1)
                     pdata = TapeUtils.PageData.CreateDefault(TapeUtils.PageData.DefaultPages.HPLTO6_PerformanceDataLogPage, logdata)
                     Invoke(Sub() TextBoxDebugOutput.AppendText(pdata.GetSummary()))
                     If Not Directory.Exists(My.Settings.logPagesPath) Then Directory.CreateDirectory(My.Settings.logPagesPath)
                     File.WriteAllText(Path.Combine(My.Settings.logPagesPath, "0x34.xml"), pdata.GetSerializedText())
#End Region
#Region "0x35"
                     logdata = TapeUtils.LogSense(ConfTapeDrive, &H35, 0, PageControl:=1)
                     pdata = TapeUtils.PageData.CreateDefault(TapeUtils.PageData.DefaultPages.HPLTO6_DTDeviceErrorLogPage, logdata)
                     Invoke(Sub() TextBoxDebugOutput.AppendText(pdata.GetSummary()))
                     If Not Directory.Exists(My.Settings.logPagesPath) Then Directory.CreateDirectory(My.Settings.logPagesPath)
                     File.WriteAllText(Path.Combine(My.Settings.logPagesPath, "0x35.xml"), pdata.GetSerializedText())
#End Region
#Region "0x3E"
                     logdata = TapeUtils.LogSense(ConfTapeDrive, &H3E, 0, PageControl:=1)
                     pdata = TapeUtils.PageData.CreateDefault(TapeUtils.PageData.DefaultPages.HPLTO6_DeviceStatusLogPage, logdata)
                     Invoke(Sub() TextBoxDebugOutput.AppendText(pdata.GetSummary()))
                     If Not Directory.Exists(My.Settings.logPagesPath) Then Directory.CreateDirectory(My.Settings.logPagesPath)
                     File.WriteAllText(Path.Combine(My.Settings.logPagesPath, "0x3E.xml"), pdata.GetSerializedText())
#End Region
                     Invoke(Sub() Panel1.Enabled = True)
                 End Sub)


    End Sub
    Public PageItem As New List(Of TapeUtils.PageData)
    Private Sub TabControl1_SelectedIndexChanged(sender As Object, e As EventArgs) Handles TabControl1.SelectedIndexChanged
        If TabControl1.SelectedTab Is TabPageLog Then
            If Directory.Exists(My.Settings.logPagesPath) Then
                ComboBox4.Items.Clear()
                PageItem.Clear()
                For Each f As FileInfo In New DirectoryInfo(My.Settings.logPagesPath).GetFiles
                    Try
                        Dim pdata As TapeUtils.PageData = TapeUtils.PageData.FromXML(File.ReadAllText(f.FullName))
                        PageItem.Add(pdata)
                        ComboBox4.Items.Add($"0x{Hex(pdata.PageCode).PadLeft(2, "0"c)} - {pdata.Name}")
                        ComboBox5.SelectedIndex = 1
                    Catch ex As Exception

                    End Try
                Next
            End If
        End If
    End Sub

    Private Sub Button12_Click(sender As Object, e As EventArgs) Handles ButtonRunLogSense.Click
        If ComboBox4.SelectedIndex >= 0 Then
            Dim index As Integer = ComboBox4.SelectedIndex
            Dim pc As Integer = ComboBox5.SelectedIndex
            Panel1.Enabled = False
            Task.Run(Sub()
                         Dim logdata As Byte() = TapeUtils.LogSense(TapeDrive:=ConfTapeDrive, PageCode:=CByte(PageItem(index).PageCode), SubPageCode:=0, PageControl:=CByte(pc))
                         Invoke(Sub()
                                    PageItem(index).RawData = logdata
                                    TextBoxDebugOutput.Text = PageItem(index).GetSummary()
                                    If CheckBoxShowRawLogPageData.Checked Then
                                        TextBoxDebugOutput.AppendText("Raw Data".PadLeft(41, "="c).PadRight(75, "="c))
                                        TextBoxDebugOutput.AppendText(vbCrLf)
                                        TextBoxDebugOutput.AppendText(IOManager.Byte2Hex(PageItem(index).RawData, True))
                                    End If
                                    Panel1.Enabled = True
                                End Sub)

                     End Sub)
        End If
    End Sub
    Public TestEnabled As Boolean
    Public blist As List(Of Byte())
    Private Sub ButtonTest_Click(sender As Object, e As EventArgs) Handles ButtonTest.Click
        If TestEnabled Then
            ButtonTest.Enabled = False
            TestEnabled = False
            Exit Sub
        End If
        Dim progmax As Long
        If MessageBox.Show(New Form With {.TopMost = True}, "Write will destroy everything after current position. Continue?", "Warning", MessageBoxButtons.OKCancel) = DialogResult.OK Then
            TestEnabled = True
            Dim progval As Long = 0
            Dim info As String = ""
            Dim running As Boolean = True
            Dim randomNum As Boolean = RadioButtonTest1.Checked
            Dim blkLen As Integer = CInt(NumericUpDownTestBlkSize.Value)
            Dim blkNum As Long = CLng(NumericUpDownTestBlkNum.Value)
            progmax = blkNum * blkLen
            Dim sec As Integer = -1
            Dim SenseMsg As String = ""
            Dim zbcstartblk As Long = 0
            Dim zbcsectorlen As Integer = 4096
            Select Case My.Settings.TapeUtils_DriverType
                Case TapeUtils.DriverType.ZBCDevice
                    DisplayHelper.ShowInputDialog("Start LBA", "ZBC test write param", zbcstartblk)
                    DisplayHelper.ShowInputDialog("Sector length", "ZBC test write param", zbcsectorlen)
                Case Else

            End Select
            Dim th As New Threading.Thread(
            Sub()
                Dim r As New Random()
                Dim b(blkLen - 1) As Byte
                If blist Is Nothing OrElse blist(0).Length <> blkLen Then
                    blist = New List(Of Byte())(1000)
                    For i As Integer = 0 To 999
                        If randomNum Then
                            r.NextBytes(b)
                        End If
                        blist.Add(DirectCast(b.Clone(), Byte()))
                    Next
                End If

                Invoke(Sub() TextBoxDebugOutput.AppendText($"Start{vbCrLf}"))
                sec = 0
                Dim handle As IntPtr
                Dim bH(7) As Byte
                If Not TapeUtils.OpenTapeDrive(ConfTapeDrive, handle) Then MessageBox.Show(New Form With {.TopMost = True}, "False")
                If blkLen = 0 Then
                    For i As Long = 0 To blkNum - 1
                        If Not TestEnabled Then Exit For
                        TapeUtils.WriteFileMark(handle)
                        progval = i * blkLen
                    Next
                Else
                    Dim LastC1Err(31) As Integer, LastNoCCPs(31) As Integer
                    For i As Long = 0 To blkNum - 1
                        If Not TestEnabled Then Exit For
                        Dim sense(63) As Byte
                        Dim zbcLBAWritten As Long = 0
                        Select Case My.Settings.TapeUtils_DriverType
                            Case TapeUtils.DriverType.ZBCDevice
                                zbcLBAWritten = blkLen \ zbcsectorlen
                                Dim cdb As Byte() = {&H2A, &H0,
                                                          CByte((zbcstartblk >> 24) And &HFF),
                                                          CByte((zbcstartblk >> 16) And &HFF),
                                                          CByte((zbcstartblk >> 8) And &HFF),
                                                          CByte(zbcstartblk And &HFF), &H0,
                                                          CByte(zbcLBAWritten >> 8 And &HFF),
                                                          CByte(zbcLBAWritten And &HFF), &H0}
                                TapeUtils.SendSCSICommand(handle, cdb,
                                                          blist(CInt(i Mod 1000)), 0, Function(sensedata As Byte())
                                                                                          sense = sensedata
                                                                                          Return True
                                                                                      End Function)
                            Case Else
                                sense = TapeUtils.Write(handle, blist(CInt(i Mod 1000)), blkLen)
                        End Select
                        If ((sense(2) >> 6) And &H1) = 1 Then
                            If (sense(2) And &HF) = 13 Then
                                Invoke(Sub() MessageBox.Show(New Form With {.TopMost = True}, My.Resources.ResText_VOF))
                                Exit For
                            Else
                                SenseMsg = My.Resources.ResText_EWEOM
                            End If
                        ElseIf (sense(2) And &HF) <> 0 Then
                            SenseMsg = TapeUtils.ParseSenseData(sense)
                            Select Case MessageBox.Show(New Form With {.TopMost = True}, $"{My.Resources.ResText_WErr}{vbCrLf}{TapeUtils.ParseSenseData(sense)}{vbCrLf}{vbCrLf}sense{vbCrLf}{TapeUtils.Byte2Hex(sense, True)}", My.Resources.ResText_Warning, MessageBoxButtons.AbortRetryIgnore)
                                Case DialogResult.Abort
                                    Exit For
                                Case DialogResult.Retry
                                    i -= 1
                                    Continue For
                                Case DialogResult.Ignore
                                    Continue For
                            End Select
                        Else
                            SenseMsg = ""
                        End If
                        If My.Settings.TapeUtils_DriverType = TapeUtils.DriverType.ZBCDevice Then
                            Threading.Interlocked.Add(zbcstartblk, zbcLBAWritten)
                        Else
                            Try
                                If i Mod 200 = 0 Then
                                    Dim result As New StringBuilder
                                    Dim WERLHeader As Byte()
                                    Dim WERLPage As Byte()
                                    Dim WERLPageLen As Integer
                                    SyncLock TapeUtils.GetSCSIOperationLock(handle)
                                        WERLHeader = TapeUtils.SCSIReadParam(handle, {&H1C, &H1, &H88, &H0, &H4, &H0}, 4)
                                        If WERLHeader.Length <> 4 Then Exit Try
                                        WERLPageLen = WERLHeader(2)
                                        WERLPageLen <<= 8
                                        WERLPageLen = WERLPageLen Or WERLHeader(3)
                                        If WERLPageLen = 0 Then Exit Try
                                        WERLPageLen += 4
                                        WERLPage = TapeUtils.SCSIReadParam(handle:=handle, cdbData:=New Byte() {&H1C, &H1, &H88, CByte((WERLPageLen >> 8) And &HFF), CByte(WERLPageLen And &HFF), &H0}, paramLen:=WERLPageLen)
                                    End SyncLock
                                    Dim WERLData As String() = Encoding.ASCII.GetString(WERLPage, 4, WERLPage.Length - 4).Split({vbCr, vbLf, vbTab}, StringSplitOptions.RemoveEmptyEntries)
                                    info = ""
                                    Try
                                        For ch As Integer = 4 To WERLData.Length - 5 Step 5
                                            Dim chan As Integer = (ch - 4) \ 5
                                            Dim C1err As Integer = Integer.Parse(WERLData(ch + 0), Globalization.NumberStyles.HexNumber)
                                            Dim NoCCPs As Integer = Integer.Parse(WERLData(ch + 4), Globalization.NumberStyles.HexNumber)

                                            If NoCCPs - LastNoCCPs(chan) > 0 Then
                                                Dim resulttxt As String = (Math.Round(Math.Log10((C1err - LastC1Err(chan)) / (NoCCPs - LastNoCCPs(chan)) / 2 / 1920), 2).ToString("f2"))
                                                resulttxt = resulttxt.Replace("∞", "Inf")
                                                result.Append(resulttxt.PadLeft(6).PadRight(7))
                                                LastC1Err(chan) = C1err
                                                LastNoCCPs(chan) = NoCCPs
                                            Else
                                                result.Append("-".PadLeft(4).PadRight(7))
                                            End If
                                        Next
                                    Catch
                                    End Try
                                    info = result.ToString()
                                End If
                            Catch ex As Exception
                                info = ex.ToString()
                            End Try
                        End If

                        progval = i * blkLen
                    Next
                End If
                TapeUtils.Flush(handle)
                TapeUtils.CloseTapeDrive(handle)


                running = False
                TestEnabled = False
                Invoke(Sub()
                           ButtonTest.Enabled = True
                           ButtonTest.Text = "Start"
                       End Sub)
            End Sub)
            Dim thprog As New Threading.Thread(
            Sub()
                Dim lastval As Long = 0
                Dim len1 As Integer = progmax.ToString().Length
                While running
                    Threading.Thread.Sleep(1000)
                    Dim prognow As Long = progval
                    Invoke(Sub()
                               Dim texta As String = ($"{sec.ToString().PadLeft(4)}: {prognow.ToString().PadLeft(Math.Max(15, len1))} (+{IOManager.FormatSize(prognow - lastval).PadLeft(10)})")
                               Dim infostart As Integer = texta.Length
                               TextBoxDebugOutput.AppendText($"{texta}{info} {SenseMsg}{vbCrLf}")
                               If info.Length > 0 Then FormatLogRateColor(TextBoxDebugOutput, infostart, info)
                           End Sub)
                    If sec >= 0 Then sec += 1
                    lastval = prognow
                End While
                Invoke(Sub()
                           Dim texta As String = ($"{sec.ToString().PadLeft(4)}: {progval.ToString().PadLeft(Math.Max(15, len1))} (+{IOManager.FormatSize(progval - lastval).PadLeft(10)})")
                           Dim infostart As Integer = texta.Length
                           TextBoxDebugOutput.AppendText($"{texta}{info} {SenseMsg}{vbCrLf}")
                           If info.Length > 0 Then FormatLogRateColor(TextBoxDebugOutput, infostart, info)
                           TextBoxDebugOutput.AppendText($"End")
                       End Sub)
            End Sub)
            TextBoxDebugOutput.Clear()
            TextBoxDebugOutput.AppendText($"Preparing... {vbCrLf}")
            thprog.Start()
            ButtonTest.Text = "Stop"
            th.Start()
        End If

    End Sub

    Public Sub FormatLogRateColor(textbox As RichTextBox, StartIndex As Integer, infostr As String)
        If textbox Is Nothing OrElse String.IsNullOrEmpty(infostr) OrElse textbox.IsDisposed OrElse textbox.Disposing Then Return

        ' The callers normally arrive here from the UI thread. Keep the method safe for
        ' other callers too, but marshal the whole formatting operation only once.
        If textbox.InvokeRequired Then
            textbox.Invoke(New Action(Sub() FormatLogRateColor(textbox, StartIndex, infostr)))
            Return
        End If

        Const seglen As Integer = 7
        Dim segmentCount As Integer = infostr.Length \ seglen
        If segmentCount <= 0 Then Return

        Dim lineStart As Integer = textbox.GetFirstCharIndexFromLine(textbox.Lines.Length - 2) + StartIndex
        If lineStart < 0 OrElse lineStart >= textbox.TextLength Then Return

        ' SelectionBackColor causes RichEdit to repaint for every range. Suppress redraw
        ' while applying all channel colors so one output line becomes one repaint.
        NativeMethods.SendMessage(textbox.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero).ThrowIfFailed("Disable text redraw failed.")
        Try
            For i As Integer = 0 To segmentCount - 1
                Dim selectionStart As Integer = lineStart + i * seglen
                Dim selectionLength As Integer = Math.Min(seglen, textbox.TextLength - selectionStart)
                If selectionLength <= 0 Then Exit For

                Dim segvalue As String = infostr.Substring(i * seglen, seglen).Replace(" ", "")
                textbox.Select(selectionStart, selectionLength)
                textbox.SelectionBackColor = ErrRateHelper.GetColor(segvalue)
            Next
        Finally
            textbox.Select(textbox.TextLength, 0)
            NativeMethods.SendMessage(textbox.Handle, WM_SETREDRAW, New IntPtr(1), IntPtr.Zero).ThrowIfFailed("Enable text redraw failed.")
            textbox.Invalidate()
        End Try
    End Sub

    Private Sub Button15_Click(sender As Object, e As EventArgs) Handles ButtonRDErrRateLog.Click
        Panel1.Enabled = False
        Task.Run(Sub()
                     Dim result As New StringBuilder
                     Try
                         Dim WERLHeader As Byte()
                         Dim RERLHeader As Byte()
                         Dim WERLPage As Byte()
                         Dim RERLPage As Byte()
                          SyncLock TapeUtils.GetSCSIOperationLock(ConfTapeDrive)
                             WERLHeader = TapeUtils.SCSIReadParam(ConfTapeDrive, {&H1C, &H1, &H88, &H0, &H4, &H0}, 4)
                             If WERLHeader.Length <> 4 Then Exit Try
                             RERLHeader = TapeUtils.SCSIReadParam(ConfTapeDrive, {&H1C, &H1, &H87, &H0, &H4, &H0}, 4)
                             If RERLHeader.Length <> 4 Then Exit Try
                             Dim WERLPageLen As Integer = WERLHeader(2)
                             WERLPageLen <<= 8
                             WERLPageLen = WERLPageLen Or WERLHeader(3)
                             If WERLPageLen = 0 Then Exit Try
                             WERLPageLen += 4
                             WERLPage = TapeUtils.SCSIReadParam(TapeDrive:=ConfTapeDrive, cdbData:=New Byte() {&H1C, &H1, &H88, CByte((WERLPageLen >> 8) And &HFF), CByte(WERLPageLen And &HFF), &H0}, paramLen:=WERLPageLen)

                             Dim RERLPageLen As Integer = RERLHeader(2)
                             RERLPageLen <<= 8
                             RERLPageLen = RERLPageLen Or RERLHeader(3)
                             If RERLPageLen = 0 Then Exit Try
                             RERLPageLen += 4
                             RERLPage = TapeUtils.SCSIReadParam(TapeDrive:=ConfTapeDrive, cdbData:=New Byte() {&H1C, &H1, &H87, CByte((RERLPageLen >> 8) And &HFF), CByte(RERLPageLen And &HFF), &H0}, paramLen:=RERLPageLen)
                         End SyncLock
                         Dim WERLData As String() = Encoding.ASCII.GetString(WERLPage, 4, WERLPage.Length - 4).Split({vbCr, vbLf, vbTab}, StringSplitOptions.RemoveEmptyEntries)
                         Dim RERLData As String() = Encoding.ASCII.GetString(RERLPage, 4, RERLPage.Length - 4).Split({vbCr, vbLf, vbTab}, StringSplitOptions.RemoveEmptyEntries)
                         result.AppendLine($"Write Error Rate Log")
                         result.AppendLine($"  Datasets Written     : {WERLData(0)}")
                         result.AppendLine($"  CWI-4 Sets Written   : {WERLData(1)}")
                         result.AppendLine($"  CWI-4 Set Retries    : {WERLData(2)}")
                         result.AppendLine($"  Unwritable Datasets  : {WERLData(3)}")
                         result.AppendLine($"  =========+==========+==========+===============+==========+============+=============+=============+==================")
                         result.AppendLine($"   Channel | No. CCPs | C1 code  |  C1 codeword  |  Header  | Write Pass | C1 codeword |  Bit Error  |    Block per     ")
                         result.AppendLine($"           |          |  error   | uncorrectable |  error   |   error    | error rate  | rate(log10) | Uncorrectable C1 ")
                         result.AppendLine($"  ---------+----------+----------+---------------+----------+------------+-------------+-------------+------------------")
                         For i As Integer = 4 To WERLData.Length - 5 Step 5
                             Dim chan As Integer = (i - 4) \ 5
                             Dim C1err As Integer = Integer.Parse(WERLData(i + 0), Globalization.NumberStyles.HexNumber)
                             Dim C1cwerr As Integer = Integer.Parse(WERLData(i + 1), Globalization.NumberStyles.HexNumber)
                             Dim Headerrr As Integer = Integer.Parse(WERLData(i + 2), Globalization.NumberStyles.HexNumber)
                             Dim WrPasserr As Integer = Integer.Parse(WERLData(i + 3), Globalization.NumberStyles.HexNumber)
                             Dim NoCCPs As Integer = Integer.Parse(WERLData(i + 4), Globalization.NumberStyles.HexNumber)
                             result.Append("   ")
                             result.Append(chan.ToString.PadLeft(5))
                             result.Append("   | ")
                             result.Append(WERLData(i + 4).PadLeft(8, "0"c))
                             result.Append(" | ")
                             result.Append(WERLData(i + 0).PadLeft(8, "0"c))
                             result.Append(" |   ")
                             result.Append(WERLData(i + 1).PadLeft(8, "0"c))
                             result.Append("    | ")
                             result.Append(WERLData(i + 2).PadLeft(8, "0"c))
                             result.Append(" |  ")
                             result.Append(WERLData(i + 3).PadLeft(8, "0"c))
                             result.Append("  |  ")
                             If NoCCPs > 0 Then
                                 result.Append((C1err / NoCCPs / 2).ToString("E3"))
                             Else
                                 result.Append("          ")
                             End If
                             result.Append(" |    ")
                             If NoCCPs > 0 Then
                                 result.Append(Math.Round(Math.Log10(C1err / NoCCPs / 2 / 1920), 2).ToString("f2").PadRight(4))
                             Else
                                 result.Append("     ")
                             End If
                             result.Append("    |")
                             If C1cwerr > 0 Then
                                 result.AppendLine(Math.Round(NoCCPs * 2 / C1cwerr, 1).ToString("f1").PadLeft(11).PadRight(18))
                             Else
                                 result.AppendLine("".PadLeft(11).PadRight(18))
                             End If
                         Next
                         result.AppendLine($"  =========+==========+==========+===============+==========+============+=============+=============+==================")
                         result.AppendLine()
                         result.AppendLine($"Read Error Rate Log")
                         result.AppendLine($"  Datasets Read        : {RERLData(0)}")
                         result.AppendLine($"  Subdataset C2 Errors : {RERLData(2)}")
                         result.AppendLine($"  Dataset C2 Errors    : {RERLData(3)}")
                         result.AppendLine($"  X-Chan Interpolations: {RERLData(4)}")
                         result.AppendLine($"  =========+==========+==========+===============+==========+============+=============+=============+==================")
                         result.AppendLine($"   Channel | No. CCPs | C1 code  |  C1 codeword  |  Header  | Write Pass | C1 codeword |  Bit Error  |    Block per     ")
                         result.AppendLine($"           |          |  error   | uncorrectable |  error   |   error    | error rate  | rate(log10) | Uncorrectable C1 ")
                         result.AppendLine($"  ---------+----------+----------+---------------+----------+------------+-------------+-------------+------------------")
                         For i As Integer = 5 To RERLData.Length - 5 Step 5
                             Dim chan As Integer = (i - 4) \ 5
                             Dim C1err As Integer = Integer.Parse(RERLData(i + 0), Globalization.NumberStyles.HexNumber)
                             Dim C1cwerr As Integer = Integer.Parse(RERLData(i + 1), Globalization.NumberStyles.HexNumber)
                             Dim Headerrr As Integer = Integer.Parse(RERLData(i + 2), Globalization.NumberStyles.HexNumber)
                             Dim WrPasserr As Integer = Integer.Parse(RERLData(i + 3), Globalization.NumberStyles.HexNumber)
                             Dim NoCCPs As Integer = Integer.Parse(RERLData(i + 4), Globalization.NumberStyles.HexNumber)
                             result.Append("   ")
                             result.Append(chan.ToString.PadLeft(5))
                             result.Append("   | ")
                             result.Append(RERLData(i + 4).PadLeft(8, "0"c))
                             result.Append(" | ")
                             result.Append(RERLData(i + 0).PadLeft(8, "0"c))
                             result.Append(" |   ")
                             result.Append(RERLData(i + 1).PadLeft(8, "0"c))
                             result.Append("    | ")
                             result.Append(RERLData(i + 2).PadLeft(8, "0"c))
                             result.Append(" |  ")
                             result.Append(RERLData(i + 3).PadLeft(8, "0"c))
                             result.Append("  |  ")
                             If NoCCPs > 0 Then
                                 result.Append((C1err / NoCCPs / 2).ToString("E3"))
                             Else
                                 result.Append("          ")
                             End If
                             result.Append(" |    ")
                             If NoCCPs > 0 Then
                                 result.Append(Math.Round(Math.Log10(C1err / NoCCPs / 2 / 1920), 2).ToString("f2").PadRight(4))
                             Else
                                 result.Append("     ")
                             End If
                             result.Append("    |")
                             If C1cwerr > 0 Then
                                 result.AppendLine(Math.Round(NoCCPs * 2 / C1cwerr, 1).ToString("f1").PadLeft(11).PadRight(18))
                             Else
                                 result.AppendLine("".PadLeft(11).PadRight(18))
                             End If
                         Next
                         result.AppendLine($"  =========+==========+==========+===============+==========+============+=============+=============+==================")
                     Catch ex As Exception
                         result.Append(ex.ToString())
                     End Try
                     Invoke(Sub()
                                TextBoxDebugOutput.Text = result.ToString()
                                Panel1.Enabled = True
                            End Sub)
                 End Sub)

    End Sub

    Private Sub 在当前进程运行ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 在当前进程运行ToolStripMenuItem.Click
        ApplicationNavigation.ShowWriter(ConfTapeDrive)
    End Sub

    Private Sub 不读取索引ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 不读取索引ToolStripMenuItem.Click
        ApplicationNavigation.ShowWriter(ConfTapeDrive, offlineMode:=True)
    End Sub

    Private Sub BrowseBinaryFileToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles BrowseBinaryFileToolStripMenuItem.Click
        If OpenFileDialog1.ShowDialog = DialogResult.OK Then
            Enabled = False
            Dim CMInfo As TapeUtils.CMParser = Nothing
            Try
                CMInfo = New TapeUtils.CMParser() With {.a_CMBuffer = File.ReadAllBytes(OpenFileDialog1.FileName)}
                CMInfo.RunParse()
            Catch ex As Exception
                TextBoxDebugOutput.AppendText("CM Data Parsing Failed." & vbCrLf)
            End Try
            TextBoxDebugOutput.Text = ""
            Try
                TextBoxDebugOutput.AppendText(CMInfo.GetReport())
                If CheckBoxParseCMData.Checked AndAlso CMInfo IsNot Nothing Then
                    TextBoxDebugOutput.AppendText(CMInfo.GetSerializedText())
                    Dim PG1 As New SettingPanel
                    PG1.SelectedObject = CMInfo
                    PG1.Text = CMInfo.CartridgeMfgData.CartridgeSN
                    PG1.Show()
                    TextBoxDebugOutput.AppendText(vbCrLf)
                End If
            Catch ex As Exception
                TextBoxDebugOutput.AppendText("| CM data parsing failed.".PadRight(74) & "|" & vbCrLf)
            End Try
            TextBoxDebugOutput.Select(0, 0)
            TextBoxDebugOutput.ScrollToCaret()
            Enabled = True
        End If
    End Sub

    Private Sub ReadThroughDiagnosticCommandToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles ReadThroughDiagnosticCommandToolStripMenuItem.Click
        Enabled = False
        TextBoxDebugOutput.Text = ""
        Task.Run(Sub()
                     Dim CMInfo As TapeUtils.CMParser = Nothing
                     Try
                         Dim errormsg As Exception = Nothing
                         CMInfo = New TapeUtils.CMParser(TapeUtils.ReceiveDiagCM(ConfTapeDrive), errormsg)
                         If errormsg IsNot Nothing Then Throw errormsg
                     Catch ex As Exception
                         Invoke(Sub() TextBoxDebugOutput.AppendText("CM Data Parsing Failed." & vbCrLf & ex.ToString & vbCrLf))
                     End Try
                     Invoke(
                     Sub()
                         Try
                             TextBoxDebugOutput.AppendText(CMInfo.GetReport())
                         Catch ex As Exception
                             TextBoxDebugOutput.AppendText("Report generation failed.".PadRight(74) & vbCrLf & ex.ToString & vbCrLf)
                         End Try
                         Try
                             If CheckBoxParseCMData.Checked AndAlso CMInfo IsNot Nothing Then
                                 TextBoxDebugOutput.AppendText(CMInfo.GetSerializedText())
                                 Dim PG1 As New SettingPanel
                                 PG1.SelectedObject = CMInfo
                                 PG1.Text = CMInfo.CartridgeMfgData.CartridgeSN
                                 PG1.Show()
                                 TextBoxDebugOutput.AppendText(vbCrLf)
                             End If
                         Catch ex As Exception
                             TextBoxDebugOutput.AppendText("CM Data Parsing failed.".PadRight(74) & vbCrLf & ex.ToString & vbCrLf)
                         End Try
                         TextBoxDebugOutput.Select(0, 0)
                         TextBoxDebugOutput.ScrollToCaret()
                         If Directory.Exists(My.Application.Info.DirectoryPath & "\Info") Then
                             Dim fn As String
                             Try
                                 fn = CMInfo.ApplicationSpecificData.Barcode
                                 If fn Is Nothing OrElse fn.Length = 0 Then fn = CMInfo.CartridgeMfgData.CartridgeSN
                                 If fn Is Nothing Then fn = ""
                                 File.WriteAllText($"{My.Application.Info.DirectoryPath}\Info\{fn}.txt", TextBoxDebugOutput.Text)
                             Catch ex As Exception

                             End Try
                         End If
                         Enabled = True
                     End Sub)
                 End Sub)
    End Sub

    Private Sub DiskToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles DiskToolStripMenuItem.Click
        LoadComplete = False
        CheckBoxAutoRefresh.Checked = False
        ListBox1.Items.Clear()
        Dim DevList As List(Of TapeUtils.BlockDevice)
        LastDeviceList = TapeUtils.GetDiskDriveList()
        DevList = LastDeviceList
        For Each D As TapeUtils.BlockDevice In DevList
            D.DeviceType = "PhysicalDrive"
            ListBox1.Items.Add(D.ToString())
        Next
        ListBox1.SelectedIndex = Math.Min(SelectedIndex, ListBox1.Items.Count - 1)
        Dim t As String = ComboBoxDriveLetter.Text
        ComboBoxDriveLetter.Items.Clear()
        ComboBoxDriveLetter.Text = ""
        For Each s As String In AvailableDriveLetters
            ComboBoxDriveLetter.Items.Add(s)
        Next
        If ComboBoxDriveLetter.Items.Count > 0 Then
            If Not ComboBoxDriveLetter.Items.Contains(t) Then
                ComboBoxDriveLetter.SelectedIndex = 0
            Else
                ComboBoxDriveLetter.Text = t
            End If
        End If

        LoadComplete = True
        SelectedIndex = ListBox1.SelectedIndex
    End Sub

    Private Sub LTFSConfigurator_KeyDown(sender As Object, e As KeyEventArgs) Handles Me.KeyDown
        Select Case e.KeyCode
            Case Keys.F12
                Dim SP1 As New SettingPanel
                SP1.Text = Text
                SP1.SelectedObject = Me
                SP1.Show()
        End Select
    End Sub
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

    Private Sub RadioButtonTest1_CheckedChanged(sender As Object, e As EventArgs) Handles RadioButtonTest1.CheckedChanged
        blist = Nothing
    End Sub

    Private Sub ReInitializeToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles ReInitializeToolStripMenuItem.Click
        Enabled = False
        Task.Run(Sub()
                     Dim cdbData As Byte() = {4, 0, 0, 0, 0, 0}
                     Dim data As IntPtr = Marshal.AllocHGlobal(1)
                     Dim senseData(63) As Byte
                     Dim handle As IntPtr
                     SyncLock TapeUtils.GetSCSIOperationLock(ConfTapeDrive)
                         TapeUtils.OpenTapeDrive(ConfTapeDrive, handle)
                         TapeUtils.TapeSCSIIOCtlUnmanaged(handle, cdbData, data, 0, 1, 60000, senseData)
                         TapeUtils.CloseTapeDrive(handle)
                     End SyncLock
                     Marshal.FreeHGlobal(data)
                     Invoke(Sub()
                                PrintCommandResult(cdbData, Nothing, senseData)
                                Enabled = True
                            End Sub)
                 End Sub)
    End Sub

    Private Sub QuickEraseToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles QuickEraseToolStripMenuItem.Click
        Enabled = False
        Task.Run(Sub()
                     Dim cdbData As Byte() = {&H19, 0, 0, 0, 0, 0}
                     Dim data As IntPtr = Marshal.AllocHGlobal(1)
                     Dim senseData(63) As Byte
                     Dim handle As IntPtr
                     SyncLock TapeUtils.GetSCSIOperationLock(ConfTapeDrive)
                         TapeUtils.OpenTapeDrive(ConfTapeDrive, handle)
                         TapeUtils.TapeSCSIIOCtlUnmanaged(handle, cdbData, data, 0, 1, 60000, senseData)
                         TapeUtils.CloseTapeDrive(handle)
                     End SyncLock
                     Marshal.FreeHGlobal(data)
                     Invoke(Sub()
                                PrintCommandResult(cdbData, Nothing, senseData)
                                Enabled = True
                            End Sub)
                 End Sub)
    End Sub
    Public Sub PrintCommandResult(ByVal cdb As Byte(), ByVal param As Byte(), ByVal sense As Byte())

        Invoke(Sub()
                   TextBoxDebugOutput.Text = "CDB" & vbCrLf
                   TextBoxDebugOutput.Text &= IOManager.Byte2Hex(cdb, True)
                   TextBoxDebugOutput.Text &= vbCrLf & vbCrLf & "Param" & vbCrLf
                   If param IsNot Nothing AndAlso param.Length > 0 Then TextBoxDebugOutput.Text &= IOManager.Byte2Hex(param, True) & vbCrLf
                   TextBoxDebugOutput.Text &= vbCrLf & vbCrLf & "Sense" & vbCrLf
                   If sense IsNot Nothing AndAlso sense.Length > 0 Then TextBoxDebugOutput.Text &= IOManager.Byte2Hex(sense, True) & vbCrLf
                   TextBoxDebugOutput.Text &= TapeUtils.ParseSenseData(sense)
               End Sub)
    End Sub

    Private Sub ButtonDiagTest_Click(sender As Object, e As EventArgs) Handles ButtonDiagTest.Click
        If Not My.Settings.TapeUtils_DriverType = TapeUtils.DriverType.LTO OrElse Not MessageBox.Show(New Form With {.TopMost = True}, "Diagnostic write will corrupt data near target position. Continue?", "Warning", MessageBoxButtons.OKCancel) = DialogResult.OK Then
            Exit Sub
        End If
        Dim Sets As UInteger = CUInt(NumericUpDownTestSets.Value), Speed As Integer = CInt(NumericUpDownTestSpeed.Value), StartLen As Integer = CInt(NumericUpDownTestStartLen.Value), Wrap As Integer = CInt(NumericUpDownTestWrap.Value)
        Dim th As New Threading.Thread(
            Sub()
                Try
                    Dim cdbData() As Byte = {&H1D, &H11, 0, 0, &H24, 0}
                    Dim dataData() As Byte = {&H96, 0, 0, &H20,
                    CByte(Sets >> 24 And &HFF),
                    CByte(Sets >> 16 And &HFF),
                    CByte(Sets >> 8 And &HFF),
                    CByte(Sets >> 0 And &HFF),
                    0, 0, 0, 0, 0, 0, 0, 0,
                    0, 0, 0, 0,
                    CByte(Speed >> 24 And &HFF),
                    CByte(Speed >> 16 And &HFF),
                    CByte(Speed >> 8 And &HFF),
                    CByte(Speed >> 0 And &HFF),
                    CByte(StartLen >> 24 And &HFF),
                    CByte(StartLen >> 16 And &HFF),
                    CByte(StartLen >> 8 And &HFF),
                    CByte(StartLen >> 0 And &HFF),
                    CByte(Wrap >> 24 And &HFF),
                    CByte(Wrap >> 16 And &HFF),
                    CByte(Wrap >> 8 And &HFF),
                    CByte(Wrap >> 0 And &HFF),
                    0, 0, 0, 0}
                    Dim dataBufferPtr As IntPtr
                    dataBufferPtr = Marshal.AllocHGlobal(dataData.Length)
                    Marshal.Copy(dataData, 0, dataBufferPtr, dataData.Length)

                    Dim senseBuffer(63) As Byte
                    Dim succ As Boolean
                    SyncLock TapeUtils.GetSCSIOperationLock(ConfTapeDrive)
                        Dim handle As IntPtr
                        TapeUtils.OpenTapeDrive(ConfTapeDrive, handle)
                        succ = TapeUtils.TapeSCSIIOCtlUnmanaged(handle, cdbData, dataBufferPtr, CUInt(dataData.Length), 0, CUInt(CInt(TextBoxTimeoutValue.Text)), senseBuffer)
                        TapeUtils.CloseTapeDrive(handle)
                    End SyncLock
                    Marshal.Copy(dataBufferPtr, dataData, 0, dataData.Length)
                    Invoke(Sub()
                               PrintCommandResult(cdbData, dataData, senseBuffer)
                           End Sub)
                    Marshal.FreeHGlobal(dataBufferPtr)
                    If succ Then
                        Invoke(Sub() TextBoxDebugOutput.Text &= vbCrLf & "OK")
                    Else
                        Invoke(Sub() TextBoxDebugOutput.Text &= vbCrLf & "FAIL")
                    End If
                Catch ex As Exception
                    MessageBox.Show(New Form With {.TopMost = True}, ex.ToString)
                End Try
                Invoke(Sub() Panel2.Enabled = True)
            End Sub)
        Panel2.Enabled = False
        th.Start()
    End Sub

    Private Sub WriteToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles WriteToolStripMenuItem.Click
        If MessageBox.Show(New Form With {.TopMost = True}, "Write will destroy everything after current position. Continue?", "Warning", MessageBoxButtons.OKCancel) = DialogResult.OK Then
            If OpenFileDialog1.ShowDialog = DialogResult.OK Then
                Dim fname As String = OpenFileDialog1.FileName
                Dim th As New Threading.Thread(
                    Sub()
                        Dim fs As New FileStream(fname, FileMode.Open, FileAccess.Read, FileShare.Read)
                        If fs.Length = 0 Then
                            TapeUtils.WriteFileMark(ConfTapeDrive)
                            Invoke(Sub()
                                       TextBoxDebugOutput.Text = $"Filemark written."
                                       Panel1.Enabled = True
                                   End Sub)
                        Else
                            Invoke(Sub() TextBoxDebugOutput.Text = $"Writing: {fname}")
                            Dim buffer(CInt(Math.Min(NumericUpDownBlockLen.Value - 1, fs.Length - 1))) As Byte
                            While fs.Read(buffer, 0, buffer.Length) > 0
                                TapeUtils.Write(ConfTapeDrive, buffer)
                            End While
                            Invoke(Sub()
                                       TextBoxDebugOutput.Text = $"Write finished: {fname}"
                                       Panel1.Enabled = True
                                   End Sub)
                        End If
                        fs.Close()
                    End Sub)
                Panel1.Enabled = False
                th.Start()
            End If
        End If
    End Sub
    <Category("AudioPlayer")>
    Public Property sampleRate As Integer = 44100
    <Category("AudioPlayer")>
    Public Property channels As Short = 2
    <Category("AudioPlayer")>
    Public Property bitsPerSample As Short = 16
    <Category("AudioPlayer")>
    Public Property isFloat As Boolean = False
    Private Sub PlayPCMToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles PlayPCMToolStripMenuItem.Click
        For Each c As Control In Panel1.Controls
            c.Enabled = False
        Next
        Panel2.Enabled = True
        For Each c As Control In Panel2.Controls
            c.Enabled = False
        Next
        For Each c As Control In TabControl1.Controls
            c.Enabled = False
        Next
        For Each c As Control In TabPageCommand.Controls
            c.Enabled = False
        Next
        TabControl1.Enabled = True
        TabPageCommand.Enabled = True
        ButtonStopRawDump.Enabled = True
        TextBoxDebugOutput.Enabled = True
        TextBoxDebugOutput.Text = ""
        Dim log As Boolean = CheckBoxEnableDumpLog.Checked
        Dim thprog As New Threading.Thread(
                Sub()
                    Dim ReadLen As UInteger = CUInt(NumericUpDownBlockLen.Value)
                    Dim FileNum As Integer = 0

                    'Position
                    Dim pos As New TapeUtils.PositionData(ConfTapeDrive)

                    Dim Partition As Byte = pos.PartitionNumber
                    Dim Block As UInteger = CUInt(pos.BlockNumber)
                    Dim BlkNum As Integer = CInt(Block)
                    Dim player As New IOManager.StreamPcmPlayer()
                    Dim HeaderReaded As Boolean = False
                    Dim HeaderChanged As Boolean = False
                    Dim TotalPlayTime As New TimeSpan
                    Invoke(Sub()
                               TextBoxDebugOutput.AppendText($"Start playing at P{pos.PartitionNumber} B{pos.BlockNumber}{vbCrLf}")
                           End Sub)
                    While True
                        Dim sense(63) As Byte
                        pos = TapeUtils.ReadPosition(ConfTapeDrive)
                        Dim readData As Byte() = TapeUtils.ReadBlock(ConfTapeDrive, sense, ReadLen)
                        Dim Add_Key As UInt16 = CUShort(CInt(sense(12)) << 8 Or sense(13))
                        If readData.Length > 0 Then
                            Dim len0 As Integer = readData.Length
                            Dim analyzedChannels As Integer = channels
                            Dim analyzedBitsPerSample As Integer = bitsPerSample
                            readData = IOManager.WaveFileHelper.AnalyzeAndRemoveWavHeader(readData, sampleRate, analyzedChannels, analyzedBitsPerSample, isFloat, HeaderChanged)
                            channels = CShort(analyzedChannels)
                            bitsPerSample = CShort(analyzedBitsPerSample)
                            If len0 <> readData.Length Then
                                player.Flush()
                                While player.IsPlaying
                                    Threading.Thread.Sleep(100)
                                End While
                                player.StopPlayback()
                                HeaderChanged = True
                            End If
                            If Not HeaderReaded Or HeaderChanged Then
                                Invoke(Sub()
                                           TextBoxDebugOutput.AppendText($"RIFF header applied: {sampleRate}Hz {channels}ch {bitsPerSample}bit {If(isFloat, "Float", "Integer")}{vbCrLf}")
                                       End Sub)
                                player.Init(sampleRate, channels, bitsPerSample, isFloat, len0 <> readData.Length, len0 * 2)
                                HeaderReaded = True
                                HeaderChanged = False
                            End If
                            Invoke(Sub()
                                       TextBoxDebugOutput.AppendText($"[{Math.Truncate(TotalPlayTime.TotalHours).ToString().PadLeft(2, "0"c)}:{Math.Truncate(TotalPlayTime.Minutes).ToString().PadLeft(2, "0"c)}:{Math.Truncate(TotalPlayTime.Seconds).ToString().PadLeft(2, "0"c)}] {readData.Length} bytes readed at P{pos.PartitionNumber} B{pos.BlockNumber}.{vbCrLf}")
                                   End Sub)
                            player.AddData(readData, isFloat)
                            TotalPlayTime += New TimeSpan(CLng(CLng(readData.Length) * 8 / bitsPerSample / channels / sampleRate * 10000000))
                        End If
                        If Add_Key <> 0 Then
                            FileNum += 1
                            BlkNum = CInt(Block)
                        End If
                        If Me Is Nothing OrElse Visible = False OrElse (Add_Key > 1 And Add_Key <> 4) Or Operation_Cancel_Flag Then
                            Exit While
                        End If
                        If log Then
                            Invoke(Sub()
                                       If TextBoxDebugOutput.Text.Length > 10000 Then TextBoxDebugOutput.Text = ""
                                       TextBoxDebugOutput.AppendText("Processing file " & FileNum.ToString.PadRight(10) & " (Block = " & Block & ") Sense:")
                                       TextBoxDebugOutput.AppendText(TapeUtils.ParseAdditionalSenseCode(Add_Key) & vbCrLf)
                                   End Sub)
                        End If
                        Block = CUInt(Block + 1)
                    End While
                    If Operation_Cancel_Flag Then player.StopPlayback()
                    While player.IsPlaying And Not Operation_Cancel_Flag
                        Threading.Thread.Sleep(100)
                    End While
                    If HeaderReaded AndAlso (Not Operation_Cancel_Flag) Then player.StopPlayback()
                    Operation_Cancel_Flag = False
                    Invoke(Sub()
                               TextBoxDebugOutput.AppendText($"Stopped.")
                           End Sub)
                    Invoke(Sub()
                               For Each c As Control In Panel1.Controls
                                   c.Enabled = True
                               Next
                               For Each c As Control In Panel2.Controls
                                   c.Enabled = True
                               Next
                               For Each c As Control In TabControl1.Controls
                                   c.Enabled = True
                               Next
                               For Each c As Control In TabPageCommand.Controls
                                   c.Enabled = True
                               Next
                           End Sub)

                End Sub)
        thprog.Start()
    End Sub

    Private Sub ManualAddToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles ManualAddToolStripMenuItem.Click
        Dim newdev As New TapeUtils.BlockDevice
        Dim addfrm As New SettingPanel With {.SelectedObject = newdev}
        If addfrm.ShowDialog = DialogResult.OK Then
            If newdev.DevicePath = "" Then newdev.DevicePath = $"\\.\{newdev.DeviceType}{newdev.DevIndex}"
            If Not Directory.Exists(My.Settings.customDevicePath) Then Directory.CreateDirectory(My.Settings.customDevicePath)
            File.WriteAllText(Path.Combine(My.Settings.customDevicePath, $"{newdev.DevicePath.Replace("\", "_").Replace("/", "_").Replace(":", "_")}.xml"), newdev.GetSerializedText())
        End If
    End Sub
    Public Class devListBrowser
        <TypeConverter(GetType(ListTypeDescriptor(Of List(Of NativeDevice), NativeDevice)))>
        Public Property devList As List(Of NativeDevice)

    End Class
    Private Sub BrowseToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles BrowseToolStripMenuItem.Click
        Dim obj As New devListBrowser
        obj.devList = NativeMethods.EnumerateDevices().ToList()
        Dim pf As New SettingPanel With {.SelectedObject = obj}
        pf.Show()
    End Sub

    Private Sub WritePCMToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles WritePCMToolStripMenuItem.Click
        If MessageBox.Show(New Form With {.TopMost = True}, "Write will destroy everything after current position. Continue?", "Warning", MessageBoxButtons.OKCancel) = DialogResult.OK Then
            If OpenFileDialog1.ShowDialog = DialogResult.OK Then
                Dim fname As String = OpenFileDialog1.FileName
                Dim th As New Threading.Thread(
                    Sub()
                        Dim finfo As New FileInfo(fname)
                        Dim reader As WaveStream
                        Select Case finfo.Extension.ToLower()
                            Case ".mp3"
                                reader = New Mp3FileReader(fname)
                            Case ".aiff"
                                reader = New AiffFileReader(fname)
                            Case ".flac"
                                reader = New NAudio.Flac.FlacReader(fname)
                            Case Else
                                Try
                                    reader = New MediaFoundationReader(fname)
                                Catch ex As Exception
                                    reader = New AudioFileReader(fname)
                                End Try
                        End Select
                        Dim tempfile As String = My.Computer.FileSystem.GetTempFileName()
                        WaveFileWriter.CreateWaveFile(tempfile, reader)

                        Dim fs As New FileStream(tempfile, FileMode.Open, FileAccess.Read, FileShare.Read)
                        If fs.Length = 0 Then
                            TapeUtils.WriteFileMark(ConfTapeDrive)
                            Invoke(Sub()
                                       TextBoxDebugOutput.Text = $"Filemark written."
                                       Panel1.Enabled = True
                                   End Sub)
                        Else
                            Invoke(Sub() TextBoxDebugOutput.Text = $"Writing: {fname}")
                            Dim buffer(CInt(Math.Min(NumericUpDownBlockLen.Value - 1, fs.Length - 1))) As Byte
                            While fs.Read(buffer, 0, buffer.Length) > 0
                                TapeUtils.Write(ConfTapeDrive, buffer)
                            End While
                            Invoke(Sub()
                                       TextBoxDebugOutput.Text = $"Write finished: {fname}"
                                       Panel1.Enabled = True
                                   End Sub)
                        End If
                        fs.Close()
                        File.Delete(tempfile)
                    End Sub)
                Panel1.Enabled = False
                th.Start()
            End If
        End If
    End Sub

    Public Property CMDList As List(Of SCSICMD)
        Get
            If My.Settings.LTFSConf_CMDList Is Nothing Then
                My.Settings.LTFSConf_CMDList = New List(Of SCSICMD)
            End If
            Return My.Settings.LTFSConf_CMDList
        End Get
        Set(value As List(Of SCSICMD))
            My.Settings.LTFSConf_CMDList = value
        End Set
    End Property

    Public Sub LoadCMD()
        While ContextMenuStripSend.Items.Count > 3
            ContextMenuStripSend.Items.RemoveAt(0)
        End While
        Dim lcmd As List(Of SCSICMD) = CMDList
        Dim idx As Integer = 0
        For Each cmd As SCSICMD In lcmd
            Dim item As New ToolStripMenuItem()
            item.Text = cmd.Name
            item.Tag = cmd
            AddHandler item.Click,
                Sub()
                    With CType(item.Tag, SCSICMD)
                        TextBoxCDBData.Text = .CDB
                        TextBoxParamData.Text = .Param
                        TextBoxDataDir.Text = .DataDIR
                        TextBoxTimeoutValue.Text = .Timeout
                    End With
                End Sub
            ContextMenuStripSend.Items.Insert(idx, item)
            idx += 1
        Next
    End Sub
    Private Sub 保存ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 保存ToolStripMenuItem.Click
        Dim cmdName As String = "Command"
        If DisplayHelper.ShowInputDialog($"Name", "Save command", cmdName) <> DialogResult.OK Then Exit Sub
        Dim cmd As New SCSICMD With {.Name = cmdName, .CDB = TextBoxCDBData.Text, .Param = TextBoxParamData.Text, .DataDIR = TextBoxDataDir.Text, .Timeout = TextBoxTimeoutValue.Text}

        Dim lcmd As List(Of SCSICMD) = CMDList
        Dim cmdExist As Boolean = False
        For i As Integer = 0 To lcmd.Count - 1
            With lcmd(i)
                If .CDB = cmd.CDB AndAlso .Param = cmd.Param AndAlso .DataDIR = cmd.DataDIR AndAlso .Timeout = cmd.Timeout Then
                    .Name = cmdName
                    CMDList = lcmd
                    My.Settings.Save()
                    cmdExist = True
                    Exit For
                End If
            End With
        Next
        If Not cmdExist Then
            lcmd.Add(cmd)
            CMDList = lcmd
            My.Settings.Save()
        End If
        LoadCMD()
    End Sub

    Private Sub 删除ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 删除ToolStripMenuItem.Click
        Dim cmd As New SCSICMD With {.Name = "", .CDB = TextBoxCDBData.Text, .Param = TextBoxParamData.Text, .DataDIR = TextBoxDataDir.Text, .Timeout = TextBoxTimeoutValue.Text}
        Dim lcmd As List(Of SCSICMD) = CMDList
        Dim cmdExist As Boolean = False
        For i As Integer = 0 To lcmd.Count - 1
            With lcmd(i)
                If .CDB = cmd.CDB AndAlso .Param = cmd.Param AndAlso .DataDIR = cmd.DataDIR AndAlso .Timeout = cmd.Timeout Then
                    lcmd.RemoveAt(i)
                    CMDList = lcmd
                    My.Settings.Save()
                    LoadCMD()
                    Exit For
                End If
            End With
        Next
    End Sub
    <Serializable>
    Public Class SCSICMD
        Public Property Name As String = ""
        Public Property CDB As String = ""
        Public Property Param As String = ""
        Public Property DataDIR As String = ""
        Public Property Timeout As String = ""
    End Class

    Private Sub DebugToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles DebugToolStripMenuItem.Click
        Dim drvhandle As IntPtr
        TapeUtils.OpenTapeDrive(ConfTapeDrive, drvhandle)
        Dim disk As New ZBCDeviceHelper With {.handle = drvhandle}
        disk.InitDevice()
        Dim testdata(4095) As Byte
        testdata(0) = &H7F
        testdata(4095) = &HFF
        Dim FirstSMRLBA As ULong = CULng(disk.CMREndLBA + 1)
        disk.WriteBytes(testdata, FirstSMRLBA, 0, False)
        Dim readout As Byte() = disk.ReadBytes(FirstSMRLBA, 0, 4096)
        MessageBox.Show("OK")
        TapeUtils.CloseTapeDrive(drvhandle)
    End Sub

    Private Sub AddImageToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles AddImageToolStripMenuItem.Click
        Dim newdev As New TapeUtils.BlockDevice With {.DevicePath = "C:\image.lcgidx", .ProductId = "TapeImage"}
        Dim addfrm As New SettingPanel With {.SelectedObject = newdev}
        If addfrm.ShowDialog = DialogResult.OK Then
            If newdev.DevicePath = "" Then newdev.DevicePath = $"\\.\{newdev.DeviceType}{newdev.DevIndex}"
            If Not Directory.Exists(My.Settings.customDevicePath) Then Directory.CreateDirectory(My.Settings.customDevicePath)
            File.WriteAllText(Path.Combine(My.Settings.customDevicePath, $"{newdev.DevicePath.Replace("\", "_").Replace("/", "_").Replace(":", "_")}.xml"), newdev.GetSerializedText())
        End If
    End Sub

    Private Sub ButtonReportZones_Click(sender As Object, e As EventArgs) Handles ButtonReportZones.Click
        Enabled = False
        Dim opt As Byte = IOManager.HexStringToByteArray(ComboBoxReportOptions.Text.Substring(0, 2))(0)
        Task.Run(Sub()
                     Dim drvhandle As IntPtr
                     TapeUtils.OpenTapeDrive(ConfTapeDrive, drvhandle)
                     Dim disk As New ZBCDeviceHelper With {.handle = drvhandle}
                     Dim MP03 As Byte() = TapeUtils.ModeSense(drvhandle, 3)
                     disk.SectorLength = CUShort(BigEndianConverter.ToUInt16(MP03, 12))
                     Invoke(Sub() NumericUpDownSectorSize.Value = If(disk.SectorLength > 0, disk.SectorLength, NumericUpDownSectorSize.Value))
                     disk.ReportZones(opt)
                     Dim output As New StringBuilder
                     output.AppendLine($"     #  ZONE_TYPE    ZONE_CONDITION    START_LBA    END_LBA   WP_LBA   WP_VALIDITY  SEQ    RESET ")
                     For i As Integer = 0 To disk.ZoneList.Count - 1
                         With disk.ZoneList(i)
                             output.AppendLine($"{i.ToString().PadLeft(6)}{ _
                            .ZoneType.ToString().PadLeft(13)}{ _
                            .ZoneCondition.ToString().PadLeft(18)}{ _
                            .ZoneStartLBA.ToString().PadLeft(11)}{ _
                            .ZoneEndLBA.ToString().PadLeft(11)}{ _
                            .ZoneWritePointerLBA.ToString().PadLeft(11)}{ _
                            If(.WRITER_POINTER_LBA_INVALID, " WP_Invalid", " WP_Valid  ")}{ _
                            If(.NON_SEQ, " NON_SEQ ", "   SEQ   ")}{ _
                            If(.RESET, " RESET ", "       ")}")
                         End With
                     Next
                     TapeUtils.CloseTapeDrive(drvhandle)
                     Invoke(Sub()
                                TextBoxDebugOutput.Text = output.ToString()
                                Enabled = True
                            End Sub)
                 End Sub)

    End Sub

    Private Sub ButtonCloseAllZones_Click(sender As Object, e As EventArgs) Handles ButtonCloseAllZones.Click
        Enabled = False
        Task.Run(Sub()
                     Dim senseData(63) As Byte
                     Dim cdb As Byte() = {&H94, &H1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0}
                     Dim data As IntPtr = Marshal.AllocHGlobal(1)
                     Dim handle As IntPtr
                     SyncLock TapeUtils.GetSCSIOperationLock(ConfTapeDrive)
                         TapeUtils.OpenTapeDrive(ConfTapeDrive, handle)
                         TapeUtils.TapeSCSIIOCtlUnmanaged(handle, cdb, data, 0, 1, 30, senseData)
                         TapeUtils.CloseTapeDrive(handle)
                     End SyncLock
                     Marshal.FreeHGlobal(data)

                     PrintCommandResult(cdb, Nothing, senseData)
                     Invoke(Sub() Enabled = True)
                 End Sub)
    End Sub

    Private Sub ButtonReadLBA_Click(sender As Object, e As EventArgs) Handles ButtonReadLBA.Click
        Enabled = False
        Dim StartLBA As ULong = CULng(NumericUpDownLBA.Value)
        Dim sectorSize As Integer = CInt(NumericUpDownSectorSize.Value)
        Task.Run(Sub()
                     Dim senseData(63) As Byte
                     Dim result As Byte() = {}
                     Dim cdb As Byte() = {&H28, 0,
                        CByte(CLng((StartLBA >> 24)) And &HFF),
                        CByte(CLng((StartLBA >> 16)) And &HFF),
                        CByte(CLng((StartLBA >> 8)) And &HFF),
                        CByte(CLng((StartLBA >> 0)) And &HFF),
                        0, 0, 1, 0}
                     Dim data As IntPtr = Marshal.AllocHGlobal(1)
                     Dim handle As IntPtr
                     SyncLock TapeUtils.GetSCSIOperationLock(ConfTapeDrive)
                         TapeUtils.OpenTapeDrive(ConfTapeDrive, handle)
                         result = TapeUtils.SCSIReadParam(handle, cdb, sectorSize, Function(s As Byte()) As Boolean
                                                                                       senseData = s
                                                                                       Return True
                                                                                   End Function, 30)
                         TapeUtils.CloseTapeDrive(handle)
                     End SyncLock
                     Marshal.FreeHGlobal(data)
                     PrintCommandResult(cdb, result, senseData)
                     Invoke(Sub() Enabled = True)
                 End Sub)
    End Sub

    Private Sub ButtonReportZone_Click(sender As Object, e As EventArgs) Handles ButtonReportZone.Click
        Dim StartLBA As ULong = CULng(NumericUpDownLBA.Value)
        Enabled = False
        Dim LBA As ULong = CULng(NumericUpDownLBA.Value)
        Task.Run(Sub()
                     Dim senseData(63) As Byte
                     Dim drvhandle As IntPtr
                     TapeUtils.OpenTapeDrive(ConfTapeDrive, drvhandle)
                     Dim cdb As Byte() = {&H95, 0,
                             CByte(CLng((StartLBA >> 56)) And &HFF),
                             CByte(CLng((StartLBA >> 48)) And &HFF),
                             CByte(CLng((StartLBA >> 40)) And &HFF),
                             CByte(CLng((StartLBA >> 32)) And &HFF),
                             CByte(CLng((StartLBA >> 24)) And &HFF),
                             CByte(CLng((StartLBA >> 16)) And &HFF),
                             CByte(CLng((StartLBA >> 8)) And &HFF),
                             CByte(CLng((StartLBA >> 0)) And &HFF),
                             0, 0, 0, 128, &H80, 0}
                     Dim data1 As Byte() = TapeUtils.SCSIReadParam(drvhandle, cdb, 128, Function(s As Byte()) As Boolean
                                                                                            senseData = s
                                                                                            Return True
                                                                                        End Function, 60)
                     Dim readed As New ZBCDeviceHelper.Zone(data1, 64)
                     Dim output As New StringBuilder
                     output.AppendLine($"     #  ZONE_TYPE    ZONE_CONDITION    START_LBA    END_LBA   WP_LBA   WP_VALIDITY  SEQ    RESET ")
                     With readed
                         output.AppendLine($"{"".PadLeft(6)}{ _
                            .ZoneType.ToString().PadLeft(13)}{ _
                            .ZoneCondition.ToString().PadLeft(18)}{ _
                            .ZoneStartLBA.ToString().PadLeft(11)}{ _
                            .ZoneEndLBA.ToString().PadLeft(11)}{ _
                            .ZoneWritePointerLBA.ToString().PadLeft(11)}{ _
                            If(.WRITER_POINTER_LBA_INVALID, " WP_Invalid", " WP_Valid  ")}{ _
                            If(.NON_SEQ, " NON_SEQ ", "   SEQ   ")}{ _
                            If(.RESET, " RESET ", "       ")}")
                     End With
                     output.AppendLine()
                     TapeUtils.CloseTapeDrive(drvhandle)

                     Invoke(Sub()
                                PrintCommandResult(cdb, data1, senseData)
                                TextBoxDebugOutput.Text = output.ToString() & TextBoxDebugOutput.Text
                                Enabled = True
                            End Sub)
                 End Sub)

    End Sub

    Private Sub ButtonOpenZone_Click(sender As Object, e As EventArgs) Handles ButtonOpenZone.Click
        Enabled = False
        Dim LBA As ULong = CULng(NumericUpDownLBA.Value)
        Task.Run(Sub()
                     Dim senseData(63) As Byte
                     Dim cdb As Byte() = {&H94, &H3,
                                          CByte(CLng((LBA >> 56)) And &HFF),
                                          CByte(CLng((LBA >> 48)) And &HFF),
                                          CByte(CLng((LBA >> 40)) And &HFF),
                                          CByte(CLng((LBA >> 32)) And &HFF),
                                          CByte(CLng((LBA >> 24)) And &HFF),
                                          CByte(CLng((LBA >> 16)) And &HFF),
                                          CByte(CLng((LBA >> 8)) And &HFF),
                                          CByte(CLng((LBA >> 0)) And &HFF),
                                          0, 0, 0, 0, 0, 0}
                     Dim data As IntPtr = Marshal.AllocHGlobal(1)
                     Dim handle As IntPtr
                     SyncLock TapeUtils.GetSCSIOperationLock(ConfTapeDrive)
                         TapeUtils.OpenTapeDrive(ConfTapeDrive, handle)
                         TapeUtils.TapeSCSIIOCtlUnmanaged(handle, cdb, data, 0, 1, 30, senseData)
                         TapeUtils.CloseTapeDrive(handle)
                     End SyncLock
                     Marshal.FreeHGlobal(data)

                     PrintCommandResult(cdb, Nothing, senseData)
                     Invoke(Sub() Enabled = True)
                 End Sub)
    End Sub

    Private Sub ButtonCloseZone_Click(sender As Object, e As EventArgs) Handles ButtonCloseZone.Click
        Enabled = False
        Dim LBA As ULong = CULng(NumericUpDownLBA.Value)
        Task.Run(Sub()
                     Dim senseData(63) As Byte
                     Dim cdb As Byte() = {&H94, &H1,
                                          CByte(CLng((LBA >> 56)) And &HFF),
                                          CByte(CLng((LBA >> 48)) And &HFF),
                                          CByte(CLng((LBA >> 40)) And &HFF),
                                          CByte(CLng((LBA >> 32)) And &HFF),
                                          CByte(CLng((LBA >> 24)) And &HFF),
                                          CByte(CLng((LBA >> 16)) And &HFF),
                                          CByte(CLng((LBA >> 8)) And &HFF),
                                          CByte(CLng((LBA >> 0)) And &HFF),
                                          0, 0, 0, 0, 0, 0}
                     Dim data As IntPtr = Marshal.AllocHGlobal(1)
                     Dim handle As IntPtr
                     SyncLock TapeUtils.GetSCSIOperationLock(ConfTapeDrive)
                         TapeUtils.OpenTapeDrive(ConfTapeDrive, handle)
                         TapeUtils.TapeSCSIIOCtlUnmanaged(handle, cdb, data, 0, 1, 30, senseData)
                         TapeUtils.CloseTapeDrive(handle)
                     End SyncLock
                     Marshal.FreeHGlobal(data)

                     PrintCommandResult(cdb, Nothing, senseData)
                     Invoke(Sub() Enabled = True)
                 End Sub)
    End Sub

    Private Sub ButtonFinishZone_Click(sender As Object, e As EventArgs) Handles ButtonFinishZone.Click
        Enabled = False
        Dim LBA As ULong = CULng(NumericUpDownLBA.Value)
        Task.Run(Sub()
                     Dim senseData(63) As Byte
                     Dim cdb As Byte() = {&H94, &H2,
                                          CByte(CLng((LBA >> 56)) And &HFF),
                                          CByte(CLng((LBA >> 48)) And &HFF),
                                          CByte(CLng((LBA >> 40)) And &HFF),
                                          CByte(CLng((LBA >> 32)) And &HFF),
                                          CByte(CLng((LBA >> 24)) And &HFF),
                                          CByte(CLng((LBA >> 16)) And &HFF),
                                          CByte(CLng((LBA >> 8)) And &HFF),
                                          CByte(CLng((LBA >> 0)) And &HFF),
                                          0, 0, 0, 0, 0, 0}
                     Dim data As IntPtr = Marshal.AllocHGlobal(1)
                     Dim handle As IntPtr
                     SyncLock TapeUtils.GetSCSIOperationLock(ConfTapeDrive)
                         TapeUtils.OpenTapeDrive(ConfTapeDrive, handle)
                         TapeUtils.TapeSCSIIOCtlUnmanaged(handle, cdb, data, 0, 1, 30, senseData)
                         TapeUtils.CloseTapeDrive(handle)
                     End SyncLock
                     Marshal.FreeHGlobal(data)

                     PrintCommandResult(cdb, Nothing, senseData)
                     Invoke(Sub() Enabled = True)
                 End Sub)
    End Sub

    Private Sub ButtonResetWritePointer_Click(sender As Object, e As EventArgs) Handles ButtonResetWritePointer.Click
        Enabled = False
        Dim LBA As ULong = CULng(NumericUpDownLBA.Value)
        Task.Run(Sub()
                     Dim senseData(63) As Byte
                     Dim cdb As Byte() = {&H94, &H4,
                                          CByte(CLng((LBA >> 56)) And &HFF),
                                          CByte(CLng((LBA >> 48)) And &HFF),
                                          CByte(CLng((LBA >> 40)) And &HFF),
                                          CByte(CLng((LBA >> 32)) And &HFF),
                                          CByte(CLng((LBA >> 24)) And &HFF),
                                          CByte(CLng((LBA >> 16)) And &HFF),
                                          CByte(CLng((LBA >> 8)) And &HFF),
                                          CByte(CLng((LBA >> 0)) And &HFF),
                                          0, 0, 0, 0, 0, 0}
                     Dim data As IntPtr = Marshal.AllocHGlobal(1)
                     Dim handle As IntPtr
                     SyncLock TapeUtils.GetSCSIOperationLock(ConfTapeDrive)
                         TapeUtils.OpenTapeDrive(ConfTapeDrive, handle)
                         TapeUtils.TapeSCSIIOCtlUnmanaged(handle, cdb, data, 0, 1, 30, senseData)
                         TapeUtils.CloseTapeDrive(handle)
                     End SyncLock
                     Marshal.FreeHGlobal(data)

                     PrintCommandResult(cdb, Nothing, senseData)
                     Invoke(Sub() Enabled = True)
                 End Sub)
    End Sub

    Private Sub ButtonWriteRandom_Click(sender As Object, e As EventArgs) Handles ButtonWriteRandom.Click
        Enabled = False
        Dim LBA As ULong = CULng(NumericUpDownLBA.Value)
        Dim sectorSize As Integer = CInt(NumericUpDownSectorSize.Value)
        Dim count As Integer = CInt(NumericUpDownWriteLBACount.Value)
        Task.Run(Sub()
                     Dim senseData(63) As Byte
                     Dim toSend(sectorSize - 1) As Byte
                     Dim drvhandle As IntPtr
                     TapeUtils.OpenTapeDrive(ConfTapeDrive, drvhandle)
                     Dim cdb As Byte() = Array.Empty(Of Byte)()
                     Dim r As New Random
                     For i As Integer = 1 To count
                         r.NextBytes(toSend)
                         cdb = {&H2A, 0,
                                          CByte((CLng((LBA + i - 1)) >> 24) And &HFF),
                                          CByte((CLng((LBA + i - 1)) >> 16) And &HFF),
                                          CByte((CLng((LBA + i - 1)) >> 8) And &HFF),
                                          CByte((CLng((LBA + i - 1)) >> 0) And &HFF),
                                          0, 0, 1, 0}

                         TapeUtils.SendSCSICommand(drvhandle, cdb, toSend, 0, Function(s As Byte()) As Boolean
                                                                                  senseData = s
                                                                                  Return True
                                                                              End Function)
                     Next
                     TapeUtils.CloseTapeDrive(drvhandle)


                     PrintCommandResult(cdb, toSend, senseData)
                     Invoke(Sub() Enabled = True)
                 End Sub)


    End Sub

    Private Sub ButtonDumpLBA_Click(sender As Object, e As EventArgs) Handles ButtonDumpLBA.Click
        If SaveFileDialog2.ShowDialog() = DialogResult.OK Then
            Enabled = False
            Dim LBA As ULong = CULng(NumericUpDownLBA.Value)
            Dim sectorSize As Integer = CInt(NumericUpDownSectorSize.Value)
            Dim blocklim As Integer = 524288
            Dim batch As Integer = blocklim \ sectorSize
            blocklim = batch * sectorSize
            Dim count As Integer = CInt(NumericUpDownWriteLBACount.Value)
            Dim progmsg As String = ""
            Dim fin As Boolean = False
            Task.Run(Sub()
                         While Not fin
                             Threading.Thread.Sleep(100)
                             Invoke(Sub() TextBoxDebugOutput.Text = progmsg)
                         End While
                         Invoke(Sub() TextBoxDebugOutput.Text = progmsg)
                     End Sub)
            Task.Run(Async Function()
                         Using fs As New FileStream(SaveFileDialog2.FileName, FileMode.Create)
                             Dim senseData(63) As Byte
                             Dim handle As IntPtr
                             TapeUtils.OpenTapeDrive(ConfTapeDrive, handle)
                             Dim stopflag As Boolean = False
                             For i As ULong = LBA To CULng(LBA + count - 1) Step CULng(batch)
                                 If stopflag Then Exit For
                                 Dim readsize As ULong = CULng(blocklim)
                                 If i + batch > LBA + count Then
                                     readsize = CULng((LBA + count - i) * sectorSize)
                                 End If

                                 Dim result As Byte() = ArrayPool(Of Byte).Shared.Rent(CInt(readsize))
                                 Dim cdb As Byte() = {&H28, 0,
                                        CByte(CLng((i >> 24)) And &HFF),
                                        CByte(CLng((i >> 16)) And &HFF),
                                        CByte(CLng((i >> 8)) And &HFF),
                                        CByte(CLng((i >> 0)) And &HFF),
                                        0,
                                        CByte((batch >> 8) And &HFF),
                                        CByte((batch >> 0) And &HFF), 0}
                                 Dim readsucc As Boolean = False
                                 While Not readsucc
                                     result = TapeUtils.SCSIReadParam(handle, cdb, CInt(readsize), Function(s As Byte()) As Boolean
                                                                                                       senseData = s
                                                                                                       Return True
                                                                                                   End Function, 30)
                                     If (senseData(2) And &HF) <> 0 Then
                                         progmsg = $"sense err {TapeUtils.Byte2Hex(senseData, True)}{vbCrLf}"
                                         Try
                                             Throw New Exception("SCSI sense error")
                                         Catch ex As Exception
                                             Select Case MessageBox.Show(New Form With {.TopMost = True}, $"{My.Resources.ResText_RestoreErr}{vbCrLf}{TapeUtils.ParseSenseData(senseData)}{vbCrLf}{vbCrLf}sense{vbCrLf}{TapeUtils.Byte2Hex(senseData, True)}{vbCrLf}{ex.StackTrace}", My.Resources.ResText_Warning, MessageBoxButtons.AbortRetryIgnore)
                                                 Case DialogResult.Abort
                                                     fs.Close()
                                                     stopflag = True
                                                     Throw New Exception(TapeUtils.ParseSenseData(senseData))
                                                 Case DialogResult.Retry
                                                     readsucc = False
                                                 Case DialogResult.Ignore
                                                     readsucc = True
                                                     Exit While
                                             End Select
                                         End Try
                                     Else
                                         readsucc = True
                                     End If
                                 End While
                                 progmsg = $"LBA {LBA}>{i}>{LBA + count - 1}"
                                 Await WriteAndFree(fs, result, 0, CInt(readsize), ArrayPool(Of Byte).Shared)
                             Next
                             progmsg = $"LBA {LBA}>{LBA + count - 1}>{LBA + count - 1}"
                             TapeUtils.CloseTapeDrive(handle)
                         End Using
                         fin = True
                         Invoke(Sub() Enabled = True)
                     End Function)
        End If
    End Sub
    Public Async Function WriteAndFree(fs As FileStream, toWrite As Byte(), offset As Integer, count As Integer, pool As ArrayPool(Of Byte)) As Task
        Await fs.WriteAsync(toWrite, offset, count)
        pool.Return(toWrite)
    End Function

    Private Sub ButtonRestoreLBA_Click(sender As Object, e As EventArgs) Handles ButtonRestoreLBA.Click
        If OpenFileDialog1.ShowDialog = DialogResult.OK Then
            Enabled = False
            Dim LBA As ULong = CULng(NumericUpDownLBA.Value)
            Dim sectorSize As Integer = CInt(NumericUpDownSectorSize.Value)
            Dim blocklim As Integer = 524288
            Dim batch As Integer = blocklim \ sectorSize
            blocklim = batch * sectorSize
            Dim count As Integer = 0
            Dim progmsg As String = ""
            Dim fin As Boolean = False
            Task.Run(Sub()
                         While Not fin
                             Threading.Thread.Sleep(100)
                             Invoke(Sub() TextBoxDebugOutput.Text = progmsg)
                         End While
                         Invoke(Sub() TextBoxDebugOutput.Text = progmsg)
                     End Sub)
            Task.Run(Async Function()
                         Using fs As New FileStream(OpenFileDialog1.FileName, FileMode.Open)

                             Dim senseData(63) As Byte
                             Dim handle As IntPtr
                             TapeUtils.OpenTapeDrive(ConfTapeDrive, handle)
                             Dim writeLBACount As Integer
                             Dim stopflag As Boolean = False
                             For i As ULong = LBA To CULng(LBA + count - 1) Step CULng(batch)
                                 If stopflag Then Exit For
                                 Dim currentLBA As ULong = i
                                 Dim writeData As Byte() = ArrayPool(Of Byte).Shared.Rent(blocklim)
                                 Dim readsize As ULong = CULng(fs.Read(writeData, 0, blocklim))
                                 writeLBACount = CInt(Math.Ceiling(readsize / sectorSize))
                                 Dim writeSize As Integer = writeLBACount * sectorSize
                                 Dim cdb As Byte() = {&H2A, 0,
                                        CByte(CLng((i >> 24)) And &HFF),
                                        CByte(CLng((i >> 16)) And &HFF),
                                        CByte(CLng((i >> 8)) And &HFF),
                                        CByte(CLng((i >> 0)) And &HFF),
                                        0,
                                        CByte((writeLBACount >> 8) And &HFF),
                                        CByte((writeLBACount >> 0) And &HFF), 0}
                                 Dim writesucc As Boolean = False
                                 Await Task.Run(Sub()
                                                    While Not writesucc
                                                        If Not TapeUtils.SendSCSICommand(handle, cdb, writeData, writeSize, 0, Function(s As Byte()) As Boolean
                                                                                                                                   senseData = s
                                                                                                                                   Return True
                                                                                                                               End Function, 30) Then
                                                            Dim ErrCode As Integer = TapeUtils.LastWin32Error
                                                            Dim win32ex As New Win32Exception(ErrCode)
                                                            Select Case MessageBox.Show($"WinError{vbCrLf}Code: 0x{ErrCode.ToString("X8")}h{vbCrLf}{win32ex.Message}",
                                                                                        My.Resources.ResText_Warning,
                                                                                        MessageBoxButtons.AbortRetryIgnore,
                                                                                        Nothing,
                                                                                        MessageBoxDefaultButton.Button2,
                                                                                        MessageBoxOptions.DefaultDesktopOnly)
                                                                Case DialogResult.Abort
                                                                    fs.Close()
                                                                    stopflag = True
                                                                    ArrayPool(Of Byte).Shared.Return(writeData)
                                                                    Throw win32ex
                                                                Case DialogResult.Retry
                                                                    writesucc = False
                                                                Case DialogResult.Ignore
                                                                    writesucc = True
                                                                    Exit While
                                                            End Select
                                                        ElseIf (senseData(2) And &HF) <> 0 Then
                                                            progmsg = $"sense err {TapeUtils.Byte2Hex(senseData, True)}{vbCrLf}"
                                                            Try
                                                                Throw New Exception("SCSI sense error")
                                                            Catch ex As Exception
                                                                Select Case MessageBox.Show(New Form With {.TopMost = True}, $"{My.Resources.ResText_Error}{vbCrLf}{TapeUtils.ParseSenseData(senseData)}{vbCrLf}{vbCrLf}sense{vbCrLf}{TapeUtils.Byte2Hex(senseData, True)}{vbCrLf}{ex.StackTrace}", My.Resources.ResText_Warning, MessageBoxButtons.AbortRetryIgnore)
                                                                    Case DialogResult.Abort
                                                                        fs.Close()
                                                                        stopflag = True
                                                                        ArrayPool(Of Byte).Shared.Return(writeData)
                                                                        Throw New Exception(TapeUtils.ParseSenseData(senseData))
                                                                    Case DialogResult.Retry
                                                                        writesucc = False
                                                                    Case DialogResult.Ignore
                                                                        writesucc = True
                                                                        Exit While
                                                                End Select
                                                            End Try
                                                        Else
                                                            writesucc = True
                                                        End If
                                                    End While
                                                    ArrayPool(Of Byte).Shared.Return(writeData)
                                                    progmsg = $"LBA {LBA}>{currentLBA}>{LBA + writeLBACount - 1}"
                                                End Sub)

                             Next
                             progmsg = $"LBA {LBA}>{LBA + writeLBACount - 1}>{LBA + writeLBACount - 1}"
                             TapeUtils.CloseTapeDrive(handle)
                         End Using
                         fin = True
                         Invoke(Sub() Enabled = True)
                     End Function)
        End If
    End Sub

    Private Sub ButtonBOT_Click(sender As Object, e As EventArgs) Handles ButtonBOT.Click
        Enabled = False
        Select Case TapeUtils.DriverTypeSetting
            Case TapeUtils.DriverType.LTO, TapeUtils.DriverType.IBM3592
                Dim CMInfo As TapeUtils.CMParser = Nothing
                Task.Run(Sub()
                             Try
                                 CMInfo = New TapeUtils.CMParser(ConfTapeDrive)
                             Catch
                                 Enabled = True
                                 Exit Sub
                             End Try
                             Invoke(Sub()
                                        With CMInfo.InitialisationData
                                            If .LP1 > 0 AndAlso .LP3 > .LP1 AndAlso .LP5 > .LP3 Then
                                                Select Case CMInfo.CartridgeMfgData.CartridgeTypeAbbr
                                                    Case "L1", "L2"
                                                        NumericUpDownTestStartLen.Value = CDec(Math.Round((.LP3 - .LP1) * 7.2 + 7100 + 100))
                                                    Case "L3"
                                                        NumericUpDownTestStartLen.Value = CDec(Math.Round((.LP3 - .LP1) * 7.2 + 8100 + 100))
                                                    Case "L4", "L5", "L6"
                                                        NumericUpDownTestStartLen.Value = CDec(Math.Round((.LP3 - .LP1) * 7.2 + 9600 + 100))
                                                End Select
                                            End If
                                        End With
                                        Enabled = True
                                    End Sub)
                         End Sub)
            Case Else
                Enabled = True
        End Select
    End Sub

    Private Sub ButtonEOT_Click(sender As Object, e As EventArgs) Handles ButtonEOT.Click
        Enabled = False
        Select Case TapeUtils.DriverTypeSetting
            Case TapeUtils.DriverType.LTO, TapeUtils.DriverType.IBM3592
                Dim CMInfo As TapeUtils.CMParser = Nothing
                Task.Run(Sub()
                             Try
                                 CMInfo = New TapeUtils.CMParser(ConfTapeDrive)
                             Catch
                                 Enabled = True
                                 Exit Sub
                             End Try
                             Invoke(Sub()
                                        With CMInfo.InitialisationData
                                            If .LP1 > 0 AndAlso .LP3 > .LP1 AndAlso .LP5 > .LP3 Then
                                                Select Case CMInfo.CartridgeMfgData.CartridgeTypeAbbr
                                                    Case "L1", "L2", "L3", "L4", "L5", "L6"
                                                        NumericUpDownTestStartLen.Value = CDec(Math.Round(.LP5 - .LP3) * 7.2)
                                                End Select
                                            End If
                                        End With
                                        Enabled = True
                                    End Sub)
                         End Sub)
            Case Else
                Enabled = True
        End Select
    End Sub

    Private Sub ButtonDebugWriteBuffer_Click(sender As Object, e As EventArgs) Handles ButtonDebugWriteBuffer.Click
        If OpenFileDialog1.ShowDialog = DialogResult.OK Then
            Enabled = False
            Dim selectedBufferPage As String = Convert.ToString(ComboBoxBufferPage.SelectedItem)
            Dim BufferID As Byte = Convert.ToByte(selectedBufferPage.Substring(0, 2), 16)
            Dim Mode As Byte = CByte(NumericUpDownRBMode.Value)
            Task.Run(Sub()
                         Dim BufferData As Byte() = File.ReadAllBytes(OpenFileDialog1.FileName)
                         TapeUtils.WriteBuffer(ConfTapeDrive, BufferID, Mode, BufferData)
                         Invoke(Sub()
                                    TextBoxDebugOutput.Text = "Buffer len=" & BufferData.Length & vbCrLf
                                    TextBoxDebugOutput.Text &= IOManager.Byte2Hex(BufferData, True)
                                    Enabled = True
                                End Sub)
                     End Sub)
        End If
    End Sub

    Private Sub SCSIAdapterToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles SCSIAdapterToolStripMenuItem.Click
        LoadComplete = False
        CheckBoxAutoRefresh.Checked = False
        ListBox1.Items.Clear()
        Dim DevList As List(Of TapeUtils.BlockDevice)
        LastDeviceList = TapeUtils.GetHBAList()
        DevList = LastDeviceList
        For Each D As TapeUtils.BlockDevice In DevList
            D.DeviceType = "SCSIAdapter"
            ListBox1.Items.Add(D.ToString())
        Next
        ListBox1.SelectedIndex = Math.Min(SelectedIndex, ListBox1.Items.Count - 1)
        Dim t As String = ComboBoxDriveLetter.Text
        ComboBoxDriveLetter.Items.Clear()
        ComboBoxDriveLetter.Text = ""
        For Each s As String In AvailableDriveLetters
            ComboBoxDriveLetter.Items.Add(s)
        Next
        If ComboBoxDriveLetter.Items.Count > 0 Then
            If Not ComboBoxDriveLetter.Items.Contains(t) Then
                ComboBoxDriveLetter.SelectedIndex = 0
            Else
                ComboBoxDriveLetter.Text = t
            End If
        End If

        LoadComplete = True
        SelectedIndex = ListBox1.SelectedIndex
    End Sub

    Private Sub ButtonVerify_Click(sender As Object, e As EventArgs) Handles ButtonVerify.Click
        If TestEnabled Then
            ButtonTest.Enabled = False
            TestEnabled = False
            Exit Sub
        End If
        Dim progmax As Long
        If MessageBox.Show(New Form With {.TopMost = True}, "Start verify?", "Info", MessageBoxButtons.OKCancel) = DialogResult.OK Then
            TestEnabled = True
            Dim progval As Long = 0
            Dim info As String = ""
            Dim running As Boolean = True
            Dim randomNum As Boolean = RadioButtonTest1.Checked
            Dim blkLen As Integer = CInt(NumericUpDownTestBlkSize.Value)
            Dim blkNum As Long = CLng(NumericUpDownTestBlkNum.Value)
            progmax = blkNum * blkLen
            Dim sec As Integer = -1
            Dim SenseMsg As String = ""
            Dim zbcstartblk As Long = 0
            Dim zbcsectorlen As Integer = 4096
            Select Case My.Settings.TapeUtils_DriverType
                Case TapeUtils.DriverType.ZBCDevice
                    DisplayHelper.ShowInputDialog("Start LBA", "ZBC test verify param", zbcstartblk)
                    DisplayHelper.ShowInputDialog("Sector length", "ZBC test verify param", zbcsectorlen)
                Case Else

            End Select
            Dim th As New Threading.Thread(
            Sub()

                Invoke(Sub() TextBoxDebugOutput.AppendText($"Start{vbCrLf}"))
                sec = 0
                Dim handle As IntPtr
                Dim bH(7) As Byte
                If Not TapeUtils.OpenTapeDrive(ConfTapeDrive, handle) Then MessageBox.Show(New Form With {.TopMost = True}, "False")

                Dim LastC1Err(31) As Integer, LastNoCCPs(31) As Integer
                For i As Long = 0 To blkNum - 1
                    If Not TestEnabled Then Exit For
                    Dim sense(63) As Byte
                    Dim zbcLBAWritten As Long = 0
                    Dim cdb As Byte()
                    Select Case My.Settings.TapeUtils_DriverType
                        Case TapeUtils.DriverType.ZBCDevice
                            zbcLBAWritten = blkLen \ zbcsectorlen
                            cdb = {&H2F, &H0,
                                                      CByte((zbcstartblk >> 24) And &HFF),
                                                      CByte((zbcstartblk >> 16) And &HFF),
                                                      CByte((zbcstartblk >> 8) And &HFF),
                                                      CByte(zbcstartblk And &HFF), &H0,
                                                      CByte(zbcLBAWritten >> 8 And &HFF),
                                                      CByte(zbcLBAWritten And &HFF), &H0}
                        Case Else
                            cdb = {&H13, 0, CByte((blkLen >> 16) And &HFF), CByte((blkLen >> 8) And &HFF), CByte(blkLen And &HFF), 0}
                    End Select
                    TapeUtils.SCSIReadParam(handle, cdb, 0, Function(sensedata As Byte())
                                                                sense = sensedata
                                                                Return True
                                                            End Function)
                    If (sense(2) And &HF) <> 0 Then
                        SenseMsg = TapeUtils.ParseSenseData(sense)
                        If (sense(2) And &HFF) = 8 Then Exit For
                        Select Case MessageBox.Show(New Form With {.TopMost = True}, $"{My.Resources.ResText_RErrSCSI}{vbCrLf}{TapeUtils.ParseSenseData(sense)}{vbCrLf}{vbCrLf}sense{vbCrLf}{TapeUtils.Byte2Hex(sense, True)}", My.Resources.ResText_Warning, MessageBoxButtons.AbortRetryIgnore)
                            Case DialogResult.Abort
                                Exit For
                            Case DialogResult.Retry
                                i -= 1
                                Continue For
                            Case DialogResult.Ignore
                                Continue For
                        End Select
                    Else
                        SenseMsg = ""
                    End If
                    If My.Settings.TapeUtils_DriverType = TapeUtils.DriverType.ZBCDevice Then
                        Threading.Interlocked.Add(zbcstartblk, zbcLBAWritten)
                    Else
                        Try
                            If i Mod 200 = 0 Then
                                Dim result As New StringBuilder
                                Dim RERLHeader As Byte()
                                Dim RERLPage As Byte()
                                Dim RERLPageLen As Integer
                                SyncLock TapeUtils.GetSCSIOperationLock(handle)
                                    RERLHeader = TapeUtils.SCSIReadParam(handle, {&H1C, &H1, &H87, &H0, &H4, &H0}, 4)
                                    If RERLHeader.Length <> 4 Then Exit Try
                                    RERLPageLen = RERLHeader(2)
                                    RERLPageLen <<= 8
                                    RERLPageLen = RERLPageLen Or RERLHeader(3)
                                    If RERLPageLen = 0 Then Exit Try
                                    RERLPageLen += 4
                                    RERLPage = TapeUtils.SCSIReadParam(handle:=handle, cdbData:=New Byte() {&H1C, &H1, &H87, CByte((RERLPageLen >> 8) And &HFF), CByte(RERLPageLen And &HFF), &H0}, paramLen:=RERLPageLen)
                                End SyncLock
                                Dim RERLData As String() = Encoding.ASCII.GetString(RERLPage, 4, RERLPage.Length - 4).Split({vbCr, vbLf, vbTab}, StringSplitOptions.RemoveEmptyEntries)
                                info = ""
                                Try
                                    For ch As Integer = 5 To RERLData.Length - 5 Step 5
                                        Dim chan As Integer = (ch - 4) \ 5
                                        Dim C1err As Integer = Integer.Parse(RERLData(ch + 0), Globalization.NumberStyles.HexNumber)
                                        Dim NoCCPs As Integer = Integer.Parse(RERLData(ch + 4), Globalization.NumberStyles.HexNumber)

                                        If NoCCPs - LastNoCCPs(chan) > 0 Then
                                            Dim resulttxt As String = (Math.Round(Math.Log10((C1err - LastC1Err(chan)) / (NoCCPs - LastNoCCPs(chan)) / 2 / 1920), 2).ToString("f2"))
                                            resulttxt = resulttxt.Replace("∞", "Inf")
                                            result.Append(resulttxt.PadLeft(6).PadRight(7))
                                            LastC1Err(chan) = C1err
                                            LastNoCCPs(chan) = NoCCPs
                                        Else
                                            result.Append("-".PadLeft(4).PadRight(7))
                                        End If
                                    Next
                                Catch
                                End Try
                                info = result.ToString()
                            End If
                        Catch ex As Exception
                            info = ex.ToString()
                        End Try
                    End If

                    progval = i * blkLen
                Next
                TapeUtils.CloseTapeDrive(handle)

                running = False
                TestEnabled = False
                Invoke(Sub()
                           ButtonTest.Enabled = True
                           ButtonTest.Text = "Start"
                       End Sub)
            End Sub)
            Dim thprog As New Threading.Thread(
            Sub()
                Dim lastval As Long = 0
                Dim len1 As Integer = progmax.ToString().Length
                While running
                    Threading.Thread.Sleep(1000)
                    Dim prognow As Long = progval
                    Invoke(Sub()
                               Dim texta As String = ($"{sec.ToString().PadLeft(4)}: {prognow.ToString().PadLeft(Math.Max(15, len1))} (+{IOManager.FormatSize(prognow - lastval).PadLeft(10)})")
                               Dim infostart As Integer = texta.Length
                               TextBoxDebugOutput.AppendText($"{texta}{info} {SenseMsg}{vbCrLf}")
                               If info.Length > 0 Then FormatLogRateColor(TextBoxDebugOutput, infostart, info)
                           End Sub)
                    If sec >= 0 Then sec += 1
                    lastval = prognow
                End While
                Invoke(Sub()
                           Dim texta As String = ($"{sec.ToString().PadLeft(4)}: {progval.ToString().PadLeft(Math.Max(15, len1))} (+{IOManager.FormatSize(progval - lastval).PadLeft(10)})")
                           Dim infostart As Integer = texta.Length
                           TextBoxDebugOutput.AppendText($"{texta}{info} {SenseMsg}{vbCrLf}")
                           If info.Length > 0 Then FormatLogRateColor(TextBoxDebugOutput, infostart, info)
                           TextBoxDebugOutput.AppendText($"End")
                       End Sub)
            End Sub)
            TextBoxDebugOutput.Clear()
            thprog.Start()
            ButtonTest.Text = "Stop"
            th.Start()
        End If
    End Sub
End Class
