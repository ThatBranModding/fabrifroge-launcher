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

        public LauncherPackage? GetPlatformPackage()
        {
            var architecture = RuntimeInformation.ProcessArchitecture;
            var rid = OperatingSystem.IsWindows()
                ? architecture == Architecture.Arm64 ? "win-arm64" : architecture == Architecture.X64 ? "win-x64" : ""
                : OperatingSystem.IsMacOS()
                    ? architecture == Architecture.Arm64 ? "osx-arm64" : architecture == Architecture.X64 ? "osx-x64" : ""
                    : OperatingSystem.IsLinux()
                        ? architecture == Architecture.Arm64 ? "linux-arm64" : architecture == Architecture.X64 ? "linux-x64" : ""
                        : "";
            if (Downloads == null) return null;
            foreach (var entry in Downloads)
                if (entry.Key.Equals(rid, StringComparison.OrdinalIgnoreCase)) return entry.Value;
            return null;
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
        try
        {
            var uri = new Uri(url);
            var extension = uri.AbsolutePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ? ".tar.gz" : ".zip";
            var tmp = Path.Combine(Path.GetTempPath(), $"FabriFrogeLauncher_update_{Guid.NewGuid():N}{extension}");
            log($"Launcher update: downloading -> {tmp}");

            using var resp = await http.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            await using var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None);
            await resp.Content.CopyToAsync(fs, ct);

            log("Launcher update: download complete.");
            return tmp;
        }
        catch (Exception ex)
        {
            log($"Launcher update: download failed: {ex.Message}");
            return null;
        }
    }

    public static bool VerifySha256(string filePath, string expectedSha256, Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256))
        {
            log("Launcher update: no SHA256 provided; skipping verification.");
            return true;
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

    // ✅ IMPORTANT: apply-mode must NOT run from install dir
    public static void StartApplyUpdateAndExit(string zipPath, Action<string> log)
    {
        try
        {
            var pid = Environment.ProcessId;

            var installedExePath = GetExePath();
            var installedDir = Path.GetDirectoryName(installedExePath) ?? AppContext.BaseDirectory;
            var exeName = Path.GetFileName(installedExePath);

            ApplyLog("=== StartApplyUpdateAndExit ===");
            ApplyLog($"installedExePath={installedExePath}");
            ApplyLog($"installedDir={installedDir}");
            ApplyLog($"exeName={exeName}");
            ApplyLog($"zipPath={zipPath}");
            ApplyLog($"pid={pid}");

            // Helper folder
            var helperDir = Path.Combine(Path.GetTempPath(), $"FabriFrogeLauncher_apply_{Guid.NewGuid():N}");
            Directory.CreateDirectory(helperDir);

            ApplyLog($"helperDir={helperDir}");
            log($"Launcher update: helperDir={helperDir}");

            // Copy install -> helper so helper loads DLLs from TEMP, not install folder
            CopyDirectoryRecursiveWithRetries(installedDir, helperDir, retries: 20, delayMs: 200);

            var helperExePath = Path.Combine(helperDir, exeName);
            if (!File.Exists(helperExePath))
            {
                var found = FindFirstExe(helperDir);
                if (!string.IsNullOrWhiteSpace(found)) helperExePath = found!;
            }

            if (!File.Exists(helperExePath))
                throw new InvalidOperationException("Helper EXE not found after copy.");
            EnsureExecutable(helperExePath);

            ApplyLog($"helperExePath={helperExePath}");

            ApplyLog($"Launching helper apply-mode: {helperExePath}");

            var psi = new ProcessStartInfo
            {
                FileName = helperExePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = helperDir
            };
            psi.ArgumentList.Add(ApplyArg);
            psi.ArgumentList.Add(zipPath);
            psi.ArgumentList.Add(installedDir);
            psi.ArgumentList.Add(exeName);
            psi.ArgumentList.Add(pid.ToString());

            // ✅ Flag to stop App.xaml.cs from redirecting helper back to install root
            psi.Environment["FF_UPDATER_HELPER"] = "1";

            Process.Start(psi);

            ApplyLog("Hard exiting current process now...");
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            ApplyLog("StartApplyUpdateAndExit exception: " + ex);
            log($"Launcher update: failed to start apply mode: {ex}");
        }
    }

    public static async Task<int> RunApplyUpdateModeAsync(string[] args, Action<string> log)
    {
        try
        {
            if (args.Length < 5 || !string.Equals(args[0], ApplyArg, StringComparison.OrdinalIgnoreCase))
                return -1;

            var zipPath = args[1];
            var targetDir = args[2];
            var exeName = args[3];
            var pidStr = args[4];

            log($"Apply mode: zip={zipPath}");
            log($"Apply mode: targetDir={targetDir}");
            log($"Apply mode: exeName={exeName}");
            log($"Apply mode: oldPid={pidStr}");

            if (!int.TryParse(pidStr, out var oldPid))
            {
                log($"Apply mode: invalid pid: {pidStr}");
                return 3;
            }

            log($"Apply mode: waiting for old PID {oldPid} to exit...");
            await WaitForProcessExitOrTimeoutAsync(oldPid, secondsTimeout: 30, log);

            if (!File.Exists(zipPath))
            {
                log($"Apply mode: zip missing: {zipPath}");
                return 4;
            }

            Directory.CreateDirectory(targetDir);

            var extractDir = Path.Combine(Path.GetTempPath(), $"FabriFrogeLauncher_extract_{Guid.NewGuid():N}");
            Directory.CreateDirectory(extractDir);

            log($"Apply mode: extracting zip -> {extractDir}");
            if (zipPath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            {
                await using var archiveStream = File.OpenRead(zipPath);
                using var gzip = new GZipStream(archiveStream, CompressionMode.Decompress);
                TarFile.ExtractToDirectory(gzip, extractDir, overwriteFiles: true);
            }
            else ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);

            // ✅ Use robocopy FIRST (better with transient locks)
            log($"Apply mode: robocopy mirror -> {targetDir}");
            var payloadRoot = FindPayloadRoot(extractDir, exeName);
            CopyDirectoryRecursive(payloadRoot, targetDir);

            // cleanup
            try { File.Delete(zipPath); } catch { }
            try { Directory.Delete(extractDir, recursive: true); } catch { }

            var newExePath = Path.Combine(targetDir, exeName);
            if (!File.Exists(newExePath))
            {
                var found = FindFirstExe(targetDir);
                if (!string.IsNullOrWhiteSpace(found))
                    newExePath = found!;
            }
            EnsureExecutable(newExePath);

            log($"Apply mode: relaunching: {newExePath}");

            var psi = new ProcessStartInfo
            {
                FileName = newExePath,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(newExePath) ?? targetDir
            };
            Process.Start(psi);

            return 0;
        }
        catch (Exception ex)
        {
            log($"Apply mode error: {ex}");
            return 1;
        }
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
