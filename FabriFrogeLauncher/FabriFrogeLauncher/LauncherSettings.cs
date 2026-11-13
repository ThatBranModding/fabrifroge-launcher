using System;
using System.IO;
using System.Text.Json;

namespace FabriFrogeLauncher;

public sealed class LauncherSettings
{
    public int RamMb { get; set; } = 8192;
    public string? InstallDirectory { get; set; }

    private const string FileName = "launcher_settings.json";

    public static string GetSettingsPath()
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FabriFroge");

        Directory.CreateDirectory(appData);
        return Path.Combine(appData, FileName);
    }

    public static LauncherSettings Load(Action<string>? log = null)
    {
        try
        {
            var path = GetSettingsPath();
            if (!File.Exists(path))
                return new LauncherSettings();

            var json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<LauncherSettings>(json);
            return loaded ?? new LauncherSettings();
        }
        catch (Exception ex)
        {
            log?.Invoke($"Settings load failed: {ex.Message}");
            return new LauncherSettings();
        }
    }

    public void Save(Action<string>? log = null)
    {
        try
        {
            var path = GetSettingsPath();
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(tmp, json);
            if (!OperatingSystem.IsWindows())
            {
                try { File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            log?.Invoke($"Settings save failed: {ex.Message}");
        }
    }

    public string GetResolvedInstallDirectory(string defaultInstallRoot)
    {
        if (!string.IsNullOrWhiteSpace(InstallDirectory))
            return InstallDirectory;

        return defaultInstallRoot;
    }
}
