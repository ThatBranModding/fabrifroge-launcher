using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace FabriFrogeLauncher;

public sealed class LaunchProfiles
{
    // launcher_profiles.json root structure (we keep unknown fields via JsonExtensionData)
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    [JsonPropertyName("profiles")]
    public Dictionary<string, LauncherProfile> Profiles { get; set; } = new();

    public static async Task<LaunchProfiles> LoadAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            return new LaunchProfiles();

        var json = await File.ReadAllTextAsync(path, ct);
        var obj = JsonSerializer.Deserialize<LaunchProfiles>(json) ?? new LaunchProfiles();

        obj.Profiles ??= new();
        return obj;
    }

    public async Task SaveAsync(string path, CancellationToken ct)
    {
        var opts = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        var json = JsonSerializer.Serialize(this, opts);
        await File.WriteAllTextAsync(path, json, ct);
    }

    public void UpsertProfile(string id, string name, string gameDir, string javaw, PackManifest manifest)
    {
        // IMPORTANT: For NeoForge, Minecraft Launcher typically uses versionId of the installed loader profile.
        // We can’t “install NeoForge into the official launcher” reliably without running its installer.
        // For now we create a profile that sets gameDir + java, and expects the user to have a matching version already.
        // Next step we’ll add: auto-download NeoForge installer and run it silently.
        var p = new LauncherProfile
        {
            Name = name,
            GameDir = gameDir,
            JavaDir = javaw,
            Type = "custom",
            Created = DateTime.UtcNow.ToString("o"),
            LastUsed = DateTime.UtcNow.ToString("o"),
            Icon = "Furnace", // placeholder; can be custom later
            LastVersionId = manifest.Minecraft, // will be replaced once we install NeoForge properly
        };

        Profiles[id] = p;
    }
}

public sealed class LauncherProfile
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("created")]
    public string? Created { get; set; }

    [JsonPropertyName("lastUsed")]
    public string? LastUsed { get; set; }

    [JsonPropertyName("icon")]
    public string? Icon { get; set; }

    [JsonPropertyName("gameDir")]
    public string? GameDir { get; set; }

    [JsonPropertyName("javaDir")]
    public string? JavaDir { get; set; }

    [JsonPropertyName("lastVersionId")]
    public string? LastVersionId { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}
