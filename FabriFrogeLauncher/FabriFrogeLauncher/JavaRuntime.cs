using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

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
    private static string MetadataUrl()
        => $"https://api.adoptium.net/v3/assets/latest/21/hotspot?architecture={ArchitectureName()}&image_type=jre&os={PlatformName()}&vendor=eclipse";

    public static async Task EnsureTemurin21Async(HttpClient http, string installRoot, Action<string> log, CancellationToken ct)
    {
        var root = RuntimeRoot(installRoot);
        var java = JavaPath(installRoot);
        var marker = Path.Combine(root, ".complete");
        if (File.Exists(java) && File.Exists(marker))
        {
            log("Java 21 runtime already present.");
            return;
        }
        Directory.CreateDirectory(root);
        var archive = Path.Combine(root, OperatingSystem.IsWindows() ? "temurin21.zip" : "temurin21.tar.gz");
        var extract = Path.Combine(root, "_extract-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var metadataResponse = await http.GetAsync(MetadataUrl(), ct);
            metadataResponse.EnsureSuccessStatusCode();
            using var metadata = JsonDocument.Parse(await metadataResponse.Content.ReadAsStringAsync(ct));
            var package = metadata.RootElement[0].GetProperty("binary").GetProperty("package");
            var url = package.GetProperty("link").GetString() ?? throw new InvalidDataException("Adoptium did not return a runtime package URL.");
            var expectedHash = package.GetProperty("checksum").GetString()?.Trim().ToLowerInvariant();
            if (expectedHash == null || expectedHash.Length != 64 || !expectedHash.All(Uri.IsHexDigit))
                throw new InvalidDataException("Adoptium did not return a valid SHA-256 checksum.");

            log($"Downloading verified Temurin JRE 21 for {PlatformName()} {ArchitectureName()}...");
            using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                await using var output = new FileStream(archive, FileMode.Create, FileAccess.Write, FileShare.None);
                await stream.CopyToAsync(output, ct);
                await output.FlushAsync(ct);
            }

            await using (var stream = File.OpenRead(archive))
            {
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
                if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual), Convert.FromHexString(expectedHash)))
                    throw new InvalidDataException("Temurin runtime SHA-256 verification failed.");
            }

            Directory.CreateDirectory(extract);
            if (OperatingSystem.IsWindows()) ZipFile.ExtractToDirectory(archive, extract);
            else
            {
                await using var fs = File.OpenRead(archive);
                using var gzip = new GZipStream(fs, CompressionMode.Decompress);
                TarFile.ExtractToDirectory(gzip, extract, overwriteFiles: true);
            }

            var javaName = OperatingSystem.IsWindows() ? "java.exe" : "java";
            var extractedJava = Directory.EnumerateFiles(extract, javaName, SearchOption.AllDirectories)
                .FirstOrDefault(path => Path.GetFileName(Path.GetDirectoryName(path)) == "bin")
                ?? throw new InvalidDataException("Temurin archive did not contain the Java executable.");
            var runtimeHome = Directory.GetParent(Path.GetDirectoryName(extractedJava)!)!.FullName;
            CopyDirectory(runtimeHome, root);
            if (!File.Exists(java)) throw new InvalidDataException("Temurin extraction did not produce the expected Java executable.");

            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(java, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            await File.WriteAllTextAsync(marker + ".tmp", expectedHash, ct);
            File.Move(marker + ".tmp", marker, overwrite: true);
            log("Java 21 runtime installed and checksum verified.");
        }
        catch
        {
            try { if (Directory.Exists(extract)) Directory.Delete(extract, true); } catch { }
            try { if (File.Exists(archive)) File.Delete(archive); } catch { }
            throw;
        }
        try { if (Directory.Exists(extract)) Directory.Delete(extract, true); } catch { }
        try { if (File.Exists(archive)) File.Delete(archive); } catch { }
    }

    private static void CopyDirectory(string source, string dest)
    {
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dest, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(dest, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }
}
