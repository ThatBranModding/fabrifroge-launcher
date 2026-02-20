using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.ProcessBuilder;

namespace FabriFrogeLauncher;

public static class GameEngine
{
    private static (string javaExe, string javawExe) ResolveJava(string installRoot)
    {
        // NOTE: you discovered your runtime path is FabriFroge\FabriFroge\runtimes\temurin-21\bin
        var candidates = new[]
        {
            Path.Combine(installRoot, "runtimes", "temurin-21", "bin"),
            Path.Combine(installRoot, "runtimes", "temurin21", "bin"),
            Path.Combine(installRoot, "runtimes", "java21", "bin"),

            Path.Combine(installRoot, "FabriFroge", "runtimes", "temurin-21", "bin"),
            Path.Combine(installRoot, "FabriFroge", "runtimes", "temurin21", "bin"),
            Path.Combine(installRoot, "FabriFroge", "runtimes", "java21", "bin"),
        };

        foreach (var dir in candidates)
        {
            var java = Path.Combine(dir, "java.exe");
            var javaw = Path.Combine(dir, "javaw.exe");
            if (File.Exists(java) && File.Exists(javaw))
                return (java, javaw);
        }

        throw new Exception(
            "Java runtime missing. Could not locate java.exe/javaw.exe under:\n" +
            string.Join("\n", candidates));
    }

    /// <summary>
    /// Builds a safe, modern set of JVM args for Java 21 + modded Minecraft.
    /// Conservative defaults (no flags that commonly break systems).
    /// </summary>
    private static List<string> BuildDefaultJvmArgs(int ramMb)
    {
        // Clamp to sane bounds
        int xmx = Math.Clamp(ramMb, 2048, 65536);
        int xms = Math.Clamp(xmx / 2, 1024, xmx);

        return new List<string>
        {
            // Memory
            $"-Xms{xms}M",
            $"-Xmx{xmx}M",

            // GC / performance defaults
            "-XX:+UseG1GC",
            "-XX:+ParallelRefProcEnabled",
            "-XX:MaxGCPauseMillis=200",
            "-XX:+UnlockExperimentalVMOptions",
            "-XX:+DisableExplicitGC",
            "-XX:+AlwaysPreTouch",

            "-XX:G1NewSizePercent=20",
            "-XX:G1MaxNewSizePercent=60",
            "-XX:G1HeapRegionSize=16M",
            "-XX:G1ReservePercent=20",
            "-XX:InitiatingHeapOccupancyPercent=15",

            // Mostly harmless QoL / stability
            "-Dsun.rmi.dgc.server.gcInterval=2147483646",
            "-Dsun.rmi.dgc.client.gcInterval=2147483646",
            "-Dfile.encoding=UTF-8"
        };
    }

    /// <summary>
    /// Inject JVM args into the built java command line.
    /// We insert BEFORE "-cp" so the JVM consumes them (not Minecraft).
    /// </summary>
    private static void InjectJvmArgs(Process proc, IEnumerable<string> jvmArgs, Action<string> log)
    {
        if (proc?.StartInfo == null)
            return;

        var argsToAdd = string.Join(" ", jvmArgs.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()));
        if (string.IsNullOrWhiteSpace(argsToAdd))
            return;

        var original = proc.StartInfo.Arguments ?? "";

        // Most Minecraft launches include "-cp" (or "-classpath")
        int cpIndex = IndexOfToken(original, " -cp ");
        if (cpIndex < 0)
            cpIndex = IndexOfToken(original, " -classpath ");

        string updated;
        if (cpIndex >= 0)
        {
            // Insert right before -cp
            updated = original.Insert(cpIndex, " " + argsToAdd);
        }
        else
        {
            // Fallback: prepend after any initial whitespace
            updated = argsToAdd + " " + original;
        }

        proc.StartInfo.Arguments = updated;

        log("Injected JVM args into java command line (before -cp).");
    }

    private static int IndexOfToken(string haystack, string token)
    {
        return haystack.IndexOf(token, StringComparison.OrdinalIgnoreCase);
    }

    public static async Task LaunchAsync(
        HttpClient http,
        string installRoot,
        PackManifest manifest,
        MSession? sessionOrNull,
        string offlineUsernameFallback,
        int ramMb,
        Action<string> log,
        CancellationToken ct)
    {
        var instanceRoot = Installer.GetInstanceRoot(installRoot, manifest.PackId);
        var mcDir = Path.Combine(instanceRoot, ".minecraft");
        Directory.CreateDirectory(mcDir);

        var (javaExe, javawExe) = ResolveJava(installRoot);
        log($"Using Java: {javaExe}");

        // Ensure NeoForge installed into THIS instance’s .minecraft
        if (manifest.Loader?.Type?.Equals("neoforge", StringComparison.OrdinalIgnoreCase) == true)
        {
            await NeoForgeClientInstaller.EnsureInstalledAsync(
                http,
                mcDir,
                javaExe,
                manifest.Loader.Version,
                log,
                ct);
        }

        var path = new MinecraftPath(mcDir);
        var launcher = new MinecraftLauncher(path);

        var versionId = manifest.Loader?.Type?.Equals("neoforge", StringComparison.OrdinalIgnoreCase) == true
            ? NeoForgeClientInstaller.GetInstalledVersionId(manifest.Loader.Version)
            : manifest.Minecraft;

        log($"Preparing launch version: {versionId}");

        // Ensure vanilla assets/libs exist
        await launcher.InstallAsync(manifest.Minecraft);

        var session = sessionOrNull ?? MSession.CreateOfflineSession(offlineUsernameFallback);

        // Build JVM args (includes Xms/Xmx)
        var jvmArgs = BuildDefaultJvmArgs(ramMb);

        log($"RAM selected: {ramMb} MB");
        log("JVM Args:");
        foreach (var a in jvmArgs)
            log($"  {a}");

        // IMPORTANT: Use javaw for no console window
        var opt = new MLaunchOption
        {
            Session = session,
            MaximumRamMb = ramMb,
            JavaPath = javawExe
        };

        var proc = await launcher.BuildProcessAsync(versionId, opt);

        // Inject our JVM args into the final command line (since your CmlLib lacks JVMArguments)
        InjectJvmArgs(proc, jvmArgs, log);

        log("Starting Minecraft...");
        proc.Start();
    }
}
