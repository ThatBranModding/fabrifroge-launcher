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
    private const string LatestUrl = "https://files.fabrifroge.com/launcher/latest.json";
    private readonly HttpClient _http = Net.CreateHttpClient();
    private LauncherSettings _settings = new();
    private MSession? _session;
    private PackManifest? _manifest;
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
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var latest = await Net.GetJsonAsync<LatestInfo>(_http, LatestUrl, cts.Token);
            if (!string.IsNullOrWhiteSpace(latest.ManifestUrl))
                _manifest = await Net.GetJsonAsync<PackManifest>(_http, latest.ManifestUrl, cts.Token);
            else if (!string.IsNullOrWhiteSpace(latest.PackId))
                _manifest = new PackManifest { PackId = latest.PackId, Version = latest.LatestVersion };
            VersionLabel.Text = $"FabriFroge Cobblemon {latest.LatestVersion}";
            LatestVersion.Text = latest.LatestVersion;
            RefreshVersion();
            _session = AuthService.TryLoadSession(Root, Log);
            AccountLabel.Text = _session?.Username ?? "Playing offline";
            try { await CheckLauncherUpdateAsync(false); } catch (Exception ex) { Log($"Launcher update check failed: {ex.Message}"); }
            await AutoCheckPackAsync();
        }
        catch (Exception ex) { Log($"Startup check: {ex.Message}"); Status.Text = "Ready (pack info unavailable)"; }
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

    private void SetBusy(bool busy) { PlayButton.IsEnabled = !busy; }
    private async Task<bool> AskAsync(string title, string message)
    {
        var yes = new Button { Content = "Yes", MinWidth = 90 };
        var no = new Button { Content = "No", MinWidth = 90 };
        var dialog = new Window { Title = title, Width = 440, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false };
        yes.Click += (_, _) => dialog.Close(true);
        no.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel { Margin = new Thickness(20), Spacing = 16, Children =
        {
            new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
            new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8, Children = { no, yes } }
        }};
        return await dialog.ShowDialog<bool>(this);
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
        if (_manifest == null) return;
        var path = Path.Combine(Installer.GetPackRoot(Root, _manifest.PackId), "last_manifest.json");
        if (File.Exists(path)) { try { var m = JsonSerializer.Deserialize<PackManifest>(File.ReadAllText(path), Net.JsonOpts); if (m != null) { InstalledVersion.Text = m.Version; VersionLabel.Text = $"FabriFroge Cobblemon {m.Version}"; } } catch { } }
        else InstalledVersion.Text = "(none)";
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
            Log(changed ? "Pack files updated." : "Pack files already match the manifest.");
        });
    }

    private async void Install_Click(object? s, RoutedEventArgs e) => await InstallOrRepairAsync(false);
    private async void Repair_Click(object? s, RoutedEventArgs e) => await InstallOrRepairAsync(true);
    private async void CheckUpdates_Click(object? s, RoutedEventArgs e) => await RunAsync("Checking pack updates", async ct => { var m = await GetManifestAsync(ct); VersionLabel.Text = "Latest pack version " + m.Version; RefreshVersion(); });
    private async void Play_Click(object? s, RoutedEventArgs e) => await RunAsync("Launching Minecraft", async ct =>
    {
        var m = _manifest ?? await GetManifestAsync(ct);
        var session = await EnsureFreshSessionForPlayAsync(ct);
        var user = session.Username ?? Environment.UserName;
        var gameDirectory = Path.Combine(Installer.GetInstanceRoot(Root, m.PackId), ".minecraft");
        await GameEngine.LaunchAsync(_http, Root, m, session, user, (int)(RamInput.Value ?? 7168), Log, ct,
            gameDirectory);
    });
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
    private async Task CheckLauncherUpdateAsync(bool showStatus)
    {
        var info = await SelfUpdater.FetchLatestAsync(_http, Log, _operation.Token);
        if (info?.Version is null) { if (showStatus) Status.Text = "Update information unavailable"; return; }
        var current = SelfUpdater.GetCurrentVersion();
        if (SelfUpdater.IsNewer(info.Version, current, out var parsed))
        {
            var package = info.GetPlatformPackage();
            if (package == null || string.IsNullOrWhiteSpace(package.Url) || string.IsNullOrWhiteSpace(package.Sha256))
            {
                Log($"Launcher {parsed} is available, but update metadata has no verified package for {System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}.");
                if (showStatus) Status.Text = "Update package unavailable for this platform";
                return;
            }
            var accepted = await AskAsync("Launcher update", $"A launcher update is available.\n\nCurrent: {current}\nLatest: {parsed}\n\nUpdate now?");
            if (!accepted) { Log("Launcher update skipped."); return; }
            var zip = await SelfUpdater.DownloadUpdateAsync(_http, package.Url, Log, _operation.Token);
            if (zip == null) throw new InvalidOperationException("Launcher update download failed.");
            if (!SelfUpdater.VerifySha256(zip, package.Sha256, Log)) throw new InvalidDataException("Downloaded launcher update failed SHA-256 verification.");
            SelfUpdater.StartApplyUpdateAndExit(zip, Log);
        }
        else if (showStatus) Status.Text = "Launcher is up to date";
    }
    private async void LauncherUpdate_Click(object? s, RoutedEventArgs e) => await RunAsync("Checking launcher update", _ => CheckLauncherUpdateAsync(true));
    private async Task AutoCheckPackAsync()
    {
        if (_manifest == null) return;
        var installedPath = Path.Combine(Installer.GetPackRoot(Root, _manifest.PackId), "last_manifest.json");
        string? installed = null;
        try { if (File.Exists(installedPath)) installed = JsonSerializer.Deserialize<PackManifest>(await File.ReadAllTextAsync(installedPath))?.Version; } catch { }
        if (string.Equals(installed, _manifest.Version, StringComparison.OrdinalIgnoreCase)) return;
        var prompt = installed == null ? $"FabriFroge is not installed yet.\n\nLatest available: {_manifest.Version}\n\nInstall now?" : $"An update is available.\n\nInstalled: {installed}\nLatest: {_manifest.Version}\n\nUpdate now?";
        Progress.Value = 100;
        Status.Text = installed == _manifest.Version ? "Up to date" : "Update available";
        if (await AskAsync("FabriFroge Launcher", prompt)) await InstallOrRepairAsync(false);
    }
}
