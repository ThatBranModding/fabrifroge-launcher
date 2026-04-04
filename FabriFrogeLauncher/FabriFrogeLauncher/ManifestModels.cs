using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FabriFrogeLauncher;

// ===== Manifest / API Models ONLY =====
// Do NOT put Installer / InstallProgress / FileIndex etc. in here,
// because they already exist in Installer.cs and will cause duplicate-type errors.

public sealed class PackManifest
{
    [JsonPropertyName("packId")]
    public string PackId { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("minecraft")]
    public string Minecraft { get; set; } = "";

    [JsonPropertyName("loader")]
    public LoaderInfo? Loader { get; set; }

    [JsonPropertyName("instance")]
    public InstanceInfo? Instance { get; set; }

    [JsonPropertyName("managedRoots")]
    public List<string> ManagedRoots { get; set; } = new();

    [JsonPropertyName("files")]
    public List<ManifestFile> Files { get; set; } = new();

    // IMPORTANT: this is what makes your launcher see the remove list.
    // Your Installer.GetRemoveEntries(...) will pick this up.
    [JsonPropertyName("remove")]
    public List<string> Remove { get; set; } = new();
}

public sealed class ManifestFile
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";
}

public sealed class LoaderInfo
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";
}

public sealed class InstanceInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("prismInstanceId")]
    public string PrismInstanceId { get; set; } = "";

    [JsonPropertyName("recommendedRamMb")]
    public int RecommendedRamMb { get; set; }

    [JsonPropertyName("javaMajor")]
    public int JavaMajor { get; set; }
}

// Optional: only keep this if your code references LatestInfo.
// If you already have a LatestInfo model elsewhere, delete this block.
public sealed class LatestInfo
{
    // What your MainWindow.xaml.cs is asking for:
    [JsonPropertyName("latestVersion")]
    public string LatestVersion { get; set; } = "";

    // Common extra fields (safe to have even if the JSON doesn’t include them)
    [JsonPropertyName("packId")]
    public string PackId { get; set; } = "";

    [JsonPropertyName("manifestUrl")]
    public string ManifestUrl { get; set; } = "";
}