Imports System.ComponentModel
Imports LTFSCopyGUI
Imports System
Imports Serilog
Imports Serilog.Context

Public Class FileBrowser
    Private Const BrowserPageSize As Integer = 1024
    Public Property schema As ltfsindex
    Private ReadOnly _logSessionId As String = $"filebrowser-{Guid.NewGuid().ToString("N").Substring(0, 8)}"
    Public Overloads Shared Sub Show(FList As ltfsindex)
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(FileBrowser))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FileBrowser")
                Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "WindowOpen")
                    Log.Information("File browser window requested.")
                End Using
            End Using
        End Using
        Dim FB1 As New FileBrowser
        With FB1
            .schema = FList
            .Show()
        End With
    End Sub
    Public Overloads Shared Function ShowDialog(FList As ltfsindex) As DialogResult
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(FileBrowser))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FileBrowser")
                Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "WindowOpen")
                    Log.Information("File browser dialog requested.")
                End Using
            End Using
        End Using
        Dim FB1 As New FileBrowser
        With FB1
            .schema = FList
            Return .ShowDialog()
        End With
    End Function
    Private Sub FileBrowser_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(FileBrowser))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FileBrowser")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Lifecycle")
                        Log.Information("File browser loading index tree. HasSchema={HasSchema}.", schema IsNot Nothing)
                    End Using
                End Using
            End Using
        End Using
        SuspendLayout()
        SyncLock EventLock
            If EventLock Then Exit Sub
            EventLock = True
        End SyncLock
        CheckBox1.Checked = My.Settings.FileBrowser_CopyInfo
        TreeView1.Nodes.Clear()
        If schema IsNot Nothing Then
            AddRootItems()
        End If
        For Each n As TreeNode In TreeView1.Nodes
            RefreshChackState(n)
        Next
        SyncLock EventLock
            EventLock = False
        End SyncLock
        ResumeLayout()
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(FileBrowser))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FileBrowser")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Lifecycle")
                        Log.Information("File browser index tree loaded. RootNodeCount={RootNodeCount}.", TreeView1.Nodes.Count)
                    End Using
                End Using
            End Using
        End Using
    End Sub
    Private NotInheritable Class BrowserTreeNode
        Inherits TreeNode

        Public Property ChildrenLoaded As Boolean
        Public Property ChildrenComplete As Boolean
        Public Property NextDirectoryIndex As Integer
        Public Property NextFileIndex As Integer
        Public Property IsPlaceholder As Boolean
    End Class

    Private Function CreateDirectoryNode(directory As ltfsindex.directory) As BrowserTreeNode
        If directory Is Nothing Then Return Nothing
        Dim node As New BrowserTreeNode With {
            .Text = If(directory.name, String.Empty),
            .Tag = directory,
            .Checked = directory.Selected,
            .ChildrenComplete = Not directory.HasPotentialChildren()}
        AddUnloadedChildrenMarker(node)
        Return node
    End Function

    Private Function CreateFileNode(file As ltfsindex.file) As BrowserTreeNode
        If file Is Nothing Then Return Nothing
        Return New BrowserTreeNode With {
            .Text = If(file.name, String.Empty),
            .Tag = file,
            .Checked = file.Selected}
    End Function

    Private Sub AddUnloadedChildrenMarker(node As BrowserTreeNode)
        If node Is Nothing OrElse node.Tag IsNot Nothing AndAlso
           TypeOf node.Tag Is ltfsindex.directory AndAlso
           Not DirectCast(node.Tag, ltfsindex.directory).HasPotentialChildren() Then Return
        Dim marker As New BrowserTreeNode With {
            .Text = "...",
            .Tag = New UnloadedChildrenMarker With {.Owner = node},
            .IsPlaceholder = True,
            .ChildrenComplete = True}
        'A dummy child keeps the standard WinForms expand glyph without
        'creating the directory's real children up front.
        marker.Nodes.Add(New TreeNode())
        node.Nodes.Add(marker)
    End Sub

    Private NotInheritable Class UnloadedChildrenMarker
        Public Property Owner As BrowserTreeNode
    End Class

    Private Sub AddRootItems()
        If schema Is Nothing Then Exit Sub
        Try
            If schema._directory IsNot Nothing Then
                For Each directory As ltfsindex.directory In schema._directory
                    Dim node As BrowserTreeNode = CreateDirectoryNode(directory)
                    If node IsNot Nothing Then TreeView1.Nodes.Add(node)
                Next
            End If
            If schema._file IsNot Nothing Then
                For Each file As ltfsindex.file In schema._file
                    Dim node As BrowserTreeNode = CreateFileNode(file)
                    If node IsNot Nothing Then TreeView1.Nodes.Add(node)
                Next
            End If
        Catch ex As Exception
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(FileBrowser))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FileBrowser")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                        Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Error")
                            Log.Error(ex, "File browser failed to build an index tree.")
                        End Using
                    End Using
                End Using
            End Using
            MessageBox.Show(New Form With {.TopMost = True}, ex.ToString)
        End Try

    End Sub

    Private Sub TreeView1_AfterSelect(sender As Object, e As TreeViewEventArgs) Handles TreeView1.AfterSelect
        SyncLock EventLock
            If EventLock Then Exit Sub
            EventLock = True
        End SyncLock
        Dim n As Object = e.Node.Tag
        If TypeOf n Is ltfsindex.file Then
                Text = "File: " & DirectCast(n, ltfsindex.file).name
                If CheckBox1.Checked Then
                    Clipboard.SetText("File" & vbTab & DirectCast(n, ltfsindex.file).name & vbCrLf)
                End If
        ElseIf TypeOf n Is ltfsindex.directory Then
                Dim directory As ltfsindex.directory = DirectCast(n, ltfsindex.directory)
                Text = "Directory: " & directory.name & " (DirCount=" & directory.GetLazyDirectDirectoryCount() & " FileCount=" & directory.GetLazyDirectFileCount() & ")"

                If CheckBox1.Checked Then
                    Dim o As New Text.StringBuilder()
                    For Each d As ltfsindex.directory In directory.EnumerateLazyDirectories()
                        o.Append("Directory").Append(vbTab).Append(d.name).Append(vbCrLf)
                    Next
                    For Each d As ltfsindex.file In directory.EnumerateLazyFiles()
                        o.Append("File").Append(vbTab).Append(d.name).Append(vbCrLf)
                    Next
                    Clipboard.SetText(o.ToString())
                End If
        End If
        SyncLock EventLock
            EventLock = False
        End SyncLock
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(FileBrowser))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FileBrowser")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "SelectionChanged")
                        Log.Information("File browser selection changed. NodeText={NodeText} Checked={Checked}.", e.Node.Text, e.Node.Checked)
                    End Using
                End Using
            End Using
        End Using
    End Sub

    Private Sub LoadDirectoryNodeChildren(node As TreeNode)
        Dim browserNode As BrowserTreeNode = TryCast(node, BrowserTreeNode)
        If browserNode Is Nothing OrElse browserNode.IsPlaceholder OrElse browserNode.ChildrenComplete Then Return
        Dim directory As ltfsindex.directory = TryCast(node.Tag, ltfsindex.directory)
        If directory Is Nothing Then Return

        browserNode.ChildrenLoaded = True
        Try
            RemoveUnloadedChildrenMarkers(browserNode)

            Dim directoryCount As Integer = directory.GetLazyDirectDirectoryCount()
            Dim fileCount As Integer = directory.GetLazyDirectFileCount()
            Dim remaining As Integer = BrowserPageSize
            While remaining > 0 AndAlso browserNode.NextDirectoryIndex < directoryCount
                Dim childDirectory As ltfsindex.directory = directory.GetLazyDirectoryAt(browserNode.NextDirectoryIndex)
                Dim childNode As BrowserTreeNode = CreateDirectoryNode(childDirectory)
                If childNode IsNot Nothing Then
                    node.Nodes.Add(childNode)
                    browserNode.NextDirectoryIndex += 1
                    remaining -= 1
                Else
                    browserNode.NextDirectoryIndex += 1
                End If
            End While
            While remaining > 0 AndAlso browserNode.NextFileIndex < fileCount
                Dim childFile As ltfsindex.file = directory.GetLazyFileAt(browserNode.NextFileIndex)
                Dim childNode As BrowserTreeNode = CreateFileNode(childFile)
                If childNode IsNot Nothing Then
                    node.Nodes.Add(childNode)
                    browserNode.NextFileIndex += 1
                    remaining -= 1
                Else
                    browserNode.NextFileIndex += 1
                End If
            End While

            browserNode.ChildrenComplete = browserNode.NextDirectoryIndex >= directoryCount AndAlso
                                             browserNode.NextFileIndex >= fileCount
            If Not browserNode.ChildrenComplete Then AddUnloadedChildrenMarker(browserNode)
        Catch
            browserNode.ChildrenLoaded = False
            RemoveUnloadedChildrenMarkers(browserNode)
            AddUnloadedChildrenMarker(browserNode)
            Throw
        End Try
    End Sub

    Private Shared Sub RemoveUnloadedChildrenMarkers(node As BrowserTreeNode)
        If node Is Nothing Then Return
        For i As Integer = node.Nodes.Count - 1 To 0 Step -1
            If IsUnloadedChildrenMarker(node.Nodes(i)) Then node.Nodes.RemoveAt(i)
        Next
    End Sub

    Private Sub TreeView1_BeforeExpand(sender As Object, e As TreeViewCancelEventArgs) Handles TreeView1.BeforeExpand
        If TypeOf e.Node.Tag Is UnloadedChildrenMarker Then
            e.Cancel = True
            Dim marker As UnloadedChildrenMarker = DirectCast(e.Node.Tag, UnloadedChildrenMarker)
            If marker.Owner IsNot Nothing Then LoadDirectoryNodeChildren(marker.Owner)
            Return
        End If
        LoadDirectoryNodeChildren(e.Node)
    End Sub

    Private Sub TreeView1_BeforeSelect(sender As Object, e As TreeViewCancelEventArgs) Handles TreeView1.BeforeSelect
        If IsUnloadedChildrenMarker(e.Node) Then e.Cancel = True
    End Sub

    Private Shared Function IsUnloadedChildrenMarker(node As TreeNode) As Boolean
        Return node IsNot Nothing AndAlso TypeOf node.Tag Is UnloadedChildrenMarker
    End Function
    Public Sub RecursivelySetNodeCheckStatus(n As TreeNode, Checked As Boolean)
        If n Is Nothing OrElse IsUnloadedChildrenMarker(n) Then Return
        n.Checked = Checked
        For Each nc As TreeNode In n.Nodes
            If Not IsUnloadedChildrenMarker(nc) Then RecursivelySetNodeCheckStatus(nc, Checked)
        Next
    End Sub
    Public Sub RefreshIndexSelection(n As Object, Selected As Boolean)
        If TypeOf (n) Is ltfsindex.file Then
            Dim file As ltfsindex.file = CType(n, ltfsindex.file)
            If file.Selected <> Selected Then file.Selected = Selected
        End If
        If TypeOf (n) Is ltfsindex.directory Then
            Dim directory As ltfsindex.directory = CType(n, ltfsindex.directory)
            If directory.Selected <> Selected Then directory.Selected = Selected
        End If
    End Sub
    Public Function RefreshChackState(n As TreeNode) As CheckState
        If n Is Nothing OrElse IsUnloadedChildrenMarker(n) Then Return CheckState.Unchecked
        If n.Nodes Is Nothing Then
            RefreshIndexSelection(n.Tag, n.Checked)
            Return GetCheckState(n.Checked)
        End If
        Dim actualChildCount As Integer = 0
        Dim hasUnloadedChildren As Boolean = False
        For Each child As TreeNode In n.Nodes
            If IsUnloadedChildrenMarker(child) Then
                hasUnloadedChildren = True
            Else
                actualChildCount += 1
            End If
        Next
        If actualChildCount = 0 Then
            Dim directory As ltfsindex.directory = TryCast(n.Tag, ltfsindex.directory)
            Dim selected As Boolean = If(directory Is Nothing, n.Checked, directory.Selected)
            RefreshIndexSelection(n.Tag, selected)
            Dim emptyResult As CheckState = GetCheckState(selected)
            TreeView1.SetNodeCheckState(n, emptyResult)
            Return emptyResult
        End If
        Dim nChecked As Integer = 0, nUnChecked As Integer = 0
        For Each nd As TreeNode In n.Nodes
            If IsUnloadedChildrenMarker(nd) Then Continue For
            Dim status As CheckState = RefreshChackState(nd)
            Select Case status
                Case CheckState.Checked
                    nChecked += 1
                Case CheckState.Unchecked
                    nUnChecked += 1
                Case CheckState.Indeterminate
                    nChecked += 1
                    nUnChecked += 1
            End Select
        Next

        'A marker means that only one page of children is present in the
        'TreeView.  Do not derive the directory's persistent selection from
        'that page; the remaining children are kept in the lazy store.
        If hasUnloadedChildren Then
            Dim lazyDirectory As ltfsindex.directory = TryCast(n.Tag, ltfsindex.directory)
            Dim lazySelected As Boolean = If(lazyDirectory Is Nothing, n.Checked, lazyDirectory.Selected)
            RefreshIndexSelection(n.Tag, lazySelected)
            Dim lazyResult As CheckState
            If (lazySelected AndAlso nUnChecked > 0) OrElse
               ((Not lazySelected) AndAlso nChecked > 0) OrElse
               (nChecked > 0 AndAlso nUnChecked > 0) Then
                lazyResult = CheckState.Indeterminate
            Else
                lazyResult = GetCheckState(lazySelected)
            End If
            TreeView1.SetNodeCheckState(n, lazyResult)
            Return lazyResult
        End If

        Dim Result As CheckState
        If nChecked > 0 And nUnChecked = 0 Then
            RefreshIndexSelection(n.Tag, True)
            Result = CheckState.Checked
        ElseIf nChecked = 0 And nUnChecked > 0 Then
            RefreshIndexSelection(n.Tag, False)
            Result = CheckState.Unchecked
        ElseIf nChecked > 0 And nUnChecked > 0 Then
            RefreshIndexSelection(n.Tag, True)
            Result = CheckState.Indeterminate
        Else
            RefreshIndexSelection(n.Tag, n.Checked)
            Result = GetCheckState(n.Checked)
        End If
        TreeView1.SetNodeCheckState(n, Result)
        Return Result
    End Function
    Public Function GetCheckState(Checked As Boolean) As CheckState
        If Checked Then Return CheckState.Checked Else Return CheckState.Unchecked
    End Function
    Public Class ObjectBoolean
        Public Value As Boolean = False
        Public Sub New(v As Boolean)
            Value = v
        End Sub
        Public Sub New()

        End Sub

        Public Shared Widening Operator CType(v As ObjectBoolean) As Boolean
            Return v.Value
        End Operator

        Public Shared Widening Operator CType(v As Boolean) As ObjectBoolean
            Return New ObjectBoolean(v)
        End Operator
    End Class
    Private EventLock As New ObjectBoolean
    Private Sub TreeView1_AfterCheck(sender As Object, e As TreeViewEventArgs) Handles TreeView1.AfterCheck
        SyncLock EventLock
            If EventLock Then Exit Sub
            EventLock = True
        End SyncLock
        Dim directory As ltfsindex.directory = TryCast(e.Node.Tag, ltfsindex.directory)
        If directory IsNot Nothing Then
            'The node may contain only the paging marker.  Propagate the
            'selection through the lazy records instead of relying on the
            'currently visible TreeNodes.
            directory.SetLazySelection(e.Node.Checked)
        End If
        If e.Node.Nodes IsNot Nothing AndAlso e.Node.Nodes.Count > 0 Then
            RecursivelySetNodeCheckStatus(e.Node, e.Node.Checked)
        End If
        For Each n As TreeNode In TreeView1.Nodes
            RefreshChackState(n)
        Next
        SyncLock EventLock
            EventLock = False
        End SyncLock
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(FileBrowser))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FileBrowser")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "SelectionChanged")
                        Log.Information("File browser node check state changed. NodeText={NodeText} Checked={Checked}.", e.Node.Text, e.Node.Checked)
                    End Using
                End Using
            End Using
        End Using
    End Sub

    Private Sub Button1_Click(sender As Object, e As EventArgs) Handles Button1.Click
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(FileBrowser))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FileBrowser")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "WindowClose")
                        Log.Information("File browser accepted the current selection.")
                    End Using
                End Using
            End Using
        End Using
        DialogResult = DialogResult.OK
        Close()
    End Sub

    Private Sub Button2_Click(sender As Object, e As EventArgs) Handles Button2.Click
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(FileBrowser))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FileBrowser")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "WindowClose")
                        Log.Information("File browser canceled the current selection.")
                    End Using
                End Using
            End Using
        End Using
        DialogResult = DialogResult.Cancel
        Close()
    End Sub

    Private Sub 全选ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 全选ToolStripMenuItem.Click
        If schema Is Nothing Then Return
        If schema._file IsNot Nothing Then
            For Each rootFile As ltfsindex.file In schema._file
                rootFile.Selected = True
            Next
        End If
        If schema._directory IsNot Nothing Then
            For Each rootDirectory As ltfsindex.directory In schema._directory
                rootDirectory.SetLazySelection(True)
            Next
        End If
        RefreshLoadedSelectionStates()
    End Sub

    Private Sub 按大小ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 按大小ToolStripMenuItem.Click
        SuspendLayout()
        Dim sMin As Long = 0, sMax As Long = Long.MaxValue
        DisplayHelper.ShowInputDialog("Minimum Bytes", "By Size", sMin)
        DisplayHelper.ShowInputDialog("Maximum Bytes", "By Size", sMax)
        If sMax < sMin Then
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(FileBrowser))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FileBrowser")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                        Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Filter")
                            Log.Warning("File browser size filter was ignored because the maximum was below the minimum. MinimumBytes={MinimumBytes} MaximumBytes={MaximumBytes}.", sMin, sMax)
                        End Using
                    End Using
                End Using
            End Using
            ResumeLayout()
            Exit Sub
        End If
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(FileBrowser))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FileBrowser")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Filter")
                        Log.Information("File browser size filter applied. MinimumBytes={MinimumBytes} MaximumBytes={MaximumBytes}.", sMin, sMax)
                    End Using
                End Using
            End Using
        End Using
        ApplySelectionFilter(Function(value As ltfsindex.file)
                                 Dim length As Long = value.length
                                 Return sMin <= length AndAlso length <= sMax
                             End Function)
        ResumeLayout()
    End Sub

    Private Sub 匹配文件名ToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles 匹配文件名ToolStripMenuItem.Click
        SuspendLayout()
        Dim pattern As String = "*"
        DisplayHelper.ShowInputDialog("Regex", "By regex", pattern)
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(FileBrowser))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FileBrowser")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Filter")
                        Log.Information("File browser filename filter applied. Pattern={Pattern}.", pattern)
                    End Using
                End Using
            End Using
        End Using
        Dim matcher As New System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.Compiled)
        ApplySelectionFilter(Function(value As ltfsindex.file)
                                 Return matcher.IsMatch(If(value.name, String.Empty))
                             End Function)
        ResumeLayout()
    End Sub

    Private Sub ApplySelectionFilter(predicate As Func(Of ltfsindex.file, Boolean))
        If schema Is Nothing OrElse predicate Is Nothing Then Return

        If schema._file IsNot Nothing Then
            For Each rootFile As ltfsindex.file In schema._file
                rootFile.Selected = predicate(rootFile)
            Next
        End If
        If schema._directory IsNot Nothing Then
            For Each rootDirectory As ltfsindex.directory In schema._directory
                ApplyDirectorySelectionFilter(rootDirectory, predicate)
            Next
        End If

        RefreshLoadedSelectionStates()
    End Sub

    Private Function ApplyDirectorySelectionFilter(directory As ltfsindex.directory,
                                                    predicate As Func(Of ltfsindex.file, Boolean)) As Boolean
        If directory Is Nothing Then Return False
        Dim hasSelectedFile As Boolean = False
        For Each childFile As ltfsindex.file In directory.EnumerateLazyFiles()
            Dim selected As Boolean = predicate(childFile)
            childFile.Selected = selected
            If selected Then hasSelectedFile = True
        Next

        For Each childDirectory As ltfsindex.directory In directory.EnumerateLazyDirectories()
            If ApplyDirectorySelectionFilter(childDirectory, predicate) Then hasSelectedFile = True
        Next

        directory.Selected = hasSelectedFile
        Return hasSelectedFile
    End Function

    Private Sub RefreshLoadedSelectionStates()
        SyncLock EventLock
            If EventLock Then Return
            EventLock = True
        End SyncLock
        Try
            For Each node As TreeNode In TreeView1.Nodes
                RefreshChackState(node)
            Next
        Finally
            SyncLock EventLock
                EventLock = False
            End SyncLock
        End Try
    End Sub

    Private Sub FileBrowser_Closing(sender As Object, e As CancelEventArgs) Handles Me.Closing
        My.Settings.FileBrowser_CopyInfo = CheckBox1.Checked
        My.Settings.Save()
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(FileBrowser))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FileBrowser")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Lifecycle")
                        Log.Information("File browser closed. CopyInfoEnabled={CopyInfoEnabled}.", CheckBox1.Checked)
                    End Using
                End Using
            End Using
        End Using
    End Sub
End Class
