﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using SysBot.Base;
using SysBot.Pokemon.SV.BotRaid.Helpers;
using SysBot.Pokemon.WinForms.WebApi;

namespace SysBot.Pokemon.WinForms;

public static class WebApiExtensions
{
    private static BotServer? _server;
    private static TcpListener? _tcp;
    private static CancellationTokenSource? _cts;
    private static CancellationTokenSource? _monitorCts;
    private static Main? _main;

    private const int WebPort = 8080;
    private static int _tcpPort = 0;

    public static void InitWebServer(this Main mainForm)
    {
        _main = mainForm;

        try
        {
            if (IsPortInUse(WebPort))
            {
                LogUtil.LogInfo($"Web port {WebPort} is in use by another bot instance. Starting as slave...", "WebServer");
                _tcpPort = FindAvailablePort(8081);
                StartTcpOnly();
                LogUtil.LogInfo($"Slave instance started with TCP port {_tcpPort}. Monitoring master...", "WebServer");

                // Start monitoring for master failure
                StartMasterMonitor();
                return;
            }

            // Try to add URL reservation for network access
            TryAddUrlReservation(WebPort);

            _tcpPort = FindAvailablePort(8081);
            LogUtil.LogInfo($"Starting as master web server on port {WebPort} with TCP port {_tcpPort}", "WebServer");
            StartFullServer();
            LogUtil.LogInfo($"Web interface is available at http://localhost:{WebPort}", "WebServer");
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Failed to initialize web server: {ex.Message}", "WebServer");
        }
    }

    private static void StartMasterMonitor()
    {
        _monitorCts = new CancellationTokenSource();

        Task.Run(async () =>
        {
            var random = new Random();

            while (!_monitorCts.Token.IsCancellationRequested)
            {
                try
                {
                    // Check every 10-15 seconds (randomized to prevent race conditions)
                    await Task.Delay(10000 + random.Next(5000), _monitorCts.Token);

                    if (!IsPortInUse(WebPort))
                    {
                        LogUtil.LogInfo("Master web server is down. Attempting to take over...", "WebServer");

                        // Wait a random amount to reduce race conditions between multiple slaves
                        await Task.Delay(random.Next(1000, 3000));

                        // Double-check that no one else took over
                        if (!IsPortInUse(WebPort))
                        {
                            TryTakeOverAsMaster();
                            break; // Stop monitoring once we've taken over
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogUtil.LogError($"Error in master monitor: {ex.Message}", "WebServer");
                }
            }
        }, _monitorCts.Token);
    }

    private static void TryTakeOverAsMaster()
    {
        try
        {
            // Try to add URL reservation
            TryAddUrlReservation(WebPort);

            // Start the web server
            _server = new BotServer(_main!, WebPort, _tcpPort);
            _server.Start();

            // Stop the monitor since we're now the master
            _monitorCts?.Cancel();
            _monitorCts = null;

            LogUtil.LogInfo($"Successfully took over as master web server on port {WebPort}", "WebServer");
            LogUtil.LogInfo($"Web interface is now available at http://localhost:{WebPort}", "WebServer");

            // Show notification to user if possible
            if (_main != null)
            {
                _main.BeginInvoke((MethodInvoker)(() =>
                {
                    System.Windows.Forms.MessageBox.Show(
                        $"This instance has taken over as the master web server.\n\nWeb interface available at:\nhttp://localhost:{WebPort}",
                        "Master Server Takeover",
                        System.Windows.Forms.MessageBoxButtons.OK,
                        System.Windows.Forms.MessageBoxIcon.Information);
                }));
            }
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Failed to take over as master: {ex.Message}", "WebServer");

            // If we failed, go back to monitoring
            StartMasterMonitor();
        }
    }

    private static bool TryAddUrlReservation(int port)
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = $"http add urlacl url=http://+:{port}/ user=Everyone",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                Verb = "runas" // Request admin privileges
            };

            using var process = System.Diagnostics.Process.Start(startInfo);
            process?.WaitForExit(5000);
            return process?.ExitCode == 0;
        }
        catch
        {
            // If we can't add the reservation, the server will fall back to localhost
            return false;
        }
    }

    private static void StartTcpOnly()
    {
        CreatePortFile();
        StartTcp();
    }

    private static void StartFullServer()
    {
        CreatePortFile();
        _server = new BotServer(_main!, WebPort, _tcpPort);
        _server.Start();
        StartTcp();
    }

    private static void StartTcp()
    {
        _cts = new CancellationTokenSource();

        Task.Run(async () =>
        {
            try
            {
                _tcp = new TcpListener(System.Net.IPAddress.Loopback, _tcpPort);
                _tcp.Start();

                while (!_cts.Token.IsCancellationRequested)
                {
                    var tcpTask = _tcp.AcceptTcpClientAsync();
                    var tcs = new TaskCompletionSource<bool>();

                    using (var registration = _cts.Token.Register(() => tcs.SetCanceled()))
                    {
                        var completedTask = await Task.WhenAny(tcpTask, tcs.Task);
                        if (completedTask == tcpTask && tcpTask.IsCompletedSuccessfully)
                        {
                            _ = Task.Run(() => HandleClient(tcpTask.Result));
                        }
                    }
                }
            }
            catch (Exception ex) when (!_cts.Token.IsCancellationRequested)
            {
                LogUtil.LogError($"TCP listener error: {ex.Message}", "TCP");
            }
        });
    }

    private static async Task HandleClient(TcpClient client)
    {
        try
        {
            using (client)
            {
                client.ReceiveTimeout = 5000;
                client.SendTimeout = 5000;

                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

                var command = await reader.ReadLineAsync();
                if (!string.IsNullOrEmpty(command))
                {
                    var response = ProcessCommand(command);
                    await writer.WriteLineAsync(response);
                    await stream.FlushAsync();
                    await Task.Delay(100);
                }
            }
        }
        catch (Exception ex) when (!(ex is IOException { InnerException: SocketException }))
        {
            LogUtil.LogError($"Error handling TCP client: {ex.Message}", "TCP");
        }
    }

    private static string ProcessCommand(string command)
    {
        if (_main == null)
            return "ERROR: Main form not initialized";

        var parts = command.Split(':');
        var cmd = parts[0].ToUpperInvariant();
        var botId = parts.Length > 1 ? parts[1] : null;

        return cmd switch
        {
            "STARTALL" => ExecuteGlobalCommand(BotControlCommand.Start),
            "STOPALL" => ExecuteGlobalCommand(BotControlCommand.Stop),
            "IDLEALL" => ExecuteGlobalCommand(BotControlCommand.Idle),
            "RESUMEALL" => ExecuteGlobalCommand(BotControlCommand.Resume),
            "RESTARTALL" => ExecuteGlobalCommand(BotControlCommand.Restart),
            "REBOOTALL" => ExecuteGlobalCommand(BotControlCommand.RebootAndStop),
            "REFRESHMAPALL" => ExecuteGlobalCommand(BotControlCommand.RefreshMap),
            "SCREENONALL" => ExecuteGlobalCommand(BotControlCommand.ScreenOnAll),
            "SCREENOFFALL" => ExecuteGlobalCommand(BotControlCommand.ScreenOffAll),
            "LISTBOTS" => GetBotsList(),
            "STATUS" => GetBotStatuses(botId),
            "ISREADY" => CheckReady(),
            "INFO" => GetInstanceInfo(),
            "VERSION" => GetVersionForBotType(DetectBotType()),
            "UPDATE" => TriggerUpdate(),
            _ => $"ERROR: Unknown command '{cmd}'"
        };
    }

    private static string TriggerUpdate()
    {
        try
        {
            if (_main == null)
                return "ERROR: Main form not initialized";

            var botType = DetectBotType();

            _main.BeginInvoke((MethodInvoker)(async () =>
            {
                try
                {
                    bool updateAvailable = false;
                    string newVersion = "";

                    if (botType == BotType.RaidBot)
                    {
                        // Use RaidBot UpdateChecker
                        var (available, _, version) = await UpdateChecker.CheckForUpdatesAsync(false);
                        updateAvailable = available;
                        newVersion = version;
                    }
                    else if (botType == BotType.PokeBot)
                    {
                        // Use PokeBot UpdateChecker
                        var pokeBotUpdateCheckerType = Type.GetType("SysBot.Pokemon.Helpers.UpdateChecker, SysBot.Pokemon");
                        if (pokeBotUpdateCheckerType != null)
                        {
                            var checkMethod = pokeBotUpdateCheckerType.GetMethod("CheckForUpdatesAsync");
                            if (checkMethod != null)
                            {
                                var task = (Task<(bool, string, string)>)checkMethod.Invoke(null, new object[] { false });
                                var result = await task;
                                updateAvailable = result.Item1;
                                newVersion = result.Item3;
                            }
                        }
                    }

                    if (updateAvailable && !string.IsNullOrEmpty(newVersion))
                    {
                        var updateForm = new UpdateForm(false, newVersion, true);
                        updateForm.PerformUpdate();
                    }
                }
                catch (Exception ex)
                {
                    LogUtil.LogError($"Error in TriggerUpdate: {ex.Message}", "WebAPI");
                }
            }));

            return "OK: Update triggered";
        }
        catch (Exception ex)
        {
            return $"ERROR: {ex.Message}";
        }
    }

    private static string ExecuteGlobalCommand(BotControlCommand command)
    {
        try
        {
            _main!.BeginInvoke((MethodInvoker)(() =>
            {
                var sendAllMethod = _main.GetType().GetMethod("SendAll",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                sendAllMethod?.Invoke(_main, new object[] { command });
            }));

            return $"OK: {command} command sent to all bots";
        }
        catch (Exception ex)
        {
            return $"ERROR: Failed to execute {command} - {ex.Message}";
        }
    }

    private static string GetBotsList()
    {
        try
        {
            var botList = new List<object>();
            var config = GetConfig();
            var controllers = GetBotControllers();

            // If no controllers found in UI, try to get from Bots property
            if (controllers.Count == 0)
            {
                var botsProperty = _main!.GetType().GetProperty("Bots",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                if (botsProperty?.GetValue(_main) is List<PokeBotState> bots)
                {
                    foreach (var bot in bots)
                    {
                        botList.Add(new
                        {
                            Id = $"{bot.Connection.IP}:{bot.Connection.Port}",
                            Name = bot.Connection.IP,
                            RoutineType = bot.InitialRoutine.ToString(),
                            Status = "Unknown",
                            ConnectionType = bot.Connection.Protocol.ToString(),
                            bot.Connection.IP,
                            bot.Connection.Port
                        });
                    }

                    return System.Text.Json.JsonSerializer.Serialize(new { Bots = botList });
                }
            }

            // Use controllers if available
            foreach (var controller in controllers)
            {
                var state = controller.State;
                var botName = GetBotName(state, config);
                var status = controller.ReadBotState();

                botList.Add(new
                {
                    Id = $"{state.Connection.IP}:{state.Connection.Port}",
                    Name = botName,
                    RoutineType = state.InitialRoutine.ToString(),
                    Status = status,
                    ConnectionType = state.Connection.Protocol.ToString(),
                    state.Connection.IP,
                    state.Connection.Port
                });
            }

            return System.Text.Json.JsonSerializer.Serialize(new { Bots = botList });
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"GetBotsList error: {ex.Message}", "WebAPI");
            return $"ERROR: Failed to get bots list - {ex.Message}";
        }
    }

    private static string GetBotStatuses(string? botId)
    {
        try
        {
            var config = GetConfig();
            var controllers = GetBotControllers();

            if (string.IsNullOrEmpty(botId))
            {
                var statuses = controllers.Select(c => new
                {
                    Id = $"{c.State.Connection.IP}:{c.State.Connection.Port}",
                    Name = GetBotName(c.State, config),
                    Status = c.ReadBotState()
                }).ToList();

                return System.Text.Json.JsonSerializer.Serialize(statuses);
            }

            var botController = controllers.FirstOrDefault(c =>
                $"{c.State.Connection.IP}:{c.State.Connection.Port}" == botId);

            return botController?.ReadBotState() ?? "ERROR: Bot not found";
        }
        catch (Exception ex)
        {
            return $"ERROR: Failed to get status - {ex.Message}";
        }
    }

    private static string CheckReady()
    {
        try
        {
            var controllers = GetBotControllers();
            var hasRunningBots = controllers.Any(c => c.GetBot()?.IsRunning ?? false);
            return hasRunningBots ? "READY" : "NOT_READY";
        }
        catch
        {
            return "NOT_READY";
        }
    }

    private static string GetInstanceInfo()
    {
        try
        {
            var config = GetConfig();
            var version = GetVersion();
            var mode = config?.Mode.ToString() ?? "Unknown";
            var name = GetInstanceName(config, mode);

            var info = new
            {
                Version = version,
                Mode = mode,
                Name = name,
                BotType = "RaidBot",
                Environment.ProcessId,
                Port = _tcpPort
            };

            return System.Text.Json.JsonSerializer.Serialize(info);
        }
        catch (Exception ex)
        {
            return $"ERROR: Failed to get instance info - {ex.Message}";
        }
    }

    private static List<BotController> GetBotControllers()
    {
        var flpBotsField = _main!.GetType().GetField("FLP_Bots",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        if (flpBotsField?.GetValue(_main) is FlowLayoutPanel flpBots)
        {
            return flpBots.Controls.OfType<BotController>().ToList();
        }

        return new List<BotController>();
    }

    private static ProgramConfig? GetConfig()
    {
        var configProp = _main?.GetType().GetProperty("Config",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return configProp?.GetValue(_main) as ProgramConfig;
    }

    private static string GetBotName(PokeBotState state, ProgramConfig? config)
    {
        // Always return IP address as the bot name
        return state.Connection.IP;
    }

    private static string GetVersion()
    {
        return SVRaidBot.Version;
    }

    private static string GetInstanceName(ProgramConfig? config, string mode)
    {
        if (!string.IsNullOrEmpty(config?.Hub?.BotName))
            return config.Hub.BotName;

        return mode switch
        {
            "LGPE" => "LGPE",
            "BDSP" => "BDSP",
            "SWSH" => "SWSH",
            "SV" => "SV",
            "LA" => "LA",
            _ => "SVRaidBot"
        };
    }

    private static void CreatePortFile()
    {
        try
        {
            var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
            var exeDir = Path.GetDirectoryName(exePath) ?? Program.WorkingDirectory;
            var portFile = Path.Combine(exeDir, $"SVRaidBot_{Environment.ProcessId}.port");
            File.WriteAllText(portFile, _tcpPort.ToString());
            LogUtil.LogInfo($"Created port file: {portFile} with TCP port {_tcpPort}", "WebServer");
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Failed to create port file: {ex.Message}", "WebServer");
        }
    }

    private static void CleanupPortFile()
    {
        try
        {
            var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
            var exeDir = Path.GetDirectoryName(exePath) ?? Program.WorkingDirectory;
            var portFile = Path.Combine(exeDir, $"SVRaidBot_{Environment.ProcessId}.port");

            if (File.Exists(portFile))
                File.Delete(portFile);
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Failed to cleanup port file: {ex.Message}", "WebServer");
        }
    }

    private static int FindAvailablePort(int startPort)
    {
        for (int port = startPort; port < startPort + 100; port++)
        {
            if (!IsPortInUse(port))
                return port;
        }
        throw new InvalidOperationException("No available ports found");
    }

    private static bool IsPortInUse(int port)
    {
        try
        {
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMilliseconds(200) };
            var response = client.GetAsync($"http://localhost:{port}/api/bot/instances").Result;
            return response.IsSuccessStatusCode;
        }
        catch
        {
            // Also check TCP
            try
            {
                using var tcpClient = new TcpClient();
                var result = tcpClient.BeginConnect("127.0.0.1", port, null, null);
                var success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(200));
                if (success)
                {
                    tcpClient.EndConnect(result);
                    return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }
    }

    public static void StopWebServer(this Main mainForm)
    {
        try
        {
            _cts?.Cancel();
            _tcp?.Stop();
            _server?.Dispose();
            CleanupPortFile();
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Error stopping web server: {ex.Message}", "WebServer");
        }
    }

    public static void StartWebServer(this Main mainForm, int port = 8080, int tcpPort = 8081)
    {
        try
        {
            var server = new BotServer(mainForm, port, tcpPort);
            server.Start();

            // Create port files for both bot types for universal detection
            CreatePortFiles(tcpPort);

            var config = GetConfig();
            LogUtil.LogInfo($"Web server started on port {port}, TCP on {tcpPort}", "WebServer");
            LogUtil.LogInfo($"Access the control panel at: http://localhost:{port}/", "WebServer");

            var property = mainForm.GetType().GetProperty("WebServer",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            property?.SetValue(mainForm, server);
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Failed to start web server: {ex.Message}", "WebServer");
        }
    }

    private static void CreatePortFiles(int tcpPort)
    {
        try
        {
            var exePath = Application.ExecutablePath;
            var exeDir = Path.GetDirectoryName(exePath);
            var processId = Environment.ProcessId;

            // Create both bot type port files for universal detection
            var pokeBotPortFile = Path.Combine(exeDir!, $"PokeBot_{processId}.port");
            var raidBotPortFile = Path.Combine(exeDir!, $"SVRaidBot_{processId}.port");

            // Delete existing files first
            if (File.Exists(pokeBotPortFile))
                File.Delete(pokeBotPortFile);
            if (File.Exists(raidBotPortFile))
                File.Delete(raidBotPortFile);

            // Create the appropriate port file based on bot type
            var botType = DetectBotType();
            if (botType == BotType.RaidBot)
            {
                File.WriteAllText(raidBotPortFile, tcpPort.ToString());
                LogUtil.LogInfo($"Created RaidBot port file: {raidBotPortFile}", "WebServer");
            }
            else if (botType == BotType.PokeBot)
            {
                File.WriteAllText(pokeBotPortFile, tcpPort.ToString());
                LogUtil.LogInfo($"Created PokeBot port file: {pokeBotPortFile}", "WebServer");
            }
            else
            {
                // Unknown type, create both for safety
                File.WriteAllText(raidBotPortFile, tcpPort.ToString());
                File.WriteAllText(pokeBotPortFile, tcpPort.ToString());
                LogUtil.LogInfo($"Created both bot type port files (Unknown type detected)", "WebServer");
            }
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Failed to create port file: {ex.Message}", "WebServer");
        }
    }

    private enum BotType
    {
        PokeBot,
        RaidBot,
        Unknown
    }

    private static BotType DetectBotType()
    {
        try
        {
            // Try to detect RaidBot first (since this is in RaidBot folder)
            var raidBotType = Type.GetType("SysBot.Pokemon.SV.BotRaid.Helpers.SVRaidBot, SysBot.Pokemon");
            if (raidBotType != null)
                return BotType.RaidBot;

            // Try to detect PokeBot
            var pokeBotType = Type.GetType("SysBot.Pokemon.Helpers.PokeBot, SysBot.Pokemon");
            if (pokeBotType != null)
                return BotType.PokeBot;

            return BotType.Unknown;
        }
        catch
        {
            return BotType.Unknown;
        }
    }

    private static string GetVersionForBotType(BotType botType)
    {
        switch (botType)
        {
            case BotType.RaidBot:
                return SVRaidBot.Version;
            case BotType.PokeBot:
                var pokeBotType = Type.GetType("SysBot.Pokemon.Helpers.PokeBot, SysBot.Pokemon");
                if (pokeBotType != null)
                {
                    var pokeBot = Activator.CreateInstance(pokeBotType);
                    return pokeBot?.GetType().Assembly.GetName().Version?.ToString() ?? "N/A";
                }
                break;
            case BotType.Unknown:
                return "N/A";
        }
        return "N/A";
    }


    public static string HandleApiRequest(this Main mainForm, string command)
    {
        try
        {
            var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var cmd = parts[0].ToUpper();

            return cmd switch
            {
                "STATUS" => GetStatusResponse(mainForm),
                "START" => HandleStartCommand(mainForm, parts),
                "STOP" => HandleStopCommand(mainForm, parts),
                "IDLE" => HandleIdleCommand(mainForm, parts),
                "STARTALL" => HandleStartAllCommand(mainForm),
                "STOPALL" => HandleStopAllCommand(mainForm),
                "IDLEALL" => HandleIdleAllCommand(mainForm),
                "LISTBOTS" => GetBotsListResponse(mainForm),
                "INFO" => GetInfoResponse(mainForm),
                "VERSION" => GetVersionResponse(),
                "UPDATE" => HandleUpdateCommand(mainForm),
                "REFRESHMAPALL" => HandleRefreshMapAllCommand(mainForm),
                _ => CreateErrorResponse($"Unknown command: {cmd}")
            };
        }
        catch (Exception ex)
        {
            return CreateErrorResponse($"Error processing command: {ex.Message}");
        }
    }

    private static string HandleRefreshMapAllCommand(Main mainForm)
    {
        try
        {
            // Check if this is a RaidBot
            var botType = DetectBotType();
            if (botType != BotType.RaidBot)
            {
                return CreateErrorResponse("REFRESHMAPALL command is only available for RaidBot instances");
            }

            var flpBotsField = mainForm.GetType().GetField("FLP_Bots",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            if (flpBotsField?.GetValue(mainForm) is FlowLayoutPanel flpBots)
            {
                var controllers = flpBots.Controls.OfType<BotController>().ToList();
                var refreshed = 0;

                foreach (var controller in controllers)
                {
                    try
                    {
                        // Send refresh map command to each bot
                        controller.SendCommand(BotControlCommand.RefreshMap, false);
                        refreshed++;
                    }
                    catch (Exception ex)
                    {
                        LogUtil.LogError($"Failed to refresh map for bot: {ex.Message}", "WebServer");
                    }
                }

                return $"Refresh map command sent to {refreshed} RaidBot instances";
            }

            return CreateErrorResponse("No bots found to refresh");
        }
        catch (Exception ex)
        {
            return CreateErrorResponse($"Error refreshing maps: {ex.Message}");
        }
    }

    private static string GetStatusResponse(Main mainForm)
    {
        try
        {
            var config = GetConfig();
            var controllers = GetBotControllers();
            
            var statuses = controllers.Select(c => new
            {
                Name = GetBotName(c.State, config),
                Status = c.ReadBotState(),
                IP = c.State.Connection.IP,
                Port = c.State.Connection.Port
            }).ToList();

            return System.Text.Json.JsonSerializer.Serialize(new { Bots = statuses });
        }
        catch (Exception ex)
        {
            return CreateErrorResponse($"Error getting status: {ex.Message}");
        }
    }

    private static string HandleStartCommand(Main mainForm, string[] parts)
    {
        try
        {
            if (parts.Length > 1)
            {
                // Start specific bot by ID/IP
                var botId = parts[1];
                var controllers = GetBotControllers();
                var controller = controllers.FirstOrDefault(c => 
                    c.State.Connection.IP == botId || 
                    $"{c.State.Connection.IP}:{c.State.Connection.Port}" == botId);
                
                if (controller != null)
                {
                    controller.SendCommand(BotControlCommand.Start, false);
                    return $"Start command sent to bot {botId}";
                }
                return CreateErrorResponse($"Bot {botId} not found");
            }
            else
            {
                // Start all bots
                return ExecuteGlobalCommand(BotControlCommand.Start);
            }
        }
        catch (Exception ex)
        {
            return CreateErrorResponse($"Error starting bots: {ex.Message}");
        }
    }

    private static string HandleStopCommand(Main mainForm, string[] parts)
    {
        try
        {
            if (parts.Length > 1)
            {
                // Stop specific bot by ID/IP
                var botId = parts[1];
                var controllers = GetBotControllers();
                var controller = controllers.FirstOrDefault(c => 
                    c.State.Connection.IP == botId || 
                    $"{c.State.Connection.IP}:{c.State.Connection.Port}" == botId);
                
                if (controller != null)
                {
                    controller.SendCommand(BotControlCommand.Stop, false);
                    return $"Stop command sent to bot {botId}";
                }
                return CreateErrorResponse($"Bot {botId} not found");
            }
            else
            {
                // Stop all bots
                return ExecuteGlobalCommand(BotControlCommand.Stop);
            }
        }
        catch (Exception ex)
        {
            return CreateErrorResponse($"Error stopping bots: {ex.Message}");
        }
    }

    private static string HandleIdleCommand(Main mainForm, string[] parts)
    {
        try
        {
            if (parts.Length > 1)
            {
                // Idle specific bot by ID/IP
                var botId = parts[1];
                var controllers = GetBotControllers();
                var controller = controllers.FirstOrDefault(c => 
                    c.State.Connection.IP == botId || 
                    $"{c.State.Connection.IP}:{c.State.Connection.Port}" == botId);
                
                if (controller != null)
                {
                    controller.SendCommand(BotControlCommand.Idle, false);
                    return $"Idle command sent to bot {botId}";
                }
                return CreateErrorResponse($"Bot {botId} not found");
            }
            else
            {
                // Idle all bots
                return ExecuteGlobalCommand(BotControlCommand.Idle);
            }
        }
        catch (Exception ex)
        {
            return CreateErrorResponse($"Error idling bots: {ex.Message}");
        }
    }

    private static string HandleStartAllCommand(Main mainForm)
    {
        return ExecuteGlobalCommand(BotControlCommand.Start);
    }

    private static string HandleStopAllCommand(Main mainForm)
    {
        return ExecuteGlobalCommand(BotControlCommand.Stop);
    }

    private static string HandleIdleAllCommand(Main mainForm)
    {
        return ExecuteGlobalCommand(BotControlCommand.Idle);
    }

    private static string GetBotsListResponse(Main mainForm)
    {
        return GetBotsList();
    }

    private static string GetInfoResponse(Main mainForm)
    {
        return GetInstanceInfo();
    }

    private static string GetVersionResponse()
    {
        return SVRaidBot.Version;
    }

    private static string HandleUpdateCommand(Main mainForm)
    {
        return TriggerUpdate();
    }

    private static string CreateErrorResponse(string message)
    {
        return System.Text.Json.JsonSerializer.Serialize(new { Error = message, Timestamp = DateTime.Now });
    }
}