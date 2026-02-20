using System;
using System.IO;
using System.Text.Json;

namespace FabriFrogeLauncher;

public sealed class LauncherSettings
{
    public int RamMb { get; set; } = 8192;

    private const string FileName = "launcher_settings.json";

    public static string GetSettingsPath(string installRoot)
    {
        // store alongside your install root so each install has its own settings
        Directory.CreateDirectory(installRoot);
        return Path.Combine(installRoot, FileName);
    }

    public static LauncherSettings Load(string installRoot, Action<string>? log = null)
    {
        try
        {
            var path = GetSettingsPath(installRoot);
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

    public void Save(string installRoot, Action<string>? log = null)
    {
        try
        {
            var path = GetSettingsPath(installRoot);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            log?.Invoke($"Settings save failed: {ex.Message}");
        }
    }
}
