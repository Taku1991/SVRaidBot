using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
// using SysBot.Base;
// using SysBot.Pokemon.Helpers;

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

        Console.WriteLine($"UpdateManager: Idling all bots across {instancesNeedingUpdate.Count} instances before updates...");

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
                    Console.WriteLine($"UpdateManager: Failed to send idle command to port {port}");
                }
            }
        }

        Console.WriteLine("UpdateManager: Waiting for all bots to finish current operations and go idle...");

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
                    Console.WriteLine("UpdateManager: Master instance update triggered");
                }
                else
                {
                    Console.WriteLine($"UpdateManager: Triggering update for instance on port {port}...");
                    var updateResponse = BotServer.QueryRemote(port, "UPDATE");
                    if (!updateResponse.StartsWith("ERROR"))
                    {
                        instanceResult.UpdateStarted = true;
                        result.UpdatesStarted++;
                        Console.WriteLine($"UpdateManager: Update triggered for instance on port {port}");
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
                Console.WriteLine($"UpdateManager: Error updating instance on port {port}: {ex.Message}");
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

        Console.WriteLine($"UpdateManager: Starting idle process for {instancesNeedingUpdate.Count} instances that need updates...");

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
                Console.WriteLine($"UpdateManager: Error idling instance on port {port}: {ex.Message}");
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

        Console.WriteLine("UpdateManager: Waiting for all instances to be idle before proceeding with updates...");

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
                    Console.WriteLine("UpdateManager: Master instance update triggered");
                }
                else
                {
                    Console.WriteLine($"UpdateManager: Triggering update for instance on port {port}...");
                    var updateResponse = BotServer.QueryRemote(port, "UPDATE");
                    if (!updateResponse.StartsWith("ERROR"))
                    {
                        instanceResult.UpdateStarted = true;
                        result.UpdatesStarted++;
                        Console.WriteLine($"UpdateManager: Update triggered for instance on port {port}");
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
                Console.WriteLine($"UpdateManager: Error updating instance on port {port}: {ex.Message}");
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
                Console.WriteLine("UpdateManager: All instances are idle, ready for updates");
                return true;
            }

            await Task.Delay(5000);
        }

        Console.WriteLine($"UpdateManager: Timeout waiting for all instances to idle after {timeoutSeconds} seconds");
        return false;
    }

    private static List<(int ProcessId, int Port, string Version)> GetAllInstances(int currentPort)
    {
        var instances = new List<(int ProcessId, int Port, string Version)>();

        instances.Add((Environment.ProcessId, currentPort, GetCurrentVersion()));

        var processNames = new[] { "PokeBot", "SVRaidBot", "SysBot.Pokemon.WinForms", "SysBot" };
        foreach (var processName in processNames)
        {
            try
            {
                var processes = Process.GetProcessesByName(processName)
                    .Where(p => p.Id != Environment.ProcessId);

                foreach (var process in processes)
                {
                    try
                    {
                        var exePath = process.MainModule?.FileName;
                        if (string.IsNullOrEmpty(exePath))
                            continue;

                        var exeDir = Path.GetDirectoryName(exePath)!;
                        
                        // Try both PokeBot and SVRaidBot port file formats
                        var pokeBotPortFile = Path.Combine(exeDir, $"PokeBot_{process.Id}.port");
                        var raidBotPortFile = Path.Combine(exeDir, $"SVRaidBot_{process.Id}.port");
                        
                        string? portFile = null;
                        if (File.Exists(pokeBotPortFile))
                            portFile = pokeBotPortFile;
                        else if (File.Exists(raidBotPortFile))
                            portFile = raidBotPortFile;
                        
                        if (portFile == null)
                            continue;

                        var portText = File.ReadAllText(portFile).Trim();
                        if (portText.StartsWith("ERROR:") || !int.TryParse(portText, out var port))
                            continue;

                        if (!IsPortOpen(port))
                            continue;

                        var versionResponse = BotServer.QueryRemote(port, "VERSION");
                        var version = versionResponse.StartsWith("ERROR") ? "Unknown" : versionResponse.Trim();

                        instances.Add((process.Id, port, version));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"UpdateManager: Error checking process {process.Id}: {ex.Message}");
                    }
                }
            }
            catch { }
        }

        return instances;
    }

    private static string GetCurrentVersion()
    {
        try
        {
            // Try to get SVRaidBot version first
            var svRaidBotType = Type.GetType("SysBot.Pokemon.SV.BotRaid.Helpers.SVRaidBot, SysBot.Pokemon");
            if (svRaidBotType != null)
            {
                var versionField = svRaidBotType.GetField("Version",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (versionField != null)
                {
                    return versionField.GetValue(null)?.ToString() ?? "Unknown";
                }
            }
        }
        catch { }
        
        // Fallback to assembly version
        try
        {
            return System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "Unknown";
        }
        catch
        {
            return "Unknown";
        }
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