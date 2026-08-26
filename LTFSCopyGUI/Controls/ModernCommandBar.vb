Imports System.Drawing.Drawing2D

Public Enum ModernCommandIcon
    ImportFiles
    WriteTape
    BackupIndex
    MergeHash
    VerifySource
    SafeEject
    ForceEject
End Enum

Public Class ModernCommandBar
    Inherits Panel

    Private ReadOnly commandToolTip As New ToolTip() With {
        .AutoPopDelay = 10000,
        .InitialDelay = 450,
        .ReshowDelay = 100,
        .ShowAlways = True
    }

    Public Sub New()
        SetStyle(ControlStyles.ContainerControl Or ControlStyles.OptimizedDoubleBuffer Or
                 ControlStyles.ResizeRedraw Or ControlStyles.SupportsTransparentBackColor, True)
        BackColor = Color.Transparent
        TabStop = False
    End Sub

    Protected Overrides Sub OnControlAdded(e As ControlEventArgs)
        MyBase.OnControlAdded(e)
        Dim button = TryCast(e.Control, ModernCommandButton)
        If button Is Nothing Then Return

        button.Margin = Padding.Empty
        commandToolTip.SetToolTip(button, button.Text)
        AddHandler button.TextChanged,
            Sub(sender As Object, args As EventArgs)
                Dim changedButton = DirectCast(sender, ModernCommandButton)
                commandToolTip.SetToolTip(changedButton, changedButton.Text)
            End Sub
    End Sub

    Protected Overrides Sub OnLayout(levent As LayoutEventArgs)
        MyBase.OnLayout(levent)

        Dim buttonSize = ScaleLogical(24)
        Dim gapCount = Math.Max(0, Controls.Count - 1)
        Dim availableForGaps = Math.Max(0, ClientSize.Width - Controls.Count * buttonSize)
        Dim gap = If(gapCount > 0, availableForGaps \ gapCount, 0)
        Dim extraGapPixels = If(gapCount > 0, availableForGaps Mod gapCount, 0)
        Dim occupiedWidth = Controls.Count * buttonSize + availableForGaps
        Dim x = Math.Max(0, (ClientSize.Width - occupiedWidth) \ 2)
        Dim y = Math.Max(0, (ClientSize.Height - buttonSize) \ 2)

        For index = 0 To Controls.Count - 1
            Dim child = Controls(index)
            child.SetBounds(x, y, buttonSize, buttonSize)
            x += buttonSize + gap
            If index < extraGapPixels Then x += 1
        Next
    End Sub

    Private Function ScaleLogical(value As Integer) As Integer
        Return CInt(Math.Round(value * DeviceDpi / 96.0R))
    End Function

    Protected Overrides Sub Dispose(disposing As Boolean)
        If disposing Then commandToolTip.Dispose()
        MyBase.Dispose(disposing)
    End Sub
End Class

Public Class ModernCommandButton
    Inherits Button

    Private keyboardPressed As Boolean

    Public Property CommandIcon As ModernCommandIcon

    Public Sub New()
        SetStyle(ControlStyles.AllPaintingInWmPaint Or ControlStyles.OptimizedDoubleBuffer Or
                 ControlStyles.ResizeRedraw Or ControlStyles.SupportsTransparentBackColor Or
                 ControlStyles.UserPaint, True)
        AutoSize = False
        FlatStyle = FlatStyle.Flat
        FlatAppearance.BorderSize = 0
        FlatAppearance.MouseDownBackColor = Color.Transparent
        FlatAppearance.MouseOverBackColor = Color.Transparent
        UseVisualStyleBackColor = False
        BackColor = Color.Transparent
        TabStop = True
    End Sub

    Protected Overrides Sub OnPaint(e As PaintEventArgs)
        Dim g = e.Graphics
        OnPaintBackground(e)
        g.SmoothingMode = SmoothingMode.AntiAlias
        g.PixelOffsetMode = PixelOffsetMode.HighQuality

        Dim bounds = New Rectangle(0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1))
        Dim state = GetState()
        Dim background = GetStateBackground(state)
        If background.A > 0 AndAlso bounds.Width > 0 AndAlso bounds.Height > 0 Then
            Using path = RoundedRectangle(bounds, Math.Max(2.0F, CSng(DeviceDpi) / 24.0F)),
                  brush As New SolidBrush(background)
                g.FillPath(brush, path)
            End Using
        End If

        Dim iconColor = If(Enabled, GetIconColor(), SystemColors.GrayText)
        Dim iconScale = Math.Min(ClientSize.Width, ClientSize.Height) / 24.0F
        Dim stateOffset = If(state = ButtonState.Pressed, Math.Max(1.0F, CSng(DeviceDpi) / 96.0F), 0.0F)
        Dim saved = g.Save()
        g.TranslateTransform((ClientSize.Width - 16.0F * iconScale) / 2.0F + stateOffset,
                             (ClientSize.Height - 16.0F * iconScale) / 2.0F + stateOffset)
        g.ScaleTransform(iconScale, iconScale)
        DrawIcon(g, iconColor)
        g.Restore(saved)

        If Focused AndAlso ShowFocusCues Then
            Dim focusBounds = Rectangle.Inflate(bounds, -2, -2)
            ControlPaint.DrawFocusRectangle(g, focusBounds)
        End If
    End Sub

    Private Enum ButtonState
        Normal
        Hot
        Pressed
    End Enum

    Private Function GetState() As ButtonState
        If keyboardPressed Then Return ButtonState.Pressed
        If Capture AndAlso ClientRectangle.Contains(PointToClient(MousePosition)) Then Return ButtonState.Pressed
        If ClientRectangle.Contains(PointToClient(MousePosition)) Then Return ButtonState.Hot
        Return ButtonState.Normal
    End Function

    Protected Overrides Sub OnKeyDown(kevent As KeyEventArgs)
        If kevent.KeyCode = Keys.Space AndAlso Not keyboardPressed Then
            keyboardPressed = True
            Invalidate()
        End If
        MyBase.OnKeyDown(kevent)
    End Sub

    Protected Overrides Sub OnKeyUp(kevent As KeyEventArgs)
        If kevent.KeyCode = Keys.Space AndAlso keyboardPressed Then
            keyboardPressed = False
            Invalidate()
        End If
        MyBase.OnKeyUp(kevent)
    End Sub

    Protected Overrides Sub OnMouseEnter(e As EventArgs)
        MyBase.OnMouseEnter(e)
        Invalidate()
    End Sub

    Protected Overrides Sub OnMouseLeave(e As EventArgs)
        MyBase.OnMouseLeave(e)
        Invalidate()
    End Sub

    Protected Overrides Sub OnMouseDown(mevent As MouseEventArgs)
        MyBase.OnMouseDown(mevent)
        Invalidate()
    End Sub

    Protected Overrides Sub OnMouseUp(mevent As MouseEventArgs)
        MyBase.OnMouseUp(mevent)
        Invalidate()
    End Sub

    Private Function GetStateBackground(state As ButtonState) As Color
        If Not Enabled Then Return Color.Transparent
        Select Case state
            Case ButtonState.Hot
                Return Color.FromArgb(30, SystemColors.Highlight)
            Case ButtonState.Pressed
                Return Color.FromArgb(58, SystemColors.Highlight)
            Case Else
                Return Color.Transparent
        End Select
    End Function

    Private Function GetIconColor() As Color
        Select Case CommandIcon
            Case ModernCommandIcon.WriteTape
                Return Color.Black
            Case ModernCommandIcon.SafeEject
                Return Color.FromArgb(28, 145, 70)
            Case ModernCommandIcon.ForceEject
                Return Color.FromArgb(210, 92, 43)
            Case Else
                Return SystemColors.ControlText
        End Select
    End Function

    Private Sub DrawIcon(g As Graphics, color As Color)
        Using pen As New Pen(color, 1.45F)
            pen.StartCap = LineCap.Round
            pen.EndCap = LineCap.Round
            pen.LineJoin = LineJoin.Round

            Select Case CommandIcon
                Case ModernCommandIcon.ImportFiles
                    If My.Settings.LTFSWriter_ModernIcon Then
                        g.DrawLines(pen, {New PointF(1.5F, 5.0F), New PointF(1.5F, 13.5F), New PointF(14.5F, 13.5F), New PointF(14.5F, 5.0F), New PointF(8.0F, 5.0F), New PointF(6.5F, 3.0F), New PointF(1.5F, 3.0F)})
                        g.DrawLine(pen, 8.0F, 6.5F, 8.0F, 11.0F)
                        g.DrawLines(pen, {New PointF(5.9F, 9.0F), New PointF(8.0F, 11.1F), New PointF(10.1F, 9.0F)})
                    Else
                        g.InterpolationMode = InterpolationMode.NearestNeighbor
                        If Enabled Then
                            g.DrawImage(My.Resources.Icon_Open, New RectangleF(0F, 0F, 16.0F, 16.0F))
                        Else
                            ControlPaint.DrawImageDisabled(g, My.Resources.Icon_Open, 0, 0, BackColor)
                        End If
                    End If
                Case ModernCommandIcon.WriteTape
                    If My.Settings.LTFSWriter_ModernIcon Then
                        Dim playColor = If(Enabled, Color.FromArgb(28, 145, 70), color)
                        Using ringPen As New Pen(color, 3.8F),
                          playBrush As New SolidBrush(playColor)
                            g.DrawEllipse(ringPen, 1.9F, 1.9F, 10.2F, 10.2F)
                            g.FillPolygon(playBrush, {New PointF(8.0F, 7.2F), New PointF(15.5F, 11.6F), New PointF(8.0F, 16.0F)})
                        End Using
                    Else
                        g.InterpolationMode = InterpolationMode.NearestNeighbor
                        If Enabled Then
                            g.DrawImage(My.Resources.Icon_Copy, New RectangleF(0F, 0F, 16.0F, 16.0F))
                        Else
                            ControlPaint.DrawImageDisabled(g, My.Resources.Icon_Copy, 0, 0, BackColor)
                        End If
                    End If
                Case ModernCommandIcon.BackupIndex
                    If My.Settings.LTFSWriter_ModernIcon Then
                        g.DrawLines(pen, {New PointF(3.0F, 1.5F), New PointF(9.5F, 1.5F), New PointF(13.0F, 5.0F), New PointF(13.0F, 14.5F), New PointF(3.0F, 14.5F), New PointF(3.0F, 1.5F)})
                        g.DrawLines(pen, {New PointF(9.5F, 1.5F), New PointF(9.5F, 5.0F), New PointF(13.0F, 5.0F)})
                        g.DrawLine(pen, 5.5F, 8.0F, 10.5F, 8.0F)
                        g.DrawLine(pen, 5.5F, 10.5F, 9.0F, 10.5F)
                    Else
                        g.InterpolationMode = InterpolationMode.NearestNeighbor
                        If Enabled Then
                            g.DrawImage(My.Resources.Icon_SaveIndex, New RectangleF(0F, 0F, 16.0F, 16.0F))
                        Else
                            ControlPaint.DrawImageDisabled(g, My.Resources.Icon_SaveIndex, 0, 0, BackColor)
                        End If
                    End If

                Case ModernCommandIcon.MergeHash
                    If My.Settings.LTFSWriter_ModernIcon Then
                        g.DrawLine(pen, 5.5F, 2.0F, 3.8F, 14.0F)
                        g.DrawLine(pen, 10.2F, 2.0F, 8.5F, 14.0F)
                        g.DrawLine(pen, 2.0F, 6.0F, 13.0F, 6.0F)
                        g.DrawLine(pen, 1.5F, 10.0F, 12.5F, 10.0F)
                        g.DrawLines(pen, {New PointF(12.0F, 8.0F), New PointF(14.0F, 10.0F), New PointF(12.0F, 12.0F)})
                    Else
                        g.InterpolationMode = InterpolationMode.NearestNeighbor
                        If Enabled Then
                            g.DrawImage(My.Resources.Icon_MergeSHA1, New RectangleF(0F, 0F, 16.0F, 16.0F))
                        Else
                            ControlPaint.DrawImageDisabled(g, My.Resources.Icon_MergeSHA1, 0, 0, BackColor)
                        End If
                    End If

                Case ModernCommandIcon.VerifySource
                    If My.Settings.LTFSWriter_ModernIcon Then
                        g.DrawLines(pen, {New PointF(3.0F, 1.5F), New PointF(9.5F, 1.5F), New PointF(13.0F, 5.0F), New PointF(13.0F, 8.0F)})
                        g.DrawLines(pen, {New PointF(9.5F, 1.5F), New PointF(9.5F, 5.0F), New PointF(13.0F, 5.0F)})
                        g.DrawLine(pen, 3.0F, 1.5F, 3.0F, 14.5F)
                        g.DrawLine(pen, 3.0F, 14.5F, 7.0F, 14.5F)
                        g.DrawLines(pen, {New PointF(7.0F, 11.5F), New PointF(9.5F, 14.0F), New PointF(14.5F, 9.0F)})
                    Else
                        g.InterpolationMode = InterpolationMode.NearestNeighbor
                        If Enabled Then
                            g.DrawImage(My.Resources.Icon_CheckSum, New RectangleF(0F, 0F, 16.0F, 16.0F))
                        Else
                            ControlPaint.DrawImageDisabled(g, My.Resources.Icon_CheckSum, 0, 0, BackColor)
                        End If
                    End If

                Case ModernCommandIcon.SafeEject
                    If My.Settings.LTFSWriter_ModernIcon Then
                        g.DrawPolygon(pen, {New PointF(8.0F, 2.0F), New PointF(13.0F, 10.0F), New PointF(3.0F, 10.0F)})
                        g.DrawLine(pen, 3.0F, 13.5F, 13.0F, 13.5F)
                    Else
                        g.InterpolationMode = InterpolationMode.NearestNeighbor
                        If Enabled Then
                            g.DrawImage(My.Resources.Icon_Eject, New RectangleF(0F, 0F, 16.0F, 16.0F))
                        Else
                            ControlPaint.DrawImageDisabled(g, My.Resources.Icon_Eject, 0, 0, BackColor)
                        End If
                    End If

                Case ModernCommandIcon.ForceEject
                    If My.Settings.LTFSWriter_ModernIcon Then
                        g.DrawPolygon(pen, {New PointF(8.0F, 2.0F), New PointF(13.0F, 10.0F), New PointF(3.0F, 10.0F)})
                        g.DrawLine(pen, 3.0F, 13.5F, 13.0F, 13.5F)
                        g.DrawLine(pen, 8.0F, 4.2F, 8.0F, 7.0F)
                        Using brush As New SolidBrush(color)
                            g.FillEllipse(brush, 7.35F, 8.0F, 1.3F, 1.3F)
                        End Using
                    Else
                        g.InterpolationMode = InterpolationMode.NearestNeighbor
                        If Enabled Then
                            g.DrawImage(My.Resources.Icon_ForceEject, New RectangleF(0F, 0F, 16.0F, 16.0F))
                        Else
                            ControlPaint.DrawImageDisabled(g, My.Resources.Icon_ForceEject, 0, 0, BackColor)
                        End If
                    End If
            End Select
        End Using
    End Sub

    Private Shared Function RoundedRectangle(bounds As Rectangle, radius As Single) As GraphicsPath
        Dim path As New GraphicsPath()
        Dim diameter = radius * 2.0F
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180.0F, 90.0F)
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270.0F, 90.0F)
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0.0F, 90.0F)
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90.0F, 90.0F)
        path.CloseFigure()
        Return path
    End Function
End Class

Friend Module GraphicsExtensions
    <Runtime.CompilerServices.Extension>
    Public Sub DrawRoundedRectangle(g As Graphics, pen As Pen, bounds As RectangleF, radius As Single)
        Using path As New GraphicsPath()
            Dim diameter = radius * 2.0F
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180.0F, 90.0F)
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270.0F, 90.0F)
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0.0F, 90.0F)
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90.0F, 90.0F)
            path.CloseFigure()
            g.DrawPath(pen, path)
        End Using
    End Sub
End Module
