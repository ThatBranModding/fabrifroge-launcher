using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FabriFrogeLauncher;

public sealed class InstallProgress
{
    public int FilesDone { get; set; }
    public int FilesTotal { get; set; }
    public long BytesDone { get; set; }
    public long BytesTotal { get; set; }

    public string CurrentFile { get; set; } = "";
}

internal sealed class FileIndex
{
    public int Schema { get; set; } = 1;
    public string PackId { get; set; } = "";
    public string Version { get; set; } = "";
    public Dictionary<string, FileIndexEntry> Files { get; set; } = new(StringComparer.Ordinal);
}

internal sealed class FileIndexEntry
{
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
}

public static class Installer
{
    private const int MaxParallelDownloads = 6;

    // Files that should NEVER be managed by the pack once created (player-owned settings)
    // Paths are relative to instanceRoot and use manifest-style slashes.
    private static readonly HashSet<string> PreservePaths = new(StringComparer.OrdinalIgnoreCase)
    {
        ".minecraft/options.txt",
        ".minecraft/optionsof.txt",
        ".minecraft/optionsshaders.txt",
    };

    private static bool IsPreserved(string manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath)) return false;
        // Normalize to forward slashes like your manifest uses
        var p = manifestPath.Replace('\\', '/').TrimStart('/');
        return PreservePaths.Contains(p);
    }

    public static string GetPackRoot(string installRoot, string packId)
        => Path.Combine(installRoot, "FabriFroge", "packs", packId);

    public static string GetInstanceRoot(string installRoot, string packId)
        => Path.Combine(GetPackRoot(installRoot, packId), "instance");

    private static string GetIndexPath(string packRoot)
        => Path.Combine(packRoot, "file_index.json");

    private static string GetLastManifestPath(string packRoot)
        => Path.Combine(packRoot, "last_manifest.json");

    public static async Task<bool> InstallOrUpdateAsync(
        HttpClient http,
        string installRoot,
        PackManifest manifest,
        Action<string> log,
        Action<InstallProgress> onProgress,
        CancellationToken ct,
        bool fullVerify = false)
    {
        var packRoot = GetPackRoot(installRoot, manifest.PackId);
        var instanceRoot = GetInstanceRoot(installRoot, manifest.PackId);

        Directory.CreateDirectory(packRoot);
        Directory.CreateDirectory(instanceRoot);

        var totalBytes = manifest.Files.Sum(f => f.Size);
        var progress = new InstallProgress
        {
            FilesTotal = manifest.Files.Count,
            BytesTotal = totalBytes,
            FilesDone = 0,
            BytesDone = 0
        };

        long bytesDone = 0;
        int filesDone = 0;
        object progressLock = new();

        void PushProgress()
        {
            lock (progressLock)
            {
                progress.BytesDone = bytesDone;
                progress.FilesDone = filesDone;
                onProgress(progress);
            }
        }

        var indexPath = GetIndexPath(packRoot);
        var index = LoadIndex(indexPath, manifest.PackId);

        // IMPORTANT: do not remove preserved files even if manifest says so
        ApplyRemoveList(GetRemoveEntries(manifest), instanceRoot, index, log);

        var work = new List<ManifestFile>();

        foreach (var f in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();

            var normalizedPath = (f.Path ?? "").Replace('\\', '/').TrimStart('/');
            var target = Path.Combine(instanceRoot, normalizedPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            // If this is a preserved file and it already exists, we NEVER overwrite it.
            // We also avoid re-downloading it on repair/update.
            if (IsPreserved(normalizedPath) && File.Exists(target))
            {
                // Keep the index entry aligned to what is actually on disk (so later runs don’t keep “wanting” it).
                try
                {
                    var existingHash = await Sha256HexOfFileAsync(target, ct);
                    var size = new FileInfo(target).Length;
                    index.Files[normalizedPath] = new FileIndexEntry { Sha256 = existingHash, Size = size };
                }
                catch
                {
                    // ignore; still preserve the file
                }

                Interlocked.Increment(ref filesDone);
                // Don’t count bytes here as “downloaded”; but progress should still reach 100,
                // so we add the manifest size as a progress approximation.
                Interlocked.Add(ref bytesDone, f.Size);
                PushProgress();
                continue;
            }

            // Fast path (no hashing) if NOT full verify
            if (!fullVerify && index.Files.TryGetValue(normalizedPath, out var idx))
            {
                if (string.Equals(idx.Sha256, f.Sha256, StringComparison.OrdinalIgnoreCase) &&
                    idx.Size == f.Size &&
                    File.Exists(target))
                {
                    Interlocked.Increment(ref filesDone);
                    Interlocked.Add(ref bytesDone, f.Size);
                    PushProgress();
                    continue;
                }
            }

            // If file exists, avoid download by hashing
            if (File.Exists(target))
            {
                var existingHash = await Sha256HexOfFileAsync(target, ct);
                if (string.Equals(existingHash, f.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    index.Files[normalizedPath] = new FileIndexEntry { Sha256 = f.Sha256, Size = f.Size };

                    Interlocked.Increment(ref filesDone);
                    Interlocked.Add(ref bytesDone, f.Size);
                    PushProgress();
                    continue;
                }
            }

            work.Add(f);
        }

        if (work.Count == 0)
        {
            WriteVersionMarker(packRoot, manifest.Version);

            index.Version = manifest.Version;
            SaveIndex(indexPath, index);

            SaveLastManifest(packRoot, manifest);

            lock (progressLock)
            {
                progress.BytesDone = totalBytes;
                progress.FilesDone = manifest.Files.Count;
                onProgress(progress);
            }

            log(fullVerify
                ? $"Repair complete (all files verified): {manifest.PackId} {manifest.Version}"
                : $"Install complete (already up to date): {manifest.PackId} {manifest.Version}");

            return false;
        }

        log($"{(fullVerify ? "Repairing" : "Updating")} {work.Count}/{manifest.Files.Count} files (parallel={MaxParallelDownloads})...");

        using var sem = new SemaphoreSlim(MaxParallelDownloads, MaxParallelDownloads);
        var tasks = new List<Task>(work.Count);

        foreach (var f in work)
        {
            ct.ThrowIfCancellationRequested();
            await sem.WaitAsync(ct);

            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    ct.ThrowIfCancellationRequested();

                    var normalizedPath = (f.Path ?? "").Replace('\\', '/').TrimStart('/');
                    var target = Path.Combine(instanceRoot, normalizedPath.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                    // Double-safety: never overwrite preserved files if they exist
                    if (IsPreserved(normalizedPath) && File.Exists(target))
                    {
                        Interlocked.Increment(ref filesDone);
                        PushProgress();
                        return;
                    }

                    var tmp = target + ".tmp";
                    if (File.Exists(tmp)) File.Delete(tmp);

                    await DownloadToFileAsync(
                        http,
                        f.Url,
                        tmp,
                        f.Size,
                        delta =>
                        {
                            Interlocked.Add(ref bytesDone, delta);
                            PushProgress();
                        },
                        ct,
                        normalizedPath);

                    var downloadedHash = await Sha256HexOfFileAsync(tmp, ct);
                    if (!string.Equals(downloadedHash, f.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        try { File.Delete(tmp); } catch { }
                        throw new Exception($"SHA256 mismatch for {normalizedPath}");
                    }

                    if (File.Exists(target)) File.Delete(target);
                    File.Move(tmp, target);

                    lock (index)
                    {
                        index.Files[normalizedPath] = new FileIndexEntry { Sha256 = f.Sha256, Size = f.Size };
                    }

                    Interlocked.Increment(ref filesDone);
                    PushProgress();
                }
                finally
                {
                    sem.Release();
                }
            }, ct));
        }

        await Task.WhenAll(tasks);

        if (bytesDone > totalBytes)
            bytesDone = totalBytes;

        WriteVersionMarker(packRoot, manifest.Version);

        index.Version = manifest.Version;
        SaveIndex(indexPath, index);

        SaveLastManifest(packRoot, manifest);

        lock (progressLock)
        {
            progress.BytesDone = bytesDone;
            progress.FilesDone = manifest.Files.Count;
            onProgress(progress);
        }

        log($"Install complete: {manifest.PackId} {manifest.Version}");
        return true;
    }

    private static void SaveLastManifest(string packRoot, PackManifest manifest)
    {
        try
        {
            var p = GetLastManifestPath(packRoot);
            var tmp = p + ".tmp";
            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(tmp, JsonSerializer.Serialize(manifest, opts));
            if (File.Exists(p)) File.Delete(p);
            File.Move(tmp, p);
        }
        catch
        {
            // ignore
        }
    }

    private static IReadOnlyList<string> GetRemoveEntries(PackManifest manifest)
    {
        try
        {
            var t = manifest.GetType();

            var prop =
                t.GetProperty("Remove", BindingFlags.Public | BindingFlags.Instance) ??
                t.GetProperty("remove", BindingFlags.Public | BindingFlags.Instance) ??
                t.GetProperty("RemoveFiles", BindingFlags.Public | BindingFlags.Instance) ??
                t.GetProperty("removeFiles", BindingFlags.Public | BindingFlags.Instance);

            if (prop?.GetValue(manifest) is IEnumerable<string> list)
                return list.ToList();

            var field =
                t.GetField("Remove", BindingFlags.Public | BindingFlags.Instance) ??
                t.GetField("remove", BindingFlags.Public | BindingFlags.Instance);

            if (field?.GetValue(manifest) is IEnumerable<string> flist)
                return flist.ToList();
        }
        catch { }

        return Array.Empty<string>();
    }

    private static void ApplyRemoveList(IReadOnlyList<string> removeEntries, string instanceRoot, FileIndex index, Action<string> log)
    {
        if (removeEntries == null || removeEntries.Count == 0)
            return;

        foreach (var rel in removeEntries)
        {
            try
            {
                var normalized = (rel ?? "").Replace('\\', '/').TrimStart('/');
                // Never remove preserved files
                if (IsPreserved(normalized))
                    continue;

                var target = Path.Combine(instanceRoot, normalized.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(target))
                {
                    File.Delete(target);
                    log($"Removed: {normalized}");
                }

                index.Files.Remove(normalized);
            }
            catch
            {
                // ignore
            }
        }
    }

    private static FileIndex LoadIndex(string indexPath, string packId)
    {
        try
        {
            if (!File.Exists(indexPath))
                return new FileIndex { PackId = packId };

            var json = File.ReadAllText(indexPath);
            var idx = JsonSerializer.Deserialize<FileIndex>(json) ?? new FileIndex();

            if (!string.Equals(idx.PackId, packId, StringComparison.Ordinal))
                return new FileIndex { PackId = packId };

            idx.PackId = packId;
            idx.Files ??= new Dictionary<string, FileIndexEntry>(StringComparer.Ordinal);
            return idx;
        }
        catch
        {
            return new FileIndex { PackId = packId };
        }
    }

    private static void SaveIndex(string indexPath, FileIndex index)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(indexPath)!);

            var opts = new JsonSerializerOptions { WriteIndented = true };
            var tmp = indexPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(index, opts));
            if (File.Exists(indexPath)) File.Delete(indexPath);
            File.Move(tmp, indexPath);
        }
        catch
        {
            // ignore
        }
    }

    private static void WriteVersionMarker(string packRoot, string version)
    {
        var finalPath = Path.Combine(packRoot, "current.txt");
        var tmpPath = finalPath + ".tmp";
        File.WriteAllText(tmpPath, version);
        if (File.Exists(finalPath)) File.Delete(finalPath);
        File.Move(tmpPath, finalPath);
    }

    private static async Task DownloadToFileAsync(
        HttpClient http,
        string url,
        string outPath,
        long expectedSize,
        Action<long> onBytes,
        CancellationToken ct,
        string logicalPath)
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"{(int)resp.StatusCode} {resp.ReasonPhrase} while downloading: {logicalPath}");

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        await using var fs = new FileStream(outPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);

        var buffer = new byte[128 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
        {
            await fs.WriteAsync(buffer, 0, read, ct);
            onBytes(read);
        }

        await fs.FlushAsync(ct);
        _ = expectedSize;
    }

    private static async Task<string> Sha256HexOfFileAsync(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(fs, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}