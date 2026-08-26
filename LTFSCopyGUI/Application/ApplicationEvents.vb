Imports System.ComponentModel
Imports System.Collections.Generic
Imports System.IO
Imports System.Runtime.InteropServices
Imports Microsoft.VisualBasic.ApplicationServices
Imports LTFSCopyGUI.Native

Namespace My
    ' 以下事件可用于 MyApplication: 
    ' Startup:应用程序启动时在创建启动窗体之前引发。
    ' Shutdown:在关闭所有应用程序窗体后引发。如果应用程序非正常终止，则不会引发此事件。
    ' UnhandledException:在应用程序遇到未经处理的异常时引发。
    ' StartupNextInstance:在启动单实例应用程序且应用程序已处于活动状态时引发。 
    ' NetworkAvailabilityChanged:在连接或断开网络连接时引发。
    <TypeConverter(GetType(ExpandableObjectConverter))>
    Partial Friend Class MyApplication
        Public Shared Sub InitConsole()
            Dim attachResult As NativeCallResult = NativeMethods.AttachConsole(-1)
            If Not attachResult.Succeeded Then
                Dim allocResult As NativeCallResult = NativeMethods.AllocConsole()
                allocResult.ThrowIfFailed("AllocConsole failed.")
            Else
                Dim CurrentLine As Integer = Console.CursorTop
                Console.SetCursorPosition(0, Console.CursorTop)
                Console.Write("".PadRight(Console.WindowWidth))
                Console.SetCursorPosition(0, CurrentLine - 1)
            End If
        End Sub
        Public Sub CloseConsole()
            SendKeys.SendWait("{ENTER}")
            Dim freeResult As NativeCallResult = NativeMethods.FreeConsole()
            freeResult.ThrowIfFailed("FreeConsole failed.")
        End Sub
        Public Function CheckUAC(e As StartupEventArgs) As Boolean
            If ApplicationElevation.IsAdministrator Then Return True

            Dim elevatedProcessStarted = ApplicationElevation.StartElevated(e.CommandLine)
            e.Cancel = True

            If elevatedProcessStarted Then
                Serilog.Log.Information("Unelevated startup canceled after launching the elevated application process.")
            Else
                Serilog.Log.Information("Startup canceled because the elevated application process was not started.")
            End If
            ApplicationElevation.ExitCurrentProcess()
            Return False
        End Function

        Private Shared Function NormalizeTapeDrive(tapeDrive As String) As String
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

        Private Sub MyApplication_Startup(sender As Object, e As StartupEventArgs) Handles Me.Startup
            If e.CommandLine.Count <> 0 Then
                Dim param As String = e.CommandLine(0)
                If param.StartsWith("/") Then param = "-" & param.TrimStart("/"c)
                Select Case param
                    Case "-s", "-open", "-writer-process", "-t", "-f", "-c", "-l", "-copy"
                    Case "-rb", "-wb", "-raw", "-mkltfs"
                        If Not CheckUAC(e) Then Return
                        InitConsole()
                    Case Else
                        InitConsole()
                End Select
            End If
            Dim writerProcessArgumentIndex As Integer = -1
            For commandIndex As Integer = 0 To e.CommandLine.Count - 1
                Dim commandArgument As String = e.CommandLine(commandIndex)
                If commandArgument.StartsWith("/") Then commandArgument = "-" & commandArgument.TrimStart("/"c)
                If String.Equals(commandArgument, "-writer-process", StringComparison.OrdinalIgnoreCase) Then
                    writerProcessArgumentIndex = commandIndex
                    Exit For
                End If
            Next
            Dim logDirectory As String = Path.Combine(Windows.Forms.Application.StartupPath, "log")
            Dim writerSessionId As String = Nothing
            If writerProcessArgumentIndex >= 0 Then
                logDirectory = Path.Combine(logDirectory, "writer")
                If writerProcessArgumentIndex + 2 < e.CommandLine.Count Then
                    writerSessionId = e.CommandLine(writerProcessArgumentIndex + 2)
                End If
            End If
            AppLogging.Initialize(logDirectory, Settings.LTFSWriter_LogEnabled, writerSessionId)
            If Not Directory.Exists(Settings.cfgPath) Then Directory.CreateDirectory(Settings.cfgPath)
            '旧设定迁移
            If File.Exists(Path.Combine(Windows.Forms.Application.StartupPath, "lang.ini")) Then
                Try
                    File.Move(Path.Combine(Windows.Forms.Application.StartupPath, "lang.ini"), Settings.langcfgFile)
                Catch
                End Try
            End If
            If File.Exists(Path.Combine(Windows.Forms.Application.StartupPath, "license.key")) Then
                Try
                    File.Move(Path.Combine(Windows.Forms.Application.StartupPath, "license.key"), Settings.licenseFile)
                Catch
                End Try
            End If
            Dim drvSettingUpdated As Boolean = False
            If File.Exists(Path.Combine(Windows.Forms.Application.StartupPath, "driver.txt")) Then
                Try
                    Dim driverTags As String() = File.ReadAllLines(Path.Combine(Windows.Forms.Application.StartupPath, "driver.txt"))
                    For Each line As String In driverTags
                        Dim result = line.Split({";", ",", vbTab}, StringSplitOptions.None)
                        TapeUtils.TagDictionary.Add(result(0), result(1))
                    Next
                    drvSettingUpdated = True
                    File.Delete(Path.Combine(Windows.Forms.Application.StartupPath, "driver.txt"))
                Catch
                End Try
            End If
            If File.Exists(Path.Combine(Windows.Forms.Application.StartupPath, "blocklen.ini")) Then
                Dim blval As Integer = Integer.Parse(File.ReadAllText(Path.Combine(Windows.Forms.Application.StartupPath, "blocklen.ini")))
                If blval > 0 Then TapeUtils.GlobalBlockLimit = blval
                drvSettingUpdated = True
                File.Delete(Path.Combine(Windows.Forms.Application.StartupPath, "blocklen.ini"))
            End If
            If drvSettingUpdated Then File.WriteAllText(Settings.driveSettingFile, (New TapeUtils()).GetSerializedText())
            '读取独立配置文件
            If File.Exists(Settings.langcfgFile) Then
                Try
                    Dim lang As String = File.ReadAllText(Settings.langcfgFile)
                    Threading.Thread.CurrentThread.CurrentCulture = New Globalization.CultureInfo(lang)
                    Threading.Thread.CurrentThread.CurrentUICulture = New Globalization.CultureInfo(lang)
                Catch
                End Try
            End If
            If File.Exists(Settings.driveSettingFile) Then
                Try
                    TapeUtils.FromFile(Settings.driveSettingFile)
                Catch
                End Try
            End If
            TapeUtils.DriverTypeSetting = Settings.TapeUtils_DriverType
            Settings.Application_License = Resources.StrDefaultLicense
            If File.Exists(Settings.licenseFile) Then
                Dim rsa As New Security.Cryptography.RSACryptoServiceProvider()

                If File.Exists(Settings.licKeyFile) Then
                    rsa.FromXmlString(File.ReadAllText(Settings.licKeyFile))
                Else
                    rsa.FromXmlString("<RSAKeyValue><Modulus>4q9IKAIqJVyJteY0L7mCVnuBvNv+ciqlJ79X8RdTOzAOsuwTrmdlXIJn0dNsY0EdTNQrJ+idmAcMzIDX65ZnQzMl9x2jfvLZfeArqzNYERkq0jpa/vwdk3wfqEUKhBrGzy14gt/tawRXp3eBGZSEN++Wllh8Zqf8Huiu6U+ZO9k=</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>")
                End If
                Dim lic_string = File.ReadAllText(Settings.licenseFile)

                Try
                    Dim LicStr As String() = lic_string.Split({vbCr, vbLf}, StringSplitOptions.RemoveEmptyEntries)
                    Dim strBody As String = LicStr(0)
                    Dim strSign As String = LicStr(1)
                    Dim bSign As Byte() = Convert.FromBase64String(strSign)
                    Dim bLicStr As Byte() = Convert.FromBase64String(strBody)
                    'lic_string = System.Text.Encoding.UTF8.GetString(rsa.Decrypt(key, False))
                    If rsa.VerifyData(bLicStr, "SHA256", bSign) Then
                        Settings.Application_License = Text.Encoding.UTF8.GetString(bLicStr)
                    Else
                        Throw New Exception()
                    End If
                Catch ex As Exception
                    If rsa.PublicOnly Then
                        MessageBox.Show(New Form With {.TopMost = True}, Resources.StrLicenseInvalid)
                    Else
                        Settings.Application_License = lic_string
                        Dim bLicStr As Byte() = Text.Encoding.UTF8.GetBytes(lic_string)
                        Dim bSign As Byte() = rsa.SignData(bLicStr, "SHA256")
                        Dim strBody As String = Convert.ToBase64String(bLicStr)
                        Dim strSign As String = Convert.ToBase64String(bSign)
                        File.WriteAllText(Settings.licenseFile, $"{strBody}{vbCrLf}{strSign}")
                    End If
                End Try
            End If
            DisplayHelper.DisplayFont = Settings.Application_BaseFont
            If e.CommandLine.Count = 0 Then

            Else
                Dim param() As String = e.CommandLine.ToArray()
                Dim IndexRead As Boolean = True
                For i As Integer = 0 To param.Count - 1
                    If param(i).StartsWith("/") Then param(i) = "-" & param(i).TrimStart("/"c)

                    Select Case param(i)
                        Case "-s"
                            IndexRead = False
                        Case "-open"
                            If i >= param.Count - 1 Then Exit For
                            If Not CheckUAC(e) Then Return

                            Dim routeArguments As New List(Of String)
                            For routeIndex As Integer = i + 2 To param.Count - 1
                                routeArguments.Add(param(routeIndex))
                            Next
                            ApplicationNavigation.ScheduleStartupNavigation(param(i + 1), routeArguments)
                            MainForm = Form1
                            Exit For
                        Case "-writer-process"
                            If Not CheckUAC(e) Then Return
                            If i >= param.Count - 1 Then Exit For

                            Dim tapeDrive As String = NormalizeTapeDrive(param(i + 1))
                            Dim sessionId As String = Nothing
                            If i + 2 < param.Count Then sessionId = param(i + 2)
                            Dim offlineMode As Boolean = i + 3 < param.Count AndAlso
                                                         String.Equals(param(i + 3), "offline", StringComparison.OrdinalIgnoreCase)

                            Dim writer As New LTFSWriter(sessionId) With {
                                .TapeDrive = tapeDrive,
                                .OfflineMode = offlineMode
                            }
                            If offlineMode Then writer.ExtraPartitionCount = 1
                            MainForm = writer
                            Exit For
                        Case "-t"
                            If Not CheckUAC(e) Then Return
                            If i < param.Count - 1 Then
                                Dim TapeDrive As String = NormalizeTapeDrive(param(i + 1))
                                Dim routeArguments As New List(Of String) From {TapeDrive}
                                If Not IndexRead Then routeArguments.Add("offline")
                                ApplicationNavigation.ScheduleStartupNavigation("writer", routeArguments)
                                MainForm = Form1
                                Exit For
                            End If
                        Case "-f"
                            If i < param.Count - 1 Then
                                Dim indexFile As String = param(i + 1).TrimStart(""""c).TrimEnd(""""c)

                                If File.Exists(indexFile) Then
                                    Dim LWF As New LTFSWriter With {.Barcode = Resources.StrIndexView, .TapeDrive = "", .OfflineMode = True}
                                    Dim OnLWFLoad As New EventHandler(Sub()
                                                                          LWF.Invoke(Sub()
                                                                                         LWF.LoadIndexFile(indexFile, True)
                                                                                         LWF.ToolStripStatusLabel1.Text = Resources.StrIndexView
                                                                                     End Sub)
                                                                          RemoveHandler LWF.Load, OnLWFLoad
                                                                      End Sub
                                        )
                                    AddHandler LWF.Load, OnLWFLoad
                                    MainForm = LWF
                                End If
                                Exit For
                            End If
                        Case "-c"
                            If Not CheckUAC(e) Then Return
                            MainForm = LTFSConfigurator
                            Exit For
                        Case "-l"
                            If Not CheckUAC(e) Then Return
                            MainForm = ChangerTool
                            Exit For
                        Case "-rb"
                            If Not CheckUAC(e) Then Return
                            If i < param.Count - 1 Then
                                Dim TapeDrive As String = param(i + 1)
                                If TapeDrive.StartsWith("TAPE") Then
                                    TapeDrive = "\\.\" & TapeDrive
                                ElseIf TapeDrive.StartsWith("\\.\") Then
                                    'Do Nothing
                                ElseIf TapeDrive = Val(TapeDrive).ToString Then
                                    TapeDrive = "\\.\TAPE" & TapeDrive
                                Else

                                End If
                                Dim Barcode As String = TapeUtils.ReadBarcode(TapeDrive)
                                Console.WriteLine($"{TapeDrive}{vbCrLf}Barcode:{Barcode}")
                                CloseConsole()
                                End
                            End If
                        Case "-wb"
                            If Not CheckUAC(e) Then Return
                            If i < param.Count - 2 Then
                                Dim TapeDrive As String = param(i + 1)
                                If TapeDrive.StartsWith("TAPE") Then
                                    TapeDrive = "\\.\" & TapeDrive
                                ElseIf TapeDrive.StartsWith("\\.\") Then
                                    'Do Nothing
                                ElseIf TapeDrive = Val(TapeDrive).ToString Then
                                    TapeDrive = "\\.\TAPE" & TapeDrive
                                Else

                                End If
                                Dim Barcode As String = param(i + 2)
                                If TapeUtils.SetBarcode(TapeDrive, Barcode) Then
                                    Console.WriteLine($"{TapeDrive}{vbCrLf}Barcode->{TapeUtils.ReadBarcode(TapeDrive)}")
                                Else
                                    Console.WriteLine($"{TapeDrive}{vbCrLf}{Resources.StrBCSFail}")
                                End If
                                CloseConsole()
                                End
                            End If
                        Case "-raw"
                            If Not CheckUAC(e) Then Return
                            If i < param.Count - 4 Then
                                Dim TapeDrive As String = param(i + 1)
                                If TapeDrive.StartsWith("TAPE") Then
                                    TapeDrive = "\\.\" & TapeDrive
                                ElseIf TapeDrive.StartsWith("\\.\") Then
                                    'Do Nothing
                                ElseIf TapeDrive = Val(TapeDrive).ToString Then
                                    TapeDrive = "\\.\TAPE" & TapeDrive
                                Else

                                End If
                                Dim cdb As Byte() = IOManager.HexStringToByteArray(param(i + 2))
                                Dim data As Byte() = IOManager.HexStringToByteArray(param(i + 3))
                                Dim dataDir As Byte = Byte.Parse(param(i + 4))
                                Dim TimeOut As Integer = 60000
                                If i + 5 <= param.Length - 1 Then
                                    TimeOut = Integer.Parse(param(i + 5))
                                End If
                                Dim sense As Byte() = {}

                                If TapeUtils.SendSCSICommand(TapeDrive, cdb, data, dataDir, Function(s As Byte())
                                                                                                sense = s
                                                                                                Return True
                                                                                            End Function, TimeOut) Then
                                    Console.WriteLine($"{TapeDrive}
cdb:
{TapeUtils.Byte2Hex(cdb)}
param:
{TapeUtils.Byte2Hex(data)}
dataDir:{dataDir}

{Resources.StrSCSISucc}
sense:
{TapeUtils.Byte2Hex(sense)}
{TapeUtils.ParseSenseData(sense)}")
                                Else
                                    Console.WriteLine($"{TapeDrive}
cdb:
{TapeUtils.Byte2Hex(cdb)}
param:
{TapeUtils.Byte2Hex(data)}
dataDir:{dataDir}


{Resources.StrSCSIFail}")
                                End If

                                CloseConsole()
                                End
                            End If
                        Case "-mkltfs"
                            If Not CheckUAC(e) Then Return
                            'Console.WriteLine($"{i} {param.Count}")
                            If i < param.Count - 1 Then
                                Dim TapeDrive As String = param(i + 1)
                                If TapeDrive.StartsWith("TAPE") Then
                                    TapeDrive = "\\.\" & TapeDrive
                                ElseIf TapeDrive.StartsWith("\\.\") Then
                                    'Do Nothing
                                ElseIf TapeDrive = Val(TapeDrive).ToString Then
                                    TapeDrive = "\\.\TAPE" & TapeDrive
                                Else

                                End If
                                Dim Barcode As String = ""
                                If i + 2 <= param.Length - 1 Then
                                    Barcode = param(i + 2).Replace("""", "")
                                    If Barcode.Length > 20 Then Barcode = Barcode.Substring(0, 20)
                                End If

                                Dim VolLabel As String = ""
                                If i + 3 <= param.Length - 1 Then
                                    VolLabel = param(i + 3).Replace("""", "")
                                End If

                                Dim Partition As Byte = 1
                                If i + 4 <= param.Length - 1 Then
                                    Partition = CByte(Partition And CLng(Val(param(i + 4))))
                                End If

                                Dim Capacity As UInt16 = &HFFFF
                                If i + 5 <= param.Length - 1 Then
                                    Capacity = CUShort(CLng(Capacity) And CLng(Val(param(i + 5))))
                                End If
                                Dim BlockLen As Integer = 524288
                                If i + 6 <= param.Length - 1 Then
                                    BlockLen = CInt(Val(param(i + 6)))
                                End If
                                Dim P0Size As UInt16 = 1
                                If i + 7 <= param.Length - 1 Then
                                    P0Size = CUShort(&HFFFFL And CLng(Val(param(i + 7))))
                                End If
                                Dim P1Size As UInt16 = &HFFFF
                                If i + 8 <= param.Length - 1 Then
                                    P1Size = CUShort(&HFFFFL And CLng(Val(param(i + 8))))
                                End If

                                Dim sense As Byte() = {}
                                If TapeUtils.mkltfs(TapeDrive, Barcode, VolLabel, Partition, BlockLen, True,
                                    Sub(s As String)
                                        'ProgReport
                                        Console.WriteLine(s)
                                    End Sub,
                                    Sub(s As String)
                                        'OnFin
                                        Console.WriteLine(s)
                                    End Sub,
                                    Sub(s As String)
                                        'OnErr
                                        Console.WriteLine(s)
                                    End Sub, Capacity, P0Size, P1Size) Then
                                    Console.WriteLine(Resources.StrFormatFin)
                                Else
                                    Console.WriteLine(Resources.StrFormatError)
                                End If
                                CloseConsole()
                                End
                            End If
                        Case "-copy"
                            If Not CheckUAC(e) Then Return
                            MainForm = New TapeCopy()
                        Case "-gt"
                            If i < param.Count - 2 Then
                                Dim Num1 As Byte = Byte.Parse(param(i + 1))
                                Dim Num2 As Byte = Byte.Parse(param(i + 2))
                                Console.WriteLine($"{TapeUtils.GX256.Times(Num1, Num2)}")
                                CloseConsole()
                                End
                            End If
                        Case "-crc"
                            If i < param.Count - 1 Then
                                Dim Num1 As Byte()
                                If File.Exists(param(i + 1)) Then
                                    If param(i + 1).ToLower.EndsWith(".txt") OrElse param(i + 1).ToLower.EndsWith(".csv") OrElse param(i + 1).ToLower.EndsWith(".tsv") Then
                                        Num1 = IOManager.HexStringToByteArray(File.ReadAllText(param(i + 1)).Replace(" ", "").Replace(vbCr, "").Replace(vbLf, "").Replace(vbTab, "").Replace(",", ""))
                                    Else
                                        Num1 = File.ReadAllBytes(param(i + 1))
                                    End If
                                Else
                                    Num1 = IOManager.HexStringToByteArray(param(i + 1))
                                End If
                                Console.WriteLine($"{TapeUtils.Byte2Hex(TapeUtils.GX256.CalcCRC(Num1))}")
                                CloseConsole()
                                End
                            End If
                        Case "-lic"
                            If i < param.Count - 1 Then
                                Dim ltext As String = param(i + 1)
                                If ltext.StartsWith("""") AndAlso ltext.EndsWith("""") Then
                                    ltext = ltext.Substring(1, ltext.Length - 2)
                                End If
                                Dim rsa As New Security.Cryptography.RSACryptoServiceProvider()

                                If File.Exists(Settings.licKeyFile) Then
                                    rsa.FromXmlString(File.ReadAllText(Settings.licKeyFile))
                                Else
                                    rsa.FromXmlString("<RSAKeyValue><Modulus>4q9IKAIqJVyJteY0L7mCVnuBvNv+ciqlJ79X8RdTOzAOsuwTrmdlXIJn0dNsY0EdTNQrJ+idmAcMzIDX65ZnQzMl9x2jfvLZfeArqzNYERkq0jpa/vwdk3wfqEUKhBrGzy14gt/tawRXp3eBGZSEN++Wllh8Zqf8Huiu6U+ZO9k=</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>")
                                End If
                                If rsa.PublicOnly Then
                                    MessageBox.Show(New Form With {.TopMost = True}, Resources.StrLicenseInvalid)
                                Else
                                    Settings.Application_License = ltext
                                    Dim bLicStr As Byte() = Text.Encoding.UTF8.GetBytes(ltext)
                                    Dim bSign As Byte() = rsa.SignData(bLicStr, "SHA256")
                                    Dim strBody As String = Convert.ToBase64String(bLicStr)
                                    Dim strSign As String = Convert.ToBase64String(bSign)
                                    Console.WriteLine($"{strBody}{vbCrLf}{strSign}")
                                End If
                                CloseConsole()
                                End
                            End If
                        Case "-svc"
                            If i < param.Count - 0 Then
                                If Not CheckUAC(e) Then Return
                                Dim port As Integer = 25900
                                If i + 2 <= param.Length - 1 Then
                                    port = Integer.Parse(param(i + 2))
                                End If

                                Dim sck As New Net.Sockets.Socket(Net.Sockets.AddressFamily.InterNetwork, Net.Sockets.SocketType.Stream, Net.Sockets.ProtocolType.Tcp)
                                Dim server As New Net.IPEndPoint(Net.IPAddress.Any, port)
                                sck.Bind(server)
                                sck.Listen(0)
                                Dim objList As New Dictionary(Of Guid, Object)

                                While True
                                    Dim connect As Net.Sockets.Socket = sck.Accept()
                                    Dim rEP As Net.IPEndPoint = CType(connect.RemoteEndPoint, Net.IPEndPoint)
                                    Console.WriteLine($"In: {rEP.Address}:{rEP.Port}")
                                    Dim header(3) As Byte
                                    Dim hLength As Integer = connect.Receive(header, 4, Net.Sockets.SocketFlags.None)
                                    If hLength <> 4 Then
                                        connect.Close()
                                        Console.WriteLine("Invalid data")
                                        Continue While
                                    End If
                                    Dim length As Integer = BitConverter.ToInt32(header, 0)
                                    Dim data(length - 1) As Byte
                                    Dim dLength As Integer = connect.Receive(data, length, Net.Sockets.SocketFlags.None)
                                    If dLength <> length Then
                                        connect.Close()
                                        Console.WriteLine("Invalid data")
                                        Continue While
                                    End If
                                    Dim msg As String = Text.Encoding.UTF8.GetString(data)
                                    Console.WriteLine(msg)
                                    Console.WriteLine()
                                    Console.WriteLine($"Out: ")
                                    Dim cmd As IOManager.NetworkCommand
                                    Try
                                        cmd = IOManager.NetworkCommand.FromXML(msg)
                                        Dim result As New IOManager.NetworkCommand With {.HashCode = cmd.HashCode}
                                        Select Case cmd.CommandType
                                            Case IOManager.NetworkCommand.CommandTypeDef.SCSICommand
                                                result.CommandType = IOManager.NetworkCommand.CommandTypeDef.SCSISenseData
                                                If cmd.PayLoad.Count = 5 Then
                                                    Dim devpath As String = Text.Encoding.UTF8.GetString(cmd.PayLoad(0))
                                                    Dim cdbdata As Byte() = cmd.PayLoad(1)
                                                    Dim paramdata As Byte() = cmd.PayLoad(2)
                                                    Dim datadir As Byte = cmd.PayLoad(3)(0)
                                                    Dim timeout As Integer = BitConverter.ToInt32(cmd.PayLoad(4), 0)
                                                    Dim paramPtr As IntPtr = Marshal.AllocHGlobal(paramdata.Length)
                                                    If paramdata.Length > 0 Then Marshal.Copy(paramdata, 0, paramPtr, paramdata.Length)
                                                    Dim sensedata(63) As Byte
                                                    Dim succ As Boolean = TapeUtils.IOCtlDirect(devpath, cdbdata, paramPtr, CUInt(paramdata.Length), datadir, CUInt(timeout), sensedata)
                                                    Marshal.Copy(paramPtr, paramdata, 0, paramdata.Length)
                                                    Marshal.FreeHGlobal(paramPtr)
                                                    result.PayLoad.Add(paramdata)
                                                    result.PayLoad.Add(sensedata)
                                                    If Not succ Then result.CommandType = IOManager.NetworkCommand.CommandTypeDef.SCSIIOCtlError
                                                End If
                                            Case IOManager.NetworkCommand.CommandTypeDef.General
                                                result.CommandType = IOManager.NetworkCommand.CommandTypeDef.GeneralData
                                                Try
                                                    If cmd.PayLoad.Count >= 2 Then
                                                        Dim ItemGuid As Guid = New Guid(cmd.PayLoad(0))
                                                        Dim ItemType As String = Text.Encoding.UTF8.GetString(cmd.PayLoad(1))
                                                        If ItemGuid = Guid.Empty Then
                                                            'new
                                                            Select Case ItemType.ToLower
                                                                Case "ltfswriter"
                                                                    Dim itemID As Guid = Guid.NewGuid()
                                                                    Dim LWF As LTFSWriter
                                                                    Task.Run(Sub()
                                                                                 LWF = New LTFSWriter()
                                                                                 objList.Add(itemID, LWF)
                                                                             End Sub).Wait()
                                                                    result.PayLoad.Add(itemID.ToByteArray())
                                                                    Console.WriteLine($"New LWF {itemID.ToString()}")
                                                            End Select
                                                        ElseIf Not objList.ContainsKey(ItemGuid) Then
                                                            result.PayLoad.Add((New Guid()).ToByteArray())
                                                        Else
                                                            Select Case ItemType.ToLower
                                                                Case "ltfswriter"
                                                                    Dim item As Object = Nothing
                                                                    If Not objList.TryGetValue(ItemGuid, item) Then Exit Select
                                                                    Dim LWF As LTFSWriter = TryCast(item, LTFSWriter)
                                                                    If LWF Is Nothing Then Exit Select
                                                                    Dim Command As String = Text.Encoding.UTF8.GetString(cmd.PayLoad(2))

                                                                    Select Case Command
                                                                        Case "show"
                                                                            Task.Run(Sub()
                                                                                         LWF.ShowDialog()
                                                                                     End Sub)
                                                                            result.PayLoad.Add(Text.Encoding.UTF8.GetBytes("show OK"))
                                                                        Case "close"
                                                                            LWF.Close()
                                                                            result.PayLoad.Add(Text.Encoding.UTF8.GetBytes("close OK"))
                                                                        Case "dispose"
                                                                            LWF.Dispose()
                                                                            objList.Remove(ItemGuid)
                                                                            result.PayLoad.Add(Text.Encoding.UTF8.GetBytes("dispose OK"))
                                                                        Case "-s"
                                                                            Dim param1 As String = Text.Encoding.UTF8.GetString(cmd.PayLoad(3))
                                                                            Select Case param1.ToLower()
                                                                                Case "on"
                                                                                    IndexRead = False
                                                                                    result.PayLoad.Add(Text.Encoding.UTF8.GetBytes("-s on OK"))
                                                                                Case "off"
                                                                                    IndexRead = True
                                                                                    result.PayLoad.Add(Text.Encoding.UTF8.GetBytes("-s off OK"))
                                                                            End Select
                                                                            LWF.OfflineMode = Not IndexRead
                                                                            If Not IndexRead Then LWF.ExtraPartitionCount = 1 Else LWF.ExtraPartitionCount = 0
                                                                        Case "-t"
                                                                            Dim param1 As String = Text.Encoding.UTF8.GetString(cmd.PayLoad(3))
                                                                            If param1.StartsWith("TAPE") Then
                                                                                param1 = "\\.\" & param1
                                                                            ElseIf param1.StartsWith("\\.\") Then
                                                                                'Do Nothing
                                                                            ElseIf param1 = Val(param1).ToString Then
                                                                                param1 = "\\.\TAPE" & param1
                                                                            Else

                                                                            End If
                                                                            LWF.TapeDrive = param1
                                                                            result.PayLoad.Add(Text.Encoding.UTF8.GetBytes($"-t {param1} OK"))
                                                                        Case "click"
                                                                            Dim ctrlName As String = Text.Encoding.UTF8.GetString(cmd.PayLoad(3))
                                                                            Dim objSearchList As New List(Of Object)
                                                                            Dim FilterList As New List(Of Object)
                                                                            For Each ctrl As Control In LWF.Controls
                                                                                FilterList.Add(ctrl)
                                                                            Next
                                                                            While FilterList.Count > 0
                                                                                Dim q As New List(Of Object)
                                                                                For Each c As Object In FilterList
                                                                                    If TypeOf c Is Button Then
                                                                                        objSearchList.Add(c)
                                                                                    ElseIf TypeOf c Is Label Then
                                                                                        objSearchList.Add(c)
                                                                                    ElseIf TypeOf c Is Panel Then
                                                                                        q.AddRange((CType(c, Panel).Controls).Cast(Of Object)())
                                                                                    ElseIf TypeOf c Is MenuStrip Then
                                                                                        q.AddRange((CType(c, MenuStrip).Items).Cast(Of Object)())
                                                                                    ElseIf TypeOf c Is ToolStrip Then
                                                                                        q.AddRange((CType(c, ToolStrip).Items).Cast(Of Object)())
                                                                                    ElseIf TypeOf c Is ToolStripMenuItem Then
                                                                                        objSearchList.Add(c)
                                                                                        q.AddRange((CType(c, ToolStripMenuItem).DropDownItems).Cast(Of Object)())
                                                                                    ElseIf TypeOf c Is ToolStripDropDownButton Then
                                                                                        objSearchList.Add(c)
                                                                                    ElseIf TypeOf c Is ToolStripButton Then
                                                                                        objSearchList.Add(c)
                                                                                    ElseIf TypeOf c Is ToolStripStatusLabel Then
                                                                                        objSearchList.Add(c)
                                                                                    ElseIf TypeOf c Is ContextMenuStrip Then
                                                                                        q.AddRange((CType(c, ContextMenuStrip).Items).Cast(Of Object)())
                                                                                    ElseIf TypeOf c Is StatusStrip Then
                                                                                        q.AddRange((CType(c, StatusStrip).Items).Cast(Of Object)())
                                                                                    End If
                                                                                Next
                                                                                FilterList = q
                                                                            End While
                                                                            For Each obj As Object In objSearchList
                                                                                If TypeOf obj Is Button Then
                                                                                    With CType(obj, Button)
                                                                                        If .Name = ctrlName Then
                                                                                            Call GetType(Button).GetMethod("OnClick", Reflection.BindingFlags.NonPublic Or Reflection.BindingFlags.Instance).Invoke(obj, {EventArgs.Empty})
                                                                                            result.PayLoad.Add(Text.Encoding.UTF8.GetBytes("OK"))
                                                                                            Exit Select
                                                                                        End If
                                                                                    End With
                                                                                ElseIf TypeOf obj Is Label Then
                                                                                    With CType(obj, Label)
                                                                                        If .Name = ctrlName Then
                                                                                            Call GetType(Label).GetMethod("OnClick", Reflection.BindingFlags.NonPublic Or Reflection.BindingFlags.Instance).Invoke(obj, {EventArgs.Empty})
                                                                                            result.PayLoad.Add(Text.Encoding.UTF8.GetBytes("OK"))
                                                                                            Exit Select
                                                                                        End If
                                                                                    End With
                                                                                ElseIf TypeOf obj Is ToolStripMenuItem Then
                                                                                    With CType(obj, ToolStripMenuItem)
                                                                                        If .Name = ctrlName Then
                                                                                            Call GetType(ToolStripMenuItem).GetMethod("OnClick", Reflection.BindingFlags.NonPublic Or Reflection.BindingFlags.Instance).Invoke(obj, {EventArgs.Empty})
                                                                                            result.PayLoad.Add(Text.Encoding.UTF8.GetBytes("OK"))
                                                                                            Exit Select
                                                                                        End If
                                                                                    End With
                                                                                ElseIf TypeOf obj Is ToolStripDropDownButton Then
                                                                                    With CType(obj, ToolStripDropDownButton)
                                                                                        If .Name = ctrlName Then
                                                                                            Call GetType(ToolStripDropDownButton).GetMethod("OnClick", Reflection.BindingFlags.NonPublic Or Reflection.BindingFlags.Instance).Invoke(obj, {EventArgs.Empty})
                                                                                            result.PayLoad.Add(Text.Encoding.UTF8.GetBytes("OK"))
                                                                                            Exit Select
                                                                                        End If
                                                                                    End With
                                                                                ElseIf TypeOf obj Is ToolStripButton Then
                                                                                    With CType(obj, ToolStripButton)
                                                                                        If .Name = ctrlName Then
                                                                                            Call GetType(ToolStripButton).GetMethod("OnClick", Reflection.BindingFlags.NonPublic Or Reflection.BindingFlags.Instance).Invoke(obj, {EventArgs.Empty})
                                                                                            result.PayLoad.Add(Text.Encoding.UTF8.GetBytes("OK"))
                                                                                            Exit Select
                                                                                        End If
                                                                                    End With
                                                                                ElseIf TypeOf obj Is ToolStripStatusLabel Then
                                                                                    With CType(obj, ToolStripStatusLabel)
                                                                                        If .Name = ctrlName Then
                                                                                            Call GetType(ToolStripStatusLabel).GetMethod("OnClick", Reflection.BindingFlags.NonPublic Or Reflection.BindingFlags.Instance).Invoke(obj, {EventArgs.Empty})
                                                                                            result.PayLoad.Add(Text.Encoding.UTF8.GetBytes("OK"))
                                                                                            Exit Select
                                                                                        End If
                                                                                    End With
                                                                                End If
                                                                            Next
                                                                            result.PayLoad.Add(Text.Encoding.UTF8.GetBytes("Not found"))
                                                                        Case "gettext"
                                                                            Dim ctrlName As String = Text.Encoding.UTF8.GetString(cmd.PayLoad(3))
                                                                            If ctrlName = "" Then
                                                                                result.PayLoad.Add(Text.Encoding.UTF8.GetBytes("OK"))
                                                                                result.PayLoad.Add(Text.Encoding.UTF8.GetBytes(LWF.Text))
                                                                                Exit Select
                                                                            End If
                                                                            Dim objSearchList As New List(Of Object)
                                                                            objSearchList.Add(LWF)
                                                                            Dim FilterList As New List(Of Object)
                                                                            For Each ctrl As Control In LWF.Controls
                                                                                FilterList.Add(ctrl)
                                                                            Next
                                                                            While FilterList.Count > 0
                                                                                Dim q As New List(Of Object)
                                                                                For Each c As Object In FilterList
                                                                                    If TypeOf c Is Button Then
                                                                                        objSearchList.Add(c)
                                                                                    ElseIf TypeOf c Is Label Then
                                                                                        objSearchList.Add(c)
                                                                                    ElseIf TypeOf c Is Panel Then
                                                                                        q.AddRange((CType(c, Panel).Controls).Cast(Of Object)())
                                                                                    ElseIf TypeOf c Is MenuStrip Then
                                                                                        objSearchList.Add(c)
                                                                                        q.AddRange((CType(c, MenuStrip).Items).Cast(Of Object)())
                                                                                    ElseIf TypeOf c Is ToolStrip Then
                                                                                        objSearchList.Add(c)
                                                                                        q.AddRange((CType(c, ToolStrip).Items).Cast(Of Object)())
                                                                                    ElseIf TypeOf c Is ToolStripMenuItem Then
                                                                                        objSearchList.Add(c)
                                                                                        q.AddRange((CType(c, ToolStripMenuItem).DropDownItems).Cast(Of Object)())
                                                                                    ElseIf TypeOf c Is ToolStripDropDownButton Then
                                                                                        objSearchList.Add(c)
                                                                                    ElseIf TypeOf c Is ToolStripButton Then
                                                                                        objSearchList.Add(c)
                                                                                    ElseIf TypeOf c Is ToolStripStatusLabel Then
                                                                                        objSearchList.Add(c)
                                                                                    ElseIf TypeOf c Is ContextMenuStrip Then
                                                                                        objSearchList.Add(c)
                                                                                        q.AddRange((CType(c, ContextMenuStrip).Items).Cast(Of Object)())
                                                                                    ElseIf TypeOf c Is StatusStrip Then
                                                                                        objSearchList.Add(c)
                                                                                        q.AddRange((CType(c, StatusStrip).Items).Cast(Of Object)())
                                                                                    End If
                                                                                Next
                                                                                FilterList = q
                                                                            End While
                                                                            For Each obj As Object In objSearchList
                                                                                If TypeOf obj Is Button Then
                                                                                    With CType(obj, Button)
                                                                                        If .Name = ctrlName Then
                                                                                            result.PayLoad.Add(Text.Encoding.UTF8.GetBytes("OK"))
                                                                                            result.PayLoad.Add(Text.Encoding.UTF8.GetBytes(.Text))
                                                                                            Exit Select
                                                                                        End If
                                                                                    End With
                                                                                ElseIf TypeOf obj Is Form Then
                                                                                    With CType(obj, Form)
                                                                                        If .Name = ctrlName Then
                                                                                            result.PayLoad.Add(Text.Encoding.UTF8.GetBytes("OK"))
                                                                                            result.PayLoad.Add(Text.Encoding.UTF8.GetBytes(.Text))
                                                                                            Exit Select
                                                                                        End If
                                                                                    End With
                                                                                ElseIf TypeOf obj Is Label Then
                                                                                    With CType(obj, Label)
                                                                                        If .Name = ctrlName Then
                                                                                            result.PayLoad.Add(Text.Encoding.UTF8.GetBytes("OK"))
                                                                                            result.PayLoad.Add(Text.Encoding.UTF8.GetBytes(.Text))
                                                                                            Exit Select
                                                                                        End If
                                                                                    End With
                                                                                ElseIf TypeOf obj Is ToolStripMenuItem Then
                                                                                    With CType(obj, ToolStripMenuItem)
                                                                                        If .Name = ctrlName Then
                                                                                            result.PayLoad.Add(Text.Encoding.UTF8.GetBytes("OK"))
                                                                                            result.PayLoad.Add(Text.Encoding.UTF8.GetBytes(.Text))
                                                                                            Exit Select
                                                                                        End If
                                                                                    End With
                                                                                ElseIf TypeOf obj Is ToolStripDropDownButton Then
                                                                                    With CType(obj, ToolStripDropDownButton)
                                                                                        If .Name = ctrlName Then
                                                                                            result.PayLoad.Add(Text.Encoding.UTF8.GetBytes("OK"))
                                                                                            result.PayLoad.Add(Text.Encoding.UTF8.GetBytes(.Text))
                                                                                            Exit Select
                                                                                        End If
                                                                                    End With
                                                                                ElseIf TypeOf obj Is ToolStripButton Then
                                                                                    With CType(obj, ToolStripButton)
                                                                                        If .Name = ctrlName Then
                                                                                            result.PayLoad.Add(Text.Encoding.UTF8.GetBytes("OK"))
                                                                                            result.PayLoad.Add(Text.Encoding.UTF8.GetBytes(.Text))
                                                                                            Exit Select
                                                                                        End If
                                                                                    End With
                                                                                ElseIf TypeOf obj Is ToolStripStatusLabel Then
                                                                                    With CType(obj, ToolStripStatusLabel)
                                                                                        If .Name = ctrlName Then
                                                                                            result.PayLoad.Add(Text.Encoding.UTF8.GetBytes("OK"))
                                                                                            result.PayLoad.Add(Text.Encoding.UTF8.GetBytes(.Text))
                                                                                            Exit Select
                                                                                        End If
                                                                                    End With
                                                                                End If
                                                                            Next
                                                                            result.PayLoad.Add(Text.Encoding.UTF8.GetBytes("Not found"))
                                                                    End Select
                                                            End Select
                                                        End If
                                                    End If
                                                Catch ex As Exception
                                                    result.PayLoad.Add(Text.Encoding.UTF8.GetBytes(ex.ToString()))
                                                End Try

                                        End Select
                                        Dim resp As String = result.GetSerializedText()
                                        Dim respData As New List(Of Byte)
                                        respData.AddRange(BitConverter.GetBytes(resp.Length))
                                        respData.AddRange(Text.Encoding.UTF8.GetBytes(resp))
                                        Console.WriteLine(resp)
                                        connect.Send(respData.ToArray())
                                    Catch ex As Exception
                                        Console.WriteLine(ex.ToString())
                                    End Try
                                    connect.Close()
                                End While
                                AddHandler Console.CancelKeyPress,
                                    Sub(ByVal senderc As Object, ByVal args As ConsoleCancelEventArgs)
                                        sck.Shutdown(Net.Sockets.SocketShutdown.Both)
                                        sck.Close()
                                    End Sub
                                CloseConsole()
                                End
                            End If
                        Case "-remoteraw"
                            If Not CheckUAC(e) Then Return
                            If i < param.Count - 6 Then
                                Dim ipstr As String = param(i + 1)
                                Dim ip As New Net.IPAddress(0)
                                Net.IPAddress.TryParse(ipstr, ip)
                                Dim port As String = param(i + 2)
                                Dim TapeDrive As String = param(i + 3)
                                If TapeDrive.StartsWith("TAPE") Then
                                    TapeDrive = "\\.\" & TapeDrive
                                ElseIf TapeDrive.StartsWith("\\.\") Then
                                    'Do Nothing
                                ElseIf TapeDrive = Val(TapeDrive).ToString Then
                                    TapeDrive = "\\.\TAPE" & TapeDrive
                                Else

                                End If
                                Dim cdb As Byte() = IOManager.HexStringToByteArray(param(i + 4))
                                Dim data As Byte() = IOManager.HexStringToByteArray(param(i + 5))
                                Dim dataDir As Byte = CByte(Val(param(i + 6)))
                                Dim TimeOut As Integer = 60000
                                If i + 7 <= param.Length - 1 Then
                                    TimeOut = CInt(Val(param(i + 7)))
                                End If
                                Dim sense As Byte() = {}
                                Dim pl As New List(Of Byte())
                                pl.Add(Text.Encoding.UTF8.GetBytes(TapeDrive))
                                pl.Add(cdb)
                                pl.Add(data)
                                pl.Add({dataDir})
                                pl.Add(BitConverter.GetBytes(TimeOut))
                                Dim cmd As New IOManager.NetworkCommand With {.CommandType = IOManager.NetworkCommand.CommandTypeDef.SCSICommand,
                                    .PayLoad = pl,
                                    .HashCode = Now.Ticks.GetHashCode()}
                                Dim result As IOManager.NetworkCommand = cmd.SendTo(ip, Integer.Parse(port))
                                If result.HashCode = cmd.HashCode AndAlso result.CommandType = IOManager.NetworkCommand.CommandTypeDef.SCSISenseData Then
                                    data = result.PayLoad(0)
                                    sense = result.PayLoad(1)
                                    Console.WriteLine($"{TapeDrive}
cdb:
{TapeUtils.Byte2Hex(cdb)}
param:
{TapeUtils.Byte2Hex(data)}
dataDir:{dataDir}

{Resources.StrSCSISucc}
sense:
{TapeUtils.Byte2Hex(sense)}
{TapeUtils.ParseSenseData(sense)}")
                                Else
                                    Console.WriteLine($"{TapeDrive}
cdb:
{TapeUtils.Byte2Hex(cdb)}
param:
{TapeUtils.Byte2Hex(data)}
dataDir:{dataDir}


{Resources.StrSCSIFail}")
                                End If

                                CloseConsole()
                                End
                            End If
                        Case Else
                            Try
                                Console.WriteLine($"{ApplicationWheels.ApplicationInfo}
{Resources.StrCMDHelpText}")

                                CloseConsole()
                                End

                            Catch ex As Exception
                                MessageBox.Show(New Form With {.TopMost = True}, ex.ToString)
                            End Try
                            'End
                    End Select
                Next
            End If

        End Sub

        Private Sub MyApplication_Shutdown(sender As Object, e As EventArgs) Handles Me.Shutdown
            Try
                Serilog.Log.Information("Application shutting down.")
                Serilog.Log.CloseAndFlush()
            Finally
                ' A worker created with Thread (rather than Task) is a
                ' foreground thread by default.  It may be blocked in a
                ' synchronous Control.Invoke after the UI message loop has
                ' closed, which otherwise keeps the process alive forever.
                ' Shutdown is the last application-level cleanup point, so
                ' do not let such a worker veto the user's request to exit.
                Environment.Exit(0)
            End Try
        End Sub

        Private Sub MyApplication_UnhandledException(sender As Object, e As UnhandledExceptionEventArgs) Handles Me.UnhandledException
            Serilog.Log.Fatal(e.Exception, "Unhandled application exception.")
            Serilog.Log.CloseAndFlush()
            MessageBox.Show(e.Exception.ToString())
        End Sub

    End Class
End Namespace
