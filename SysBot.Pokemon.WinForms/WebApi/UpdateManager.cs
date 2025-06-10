using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using SysBot.Base;
using SysBot.Pokemon.Helpers;

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
        }

        if (instancesNeedingUpdate.Count == 0)
        {
            return result;
        }

        // Start idling all bots
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

            await Task.Delay(delayMs);
        }

        LogUtil.LogError($"Timeout after {timeoutSeconds} seconds waiting for all instances to idle", "UpdateManager");
        return false;
    }

    private static List<(int ProcessId, int Port, string Version)> GetAllInstances(int currentPort)
    {
        var version = "Unknown";
        try
        {
            // Try SVRaidBot version first
            var raidBotType = Type.GetType("SVRaidBot, SysBot.Pokemon.WinForms");
            if (raidBotType != null)
            {
                var versionField = raidBotType.GetField("Version",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (versionField != null)
                {
                    version = versionField.GetValue(null)?.ToString() ?? "Unknown";
                }
            }

            // Fallback to PokeBot version for compatibility
            if (version == "Unknown")
            {
                var tradeBotType = Type.GetType("SysBot.Pokemon.Helpers.PokeBot, SysBot.Pokemon");
                if (tradeBotType != null)
                {
                    var versionField = tradeBotType.GetField("Version",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (versionField != null)
                    {
                        version = versionField.GetValue(null)?.ToString() ?? "Unknown";
                    }
                }
            }

            if (version == "Unknown")
            {
                version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
            }
        }
        catch
        {
            version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
        }

        var instances = new List<(int, int, string)>
        {
            (Environment.ProcessId, currentPort, version)
        };

        try
        {
            // Search for both SVRaidBot and PokeBot processes for compatibility
            var raidBotProcesses = Process.GetProcessesByName("SVRaidBot")
                .Where(p => p.Id != Environment.ProcessId);
            
            var pokeBotProcesses = Process.GetProcessesByName("PokeBot")
                .Where(p => p.Id != Environment.ProcessId);

            var processes = raidBotProcesses.Concat(pokeBotProcesses);

            foreach (var process in processes)
            {
                try
                {
                    var exePath = process.MainModule?.FileName;
                    if (string.IsNullOrEmpty(exePath))
                        continue;

                    // Try both SVRaidBot and PokeBot port file formats
                    var raidBotPortFile = Path.Combine(Path.GetDirectoryName(exePath)!, $"SVRaidBot_{process.Id}.port");
                    var pokeBotPortFile = Path.Combine(Path.GetDirectoryName(exePath)!, $"PokeBot_{process.Id}.port");
                    
                    string? portFile = null;
                    if (File.Exists(raidBotPortFile))
                        portFile = raidBotPortFile;
                    else if (File.Exists(pokeBotPortFile))
                        portFile = pokeBotPortFile;
                    
                    if (portFile == null)
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
                catch { }
            }
        }
        catch { }

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
