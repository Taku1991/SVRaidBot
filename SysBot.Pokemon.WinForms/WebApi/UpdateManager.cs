using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Reflection;
using SysBot.Base;
using SysBot.Pokemon.SV.BotRaid.Helpers;

namespace SysBot.Pokemon.WinForms.WebApi;

public static class UpdateManager
{
    public static bool IsSystemUpdateInProgress { get; private set; }
    public static bool IsSystemRestartInProgress { get; private set; }

    private static readonly ConcurrentDictionary<string, UpdateStatus> _activeUpdates = new();
    
    // Security constants
    private const long MAX_DOWNLOAD_SIZE = 500 * 1024 * 1024; // 500MB
    private const int MAX_CONCURRENT_UPDATES = 3;
    private const int COMMAND_TIMEOUT_MS = 30000; // 30 seconds
    
    // Input validation
    private static readonly Regex ValidBotTypeRegex = new(@"^[A-Za-z0-9]+$", RegexOptions.Compiled);
    private static readonly HashSet<string> AllowedBotTypes = new() { "PokeBot", "RaidBot" };
    
    // Security helper methods
    private static bool ValidateBotType(string botType)
    {
        return !string.IsNullOrWhiteSpace(botType) && 
               ValidBotTypeRegex.IsMatch(botType) && 
               AllowedBotTypes.Contains(botType);
    }
    
    private static string SanitizeForFilename(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "Unknown";
            
        var sanitized = new StringBuilder();
        foreach (char c in input)
        {
            if (char.IsLetterOrDigit(c))
                sanitized.Append(c);
        }
        return sanitized.Length > 0 ? sanitized.ToString() : "Unknown";
    }
    
    private static bool ValidateFilePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
            
        try
        {
            var fullPath = Path.GetFullPath(path);
            return !path.Contains("..") && 
                   !path.Contains(":") || path.Length > 3; // Allow drive letters
        }
        catch
        {
            return false;
        }
    }
    
    private static bool IsPortValid(int port)
    {
        return port > 0 && port <= 65535;
    }

    private static BotType ParseBotType(string botType)
    {
        return botType.ToLowerInvariant() switch
        {
            "pokebot" => BotType.PokeBot,
            "raidbot" => BotType.RaidBot,
            _ => BotType.Unknown
        };
    }

    public class UpdateAllResult
    {
        public int TotalInstances { get; set; }
        public int UpdatesNeeded { get; set; }
        public int UpdatesStarted { get; set; }
        public int UpdatesFailed { get; set; }
        public List<InstanceUpdateResult> InstanceResults { get; set; } = [];
    }

    public class InstanceUpdateResult
    {
        public int Port { get; set; }
        public int ProcessId { get; set; }
        public string CurrentVersion { get; set; } = string.Empty;
        public string LatestVersion { get; set; } = string.Empty;
        public bool NeedsUpdate { get; set; }
        public bool UpdateStarted { get; set; }
        public string? Error { get; set; }
    }

    public static async Task<UpdateAllResult> UpdateAllInstancesAsync(Main mainForm, int currentPort)
    {
        var result = new UpdateAllResult();

        var (updateAvailable, _, latestVersion) = await UpdateChecker.CheckForUpdatesAsync(false);
        if (string.IsNullOrEmpty(latestVersion))
        {
            result.UpdatesFailed = 1;
            result.InstanceResults.Add(new InstanceUpdateResult
            {
                Error = "Failed to fetch latest version"
            });
            return result;
        }

        var instances = GetAllInstances(currentPort);
        result.TotalInstances = instances.Count;

        var instancesNeedingUpdate = new List<(int ProcessId, int Port, string Version)>();

        foreach (var instance in instances)
        {
            if (instance.Version != latestVersion)
            {
                instancesNeedingUpdate.Add(instance);
                result.UpdatesNeeded++;
            }
        }

        if (instancesNeedingUpdate.Count == 0)
        {
            return result;
        }

        LogUtil.LogInfo($"Idling all bots across {instancesNeedingUpdate.Count} instances before updates...", "UpdateManager");

        foreach (var (processId, port, version) in instancesNeedingUpdate)
        {
            if (processId == Environment.ProcessId)
            {
                var flpBotsField = mainForm.GetType().GetField("FLP_Bots",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                if (flpBotsField?.GetValue(mainForm) is FlowLayoutPanel flpBots)
                {
                    var controllers = flpBots.Controls.OfType<BotController>().ToList();
                    foreach (var controller in controllers)
                    {
                        var currentState = controller.ReadBotState();
                        if (currentState != "IDLE" && currentState != "STOPPED")
                        {
                            controller.SendCommand(BotControlCommand.Idle, false);
                        }
                    }
                }
            }
            else
            {
                var idleResponse = BotServer.QueryRemote(port, "IDLEALL");
                if (idleResponse.StartsWith("ERROR"))
                {
                    LogUtil.LogError($"Failed to send idle command to port {port}", "UpdateManager");
                }
            }
        }

        LogUtil.LogInfo("Waiting for all bots to finish current operations and go idle...", "UpdateManager");

        // Pass ALL instances to check, not just ones needing update
        var allInstances = instances.Select(i => (i.ProcessId, i.Port, i.Version)).ToList();
        var allIdle = await WaitForAllInstancesToBeIdle(mainForm, allInstances, 300);

        if (!allIdle)
        {
            result.UpdatesFailed = instancesNeedingUpdate.Count;
            foreach (var (processId, port, version) in instancesNeedingUpdate)
            {
                result.InstanceResults.Add(new InstanceUpdateResult
                {
                    Port = port,
                    ProcessId = processId,
                    CurrentVersion = version,
                    LatestVersion = latestVersion,
                    NeedsUpdate = true,
                    Error = "Timeout waiting for all instances to idle - updates cancelled"
                });
            }
            return result;
        }

        var sortedInstances = instancesNeedingUpdate
            .Where(i => i.ProcessId != Environment.ProcessId)
            .Concat(instancesNeedingUpdate.Where(i => i.ProcessId == Environment.ProcessId))
            .ToList();

        foreach (var (processId, port, currentVersion) in sortedInstances)
        {
            var instanceResult = new InstanceUpdateResult
            {
                Port = port,
                ProcessId = processId,
                CurrentVersion = currentVersion,
                LatestVersion = latestVersion,
                NeedsUpdate = true
            };

            try
            {
                if (processId == Environment.ProcessId)
                {
                    var updateForm = new UpdateForm(false, latestVersion, true);
                    mainForm.BeginInvoke((MethodInvoker)(() =>
                    {
                        updateForm.PerformUpdate();
                    }));

                    instanceResult.UpdateStarted = true;
                    result.UpdatesStarted++;
                    LogUtil.LogInfo("Master instance update triggered", "UpdateManager");
                }
                else
                {
                    LogUtil.LogInfo($"Triggering update for instance on port {port}...", "UpdateManager");
                    var updateResponse = BotServer.QueryRemote(port, "UPDATE");
                    if (!updateResponse.StartsWith("ERROR"))
                    {
                        instanceResult.UpdateStarted = true;
                        result.UpdatesStarted++;
                        LogUtil.LogInfo($"Update triggered for instance on port {port}", "UpdateManager");
                        await Task.Delay(5000);
                    }
                    else
                    {
                        instanceResult.Error = "Failed to start update";
                        result.UpdatesFailed++;
                    }
                }
            }
            catch (Exception ex)
            {
                instanceResult.Error = ex.Message;
                result.UpdatesFailed++;
                LogUtil.LogError($"Error updating instance on port {port}: {ex.Message}", "UpdateManager");
            }

            result.InstanceResults.Add(instanceResult);
        }

        return result;
    }

    public static async Task<UpdateAllResult> StartUpdateProcessAsync(Main mainForm, int currentPort)
    {
        var result = new UpdateAllResult();

        var (updateAvailable, _, latestVersion) = await UpdateChecker.CheckForUpdatesAsync(false);
        if (string.IsNullOrEmpty(latestVersion))
        {
            result.UpdatesFailed = 1;
            result.InstanceResults.Add(new InstanceUpdateResult
            {
                Error = "Failed to fetch latest version"
            });
            return result;
        }

        var instances = GetAllInstances(currentPort);
        result.TotalInstances = instances.Count;

        var instancesNeedingUpdate = new List<(int ProcessId, int Port, string Version)>();

        foreach (var instance in instances)
        {
            if (instance.Version != latestVersion)
            {
                instancesNeedingUpdate.Add(instance);
                result.UpdatesNeeded++;
                result.InstanceResults.Add(new InstanceUpdateResult
                {
                    Port = instance.Port,
                    ProcessId = instance.ProcessId,
                    CurrentVersion = instance.Version,
                    LatestVersion = latestVersion,
                    NeedsUpdate = true
                });
            }

            return result;
        }

        // Start idling all bots
        LogUtil.LogInfo($"Idling all bots across {instancesNeedingUpdate.Count} instances before updates...", "UpdateManager");

        foreach (var (processId, port, version) in instancesNeedingUpdate)
        {
            LogUtil.LogError($"Error in {targetBotType} update: {ex.Message}", "UpdateManager");
            result.UpdatesFailed = 1;
            return result;
        }
    }

    private static async Task<(bool, string, string)> CheckForUpdatesForBotType(BotType botType)
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
        catch (Exception ex)
        {
            LogUtil.LogError($"Error checking updates for {botType}: {ex.Message}", "UpdateManager");
            return (false, "", "Unknown");
        }
    }

    private static async Task<(bool, string, string)> CheckPokeBotUpdates()
    {
        try
        {
            // Try to use PokeBotUpdateChecker if available
            var updateCheckerType = Type.GetType("SysBot.Pokemon.WinForms.PokeBotUpdateChecker, SysBot.Pokemon.WinForms");
            if (updateCheckerType != null)
            {
                var method = updateCheckerType.GetMethod("CheckForUpdatesAsync", 
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (method != null)
                {
                    var task = (Task<(bool, string, string)>)method.Invoke(null, new object[] { false });
                    return await task;
                }
            }
            
            return (false, "", "Unknown");
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Error checking PokeBot updates: {ex.Message}", "UpdateManager");
            return (false, "", "Unknown");
        }
    }

    private static async Task<(bool, string, string)> CheckRaidBotUpdates()
    {
        try
        {
            // Try to use RaidBotUpdateChecker if available
            var updateCheckerType = Type.GetType("SysBot.Pokemon.WinForms.RaidBotUpdateChecker, SysBot.Pokemon.WinForms");
            if (updateCheckerType != null)
            {
                var method = updateCheckerType.GetMethod("CheckForUpdatesAsync", 
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (method != null)
                {
                    var task = (Task<(bool, string, string)>)method.Invoke(null, new object[] { false });
                    return await task;
                }
            }
            
            return (false, "", "Unknown");
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Error checking RaidBot updates: {ex.Message}", "UpdateManager");
            return (false, "", "Unknown");
        }
    }

    private static async Task<string> GetLatestVersionForBotType(string botType)
    {
        if (!ValidateBotType(botType))
        {
            LogUtil.LogError($"Invalid bot type: {botType}", "UpdateManager");
            return "Unknown";
        }

        try
        {
            if (botType == "PokeBot")
            {
                var (updateAvailable, _, latestVersion) = await CheckPokeBotUpdates();
                return updateAvailable ? latestVersion ?? "Unknown" : "Unknown";
            }
            else if (botType == "RaidBot")
            {
                var (updateAvailable, _, latestVersion) = await CheckRaidBotUpdates();
                return updateAvailable ? latestVersion ?? "Unknown" : "Unknown";
            }
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Error getting latest version for {botType}: {ex.Message}", "UpdateManager");
        }
        return "Unknown";
    }

    // Helper methods for staged update process
    private static bool SendLocalIdleCommand(Main mainForm)
    {
        try
        {
            // Send idle command to all local bots
            var idleMethod = mainForm.GetType().GetMethod("SendAll",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            
            if (idleMethod != null)
            {
                // Get BotControlCommand.Idle enum value
                var botControlCommandType = Type.GetType("SysBot.Pokemon.WinForms.Controls.BotControlCommand, SysBot.Pokemon.WinForms");
                if (botControlCommandType != null)
                {
                    var idleCommand = Enum.Parse(botControlCommandType, "Idle");
                    idleMethod.Invoke(mainForm, new object[] { idleCommand });
                    return true;
                }
            }
            
            return false;
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Failed to send local idle command: {ex.Message}", "UpdateManager");
            return false;
        }
    }
    
    private static string GetExecutablePathForInstance(int processId, int port)
    {
        try
        {
            var process = Process.GetProcessById(processId);
            return process.MainModule?.FileName ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
    
    private static async Task<bool> StopBotInstance(int port, int processId, bool isLocalInstance, Main mainForm)
    {
        try
        {
            
            if (isLocalInstance)
            {
                // For local instance, we'll shut down after all remote instances are updated
                return true;
            }
            else
            {
                // For remote instances, send stop command and wait for process to end
                var response = BotServer.QueryRemote(port, "STOP");
                if (response.StartsWith("ERROR"))
                {
                    LogUtil.LogError($"Failed to send stop command to port {port}: {response}", "UpdateManager");
                    return false;
                }
            }
        }

        // Return immediately after starting the idle process
        return result;
    }

    public static async Task<UpdateAllResult> ProceedWithUpdatesAsync(Main mainForm, int currentPort)
    {
        var result = new UpdateAllResult();

        var (updateAvailable, _, latestVersion) = await UpdateChecker.CheckForUpdatesAsync(false);
        var instances = GetAllInstances(currentPort);
        var instancesNeedingUpdate = instances.Where(i => i.Version != latestVersion).ToList();

        result.TotalInstances = instances.Count;
        result.UpdatesNeeded = instancesNeedingUpdate.Count;

        var sortedInstances = instancesNeedingUpdate
            .Where(i => i.ProcessId != Environment.ProcessId)
            .Concat(instancesNeedingUpdate.Where(i => i.ProcessId == Environment.ProcessId))
            .ToList();

        foreach (var (processId, port, currentVersion) in sortedInstances)
        {
            var instanceResult = new InstanceUpdateResult
            {
                Port = port,
                ProcessId = processId,
                CurrentVersion = currentVersion,
                LatestVersion = latestVersion,
                NeedsUpdate = true
            };

            try
            {
                if (processId == Environment.ProcessId)
                {
                    var updateForm = new UpdateForm(false, latestVersion, true);
                    mainForm.BeginInvoke((MethodInvoker)(() =>
                    {
                        updateForm.PerformUpdate();
                    }));

                    instanceResult.UpdateStarted = true;
                    result.UpdatesStarted++;
                    LogUtil.LogInfo("Master instance update triggered", "UpdateManager");
                }
                catch
                {
                    LogUtil.LogInfo($"Triggering update for instance on port {port}...", "UpdateManager");
                    var updateResponse = BotServer.QueryRemote(port, "UPDATE");
                    if (!updateResponse.StartsWith("ERROR"))
                    {
                        instanceResult.UpdateStarted = true;
                        result.UpdatesStarted++;
                        LogUtil.LogInfo($"Update triggered for instance on port {port}", "UpdateManager");
                        await Task.Delay(5000);
                    }
                    else
                    {
                        instanceResult.Error = "Failed to start update";
                        result.UpdatesFailed++;
                    }
                }
            }
            catch (Exception ex)
            {
                instanceResult.Error = ex.Message;
                result.UpdatesFailed++;
                LogUtil.LogError($"Error updating instance on port {port}: {ex.Message}", "UpdateManager");
            }

            result.InstanceResults.Add(instanceResult);
        }

        return result;
    }

    private static async Task<bool> WaitForAllInstancesToBeIdle(Main mainForm, List<(int ProcessId, int Port, string Version)> instances, int timeoutSeconds)
    {
        var endTime = DateTime.Now.AddSeconds(timeoutSeconds);
        const int delayMs = 1000;

        while (DateTime.Now < endTime)
        {
            var allInstancesIdle = true;
            var statusReport = new List<string>();

            foreach (var (processId, port, version) in instances)
            {
                if (processId == Environment.ProcessId)
                {
                    var flpBotsField = mainForm.GetType().GetField("FLP_Bots",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                    if (flpBotsField?.GetValue(mainForm) is FlowLayoutPanel flpBots)
                    {
                        var controllers = flpBots.Controls.OfType<BotController>().ToList();
                        var notIdle = controllers.Where(c =>
                        {
                            var state = c.ReadBotState();
                            return state != "IDLE" && state != "STOPPED";
                        }).ToList();

                        if (notIdle.Any())
                        {
                            allInstancesIdle = false;
                            var states = notIdle.Select(c => c.ReadBotState()).Distinct();
                            statusReport.Add($"Master: {string.Join(", ", states)}");
                        }
                    }
                }
                else
                {
                    var botsResponse = BotServer.QueryRemote(port, "LISTBOTS");

                    if (botsResponse.StartsWith("{") && botsResponse.Contains("Bots"))
                    {
                        try
                        {
                            var botsData = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, List<Dictionary<string, object>>>>(botsResponse);
                            if (botsData?.ContainsKey("Bots") == true)
                            {
                                var bots = botsData["Bots"];
                                var notIdle = bots.Where(b =>
                                {
                                    if (b.TryGetValue("Status", out var status))
                                    {
                                        var statusStr = status?.ToString() ?? "";
                                        return statusStr != "IDLE" && statusStr != "STOPPED";
                                    }
                                    return false;
                                }).ToList();

                                if (notIdle.Count != 0)
                                {
                                    allInstancesIdle = false;
                                    var states = notIdle.Select(b => b.TryGetValue("Status", out var s) ? s?.ToString() : "Unknown").Distinct();
                                    statusReport.Add($"Port {port}: {string.Join(", ", states)}");
                                }
                            }
                        }
                        catch { }
                    }
                }
            }

            if (allInstancesIdle)
            {
                LogUtil.LogInfo("All bots across all instances are now idle", "UpdateManager");
                return true;
            }
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Error stopping bot instance on port {port}: {ex.Message}", "UpdateManager");
            return false;
        }
    }
    
    private static async Task<bool> PerformBotUpdate(string botType, string executablePath)
    {
        try
        {
            LogUtil.LogInfo($"Downloading and installing {botType} update", "UpdateManager");
            
            // Get download URL
            string downloadUrl;
            if (botType == "PokeBot")
            {
                var updateCheckerType = Type.GetType("SysBot.Pokemon.WinForms.UpdateChecker, SysBot.Pokemon.WinForms");
                var method = updateCheckerType?.GetMethod("FetchDownloadUrlAsync", 
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                var task = (Task<string>)method?.Invoke(null, null);
                downloadUrl = await task ?? string.Empty;
            }
            else if (botType == "RaidBot")
            {
                var updateCheckerType = Type.GetType("SysBot.Pokemon.WinForms.RaidBotUpdateChecker, SysBot.Pokemon.WinForms");
                var method = updateCheckerType?.GetMethod("FetchDownloadUrlAsync", 
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                var task = (Task<string>)method?.Invoke(null, null);
                downloadUrl = await task ?? string.Empty;
            }
            else
            {
                LogUtil.LogError($"Unknown bot type: {botType}", "UpdateManager");
                return false;
            }
            
            if (string.IsNullOrWhiteSpace(downloadUrl))
            {
                LogUtil.LogError($"Failed to get download URL for {botType}", "UpdateManager");
                return false;
            }
            
            // Download update
            var tempPath = await DownloadUpdateAsync(downloadUrl, botType);
            if (string.IsNullOrEmpty(tempPath))
            {
                LogUtil.LogError($"Failed to download {botType} update", "UpdateManager");
                return false;
            }
            
            // Install update by replacing executable
            var executableDir = Path.GetDirectoryName(executablePath);
            var backupPath = Path.Combine(executableDir, Path.GetFileName(executablePath) + ".backup");
            
            // Create backup
            if (File.Exists(executablePath))
            {
                if (File.Exists(backupPath))
                    File.Delete(backupPath);
                File.Move(executablePath, backupPath);
            }
            
            // Install new version
            File.Move(tempPath, executablePath);
            
            LogUtil.LogInfo($"Successfully installed {botType} update", "UpdateManager");
            return true;
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Error performing bot update: {ex.Message}", "UpdateManager");
            return false;
        }
    }
    
    private static async Task<bool> RestartBotInstance(string executablePath, int expectedPort)
    {
        try
        {
            
            var workingDirectory = Path.GetDirectoryName(executablePath);
            
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = workingDirectory,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal
            };
            
            var process = Process.Start(startInfo);
            if (process == null)
            {
                LogUtil.LogError($"Failed to start process: {executablePath}", "UpdateManager");
                return false;
            }
            
            // Wait for bot to start and create port file
            await Task.Delay(5000);
            
            // Verify it's running by checking if port is responding
            for (int i = 0; i < 10; i++)
            {
                if (IsPortOpen(expectedPort))
                {
                    LogUtil.LogInfo($"Bot successfully restarted on port {expectedPort}", "UpdateManager");
                    return true;
                }
                await Task.Delay(1000);
            }
            
            LogUtil.LogError($"Bot started but port {expectedPort} is not responding", "UpdateManager");
            return false;
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Error restarting bot instance: {ex.Message}", "UpdateManager");
            return false;
        }
    }
    
    private static bool IsPortOpen(int port)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            var result = client.BeginConnect("127.0.0.1", port, null, null);
            var success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(1000));
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

    private static List<(int ProcessId, int Port, string Version)> GetAllInstances(int currentPort)
    {
        var instances = new List<(int, int, string)>
        {
            (Environment.ProcessId, currentPort, SVRaidBot.Version)
        };

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
                    if (!int.TryParse(portText, out var port))
                        continue;

                    if (!IsPortOpen(port))
                        continue;

                    var versionResponse = BotServer.QueryRemote(port, "VERSION");
                    var version = versionResponse.StartsWith("ERROR") ? "Unknown" : versionResponse.Trim();

                    instances.Add((process.Id, port, version));
                }
                catch (Exception ex)
                {
                    LogUtil.LogError($"Error getting instance info for process {process.Id}: {ex.Message}", "UpdateManager");
                }
            }
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Error scanning for instances: {ex.Message}", "UpdateManager");
        }

        return instances;
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
}