using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace FabriFrogeLauncher;

public static class JavaRuntime
{
    // You host this on your files site
    // Change the filename to match what you upload.
    private const string JreZipUrl = "https://files.fabrifroge.com/launcher/deps/temurin-jre21-win64.zip";

    public static string RuntimeRoot(string installRoot)
        => Path.Combine(installRoot, "FabriFroge", "runtimes", "temurin-21");

    public static string JavawPath(string installRoot)
        => Path.Combine(RuntimeRoot(installRoot), "bin", "javaw.exe");

    public static async Task EnsureTemurin21Async(HttpClient http, string installRoot, Action<string> log, CancellationToken ct)
    {
        var root = RuntimeRoot(installRoot);
        var javaw = JavawPath(installRoot);

        if (File.Exists(javaw))
        {
            log("Java 21 runtime already present.");
            return;
        }

        Directory.CreateDirectory(root);

        var tmpZip = Path.Combine(root, "temurin21.zip");
        if (File.Exists(tmpZip)) File.Delete(tmpZip);

        log("Downloading Temurin JRE 21...");
        await DownloadFileAsync(http, JreZipUrl, tmpZip, log, ct);

        log("Extracting Java runtime...");
        var extractTmp = Path.Combine(root, "_extract");
        if (Directory.Exists(extractTmp)) Directory.Delete(extractTmp, true);
        Directory.CreateDirectory(extractTmp);

        ZipFile.ExtractToDirectory(tmpZip, extractTmp);

        // The zip typically contains a single top-level folder like:
        // jdk-21.x... or jre-21.x...
        var topDirs = Directory.GetDirectories(extractTmp);
        if (topDirs.Length == 0)
            throw new Exception("JRE zip had no top-level folder.");

        var extractedHome = topDirs[0];

        // Move contents into root (so root/bin/javaw.exe exists)
        CopyDirectory(extractedHome, root);

        // Cleanup
        try { File.Delete(tmpZip); } catch { }
        try { Directory.Delete(extractTmp, true); } catch { }

        if (!File.Exists(javaw))
            throw new Exception("Java runtime install finished, but javaw.exe not found.");

        log("Java 21 runtime installed.");
    }

    private static async Task DownloadFileAsync(HttpClient http, string url, string outPath, Action<string> log, CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        await using var fs = new FileStream(outPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);

        var buffer = new byte[1024 * 128];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            if (read <= 0) break;
            await fs.WriteAsync(buffer.AsMemory(0, read), ct);
        }

        await fs.FlushAsync(ct);
        log("Java zip downloaded.");
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        foreach (var dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceDir, dir);
            Directory.CreateDirectory(Path.Combine(destDir, rel));
        }

        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceDir, file);
            var dest = Path.Combine(destDir, rel);

            var parent = Path.GetDirectoryName(dest);
            if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);

            File.Copy(file, dest, overwrite: true);
        }
    }
}
