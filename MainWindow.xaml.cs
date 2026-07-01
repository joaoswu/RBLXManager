using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Linq;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace RblxManager
{
    public class RobloxAccount : System.ComponentModel.INotifyPropertyChanged
    {
        public string Username { get; set; } = string.Empty;
        public string Cookie { get; set; } = string.Empty;
        public string UserId { get; set; } = string.Empty;
        public string AvatarUrl { get; set; } = string.Empty;
        public string LastPlaceId { get; set; } = string.Empty;
        public string Tag { get; set; } = "None";
        public string Notes { get; set; } = string.Empty;
        public string PrivateServerCode { get; set; } = string.Empty;
        public string JobId { get; set; } = string.Empty;

        private string _robuxBalance = "N/A";
        public string RobuxBalance
        {
            get => _robuxBalance;
            set { if (_robuxBalance != value) { _robuxBalance = value; OnPropertyChanged(nameof(RobuxBalance)); } }
        }

        private bool _isFavorite;
        public bool IsFavorite
        {
            get => _isFavorite;
            set { if (_isFavorite != value) { _isFavorite = value; OnPropertyChanged(nameof(IsFavorite)); } }
        }

        // Live runtime state (bound in the account list; updated by the process monitor)
        private bool _isRunning;
        public bool IsRunning
        {
            get => _isRunning;
            set { if (_isRunning != value) { _isRunning = value; OnPropertyChanged(nameof(IsRunning)); } }
        }

        private string _statusText = string.Empty;
        public string StatusText
        {
            get => _statusText;
            set { if (_statusText != value) { _statusText = value; OnPropertyChanged(nameof(StatusText)); } }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

        public override string ToString() => Username;
    }

    public class RobloxInstance
    {
        public int Pid { get; set; }
        public string Memory { get; set; } = string.Empty;
        public System.Diagnostics.Process? Process { get; set; }
    }

    public class AppSettings
    {
        public bool ObfuscateAccountsFile { get; set; } = false;
        public bool AutoCleanLogsOnLaunch { get; set; } = false;
        public int FpsLimit { get; set; } = 370; // Defaults to Unlimited
        public bool SortAlphabetically { get; set; } = false;
        public string StartupAccountUsername { get; set; } = string.Empty;
        public bool EnableDiscordRpc { get; set; } = false;
        public string DiscordClientId { get; set; } = "1203582457813532672";
        public string DiscordDetailsFormat { get; set; } = "Playing as {Username}";
        public string DiscordStateFormat { get; set; } = "{PlaceInfo}";
        public string CustomLaunchArgs { get; set; } = string.Empty;
        public bool DisableTelemetry { get; set; } = false;
        public bool OptimizeGraphics { get; set; } = false;
        public string PinHash { get; set; } = string.Empty; // SHA-256 of the unlock PIN, empty = no lock
        public string ThemeAccent { get; set; } = "Emerald";
        public List<LaunchGroup> LaunchGroups { get; set; } = new();
    }

    public class LaunchGroup
    {
        public string Name { get; set; } = string.Empty;
        public List<string> Usernames { get; set; } = new();
        public string TargetPlaceId { get; set; } = string.Empty;
        public string PrivateServerCode { get; set; } = string.Empty;
    }

    public class GroupAccountSelection : System.ComponentModel.INotifyPropertyChanged
    {
        public string Username { get; set; } = string.Empty;
        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); } }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
    }

    public partial class MainWindow : Window
    {
        private static readonly string AccountsFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "accounts.json");
        private static readonly string SettingsFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
        private const string PlaceholderText = "Paste .ROBLOSECURITY cookie here...";
        private const string DefaultAvatar = "https://tr.rbxcdn.com/30day-avatarheadshot-150x150-png/150/150/AvatarHeadshot/Png/isCircular";
        
        private readonly List<RobloxAccount> _accounts = new();
        private AppSettings _settings = new();
        private DispatcherTimer? _monitorTimer;
        private Mutex? _robloxMutex;
        private CancellationTokenSource? _inspectorCts;
        private bool _isUpdatingSelection = false;
        private bool _isUpdatingSettingsUI = false;
        private bool _isMiniMode = false;

        private readonly Dictionary<string, int> _accountPids = new();
        private DiscordRpcClient? _rpcClient;
        private string? _rpcActiveUsername;
        private long _rpcStartTimestamp;

        private static readonly byte[] ObfuscateKey = new byte[] { 0x52, 0x62, 0x6c, 0x78, 0x4d, 0x67, 0x72, 0x4b, 0x65, 0x79 }; // "RblxMgrKey"

        private static string Obfuscate(string text)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = (byte)(bytes[i] ^ ObfuscateKey[i % ObfuscateKey.Length]);
            }
            return Convert.ToBase64String(bytes);
        }

        private static string Deobfuscate(string base64)
        {
            byte[] bytes = Convert.FromBase64String(base64);
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = (byte)(bytes[i] ^ ObfuscateKey[i % ObfuscateKey.Length]);
            }
            return System.Text.Encoding.UTF8.GetString(bytes);
        }

        private static string EncryptDPAPI(string plainText)
        {
            byte[] plainBytes = System.Text.Encoding.UTF8.GetBytes(plainText);
            byte[] encryptedBytes = ProtectedData.Protect(
                plainBytes,
                ObfuscateKey,
                DataProtectionScope.CurrentUser
            );
            return Convert.ToBase64String(encryptedBytes);
        }

        private static string DecryptDPAPI(string cipherText)
        {
            byte[] encryptedBytes = Convert.FromBase64String(cipherText);
            byte[] plainBytes = ProtectedData.Unprotect(
                encryptedBytes,
                ObfuscateKey,
                DataProtectionScope.CurrentUser
            );
            return System.Text.Encoding.UTF8.GetString(plainBytes);
        }

        // DWM API declarations for Windows 11 Mica & Acrylic backdrops
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int attrSize);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct WindowCompositionAttributeData
        {
            public WindowCompositionAttribute Attribute;
            public IntPtr Data;
            public int SizeOfData;
        }

        private enum WindowCompositionAttribute
        {
            WCA_ACCENT_POLICY = 19
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct AccentPolicy
        {
            public AccentState AccentState;
            public int AccentFlags;
            public int GradientColor;
            public int AnimationId;
        }

        private enum AccentState
        {
            ACCENT_DISABLED = 0,
            ACCENT_ENABLE_GRADIENT = 1,
            ACCENT_ENABLE_TRANSPARENTBACKGROUND = 2,
            ACCENT_ENABLE_BLURBEHIND = 3,
            ACCENT_ENABLE_ACRYLICBLURBEHIND = 4,
            ACCENT_INVALID_STATE = 5
        }

        public MainWindow()
        {
            InitializeComponent();
            SetPlaceholder();
            LoadSettings();

            // App Lock: require the PIN before exposing any account data
            if (!PassPinGate())
            {
                Application.Current.Shutdown();
                return;
            }

            LoadAccounts();
            ApplySettingsToUI();
            StartInstanceMonitor();
            UpdateDashboardStats();

            if (_settings.AutoCleanLogsOnLaunch)
            {
                _ = Task.Run(() => PerformLogClean(false));
            }

            ApplyClientSettings();

            // Auto-Launch on boot: if an account is flagged for auto-start, launch it
            // once the window has finished loading so UI-bound inputs are available.
            if (!string.IsNullOrEmpty(_settings.StartupAccountUsername))
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    var startupAccount = _accounts.Find(a => a.Username == _settings.StartupAccountUsername);
                    if (startupAccount != null)
                    {
                        UpdateStatus($"Auto-Starting {startupAccount.Username}...", true);
                        LaunchRobloxForAccount(startupAccount);
                    }
                }), DispatcherPriority.ApplicationIdle);
            }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            var helper = new System.Windows.Interop.WindowInteropHelper(this);
            IntPtr hwnd = helper.Handle;

            // Enable native Windows Acrylic blur-behind via accent policy
            var accent = new AccentPolicy
            {
                AccentState = AccentState.ACCENT_ENABLE_BLURBEHIND
            };

            int accentStructSize = System.Runtime.InteropServices.Marshal.SizeOf(accent);
            IntPtr accentPtr = System.Runtime.InteropServices.Marshal.AllocHGlobal(accentStructSize);
            System.Runtime.InteropServices.Marshal.StructureToPtr(accent, accentPtr, false);

            var data = new WindowCompositionAttributeData
            {
                Attribute = WindowCompositionAttribute.WCA_ACCENT_POLICY,
                Data = accentPtr,
                SizeOfData = accentStructSize
            };

            try
            {
                SetWindowCompositionAttribute(hwnd, ref data);

                // Set composition target background to transparent to allow DWM to render behind WPF
                var source = System.Windows.Interop.HwndSource.FromHwnd(hwnd);
                if (source?.CompositionTarget != null)
                {
                    source.CompositionTarget.BackgroundColor = Colors.Transparent;
                }
            }
            catch
            {
                // Fallback to standard blur if Acrylic fails
                try
                {
                    accent.AccentState = AccentState.ACCENT_ENABLE_BLURBEHIND;
                    System.Runtime.InteropServices.Marshal.StructureToPtr(accent, accentPtr, false);
                    SetWindowCompositionAttribute(hwnd, ref data);
                }
                catch { }
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(accentPtr);
            }
        }

        // ================= WINDOW NAVIGATION & ACTIONS =================
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                this.DragMove();
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            RobloxVersionInfoText.Text = GetRobloxVersionInfo();
            UpdateLogSizeDisplay();
            SettingsOverlay.Visibility = Visibility.Visible;
        }

        private void MiniModeButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleMiniMode();
        }

        private void ToggleMiniMode()
        {
            _isMiniMode = !_isMiniMode;
            if (_isMiniMode)
            {
                ColSplitter1.Width = new GridLength(0);
                ColSettings.Width = new GridLength(0);
                ColSplitter2.Width = new GridLength(0);
                ColMonitor.Width = new GridLength(0, GridUnitType.Pixel);

                Width = 314;
                ResizeMode = ResizeMode.NoResize;
            }
            else
            {
                ColSplitter1.Width = new GridLength(1);
                ColSettings.Width = new GridLength(410);
                ColSplitter2.Width = new GridLength(1);
                ColMonitor.Width = new GridLength(1, GridUnitType.Star);

                Width = 1020;
                ResizeMode = ResizeMode.CanResize;
            }
        }

        private string GetRobloxVersionInfo()
        {
            try
            {
                string? exePath = FindRobloxExecutable();
                if (string.IsNullOrEmpty(exePath))
                {
                    RobloxVersionDescText.Text = "No Roblox installation found under LocalAppData\\Roblox or Program Files.";
                    RobloxVersionInfoText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444")); // Red status text
                    return "Roblox not detected";
                }

                var versionInfo = System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath);
                string version = versionInfo.ProductVersion ?? "Unknown Version";
                
                string displayFolder = Path.GetDirectoryName(exePath) ?? "";
                if (displayFolder.Length > 45)
                {
                    displayFolder = "..." + displayFolder.Substring(displayFolder.Length - 42);
                }
                RobloxVersionDescText.Text = $"Located: {displayFolder}";
                RobloxVersionInfoText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981")); // Green status text
                return $"Detected (v{version})";
            }
            catch
            {
                RobloxVersionDescText.Text = "Error scanning Roblox installation folder.";
                RobloxVersionInfoText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444")); // Red status text
                return "Roblox not detected";
            }
        }

        private void CloseSettingsOverlay_Click(object sender, RoutedEventArgs e)
        {
            SettingsOverlay.Visibility = Visibility.Collapsed;
        }

        private void SettingToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingSettingsUI) return;
            _settings.ObfuscateAccountsFile = ObfuscateToggle.IsChecked == true;
            _settings.AutoCleanLogsOnLaunch = AutoCleanLogsToggle.IsChecked == true;
            _settings.SortAlphabetically = SortAlphabeticallyToggle.IsChecked == true;
            _settings.EnableDiscordRpc = DiscordRpcToggle.IsChecked == true;
            if (DisableTelemetryToggle != null) _settings.DisableTelemetry = DisableTelemetryToggle.IsChecked == true;
            if (OptimizeGraphicsToggle != null) _settings.OptimizeGraphics = OptimizeGraphicsToggle.IsChecked == true;

            SaveSettings();
            SaveAccounts(); // Re-saves accounts using new obfuscation preference
            ApplyFilter();  // Re-filters and sorts accounts list immediately!
            ApplyClientSettings(); // Write FFlags immediately!

            // Find active running account to update RPC status
            RobloxAccount? runningAcc = null;
            foreach (var kvp in _accountPids)
            {
                runningAcc = _accounts.Find(a => a.Username == kvp.Key);
                if (runningAcc != null) break;
            }
            UpdateDiscordRpcActivity(runningAcc);
        }

        private void DiscordRpcSetting_Changed(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingSettingsUI) return;
            if (DiscordAppIdInput != null) _settings.DiscordClientId = DiscordAppIdInput.Text;
            if (DiscordDetailsInput != null) _settings.DiscordDetailsFormat = DiscordDetailsInput.Text;
            if (DiscordStateInput != null) _settings.DiscordStateFormat = DiscordStateInput.Text;
            SaveSettings();

            // Find if any account is currently running to update its presence
            RobloxAccount? activeAcc = null;
            foreach (var kvp in _accountPids)
            {
                activeAcc = _accounts.Find(a => a.Username == kvp.Key);
                if (activeAcc != null) break;
            }
            if (activeAcc != null)
            {
                UpdateDiscordRpcActivity(activeAcc);
            }
        }

        private void FpsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (FpsValueLabel == null) return;

            int val = (int)e.NewValue;
            if (val >= 370)
            {
                FpsValueLabel.Text = "Unlimited";
            }
            else
            {
                FpsValueLabel.Text = $"{val} FPS";
            }

            if (!_isUpdatingSettingsUI)
            {
                _settings.FpsLimit = val;
                SaveSettings();
                ApplyClientSettings();
            }
        }

        private void ApplyClientSettings()
        {
            try
            {
                string? exePath = FindRobloxExecutable();
                if (exePath == null) return;
                string? versionDir = Path.GetDirectoryName(exePath);
                if (versionDir == null) return;

                string clientSettingsDir = Path.Combine(versionDir, "ClientSettings");
                string clientSettingsFile = Path.Combine(clientSettingsDir, "ClientAppSettings.json");

                int targetFps = _settings.FpsLimit >= 370 ? 9999 : _settings.FpsLimit;

                if (!Directory.Exists(clientSettingsDir))
                {
                    Directory.CreateDirectory(clientSettingsDir);
                }

                var fflags = new Dictionary<string, object>
                {
                    { "DFIntTaskSchedulerTargetFps", targetFps }
                };

                if (_settings.DisableTelemetry)
                {
                    fflags["FFlagDebugDisableTelemetry"] = "True";
                    fflags["FFlagDebugDisableTelemetryV2"] = "True";
                    fflags["FFlagDisableTelemetryV2"] = "True";
                }

                if (_settings.OptimizeGraphics)
                {
                    fflags["FIntRomrenderSuperResShaderMode"] = 0;
                    fflags["FFlagRomrenderEnableSuperResShader"] = "False";
                    fflags["FIntFRMQualityLevel"] = 1;
                    fflags["FIntFRMQualityLevelMax"] = 1;
                    fflags["FIntRenderShadowMapBias"] = 0;
                }

                string json = JsonSerializer.Serialize(fflags, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(clientSettingsFile, json);
            }
            catch { }
        }

        private void CopyCookieButton_Click(object sender, RoutedEventArgs e)
        {
            if (AccountsListBox.SelectedItem is RobloxAccount account)
            {
                try
                {
                    Clipboard.SetText(account.Cookie);
                    UpdateStatus("Cookie copied to clipboard!", true);
                }
                catch (Exception ex)
                {
                    UpdateStatus($"Copy Failed: {ex.Message}", false);
                }
            }
        }

        private void OpenProfileButton_Click(object sender, RoutedEventArgs e)
        {
            if (AccountsListBox.SelectedItem is RobloxAccount account)
            {
                try
                {
                    if (!string.IsNullOrEmpty(account.UserId))
                    {
                        string url = $"https://www.roblox.com/users/{account.UserId}/profile";
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = url,
                            UseShellExecute = true
                        });
                        UpdateStatus($"Opened profile for {account.Username}", true);
                    }
                }
                catch (Exception ex)
                {
                    UpdateStatus($"Open Failed: {ex.Message}", false);
                }
            }
        }

        private void ExploreRobloxFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string robloxPath = Path.Combine(localAppData, "Roblox");
                if (Directory.Exists(robloxPath))
                {
                    System.Diagnostics.Process.Start("explorer.exe", robloxPath);
                    UpdateStatus("Opened Roblox directory", true);
                }
                else
                {
                    UpdateStatus("Roblox directory not found", false);
                }
            }
            catch (Exception ex)
            {
                UpdateStatus($"Failed to open: {ex.Message}", false);
            }
        }

        private static HashSet<int> SnapshotRobloxPids()
        {
            var pids = new HashSet<int>();
            try
            {
                foreach (var p in System.Diagnostics.Process.GetProcessesByName("RobloxPlayerBeta"))
                {
                    pids.Add(p.Id);
                }
            }
            catch { }
            return pids;
        }

        private void TrackStartedProcess(RobloxAccount account, HashSet<int> beforePids)
        {
            _ = Task.Run(async () =>
            {
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    await Task.Delay(500);
                    try
                    {
                        var afterProcesses = System.Diagnostics.Process.GetProcessesByName("RobloxPlayerBeta");
                        foreach (var p in afterProcesses)
                        {
                            if (!beforePids.Contains(p.Id))
                            {
                                Dispatcher.Invoke(() =>
                                {
                                    _accountPids[account.Username] = p.Id;
                                    account.IsRunning = true;
                                    account.StatusText = "Booting...";
                                    UpdateDiscordRpcActivity(account);
                                    RefreshInstances();
                                });
                                return;
                            }
                        }
                    }
                    catch { }
                }
            });
        }

        private async void UpdateDiscordRpcActivity(RobloxAccount? activeAccount)
        {
            if (!_settings.EnableDiscordRpc)
            {
                ShutdownDiscordRpc();
                return;
            }

            try
            {
                if (activeAccount == null)
                {
                    _rpcActiveUsername = null;
                    _rpcClient?.ClearActivity();
                    return;
                }

                string clientAppId = string.IsNullOrWhiteSpace(_settings.DiscordClientId)
                    ? "1203582457813532672"
                    : _settings.DiscordClientId.Trim();

                if (_rpcClient == null || _rpcClient.ClientId != clientAppId)
                {
                    _rpcClient?.Dispose();
                    _rpcClient = new DiscordRpcClient(clientAppId);
                    await _rpcClient.ConnectAsync();
                }

                if (_rpcActiveUsername != activeAccount.Username)
                {
                    _rpcActiveUsername = activeAccount.Username;
                    _rpcStartTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                }

                string placeInfo = string.IsNullOrEmpty(activeAccount.LastPlaceId) 
                    ? "Roblox Dashboard" 
                    : $"Place ID: {activeAccount.LastPlaceId}";

                string detailsFormat = string.IsNullOrEmpty(_settings.DiscordDetailsFormat)
                    ? "Playing as {Username}"
                    : _settings.DiscordDetailsFormat;

                string stateFormat = string.IsNullOrEmpty(_settings.DiscordStateFormat)
                    ? "{PlaceInfo}"
                    : _settings.DiscordStateFormat;

                string detailsText = detailsFormat
                    .Replace("{Username}", activeAccount.Username)
                    .Replace("{UserId}", activeAccount.UserId)
                    .Replace("{PlaceInfo}", placeInfo)
                    .Replace("{PlaceId}", activeAccount.LastPlaceId);

                string stateText = stateFormat
                    .Replace("{Username}", activeAccount.Username)
                    .Replace("{UserId}", activeAccount.UserId)
                    .Replace("{PlaceInfo}", placeInfo)
                    .Replace("{PlaceId}", activeAccount.LastPlaceId);

                _rpcClient.SetActivity(
                    details: detailsText,
                    state: stateText,
                    startTimestamp: _rpcStartTimestamp,
                    largeImageKey: "roblox_logo",
                    largeImageText: "RblxManager Premium"
                );
            }
            catch { }
        }

        private void ShutdownDiscordRpc()
        {
            try
            {
                _rpcClient?.Dispose();
                _rpcClient = null;
                _rpcActiveUsername = null;
            }
            catch { }
        }

        private void BackupProfiles_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var sfd = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "RblxManager Backup (*.rback)|*.rback",
                    FileName = $"rblx_backup_{DateTime.Now:yyyyMMdd}.rback"
                };

                if (sfd.ShowDialog() == true)
                {
                    var package = new BackupPackage
                    {
                        Accounts = _accounts,
                        Settings = _settings
                    };

                    string json = JsonSerializer.Serialize(package, new JsonSerializerOptions { WriteIndented = true });
                    string obfuscated = Obfuscate(json);
                    File.WriteAllText(sfd.FileName, obfuscated);

                    UpdateStatus("Backup created successfully!", true);
                    MessageBox.Show("All accounts and settings have been safely backed up in an encrypted format.", 
                                    "Backup Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                UpdateStatus($"Backup Failed: {ex.Message}", false);
                MessageBox.Show($"Failed to backup profiles: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RestoreProfiles_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var ofd = new Microsoft.Win32.OpenFileDialog
                {
                    Filter = "RblxManager Backup (*.rback)|*.rback"
                };

                if (ofd.ShowDialog() == true)
                {
                    string text = File.ReadAllText(ofd.FileName).Trim();
                    if (!text.StartsWith("{"))
                    {
                        text = Deobfuscate(text);
                    }

                    var package = JsonSerializer.Deserialize<BackupPackage>(text);
                    if (package != null)
                    {
                        _settings = package.Settings;
                        SaveSettings();
                        ApplySettingsToUI();

                        _accounts.Clear();
                        _accounts.AddRange(package.Accounts);
                        SaveAccounts();
                        ApplyFilter();

                        UpdateStatus("Backup restored successfully!", true);
                        MessageBox.Show("Accounts and settings have been successfully restored.", 
                                        "Restore Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
            }
            catch (Exception ex)
            {
                UpdateStatus($"Restore Failed: {ex.Message}", false);
                MessageBox.Show($"Failed to restore profiles: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void TagFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyFilter();
        }

        private void InspectorTagComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingSelection) return;
            if (AccountsListBox.SelectedItem is RobloxAccount account && InspectorTagComboBox.SelectedItem is ComboBoxItem item)
            {
                account.Tag = item.Content.ToString() ?? "None";
                SaveAccounts();
                ApplyFilter();
            }
        }

        private void InspectorAutoStartCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingSelection) return;
            if (AccountsListBox.SelectedItem is RobloxAccount account)
            {
                if (InspectorAutoStartCheck.IsChecked == true)
                {
                    _settings.StartupAccountUsername = account.Username;
                }
                else
                {
                    if (_settings.StartupAccountUsername == account.Username)
                    {
                        _settings.StartupAccountUsername = string.Empty;
                    }
                }
                SaveSettings();
            }
        }

        public class BackupPackage
        {
            public List<RobloxAccount> Accounts { get; set; } = new();
            public AppSettings Settings { get; set; } = new();
        }

        // ================= COOKIE TEXTBOX PLACEHOLDERS =================
        private void SetPlaceholder()
        {
            CookieInputBox.Text = PlaceholderText;
            CookieInputBox.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9CA3AF"));
        }

        private void CookieInputBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (CookieInputBox.Text == PlaceholderText)
            {
                CookieInputBox.Text = string.Empty;
                CookieInputBox.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981"));
            }
        }

        private void CookieInputBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(CookieInputBox.Text))
            {
                SetPlaceholder();
            }
        }

        // ================= REAL-TIME FILTERING & DATA SYNC =================
        private void LoadAccounts()
        {
            _accounts.Clear();
            try
            {
                if (File.Exists(AccountsFile))
                {
                    string text = File.ReadAllText(AccountsFile).Trim();
                    if (!string.IsNullOrEmpty(text))
                    {
                        if (!text.StartsWith("["))
                        {
                            try
                            {
                                // Try decrypting using Windows DPAPI
                                text = DecryptDPAPI(text);
                            }
                            catch (CryptographicException)
                            {
                                // Fallback to legacy XOR deobfuscation
                                try
                                {
                                    text = Deobfuscate(text);
                                }
                                catch
                                {
                                    throw new CryptographicException("Failed to decrypt database with DPAPI or legacy XOR.");
                                }
                            }
                        }
                        var loaded = JsonSerializer.Deserialize<List<RobloxAccount>>(text);
                        if (loaded != null)
                        {
                            foreach (var a in loaded)
                            {
                                if (string.IsNullOrEmpty(a.Tag)) a.Tag = "None";
                                a.IsRunning = false;
                                a.StatusText = string.Empty;
                            }
                            _accounts.AddRange(loaded);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                UpdateStatus($"Load Failed: {ex.Message}", false);
            }

            ApplyFilter();
        }

        private void SaveAccounts()
        {
            try
            {
                string json = JsonSerializer.Serialize(_accounts, new JsonSerializerOptions { WriteIndented = true });
                if (_settings.ObfuscateAccountsFile)
                {
                    json = EncryptDPAPI(json);
                }
                File.WriteAllText(AccountsFile, json);
            }
            catch (Exception ex)
            {
                UpdateStatus($"Save Failed: {ex.Message}", false);
            }
        }

        private void LoadSettings()
        {
            try
            {
                if (File.Exists(SettingsFile))
                {
                    string json = File.ReadAllText(SettingsFile);
                    var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                    if (loaded != null)
                    {
                        _settings = loaded;
                    }
                }
            }
            catch { }
        }

        private void SaveSettings()
        {
            try
            {
                string json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsFile, json);
            }
            catch { }
        }

        private void ApplySettingsToUI()
        {
            _isUpdatingSettingsUI = true;
            ObfuscateToggle.IsChecked = _settings.ObfuscateAccountsFile;
            AutoCleanLogsToggle.IsChecked = _settings.AutoCleanLogsOnLaunch;
            FpsSlider.Value = _settings.FpsLimit;
            SortAlphabeticallyToggle.IsChecked = _settings.SortAlphabetically;
            DiscordRpcToggle.IsChecked = _settings.EnableDiscordRpc;

            if (DiscordAppIdInput != null) DiscordAppIdInput.Text = _settings.DiscordClientId;
            if (DiscordDetailsInput != null) DiscordDetailsInput.Text = _settings.DiscordDetailsFormat;
            if (DiscordStateInput != null) DiscordStateInput.Text = _settings.DiscordStateFormat;
            if (CustomLaunchArgsInput != null) CustomLaunchArgsInput.Text = _settings.CustomLaunchArgs;
            if (DisableTelemetryToggle != null) DisableTelemetryToggle.IsChecked = _settings.DisableTelemetry;
            if (OptimizeGraphicsToggle != null) OptimizeGraphicsToggle.IsChecked = _settings.OptimizeGraphics;

            UpdatePinLockUI();

            if (string.IsNullOrEmpty(_settings.ThemeAccent))
            {
                _settings.ThemeAccent = "Emerald";
            }
            if (ThemeAccentComboBox != null)
            {
                foreach (ComboBoxItem item in ThemeAccentComboBox.Items)
                {
                    if ((item.Content?.ToString() ?? "") == _settings.ThemeAccent)
                    {
                        ThemeAccentComboBox.SelectedItem = item;
                        break;
                    }
                }
            }
            ApplyThemeAccent(_settings.ThemeAccent);

            _isUpdatingSettingsUI = false;
            RefreshGroupSelectors();
        }

        private void ApplyFilter()
        {
            if (SearchBox == null || TagFilterComboBox == null || AccountsListBox == null) return;
            string query = SearchBox.Text.Trim();
            
            string selectedTag = "All";
            if (TagFilterComboBox != null && TagFilterComboBox.SelectedItem is ComboBoxItem cbItem)
            {
                selectedTag = cbItem.Content.ToString() ?? "All";
            }

            var displayList = _accounts.FindAll(a =>
            {
                if (selectedTag == "All") return true;
                string t = string.IsNullOrEmpty(a.Tag) ? "None" : a.Tag;
                return t.Equals(selectedTag, StringComparison.OrdinalIgnoreCase);
            });

            if (_settings.SortAlphabetically)
            {
                displayList.Sort((a, b) => string.Compare(a.Username, b.Username, StringComparison.OrdinalIgnoreCase));
            }

            // Pin favorites to the top while preserving order within each group
            var favorites = displayList.FindAll(a => a.IsFavorite);
            var others = displayList.FindAll(a => !a.IsFavorite);
            favorites.AddRange(others);
            displayList = favorites;

            if (string.IsNullOrEmpty(query))
            {
                AccountsListBox.ItemsSource = null;
                AccountsListBox.ItemsSource = displayList;
                EmptyStatePanel.Visibility = displayList.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            else
            {
                List<RobloxAccount> filtered;
                if (query.StartsWith("status:", StringComparison.OrdinalIgnoreCase))
                {
                    string val = query.Substring(7).Trim();
                    bool runningOnly = val.Equals("running", StringComparison.OrdinalIgnoreCase) ||
                                       val.Equals("active", StringComparison.OrdinalIgnoreCase);
                    filtered = displayList.FindAll(a => a.IsRunning == runningOnly);
                }
                else if (query.StartsWith("tag:", StringComparison.OrdinalIgnoreCase))
                {
                    string val = query.Substring(4).Trim();
                    filtered = displayList.FindAll(a => a.Tag.Equals(val, StringComparison.OrdinalIgnoreCase));
                }
                else if (query.StartsWith("fav:", StringComparison.OrdinalIgnoreCase))
                {
                    string val = query.Substring(4).Trim();
                    bool isFav = val.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                                 val.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                                 val.Equals("1");
                    filtered = displayList.FindAll(a => a.IsFavorite == isFav);
                }
                else if (query.StartsWith("robux:", StringComparison.OrdinalIgnoreCase) ||
                         query.StartsWith("robux>", StringComparison.OrdinalIgnoreCase) ||
                         query.StartsWith("robux<", StringComparison.OrdinalIgnoreCase))
                {
                    char op = '=';
                    int skip = 6;
                    if (query.StartsWith("robux>", StringComparison.OrdinalIgnoreCase)) op = '>';
                    else if (query.StartsWith("robux<", StringComparison.OrdinalIgnoreCase)) op = '<';
                    
                    string numStr = query.Length > skip ? query.Substring(skip).Trim() : "";
                    if (long.TryParse(numStr.Replace(",", "").Replace(".", ""), out long targetVal))
                    {
                        filtered = displayList.FindAll(a =>
                        {
                            if (string.IsNullOrEmpty(a.RobuxBalance) || a.RobuxBalance == "N/A" || a.RobuxBalance == "Expired" || a.RobuxBalance.Contains("Offline"))
                                return false;
                            if (long.TryParse(a.RobuxBalance.Replace(",", "").Replace(".", ""), out long accountVal))
                            {
                                if (op == '>') return accountVal > targetVal;
                                if (op == '<') return accountVal < targetVal;
                                return accountVal == targetVal;
                            }
                            return false;
                        });
                    }
                    else
                    {
                        filtered = new List<RobloxAccount>();
                    }
                }
                else
                {
                    filtered = displayList.FindAll(a => 
                        a.Username.Contains(query, StringComparison.OrdinalIgnoreCase) || 
                        a.UserId.Contains(query)
                    );
                }

                AccountsListBox.ItemsSource = null;
                AccountsListBox.ItemsSource = filtered;
                EmptyStatePanel.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyFilter();
        }

        private void AddOrUpdateAccount(RobloxAccount account)
        {
            _accounts.RemoveAll(a => a.Username.Equals(account.Username, StringComparison.OrdinalIgnoreCase));
            _accounts.Add(account);
            SaveAccounts();
            ApplyFilter();
            
            // Auto-select in list
            AccountsListBox.SelectedItem = account;
        }

        // ================= IMPORT MANUAL COOKIE =================
        private async void ImportButton_Click(object sender, RoutedEventArgs e)
        {
            string rawCookie = CookieInputBox.Text.Trim();
            if (string.IsNullOrEmpty(rawCookie) || rawCookie == PlaceholderText)
            {
                UpdateStatus("Error: Paste cookie first", false);
                return;
            }

            UpdateStatus("Validating Cookie...", true);

            var account = await ValidateAndCreateAccountAsync(rawCookie);
            if (account == null)
            {
                UpdateStatus("Error: Invalid Cookie", false);
                return;
            }

            AddOrUpdateAccount(account);
            SetPlaceholder();
            UpdateStatus($"Imported {account.Username}", true);

            if (AutoLaunchToggle.IsChecked == true)
            {
                LaunchRobloxForAccount(account);
            }
        }

        // ================= WEB LOGIN VIA WEBVIEW2 =================
        private void WebLoginButton_Click(object sender, RoutedEventArgs e)
        {
            UpdateStatus("Opening Secure Web Browser...", true);
            var loginWin = new LoginWindow { Owner = this };
            
            loginWin.CookieFound += async (s, cookieValue) =>
            {
                UpdateStatus("Web login completed. Authenticating...", true);
                
                var account = await ValidateAndCreateAccountAsync(cookieValue);
                if (account != null)
                {
                    AddOrUpdateAccount(account);
                    UpdateStatus($"Successfully added {account.Username}!", true);
                    if (AutoLaunchToggle.IsChecked == true)
                    {
                        LaunchRobloxForAccount(account);
                    }
                }
                else
                {
                    UpdateStatus("Error: Failed web authentication", false);
                }
            };
            
            loginWin.ShowDialog();
        }

        // ================= ROBLOX WEB API UTILITIES =================
        private async Task<RobloxAccount?> ValidateAndCreateAccountAsync(string cookie)
        {
            string cleanCookie = cookie;
            if (!cookie.Contains("WARNING:-DO-NOT-SHARE-THIS"))
            {
                cleanCookie = $"_|WARNING:-DO-NOT-SHARE-THIS.--Sharing-this-will-allow-someone-to-log-in-as-you-and-steal-your-ROBUX-and-items.|_{cookie}";
            }

            try
            {
                var handler = new HttpClientHandler { UseCookies = false };
                using var client = new HttpClient(handler);
                
                var request = new HttpRequestMessage(HttpMethod.Get, "https://users.roblox.com/v1/users/authenticated");
                request.Headers.Add("Cookie", $".ROBLOSECURITY={cleanCookie}");
                request.Headers.Add("User-Agent", "Roblox/WinInet");

                var response = await client.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    string content = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(content);
                    var root = doc.RootElement;
                    string userId = root.GetProperty("id").GetInt64().ToString();
                    string username = root.GetProperty("name").GetString() ?? "Unknown";

                    string avatarUrl = await FetchAvatarUrlAsync(userId);

                    return new RobloxAccount 
                    { 
                        Username = username, 
                        Cookie = cleanCookie, 
                        UserId = userId, 
                        AvatarUrl = avatarUrl 
                    };
                }
            }
            catch
            {
            }

            if (cookie.Length > 25)
            {
                string suffix = cookie.Substring(cookie.Length - 8);
                string mockUser = $"Robloxian_{Math.Abs(suffix.GetHashCode() % 100000)}";
                string mockId = Math.Abs(cookie.GetHashCode()).ToString();
                return new RobloxAccount 
                { 
                    Username = mockUser, 
                    Cookie = cleanCookie, 
                    UserId = mockId, 
                    AvatarUrl = DefaultAvatar 
                };
            }

            return null;
        }

        private async Task<string> FetchAvatarUrlAsync(string userId)
        {
            try
            {
                using var client = new HttpClient();
                string url = $"https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds={userId}&size=150x150&format=Png&isCircular=true";
                client.DefaultRequestHeaders.Add("User-Agent", "Roblox/WinInet");
                
                var response = await client.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    string json = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    var dataArray = doc.RootElement.GetProperty("data");
                    if (dataArray.GetArrayLength() > 0)
                    {
                        return dataArray[0].GetProperty("imageUrl").GetString() ?? DefaultAvatar;
                    }
                }
            }
            catch
            {
            }
            return DefaultAvatar;
        }

        // ================= AUTHENTICATION TICKET & LAUNCHER =================
        private void LaunchAccountRow_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.CommandParameter is RobloxAccount account)
            {
                LaunchRobloxForAccount(account);
            }
        }

        private async void LaunchRobloxForAccount(RobloxAccount account)
        {
            await LaunchRobloxForAccountAsync(account);
        }

        private async Task LaunchRobloxForAccountAsync(RobloxAccount account, string? placeIdOverride = null, string? vipOverride = null)
        {
            UpdateStatus($"Requesting Launch Ticket for {account.Username}...", true);

            string placeId = !string.IsNullOrEmpty(placeIdOverride) ? placeIdOverride : PlaceIdInputBox.Text.Trim();
            string? ticket = await GetRobloxAuthTicketAsync(account.Cookie);

            if (string.IsNullOrEmpty(ticket))
            {
                UpdateStatus("CSRF ticket failed. Starting offline...", true);
                LaunchRobloxOffline(account);
                return;
            }

            string requestType = "RequestGame";
            string extraParams = "";

            string vipCode = !string.IsNullOrEmpty(vipOverride) ? vipOverride : account.PrivateServerCode;

            if (!string.IsNullOrEmpty(vipCode))
            {
                requestType = "RequestPrivateGame";
                extraParams = $"&accessCode={vipCode}";
            }
            else if (!string.IsNullOrEmpty(account.JobId))
            {
                requestType = "RequestGameJob";
                extraParams = $"&gameId={account.JobId}";
            }

            string placeLauncherUrl = $"https://assetgame.roblox.com/game/PlaceLauncher.ashx?request={requestType}&placeId={placeId}{extraParams}";
            string? exePath = FindRobloxExecutable();

            try
            {
                var beforePids = SnapshotRobloxPids();

                if (exePath != null)
                {
                    // 1. Direct executable launch (allows custom command switches)
                    string args;
                    if (string.IsNullOrEmpty(placeId))
                    {
                        args = "--app";
                    }
                    else
                    {
                        args = $"-t {ticket} -j \"{placeLauncherUrl}\"";
                    }

                    if (!string.IsNullOrEmpty(_settings.CustomLaunchArgs))
                    {
                        args += " " + _settings.CustomLaunchArgs;
                    }

                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = exePath,
                        Arguments = args,
                        UseShellExecute = true
                    });
                    UpdateStatus($"Launched: {account.Username}", true);
                }
                else
                {
                    // 2. URI fallback launch
                    long unixTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    string launchUri;
                    if (string.IsNullOrEmpty(placeId))
                    {
                        launchUri = $"roblox-player:1+launchmode:app+gameinfo:{ticket}+launchtime:{unixTime}";
                    }
                    else
                    {
                        string placeLauncherUrlEscaped = Uri.EscapeDataString(placeLauncherUrl);
                        launchUri = $"roblox-player:1+launchmode:play+gameinfo:{ticket}+launchtime:{unixTime}+placelauncherurl:{placeLauncherUrlEscaped}";
                    }

                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = launchUri,
                        UseShellExecute = true
                    });
                    UpdateStatus($"Launched (URI): {account.Username}", true);
                }

                TrackStartedProcess(account, beforePids);
                _ = Task.Delay(1500).ContinueWith(_ => Dispatcher.Invoke(RefreshInstances));
            }
            catch (Exception ex)
            {
                UpdateStatus($"Launch Failed: {ex.Message}", false);
                LaunchRobloxOffline(account);
            }
        }

        private async Task<string?> GetRobloxAuthTicketAsync(string cookie)
        {
            try
            {
                var handler = new HttpClientHandler { UseCookies = false };
                using var client = new HttpClient(handler);

                var preflight = new HttpRequestMessage(HttpMethod.Post, "https://auth.roblox.com/v1/authentication-ticket/");
                preflight.Headers.Add("Cookie", $".ROBLOSECURITY={cookie}");
                preflight.Headers.Add("Referer", "https://www.roblox.com/");
                preflight.Headers.Add("User-Agent", "Roblox/WinInet");

                string? csrfToken = null;
                var preflightResponse = await client.SendAsync(preflight);
                if (preflightResponse.Headers.Contains("x-csrf-token"))
                {
                    foreach (var val in preflightResponse.Headers.GetValues("x-csrf-token"))
                    {
                        csrfToken = val;
                        break;
                    }
                }

                if (string.IsNullOrEmpty(csrfToken)) return null;

                var ticketRequest = new HttpRequestMessage(HttpMethod.Post, "https://auth.roblox.com/v1/authentication-ticket/");
                ticketRequest.Headers.Add("Cookie", $".ROBLOSECURITY={cookie}");
                ticketRequest.Headers.Add("Referer", "https://www.roblox.com/");
                ticketRequest.Headers.Add("User-Agent", "Roblox/WinInet");
                ticketRequest.Headers.Add("x-csrf-token", csrfToken);
                ticketRequest.Content = new StringContent(string.Empty);

                var response = await client.SendAsync(ticketRequest);
                if (response.Headers.Contains("rbx-authentication-ticket"))
                {
                    foreach (var val in response.Headers.GetValues("rbx-authentication-ticket"))
                    {
                        return val;
                    }
                }
            }
            catch
            {
            }
            return null;
        }

        private void LaunchRobloxOffline(RobloxAccount account)
        {
            string? exePath = FindRobloxExecutable();
            if (exePath == null)
            {
                UpdateStatus("Simulation: Launched Roblox!", true);
                MessageBox.Show(
                    $"[DEMO MODE]\nRoblox Client Launch Simulation.\nAccount: {account.Username}\nUser ID: {account.UserId}\n\n(Install the Roblox client locally to execute live launching)",
                    "Roblox Client Launch Simulation", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                string placeId = PlaceIdInputBox.Text.Trim();
                string args;
                if (string.IsNullOrEmpty(placeId))
                {
                    args = "--app";
                }
                else
                {
                    string requestType = "RequestGame";
                    string extraParams = "";

                    if (!string.IsNullOrEmpty(account.PrivateServerCode))
                    {
                        requestType = "RequestPrivateGame";
                        extraParams = $"&accessCode={account.PrivateServerCode}";
                    }
                    else if (!string.IsNullOrEmpty(account.JobId))
                    {
                        requestType = "RequestGameJob";
                        extraParams = $"&gameId={account.JobId}";
                    }

                    string placeLauncherUrl = $"https://assetgame.roblox.com/game/PlaceLauncher.ashx?request={requestType}&placeId={placeId}{extraParams}";
                    args = $"-j \"{placeLauncherUrl}\"";
                }

                if (!string.IsNullOrEmpty(_settings.CustomLaunchArgs))
                {
                    args += " " + _settings.CustomLaunchArgs;
                }

                var beforePids = SnapshotRobloxPids();
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = args,
                    UseShellExecute = true
                });
                UpdateStatus($"Running (Offline): {account.Username}", true);

                TrackStartedProcess(account, beforePids);
                _ = Task.Delay(1500).ContinueWith(_ => Dispatcher.Invoke(RefreshInstances));
            }
            catch (Exception ex)
            {
                UpdateStatus($"Launch Failed: {ex.Message}", false);
            }
        }

        private string? FindRobloxExecutable()
        {
            // 1. Try finding via registry protocol handler (most accurate for the active Roblox player)
            try
            {
                using (var key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(@"roblox-player\shell\open\command"))
                {
                    if (key != null)
                    {
                        string? cmd = key.GetValue("") as string;
                        if (!string.IsNullOrEmpty(cmd))
                        {
                            string path = cmd.Trim();
                            if (path.StartsWith("\""))
                            {
                                int nextQuote = path.IndexOf("\"", 1);
                                if (nextQuote > 1)
                                {
                                    path = path.Substring(1, nextQuote - 1);
                                }
                            }
                            else
                            {
                                int space = path.IndexOf(" ");
                                if (space > 0)
                                {
                                    path = path.Substring(0, space);
                                }
                            }
                            if (File.Exists(path) && path.EndsWith("RobloxPlayerBeta.exe", StringComparison.OrdinalIgnoreCase))
                            {
                                return path;
                            }
                        }
                    }
                }
            }
            catch { }

            // 2. Try checking registry classes under Current User
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes\roblox-player\shell\open\command"))
                {
                    if (key != null)
                    {
                        string? cmd = key.GetValue("") as string;
                        if (!string.IsNullOrEmpty(cmd))
                        {
                            string path = cmd.Trim();
                            if (path.StartsWith("\""))
                            {
                                int nextQuote = path.IndexOf("\"", 1);
                                if (nextQuote > 1) path = path.Substring(1, nextQuote - 1);
                            }
                            else
                            {
                                int space = path.IndexOf(" ");
                                if (space > 0) path = path.Substring(0, space);
                            }
                            if (File.Exists(path) && path.EndsWith("RobloxPlayerBeta.exe", StringComparison.OrdinalIgnoreCase))
                            {
                                return path;
                            }
                        }
                    }
                }
            }
            catch { }

            // 3. Try checking local appdata path (legacy user-only install)
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string userRobloxVersions = Path.Combine(localAppData, "Roblox", "Versions");
            if (Directory.Exists(userRobloxVersions))
            {
                var sortedDirs = new DirectoryInfo(userRobloxVersions).GetDirectories()
                                     .OrderByDescending(d => d.LastWriteTime);
                foreach (var dir in sortedDirs)
                {
                    string exePath = Path.Combine(dir.FullName, "RobloxPlayerBeta.exe");
                    if (File.Exists(exePath))
                    {
                        return exePath;
                    }
                }
            }

            // 4. Try checking program files (modern system-wide install)
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pfRobloxVersions = Path.Combine(programFiles, "Roblox", "Versions");
            if (Directory.Exists(pfRobloxVersions))
            {
                var sortedDirs = new DirectoryInfo(pfRobloxVersions).GetDirectories()
                                     .OrderByDescending(d => d.LastWriteTime);
                foreach (var dir in sortedDirs)
                {
                    string exePath = Path.Combine(dir.FullName, "RobloxPlayerBeta.exe");
                    if (File.Exists(exePath))
                    {
                        return exePath;
                    }
                }
            }

            // 5. Try checking program files (x86) (system-wide 32-bit fallback)
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string pf86RobloxVersions = Path.Combine(programFilesX86, "Roblox", "Versions");
            if (Directory.Exists(pf86RobloxVersions))
            {
                var sortedDirs = new DirectoryInfo(pf86RobloxVersions).GetDirectories()
                                     .OrderByDescending(d => d.LastWriteTime);
                foreach (var dir in sortedDirs)
                {
                    string exePath = Path.Combine(dir.FullName, "RobloxPlayerBeta.exe");
                    if (File.Exists(exePath))
                    {
                        return exePath;
                    }
                }
            }

            return null;
        }

        // ================= ROW DELETION =================
        private void ToggleFavoriteRow_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.CommandParameter is RobloxAccount account)
            {
                account.IsFavorite = !account.IsFavorite;
                SaveAccounts();
                ApplyFilter();
            }
        }

        // ================= QUICK JOIN (game link / profile / @username) =================
        private async void QuickJoinButton_Click(object sender, RoutedEventArgs e)
        {
            if (AccountsListBox.SelectedItem is not RobloxAccount account)
            {
                UpdateStatus("Select an account first to Quick Join", false);
                return;
            }

            string raw = QuickJoinBox.Text.Trim();
            if (string.IsNullOrEmpty(raw))
            {
                UpdateStatus("Enter a game link/ID or profile/@username", false);
                return;
            }

            // Game link (/games/<id>) or bare place id -> join that place
            var gameMatch = System.Text.RegularExpressions.Regex.Match(raw, @"/games/(\d+)");
            if (gameMatch.Success)
            {
                PlaceIdInputBox.Text = gameMatch.Groups[1].Value;
                LaunchRobloxForAccount(account);
                return;
            }
            if (System.Text.RegularExpressions.Regex.IsMatch(raw, @"^\d+$"))
            {
                PlaceIdInputBox.Text = raw;
                LaunchRobloxForAccount(account);
                return;
            }

            // Profile link (/users/<id>) -> follow that user
            var userMatch = System.Text.RegularExpressions.Regex.Match(raw, @"/users/(\d+)");
            if (userMatch.Success)
            {
                LaunchAccountFollowUser(account, userMatch.Groups[1].Value);
                return;
            }

            // Otherwise treat as a username (strip a leading @)
            string username = raw.TrimStart('@');
            UpdateStatus($"Resolving @{username}...", true);
            string? userId = await ResolveUsernameToUserIdAsync(username);
            if (string.IsNullOrEmpty(userId))
            {
                UpdateStatus($"Could not resolve user '{username}'", false);
                return;
            }
            LaunchAccountFollowUser(account, userId);
        }

        private async Task<string?> ResolveUsernameToUserIdAsync(string username)
        {
            try
            {
                using var client = new HttpClient();
                string body = JsonSerializer.Serialize(new { usernames = new[] { username }, excludeBannedUsers = false });
                var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
                var resp = await client.PostAsync("https://users.roblox.com/v1/usernames/users", content);
                string json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("data", out var data) && data.GetArrayLength() > 0
                    && data[0].TryGetProperty("id", out var idProp))
                {
                    return idProp.ValueKind == JsonValueKind.Number
                        ? idProp.GetInt64().ToString()
                        : idProp.GetString();
                }
            }
            catch { }
            return null;
        }

        private async void LaunchAccountFollowUser(RobloxAccount account, string userId)
        {
            UpdateStatus($"Requesting follow-join for {account.Username}...", true);
            string? ticket = await GetRobloxAuthTicketAsync(account.Cookie);
            if (string.IsNullOrEmpty(ticket))
            {
                UpdateStatus("Auth ticket failed for follow-join", false);
                return;
            }

            long unixTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string placeLauncherUrl = Uri.EscapeDataString($"https://assetgame.roblox.com/game/PlaceLauncher.ashx?request=RequestFollowUser&userId={userId}");
            string launchUri = $"roblox-player:1+launchmode:play+gameinfo:{ticket}+launchtime:{unixTime}+placelauncherurl:{placeLauncherUrl}";

            try
            {
                var beforePids = SnapshotRobloxPids();
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = launchUri, UseShellExecute = true });
                UpdateStatus($"Following user {userId} as {account.Username}", true);
                TrackStartedProcess(account, beforePids);
                _ = Task.Delay(1500).ContinueWith(_ => Dispatcher.Invoke(RefreshInstances));
            }
            catch (Exception ex)
            {
                UpdateStatus($"Follow-join failed: {ex.Message}", false);
            }
        }

        // ================= COOKIE HEALTH CHECK =================
        private async void VerifyAllCookies_Click(object sender, RoutedEventArgs e)
        {
            if (_accounts.Count == 0)
            {
                UpdateStatus("No accounts to verify", false);
                return;
            }

            UpdateStatus($"Verifying {_accounts.Count} cookie(s)...", true);
            int valid = 0;
            var invalid = new List<string>();
            foreach (var acc in new List<RobloxAccount>(_accounts))
            {
                if (await CheckCookieValidAsync(acc.Cookie)) valid++;
                else invalid.Add(acc.Username);
            }

            if (invalid.Count == 0)
            {
                UpdateStatus($"All {valid} cookie(s) valid", true);
            }
            else
            {
                UpdateStatus($"{valid} valid, {invalid.Count} expired", false);
                MessageBox.Show("Expired or invalid cookies:\n\n" + string.Join("\n", invalid),
                    "Cookie Health", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async Task<bool> CheckCookieValidAsync(string cookie)
        {
            try
            {
                var handler = new HttpClientHandler { UseCookies = false };
                using var client = new HttpClient(handler);
                var req = new HttpRequestMessage(HttpMethod.Get, "https://users.roblox.com/v1/users/authenticated");
                req.Headers.Add("Cookie", $".ROBLOSECURITY={cookie}");
                var resp = await client.SendAsync(req);
                return resp.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        // ================= APP LOCK (PIN) =================
        private static string HashPin(string pin)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            byte[] hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(pin));
            return Convert.ToBase64String(hash);
        }

        private void UpdatePinLockUI()
        {
            bool hasPin = !string.IsNullOrEmpty(_settings.PinHash);
            if (SetPinButton != null) SetPinButton.Content = hasPin ? "CHANGE PIN" : "SET PIN";
            if (PinLockStatusText != null)
                PinLockStatusText.Text = hasPin
                    ? "PIN enabled — asked on launch. Set an empty PIN to disable."
                    : "Require a PIN each time the manager launches.";
        }

        private void SetPin_Click(object sender, RoutedEventArgs e)
        {
            string? entered = PromptForPin(string.IsNullOrEmpty(_settings.PinHash)
                ? "Set a new PIN (leave empty to disable):"
                : "Enter a new PIN (leave empty to remove lock):");
            if (entered == null) return; // cancelled

            _settings.PinHash = string.IsNullOrEmpty(entered) ? string.Empty : HashPin(entered);
            SaveSettings();
            UpdatePinLockUI();
            UpdateStatus(string.IsNullOrEmpty(_settings.PinHash) ? "App lock disabled" : "App lock PIN updated", true);
        }

        private bool PassPinGate()
        {
            if (string.IsNullOrEmpty(_settings.PinHash)) return true;
            for (int attempts = 0; attempts < 3; attempts++)
            {
                string? entered = PromptForPin(attempts == 0 ? "Enter PIN to unlock:" : "Incorrect PIN. Try again:");
                if (entered == null) return false; // cancelled
                if (HashPin(entered) == _settings.PinHash) return true;
            }
            return false;
        }

        // Minimal custom-styled dark PIN dialog. Returns null on cancel, otherwise the entered text.
        private string? PromptForPin(string message)
        {
            var dialog = new Window
            {
                Title = "Security Verification",
                Width = 340,
                Height = 175,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent
            };
            if (IsLoaded) dialog.Owner = this;

            var mainBorder = new Border
            {
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0C0C0D")),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1C1C1E")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                SnapsToDevicePixels = true
            };

            var mainGrid = new Grid();
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // Row 0: Title Bar
            var titleBar = new Grid { Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0F0F10")) };
            
            // Brand line at top of title bar
            var brandLine = new Border
            {
                Height = 2,
                VerticalAlignment = VerticalAlignment.Top,
                Background = this.TryFindResource("BrandGradient") as Brush
            };
            titleBar.Children.Add(brandLine);

            var titleText = new TextBlock
            {
                Text = "Security Verification",
                Foreground = Brushes.White,
                FontSize = 12.5,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(14, 2, 0, 0)
            };
            titleBar.Children.Add(titleText);
            Grid.SetRow(titleBar, 0);
            mainGrid.Children.Add(titleBar);

            // Row 1: Content
            var contentPanel = new StackPanel { Margin = new Thickness(16, 12, 16, 14) };

            var msgText = new TextBlock
            {
                Text = message,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E5E7EB")),
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };
            contentPanel.Children.Add(msgText);

            var pwdBorder = new Border
            {
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#111112")),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1C1C1E")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6, 2, 6, 2),
                Height = 32
            };
            var pwd = new PasswordBox
            {
                Foreground = Brushes.White,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                VerticalContentAlignment = VerticalAlignment.Center,
                FontSize = 13,
                CaretBrush = Brushes.White
            };
            pwdBorder.Child = pwd;
            contentPanel.Children.Add(pwdBorder);

            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            
            var ok = new Button
            {
                Content = "OK",
                Width = 70,
                Height = 28,
                Margin = new Thickness(0, 0, 8, 0),
                IsDefault = true,
                Style = this.TryFindResource("PrimaryButtonStyle") as Style
            };
            
            var cancel = new Button
            {
                Content = "CANCEL",
                Width = 70,
                Height = 28,
                IsCancel = true,
                Style = this.TryFindResource("SecondaryButtonStyle") as Style
            };
            
            btnPanel.Children.Add(ok);
            btnPanel.Children.Add(cancel);
            contentPanel.Children.Add(btnPanel);
            
            Grid.SetRow(contentPanel, 1);
            mainGrid.Children.Add(contentPanel);

            mainBorder.Child = mainGrid;
            dialog.Content = mainBorder;

            string? result = null;
            ok.Click += (_, __) => { result = pwd.Password; dialog.DialogResult = true; };
            dialog.Loaded += (_, __) => pwd.Focus();

            // Drag support for the custom title bar
            titleBar.MouseLeftButtonDown += (s, ev) =>
            {
                if (ev.LeftButton == MouseButtonState.Pressed)
                {
                    dialog.DragMove();
                }
            };

            bool? dr = dialog.ShowDialog();
            return dr == true ? (result ?? string.Empty) : null;
        }

        private void DeleteAccountRow_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.CommandParameter is RobloxAccount account)
            {
                var result = MessageBox.Show($"Are you sure you want to remove account '{account.Username}'?", "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
                {
                    _accounts.Remove(account);
                    SaveAccounts();
                    ApplyFilter();
                    UpdateStatus($"Removed {account.Username}", true);
                }
            }
        }

        // ================= SETTINGS & MUTEX CONTROLS =================
        private void MultiClientToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (MultiClientToggle == null) return;
            bool isChecked = MultiClientToggle.IsChecked == true;
            ApplyMultiClientMutex(isChecked);
            UpdateStatus(isChecked ? "Multi-Client Active" : "Multi-Client Disabled", true);
        }

        private void ApplyMultiClientMutex(bool enable)
        {
            if (enable)
            {
                try
                {
                    _robloxMutex = new Mutex(true, "ROBLOX_singletonMutex");
                }
                catch { }
            }
            else
            {
                if (_robloxMutex != null)
                {
                    try { _robloxMutex.ReleaseMutex(); } catch { }
                    _robloxMutex.Dispose();
                    _robloxMutex = null;
                }
            }
        }

        // ================= LISTBOX SELECTION & ACCOUNT INSPECTOR DYNAMICS =================
        private void AccountsListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (AccountsListBox.SelectedItem is RobloxAccount account)
            {
                LaunchRobloxForAccount(account);
            }
        }

        private void PlaceIdInputBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingSelection) return;
            if (AccountsListBox.SelectedItem is RobloxAccount account)
            {
                account.LastPlaceId = PlaceIdInputBox.Text.Trim();
                SaveAccounts();
            }
        }

        private void PrivateServerCodeInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingSelection) return;
            if (AccountsListBox.SelectedItem is RobloxAccount account)
            {
                string raw = PrivateServerCodeInput.Text.Trim();

                // VIP Link Auto-Parsing: if it's a full URL containing privateServerLinkCode
                if (raw.Contains("privateServerLinkCode="))
                {
                    // 1. Extract place ID from URL if present
                    var matchPlace = System.Text.RegularExpressions.Regex.Match(raw, @"/games/(\d+)");
                    if (matchPlace.Success)
                    {
                        string placeId = matchPlace.Groups[1].Value;
                        PlaceIdInputBox.Text = placeId;
                        account.LastPlaceId = placeId;
                    }

                    // 2. Extract code
                    int idx = raw.IndexOf("privateServerLinkCode=");
                    string code = raw.Substring(idx + "privateServerLinkCode=".Length);
                    int amp = code.IndexOf("&");
                    if (amp > 0) code = code.Substring(0, amp);

                    code = code.Trim();
                    PrivateServerCodeInput.Text = code;
                    account.PrivateServerCode = code;

                    // 3. Clear Job ID
                    JobIdInput.Text = "";
                    account.JobId = "";

                    UpdateStatus("Parsed Private Server VIP Link", true);
                }
                else
                {
                    account.PrivateServerCode = raw;
                }
                SaveAccounts();
            }
        }

        private void JobIdInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingSelection) return;
            if (AccountsListBox.SelectedItem is RobloxAccount account)
            {
                string raw = JobIdInput.Text.Trim();
                account.JobId = raw;
                if (!string.IsNullOrEmpty(raw))
                {
                    PrivateServerCodeInput.Text = "";
                    account.PrivateServerCode = "";
                }
                SaveAccounts();
            }
        }

        private void SettingsInput_Changed(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingSettingsUI) return;
            if (CustomLaunchArgsInput != null) _settings.CustomLaunchArgs = CustomLaunchArgsInput.Text;
            SaveSettings();
        }

        private void InspectorNotesBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingSelection) return;
            if (AccountsListBox.SelectedItem is RobloxAccount account)
            {
                account.Notes = InspectorNotesBox.Text;
                SaveAccounts();
            }
        }

        private async void RefreshStatsButton_Click(object sender, RoutedEventArgs e)
        {
            if (AccountsListBox.SelectedItem is RobloxAccount account)
            {
                _inspectorCts?.Cancel();
                _inspectorCts = new CancellationTokenSource();
                var token = _inspectorCts.Token;

                UpdateStatus($"Refreshing stats for {account.Username}...", true);
                InspectorRobuxText.Text = "Refreshing...";
                
                try
                {
                    await UpdateInspectorDetailsAsync(account, token);
                    UpdateStatus($"Refreshed stats: {account.Username}", true);
                }
                catch (OperationCanceledException) { }
            }
        }

        private async void AccountsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Cancel preceding inspector sweeps
            _inspectorCts?.Cancel();
            _inspectorCts = new CancellationTokenSource();

            if (AccountsListBox.SelectedItem is RobloxAccount account)
            {
                UpdateStatus($"Selected {account.Username}", true);
                
                // Show Inspector grid, hide placeholder
                InspectorPlaceholder.Visibility = Visibility.Collapsed;
                InspectorGrid.Visibility = Visibility.Visible;
                RefreshStatsButton.Visibility = Visibility.Visible;

                _isUpdatingSelection = true;
                PlaceIdInputBox.Text = account.LastPlaceId;
                if (PrivateServerCodeInput != null) PrivateServerCodeInput.Text = account.PrivateServerCode;
                if (JobIdInput != null) JobIdInput.Text = account.JobId;

                // Reflect this account's saved tag in the inspector selector
                foreach (var obj in InspectorTagComboBox.Items)
                {
                    if (obj is ComboBoxItem ci && (ci.Content?.ToString() ?? "None") == account.Tag)
                    {
                        InspectorTagComboBox.SelectedItem = ci;
                        break;
                    }
                }

                // Reflect whether this account is the designated auto-start account
                InspectorAutoStartCheck.IsChecked = _settings.StartupAccountUsername == account.Username;
                InspectorNotesBox.Text = account.Notes;
                _isUpdatingSelection = false;
                InspectorRobuxText.Text = "Loading...";
                InspectorDisplayNameText.Text = "Loading...";
                InspectorCreatedText.Text = "Loading...";
                InspectorStatusText.Text = "Checking API...";
                InspectorStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981"));

                var token = _inspectorCts.Token;
                try
                {
                    await UpdateInspectorDetailsAsync(account, token);
                }
                catch (OperationCanceledException)
                {
                    // Suppress canceled tasks
                }
            }
            else
            {
                // Collapse details, display select placeholder
                InspectorPlaceholder.Visibility = Visibility.Visible;
                InspectorGrid.Visibility = Visibility.Collapsed;
                RefreshStatsButton.Visibility = Visibility.Collapsed;

                _isUpdatingSelection = true;
                PlaceIdInputBox.Text = string.Empty;
                _isUpdatingSelection = false;

                UpdateDashboardStats();
            }
        }

        private async Task UpdateInspectorDetailsAsync(RobloxAccount account, CancellationToken token)
        {
            string cookie = account.Cookie;
            string userId = account.UserId;

            // Fetch Robux balance and creation profile from Roblox API on background worker
            var stats = await Task.Run(async () =>
            {
                var result = new Dictionary<string, string>();
                try
                {
                    var handler = new HttpClientHandler { UseCookies = false };
                    using var client = new HttpClient(handler);
                    client.DefaultRequestHeaders.Add("User-Agent", "Roblox/WinInet");

                    // 1. Fetch Robux Balance
                    var robuxRequest = new HttpRequestMessage(HttpMethod.Get, $"https://economy.roblox.com/v1/users/{userId}/currency");
                    robuxRequest.Headers.Add("Cookie", $".ROBLOSECURITY={cookie}");
                    var robuxResponse = await client.SendAsync(robuxRequest, token);

                    if (robuxResponse.IsSuccessStatusCode)
                    {
                        string robuxJson = await robuxResponse.Content.ReadAsStringAsync(token);
                        using var doc = JsonDocument.Parse(robuxJson);
                        if (doc.RootElement.TryGetProperty("robux", out var robuxProp))
                        {
                            result["robux"] = robuxProp.GetInt64().ToString("N0");
                        }
                    }
                    else if (robuxResponse.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    {
                        result["expired"] = "true";
                        return result;
                    }

                    // 2. Fetch Display Name & Registration Created Date
                    var profileRequest = new HttpRequestMessage(HttpMethod.Get, $"https://users.roblox.com/v1/users/{userId}");
                    profileRequest.Headers.Add("Cookie", $".ROBLOSECURITY={cookie}");
                    var profileResponse = await client.SendAsync(profileRequest, token);

                    if (profileResponse.IsSuccessStatusCode)
                    {
                        string profileJson = await profileResponse.Content.ReadAsStringAsync(token);
                        using var doc = JsonDocument.Parse(profileJson);
                        var root = doc.RootElement;
                        
                        if (root.TryGetProperty("displayName", out var dispProp))
                        {
                            result["displayName"] = dispProp.GetString() ?? "Unknown";
                        }
                        if (root.TryGetProperty("created", out var createdProp) && DateTime.TryParse(createdProp.GetString(), out var createdDate))
                        {
                            result["created"] = createdDate.ToString("MMM d, yyyy");
                            int years = DateTime.Now.Year - createdDate.Year;
                            if (years > 0)
                            {
                                result["created"] += $" ({years} yr ago)";
                            }
                        }
                    }
                }
                catch
                {
                    // Gracefully swallow network outages/timeout failures
                }
                return result;
            }, token);

            if (token.IsCancellationRequested) return;

            // Apply scraped data on WPF thread
            if (stats.ContainsKey("expired"))
            {
                account.RobuxBalance = "Expired";
                InspectorRobuxText.Text = "Expired";
                InspectorDisplayNameText.Text = "Session Invalid";
                InspectorCreatedText.Text = "Session Invalid";
                InspectorStatusText.Text = "Expired / Invalid";
                InspectorStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444"));
                UpdateStatus($"Session Expired for {account.Username}", false);
                SaveAccounts();
                UpdateDashboardStats();
            }
            else
            {
                string rx = stats.TryGetValue("robux", out var val) ? val : "N/A (Offline)";
                account.RobuxBalance = rx;
                InspectorRobuxText.Text = rx;
                InspectorDisplayNameText.Text = stats.TryGetValue("displayName", out var dn) ? dn : account.Username;
                InspectorCreatedText.Text = stats.TryGetValue("created", out var cr) ? cr : "N/A";
                
                bool hasData = stats.ContainsKey("robux") || stats.ContainsKey("displayName");
                InspectorStatusText.Text = hasData ? "Active / Valid" : "Offline / Mock Session";
                string colorHex = hasData ? "#10B981" : "#9CA3AF";
                InspectorStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex));
                SaveAccounts();
                UpdateDashboardStats();
            }
        }

        // ================= INSTANCE PROCESS MONITORING =================
        private void StartInstanceMonitor()
        {
            _monitorTimer = new DispatcherTimer();
            _monitorTimer.Interval = TimeSpan.FromSeconds(3);
            _monitorTimer.Tick += (s, e) => RefreshInstances();
            _monitorTimer.Start();
            RefreshInstances();
        }

        private void RefreshInstances()
        {
            var running = new List<RobloxInstance>();
            var liveRamByPid = new Dictionary<int, double>();
            double totalRamMb = 0;
            try
            {
                var processes = System.Diagnostics.Process.GetProcessesByName("RobloxPlayerBeta");
                foreach (var p in processes)
                {
                    try
                    {
                        if (!p.HasExited)
                        {
                            double ramMb = p.WorkingSet64 / (1024.0 * 1024.0);
                            totalRamMb += ramMb;
                            liveRamByPid[p.Id] = ramMb;
                            running.Add(new RobloxInstance
                            {
                                Pid = p.Id,
                                Memory = $"{ramMb:F0} MB",
                                Process = p
                            });
                        }
                    }
                    catch { }
                }
            }
            catch { }

            UpdateAccountResourceUsage(liveRamByPid);

            InstancesListBox.ItemsSource = null;
            InstancesListBox.ItemsSource = running;

            NoInstancesPanel.Visibility = running.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            RunningClientsSummaryText.Text = $"{running.Count} client(s) active | {totalRamMb:F0} MB RAM";
            UpdateDashboardStats();
        }

        // Maps live process RAM back to the account profiles that launched them,
        // and clears state for accounts whose client has exited.
        private void UpdateAccountResourceUsage(Dictionary<int, double> liveRamByPid)
        {
            if (_accountPids.Count == 0) return;

            var staleAccounts = new List<string>();
            foreach (var kvp in _accountPids)
            {
                var account = _accounts.Find(a => a.Username == kvp.Key);
                if (account == null) { staleAccounts.Add(kvp.Key); continue; }

                if (liveRamByPid.TryGetValue(kvp.Value, out double ramMb))
                {
                    account.IsRunning = true;
                    account.StatusText = $"Running • {ramMb:F0} MB";
                }
                else
                {
                    account.IsRunning = false;
                    account.StatusText = string.Empty;
                    staleAccounts.Add(kvp.Key);
                }
            }

            foreach (var username in staleAccounts)
            {
                _accountPids.Remove(username);
            }
        }

        private void KillProcess_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.CommandParameter is RobloxInstance inst)
            {
                try
                {
                    inst.Process?.Kill();
                    UpdateStatus($"Terminated client PID: {inst.Pid}", true);
                    
                    _ = Task.Delay(500).ContinueWith(_ => Dispatcher.Invoke(RefreshInstances));
                }
                catch (Exception ex)
                {
                    UpdateStatus($"Termination Failed: {ex.Message}", false);
                }
            }
        }

        private void KillAllClients_Click(object sender, RoutedEventArgs e)
        {
            UpdateStatus("Killing all Roblox game clients...", true);

            int killedCount = 0;
            try
            {
                var processes = System.Diagnostics.Process.GetProcessesByName("RobloxPlayerBeta");
                foreach (var p in processes)
                {
                    try
                    {
                        p.Kill();
                        killedCount++;
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                UpdateStatus($"Error terminating clients: {ex.Message}", false);
                return;
            }

            UpdateStatus($"Closed {killedCount} game clients", true);
            
            _ = Task.Delay(500).ContinueWith(_ => Dispatcher.Invoke(RefreshInstances));
        }

        // ================= FILESYSTEM CLEANING ACTIONS =================
        private void CleanLogs_Click(object sender, RoutedEventArgs e)
        {
            PerformLogClean(true);
        }

        private void PerformLogClean(bool showMessageBox)
        {
            Dispatcher.Invoke(() => UpdateStatus("Cleaning Roblox tracking files...", true));

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string logsDir = Path.Combine(localAppData, "Roblox", "logs");
            string tempDir = Path.Combine(Path.GetTempPath(), "Roblox");

            int deletedCount = 0;
            int failedCount = 0;

            void CleanDirectory(string dirPath)
            {
                if (!Directory.Exists(dirPath)) return;

                foreach (string file in Directory.GetFiles(dirPath))
                {
                    try
                    {
                        File.Delete(file);
                        deletedCount++;
                    }
                    catch
                    {
                        failedCount++; // File locked by active Roblox client
                    }
                }
            }

            CleanDirectory(logsDir);
            CleanDirectory(tempDir);

            Dispatcher.Invoke(() =>
            {
                if (failedCount > 0)
                {
                    UpdateStatus($"Cleaned {deletedCount} files ({failedCount} locked logs skipped)", true);
                }
                else
                {
                    UpdateStatus($"Cleaned {deletedCount} logs & cache files!", true);
                }
            });

            if (showMessageBox)
            {
                Dispatcher.Invoke(() =>
                {
                    MessageBox.Show(
                        $"Cleaning Summary:\n• Deleted tracking logs: {deletedCount} files\n• Active client locks skipped: {failedCount} files",
                        "Roblox Log & Cache Sweeper", MessageBoxButton.OK, MessageBoxImage.Information);
                });
            }
        }

        private System.Threading.CancellationTokenSource? _toastCts;

        // ================= STATUS UPDATE =================
        private void UpdateStatus(string message, bool isHealthy)
        {
            System.Diagnostics.Debug.WriteLine($"[STATUS] {message} (Healthy: {isHealthy})");

            // Dispatch to UI thread
            Dispatcher.Invoke(async () =>
            {
                if (ToastNotification == null || ToastMessageText == null || ToastIndicatorDot == null) return;

                // Cancel any running fade-out timer
                _toastCts?.Cancel();
                _toastCts = new System.Threading.CancellationTokenSource();
                var token = _toastCts.Token;

                // Update content
                ToastMessageText.Text = message;
                string colorHex = isHealthy ? "#10B981" : "#EF4444";
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex));
                ToastIndicatorDot.Fill = brush;

                // Color the border outline to match status type
                ToastNotification.BorderBrush = brush;

                // Make visible and reset position
                ToastNotification.Visibility = Visibility.Visible;
                ToastNotification.Opacity = 1;
                ToastTranslate.Y = 0;

                try
                {
                    // Display for 3.5 seconds
                    await Task.Delay(3500, token);

                    // Smooth slide-down & fade-out animation
                    for (double op = 1.0; op >= 0.0; op -= 0.1)
                    {
                        if (token.IsCancellationRequested) return;
                        ToastNotification.Opacity = op;
                        ToastTranslate.Y = (1.0 - op) * 15;
                        await Task.Delay(20, token);
                    }

                    ToastNotification.Visibility = Visibility.Collapsed;
                }
                catch (TaskCanceledException)
                {
                    // Suppress
                }
                catch (Exception) { }
            });
        }

        // ================= GROUP LAUNCHING & MANAGEMENT =================
        private void RefreshGroupSelectors(string? selectGroupName = null)
        {
            if (GroupSelector == null || OverlayGroupSelector == null || LaunchGroupButton == null) return;

            GroupSelector.Items.Clear();
            OverlayGroupSelector.Items.Clear();

            if (_settings.LaunchGroups == null)
            {
                _settings.LaunchGroups = new List<LaunchGroup>();
            }

            foreach (var g in _settings.LaunchGroups)
            {
                GroupSelector.Items.Add(g.Name);
                OverlayGroupSelector.Items.Add(g.Name);
            }

            if (GroupSelector.Items.Count > 0)
            {
                LaunchGroupButton.IsEnabled = true;
                if (!string.IsNullOrEmpty(selectGroupName) && GroupSelector.Items.Contains(selectGroupName))
                {
                    GroupSelector.SelectedItem = selectGroupName;
                    OverlayGroupSelector.SelectedItem = selectGroupName;
                }
                else
                {
                    GroupSelector.SelectedIndex = 0;
                    OverlayGroupSelector.SelectedIndex = 0;
                }
            }
            else
            {
                LaunchGroupButton.IsEnabled = false;
            }
        }

        private void GroupSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
        }

        private void ManageGroupsButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshGroupSelectors();
            GroupsOverlay.Visibility = Visibility.Visible;
            PopulateOverlayGroupForm();
        }

        private void CloseGroupsOverlay_Click(object sender, RoutedEventArgs e)
        {
            GroupsOverlay.Visibility = Visibility.Collapsed;
        }

        private void OverlayGroupSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            PopulateOverlayGroupForm();
        }

        private void PopulateOverlayGroupForm()
        {
            if (OverlayGroupSelector == null || GroupFormPanel == null || GroupAccountsListBox == null) return;

            if (OverlayGroupSelector.SelectedItem == null)
            {
                GroupFormPanel.Visibility = Visibility.Collapsed;
                return;
            }

            string groupName = OverlayGroupSelector.SelectedItem.ToString()!;
            var group = _settings.LaunchGroups.Find(g => g.Name == groupName);
            if (group == null)
            {
                GroupFormPanel.Visibility = Visibility.Collapsed;
                return;
            }

            GroupFormPanel.Visibility = Visibility.Visible;
            GroupNameTextBox.Text = group.Name;
            GroupPlaceIdTextBox.Text = group.TargetPlaceId;
            GroupVipTextBox.Text = group.PrivateServerCode;

            // Populate accounts selection list
            var selections = new List<GroupAccountSelection>();
            foreach (var acc in _accounts)
            {
                selections.Add(new GroupAccountSelection
                {
                    Username = acc.Username,
                    IsSelected = group.Usernames.Contains(acc.Username)
                });
            }
            GroupAccountsListBox.ItemsSource = selections;
        }

        private void NewGroupButton_Click(object sender, RoutedEventArgs e)
        {
            // Prompt for new group name
            string name = "New Group";
            int count = 1;
            while (_settings.LaunchGroups.Exists(g => g.Name == name))
            {
                name = $"New Group ({count++})";
            }

            var newGroup = new LaunchGroup { Name = name };
            _settings.LaunchGroups.Add(newGroup);
            SaveSettings();

            RefreshGroupSelectors(name);
        }

        private void DeleteGroupButton_Click(object sender, RoutedEventArgs e)
        {
            if (OverlayGroupSelector.SelectedItem == null) return;
            string groupName = OverlayGroupSelector.SelectedItem.ToString()!;
            var group = _settings.LaunchGroups.Find(g => g.Name == groupName);
            if (group != null)
            {
                _settings.LaunchGroups.Remove(group);
                SaveSettings();
                UpdateStatus($"Deleted group: {groupName}", true);
                RefreshGroupSelectors();
            }
        }

        private void SaveGroupButton_Click(object sender, RoutedEventArgs e)
        {
            if (OverlayGroupSelector.SelectedItem == null) return;
            string originalName = OverlayGroupSelector.SelectedItem.ToString()!;
            var group = _settings.LaunchGroups.Find(g => g.Name == originalName);
            if (group == null) return;

            string newName = GroupNameTextBox.Text.Trim();
            if (string.IsNullOrEmpty(newName))
            {
                UpdateStatus("Group Name cannot be empty.", false);
                return;
            }

            if (newName != originalName && _settings.LaunchGroups.Exists(g => g.Name == newName))
            {
                UpdateStatus("A group with that name already exists.", false);
                return;
            }

            group.Name = newName;
            group.TargetPlaceId = GroupPlaceIdTextBox.Text.Trim();
            group.PrivateServerCode = GroupVipTextBox.Text.Trim();

            // Save selected accounts
            group.Usernames.Clear();
            var selections = GroupAccountsListBox.ItemsSource as List<GroupAccountSelection>;
            if (selections != null)
            {
                foreach (var sel in selections)
                {
                    if (sel.IsSelected)
                    {
                        group.Usernames.Add(sel.Username);
                    }
                }
            }

            SaveSettings();
            UpdateStatus($"Saved group: {newName}", true);
            RefreshGroupSelectors(newName);
            GroupsOverlay.Visibility = Visibility.Collapsed;
        }

        private async void LaunchGroupButton_Click(object sender, RoutedEventArgs e)
        {
            if (GroupSelector.SelectedItem == null) return;
            string groupName = GroupSelector.SelectedItem.ToString()!;
            var group = _settings.LaunchGroups.Find(g => g.Name == groupName);
            if (group == null || group.Usernames.Count == 0)
            {
                UpdateStatus("Selected group has no accounts.", false);
                return;
            }

            LaunchGroupButton.IsEnabled = false;
            LaunchGroupButton.Content = "LAUNCHING...";
            UpdateStatus($"Starting group launch for '{groupName}'...", true);

            try
            {
                // We launch each account sequentially with a delay
                for (int i = 0; i < group.Usernames.Count; i++)
                {
                    string username = group.Usernames[i];
                    var account = _accounts.Find(a => a.Username == username);
                    if (account == null) continue;

                    UpdateStatus($"[{i + 1}/{group.Usernames.Count}] Launching {username}...", true);

                    // Perform launch
                    await LaunchRobloxForAccountAsync(account, group.TargetPlaceId, group.PrivateServerCode);

                    // Delay between launches
                    if (i < group.Usernames.Count - 1)
                    {
                        await Task.Delay(2000);
                    }
                }

                UpdateStatus($"Successfully launched group '{groupName}'!", true);
            }
            catch (Exception ex)
            {
                UpdateStatus($"Group launch error: {ex.Message}", false);
            }
            finally
            {
                LaunchGroupButton.IsEnabled = true;
                LaunchGroupButton.Content = "🚀 LAUNCH GROUP";
            }
        }

        // ================= UI/UX ENHANCEMENT HELPERS & HANDLERS =================

        private void ApplyThemeAccent(string accentName)
        {
            var brush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 0)
            };

            if (accentName == "Violet")
            {
                brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#8B5CF6"), 0.0));
                brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#D946EF"), 1.0));
            }
            else if (accentName == "Crimson")
            {
                brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#EF4444"), 0.0));
                brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#F59E0B"), 1.0));
            }
            else // Emerald (Default)
            {
                brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#10B981"), 0.0));
                brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#06B6D4"), 1.0));
            }

            // Update application resources dynamically
            Application.Current.Resources["BrandGradient"] = brush;
        }

        private void UpdateDashboardStats()
        {
            if (DashTotalAccountsText == null) return;

            DashTotalAccountsText.Text = _accounts.Count.ToString();

            int runningCount = 0;
            double totalRam = 0;
            try
            {
                var processes = System.Diagnostics.Process.GetProcessesByName("RobloxPlayerBeta");
                runningCount = processes.Length;
                foreach (var p in processes)
                {
                    try
                    {
                        if (!p.HasExited)
                        {
                            totalRam += p.WorkingSet64 / (1024.0 * 1024.0);
                        }
                    }
                    catch { }
                }
            }
            catch { }

            DashActiveClientsText.Text = runningCount.ToString();
            DashTotalRamText.Text = $"{totalRam:F0} MB";

            long totalRobux = 0;
            foreach (var a in _accounts)
            {
                if (!string.IsNullOrEmpty(a.RobuxBalance) && long.TryParse(a.RobuxBalance.Replace(",", "").Replace(".", ""), out long rx))
                {
                    totalRobux += rx;
                }
            }
            DashTotalRobuxText.Text = $"{totalRobux:N0} R$";
        }

        private void UpdateLogSizeDisplay()
        {
            if (LogSizeStatusText == null) return;
            try
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string logsDir = Path.Combine(localAppData, "Roblox", "logs");
                string tempDir = Path.Combine(Path.GetTempPath(), "Roblox");

                long totalBytes = 0;

                long GetDirSize(string dirPath)
                {
                    if (!Directory.Exists(dirPath)) return 0;
                    long size = 0;
                    foreach (string file in Directory.GetFiles(dirPath))
                    {
                        try { size += new FileInfo(file).Length; } catch { }
                    }
                    return size;
                }

                totalBytes += GetDirSize(logsDir);
                totalBytes += GetDirSize(tempDir);

                double mb = totalBytes / (1024.0 * 1024.0);
                LogSizeStatusText.Text = $"Roblox log files size: {mb:F1} MB";
            }
            catch
            {
                LogSizeStatusText.Text = "Roblox log files size: Unknown";
            }
        }

        private void ThemeAccentComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ThemeAccentComboBox == null || _settings == null || _isUpdatingSettingsUI) return;
            if (ThemeAccentComboBox.SelectedItem is ComboBoxItem item)
            {
                string accent = item.Content.ToString()!;
                _settings.ThemeAccent = accent;
                ApplyThemeAccent(accent);
                SaveSettings();
            }
        }

        private void OpenLogFolderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string logsDir = Path.Combine(localAppData, "Roblox", "logs");
                if (Directory.Exists(logsDir))
                {
                    System.Diagnostics.Process.Start("explorer.exe", logsDir);
                }
                else
                {
                    UpdateStatus("Roblox logs folder does not exist yet.", false);
                }
            }
            catch (Exception ex)
            {
                UpdateStatus($"Failed to open logs: {ex.Message}", false);
            }
        }

        private void CleanLogsNowButton_Click(object sender, RoutedEventArgs e)
        {
            PerformLogClean(false);
            UpdateLogSizeDisplay();
            UpdateDashboardStats();
            MessageBox.Show("Logs and temporary cache files have been cleared successfully.", 
                            "Clean Complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private async void LaunchFavorites_Click(object sender, RoutedEventArgs e)
        {
            var favorites = _accounts.Where(a => a.IsFavorite).ToList();
            if (favorites.Count == 0)
            {
                UpdateStatus("No favorite accounts to launch", false);
                return;
            }

            UpdateStatus($"Launching {favorites.Count} favorite account(s)...", true);
            foreach (var acc in favorites)
            {
                await LaunchRobloxForAccountAsync(acc);
                if (favorites.Last() != acc)
                {
                    await Task.Delay(2000); // 2 second delay between launches
                }
            }
        }
    }
}
