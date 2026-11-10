using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;

namespace FabriFrogeLauncher;

public static class JavaRuntime
{
    public static string RuntimeRoot(string installRoot) => Path.Combine(installRoot, "FabriFroge", "runtimes", "temurin-21");
    public static string JavaPath(string installRoot) => Path.Combine(RuntimeRoot(installRoot), "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
    public static string JavawPath(string installRoot) => JavaPath(installRoot);

    private static string PlatformName() => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "mac" : "linux";
    private static string ArchitectureName() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x64", Architecture.Arm64 => "aarch64", Architecture.X86 => "x86",
        _ => throw new PlatformNotSupportedException($"Temurin 21 is not available for {RuntimeInformation.ProcessArchitecture}.")
    };
    private static string RuntimeUrl() => $"https://api.adoptium.net/v3/binary/latest/21/ga/{PlatformName()}/{ArchitectureName()}/jre/hotspot/normal/eclipse";

    public static async Task EnsureTemurin21Async(HttpClient http, string installRoot, Action<string> log, CancellationToken ct)
    {
        var root = RuntimeRoot(installRoot);
        var java = JavaPath(installRoot);
        if (File.Exists(java)) { log("Java 21 runtime already present."); return; }
        Directory.CreateDirectory(root);
        var archive = Path.Combine(root, OperatingSystem.IsWindows() ? "temurin21.zip" : "temurin21.tar.gz");
        log($"Downloading Temurin JRE 21 for {PlatformName()} {ArchitectureName()}...");
        using (var response = await http.GetAsync(RuntimeUrl(), HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            await using var output = new FileStream(archive, FileMode.Create, FileAccess.Write, FileShare.None);
            await stream.CopyToAsync(output, ct);
        }
        var temp = Path.Combine(root, "_extract");
        if (Directory.Exists(temp)) Directory.Delete(temp, true);
        Directory.CreateDirectory(temp);
        if (OperatingSystem.IsWindows()) ZipFile.ExtractToDirectory(archive, temp);
        else
        {
            await using var fs = File.OpenRead(archive);
            using var gzip = new GZipStream(fs, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gzip, temp, overwriteFiles: true);
        }
        var home = Directory.GetDirectories(temp).FirstOrDefault() ?? temp;
        CopyDirectory(home, root);
        File.Delete(archive); Directory.Delete(temp, true);
        if (!File.Exists(java)) throw new InvalidOperationException("Temurin extraction completed, but Java was not found at " + java);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(java, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        log("Java 21 runtime installed.");
    }

    private static void CopyDirectory(string source, string dest)
    {
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(Path.Combine(dest, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(dest, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target, true);
        }
    }
}
