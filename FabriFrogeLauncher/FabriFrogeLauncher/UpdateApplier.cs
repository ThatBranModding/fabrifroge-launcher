using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace FabriFrogeLauncher;

public static class UpdateApplier
{
    /// <summary>
    /// Downloads the zip from zipUrl into a temp file then hands off to SelfUpdater apply mode.
    /// </summary>
    public static async Task ApplyZipUpdateAndRelaunchAsync(
        string zipUrl,
        Action<string> log,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(zipUrl))
            throw new ArgumentException("zipUrl is empty.", nameof(zipUrl));

        var tmpZip = Path.Combine(Path.GetTempPath(), $"FabriFrogeLauncher_update_{Guid.NewGuid():N}.zip");
        log($"[UPD] Downloading update zip -> {tmpZip}");
        log($"[UPD] URL: {zipUrl}");

        using var http = Net.CreateHttpClient(); // use your existing client factory
        using var resp = await http.GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        await using (var fs = new FileStream(tmpZip, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await resp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
        }

        log("[UPD] Download complete. Launching apply mode...");
        SelfUpdater.StartApplyUpdateAndExit(tmpZip, log);
    }
}
