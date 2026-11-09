Option Strict On
Option Explicit On

Imports System.Globalization
Imports System.Text
Imports System.Text.RegularExpressions

Public Class ScsiEncodedCommand
    Public Property CDB As Byte()
    Public Property Param As Byte()
End Class

Public NotInheritable Class ScsiCodec
    Private Sub New()
    End Sub

    Public Shared Function CreateRuntimeNodes(command As ScsiCommandDef, template As ScsiTemplate) As List(Of ScsiValueNode)
        Dim result As New List(Of ScsiValueNode)
        For Each def In command.Elements
            result.Add(CreateNode(def, template, Nothing))
        Next

        ' UI 初次渲染时也需要跨顶层节点解析 PresenceFormula / OffsetFormula。
        ' 例如 LOG SELECT 的 Param PageList 引用 CDB 中的 PC 字段；
        ' 这些节点是同一命令的兄弟根节点，彼此没有 Parent 关系。
        _currentTemplate = template
        _currentNodes = result

        Return result
    End Function

    Public Shared Function CreateNode(def As ScsiElementDef, template As ScsiTemplate, parent As ScsiValueNode) As ScsiValueNode
        Dim n As New ScsiValueNode With {.Definition = def, .Parent = parent, .Value = def.DefaultValue}
        If def.Kind = "PageList" Then
            Dim allowed = GetAllowedPageTemplates(def, template)
            For i As Integer = 0 To Math.Max(0, def.MinItems) - 1
                If allowed.Count = 0 Then Exit For
                Dim item = CreateNode(allowed(0), template, n)
                item.InstanceName = allowed(0).Name & " [" & (i + 1).ToString() & "]"
                n.Items.Add(item)
            Next
        Else
            For Each childDef In def.Children
                n.Children.Add(CreateNode(childDef, template, n))
            Next
        End If
        Return n
    End Function

    ''' <summary>
    ''' 递归收集所有真正的 Page 模板。
    ''' Category 仅用于分组，不作为可解码的 Page 模板。
    ''' </summary>
    Public Shared Function GetAllPageTemplates(template As ScsiTemplate) As List(Of ScsiElementDef)
        Dim result As New List(Of ScsiElementDef)
        If template Is Nothing OrElse template.PageTemplates Is Nothing Then
            Return result
        End If
        For Each item As ScsiElementDef In template.PageTemplates
            CollectPageTemplates(item, result)
        Next
        Return result
    End Function

    Private Shared Sub CollectPageTemplates(element As ScsiElementDef, result As List(Of ScsiElementDef))
        If element Is Nothing Then Return
        If String.Equals(element.Kind, "Category", StringComparison.OrdinalIgnoreCase) Then
            ' 分类节点不加入结果，只递归收集其子节点。
            If element.Children IsNot Nothing Then
                For Each child As ScsiElementDef In element.Children
                    CollectPageTemplates(child, result)
                Next
            End If
        Else
            ' 非 Category 节点视为实际 Page 模板。
            result.Add(element)
        End If
    End Sub

    Public Shared Function GetAllowedPageTemplates(listDef As ScsiElementDef, template As ScsiTemplate) As List(Of ScsiElementDef)
        Dim allPages As List(Of ScsiElementDef) = GetAllPageTemplates(template)
        If listDef Is Nothing Then Return allPages
        ' AllowedPageTemplateIds 非空时优先使用，否则使用 PageTemplateId。
        Dim idText As String = If(String.IsNullOrWhiteSpace(listDef.AllowedPageTemplateIds), listDef.PageTemplateId, listDef.AllowedPageTemplateIds)
        Dim selected As List(Of String) = Regex.Split(If(idText, ""), "[;,]").Select(Function(s) s.Trim()).Where(Function(s) s <> "").ToList()
        ' 两个属性都没有设置时，允许所有 Page 模板。
        If selected.Count = 0 Then Return allPages
        ' 收集最终允许的 Page ID。
        Dim allowedIds As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        For Each id In selected
            ' 1. 如果填写的是普通 Page 的 ID 或名称，直接允许该 Page。
            For Each page In allPages
                If String.Equals(id, page.Id, StringComparison.OrdinalIgnoreCase) OrElse String.Equals(id, page.Name, StringComparison.OrdinalIgnoreCase) Then
                    allowedIds.Add(page.Id)
                End If
            Next
            ' 2. 如果填写的是 Category 的 ID 或名称，
            '    则递归允许该 Category 下的所有 Page。
            For Each category In GetAllCategories(template)
                If String.Equals(id, category.Id, StringComparison.OrdinalIgnoreCase) OrElse String.Equals(id, category.Name, StringComparison.OrdinalIgnoreCase) Then
                    CollectCategoryPageIds(category, allowedIds)
                End If
            Next
        Next
        ' 按模板原有顺序返回，并通过 ID 去重。
        Return allPages.Where(Function(p) allowedIds.Contains(p.Id)).ToList()
    End Function

    ''' <summary>
    ''' 递归收集所有 Category 节点，包括多层嵌套的 Category。
    ''' </summary>
    Private Shared Function GetAllCategories(template As ScsiTemplate) As List(Of ScsiElementDef)
        Dim result As New List(Of ScsiElementDef)
        If template Is Nothing OrElse template.PageTemplates Is Nothing Then
            Return result
        End If
        For Each item As ScsiElementDef In template.PageTemplates
            CollectCategories(item, result)
        Next
        Return result
    End Function
    Private Shared Sub CollectCategories(element As ScsiElementDef, result As List(Of ScsiElementDef))
        If element Is Nothing Then Return
        If String.Equals(element.Kind, "Category", StringComparison.OrdinalIgnoreCase) Then
            result.Add(element)
        End If

        If element.Children IsNot Nothing Then
            For Each child As ScsiElementDef In element.Children
                CollectCategories(child, result)
            Next
        End If
    End Sub

    ''' <summary>
    ''' 收集指定 Category 下的全部实际 Page ID。
    ''' 支持任意层级的 Category 嵌套。
    ''' </summary>
    Private Shared Sub CollectCategoryPageIds(category As ScsiElementDef, allowedIds As HashSet(Of String))
        If category Is Nothing OrElse category.Children Is Nothing Then Return
        For Each child As ScsiElementDef In category.Children
            If child Is Nothing Then Continue For
            If String.Equals(child.Kind, "Category", StringComparison.OrdinalIgnoreCase) Then
                ' 子分类：继续递归。
                CollectCategoryPageIds(child, allowedIds)
            Else
                ' 非 Category 节点视为实际 Page 模板。
                If Not String.IsNullOrWhiteSpace(child.Id) Then
                    allowedIds.Add(child.Id)
                End If
            End If
        Next
    End Sub

    Public Shared Function Encode(command As ScsiCommandDef, template As ScsiTemplate, nodes As List(Of ScsiValueNode)) As ScsiEncodedCommand
        If command.CdbLength < 1 OrElse command.CdbLength > 260 Then Throw New InvalidOperationException("CDB 长度必须在 1–260 字节范围内。")
        _currentNodes = nodes
        ValidatePageLists(nodes, template)
        RecalculateFormulas(nodes)
        Dim cdb(command.CdbLength - 1) As Byte
        Dim paramLength = RequiredAreaBytes(nodes, ScsiArea.Param)
        Dim param As Byte()
        If paramLength > 0 Then
            ReDim param(paramLength - 1)
        Else
            param = New Byte() {}
        End If

        For Each node In nodes
            If node.Definition.Area = ScsiArea.CDB Then
                WriteNode(node, cdb, 0, True)
            Else
                WriteNode(node, param, 0, True)
            End If
        Next
        Return New ScsiEncodedCommand With {.CDB = cdb, .Param = param}
    End Function

    Public Shared Sub Decode(command As ScsiCommandDef, template As ScsiTemplate, nodes As List(Of ScsiValueNode), cdb As Byte(), param As Byte())
        If cdb Is Nothing OrElse cdb.Length < command.CdbLength Then Throw New ArgumentException("CDB 数据长度不足。", NameOf(cdb))
        _currentTemplate = template
        _currentNodes = nodes
        Try
            For Each node In nodes
                If node.Definition.Area = ScsiArea.CDB Then
                    DecodeNode(node, cdb, 0, True, cdb.Length)
                Else
                    DecodeNode(node, If(param, New Byte() {}), 0, True, If(param Is Nothing, 0, param.Length))
                End If
            Next
            RecalculateFormulas(nodes)
        Finally
            _currentTemplate = Nothing
        End Try
    End Sub


    Private Shared Sub ValidatePageLists(nodes As IEnumerable(Of ScsiValueNode), template As ScsiTemplate)
        For Each node In Flatten(nodes)
            If node.Definition Is Nothing OrElse Not node.Definition.Kind.Equals("PageList", StringComparison.OrdinalIgnoreCase) Then Continue For
            Dim d = node.Definition
            If d.MinItems > 0 AndAlso node.Items.Count < d.MinItems Then
                Throw New InvalidOperationException(String.Format("{0} 至少需要 {1} 个 Sub Page，当前为 {2} 个。", d.Name, d.MinItems, node.Items.Count))
            End If
            If d.MaxItems > 0 AndAlso node.Items.Count > d.MaxItems Then
                Throw New InvalidOperationException(String.Format("{0} 最多允许 {1} 个 Sub Page，当前为 {2} 个。", d.Name, d.MaxItems, node.Items.Count))
            End If
            Dim allowed = GetAllowedPageTemplates(d, template)
            For Each item In node.Items
                If item.Definition Is Nothing OrElse Not allowed.Any(Function(p) String.Equals(p.Id, item.Definition.Id, StringComparison.OrdinalIgnoreCase)) Then
                    Throw New InvalidOperationException("PageList " & d.Name & " 中存在不允许的 Sub Page 模板。")
                End If
            Next
        Next
    End Sub

    Public Shared Sub RecalculateFormulas(nodes As List(Of ScsiValueNode))
        Dim all = Flatten(nodes).ToList()
        For Each node In all
            Dim d = node.Definition
            If d Is Nothing OrElse String.IsNullOrWhiteSpace(d.Formula) Then Continue For
            Dim result = BitCodec.EvaluateLengthFormula(d.Formula,
                Function(id)
                    Dim target = all.FirstOrDefault(Function(x) x.Definition IsNot Nothing AndAlso String.Equals(x.Definition.Id, id, StringComparison.OrdinalIgnoreCase))
                    If target Is Nothing Then
                        Throw New InvalidOperationException("公式引用的元素不存在：" & id)
                    End If
                    Return CLng((ElementExtentBits(target) + 7) \ 8)
                End Function,
                Function(id)
                    Dim target = all.FirstOrDefault(Function(x) x.Definition IsNot Nothing AndAlso String.Equals(x.Definition.Id, id, StringComparison.OrdinalIgnoreCase))
                    If target Is Nothing Then
                        Throw New InvalidOperationException("公式引用的元素不存在：" & id)
                    End If
                    Return ParseInteger(target.Value)
                End Function)
            node.Value = result
        Next
    End Sub

    Public Shared Function RequiredAreaBytes(nodes As List(Of ScsiValueNode), area As ScsiArea) As Integer
        Dim endBit As Long = 0
        For Each n In nodes
            If n.Definition.Area <> area Then Continue For
            endBit = Math.Max(endBit, EffectiveOffsetBits(n) + ElementExtentBits(n))
        Next
        If endBit > Integer.MaxValue * 8L Then Throw New OverflowException("Param 长度过大。")
        Return CInt((endBit + 7) \ 8)
    End Function

    Private Shared _currentNodes As List(Of ScsiValueNode)

    ' 按作用域查找公式引用：优先当前 Page 实例，再查当前节点子树，最后查完整解析树。
    ' DecodePageList 试探候选 Page 时，候选节点尚未加入根节点，因此不能只查 _currentNodes。
    Private Shared Function FindFormulaReference(node As ScsiValueNode, id As String) As ScsiValueNode
        If node Is Nothing OrElse String.IsNullOrWhiteSpace(id) Then Return Nothing

        ' 优先使用最近的 Page 实例，避免重复 Page 中相同 GUID 相互串用。
        Dim pageScope As ScsiValueNode = node
        While pageScope IsNot Nothing
            If pageScope.Definition IsNot Nothing AndAlso
               String.Equals(pageScope.Definition.Kind, "Page", StringComparison.OrdinalIgnoreCase) Then
                Exit While
            End If
            pageScope = pageScope.Parent
        End While

        If pageScope IsNot Nothing Then
            Dim target = Flatten(New List(Of ScsiValueNode) From {pageScope}).FirstOrDefault(
                Function(x) x.Definition IsNot Nothing AndAlso
                    String.Equals(x.Definition.Id, id, StringComparison.OrdinalIgnoreCase))
            If target IsNot Nothing Then Return target
        End If

        ' 兼容尚未归属于 Page 的临时节点，以及当前节点子树内的引用。
        Dim localTarget = Flatten(New List(Of ScsiValueNode) From {node}).FirstOrDefault(
            Function(x) x.Definition IsNot Nothing AndAlso
                String.Equals(x.Definition.Id, id, StringComparison.OrdinalIgnoreCase))
        If localTarget IsNot Nothing Then Return localTarget

        ' 最后回退到常规解析树。
        Dim allNodes = If(_currentNodes, New List(Of ScsiValueNode)())
        Return Flatten(allNodes).FirstOrDefault(
            Function(x) x.Definition IsNot Nothing AndAlso
                String.Equals(x.Definition.Id, id, StringComparison.OrdinalIgnoreCase))
    End Function

    ' 返回元素相对父节点的有效偏移（固定 BitOffset + 动态 OffsetFormula）。
    Private Shared Function EffectiveOffsetBits(node As ScsiValueNode) As Long
        Dim d = node.Definition
        If d Is Nothing Then Return 0
        Dim result As Long = d.BitOffset
        If Not String.IsNullOrWhiteSpace(d.OffsetFormula) Then
            result += BitCodec.EvaluateOffsetFormula(d.OffsetFormula,
                Function(id)
                    Dim target = FindFormulaReference(node, id)
                    If target Is Nothing Then
                        Throw New InvalidOperationException("动态偏移公式引用的元素不存在：" & id)
                    End If

                    Return ParseInteger(target.Value)
                End Function,
                Function(id)
                    Dim target = FindFormulaReference(node, id)

                    If target Is Nothing Then
                        Throw New InvalidOperationException("动态偏移公式引用的元素不存在：" & id)
                    End If

                    Return ElementExtentBits(target)
                End Function)
        End If
        If result < 0 OrElse result > Integer.MaxValue Then Throw New InvalidOperationException("元素动态偏移无效：" & d.Name)
        Return result
    End Function

    Public Shared Function IsNodePresent(node As ScsiValueNode) As Boolean
        Dim d = node.Definition
        If d Is Nothing OrElse String.IsNullOrWhiteSpace(d.PresenceFormula) Then Return True
        Return BitCodec.EvaluatePresenceFormula(d.PresenceFormula,
            Function(id)
                Dim target = FindFormulaReference(node, id)
                If target Is Nothing Then
                    Throw New InvalidOperationException("存在条件公式引用的元素不存在：" & id)
                End If
                Return ParseInteger(target.Value)
            End Function,
            Function(id)
                Dim target = FindFormulaReference(node, id)
                If target Is Nothing Then
                    Throw New InvalidOperationException("存在条件公式引用的元素不存在：" & id)
                End If
                Return ElementExtentBits(target)
            End Function) <> 0
    End Function

    Public Shared Function ElementExtentBits(node As ScsiValueNode) As Long
        Dim d = node.Definition
        If d Is Nothing OrElse Not IsNodePresent(node) Then Return 0
        Select Case d.Kind.ToUpperInvariant()
            Case "FIELD"
                If d.FieldType = ScsiFieldType.ASCII OrElse d.FieldType = ScsiFieldType.Binary Then
                    Return CLng(Math.Max(0, GetFieldByteLength(node))) * 8L
                End If
                Return Math.Max(0, d.BitLength)
            Case "PAGELIST"
                Dim configuredLength = GetConfiguredByteLength(node)
                If configuredLength > 0 Then Return CLng(configuredLength) * 8L
                Dim total As Long = 0
                For Each item In node.Items
                    total += PageExtentBits(item)
                Next
                Return total
            Case "GROUP", "PAGE"
                Dim configuredLength = GetConfiguredByteLength(node)
                If configuredLength > 0 Then Return CLng(configuredLength) * 8L
                Dim endBit As Long = 0
                For Each child In node.Children
                    endBit = Math.Max(endBit, EffectiveOffsetBits(child) + ElementExtentBits(child))
                Next
                Return endBit
            Case Else
                Return 0
        End Select
    End Function

    Private Shared Function PageExtentBits(pageNode As ScsiValueNode) As Long
        Return ElementExtentBits(pageNode)
    End Function

    Private Shared Sub WriteNode(node As ScsiValueNode, target As Byte(), baseBit As Integer, root As Boolean)
        Dim d = node.Definition
        If d Is Nothing OrElse Not IsNodePresent(node) Then Return
        Dim startBit As Long = If(root, 0L, CLng(baseBit)) + EffectiveOffsetBits(node)
        If startBit < 0 OrElse startBit > Integer.MaxValue Then Throw New InvalidOperationException("元素偏移无效：" & d.Name)
        Select Case d.Kind.ToUpperInvariant()
            Case "FIELD"
                WriteField(node, target, CInt(startBit))
            Case "GROUP", "PAGE"
                For Each child In node.Children
                    WriteNode(child, target, CInt(startBit), False)
                Next
            Case "PAGELIST"
                Dim cursor As Long = startBit
                For Each item In node.Items
                    If (cursor Mod 8) <> 0 Then Throw New InvalidOperationException("PageList 必须按字节对齐：" & d.Name)
                    WriteNode(item, target, CInt(cursor), False)
                    cursor += PageExtentBits(item)
                Next
        End Select
    End Sub

    Private Shared Sub WriteField(node As ScsiValueNode, target As Byte(), bitOffset As Integer)
        Dim d = node.Definition
        If d.Formula <> "" Then
            ' Value was recalculated immediately before encoding.
        End If
        If d.FieldType = ScsiFieldType.ASCII Then
            Dim count = GetFieldByteLength(node)
            EnsureRange(target, bitOffset, count * 8, d.Name)
            Dim src = Encoding.ASCII.GetBytes(Convert.ToString(If(node.Value, ""), CultureInfo.InvariantCulture))
            For i As Integer = 0 To Math.Min(count, src.Length) - 1
                target(bitOffset \ 8 + i) = src(i)
            Next
            Return
        ElseIf d.FieldType = ScsiFieldType.Binary Then
            Dim count = GetFieldByteLength(node)
            EnsureRange(target, bitOffset, count * 8, d.Name)
            Dim src = ParseHexBytes(Convert.ToString(If(node.Value, ""), CultureInfo.InvariantCulture))
            If src.Length > count Then Throw New InvalidOperationException(d.Name & " 的 Binary 数据超过指定长度 " & count & " 字节。")
            Array.Copy(src, 0, target, bitOffset \ 8, src.Length)
            Return
        End If

        Dim width = d.BitLength
        Dim u As ULong
        If d.IsSigned Then
            Dim signedValue As Long = ParseInteger(node.Value)
            If width < 64 Then
                Dim minValue As Long = -(1L << (width - 1))
                Dim maxValue As Long = (1L << (width - 1)) - 1L
                If signedValue < minValue OrElse signedValue > maxValue Then Throw New InvalidOperationException(d.Name & " 超出有符号 " & width & " 位范围。")
                u = BitConverter.ToUInt64(BitConverter.GetBytes(signedValue), 0) And ((1UL << width) - 1UL)
            Else
                u = BitConverter.ToUInt64(BitConverter.GetBytes(signedValue), 0)
            End If
        Else
            u = ParseUnsigned(node.Value)
            If width < 64 AndAlso u >= (1UL << width) Then Throw New InvalidOperationException(d.Name & " 超出 " & width & " 位范围。")
        End If
        BitCodec.WriteUnsigned(target, bitOffset, width, u)
    End Sub

    Private Shared Sub DecodeNode(node As ScsiValueNode, source As Byte(), baseBit As Integer, root As Boolean, limitBytes As Integer)
        Dim d = node.Definition
        If d Is Nothing Then Return
        If Not IsNodePresent(node) Then
            ' 不生效的 PageList 必须清除上一次解码残留的页面。
            If String.Equals(d.Kind, "PageList", StringComparison.OrdinalIgnoreCase) Then
                node.Items.Clear()
            End If
            Return
        End If
        Dim startBitLong As Long = If(root, 0L, CLng(baseBit)) + EffectiveOffsetBits(node)
        If startBitLong < 0 OrElse startBitLong > Integer.MaxValue Then Return
        Dim startBit = CInt(startBitLong)
        Select Case d.Kind.ToUpperInvariant()
            Case "FIELD"
                DecodeField(node, source, startBit, limitBytes)
            Case "GROUP", "PAGE"
                For Each child In node.Children
                    DecodeNode(child, source, startBit, False, limitBytes)
                Next
            Case "PAGELIST"
                DecodePageList(node, source, startBit, limitBytes)
        End Select
    End Sub
    ' 获取 Field 的实际字节长度。
    ' LengthFieldId 非空时：
    ' 1. 如果当前字段属于 Page，优先在当前 Page 实例内查找；
    ' 2. 如果当前字段不属于 Page，则允许引用当前指令的其他顶层节点，
    '    例如 Param 中的 Read Data 引用 CDB 中的 Transfer Length；
    ' 3. 不允许 Page 内部字段在局部找不到时随意引用其他 Page 的同 GUID 字段。
    Private Shared Function GetFieldByteLength(node As ScsiValueNode) As Integer
        Dim d = node.Definition
        If Not String.IsNullOrWhiteSpace(d.LengthFieldId) Then
            ' 向上查找当前字段所属的 Page 实例。
            Dim pageNode As ScsiValueNode = node
            While pageNode IsNot Nothing
                If pageNode.Definition IsNot Nothing AndAlso String.Equals(pageNode.Definition.Kind, "Page", StringComparison.OrdinalIgnoreCase) Then
                    Exit While
                End If
                pageNode = pageNode.Parent
            End While
            Dim lengthNode As ScsiValueNode = Nothing
            If pageNode IsNot Nothing Then
                ' Page 内字段：只允许在当前 Page 实例中查找。
                lengthNode = Flatten(New List(Of ScsiValueNode) From {pageNode}).FirstOrDefault(Function(n) n.Definition IsNot Nothing AndAlso String.Equals(n.Definition.Id, d.LengthFieldId, StringComparison.OrdinalIgnoreCase))
                If lengthNode Is Nothing Then
                    Throw New InvalidOperationException("字段 " & d.Name & " 引用的长度字段不存在：" & d.LengthFieldId)
                End If
            Else
                ' 顶层字段：允许引用当前指令中其他区域的字段，
                ' 例如 Param 中的 Binary 字段引用 CDB 中的长度字段。
                Dim allNodes = If(_currentNodes, New List(Of ScsiValueNode)())
                lengthNode = Flatten(allNodes).FirstOrDefault(Function(n) n.Definition IsNot Nothing AndAlso String.Equals(n.Definition.Id, d.LengthFieldId, StringComparison.OrdinalIgnoreCase))
                If lengthNode Is Nothing Then
                    Throw New InvalidOperationException("字段 " & d.Name & " 引用的长度字段不存在：" & d.LengthFieldId)
                End If
            End If
            Dim lengthValue As Long = ParseInteger(lengthNode.Value)
            If lengthValue < 0 OrElse lengthValue > Integer.MaxValue Then
                Throw New InvalidOperationException("字段 " & d.Name & " 的动态长度无效。")
            End If
            Return CInt(lengthValue)
        End If
        ' ByteLength = -1：
        ' 生成时，Binary 字段长度由输入的十六进制数据实际字节数决定。
        ' 解析时由 DecodeField 单独处理，不通过此处计算剩余长度。
        If d.FieldType = ScsiFieldType.Binary AndAlso d.ByteLength = -1 AndAlso String.IsNullOrWhiteSpace(d.ByteLengthExpression) Then
            Return ParseHexBytes(Convert.ToString(If(node.Value, ""), CultureInfo.InvariantCulture)).Length
        End If
        Dim configuredLength = GetConfiguredByteLength(node)
        Return If(configuredLength > 0, configuredLength, (d.BitLength + 7) \ 8)
    End Function

    ' 计算 ByteLength：支持固定整数，或引用当前已解析节点的表达式。
    ' 表达式结果单位为字节；.length 与其他公式一致，单位为 bit。
    Private Shared Function GetConfiguredByteLength(node As ScsiValueNode) As Integer
        If node Is Nothing OrElse node.Definition Is Nothing Then Return 0
        Dim d = node.Definition
        If String.IsNullOrWhiteSpace(d.ByteLengthExpression) Then Return Math.Max(0, d.ByteLength)
        ' 优先搜索当前 Page 实例，确保表达式引用的是同一实例内的字段。
        Dim scope As New List(Of ScsiValueNode)
        Dim pageScope As ScsiValueNode = node
        While pageScope IsNot Nothing
            If pageScope.Definition IsNot Nothing AndAlso String.Equals(pageScope.Definition.Kind, "Page", StringComparison.OrdinalIgnoreCase) Then
                Exit While
            End If
            pageScope = pageScope.Parent
        End While
        ' 当前 Page 优先，避免匹配到其他 Page 实例中的同 GUID 字段。
        If pageScope IsNot Nothing Then
            scope.AddRange(Flatten(New List(Of ScsiValueNode) From {pageScope}))
        End If
        ' 补充当前节点子树，兼容不属于 Page 的节点。
        scope.AddRange(Flatten(New List(Of ScsiValueNode) From {node}).Where(Function(x) Not scope.Any(Function(y) Object.ReferenceEquals(x, y))))
        ' 最后补充整个解析树，兼容跨 Page 引用等现有用法。
        Dim scopeRoot As ScsiValueNode = node
        While scopeRoot.Parent IsNot Nothing
            scopeRoot = scopeRoot.Parent
        End While
        scope.AddRange(Flatten(New List(Of ScsiValueNode) From {scopeRoot}).Where(Function(x) Not scope.Any(Function(y) Object.ReferenceEquals(x, y))))
        Dim result = BitCodec.EvaluateOffsetFormula(d.ByteLengthExpression,
            Function(id)
                Dim target = scope.FirstOrDefault(Function(x) x.Definition IsNot Nothing AndAlso String.Equals(x.Definition.Id, id, StringComparison.OrdinalIgnoreCase))
                If target Is Nothing Then Throw New InvalidOperationException("ByteLength 表达式引用的元素不存在：" & id)
                Return ParseInteger(target.Value)
            End Function,
            Function(id)
                Dim target = scope.FirstOrDefault(Function(x) x.Definition IsNot Nothing AndAlso String.Equals(x.Definition.Id, id, StringComparison.OrdinalIgnoreCase))
                If target Is Nothing Then Throw New InvalidOperationException("ByteLength 表达式引用的元素不存在：" & id)
                Return ElementExtentBits(target)
            End Function)
        If result < 0 OrElse result > Integer.MaxValue Then Throw New InvalidOperationException("元素 " & d.Name & " 的 ByteLength 表达式结果无效：" & result.ToString(CultureInfo.InvariantCulture))
        Return CInt(result)
    End Function

    Private Shared Sub DecodeField(node As ScsiValueNode, source As Byte(), bitOffset As Integer, limitBytes As Integer)
        Dim d = node.Definition
        If d.FieldType = ScsiFieldType.ASCII Then
            Dim count = GetFieldByteLength(node)
            If bitOffset Mod 8 <> 0 OrElse bitOffset \ 8 + count > Math.Min(source.Length, limitBytes) Then Return
            node.Value = Encoding.ASCII.GetString(source, bitOffset \ 8, count).TrimEnd(ChrW(0), " "c)
            Return
        ElseIf d.FieldType = ScsiFieldType.Binary Then
            If bitOffset Mod 8 <> 0 Then Return
            Dim startByte As Integer = bitOffset \ 8
            Dim maxBytes As Integer = Math.Min(source.Length, limitBytes)
            ' ByteLength = -1：解析当前有效数据区域中剩余的全部字节。
            If d.ByteLength = -1 AndAlso String.IsNullOrWhiteSpace(d.ByteLengthExpression) AndAlso String.IsNullOrWhiteSpace(d.LengthFieldId) Then
                If startByte > maxBytes Then Return
                Dim count As Integer = maxBytes - startByte
                node.Value = String.Join(" ", source.Skip(startByte).Take(count).Select(Function(b) b.ToString("X2")))
                Return
            End If
            ' 其他情况继续使用原有长度计算逻辑。
            Dim fixedCount As Integer = GetFieldByteLength(node)
            If startByte + fixedCount > maxBytes Then Return
            node.Value = String.Join(" ", source.Skip(startByte).Take(fixedCount).Select(Function(b) b.ToString("X2")))
            Return
        End If
        If bitOffset + d.BitLength > Math.Min(source.Length, limitBytes) * 8 Then Return
        Dim raw = BitCodec.ReadUnsigned(source, bitOffset, d.BitLength)
        If d.IsSigned AndAlso d.BitLength < 64 AndAlso ((raw >> (d.BitLength - 1)) And 1UL) = 1UL Then
            Dim signedBits As ULong = raw Or (ULong.MaxValue << d.BitLength)
            node.Value = BitConverter.ToInt64(BitConverter.GetBytes(signedBits), 0)
        ElseIf d.IsSigned AndAlso d.BitLength = 64 Then
            node.Value = BitConverter.ToInt64(BitConverter.GetBytes(raw), 0)
        Else
            node.Value = raw.ToString(CultureInfo.InvariantCulture)
        End If
    End Sub

    Private Shared Sub DecodePageList(listNode As ScsiValueNode, source As Byte(), startBit As Integer, limitBytes As Integer)
        If startBit Mod 8 <> 0 Then Throw New InvalidOperationException("PageList 必须按字节对齐：" & listNode.Definition.Name)
        Dim cursor = startBit \ 8
        Dim maxBytes = Math.Min(source.Length, limitBytes)
        Dim listByteLength = GetConfiguredByteLength(listNode)
        If listByteLength > 0 Then maxBytes = Math.Min(maxBytes, cursor + listByteLength)
        Dim allowed = GetAllowedPageTemplates(listNode.Definition, CurrentTemplate)
        If allowed.Count = 0 Then Return
        ' 优先尝试带有 MatchFieldId 的精确匹配模板；没有匹配条件的模板作为兜底，
        ' 避免一个无条件模板排在前面时吞掉后续所有专用模板。
        allowed = allowed.OrderBy(Function(p) If(String.IsNullOrWhiteSpace(p.MatchFieldId), 1, 0)).ToList()
        listNode.Items.Clear()
        Dim guard As Integer = 0
        While cursor < maxBytes AndAlso guard < 4096
            guard += 1
            Dim chosen As ScsiValueNode = Nothing
            Dim chosenSize As Integer = 0
            For Each pageDef In allowed
                Dim candidate = CreateNode(pageDef, CurrentTemplate, listNode)
                ' 先解析 Page 头部，只用已解析的匹配字段判断候选模板是否适用。
                ' 不匹配的候选必须立即跳过，不能继续计算 ByteLength / LengthFieldId，
                ' 否则无关模板中的动态长度表达式可能因默认值或不适用字段而报错。
                DecodePageHeaderFields(candidate, source, cursor * 8, maxBytes)
                If Not PageMatches(candidate, pageDef) Then Continue For

                ' 只有匹配成功的候选，才继续计算动态长度并解码完整 Page。
                Dim minBytes = GetConfiguredByteLength(candidate)
                If minBytes <= 0 Then minBytes = CInt((ElementExtentBits(candidate) + 7) \ 8)
                If minBytes <= 0 OrElse cursor + minBytes > maxBytes Then
                    ' Still allow variable-length pages with a header shorter than the calculated size.
                    minBytes = Math.Min(maxBytes - cursor, 4)
                End If
                Dim size = DetermineDecodedPageLength(candidate, source, cursor, maxBytes)
                If size <= 0 OrElse cursor + size > maxBytes Then Continue For
                DecodeNode(candidate, source, cursor * 8, False, cursor + size)
                chosen = candidate
                chosenSize = size
                Exit For
            Next
            If chosen Is Nothing OrElse chosenSize <= 0 Then Exit While
            chosen.Parent = listNode
            chosen.InstanceName = chosen.Definition.Name & " [" & (listNode.Items.Count + 1).ToString() & "]"
            listNode.Items.Add(chosen)
            cursor += chosenSize
        End While
    End Sub

    ' Current template is set around public Decode; avoids threading template through every recursive call.
    Private Shared _currentTemplate As ScsiTemplate
    Private Shared ReadOnly Property CurrentTemplate As ScsiTemplate
        Get
            If _currentTemplate Is Nothing Then Throw New InvalidOperationException("解码模板上下文未初始化。")
            Return _currentTemplate
        End Get
    End Property


    Private Shared Sub DecodePageHeaderFields(node As ScsiValueNode, source As Byte(), baseBit As Integer, limitBytes As Integer)
        If node Is Nothing OrElse node.Definition Is Nothing Then Return
        Dim d = node.Definition
        Dim startBit = baseBit + CInt(EffectiveOffsetBits(node))
        If d.Kind.Equals("Field", StringComparison.OrdinalIgnoreCase) Then
            DecodeField(node, source, startBit, limitBytes)
            Return
        End If
        If d.Kind.Equals("PageList", StringComparison.OrdinalIgnoreCase) Then Return
        For Each child In node.Children
            DecodePageHeaderFields(child, source, startBit, limitBytes)
        Next
    End Sub

    Private Shared Function DetermineDecodedPageLength(pageNode As ScsiValueNode, source As Byte(), cursor As Integer, limitBytes As Integer) As Integer
        Dim d = pageNode.Definition
        Dim configuredLength = GetConfiguredByteLength(pageNode)
        If configuredLength > 0 Then Return configuredLength
        If Not String.IsNullOrWhiteSpace(d.LengthFieldId) Then
            Dim lengthNode = Flatten(New List(Of ScsiValueNode) From {pageNode}).FirstOrDefault(Function(n) String.Equals(n.Definition.Id, d.LengthFieldId, StringComparison.OrdinalIgnoreCase))
            If lengthNode IsNot Nothing Then
                Dim value = ParseInteger(lengthNode.Value)
                If value >= 0 AndAlso value <= Integer.MaxValue Then
                    Dim total = CInt(value)
                    If Not d.LengthIncludesHeader Then
                        Dim relativeBits = RelativeBitOffset(pageNode, lengthNode)
                        Dim headerBytes = CInt((relativeBits + ElementExtentBits(lengthNode) + 7) \ 8)
                        total += headerBytes
                    End If
                    Return total
                End If
            End If
        End If
        Return CInt((ElementExtentBits(pageNode) + 7) \ 8)
    End Function

    Private Shared Function RelativeBitOffset(rootNode As ScsiValueNode, targetNode As ScsiValueNode) As Long
        Dim offset As Long = 0
        Dim current = targetNode
        While current IsNot Nothing AndAlso Not Object.ReferenceEquals(current, rootNode)
            If current.Definition IsNot Nothing Then offset += EffectiveOffsetBits(current)
            current = current.Parent
        End While
        If current Is Nothing Then Throw New InvalidOperationException("长度字段不属于 Page 模板：" & targetNode.Definition.Name)
        Return offset
    End Function

    Private Shared Function PageMatches(node As ScsiValueNode, def As ScsiElementDef) As Boolean
        If String.IsNullOrWhiteSpace(def.MatchFieldId) Then
            Return True
        End If
        Dim field = Flatten(New List(Of ScsiValueNode) From {node}).FirstOrDefault(Function(n) n.Definition IsNot Nothing AndAlso String.Equals(n.Definition.Id, def.MatchFieldId, StringComparison.OrdinalIgnoreCase))
        If field Is Nothing Then Return False
        Dim actualValue As String = Convert.ToString(If(field.Value, ""), CultureInfo.InvariantCulture).Trim()
        If String.IsNullOrWhiteSpace(actualValue) Then
            Return False
        End If
        ' 同时支持英文分号和英文逗号分隔。
        Dim matchValues As String() = def.MatchValue.Split(New Char() {";"c, ","c}, StringSplitOptions.RemoveEmptyEntries)
        For Each item As String In matchValues
            Dim expectedValue As String = item.Trim()
            If expectedValue = "" Then Continue For
            ' 优先按数值比较，支持十进制和 0x 前缀十六进制。
            Try
                If ParseInteger(actualValue) = ParseInteger(expectedValue) Then
                    Return True
                End If
            Catch
                ' 非数值字段退回不区分大小写的字符串比较。
                If String.Equals(actualValue, expectedValue, StringComparison.OrdinalIgnoreCase) Then
                    Return True
                End If
            End Try
        Next
        Return False
    End Function

    Private Shared Iterator Function Flatten(nodes As IEnumerable(Of ScsiValueNode)) As IEnumerable(Of ScsiValueNode)
        For Each n In nodes
            Yield n
            For Each c In Flatten(n.Children)
                Yield c
            Next
            For Each item In Flatten(n.Items)
                Yield item
            Next
        Next
    End Function

    Private Shared Function ParseInteger(value As Object) As Long
        Dim s = Convert.ToString(If(value, "0"), CultureInfo.InvariantCulture).Trim()
        If s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) Then Return Convert.ToInt64(s.Substring(2), 16)
        If s = "" Then Return 0
        Return Long.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture)
    End Function

    Private Shared Function ParseUnsigned(value As Object) As ULong
        Dim s = Convert.ToString(If(value, "0"), CultureInfo.InvariantCulture).Trim()
        If s = "" Then Return 0UL
        If s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) Then
            Return ULong.Parse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
        End If
        Return ULong.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture)
    End Function

    Private Shared Function ParseHexBytes(value As String) As Byte()
        Dim s = If(value, "").Trim()
        If s = "" Then Return New Byte() {}
        s = Regex.Replace(s, "(?i)0x", "")
        s = Regex.Replace(s, "[,\-:\s]", "")
        If (s.Length Mod 2) <> 0 Then Throw New FormatException("Binary 十六进制字符串必须是偶数个字符。")
        Dim result(s.Length \ 2 - 1) As Byte
        For i As Integer = 0 To result.Length - 1
            result(i) = Byte.Parse(s.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
        Next
        Return result
    End Function

    Private Shared Sub EnsureRange(target As Byte(), bitOffset As Integer, bitLength As Integer, fieldName As String)
        If bitOffset < 0 OrElse bitLength < 0 OrElse CLng(bitOffset) + bitLength > CLng(target.Length) * 8L Then
            Throw New InvalidOperationException("字段 " & fieldName & " 超出输出缓冲区。请检查偏移、位宽和长度定义。")
        End If
        If bitOffset Mod 8 <> 0 Then Throw New InvalidOperationException("ASCII/Binary 字段必须按字节对齐：" & fieldName)
    End Sub
End Class
