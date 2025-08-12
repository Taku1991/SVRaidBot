using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
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

public partial class BotServer(Main mainForm, int port = 8080, int tcpPort = 8081) : IDisposable
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
    
    // Instance cache to prevent expensive re-scanning
    private static readonly object _instanceCacheLock = new();
    private static List<BotInstance>? _cachedInstances = null;
    private static DateTime _lastCacheUpdate = DateTime.MinValue;
    private static readonly TimeSpan _cacheTimeout = TimeSpan.FromSeconds(2); // Cache for 2 seconds

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

            // Handle query parameters (like ?v=2.0.0) by using only the path
            var requestPath = request.Url?.LocalPath ?? "";
            
            string? responseString = requestPath switch
            {
                "/" => HtmlTemplate,
                "/BotControlPanel.css" => ServeEmbeddedResource("BotControlPanel.css", "text/css"),
                "/BotControlPanel.js" => ServeEmbeddedResource("BotControlPanel.js", "application/javascript"),
                "/api/bot/instances" => GetInstances(),
                "/api/bot/debug" => GetDebugInfo(),
                var path when path?.StartsWith("/api/bot/instances/") == true && path.EndsWith("/bots") =>
                    GetBots(path),
                var path when path?.StartsWith("/api/bot/instances/") == true && path.EndsWith("/command") =>
                    await RunCommand(request, path),
                "/api/bot/command/all" => await RunAllCommand(request),
                "/api/bot/update/check" => await CheckForUpdates(),
                "/api/bot/update/idle-status" => GetIdleStatus(),
                "/api/bot/update/active" => GetActiveUpdates(),
                "/api/bot/update/all" => await UpdateAllInstances(request),
                "/api/bot/update/pokebot" => await UpdatePokeBotInstances(request),
                "/api/bot/update/raidbot" => await UpdateRaidBotInstances(request),
                "/api/bot/restart/all" => await RestartAllInstances(request),
                "/api/bot/restart/proceed" => await ProceedWithRestarts(request),
                "/api/bot/restart/schedule" => await UpdateRestartSchedule(request),
                "/icon.ico" => ServeIcon(),
                var path when path?.EndsWith(".png") == true => ServeImage(path),
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
                
                // Set appropriate content type
                if (requestPath == "/")
                {
                    response.ContentType = "text/html; charset=utf-8";
                }
                else if (requestPath == "/BotControlPanel.css")
                {
                    response.ContentType = "text/css; charset=utf-8";
                }
                else if (requestPath == "/BotControlPanel.js")
                {
                    response.ContentType = "application/javascript; charset=utf-8";
                }
                else if (requestPath == "/icon.ico")
                {
                    response.ContentType = "image/x-icon";
                }
                else if (requestPath.EndsWith(".png"))
                {
                    response.ContentType = "image/png";
                }
                else
                {
                    response.ContentType = "application/json; charset=utf-8";
                }
            }

            // Handle binary content for icon and images
            if (requestPath == "/icon.ico" && responseString == "BINARY_ICON")
            {
                var iconBytes = GetIconBytes();
                if (iconBytes != null)
                {
                    response.ContentLength64 = iconBytes.Length;
                    await response.OutputStream.WriteAsync(iconBytes, 0, iconBytes.Length);
                    await response.OutputStream.FlushAsync();
                    return;
                }
            }
            else if (requestPath.EndsWith(".png") && responseString == "BINARY_IMAGE")
            {
                var imageBytes = GetImageBytes(requestPath);
                if (imageBytes != null)
                {
                    response.ContentLength64 = imageBytes.Length;
                    await response.OutputStream.WriteAsync(imageBytes, 0, imageBytes.Length);
                    await response.OutputStream.FlushAsync();
                    return;
                }
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

    private async Task<string> UpdatePokeBotInstances(HttpListenerRequest request)
    {
        try
        {
            // Start PokeBot-specific update
            var result = await UpdateManager.StartPokeBotUpdateAsync(_mainForm, _tcpPort);

            return JsonSerializer.Serialize(new
            {
                Stage = "updating",
                Success = result.UpdatesFailed == 0 && result.UpdatesNeeded > 0,
                result.TotalInstances,
                result.UpdatesNeeded,
                result.UpdatesStarted,
                result.UpdatesFailed,
                BotType = "Unknown",
                Message = result.UpdatesNeeded == 0 ? "All PokeBot instances are already up to date" :
                         result.UpdatesStarted > 0 ? $"PokeBot update initiated for {result.UpdatesStarted} instances" :
                         "No PokeBot updates were started",
                Results = result.InstanceResults.Select(r => new
                {
                    r.Port,
                    r.ProcessId,
                    r.CurrentVersion,
                    r.LatestVersion,
                    r.NeedsUpdate,
                    r.UpdateStarted,
                    r.Error,
                    r.BotType
                })
            });
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Failed to start PokeBot update: {ex.Message}", "WebServer");
            return CreateErrorResponse(ex.Message);
        }
    }

    private async Task<string> UpdateRaidBotInstances(HttpListenerRequest request)
    {
        try
        {
            // Start RaidBot-specific update
            var result = await UpdateManager.StartRaidBotUpdateAsync(_mainForm, _tcpPort);

            return JsonSerializer.Serialize(new
            {
                Stage = "updating",
                Success = result.UpdatesFailed == 0 && result.UpdatesNeeded > 0,
                result.TotalInstances,
                result.UpdatesNeeded,
                result.UpdatesStarted,
                result.UpdatesFailed,
                BotType = "RaidBot",
                Message = result.UpdatesNeeded == 0 ? "All RaidBot instances are already up to date" :
                         result.UpdatesStarted > 0 ? $"RaidBot update initiated for {result.UpdatesStarted} instances" :
                         "No RaidBot updates were started",
                Results = result.InstanceResults.Select(r => new
                {
                    r.Port,
                    r.ProcessId,
                    r.CurrentVersion,
                    r.LatestVersion,
                    r.NeedsUpdate,
                    r.UpdateStarted,
                    r.Error,
                    r.BotType
                })
            });
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Failed to start RaidBot update: {ex.Message}", "WebServer");
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
        try
        {
            lock (_instanceCacheLock)
            {
                // Check if cache is still valid
                if (_cachedInstances != null && DateTime.Now - _lastCacheUpdate < _cacheTimeout)
                {
                    // Update only the local instance (which is fast) and return cached remote instances
                    var localInstance = CreateLocalInstance();
                    var result = new List<BotInstance> { localInstance };
                    
                    // Add cached remote instances
                    result.AddRange(_cachedInstances.Where(i => !i.IsLocal));
                    
                    return JsonSerializer.Serialize(new { Instances = result });
                }
                
                // Cache expired or doesn't exist - perform full scan
                var instances = new List<BotInstance>
                {
                    CreateLocalInstance()
                };
                
                // Perform expensive remote scan
                var remoteInstances = ScanRemoteInstancesFast();
                instances.AddRange(remoteInstances);
                
                // Cache the results
                _cachedInstances = new List<BotInstance>(instances);
                _lastCacheUpdate = DateTime.Now;
                
                return JsonSerializer.Serialize(new { Instances = instances });
            }
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Error in GetInstances: {ex.Message}", "WebServer");
            
            // Fallback to just local instance if there's an error
            var localInstance = CreateLocalInstance();
            return JsonSerializer.Serialize(new { Instances = new[] { localInstance } });
        }
    }

    private string GetDebugInfo()
    {
        try
        {
            var config = GetConfig();
            var controllers = GetBotControllers();
            var localInstance = CreateLocalInstance();
            
            var debugInfo = new
            {
                ConfigExists = config != null,
                ConfigMode = config?.Mode.ToString(),
                ControllerCount = controllers?.Count ?? 0,
                TcpPort = _tcpPort,
                LocalInstance = new
                {
                    localInstance.ProcessId,
                    localInstance.Name,
                    localInstance.Port,
                    localInstance.IP,
                    localInstance.Version,
                    localInstance.Mode,
                    localInstance.BotCount,
                    localInstance.IsOnline,
                    localInstance.BotType
                },
                RawGetInstancesResponse = GetInstances()
            };

            return JsonSerializer.Serialize(debugInfo, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { Error = ex.Message, StackTrace = ex.StackTrace }, new JsonSerializerOptions { WriteIndented = true });
        }
    }

    private BotInstance CreateLocalInstance()
    {
        try
        {
            var config = GetConfig();
            var controllers = GetBotControllers();

            var mode = config?.Mode.ToString() ?? "Unknown";
            var botType = DetectLocalBotType();
            var name = GetBotName(config, botType);

            var version = GetVersionForBotType(botType);

            // Reduced logging - only log when instance is created for the first time

            var botStatuses = controllers?.Select(c => 
            {
                try
                {
                    return new BotStatusInfo
                    {
                        Name = GetBotName(c.State, config),
                        Status = c.ReadBotState()
                    };
                }
                catch
                {
                    return new BotStatusInfo { Name = "Unknown", Status = "Error" };
                }
            }).ToList() ?? new List<BotStatusInfo>();

            var instance = new BotInstance
            {
                ProcessId = Environment.ProcessId,
                Name = name ?? "SVRaidBot",
                Port = _tcpPort,
                WebPort = GetWebPortForTcpPort(_tcpPort),
                IP = "127.0.0.1",
                Version = version,
                Mode = mode,
                BotCount = botStatuses.Count,
                IsOnline = true,
                IsMaster = true,
                IsRemote = false,
                BotStatuses = botStatuses,
                BotType = botType.ToString()
            };

            LogUtil.LogInfo($"Local instance created: Port={instance.Port}, Version={instance.Version}, Mode={instance.Mode}, BotCount={instance.BotCount}", "WebServer");
            return instance;
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Error creating local instance: {ex.Message}", "WebServer");
            
            // Return minimal working instance
            return new BotInstance
            {
                ProcessId = Environment.ProcessId,
                Name = "SVRaidBot (Error)",
                Port = _tcpPort,
                WebPort = GetWebPortForTcpPort(_tcpPort),
                IP = "127.0.0.1",
                Version = "Error",
                Mode = "Error",
                BotCount = 0,
                IsOnline = true,
                IsMaster = true,
                IsRemote = false,
                BotStatuses = new List<BotStatusInfo>(),
                BotType = "RaidBot"
            };
        }
    }

    private BotType DetectLocalBotType()
    {
        try
        {
            // This is SVRaidBot - always return RaidBot
            return BotType.RaidBot;
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
    
    private List<BotInstance> ScanRemoteInstancesFast()
    {
        var instances = new List<BotInstance>();

        try
        {
            // Get Tailscale configuration
            var config = GetConfig();
            var tailscaleConfig = config?.Hub?.Tailscale;

            // If Tailscale is enabled, scan remote nodes (this is fast network scanning)
            if (tailscaleConfig?.Enabled == true)
            {
                var tasks = tailscaleConfig.RemoteNodes.Select(remoteIP => 
                    Task.Run(() => 
                    {
                        try
                        {
                            return ScanRemoteNodeFast(remoteIP, tailscaleConfig);
                        }
                        catch (Exception ex)
                        {
                            LogUtil.LogError($"Error scanning remote node {remoteIP}: {ex.Message}", "WebServer");
                            return new List<BotInstance>();
                        }
                    })).ToArray();

                // Wait for all remote scans to complete with timeout
                if (Task.WaitAll(tasks, TimeSpan.FromMilliseconds(1500))) // Increased timeout
                {
                    foreach (var task in tasks)
                    {
                        instances.AddRange(task.Result);
                    }
                }
                else
                {
                    LogUtil.LogInfo("Some remote node scans timed out", "WebServer");
                }
            }

            // Scan full bot port range for multi-bot support (8080-8100)
            var portsToScan = Enumerable.Range(8080, 21).ToArray(); // 8080 to 8100 inclusive
            var portTasks = new List<Task<BotInstance?>>();
            
            foreach (var port in portsToScan)
            {
                if (port == _tcpPort) continue; // Skip our own port
                
                var currentPort = port;
                var task = Task.Run(() => TryCreateInstanceFromPortAndIPFast("127.0.0.1", currentPort));
                portTasks.Add(task);
            }
            
            // Also scan the configured port range if different
            var portRange = GetLocalPortRange(tailscaleConfig);
            for (int port = portRange.start; port <= portRange.end; port++)
            {
                if (port == _tcpPort) continue; // Skip our own port
                if (portsToScan.Contains(port)) continue; // Skip already scanned ports
                
                var currentPort = port;
                var task = Task.Run(() => TryCreateInstanceFromPortAndIPFast("127.0.0.1", currentPort));
                portTasks.Add(task);
            }
            
            // Wait for port scans with longer timeout for full range
            var portScanCompleted = Task.WaitAll(portTasks.ToArray(), TimeSpan.FromMilliseconds(3000)); // Increased for stability
            
            foreach (var task in portTasks.Where(t => t.IsCompleted))
            {
                try
                {
                    var instance = task.Result;
                    if (instance != null)
                    {
                        instances.Add(instance);
                    }
                }
                catch
                {
                    // Ignore individual task failures
                }
            }
            
            if (!portScanCompleted)
            {
                LogUtil.LogInfo("Port scan partially timed out - some instances may not be shown", "WebServer");
            }
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Error in fast remote instance scan: {ex.Message}", "WebServer");
        }

        return instances;
    }

    private List<BotInstance> ScanRemoteNodeFast(string remoteIP, TailscaleSettings tailscaleConfig)
    {
        var instances = new List<BotInstance>();
        var portRange = GetPortRangeForNode(remoteIP, tailscaleConfig);

        for (int port = portRange.start; port <= portRange.end; port++)
        {
            try
            {
                var instance = TryCreateInstanceFromPortAndIPFast(remoteIP, port);
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

    private static int GetWebPortForTcpPort(int tcpPort)
    {
        // Standard port mapping: TCP port + 1000 for web port
        // e.g., TCP 8080 -> Web 9080, TCP 8081 -> Web 9081
        return tcpPort + 1000;
    }

    private static BotInstance? TryCreateInstanceFromPortAndIPFast(string ip, int port)
    {
        try
        {
            // Quick port check with minimal timeout
            if (!IsPortOpenFast(ip, port))
                return null;

            // Quick INFO query
            var infoResponse = QueryRemoteFast(ip, port, "INFO");
            if (string.IsNullOrEmpty(infoResponse) || 
                infoResponse.StartsWith("Failed") || 
                infoResponse.StartsWith("ERROR") ||
                !infoResponse.Contains("{"))
            {
                return null;
            }

            var instance = new BotInstance
            {
                ProcessId = 0,
                Name = "Unknown Bot",
                Port = port,
                WebPort = GetWebPortForTcpPort(port),
                IP = ip,
                Version = "Unknown",
                Mode = "Unknown",
                BotCount = 0,
                IsOnline = true,
                IsRemote = ip != "127.0.0.1",
                BotType = "Unknown"
            };

            UpdateInstanceInfoFast(instance, ip, port);
            return instance;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsPortOpenFast(string ip, int port)
    {
        try
        {
            using var client = new TcpClient();
            var result = client.BeginConnect(ip, port, null, null);
            var success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(300)); // Increased for stability
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

    private static string QueryRemoteFast(string ip, int port, string command)
    {
        try
        {
            using var client = new TcpClient();
            
            var result = client.BeginConnect(ip, port, null, null);
            var success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(300)); // Increased for stability
            
            if (!success || !client.Connected)
            {
                return "ERROR: Connection timeout";
            }
            
            client.EndConnect(result);
            client.ReceiveTimeout = 200;  // Very fast timeout
            client.SendTimeout = 200;

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

    private static void UpdateInstanceInfoFast(BotInstance instance, string ip, int port)
    {
        try
        {
            var infoResponse = QueryRemoteFast(ip, port, "INFO");
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

                if (root.TryGetProperty("BotType", out var botType))
                {
                    instance.BotType = botType.GetString() ?? "PokeBot";
                }
                else
                {
                    // Enhanced bot type detection
                    var versionStr = instance.Version.ToLower();
                    var nameStr = instance.Name.ToLower();
                    
                    // Enhanced bot type detection for full port range
                    if (nameStr.Contains("raid") || versionStr.Contains("raid") || nameStr.Contains("sv") || port >= 8082)
                    {
                        instance.BotType = "RaidBot";
                        if (instance.Name == "Unknown Bot")
                            instance.Name = $"SVRaidBot (Port {port})";
                    }
                    else if (port == 8080)
                    {
                        instance.BotType = "PokeBot";
                        if (instance.Name == "Unknown Bot")
                            instance.Name = "PokeBot (Master)";
                    }
                    else if (port == 8081)
                    {
                        instance.BotType = "PokeBot";
                        if (instance.Name == "Unknown Bot")
                            instance.Name = "PokeBot (Secondary)";
                    }
                    else
                    {
                        instance.BotType = "PokeBot";
                        if (instance.Name == "Unknown Bot")
                            instance.Name = $"PokeBot (Port {port})";
                    }
                }
            }

            // Try to get process ID from port files
            if (ip == "127.0.0.1" && instance.ProcessId == 0)
            {
                instance.ProcessId = GetProcessIdFromPortFiles(port);
            }

            // Quick bot count check - skip if too slow
            try
            {
                var botsResponse = QueryRemoteFast(ip, port, "LISTBOTS");
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
            catch
            {
                // If bot list query fails or times out, just skip it
                instance.BotCount = 0;
            }
        }
        catch
        {
            // If anything fails, just use default values
        }
    }
    
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

            // Always scan full bot port range first for multi-bot support
            var standardPorts = Enumerable.Range(8080, 21).ToArray(); // 8080 to 8100 inclusive
            foreach (var port in standardPorts)
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
                        LogUtil.LogInfo($"Found standard bot instance: {instance.Name} on port {port}", "WebServer");
                    }
                }
                catch (Exception ex)
                {
                    LogUtil.LogError($"Error scanning standard port {port}: {ex.Message}", "WebServer");
                }
            }
            
            // Also scan the configured port range if different
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
            var success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(300)); // Increased for stability
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
                // For remote bots, ensure commands have ALL suffix for compatibility
                var tcpCommand = commandRequest.Command.ToUpper();
                if (!tcpCommand.EndsWith("ALL") && IsGlobalCommand(tcpCommand))
                {
                    tcpCommand += "ALL";
                }
                LogUtil.LogInfo($"[Command] Sending {tcpCommand} to remote bot at {ip}:{port}", "WebServer");
                var result = QueryRemote(ip, port, tcpCommand);
                
                var success = !result.StartsWith("ERROR") && !result.StartsWith("Failed");
                LogUtil.LogInfo($"[Command] Remote command result: {(success ? "SUCCESS" : "FAILED")} - {result}", "WebServer");

                return JsonSerializer.Serialize(new CommandResponse
                {
                    Success = success,
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
                    var result = QueryRemote(instance.Port, $"{commandRequest.Command.ToUpper()}ALL");
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

    private static bool IsGlobalCommand(string command)
    {
        var globalCommands = new[] { "START", "STOP", "IDLE", "RESUME", "RESTART", "REBOOT", "SCREENON", "SCREENOFF", "REFRESHMAP", "UPDATE" };
        return globalCommands.Contains(command);
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
            "update" => BotControlCommand.Restart, // Update als Restart behandeln
            _ => BotControlCommand.Start // Fallback zu Start statt None
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

        // LogUtil.LogInfo($"Scanning {remoteIP} ports {portRange.start}-{portRange.end}", "WebServer"); // Suppressed for cleaner logs

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
                WebPort = GetWebPortForTcpPort(port),
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

    private static int GetProcessIdFromPortFiles(int port)
    {
        try
        {
            // Look for port files in common locations
            var searchDirectories = new[]
            {
                Directory.GetCurrentDirectory(),
                @"F:\PokeBot\SysBot.Pokemon.WinForms\bin\Debug\net9.0-windows\win-x86",
                @"F:\PokeBot\SVRaidBot\SysBot.Pokemon.WinForms\bin\Debug\net9.0-windows\win-x64",
                Path.Combine(AppContext.BaseDirectory)
            };

            foreach (var directory in searchDirectories)
            {
                if (!Directory.Exists(directory)) continue;

                var portFiles = Directory.GetFiles(directory, "*.port", SearchOption.TopDirectoryOnly);
                foreach (var file in portFiles)
                {
                    try
                    {
                        // Quick filename check first to avoid unnecessary file reads
                        var fileName = Path.GetFileNameWithoutExtension(file);
                        if (fileName.Contains("_"))
                        {
                            var content = File.ReadAllText(file).Trim();
                            if (int.TryParse(content, out var filePort) && filePort == port)
                            {
                                // Extract process ID from filename (e.g., "SVRaidBot_23928.port" -> 23928)
                                var lastUnderscore = fileName.LastIndexOf('_');
                                if (lastUnderscore > 0 && lastUnderscore < fileName.Length - 1)
                                {
                                    var processIdStr = fileName.Substring(lastUnderscore + 1);
                                    if (int.TryParse(processIdStr, out var processId))
                                    {
                                        LogUtil.LogInfo($"Found process ID {processId} for port {port} from file {file}", "WebServer");
                                        return processId;
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogUtil.LogError($"Error reading port file {file}: {ex.Message}", "WebServer");
                    }
                }
            }

            return 0;
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Error searching for port files for port {port}: {ex.Message}", "WebServer");
            return 0;
        }
    }

    public void Dispose()
    {
        Stop();
        _listener?.Close();
        _cts?.Dispose();
    }

    private string GetActiveUpdates()
    {
        try
        {
            // For SVRaidBot, we'll use the GetActiveOperations method
            return GetActiveOperations();
        }
        catch (Exception ex)
        {
            return CreateErrorResponse(ex.Message);
        }
    }

    private async Task<string> RestartAllInstances(HttpListenerRequest request)
    {
        try
        {
            // Simple implementation for RaidBot
            return JsonSerializer.Serialize(new
            {
                Success = true,
                TotalInstances = 1,
                Error = (string?)null,
                Message = "Restart not implemented for RaidBot yet"
            });
        }
        catch (Exception ex)
        {
            return CreateErrorResponse(ex.Message);
        }
    }

    private async Task<string> ProceedWithRestarts(HttpListenerRequest request)
    {
        try
        {
            // Simple implementation for RaidBot
            return JsonSerializer.Serialize(new
            {
                Success = true,
                TotalInstances = 1,
                MasterRestarting = false,
                Error = (string?)null,
                Message = "Restart proceed not implemented for RaidBot yet"
            });
        }
        catch (Exception ex)
        {
            return CreateErrorResponse(ex.Message);
        }
    }

    private async Task<string> UpdateRestartSchedule(HttpListenerRequest request)
    {
        try
        {
            // Simple implementation - return current schedule
            return GetRestartSchedule();
        }
        catch (Exception ex)
        {
            return CreateErrorResponse(ex.Message);
        }
    }

}

public class BotInstance
{
    public int ProcessId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Port { get; set; }
    public int WebPort { get; set; }
    public string IP { get; set; } = "127.0.0.1";
    public string Version { get; set; } = string.Empty;
    public int BotCount { get; set; }
    public string Mode { get; set; } = string.Empty;
    public bool IsOnline { get; set; }
    public bool IsMaster { get; set; }
    public bool IsRemote { get; set; }
    public bool IsLocal => !IsRemote;
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

// Additional methods for image and icon serving
public partial class BotServer
{
    private string ServeEmbeddedResource(string resourceName, string contentType)
    {
        try
        {
            // For SVRaidBot, try file system first, then embedded resource
            var exePath = System.Windows.Forms.Application.ExecutablePath;
            var exeDir = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory;
            var resourcePath = Path.Combine(exeDir, "Resources", resourceName);
            
            if (File.Exists(resourcePath))
            {
                return File.ReadAllText(resourcePath);
            }
            
            // Fallback to embedded resource
            return LoadEmbeddedResource(resourceName);
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Failed to load resource '{resourceName}': {ex.Message}", "WebServer");
            return $"/* Error loading {resourceName}: {ex.Message} */";
        }
    }

    private string ServeIcon()
    {
        return "BINARY_ICON"; // Special marker for binary content
    }
    
    private string ServeImage(string path)
    {
        return "BINARY_IMAGE"; // Special marker for binary content
    }
    
    private byte[]? GetIconBytes()
    {
        try
        {
            // First try to find icon.ico in the executable directory
            var exePath = Application.ExecutablePath;
            var exeDir = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory;
            var iconPath = Path.Combine(exeDir, "icon.ico");

            if (File.Exists(iconPath))
            {
                return File.ReadAllBytes(iconPath);
            }

            // If not found, try to extract from embedded resources
            var assembly = Assembly.GetExecutingAssembly();
            var iconStream = assembly.GetManifestResourceStream("SysBot.Pokemon.WinForms.icon.ico");

            if (iconStream != null)
            {
                using (iconStream)
                {
                    var buffer = new byte[iconStream.Length];
                    iconStream.ReadExactly(buffer);
                    return buffer;
                }
            }

            // Try to get the application icon as a fallback
            var icon = Icon.ExtractAssociatedIcon(exePath);
            if (icon != null)
            {
                using (var ms = new MemoryStream())
                {
                    icon.Save(ms);
                    return ms.ToArray();
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Failed to load icon: {ex.Message}", "WebServer");
            return null;
        }
    }
    
    private byte[]? GetImageBytes(string imagePath)
    {
        try
        {
            // Extract filename from path (e.g., "/update_pokebot.png" -> "update_pokebot.png")
            var fileName = Path.GetFileName(imagePath);
            
            // First try to find image in the executable directory
            var exePath = Application.ExecutablePath;
            var exeDir = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory;
            var resourcesDir = Path.Combine(exeDir, "Resources");
            var imagePath1 = Path.Combine(resourcesDir, fileName);
            var imagePath2 = Path.Combine(exeDir, fileName);
            
            if (File.Exists(imagePath1))
            {
                return File.ReadAllBytes(imagePath1);
            }
            
            if (File.Exists(imagePath2))
            {
                return File.ReadAllBytes(imagePath2);
            }
            
            // Try to extract from embedded resources
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(name => name.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
            
            if (!string.IsNullOrEmpty(resourceName))
            {
                using var imageStream = assembly.GetManifestResourceStream(resourceName);
                if (imageStream != null)
                {
                    using var memoryStream = new MemoryStream();
                    imageStream.CopyTo(memoryStream);
                    return memoryStream.ToArray();
                }
            }
            
            return null;
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Failed to load image {imagePath}: {ex.Message}", "WebServer");
            return null;
        }
    }

    private string GetActiveOperations()
    {
        try
        {
            // Return empty active operations for now
            var response = new
            {
                active = false,
                operation = (string?)null,
                progress = 0,
                status = "idle",
                startTime = (string?)null,
                estimatedTime = (string?)null
            };

            return JsonSerializer.Serialize(response, new JsonSerializerOptions 
            { 
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase 
            });
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Error getting active operations: {ex.Message}", "WebServer");
            return JsonSerializer.Serialize(new { error = "Failed to get active operations" });
        }
    }

    private string GetRestartSchedule()
    {
        try
        {
            // Return empty restart schedule for now
            var response = new
            {
                enabled = false,
                time = "00:00",
                nextRestart = (string?)null
            };

            return JsonSerializer.Serialize(response, new JsonSerializerOptions 
            { 
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase 
            });
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Error getting restart schedule: {ex.Message}", "WebServer");
            return JsonSerializer.Serialize(new { error = "Failed to get restart schedule" });
        }
    }
}