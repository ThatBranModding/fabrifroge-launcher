using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CmlLib.Core.Auth;

// IMPORTANT: only alias what we need from WinForms to avoid MessageBox conflicts
using FolderBrowserDialog = System.Windows.Forms.FolderBrowserDialog;
using WinFormsDialogResult = System.Windows.Forms.DialogResult;

namespace FabriFrogeLauncher;

public partial class MainWindow : Window
{
    private const string LatestUrl = "https://files.fabrifroge.com/launcher/latest.json";
    private CancellationTokenSource? _cts;

    private MSession? _session;

    private string _progressMode = "";
    private string _lastPackId = "";
    private bool _didAutoCheck = false;

    // Console default hidden
    private bool _consoleVisible = false;

    // Background WebP
    private readonly WebpBackgroundPlayer _bgPlayer = new();
    private CancellationTokenSource? _bgCts;

    // One reusable bitmap for UI thread updates
    private WriteableBitmap? _bgWb;

    // Coalesce UI updates (avoid Dispatcher spam)
    private byte[]? _pendingPixels;
    private int _pendingW;
    private int _pendingH;
    private int _bgUiScheduled = 0;

    private LauncherSettings _settings = new();
    private bool _ramInitDone = false;

    // Default RAM when settings missing
    private const int DefaultRamMb = 10240; // 10GB

    // -------------------------
    // HARD LOCKOUT (launcher update vs modpack update vs play)
    // -------------------------
    private int _launcherUpdateInProgress = 0; // 0/1
    private int _packOpInProgress = 0;         // 0/1

    private bool IsLauncherUpdateRunning => Interlocked.CompareExchange(ref _launcherUpdateInProgress, 0, 0) == 1;
    private bool IsPackOpRunning => Interlocked.CompareExchange(ref _packOpInProgress, 0, 0) == 1;

    private bool TryBeginLauncherUpdate()
    {
        // Never allow launcher update if a pack op is running.
        if (IsPackOpRunning) return false;
        return Interlocked.CompareExchange(ref _launcherUpdateInProgress, 1, 0) == 0;
    }

    private void EndLauncherUpdate()
        => Interlocked.Exchange(ref _launcherUpdateInProgress, 0);

    private bool TryBeginPackOp()
    {
        // Never allow pack operations if launcher update flow is running.
        if (IsLauncherUpdateRunning) return false;
        return Interlocked.CompareExchange(ref _packOpInProgress, 1, 0) == 0;
    }

    private void EndPackOp()
        => Interlocked.Exchange(ref _packOpInProgress, 0);

    // -------------------------
    // Background pause/resume when not in view
    // -------------------------
    private bool _bgShouldRun = false;

    public MainWindow()
    {
        InitializeComponent();

        InstallPathBox.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FabriFroge"
        );

        // Start offline by default; we will restore cached session (if any) WITHOUT validating it here.
        _session = MSession.CreateOfflineSession(Environment.UserName);
        SignedInAsText.Text = "(offline)";

        InstalledVersionText.Text = "(none)";
        LatestVersionText.Text = "(unknown)";
        ProgressText.Text = "";

        JavaRamStatusText.Text = "Java: Temurin 21";
        RamStatusText.Text = "RAM: (checking)";

        // Ensure console starts hidden AND translated off-screen
        SetConsoleVisible(false);

        // RAM UI init (populates & loads settings)
        InitRamSelector();
        LoadSettingsAndApply();

        // Pause/resume background depending on visibility/focus/minimized
        StateChanged += (_, __) => UpdateBackgroundPlayback();
        Activated += (_, __) => UpdateBackgroundPlayback();
        Deactivated += (_, __) => UpdateBackgroundPlayback();
        IsVisibleChanged += (_, __) => UpdateBackgroundPlayback();
    }

    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        // Background: start only if actually visible/active
        UpdateBackgroundPlayback();

        // Restore cached session on startup, but DO NOT validate token here.
        await TryRestoreSessionOnStartupAsync();

        // ✅ Run self-updater FIRST (it holds a lock while it evaluates/prompts/applies)
        // so the pack auto-check won't early-return due to IsLauncherUpdateRunning.
        await CheckLauncherSelfUpdateAsync();

        // ✅ Now safely check pack installed/latest on launch.
        if (!_didAutoCheck)
        {
            _didAutoCheck = true;
            _ = AutoCheckForUpdatesAsync();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        try { _bgCts?.Cancel(); } catch { }
        try { _bgCts?.Dispose(); } catch { }
        _bgCts = null;

        try { _bgPlayer.Stop(); } catch { }
        try { _cts?.Cancel(); } catch { }

        base.OnClosed(e);
    }

    // -------------------------
    // Titlebar window controls
    // -------------------------
    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = (WindowState == WindowState.Maximized) ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        try { DragMove(); } catch { /* ignore */ }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e)
        => WindowState = (WindowState == WindowState.Maximized) ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // -------------------------
    // Play menu (hamburger)
    // -------------------------
    private void PlayMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (PlayMenuPopup == null) return;
        PlayMenuPopup.IsOpen = !PlayMenuPopup.IsOpen;
    }

    // -------------------------
    // Console toggle
    // -------------------------
    private void ToggleConsoleButton_Click(object sender, RoutedEventArgs e)
        => SetConsoleVisible(!_consoleVisible);

    private void SetConsoleVisible(bool visible)
    {
        _consoleVisible = visible;

        if (ConsoleDrawer != null)
            ConsoleDrawer.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

        if (ConsolePanel != null)
            ConsolePanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

        // If the drawer is translated off-screen, Visibility alone won’t show it.
        if (ConsoleDrawerTransform != null)
            ConsoleDrawerTransform.X = visible ? 0 : 540;

        if (ToggleConsoleButton != null)
            ToggleConsoleButton.Content = visible ? "Hide Console" : "Show Console";
    }

    // -------------------------
    // RAM selector + persistence
    // -------------------------
    private void InitRamSelector()
    {
        if (RamComboBox == null) return;

        var ramOptions = new[]
        {
            2048, 3072, 4096, 6144, 8192, 10240, 12288, 16384, 20480, 24576, 32768
        };

        RamComboBox.Items.Clear();
        foreach (var mb in ramOptions)
        {
            var label = mb >= 1024
                ? $"{mb / 1024.0:0.#} GB"
                : $"{mb} MB";

            RamComboBox.Items.Add(new ComboBoxRamItem(mb, label));
        }

        _ramInitDone = true;
    }

    private void LoadSettingsAndApply()
    {
        var installRoot = InstallPathBox.Text ?? "";
        _settings = LauncherSettings.Load(installRoot, Log);

        if (_settings.RamMb <= 0)
            _settings.RamMb = DefaultRamMb;

        ApplyRamToUi(_settings.RamMb);

        if (RamComboBox != null)
        {
            _ramInitDone = false;

            var match = RamComboBox.Items
                .OfType<ComboBoxRamItem>()
                .FirstOrDefault(i => i.Mb == _settings.RamMb);

            if (match != null)
                RamComboBox.SelectedItem = match;
            else
                RamComboBox.SelectedItem =
                    RamComboBox.Items.OfType<ComboBoxRamItem>().FirstOrDefault(i => i.Mb == DefaultRamMb)
                    ?? RamComboBox.Items.OfType<ComboBoxRamItem>().FirstOrDefault();

            _ramInitDone = true;
        }

        SaveSettings();
    }

    private void SaveSettings()
    {
        var installRoot = InstallPathBox.Text ?? "";
        _settings.Save(installRoot, Log);
    }

    private void ApplyRamToUi(int ramMb)
    {
        if (RamStatusText != null)
            RamStatusText.Text = $"RAM: {ramMb} MB";
    }

    private void RamComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ramInitDone) return;
        if (RamComboBox?.SelectedItem is not ComboBoxRamItem item) return;

        _settings.RamMb = item.Mb;
        ApplyRamToUi(_settings.RamMb);
        SaveSettings();
    }

    private sealed class ComboBoxRamItem
    {
        public int Mb { get; }
        public string Label { get; }

        public ComboBoxRamItem(int mb, string label)
        {
            Mb = mb;
            Label = label;
        }

        public override string ToString() => Label;
    }

    // -------------------------
    // Background WebP (pause/resume)
    // -------------------------
    private bool IsLauncherInView()
    {
        if (!IsVisible) return false;
        if (WindowState == WindowState.Minimized) return false;
        if (!IsActive) return false;
        return true;
    }

    private void UpdateBackgroundPlayback()
    {
        bool shouldRun = IsLauncherInView();
        if (shouldRun == _bgShouldRun)
            return;

        _bgShouldRun = shouldRun;

        if (!_bgShouldRun)
        {
            // Pause
            try { _bgCts?.Cancel(); } catch { }
            try { _bgCts?.Dispose(); } catch { }
            _bgCts = null;

            try { _bgPlayer.Stop(); } catch { }

            Log("Background paused (not in view).");
        }
        else
        {
            // Resume
            Log("Background resumed.");
            _ = StartWebpBackgroundAsync();
        }
    }

    private async Task StartWebpBackgroundAsync()
    {
        try
        {
            if (!_bgShouldRun)
                return;

            if (BgAnim == null)
            {
                Log("BgAnim Image missing from XAML.");
                return;
            }

            _bgCts?.Cancel();
            _bgCts?.Dispose();
            _bgCts = new CancellationTokenSource();

            var baseDir = AppContext.BaseDirectory;
            var webpPath = Path.Combine(baseDir, "Assets", "LauncherBackground", "Lapras", "lapras_day.webp");

            Log($"Launcher background (WebP): {webpPath}");

            await _bgPlayer.StartAsync(
                webpPath,
                (w, h, pixels) => QueueBgFrame(w, h, pixels),
                Log,
                _bgCts.Token
            );
        }
        catch (Exception ex)
        {
            Log($"Background start failed: {ex}");
        }
    }

    private void QueueBgFrame(int w, int h, byte[] pixels)
    {
        if (!_bgShouldRun)
            return;

        _pendingW = w;
        _pendingH = h;
        _pendingPixels = pixels;

        if (Interlocked.Exchange(ref _bgUiScheduled, 1) == 1)
            return;

        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            try
            {
                if (!_bgShouldRun) return;
                if (_pendingPixels == null) return;

                EnsureBgWriteableBitmap(_pendingW, _pendingH);

                _bgWb!.WritePixels(
                    new Int32Rect(0, 0, _pendingW, _pendingH),
                    _pendingPixels,
                    _pendingW * 4,
                    0);

                BgAnim.Source = _bgWb;
            }
            catch (Exception ex)
            {
                Log($"Background UI update failed: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _bgUiScheduled, 0);
            }
        }));
    }

    private void EnsureBgWriteableBitmap(int w, int h)
    {
        if (_bgWb != null && _bgWb.PixelWidth == w && _bgWb.PixelHeight == h)
            return;

        _bgWb = new WriteableBitmap(w, h, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
    }

    // -------------------------
    // Launcher Self-Updater (HARD LOCKOUT)
    // -------------------------
    private async Task CheckLauncherSelfUpdateAsync()
    {
        // Acquire launcher-update lock (will refuse if pack op running)
        if (!TryBeginLauncherUpdate())
            return;

        try
        {
            // Disable UI for pack/play while we *evaluate and possibly download/apply*.
            Dispatcher.BeginInvoke(new Action(() => SetButtonsEnabled(false)));

            using var http = Net.CreateHttpClient();

            var current = SelfUpdater.GetCurrentVersion();
            Log($"Launcher version (current): {current}");

            var latest = await SelfUpdater.FetchLatestAsync(http, Log, CancellationToken.None);
            if (latest == null)
                return;

            Log($"Launcher version (latest): {latest.Version}");

            if (string.IsNullOrWhiteSpace(latest.Version) || string.IsNullOrWhiteSpace(latest.Url))
            {
                Log("Launcher update manifest missing Version/Url.");
                return;
            }

            if (!SelfUpdater.IsNewer(latest.Version, current, out var parsedLatest))
            {
                Log("Launcher is up to date.");
                return;
            }

            var result = await Dispatcher.InvokeAsync(() =>
            {
                var msg =
                    $"A launcher update is available.\n\n" +
                    $"Current: {current}\n" +
                    $"Latest: {parsedLatest}\n\n" +
                    $"Update now?\n\n" +
                    $"(The modpack update will be locked until the launcher update finishes.)";

                return System.Windows.MessageBox.Show(
                    this,
                    msg,
                    "FabriFroge Launcher Update",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information,
                    MessageBoxResult.Yes);
            });

            Log($"Launcher update prompt result: {result}");

            if (result != MessageBoxResult.Yes)
            {
                Log("User skipped launcher update.");
                return;
            }

            // Still holding launcher-update lock here, so pack ops cannot start.
            var downloaded = await SelfUpdater.DownloadUpdateAsync(http, latest.Url, Log, CancellationToken.None);
            if (downloaded == null)
                return;

            if (!SelfUpdater.VerifySha256(downloaded, latest.Sha256 ?? "", Log))
            {
                try { File.Delete(downloaded); } catch { }

                await Dispatcher.InvokeAsync(() =>
                    System.Windows.MessageBox.Show(
                        "Launcher update failed verification (SHA256 mismatch).",
                        "Update Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error));

                return;
            }

            await Dispatcher.InvokeAsync(() =>
            {
                Log("Launcher update: starting apply mode...");
                SelfUpdater.StartApplyUpdateAndExit(downloaded, Log);
            });
        }
        catch (Exception ex)
        {
            Log($"Launcher self-update error: {ex}");
        }
        finally
        {
            EndLauncherUpdate();
            Dispatcher.BeginInvoke(new Action(() => SetButtonsEnabled(true)));
        }
    }

    // -------------------------
    // Auth restore on startup (NO TOKEN CHECK)
    // -------------------------
    private async Task TryRestoreSessionOnStartupAsync()
    {
        try
        {
            var installRoot = InstallPathBox.Text ?? "";
            var restored = AuthService.TryLoadSession(installRoot, Log);

            if (restored != null)
            {
                _session = restored;
                SignedInAsText.Text = string.IsNullOrWhiteSpace(restored.Username) ? "(signed in)" : restored.Username;
                Log($"Restored session (not validated): {SignedInAsText.Text}");
            }
            else
            {
                Log("No saved Microsoft session found. Starting offline.");
            }
        }
        catch (Exception ex)
        {
            Log($"Startup restore error: {ex}");
        }

        await Task.CompletedTask;
    }

    // -------------------------
    // ✅ Token validation + forced reauth (ONLY when clicking Play)
    // -------------------------
    private static async Task<bool> ValidateMinecraftTokenAsync(HttpClient http, string accessToken, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        try
        {
            using var resp = await http.SendAsync(req, ct);

            if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
                return false;

            if ((int)resp.StatusCode >= 500)
                return true;

            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return true;
        }
    }

    private async Task<MSession> EnsureFreshSessionForPlayAsync(HttpClient http, CancellationToken ct)
    {
        var installRoot = InstallPathBox.Text ?? "";

        var loaded = AuthService.TryLoadSession(installRoot, Log);
        if (loaded != null)
            _session = loaded;

        var sessionToUse = _session ?? MSession.CreateOfflineSession(Environment.UserName);

        if (string.IsNullOrWhiteSpace(sessionToUse.AccessToken))
            return sessionToUse;

        Log("Validating Microsoft/Minecraft session...");
        bool ok = await ValidateMinecraftTokenAsync(http, sessionToUse.AccessToken, ct);

        if (ok)
        {
            Log("Session OK.");
            return sessionToUse;
        }

        Log("Session expired/invalid. Clearing cached auth and re-signing in...");
        AuthService.ClearSavedSession(installRoot, Log);

        var fresh = await AuthService.SignInMicrosoftAsync(installRoot, Log, ct);
        _session = fresh;

        SignedInAsText.Text = string.IsNullOrWhiteSpace(fresh.Username) ? "(signed in)" : fresh.Username;
        AuthService.SaveSession(installRoot, fresh, Log);

        Log("Re-auth complete.");
        return fresh;
    }

    // -------------------------
    // Update-check / install logic (HARD LOCKOUT)
    // -------------------------
    private async Task AutoCheckForUpdatesAsync()
    {
        if (IsLauncherUpdateRunning) return;
        if (IsPackOpRunning) return;

        try
        {
            SetProgressMode("Checking");
            Progress.Value = 0;
            Progress.IsIndeterminate = false;
            ProgressText.Text = "Checking for updates...";

            using var localCts = new CancellationTokenSource();
            var (_, manifest) = await FetchLatestAndManifestAsync(localCts.Token);

            var installed = (InstalledVersionText.Text ?? "(none)").Trim();
            var latest = (manifest.Version ?? "(unknown)").Trim();

            if (string.Equals(installed, latest, StringComparison.OrdinalIgnoreCase))
            {
                Log($"Up to date: {installed}");
                Progress.Value = 100;
                ProgressText.Text = "Up to date";
                return;
            }

            Log($"Update available: installed={installed} latest={latest}");
            Progress.Value = 100;
            ProgressText.Text = "Update available";

            await Dispatcher.InvokeAsync(async () =>
            {
                if (IsLauncherUpdateRunning) return;

                var title = "FabriFroge Launcher";
                var msg = installed == "(none)"
                    ? $"FabriFroge is not installed yet.\n\nLatest available: {latest}\n\nInstall now?"
                    : $"An update is available.\n\nInstalled: {installed}\nLatest: {latest}\n\nUpdate now?";

                var result = System.Windows.MessageBox.Show(msg, title, MessageBoxButton.YesNo, MessageBoxImage.Information);

                if (result == MessageBoxResult.Yes)
                {
                    try
                    {
                        await DoInstallOrUpdateAsync(fullVerify: false, mode: "Updating", showCompleteDialog: true);
                    }
                    catch (Exception ex)
                    {
                        Log($"Auto-update failed: {ex}");
                        System.Windows.MessageBox.Show(ex.Message, "Auto Update Error");
                    }
                }
                else
                {
                    Log("User skipped update prompt.");
                }
            });
        }
        catch (Exception ex)
        {
            Log($"Auto update-check failed: {ex.Message}");
            ProgressText.Text = "";
        }
        finally
        {
            _progressMode = "";
        }
    }

    private async Task DoInstallOrUpdateAsync(bool fullVerify, string mode, bool showCompleteDialog)
    {
        if (!TryBeginPackOp())
        {
            if (IsLauncherUpdateRunning)
            {
                System.Windows.MessageBox.Show(
                    "A launcher update is in progress. Please update/restart the launcher first.",
                    "FabriFroge Launcher",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            return;
        }

        try
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();

            SetButtonsEnabled(false);
            SetProgressMode(mode);
            Progress.IsIndeterminate = false;

            using var http = Net.CreateHttpClient();
            var (_, manifest) = await FetchLatestAndManifestAsync(_cts.Token);

            await JavaRuntime.EnsureTemurin21Async(http, InstallPathBox.Text ?? "", Log, _cts.Token);

            bool changed = await Installer.InstallOrUpdateAsync(
                http,
                InstallPathBox.Text ?? "",
                manifest,
                Log,
                SetProgress,
                _cts.Token,
                fullVerify: fullVerify);

            RefreshInstalledLabelIfPossible();

            Progress.Value = 100;

            if (!changed)
            {
                ProgressText.Text = $"{mode} 100.0% (Up to date)";
                if (showCompleteDialog)
                    System.Windows.MessageBox.Show("Already up to date.", "FabriFroge Launcher");
                return;
            }

            ProgressText.Text = $"{mode} 100.0%";

            if (showCompleteDialog)
                System.Windows.MessageBox.Show(fullVerify ? "Repair complete." : "Update complete.", "FabriFroge Launcher");
        }
        finally
        {
            EndPackOp();
            SetButtonsEnabled(true);
        }
    }

    private void Log(string msg)
    {
        Dispatcher.Invoke(() =>
        {
            LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}\n");
            LogBox.ScrollToEnd();
        });
    }

    private void SetProgressMode(string mode)
    {
        _progressMode = mode ?? "";
        Dispatcher.Invoke(() =>
        {
            if (!string.IsNullOrWhiteSpace(_progressMode))
            {
                if (_progressMode.Equals("Launching", StringComparison.OrdinalIgnoreCase))
                {
                    Progress.IsIndeterminate = true;
                    ProgressText.Text = "Launching...";
                    Progress.Value = 0;
                }
                else
                {
                    Progress.IsIndeterminate = false;
                    Progress.Value = 0;
                    ProgressText.Text = $"{_progressMode} 0.0%";
                }
            }
        });
    }

    private void SetProgress(InstallProgress p)
    {
        Dispatcher.Invoke(() =>
        {
            Progress.IsIndeterminate = false;

            double pct = p.BytesTotal > 0 ? (p.BytesDone / (double)p.BytesTotal) * 100.0 : 0;
            pct = Math.Clamp(pct, 0, 100);

            Progress.Value = pct;

            var mode = string.IsNullOrWhiteSpace(_progressMode) ? "Working" : _progressMode;
            ProgressText.Text = $"{mode} {pct:0.0}%";
        });
    }

    private void SetButtonsEnabled(bool enabled)
    {
        if (IsLauncherUpdateRunning)
            enabled = false;

        CheckUpdatesButton.IsEnabled = enabled;
        InstallButton.IsEnabled = enabled;
        RepairButton.IsEnabled = enabled;

        PlayButton.IsEnabled = enabled;
        PlayIconButton.IsEnabled = enabled;

        SignInButton.IsEnabled = enabled;
        SignOutButton.IsEnabled = enabled;

        // Allow these even when locked (UI convenience)
        ToggleConsoleButton.IsEnabled = true;
        PlayMenuButton.IsEnabled = true;

        RamComboBox.IsEnabled = enabled;
    }

    private async Task<(LatestInfo latest, PackManifest manifest)> FetchLatestAndManifestAsync(CancellationToken ct)
    {
        using var http = Net.CreateHttpClient();

        Log($"Latest URL: {LatestUrl}");
        var latest = await Net.GetJsonAsync<LatestInfo>(http, LatestUrl, ct);

        Log($"Latest packId={latest.PackId} version={latest.LatestVersion}");
        Log($"Manifest URL: {latest.ManifestUrl}");

        var manifest = await Net.GetJsonAsync<PackManifest>(http, latest.ManifestUrl, ct);
        Log($"Manifest version={manifest.Version} files={manifest.Files.Count}");

        _lastPackId = manifest.PackId;
        LatestVersionText.Text = manifest.Version;
        InstalledVersionText.Text = ReadInstalledVersion(InstallPathBox.Text ?? "", manifest.PackId);

        return (latest, manifest);
    }

    private string ReadInstalledVersion(string installRoot, string packId)
    {
        try
        {
            var packRoot = Installer.GetPackRoot(installRoot, packId);
            var current = Path.Combine(packRoot, "current.txt");
            if (File.Exists(current))
                return File.ReadAllText(current).Trim();
        }
        catch { }

        return "(none)";
    }

    private void RefreshInstalledLabelIfPossible()
    {
        if (string.IsNullOrWhiteSpace(_lastPackId))
            return;

        InstalledVersionText.Text = ReadInstalledVersion(InstallPathBox.Text ?? "", _lastPackId);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Choose where to install FabriFroge",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };

        if (dlg.ShowDialog() == WinFormsDialogResult.OK && !string.IsNullOrWhiteSpace(dlg.SelectedPath))
        {
            InstallPathBox.Text = dlg.SelectedPath;
            RefreshInstalledLabelIfPossible();
            LoadSettingsAndApply();
        }
    }

    private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsLauncherUpdateRunning) return;
        if (IsPackOpRunning) return;

        try
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();

            SetButtonsEnabled(false);

            var (_, manifest) = await FetchLatestAndManifestAsync(_cts.Token);

            System.Windows.MessageBox.Show(
                $"Installed: {InstalledVersionText.Text}\nLatest: {manifest.Version}\nFiles: {manifest.Files.Count}",
                "FabriFroge Launcher"
            );
        }
        catch (Exception ex)
        {
            Log(ex.ToString());
            System.Windows.MessageBox.Show(ex.Message, "Error");
        }
        finally
        {
            SetButtonsEnabled(true);
        }
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await DoInstallOrUpdateAsync(fullVerify: false, mode: "Updating", showCompleteDialog: true);
        }
        catch (Exception ex)
        {
            Log(ex.ToString());
            System.Windows.MessageBox.Show(ex.Message, "Error");
            SetButtonsEnabled(true);
        }
    }

    private async void RepairButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await DoInstallOrUpdateAsync(fullVerify: true, mode: "Repairing", showCompleteDialog: true);
        }
        catch (Exception ex)
        {
            Log(ex.ToString());
            System.Windows.MessageBox.Show(ex.Message, "Error");
            SetButtonsEnabled(true);
        }
    }

    private async void SignInButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsLauncherUpdateRunning) return;
        if (IsPackOpRunning) return;

        try
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();

            SetButtonsEnabled(false);

            MSession session = await AuthService.SignInMicrosoftAsync(
                InstallPathBox.Text ?? "",
                Log,
                _cts.Token);

            _session = session;
            SignedInAsText.Text = string.IsNullOrWhiteSpace(session.Username) ? "(signed in)" : session.Username;

            AuthService.SaveSession(InstallPathBox.Text ?? "", session, Log);

            System.Windows.MessageBox.Show($"Signed in as {SignedInAsText.Text}", "FabriFroge Launcher");
        }
        catch (Exception ex)
        {
            Log(ex.ToString());
            System.Windows.MessageBox.Show(ex.Message, "Microsoft Sign-in Error");
        }
        finally
        {
            SetButtonsEnabled(true);
        }
    }

    private void SignOutButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsLauncherUpdateRunning) return;
        if (IsPackOpRunning) return;

        try
        {
            AuthService.ClearSavedSession(InstallPathBox.Text ?? "", Log);
            _session = MSession.CreateOfflineSession(Environment.UserName);
            SignedInAsText.Text = "(offline)";
        }
        catch (Exception ex)
        {
            Log(ex.ToString());
            System.Windows.MessageBox.Show(ex.Message, "Error");
        }
    }

    // ✅ Play: validate/refresh auth here (NOT on startup)
    private async void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsLauncherUpdateRunning)
        {
            System.Windows.MessageBox.Show(
                "A launcher update is required. Please update/restart the launcher first.",
                "FabriFroge Launcher",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (IsPackOpRunning)
        {
            System.Windows.MessageBox.Show(
                "The modpack is currently being installed/updated. Please wait until it finishes.",
                "FabriFroge Launcher",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();

            SetButtonsEnabled(false);
            SetProgressMode("Launching");

            using var http = Net.CreateHttpClient();

            // ensure session is valid RIGHT NOW
            var sessionToUse = await EnsureFreshSessionForPlayAsync(http, _cts.Token);

            var (_, manifest) = await FetchLatestAndManifestAsync(_cts.Token);

            await JavaRuntime.EnsureTemurin21Async(http, InstallPathBox.Text ?? "", Log, _cts.Token);

            int ramToUse = _settings?.RamMb > 0 ? _settings.RamMb : DefaultRamMb;
            ApplyRamToUi(ramToUse);

            await GameEngine.LaunchAsync(
                http,
                InstallPathBox.Text ?? "",
                manifest,
                sessionToUse,
                Environment.UserName,
                ramToUse,
                Log,
                _cts.Token);

            Progress.IsIndeterminate = false;
            Progress.Value = 100;
            ProgressText.Text = "Running";
        }
        catch (Exception ex)
        {
            Log(ex.ToString());
            Progress.IsIndeterminate = false;
            ProgressText.Text = "";
            System.Windows.MessageBox.Show(ex.Message, "Launch Error");
        }
        finally
        {
            SetButtonsEnabled(true);
        }
    }
}
