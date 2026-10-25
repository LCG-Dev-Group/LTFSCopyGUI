Option Strict On
Option Explicit On

Imports System.Globalization
Imports System.IO

Partial Public Class SCSIGUI
    Private _template As ScsiTemplate
    Private _templatePath As String
    Private _selectedCommand As ScsiCommandDef
    Private _runtimeNodes As List(Of ScsiValueNode)
    Private _loading As Boolean
    Private _editors As New Dictionary(Of ScsiValueNode, Control)()
    Private _elementMenu As ContextMenuStrip

    Private Sub LoadTemplateList()
        _loading = True
        Try
            cboTemplates.Items.Clear()
            For Each cname In XmlTemplateStore.ListTemplates()
                cboTemplates.Items.Add(cname)
            Next
            If cboTemplates.Items.Count > 0 Then cboTemplates.SelectedIndex = 0
        Finally
            _loading = False
        End Try
        If cboTemplates.SelectedIndex >= 0 Then LoadSelectedTemplate()
    End Sub

    Private Sub TemplateChanged(sender As Object, e As EventArgs) Handles cboTemplates.SelectedIndexChanged
        If Not _loading AndAlso cboTemplates.SelectedIndex >= 0 Then LoadSelectedTemplate()
    End Sub

    Private Sub LoadSelectedTemplate()
        If cboTemplates.SelectedItem Is Nothing Then Return
        Try
            _templatePath = Path.Combine(XmlTemplateStore.TemplateDirectory(), CStr(cboTemplates.SelectedItem) & ".xml")
            _template = XmlTemplateStore.LoadTemplate(_templatePath)
            RefreshCommands()
        Catch ex As Exception
            MessageBox.Show("加载模板失败：" & ex.Message, "模板错误", MessageBoxButtons.OK, MessageBoxIcon.Error)
        End Try
    End Sub

    Private Sub RefreshCommands(Optional selectIndex As Integer = 0)
        lstCommands.BeginUpdate()
        lstCommands.Items.Clear()
        If _template IsNot Nothing Then
            For Each c In _template.Commands
                lstCommands.Items.Add(New CommandListItem(c))
            Next
        End If
        lstCommands.EndUpdate()
        If lstCommands.Items.Count > 0 Then
            lstCommands.SelectedIndex = Math.Max(0, Math.Min(selectIndex, lstCommands.Items.Count - 1))
        Else
            _selectedCommand = Nothing
            _runtimeNodes = Nothing
            RenderValueEditors()
        End If
        RefreshDefinitionTree()
    End Sub

    Private Sub CommandChanged(sender As Object, e As EventArgs) Handles lstCommands.SelectedIndexChanged
        Dim item = TryCast(lstCommands.SelectedItem, CommandListItem)
        _selectedCommand = If(item Is Nothing, Nothing, item.Command)
        If _selectedCommand IsNot Nothing AndAlso _template IsNot Nothing Then
            _runtimeNodes = ScsiCodec.CreateRuntimeNodes(_selectedCommand, _template)
        Else
            _runtimeNodes = Nothing
        End If
        RenderValueEditors()
        RefreshDefinitionTree()
    End Sub

    Private Sub RenderValueEditors()
        ' 只重建动态参数编辑器；指令信息、按钮和 CDB/Param 文本框由 Designer 静态创建。
        pnlValues.SuspendLayout()
        pnlValues.Controls.Clear()
        _editors.Clear()

        If _selectedCommand Is Nothing OrElse _runtimeNodes Is Nothing Then
            lblCommandInfo.Text = "请选择指令"
            pnlValues.ResumeLayout(True)
            Return
        End If

        lblCommandInfo.Text = String.Format("{0}   Opcode=0x{1:X}   CDB={2} bytes   DataIn={3}",
            _selectedCommand.Name, _selectedCommand.Opcode, _selectedCommand.CdbLength, CInt(_selectedCommand.DataIn))

        For Each node In _runtimeNodes
            RenderNode(node, pnlValues, 0)
        Next

        FitNestedPanelHeights(pnlValues)
        pnlValues.ResumeLayout(True)
    End Sub

    Private Function FindHexBox(key As String) As TextBox
        Select Case key
            Case "cdbInput" : Return txtCdbInput
            Case "paramInput" : Return txtParamInput
            Case "cdbOutput" : Return txtCdbOutput
            Case "paramOutput" : Return txtParamOutput
            Case Else : Return Nothing
        End Select
    End Function

    Private Sub RenderNode(node As ScsiValueNode, host As Control, indent As Integer)
        Dim d = node.Definition
        If d Is Nothing Then Return

        ' PresenceFormula 不成立时，不在界面显示该节点。
        Try
            If Not ScsiCodec.IsNodePresent(node) Then Return
        Catch ex As Exception
            MessageBox.Show(ex.ToString())
            Return
        End Try
        Select Case d.Kind.ToUpperInvariant()
            Case "GROUP", "PAGE"
                Dim group As New FlowLayoutPanel With {
                    .FlowDirection = FlowDirection.TopDown, .WrapContents = False, .AutoSize = True,
                    .AutoSizeMode = AutoSizeMode.GrowAndShrink, .Width = Math.Max(500, pnlValues.ClientSize.Width - 45),
                    .Margin = New Padding(8 + indent * 8, 4, 4, 4), .Padding = New Padding(5),
                    .BorderStyle = BorderStyle.FixedSingle
                }
                group.Controls.Add(New Label With {.Text = d.Name & " [" & d.Kind & "]", .AutoSize = True, .Font = New Font(Font, FontStyle.Bold)})
                For Each child In node.Children
                    RenderNode(child, group, indent + 1)
                Next
                host.Controls.Add(group)
            Case "PAGELIST"
                RenderPageList(node, host, indent)
            Case Else
                RenderField(node, host, indent)
        End Select
    End Sub


    ' 递归修正 Group、Page 和 Page 实例容器高度。先处理最内层，再处理外层。
    Private Sub FitNestedPanelHeights(parent As Control)
        If parent Is Nothing Then Return

        ' 先从最内层向外完成布局，确保子容器高度变化能传递到父容器。
        For Each child As Control In parent.Controls
            FitNestedPanelHeights(child)
        Next

        Dim flow = TryCast(parent, FlowLayoutPanel)
        If flow Is Nothing OrElse flow Is pnlValues Then Return
        If flow.BorderStyle <> BorderStyle.FixedSingle Then Return

        flow.SuspendLayout()
        Try
            ' 让 WinForms 先重新计算当前层所有子控件的位置。
            flow.PerformLayout()

            Dim contentBottom As Integer = 0
            For Each child As Control In flow.Controls
                If Not child.Visible Then Continue For
                Dim childBottom As Integer = child.Top + child.Height + child.Margin.Bottom
                If childBottom > contentBottom Then contentBottom = childBottom
            Next

            ' BorderStyle.FixedSingle 还需要边框空间；额外预留 12px，避免最后一行
            ' TextBox/ComboBox 的边框与 Group 边框重叠。Margin 不计入容器内部高度。
            Dim requiredHeight As Integer =
                flow.Padding.Top + contentBottom + flow.Padding.Bottom + 2 + 2

            If requiredHeight < 36 Then requiredHeight = 36

            ' 固定计算后的高度，避免 AutoSize 在外层布局时又将高度压回去。
            flow.AutoSize = False
            If flow.Height < requiredHeight OrElse flow.Height > requiredHeight + 2 Then
                flow.Height = requiredHeight
            End If
        Finally
            flow.ResumeLayout(True)
        End Try

        ' 重新布局一次，令新的子容器高度更新到父容器的排列结果。
        If parent IsNot pnlValues Then parent.PerformLayout()
    End Sub

    Private Sub RenderField(node As ScsiValueNode, host As Control, indent As Integer)
        Dim d = node.Definition
        Dim row As New TableLayoutPanel With {
            .ColumnCount = 3, .RowCount = 1, .Width = Math.Max(500, pnlValues.ClientSize.Width - 45 - indent * 8),
            .Height = 40, .Margin = New Padding(8 + indent * 8, 1, 4, 1)
        }
        row.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 42.0F))
        row.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 42.0F))
        row.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 16.0F))
        row.Controls.Add(New Label With {.Text = d.Name, .Dock = DockStyle.Fill, .TextAlign = ContentAlignment.MiddleLeft}, 0, 0)

        Dim editor As Control
        If d.FieldType = ScsiFieldType.EnumValue Then
            Dim cb As New ComboBox With {.Dock = DockStyle.Fill, .DropDownStyle = ComboBoxStyle.DropDownList}
            For Each part In d.EnumItems.Split(";"c)
                If String.IsNullOrWhiteSpace(part) Then Continue For
                Dim kv = part.Split(New Char() {"="c}, 2)
                If kv.Length = 2 Then cb.Items.Add(New EnumOption(kv(0).Trim(), kv(1).Trim()))
            Next
            Dim current = Convert.ToString(node.Value, CultureInfo.InvariantCulture)
            For i As Integer = 0 To cb.Items.Count - 1
                If String.Equals(DirectCast(cb.Items(i), EnumOption).Value, current, StringComparison.OrdinalIgnoreCase) Then
                    cb.SelectedIndex = i
                    Exit For
                End If
            Next
            If cb.SelectedIndex < 0 AndAlso cb.Items.Count > 0 Then cb.SelectedIndex = 0
            AddHandler cb.SelectedIndexChanged, Sub(sender, e)
                                                    If cb.SelectedItem IsNot Nothing Then
                                                        node.Value = DirectCast(cb.SelectedItem, EnumOption).Value
                                                        ValuesChanged()
                                                        ' 枚举字段可能被其他节点的 PresenceFormula 引用，
                                                        ' 选择变化后重建界面，使条件字段立即显示/隐藏。
                                                        RebuildValueEditors()
                                                    End If
                                                End Sub
            editor = cb
            editor.Enabled = Not d.ReadOnlyCalculated AndAlso String.IsNullOrWhiteSpace(d.Formula)
        Else
            Dim initialText As String = Convert.ToString(node.Value, CultureInfo.InvariantCulture)
            If d.FieldType = ScsiFieldType.IntegerValue Then
                initialText = FormatIntegerAsHex(initialText)
            End If

            Dim tb As New TextBox With {.Dock = DockStyle.Fill, .Text = initialText}
            If d.FieldType = ScsiFieldType.IntegerValue Then
                tb.CharacterCasing = CharacterCasing.Upper
                AddHandler tb.TextChanged, Sub(sender, e)
                                               Dim hexValue As String = Nothing
                                               If TryNormalizeHexInput(tb.Text, hexValue) Then
                                                   node.Value = hexValue
                                                   ValuesChanged()
                                               End If
                                           End Sub
                AddHandler tb.Validated, Sub(sender, e)
                                             Dim hexValue As String = Nothing
                                             If TryNormalizeHexInput(tb.Text, hexValue) Then
                                                 Dim displayValue = hexValue.Substring(2).ToUpperInvariant()
                                                 If tb.Text <> displayValue Then tb.Text = displayValue
                                                 node.Value = hexValue
                                                 ValuesChanged()
                                             End If
                                         End Sub
            Else
                AddHandler tb.TextChanged, Sub(sender, e)
                                               node.Value = tb.Text
                                               ValuesChanged()
                                           End Sub
            End If
            tb.ReadOnly = d.ReadOnlyCalculated OrElse (Not String.IsNullOrWhiteSpace(d.Formula))
            editor = tb
        End If
        _editors(node) = editor
        row.Controls.Add(editor, 1, 0)
        row.Controls.Add(New Label With {
            .Text = d.Area.ToString() & "  bit " & d.BitOffset & ":" & d.BitLength,
            .Dock = DockStyle.Fill, .TextAlign = ContentAlignment.MiddleLeft, .ForeColor = SystemColors.GrayText
        }, 2, 0)
        host.Controls.Add(row)
    End Sub

    ' 整数参数文本框采用十六进制显示/输入；内部统一保存为 0x 前缀，供 ScsiCodec 解析。
    Private Shared Function FormatIntegerAsHex(value As String) As String
        If String.IsNullOrWhiteSpace(value) Then Return "0"
        Dim s = value.Trim()
        If s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) Then
            Return s.Substring(2).ToUpperInvariant()
        End If

        Dim unsignedValue As ULong
        If ULong.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, unsignedValue) Then
            Return unsignedValue.ToString("X", CultureInfo.InvariantCulture)
        End If

        Dim signedValue As Long
        If Long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, signedValue) AndAlso signedValue >= 0 Then
            Return signedValue.ToString("X", CultureInfo.InvariantCulture)
        End If
        Return s
    End Function

    Private Shared Function TryNormalizeHexInput(text As String, ByRef normalized As String) As Boolean
        normalized = Nothing
        If text Is Nothing Then Return False
        Dim s = text.Trim()
        If s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) Then s = s.Substring(2)
        If s.Length = 0 Then Return False

        For Each ch As Char In s
            If Not Uri.IsHexDigit(ch) Then Return False
        Next

        normalized = "0x" & s.ToUpperInvariant()
        Return True
    End Function

    Private Sub RenderPageList(node As ScsiValueNode, host As Control, indent As Integer)
        Dim d = node.Definition
        Dim outer As New FlowLayoutPanel With {
            .FlowDirection = FlowDirection.TopDown, .WrapContents = False, .AutoSize = True,
            .AutoSizeMode = AutoSizeMode.GrowAndShrink, .Width = Math.Max(500, pnlValues.ClientSize.Width - 45),
            .Margin = New Padding(8 + indent * 8, 4, 4, 4), .Padding = New Padding(5),
            .BorderStyle = BorderStyle.FixedSingle
        }
        Dim header As New FlowLayoutPanel With {.AutoSize = True, .WrapContents = False}
        header.Controls.Add(New Label With {.Text = d.Name & " [PageList]", .AutoSize = True, .Font = New Font(Font, FontStyle.Bold), .Margin = New Padding(0, 6, 12, 0)})
        Dim selector As New ComboBox With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 210}
        Dim allowed = ScsiCodec.GetAllowedPageTemplates(d, _template)
        For Each p In allowed
            selector.Items.Add(New PageTemplateOption(p))
        Next
        If selector.Items.Count > 0 Then selector.SelectedIndex = 0
        Dim add As New Button With {.Text = "添加 Page", .AutoSize = True, .Enabled = selector.Items.Count > 0}
        AddHandler add.Click, Sub(sender, e)
                                  If selector.SelectedItem Is Nothing Then Return
                                  If d.MaxItems > 0 AndAlso node.Items.Count >= d.MaxItems Then
                                      MessageBox.Show("Page 数量已达到 MaxItems 限制。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information)
                                      Return
                                  End If
                                  Dim pageDef = DirectCast(selector.SelectedItem, PageTemplateOption).Page
                                  Dim item = ScsiCodec.CreateNode(pageDef, _template, node)
                                  item.InstanceName = pageDef.Name & " [" & (node.Items.Count + 1).ToString() & "]"
                                  node.Items.Add(item)
                                  RebuildValueEditors()
                              End Sub
        header.Controls.Add(selector)
        header.Controls.Add(add)
        header.Controls.Add(New Label With {
            .Text = If(d.MaxItems > 0, String.Format("实例数：{0}/{1}", node.Items.Count, d.MaxItems), String.Format("实例数：{0}", node.Items.Count)),
            .AutoSize = True, .Margin = New Padding(8, 6, 0, 0), .ForeColor = SystemColors.GrayText
        })
        outer.Controls.Add(header)

        For i As Integer = 0 To node.Items.Count - 1
            Dim item = node.Items(i)
            Dim itemPanel As New FlowLayoutPanel With {
                .FlowDirection = FlowDirection.TopDown, .WrapContents = False, .AutoSize = True,
                .AutoSizeMode = AutoSizeMode.GrowAndShrink, .Width = Math.Max(480, pnlValues.ClientSize.Width - 70),
                .Margin = New Padding(12, 3, 3, 3), .Padding = New Padding(4), .BorderStyle = BorderStyle.FixedSingle
            }
            Dim itemHeader As New FlowLayoutPanel With {.AutoSize = True, .WrapContents = False}
            itemHeader.Controls.Add(New Label With {.Text = item.InstanceName, .AutoSize = True, .Font = New Font(Font, FontStyle.Bold), .Margin = New Padding(0, 6, 12, 0)})
            Dim moveUp As New Button With {.Text = "↑ 上移", .AutoSize = True, .Tag = item, .Enabled = (i > 0)}
            AddHandler moveUp.Click, Sub(sender, e)
                                         Dim target = DirectCast(DirectCast(sender, Button).Tag, ScsiValueNode)
                                         Dim oldIndex = node.Items.IndexOf(target)
                                         If oldIndex <= 0 Then Return
                                         node.Items.RemoveAt(oldIndex)
                                         node.Items.Insert(oldIndex - 1, target)
                                         RenumberPageInstances(node)
                                         RebuildValueEditors()
                                     End Sub
            Dim moveDown As New Button With {.Text = "↓ 下移", .AutoSize = True, .Tag = item, .Enabled = (i < node.Items.Count - 1)}
            AddHandler moveDown.Click, Sub(sender, e)
                                           Dim target = DirectCast(DirectCast(sender, Button).Tag, ScsiValueNode)
                                           Dim oldIndex = node.Items.IndexOf(target)
                                           If oldIndex < 0 OrElse oldIndex >= node.Items.Count - 1 Then Return
                                           node.Items.RemoveAt(oldIndex)
                                           node.Items.Insert(oldIndex + 1, target)
                                           RenumberPageInstances(node)
                                           RebuildValueEditors()
                                       End Sub
            Dim remove As New Button With {.Text = "删除", .AutoSize = True, .Tag = item}
            AddHandler remove.Click, Sub(sender, e)
                                         If d.MinItems > 0 AndAlso node.Items.Count <= d.MinItems Then
                                             MessageBox.Show("实例数不能低于 MinItems。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information)
                                             Return
                                         End If
                                         Dim target = DirectCast(DirectCast(sender, Button).Tag, ScsiValueNode)
                                         node.Items.Remove(target)
                                         RenumberPageInstances(node)
                                         RebuildValueEditors()
                                     End Sub
            itemHeader.Controls.Add(moveUp)
            itemHeader.Controls.Add(moveDown)
            itemHeader.Controls.Add(remove)
            itemPanel.Controls.Add(itemHeader)
            For Each child In item.Children
                RenderNode(child, itemPanel, indent + 2)
            Next
            outer.Controls.Add(itemPanel)
        Next
        host.Controls.Add(outer)
    End Sub

    Private Sub RenumberPageInstances(listNode As ScsiValueNode)
        If listNode Is Nothing Then Return
        For i As Integer = 0 To listNode.Items.Count - 1
            listNode.Items(i).InstanceName = listNode.Items(i).Definition.Name & " [" & (i + 1).ToString(CultureInfo.InvariantCulture) & "]"
        Next
    End Sub

    Private Sub ValuesChanged()
        If _runtimeNodes Is Nothing Then Return
        Try
            ScsiCodec.RecalculateFormulas(_runtimeNodes)
            For Each pair In _editors
                If pair.Key.Definition IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(pair.Key.Definition.Formula) Then
                    Dim tb = TryCast(pair.Value, TextBox)
                    If tb IsNot Nothing Then
                        Dim formulaText = Convert.ToString(pair.Key.Value, CultureInfo.InvariantCulture)
                        If pair.Key.Definition.FieldType = ScsiFieldType.IntegerValue Then
                            formulaText = FormatIntegerAsHex(formulaText)
                        End If
                        If tb.Text <> formulaText Then
                            Dim caret = tb.SelectionStart
                            tb.Text = formulaText
                            tb.SelectionStart = Math.Min(caret, tb.TextLength)
                        End If
                    End If
                End If
            Next
        Catch
            ' 公式未完成编辑时不打断用户输入；编码时会显示详细错误。
        End Try
    End Sub

    Private Sub RebuildValueEditors()
        RenderValueEditors()
    End Sub

    Private Sub EncodeButtonClick(sender As Object, e As EventArgs) Handles btnEncode.Click
        If _selectedCommand Is Nothing OrElse _runtimeNodes Is Nothing Then Return
        Try
            Dim result = ScsiCodec.Encode(_selectedCommand, _template, _runtimeNodes)
            Dim cdbBox = FindHexBox("cdbOutput")
            Dim paramBox = FindHexBox("paramOutput")
            If cdbBox IsNot Nothing Then cdbBox.Text = BitConverter.ToString(result.CDB).Replace("-", " ")
            If paramBox IsNot Nothing Then paramBox.Text = BitConverter.ToString(result.Param).Replace("-", " ")
        Catch ex As Exception
            MessageBox.Show("生成 CDB/Param 失败：" & ex.Message, "编解码错误", MessageBoxButtons.OK, MessageBoxIcon.Error)
        End Try
    End Sub

    Private Sub DecodeButtonClick(sender As Object, e As EventArgs) Handles btnDecode.Click
        If _selectedCommand Is Nothing OrElse _runtimeNodes Is Nothing Then Return
        Try
            Dim cdbBox = FindHexBox("cdbInput")
            Dim paramBox = FindHexBox("paramInput")
            Dim cdb = ParseHex(cdbBox.Text)
            Dim param = ParseHex(paramBox.Text)
            If cdb.Length = 0 Then Throw New FormatException("CDB 输入不能为空。")

            ' 根据 CDB 开头的一个或多个 Opcode 字节自动匹配指令。
            Dim matchedCommand = FindCommandByOpcodePrefix(cdb)
            If matchedCommand Is Nothing Then
                Throw New InvalidOperationException(
                    "没有找到 Opcode 与 CDB 开头匹配的指令：" &
                    BitConverter.ToString(cdb.Take(Math.Min(4, cdb.Length)).ToArray()).Replace("-", " "))
            End If

            If Not Object.ReferenceEquals(matchedCommand, _selectedCommand) Then
                Dim matchedIndex = _template.Commands.IndexOf(matchedCommand)
                If matchedIndex >= 0 Then lstCommands.SelectedIndex = matchedIndex
            End If

            If _selectedCommand Is Nothing OrElse _runtimeNodes Is Nothing Then
                Throw New InvalidOperationException("无法初始化匹配指令的参数节点。")
            End If

            ScsiCodec.Decode(_selectedCommand, _template, _runtimeNodes, cdb, param)
            RenderValueEditors()

        Catch ex As Exception
            MessageBox.Show("解析 CDB/Param 失败：" & ex.Message, "编解码错误", MessageBoxButtons.OK, MessageBoxIcon.Error)
        End Try
    End Sub

    ' Opcode 可能占用多个字节；按整数的最短大端字节序列匹配 CDB 前缀。
    ' 若多个指令匹配同一前缀，优先选择 Opcode 字节数最多的指令。
    Private Function FindCommandByOpcodePrefix(cdb As Byte()) As ScsiCommandDef
        If _template Is Nothing OrElse cdb Is Nothing OrElse cdb.Length = 0 Then Return Nothing

        Dim best As ScsiCommandDef = Nothing
        Dim bestLength As Integer = -1

        For Each cmd In _template.Commands
            If cmd.Opcode < 0 Then Continue For

            Dim opcodeLength As Integer = 1
            Dim temp As Integer = cmd.Opcode
            While temp > &HFF
                opcodeLength += 1
                temp >>= 8
            End While

            If opcodeLength > cdb.Length OrElse opcodeLength < bestLength Then Continue For

            Dim matches As Boolean = True
            For i As Integer = 0 To opcodeLength - 1
                Dim shift As Integer = (opcodeLength - 1 - i) * 8
                Dim expected As Byte = CByte((cmd.Opcode >> shift) And &HFF)
                If cdb(i) <> expected Then
                    matches = False
                    Exit For
                End If
            Next

            If matches AndAlso opcodeLength > bestLength Then
                best = cmd
                bestLength = opcodeLength
            ElseIf matches AndAlso opcodeLength = bestLength AndAlso
                   Object.ReferenceEquals(Command, _selectedCommand) Then
                best = cmd
            End If
        Next

        Return best
    End Function

    Private Function ParseHex(value As String) As Byte()
        Dim s = If(value, "").Trim()
        If s = "" Then Return New Byte() {}
        s = System.Text.RegularExpressions.Regex.Replace(s, "(?i)0x", "")
        s = System.Text.RegularExpressions.Regex.Replace(s, "[,\-:\s]", "")
        If s.Length Mod 2 <> 0 Then Throw New FormatException("十六进制字符串必须为偶数个字符。")
        Dim bytes(s.Length \ 2 - 1) As Byte
        For i As Integer = 0 To bytes.Length - 1
            bytes(i) = Byte.Parse(s.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
        Next
        Return bytes
    End Function

    Private Sub RefreshDefinitionTree()
        Dim selectedId As String = ""
        Dim selectedDef = SelectedDefinition()
        If selectedDef IsNot Nothing Then selectedId = selectedDef.Id
        tvElements.BeginUpdate()
        tvElements.Nodes.Clear()
        If _template IsNot Nothing Then
            Dim commandRoot As New TreeNode(If(_selectedCommand Is Nothing, "当前指令元素", "当前指令元素 - " & _selectedCommand.Name & " (0x" & _selectedCommand.Opcode.ToString("X2") & ")")) With {.Tag = "command-root"}
            If _selectedCommand IsNot Nothing Then
                For Each e In _selectedCommand.Elements
                    commandRoot.Nodes.Add(BuildElementNode(e))
                Next
            End If
            tvElements.Nodes.Add(commandRoot)

            Dim pageRoot As New TreeNode("Page 模板") With {.Tag = "page-root"}
            For Each p In _template.PageTemplates
                pageRoot.Nodes.Add(BuildElementNode(p))
            Next
            tvElements.Nodes.Add(pageRoot)
            commandRoot.Expand()
            pageRoot.Expand()
        End If
        tvElements.EndUpdate()
        SetupElementMenu()
        If selectedId <> "" Then
            Dim restored = FindElementNode(tvElements.Nodes, selectedId)
            If restored IsNot Nothing Then
                tvElements.SelectedNode = restored
                pg.SelectedObject = If(chkEdit.Checked, restored.Tag, Nothing)
                Return
            End If
        End If
        pg.SelectedObject = Nothing
    End Sub

    Private Function FindElementNode(nodes As TreeNodeCollection, id As String) As TreeNode
        For Each n As TreeNode In nodes
            Dim d = TryCast(n.Tag, ScsiElementDef)
            If d IsNot Nothing AndAlso String.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase) Then Return n
            Dim child = FindElementNode(n.Nodes, id)
            If child IsNot Nothing Then Return child
        Next
        Return Nothing
    End Function

    Private Function BuildElementNode(e As ScsiElementDef) As TreeNode
        Dim n As New TreeNode(e.Name & " [" & e.Kind & "]") With {.Tag = e}
        For Each c In e.Children
            n.Nodes.Add(BuildElementNode(c))
        Next
        Return n
    End Function

    Private Sub SetupElementMenu()
        If _elementMenu IsNot Nothing Then _elementMenu.Dispose()
        _elementMenu = New ContextMenuStrip()
        AddMenuItem("新增子元素", AddressOf AddChildElement)
        AddMenuItem("新增同级元素", AddressOf AddSiblingElement)
        AddMenuItem("新增多个同级元素", AddressOf AddMultipleSiblingElements)
        AddMenuItem("删除元素", AddressOf DeleteElement)
        _elementMenu.Items.Add(New ToolStripSeparator())
        AddMenuItem("上移", Sub(s, e) MoveElement(-1))
        AddMenuItem("下移", Sub(s, e) MoveElement(1))
        _elementMenu.Items.Add(New ToolStripSeparator())
        AddMenuItem("新增 Page 模板", AddressOf AddPageTemplate)
        tvElements.ContextMenuStrip = _elementMenu
    End Sub

    Private Sub AddMenuItem(caption As String, handler As EventHandler)
        Dim item As New ToolStripMenuItem(caption)
        AddHandler item.Click, handler
        _elementMenu.Items.Add(item)
    End Sub

    Private Sub ElementSelected(sender As Object, e As TreeViewEventArgs) Handles tvElements.AfterSelect
        If Not chkEdit.Checked Then
            pg.SelectedObject = Nothing
            Return
        End If

        ' 选择“当前指令元素”根节点时，直接编辑指令模板本身。
        If String.Equals(Convert.ToString(e.Node.Tag), "command-root", StringComparison.Ordinal) Then
            pg.SelectedObject = _selectedCommand
            Return
        End If

        Dim def = TryCast(e.Node.Tag, ScsiElementDef)
        pg.SelectedObject = def
    End Sub

    Private Sub DefinitionPropertyChanged(sender As Object, e As PropertyValueChangedEventArgs) Handles pg.PropertyValueChanged
        If tvElements.SelectedNode IsNot Nothing AndAlso TypeOf tvElements.SelectedNode.Tag Is ScsiElementDef Then
            Dim el = DirectCast(tvElements.SelectedNode.Tag, ScsiElementDef)
            ' 仅当 Name 属性发生变化时，更新当前树节点的显示名称
            If e.ChangedItem IsNot Nothing AndAlso (String.Equals(e.ChangedItem.PropertyDescriptor.Name, NameOf(ScsiElementDef.Name), StringComparison.Ordinal) OrElse String.Equals(e.ChangedItem.PropertyDescriptor.Name, NameOf(ScsiElementDef.Kind), StringComparison.Ordinal)) Then
                tvElements.SelectedNode.Text = el.Name & " [" & el.Kind & "]"
            End If
            If _runtimeNodes IsNot Nothing Then
                _runtimeNodes = ScsiCodec.CreateRuntimeNodes(_selectedCommand, _template)
                RenderValueEditors()
            End If
            Return
        End If

        If tvElements.SelectedNode IsNot Nothing AndAlso
           String.Equals(Convert.ToString(tvElements.SelectedNode.Tag), "command-root", StringComparison.Ordinal) AndAlso
           _selectedCommand IsNot Nothing Then
            ' 先更新当前根节点文本。更新 ListBox 项目可能触发 SelectedIndexChanged，
            ' CommandChanged 会重建元素树并清空原 SelectedNode，因此不能在其后继续解引用 SelectedNode。
            Dim rootNode As TreeNode = tvElements.SelectedNode
            If rootNode IsNot Nothing Then
                rootNode.Text = "当前指令元素 - " & _selectedCommand.Name & " (0x" & _selectedCommand.Opcode.ToString("X2") & ")"
            End If

            ' 同步左侧指令列表中显示的名称和操作码。
            Dim index As Integer = If(_template Is Nothing, -1, _template.Commands.IndexOf(_selectedCommand))
            If index >= 0 AndAlso index < lstCommands.Items.Count Then
                lstCommands.Items(index) = New CommandListItem(_selectedCommand)
                If lstCommands.SelectedIndex <> index Then lstCommands.SelectedIndex = index
            End If
            If _runtimeNodes IsNot Nothing Then
                _runtimeNodes = ScsiCodec.CreateRuntimeNodes(_selectedCommand, _template)
                RenderValueEditors()
            End If
        End If
    End Sub


    Private Function SelectedDefinition() As ScsiElementDef
        If tvElements.SelectedNode Is Nothing Then Return Nothing
        Return TryCast(tvElements.SelectedNode.Tag, ScsiElementDef)
    End Function

    Private Function SelectedCollection(ByVal node As TreeNode, ByRef collection As List(Of ScsiElementDef), ByRef selectedIndex As Integer) As Boolean
        collection = Nothing
        selectedIndex = -1
        If node Is Nothing Then Return False
        Dim def = TryCast(node.Tag, ScsiElementDef)
        If def IsNot Nothing Then
            If node.Parent Is Nothing Then Return False
            Dim parentDef = TryCast(node.Parent.Tag, ScsiElementDef)
            If parentDef IsNot Nothing Then
                collection = parentDef.Children
            ElseIf Convert.ToString(node.Parent.Tag) = "command-root" AndAlso _selectedCommand IsNot Nothing Then
                collection = _selectedCommand.Elements
            ElseIf Convert.ToString(node.Parent.Tag) = "page-root" AndAlso _template IsNot Nothing Then
                collection = _template.PageTemplates
            End If
            If collection IsNot Nothing Then selectedIndex = collection.IndexOf(def)
            Return collection IsNot Nothing AndAlso selectedIndex >= 0
        End If
        Return False
    End Function

    Private Sub AddChildElement(sender As Object, e As EventArgs)
        If Not chkEdit.Checked OrElse tvElements.SelectedNode Is Nothing Then Return

        ' 指令根节点不是 ScsiElementDef，需直接向当前指令的 Elements 集合添加字段。
        If Convert.ToString(tvElements.SelectedNode.Tag) = "command-root" Then
            If _selectedCommand Is Nothing Then Return

            Dim child As New ScsiElementDef With {
                .Name = "NewField",
                .Kind = "Field",
                .Area = ScsiArea.CDB,
                .FieldType = ScsiFieldType.IntegerValue,
                .BitOffset = 0,
                .BitLength = 8,
                .DefaultValue = "0"
            }
            _selectedCommand.Elements.Add(child)
            RefreshDefinitionTree()
            Return
        End If

        Dim parentDef = SelectedDefinition()
        If parentDef Is Nothing Then
            If Convert.ToString(tvElements.SelectedNode.Tag) = "page-root" Then
                AddPageTemplate(sender, e)
            Else
                Return
            End If
        Else
            If parentDef.Kind <> "Group" AndAlso parentDef.Kind <> "Page" AndAlso parentDef.Kind <> "Category" Then
                MessageBox.Show("只有 Group/Page/Category 可以直接包含固定子元素；PageList 通过模板列表添加 Page。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information)
                Return
            End If
            Dim child As New ScsiElementDef With {.Name = "NewField", .Kind = "Field", .Area = parentDef.Area, .FieldType = ScsiFieldType.IntegerValue, .BitOffset = 0, .BitLength = 8, .DefaultValue = "0"}
            parentDef.Children.Add(child)
        End If
        RefreshDefinitionTree()
    End Sub


    Private Sub AddSiblingElement(sender As Object, e As EventArgs)
        If Not chkEdit.Checked Then Return
        Dim col As List(Of ScsiElementDef) = Nothing
        Dim idx As Integer
        If Not SelectedCollection(tvElements.SelectedNode, col, idx) Then Return
        col.Insert(idx + 1, New ScsiElementDef With {.Name = "NewField", .Kind = "Field", .Area = col(idx).Area, .FieldType = ScsiFieldType.IntegerValue, .BitOffset = 0, .BitLength = 8, .DefaultValue = "0"})
        RefreshDefinitionTree()
    End Sub

    Private Sub AddMultipleSiblingElements(sender As Object, e As EventArgs)
        If Not chkEdit.Checked Then Return

        Dim col As List(Of ScsiElementDef) = Nothing
        Dim idx As Integer
        If Not SelectedCollection(tvElements.SelectedNode, col, idx) Then Return

        ' 使用输入对话框获取新增数量；取消或输入无效内容时不修改模板。
        Dim countText As String = ""
        If DisplayHelper.ShowInputDialog("请输入要新增的同级元素数量：", "新增多个同级元素", countText) <> DialogResult.OK Then Return

        Dim count As Integer
        If Not Integer.TryParse(countText, count) OrElse count <= 0 Then Return
        If count > 1000 Then
            MessageBox.Show("一次最多新增 1000 个元素。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information)
            Return
        End If

        Dim source As ScsiElementDef = col(idx)
        For i As Integer = 1 To count
            col.Insert(idx + i, New ScsiElementDef With {
                .Name = "NewField",
                .Kind = "Field",
                .Area = source.Area,
                .FieldType = ScsiFieldType.IntegerValue,
                .BitOffset = 0,
                .BitLength = 8,
                .DefaultValue = "0"
            })
        Next

        RefreshDefinitionTree()
    End Sub

    Private Sub DeleteElement(sender As Object, e As EventArgs)
        If Not chkEdit.Checked Then Return
        Dim col As List(Of ScsiElementDef) = Nothing
        Dim idx As Integer
        If Not SelectedCollection(tvElements.SelectedNode, col, idx) Then Return
        If MessageBox.Show("确定删除元素““" & col(idx).Name & "””？", "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question) <> DialogResult.Yes Then Return
        col.RemoveAt(idx)
        RefreshDefinitionTree()
    End Sub

    Private Sub MoveElement(delta As Integer)
        If Not chkEdit.Checked Then Return
        Dim col As List(Of ScsiElementDef) = Nothing
        Dim idx As Integer
        If Not SelectedCollection(tvElements.SelectedNode, col, idx) Then Return
        Dim dest = idx + delta
        If dest < 0 OrElse dest >= col.Count Then Return
        Dim tmp = col(idx)
        col(idx) = col(dest)
        col(dest) = tmp
        RefreshDefinitionTree()
    End Sub

    Private Sub AddPageTemplate(sender As Object, e As EventArgs)
        If Not chkEdit.Checked OrElse _template Is Nothing Then Return
        Dim p As New ScsiElementDef With {
            .Name = "NewPage", .Kind = "Page", .Area = ScsiArea.Param,
            .FieldType = ScsiFieldType.Binary, .BitOffset = 0, .BitLength = 0, .ByteLength = 2
        }
        _template.PageTemplates.Add(p)
        RefreshDefinitionTree()
    End Sub

    Private Sub EditModeChanged(sender As Object, e As EventArgs) Handles chkEdit.CheckedChanged
        UpdateEditUi()
        If Not chkEdit.Checked Then pg.SelectedObject = Nothing
        If chkEdit.Checked Then tabs.SelectedTab = tabDefinition Else tabs.SelectedTab = tabValues
        RefreshDefinitionTree()
        btnGUIDInc.Visible = chkEdit.Checked
    End Sub

    Private Sub UpdateEditUi()
        Dim edit = chkEdit.Checked
        btnSave.Enabled = edit
        btnAdd.Enabled = edit
        btnDelete.Enabled = edit
        btnUp.Enabled = edit
        btnDown.Enabled = edit
        tabDefinition.Enabled = edit
    End Sub

    Private Sub AddCommandButtonClick(sender As Object, e As EventArgs) Handles btnAdd.Click
        If _template Is Nothing Then Return
        Dim c As New ScsiCommandDef With {.Name = "New SCSI Command", .Opcode = 0, .CdbLength = 6}
        _template.Commands.Add(c)
        RefreshCommands(_template.Commands.Count - 1)
    End Sub

    Private Sub DeleteCommandButtonClick(sender As Object, e As EventArgs) Handles btnDelete.Click
        If _template Is Nothing OrElse _selectedCommand Is Nothing Then Return
        If MessageBox.Show("确定删除指令“" & _selectedCommand.Name & "”？", "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question) <> DialogResult.Yes Then Return
        Dim index = _template.Commands.IndexOf(_selectedCommand)
        _template.Commands.Remove(_selectedCommand)
        _selectedCommand = Nothing
        RefreshCommands(Math.Max(0, index - 1))
    End Sub

    Private Sub MoveCommandUpButtonClick(sender As Object, e As EventArgs) Handles btnUp.Click
        MoveCommand(-1)
    End Sub
    Private Sub MoveCommandDownButtonClick(sender As Object, e As EventArgs) Handles btnDown.Click
        MoveCommand(1)
    End Sub

    Private Sub MoveCommand(delta As Integer)
        If _template Is Nothing OrElse _selectedCommand Is Nothing Then Return
        Dim index = _template.Commands.IndexOf(_selectedCommand)
        Dim target = index + delta
        If target < 0 OrElse target >= _template.Commands.Count Then Return
        Dim tmp = _template.Commands(index)
        _template.Commands(index) = _template.Commands(target)
        _template.Commands(target) = tmp
        RefreshCommands(target)
    End Sub

    Private Sub SaveTemplate(sender As Object, e As EventArgs) Handles btnSave.Click
        If _template Is Nothing OrElse String.IsNullOrEmpty(_templatePath) Then Return
        Try
            XmlTemplateStore.SaveTemplate(_templatePath, _template)
            MessageBox.Show("模板已保存：" & _templatePath, "保存成功", MessageBoxButtons.OK, MessageBoxIcon.Information)
            RefreshCommands(If(_selectedCommand Is Nothing, 0, _template.Commands.IndexOf(_selectedCommand)))
        Catch ex As Exception
            MessageBox.Show("保存失败：" & ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error)
        End Try
    End Sub

    Private Sub SCSIGUI_Load(sender As Object, e As EventArgs) Handles Me.Load
        UpdateEditUi()
        LoadTemplateList()
    End Sub

    Private Sub btnCopy_Click(sender As Object, e As EventArgs) Handles btnCopy.Click
        Dim sb As New System.Text.StringBuilder()
        sb.AppendLine("[SCSIGUI]")
        sb.AppendLine("CDB:")
        sb.AppendLine(txtCdbOutput.Text)
        sb.AppendLine("PARAM:")
        sb.AppendLine(txtParamOutput.Text)
        sb.AppendLine("DataIn:")
        sb.AppendLine(CInt(_selectedCommand.DataIn).ToString())
        sb.AppendLine("[/SCSIGUI]")
        Clipboard.SetText(sb.ToString())
    End Sub

    Private Sub btnPaste_Click(sender As Object, e As EventArgs) Handles btnPaste.Click
        Try
            Dim content As String = Clipboard.GetText()
            If String.IsNullOrWhiteSpace(content) Then
                Throw New FormatException("剪贴板为空。")
            End If
            Dim lines As String() = content.Split(New String() {vbCrLf, vbLf, vbCr}, StringSplitOptions.RemoveEmptyEntries)
            Dim values As New Dictionary(Of String, System.Text.StringBuilder)(StringComparer.OrdinalIgnoreCase) From {{"CDB", New System.Text.StringBuilder()}, {"PARAM", New System.Text.StringBuilder()}}
            Dim currentKey As String = Nothing
            Dim hasStart As Boolean = False
            Dim hasEnd As Boolean = False

            For Each rawLine As String In lines
                Dim line As String = rawLine.Trim()
                If line = "[SCSIGUI]" Then
                    hasStart = True
                    Continue For
                End If
                If line = "[/SCSIGUI]" Then
                    hasEnd = True
                    Exit For
                End If
                If Not hasStart Then Continue For
                Select Case line.ToUpperInvariant()
                    Case "CDB:"
                        currentKey = "CDB"
                        Continue For
                    Case "PARAM:"
                        currentKey = "PARAM"
                        Continue For
                    Case "DATAIN:"
                        currentKey = Nothing
                        Continue For
                End Select
                If currentKey IsNot Nothing Then
                    If values(currentKey).Length > 0 Then
                        values(currentKey).AppendLine()
                    End If
                    values(currentKey).Append(line)
                End If
            Next
            If Not hasStart OrElse Not hasEnd Then
                Throw New FormatException("剪贴板内容不是有效的 SCSIGUI 数据。")
            End If
            If Not values.ContainsKey("CDB") OrElse values("CDB").Length = 0 Then
                Throw New FormatException("剪贴板中 CDB 不能为空。")
            End If
            If Not values.ContainsKey("PARAM") Then
                Throw New FormatException("剪贴板中缺少 PARAM 字段。")
            End If
            txtCdbInput.Text = values("CDB").ToString()
            txtParamInput.Text = values("PARAM").ToString()
        Catch ex As Exception
            MessageBox.Show(ex.ToString())
        End Try
    End Sub

    Private Sub Button1_Click(sender As Object, e As EventArgs) Handles btnGUIDInc.Click
        If Clipboard.ContainsText Then
            Clipboard.SetText(IOManager.IncrementGuidsInText(Clipboard.GetText()))
            MessageBox.Show("OK")
        End If
    End Sub
End Class

Public Class PageTemplateOption
    Public ReadOnly Property Page As ScsiElementDef
    Public Sub New(page As ScsiElementDef)
        Me.Page = page
    End Sub
    Public Overrides Function ToString() As String
        Return Page.Name
    End Function
End Class
