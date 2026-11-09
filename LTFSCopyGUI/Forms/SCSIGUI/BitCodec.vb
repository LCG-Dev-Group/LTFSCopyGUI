Option Strict On
Option Explicit On

Imports System.Globalization
Imports System.Text.RegularExpressions

Public NotInheritable Class BitCodec
    Private Sub New()
    End Sub

    ' BitOffset is absolute within the selected byte array; bit 0 means MSB of byte 0.
    Public Shared Sub WriteUnsigned(target As Byte(), bitOffset As Integer, bitLength As Integer, value As ULong)
        If bitLength < 1 OrElse bitLength > 64 Then Throw New ArgumentOutOfRangeException(NameOf(bitLength))
        If bitOffset < 0 OrElse bitOffset + bitLength > target.Length * 8 Then Throw New ArgumentOutOfRangeException(NameOf(bitOffset))
        For i As Integer = 0 To bitLength - 1
            Dim sourceBit As Integer = bitLength - 1 - i
            Dim bit As Integer = CInt((value >> sourceBit) And 1UL)
            Dim targetBit As Integer = bitOffset + i
            Dim mask As Byte = CByte(1 << (7 - (targetBit Mod 8)))
            If bit = 1 Then
                target(targetBit \ 8) = CByte(target(targetBit \ 8) Or mask)
            Else
                target(targetBit \ 8) = CByte(target(targetBit \ 8) And Not mask)
            End If
        Next
    End Sub

    Public Shared Function ReadUnsigned(source As Byte(), bitOffset As Integer, bitLength As Integer) As ULong
        If bitLength < 1 OrElse bitLength > 64 Then Throw New ArgumentOutOfRangeException(NameOf(bitLength))
        If bitOffset < 0 OrElse bitOffset + bitLength > source.Length * 8 Then Throw New ArgumentOutOfRangeException(NameOf(bitOffset))
        Dim result As ULong = 0UL
        For i As Integer = 0 To bitLength - 1
            Dim targetBit = bitOffset + i
            Dim bit = (source(targetBit \ 8) >> (7 - (targetBit Mod 8))) And 1
            result = (result << 1) Or CULng(bit)
        Next
        Return result
    End Function

    ' 动态偏移公式：
    '   <GUID>.value  = 引用元素的数值
    '   <GUID>.length = 引用元素实际占用的长度，单位 bit
    ' 支持 + - * / 和括号，不使用 Eval。
    Public Shared Function EvaluateOffsetFormula(formula As String, valueById As Func(Of String, Long), lengthById As Func(Of String, Long)) As Long
        If String.IsNullOrWhiteSpace(formula) Then Return 0

        ' 先替换 .length，再替换 .value，避免引用表达式互相干扰。
        Dim expr As String = Regex.Replace(formula, "<([0-9a-fA-F\-]+)>\.length",
            Function(m)
                Return lengthById(m.Groups(1).Value).ToString(
                CultureInfo.InvariantCulture)
            End Function, RegexOptions.IgnoreCase)

        expr = Regex.Replace(expr, "<([0-9a-fA-F\-]+)>\.value",
                             Function(m)
                                 Return valueById(m.Groups(1).Value).ToString(CultureInfo.InvariantCulture)
                             End Function, RegexOptions.IgnoreCase)

        Return CLng(New SafeMathParser(expr).Parse())
    End Function
    ' 存在条件公式，支持四则运算及等号比较。
    ' 比较结果：True = 1，False = 0。
    Public Shared Function EvaluatePresenceFormula(formula As String, valueById As Func(Of String, Long), lengthById As Func(Of String, Long)) As Long
        If String.IsNullOrWhiteSpace(formula) Then Return 1
        ' 先替换 .length，再替换 .value。
        Dim expr As String = Regex.Replace(formula, "<([0-9a-fA-F\-]+)>\.length",
            Function(m)
                Return lengthById(m.Groups(1).Value).ToString(CultureInfo.InvariantCulture)
            End Function, RegexOptions.IgnoreCase)
        expr = Regex.Replace(expr, "<([0-9a-fA-F\-]+)>\.value",
            Function(m)
                Return valueById(m.Groups(1).Value).ToString(CultureInfo.InvariantCulture)
            End Function, RegexOptions.IgnoreCase)
        Return CLng(New SafeMathParser(expr).ParseComparison())
    End Function

    ' Formula 支持：
    '   <GUID>.length：引用字段长度，单位为字节
    '   <GUID>.value ：引用字段的数值
    ' 支持 + - * / 和括号。
    Public Shared Function EvaluateLengthFormula(formula As String, lengthById As Func(Of String, Long), valueById As Func(Of String, Long)) As Long
        If String.IsNullOrWhiteSpace(formula) Then Return 0
        ' 先替换 .length，再替换 .value。
        Dim expr As String = Regex.Replace(formula, "<([0-9a-fA-F\-]+)>\.length", Function(m) lengthById(m.Groups(1).Value).ToString(CultureInfo.InvariantCulture), RegexOptions.IgnoreCase)
        expr = Regex.Replace(expr, "<([0-9a-fA-F\-]+)>\.value", Function(m) valueById(m.Groups(1).Value).ToString(CultureInfo.InvariantCulture), RegexOptions.IgnoreCase)
        Return CLng(New SafeMathParser(expr).Parse())
    End Function

    Private NotInheritable Class SafeMathParser
        Private ReadOnly _s As String
        Private _p As Integer
        Public Sub New(s As String)
            _s = s
        End Sub
        Public Function Parse() As Double
            Dim v = Expr()
            SkipWs()
            If _p <> _s.Length Then Throw New FormatException("长度公式包含不支持的字符：" & _s.Substring(_p))
            Return v
        End Function
        Private Function Expr() As Double
            Dim v = Term()
            Do
                SkipWs()
                If Take("+"c) Then
                    v += Term()
                ElseIf Take("-"c) Then
                    v -= Term()
                Else
                    Exit Do
                End If
            Loop
            Return v
        End Function
        Private Function Term() As Double
            Dim v = Factor()
            Do
                SkipWs()
                If Take("*"c) Then
                    v *= Factor()
                ElseIf Take("/"c) Then
                    Dim d = Factor()
                    If d = 0 Then Throw New DivideByZeroException()
                    v /= d
                ElseIf Take("\"c) Then
                    Dim d = Factor()
                    If d = 0 Then Throw New DivideByZeroException()
                    v = Math.Truncate(v / d)
                Else
                    Exit Do
                End If
            Loop
            Return v
        End Function
        Private Function Factor() As Double
            SkipWs()
            If Take("("c) Then
                Dim v = Expr()
                SkipWs()
                If Not Take(")"c) Then Throw New FormatException("缺少右括号。")
                Return v
            End If
            Dim start = _p
            If Take("-"c) Then Return -Factor()
            While _p < _s.Length AndAlso (Char.IsDigit(_s(_p)) OrElse _s(_p) = "."c)
                _p += 1
            End While
            If start = _p Then Throw New FormatException("公式需要数字。")
            Return Double.Parse(_s.Substring(start, _p - start), CultureInfo.InvariantCulture)
        End Function
        Public Function ParseComparison() As Double
            Dim leftValue As Double = Expr()
            SkipWs()
            If Take("="c) Then
                Dim rightValue As Double = Expr()
                SkipWs()
                If _p <> _s.Length Then
                    Throw New FormatException("存在条件公式包含不支持的字符：" & _s.Substring(_p))
                End If
                Return If(leftValue = rightValue, 1.0R, 0.0R)
            End If
            If _p <> _s.Length Then
                Throw New FormatException("存在条件公式包含不支持的字符：" & _s.Substring(_p))
            End If
            Return leftValue
        End Function
        Private Sub SkipWs()
            While _p < _s.Length AndAlso Char.IsWhiteSpace(_s(_p))
                _p += 1
            End While
        End Sub
        Private Function Take(c As Char) As Boolean
            If _p < _s.Length AndAlso _s(_p) = c Then
                _p += 1
                Return True
            End If
            Return False
        End Function
    End Class
End Class
