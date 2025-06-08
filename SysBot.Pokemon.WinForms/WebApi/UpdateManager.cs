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
            }
        }

        if (instancesNeedingUpdate.Count == 0)
        {
            return result;
        }

        LogUtil.LogInfo($"Starting idle process for {instancesNeedingUpdate.Count} instances that need updates...", "UpdateManager");

        foreach (var (processId, port, version) in instancesNeedingUpdate)
        {
            var instanceResult = new InstanceUpdateResult
            {
                Port = port,
                ProcessId = processId,
                CurrentVersion = version,
                LatestVersion = latestVersion,
                NeedsUpdate = true
            };

            try
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
                        instanceResult.Error = "Failed to send idle command";
                        result.UpdatesFailed++;
                    }
                }
            }
            catch (Exception ex)
            {
                instanceResult.Error = ex.Message;
                result.UpdatesFailed++;
                LogUtil.LogError($"Error idling instance on port {port}: {ex.Message}", "UpdateManager");
            }

            result.InstanceResults.Add(instanceResult);
        }

        return result;
    }

    public static async Task<UpdateAllResult> ProceedWithUpdatesAsync(Main mainForm, int currentPort)
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

        var instancesNeedingUpdate = instances
            .Where(i => i.Version != latestVersion)
            .Select(i => (i.ProcessId, i.Port, i.Version))
            .ToList();

        result.UpdatesNeeded = instancesNeedingUpdate.Count;

        if (instancesNeedingUpdate.Count == 0)
        {
            return result;
        }

        LogUtil.LogInfo("Waiting for all instances to be idle before proceeding with updates...", "UpdateManager");

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

    private static async Task<bool> WaitForAllInstancesToBeIdle(Main mainForm, List<(int ProcessId, int Port, string Version)> instances, int timeoutSeconds)
    {
        var startTime = DateTime.Now;
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);

        while (DateTime.Now - startTime < timeout)
        {
            var allIdle = true;

            foreach (var (processId, port, _) in instances)
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
                                allIdle = false;
                                break;
                            }
                        }
                    }
                }
                else
                {
                    var statusResponse = BotServer.QueryRemote(port, "LISTBOTS");
                    if (statusResponse.StartsWith("{") && statusResponse.Contains("Bots"))
                    {
                        // Parse the bot status response to check if all are idle
                        if (!statusResponse.Contains("\"Status\":\"IDLE\"") && !statusResponse.Contains("\"Status\":\"STOPPED\""))
                        {
                            allIdle = false;
                        }
                    }
                    else
                    {
                        allIdle = false;
                    }
                }

                if (!allIdle)
                    break;
            }

            if (allIdle)
            {
                LogUtil.LogInfo("All instances are idle, ready for updates", "UpdateManager");
                return true;
            }

            await Task.Delay(5000);
        }

        LogUtil.LogError($"Timeout waiting for all instances to idle after {timeoutSeconds} seconds", "UpdateManager");
        return false;
    }

    private static List<(int ProcessId, int Port, string Version)> GetAllInstances(int currentPort)
    {
        var instances = new List<(int ProcessId, int Port, string Version)>();

        instances.Add((Environment.ProcessId, currentPort, GetCurrentVersion()));

        var processes = Process.GetProcessesByName("SysBot.Pokemon.WinForms");
        foreach (var process in processes)
        {
            if (process.Id == Environment.ProcessId)
                continue;

            try
            {
                for (int port = 8080; port <= 8090; port++)
                {
                    if (port == currentPort) continue;

                    if (IsPortOpen(port))
                    {
                        var versionResponse = BotServer.QueryRemote(port, "VERSION");
                        if (!versionResponse.StartsWith("ERROR"))
                        {
                            instances.Add((process.Id, port, versionResponse.Trim()));
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogUtil.LogError($"Error checking process {process.Id}: {ex.Message}", "UpdateManager");
            }
        }

        return instances;
    }

    private static string GetCurrentVersion()
    {
        return UpdateChecker.GetCurrentVersion() ?? "Unknown";
    }

    private static bool IsPortOpen(int port)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            var result = client.BeginConnect("127.0.0.1", port, null, null);
            var success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(100));
            client.Close();
            return success;
        }
        catch
        {
            return false;
        }
    }
}