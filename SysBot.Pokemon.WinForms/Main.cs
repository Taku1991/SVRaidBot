using PKHeX.Core;
using SysBot.Base;
using SysBot.Pokemon.SV.BotRaid.Helpers;
using SysBot.Web;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Net;
using System.Text;
using System.ComponentModel;

namespace SysBot.Pokemon.WinForms
{
    public sealed partial class Main : Form
    {
        private readonly List<PokeBotState> Bots = new();
        private ProgramConfig Config { get; set; }
        private IPokeBotRunner RunningEnvironment { get; set; }

        public readonly ISwitchConnectionAsync? SwitchConnection;
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public static bool IsUpdating { get; set; } = false;
        private System.Windows.Forms.Timer _autoSaveTimer;
        private TcpListener? _tcpListener;
        private CancellationTokenSource? _cts;

        public Main()
        {
            InitializeComponent();
            Load += async (sender, e) => await InitializeAsync();

            // Initialize dummy controls for compatibility
            TC_Main = new TabControl { Visible = false };
            Tab_Bots = new TabPage();
            Tab_Hub = new TabPage();
            Tab_Logs = new TabPage();
            TC_Main.TabPages.AddRange(new[] { Tab_Bots, Tab_Hub, Tab_Logs });
            comboBox1 = new ComboBox { Visible = false };
            TC_Main.SendToBack();
            comboBox1.SendToBack();
        }

        private async Task InitializeAsync()
        {
            if (IsUpdating)
                return;
            string discordName = string.Empty;

            // Update checker
            UpdateChecker updateChecker = new();
            await UpdateChecker.CheckForUpdatesAsync();

            if (File.Exists(Program.ConfigPath))
            {
                var lines = File.ReadAllText(Program.ConfigPath);
                Config = JsonSerializer.Deserialize(lines, ProgramConfigContext.Default.ProgramConfig) ?? new ProgramConfig();
                LogConfig.MaxArchiveFiles = Config.Hub.MaxArchiveFiles;
                LogConfig.LoggingEnabled = Config.Hub.LoggingEnabled;

                RunningEnvironment = GetRunner(Config);
                foreach (var bot in Config.Bots)
                {
                    bot.Initialize();
                    AddBot(bot);
                }
            }
            else
            {
                Config = new ProgramConfig();
                RunningEnvironment = GetRunner(Config);
            }

            LoadControls();
            Text = $"{(string.IsNullOrEmpty(Config.Hub.BotName) ? "S/V RaidBot" : Config.Hub.BotName)} {SVRaidBot.Version} ({Config.Mode})";
            trayIcon.Text = (Config?.Hub?.BotName != null && !string.IsNullOrEmpty(Config.Hub.BotName)) ? Config.Hub.BotName : "S/V RaidBot";
            Task.Run(BotMonitor);
            InitUtil.InitializeStubs(Config.Mode);
            StartTcpListener();
            
            // Starte die Web-API
            WebApiIntegration.StartWebApi(RunningEnvironment, 6500);

            LogUtil.LogInfo($"Bot initialization complete", "System");
        }

        private void RTB_Logs_TextChanged(object sender, EventArgs e)
        {
            RTB_Logs.Invalidate();
            RTB_Logs.Update();
        }

        private static IPokeBotRunner GetRunner(ProgramConfig cfg) => cfg.Mode switch
        {
            ProgramMode.SV => new PokeBotRunnerImpl<PK9>(cfg.Hub, new BotFactory9SV()),
            _ => throw new IndexOutOfRangeException("Unsupported mode."),
        };

        private async Task BotMonitor()
        {
            while (!Disposing)
            {
                try
                {
                    foreach (var c in FLP_Bots.Controls.OfType<BotController>())
                        c.ReadState();
                }
                catch
                {
                    // Updating the collection by adding/removing bots will change the iterator
                    // Can try a for-loop or ToArray, but those still don't prevent concurrent mutations of the array.
                    // Just try, and if failed, ignore. Next loop will be fine. Locks on the collection are kinda overkill, since this task is not critical.
                }
                await Task.Delay(2_000).ConfigureAwait(false);
            }
        }

        private void StartTcpListener(int preferredPort = 0)
        {
            if (_tcpListener != null) return;
            int basePort = 55000;
            int portRange = 5000;

            int initialPort = preferredPort > 0
                ? preferredPort
                : basePort + (Environment.ProcessId % portRange);

            const int maxAttempts = 10;
            int currentAttempt = 0;
            int portToUse = initialPort;
            bool success = false;

            while (!success && currentAttempt < maxAttempts)
            {
                try
                {
                    currentAttempt++;
                    if (currentAttempt > 1)
                    {
                        int randomOffset = (Environment.ProcessId + Environment.TickCount) % portRange;
                        portToUse = basePort + ((initialPort + randomOffset * currentAttempt) % portRange);
                        LogUtil.LogInfo($"TCP port conflict detected. Trying alternate port {portToUse} (attempt {currentAttempt}/{maxAttempts})", "TCP");
                    }
                    _cts = new CancellationTokenSource();
                    _tcpListener = new TcpListener(IPAddress.Loopback, portToUse);
                    _tcpListener.Start();
                    success = true;
                    LogUtil.LogInfo($"TCP Listener started on port {portToUse}", "TCP");
                    string portInfoPath = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), $"SVRaidBot_{Environment.ProcessId}.port");
                    File.WriteAllText(portInfoPath, portToUse.ToString());
                    Task.Run(() => AcceptLoop(_cts.Token));
                }
                catch (Exception ex)
                {
                    if (currentAttempt >= maxAttempts)
                    {
                        LogUtil.LogError($"All attempts to start TCP listener failed. Last attempt on port {portToUse}: {ex.Message}", "TCP");

                        try
                        {
                            string portInfoPath = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), $"SVRaidBot_{Environment.ProcessId}.port");
                            File.WriteAllText(portInfoPath, "ERROR:" + ex.Message);
                        }
                        catch { }
                    }
                    else
                    {
                        LogUtil.LogError($"Failed to start TCP listener on port {portToUse}: {ex.Message}. Will try another port.", "TCP");

                        // Clean up before retry
                        _tcpListener = null;
                        _cts?.Dispose();
                        _cts = null;
                    }
                }
            }
        }

        private async Task AcceptLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _tcpListener != null)
            {
                try
                {
                    var client = await _tcpListener.AcceptTcpClientAsync();
                    if (client != null)
                        _ = Task.Run(() => HandleClient(client, ct));
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogUtil.LogError($"AcceptLoop error: {ex.Message}", "TCP");
                    await Task.Delay(500);
                }
            }
        }

        private async Task HandleClient(TcpClient client, CancellationToken ct)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

                    var commandLine = await reader.ReadLineAsync();
                    if (string.IsNullOrEmpty(commandLine)) return;

                    LogUtil.LogInfo($"Received command: {commandLine}", "TCP");
                    string response = ExecuteBotCommand(commandLine.Trim());
                    await writer.WriteLineAsync(response);
                }
                catch (Exception ex)
                {
                    LogUtil.LogError($"HandleClient error: {ex.Message}", "TCP");
                }
            }
        }

        private string ExecuteBotCommand(string cmd)
        {
            switch (cmd)
            {
                case "StopAll":
                    RunningEnvironment.StopAll();
                    return "STOPPED";

                case "StartAll":
                    // Reset error state when starting
                    SysBot.Pokemon.SV.BotRaid.RotatingRaidBotSV.HasErrored = false;
                    RunningEnvironment.InitializeStart();
                    foreach (var c in FLP_Bots.Controls.OfType<BotController>())
                        c.SendCommand(BotControlCommand.Start);
                    return "STARTED";

                case "IdleAll":
                    foreach (var c in FLP_Bots.Controls.OfType<BotController>())
                        c.SendCommand(BotControlCommand.Idle);
                    return "IDLING";

                case "ResumeAll":
                    // Reset error state when resuming
                    SysBot.Pokemon.SV.BotRaid.RotatingRaidBotSV.HasErrored = false;
                    foreach (var c in FLP_Bots.Controls.OfType<BotController>())
                        c.SendCommand(BotControlCommand.Resume);
                    return "RESUMED";

                case "RestartAll":
                    // Reset error state when restarting
                    SysBot.Pokemon.SV.BotRaid.RotatingRaidBotSV.HasErrored = false;
                    foreach (var c in FLP_Bots.Controls.OfType<BotController>())
                    {
                        RunningEnvironment.InitializeStart();
                        c.SendCommand(BotControlCommand.Restart);
                    }
                    return "RESTARTING";

                case "RebootAll":
                    // Reset error state when rebooting
                    SysBot.Pokemon.SV.BotRaid.RotatingRaidBotSV.HasErrored = false;
                    foreach (var c in FLP_Bots.Controls.OfType<BotController>())
                        c.SendCommand(BotControlCommand.RebootAndStop);
                    return "REBOOTING";

                case "Status":
                    // Check actual status of each bot's routine
                    foreach (var c in FLP_Bots.Controls.OfType<BotController>())
                    {
                        try
                        {
                            var botState = c.ReadBotState();
                            // If any bot is not idle, return its status
                            if (botState != "IDLE")
                            {
                                return botState;
                            }
                        }
                        catch
                        {
                            return "ERROR"; // Error reading state
                        }
                    }
                    return "IDLE"; // All bots are idle

                case "IsReady":
                    // First check - are there any configured bots?
                    if (FLP_Bots.Controls.OfType<BotController>().Count() == 0)
                    {
                        LogUtil.LogInfo("No bots are configured", "TCP");
                        return "NOT_READY";
                    }

                    // Second check - static error flag
                    try
                    {
                        if (SysBot.Pokemon.SV.BotRaid.RotatingRaidBotSV.HasErrored)
                        {
                            LogUtil.LogInfo("Static HasErrored flag is true", "TCP");
                            return "NOT_READY";
                        }
                    }
                    catch { /* Ignore if not available */ }

                    // Third check - scan log records for recent errors
                    // If any error message was logged in the last 10 minutes, consider the bot not ready
                    // This ensures we detect post-crash scenarios even when IsRunning is false
                    try
                    {
                        // Check the RTB_Logs text for recent error messages
                        string logText = RTB_Logs.Text;
                        if (!string.IsNullOrEmpty(logText))
                        {
                            // First check for the most definitive crash message
                            if (logText.Contains("Bot has crashed"))
                            {
                                LogUtil.LogInfo("Found 'Bot has crashed' in logs", "TCP");
                                return "NOT_READY";
                            }

                            // Then check for ending message
                            if (logText.Contains("Ending RotatingRaidBotSV loop"))
                            {
                                LogUtil.LogInfo("Found 'Ending RotatingRaidBotSV loop' in logs", "TCP");
                                return "NOT_READY";
                            }

                            // Check if there are connection errors in recent logs
                            if (logText.Contains("Connection error"))
                            {
                                LogUtil.LogInfo("Found 'Connection error' in logs", "TCP");
                                return "NOT_READY";
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogUtil.LogError($"Error checking logs: {ex.Message}", "TCP");
                    }

                    // Fourth check - inspect each bot's state
                    foreach (var c in FLP_Bots.Controls.OfType<BotController>())
                    {
                        try
                        {
                            var botSource = c.GetBot();
                            if (botSource == null)
                            {
                                LogUtil.LogInfo("Bot source is null", "TCP");
                                return "NOT_READY";
                            }

                            // Check if the bot is in a known error state
                            string state = c.ReadBotState();
                            if (state == "ERROR" || state == "STOPPING")
                            {
                                LogUtil.LogInfo($"Bot state is {state}", "TCP");
                                return "NOT_READY";
                            }

                            // Check for any connection issues
                            try
                            {
                                if (botSource.Bot == null || botSource.Bot.Connection == null)
                                {
                                    LogUtil.LogInfo("Bot or Bot.Connection is null", "TCP");
                                    return "NOT_READY";
                                }

                                if (!botSource.Bot.Connection.Connected)
                                {
                                    LogUtil.LogInfo("Bot is not connected", "TCP");
                                    return "NOT_READY";
                                }
                            }
                            catch
                            {
                                LogUtil.LogInfo("Exception checking connection status", "TCP");
                                return "NOT_READY";
                            }
                        }
                        catch (Exception ex)
                        {
                            LogUtil.LogInfo($"Exception while checking bot: {ex.Message}", "TCP");
                            return "NOT_READY";
                        }
                    }

                    return "READY"; // All checks passed, bot is ready

                case "RefreshMap":
                    foreach (var c in FLP_Bots.Controls.OfType<BotController>())
                        c.SendCommand(BotControlCommand.RefreshMap, false);
                    return "REFRESHED";

                default:
                    return $"Unknown command: {cmd}";
            }
        }

        private void StopTcpListener()
        {
            try
            {
                // Delete port info file as an extra precaution
                string portInfoPath = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), $"SVRaidBot_{Environment.ProcessId}.port");
                if (File.Exists(portInfoPath))
                    File.Delete(portInfoPath);

                _cts?.Cancel();
                _tcpListener?.Stop();
                _cts = null;
                _tcpListener = null;
            }
            catch { }
        }

        private void LoadControls()
        {
            MinimumSize = Size;
            PG_Hub.SelectedObject = RunningEnvironment.Config;
            _autoSaveTimer = new System.Windows.Forms.Timer
            {
                Interval = 10_000,
                Enabled = true
            };
            _autoSaveTimer.Tick += (s, e) => SaveCurrentConfig();
            var routines = ((PokeRoutineType[])Enum.GetValues(typeof(PokeRoutineType))).Where(z => RunningEnvironment.SupportsRoutine(z));
            var list = routines.Select(z => new ComboItem(z.ToString(), (int)z)).ToArray();
            CB_Routine.DisplayMember = nameof(ComboItem.Text);
            CB_Routine.ValueMember = nameof(ComboItem.Value);
            CB_Routine.DataSource = list;
            CB_Routine.SelectedValue = (int)PokeRoutineType.RotatingRaidBot; // default option

            var protocols = (SwitchProtocol[])Enum.GetValues(typeof(SwitchProtocol));
            var listP = protocols.Select(z => new ComboItem(z.ToString(), (int)z)).ToArray();
            CB_Protocol.DisplayMember = nameof(ComboItem.Text);
            CB_Protocol.ValueMember = nameof(ComboItem.Value);
            CB_Protocol.DataSource = listP;
            CB_Protocol.SelectedIndex = (int)SwitchProtocol.WiFi; // default option

            LogUtil.Forwarders.Add(AppendLog);
        }

        private void AppendLog(string message, string identity)
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] - {identity}: {message}{Environment.NewLine}";
            if (InvokeRequired)
                Invoke((MethodInvoker)(() => UpdateLog(line)));
            else
                UpdateLog(line);
        }

        private void UpdateLog(string line)
        {
            // ghetto truncate
            if (RTB_Logs.Lines.Length > 99_999)
                RTB_Logs.Lines = RTB_Logs.Lines.Skip(25_0000).ToArray();

            RTB_Logs.AppendText(line);
            RTB_Logs.ScrollToCaret();
        }

        private ProgramConfig GetCurrentConfiguration()
        {
            if (Config == null)
            {
                throw new InvalidOperationException("Config has not been initialized because a valid license was not entered.");
            }
            Config.Bots = Bots.ToArray();
            return Config;
        }

        private void Main_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (IsUpdating)
            {
                return;
            }

            // If not exiting, minimize to tray instead
            if (!isExiting)
            {
                e.Cancel = true;
                WindowState = FormWindowState.Minimized;
                ShowInTaskbar = false;
                trayIcon.Visible = true;
                trayIcon.ShowBalloonTip(2000, "S/V RaidBot", "Application minimized to system tray", ToolTipIcon.Info);
                return;
            }

            StopTcpListener();

            // Delete the port info file
            try
            {
                string portInfoPath = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), $"SVRaidBot_{Environment.ProcessId}.port");
                if (File.Exists(portInfoPath))
                    File.Delete(portInfoPath);
            }
            catch { /* Ignore cleanup errors */ }

            if (_autoSaveTimer != null)
            {
                _autoSaveTimer.Stop();
                _autoSaveTimer.Dispose();
            }

            if (animationTimer != null)
            {
                animationTimer.Stop();
                animationTimer.Dispose();
            }

            SaveCurrentConfig();
            var bots = RunningEnvironment;
            if (!bots.IsRunning)
                return;

            async Task WaitUntilNotRunning()
            {
                while (bots.IsRunning)
                    await Task.Delay(10).ConfigureAwait(false);
            }

            // Try to let all bots hard-stop before ending execution of the entire program.
            WindowState = FormWindowState.Minimized;
            ShowInTaskbar = false;
            bots.StopAll();
            Task.WhenAny(WaitUntilNotRunning(), Task.Delay(5_000)).ConfigureAwait(true).GetAwaiter().GetResult();
        }

        private void SaveCurrentConfig()
        {
            var cfg = GetCurrentConfiguration();
            var lines = JsonSerializer.Serialize(cfg, ProgramConfigContext.Default.ProgramConfig);
            File.WriteAllText(Program.ConfigPath, lines);
        }

        [JsonSerializable(typeof(ProgramConfig))]
        [JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
        public sealed partial class ProgramConfigContext : JsonSerializerContext
        { }

        private void B_Start_Click(object sender, EventArgs e)
        {
            // Reset error state when starting
            SV.BotRaid.RotatingRaidBotSV.HasErrored = false;

            SaveCurrentConfig();

            LogUtil.LogInfo("Starting all bots...", "Form");
            RunningEnvironment.InitializeStart();
            SendAll(BotControlCommand.Start);
            btnNavLogs.PerformClick(); // Switch to logs view

            if (Bots.Count == 0)
                WinFormsUtil.Alert("No bots configured, but all supporting services have been started.");
        }

        private void B_RebootAndStop_Click(object sender, EventArgs e)
        {
            B_Stop_Click(sender, e);
            Task.Run(async () =>
            {
                await Task.Delay(3_500).ConfigureAwait(false);
                SaveCurrentConfig();
                LogUtil.LogInfo("Restarting all the consoles...", "Form");
                RunningEnvironment.InitializeStart();
                SendAll(BotControlCommand.RebootAndStop);
                Invoke((MethodInvoker)(() => btnNavLogs.PerformClick())); // Switch to logs view
                if (Bots.Count == 0)
                    WinFormsUtil.Alert("No bots configured, but all supporting services have been issued the reboot command.");
            });
        }

        private async void Updater_Click(object sender, EventArgs e)
        {
            var (updateAvailable, updateRequired, newVersion) = await UpdateChecker.CheckForUpdatesAsync();
            if (!updateAvailable)
            {
                var result = MessageBox.Show(
                    "You are on the latest version. Would you like to re-download the current version?",
                    "Update Check",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (result == DialogResult.Yes)
                {
                    UpdateForm updateForm = new(updateRequired, newVersion, updateAvailable: false);
                    updateForm.ShowDialog();
                }
            }
            else
            {
                UpdateForm updateForm = new(updateRequired, newVersion, updateAvailable: true);
                updateForm.ShowDialog();
            }
        }

        private void RefreshMap_Click(object sender, EventArgs e)
        {
            SaveCurrentConfig();
            LogUtil.LogInfo("Sending RefreshMap command to all bots.", "Refresh Map");
            SendAll(BotControlCommand.RefreshMap);
            btnNavLogs.PerformClick(); // Switch to logs view
            if (Bots.Count == 0)
                WinFormsUtil.Alert("No bots configured, but all supporting services have been issued the refresh map command.");
        }

        private void SendAll(BotControlCommand cmd)
        {
            foreach (var c in FLP_Bots.Controls.OfType<BotController>())
                c.SendCommand(cmd, false);

            LogUtil.LogText($"All bots have been issued a command to {cmd}.");
        }

        private void B_Stop_Click(object sender, EventArgs e)
        {
            var env = RunningEnvironment;
            if (!env.IsRunning && (ModifierKeys & Keys.Alt) == 0)
            {
                WinFormsUtil.Alert("Nothing is currently running.");
                return;
            }

            var cmd = BotControlCommand.Stop;

            if ((ModifierKeys & Keys.Control) != 0 || (ModifierKeys & Keys.Shift) != 0) // either, because remembering which can be hard
            {
                if (env.IsRunning)
                {
                    WinFormsUtil.Alert("Commanding all bots to Idle.", "Press Stop (without a modifier key) to hard-stop and unlock control, or press Stop with the modifier key again to resume.");
                    cmd = BotControlCommand.Idle;
                }
                else
                {
                    WinFormsUtil.Alert("Commanding all bots to resume their original task.", "Press Stop (without a modifier key) to hard-stop and unlock control.");
                    cmd = BotControlCommand.Resume;
                }
            }
            SendAll(cmd);
        }

        private void B_New_Click(object sender, EventArgs e)
        {
            var cfg = CreateNewBotConfig();
            if (!AddBot(cfg))
            {
                WinFormsUtil.Alert("Unable to add bot; ensure details are valid and not duplicate with an already existing bot.");
                return;
            }
            System.Media.SystemSounds.Asterisk.Play();
        }

        private bool AddBot(PokeBotState cfg)
        {
            if (!cfg.IsValid())
                return false;

            // Disallow duplicate routines.
            if (Bots.Any(z => z.Connection.Equals(cfg.Connection) && cfg.NextRoutineType == z.NextRoutineType))
                return false;

            PokeRoutineExecutorBase newBot;
            try
            {
                Console.WriteLine($"Current Mode ({Config.Mode}) does not support this type of bot ({cfg.CurrentRoutineType}).");
                newBot = RunningEnvironment.CreateBotFromConfig(cfg);
            }
            catch
            {
                return false;
            }

            try
            {
                RunningEnvironment.Add(newBot);
            }
            catch (ArgumentException ex)
            {
                WinFormsUtil.Error(ex.Message);
                return false;
            }

            AddBotControl(cfg);
            Bots.Add(cfg);
            return true;
        }

        private void AddBotControl(PokeBotState cfg)
        {
            var row = new BotController { Width = FLP_Bots.Width - 60 };
            row.Initialize(RunningEnvironment, cfg);
            FLP_Bots.Controls.Add(row);
            FLP_Bots.SetFlowBreak(row, true);

            // Modern dark theme styling is now handled in BotController itself

            row.Click += (s, e) =>
            {
                var details = cfg.Connection;
                TB_IP.Text = details.IP;
                NUD_Port.Value = details.Port;
                CB_Protocol.SelectedIndex = (int)details.Protocol;
                CB_Routine.SelectedValue = (int)cfg.InitialRoutine;
            };

            row.Remove += (s, e) =>
            {
                Bots.Remove(row.State);
                RunningEnvironment.Remove(row.State, !RunningEnvironment.Config.SkipConsoleBotCreation);
                FLP_Bots.Controls.Remove(row);
            };
        }

        private PokeBotState CreateNewBotConfig()
        {
            var ip = TB_IP.Text;
            var port = (int)NUD_Port.Value;
            var cfg = BotConfigUtil.GetConfig<SwitchConnectionConfig>(ip, port);
            cfg.Protocol = (SwitchProtocol)WinFormsUtil.GetIndex(CB_Protocol);

            var pk = new PokeBotState { Connection = cfg };
            var type = (PokeRoutineType)WinFormsUtil.GetIndex(CB_Routine);
            pk.Initialize(type);
            return pk;
        }

        private void FLP_Bots_Resize(object sender, EventArgs e)
        {
            foreach (var c in FLP_Bots.Controls.OfType<BotController>())
                c.Width = FLP_Bots.Width - 60; // Account for scrollbar and padding
        }

        private void CB_Protocol_SelectedIndexChanged(object sender, EventArgs e)
        {
            TB_IP.Visible = CB_Protocol.SelectedIndex == 0;
        }

        private void ShowFromTray()
        {
            Show();
            WindowState = FormWindowState.Normal;
            ShowInTaskbar = true;
            trayIcon.Visible = false;
            BringToFront();
            Activate();

            int headerHeight = headerPanel.Height + 10;

            // Adjust padding only if panels haven't been initialized yet (checking Padding.Top as an indicator)
            if (hubPanel.Padding.Top <= 40)
                hubPanel.Padding = new Padding(40, headerHeight, 40, 40);

            if (logsPanel.Padding.Top <= 40)
                logsPanel.Padding = new Padding(40, headerHeight, 40, 40);

            // Force layout recalculation explicitly
            hubPanel.PerformLayout();
            PG_Hub.Refresh();

            logsPanel.PerformLayout();
            RTB_Logs.Refresh();
        }

        private void ExitApplication()
        {
            var bots = RunningEnvironment;
            if (bots != null && bots.IsRunning)
            {
                var result = MessageBox.Show(
                    "Bots are currently running. Do you want to stop all bots and exit?",
                    "Exit Confirmation",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);

                if (result != DialogResult.Yes)
                    return;

                bots.StopAll();
            }

            isExiting = true;
            trayIcon.Dispose();
            Application.Exit();
        }
    }

    // Extension methods for graphics
    public static class GraphicsExtensions
    {
        public static void AddRoundedRectangle(this GraphicsPath path, Rectangle rect, int radius)
        {
            path.AddArc(rect.X, rect.Y, radius * 2, radius * 2, 180, 90);
            path.AddArc(rect.Right - radius * 2, rect.Y, radius * 2, radius * 2, 270, 90);
            path.AddArc(rect.Right - radius * 2, rect.Bottom - radius * 2, radius * 2, radius * 2, 0, 90);
            path.AddArc(rect.X, rect.Bottom - radius * 2, radius * 2, radius * 2, 90, 90);
            path.CloseFigure();
        }
    }
}