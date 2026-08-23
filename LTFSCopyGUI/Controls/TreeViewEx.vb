Imports LTFSCopyGUI.Native

Public Class TreeViewEx
    Inherits TreeView
    Private Const TVIS_STATEIMAGEMASK As UInteger = &HF000
    Private Const TVS_EX_DOUBLEBUFFER As UInteger = &H4
    Private Const TVS_EX_PARTIALCHECKBOXES As UInteger = &H80

    Private Function INDEXTOSTATEIMAGEMASK(i As Integer) As Integer
        Return i << 12
    End Function

    Protected Overridable ReadOnly Property UsePartialCheckboxes As Boolean
        Get
            Return True
        End Get
    End Property

    Protected Overrides Sub OnHandleCreated(e As EventArgs)
        MyBase.OnHandleCreated(e)
        Dim style As UInteger = TVS_EX_DOUBLEBUFFER
        If UsePartialCheckboxes Then style = style Or TVS_EX_PARTIALCHECKBOXES
        NativeMethods.SetTreeViewExtendedStyle(Handle, style, style).ThrowIfFailed("Set tree view extended style failed.")
    End Sub

    Public Sub SetNodeCheckState(node As TreeNode, state As CheckState)
        If state = CheckState.Indeterminate Then
            If Environment.OSVersion.Version.Major >= 6 Then
                NativeMethods.SetTreeViewItemState(
                    Handle,
                    node.Handle,
                    CUInt(INDEXTOSTATEIMAGEMASK(3)),
                    TVIS_STATEIMAGEMASK).ThrowIfFailed("Set tree node state failed.")
            Else
                node.Checked = False
            End If
        Else
            node.Checked = (state = CheckState.Checked)
        End If
    End Sub
End Class
