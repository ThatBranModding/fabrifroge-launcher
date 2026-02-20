using System.Text.Json.Serialization;

namespace FabriFrogeLauncher;

public sealed class LatestInfo
{
    [JsonPropertyName("packId")] public string PackId { get; set; } = "";
    [JsonPropertyName("latestVersion")] public string LatestVersion { get; set; } = "";
    [JsonPropertyName("manifestUrl")] public string ManifestUrl { get; set; } = "";
    [JsonPropertyName("minLauncherVersion")] public string MinLauncherVersion { get; set; } = "";
    [JsonPropertyName("notes")] public string Notes { get; set; } = "";
}

public sealed class PackManifest
{
    [JsonPropertyName("packId")] public string PackId { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("minecraft")] public string Minecraft { get; set; } = "";
    [JsonPropertyName("loader")] public LoaderInfo Loader { get; set; } = new();
    [JsonPropertyName("instance")] public InstanceInfo Instance { get; set; } = new();
    [JsonPropertyName("managedRoots")] public string[] ManagedRoots { get; set; } = Array.Empty<string>();
    [JsonPropertyName("files")] public List<ManifestFile> Files { get; set; } = new();
}

public sealed class LoaderInfo
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
}

public sealed class InstanceInfo
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("prismInstanceId")] public string PrismInstanceId { get; set; } = "";
    [JsonPropertyName("recommendedRamMb")] public int RecommendedRamMb { get; set; }
    [JsonPropertyName("javaMajor")] public int JavaMajor { get; set; }
}

public sealed class ManifestFile
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("url")] public string Url { get; set; } = "";
}
