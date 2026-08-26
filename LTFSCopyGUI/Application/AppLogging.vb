Imports System
Imports System.Diagnostics
Imports System.IO
Imports System.Text
Imports Serilog
Imports Serilog.Configuration
Imports Serilog.Core
Imports Serilog.Events
Imports Serilog.Expressions
Imports Serilog.Formatting.Compact
Imports Serilog.Templates

''' <summary>
''' Configures the process-wide Serilog logger.
''' </summary>
''' <remarks>
''' Application code deliberately writes through Serilog.Log. This module only
''' owns the one-time pipeline setup and lifecycle; it is not a logger facade.
''' </remarks>
Public Module AppLogging
    Private Const NormalLogFileSize As Long = 128L * 1024L * 1024L
    Private Const ScsiLogFileSize As Long = 1024L * 1024L * 1024L
    Private Const AsyncBufferSize As Integer = 10000

    Private ReadOnly StateLock As New Object()
    Private ReadOnly InformationLevel As New LoggingLevelSwitch(LogEventLevel.Warning)
    Private _initialized As Boolean
    Private _processExitRegistered As Boolean
    Private _runId As String = String.Empty
    Private _logDirectory As String = String.Empty

    Public ReadOnly Property RunId As String
        Get
            SyncLock StateLock
                Return _runId
            End SyncLock
        End Get
    End Property

    Public Sub Initialize(requestedDirectory As String,
                          informationEnabled As Boolean,
                          Optional sessionId As String = Nothing)
        SyncLock StateLock
            InformationLevel.MinimumLevel = If(informationEnabled,
                                               LogEventLevel.Information,
                                               LogEventLevel.Warning)

            If _initialized Then Return

            _logDirectory = ResolveLogDirectory(requestedDirectory)
            _runId = If(String.IsNullOrWhiteSpace(sessionId),
                        Guid.NewGuid().ToString("N").Substring(0, 8),
                        sessionId.Trim())

            Dim sessionLog As Boolean = Not String.IsNullOrWhiteSpace(sessionId)
            Dim normalLogPath As String
            Dim scsiLogDirectory As String
            Dim scsiLogPath As String
            Dim sharedFile As Boolean
            Dim rollingInterval As RollingInterval

            If sessionLog Then
                Dim fileStem = SanitizeFileName(_runId)
                normalLogPath = Path.Combine(_logDirectory, $"{fileStem}.log")
                scsiLogDirectory = _logDirectory
                scsiLogPath = Path.Combine(_logDirectory, $"{fileStem}-scsi.jsonl")
                sharedFile = False
                rollingInterval = RollingInterval.Infinite
            Else
                normalLogPath = Path.Combine(_logDirectory, "application-.log")
                scsiLogDirectory = Path.Combine(_logDirectory, "scsi")
                scsiLogPath = Path.Combine(scsiLogDirectory, "scsi-.jsonl")
                sharedFile = True
                rollingInterval = RollingInterval.Day
            End If

            Directory.CreateDirectory(scsiLogDirectory)

            Dim normalFormatter As New ExpressionTemplate(
                "{@t:yyyy-MM-dd HH:mm:ss.fff zzz} [{@l:u3}]" &
                "{#if SourceContext is not null} [{SourceContext}]{#end}" &
                "{#if Category is not null} [category={Category}]{#end}" &
                "{#if EventType is not null} [event={EventType}]{#end}" &
                "{#if SessionId is not null} [session={SessionId}]{#end}" &
                "{#if ThreadId is not null} [thread={ThreadId}" &
                "{#if ThreadName is not null}:{ThreadName}{#end}]{#end} " &
                "{@m}" & vbCrLf &
                "{#if @x is not null}{@x}" & vbCrLf & "{#end}")
            Dim scsiFormatter As New CompactJsonFormatter()

            Dim configuration As New LoggerConfiguration()
            configuration.MinimumLevel.ControlledBy(InformationLevel)
            configuration.Enrich.FromLogContext()
            configuration.Enrich.WithThreadId()
            configuration.Enrich.WithThreadName()
            configuration.Enrich.WithProperty("Application", "LTFSCopyGUI")
            configuration.Enrich.WithProperty("RunId", _runId)
            configuration.Enrich.WithProperty("ProcessId", Process.GetCurrentProcess().Id)

            ' Normal events are human-readable. SCSI console events are routed
            ' exclusively to the JSONL sink below by an expression filter.
            configuration.WriteTo.Logger(
                Sub(subLogger As LoggerConfiguration)
                    subLogger.Filter.ByExcluding("LogStream = 'scsi-console'")
                    subLogger.WriteTo.Async(
                        Sub(asyncSink As LoggerSinkConfiguration)
                            asyncSink.File(
                                normalFormatter,
                                normalLogPath,
                                rollingInterval:=rollingInterval,
                                rollOnFileSizeLimit:=True,
                                fileSizeLimitBytes:=NormalLogFileSize,
                                retainedFileCountLimit:=31,
                                shared:=sharedFile,
                                flushToDiskInterval:=TimeSpan.FromSeconds(1),
                                encoding:=New UTF8Encoding(False))
                        End Sub,
                        bufferSize:=AsyncBufferSize,
                        blockWhenFull:=False)
                End Sub)

            ' Keep the detailed SCSI payload machine-readable and one event per
            ' line. This makes large command traces safe to ingest and query.
            configuration.WriteTo.Logger(
                Sub(subLogger As LoggerConfiguration)
                    subLogger.Filter.ByIncludingOnly("LogStream = 'scsi-console'")
                    subLogger.WriteTo.Async(
                        Sub(asyncSink As LoggerSinkConfiguration)
                            asyncSink.File(
                                scsiFormatter,
                                scsiLogPath,
                                rollingInterval:=rollingInterval,
                                rollOnFileSizeLimit:=True,
                                fileSizeLimitBytes:=ScsiLogFileSize,
                                retainedFileCountLimit:=31,
                                shared:=sharedFile,
                                flushToDiskInterval:=TimeSpan.FromSeconds(1),
                                encoding:=New UTF8Encoding(False))
                        End Sub,
                        bufferSize:=AsyncBufferSize,
                        blockWhenFull:=False)
                End Sub)

            Log.Logger = configuration.CreateLogger()
            _initialized = True

            If Not _processExitRegistered Then
                AddHandler AppDomain.CurrentDomain.ProcessExit, AddressOf OnProcessExit
                _processExitRegistered = True
            End If
        End SyncLock

        ' Preserve the old force-on-start behavior: even when informational
        ' logging is disabled, the startup event proves which directory owns
        ' the active pipeline and creates the normal log file.
        Dim configuredLevel = InformationLevel.MinimumLevel
        Try
            InformationLevel.MinimumLevel = LogEventLevel.Information
            Log.Information("Logging initialized. LogDirectory={LogDirectory}", _logDirectory)
        Finally
            InformationLevel.MinimumLevel = configuredLevel
        End Try
    End Sub

    Public Sub SetInformationEnabled(value As Boolean)
        InformationLevel.MinimumLevel = If(value,
                                           LogEventLevel.Information,
                                           LogEventLevel.Warning)
    End Sub

    Private Sub OnProcessExit(sender As Object, e As EventArgs)
        Log.CloseAndFlush()
    End Sub

    Private Function ResolveLogDirectory(requestedDirectory As String) As String
        Dim fallbackRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LTFSCopyGUI",
            "log")
        Dim fallback = fallbackRoot
        If Not String.IsNullOrWhiteSpace(requestedDirectory) AndAlso
           String.Equals(Path.GetFileName(requestedDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                         "writer",
                         StringComparison.OrdinalIgnoreCase) Then
            fallback = Path.Combine(fallbackRoot, "writer")
        End If

        For Each candidate In {requestedDirectory, fallback}
            If String.IsNullOrWhiteSpace(candidate) Then Continue For
            If CanWriteDirectory(candidate) Then Return candidate
        Next

        Return If(String.IsNullOrWhiteSpace(requestedDirectory), fallback, requestedDirectory)
    End Function

    Private Function SanitizeFileName(value As String) As String
        Dim result As New StringBuilder(If(value, String.Empty))
        For i As Integer = 0 To result.Length - 1
            If Array.IndexOf(Path.GetInvalidFileNameChars(), result(i)) >= 0 Then
                result(i) = "_"c
            End If
        Next
        If result.Length = 0 Then Return "session"
        Return result.ToString()
    End Function

    Private Function CanWriteDirectory(directoryPath As String) As Boolean
        Dim probePath As String = Nothing
        Try
            Directory.CreateDirectory(directoryPath)
            probePath = Path.Combine(directoryPath, $".log-probe-{Guid.NewGuid():N}.tmp")
            Using probe As New FileStream(
                probePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1,
                FileOptions.DeleteOnClose)
                probe.WriteByte(0)
            End Using
            Return True
        Catch
            Return False
        Finally
            If probePath IsNot Nothing Then
                Try
                    If File.Exists(probePath) Then File.Delete(probePath)
                Catch
                End Try
            End If
        End Try
    End Function
End Module
