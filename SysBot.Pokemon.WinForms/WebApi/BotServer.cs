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
using SysBot.Pokemon.WinForms.WebApi.Models;

namespace SysBot.Pokemon.WinForms.WebApi;

public class BotServer(Main mainForm, int port = 9090, int tcpPort = 9091) : IDisposable
{
    private HttpListener? _listener;
    private Thread? _listenerThread;
    private readonly int _port = port;
    private readonly int _tcpPort = tcpPort;
    private readonly CancellationTokenSource _cts = new();
    private readonly Main _mainForm = mainForm ?? throw new ArgumentNullException(nameof(mainForm));
    private volatile bool _running;
    private string? _htmlTemplate;

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
            }
            catch (HttpListenerException ex) when (ex.ErrorCode == 5)
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://localhost:{_port}/");
                _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
                _listener.Start();

                LogUtil.LogError($"Web server requires administrator privileges for network access. Currently limited to localhost only.", "WebServer");
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
                    GetBots(ExtractPort(path)),
                var path when path?.StartsWith("/api/bot/instances/") == true && path.EndsWith("/command") =>
                    await RunCommand(request, ExtractPort(path)),
                "/api/bot/command/all" => await RunAllCommand(request),
                "/api/bot/update/check" => await CheckForUpdates(),
                "/api/bot/update/idle-status" => GetIdleStatus(),
                "/api/bot/update/active" => GetActiveUpdates(),
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
            var (updateAvailable, _, latestVersion) = await UpdateChecker.CheckForUpdatesAsync(false);
            var changelog = await UpdateChecker.FetchChangelogAsync();

            return JsonSerializer.Serialize(new
            {
                version = latestVersion,
                changelog,
                available = updateAvailable
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

    private static int ExtractPort(string path)
    {
        var parts = path.Split('/');
        return parts.Length > 4 && int.TryParse(parts[4], out var port) ? port : 0;
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
        var config = GetConfig();
        var controllers = GetBotControllers();

        var mode = config?.Mode.ToString() ?? "Unknown";
        var name = config?.Hub?.BotName ?? "SVRaidBot";

        var version = "Unknown";
        try
        {
            var svRaidBotType = Type.GetType("SysBot.Pokemon.SV.BotRaid.Helpers.SVRaidBot, SysBot.Pokemon");
            if (svRaidBotType != null)
            {
                var versionField = svRaidBotType.GetField("Version",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (versionField != null)
                {
                    version = versionField.GetValue(null)?.ToString() ?? "Unknown";
                }
            }

            if (version == "Unknown")
            {
                version = _mainForm.GetType().Assembly.GetName().Version?.ToString() ?? "1.0.0";
            }
        }
        catch
        {
            version = _mainForm.GetType().Assembly.GetName().Version?.ToString() ?? "1.0.0";
        }

        var botStatuses = controllers.Select(c => new BotStatusInfo
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

            return instance;
        }
        catch (Exception ex)
        {
            ProcessId = Environment.ProcessId,
            Name = name,
            Port = _tcpPort,
            Version = version,
            Mode = mode,
            BotCount = botStatuses.Count,
            IsOnline = true,
            IsMaster = true,
            BotStatuses = botStatuses
        };
    }

    private static List<BotInstance> ScanRemoteInstances()
    {
        var instances = new List<BotInstance>();
        var currentPid = Environment.ProcessId;

        try
        {
            // Local sibling instances on the same host
            var processNames = new[] { "SVRaidBot", "SysBot.Pokemon.WinForms", "SysBot" };
            var processes = processNames
                .SelectMany(name => Process.GetProcessesByName(name))
                .Where(p => p.Id != currentPid)
                .Distinct();

            foreach (var process in processes)
            {
                var instance = TryCreateInstance(process);
                if (instance != null)
                    instances.Add(instance);
            }

            // Optional: single remote SVRaidBot reachable over Tailscale, configured via env vars
            // SVRB_REMOTE_HOST=<tailscale-ip-or-hostname>, SVRB_REMOTE_PORT=<tcpPort>
            var remoteHost = Environment.GetEnvironmentVariable("SVRB_REMOTE_HOST");
            var remotePortVar = Environment.GetEnvironmentVariable("SVRB_REMOTE_PORT");
            if (!string.IsNullOrWhiteSpace(remoteHost) && int.TryParse(remotePortVar, out var remotePort))
            {
                var remote = new BotInstance
                {
                    ProcessId = 0,
                    Name = "SVRaidBot (Remote)",
                    Port = remotePort,
                    Version = "Unknown",
                    Mode = "SV",
                    Host = remoteHost,
                    IsOnline = IsPortOpen(remoteHost, remotePort)
                };

                if (remote.IsOnline)
                {
                    UpdateInstanceInfo(remote, remoteHost, remotePort);
                }

                instances.Add(remote);
            }
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Error scanning remote instances: {ex.Message}", "WebServer");
        }

        return instances;
    }

    private static BotInstance? TryCreateInstance(Process process)
    {
        try
        {
            var exePath = process.MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath))
                return null;

            var portFile = Path.Combine(Path.GetDirectoryName(exePath)!, $"SVRaidBot_{process.Id}.port");
            if (!File.Exists(portFile))
                return null;

            var portText = File.ReadAllText(portFile).Trim();
            if (portText.StartsWith("ERROR:") || !int.TryParse(portText, out var port))
                return null;

            var isOnline = IsPortOpen(port);
            var instance = new BotInstance
            {
                ProcessId = process.Id,
                Name = "SVRaidBot",
                Port = port,
                Version = "Unknown",
                Mode = "Unknown",
                BotCount = 0,
                IsOnline = isOnline
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
                    instance.Name = name.GetString() ?? "SVRaidBot";
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

    private static void UpdateInstanceInfo(BotInstance instance, string host, int port)
    {
        try
        {
            var infoResponse = QueryRemote(host, port, "INFO");
            if (infoResponse.StartsWith("{"))
            {
                using var doc = JsonDocument.Parse(infoResponse);
                var root = doc.RootElement;

                if (root.TryGetProperty("Version", out var version))
                    instance.Version = version.GetString() ?? "Unknown";

                if (root.TryGetProperty("Mode", out var mode))
                    instance.Mode = mode.GetString() ?? "Unknown";

                if (root.TryGetProperty("Name", out var name))
                    instance.Name = name.GetString() ?? "SVRaidBot";
            }

            var botsResponse = QueryRemote(host, port, "LISTBOTS");
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
            var success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(200));
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

    private static bool IsPortOpen(string host, int port)
    {
        try
        {
            using var client = new TcpClient();
            var result = client.BeginConnect(host, port, null, null);
            var success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(500));
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

    private string GetBots(int port)
    {
    if (port == _tcpPort)
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
        // Find if this port belongs to a configured remote instance
        var remoteHost = Environment.GetEnvironmentVariable("SVRB_REMOTE_HOST");
        var remotePortVar = Environment.GetEnvironmentVariable("SVRB_REMOTE_PORT");
        if (!string.IsNullOrWhiteSpace(remoteHost) && int.TryParse(remotePortVar, out var remotePort) && remotePort == port)
        {
            return QueryRemote(remoteHost, port, "LISTBOTS");
        }
        return QueryRemote(port, "LISTBOTS");
    }

    private async Task<string> RunCommand(HttpListenerRequest request, int port)
    {
        try
        {
            using var reader = new StreamReader(request.InputStream);
            var body = await reader.ReadToEndAsync();
            var commandRequest = JsonSerializer.Deserialize<BotCommandRequest>(body);

            if (commandRequest == null)
                return CreateErrorResponse("Invalid command request");

            if (port == _tcpPort)
            {
                return RunLocalCommand(commandRequest.Command);
            }

            var tcpCommand = $"{commandRequest.Command}All".ToUpper();
            string result;
            var remoteHost = Environment.GetEnvironmentVariable("SVRB_REMOTE_HOST");
            var remotePortVar = Environment.GetEnvironmentVariable("SVRB_REMOTE_PORT");
            if (!string.IsNullOrWhiteSpace(remoteHost) && int.TryParse(remotePortVar, out var remotePort) && remotePort == port)
                result = QueryRemote(remoteHost, port, tcpCommand);
            else
                result = QueryRemote(port, tcpCommand);

            return JsonSerializer.Serialize(new CommandResponse
            {
                Success = !result.StartsWith("ERROR"),
                Message = result,
                Port = port,
                Command = commandRequest.Command,
                Timestamp = DateTime.Now
            });
        }
        catch (Exception ex)
        {
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
                    var result = !string.IsNullOrWhiteSpace(instance.Host)
                        ? QueryRemote(instance.Host, instance.Port, $"{commandRequest.Command}All".ToUpper())
                        : QueryRemote(instance.Port, $"{commandRequest.Command}All".ToUpper());
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

    public static string QueryRemote(string host, int port, string command)
    {
        try
        {
            using var client = new TcpClient();
            client.Connect(host, port);

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
    public string Version { get; set; } = string.Empty;
    public int BotCount { get; set; }
    public string Mode { get; set; } = string.Empty;
    public bool IsOnline { get; set; }
    public bool IsMaster { get; set; }
    public string? Host { get; set; } // null/127.0.0.1 for local, Tailscale IP/host for remote
    public List<BotStatusInfo>? BotStatuses { get; set; }
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