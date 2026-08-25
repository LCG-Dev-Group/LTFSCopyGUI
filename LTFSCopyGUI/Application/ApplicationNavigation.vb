Imports System
Imports System.Collections.Generic
Imports System.Windows.Forms
Imports Serilog
Imports Serilog.Context

''' <summary>
''' Keeps normal window navigation inside the current LCG process.
''' A process restart is used only when the requested operation needs UAC.
''' </summary>
Public Module ApplicationNavigation
    Public Sub ShowConfigurator()
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(ApplicationNavigation))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Navigation")
                Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "WindowOpen")
                    Log.Information("Configurator window navigation requested.")
                End Using
            End Using
        End Using
        If Not EnsureAdministrator("configurator") Then Return
        ShowAndActivate(LTFSConfigurator)
    End Sub

    Public Sub ShowChangerTool()
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(ApplicationNavigation))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Navigation")
                Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "WindowOpen")
                    Log.Information("Changer window navigation requested.")
                End Using
            End Using
        End Using
        If Not EnsureAdministrator("changer") Then Return
        ShowAndActivate(ChangerTool)
    End Sub

    Public Sub ShowTapeCopy()
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(ApplicationNavigation))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Navigation")
                Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "WindowOpen")
                    Log.Information("Tape copy window navigation requested.")
                End Using
            End Using
        End Using
        If Not EnsureAdministrator("tapecopy") Then Return
        ShowAndActivate(TapeCopy)
    End Sub
    Public Sub ShowWriterIntraProcess(tapeDrive As String, Optional offlineMode As Boolean = False)
        If String.IsNullOrWhiteSpace(tapeDrive) Then
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(ApplicationNavigation))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Navigation")
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Error")
                        Log.Warning("Writer navigation was ignored because no tape drive was supplied.")
                    End Using
                End Using
            End Using
            Return
        End If
        Dim normalizedTapeDrive = NormalizeTapeDrive(tapeDrive)
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(ApplicationNavigation))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Navigation")
                Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "WindowOpen")
                    Log.Information("Writer window navigation requested. TapeDrive={TapeDrive} NormalizedTapeDrive={NormalizedTapeDrive} OfflineMode={OfflineMode}.",
                                    tapeDrive,
                                    normalizedTapeDrive,
                                    offlineMode)
                End Using
            End Using
        End Using
        If Not EnsureAdministrator("writer", normalizedTapeDrive, If(offlineMode, "offline", String.Empty)) Then Return

        Dim writer As New LTFSWriter With {
            .TapeDrive = normalizedTapeDrive,
            .OfflineMode = offlineMode
        }
        writer.Show()
        writer.BringToFront()
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(ApplicationNavigation))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Navigation")
                Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "WindowOpen")
                    Log.Information("Writer window opened. TapeDrive={TapeDrive} OfflineMode={OfflineMode}.", normalizedTapeDrive, offlineMode)
                End Using
            End Using
        End Using
    End Sub

    Public Sub ShowWriter(tapeDrive As String, Optional offlineMode As Boolean = False)
        If String.IsNullOrWhiteSpace(tapeDrive) Then
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(ApplicationNavigation))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Navigation")
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Error")
                        Log.Warning("Writer navigation was ignored because no tape drive was supplied.")
                    End Using
                End Using
            End Using
            Return
        End If
        Dim normalizedTapeDrive = NormalizeTapeDrive(tapeDrive)
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(ApplicationNavigation))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Navigation")
                Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "WindowOpen")
                    Log.Information("Writer window navigation requested. TapeDrive={TapeDrive} NormalizedTapeDrive={NormalizedTapeDrive} OfflineMode={OfflineMode}.",
                                    tapeDrive,
                                    normalizedTapeDrive,
                                    offlineMode)
                End Using
            End Using
        End Using
        If Not EnsureAdministrator("writer", normalizedTapeDrive, If(offlineMode, "offline", String.Empty)) Then Return

        Dim sessionId = $"writer-{Guid.NewGuid().ToString("N").Substring(0, 8)}"
        Dim writerProcess = ApplicationElevation.StartWriterProcess(normalizedTapeDrive, sessionId, offlineMode)
        If writerProcess Is Nothing Then Return

        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(ApplicationNavigation))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Navigation")
                Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "WindowOpen")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", sessionId)
                        Log.Information("Writer process requested for tape drive. TapeDrive={TapeDrive} OfflineMode={OfflineMode} ProcessId={ProcessId}.",
                                        normalizedTapeDrive,
                                        offlineMode,
                                        writerProcess.Id)
                    End Using
                End Using
            End Using
        End Using
    End Sub

    ''' <summary>
    ''' Restores the normal parent window for a UAC-restarted process, then
    ''' opens the requested child window after the parent has been displayed.
    ''' </summary>
    Public Sub ScheduleStartupNavigation(route As String, routeArguments As IEnumerable(Of String))
        Dim parent As Form = Form1
        Dim handler As EventHandler = Nothing
        handler = Sub(sender As Object, e As EventArgs)
                      RemoveHandler parent.Shown, handler
                      Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(ApplicationNavigation))
                          Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Navigation")
                              Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "StartupRoute")
                                  Log.Information("Startup route is being opened after the parent window became visible. Route={Route}.", route)
                              End Using
                          End Using
                      End Using
                      ShowRoute(route, routeArguments)
                  End Sub
        AddHandler parent.Shown, handler
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(ApplicationNavigation))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Navigation")
                Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "StartupRoute")
                    Log.Information("Startup route scheduled. Route={Route}.", route)
                End Using
            End Using
        End Using
    End Sub

    Private Function EnsureAdministrator(route As String, ParamArray routeArguments() As String) As Boolean
        If ApplicationElevation.IsAdministrator Then
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(ApplicationNavigation))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Navigation")
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "PrivilegeCheck")
                        Log.Information("Administrator check passed for navigation route. Route={Route}.", route)
                    End Using
                End Using
            End Using
            Return True
        End If

        Dim arguments As New List(Of String) From {"-open", route}
        If routeArguments IsNot Nothing Then
            For Each routeArgument In routeArguments
                If Not String.IsNullOrEmpty(routeArgument) Then arguments.Add(routeArgument)
            Next
        End If

        Dim elevationStarted = ApplicationElevation.StartElevated(arguments)
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(ApplicationNavigation))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Navigation")
                Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "PrivilegeCheck")
                    Log.Information("Administrator check failed; elevation was requested. Route={Route} ElevationStarted={ElevationStarted}.", route, elevationStarted)
                End Using
            End Using
        End Using
        If elevationStarted Then
            ApplicationElevation.ExitCurrentProcess()
        End If
        Return False
    End Function

    Private Function NormalizeTapeDrive(tapeDrive As String) As String
        If String.IsNullOrWhiteSpace(tapeDrive) Then Return String.Empty

        If tapeDrive.StartsWith("TAPE") Then
            Return "\\." & tapeDrive
        ElseIf tapeDrive.StartsWith("\\.") Then
            Return tapeDrive
        ElseIf tapeDrive = Val(tapeDrive).ToString Then
            Return "\\.\TAPE" & tapeDrive
        End If

        Return tapeDrive
    End Function

    Private Sub ShowRoute(route As String, routeArguments As IEnumerable(Of String))
        Dim arguments As New List(Of String)
        If routeArguments IsNot Nothing Then arguments.AddRange(routeArguments)

        Select Case If(route, String.Empty).ToLowerInvariant()
            Case "configurator"
                ShowConfigurator()
            Case "changer"
                ShowChangerTool()
            Case "tapecopy"
                ShowTapeCopy()
            Case "writer"
                If arguments.Count = 0 Then
                    Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(ApplicationNavigation))
                        Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Navigation")
                            Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Error")
                                Log.Error("Startup writer route was ignored because the tape drive argument was missing.")
                            End Using
                        End Using
                    End Using
                    Return
                End If
                Dim offlineMode = arguments.Count > 1 AndAlso
                                   String.Equals(arguments(1), "offline", StringComparison.OrdinalIgnoreCase)
                ShowWriter(arguments(0), offlineMode)
            Case Else
                Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(ApplicationNavigation))
                    Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Navigation")
                        Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Error")
                            Log.Error("Unknown startup navigation route was requested. Route={Route}.", route)
                        End Using
                    End Using
                End Using
        End Select
    End Sub

    Private Sub ShowAndActivate(form As Form)
        form.Show()
        If form.WindowState = FormWindowState.Minimized Then form.WindowState = FormWindowState.Normal
        form.BringToFront()
        form.Activate()
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(ApplicationNavigation))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "Navigation")
                Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "WindowActivated")
                    Log.Information("Window shown and activated. WindowType={WindowType}.", form.GetType().Name)
                End Using
            End Using
        End Using
    End Sub
End Module
