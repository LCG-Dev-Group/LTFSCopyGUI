Imports System
Imports System.Collections.Generic
Imports System.ComponentModel
Imports System.Diagnostics
Imports System.Drawing
Imports System.Globalization
Imports System.Numerics
Imports System.Windows.Forms
Imports LTFSCopyGUI.Native
Imports Vortice
Imports Vortice.DCommon
Imports Vortice.Direct2D1
Imports Vortice.DirectWrite
Imports Vortice.DXGI
Imports DrawingColor = System.Drawing.Color
Imports D2DColor4 = Vortice.Mathematics.Color4

''' <summary>
''' A small, Windows-only, Direct2D backed chart control for the LTFS writer.
'''
''' The control owns a bounded ring buffer and renders in device-independent
''' pixels.  The HWND render target is resized and its DPI is reset whenever
''' the control moves between monitors, so layout remains independent of the
''' monitor scale factor.
''' </summary>
<DefaultProperty("VisibleSamples")>
Public Class Direct2DChartControl
    Inherits Control

    Private Const DipsPerInch As Single = 96.0F
    Private Const DefaultMaxSamples As Integer = 3600 * 6
    Private Const DefaultVisibleSamples As Integer = 600
    Private Const PlotBottomMargin As Single = 20.0F
    Private Const PlotTopMargin As Single = 12.0F
    Private Const AnimationIntervalMilliseconds As Integer = 15
    Private Const AnimationDurationMilliseconds As Double = 180.0R

    Private Structure AxisInfo
        Public Minimum As Double
        Public Maximum As Double
        Public StepSize As Double
        Public IsLogarithmic As Boolean
        Public LogMinimum As Integer
        Public LogMaximum As Integer
    End Structure

    Private Structure ChartPoint
        Public X As Double
        Public Y As Double

        Public Sub New(xValue As Double, yValue As Double)
            X = xValue
            Y = yValue
        End Sub
    End Structure

    Private ReadOnly _dataLock As New Object()
    Private _primaryValues() As Double
    Private _secondaryValues() As Double
    Private _thirdValues() As Double
    Private _sampleCount As Integer
    Private _writeIndex As Integer
    Private _firstSampleIndex As Long
    Private _nextSampleIndex As Long

    Private _visibleSamples As Integer = DefaultVisibleSamples
    Private _maxSamples As Integer = DefaultMaxSamples
    Private _viewStart As Double = -(DefaultVisibleSamples - 1.0R)
    Private _targetViewStart As Double = -(DefaultVisibleSamples - 1.0R)
    Private _lastAnimationTime As Stopwatch
    Private _followLatest As Boolean = True
    Private _smoothScrolling As Boolean = True
    Private _isLogarithmic As Boolean

    Private ReadOnly _animationTimer As Timer
    Private _factory As ID2D1Factory
    Private _writeFactory As IDWriteFactory
    Private _renderTarget As ID2D1HwndRenderTarget
    Private _clientPixelSize As Size
    Private _dpiX As Single = DipsPerInch
    Private _dpiY As Single = DipsPerInch

    Private _gridBrush As ID2D1SolidColorBrush
    Private _axisBrush As ID2D1SolidColorBrush
    Private _textBrush As ID2D1SolidColorBrush
    Private _primaryBrush As ID2D1SolidColorBrush
    Private _secondaryBrush As ID2D1SolidColorBrush
    Private _thirdBrush As ID2D1SolidColorBrush
    Private _axisLabelLeftFormat As IDWriteTextFormat
    Private _axisLabelRightFormat As IDWriteTextFormat
    Private _axisTitleFormat As IDWriteTextFormat
    Private _chartTitleFormat As IDWriteTextFormat
    Private _xTitleFormat As IDWriteTextFormat

    Private _primaryColor As DrawingColor = DrawingColor.FromArgb(128, 128, 255)
    Private _secondaryColor As DrawingColor = DrawingColor.FromArgb(255, 128, 0)
    Private _thirdColor As DrawingColor = DrawingColor.Red
    Private _gridColor As DrawingColor = DrawingColor.FromArgb(205, 205, 225)
    Private _axisColor As DrawingColor = DrawingColor.FromArgb(110, 110, 130)
    Private _chartTitle As String = "10分钟"
    Private _primaryAxisTitle As String = "速度 (MiB/s)"
    Private _secondaryAxisTitle As String = "每秒文件"
    Private _xAxisTitle As String = ""
    Private _lastRenderException As Exception

    Public Sub New()
        SetStyle(ControlStyles.UserPaint Or
                 ControlStyles.AllPaintingInWmPaint Or
                 ControlStyles.Opaque Or
                 ControlStyles.ResizeRedraw, True)
        UpdateStyles()

        BackColor = SystemColors.Window
        ForeColor = SystemColors.ControlText
        TabStop = False

        ReallocateDataBuffers(DefaultMaxSamples)

        _animationTimer = New Timer With {.Interval = AnimationIntervalMilliseconds}
        AddHandler _animationTimer.Tick, AddressOf AnimationTimer_Tick
    End Sub

    <Category("Chart")>
    <DefaultValue(DefaultVisibleSamples)>
    Public Property VisibleSamples As Integer
        Get
            Return _visibleSamples
        End Get
        Set(value As Integer)
            Dim newValue As Integer = Math.Max(2, Math.Min(value, _maxSamples))
            If _visibleSamples = newValue Then Return
            _visibleSamples = newValue
            UpdateTargetView()
            Invalidate()
        End Set
    End Property

    <Category("Chart")>
    <DefaultValue(DefaultMaxSamples)>
    Public Property MaxSamples As Integer
        Get
            Return _maxSamples
        End Get
        Set(value As Integer)
            Dim newValue As Integer = Math.Max(2, value)
            If _maxSamples = newValue Then Return
            _maxSamples = newValue
            ReallocateDataBuffers(newValue)
            UpdateTargetView()
            Invalidate()
        End Set
    End Property

    <Category("Chart")>
    <DefaultValue(True)>
    Public Property FollowLatest As Boolean
        Get
            Return _followLatest
        End Get
        Set(value As Boolean)
            _followLatest = value
            If value Then UpdateTargetView()
            Invalidate()
        End Set
    End Property

    <Category("Chart")>
    <DefaultValue(True)>
    Public Property SmoothScrolling As Boolean
        Get
            Return _smoothScrolling
        End Get
        Set(value As Boolean)
            _smoothScrolling = value
            If Not value Then
                _viewStart = _targetViewStart
                _animationTimer.Stop()
            End If
            Invalidate()
        End Set
    End Property

    <Category("Chart")>
    <DefaultValue(False)>
    Public Property IsLogarithmic As Boolean
        Get
            Return _isLogarithmic
        End Get
        Set(value As Boolean)
            If _isLogarithmic = value Then Return
            _isLogarithmic = value
            Invalidate()
        End Set
    End Property

    <Category("Chart")>
    <DefaultValue("10分钟")>
    Public Property ChartTitle As String
        Get
            Return _chartTitle
        End Get
        Set(value As String)
            _chartTitle = If(value, String.Empty)
            Invalidate()
        End Set
    End Property

    <Category("Chart")>
    <DefaultValue("速度 (MiB/s)")>
    Public Property PrimaryAxisTitle As String
        Get
            Return _primaryAxisTitle
        End Get
        Set(value As String)
            _primaryAxisTitle = If(value, String.Empty)
            Invalidate()
        End Set
    End Property

    <Category("Chart")>
    <DefaultValue("每秒文件")>
    Public Property SecondaryAxisTitle As String
        Get
            Return _secondaryAxisTitle
        End Get
        Set(value As String)
            _secondaryAxisTitle = If(value, String.Empty)
            Invalidate()
        End Set
    End Property

    <Category("Chart")>
    <DefaultValue("")>
    Public Property XAxisTitle As String
        Get
            Return _xAxisTitle
        End Get
        Set(value As String)
            _xAxisTitle = If(value, String.Empty)
            Invalidate()
        End Set
    End Property

    <Category("Appearance")>
    Public Property PrimaryColor As DrawingColor
        Get
            Return _primaryColor
        End Get
        Set(value As DrawingColor)
            _primaryColor = value
            DisposeBrushes()
            Invalidate()
        End Set
    End Property

    <Category("Appearance")>
    Public Property SecondaryColor As DrawingColor
        Get
            Return _secondaryColor
        End Get
        Set(value As DrawingColor)
            _secondaryColor = value
            DisposeBrushes()
            Invalidate()
        End Set
    End Property

    <Category("Appearance")>
    Public Property ThirdColor As DrawingColor
        Get
            Return _thirdColor
        End Get
        Set(value As DrawingColor)
            _thirdColor = value
            DisposeBrushes()
            Invalidate()
        End Set
    End Property

    <Category("Appearance")>
    Public Property GridColor As DrawingColor
        Get
            Return _gridColor
        End Get
        Set(value As DrawingColor)
            _gridColor = value
            DisposeBrushes()
            Invalidate()
        End Set
    End Property

    <Category("Appearance")>
    Public Property AxisColor As DrawingColor
        Get
            Return _axisColor
        End Get
        Set(value As DrawingColor)
            _axisColor = value
            DisposeBrushes()
            Invalidate()
        End Set
    End Property

    <Browsable(False)>
    Public ReadOnly Property SampleCount As Integer
        Get
            SyncLock _dataLock
                Return _sampleCount
            End SyncLock
        End Get
    End Property

    <Browsable(False)>
    Public ReadOnly Property CurrentDpi As Single
        Get
            Return _dpiX
        End Get
    End Property

    ''' <summary>
    ''' Adds one sample.  The method is thread-safe; invalidation is marshalled
    ''' to the UI thread when it is called by a worker.
    ''' </summary>
    Public Sub AppendSample(primaryValue As Double, secondaryValue As Double, Optional thirdValue As Double = Double.NaN)
        SyncLock _dataLock
            AppendSampleUnsafe(primaryValue, secondaryValue, thirdValue)
        End SyncLock

        If _followLatest Then
            UpdateTargetView()
        End If
        RequestRedraw()
    End Sub

    ''' <summary>
    ''' Replaces the ring buffer with a snapshot of the supplied series.
    ''' </summary>
    Public Sub SetData(primaryValues As IList(Of Double), secondaryValues As IList(Of Double), Optional thirdValues As IList(Of Double) = Nothing)
        If primaryValues Is Nothing Then Throw New ArgumentNullException(NameOf(primaryValues))
        If secondaryValues Is Nothing Then Throw New ArgumentNullException(NameOf(secondaryValues))

        SyncLock _dataLock
            ClearDataUnsafe()
            Dim count As Integer = Math.Min(primaryValues.Count, secondaryValues.Count)
            For i As Integer = 0 To count - 1
                Dim thirdValue As Double = Double.NaN
                If thirdValues IsNot Nothing AndAlso i < thirdValues.Count Then thirdValue = thirdValues(i)
                AppendSampleUnsafe(primaryValues(i), secondaryValues(i), thirdValue)
            Next
        End SyncLock

        UpdateTargetView()
        _viewStart = _targetViewStart
        _animationTimer.Stop()
        RequestRedraw()
    End Sub

    Public Sub ClearData()
        SyncLock _dataLock
            ClearDataUnsafe()
        End SyncLock
        _viewStart = -(_visibleSamples - 1.0R)
        _targetViewStart = _viewStart
        _animationTimer.Stop()
        RequestRedraw()
    End Sub

    ''' <summary>
    ''' Sets the left edge of the visible sample window.  Setting this turns
    ''' off automatic following until FollowLatest is set to True again.
    ''' </summary>
    Public Sub SetViewStart(value As Double)
        If Double.IsNaN(value) OrElse Double.IsInfinity(value) Then Return
        _followLatest = False
        _targetViewStart = Math.Max(0.0R, value)
        If _smoothScrolling Then
            StartAnimation()
        Else
            _viewStart = _targetViewStart
        End If
        RequestRedraw()
    End Sub

    Protected Overrides Sub OnHandleCreated(e As EventArgs)
        MyBase.OnHandleCreated(e)
        RefreshDpi()
        UpdateTargetView()
        RequestRedraw()
    End Sub

    Protected Overrides Sub OnHandleDestroyed(e As EventArgs)
        _animationTimer.Stop()
        DisposeDeviceResources()
        MyBase.OnHandleDestroyed(e)
    End Sub

    Protected Overrides Sub OnResize(e As EventArgs)
        MyBase.OnResize(e)
        If _renderTarget IsNot Nothing Then
            Try
                Dim newPixelSize As Size = GetClientPixelSize()
                If newPixelSize.Width > 0 AndAlso newPixelSize.Height > 0 Then
                    _renderTarget.Resize(newPixelSize)
                    _renderTarget.SetDpi(_dpiX, _dpiY)
                    _clientPixelSize = newPixelSize
                End If
            Catch
                DisposeRenderTarget()
            End Try
        End If
        Invalidate()
    End Sub

    Private Sub Direct2DChartControl_DpiChanged(sender As Object, e As EventArgs) Handles MyBase.DpiChangedBeforeParent, MyBase.DpiChangedAfterParent
        RefreshDpi()
        DisposeRenderTarget()
        Invalidate()
    End Sub

    Protected Overrides Sub OnFontChanged(e As EventArgs)
        MyBase.OnFontChanged(e)
        DisposeTextFormats()
        Invalidate()
    End Sub

    Protected Overrides Sub OnBackColorChanged(e As EventArgs)
        MyBase.OnBackColorChanged(e)
        DisposeBrushes()
        Invalidate()
    End Sub

    Protected Overrides Sub OnForeColorChanged(e As EventArgs)
        MyBase.OnForeColorChanged(e)
        DisposeBrushes()
        Invalidate()
    End Sub

    Protected Overrides Sub OnPaintBackground(e As PaintEventArgs)
        ' The Direct2D render target paints the complete client area.
    End Sub

    Protected Overrides Sub OnPaint(e As PaintEventArgs)
        If Not IsHandleCreated OrElse ClientSize.Width <= 0 OrElse ClientSize.Height <= 0 Then Return

        Try
            EnsureDeviceResources()
            EnsureRenderTarget()
            If _renderTarget Is Nothing Then
                e.Graphics.Clear(BackColor)
                Return
            End If

            _renderTarget.BeginDraw()
            _renderTarget.SetDpi(_dpiX, _dpiY)
            _renderTarget.AntialiasMode = AntialiasMode.PerPrimitive
            _renderTarget.TextAntialiasMode = Direct2D1.TextAntialiasMode.Grayscale
            _renderTarget.Clear(ToColor4(BackColor))
            DrawChart()

            Dim result = _renderTarget.EndDraw()
            If result.Failure Then
                DisposeRenderTarget()
            Else
                _lastRenderException = Nothing
            End If
        Catch ex As Exception
            _lastRenderException = ex
            DisposeRenderTarget()
            Try
                e.Graphics.Clear(BackColor)
            Catch
            End Try
        End Try
    End Sub

    Private Sub AnimationTimer_Tick(sender As Object, e As EventArgs)
        If Not _smoothScrolling Then
            _viewStart = _targetViewStart
            _animationTimer.Stop()
            Invalidate()
            Return
        End If

        If _lastAnimationTime Is Nothing Then _lastAnimationTime = Stopwatch.StartNew()
        Dim elapsed As Double = Math.Max(1.0R, _lastAnimationTime.Elapsed.TotalMilliseconds)
        _lastAnimationTime.Restart()

        Dim distance As Double = _targetViewStart - _viewStart
        If Math.Abs(distance) < 0.01R Then
            _viewStart = _targetViewStart
            _animationTimer.Stop()
        Else
            Dim alpha As Double = 1.0R - Math.Exp(-elapsed / AnimationDurationMilliseconds)
            _viewStart += distance * alpha
        End If
        Invalidate()
    End Sub

    Private Sub EnsureDeviceResources()
        If _factory Is Nothing Then
            _factory = D2D1.D2D1CreateFactory(Of ID2D1Factory)(Direct2D1.FactoryType.SingleThreaded)
        End If
        If _writeFactory Is Nothing Then
            _writeFactory = DWrite.DWriteCreateFactory(Of IDWriteFactory)(DirectWrite.FactoryType.Shared)
        End If
        If _axisLabelLeftFormat Is Nothing Then CreateTextFormats()
    End Sub

    Private Sub EnsureRenderTarget()
        If _renderTarget IsNot Nothing OrElse Not IsHandleCreated Then Return

        Dim pixelSize As Size = GetClientPixelSize()
        If pixelSize.Width <= 0 OrElse pixelSize.Height <= 0 Then Return

        Dim pixelFormat = New PixelFormat(Format.B8G8R8A8_UNorm, DCommon.AlphaMode.Ignore)
        Dim renderProperties = New RenderTargetProperties(pixelFormat)
        renderProperties.DpiX = _dpiX
        renderProperties.DpiY = _dpiY

        Dim hwndProperties As New HwndRenderTargetProperties With {
            .Hwnd = Handle,
            .PixelSize = pixelSize,
            .PresentOptions = PresentOptions.None
        }

        _renderTarget = _factory.CreateHwndRenderTarget(renderProperties, hwndProperties)
        _renderTarget.SetDpi(_dpiX, _dpiY)
        _renderTarget.AntialiasMode = AntialiasMode.PerPrimitive
        _renderTarget.TextAntialiasMode = Direct2D1.TextAntialiasMode.Grayscale
        _clientPixelSize = pixelSize
        CreateBrushes()
    End Sub

    Private Sub CreateTextFormats()
        DisposeTextFormats()
        If _writeFactory Is Nothing Then Return

        Dim chartFont As Font = If(Font, SystemFonts.MessageBoxFont)
        Dim familyName As String = chartFont.FontFamily.Name
        Dim sizeInDips As Single = Math.Max(8.0F, chartFont.SizeInPoints * DipsPerInch / 72.0F)
        Dim weight As FontWeight = If(chartFont.Bold, FontWeight.Bold, FontWeight.Normal)
        Dim style As DirectWrite.FontStyle = If(chartFont.Italic, DirectWrite.FontStyle.Italic, DirectWrite.FontStyle.Normal)

        _axisLabelLeftFormat = _writeFactory.CreateTextFormat(familyName, weight, style, sizeInDips)
        _axisLabelLeftFormat.TextAlignment = TextAlignment.Trailing
        _axisLabelLeftFormat.ParagraphAlignment = ParagraphAlignment.Center
        _axisLabelLeftFormat.WordWrapping = WordWrapping.NoWrap

        _axisLabelRightFormat = _writeFactory.CreateTextFormat(familyName, weight, style, sizeInDips)
        _axisLabelRightFormat.TextAlignment = TextAlignment.Leading
        _axisLabelRightFormat.ParagraphAlignment = ParagraphAlignment.Center
        _axisLabelRightFormat.WordWrapping = WordWrapping.NoWrap

        _axisTitleFormat = _writeFactory.CreateTextFormat(familyName, weight, style, sizeInDips)
        _axisTitleFormat.TextAlignment = TextAlignment.Center
        _axisTitleFormat.ParagraphAlignment = ParagraphAlignment.Center
        _axisTitleFormat.WordWrapping = WordWrapping.NoWrap

        _chartTitleFormat = _writeFactory.CreateTextFormat(familyName, weight, style, sizeInDips)
        _chartTitleFormat.TextAlignment = TextAlignment.Center
        _chartTitleFormat.ParagraphAlignment = ParagraphAlignment.Center
        _chartTitleFormat.WordWrapping = WordWrapping.NoWrap

        _xTitleFormat = _writeFactory.CreateTextFormat(familyName, weight, style, sizeInDips)
        _xTitleFormat.TextAlignment = TextAlignment.Center
        _xTitleFormat.ParagraphAlignment = ParagraphAlignment.Center
        _xTitleFormat.WordWrapping = WordWrapping.NoWrap
    End Sub

    Private Sub CreateBrushes()
        DisposeBrushes()
        _gridBrush = _renderTarget.CreateSolidColorBrush(ToColor4(_gridColor), Nothing)
        _axisBrush = _renderTarget.CreateSolidColorBrush(ToColor4(_axisColor), Nothing)
        _textBrush = _renderTarget.CreateSolidColorBrush(ToColor4(ForeColor), Nothing)
        _primaryBrush = _renderTarget.CreateSolidColorBrush(ToColor4(_primaryColor), Nothing)
        _secondaryBrush = _renderTarget.CreateSolidColorBrush(ToColor4(_secondaryColor), Nothing)
        _thirdBrush = _renderTarget.CreateSolidColorBrush(ToColor4(_thirdColor), Nothing)
    End Sub

    Private Sub DrawChart()
        If _renderTarget Is Nothing OrElse _axisLabelLeftFormat Is Nothing Then Return

        Dim renderSize As SizeF = _renderTarget.Size
        Dim plotLeft As Single = 58.0F
        Dim plotRight As Single = Math.Max(plotLeft + 10.0F, renderSize.Width - 58.0F)
        Dim plotTop As Single = 24.0F - If(ChartTitle.Length = 0, PlotTopMargin, 0)
        Dim plotBottom As Single = Math.Max(plotTop + 10.0F, renderSize.Height - If(XAxisTitle.Length = 0, 12, PlotBottomMargin))
        Dim plot As New RectangleF(plotLeft, plotTop, plotRight - plotLeft, plotBottom - plotTop)
        If plot.Width <= 10.0F OrElse plot.Height <= 10.0F Then Return

        Dim firstX As Double
        Dim lastX As Double
        SyncLock _dataLock
            firstX = _firstSampleIndex
            lastX = _nextSampleIndex - 1L
        End SyncLock

        Dim xSpan As Double = Math.Max(1.0R, _visibleSamples - 1.0R)
        Dim viewStart As Double = _viewStart
        Dim viewEnd As Double = viewStart + xSpan

        Dim primaryAxis As AxisInfo = GetAxisInfo(0, viewStart, viewEnd)
        Dim secondaryAxis As AxisInfo = GetAxisInfo(1, viewStart, viewEnd)

        DrawAxes(plot, primaryAxis, secondaryAxis)

        Dim clip As New RawRectF(plot.Left, plot.Top, plot.Right, plot.Bottom)
        _renderTarget.PushAxisAlignedClip(clip, AntialiasMode.PerPrimitive)
        Try
            DrawSeries(0, primaryAxis, plot, viewStart, viewEnd, _primaryBrush)
            DrawSeries(1, secondaryAxis, plot, viewStart, viewEnd, _secondaryBrush)
            DrawSeries(2, secondaryAxis, plot, viewStart, viewEnd, _thirdBrush)
        Finally
            _renderTarget.PopAxisAlignedClip()
        End Try

        If _chartTitle.Length > 0 Then
            _renderTarget.DrawText(_chartTitle, _chartTitleFormat, New RectangleF(plot.Left, 0.0F, plot.Width, plot.Top), _textBrush)
        End If
        If _xAxisTitle.Length > 0 Then
            _renderTarget.DrawText(_xAxisTitle, _xTitleFormat, New RectangleF(plot.Left, plot.Bottom, plot.Width, renderSize.Height - plot.Bottom), _textBrush)
        End If
        DrawVerticalTitle(_primaryAxisTitle, 17.0F, (plot.Top + plot.Bottom) / 2.0F, _primaryColor)
        DrawVerticalTitle(_secondaryAxisTitle, renderSize.Width - 17.0F, (plot.Top + plot.Bottom) / 2.0F, _secondaryColor)
    End Sub

    Private Sub DrawAxes(plot As RectangleF, primaryAxis As AxisInfo, secondaryAxis As AxisInfo)
        Dim gridWidth As Single = 1.0F
        Dim axisWidth As Single = 1.0F

        If primaryAxis.IsLogarithmic Then
            For power As Integer = primaryAxis.LogMinimum To primaryAxis.LogMaximum
                Dim value As Double = Math.Pow(10.0R, power)
                Dim y As Single = ValueToY(value, primaryAxis, plot)
                _renderTarget.DrawLine(New Vector2(plot.Left, y), New Vector2(plot.Right, y), _gridBrush, gridWidth)
                Dim label As String = FormatAxisValue(value, True)
                _renderTarget.DrawText(label, _axisLabelLeftFormat, New RectangleF(0.0F, y - 10.0F, plot.Left - 5.0F, 20.0F), _primaryBrush)
            Next
        Else
            Dim tickCount As Integer = 5
            For i As Integer = 0 To tickCount
                Dim value As Double = primaryAxis.Minimum + primaryAxis.StepSize * i
                If value > primaryAxis.Maximum + primaryAxis.StepSize * 0.01R Then Exit For
                Dim y As Single = ValueToY(value, primaryAxis, plot)
                _renderTarget.DrawLine(New Vector2(plot.Left, y), New Vector2(plot.Right, y), _gridBrush, gridWidth)
                _renderTarget.DrawText(FormatAxisValue(value, False), _axisLabelLeftFormat, New RectangleF(0.0F, y - 10.0F, plot.Left - 5.0F, 20.0F), _primaryBrush)
            Next
        End If

        If secondaryAxis.IsLogarithmic Then
            For power As Integer = secondaryAxis.LogMinimum To secondaryAxis.LogMaximum
                Dim value As Double = Math.Pow(10.0R, power)
                Dim y As Single = ValueToY(value, secondaryAxis, plot)
                _renderTarget.DrawText(FormatAxisValue(value, True), _axisLabelRightFormat, New RectangleF(plot.Right + 5.0F, y - 10.0F, 54.0F, 20.0F), _secondaryBrush)
            Next
        Else
            Dim tickCount As Integer = 5
            For i As Integer = 0 To tickCount
                Dim value As Double = secondaryAxis.Minimum + secondaryAxis.StepSize * i
                If value > secondaryAxis.Maximum + secondaryAxis.StepSize * 0.01R Then Exit For
                Dim y As Single = ValueToY(value, secondaryAxis, plot)
                _renderTarget.DrawText(FormatAxisValue(value, False), _axisLabelRightFormat, New RectangleF(plot.Right + 5.0F, y - 10.0F, 54.0F, 20.0F), _secondaryBrush)
            Next
        End If

        _renderTarget.DrawLine(New Vector2(plot.Left, plot.Top), New Vector2(plot.Left, plot.Bottom), _axisBrush, axisWidth)
        _renderTarget.DrawLine(New Vector2(plot.Right, plot.Top), New Vector2(plot.Right, plot.Bottom), _axisBrush, axisWidth)
        _renderTarget.DrawLine(New Vector2(plot.Left, plot.Bottom), New Vector2(plot.Right, plot.Bottom), _axisBrush, axisWidth)
    End Sub

    Private Sub DrawVerticalTitle(text As String, x As Single, centerY As Single, color As DrawingColor)
        If String.IsNullOrEmpty(text) OrElse _axisTitleFormat Is Nothing Then Return
        Dim oldTransform As Matrix3x2 = _renderTarget.Transform
        Try
            _renderTarget.Transform = Matrix3x2.CreateRotation(-CSng(Math.PI / 2.0R), New Vector2(x, centerY))
            Dim rectangle As New RectangleF(x - 55.0F, centerY - 11.0F, 110.0F, 22.0F)
            Dim brush As ID2D1SolidColorBrush = If(color = _primaryColor, _primaryBrush, _secondaryBrush)
            _renderTarget.DrawText(text, _axisTitleFormat, rectangle, brush)
        Finally
            _renderTarget.Transform = oldTransform
        End Try
    End Sub

    Private Sub DrawSeries(seriesIndex As Integer, axis As AxisInfo, plot As RectangleF, viewStart As Double, viewEnd As Double, brush As ID2D1Brush)
        Dim points As List(Of ChartPoint) = GetVisiblePoints(seriesIndex, viewStart, viewEnd, Math.Max(256, CInt(Math.Ceiling(plot.Width * 1.5F))))
        If points.Count < 2 Then Return

        Using geometry As ID2D1PathGeometry = _factory.CreatePathGeometry()
            Using sink As ID2D1GeometrySink = geometry.Open()
                Dim started As Boolean = False
                Dim previousWasValid As Boolean = False
                For Each point As ChartPoint In points
                    If Double.IsNaN(point.Y) OrElse Double.IsInfinity(point.Y) Then
                        If started AndAlso previousWasValid Then sink.EndFigure(FigureEnd.Open)
                        started = False
                        previousWasValid = False
                        Continue For
                    End If

                    Dim x As Single = CSng(plot.Left + ((point.X - viewStart) / Math.Max(1.0R, viewEnd - viewStart)) * plot.Width)
                    Dim y As Single = ValueToY(point.Y, axis, plot)
                    Dim vector As New Vector2(x, y)
                    If Not started Then
                        sink.BeginFigure(vector, FigureBegin.Hollow)
                        started = True
                    Else
                        sink.AddLine(vector)
                    End If
                    previousWasValid = True
                Next
                If started AndAlso previousWasValid Then sink.EndFigure(FigureEnd.Open)
                sink.Close()
            End Using
            _renderTarget.DrawGeometry(geometry, brush, 2.0F)
        End Using
    End Sub

    Private Function GetAxisInfo(seriesIndex As Integer, viewStart As Double, viewEnd As Double) As AxisInfo
        Dim minimum As Double = Double.PositiveInfinity
        Dim maximum As Double = Double.NegativeInfinity
        Dim hasValue As Boolean = False

        SyncLock _dataLock
            Dim first As Long = Math.Max(_firstSampleIndex, CLng(Math.Floor(viewStart)))
            Dim last As Long = Math.Min(_nextSampleIndex - 1L, CLng(Math.Ceiling(viewEnd)))
            If first <= last Then
                For x As Long = first To last
                    Dim value As Double
                    If TryGetValueUnsafe(seriesIndex, x, value) AndAlso Not Double.IsNaN(value) AndAlso Not Double.IsInfinity(value) Then
                        If _isLogarithmic AndAlso seriesIndex = 0 AndAlso value <= 0.0R Then Continue For
                        minimum = Math.Min(minimum, value)
                        maximum = Math.Max(maximum, value)
                        hasValue = True
                    End If
                Next
            End If
        End SyncLock

        If Not hasValue Then
            minimum = If(_isLogarithmic AndAlso seriesIndex = 0, 1.0R, 0.0R)
            maximum = If(_isLogarithmic AndAlso seriesIndex = 0, 10.0R, 1.0R)
        End If

        If _isLogarithmic AndAlso seriesIndex = 0 Then
            Dim logMin As Integer = CInt(Math.Floor(Math.Log10(Math.Max(Double.Epsilon, minimum))))
            Dim logMax As Integer = CInt(Math.Ceiling(Math.Log10(Math.Max(Double.Epsilon, maximum))))
            If logMax <= logMin Then logMax = logMin + 1
            Return New AxisInfo With {
                .Minimum = Math.Pow(10.0R, logMin),
                .Maximum = Math.Pow(10.0R, logMax),
                .StepSize = 1.0R,
                .IsLogarithmic = True,
                .LogMinimum = logMin,
                .LogMaximum = logMax
            }
        End If

        If maximum <= minimum Then
            If minimum = 0.0R Then
                maximum = 1.0R
            Else
                Dim padding As Double = Math.Max(Math.Abs(minimum) * 0.1R, 0.5R)
                minimum -= padding
                maximum += padding
            End If
        End If

        Dim rawStep As Double = (maximum - minimum) / 5.0R
        Dim stepSize As Double = NiceStep(rawStep)
        Dim axisMinimum As Double = Math.Floor(minimum / stepSize) * stepSize
        Dim axisMaximum As Double = Math.Ceiling(maximum / stepSize) * stepSize
        If axisMaximum <= axisMinimum Then axisMaximum = axisMinimum + stepSize

        Return New AxisInfo With {
            .Minimum = axisMinimum,
            .Maximum = axisMaximum,
            .StepSize = stepSize,
            .IsLogarithmic = False,
            .LogMinimum = 0,
            .LogMaximum = 0
        }
    End Function

    Private Shared Function NiceStep(value As Double) As Double
        If value <= 0.0R OrElse Double.IsNaN(value) OrElse Double.IsInfinity(value) Then Return 1.0R
        Dim exponent As Double = Math.Floor(Math.Log10(value))
        Dim fraction As Double = value / Math.Pow(10.0R, exponent)
        Dim niceFraction As Double
        If fraction <= 1.0R Then
            niceFraction = 1.0R
        ElseIf fraction <= 2.0R Then
            niceFraction = 2.0R
        ElseIf fraction <= 5.0R Then
            niceFraction = 5.0R
        Else
            niceFraction = 10.0R
        End If
        Return niceFraction * Math.Pow(10.0R, exponent)
    End Function

    Private Shared Function ValueToY(value As Double, axis As AxisInfo, plot As RectangleF) As Single
        Dim transformedValue As Double = value
        Dim transformedMinimum As Double = axis.Minimum
        Dim transformedMaximum As Double = axis.Maximum
        If axis.IsLogarithmic Then
            If value <= 0.0R OrElse Double.IsNaN(value) Then Return plot.Bottom
            transformedValue = Math.Log10(value)
            transformedMinimum = axis.LogMinimum
            transformedMaximum = axis.LogMaximum
        End If
        Dim fraction As Double = (transformedValue - transformedMinimum) / Math.Max(Double.Epsilon, transformedMaximum - transformedMinimum)
        fraction = Math.Max(0.0R, Math.Min(1.0R, fraction))
        Return CSng(plot.Bottom - fraction * plot.Height)
    End Function

    Private Shared Function FormatAxisValue(value As Double, logarithmic As Boolean) As String
        If logarithmic Then
            If value >= 1000.0R OrElse value < 0.01R Then Return value.ToString("0.###E+0", CultureInfo.CurrentCulture)
            Return value.ToString("0.###", CultureInfo.CurrentCulture)
        End If
        If Math.Abs(value) >= 1000.0R Then Return value.ToString("0.#", CultureInfo.CurrentCulture)
        Return value.ToString("0.##", CultureInfo.CurrentCulture)
    End Function

    Private Function GetVisiblePoints(seriesIndex As Integer, viewStart As Double, viewEnd As Double, maxPoints As Integer) As List(Of ChartPoint)
        Dim points As New List(Of ChartPoint)()
        SyncLock _dataLock
            Dim first As Long = Math.Max(_firstSampleIndex, CLng(Math.Floor(viewStart)) - 1L)
            Dim last As Long = Math.Min(_nextSampleIndex - 1L, CLng(Math.Ceiling(viewEnd)) + 1L)
            If first > last Then Return points

            Dim sourceCount As Long = last - first + 1L
            Dim stride As Long = Math.Max(1L, CInt(Math.Ceiling(sourceCount / CDbl(Math.Max(2, maxPoints)))))
            Dim x As Long = first
            While x <= last
                Dim value As Double
                If TryGetValueUnsafe(seriesIndex, x, value) Then points.Add(New ChartPoint(x, value))
                x += stride
            End While

            If last >= first AndAlso (points.Count = 0 OrElse points(points.Count - 1).X <> last) Then
                Dim lastValue As Double
                If TryGetValueUnsafe(seriesIndex, last, lastValue) Then points.Add(New ChartPoint(last, lastValue))
            End If
        End SyncLock
        Return points
    End Function

    Private Function TryGetValueUnsafe(seriesIndex As Integer, sampleIndex As Long, ByRef value As Double) As Boolean
        If sampleIndex < _firstSampleIndex OrElse sampleIndex >= _nextSampleIndex OrElse _sampleCount <= 0 Then
            value = Double.NaN
            Return False
        End If

        Dim offset As Integer = CInt(sampleIndex - _firstSampleIndex)
        Dim oldestIndex As Integer = _writeIndex - _sampleCount
        If oldestIndex < 0 Then oldestIndex += _maxSamples
        Dim index As Integer = oldestIndex + offset
        If index >= _maxSamples Then index -= _maxSamples

        Select Case seriesIndex
            Case 0
                value = _primaryValues(index)
            Case 1
                value = _secondaryValues(index)
            Case Else
                value = _thirdValues(index)
        End Select
        Return True
    End Function

    Private Sub AppendSampleUnsafe(primaryValue As Double, secondaryValue As Double, thirdValue As Double)
        _primaryValues(_writeIndex) = primaryValue
        _secondaryValues(_writeIndex) = secondaryValue
        _thirdValues(_writeIndex) = thirdValue
        _writeIndex += 1
        If _writeIndex >= _maxSamples Then _writeIndex = 0

        If _sampleCount < _maxSamples Then
            _sampleCount += 1
        Else
            _firstSampleIndex += 1L
        End If
        _nextSampleIndex += 1L
    End Sub

    Private Sub ClearDataUnsafe()
        Array.Clear(_primaryValues, 0, _primaryValues.Length)
        Array.Clear(_secondaryValues, 0, _secondaryValues.Length)
        Array.Clear(_thirdValues, 0, _thirdValues.Length)
        _sampleCount = 0
        _writeIndex = 0
        _firstSampleIndex = 0L
        _nextSampleIndex = 0L
    End Sub

    Private Sub ReallocateDataBuffers(capacity As Integer)
        Dim primary() As Double = Nothing
        Dim secondary() As Double = Nothing
        Dim third() As Double = Nothing

        SyncLock _dataLock
            primary = New Double(capacity - 1) {}
            secondary = New Double(capacity - 1) {}
            third = New Double(capacity - 1) {}

            If _primaryValues IsNot Nothing Then
                Dim keepCount As Integer = Math.Min(_sampleCount, capacity)
                Dim start As Long = Math.Max(_firstSampleIndex, _nextSampleIndex - keepCount)
                For i As Integer = 0 To keepCount - 1
                    Dim p As Double
                    Dim s As Double
                    Dim t As Double
                    If TryGetValueUnsafe(0, start + i, p) Then primary(i) = p
                    If TryGetValueUnsafe(1, start + i, s) Then secondary(i) = s
                    If TryGetValueUnsafe(2, start + i, t) Then third(i) = t
                Next
                _firstSampleIndex = start
                _nextSampleIndex = start + keepCount
                _sampleCount = keepCount
            Else
                _sampleCount = 0
                _firstSampleIndex = 0L
                _nextSampleIndex = 0L
            End If

            _maxSamples = capacity
            _primaryValues = primary
            _secondaryValues = secondary
            _thirdValues = third
            _writeIndex = If(_sampleCount = capacity, 0, _sampleCount)
        End SyncLock

        If _visibleSamples > capacity Then _visibleSamples = capacity
    End Sub

    Private Sub UpdateTargetView()
        If IsHandleCreated AndAlso InvokeRequired Then
            Try
                BeginInvoke(New MethodInvoker(AddressOf UpdateTargetView))
            Catch ex As ObjectDisposedException
            Catch ex As InvalidOperationException
            End Try
            Return
        End If

        Dim target As Double = 0.0R
        SyncLock _dataLock
            If _followLatest AndAlso _sampleCount > 0 Then
                target = _nextSampleIndex - _visibleSamples
            ElseIf Not _followLatest Then
                target = Math.Max(0.0R, _targetViewStart)
            Else
                target = -(_visibleSamples - 1.0R)
            End If
        End SyncLock
        _targetViewStart = target

        If _smoothScrolling Then
            StartAnimation()
        Else
            _viewStart = _targetViewStart
        End If
    End Sub

    Private Sub StartAnimation()
        If _viewStart = _targetViewStart Then
            _animationTimer.Stop()
            Return
        End If
        If _lastAnimationTime Is Nothing Then _lastAnimationTime = Stopwatch.StartNew()
        _lastAnimationTime.Restart()
        _animationTimer.Start()
    End Sub

    Private Sub RequestRedraw()
        If IsDisposed OrElse Not IsHandleCreated Then Return
        If InvokeRequired Then
            Try
                BeginInvoke(New MethodInvoker(AddressOf Invalidate))
            Catch ex As ObjectDisposedException
            Catch ex As InvalidOperationException
            End Try
        Else
            Invalidate()
        End If
    End Sub

    Private Sub RefreshDpi()
        Dim dpi As Integer = 96
        If IsHandleCreated Then
            Try
                dpi = CInt(NativeMethods.GetDpiForWindow(Handle))
            Catch ex As EntryPointNotFoundException
                dpi = DeviceDpi
            Catch ex As DllNotFoundException
                dpi = DeviceDpi
            Catch ex As Win32Exception
                dpi = DeviceDpi
            End Try
        ElseIf DeviceDpi > 0 Then
            dpi = DeviceDpi
        End If
        If dpi <= 0 Then dpi = 96
        _dpiX = dpi
        _dpiY = dpi
    End Sub

    Private Function GetClientPixelSize() As Size
        If Not IsHandleCreated Then Return Size.Empty
        Try
            Dim rect As NativeRect = NativeMethods.GetClientRect(Handle)
            Return New Size(Math.Max(0, rect.Right - rect.Left), Math.Max(0, rect.Bottom - rect.Top))
        Catch ex As Win32Exception
            Return ClientSize
        End Try
    End Function

    Private Shared Function ToColor4(color As DrawingColor) As D2DColor4
        Const ByteToUnit As Single = 1.0F / 255.0F
        Return New D2DColor4(color.R * ByteToUnit,
                              color.G * ByteToUnit,
                              color.B * ByteToUnit,
                              color.A * ByteToUnit)
    End Function

    Private Sub DisposeRenderTarget()
        If _renderTarget IsNot Nothing Then
            _renderTarget.Dispose()
            _renderTarget = Nothing
        End If
        _clientPixelSize = Size.Empty
        DisposeBrushes()
    End Sub

    Private Sub DisposeDeviceResources()
        DisposeRenderTarget()
        DisposeTextFormats()
        If _writeFactory IsNot Nothing Then
            _writeFactory.Dispose()
            _writeFactory = Nothing
        End If
        If _factory IsNot Nothing Then
            _factory.Dispose()
            _factory = Nothing
        End If
    End Sub

    Private Sub DisposeBrushes()
        DisposeBrush(_gridBrush)
        DisposeBrush(_axisBrush)
        DisposeBrush(_textBrush)
        DisposeBrush(_primaryBrush)
        DisposeBrush(_secondaryBrush)
        DisposeBrush(_thirdBrush)
        _gridBrush = Nothing
        _axisBrush = Nothing
        _textBrush = Nothing
        _primaryBrush = Nothing
        _secondaryBrush = Nothing
        _thirdBrush = Nothing
    End Sub

    Private Shared Sub DisposeBrush(ByRef brush As ID2D1SolidColorBrush)
        If brush IsNot Nothing Then brush.Dispose()
        brush = Nothing
    End Sub

    Private Sub DisposeTextFormats()
        DisposeTextFormat(_axisLabelLeftFormat)
        DisposeTextFormat(_axisLabelRightFormat)
        DisposeTextFormat(_axisTitleFormat)
        DisposeTextFormat(_chartTitleFormat)
        DisposeTextFormat(_xTitleFormat)
        _axisLabelLeftFormat = Nothing
        _axisLabelRightFormat = Nothing
        _axisTitleFormat = Nothing
        _chartTitleFormat = Nothing
        _xTitleFormat = Nothing
    End Sub

    Private Shared Sub DisposeTextFormat(ByRef textFormat As IDWriteTextFormat)
        If textFormat IsNot Nothing Then textFormat.Dispose()
        textFormat = Nothing
    End Sub

    Protected Overrides Sub Dispose(disposing As Boolean)
        If disposing Then
            _animationTimer.Stop()
            _animationTimer.Dispose()
            DisposeDeviceResources()
        End If
        MyBase.Dispose(disposing)
    End Sub
End Class
