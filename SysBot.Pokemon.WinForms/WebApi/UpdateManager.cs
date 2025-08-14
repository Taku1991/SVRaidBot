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
        public string BotType { get; set; } = "Unknown";
    }

    public class UpdateStatus
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public DateTime StartTime { get; set; } = DateTime.Now;
        public string Stage { get; set; } = "initializing";
        public string Message { get; set; } = "Starting update process...";
        public int Progress { get; set; } = 0;
        public bool IsComplete { get; set; }
        public bool Success { get; set; }
        public UpdateAllResult? Result { get; set; }
    }

    public enum BotType
    {
        PokeBot,
        RaidBot,
        Unknown
    }

    // New staged update process implementation
    public static async Task<UpdateAllResult> StartUpdateProcessAsync(Main mainForm, int currentPort)
    {
        var result = new UpdateAllResult();
        
        try
        {
            
            var instances = GetAllInstances(currentPort);
            result.TotalInstances = instances.Count;
            
            var instancesNeedingUpdate = new List<(int ProcessId, int Port, string Version, string BotType)>();
            
            // Check which instances need updates
            foreach (var instance in instances)
            {
                try
                {
                    var (updateAvailable, _, latestVersion) = await CheckForUpdatesForBotType(ParseBotType(instance.BotType));
                    if (updateAvailable && !string.IsNullOrEmpty(latestVersion) && instance.Version != latestVersion)
                    {
                        instancesNeedingUpdate.Add((instance.ProcessId, instance.Port, instance.Version, instance.BotType));
                    }
                }
                catch (Exception ex)
                {
                    LogUtil.LogError($"Error checking updates for instance {instance.Port}: {ex.Message}", "UpdateManager");
                }
            }
            
            result.UpdatesNeeded = instancesNeedingUpdate.Count;
            
            if (instancesNeedingUpdate.Count == 0)
            {
                return result;
            }
            
            
            // Phase 1: Send idle commands to all instances
            foreach (var instance in instancesNeedingUpdate)
            {
                try
                {
                    bool idleSuccess = false;
                    if (instance.Port == currentPort)
                    {
                        // Local instance - use direct method call
                        idleSuccess = SendLocalIdleCommand(mainForm);
                    }
                    else
                    {
                        // Remote instance - use TCP command
                        var response = BotServer.QueryRemote(instance.Port, "IDLE");
                        idleSuccess = !response.StartsWith("ERROR");
                    }
                    
                    var instanceResult = new InstanceUpdateResult
                    {
                        Port = instance.Port,
                        ProcessId = instance.ProcessId,
                        CurrentVersion = instance.Version,
                        LatestVersion = await GetLatestVersionForBotType(instance.BotType),
                        NeedsUpdate = true,
                        UpdateStarted = idleSuccess,
                        BotType = instance.BotType,
                        Error = idleSuccess ? null : "Failed to send idle command"
                    };
                    result.InstanceResults.Add(instanceResult);
                    
                    if (idleSuccess)
                    {
                        result.UpdatesStarted++;
                    }
                    else
                    {
                        result.UpdatesFailed++;
                        LogUtil.LogError($"Failed to send idle command to instance on port {instance.Port}", "UpdateManager");
                    }
                }
                catch (Exception ex)
                {
                    result.UpdatesFailed++;
                    LogUtil.LogError($"Error sending idle command to instance {instance.Port}: {ex.Message}", "UpdateManager");
                    
                    var instanceResult = new InstanceUpdateResult
                    {
                        Port = instance.Port,
                        ProcessId = instance.ProcessId,
                        CurrentVersion = instance.Version,
                        LatestVersion = "Unknown",
                        NeedsUpdate = true,
                        UpdateStarted = false,
                        BotType = instance.BotType,
                        Error = ex.Message
                    };
                    result.InstanceResults.Add(instanceResult);
                }
            }
            
            return result;
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Error in StartUpdateProcessAsync: {ex.Message}", "UpdateManager");
            result.UpdatesFailed = 1;
            return result;
        }
    }
    
    public static async Task<UpdateAllResult> ProceedWithUpdatesAsync(Main mainForm, int currentPort)
    {
        var result = new UpdateAllResult();
        
        try
        {
            
            var instances = GetAllInstances(currentPort);
            result.TotalInstances = instances.Count;
            
            var instancesNeedingUpdate = new List<(int ProcessId, int Port, string Version, string BotType, string ExecutablePath)>();
            
            // Get instances that need updates with their executable paths
            foreach (var instance in instances)
            {
                try
                {
                    var (updateAvailable, _, latestVersion) = await CheckForUpdatesForBotType(ParseBotType(instance.BotType));
                    if (updateAvailable && !string.IsNullOrEmpty(latestVersion) && instance.Version != latestVersion)
                    {
                        // Get executable path for this instance
                        var executablePath = GetExecutablePathForInstance(instance.ProcessId, instance.Port);
                        if (!string.IsNullOrEmpty(executablePath))
                        {
                            instancesNeedingUpdate.Add((instance.ProcessId, instance.Port, instance.Version, instance.BotType, executablePath));
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogUtil.LogError($"Error checking updates for instance {instance.Port}: {ex.Message}", "UpdateManager");
                }
            }
            
            result.UpdatesNeeded = instancesNeedingUpdate.Count;
            
            if (instancesNeedingUpdate.Count == 0)
            {
                return result;
            }
            
            
            // Process each instance sequentially: Stop → Update → Restart
            foreach (var (processId, port, currentVersion, botType, executablePath) in instancesNeedingUpdate)
            {
                var instanceResult = new InstanceUpdateResult
                {
                    Port = port,
                    ProcessId = processId,
                    CurrentVersion = currentVersion,
                    LatestVersion = await GetLatestVersionForBotType(botType),
                    NeedsUpdate = true,
                    BotType = botType
                };
                
                try
                {
                    
                    // Step 1: Verify bot is idle and stop it
                    bool stopSuccess = await StopBotInstance(port, processId, currentPort == port, mainForm);
                    if (!stopSuccess)
                    {
                        instanceResult.Error = "Failed to stop bot instance";
                        instanceResult.UpdateStarted = false;
                        result.UpdatesFailed++;
                        result.InstanceResults.Add(instanceResult);
                        continue;
                    }
                    
                    // Step 2: Download and install update
                    bool updateSuccess = await PerformBotUpdate(botType, executablePath);
                    if (!updateSuccess)
                    {
                        instanceResult.Error = "Failed to download or install update";
                        instanceResult.UpdateStarted = false;
                        result.UpdatesFailed++;
                        
                        // Try to restart the old version
                        _ = Task.Run(async () => 
                        {
                            await Task.Delay(2000);
                            await RestartBotInstance(executablePath, port);
                        });
                        
                        result.InstanceResults.Add(instanceResult);
                        continue;
                    }
                    
                    // Step 3: Restart with new version
                    bool restartSuccess = await RestartBotInstance(executablePath, port);
                    if (!restartSuccess)
                    {
                        instanceResult.Error = "Update completed but failed to restart bot";
                        instanceResult.UpdateStarted = true; // Update was successful
                        result.UpdatesStarted++;
                        result.InstanceResults.Add(instanceResult);
                        continue;
                    }
                    
                    // Success!
                    instanceResult.UpdateStarted = true;
                    result.UpdatesStarted++;
                    
                    // Wait a bit before processing next bot
                    await Task.Delay(3000);
                }
                catch (Exception ex)
                {
                    result.UpdatesFailed++;
                    instanceResult.Error = ex.Message;
                    instanceResult.UpdateStarted = false;
                    LogUtil.LogError($"Error updating instance {port}: {ex.Message}", "UpdateManager");
                }
                
                result.InstanceResults.Add(instanceResult);
            }
            
            return result;
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Error in ProceedWithUpdatesAsync: {ex.Message}", "UpdateManager");
            result.UpdatesFailed = 1;
            return result;
        }
    }

    public static async Task<UpdateAllResult> StartPokeBotUpdateAsync(Main mainForm, int currentPort)
    {
        return await StartSpecificBotTypeUpdateAsync(mainForm, currentPort, BotType.PokeBot);
    }

    public static async Task<UpdateAllResult> StartRaidBotUpdateAsync(Main mainForm, int currentPort)
    {
        return await StartSpecificBotTypeUpdateAsync(mainForm, currentPort, BotType.RaidBot);
    }

    private static async Task<UpdateAllResult> StartSpecificBotTypeUpdateAsync(Main mainForm, int currentPort, BotType targetBotType)
    {
        var result = new UpdateAllResult();
        
        if (!IsPortValid(currentPort))
        {
            LogUtil.LogError($"Invalid port: {currentPort}", "UpdateManager");
            result.UpdatesFailed = 1;
            return result;
        }

        try
        {
            var instances = GetAllInstances(currentPort);
            var targetInstances = instances.Where(i => i.BotType == targetBotType.ToString()).ToList();
            
            result.TotalInstances = targetInstances.Count;

            if (targetInstances.Count == 0)
            {
                return result;
            }

            var (updateAvailable, _, latestVersion) = await CheckForUpdatesForBotType(targetBotType);
            
            if (!updateAvailable || string.IsNullOrEmpty(latestVersion))
            {
                return result;
            }

            var instancesNeedingUpdate = targetInstances.Where(i => i.Version != latestVersion).ToList();
            result.UpdatesNeeded = instancesNeedingUpdate.Count;

            foreach (var instance in instancesNeedingUpdate)
            {
                var instanceResult = new InstanceUpdateResult
                {
                    Port = instance.Port,
                    ProcessId = instance.ProcessId,
                    CurrentVersion = instance.Version,
                    LatestVersion = latestVersion,
                    NeedsUpdate = true,
                    UpdateStarted = false, // Would be true if update was actually started
                    BotType = targetBotType.ToString()
                };
                
                result.InstanceResults.Add(instanceResult);
            }

            return result;
        }
        catch (Exception ex)
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
                
                // Wait for process to actually stop
                await Task.Delay(3000);
                
                // Verify process is stopped
                try
                {
                    var process = Process.GetProcessById(processId);
                    if (!process.HasExited)
                    {
                        process.Kill();
                        await Task.Delay(2000);
                    }
                }
                catch
                {
                    // Process is already dead, which is what we want
                }
                
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

    private static async Task<string> DownloadUpdateAsync(string downloadUrl, string botType)
    {
        try
        {
            // Input validation
            if (string.IsNullOrWhiteSpace(downloadUrl) || !Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri))
            {
                LogUtil.LogError("Invalid download URL provided", "UpdateManager");
                return string.Empty;
            }

            if (!ValidateBotType(botType))
            {
                LogUtil.LogError($"Invalid bot type: {botType}", "UpdateManager");
                return string.Empty;
            }

            string tempPath = Path.Combine(Path.GetTempPath(), $"SysBot_{SanitizeForFilename(botType)}_{Guid.NewGuid()}.tmp");

            using (var client = new System.Net.Http.HttpClient())
            {
                client.DefaultRequestHeaders.Add("User-Agent", "SysBot-AutoUpdate/1.0");
                client.Timeout = TimeSpan.FromMinutes(5); // Reduced timeout
                
                LogUtil.LogInfo($"Starting download to: {tempPath}", "UpdateManager");
                
                var response = await client.GetAsync(downloadUrl);
                response.EnsureSuccessStatusCode();
                
                // Check content length before downloading
                if (response.Content.Headers.ContentLength > MAX_DOWNLOAD_SIZE)
                {
                    LogUtil.LogError($"Download size {response.Content.Headers.ContentLength} exceeds maximum allowed size {MAX_DOWNLOAD_SIZE}", "UpdateManager");
                    return string.Empty;
                }
                
                var fileBytes = await response.Content.ReadAsByteArrayAsync();
                
                // Additional size check after download
                if (fileBytes.Length > MAX_DOWNLOAD_SIZE)
                {
                    LogUtil.LogError($"Downloaded file size {fileBytes.Length} exceeds maximum allowed size {MAX_DOWNLOAD_SIZE}", "UpdateManager");
                    return string.Empty;
                }
                
                await File.WriteAllBytesAsync(tempPath, fileBytes);
                
                LogUtil.LogInfo($"Download completed successfully. File size: {fileBytes.Length} bytes", "UpdateManager");
                return tempPath;
            }
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Download failed: {ex.Message}", "UpdateManager");
            return string.Empty;
        }
    }

    private static List<(int ProcessId, int Port, string Version, string BotType)> GetAllInstances(int currentPort)
    {
        var instances = new List<(int, int, string, string)>();
        
        // Add current instance
        var currentBotType = DetectCurrentBotType();
        var currentVersion = GetVersionForCurrentBotType(currentBotType);
        
        instances.Add((Environment.ProcessId, currentPort, currentVersion, currentBotType));

        try
        {
            // Scan for other bot processes
            var processes = new List<Process>();
            
            // Look for PokeBot processes (try multiple names)
            try
            {
                processes.AddRange(Process.GetProcessesByName("PokeBot")
                    .Where(p => p.Id != Environment.ProcessId));
                processes.AddRange(Process.GetProcessesByName("SysBot.Pokemon.WinForms")
                    .Where(p => p.Id != Environment.ProcessId));
            }
            catch (Exception ex)
            {
                LogUtil.LogError($"Error scanning PokeBot processes: {ex.Message}", "UpdateManager");
            }
            
            // Look for RaidBot/SysBot processes (try multiple names)
            try
            {
                processes.AddRange(Process.GetProcessesByName("SysBot")
                    .Where(p => p.Id != Environment.ProcessId));
                processes.AddRange(Process.GetProcessesByName("SVRaidBot")
                    .Where(p => p.Id != Environment.ProcessId));
                processes.AddRange(Process.GetProcessesByName("SysBot.Pokemon.WinForms")
                    .Where(p => p.Id != Environment.ProcessId));
            }
            catch (Exception ex)
            {
                LogUtil.LogError($"Error scanning RaidBot processes: {ex.Message}", "UpdateManager");
            }

            foreach (var process in processes)
            {
                try
                {
                    var instance = TryGetInstanceInfo(process);
                    if (instance.HasValue)
                        instances.Add(instance.Value);
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

    private static (int ProcessId, int Port, string Version, string BotType)? TryGetInstanceInfo(Process process)
    {
        try
        {
            var exePath = process.MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath))
                return null;

            var processName = process.ProcessName.ToLowerInvariant();
            var botType = processName.Contains("raid") || processName.Contains("sv") ? "RaidBot" : "PokeBot";
            
            var exeDir = Path.GetDirectoryName(exePath)!;
            
            // Try to find port file with multiple naming conventions
            var possiblePortFiles = new List<string>();
            
            if (botType == "RaidBot")
            {
                possiblePortFiles.Add(Path.Combine(exeDir, $"SVRaidBot_{process.Id}.port"));
                possiblePortFiles.Add(Path.Combine(exeDir, $"SysBot_{process.Id}.port"));
                possiblePortFiles.Add(Path.Combine(exeDir, $"PokeBot_{process.Id}.port"));
            }
            else
            {
                possiblePortFiles.Add(Path.Combine(exeDir, $"PokeBot_{process.Id}.port"));
                possiblePortFiles.Add(Path.Combine(exeDir, $"SysBot_{process.Id}.port"));
            }
            
            // Find the first existing port file
            var portFile = possiblePortFiles.FirstOrDefault(File.Exists) ?? "";

            if (string.IsNullOrEmpty(portFile))
                return null;

            var portText = File.ReadAllText(portFile).Trim();
            if (!int.TryParse(portText, out var port) || !IsPortValid(port))
                return null;

            if (!IsPortOpen(port))
                return null;

            var versionResponse = BotServer.QueryRemote(port, "INFO");
            var version = versionResponse.StartsWith("ERROR") ? "Unknown" : ExtractVersionFromResponse(versionResponse);

            return (process.Id, port, version, botType);
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Error in TryGetInstanceInfo: {ex.Message}", "UpdateManager");
            return null;
        }
    }

    private static string DetectCurrentBotType()
    {
        try
        {
            // Check if we're in SVRaidBot by looking for specific RaidBot assemblies
            var currentAssembly = System.Reflection.Assembly.GetExecutingAssembly();
            var assemblyName = currentAssembly.GetName().Name;
            
            // Check assembly name or location
            if (assemblyName?.Contains("RaidBot") == true || 
                currentAssembly.Location.Contains("SVRaidBot"))
            {
                return "RaidBot";
            }
            
            // Try to detect RaidBot by type availability
            var raidBotType = Type.GetType("SysBot.Pokemon.SV.BotRaid.Helpers.SVRaidBot, SysBot.Pokemon");
            if (raidBotType != null)
                return "RaidBot";
            
            // Try to detect PokeBot by type availability
            var pokeBotType = Type.GetType("SysBot.Pokemon.Helpers.PokeBot, SysBot.Pokemon");
            if (pokeBotType != null)
                return "PokeBot";

            // Fallback: check executable name
            var exeName = Path.GetFileNameWithoutExtension(System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "");
            if (exeName.Contains("SVRaidBot", StringComparison.OrdinalIgnoreCase) || 
                exeName.Contains("RaidBot", StringComparison.OrdinalIgnoreCase))
                return "RaidBot";
            
            // Default to PokeBot
            return "PokeBot";
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Error detecting bot type: {ex.Message}", "UpdateManager");
            return "PokeBot";
        }
    }

    private static string GetVersionForCurrentBotType(string botType)
    {
        try
        {
            if (botType == "RaidBot")
            {
                // Try multiple ways to get RaidBot version
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
                
                // Fallback: try to get from assembly version
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                return assembly.GetName().Version?.ToString() ?? "Unknown";
            }
            else if (botType == "PokeBot")
            {
                // Try to get PokeBot version
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
                }
                catch
                {
                    // Ignore and fallback
                }
            }
            
            // Final fallback: assembly version
            var currentAssembly = System.Reflection.Assembly.GetExecutingAssembly();
            return currentAssembly.GetName().Version?.ToString() ?? "1.0.0";
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Error getting version for {botType}: {ex.Message}", "UpdateManager");
            return "Unknown";
        }
    }
    
    private static string ExtractVersionFromResponse(string response)
    {
        try
        {
            if (response.StartsWith("{"))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(response);
                if (doc.RootElement.TryGetProperty("Version", out var versionElement))
                {
                    return versionElement.GetString() ?? "Unknown";
                }
            }
            return "Unknown";
        }
        catch
        {
            return "Unknown";
        }
    }

    // Legacy method - kept for compatibility
    public static async Task<bool> PerformAutomaticUpdate(string botType, string latestVersion)
    {
        try
        {
            LogUtil.LogInfo($"Starting automatic {botType} update to version {latestVersion}", "UpdateManager");

            string? downloadUrl = null;
            
            // Get download URL based on bot type
            if (botType == "PokeBot")
            {
                var updateCheckerType = Type.GetType("SysBot.Pokemon.WinForms.PokeBotUpdateChecker, SysBot.Pokemon.WinForms");
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

            if (string.IsNullOrWhiteSpace(downloadUrl))
            {
                LogUtil.LogError($"Failed to fetch download URL for {botType}", "UpdateManager");
                return false;
            }

            LogUtil.LogInfo($"Downloading {botType} update from: {downloadUrl}", "UpdateManager");

            // Download new version
            string tempPath = await DownloadUpdateAsync(downloadUrl, botType);
            if (string.IsNullOrEmpty(tempPath))
            {
                LogUtil.LogError($"Failed to download {botType} update", "UpdateManager");
                return false;
            }

            LogUtil.LogInfo($"Download completed: {tempPath}", "UpdateManager");

            // Install update automatically
            bool installSuccess = await InstallUpdateAutomatically(tempPath, botType);
            
            if (installSuccess)
            {
                LogUtil.LogInfo($"Automatic {botType} update completed successfully", "UpdateManager");
            }
            else
            {
                LogUtil.LogError($"Failed to install {botType} update", "UpdateManager");
            }

            return installSuccess;
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Automatic {botType} update failed: {ex.Message}", "UpdateManager");
            return false;
        }
    }

    private static async Task<bool> InstallUpdateAutomatically(string downloadedFilePath, string botType)
    {
        try
        {
            string currentExePath = Application.ExecutablePath;
            string applicationDirectory = Path.GetDirectoryName(currentExePath) ?? "";
            string executableName = Path.GetFileName(currentExePath);
            string backupPath = Path.Combine(applicationDirectory, $"{executableName}.backup");

            LogUtil.LogInfo($"Installing {botType} update: {downloadedFilePath} -> {currentExePath}", "UpdateManager");

            // Use safer .NET process management instead of batch files
            LogUtil.LogInfo($"Installing {botType} update using managed process approach", "UpdateManager");
            
            // Create backup
            if (File.Exists(currentExePath))
            {
                if (File.Exists(backupPath))
                {
                    File.Delete(backupPath);
                }
                File.Move(currentExePath, backupPath);
                LogUtil.LogInfo("Backup created successfully", "UpdateManager");
            }
            
            // Rename temp file to final executable name
            string finalPath = Path.ChangeExtension(downloadedFilePath, ".exe");
            File.Move(downloadedFilePath, finalPath);
            
            // Move to final location
            File.Move(finalPath, currentExePath);
            
            LogUtil.LogInfo("Update files installed successfully", "UpdateManager");

            // Schedule restart after brief delay
            _ = Task.Run(async () =>
            {
                await Task.Delay(3000); // Wait 3 seconds
                
                try
                {
                    LogUtil.LogInfo("Starting updated application", "UpdateManager");
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = currentExePath,
                        UseShellExecute = true,
                        WorkingDirectory = applicationDirectory
                    });
                }
                catch (Exception ex)
                {
                    LogUtil.LogError($"Failed to start updated application: {ex.Message}", "UpdateManager");
                    
                    // Restore backup on failure
                    if (File.Exists(backupPath))
                    {
                        try
                        {
                            if (File.Exists(currentExePath))
                                File.Delete(currentExePath);
                            File.Move(backupPath, currentExePath);
                            Process.Start(currentExePath);
                            LogUtil.LogInfo("Backup restored and application restarted", "UpdateManager");
                        }
                        catch (Exception restoreEx)
                        {
                            LogUtil.LogError($"Failed to restore backup: {restoreEx.Message}", "UpdateManager");
                        }
                    }
                }
            });

            // Schedule clean shutdown
            LogUtil.LogInfo($"Scheduling clean shutdown for {botType} update", "UpdateManager");
            
            await Task.Delay(1000); // Brief delay to ensure file operations complete
            
            LogUtil.LogInfo("Using Application.Exit for shutdown", "UpdateManager");
            Application.Exit();

            return true;
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Failed to install {botType} update: {ex.Message}", "UpdateManager");
            return false;
        }
    }
}