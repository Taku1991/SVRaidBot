﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using SysBot.Base;

namespace SysBot.Pokemon.WinForms.WebApi;

public class BotServer(Main mainForm, int port = 8080, int tcpPort = 8081) : IDisposable
{
    private HttpListener? _listener;
    private Thread? _listenerThread;
    private readonly int _port = port;
    private readonly int _tcpPort = tcpPort;
    private readonly CancellationTokenSource _cts = new();
    private readonly Main _mainForm = mainForm ?? throw new ArgumentNullException(nameof(mainForm));
    private volatile bool _running;
    private string? _htmlTemplate;

    // Bot type detection cache
    private static readonly Dictionary<int, BotType> _botTypeCache = new();

    public enum BotType
    {
        PokeBot,
        RaidBot,
        Unknown
    }

    private string HtmlTemplate
    {
        get
        {
            if (_htmlTemplate == null)
            {
                _htmlTemplate = LoadEmbeddedResource("BotControlPanel.html");
            }
            return _htmlTemplate;
        }
    }

    private static string LoadEmbeddedResource(string resourceName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var fullResourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(resourceName, StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrEmpty(fullResourceName))
        {
            throw new FileNotFoundException($"Embedded resource '{resourceName}' not found. Available resources: {string.Join(", ", assembly.GetManifestResourceNames())}");
        }

        using var stream = assembly.GetManifestResourceStream(fullResourceName);
        if (stream == null)
        {
            throw new FileNotFoundException($"Could not load embedded resource '{fullResourceName}'");
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public void Start()
    {
        if (_running) return;

        try
        {
            _listener = new HttpListener();

            try
            {
                _listener.Prefixes.Add($"http://+:{_port}/");
                _listener.Start();
                LogUtil.LogInfo($"Web server listening on all interfaces at port {_port}", "WebServer");
            }
            catch (HttpListenerException ex) when (ex.ErrorCode == 5)
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://localhost:{_port}/");
                _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
                _listener.Start();

                LogUtil.LogError($"Web server requires administrator privileges for network access. Currently limited to localhost only.", "WebServer");
                LogUtil.LogInfo("To enable network access, either:", "WebServer");
                LogUtil.LogInfo("1. Run this application as Administrator", "WebServer");
                LogUtil.LogInfo($"2. Or run this command as admin: netsh http add urlacl url=http://+:{_port}/ user=Everyone", "WebServer");
            }

            _running = true;

            _listenerThread = new Thread(Listen)
            {
                IsBackground = true,
                Name = "BotWebServer"
            };
            _listenerThread.Start();
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Failed to start web server: {ex.Message}", "WebServer");
            throw;
        }
    }

    public void Stop()
    {
        if (!_running) return;

        try
        {
            _running = false;
            _cts.Cancel();
            _listener?.Stop();
            _listenerThread?.Join(5000);
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Error stopping web server: {ex.Message}", "WebServer");
        }
    }

    private void Listen()
    {
        while (_running && _listener != null)
        {
            try
            {
                var asyncResult = _listener.BeginGetContext(null, null);

                while (_running && !asyncResult.AsyncWaitHandle.WaitOne(100))
                {
                    // Check if we should continue listening
                }

                if (!_running)
                    break;

                var context = _listener.EndGetContext(asyncResult);

                ThreadPool.QueueUserWorkItem(async _ =>
                {
                    try
                    {
                        await HandleRequest(context);
                    }
                    catch (Exception ex)
                    {
                        LogUtil.LogError($"Error handling request: {ex.Message}", "WebServer");
                    }
                });
            }
            catch (HttpListenerException ex) when (!_running || ex.ErrorCode == 995)
            {
                break;
            }
            catch (ObjectDisposedException) when (!_running)
            {
                break;
            }
            catch (Exception ex)
            {
                if (_running)
                {
                    LogUtil.LogError($"Error in listener: {ex.Message}", "WebServer");
                }
            }
        }
    }

    private async Task HandleRequest(HttpListenerContext context)
    {
        HttpListenerResponse? response = null;
        try
        {
            var request = context.Request;
            response = context.Response;

            response.Headers.Add("Access-Control-Allow-Origin", "*");
            response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

            if (request.HttpMethod == "OPTIONS")
            {
                response.StatusCode = 200;
                response.Close();
                return;
            }

            string? responseString = request.Url?.LocalPath switch
            {
                "/" => HtmlTemplate,
                "/api/bot/instances" => GetInstances(),
                var path when path?.StartsWith("/api/bot/instances/") == true && path.EndsWith("/bots") =>
                    GetBots(path),
                var path when path?.StartsWith("/api/bot/instances/") == true && path.EndsWith("/command") =>
                    await RunCommand(request, path),
                "/api/bot/command/all" => await RunAllCommand(request),
                "/api/bot/update/check" => await CheckForUpdates(),
                "/api/bot/update/idle-status" => GetIdleStatus(),
                "/api/bot/update/all" => await UpdateAllInstances(request),
                _ => null
            };

            if (responseString == null)
            {
                response.StatusCode = 404;
                responseString = "Not Found";
            }
            else
            {
                response.StatusCode = 200;
                response.ContentType = request.Url?.LocalPath == "/" ? "text/html" : "application/json";
            }

            var buffer = Encoding.UTF8.GetBytes(responseString);
            response.ContentLength64 = buffer.Length;

            try
            {
                await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                await response.OutputStream.FlushAsync();
            }
            catch (HttpListenerException ex) when (ex.ErrorCode == 64 || ex.ErrorCode == 1229)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (InvalidOperationException)
            {
                return;
            }
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Error processing request: {ex.Message}", "WebServer");

            if (response != null && response.OutputStream.CanWrite)
            {
                try
                {
                    response.StatusCode = 500;
                }
                catch { }
            }
        }
        finally
        {
            try
            {
                response?.Close();
            }
            catch { }
        }
    }
    private async Task<string> UpdateAllInstances(HttpListenerRequest request)
    {
        try
        {
            // Read request body to check for stage parameter
            using var reader = new StreamReader(request.InputStream);
            var body = await reader.ReadToEndAsync();
            var stage = "start"; // default

            if (!string.IsNullOrEmpty(body))
            {
                try
                {
                    var requestData = JsonSerializer.Deserialize<Dictionary<string, string>>(body);
                    if (requestData?.ContainsKey("stage") == true)
                    {
                        stage = requestData["stage"];
                    }
                }
                catch { }
            }

            if (stage == "proceed")
            {
                // Proceed with actual updates after all bots are idle
                var result = await UpdateManager.ProceedWithUpdatesAsync(_mainForm, _tcpPort);

                return JsonSerializer.Serialize(new
                {
                    Stage = "updating",
                    Success = result.UpdatesFailed == 0 && result.UpdatesNeeded > 0,
                    result.TotalInstances,
                    result.UpdatesNeeded,
                    result.UpdatesStarted,
                    result.UpdatesFailed,
                    Results = result.InstanceResults.Select(r => new
                    {
                        r.Port,
                        r.ProcessId,
                        r.CurrentVersion,
                        r.LatestVersion,
                        r.NeedsUpdate,
                        r.UpdateStarted,
                        r.Error
                    })
                });
            }
            else
            {
                // Start the idle process
                var result = await UpdateManager.StartUpdateProcessAsync(_mainForm, _tcpPort);

                return JsonSerializer.Serialize(new
                {
                    Stage = "idling",
                    Success = result.UpdatesFailed == 0,
                    result.TotalInstances,
                    result.UpdatesNeeded,
                    Results = result.InstanceResults.Select(r => new
                    {
                        r.Port,
                        r.ProcessId,
                        r.CurrentVersion,
                        r.LatestVersion,
                        r.NeedsUpdate,
                        r.Error
                    })
                });
            }
        }
        catch (Exception ex)
        {
            return CreateErrorResponse(ex.Message);
        }
    }

    private string GetIdleStatus()
    {
        try
        {
            var instances = new List<object>();

            // Local instance
            var localBots = GetBotControllers();
            var localIdleCount = 0;
            var localTotalCount = localBots.Count;
            var localNonIdleBots = new List<object>();

            foreach (var controller in localBots)
            {
                var status = controller.ReadBotState();
                var upperStatus = status?.ToUpper() ?? "";

                if (upperStatus == "IDLE" || upperStatus == "STOPPED")
                {
                    localIdleCount++;
                }
                else
                {
                    localNonIdleBots.Add(new
                    {
                        Name = GetBotName(controller.State, GetConfig()),
                        Status = status
                    });
                }
            }

            instances.Add(new
            {
                Port = _tcpPort,
                Environment.ProcessId,
                TotalBots = localTotalCount,
                IdleBots = localIdleCount,
                NonIdleBots = localNonIdleBots,
                AllIdle = localIdleCount == localTotalCount
            });

            // Remote instances
            var remoteInstances = ScanRemoteInstances().Where(i => i.IsOnline);
            foreach (var instance in remoteInstances)
            {
                var botsResponse = QueryRemote(instance.Port, "LISTBOTS");
                if (botsResponse.StartsWith("{") && botsResponse.Contains("Bots"))
                {
                    try
                    {
                        var botsData = JsonSerializer.Deserialize<Dictionary<string, List<Dictionary<string, object>>>>(botsResponse);
                        if (botsData?.ContainsKey("Bots") == true)
                        {
                            var bots = botsData["Bots"];
                            var idleCount = 0;
                            var nonIdleBots = new List<object>();

                            foreach (var bot in bots)
                            {
                                if (bot.TryGetValue("Status", out var status))
                                {
                                    var statusStr = status?.ToString()?.ToUpperInvariant() ?? "";
                                    if (statusStr == "IDLE" || statusStr == "STOPPED")
                                    {
                                        idleCount++;
                                    }
                                    else
                                    {
                                        nonIdleBots.Add(new
                                        {
                                            Name = bot.TryGetValue("Name", out var name) ? name?.ToString() : "Unknown",
                                            Status = statusStr
                                        });
                                    }
                                }
                            }

                            instances.Add(new
                            {
                                instance.Port,
                                instance.ProcessId,
                                TotalBots = bots.Count,
                                IdleBots = idleCount,
                                NonIdleBots = nonIdleBots,
                                AllIdle = idleCount == bots.Count
                            });
                        }
                    }
                    catch { }
                }
            }

            var totalBots = instances.Sum(i => (int)((dynamic)i).TotalBots);
            var totalIdle = instances.Sum(i => (int)((dynamic)i).IdleBots);
            var allInstancesIdle = instances.All(i => (bool)((dynamic)i).AllIdle);

            return JsonSerializer.Serialize(new
            {
                Instances = instances,
                TotalBots = totalBots,
                TotalIdleBots = totalIdle,
                AllBotsIdle = allInstancesIdle
            });
        }
        catch (Exception ex)
        {
            return CreateErrorResponse(ex.Message);
        }
    }

    private async Task<string> CheckForUpdates()
    {
        try
        {
            var botType = DetectLocalBotType();
            var (updateAvailable, _, latestVersion) = await CheckForUpdatesForBotType(botType);
            var changelog = await FetchChangelogForBotType(botType);

            return JsonSerializer.Serialize(new
            {
                version = latestVersion,
                changelog,
                available = updateAvailable,
                botType = botType.ToString()
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                version = "Unknown",
                changelog = "Unable to fetch update information",
                available = false,
                error = ex.Message
            });
        }
    }

    private async Task<(bool, string, string)> CheckForUpdatesForBotType(BotType botType)
    {
        try
        {
            return botType switch
            {
                BotType.PokeBot => await CheckPokeBotUpdates(),
                BotType.RaidBot => await CheckRaidBotUpdates(),
                _ => (false, "", "Unknown")
            };
        }
        catch
        {
            return (false, "", "Unknown");
        }
    }



    private async Task<(bool, string, string)> CheckPokeBotUpdates()
    {
        try
        {
            var (updateAvailable, _, newVersion) = await PokeBotUpdateChecker.CheckForUpdatesAsync(false);
            return (updateAvailable, "", newVersion);
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Error checking PokeBot updates: {ex.Message}", "BotServer");
            return (false, "", "Unknown");
        }
    }

    private async Task<(bool, string, string)> CheckRaidBotUpdates()
    {
        try
        {
            var (updateAvailable, _, newVersion) = await RaidBotUpdateChecker.CheckForUpdatesAsync(false);
            return (updateAvailable, "", newVersion);
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Error checking RaidBot updates: {ex.Message}", "BotServer");
            return (false, "", "Unknown");
        }
    }

    private async Task<string> FetchChangelogForBotType(BotType botType)
    {
        try
        {
            return botType switch
            {
                BotType.PokeBot => await FetchPokeBotChangelog(),
                BotType.RaidBot => await RaidBotUpdateChecker.FetchChangelogAsync(),
                _ => "No changelog available"
            };
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Error fetching changelog for {botType}: {ex.Message}", "BotServer");
            return "Unable to fetch changelog";
        }
    }

    private async Task<string> FetchPokeBotChangelog()
    {
        try
        {
            return await PokeBotUpdateChecker.FetchChangelogAsync();
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Error fetching PokeBot changelog: {ex.Message}", "BotServer");
            return "Unable to fetch PokeBot changelog";
        }
    }

    private static int ExtractPort(string path)
    {
        var parts = path.Split('/');
        return parts.Length > 4 && int.TryParse(parts[4], out var port) ? port : 0;
    }

    private static (string ip, int port) ExtractIpAndPort(string path)
    {
        try
        {
            // Erwartete Pfad: /api/bot/instances/{ip}:{port}/command
            // Oder: /api/bot/instances/{ip}:{port}/bots
            var parts = path.Split('/');
            if (parts.Length > 4)
            {
                var ipPortPart = parts[4]; // z.B. "100.109.222.10:8081" oder "8081"
                
                var colonIndex = ipPortPart.LastIndexOf(':');
                
                if (colonIndex > 0)
                {
                    // Format: "IP:Port"
                    var ip = ipPortPart.Substring(0, colonIndex);
                    var portStr = ipPortPart.Substring(colonIndex + 1);
                    
                    if (int.TryParse(portStr, out var port))
                    {
                        return (ip, port);
                    }
                }
                // Fallback: Port-only (für Rückwärtskompatibilität)
                else if (int.TryParse(ipPortPart, out var port))
                {
                    return ("127.0.0.1", port);
                }
            }
            
            return ("127.0.0.1", 0);
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"[WebServer] Error parsing path '{path}': {ex.Message}", "WebServer");
            return ("127.0.0.1", 0);
        }
    }

    private bool IsRemoteInstance(string ip, int port)
    {
        // Check if this IP:Port combination is in our known remote instances
        var remoteInstances = ScanRemoteInstances();
        return remoteInstances.Any(instance => 
            instance.IP == ip && instance.Port == port && instance.IsRemote);
    }

    private bool IsLocalInstance(string ip, int port)
    {
        // Eine Instanz ist lokal, wenn:
        // 1. Die IP localhost ist UND der Port unser lokaler TCP-Port ist
        // 2. Oder wenn es explizit nicht als Remote-Instanz bekannt ist und localhost IP hat
        
        var isLocalhostIP = ip == "127.0.0.1" || ip == "localhost";
        var isOurPort = port == _tcpPort;
        
        // Nur wenn es sowohl localhost IP als auch unser Port ist, behandeln wir es als lokale Instanz
        return isLocalhostIP && isOurPort;
    }


    private string GetInstances()
    {
        var instances = new List<BotInstance>
        {
            CreateLocalInstance()
        };

        instances.AddRange(ScanRemoteInstances());

        return JsonSerializer.Serialize(new { Instances = instances });
    }

    private BotInstance CreateLocalInstance()
    {
        var config = GetConfig();
        var controllers = GetBotControllers();

        var mode = config?.Mode.ToString() ?? "Unknown";
        var botType = DetectLocalBotType();
        var name = GetBotName(config, botType);

        var version = GetVersionForBotType(botType);

        var botStatuses = controllers.Select(c => new BotStatusInfo
        {
            Name = GetBotName(c.State, config),
            Status = c.ReadBotState()
        }).ToList();

        return new BotInstance
        {
            ProcessId = Environment.ProcessId,
            Name = name,
            Port = _tcpPort,
            Version = version,
            Mode = mode,
            BotCount = botStatuses.Count,
            IsOnline = true,
            IsMaster = true,
            BotStatuses = botStatuses,
            BotType = botType.ToString()
        };
    }

    private BotType DetectLocalBotType()
    {
        try
        {
            // Try to detect RaidBot first (since this is in RaidBot folder)
            var raidBotType = Type.GetType("SysBot.Pokemon.SV.BotRaid.Helpers.SVRaidBot, SysBot.Pokemon");
            if (raidBotType != null)
            {
                return BotType.RaidBot;
            }

            // Try to detect PokeBot
            var pokeBotType = Type.GetType("SysBot.Pokemon.Helpers.PokeBot, SysBot.Pokemon");
            if (pokeBotType != null)
            {
                return BotType.PokeBot;
            }

            return BotType.Unknown;
        }
        catch
        {
            return BotType.Unknown;
        }
    }

    private string GetVersionForBotType(BotType botType)
    {
        try
        {
            return botType switch
            {
                BotType.PokeBot => GetPokeBotVersion(),
                BotType.RaidBot => GetRaidBotVersion(),
                _ => _mainForm.GetType().Assembly.GetName().Version?.ToString() ?? "1.0.0"
            };
        }
        catch
        {
            return _mainForm.GetType().Assembly.GetName().Version?.ToString() ?? "1.0.0";
        }
    }

    private string GetPokeBotVersion()
    {
        try
        {
            var pokeBotType = Type.GetType("SysBot.Pokemon.Helpers.PokeBot, SysBot.Pokemon");
            if (pokeBotType != null)
            {
                var versionField = pokeBotType.GetField("Version",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (versionField != null)
                {
                    return versionField.GetValue(null)?.ToString() ?? "Unknown";
                }
            }
            return "Unknown";
        }
        catch
        {
            return "Unknown";
        }
    }

    private string GetRaidBotVersion()
    {
        try
        {
            var raidBotType = Type.GetType("SysBot.Pokemon.SV.BotRaid.Helpers.SVRaidBot, SysBot.Pokemon");
            if (raidBotType != null)
            {
                var versionField = raidBotType.GetField("Version",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (versionField != null)
                {
                    return versionField.GetValue(null)?.ToString() ?? "Unknown";
                }
            }
            return "Unknown";
        }
        catch
        {
            return "Unknown";
        }
    }

    private string GetBotName(ProgramConfig? config, BotType botType)
    {
        if (!string.IsNullOrEmpty(config?.Hub?.BotName))
            return config.Hub.BotName;

        return botType switch
        {
            BotType.PokeBot => "PokeBot",
            BotType.RaidBot => "SVRaidBot",
            _ => "Universal Bot"
        };
    }

    private static HashSet<int> _knownInstances = new HashSet<int>();
    
    private List<BotInstance> ScanRemoteInstances()
    {
        var instances = new List<BotInstance>();
        var currentInstances = new HashSet<int>();

        try
        {
            // Get Tailscale configuration
            var config = GetConfig();
            var tailscaleConfig = config?.Hub?.Tailscale;
            var scanLocalhost = true;

            // If Tailscale is enabled, scan remote nodes
            if (tailscaleConfig?.Enabled == true)
            {
                foreach (var remoteIP in tailscaleConfig.RemoteNodes)
                {
                    try
                    {
                        var remoteInstances = ScanRemoteNode(remoteIP, tailscaleConfig);
                        instances.AddRange(remoteInstances);
                        foreach (var instance in remoteInstances)
                        {
                            currentInstances.Add(instance.Port);
                        }
                    }
                    catch (Exception ex)
                    {
                        LogUtil.LogError($"Error scanning remote node {remoteIP}: {ex.Message}", "WebServer");
                    }
                }

                // If this is NOT the master node, don't scan localhost for process discovery
                // (still scan ports for local bots)
                if (!tailscaleConfig.IsMasterNode)
                {
                    scanLocalhost = false;
                }
            }

            // Local process discovery (only if enabled)
            if (scanLocalhost)
            {
                // Scan for PokeBot processes
                var pokeBotProcesses = Process.GetProcessesByName("PokeBot")
                    .Where(p => p.Id != Environment.ProcessId);

                foreach (var process in pokeBotProcesses)
                {
                    try
                    {
                        var instance = TryCreateInstanceFromProcess(process, "PokeBot");
                        if (instance != null)
                        {
                            instances.Add(instance);
                            currentInstances.Add(instance.Port);
                            
                            // Only log new instances
                            if (!_knownInstances.Contains(instance.Port))
                            {
                                LogUtil.LogInfo($"Found new PokeBot instance on port {instance.Port}: {instance.BotType}", "WebServer");
                                _knownInstances.Add(instance.Port);
                            }
                        }
                    }
                    catch
                    {
                        // Ignore process scan errors
                    }
                }

                // Scan for RaidBot processes
                var raidBotProcesses = Process.GetProcessesByName("SysBot")
                    .Where(p => p.Id != Environment.ProcessId);

                foreach (var process in raidBotProcesses)
                {
                    try
                    {
                        var instance = TryCreateInstanceFromProcess(process, "RaidBot");
                        if (instance != null)
                        {
                            instances.Add(instance);
                            currentInstances.Add(instance.Port);
                            
                            // Only log new instances
                            if (!_knownInstances.Contains(instance.Port))
                            {
                                LogUtil.LogInfo($"Found new RaidBot instance on port {instance.Port}: {instance.BotType}", "WebServer");
                                _knownInstances.Add(instance.Port);
                            }
                        }
                    }
                    catch
                    {
                        // Ignore process scan errors
                    }
                }
            }

            // Always scan localhost ports for direct connections
            var portRange = GetLocalPortRange(tailscaleConfig);
            for (int port = portRange.start; port <= portRange.end; port++)
            {
                if (port == _tcpPort) continue; // Skip our own port
                if (currentInstances.Contains(port)) continue; // Skip already found instances

                try
                {
                    var instance = TryCreateInstanceFromPortAndIP("127.0.0.1", port);
                    if (instance != null)
                    {
                        instances.Add(instance);
                        currentInstances.Add(port);
                        
                        // Only log new instances
                        if (!_knownInstances.Contains(port))
                        {
                            LogUtil.LogInfo($"Found new bot instance on port {port}: {instance.BotType}", "WebServer");
                            _knownInstances.Add(port);
                        }
                    }
                }
                catch
                {
                    // Ignore port scan errors
                }
            }
            
            // Log removed instances
            var removedInstances = _knownInstances.Except(currentInstances).ToList();
            foreach (var port in removedInstances)
            {
                LogUtil.LogInfo($"Bot instance on port {port} is no longer available", "WebServer");
                _knownInstances.Remove(port);
            }
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Error scanning remote instances: {ex.Message}", "WebServer");
        }

        return instances;
    }

    private static BotInstance? TryCreateInstanceFromPort(int port)
    {
        try
        {
            // Test if port is responding to bot commands
            if (!IsPortOpen(port))
                return null;

            // Try to get instance info
            var infoResponse = QueryRemote(port, "INFO");
            if (string.IsNullOrEmpty(infoResponse) || infoResponse.StartsWith("ERROR"))
                return null;

            // Parse the response to determine bot type and other info
            var instance = new BotInstance
            {
                ProcessId = 0, // We don't know the PID from port scanning
                Name = "Unknown Bot",
                Port = port,
                Version = "Unknown",
                Mode = "Unknown",
                BotCount = 0,
                IsOnline = true,
                BotType = "Unknown"
            };

            // Try to parse JSON response
            if (infoResponse.StartsWith("{"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(infoResponse);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("Version", out var version))
                        instance.Version = version.GetString() ?? "Unknown";

                    if (root.TryGetProperty("Mode", out var mode))
                        instance.Mode = mode.GetString() ?? "Unknown";

                    if (root.TryGetProperty("Name", out var name))
                        instance.Name = name.GetString() ?? "Unknown Bot";

                    // Determine bot type from response
                    if (root.TryGetProperty("BotType", out var botType))
                    {
                        instance.BotType = botType.GetString() ?? "Unknown";
                    }
                    else
                    {
                        // Try to determine from other fields
                        var nameStr = instance.Name.ToLowerInvariant();
                        if (nameStr.Contains("raid") || nameStr.Contains("sv"))
                            instance.BotType = "RaidBot";
                        else if (nameStr.Contains("poke"))
                            instance.BotType = "PokeBot";
                        else
                            instance.BotType = "Unknown";
                    }
                }
                catch
                {
                    // If JSON parsing fails, keep defaults
                }
            }

            // Get bot count
            var botsResponse = QueryRemote(port, "LISTBOTS");
            if (botsResponse.StartsWith("{") && botsResponse.Contains("Bots"))
            {
                try
                {
                    var botsData = JsonSerializer.Deserialize<Dictionary<string, List<BotInfo>>>(botsResponse);
                    if (botsData?.ContainsKey("Bots") == true)
                    {
                        instance.BotCount = botsData["Bots"].Count;
                        instance.BotStatuses = [.. botsData["Bots"].Select(b => new BotStatusInfo
                        {
                            Name = b.Name,
                            Status = b.Status
                        })];
                    }
                }
                catch
                {
                    // Ignore JSON parsing errors
                }
            }

            return instance;
        }
        catch
        {
            return null;
        }
    }

    private static BotInstance? TryCreateInstanceFromProcess(Process process, string botType)
    {
        try
        {
            var exePath = process.MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath))
                return null;

            var exeDir = Path.GetDirectoryName(exePath)!;
            
            // Try to find port file for this bot type
            var portFile = "";
            if (botType == "PokeBot")
            {
                portFile = Path.Combine(exeDir, $"PokeBot_{process.Id}.port");
            }
            else if (botType == "RaidBot")
            {
                portFile = Path.Combine(exeDir, $"SVRaidBot_{process.Id}.port");
                if (!File.Exists(portFile))
                {
                    // Sometimes RaidBots might also use PokeBot naming
                    portFile = Path.Combine(exeDir, $"PokeBot_{process.Id}.port");
                }
            }

            if (!File.Exists(portFile))
                return null;

            var portText = File.ReadAllText(portFile).Trim();
            if (portText.StartsWith("ERROR:") || !int.TryParse(portText, out var port))
                return null;

            var isOnline = IsPortOpen(port);
            var instance = new BotInstance
            {
                ProcessId = process.Id,
                Name = botType == "RaidBot" ? "SVRaidBot" : "PokeBot",
                Port = port,
                Version = "Unknown",
                Mode = "Unknown",
                BotCount = 0,
                IsOnline = isOnline,
                BotType = botType
            };

            if (isOnline)
            {
                UpdateInstanceInfo(instance, port);
            }

            return instance;
        }
        catch
        {
            return null;
        }
    }

    private static void UpdateInstanceInfo(BotInstance instance, int port)
    {
        try
        {
            var infoResponse = QueryRemote(port, "INFO");
            if (infoResponse.StartsWith("{"))
            {
                using var doc = JsonDocument.Parse(infoResponse);
                var root = doc.RootElement;

                if (root.TryGetProperty("Version", out var version))
                    instance.Version = version.GetString() ?? "Unknown";

                if (root.TryGetProperty("Mode", out var mode))
                    instance.Mode = mode.GetString() ?? "Unknown";

                if (root.TryGetProperty("Name", out var name))
                    instance.Name = name.GetString() ?? "Unknown Bot";

                // Try to get BotType from the INFO response
                if (root.TryGetProperty("BotType", out var botType))
                {
                    instance.BotType = botType.GetString() ?? "Unknown";
                }
                else
                {
                    // Fallback: try to detect from other properties
                    var versionStr = instance.Version.ToLower();
                    var nameStr = instance.Name.ToLower();
                    
                    if (nameStr.Contains("raid") || versionStr.Contains("raid"))
                    {
                        instance.BotType = "RaidBot";
                        if (instance.Name == "Unknown Bot")
                            instance.Name = "SVRaidBot";
                    }
                    else if (nameStr.Contains("poke") || versionStr.Contains("poke"))
                    {
                        instance.BotType = "PokeBot";
                        if (instance.Name == "Unknown Bot")
                            instance.Name = "PokeBot";
                    }
                }
            }

            var botsResponse = QueryRemote(port, "LISTBOTS");
            if (botsResponse.StartsWith("{") && botsResponse.Contains("Bots"))
            {
                var botsData = JsonSerializer.Deserialize<Dictionary<string, List<BotInfo>>>(botsResponse);
                if (botsData?.ContainsKey("Bots") == true)
                {
                    instance.BotCount = botsData["Bots"].Count;
                    instance.BotStatuses = [.. botsData["Bots"].Select(b => new BotStatusInfo
                    {
                        Name = b.Name,
                        Status = b.Status
                    })];
                }
            }
        }
        catch { }
    }

    private static bool IsPortOpen(int port)
    {
        try
        {
            using var client = new TcpClient();
            var result = client.BeginConnect("127.0.0.1", port, null, null);
            var success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(100));
            if (success)
            {
                client.EndConnect(result);
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private string GetBots(string path)
    {
        var (ip, port) = ExtractIpAndPort(path);
        var isLocalInstance = IsLocalInstance(ip, port);
      
        if (isLocalInstance)
        {
            var config = GetConfig();
            var controllers = GetBotControllers();

            var bots = controllers.Select(c => new BotInfo
            {
                Id = $"{c.State.Connection.IP}:{c.State.Connection.Port}",
                Name = GetBotName(c.State, config),
                RoutineType = c.State.InitialRoutine.ToString(),
                Status = c.ReadBotState(),
                ConnectionType = c.State.Connection.Protocol.ToString(),
                IP = c.State.Connection.IP,
                Port = c.State.Connection.Port
            }).ToList();

            return JsonSerializer.Serialize(new { Bots = bots });
        }
        else
        {
            return QueryRemote(ip, port, "LISTBOTS");
        }
    }

    private async Task<string> RunCommand(HttpListenerRequest request, string path)
    {
        try
        {
            using var reader = new StreamReader(request.InputStream);
            var body = await reader.ReadToEndAsync();
            var commandRequest = JsonSerializer.Deserialize<BotCommandRequest>(body);

            if (commandRequest == null)
                return CreateErrorResponse("Invalid command request");

            var (ip, port) = ExtractIpAndPort(path);
            
            // Entscheidung: Ist es eine lokale oder Remote-Instanz?
            var isLocalInstance = IsLocalInstance(ip, port);
            
        
            if (isLocalInstance)
            {
                return RunLocalCommand(commandRequest.Command);
            }

            else
            {
                var tcpCommand = $"{commandRequest.Command}All".ToUpper();
                var result = QueryRemote(ip, port, tcpCommand);


                return JsonSerializer.Serialize(new CommandResponse
                {
                    Success = !result.StartsWith("ERROR") && !result.StartsWith("Failed"),
                    Message = result,
                    Port = port,
                    Command = commandRequest.Command,
                    Timestamp = DateTime.Now
                });
            }
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"[Command] Error: {ex.Message}", "WebServer");
            return CreateErrorResponse(ex.Message);
        }
    }

    private async Task<string> RunAllCommand(HttpListenerRequest request)
    {
        try
        {
            using var reader = new StreamReader(request.InputStream);
            var body = await reader.ReadToEndAsync();
            var commandRequest = JsonSerializer.Deserialize<BotCommandRequest>(body);

            if (commandRequest == null)
                return CreateErrorResponse("Invalid command request");

            var results = new List<CommandResponse>();

            var localResult = JsonSerializer.Deserialize<CommandResponse>(RunLocalCommand(commandRequest.Command));
            if (localResult != null)
            {
                localResult.InstanceName = _mainForm.Text;
                results.Add(localResult);
            }

            var remoteInstances = ScanRemoteInstances().Where(i => i.IsOnline);
            foreach (var instance in remoteInstances)
            {
                try
                {
                    var result = QueryRemote(instance.Port, $"{commandRequest.Command}All".ToUpper());
                    results.Add(new CommandResponse
                    {
                        Success = !result.StartsWith("ERROR"),
                        Message = result,
                        Port = instance.Port,
                        Command = commandRequest.Command,
                        InstanceName = instance.Name
                    });
                }
                catch { }
            }

            return JsonSerializer.Serialize(new BatchCommandResponse
            {
                Results = results,
                TotalInstances = results.Count,
                SuccessfulCommands = results.Count(r => r.Success)
            });
        }
        catch (Exception ex)
        {
            return CreateErrorResponse(ex.Message);
        }
    }

    private string RunLocalCommand(string command)
    {
        try
        {
            var cmd = MapCommand(command);

            _mainForm.BeginInvoke((System.Windows.Forms.MethodInvoker)(() =>
            {
                var sendAllMethod = _mainForm.GetType().GetMethod("SendAll",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                sendAllMethod?.Invoke(_mainForm, new object[] { cmd });
            }));

            return JsonSerializer.Serialize(new CommandResponse
            {
                Success = true,
                Message = $"Command {command} sent successfully",
                Port = _tcpPort,
                Command = command,
                Timestamp = DateTime.Now
            });
        }
        catch
        {
            return JsonSerializer.Serialize(new CommandResponse
            {
                Success = true,
                Message = $"Command {command} sent successfully",
                Port = _tcpPort,
                Command = command,
                Timestamp = DateTime.Now
            });
        }
    }

    private static BotControlCommand MapCommand(string webCommand)
    {
        return webCommand.ToLower() switch
        {
            "start" => BotControlCommand.Start,
            "stop" => BotControlCommand.Stop,
            "idle" => BotControlCommand.Idle,
            "resume" => BotControlCommand.Resume,
            "restart" => BotControlCommand.Restart,
            "reboot" => BotControlCommand.RebootAndStop,
            "refreshmap" => BotControlCommand.RefreshMap,
            "screenon" => BotControlCommand.ScreenOnAll,
            "screenoff" => BotControlCommand.ScreenOffAll,
            _ => BotControlCommand.None
        };
    }

    public static string QueryRemote(int port, string command)
    {
        try
        {
            using var client = new TcpClient();
            client.Connect("127.0.0.1", port);

            using var stream = client.GetStream();
            using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
            using var reader = new StreamReader(stream, Encoding.UTF8);

            writer.WriteLine(command);
            return reader.ReadLine() ?? "No response";
        }
        catch
        {
            return "Failed to connect";
        }
    }

    private List<BotController> GetBotControllers()
    {
        var flpBotsField = _mainForm.GetType().GetField("FLP_Bots",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        if (flpBotsField?.GetValue(_mainForm) is FlowLayoutPanel flpBots)
        {
            return [.. flpBots.Controls.OfType<BotController>()];
        }

        return new List<BotController>();
    }

    private ProgramConfig? GetConfig()
    {
        var configProp = _mainForm.GetType().GetProperty("Config",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return configProp?.GetValue(_mainForm) as ProgramConfig;
    }

    private static string GetBotName(PokeBotState state, ProgramConfig? config)
    {
        return state.Connection.IP;
    }

    private static string CreateErrorResponse(string message)
    {
        return JsonSerializer.Serialize(new CommandResponse
        {
            Success = false,
            Message = $"Error: {message}"
        });
    }

    // Tailscale-specific methods
    private List<BotInstance> ScanRemoteNode(string remoteIP, TailscaleSettings tailscaleConfig)
    {
        var instances = new List<BotInstance>();
        var portRange = GetPortRangeForNode(remoteIP, tailscaleConfig);

        LogUtil.LogInfo($"Scanning {remoteIP} ports {portRange.start}-{portRange.end}", "WebServer");

        for (int port = portRange.start; port <= portRange.end; port++)
        {
            try
            {
                var instance = TryCreateInstanceFromPortAndIP(remoteIP, port);
                if (instance != null)
                {
                    instance.IsRemote = true;
                    instance.IP = remoteIP;
                    instances.Add(instance);
                }
            }
            catch
            {
                // Ignore individual port failures
            }
        }

        return instances;
    }

    private (int start, int end) GetPortRangeForNode(string ip, TailscaleSettings tailscaleConfig)
    {
        // Check if this IP has a specific port allocation
        if (tailscaleConfig.PortAllocation.NodeAllocations.TryGetValue(ip, out var allocation))
        {
            return (allocation.Start, allocation.End);
        }

        // Use global port scan range instead of default range
        return (tailscaleConfig.PortScanStart, tailscaleConfig.PortScanEnd);
    }

    private (int start, int end) GetLocalPortRange(TailscaleSettings? tailscaleConfig)
    {
        if (tailscaleConfig?.Enabled == true)
        {
            // Try to get our local IP and find our allocated range
            var localIP = GetLocalTailscaleIP();
            if (!string.IsNullOrEmpty(localIP))
            {
                return GetPortRangeForNode(localIP, tailscaleConfig);
            }
        }

        // Default fallback
        return (8081, 8110);
    }

    private string? GetLocalTailscaleIP()
    {
        try
        {
            // Try to detect our Tailscale IP
            // This is a simple heuristic - in production you might want to use Tailscale API
            var config = GetConfig();
            var tailscaleConfig = config?.Hub?.Tailscale;
            
            if (tailscaleConfig?.IsMasterNode == true)
            {
                // If we are master, we might be one of the configured IPs
                return tailscaleConfig.MasterNodeIP;
            }

            // Could implement more sophisticated detection here
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static BotInstance? TryCreateInstanceFromPortAndIP(string ip, int port)
    {
        try
        {
            var isOnline = IsPortOpen(ip, port);
            if (!isOnline)
                return null;

            var infoResponse = QueryRemote(ip, port, "INFO");
            if (string.IsNullOrEmpty(infoResponse) || 
                infoResponse.StartsWith("Failed") || 
                infoResponse.StartsWith("ERROR") ||
                !infoResponse.Contains("{"))
            {
                // Kein gültiger Bot-Server
                return null;
            }

            var instance = new BotInstance
            {
                ProcessId = 0, // Cannot get process ID from port alone
                Name = "Unknown Bot",
                Port = port,
                IP = ip,
                Version = "Unknown",
                Mode = "Unknown",
                BotCount = 0,
                IsOnline = true,
                IsRemote = ip != "127.0.0.1",
                BotType = "Unknown"
            };

            UpdateInstanceInfo(instance, ip, port);

            return instance;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsPortOpen(string ip, int port)
    {
        try
        {
            using var client = new TcpClient();
            var result = client.BeginConnect(ip, port, null, null);
            var success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(200)); // Reduziert von 1s auf 200ms
            if (success)
            {
                client.EndConnect(result);
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static void UpdateInstanceInfo(BotInstance instance, string ip, int port)
    {
        try
        {
            var infoResponse = QueryRemote(ip, port, "INFO");
            if (infoResponse.StartsWith("{"))
            {
                using var doc = JsonDocument.Parse(infoResponse);
                var root = doc.RootElement;

                if (root.TryGetProperty("Version", out var version))
                    instance.Version = version.GetString() ?? "Unknown";

                if (root.TryGetProperty("Mode", out var mode))
                    instance.Mode = mode.GetString() ?? "Unknown";

                if (root.TryGetProperty("Name", out var name))
                    instance.Name = name.GetString() ?? "Unknown Bot";

                // Try to get BotType from the INFO response
                if (root.TryGetProperty("BotType", out var botType))
                {
                    instance.BotType = botType.GetString() ?? "Unknown";
                }
                else
                {
                    // Fallback: try to detect from other properties
                    var versionStr = instance.Version.ToLower();
                    var nameStr = instance.Name.ToLower();
                    
                    if (nameStr.Contains("raid") || versionStr.Contains("raid"))
                    {
                        instance.BotType = "RaidBot";
                        if (instance.Name == "Unknown Bot")
                            instance.Name = "SVRaidBot";
                    }
                    else if (nameStr.Contains("poke") || versionStr.Contains("poke"))
                    {
                        instance.BotType = "PokeBot";
                        if (instance.Name == "Unknown Bot")
                            instance.Name = "PokeBot";
                    }
                }
            }

            var botsResponse = QueryRemote(ip, port, "LISTBOTS");
            if (botsResponse.StartsWith("{") && botsResponse.Contains("Bots"))
            {
                var botsData = JsonSerializer.Deserialize<Dictionary<string, List<BotInfo>>>(botsResponse);
                if (botsData?.ContainsKey("Bots") == true)
                {
                    instance.BotCount = botsData["Bots"].Count;
                    instance.BotStatuses = [.. botsData["Bots"].Select(b => new BotStatusInfo
                    {
                        Name = b.Name,
                        Status = b.Status
                    })];
                }
            }
        }
        catch { }
    }

    public static string QueryRemote(string ip, int port, string command)
    {
        try
        {
            using var client = new TcpClient();
            
            // Optimierter Timeout-basierter Connect
            var result = client.BeginConnect(ip, port, null, null);
            var success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(500)); // 500ms statt default
            
            if (!success || !client.Connected)
            {
                return "ERROR: Connection timeout";
            }
            
            client.EndConnect(result);
            
            // Reduzierte Socket-Timeouts
            client.ReceiveTimeout = 1000;  // 1s statt default
            client.SendTimeout = 1000;     // 1s statt default

            using var stream = client.GetStream();
            using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
            using var reader = new StreamReader(stream, Encoding.UTF8);

            writer.WriteLine(command);
            return reader.ReadLine() ?? "No response";
        }
        catch (Exception ex)
        {
            return $"ERROR: {ex.Message}";
        }
    }

    public void Dispose()
    {
        Stop();
        _listener?.Close();
        _cts?.Dispose();
    }
}

public class BotInstance
{
    public int ProcessId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Port { get; set; }
    public string IP { get; set; } = "127.0.0.1";
    public string Version { get; set; } = string.Empty;
    public int BotCount { get; set; }
    public string Mode { get; set; } = string.Empty;
    public bool IsOnline { get; set; }
    public bool IsMaster { get; set; }
    public bool IsRemote { get; set; }
    public List<BotStatusInfo>? BotStatuses { get; set; }
    public string BotType { get; set; } = "Unknown";
}

public class BotStatusInfo
{
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}

public class BotInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string RoutineType { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string ConnectionType { get; set; } = string.Empty;
    public string IP { get; set; } = string.Empty;
    public int Port { get; set; }
}

public class BotCommandRequest
{
    public string Command { get; set; } = string.Empty;
    public string? BotId { get; set; }
}

public class CommandResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public int Port { get; set; }
    public string Command { get; set; } = string.Empty;
    public string? InstanceName { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;
}

public class BatchCommandResponse
{
    public List<CommandResponse> Results { get; set; } = [];
    public int TotalInstances { get; set; }
    public int SuccessfulCommands { get; set; }
}