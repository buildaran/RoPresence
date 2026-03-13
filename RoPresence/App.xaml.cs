using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Drawing;
using System.Windows.Threading;
using System.Collections.Generic;
using DiscordRPC;
using DiscordRPC.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Windows.Forms;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using System.Windows.Controls;
using System.Windows.Media;
using Color = System.Drawing.Color;
using Brush = System.Drawing.Brush;
using SolidBrush = System.Drawing.SolidBrush;
using Pen = System.Drawing.Pen;
using Rectangle = System.Drawing.Rectangle;
using Font = System.Drawing.Font;

namespace RoPresence
{
    public class AppSettings
    {
        public bool DiscordRpcEnabled { get; set; } = true;
        public bool ShowTimestamp { get; set; } = true;
        public bool ShowCreator { get; set; } = true;
    }

    public partial class App : Application
    {
        private NotifyIcon _trayIcon;
        private ContextMenuStrip _trayMenu;
        private DiscordRpcClient _rpcClient;
        private FileSystemWatcher _logWatcher;

        private AppSettings _settings;
        private string _currentLogFile;
        private long _lastReadOffset = 0;
        private string _currentPlaceId = "";
        private string _currentJobId = "";
        private string _currentUniverseId = "";
        private string _currentGameName = "";

        private DispatcherTimer _processCheckTimer;
        private DispatcherTimer _logPollingTimer;
        private static readonly HttpClient _httpClient = new HttpClient();

        private const string DiscordAppId = "1482054506845044849";
        private const string VerifiedEmoji = "☑️";
        private const string AppTitle = "RoPresence";
        private const string AppVersion = "v1.1.0";

        private const string GitHubUser = "buildaran";
        private const string GitHubRepo = "RoPresence";
        private string UpdateUrl => $"https://api.github.com/repos/{GitHubUser}/{GitHubRepo}/releases/latest";

        private readonly string AppDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RoPresence");
        private string SettingsPath => Path.Combine(AppDataFolder, "settings.json");
        private string HistoryPath => Path.Combine(AppDataFolder, "history.txt");

        private ToolStripMenuItem _rpcToggleItem;
        private Icon _appIcon;

        private void OnStartup(object sender, StartupEventArgs e)
        {
            this.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            Directory.CreateDirectory(AppDataFolder);
            LoadSettings();

            try
            {
                string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Icon.png");
                if (File.Exists(iconPath))
                {
                    using (Bitmap bitmap = new Bitmap(iconPath))
                    {
                        _appIcon = Icon.FromHandle(bitmap.GetHicon());
                    }
                }
                else _appIcon = SystemIcons.Application;
            }
            catch { _appIcon = SystemIcons.Application; }

            SetupTray();
            InitializeDiscord();
            StartWatcher();
            StartProcessMonitor();

            _ = CheckForUpdates();

            Application.Current.Exit += OnApplicationExit;
        }

        #region Initialization & UI

        private void SetupTray()
        {
            _trayIcon = new NotifyIcon { Visible = true, Text = $"{AppTitle} - Idle", Icon = _appIcon };

            _trayMenu = new ContextMenuStrip();
            _trayMenu.Renderer = new DarkMenuRenderer();
            _trayMenu.ShowImageMargin = true; // Enabled to allow icons on the left

            // Header with Icon on the left
            var header = new ToolStripMenuItem($"{AppTitle} {AppVersion}");
            header.Enabled = false;
            header.Image = _appIcon.ToBitmap();
            header.Font = new Font(_trayMenu.Font.FontFamily, 9, System.Drawing.FontStyle.Bold);
            _trayMenu.Items.Add(header);

            _trayMenu.Items.Add(new ToolStripSeparator());

            _rpcToggleItem = new ToolStripMenuItem("Discord Rich Presence");
            _rpcToggleItem.CheckOnClick = true;
            _rpcToggleItem.Checked = _settings.DiscordRpcEnabled;
            _rpcToggleItem.CheckedChanged += (s, ev) => {
                _settings.DiscordRpcEnabled = _rpcToggleItem.Checked;
                SaveSettings();
                if (_settings.DiscordRpcEnabled && IsRobloxRunning() && !string.IsNullOrEmpty(_currentPlaceId))
                    Task.Run(() => UpdatePresence(_currentPlaceId));
                else
                    ClearPresence(false);
            };
            _trayMenu.Items.Add(_rpcToggleItem);

            _trayMenu.Items.Add("Game history", null, (s, e) => OpenFileSafe(HistoryPath, "Game History"));

            _trayMenu.Items.Add(new ToolStripSeparator());

            _trayMenu.Items.Add("Close Roblox", null, (s, e) => KillRoblox());
            _trayMenu.Items.Add("Open log file", null, (s, e) => OpenFileSafe(_currentLogFile, "Log File"));

            _trayMenu.Items.Add(new ToolStripSeparator());
            _trayMenu.Items.Add("Exit", null, (s, e) => Application.Current.Shutdown());

            _trayIcon.ContextMenuStrip = _trayMenu;
        }

        private void ShowUpdateUI(string version, string url)
        {
            this.Dispatcher.Invoke(() => {
                Window updateWin = new Window
                {
                    Title = "Update Available",
                    Width = 350,
                    Height = 180,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(43, 45, 49)),
                    Foreground = System.Windows.Media.Brushes.White,
                    ResizeMode = ResizeMode.NoResize,
                    Topmost = true
                };

                StackPanel stack = new StackPanel { Margin = new Thickness(20) };
                stack.Children.Add(new TextBlock { Text = "A new update is available!", FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 10) });
                stack.Children.Add(new TextBlock { Text = $"Version {version} is now ready to download.", Margin = new Thickness(0, 0, 0, 20) });

                System.Windows.Controls.Button btn = new System.Windows.Controls.Button
                {
                    Content = "Download on GitHub",
                    Padding = new Thickness(10, 5, 10, 5),
                    Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(88, 101, 242)),
                    Foreground = System.Windows.Media.Brushes.White,
                    BorderThickness = new Thickness(0)
                };
                btn.Click += (s, e) => { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); updateWin.Close(); };

                stack.Children.Add(btn);
                updateWin.Content = stack;
                updateWin.Show();
            });
        }

        #endregion

        #region Update Checker

        private async Task CheckForUpdates()
        {
            try
            {
                _httpClient.DefaultRequestHeaders.UserAgent.Clear();
                _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("RoPresence-App");
                var response = await _httpClient.GetStringAsync(UpdateUrl);
                var release = JObject.Parse(response);
                string latestTag = release["tag_name"]?.ToString();
                string htmlUrl = release["html_url"]?.ToString();

                if (!string.IsNullOrEmpty(latestTag) && latestTag != AppVersion)
                {
                    ShowUpdateUI(latestTag, htmlUrl);
                }
            }
            catch { }
        }

        #endregion

        #region Core Logic

        private void InitializeDiscord()
        {
            _rpcClient = new DiscordRpcClient(DiscordAppId);
            _rpcClient.Logger = new ConsoleLogger() { Level = LogLevel.Warning };

            _rpcClient.OnReady += (sender, msg) => {
                if (!string.IsNullOrEmpty(_currentPlaceId) && IsRobloxRunning() && _settings.DiscordRpcEnabled)
                {
                    Task.Run(() => UpdatePresence(_currentPlaceId));
                }
            };

            _rpcClient.Initialize();
        }

        private async Task UpdatePresence(string placeId)
        {
            if (!IsRobloxRunning() || !_settings.DiscordRpcEnabled || string.IsNullOrEmpty(placeId) || _rpcClient == null)
            {
                ClearPresence();
                return;
            }

            try
            {
                var uRes = await _httpClient.GetAsync($"https://apis.roblox.com/universes/v1/places/{placeId}/universe");
                if (!uRes.IsSuccessStatusCode) return;
                _currentUniverseId = JObject.Parse(await uRes.Content.ReadAsStringAsync())["universeId"]?.ToString();

                var gameRes = await _httpClient.GetAsync($"https://games.roblox.com/v1/games?universeIds={_currentUniverseId}");
                if (!gameRes.IsSuccessStatusCode) return;

                var gameData = JObject.Parse(await gameRes.Content.ReadAsStringAsync())["data"]?[0];
                if (gameData == null) return;

                _currentGameName = gameData["name"]?.ToString() ?? "Roblox Game";
                string creatorName = gameData["creator"]?["name"]?.ToString() ?? "Unknown";
                bool isVerified = gameData["creator"]?["hasVerifiedBadge"]?.Value<bool>() ?? false;

                string stateText = _settings.ShowCreator ? $"By {creatorName}{(isVerified ? $" {VerifiedEmoji}" : "")}" : "Playing";

                string largeImageUrl = "roblox_logo";
                try
                {
                    var iconRes = await _httpClient.GetAsync($"https://thumbnails.roblox.com/v1/places/gameicons?placeIds={placeId}&returnPolicy=PlaceHolder&size=512x512&format=Png&isCircular=false");
                    if (iconRes.IsSuccessStatusCode)
                    {
                        var iconData = JObject.Parse(await iconRes.Content.ReadAsStringAsync())["data"]?[0];
                        if (iconData != null && iconData["imageUrl"] != null)
                            largeImageUrl = iconData["imageUrl"].ToString();
                    }
                }
                catch { }

                this.Dispatcher.Invoke(() => { _trayIcon.Text = $"{AppTitle}: {_currentGameName}"; });

                LogGameHistory(_currentGameName, placeId, _currentJobId);

                if (!_rpcClient.IsInitialized) return;

                var presence = new RichPresence()
                {
                    Details = _currentGameName,
                    State = stateText,
                    Assets = new Assets()
                    {
                        LargeImageKey = largeImageUrl,
                        LargeImageText = _currentGameName
                    },
                    Buttons = new DiscordRPC.Button[]
                    {
                        new DiscordRPC.Button { Label = "View Game", Url = $"https://www.roblox.com/games/{placeId}" }
                    }
                };

                if (_settings.ShowTimestamp) presence.Timestamps = Timestamps.Now;
                _rpcClient.SetPresence(presence);
            }
            catch { }
        }

        private void StartProcessMonitor()
        {
            _processCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _processCheckTimer.Tick += (s, e) => {
                if (!IsRobloxRunning() && !string.IsNullOrEmpty(_currentPlaceId))
                {
                    try { if (File.Exists(HistoryPath)) File.WriteAllText(HistoryPath, "--- Game History Cleared ---\n"); } catch { }
                    ClearPresence();
                }
            };
            _processCheckTimer.Start();
        }

        private void StartWatcher()
        {
            string logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "logs");
            if (!Directory.Exists(logPath)) return;

            _logWatcher = new FileSystemWatcher(logPath, "*.log")
            {
                EnableRaisingEvents = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
            };

            _logWatcher.Changed += (s, e) => ProcessLog(e.FullPath);
            _logWatcher.Created += (s, e) => ProcessLog(e.FullPath);

            _logPollingTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _logPollingTimer.Tick += (s, e) => {
                try
                {
                    var latestLog = new DirectoryInfo(logPath).GetFiles("*.log").OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
                    if (latestLog != null) ProcessLog(latestLog.FullName);
                }
                catch { }
            };
            _logPollingTimer.Start();
        }

        private void ProcessLog(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (_currentLogFile != path) { _currentLogFile = path; _lastReadOffset = 0; }
                    if (fs.Length <= _lastReadOffset) return;
                    fs.Seek(_lastReadOffset, SeekOrigin.Begin);

                    using (var reader = new StreamReader(fs))
                    {
                        string content = reader.ReadToEnd();
                        _lastReadOffset = fs.Position;

                        if (content.Contains("Game ended") || content.Contains("Leaving Game"))
                        {
                            ClearPresence();
                            return;
                        }

                        var placeMatch = Regex.Match(content, @"placeId[:=]\s?(\d+)", RegexOptions.IgnoreCase);
                        if (placeMatch.Success && _currentPlaceId != placeMatch.Groups[1].Value)
                        {
                            _currentPlaceId = placeMatch.Groups[1].Value;
                            if (_settings.DiscordRpcEnabled)
                                Task.Run(() => UpdatePresence(_currentPlaceId));
                        }
                    }
                }
            }
            catch { }
        }

        private void ClearPresence(bool clearInternalState = true)
        {
            if (clearInternalState) { _currentPlaceId = ""; _currentJobId = ""; _currentGameName = ""; }
            _rpcClient?.ClearPresence();
            this.Dispatcher.Invoke(() => { if (_trayIcon != null) _trayIcon.Text = $"{AppTitle} - Idle"; });
        }

        private bool IsRobloxRunning() => Process.GetProcesses().Any(p => p.ProcessName.Contains("RobloxPlayer") || p.ProcessName.Contains("Windows10Universal"));

        private void KillRoblox()
        {
            var procs = Process.GetProcesses().Where(p => p.ProcessName.IndexOf("roblox", StringComparison.OrdinalIgnoreCase) >= 0);
            foreach (var p in procs) try { p.Kill(); } catch { }
        }

        private void OpenFileSafe(string path, string name)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (!File.Exists(path)) File.WriteAllText(path, $"--- {name} ---\n");
            try { Process.Start(new ProcessStartInfo("notepad.exe", path) { UseShellExecute = true }); } catch { }
        }

        private void LoadSettings()
        {
            try { if (File.Exists(SettingsPath)) _settings = JsonConvert.DeserializeObject<AppSettings>(File.ReadAllText(SettingsPath)); } catch { }
            if (_settings == null) _settings = new AppSettings();
        }

        private void SaveSettings() => File.WriteAllText(SettingsPath, JsonConvert.SerializeObject(_settings, Formatting.Indented));

        private void LogGameHistory(string gameName, string placeId, string jobId)
        {
            try { File.AppendAllText(HistoryPath, $"[{DateTime.Now:G}] {gameName} ({placeId})\n"); } catch { }
        }

        private void OnApplicationExit(object sender, ExitEventArgs e)
        {
            _rpcClient?.Dispose();
            if (_trayIcon != null) { _trayIcon.Visible = false; _trayIcon.Dispose(); }
        }

        #endregion
    }

    public class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkColors()) { }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (e.Item.Enabled && e.Item.Selected)
            {
                using (var brush = new SolidBrush(Color.FromArgb(64, 66, 73)))
                    e.Graphics.FillRoundedRectangle(brush, new Rectangle(4, 1, e.Item.Width - 8, e.Item.Height - 2), 4);
            }
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Color.White : Color.Gray;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Rectangle rect = new Rectangle(4, (e.Item.Height - 16) / 2, 16, 16);
            using (var brush = new SolidBrush(Color.FromArgb(64, 66, 73))) e.Graphics.FillRoundedRectangle(brush, rect, 4);
            using (var pen = new Pen(Color.White, 1.5f)) e.Graphics.DrawLines(pen, new System.Drawing.Point[] { new System.Drawing.Point(rect.X + 4, rect.Y + 8), new System.Drawing.Point(rect.X + 7, rect.Y + 11), new System.Drawing.Point(rect.X + 12, rect.Y + 5) });
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using (var pen = new Pen(Color.FromArgb(60, 60, 65)))
                e.Graphics.DrawLine(pen, 30, e.Item.Height / 2, e.Item.Width - 10, e.Item.Height / 2);
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
            // Specifically prevents the white margin while keeping icons functional
            using (var brush = new SolidBrush(Color.FromArgb(43, 45, 49)))
                e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }
    }

    public class DarkColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Color.FromArgb(43, 45, 49);
        public override Color MenuBorder => Color.FromArgb(30, 31, 34);
        public override Color MenuItemBorder => Color.Transparent;
        public override Color ImageMarginGradientBegin => Color.FromArgb(43, 45, 49);
        public override Color ImageMarginGradientMiddle => Color.FromArgb(43, 45, 49);
        public override Color ImageMarginGradientEnd => Color.FromArgb(43, 45, 49);
    }

    public static class GraphicsExtensions
    {
        public static void FillRoundedRectangle(this Graphics graphics, Brush brush, Rectangle bounds, int cornerRadius)
        {
            using (System.Drawing.Drawing2D.GraphicsPath path = new System.Drawing.Drawing2D.GraphicsPath())
            {
                int d = cornerRadius * 2;
                if (d > bounds.Width) d = bounds.Width;
                if (d > bounds.Height) d = bounds.Height;
                path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
                path.AddArc(bounds.X + bounds.Width - d, bounds.Y, d, d, 270, 90);
                path.AddArc(bounds.X + bounds.Width - d, bounds.Y + bounds.Height - d, d, d, 0, 90);
                path.AddArc(bounds.X, bounds.Y + bounds.Height - d, d, d, 90, 90);
                path.CloseFigure();
                graphics.FillPath(brush, path);
            }
        }
    }
}
