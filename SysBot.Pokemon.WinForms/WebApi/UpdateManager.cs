using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using SysBot.Base;
using SysBot.Pokemon.SV.BotRaid.Helpers;

namespace SysBot.Pokemon.WinForms.WebApi;

public static class UpdateManager
{
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

    public enum BotType
    {
        PokeBot,
        RaidBot,
        Unknown
    }

    public static async Task<UpdateAllResult> UpdateAllInstancesAsync(Main mainForm, int currentPort)
    {
        var result = new UpdateAllResult();

        var instances = GetAllInstances(currentPort);
        result.TotalInstances = instances.Count;

        var instancesNeedingUpdate = new List<(int ProcessId, int Port, string Version, BotType BotType)>();

        foreach (var instance in instances)
        {
            var latestVersion = await GetLatestVersionForBotType(instance.BotType);
            if (!string.IsNullOrEmpty(latestVersion) && instance.Version != latestVersion)
            {
                instancesNeedingUpdate.Add((instance.ProcessId, instance.Port, instance.Version, instance.BotType));
                result.UpdatesNeeded++;
            }
        }

        if (instancesNeedingUpdate.Count == 0)
        {
            return result;
        }

        LogUtil.LogInfo($"Idling all bots across {instancesNeedingUpdate.Count} instances before updates...", "UpdateManager");

        foreach (var (processId, port, version, botType) in instancesNeedingUpdate)
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

        var allInstances = instances.Select(i => (i.ProcessId, i.Port, i.Version, i.BotType)).ToList();
        var allIdle = await WaitForAllInstancesToBeIdle(mainForm, allInstances, 300);

        if (!allIdle)
        {
            result.UpdatesFailed = instancesNeedingUpdate.Count;
            foreach (var (processId, port, version, botType) in instancesNeedingUpdate)
            {
                var latestVersion = await GetLatestVersionForBotType(botType);
                result.InstanceResults.Add(new InstanceUpdateResult
                {
                    Port = port,
                    ProcessId = processId,
                    CurrentVersion = version,
                    LatestVersion = latestVersion,
                    NeedsUpdate = true,
                    Error = "Timeout waiting for all instances to idle - updates cancelled",
                    BotType = botType.ToString()
                });
            }
            return result;
        }

        var sortedInstances = instancesNeedingUpdate
            .Where(i => i.ProcessId != Environment.ProcessId)
            .Concat(instancesNeedingUpdate.Where(i => i.ProcessId == Environment.ProcessId))
            .ToList();

        foreach (var (processId, port, currentVersion, botType) in sortedInstances)
        {
            var latestVersion = await GetLatestVersionForBotType(botType);
            var instanceResult = new InstanceUpdateResult
            {
                Port = port,
                ProcessId = processId,
                CurrentVersion = currentVersion,
                LatestVersion = latestVersion,
                NeedsUpdate = true,
                BotType = botType.ToString()
            };

            try
            {
                if (processId == Environment.ProcessId)
                {
                    var updateForm = await CreateUpdateFormForBotType(botType, latestVersion);
                    if (updateForm != null)
                    {
                        mainForm.BeginInvoke((MethodInvoker)(() =>
                        {
                            updateForm.PerformUpdate();
                        }));

                        instanceResult.UpdateStarted = true;
                        result.UpdatesStarted++;
                        LogUtil.LogInfo($"Master instance ({botType}) update triggered", "UpdateManager");
                    }
                    else
                    {
                        instanceResult.Error = $"Could not create update form for {botType}";
                        result.UpdatesFailed++;
                    }
                }
                else
                {
                    LogUtil.LogInfo($"Triggering update for {botType} instance on port {port}...", "UpdateManager");
                    var updateResponse = BotServer.QueryRemote(port, "UPDATE");
                    if (!updateResponse.StartsWith("ERROR"))
                    {
                        instanceResult.UpdateStarted = true;
                        result.UpdatesStarted++;
                        LogUtil.LogInfo($"Update triggered for {botType} instance on port {port}", "UpdateManager");
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
                LogUtil.LogError($"Error updating {botType} instance on port {port}: {ex.Message}", "UpdateManager");
            }

            result.InstanceResults.Add(instanceResult);
        }

        return result;
    }

    public static async Task<UpdateAllResult> StartUpdateProcessAsync(Main mainForm, int currentPort)
    {
        var result = new UpdateAllResult();

        var instances = GetAllInstances(currentPort);
        result.TotalInstances = instances.Count;

        var instancesNeedingUpdate = new List<(int ProcessId, int Port, string Version, BotType BotType)>();

        foreach (var instance in instances)
        {
            var latestVersion = await GetLatestVersionForBotType(instance.BotType);
            if (!string.IsNullOrEmpty(latestVersion) && instance.Version != latestVersion)
            {
                instancesNeedingUpdate.Add((instance.ProcessId, instance.Port, instance.Version, instance.BotType));
                result.UpdatesNeeded++;
                result.InstanceResults.Add(new InstanceUpdateResult
                {
                    Port = instance.Port,
                    ProcessId = instance.ProcessId,
                    CurrentVersion = instance.Version,
                    LatestVersion = latestVersion,
                    NeedsUpdate = true,
                    BotType = instance.BotType.ToString()
                });
            }
        }

        if (instancesNeedingUpdate.Count == 0)
        {
            return result;
        }

        LogUtil.LogInfo($"Idling all bots across {instancesNeedingUpdate.Count} instances before updates...", "UpdateManager");

        foreach (var (processId, port, version, botType) in instancesNeedingUpdate)
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

        return result;
    }

    public static async Task<UpdateAllResult> ProceedWithUpdatesAsync(Main mainForm, int currentPort)
    {
        var result = new UpdateAllResult();

        var instances = GetAllInstances(currentPort);
        var instancesNeedingUpdate = new List<(int ProcessId, int Port, string Version, BotType BotType)>();

        foreach (var instance in instances)
        {
            var latestVersion = await GetLatestVersionForBotType(instance.BotType);
            if (!string.IsNullOrEmpty(latestVersion) && instance.Version != latestVersion)
            {
                instancesNeedingUpdate.Add((instance.ProcessId, instance.Port, instance.Version, instance.BotType));
            }
        }

        result.TotalInstances = instances.Count;
        result.UpdatesNeeded = instancesNeedingUpdate.Count;

        var sortedInstances = instancesNeedingUpdate
            .Where(i => i.ProcessId != Environment.ProcessId)
            .Concat(instancesNeedingUpdate.Where(i => i.ProcessId == Environment.ProcessId))
            .ToList();

        foreach (var (processId, port, currentVersion, botType) in sortedInstances)
        {
            var latestVersion = await GetLatestVersionForBotType(botType);
            var instanceResult = new InstanceUpdateResult
            {
                Port = port,
                ProcessId = processId,
                CurrentVersion = currentVersion,
                LatestVersion = latestVersion,
                NeedsUpdate = true,
                BotType = botType.ToString()
            };

            try
            {
                if (processId == Environment.ProcessId)
                {
                    var updateForm = await CreateUpdateFormForBotType(botType, latestVersion);
                    if (updateForm != null)
                    {
                        mainForm.BeginInvoke((MethodInvoker)(() =>
                        {
                            updateForm.PerformUpdate();
                        }));

                        instanceResult.UpdateStarted = true;
                        result.UpdatesStarted++;
                        LogUtil.LogInfo($"Master instance ({botType}) update triggered", "UpdateManager");
                    }
                    else
                    {
                        instanceResult.Error = $"Could not create update form for {botType}";
                        result.UpdatesFailed++;
                    }
                }
                else
                {
                    LogUtil.LogInfo($"Triggering update for {botType} instance on port {port}...", "UpdateManager");
                    var updateResponse = BotServer.QueryRemote(port, "UPDATE");
                    if (!updateResponse.StartsWith("ERROR"))
                    {
                        instanceResult.UpdateStarted = true;
                        result.UpdatesStarted++;
                        LogUtil.LogInfo($"Update triggered for {botType} instance on port {port}", "UpdateManager");
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
                LogUtil.LogError($"Error updating {botType} instance on port {port}: {ex.Message}", "UpdateManager");
            }

            result.InstanceResults.Add(instanceResult);
        }

        return result;
    }

    private static async Task<bool> WaitForAllInstancesToBeIdle(Main mainForm, List<(int ProcessId, int Port, string Version, BotType BotType)> instances, int timeoutSeconds)
    {
        var endTime = DateTime.Now.AddSeconds(timeoutSeconds);
        const int delayMs = 1000;

        while (DateTime.Now < endTime)
        {
            var allInstancesIdle = true;
            var statusReport = new List<string>();

            foreach (var (processId, port, version, botType) in instances)
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
                            statusReport.Add($"Master ({botType}): {string.Join(", ", states)}");
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
                                    statusReport.Add($"Port {port} ({botType}): {string.Join(", ", states)}");
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

            await Task.Delay(delayMs);
        }

        LogUtil.LogError($"Timeout after {timeoutSeconds} seconds waiting for all instances to idle", "UpdateManager");
        return false;
    }

    private static List<(int ProcessId, int Port, string Version, BotType BotType)> GetAllInstances(int currentPort)
    {
        var instances = new List<(int, int, string, BotType)>();

        // Add current instance
        var currentBotType = DetectCurrentBotType();
        var currentVersion = GetVersionForBotType(currentBotType);
        instances.Add((Environment.ProcessId, currentPort, currentVersion, currentBotType));

        try
        {
            // Scan for PokeBot processes
            var pokeBotProcesses = Process.GetProcessesByName("PokeBot")
                .Where(p => p.Id != Environment.ProcessId);

            foreach (var process in pokeBotProcesses)
            {
                try
                {
                    var instance = TryGetInstanceInfo(process, BotType.PokeBot);
                    if (instance.HasValue)
                        instances.Add(instance.Value);
                }
                catch { }
            }

            // Scan for RaidBot processes  
            var raidBotProcesses = Process.GetProcessesByName("SysBot")
                .Where(p => p.Id != Environment.ProcessId);

            foreach (var process in raidBotProcesses)
            {
                try
                {
                    var instance = TryGetInstanceInfo(process, BotType.RaidBot);
                    if (instance.HasValue)
                        instances.Add(instance.Value);
                }
                catch { }
            }
        }
        catch { }

        return instances;
    }

    private static (int, int, string, BotType)? TryGetInstanceInfo(Process process, BotType expectedBotType)
    {
        try
        {
            var exePath = process.MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath))
                return null;

            var portFileName = expectedBotType == BotType.PokeBot 
                ? $"PokeBot_{process.Id}.port" 
                : $"SVRaidBot_{process.Id}.port";

            var portFile = Path.Combine(Path.GetDirectoryName(exePath)!, portFileName);
            if (!File.Exists(portFile))
                return null;

            var portText = File.ReadAllText(portFile).Trim();
            if (!int.TryParse(portText, out var port))
                return null;

            if (!IsPortOpen(port))
                return null;

            var versionResponse = BotServer.QueryRemote(port, "VERSION");
            var version = versionResponse.StartsWith("ERROR") ? "Unknown" : versionResponse.Trim();

            return (process.Id, port, version, expectedBotType);
        }
        catch
        {
            return null;
        }
    }

    private static BotType DetectCurrentBotType()
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
        try
        {
            return botType switch
            {
                BotType.PokeBot => GetPokeBotVersion(),
                BotType.RaidBot => GetRaidBotVersion(),
                _ => "Unknown"
            };
        }
        catch
        {
            return "Unknown";
        }
    }

    private static string GetPokeBotVersion()
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

    private static string GetRaidBotVersion()
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

    private static async Task<string> GetLatestVersionForBotType(BotType botType)
    {
        try
        {
            return botType switch
            {
                BotType.PokeBot => await GetLatestPokeBotVersion(),
                BotType.RaidBot => await GetLatestRaidBotVersion(),
                _ => ""
            };
        }
        catch
        {
            return "";
        }
    }

    private static async Task<string> GetLatestPokeBotVersion()
    {
        try
        {
            var pokeBotUpdateCheckerType = Type.GetType("SysBot.Pokemon.Helpers.UpdateChecker, SysBot.Pokemon");
            if (pokeBotUpdateCheckerType != null)
            {
                var checkMethod = pokeBotUpdateCheckerType.GetMethod("CheckForUpdatesAsync");
                if (checkMethod != null)
                {
                    var task = (Task<(bool, string, string)>)checkMethod.Invoke(null, new object[] { false });
                    var (_, _, latestVersion) = await task;
                    return latestVersion;
                }
            }
            return "";
        }
        catch
        {
            return "";
        }
    }

    private static async Task<string> GetLatestRaidBotVersion()
    {
        try
        {
            var (_, _, latestVersion) = await UpdateChecker.CheckForUpdatesAsync(false);
            return latestVersion;
        }
        catch
        {
            return "";
        }
    }

    private static async Task<dynamic?> CreateUpdateFormForBotType(BotType botType, string latestVersion)
    {
        try
        {
            return botType switch
            {
                BotType.PokeBot => await CreatePokeBotUpdateForm(latestVersion),
                BotType.RaidBot => new UpdateForm(false, latestVersion, true),
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private static async Task<dynamic?> CreatePokeBotUpdateForm(string latestVersion)
    {
        try
        {
            var updateFormType = Type.GetType("SysBot.Pokemon.WinForms.UpdateForm, SysBot.Pokemon.WinForms");
            if (updateFormType != null)
            {
                return Activator.CreateInstance(updateFormType, false, latestVersion, true);
            }
            return null;
        }
        catch
        {
            return null;
        }
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