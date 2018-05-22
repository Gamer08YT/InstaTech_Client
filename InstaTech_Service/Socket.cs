﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.IO;
using System.Threading;
using JSON_Helper;
using Win32_Classes;
using System.Runtime.InteropServices;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.Net;
using System.Management;

namespace InstaTech_Service
{
    public static class Socket
    {
        // ***  Config: Change these variables for your environment.  *** //
#if DEBUG
        const string hostName = "localhost:52422";
#else
        const string hostName = "translucency.myddns.rocks";
#endif
        static string wsPort = "80";
        static string wssPort = "443";

        static string socketPath = "wss://" + hostName + "/Services/Remote_Control_Socket.cshtml";
        static string fileTransferURI = "https://" + hostName + "/Services/File_Transfer.cshtml";
        static string downloadURI = "https://" + hostName + "/Downloads/InstaTech_Service.exe";
        static string versionURI = "https://" + hostName + "/Services/Get_Service_Version.cshtml";

        // ***  Fields and Properties  *** //
        static WebSocket socket;
        static HttpClient httpClient = new HttpClient();
        static Bitmap screenshot;
        static Bitmap lastFrame;
        static Bitmap croppedFrame;
        static string ConnectionType;
        static byte[] newData;
        static System.Drawing.Rectangle boundingBox;
        static Graphics graphic;
        static bool capturing = false;
        static int totalHeight = 0;
        static int totalWidth = 0;
        // Offsets are the left and top edge of the screen, in case multiple monitor setups
        // create a situation where the edge of a monitor is in the negative.  This must
        // be converted to a 0-based max left/top to render images on the canvas properly.
        static int offsetX = 0;
        static int offsetY = 0;
        static Point cursorPos;
        static bool sendFullScreenshot = true;
        static DateTime lastMessage = DateTime.Now;
        static System.Timers.Timer idleTimer = new System.Timers.Timer(5000);
        static System.Timers.Timer heartbeatTimer = new System.Timers.Timer(300000);
        static Process deployProc;
        static Process psProcess;
        static Process cmdProcess;
        static dynamic deployFileRequest;
        static List<ArraySegment<Byte>> socketSendBuffer = new List<ArraySegment<byte>>();
        static string desktopName;
        static DateTime LastPingReceived { get; set; } = DateTime.Now;


        public static async Task StartInteractive()
        {
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            var notifierPath = Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), "Notifier.exe");
            if (File.Exists(notifierPath))
            {
                Process.Start(notifierPath);
            }
            //Initialize variables requiring screen dimensions.
            totalWidth = SystemInformation.VirtualScreen.Width;
            totalHeight = SystemInformation.VirtualScreen.Height;
            offsetX = SystemInformation.VirtualScreen.Left;
            offsetY = SystemInformation.VirtualScreen.Top;
            screenshot = new Bitmap(totalWidth, totalHeight);
            lastFrame = new Bitmap(totalWidth, totalHeight);
            graphic = Graphics.FromImage(screenshot);

            // Clean up temp files from previous file transfers.
            var path = System.IO.Path.GetTempPath() + @"\InstaTech\";
            if (System.Security.Principal.WindowsIdentity.GetCurrent().IsSystem)
            {
                path = Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments) + @"\InstaTech\";
            }
            var di = new DirectoryInfo(path);
            if (di.Exists)
            {
                di.Delete(true);
            }
            // Start idle timer.
            idleTimer.Elapsed += (object sender, System.Timers.ElapsedEventArgs e) => {
                if (DateTime.Now - lastMessage > TimeSpan.FromMinutes(5))
                {
                    SocketSend(new
                    {
                        Type = "IdleTimeout"
                    }).Wait();

                    socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Connection timed out.", CancellationToken.None).Wait();
                }
            };
            idleTimer.Start();
            
            if (Environment.GetCommandLineArgs().ToList().Exists(str => str.ToLower() == "-once"))
            {
                ConnectionType = "ClientConsoleOnce";
            }
            else
            {
                ConnectionType = "ClientConsole";
            }
            desktopName = User32.GetCurrentDesktop();
            await InitWebSocket();
            await HandleInteractiveSocket();
        }
        public static async Task StartService()
        {
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            
            if (Environment.GetCommandLineArgs().ToList().Exists(str => str.ToLower() == "-once"))
            {
                ConnectionType = "ClientServiceOnce";
            }
            else
            {
                ConnectionType = "ClientService";
            }
            await InitWebSocket();

            heartbeatTimer.Elapsed += async (object send, System.Timers.ElapsedEventArgs args) => {
                await SendHeartbeat();
            };
            heartbeatTimer.Start();
            await HandleServiceSocket();
        }
        private static async Task TestSSL()
        {
            socketPath = socketPath.Replace("ws:", "wss:");
            downloadURI = downloadURI.Replace("http:", "https:");
            fileTransferURI = fileTransferURI.Replace("http:", "https:");
            versionURI = versionURI.Replace("http:", "https:");
            try
            {
                var request = WebRequest.CreateHttp("https://" + hostName);
                var response = await request.GetResponseAsync() as HttpWebResponse;
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    throw new Exception();
                }
            }
            catch
            {
                WriteToLog("SSL check failed. Connection is not encrypted.");
                if (wsPort != "80")
                {
                    socketPath = "ws://" + hostName + ":" + wsPort + "/Services/Remote_Control_Socket.cshtml";
                }
                socketPath = socketPath.Replace("wss", "ws");
                downloadURI = downloadURI.Replace("https", "http");
                fileTransferURI = fileTransferURI.Replace("https", "http");
                versionURI = versionURI.Replace("https", "http");
            }
        }
        private static async Task InitWebSocket()
        {
            if (wssPort != "443")
            {
                socketPath = "wss://" + hostName + ":" + wssPort + "/Services/Remote_Control_Socket.cshtml";
            }
            await TestSSL();
            socket = SystemClientWebSocket.CreateClientWebSocket();
            try
            {
                await socket.ConnectAsync(new Uri(socketPath), CancellationToken.None);
            }
            catch
            {
                try
                {
                    if (wsPort != "80")
                    {
                        socketPath = "ws://" + hostName + ":" + wsPort + "/Services/Remote_Control_Socket.cshtml";
                    }
                    socket = SystemClientWebSocket.CreateClientWebSocket();
                    await socket.ConnectAsync(new Uri(socketPath), CancellationToken.None);
                }
                catch (Exception ex)
                {
                    WriteToLog(ex);
                    Environment.Exit(1);
                    return;
                }
            }
            var uptime = new PerformanceCounter("System", "System Up Time", true);
            uptime.NextValue();
            string currentUser;
            try
            {
                var mos = new ManagementObjectSearcher("Select * FROM Win32_Process WHERE ExecutablePath LIKE '%explorer.exe%'");
                var col = mos.Get();
                var process = col.Cast<ManagementObject>().First();
                var ownerInfo = new string[2];
                process.InvokeMethod("GetOwner", ownerInfo);
                currentUser = ownerInfo[1] + "\\" + ownerInfo[0];
            }
            catch
            {
                currentUser = "";
            }
            WriteToLog($"Connection opened on {socketPath}.");
            // Send notification to server that this connection is for a client service.
            var request = new
            {
                Type = "ConnectionType",
                ConnectionType = ConnectionType,
                ComputerName = Environment.MachineName,
                CurrentUser = currentUser,
                LastReboot = (DateTime.Now - TimeSpan.FromSeconds(uptime.NextValue()))
            };
            await SocketSend(request);
        }

        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            WriteToLog(e.ExceptionObject as Exception);
        }
        static private async Task HandleInteractiveSocket()
        {
            ArraySegment<byte> buffer;
            WebSocketReceiveResult result;
            string trimmedString = "";
            dynamic jsonMessage = null;
            try
            {
                while (socket.State == WebSocketState.Connecting || socket.State == WebSocketState.Open)
                {
                    buffer = ClientWebSocket.CreateClientBuffer(65536, 65536);
                    result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                    lastMessage = DateTime.Now;
                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        trimmedString = Encoding.UTF8.GetString(buffer.Take(result.Count).ToArray());
                        jsonMessage = JSON.Decode(trimmedString);

                        switch ((string)jsonMessage.Type)
                        {
                            case "CaptureScreen":
                                var thisProc = System.Diagnostics.Process.GetCurrentProcess();
                                var allProcs = System.Diagnostics.Process.GetProcessesByName("InstaTech_Service").Where(proc => proc.SessionId == Process.GetCurrentProcess().SessionId);
                                foreach (var proc in allProcs)
                                {
                                    if (proc.Id != thisProc.Id)
                                    {
                                        try
                                        {
                                            proc.Close();
                                            proc.Kill();
                                        }
                                        catch { }
                                    }
                                }
                                if (!ConnectionType.Contains("Once"))
                                {
                                    await CheckForUpdates();
                                }
                                BeginScreenCapture();
                                break;
                            case "RefreshScreen":
                                sendFullScreenshot = true;
                                break;
                            case "FrameReceived":
                                SendFrame();
                                break;
                            case "FileTransfer":
                                var url = jsonMessage.URL.ToString();
                                HttpResponseMessage httpResult = await httpClient.GetAsync(url);
                                var arrResult = await httpResult.Content.ReadAsByteArrayAsync();
                                string strFileName = jsonMessage.FileName.ToString();
                                var path = Path.GetTempPath() + @"\InstaTech\";
                                if (System.Security.Principal.WindowsIdentity.GetCurrent().IsSystem)
                                {
                                    path = Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments) + @"\InstaTech\";
                                }
                                var di = Directory.CreateDirectory(path);
                                File.WriteAllBytes(di.FullName + strFileName, arrResult);
                                Process.Start("explorer.exe", di.FullName);
                                break;
                            case "SendClipboard":
                                byte[] arrData = Convert.FromBase64String(jsonMessage.Data.ToString());
                                User32.OpenClipboard(User32.GetDesktopWindow());
                                User32.EmptyClipboard();
                                User32.SetClipboardData(1, Marshal.StringToHGlobalAnsi(Encoding.UTF8.GetString(arrData)));
                                User32.CloseClipboard();
                                break;
                            case "MouseMove":
                                User32.SendMouseMove((double)jsonMessage.PointX, (double)jsonMessage.PointY);
                                break;
                            case "MouseDown":
                                if (jsonMessage.Button == "Left")
                                {
                                    User32.SendLeftMouseDown((int)Math.Round(((double)jsonMessage.PointX * totalWidth + offsetX), 0), (int)Math.Round(((double)jsonMessage.PointY * totalHeight + offsetY), 0), (bool)jsonMessage.Alt, (bool)jsonMessage.Ctrl, (bool)jsonMessage.Shift);
                                }
                                else if (jsonMessage.Button == "Right")
                                {
                                    User32.SendRightMouseDown((int)Math.Round(((double)jsonMessage.PointX * totalWidth + offsetX), 0), (int)Math.Round(((double)jsonMessage.PointY * totalHeight + offsetY), 0), (bool)jsonMessage.Alt, (bool)jsonMessage.Ctrl, (bool)jsonMessage.Shift);
                                }
                                break;
                            case "MouseUp":
                                if (jsonMessage.Button == "Left")
                                {
                                    User32.SendLeftMouseUp((int)Math.Round(((double)jsonMessage.PointX * totalWidth + offsetX), 0), (int)Math.Round(((double)jsonMessage.PointY * totalHeight + offsetY), 0), (bool)jsonMessage.Alt, (bool)jsonMessage.Ctrl, (bool)jsonMessage.Shift);
                                }
                                else if (jsonMessage.Button == "Right")
                                {
                                    User32.SendRightMouseUp((int)Math.Round(((double)jsonMessage.PointX * totalWidth + offsetX), 0), (int)Math.Round(((double)jsonMessage.PointY * totalHeight + offsetY), 0), (bool)jsonMessage.Alt, (bool)jsonMessage.Ctrl, (bool)jsonMessage.Shift);
                                }
                                break;
                            case "MouseWheel":
                                User32.SendMouseWheel((int)Math.Round((double)jsonMessage.DeltaY * -1));
                                break;
                            case "TouchMove":
                                User32.GetCursorPos(out cursorPos);
                                User32.SetCursorPos((int)Math.Round(cursorPos.X + (double)jsonMessage.MoveByX * totalWidth), (int)Math.Round(cursorPos.Y + (double)jsonMessage.MoveByY * totalHeight));
                                break;
                            case "Tap":
                                User32.GetCursorPos(out cursorPos);
                                User32.SendLeftMouseDown(cursorPos.X, cursorPos.Y, false, false, false);
                                User32.SendLeftMouseUp(cursorPos.X, cursorPos.Y, false, false, false);
                                break;
                            case "TouchDown":
                                User32.GetCursorPos(out cursorPos);
                                User32.SendLeftMouseDown(cursorPos.X, cursorPos.Y, false, false, false);
                                break;
                            case "LongPress":
                                User32.GetCursorPos(out cursorPos);
                                User32.SendRightMouseDown(cursorPos.X, cursorPos.Y, false, false, false);
                                User32.SendRightMouseUp(cursorPos.X, cursorPos.Y, false, false, false);
                                break;
                            case "TouchUp":
                                User32.GetCursorPos(out cursorPos);
                                User32.SendLeftMouseUp(cursorPos.X, cursorPos.Y, false, false, false);
                                break;
                            case "KeyPress":
                                try
                                {
                                    string baseKey = jsonMessage.Key;
                                    string modifier = "";
                                    var modArray = jsonMessage.Modifiers as Array;
                                    if (modArray.Length > 0)
                                    {
                                        var modList = new List<string>();
                                        foreach (var mod in modArray)
                                        {
                                            modList.Add(mod.ToString());
                                        }
                                        if (modList.Contains("Alt"))
                                        {
                                            modifier += "%";
                                        }
                                        if (modList.Contains("Control"))
                                        {
                                            modifier += "^";
                                        }
                                        if (modList.Contains("Shift"))
                                        {
                                            modifier += "+";
                                        }
                                    }

                                    if (baseKey.Length > 1)
                                    {
                                        baseKey = baseKey.Replace("Arrow", "");
                                        baseKey = baseKey.Replace("PageDown", "PGDN");
                                        baseKey = baseKey.Replace("PageUp", "PGUP");
                                        if (!baseKey.StartsWith("{") && !baseKey.EndsWith("}"))
                                        {
                                            baseKey = "{" + baseKey + "}";
                                        }
                                    }
                                    SendKeys.SendWait(modifier + baseKey);
                                }
                                catch (Exception ex)
                                {
                                    WriteToLog(ex);
                                    WriteToLog("Missing keybind for " + JSON.Encode(jsonMessage));
                                }
                                break;
                            case "CtrlAltDel":
                                User32.SendSAS(false);
                                break;
                            case "UninstallService":
                                WriteToLog("Service uninstall requested.");
                                Process.Start(System.Reflection.Assembly.GetExecutingAssembly().Location, "-uninstall").WaitForExit();
                                jsonMessage.Status = "ok";
                                await SocketSend(jsonMessage);
                                break;
                            case "PartnerClose":
                                if (ConnectionType == "ClientConsoleOnce")
                                {
                                    foreach (var proc in Process.GetProcessesByName("InstaTech_Service"))
                                    {
                                        if (proc.Id != Process.GetCurrentProcess().Id)
                                        {
                                            try
                                            {
                                                proc.Close();
                                                proc.Kill();
                                            }
                                            catch { }
                                        }
                                    }
                                    Process.Start("cmd", "/c sc delete InstaTech_Service");
                                }
                                Environment.Exit(0);
                                break;
                            case "PartnerError":
                                if (ConnectionType == "ClientConsoleOnce")
                                {
                                    foreach (var proc in Process.GetProcessesByName("InstaTech_Service"))
                                    {
                                        if (proc.Id != Process.GetCurrentProcess().Id)
                                        {
                                            try
                                            {
                                                proc.Close();
                                                proc.Kill();
                                            }
                                            catch { }
                                        }
                                    }
                                    Process.Start("cmd", "/c sc delete InstaTech_Service");
                                }
                                Environment.Exit(0);
                                break;
                            default:
                                break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                WriteToLog(ex);
            }
            if (ConnectionType == "ClientConsoleOnce")
            {
                foreach (var proc in Process.GetProcessesByName("InstaTech_Service"))
                {
                    if (proc.Id != Process.GetCurrentProcess().Id)
                    {
                        try
                        {
                            proc.Close();
                            proc.Kill();
                        }
                        catch { }
                    }
                }
                Process.Start("cmd", "/c sc delete InstaTech_Service");
            }
            Environment.Exit(0);
        }

        private async static Task HandleServiceSocket()
        {
            ArraySegment<byte> buffer;
            WebSocketReceiveResult result;
            string trimmedString = "";
            dynamic jsonMessage = null;
            try
            {
                while (socket.State == WebSocketState.Connecting || socket.State == WebSocketState.Open)
                {
                    buffer = ClientWebSocket.CreateClientBuffer(65536, 65536);
                    result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        trimmedString = Encoding.UTF8.GetString(buffer.Take(result.Count).ToArray());
                        jsonMessage = JSON.Decode(trimmedString);

                        switch ((string)jsonMessage.Type)
                        {
                            case "ConnectUnattended":
                                foreach (var proc in Process.GetProcessesByName("InstaTech_Service"))
                                {
                                    if (proc.SessionId != Process.GetCurrentProcess().SessionId)
                                    {
                                        try
                                        {
                                            proc.Close();
                                            proc.Kill();
                                        }
                                        catch { }                                    
                                    }
                                }
                                desktopName = User32.GetCurrentDesktop();
                                var procInfo = new ADVAPI32.PROCESS_INFORMATION();
                                var processResult = ADVAPI32.OpenInteractiveProcess(System.Reflection.Assembly.GetExecutingAssembly().Location + " -interactive", desktopName, out procInfo);
                                if (processResult == false)
                                {
                                    var response = new
                                    {
                                        Type = "ProcessStartResult",
                                        Status = "failed"
                                    };
                                    await SocketSend(response);
                                    WriteToLog(new Exception("Error opening interactive process.  Error Code: " + Marshal.GetLastWin32Error().ToString()));
                                }
                                else
                                {
                                    var response = new
                                    {
                                        Type = "ProcessStartResult",
                                        Status = "ok"
                                    };
                                    await SocketSend(response);
                                }
                                break;
                            case "ConnectUnattendedOnce":
                                foreach (var proc in Process.GetProcessesByName("InstaTech_Service"))
                                {
                                    if (proc.SessionId != Process.GetCurrentProcess().SessionId)
                                    {
                                        try
                                        {
                                            proc.Close();
                                            proc.Kill();
                                        }
                                        catch { }
                                    }
                                }
                                var pi = new ADVAPI32.PROCESS_INFORMATION();
                                desktopName = User32.GetCurrentDesktop();
                                ADVAPI32.OpenInteractiveProcess(System.Reflection.Assembly.GetExecutingAssembly().Location + " -interactive -once", desktopName, out pi);
                                break;
                            case "ServiceDuplicate":
                                WriteToLog(new Exception("Service is already running on another computer with the same name."));
                                break;
                            case "CtrlAltDel":
                                User32.SendSAS(false);
                                break;
                            case "Uninstall":
                                Process.Start(System.Reflection.Assembly.GetExecutingAssembly().Location, "-uninstall");
                                break;
                            case "FileDeploy":
                                deployFileRequest = jsonMessage;
                                var url = jsonMessage.URL.ToString();
                                HttpResponseMessage httpResult = await httpClient.GetAsync(url);
                                var arrResult = await httpResult.Content.ReadAsByteArrayAsync();
                                string strFileName = jsonMessage.FileName.ToString();
                                var di = Directory.CreateDirectory(Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments) + @"\InstaTech\");
                                File.WriteAllBytes(di.FullName + strFileName, arrResult);
                                var ext = Path.GetExtension(strFileName).ToLower();
                                ProcessStartInfo psi;
                                if (ext == ".exe")
                                {
                                    psi = new ProcessStartInfo(di.FullName + strFileName);
                                    if (!String.IsNullOrWhiteSpace((string)jsonMessage.Arguments))
                                    {
                                        psi.Arguments = jsonMessage.Arguments;
                                    }
                                }
                                else if (ext == ".bat")
                                {
                                    psi = new ProcessStartInfo("cmd.exe", "/c \"" + di.FullName + strFileName + "\"");
                                    if (!String.IsNullOrWhiteSpace((string)jsonMessage.Arguments))
                                    {
                                        psi.Arguments += " " + jsonMessage.Arguments;
                                    }
                                }
                                else if (ext == ".ps1")
                                {
                                    if (!String.IsNullOrWhiteSpace((string)jsonMessage.Arguments))
                                    {
                                        psi = new ProcessStartInfo("powershell.exe", "-executionpolicy bypass -File " + di.FullName + strFileName + " " + jsonMessage.Arguments);
                                    }
                                    else
                                    {
                                        psi = new ProcessStartInfo("powershell.exe", "-executionpolicy bypass -File " + di.FullName + strFileName);
                                    }
                                }
                                else
                                {
                                    return;
                                }
                                psi.RedirectStandardOutput = true;
                                psi.RedirectStandardError = true;
                                psi.UseShellExecute = false;
                                deployProc = new Process();
                                deployProc.StartInfo = psi;
                                deployProc.EnableRaisingEvents = true;
                                deployProc.Exited += async (object sender, EventArgs e) =>
                                {
                                    deployFileRequest.Output = deployProc.StandardOutput.ReadToEnd();
                                    deployFileRequest.Error = deployProc.StandardError.ReadToEnd();
                                    deployFileRequest.Status = "ok";
                                    deployFileRequest.ExitCode = deployProc.ExitCode;
                                    await SocketSend(deployFileRequest);
                                };
                                deployProc.Start();
                                break;
                            case "ConsoleCommand":
                                string command = Encoding.UTF8.GetString(Convert.FromBase64String(jsonMessage.Command.ToString()));
                                if (jsonMessage.Language.ToString() == "PowerShell")
                                {
                                    if (psProcess == null || psProcess.HasExited)
                                    {
                                        var psi2 = new ProcessStartInfo("powershell.exe", "-noexit -executionpolicy bypass -Command \"\"& {" + command.Replace("\"", "\"\"\"") + "}\"\"");
                                        psi2.RedirectStandardOutput = true;
                                        psi2.RedirectStandardInput = true;
                                        psi2.RedirectStandardError = true;
                                        psi2.UseShellExecute = false;
                                        psi2.WorkingDirectory = Path.GetPathRoot(Environment.SystemDirectory);
                                        psProcess = new Process();
                                        psProcess.StartInfo = psi2;
                                        psProcess.EnableRaisingEvents = true;

                                        psProcess.OutputDataReceived += async (object sender, DataReceivedEventArgs args) =>
                                        {
                                            jsonMessage.Status = "ok";
                                            jsonMessage.Output = args.Data;
                                            await SocketSend(jsonMessage);

                                        };
                                        psProcess.ErrorDataReceived += async (object sender, DataReceivedEventArgs args) =>
                                        {
                                            jsonMessage.Status = "ok";
                                            jsonMessage.Output = args.Data;
                                            await SocketSend(jsonMessage);

                                        };
                                        psProcess.Start();
                                        psProcess.BeginOutputReadLine();
                                        psProcess.BeginErrorReadLine();
                                    }
                                    else
                                    {
                                        psProcess.StandardInput.WriteLine(command);
                                    }
                                }
                                else if (jsonMessage.Language.ToString() == "Batch")
                                {
                                    if (cmdProcess == null || cmdProcess.HasExited)
                                    {
                                        var psi2 = new ProcessStartInfo("cmd.exe", "/k " + command);
                                        psi2.RedirectStandardOutput = true;
                                        psi2.RedirectStandardInput = true;
                                        psi2.RedirectStandardError = true;
                                        psi2.UseShellExecute = false;
                                        psi2.WorkingDirectory = Path.GetPathRoot(Environment.SystemDirectory);

                                        cmdProcess = new Process();
                                        cmdProcess.StartInfo = psi2;
                                        cmdProcess.EnableRaisingEvents = true;
                                        cmdProcess.OutputDataReceived += async (object sender, DataReceivedEventArgs args) =>
                                        {
                                            jsonMessage.Status = "ok";
                                            jsonMessage.Output = args.Data;
                                            await SocketSend(jsonMessage);

                                        };
                                        cmdProcess.ErrorDataReceived += async (object sender, DataReceivedEventArgs args) =>
                                        {
                                            jsonMessage.Status = "ok";
                                            jsonMessage.Output = args.Data;
                                            await SocketSend(jsonMessage);
                                        };
                                        cmdProcess.Start();
                                        cmdProcess.BeginOutputReadLine();
                                        cmdProcess.BeginErrorReadLine();
                                    }
                                    else
                                    {
                                        cmdProcess.StandardInput.WriteLine(command);
                                    }
                                }
                                else
                                {
                                    return;
                                }

                                break;
                            case "NewConsole":
                                if (jsonMessage.Language.ToString() == "PowerShell")
                                {
                                    if (psProcess != null && !psProcess.HasExited)
                                    {
                                        psProcess.CancelOutputRead();
                                        psProcess.CancelErrorRead();
                                        psProcess.Kill();
                                    }
                                    psProcess = null;
                                }
                                else if (jsonMessage.Language.ToString() == "Batch")
                                {
                                    if (cmdProcess != null && !cmdProcess.HasExited)
                                    {
                                        cmdProcess.CancelOutputRead();
                                        cmdProcess.CancelErrorRead();
                                        cmdProcess.Kill();
                                    }
                                    cmdProcess = null;
                                }
                                jsonMessage.Status = "ok";
                                await SocketSend(jsonMessage);
                                break;
                            case "Ping":
                                LastPingReceived = DateTime.Now;
                                break;
                            default:
                                break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                WriteToLog(ex);
                //Environment.Exit(0);
            }
            if (ConnectionType == "ClientServiceOnce")
            {
                foreach (var proc in Process.GetProcessesByName("InstaTech_Service"))
                {
                    if (proc.Id != Process.GetCurrentProcess().Id)
                    {
                        try
                        {
                            proc.Close();
                            proc.Kill();
                        }
                        catch { }
                    }
                }
                Process.Start("cmd", "/c sc delete InstaTech_Service");
            }
            Environment.Exit(0);
        }

        static private void BeginScreenCapture()
        {
            capturing = true;
            sendFullScreenshot = true;
            SendFrame();
        }
        static async private void SendFrame()
        {
            if (!capturing)
            {
                return;
            }
            try
            {
                var hWnd = User32.GetDesktopWindow();
                var hDC = User32.GetWindowDC(hWnd);
                var graphDC = graphic.GetHdc();
                var copyResult = GDI32.BitBlt(graphDC, 0, 0, totalWidth, totalHeight, hDC, 0 + offsetX, 0 + offsetY, GDI32.TernaryRasterOperations.SRCCOPY | GDI32.TernaryRasterOperations.CAPTUREBLT);
                graphic.ReleaseHdc(graphDC);
                User32.ReleaseDC(hWnd, hDC);
                // Switch desktop if copy fails.
                if (!copyResult)
                {
                    capturing = false;
                    WriteToLog($"Desktop switch initiated from {desktopName} to {User32.GetCurrentDesktop()}.");
                    desktopName = User32.GetCurrentDesktop();
                    var procInfo = new ADVAPI32.PROCESS_INFORMATION();
                    if (ADVAPI32.OpenInteractiveProcess(System.Reflection.Assembly.GetExecutingAssembly().Location + " -interactive", desktopName, out procInfo))
                    {
                        var request = new
                        {
                            Type = "DesktopSwitch",
                            Status = "pending",
                            ComputerName = Environment.MachineName
                        };
                        await SocketSend(request);
                        return;
                    }
                    else
                    {
                        var error = Marshal.GetLastWin32Error();
                        if (error == 6)
                        {
                            WriteToLog("Connection was dropped due to a Windows session change.");
                        }
                        else
                        {
                            WriteToLog(new Exception("Failed to switch desktops.  Error: " + error.ToString()));
                        }
                        return;
                    }
                }
            }
            catch
            {
                return;
            }

            // Get cursor information to draw on the screenshot.
            User32.GetCursorPos(out cursorPos);
            var ci = new User32.CursorInfo();
            ci.cbSize = Marshal.SizeOf(ci);
            User32.GetCursorInfo(out ci);
            if (ci.flags == User32.CURSOR_SHOWING)
            {
                using (var icon = Icon.FromHandle(ci.hCursor))
                {
                    graphic.DrawIcon(icon, ci.ptScreenPos.x, ci.ptScreenPos.y);
                }
            }
            if (sendFullScreenshot)
            {
                var request = new
                {
                    Type = "Bounds",
                    Width = totalWidth,
                    Height = totalHeight
                };
                await SocketSend(request);
                using (var ms = new MemoryStream())
                {
                    screenshot.Save(ms, ImageFormat.Jpeg);
                    ms.WriteByte(0);
                    ms.WriteByte(0);
                    ms.WriteByte(0);
                    ms.WriteByte(0);
                    ms.WriteByte(0);
                    ms.WriteByte(0);
                    await socket.SendAsync(new ArraySegment<byte>(ms.ToArray()), WebSocketMessageType.Binary, true, CancellationToken.None);
                    sendFullScreenshot = false;
                    return;
                }
            }
            newData = GetChangedPixels(screenshot, lastFrame);
            if (newData == null)
            {
                await Task.Delay(100);
                // Ignore async warning here since it's intentional.  This is to prevent deadlock.
#pragma warning disable
                SendFrame();
#pragma warning restore
            }
            else
            {
                using (var ms = new MemoryStream())
                {
                    croppedFrame = screenshot.Clone(boundingBox, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    croppedFrame.Save(ms, ImageFormat.Jpeg);
                    // Add x,y coordinates of top-left of image so receiver knows where to draw it.
                    foreach (var metaByte in newData)
                    {
                        ms.WriteByte(metaByte);
                    }
                    await socket.SendAsync(new ArraySegment<byte>(ms.ToArray()), WebSocketMessageType.Binary, true, CancellationToken.None);
                }
            }
            lastFrame = (Bitmap)screenshot.Clone();
        }
        static public async Task SendHeartbeat()
        {
            try
            {
                if (DateTime.Now - LastPingReceived > TimeSpan.FromMinutes(1) || socket.State != WebSocketState.Open)
                {
                    Environment.Exit(1);
                }
                var uptime = new PerformanceCounter("System", "System Up Time", true);
                uptime.NextValue();
                string currentUser;
                try
                {
                    var mos = new ManagementObjectSearcher("Select * FROM Win32_Process WHERE ExecutablePath LIKE '%explorer.exe%'");
                    var col = mos.Get();
                    var process = col.Cast<ManagementObject>().First();
                    var ownerInfo = new string[2];
                    process.InvokeMethod("GetOwner", ownerInfo);
                    currentUser = ownerInfo[1] + "\\" + ownerInfo[0];
                }
                catch
                {
                    currentUser = "";
                }
                
                // Send notification to server that this connection is for a client service.
                var request = new
                {
                    Type = "Heartbeat",
                    ComputerName = Environment.MachineName,
                    CurrentUser = currentUser,
                    LastReboot = (DateTime.Now - TimeSpan.FromSeconds(uptime.NextValue()))
                };
                await SocketSend(request);
                var di = Directory.CreateDirectory(Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments) + @"\InstaTech\");
                foreach (var file in di.GetFiles())
                {
                    if (DateTime.Now - file.LastWriteTime > TimeSpan.FromDays(1))
                    {
                        try
                        {
                            file.Delete();
                        }
                        catch (Exception ex)
                        {
                            WriteToLog(ex);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                WriteToLog(ex);
            }
        }
        static private byte[] GetChangedPixels(Bitmap bitmap1, Bitmap bitmap2)
        {
            if (bitmap1.Height != bitmap2.Height || bitmap1.Width != bitmap2.Width)
            {
                throw new Exception("Bitmaps are not of equal dimensions.");
            }
            if (!Bitmap.IsAlphaPixelFormat(bitmap1.PixelFormat) || !Bitmap.IsAlphaPixelFormat(bitmap2.PixelFormat) ||
                !Bitmap.IsCanonicalPixelFormat(bitmap1.PixelFormat) || !Bitmap.IsCanonicalPixelFormat(bitmap2.PixelFormat))
            {
                throw new Exception("Bitmaps must be 32 bits per pixel and contain alpha channel.");
            }
            var width = bitmap1.Width;
            var height = bitmap1.Height;
            byte[] newImgData;
            int left = int.MaxValue;
            int top = int.MaxValue;
            int right = int.MinValue;
            int bottom = int.MinValue;

            var bd1 = bitmap1.LockBits(new System.Drawing.Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, bitmap1.PixelFormat);
            var bd2 = bitmap2.LockBits(new System.Drawing.Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, bitmap2.PixelFormat);
            // Get the address of the first line.
            IntPtr ptr1 = bd1.Scan0;
            IntPtr ptr2 = bd2.Scan0;

            // Declare an array to hold the bytes of the bitmap.
            int bytes = Math.Abs(bd1.Stride) * screenshot.Height;
            byte[] rgbValues1 = new byte[bytes];
            byte[] rgbValues2 = new byte[bytes];

            // Copy the RGBA values into the array.
            Marshal.Copy(ptr1, rgbValues1, 0, bytes);
            Marshal.Copy(ptr2, rgbValues2, 0, bytes);

            // Check RGBA value for each pixel.
            for (int counter = 0; counter < rgbValues1.Length - 4; counter += 4)
            {
                if (rgbValues1[counter] != rgbValues2[counter] ||
                    rgbValues1[counter + 1] != rgbValues2[counter + 1] ||
                    rgbValues1[counter + 2] != rgbValues2[counter + 2] ||
                    rgbValues1[counter + 3] != rgbValues2[counter + 3])
                {
                    // Change was found.
                    var pixel = counter / 4;
                    var row = (int)Math.Floor((double)pixel / bd1.Width);
                    var column = pixel % bd1.Width;
                    if (row < top)
                    {
                        top = row;
                    }
                    if (row > bottom)
                    {
                        bottom = row;
                    }
                    if (column < left)
                    {
                        left = column;
                    }
                    if (column > right)
                    {
                        right = column;
                    }
                }
            }
            if (left < right && top < bottom)
            {
                // Bounding box is valid.

                left = Math.Max(left - 20, 0);
                top = Math.Max(top - 20, 0);
                right = Math.Min(right + 20, totalWidth);
                bottom = Math.Min(bottom + 20, totalHeight);

                // Byte array that indicates top left coordinates of the image.
                newImgData = new byte[6];
                newImgData[0] = Byte.Parse(left.ToString().PadLeft(6, '0').Substring(0, 2));
                newImgData[1] = Byte.Parse(left.ToString().PadLeft(6, '0').Substring(2, 2));
                newImgData[2] = Byte.Parse(left.ToString().PadLeft(6, '0').Substring(4, 2));
                newImgData[3] = Byte.Parse(top.ToString().PadLeft(6, '0').Substring(0, 2));
                newImgData[4] = Byte.Parse(top.ToString().PadLeft(6, '0').Substring(2, 2));
                newImgData[5] = Byte.Parse(top.ToString().PadLeft(6, '0').Substring(4, 2));

                boundingBox = new System.Drawing.Rectangle(left, top, right - left, bottom - top);
                bitmap1.UnlockBits(bd1);
                bitmap2.UnlockBits(bd2);
                return newImgData;
            }
            else
            {
                bitmap1.UnlockBits(bd1);
                bitmap2.UnlockBits(bd2);
                return null;
            }
        }
        static private async Task SocketSend(dynamic JsonRequest)
        {
            var jsonRequest = JSON.Encode(JsonRequest);
            var outBuffer = new ArraySegment<byte>(Encoding.UTF8.GetBytes(jsonRequest));
            socketSendBuffer.Add(outBuffer);
            if (socketSendBuffer.Count > 1)
            {
                return;
            }
            while (socketSendBuffer.Count > 0)
            {
                try
                {
                    await socket.SendAsync(socketSendBuffer[0], WebSocketMessageType.Text, true, CancellationToken.None);
                    socketSendBuffer.RemoveAt(0);
                }
                catch (Exception ex)
                {
                    socketSendBuffer.Clear();
                    throw ex;
                }
            }
        }
        static private async Task CheckForUpdates()
        {
            WebClient webClient = new WebClient();
            HttpClient httpClient = new HttpClient();
            Directory.CreateDirectory(Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments) + @"\InstaTech\");
            var strFilePath = Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments) + @"\InstaTech\" + Path.GetFileName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            HttpResponseMessage response;
            if (File.Exists(strFilePath))
            {
                File.Delete(strFilePath);
            }
            try
            {
                response = await httpClient.GetAsync(Socket.versionURI);

            }
            catch
            {
                return;
            }
            var strCurrentVersion = await response.Content.ReadAsStringAsync();
            var thisVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            var currentVersion = Version.Parse(strCurrentVersion);
            if (currentVersion != thisVersion && currentVersion > new Version(0, 0, 0, 0))
            {
                var request = new
                {
                    Type = "ClientUpdating",
                };
                await SocketSend(request);
                Socket.WriteToLog("Update download initiated.");
                await webClient.DownloadFileTaskAsync(new Uri(Socket.downloadURI), strFilePath);
                Socket.WriteToLog("Download complete.  Launching file.");
                Process.Start(strFilePath, "-install");
                Environment.Exit(0);
                return;
            }
        }
        public static void WriteToLog(Exception ex)
        {
            var exception = ex;
            var path = Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments) + @"\InstaTech_Service_Logs.txt";

            while (exception != null)
            {
                var jsonError = new
                {
                    Type = "Error",
                    Timestamp = DateTime.Now.ToString(),
                    Message = exception?.Message,
                    Source = exception?.Source,
                    StackTrace = exception?.StackTrace,
                };
                if (File.Exists(path))
                {
                    var fi = new FileInfo(path);
                    while (fi.Length > 1000000)
                    {
                        var content = File.ReadAllLines(path);
                        File.WriteAllLines(path, content.Skip(10));
                        fi = new FileInfo(path);
                    }
                }
                File.AppendAllText(path, JSON.Encode(jsonError) + Environment.NewLine);
                exception = exception.InnerException;
            }
        }
        public static void WriteToLog(string Message)
        {
            var path = Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments) + @"\InstaTech_Service_Logs.txt";

            var jsoninfo = new
            {
                Type = "Info",
                Timestamp = DateTime.Now.ToString(),
                Message = Message
            };
            if (File.Exists(path))
            {
                var fi = new FileInfo(path);
                while (fi.Length > 1000000)
                {
                    var content = File.ReadAllLines(path);
                    File.WriteAllLines(path, content.Skip(10));
                    fi = new FileInfo(path);
                }
            }
            File.AppendAllText(path, JSON.Encode(jsoninfo) + Environment.NewLine);
        }
    }
}
