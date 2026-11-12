using System.Diagnostics;
using System.Net.Http;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CmlLib.Core.Auth;

namespace FabriFrogeLauncher;

public partial class MainWindow : Window
{
    private enum MainAction { Checking, Install, UpdateLauncher, UpdatePack, Play }

    // Keep the cross-platform launcher on its own pack feed. The legacy
    // launcher continues to use /launcher/latest.json and is not updated by
    // publishing a v2 modpack.
    private const string LatestUrl = "https://files.fabrifroge.com/launcher/v2/latest.json";
    private readonly HttpClient _http = Net.CreateHttpClient();
    private LauncherSettings _settings = new();
    private MSession? _session;
    private PackManifest? _manifest;
    private SelfUpdater.LauncherPackage? _launcherUpdatePackage;
    private string? _launcherUpdateVersion;
    private MainAction _mainAction = MainAction.Checking;
    private bool _startupChecksComplete;
    private bool _packStatusKnown;
    private bool _packInstalled;
    private bool _packUpdateAvailable;
    private bool _isBusy;
    private CancellationTokenSource _operation = new();
    private readonly WebpBackgroundPlayer _backgroundPlayer = new();
    private WriteableBitmap? _backgroundBitmap;
    private bool _backgroundRunning;
    private readonly string _backgroundPath;
    private byte[]? _pendingBackgroundFrame;
    private int _backgroundUiQueued;
    private int _pendingBackgroundWidth;
    private int _pendingBackgroundHeight;

    public MainWindow()
    {
        InitializeComponent();
        _settings = LauncherSettings.Load(Log);
        InstallPath.Text = _settings.InstallDirectory ?? DefaultInstallRoot();
        RamInput.Value = _settings.RamMb;
        Console.IsVisible = false;
        _backgroundPath = Path.Combine(AppContext.BaseDirectory, "Assets", "LauncherBackground", "Lapras", "lapras_day.webp");
        Opened += (_, _) => UpdateBackgroundPlayback();
        Activated += (_, _) => UpdateBackgroundPlayback();
        // Keep the animated scene moving while the window is visible. Losing
        // keyboard focus (for example, clicking the game launcher) must not
        // freeze the background; only hide/minimize/close stops the decoder.
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty || e.Property == IsVisibleProperty)
                UpdateBackgroundPlayback();
        };
        Closed += (_, _) => { _operation.Cancel(); _backgroundPlayer.Dispose(); _backgroundBitmap?.Dispose(); _http.Dispose(); };
        _ = InitializeAsync();
    }

    private void UpdateBackgroundPlayback()
    {
        var shouldRun = IsVisible && WindowState != WindowState.Minimized;
        if (shouldRun == _backgroundRunning) return;
        _backgroundRunning = shouldRun;
        if (shouldRun)
        {
            Log("Background animation resumed while launcher is visible.");
            _ = _backgroundPlayer.StartAsync(_backgroundPath, ShowBackgroundFrame, Log, CancellationToken.None);
        }
        else
        {
            _backgroundPlayer.Stop();
            Log("Background animation paused while launcher is minimized or hidden.");
        }
    }
    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }
    private void Minimize_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
    private void PlayMenu_Click(object? sender, RoutedEventArgs e) => PlayMenuPopup.IsOpen = !PlayMenuPopup.IsOpen;

    private static string DefaultInstallRoot() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FabriFroge");
    private string Root => string.IsNullOrWhiteSpace(InstallPath.Text) ? DefaultInstallRoot() : InstallPath.Text!;

    private async Task InitializeAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var latest = await Net.GetJsonAsync<LatestInfo>(_http, LatestUrl, cts.Token);
            if (!string.IsNullOrWhiteSpace(latest.ManifestUrl))
                _manifest = await Net.GetJsonAsync<PackManifest>(_http, latest.ManifestUrl, cts.Token);
            else if (!string.IsNullOrWhiteSpace(latest.PackId))
                _manifest = new PackManifest { PackId = latest.PackId, Version = latest.LatestVersion };
            RefreshVersion();
        }
        catch (Exception ex)
        {
            _packStatusKnown = false;
            Log($"Modpack startup check failed: {ex.Message}");
            Status.Text = "Modpack information unavailable";
        }

        try
        {
            _session = AuthService.TryLoadSession(Root, Log);
            AccountLabel.Text = _session?.Username ?? "Playing offline";
        }
        catch (Exception ex) { Log($"Account startup check failed: {ex.Message}"); }

        try { await CheckLauncherUpdateAsync(false, cts.Token); }
        catch (Exception ex) { Log($"Launcher update check failed: {ex.Message}"); }

        try { await AutoCheckPackAsync(cts.Token); }
        catch (Exception ex)
        {
            _packStatusKnown = false;
            Log($"Modpack update check failed: {ex.Message}");
        }
        _startupChecksComplete = true;
        UpdateMainActionButton();
        UpdateStartupStatus();
    }

    private async Task<PackManifest> GetManifestAsync(CancellationToken ct)
    {
        var latest = await Net.GetJsonAsync<LatestInfo>(_http, LatestUrl, ct);
        if (string.IsNullOrWhiteSpace(latest.ManifestUrl)) throw new InvalidOperationException("The latest pack metadata has no manifestUrl.");
        _manifest = await Net.GetJsonAsync<PackManifest>(_http, latest.ManifestUrl, ct);
        LatestVersion.Text = _manifest.Version;
        return _manifest;
    }

    private async Task RunAsync(string label, Func<CancellationToken, Task> action)
    {
        _operation.Cancel(); _operation.Dispose(); _operation = new CancellationTokenSource();
        SetBusy(true); Status.Text = label; Progress.Value = 0;
        try { await action(_operation.Token); Status.Text = label + " complete"; }
        catch (OperationCanceledException) { Status.Text = "Cancelled"; }
        catch (Exception ex) { Status.Text = ex.Message; Log(ex.ToString()); }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        UpdateMainActionButton();
    }

    private void UpdateMainActionButton()
    {
        if (!_startupChecksComplete || (!_packStatusKnown && _launcherUpdatePackage == null))
            _mainAction = MainAction.Checking;
        else if (!_packStatusKnown && _launcherUpdatePackage != null)
            _mainAction = MainAction.UpdateLauncher;
        else if (!_packInstalled)
            _mainAction = MainAction.Install;
        else if (_launcherUpdatePackage != null)
            _mainAction = MainAction.UpdateLauncher;
        else if (_packUpdateAvailable)
            _mainAction = MainAction.UpdatePack;
        else
            _mainAction = MainAction.Play;

        PlayButtonText.Text = _mainAction switch
        {
            MainAction.Install => "Install",
            MainAction.UpdateLauncher or MainAction.UpdatePack => "Update",
            MainAction.Play => "Play",
            _ => "Checking…"
        };
        PlayButtonIcon.Text = _mainAction == MainAction.Play ? "▶" : _mainAction == MainAction.Checking ? "…" : "↓";
        PlayButton.IsEnabled = !_isBusy && _mainAction != MainAction.Checking;
    }

    private void UpdateStartupStatus()
    {
        if (_launcherUpdatePackage != null) Status.Text = $"Launcher update available: {_launcherUpdateVersion}";
        else if (!_packStatusKnown) Status.Text = "Modpack information unavailable";
        else if (!_packInstalled) Status.Text = "Install available";
        else if (_packUpdateAvailable) Status.Text = "Modpack update available";
        else Status.Text = "Ready";
    }

    private static bool IsNewerPackVersion(string latest, string installed)
    {
        latest = latest.Trim().TrimStart('v', 'V');
        installed = installed.Trim().TrimStart('v', 'V');
        if (Version.TryParse(latest, out var latestVersion) && Version.TryParse(installed, out var installedVersion))
            return latestVersion > installedVersion;
        return !string.Equals(latest, installed, StringComparison.OrdinalIgnoreCase);
    }
    private void Log(string message) => Dispatcher.UIThread.Post(() => Console.Text += $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
    private void ShowBackgroundFrame(int width, int height, byte[] pixels)
    {
        _pendingBackgroundWidth = width;
        _pendingBackgroundHeight = height;
        _pendingBackgroundFrame = pixels.ToArray();
        if (Interlocked.Exchange(ref _backgroundUiQueued, 1) == 1) return;
        Dispatcher.UIThread.Post(ApplyLatestBackgroundFrame, DispatcherPriority.Render);
    }

    private void ApplyLatestBackgroundFrame()
    {
        try
    {
            var frame = Interlocked.Exchange(ref _pendingBackgroundFrame, null);
            if (frame == null || !_backgroundRunning) return;
            var width = _pendingBackgroundWidth;
            var height = _pendingBackgroundHeight;
        if (_backgroundBitmap == null || _backgroundBitmap.PixelSize.Width != width || _backgroundBitmap.PixelSize.Height != height)
        {
            _backgroundBitmap?.Dispose();
            _backgroundBitmap = new WriteableBitmap(new PixelSize(width, height), new Avalonia.Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            BackgroundImage.Source = _backgroundBitmap;
        }
        using var fb = _backgroundBitmap.Lock();
        System.Runtime.InteropServices.Marshal.Copy(frame, 0, fb.Address, Math.Min(frame.Length, fb.RowBytes * height));
            BackgroundImage.InvalidateVisual();
        }
        finally
        {
            Interlocked.Exchange(ref _backgroundUiQueued, 0);
            if (_pendingBackgroundFrame != null && _backgroundRunning && Interlocked.Exchange(ref _backgroundUiQueued, 1) == 0)
                Dispatcher.UIThread.Post(ApplyLatestBackgroundFrame, DispatcherPriority.Render);
        }
    }
    private void RefreshVersion()
    {
        if (_manifest == null)
        {
            _packStatusKnown = false;
            _packInstalled = false;
            _packUpdateAvailable = false;
            return;
        }

        LatestVersion.Text = _manifest.Version;
        VersionLabel.Text = $"FabriFroge Cobblemon {_manifest.Version}";
        var path = Path.Combine(Installer.GetPackRoot(Root, _manifest.PackId), "last_manifest.json");
        PackManifest? installed = null;
        if (File.Exists(path))
        {
            try { installed = JsonSerializer.Deserialize<PackManifest>(File.ReadAllText(path), Net.JsonOpts); }
            catch (Exception ex) { Log($"Could not read installed modpack version: {ex.Message}"); }
        }
        _packStatusKnown = true;
        _packInstalled = installed != null;
        _packUpdateAvailable = installed != null && IsNewerPackVersion(_manifest.Version, installed.Version);
        InstalledVersion.Text = installed?.Version ?? "(none)";
        UpdateMainActionButton();
    }
    private async Task InstallOrRepairAsync(bool verify)
    {
        await RunAsync(verify ? "Repairing game files" : "Installing or updating", async ct =>
        {
            var manifest = await GetManifestAsync(ct);
            await JavaRuntime.EnsureTemurin21Async(_http, Root, Log, ct);
            var changed = await Installer.InstallOrUpdateAsync(_http, Root, manifest, Log,
                p => Dispatcher.UIThread.Post(() => { Progress.Value = p.FilesTotal == 0 ? 0 : (double)p.FilesDone / p.FilesTotal * 100; Status.Text = $"{p.FilesDone}/{p.FilesTotal} {p.CurrentFile}"; }), ct, verify);
            await NeoForgeClientInstaller.EnsureInstalledAsync(_http, Path.Combine(Installer.GetInstanceRoot(Root, manifest.PackId), ".minecraft"), JavaRuntime.JavawPath(Root), manifest.Loader?.Version ?? "", Log, ct);
            RefreshVersion();
            UpdateStartupStatus();
            Log(changed ? "Pack files updated." : "Pack files already match the manifest.");
        });
    }

    private async void Install_Click(object? s, RoutedEventArgs e) => await InstallOrRepairAsync(false);
    private async void Repair_Click(object? s, RoutedEventArgs e) => await InstallOrRepairAsync(true);
    private async void CheckUpdates_Click(object? s, RoutedEventArgs e) => await RunAsync("Checking pack updates", async ct =>
    {
        await GetManifestAsync(ct);
        RefreshVersion();
        UpdateStartupStatus();
    });
    private async void Play_Click(object? s, RoutedEventArgs e)
    {
        switch (_mainAction)
        {
            case MainAction.Install:
            case MainAction.UpdatePack:
                await InstallOrRepairAsync(false);
                break;
            case MainAction.UpdateLauncher:
                await RunAsync("Updating launcher", ApplyLauncherUpdateAsync);
                break;
            case MainAction.Play:
                await RunAsync("Launching Minecraft", LaunchGameAsync);
                break;
        }
    }

    private async Task LaunchGameAsync(CancellationToken ct)
    {
        var m = _manifest ?? await GetManifestAsync(ct);
        var session = await EnsureFreshSessionForPlayAsync(ct);
        var user = session.Username ?? Environment.UserName;
        var gameDirectory = Path.Combine(Installer.GetInstanceRoot(Root, m.PackId), ".minecraft");
        await GameEngine.LaunchAsync(_http, Root, m, session, user, (int)(RamInput.Value ?? 7168), Log, ct,
            gameDirectory);
    }
    private async Task<MSession> EnsureFreshSessionForPlayAsync(CancellationToken ct)
    {
        var loaded = AuthService.TryLoadSession(Root, Log);
        if (loaded != null) _session = loaded;
        var current = _session ?? MSession.CreateOfflineSession(Environment.UserName);
        if (string.IsNullOrWhiteSpace(current.AccessToken)) return current;
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", current.AccessToken);
        bool valid;
        try
        {
            using var response = await _http.SendAsync(request, ct);
            valid = response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden
                ? false : (int)response.StatusCode >= 500 || response.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { valid = true; }
        if (valid) return current;
        Log("Microsoft session expired; starting sign-in again.");
        Log("Opening the default browser for Microsoft sign-in...");
        _session = await AuthService.SignInMicrosoftAsync(Root, Log, ct);
        AccountLabel.Text = _session.Username;
        return _session;
    }
    private async void SignIn_Click(object? s, RoutedEventArgs e) => await RunAsync("Microsoft sign in", async ct => { Log("Opening the default browser for Microsoft sign-in..."); _session = await AuthService.SignInMicrosoftAsync(Root, Log, ct); AccountLabel.Text = _session.Username; });
    private async void SignOut_Click(object? s, RoutedEventArgs e) => await RunAsync("Signing out", async _ => { await AuthService.SignOutAsync(Root, Log); _session = null; AccountLabel.Text = "Playing offline"; });
    private async void Switch_Click(object? s, RoutedEventArgs e) => await RunAsync("Switching account", async ct => { Log("Opening the default browser for Microsoft sign-in..."); _session = await AuthService.SwapAccountsAsync(Root, Log, ct); AccountLabel.Text = _session.Username; });
    private async void SaveSettings_Click(object? s, RoutedEventArgs e) { _settings.InstallDirectory = Root; _settings.RamMb = (int)(RamInput.Value ?? 7168); _settings.Save(Log); Status.Text = "Settings saved"; await Task.CompletedTask; }
    private void Ram_Changed(object? s, NumericUpDownValueChangedEventArgs e) { _settings.RamMb = (int)(e.NewValue ?? 7168); _settings.Save(Log); }
    private async void Browse_Click(object? s, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose FabriFroge install folder", AllowMultiple = false });
        if (folders.Count > 0) InstallPath.Text = folders[0].Path.LocalPath;
    }
    private void Wiki_Click(object? s, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("https://wiki.fabrifroge.com/") { UseShellExecute = true });
    private void ToggleConsole_Click(object? s, RoutedEventArgs e) => Console.IsVisible = !Console.IsVisible;
    private async Task CheckLauncherUpdateAsync(bool showStatus, CancellationToken ct)
    {
        var info = await SelfUpdater.FetchLatestAsync(_http, Log, ct);
        _launcherUpdatePackage = null;
        _launcherUpdateVersion = null;
        if (info?.Version is null)
        {
            Log("Launcher update feed unavailable; continuing startup checks.");
            if (showStatus) UpdateStartupStatus();
            UpdateMainActionButton();
            return;
        }
        var current = SelfUpdater.GetCurrentVersion();
        if (SelfUpdater.IsNewer(info.Version, current, out var parsed))
        {
            var package = info.GetPlatformPackage();
            if (package == null || string.IsNullOrWhiteSpace(package.Url) || string.IsNullOrWhiteSpace(package.Sha256))
            {
                Log($"Launcher {parsed} is available, but update metadata has no verified package for {System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}.");
                if (showStatus) Status.Text = "Launcher update package unavailable";
            }
            else
            {
                _launcherUpdatePackage = package;
                _launcherUpdateVersion = parsed;
                Log($"Launcher update available: {current} -> {parsed}.");
            }
        }
        else Log($"Launcher is up to date ({current}).");

        UpdateMainActionButton();
        if (showStatus) UpdateStartupStatus();
    }

    private async Task ApplyLauncherUpdateAsync(CancellationToken ct)
    {
        if (_launcherUpdatePackage == null)
            await CheckLauncherUpdateAsync(false, ct);
        var package = _launcherUpdatePackage;
        if (package == null || string.IsNullOrWhiteSpace(package.Url) || string.IsNullOrWhiteSpace(package.Sha256))
        {
            UpdateStartupStatus();
            return;
        }

        var archive = await SelfUpdater.DownloadUpdateAsync(_http, package.Url, Log, ct)
            ?? throw new InvalidOperationException("Launcher update download failed.");
        if (!SelfUpdater.VerifySha256(archive, package.Sha256, Log))
            throw new InvalidDataException("Downloaded launcher update failed SHA-256 verification.");
        SelfUpdater.StartApplyUpdateAndExit(archive, Log);
    }

    private Task AutoCheckPackAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_manifest == null)
        {
            _packStatusKnown = false;
            UpdateMainActionButton();
            return Task.CompletedTask;
        }

        RefreshVersion();
        UpdateStartupStatus();
        return Task.CompletedTask;
    }

    private async void LauncherUpdate_Click(object? s, RoutedEventArgs e) => await RunAsync("Checking launcher update", async ct =>
    {
        await CheckLauncherUpdateAsync(true, ct);
        UpdateStartupStatus();
    });
}
