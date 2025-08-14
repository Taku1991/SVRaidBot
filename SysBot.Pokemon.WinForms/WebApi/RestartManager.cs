using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using SysBot.Base;

namespace SysBot.Pokemon.WinForms.WebApi;

/// <summary>
/// Centralized restart management system that handles scheduled and manual restarts
/// with efficient timing and simplified state management.
/// </summary>
public static class RestartManager
{
    #region Private Fields
    private static readonly object _stateLock = new();
    private static RestartState _currentState = RestartState.Idle;
    private static System.Threading.Timer? _scheduleTimer;
    private static DateTime? _nextScheduledRestart;
    private static CancellationTokenSource? _restartCts;
    private static Main? _mainForm;
    private static int _tcpPort;
    
    // Consolidated file paths
    private static string WorkingDirectory => Path.GetDirectoryName(Application.ExecutablePath) ?? Environment.CurrentDirectory;
    private static string ScheduleConfigPath => Path.Combine(WorkingDirectory, "restart_schedule.json");
    private static string RestartFlagPath => Path.Combine(WorkingDirectory, "restart_in_progress.flag");
    private static string LastRestartPath => Path.Combine(WorkingDirectory, "last_restart.txt");
    private static string PreRestartPidsPath => Path.Combine(WorkingDirectory, "pre_restart_pids.json");
    #endregion

    #region Public Properties
    public static bool IsRestartInProgress
    {
        get { lock (_stateLock) return _currentState != RestartState.Idle; }
    }

    public static RestartState CurrentState
    {
        get { lock (_stateLock) return _currentState; }
    }

    public static DateTime? NextScheduledRestart
    {
        get { lock (_stateLock) return _nextScheduledRestart; }
    }
    #endregion

    #region Initialization
    public static void Initialize(Main mainForm, int tcpPort)
    {
        _mainForm = mainForm ?? throw new ArgumentNullException(nameof(mainForm));
        _tcpPort = tcpPort;
        
        CheckPostRestartStartup();
        InitializeScheduledRestarts();
    }

    public static void Shutdown()
    {
        lock (_stateLock)
        {
            _scheduleTimer?.Dispose();
            _scheduleTimer = null;
            _restartCts?.Cancel();
            _restartCts = null;
            _currentState = RestartState.Idle;
        }
    }
    #endregion

    #region Scheduled Restart Management
    public static void InitializeScheduledRestarts()
    {
        UpdateScheduleTimer();
    }

    public static RestartScheduleConfig GetScheduleConfig()
    {
        try
        {
            if (File.Exists(ScheduleConfigPath))
            {
                var json = File.ReadAllText(ScheduleConfigPath);
                var config = JsonSerializer.Deserialize<RestartScheduleConfig>(json);
                return config ?? new RestartScheduleConfig();
            }
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Failed to load restart schedule: {ex.Message}", "RestartManager");
        }
        
        return new RestartScheduleConfig();
    }

    public static void UpdateScheduleConfig(RestartScheduleConfig config)
    {
        try
        {
            // Save configuration first
            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ScheduleConfigPath, json);
            
            // Clear any existing timer before updating
            lock (_stateLock)
            {
                if (_scheduleTimer != null)
                {
                    _scheduleTimer.Change(Timeout.Infinite, Timeout.Infinite);
                    _scheduleTimer.Dispose();
                    _scheduleTimer = null;
                }
            }
            
            // Update timer based on new configuration
            UpdateScheduleTimer();
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Failed to update restart schedule: {ex.Message}", "RestartManager");
            throw;
        }
    }

    private static void UpdateScheduleTimer()
    {
        lock (_stateLock)
        {
            // Properly dispose of existing timer first
            if (_scheduleTimer != null)
            {
                _scheduleTimer.Change(Timeout.Infinite, Timeout.Infinite);
                _scheduleTimer.Dispose();
                _scheduleTimer = null;
            }
            _nextScheduledRestart = null;

            var config = GetScheduleConfig();
            if (!config.Enabled)
            {
                return;
            }

            if (!TimeSpan.TryParse(config.Time, out var scheduledTime))
            {
                LogUtil.LogError($"Invalid schedule time format: {config.Time}", "RestartManager");
                return;
            }

            var nextRestart = CalculateNextRestartTime(scheduledTime);
            _nextScheduledRestart = nextRestart;

            var delay = nextRestart - DateTime.Now;
            if (delay.TotalMilliseconds > 0)
            {
                _scheduleTimer = new System.Threading.Timer(OnScheduledRestart, null, delay, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private static DateTime CalculateNextRestartTime(TimeSpan scheduledTime)
    {
        var now = DateTime.Now;
        var today = now.Date.Add(scheduledTime);
        
        // If today's time has passed, schedule for tomorrow
        if (today <= now)
        {
            today = today.AddDays(1);
        }
        
        return today;
    }

    private static void OnScheduledRestart(object? state)
    {
        try
        {
            // Check if we already restarted today
            if (WasRestartedToday())
            {
                UpdateScheduleTimer(); // Schedule next restart
                return;
            }

            // Start the restart process asynchronously
            _ = Task.Run(async () =>
            {
                try
                {
                    await ExecuteFullRestartAsync(RestartReason.Scheduled);
                }
                catch (Exception ex)
                {
                    LogUtil.LogError($"Scheduled restart failed: {ex.Message}", "RestartManager");
                }
                finally
                {
                    UpdateScheduleTimer(); // Schedule next restart regardless of outcome
                }
            });
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Error in scheduled restart: {ex.Message}", "RestartManager");
            UpdateScheduleTimer(); // Ensure timer is rescheduled
        }
    }

    private static bool WasRestartedToday()
    {
        try
        {
            if (File.Exists(LastRestartPath))
            {
                var lastRestart = File.ReadAllText(LastRestartPath).Trim();
                return lastRestart == DateTime.Now.ToString("yyyy-MM-dd");
            }
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Failed to check last restart date: {ex.Message}", "RestartManager");
        }
        return false;
    }

    private static void RecordRestartDate()
    {
        try
        {
            File.WriteAllText(LastRestartPath, DateTime.Now.ToString("yyyy-MM-dd"));
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Failed to record restart date: {ex.Message}", "RestartManager");
        }
    }
    #endregion

    #region Manual Restart Management
    public static async Task<RestartResult> TriggerManualRestartAsync()
    {
        return await ExecuteFullRestartAsync(RestartReason.Manual);
    }
    #endregion

    #region Core Restart Logic
    private static async Task<RestartResult> ExecuteFullRestartAsync(RestartReason reason)
    {
        if (_mainForm == null)
        {
            return new RestartResult { Success = false, Error = "Main form not initialized" };
        }

        lock (_stateLock)
        {
            if (_currentState != RestartState.Idle)
            {
                return new RestartResult { Success = false, Error = "Restart already in progress" };
            }
            _currentState = RestartState.Preparing;
            _restartCts = new CancellationTokenSource();
        }

        var result = new RestartResult { Reason = reason };
        
        try
        {
            // Phase 1: Discover all instances
            SetState(RestartState.DiscoveringInstances);
            var instances = DiscoverAllInstances();
            result.TotalInstances = instances.Count;

            // Phase 2: Idle all bots
            SetState(RestartState.IdlingBots);
            await IdleAllBotsAsync(instances);

            // Phase 3: Wait for bots to become idle
            SetState(RestartState.WaitingForIdle);
            var allIdle = await WaitForBotsIdleAsync(instances);
            if (!allIdle)
            {
                await ForceStopAllBotsAsync(instances);
            }

            // Phase 4: Restart slave instances
            SetState(RestartState.RestartingSlaves);
            var slaves = instances.Where(i => i.ProcessId != Environment.ProcessId).ToList();
            await RestartSlaveInstancesAsync(slaves, result);

            // Phase 5: Restart master instance
            SetState(RestartState.RestartingMaster);
            var master = instances.FirstOrDefault(i => i.ProcessId == Environment.ProcessId);
            if (master != null)
            {
                await RestartMasterInstanceAsync(result);
            }

            // Record successful restart
            RecordRestartDate();
            result.Success = true;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            LogUtil.LogError($"{reason} restart failed: {ex.Message}", "RestartManager");
        }
        finally
        {
            SetState(RestartState.Idle);
            _restartCts?.Dispose();
            _restartCts = null;
        }

        return result;
    }

    private static void SetState(RestartState newState)
    {
        lock (_stateLock)
        {
            _currentState = newState;
        }
    }

    private static List<InstanceInfo> DiscoverAllInstances()
    {
        var instances = new List<InstanceInfo>
        {
            new InstanceInfo
            {
                ProcessId = Environment.ProcessId,
                Port = _tcpPort,
                IsMaster = true
            }
        };

        try
        {
            var processes = Process.GetProcessesByName("SVRaidBot")
                .Where(p => p.Id != Environment.ProcessId);

            foreach (var process in processes)
            {
                try
                {
                    var instance = CreateInstanceFromProcess(process);
                    if (instance != null)
                    {
                        instances.Add(instance);
                    }
                }
                catch (Exception ex)
                {
                    LogUtil.LogError($"Failed to create instance from process {process.Id}: {ex.Message}", "RestartManager");
                }
            }
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Error discovering instances: {ex.Message}", "RestartManager");
        }

        return instances;
    }

    private static InstanceInfo? CreateInstanceFromProcess(Process process)
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
            // Port file now contains TCP port on first line, web port on second line (for slaves)
            var lines = portText.Split('\n', '\r').Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
            if (lines.Length == 0 || !int.TryParse(lines[0], out var port))
                return null;

            return new InstanceInfo
            {
                ProcessId = process.Id,
                Port = port,
                IsMaster = false
            };
        }
        catch
        {
            return null;
        }
    }

    private static async Task IdleAllBotsAsync(List<InstanceInfo> instances)
    {
        await ExecuteCommandOnAllInstancesAsync(instances, BotControlCommand.Idle, "idle");
    }
    
    private static async Task ExecuteCommandOnAllInstancesAsync(List<InstanceInfo> instances, BotControlCommand command, string commandName)
    {
        var tasks = instances.Select(instance => ExecuteCommandOnInstanceAsync(instance, command, commandName));
        await Task.WhenAll(tasks);
    }
    
    private static async Task ExecuteCommandOnInstanceAsync(InstanceInfo instance, BotControlCommand command, string commandName)
    {
        try
        {
            if (instance.IsMaster)
            {
                ExecuteLocalCommand(command);
            }
            else
            {
                var response = await Task.Run(() => BotServer.QueryRemote(instance.Port, $"{commandName.ToUpper()}ALL"));
                if (response.StartsWith("ERROR"))
                {
                    LogUtil.LogError($"Failed to {commandName} bots on port {instance.Port}: {response}", "RestartManager");
                }
            }
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Error {commandName}ing instance {instance.ProcessId} on port {instance.Port}: {ex.Message}", "RestartManager");
        }
    }
    
    private static void ExecuteLocalCommand(BotControlCommand command)
    {
        _mainForm!.BeginInvoke((System.Windows.Forms.MethodInvoker)(() =>
        {
            var sendAllMethod = _mainForm.GetType().GetMethod("SendAll",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            sendAllMethod?.Invoke(_mainForm, [command]);
        }));
    }

    private static async Task<bool> WaitForBotsIdleAsync(List<InstanceInfo> instances)
    {
        var timeout = DateTime.Now.AddMinutes(3);

        while (DateTime.Now < timeout)
        {
            var allIdle = await CheckAllBotsIdleAsync(instances);
            if (allIdle)
            {
                return true;
            }

            await Task.Delay(2000);
        }

        return false;
    }

    private static async Task<bool> CheckAllBotsIdleAsync(List<InstanceInfo> instances)
    {
        try
        {
            var tasks = instances.Select(instance => CheckInstanceBotsIdleAsync(instance));
            var results = await Task.WhenAll(tasks);
            return results.All(idle => idle);
        }
        catch
        {
            return false;
        }
    }
    
    private static async Task<bool> CheckInstanceBotsIdleAsync(InstanceInfo instance)
    {
        try
        {
            if (instance.IsMaster)
            {
                return CheckLocalBotsIdle();
            }
            else
            {
                return await CheckRemoteBotsIdleAsync(instance.Port);
            }
        }
        catch
        {
            return false;
        }
    }
    
    private static bool CheckLocalBotsIdle()
    {
        var flpBotsField = _mainForm!.GetType().GetField("FLP_Bots",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            
        if (flpBotsField?.GetValue(_mainForm) is FlowLayoutPanel flpBots)
        {
            var controllers = flpBots.Controls.OfType<BotController>().ToList();
            return controllers.All(c =>
            {
                var state = c.ReadBotState();
                return state == "IDLE" || state == "STOPPED";
            });
        }
        return true;
    }
    
    private static async Task<bool> CheckRemoteBotsIdleAsync(int port)
    {
        return await Task.Run(() =>
        {
            var botsResponse = BotServer.QueryRemote(port, "LISTBOTS");
            if (!botsResponse.StartsWith("{") || !botsResponse.Contains("Bots"))
                return true;
                
            try
            {
                var botsData = JsonSerializer.Deserialize<Dictionary<string, List<Dictionary<string, object>>>>(botsResponse);
                if (botsData?.ContainsKey("Bots") == true)
                {
                    return botsData["Bots"].All(b =>
                    {
                        if (b.TryGetValue("Status", out var status))
                        {
                            var statusStr = status?.ToString()?.ToUpperInvariant() ?? "";
                            return statusStr == "IDLE" || statusStr == "STOPPED";
                        }
                        return true;
                    });
                }
            }
            catch
            {
                // If we can't parse the response, assume not idle
                return false;
            }
            return true;
        });
    }

    private static async Task ForceStopAllBotsAsync(List<InstanceInfo> instances)
    {
        await ExecuteCommandOnAllInstancesAsync(instances, BotControlCommand.Stop, "stop");
    }

    private static async Task RestartSlaveInstancesAsync(List<InstanceInfo> slaves, RestartResult result)
    {
        foreach (var slave in slaves)
        {
            var instanceResult = new InstanceRestartResult
            {
                Port = slave.Port,
                ProcessId = slave.ProcessId
            };

            try
            {
                // First ensure all bots are stopped on this instance
                var stopResponse = BotServer.QueryRemote(slave.Port, "STOPALL");
                await Task.Delay(1000);

                var response = BotServer.QueryRemote(slave.Port, "SELFRESTARTALL");
                if (!response.StartsWith("ERROR"))
                {
                    instanceResult.Success = true;

                    // Wait for process termination
                    var terminated = await WaitForProcessTerminationAsync(slave.ProcessId, 30);
                    if (terminated)
                    {
                        // Wait for instance to come back online
                        var backOnline = await WaitForInstanceOnlineAsync(slave.Port, 60);
                    }
                    else
                    {
                        LogUtil.LogError($"Process {slave.ProcessId} did not terminate in time", "RestartManager");
                    }
                }
                else
                {
                    instanceResult.Error = response;
                    LogUtil.LogError($"Failed to restart port {slave.Port}: {response}", "RestartManager");
                }
            }
            catch (Exception ex)
            {
                instanceResult.Error = ex.Message;
                LogUtil.LogError($"Error restarting port {slave.Port}: {ex.Message}", "RestartManager");
            }

            result.InstanceResults.Add(instanceResult);
        }
    }

    private static async Task RestartMasterInstanceAsync(RestartResult result)
    {
        // Save current process IDs before restart
        SavePreRestartProcessIds();
        
        // Create restart flag for post-restart detection
        File.WriteAllText(RestartFlagPath, DateTime.Now.ToString());
        
        result.MasterRestarting = true;
        
        // Give a moment for any pending operations
        await Task.Delay(2000);
        
        // Restart the application
        _mainForm!.BeginInvoke((System.Windows.Forms.MethodInvoker)(() =>
        {
            Application.Restart();
        }));
    }
    
    private static void SavePreRestartProcessIds()
    {
        try
        {
            var pids = new List<int>();
            
            // Add current process ID
            pids.Add(Environment.ProcessId);
            
            // Add all SVRaidBot process IDs
            var processes = Process.GetProcessesByName("SVRaidBot");
            foreach (var process in processes)
            {
                try
                {
                    pids.Add(process.Id);
                }
                catch { }
            }
            
            var json = JsonSerializer.Serialize(pids);
            File.WriteAllText(PreRestartPidsPath, json);
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Failed to save pre-restart process IDs: {ex.Message}", "RestartManager");
        }
    }

    private static async Task<bool> WaitForProcessTerminationAsync(int processId, int timeoutSeconds)
    {
        var endTime = DateTime.Now.AddSeconds(timeoutSeconds);

        while (DateTime.Now < endTime)
        {
            try
            {
                var process = Process.GetProcessById(processId);
                if (process.HasExited)
                    return true;
            }
            catch (ArgumentException)
            {
                // Process not found = terminated
                return true;
            }

            await Task.Delay(500);
        }

        return false;
    }

    private static async Task<bool> WaitForInstanceOnlineAsync(int port, int timeoutSeconds)
    {
        var endTime = DateTime.Now.AddSeconds(timeoutSeconds);

        while (DateTime.Now < endTime)
        {
            if (IsPortOpen(port))
            {
                await Task.Delay(1000); // Give it a moment to fully initialize
                return true;
            }

            await Task.Delay(1000);
        }

        return false;
    }

    private static bool IsPortOpen(int port)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            var result = client.BeginConnect("127.0.0.1", port, null, null);
            var success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(1));
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
    #endregion

    #region Post-Restart Startup
    private static void CheckPostRestartStartup()
    {
        try
        {
            if (!File.Exists(RestartFlagPath))
                return;

            File.Delete(RestartFlagPath);
            
            // Kill any lingering old processes
            KillOldProcesses();

            // Start the post-restart sequence asynchronously
            Task.Run(() => ExecutePostRestartSequenceAsync());
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Error in post-restart startup: {ex.Message}", "RestartManager");
        }
    }
    
    private static void KillOldProcesses()
    {
        try
        {
            if (!File.Exists(PreRestartPidsPath))
                return;
                
            var json = File.ReadAllText(PreRestartPidsPath);
            var oldPids = JsonSerializer.Deserialize<List<int>>(json);
            File.Delete(PreRestartPidsPath); // Clean up the file
            
            if (oldPids == null || oldPids.Count == 0)
                return;
            
            var currentPid = Environment.ProcessId;
            
            foreach (var pid in oldPids)
            {
                // Don't kill the current process
                if (pid == currentPid)
                    continue;
                    
                try
                {
                    var process = Process.GetProcessById(pid);
                    if (process != null && !process.HasExited)
                    {
                        process.Kill();
                        process.WaitForExit(5000); // Wait up to 5 seconds for it to exit
                    }
                }
                catch (ArgumentException)
                {
                    // Process doesn't exist, that's fine
                }
                catch (Exception ex)
                {
                    LogUtil.LogError($"Failed to kill old process {pid}: {ex.Message}", "RestartManager");
                }
            }
            
            // Give a moment for processes to fully terminate
            Thread.Sleep(1000);
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Error killing old processes: {ex.Message}", "RestartManager");
        }
    }
    
    private static async Task ExecutePostRestartSequenceAsync()
    {
        await Task.Delay(5000); // Give system time to stabilize
        
        const int maxAttempts = 12;
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                // Start all bots
                await StartAllBotsAsync();
                break;
            }
            catch (Exception ex)
            {
                LogUtil.LogError($"Error during post-restart startup attempt {attempt + 1}: {ex.Message}", "RestartManager");
                if (attempt < maxAttempts - 1)
                    await Task.Delay(5000);
            }
        }
    }
    
    private static async Task StartAllBotsAsync()
    {
        // Start local bots
        ExecuteLocalCommand(BotControlCommand.Start);
        
        // Start remote instances
        var instances = GetAllRunningInstances();
        if (instances.Count > 0)
        {
            var tasks = instances.Select(async instance =>
            {
                try
                {
                    var response = await Task.Run(() => BotServer.QueryRemote(instance.Port, "STARTALL"));
                }
                catch (Exception ex)
                {
                    LogUtil.LogError($"Failed to send start command to port {instance.Port}: {ex.Message}", "RestartManager");
                }
            });
            
            await Task.WhenAll(tasks);
        }
    }

    private static List<(int Port, int ProcessId)> GetAllRunningInstances()
    {
        var instances = new List<(int, int)>();

        try
        {
            var processes = Process.GetProcessesByName("SVRaidBot")
                .Where(p => p.Id != Environment.ProcessId);

            foreach (var process in processes)
            {
                try
                {
                    var exePath = process.MainModule?.FileName;
                    if (string.IsNullOrEmpty(exePath))
                        continue;

                    var portFile = Path.Combine(Path.GetDirectoryName(exePath)!, $"SVRaidBot_{process.Id}.port");
                    if (!File.Exists(portFile))
                        continue;

                    var portText = File.ReadAllText(portFile).Trim();
                    // Port file now contains TCP port on first line, web port on second line (for slaves)
                    var lines = portText.Split('\n', '\r').Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
                    if (lines.Length == 0 || !int.TryParse(lines[0], out var port))
                        continue;

                    if (IsPortOpen(port))
                    {
                        instances.Add((port, process.Id));
                    }
                }
                catch { }
            }
        }
        catch { }

        return instances;
    }
    #endregion
}

#region Data Classes
public enum RestartState
{
    Idle,
    Preparing,
    DiscoveringInstances,
    IdlingBots,
    WaitingForIdle,
    RestartingSlaves,
    RestartingMaster
}

public enum RestartReason
{
    Manual,
    Scheduled
}

public class RestartScheduleConfig
{
    public bool Enabled { get; set; } = false;
    public string Time { get; set; } = "00:00";
}

public class RestartResult
{
    public bool Success { get; set; }
    public RestartReason Reason { get; set; }
    public int TotalInstances { get; set; }
    public bool MasterRestarting { get; set; }
    public string? Error { get; set; }
    public List<InstanceRestartResult> InstanceResults { get; set; } = new();
}

public class InstanceRestartResult
{
    public int Port { get; set; }
    public int ProcessId { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
}

public class InstanceInfo
{
    public int ProcessId { get; set; }
    public int Port { get; set; }
    public bool IsMaster { get; set; }
}
#endregion