using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using SysBot.Pokemon.WinForms;

namespace SysBot.Pokemon.WinForms.WebApi;

public static class UpdateManager
{
    public static async Task PerformAutomaticUpdate(string botType, string newVersion)
    {
        // Fire-and-forget updater UI; prefer running on UI thread if available
        await Task.Yield();
        try
        {
            var form = new UpdateForm(updateRequired: false, newVersion: newVersion, updateAvailable: true);
            // Try to marshal to any open form (likely Main)
            var mainForm = Application.OpenForms.Count > 0 ? Application.OpenForms[0] : null;
            if (mainForm != null)
                mainForm.BeginInvoke((MethodInvoker)(() => form.PerformUpdate()));
            else
                form.PerformUpdate();
        }
        catch
        {
            // Ignore; UI-less environment
        }
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

    public static async Task<UpdateAllResult> StartUpdateProcessAsync(Main mainForm, int currentPort)
    {
        var result = new UpdateAllResult();

        var (_, _, latestVersion) = await UpdateChecker.CheckForUpdatesAsync(false);
        if (string.IsNullOrEmpty(latestVersion))
        {
            result.UpdatesFailed = 1;
            result.InstanceResults.Add(new InstanceUpdateResult { Error = "Failed to fetch latest version" });
            return result;
        }

        var instances = GetAllInstances(currentPort);
        result.TotalInstances = instances.Count;

        var needingUpdate = instances.Where(i => i.Version != latestVersion).ToList();
        result.UpdatesNeeded = needingUpdate.Count;

        foreach (var (pid, port, version) in needingUpdate)
        {
            result.InstanceResults.Add(new InstanceUpdateResult
            {
                Port = port,
                ProcessId = pid,
                CurrentVersion = version,
                LatestVersion = latestVersion,
                NeedsUpdate = true
            });
        }

        if (needingUpdate.Count == 0)
            return result;

        // Idle bots on all instances
        foreach (var (pid, port, _) in needingUpdate)
        {
            if (pid == Environment.ProcessId)
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
                            controller.SendCommand(BotControlCommand.Idle, false);
                    }
                }
            }
            else
            {
                _ = QueryRemote(port, "IDLEALL");
            }
        }

        return result;
    }

    public static async Task<UpdateAllResult> ProceedWithUpdatesAsync(Main mainForm, int currentPort)
    {
        var result = new UpdateAllResult();

        var (_, _, latestVersion) = await UpdateChecker.CheckForUpdatesAsync(false);
        var instances = GetAllInstances(currentPort);
        var needingUpdate = instances.Where(i => i.Version != latestVersion).ToList();

        result.TotalInstances = instances.Count;
        result.UpdatesNeeded = needingUpdate.Count;

        // Update remote instances first, then local last
        var ordered = needingUpdate.Where(i => i.ProcessId != Environment.ProcessId)
                                   .Concat(needingUpdate.Where(i => i.ProcessId == Environment.ProcessId));

        foreach (var (pid, port, currentVersion) in ordered)
        {
            var item = new InstanceUpdateResult
            {
                Port = port,
                ProcessId = pid,
                CurrentVersion = currentVersion,
                LatestVersion = latestVersion,
                NeedsUpdate = true
            };

            try
            {
                if (pid == Environment.ProcessId)
                {
                    var updateForm = new UpdateForm(false, latestVersion, true);
                    mainForm.BeginInvoke((MethodInvoker)(() => updateForm.PerformUpdate()));
                    item.UpdateStarted = true;
                    result.UpdatesStarted++;
                }
                else
                {
                    var resp = QueryRemote(port, "UPDATE");
                    if (!resp.StartsWith("ERROR"))
                    {
                        item.UpdateStarted = true;
                        result.UpdatesStarted++;
                    }
                    else
                    {
                        item.Error = "Failed to start update";
                        result.UpdatesFailed++;
                    }
                }
            }
            catch (Exception ex)
            {
                item.Error = ex.Message;
                result.UpdatesFailed++;
            }

            result.InstanceResults.Add(item);
        }

        return result;
    }

    private static List<(int ProcessId, int Port, string Version)> GetAllInstances(int currentPort)
    {
        var list = new List<(int, int, string)>
        {
            (Environment.ProcessId, currentPort, "Unknown")
        };

        try
        {
            var processes = Process.GetProcessesByName("SVRaidBot").Where(p => p.Id != Environment.ProcessId);
            foreach (var p in processes)
            {
                try
                {
                    var exe = p.MainModule?.FileName;
                    if (string.IsNullOrEmpty(exe)) continue;

                    var portFile = Path.Combine(Path.GetDirectoryName(exe)!, $"SVRaidBot_{p.Id}.port");
                    if (!File.Exists(portFile)) continue;

                    var text = File.ReadAllText(portFile).Trim();
                    if (!int.TryParse(text, out var port)) continue;

                    var versionResponse = QueryRemote(port, "VERSION");
                    var version = versionResponse.StartsWith("ERROR") ? "Unknown" : versionResponse.Trim();
                    list.Add((p.Id, port, version));
                }
                catch { }
            }
        }
        catch { }

        return list;
    }

    private static string QueryRemote(int port, string command)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            client.Connect("127.0.0.1", port);

            using var stream = client.GetStream();
            using var writer = new StreamWriter(stream, System.Text.Encoding.UTF8) { AutoFlush = true };
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);

            writer.WriteLine(command);
            return reader.ReadLine() ?? "No response";
        }
        catch
        {
            return "ERROR: Failed to connect";
        }
    }
}