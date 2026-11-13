using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Formats.Tar;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FabriFrogeLauncher;

public static class SelfUpdater
{
    // Cross-platform releases use a separate self-update channel from the
    // legacy Windows-only launcher at /launcher/self/latest.json.
    private const string LauncherLatestUrl = "https://files.fabrifroge.com/launcher/v2/self/latest.json";
    private const string ApplyArg = "--apply-update";

    private static readonly string ApplyLogPath =
        Path.Combine(Path.GetTempPath(), "FabriFrogeLauncher_apply.log");

    private static void ApplyLog(string msg)
    {
        try { File.AppendAllText(ApplyLogPath, $"[{DateTime.Now:HH:mm:ss}] {msg}\r\n"); }
        catch { }
    }

    public sealed class LauncherUpdateInfo
    {
        public string? Version { get; set; }
        public System.Collections.Generic.Dictionary<string, LauncherPackage>? Downloads { get; set; }
        public System.Collections.Generic.Dictionary<string, LauncherPackage>? Installers { get; set; }

        public LauncherPackage? GetPlatformInstaller()
        {
            var platformPackage = GetPlatformPackageKey();
            return platformPackage != null && Installers != null && Installers.TryGetValue(platformPackage, out var installer) ? installer : null;
        }

        private string? GetPlatformPackageKey()
        {
            var architecture = RuntimeInformation.ProcessArchitecture;
            var rid = OperatingSystem.IsWindows()
                ? architecture == Architecture.Arm64 ? "win-arm64" : architecture == Architecture.X64 ? "win-x64" : ""
                : OperatingSystem.IsMacOS()
                    ? architecture == Architecture.Arm64 ? "osx-arm64" : architecture == Architecture.X64 ? "osx-x64" : ""
                    : OperatingSystem.IsLinux()
                        ? architecture == Architecture.Arm64 ? "linux-arm64" : architecture == Architecture.X64 ? "linux-x64" : ""
                        : "";
            if (string.IsNullOrEmpty(rid)) return null;
            foreach (var entry in Downloads ?? new())
                if (entry.Key.Equals(rid, StringComparison.OrdinalIgnoreCase)) return entry.Key;
            return null;
        }

        public LauncherPackage? GetPlatformPackage()
        {
            var key = GetPlatformPackageKey();
            return key != null && Downloads != null && Downloads.TryGetValue(key, out var package) ? package : null;
        }
    }

    public sealed class LauncherPackage
    {
        public string? Url { get; set; }
        public string? Sha256 { get; set; }
    }

    public static string GetCurrentVersion()
    {
        try
        {
            var exePath = GetExePath();
            if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
            {
                var vi = FileVersionInfo.GetVersionInfo(exePath);
                if (!string.IsNullOrWhiteSpace(vi.ProductVersion)) return vi.ProductVersion.Trim();
                if (!string.IsNullOrWhiteSpace(vi.FileVersion)) return vi.FileVersion.Trim();
            }

            var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info)) return info.Trim();

            var v = asm.GetName().Version;
            return v?.ToString() ?? "0.0.0";
        }
        catch
        {
            return "0.0.0";
        }
    }

    public static async Task<LauncherUpdateInfo?> FetchLatestAsync(
        System.Net.Http.HttpClient http, Action<string> log, CancellationToken ct)
    {
        try
        {
            log($"Launcher update URL: {LauncherLatestUrl}");

            using var resp = await http.GetAsync(LauncherLatestUrl, ct);
            resp.EnsureSuccessStatusCode();

            var json = await resp.Content.ReadAsStringAsync(ct);
            json = json.TrimStart('\uFEFF', '\u200B');

            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var info = JsonSerializer.Deserialize<LauncherUpdateInfo>(json, opts);

            if (info == null)
            {
                log("Launcher update: failed to parse latest.json");
                return null;
            }

            return info;
        }
        catch (Exception ex)
        {
            log($"Launcher update: FetchLatestAsync failed: {ex.Message}");
            return null;
        }
    }

    public static bool IsNewer(string latestVersion, string currentVersion, out string parsedLatest)
    {
        parsedLatest = NormalizeVersion(latestVersion);
        var parsedCurrent = NormalizeVersion(currentVersion);

        if (!Version.TryParse(parsedLatest, out var latestV))
            return false;

        if (!Version.TryParse(parsedCurrent, out var currentV))
            currentV = new Version(0, 0, 0, 0);

        return latestV > currentV;
    }

    private static string NormalizeVersion(string v)
    {
        if (string.IsNullOrWhiteSpace(v))
            return "0.0.0";

        v = v.Trim();

        // Remove common prefixes
        if (v.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            v = v[1..];

        // Strip SemVer pre-release suffix (e.g. 1.1.0-beta.1)
        var dash = v.IndexOf('-');
        if (dash >= 0)
            v = v[..dash];

        // Strip SemVer build metadata (e.g. 1.1.0+d51f9fce...)
        var plus = v.IndexOf('+');
        if (plus >= 0)
            v = v[..plus];

        v = v.Trim();

        // Keep only the leading numeric/dot portion in case ProductVersion contains extra text
        var sb = new StringBuilder(v.Length);
        foreach (var ch in v)
        {
            if (char.IsDigit(ch) || ch == '.')
                sb.Append(ch);
            else
                break;
        }

        var cleaned = sb.ToString().Trim('.');

        if (string.IsNullOrWhiteSpace(cleaned))
            return "0.0.0";

        return cleaned;
    }

    public static async Task<string?> DownloadUpdateAsync(
        System.Net.Http.HttpClient http, string url, Action<string> log, CancellationToken ct)
    {
        var uri = new Uri(url);
        var extension = uri.AbsolutePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ? ".tar.gz" : ".zip";
        var path = Path.Combine(Path.GetTempPath(), $"FabriFrogeLauncher_update_{Guid.NewGuid():N}{extension}");
        try
        {
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    log($"Launcher update: downloading package (attempt {attempt}/3).");
                    using var response = await http.GetAsync(uri, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, ct);
                    if (!response.IsSuccessStatusCode)
                    {
                        if ((int)response.StatusCode < 500 && (int)response.StatusCode != 429)
                            response.EnsureSuccessStatusCode();
                        throw new System.Net.Http.HttpRequestException($"HTTP {(int)response.StatusCode} while downloading launcher update.");
                    }
                    await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
                    await response.Content.CopyToAsync(output, ct);
                    await output.FlushAsync(ct);
                    log("Launcher update: download complete.");
                    return path;
                }
                catch (Exception ex) when (attempt < 3 && (ex is System.Net.Http.HttpRequestException || ex is IOException) && !ct.IsCancellationRequested)
                {
                    log($"Launcher update download retry: {ex.Message}");
                    try { File.Delete(path); } catch { }
                    await Task.Delay(TimeSpan.FromMilliseconds(attempt * 700), ct);
                }
            }
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            try { File.Delete(path); } catch { }
            log($"Launcher update: download failed: {ex.Message}");
            return null;
        }
    }

    public static bool VerifySha256(string filePath, string expectedSha256, Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256) || expectedSha256.Trim().Length != 64 ||
            !expectedSha256.Trim().All(Uri.IsHexDigit))
        {
            log("Launcher update: missing or malformed SHA-256; refusing the package.");
            return false;
        }

        try
        {
            expectedSha256 = expectedSha256.Trim().ToLowerInvariant();

            using var sha = SHA256.Create();
            using var fs = File.OpenRead(filePath);
            var hash = sha.ComputeHash(fs);

            var actual = ToHexLower(hash);

            if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                log($"Launcher update: SHA256 mismatch. expected={expectedSha256} actual={actual}");
                return false;
            }

            log("Launcher update: SHA256 verified.");
            return true;
        }
        catch (Exception ex)
        {
            log($"Launcher update: SHA256 verify failed: {ex.Message}");
            return false;
        }
    }

    private static string ToHexLower(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
            sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    // Run the helper from a temporary copy, then apply a verified archive after
    // the active launcher has closed. The helper keeps a rollback copy until the
    // replacement has been written successfully.
    public static bool CanApplyInPlace()
    {
        try
        {
            var targetDir = GetUpdateTargetDirectory(GetExePath());
            Directory.CreateDirectory(targetDir);
            var probe = Path.Combine(targetDir, ".fabrifroge-write-" + Guid.NewGuid().ToString("N"));
            using (File.Create(probe)) { }
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    public static bool StartApplyUpdateAndExit(string archivePath, Action<string> log)
    {
        try
        {
            var pid = Environment.ProcessId;
            var installedExePath = GetExePath();
            var targetDir = GetUpdateTargetDirectory(installedExePath);
            var exeRelativePath = Path.GetRelativePath(targetDir, installedExePath);
            var helperDir = Path.Combine(Path.GetTempPath(), $"FabriFrogeLauncher_apply_{Guid.NewGuid():N}");
            Directory.CreateDirectory(helperDir);
            CopyDirectoryRecursiveWithRetries(targetDir, helperDir, retries: 10, delayMs: 200);
            var helperExePath = Path.Combine(helperDir, exeRelativePath);
            if (!File.Exists(helperExePath)) throw new InvalidOperationException("Temporary update helper executable is missing.");
            EnsureExecutable(helperExePath);

            ApplyLog($"Starting helper. target={targetDir}; executable={exeRelativePath}; pid={pid}");
            var psi = new ProcessStartInfo
            {
                FileName = helperExePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(helperExePath) ?? helperDir
            };
            psi.ArgumentList.Add(ApplyArg);
            psi.ArgumentList.Add(archivePath);
            psi.ArgumentList.Add(targetDir);
            psi.ArgumentList.Add(exeRelativePath);
            psi.ArgumentList.Add(pid.ToString());
            psi.Environment["FF_UPDATER_HELPER"] = "1";
            Process.Start(psi);
            Environment.Exit(0);
            return true;
        }
        catch (Exception ex)
        {
            ApplyLog("Updater handoff failed: " + ex);
            log($"Launcher update handoff failed: {ex.Message}");
            return false;
        }
    }

    public static async Task<int> RunApplyUpdateModeAsync(string[] args, Action<string> log)
    {
        string? extractDir = null;
        try
        {
            if (args.Length < 5 || !string.Equals(args[0], ApplyArg, StringComparison.OrdinalIgnoreCase))
                return -1;
            var archivePath = args[1];
            var targetDir = Path.GetFullPath(args[2]);
            var exeRelativePath = args[3];
            if (Path.IsPathRooted(exeRelativePath) || exeRelativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part == ".."))
                throw new InvalidDataException("Updater executable path is unsafe.");
            if (!int.TryParse(args[4], out var oldPid)) return 3;

            log($"Apply mode: target={targetDir}; executable={exeRelativePath}; oldPid={oldPid}");
            await WaitForProcessExitOrTimeoutAsync(oldPid, secondsTimeout: 60, log);
            if (!File.Exists(archivePath)) throw new FileNotFoundException("Update archive is missing.", archivePath);

            extractDir = Path.Combine(Path.GetTempPath(), $"FabriFrogeLauncher_extract_{Guid.NewGuid():N}");
            Directory.CreateDirectory(extractDir);
            if (archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            {
                await using var stream = File.OpenRead(archivePath);
                using var gzip = new GZipStream(stream, CompressionMode.Decompress);
                TarFile.ExtractToDirectory(gzip, extractDir, overwriteFiles: true);
            }
            else ZipFile.ExtractToDirectory(archivePath, extractDir, overwriteFiles: true);

            var payloadRoot = FindPayloadRootForUpdate(extractDir, exeRelativePath, targetDir.EndsWith(".app", StringComparison.OrdinalIgnoreCase));
            var targetExe = Path.Combine(targetDir, exeRelativePath);
            if (targetDir.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                ReplaceAppBundle(payloadRoot, targetDir, log);
            else
                ApplyOverlayTransactional(payloadRoot, targetDir, log);

            targetExe = Path.Combine(targetDir, exeRelativePath);
            if (!File.Exists(targetExe)) throw new InvalidOperationException("Updated launcher executable is missing.");
            EnsureExecutable(targetExe);
            ApplyLog("Update completed successfully; relaunching new executable.");
            Process.Start(new ProcessStartInfo { FileName = targetExe, UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(targetExe)! });
            return 0;
        }
        catch (Exception ex)
        {
            ApplyLog("Apply mode failed: " + ex);
            log($"Apply mode failed and rollback was attempted: {ex.Message}");
            return 1;
        }
        finally
        {
            try { if (extractDir != null && Directory.Exists(extractDir)) Directory.Delete(extractDir, true); } catch { }
            try { if (args.Length > 1 && File.Exists(args[1])) File.Delete(args[1]); } catch { }
        }
    }

    private static string GetUpdateTargetDirectory(string executablePath)
    {
        var directory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory;
        if (OperatingSystem.IsMacOS())
        {
            var app = Directory.GetParent(directory)?.Parent;
            if (app?.FullName.EndsWith(".app", StringComparison.OrdinalIgnoreCase) == true)
                return app.FullName;
        }
        return directory;
    }

    private static string FindPayloadRootForUpdate(string extractDir, string exeRelativePath, bool isMacApp)
    {
        var relative = exeRelativePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        if (isMacApp)
        {
            foreach (var app in Directory.EnumerateDirectories(extractDir, "*.app", SearchOption.AllDirectories))
                if (File.Exists(Path.Combine(app, relative))) return app;
        }
        var direct = Path.Combine(extractDir, relative);
        if (File.Exists(direct)) return Path.GetDirectoryName(direct)!;
        var match = Directory.EnumerateFiles(extractDir, Path.GetFileName(relative), SearchOption.AllDirectories).FirstOrDefault();
        if (match == null) throw new InvalidDataException("Update archive has no launcher executable.");
        return Path.GetDirectoryName(match)!;
    }

    private static void ReplaceAppBundle(string stagedApp, string targetApp, Action<string> log)
    {
        var parent = Path.GetDirectoryName(targetApp)!;
        var staged = Path.Combine(parent, Path.GetFileName(targetApp) + ".new-" + Guid.NewGuid().ToString("N"));
        var backup = Path.Combine(parent, Path.GetFileName(targetApp) + ".backup-" + Guid.NewGuid().ToString("N"));
        CopyDirectoryRecursive(stagedApp, staged);
        var hadOriginal = Directory.Exists(targetApp);
        try
        {
            if (hadOriginal) Directory.Move(targetApp, backup);
            Directory.Move(staged, targetApp);
            if (hadOriginal) Directory.Delete(backup, true);
        }
        catch
        {
            try { if (Directory.Exists(targetApp)) Directory.Delete(targetApp, true); } catch { }
            if (hadOriginal && Directory.Exists(backup)) Directory.Move(backup, targetApp);
            throw;
        }
        finally { try { if (Directory.Exists(staged)) Directory.Delete(staged, true); } catch { } }
        log("Mac application bundle replaced transactionally.");
    }

    private static void ApplyOverlayTransactional(string source, string target, Action<string> log)
    {
        Directory.CreateDirectory(target);
        var backupRoot = Path.Combine(Path.GetTempPath(), "FabriFrogeLauncher_rollback_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backupRoot);
        var changed = new System.Collections.Generic.List<(string target, string? backup)>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(source, file);
                var destination = Path.GetFullPath(Path.Combine(target, relative));
                if (!destination.StartsWith(Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new InvalidDataException("Update archive contains an unsafe file path.");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                string? backup = null;
                if (File.Exists(destination))
                {
                    backup = Path.Combine(backupRoot, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(destination, backup, true);
                }
                changed.Add((destination, backup));
                var staged = destination + ".update-" + Guid.NewGuid().ToString("N");
                File.Copy(file, staged, true);
                File.Move(staged, destination, true);
            }
        }
        catch
        {
            foreach (var item in changed.AsEnumerable().Reverse())
            {
                try
                {
                    if (item.backup == null) File.Delete(item.target);
                    else File.Copy(item.backup, item.target, true);
                }
                catch (Exception rollbackError) { ApplyLog("Rollback error: " + rollbackError); }
            }
            throw;
        }
        finally { try { Directory.Delete(backupRoot, true); } catch { } }
        log("Launcher files replaced transactionally.");
    }

    private static async Task WaitForProcessExitOrTimeoutAsync(int pid, int secondsTimeout, Action<string> log)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(secondsTimeout))
        {
            try
            {
                var p = Process.GetProcessById(pid);
                if (p.HasExited) return;
            }
            catch
            {
                return;
            }

            await Task.Delay(250);
        }

        log($"Apply mode: wait timeout after {secondsTimeout}s for PID {pid}. Continuing...");
    }

    private static void CopyDirectoryRecursiveWithRetries(string srcDir, string dstDir, int retries, int delayMs)
    {
        for (var attempt = 1; attempt <= retries; attempt++)
        {
            try
            {
                CopyDirectoryRecursive(srcDir, dstDir);
                return;
            }
            catch (IOException) when (attempt < retries)
            {
                Thread.Sleep(delayMs);
            }
            catch (UnauthorizedAccessException) when (attempt < retries)
            {
                Thread.Sleep(delayMs);
            }
        }

        CopyDirectoryRecursive(srcDir, dstDir);
    }

    private static void CopyDirectoryRecursive(string srcDir, string dstDir)
    {
        foreach (var dir in Directory.GetDirectories(srcDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(srcDir, dir);
            Directory.CreateDirectory(Path.Combine(dstDir, rel));
        }

        foreach (var file in Directory.GetFiles(srcDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(srcDir, file);
            var dst = Path.Combine(dstDir, rel);

            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);

            if (File.Exists(dst))
            {
                try { File.SetAttributes(dst, FileAttributes.Normal); } catch { }
            }

            File.Copy(file, dst, overwrite: true);
        }
    }

    private static int RunRobocopyMirror(string srcDir, string dstDir, Action<string> log)
    {
        // /MIR mirrors (incl deletes). If you don't want deletes, change to /E.
        var args = $"\"{srcDir}\" \"{dstDir}\" /MIR /R:20 /W:1 /NFL /NDL /NP";

        var psi = new ProcessStartInfo
        {
            FileName = "robocopy.exe",
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        log($"Apply mode: robocopy {args}");

        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();

        if (!string.IsNullOrWhiteSpace(output))
            log("robocopy: " + output.Trim());
        if (!string.IsNullOrWhiteSpace(err))
            log("robocopy(err): " + err.Trim());

        return p.ExitCode;
    }

    private static string? FindFirstExe(string root)
    {
        try
        {
            var currentName = Path.GetFileName(GetExePath());
            var matching = Directory.EnumerateFiles(root, currentName, SearchOption.AllDirectories).FirstOrDefault();
            if (matching != null) return matching;
            foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                if (Path.GetFileName(f).Equals("FabriFrogeLauncher", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f).Equals("FabriFrogeLauncher.exe", StringComparison.OrdinalIgnoreCase)) return f;
        }
        catch { }
        return null;
    }

    private static string FindPayloadRoot(string root, string executableName)
    {
        try
        {
            var candidate = Directory.EnumerateFiles(root, executableName, SearchOption.AllDirectories).FirstOrDefault();
            if (candidate != null) return Path.GetDirectoryName(candidate)!;
        }
        catch { }
        return root;
    }

    private static void EnsureExecutable(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path)) return;
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute); }
        catch { }
    }

    private static string GetExePath()
    {
        try
        {
            var p = Process.GetCurrentProcess();
            return p.MainModule?.FileName ?? Environment.ProcessPath ?? AppContext.BaseDirectory;
        }
        catch
        {
            return Environment.ProcessPath ?? AppContext.BaseDirectory;
        }
    }
}
