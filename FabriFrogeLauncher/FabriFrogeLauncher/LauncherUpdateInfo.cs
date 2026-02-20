namespace FabriFrogeLauncher;

public sealed class LauncherUpdateInfo
{
    // Example: "0.0.4"
    public string? Version { get; set; }

    // Direct download URL to a ZIP containing your published launcher files
    public string? Url { get; set; }

    // Optional but strongly recommended: lowercase hex SHA256 of the ZIP
    public string? Sha256 { get; set; }
}
