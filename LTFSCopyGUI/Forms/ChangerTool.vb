Imports System
Imports System.ComponentModel
Imports System.ComponentModel.DataAnnotations
Imports System.Runtime.InteropServices
Imports System.Security.Cryptography
Imports System.Text
Imports System.Threading
Imports LTFSCopyGUI.TapeUtils
Imports NAudio.MediaFoundation
Imports Serilog
Imports Serilog.Context

Public Class ChangerTool
    Private ReadOnly _logSessionId As String = $"changer-{Guid.NewGuid().ToString("N").Substring(0, 8)}"
    Public LoadComplete As Boolean = False
    '<TypeConverter(GetType(ExpandableObjectConverter))>
    <TypeConverter(GetType(ListTypeDescriptor(Of List(Of MediumChanger), MediumChanger)))>
    Public Property LastDeviceList As List(Of MediumChanger)
    Public ReadOnly Property CurrentChanger As MediumChanger
        Get
            If LastDeviceList Is Nothing Then Return Nothing
            If SelectedIndex < 0 Or SelectedIndex > LastDeviceList.Count - 1 Then Return Nothing
            Return LastDeviceList(SelectedIndex)
        End Get
    End Property
    Private _SelectedIndex As Integer
    Public Property SelectedIndex As Integer
        Set(value As Integer)
            If _SelectedIndex = value Then Exit Property
            _SelectedIndex = Math.Max(0, value)
            If Not LoadComplete Then Exit Property
            If CheckBox1.Checked Then
                SetUILock(True)
                Task.Run(Sub()
                             RefreshCurrentChanger()
                             Invoke(Sub()
                                        SwitchChanger()
                                        SetUILock(False)
                                    End Sub)
                         End Sub)
            Else
                SwitchChanger()
            End If
        End Set
        Get
            Return _SelectedIndex
        End Get
    End Property
    Private Sub ChangerTool_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(ChangerTool))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Changer")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Lifecycle")
                        Log.Information("Changer window loading.")
                    End Using
                End Using
            End Using
        End Using
        Text = $"ChangerTool - {ApplicationWheels.ApplicationInfo}"
        RefreshMCList()
        SetUILock(True)
        Task.Run(Sub()
                     RefreshCurrentChanger()
                     Invoke(Sub()
                                SwitchChanger()
                                SetUILock(False)
                            End Sub)
                 End Sub)
        LoadComplete = True
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(ChangerTool))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Changer")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Lifecycle")
                        Log.Information("Changer window loaded. DeviceCount={DeviceCount} SelectedIndex={SelectedIndex}.", If(LastDeviceList Is Nothing, 0, LastDeviceList.Count), SelectedIndex)
                    End Using
                End Using
            End Using
        End Using
    End Sub
    Public Sub RefreshMCList()
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(ChangerTool))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Changer")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "DeviceRefresh")
                        Log.Information("Medium changer enumeration started.")
                    End Using
                End Using
            End Using
        End Using
        Dim DeviceList As List(Of MediumChanger)
        Try
            DeviceList = GetMediumChangerList()
        Catch ex As Exception
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(ChangerTool))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Changer")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                        Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Error")
                            Log.Error(ex, "Medium changer enumeration failed.")
                        End Using
                    End Using
                End Using
            End Using
            Throw
        End Try
        LoadComplete = False
        ListBox1.Items.Clear()
        Dim DevList As List(Of MediumChanger)
        DevList = DeviceList
        LastDeviceList = DeviceList
        For Each D As MediumChanger In DevList
            ListBox1.Items.Add(D.ToString())
        Next
        ListBox1.SelectedIndex = Math.Min(SelectedIndex, ListBox1.Items.Count - 1)
        LoadComplete = True
        SelectedIndex = ListBox1.SelectedIndex
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(ChangerTool))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Changer")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "DeviceRefresh")
                        Log.Information("Medium changer enumeration completed. DeviceCount={DeviceCount} SelectedIndex={SelectedIndex}.", DeviceList.Count, SelectedIndex)
                    End Using
                End Using
            End Using
        End Using
    End Sub
    Public FullElement, EmptyElement As List(Of MediumChanger.Element)
    Public Sub RefreshCurrentChanger()
        If CurrentChanger Is Nothing Then
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(ChangerTool))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Changer")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                        Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "DeviceRefresh")
                            Log.Warning("Current medium changer refresh was skipped because no changer is selected.")
                        End Using
                    End Using
                End Using
            End Using
            Exit Sub
        End If
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(ChangerTool))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Changer")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "DeviceRefresh")
                        Log.Information("Current medium changer refresh started. DeviceIndex={DeviceIndex}.", CurrentChanger.DevIndex)
                    End Using
                End Using
            End Using
        End Using
        Try
            CurrentChanger.RefreshElementStatus(Not CheckBox2.Checked)
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(ChangerTool))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Changer")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                        Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "DeviceRefresh")
                            Log.Information("Current medium changer refresh completed. ElementCount={ElementCount}.", If(CurrentChanger.Elements Is Nothing, 0, CurrentChanger.Elements.Count))
                        End Using
                    End Using
                End Using
            End Using
        Catch ex As Exception
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(ChangerTool))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Changer")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                        Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Error")
                            Log.Error(ex, "Current medium changer refresh failed. DeviceIndex={DeviceIndex}.", CurrentChanger.DevIndex)
                        End Using
                    End Using
                End Using
            End Using
            MessageBox.Show(ex.ToString())
        End Try
    End Sub
    Public Sub SwitchChanger()
        If CurrentChanger Is Nothing Then
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(ChangerTool))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Changer")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                        Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Selection")
                            Log.Warning("Changer view switch was skipped because no changer is selected.")
                        End Using
                    End Using
                End Using
            End Using
        End If
        If CurrentChanger Is Nothing OrElse CurrentChanger.Elements Is Nothing OrElse CurrentChanger.Elements.Count = 0 Then RefreshCurrentChanger()
        Try
            TextBox1.Text = CurrentChanger.GetSerializedText()
            FullElement = New List(Of MediumChanger.Element)
            EmptyElement = New List(Of MediumChanger.Element)
            For Each e As MediumChanger.Element In CurrentChanger.Elements
                If e.Full Then FullElement.Add(e) Else EmptyElement.Add(e)
            Next
            ComboBox1.Items.Clear()
            ComboBox2.Items.Clear()
            For Each e As MediumChanger.Element In FullElement
                Dim ctext As String = $"{e.PrimaryVolumeTagInformation} @0x{Hex(e.ElementAddress).PadLeft(4, "0"c)} LUN{e.LUN}"
                Dim itext As String = e.Identifier.Replace("  ", " ").Replace("  ", " ").Replace("  ", " ")
                Dim ttext As String = e.ElementTypeCode.ToString()
                If itext.Length > 0 Then itext &= " "
                itext &= ttext
                itext = itext.TrimEnd(" "c)
                If itext.Length > 0 Then ctext = $"{ctext}({itext})"
                ComboBox1.Items.Add(ctext)
            Next
            For Each e As MediumChanger.Element In EmptyElement
                Dim ctext As String = $"0x{Hex(e.ElementAddress).PadLeft(4, "0"c)} LUN{e.LUN}"
                Dim itext As String = e.Identifier.Replace("  ", " ").Replace("  ", " ").Replace("  ", " ")
                Dim ttext As String = e.ElementTypeCode.ToString()
                If itext.Length > 0 Then itext &= " "
                itext &= ttext
                itext = itext.TrimEnd(" "c)
                If itext.Length > 0 Then ctext = $"{ctext}({itext})"
                ComboBox2.Items.Add(ctext)
            Next
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(ChangerTool))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Changer")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                        Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Selection")
                            Log.Information("Changer view updated. FullElementCount={FullElementCount} EmptyElementCount={EmptyElementCount}.", FullElement.Count, EmptyElement.Count)
                        End Using
                    End Using
                End Using
            End Using
        Catch ex As Exception
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(ChangerTool))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Changer")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                        Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Error")
                            Log.Error(ex, "Changer view update failed.")
                        End Using
                    End Using
                End Using
            End Using
        End Try

    End Sub

    Private Sub Button1_Click(sender As Object, e As EventArgs) Handles Button1.Click
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(ChangerTool))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Changer")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "DeviceRefresh")
                        Log.Information("Medium changer refresh requested by the user.")
                    End Using
                End Using
            End Using
        End Using
        RefreshMCList()
    End Sub

    Private Sub ListBox1_SelectedIndexChanged(sender As Object, e As EventArgs) Handles ListBox1.SelectedIndexChanged
        If Not LoadComplete Then Exit Sub
        SelectedIndex = ListBox1.SelectedIndex
    End Sub

    Private Sub Button3_Click(sender As Object, e As EventArgs) Handles Button3.Click
        If ComboBox1.SelectedIndex < 0 Then Exit Sub
        If ComboBox2.SelectedIndex < 0 Then Exit Sub
        If FullElement Is Nothing Then Exit Sub
        If EmptyElement Is Nothing Then Exit Sub
        If ComboBox1.SelectedIndex > FullElement.Count - 1 Then Exit Sub
        If ComboBox2.SelectedIndex > EmptyElement.Count - 1 Then Exit Sub
        If MessageBox.Show(New Form With {.TopMost = True}, $"{ComboBox1.SelectedItem} -> {ComboBox2.SelectedItem}", "", MessageBoxButtons.OKCancel) = DialogResult.OK Then
            Dim drv As String = $"\\.\CHANGER{CurrentChanger.DevIndex}"
            If CurrentChanger.device IsNot Nothing Then drv = CurrentChanger.device.DevicePath
            Dim LUN As Byte = FullElement(ComboBox1.SelectedIndex).LUN
            Dim srcElementIndex = ComboBox1.SelectedIndex
            Dim destElementIndex = ComboBox2.SelectedIndex
            Dim srcElement As MediumChanger.Element = FullElement(srcElementIndex)
            Dim destElement As MediumChanger.Element = EmptyElement(destElementIndex)
            Dim src As UInt32 = srcElement.ElementAddress
            Dim dest As UInt32 = destElement.ElementAddress
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(ChangerTool))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Changer")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                        Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Move")
                            Log.Information("Medium move requested. DevicePath={DevicePath} SourceAddress={SourceAddress} DestinationAddress={DestinationAddress} LUN={LUN}.", drv, src, dest, LUN)
                        End Using
                    End Using
                End Using
            End Using
            srcElement.Full = Not srcElement.Full
            destElement.Full = Not destElement.Full
            destElement.PrimaryVolumeTagInformation = srcElement.PrimaryVolumeTagInformation
            srcElement.PrimaryVolumeTagInformation = ""
            SetUILock(True)
            Dim th As New Thread(
                Sub()
                    Dim sense(63) As Byte
                    Dim succ As Boolean = False
                    Dim ex As Exception = Nothing
                    Try
                        MediumChanger.MoveMedium(drv, src, dest, sense, LUN:=LUN)
                        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(ChangerTool))
                            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Changer")
                                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Move")
                                        Log.Information("Medium move command completed. SourceAddress={SourceAddress} DestinationAddress={DestinationAddress}.", src, dest)
                                    End Using
                                End Using
                            End Using
                        End Using
                    Catch ex
                        succ = False
                        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(ChangerTool))
                            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Changer")
                                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Error")
                                        Log.Error(ex, "Medium move command failed. SourceAddress={SourceAddress} DestinationAddress={DestinationAddress}.", src, dest)
                                    End Using
                                End Using
                            End Using
                        End Using
                    Finally
                        succ = True
                    End Try
                    Invoke(Sub()
                               If CheckBox1.Checked Then
                                   Task.Run(
                                   Sub()
                                       RefreshCurrentChanger()
                                       Invoke(Sub()
                                                  SwitchChanger()
                                                  SetUILock(False)
                                              End Sub)
                                   End Sub)
                               Else
                                   Invoke(Sub()
                                              SwitchChanger()
                                              SetUILock(False)
                                          End Sub)
                               End If
                               If succ Then
                                   MessageBox.Show(New Form With {.TopMost = True}, $"Finished{vbCrLf}{ParseSenseData(sense)}")
                               Else
                                   MessageBox.Show(New Form With {.TopMost = True}, $"Error: {ex.ToString}")
                               End If
                           End Sub)
                End Sub)
            th.Start()
        End If

    End Sub

    Private Sub Button2_Click(sender As Object, e As EventArgs) Handles Button2.Click
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(ChangerTool))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Changer")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "DeviceRefresh")
                        Log.Information("Current medium changer refresh requested by the user.")
                    End Using
                End Using
            End Using
        End Using
        SetUILock(True)
        Task.Run(
            Sub()
                RefreshCurrentChanger()
                Invoke(Sub()
                           SwitchChanger()
                           SetUILock(False)
                       End Sub)
            End Sub)
    End Sub

    Public Sub SetUILock(Lock As Boolean)
        Invoke(Sub()
                   For Each c As Control In Controls
                       c.Enabled = Not Lock
                   Next
               End Sub)
    End Sub

    Private Sub ChangerTool_SizeChanged(sender As Object, e As EventArgs) Handles Me.SizeChanged
        If Not LoadComplete Then Exit Sub
        SuspendLayout()
        Dim sample As New ChangerTool
        ComboBox1.Width = CInt((Width - sample.Width) / 2 + sample.ComboBox1.Width)
        ComboBox2.Width = CInt((Width - sample.Width) / 2 + sample.ComboBox2.Width)
        ComboBox2.Left = CInt((Width - sample.Width) / 2 + sample.ComboBox2.Left)
        Label1.Left = CInt((Width - sample.Width) / 2 + sample.Label1.Left)
        ResumeLayout()
    End Sub

    Private Sub 排序ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 排序ToolStripMenuItem.Click
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(ChangerTool))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Changer")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Sort")
                        Log.Information("Medium changer sorting started.")
                    End Using
                End Using
            End Using
        End Using
        SetUILock(True)
        Task.Run(
            Sub()
                RefreshCurrentChanger()
                Dim drv As String = $"\\.\CHANGER{CurrentChanger.DevIndex}"
                If CurrentChanger.device IsNot Nothing Then drv = CurrentChanger.device.DevicePath
                Invoke(Sub() TextBox1.Clear())
                Dim srcList As New List(Of MediumChanger.Element)
                Dim TLList As New List(Of String)
                For Each el As MediumChanger.Element In CurrentChanger.Elements
                    If el.ElementTypeCode = MediumChanger.Element.ElementTypeCodes.StorageElement Then
                        srcList.Add(el)
                        If el.Full Then TLList.Add(el.PrimaryVolumeTagInformation)
                    End If
                Next
                srcList.Sort(New Comparison(Of MediumChanger.Element)(
                     Function(a As MediumChanger.Element, b As MediumChanger.Element) As Integer
                         Return a.ElementAddress.CompareTo(b.ElementAddress)
                     End Function))
                TLList.Sort()
                Dim IsOnTarget As New Func(Of Boolean)(
                    Function() As Boolean
                        For i As Integer = 0 To TLList.Count - 1
                            If srcList(i).PrimaryVolumeTagInformation <> TLList(i) Then Return False
                        Next
                        Return True
                    End Function)
                While Not IsOnTarget()
                    Dim movecount As Integer = 0
                    For i As Integer = 0 To TLList.Count - 1
                        If Not srcList(i).Full Then
                            Dim j As Integer = 0
                            For j = 0 To srcList.Count - 1
                                If srcList(j).PrimaryVolumeTagInformation = TLList(i) Then
                                    Dim info As String = $"0x{Hex(srcList(j).ElementAddress).PadLeft(4, "0"c)} {srcList(j).PrimaryVolumeTagInformation} -> 0x{Hex(srcList(i).ElementAddress).PadLeft(4, "0"c)}{vbCrLf}"
                                    Invoke(Sub() TextBox1.AppendText(info))
                                    While True
                                        Dim sense(63) As Byte
                                        Try
                                            MediumChanger.MoveMedium(drv, srcList(j).ElementAddress, srcList(i).ElementAddress, sense, LUN:=srcList(j).LUN)
                                            Dim sensekey As Byte = CByte(sense(2) And &HF)
                                            If sensekey <> 0 Then Throw New Exception("SCSI Sense Error")
                                        Catch ex As Exception
                                            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(ChangerTool))
                                                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Changer")
                                                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                                                        Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Error")
                                                            Log.Error(ex, "Medium changer sorting move failed. SourceAddress={SourceAddress} DestinationAddress={DestinationAddress}.", srcList(j).ElementAddress, srcList(i).ElementAddress)
                                                        End Using
                                                    End Using
                                                End Using
                                            End Using
                                            Dim result As DialogResult
                                            Invoke(Sub() result = MessageBox.Show(New Form With {.TopMost = True}, $"Error: {ex.ToString}{vbCrLf}{ParseSenseData(sense)}", "", MessageBoxButtons.AbortRetryIgnore))
                                            Select Case result
                                                Case DialogResult.Ignore
                                                    Exit While
                                                Case DialogResult.Cancel
                                                    Invoke(Sub()
                                                               SetUILock(False)
                                                           End Sub)
                                                    Exit Sub
                                                Case DialogResult.Retry
                                                    Continue While
                                            End Select
                                        End Try
                                        Exit While
                                    End While
                                    srcList(i).Full = True
                                    srcList(i).PrimaryVolumeTagInformation = srcList(j).PrimaryVolumeTagInformation
                                    srcList(j).Full = False
                                    srcList(j).PrimaryVolumeTagInformation = ""
                                    movecount += 1
                                    Exit For
                                End If
                            Next
                        End If
                    Next
                    If movecount = 0 Then
                        For i As Integer = TLList.Count To srcList.Count - 1
                            If Not srcList(i).Full Then
                                For j As Integer = 0 To TLList.Count - 1
                                    If srcList(j).Full AndAlso srcList(j).PrimaryVolumeTagInformation <> TLList(j) Then
                                        Dim info As String = $"0x{Hex(srcList(j).ElementAddress).PadLeft(4, "0"c)} {srcList(j).PrimaryVolumeTagInformation} -> 0x{Hex(srcList(i).ElementAddress).PadLeft(4, "0"c)}{vbCrLf}"
                                        Invoke(Sub() TextBox1.AppendText(info))
                                        While True
                                            Dim sense(63) As Byte
                                            Try
                                                MediumChanger.MoveMedium(drv, srcList(j).ElementAddress, srcList(i).ElementAddress, sense, LUN:=srcList(j).LUN)
                                                Dim sensekey As Byte = CByte(sense(2) And &HF)
                                                If sensekey <> 0 Then Throw New Exception("SCSI Sense Error")
                                            Catch ex As Exception
                                                Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(ChangerTool))
                                                    Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Changer")
                                                        Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                                                            Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Error")
                                                                Log.Error(ex, "Medium changer sorting move failed. SourceAddress={SourceAddress} DestinationAddress={DestinationAddress}.", srcList(j).ElementAddress, srcList(i).ElementAddress)
                                                            End Using
                                                        End Using
                                                    End Using
                                                End Using
                                                Dim result As DialogResult
                                                Invoke(Sub() result = MessageBox.Show(New Form With {.TopMost = True}, $"Error: {ex.ToString}{vbCrLf}{ParseSenseData(sense)}", "", MessageBoxButtons.AbortRetryIgnore))
                                                Select Case result
                                                    Case DialogResult.Ignore
                                                        Exit While
                                                    Case DialogResult.Cancel
                                                        Invoke(Sub()
                                                                   SetUILock(False)
                                                               End Sub)
                                                        Exit Sub
                                                    Case DialogResult.Retry
                                                        Continue While
                                                End Select
                                            End Try
                                            Exit While
                                        End While
                                        srcList(i).Full = True
                                        srcList(i).PrimaryVolumeTagInformation = srcList(j).PrimaryVolumeTagInformation
                                        srcList(j).Full = False
                                        srcList(j).PrimaryVolumeTagInformation = ""
                                        movecount += 1
                                        Exit For
                                    End If
                                Next
                                Exit For
                            End If
                        Next
                    End If
                    If movecount = 0 Then Exit While
                End While
                Invoke(Sub()
                           SetUILock(False)
                       End Sub)
                Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(ChangerTool))
                    Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Changer")
                        Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                            Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Sort")
                                Log.Information("Medium changer sorting completed.")
                            End Using
                        End Using
                    End Using
                End Using
            End Sub)
    End Sub

    Private Sub ChangerTool_KeyDown(sender As Object, e As KeyEventArgs) Handles Me.KeyDown
        Select Case e.KeyCode
            Case Keys.F12
                Dim SP1 As New SettingPanel
                SP1.Text = Text
                SP1.SelectedObject = Me
                SP1.Show()
        End Select
    End Sub

    Private Sub 批量擦除ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 批量擦除ToolStripMenuItem.Click
        If Not (MessageBox.Show(My.Resources.ResText_DataLossWarning, My.Resources.ResText_Warning, MessageBoxButtons.OKCancel) = DialogResult.OK) Then Exit Sub
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(ChangerTool))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Changer")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Erase")
                        Log.Information("Batch medium erase started.")
                    End Using
                End Using
            End Using
        End Using
        SetUILock(True)
        Task.Run(Sub()
                     Invoke(Sub() TextBox1.Text = $"Loading elements{vbCrLf}")
                     RefreshCurrentChanger()
                     If FullElement Is Nothing OrElse EmptyElement Is Nothing Then
                         SetUILock(False)
                         Exit Sub
                     End If
                     Dim changerPath As String = $"\\.\CHANGER{CurrentChanger.DevIndex}"
                     If CurrentChanger.device IsNot Nothing Then changerPath = CurrentChanger.device.DevicePath
                     Dim sense As Byte() = SCSIReadParam(TapeDrive:=changerPath, cdbData:=New Byte() {3, 0, 0, 0, &H12, 0}, paramLen:=18)
                     Dim ToErase As New List(Of MediumChanger.Element)
                     For Each fe As MediumChanger.Element In FullElement
                         If fe.ElementTypeCode = MediumChanger.Element.ElementTypeCodes.StorageElement OrElse fe.ElementTypeCode = MediumChanger.Element.ElementTypeCodes.ImportExportElement Then
                             If fe.Full Then ToErase.Add(fe)
                         End If
                     Next
                     Dim AllDrivers As New List(Of MediumChanger.Element)
                     For Each fe As MediumChanger.Element In EmptyElement
                         If fe.ElementTypeCode = MediumChanger.Element.ElementTypeCodes.DataTransferElement Then
                             fe.Identifier = fe.Identifier.Replace("  ", " ").Replace("  ", " ").Replace("  ", " ")
                             AllDrivers.Add(fe)
                         End If
                     Next
                     Dim drvList As List(Of BlockDevice) = GetTapeDriveList()
                     Dim drvMapping As New Dictionary(Of MediumChanger.Element, BlockDevice)
                     For Each drv As MediumChanger.Element In AllDrivers
                         Dim found As Boolean = False
                         For Each d As BlockDevice In drvList
                             If drv.Identifier.EndsWith(d.SerialNumber) Then
                                 found = True
                                 Invoke(Sub() TextBox1.AppendText($"Find drive [0x{Hex(drv.ElementAddress).PadLeft(4, "0"c)}]{drv.Identifier} <=> {d.SerialNumber} {d.DevicePath}{vbCrLf}"))
                                 drvMapping.Add(drv, d)
                                 Exit For
                             End If
                         Next
                     Next
                     Dim eraseSucceeded As Boolean = False
                     Try
                         If drvMapping.Keys.Count = 0 Then
                             Throw New Exception("No drive found.")
                         End If
                         While ToErase.Count > 0
                             Dim eraseTaskCount As Integer = 0
                             Dim eraseFinEvent As New AutoResetEvent(False)

                             Dim eraseMapping As New Dictionary(Of MediumChanger.Element, MediumChanger.Element) 'Drive <=> Slot
                             For i As Integer = 0 To drvMapping.Keys.Count - 1
                                 If ToErase.Count = 0 Then Exit For

                                 Dim currentDrive As MediumChanger.Element = drvMapping.Keys(i)
                                 Dim currentSlot As MediumChanger.Element = ToErase(0)
                                 Invoke(Sub() TextBox1.AppendText($"Assign drive [0x{Hex(currentDrive.ElementAddress).PadLeft(4, "0"c)}]{currentDrive.Identifier} <=> [0x{Hex(currentSlot.ElementAddress).PadLeft(4, "0"c)}]Element {currentSlot.PrimaryVolumeTagInformation}{vbCrLf}"))
                                 eraseMapping.Add(currentDrive, currentSlot)
                                 ToErase.RemoveAt(0)
                             Next
                             Dim finList As New List(Of MediumChanger.Element)
                             For i As Integer = 0 To eraseMapping.Keys.Count - 1
                                 Dim drv As MediumChanger.Element = eraseMapping.Keys(i)
                                 Dim slot As MediumChanger.Element = eraseMapping(drv)
                                 Invoke(Sub() TextBox1.AppendText($"Move [0x{Hex(slot.ElementAddress).PadLeft(4, "0"c)}]{slot.PrimaryVolumeTagInformation} -> [0x{Hex(drv.ElementAddress).PadLeft(4, "0"c)}]{drv.Identifier}{vbCrLf}"))
                                 ApplicationWheels.TryExecute(Function() As Byte()
                                                                  Dim senseReturn(63) As Byte
                                                                  MediumChanger.MoveMedium(changerPath, slot.ElementAddress, drv.ElementAddress, senseReturn, slot.LUN)
                                                                  Return senseReturn
                                                              End Function)
                                 Interlocked.Increment(eraseTaskCount)
                                 Task.Run(Sub()
                                              Invoke(Sub() TextBox1.AppendText($"Erase {drvMapping(drv).DevicePath}{vbCrLf}"))
                                              ApplicationWheels.TryExecute(Function() As Byte()
                                                                               Dim senseReturn(63) As Byte
                                                                               SCSIReadParam(TapeDrive:=drvMapping(drv).DevicePath, {&HB, 0, 0, &HFF, &HFF, 0}, 0, Function(s As Byte())
                                                                                                                                                                       senseReturn = s
                                                                                                                                                                       Return True
                                                                                                                                                                   End Function)
                                                                               Return senseReturn
                                                                           End Function, 1)
                                              ApplicationWheels.TryExecute(Function() As Byte()
                                                                               Dim senseReturn(63) As Byte
                                                                               SCSIReadParam(TapeDrive:=drvMapping(drv).DevicePath, {4, 0, 1, 0, 0, 0}, 0, Function(s As Byte())
                                                                                                                                                               senseReturn = s
                                                                                                                                                               Return True
                                                                                                                                                           End Function)
                                                                               Return senseReturn
                                                                           End Function, 1)
                                              SyncLock finList
                                                  finList.Add(drv)
                                              End SyncLock
                                              Interlocked.Decrement(eraseTaskCount)
                                              eraseFinEvent.Set()
                                          End Sub)
                             Next
                             While eraseTaskCount > 0
                                 SyncLock finList
                                     For Each drv As MediumChanger.Element In finList
                                         Dim slot As MediumChanger.Element = eraseMapping(drv)
                                         Invoke(Sub() TextBox1.AppendText($"Move [0x{Hex(drv.ElementAddress).PadLeft(4, "0"c)}]{drv.Identifier}_{slot.PrimaryVolumeTagInformation} -> [0x{Hex(slot.ElementAddress).PadLeft(4, "0"c)}]{vbCrLf}"))
                                         MediumChanger.MoveMedium(changerPath, drv.ElementAddress, slot.ElementAddress, sense, drv.LUN)
                                         ApplicationWheels.TryExecute(Function() As Byte()
                                                                          Dim senseReturn(63) As Byte
                                                                          MediumChanger.MoveMedium(changerPath, drv.ElementAddress, slot.ElementAddress, senseReturn, drv.LUN)
                                                                          Return senseReturn
                                                                      End Function)
                                     Next
                                     finList.Clear()
                                 End SyncLock
                                 eraseFinEvent.WaitOne(1000)
                             End While
                             For Each drv As MediumChanger.Element In finList
                                 Dim slot As MediumChanger.Element = eraseMapping(drv)
                                 Invoke(Sub() TextBox1.AppendText($"Move [0x{Hex(drv.ElementAddress).PadLeft(4, "0"c)}]{drv.Identifier}_{slot.PrimaryVolumeTagInformation} -> [0x{Hex(slot.ElementAddress).PadLeft(4, "0"c)}]{vbCrLf}"))
                                 ApplicationWheels.TryExecute(Function() As Byte()
                                                                  Dim senseReturn(63) As Byte
                                                                  MediumChanger.MoveMedium(changerPath, drv.ElementAddress, slot.ElementAddress, senseReturn, drv.LUN)
                                                                  Return senseReturn
                                                              End Function)
                             Next
                         End While
                         Invoke(Sub() TextBox1.AppendText("Finished"))
                         eraseSucceeded = True
                     Catch ex As Exception
                         Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(ChangerTool))
                             Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Changer")
                                 Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                                     Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Error")
                                         Log.Error(ex, "Batch medium erase failed.")
                                     End Using
                                 End Using
                             End Using
                         End Using
                         MessageBox.Show(ex.ToString())
                     End Try

                     SetUILock(False)
                     Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(ChangerTool))
                         Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Changer")
                             Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                                 Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Erase")
                                     Log.Information("Batch medium erase worker finished. Succeeded={Succeeded}.", eraseSucceeded)
                                 End Using
                             End Using
                         End Using
                     End Using
                 End Sub)

    End Sub

    Private Sub Button4_MouseUp(sender As Object, e As MouseEventArgs) Handles Button4.MouseUp
        ContextMenuStrip1.Show(Button4, e.X, e.Y)
    End Sub
End Class
