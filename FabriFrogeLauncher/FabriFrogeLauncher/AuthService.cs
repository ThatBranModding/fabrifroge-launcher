using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CmlLib.Core.Auth;
using CmlLib.Core.Auth.Microsoft;
using Microsoft.Identity.Client;
using XboxAuthNet.Game.Msal;
using XboxAuthNet.Game.Msal.OAuth;

namespace FabriFrogeLauncher;

public static class AuthService
{
    private const string SessionFileName = "ms_session.json";

    // Optional debug toggle file (place inside installRoot)
    private const string AuthDebugToggleFile = "enable_auth_debug.txt";
    private const string AuthDebugReportFile = "auth_debug_report.txt";

    private static string GetMicrosoftClientId(string? configuredClientId)
    {
        var clientId = Environment.GetEnvironmentVariable("FABRIFROGE_MSAL_CLIENT_ID") ?? configuredClientId;
        if (string.IsNullOrWhiteSpace(clientId))
            throw new InvalidOperationException("Set the Microsoft app client ID in Settings before signing in. Register a public client app for personal Microsoft accounts and have its ID allow-listed for Minecraft Xbox authentication.");
        if (!Guid.TryParse(clientId.Trim(), out _))
            throw new InvalidOperationException("The Microsoft app client ID must be the GUID shown as Application (client) ID in the Microsoft app registration.");
        return clientId.Trim();
    }

    private static async Task<JELoginHandler> BuildLoginHandlerAsync(
        string? configuredClientId,
        Func<DeviceCodeResult, Task>? deviceCodeCallback = null)
    {
        var msal = await MsalClientHelper.BuildApplicationWithCache(GetMicrosoftClientId(configuredClientId)).ConfigureAwait(false);
        return new JELoginHandlerBuilder()
            .WithOAuthProvider(new MsalDeviceCodeProvider(msal, deviceCodeCallback ?? (_ => Task.CompletedTask)))
            .Build();
    }

    // ---- Paths ----

    private static string GetPrimarySessionPath(string installRoot)
        => Path.Combine(installRoot, SessionFileName);

    private static string[] GetCandidateSessionPaths(string installRoot)
        => new[]
        {
            GetPrimarySessionPath(installRoot),
            Path.Combine(installRoot, "FabriFroge", SessionFileName),
            Path.Combine(installRoot, "auth", SessionFileName),
        };

    // ---- Debug helpers ----

    private static bool IsAuthDebugEnabled(string installRoot)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(installRoot)) return false;
            return File.Exists(Path.Combine(installRoot, AuthDebugToggleFile));
        }
        catch
        {
            return false;
        }
    }

    private static void AppendAuthDebugReport(string installRoot, string text)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(installRoot)) return;

            Directory.CreateDirectory(installRoot);
            var path = Path.Combine(installRoot, AuthDebugReportFile);

            File.AppendAllText(
                path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch
        {
            // never let debug logging break auth flow
        }
    }

    // ---- DTO (stable json shape) ----

    private sealed class SessionDto
    {
        public string? Username { get; set; }
        public string? UUID { get; set; }
        public string? AccessToken { get; set; }
        public string? ClientToken { get; set; }
        public string? UserType { get; set; }
    }

    // ---- Session cache (launcher-owned only) ----

    public static MSession? TryLoadSession(string installRoot, Action<string> log)
    {
        try
        {
            foreach (var p in GetCandidateSessionPaths(installRoot))
            {
                log($"Auth cache check: {p}");
                if (!File.Exists(p))
                    continue;

                var json = File.ReadAllText(p);

                // Handle BOM if present
                json = json.TrimStart('\uFEFF', '\u200B');

                var dto = JsonSerializer.Deserialize<SessionDto>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (dto == null)
                {
                    log($"Auth cache found but could not parse: {p}");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(dto.AccessToken) ||
                    string.IsNullOrWhiteSpace(dto.UUID) ||
                    string.IsNullOrWhiteSpace(dto.Username))
                {
                    log($"Auth cache incomplete (missing fields): {p}");
                    continue;
                }

                var session = new MSession
                {
                    Username = dto.Username!,
                    UUID = dto.UUID!,
                    AccessToken = dto.AccessToken!,
                    ClientToken = string.IsNullOrWhiteSpace(dto.ClientToken)
                        ? Guid.NewGuid().ToString("N")
                        : dto.ClientToken!,
                    UserType = string.IsNullOrWhiteSpace(dto.UserType)
                        ? "msa"
                        : dto.UserType!
                };

                log($"Auth cache loaded from: {p} (user={session.Username})");
                return session;
            }

            log("No saved Microsoft session found in any candidate path.");
            return null;
        }
        catch (Exception ex)
        {
            log($"TryLoadSession failed: {ex}");
            return null;
        }
    }

    public static void SaveSession(string installRoot, MSession session, Action<string> log)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(installRoot))
                throw new ArgumentException("installRoot is empty", nameof(installRoot));

            Directory.CreateDirectory(installRoot);

            var dto = new SessionDto
            {
                Username = session.Username,
                UUID = session.UUID,
                AccessToken = session.AccessToken,
                ClientToken = session.ClientToken,
                UserType = session.UserType
            };

            var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            var primaryPath = GetPrimarySessionPath(installRoot);
            File.WriteAllText(primaryPath, json);
            log($"Saved Microsoft session to: {primaryPath}");

            // Optional: also write to auth/ (harmless, helps if you later move paths)
            var authDirPath = Path.Combine(installRoot, "auth");
            Directory.CreateDirectory(authDirPath);
            var secondary = Path.Combine(authDirPath, SessionFileName);
            File.WriteAllText(secondary, json);
            log($"Saved Microsoft session copy to: {secondary}");
        }
        catch (Exception ex)
        {
            log($"SaveSession failed: {ex}");
            throw;
        }
    }

    public static void ClearSavedSession(string installRoot, Action<string> log)
    {
        bool any = false;

        foreach (var p in GetCandidateSessionPaths(installRoot))
        {
            try
            {
                if (File.Exists(p))
                {
                    File.Delete(p);
                    any = true;
                    log($"Deleted auth cache: {p}");
                }
            }
            catch (Exception ex)
            {
                log($"Failed to delete auth cache {p}: {ex.Message}");
            }
        }

        if (!any)
            log("Auth cache already clear.");
    }

    public static bool HasCachedAccount(string installRoot)
    {
        foreach (var p in GetCandidateSessionPaths(installRoot))
            if (File.Exists(p)) return true;
        return false;
    }

    // ---- Sign-in / sign-out flows ----

    /// <summary>
    /// Standard sign-in. First tries default cached/silent flow.
    /// If that fails due to expired/bad cached auth, clears launcher + library auth state
    /// and retries once with forced interactive login.
    /// </summary>
    public static async Task<MSession> SignInMicrosoftAsync(
        string installRoot,
        Action<string> log,
        CancellationToken ct,
        Func<DeviceCodeResult, Task>? deviceCodeCallback = null,
        string? microsoftClientId = null)
    {
        ct.ThrowIfCancellationRequested();

        bool debug = IsAuthDebugEnabled(installRoot);
        if (debug)
        {
            log("Auth debug mode ENABLED (enable_auth_debug.txt found).");
            AppendAuthDebugReport(installRoot, "Auth debug mode ENABLED.");
        }

        try
        {
            log("Starting Microsoft sign-in...");
            return await TrySignInInternalAsync(installRoot, log, ct, debug, allowRecovery: true, deviceCodeCallback, microsoftClientId).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            log("Microsoft sign-in cancelled.");
            if (debug) AppendAuthDebugReport(installRoot, "Sign-in cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            log($"Microsoft sign-in failed: {ex}");
            if (debug)
            {
                AppendAuthDebugReport(installRoot, $"Sign-in FAILED: {ex}");
                await ReachabilityProbeAsync(log, installRoot, ct).ConfigureAwait(false);
                AppendAuthDebugReport(
                    installRoot,
                    "Player checklist: try phone hotspot; change DNS to 1.1.1.1; disable antivirus HTTPS scanning/web shield; disable IPv6 temporarily; reboot router/PC.");
            }
            throw;
        }
    }

    /// <summary>
    /// Signs out launcher-owned cached session file(s) and library/MSAL auth state.
    /// </summary>
    public static async Task SignOutAsync(string installRoot, Action<string> log, string? microsoftClientId = null)
    {
        ClearSavedSession(installRoot, log);

        try
        {
            var loginHandler = await BuildLoginHandlerAsync(microsoftClientId).ConfigureAwait(false);
            await loginHandler.Signout().ConfigureAwait(false);
            log("Microsoft library signout OK.");
        }
        catch (Exception ex)
        {
            log($"Microsoft library signout failed (continuing): {ex.Message}");
        }
    }

    /// <summary>
    /// Swap Accounts: signs out cached library auth state and forces interactive login UI.
    /// </summary>
    public static async Task<MSession> SwapAccountsAsync(
        string installRoot,
        Action<string> log,
        CancellationToken ct,
        Func<DeviceCodeResult, Task>? deviceCodeCallback = null,
        string? microsoftClientId = null)
    {
        ct.ThrowIfCancellationRequested();

        bool debug = IsAuthDebugEnabled(installRoot);
        if (debug)
        {
            log("Auth debug mode ENABLED (enable_auth_debug.txt found).");
            AppendAuthDebugReport(installRoot, "Auth debug mode ENABLED.");
            AppendAuthDebugReport(installRoot, "SwapAccounts flow started.");
        }

        // Clear launcher cache first
        ClearSavedSession(installRoot, log);

        try
        {
            log("Swap Accounts: signing out library auth state...");
            var loginHandler = await BuildLoginHandlerAsync(microsoftClientId, deviceCodeCallback).ConfigureAwait(false);

            try
            {
                await loginHandler.Signout().ConfigureAwait(false);
                log("Swap Accounts: signout OK.");
                if (debug) AppendAuthDebugReport(installRoot, "Swap Accounts: loginHandler.Signout() OK.");
            }
            catch (Exception ex)
            {
                log($"Swap Accounts: signout failed (continuing): {ex.Message}");
                if (debug) AppendAuthDebugReport(installRoot, $"Swap Accounts: Signout failed: {ex}");
            }

            ct.ThrowIfCancellationRequested();

            log("Swap Accounts: starting INTERACTIVE sign-in...");
            var session = await loginHandler.AuthenticateInteractively(ct).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();
            ValidateSessionOrThrow(session);

            log($"Swap Accounts: sign-in OK: {session.Username} ({session.UUID})");
            if (debug) AppendAuthDebugReport(installRoot, $"Swap Accounts: sign-in OK: {session.Username} ({session.UUID})");

            if (debug)
                await AuthProbeMinecraftProfileAsync(session.AccessToken!, log, installRoot, ct).ConfigureAwait(false);

            SaveSession(installRoot, session, log);
            return session;
        }
        catch (Exception ex)
        {
            log($"Swap Accounts failed: {ex}");
            if (debug)
            {
                AppendAuthDebugReport(installRoot, $"Swap Accounts FAILED: {ex}");
                await ReachabilityProbeAsync(log, installRoot, ct).ConfigureAwait(false);
            }
            throw;
        }
    }

    private static async Task<MSession> TrySignInInternalAsync(
        string installRoot,
        Action<string> log,
        CancellationToken ct,
        bool debug,
        bool allowRecovery,
        Func<DeviceCodeResult, Task>? deviceCodeCallback,
        string? microsoftClientId)
    {
        var loginHandler = await BuildLoginHandlerAsync(microsoftClientId, deviceCodeCallback).ConfigureAwait(false);

        try
        {
            log("Microsoft sign-in: attempting default auth flow...");
            var session = await loginHandler.Authenticate().ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();
            ValidateSessionOrThrow(session);

            log($"Microsoft sign-in OK: {session.Username} ({session.UUID})");
            if (debug) AppendAuthDebugReport(installRoot, $"Sign-in OK: {session.Username} ({session.UUID})");

            if (debug)
                await AuthProbeMinecraftProfileAsync(session.AccessToken!, log, installRoot, ct).ConfigureAwait(false);

            SaveSession(installRoot, session, log);
            return session;
        }
        catch (Exception ex) when (allowRecovery && IsRecoverableAuthExpiry(ex))
        {
            log("Microsoft sign-in: cached auth appears expired/bad. Clearing saved auth and retrying with interactive sign-in...");
            if (debug) AppendAuthDebugReport(installRoot, $"Recoverable auth expiry detected: {ex}");

            await ResetLibraryAndLauncherAuthStateAsync(installRoot, log, debug, microsoftClientId).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();

            log("Microsoft sign-in: starting forced interactive sign-in...");
            var retryHandler = await BuildLoginHandlerAsync(microsoftClientId, deviceCodeCallback).ConfigureAwait(false);
            var retrySession = await retryHandler.AuthenticateInteractively(ct).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();
            ValidateSessionOrThrow(retrySession);

            log($"Microsoft sign-in recovery OK: {retrySession.Username} ({retrySession.UUID})");
            if (debug) AppendAuthDebugReport(installRoot, $"Recovery sign-in OK: {retrySession.Username} ({retrySession.UUID})");

            if (debug)
                await AuthProbeMinecraftProfileAsync(retrySession.AccessToken!, log, installRoot, ct).ConfigureAwait(false);

            SaveSession(installRoot, retrySession, log);
            return retrySession;
        }
    }

    private static async Task ResetLibraryAndLauncherAuthStateAsync(
        string installRoot,
        Action<string> log,
        bool debug,
        string? microsoftClientId)
    {
        ClearSavedSession(installRoot, log);

        try
        {
            var loginHandler = await BuildLoginHandlerAsync(microsoftClientId).ConfigureAwait(false);
            await loginHandler.Signout().ConfigureAwait(false);
            log("Auth recovery: library signout OK.");
            if (debug) AppendAuthDebugReport(installRoot, "Auth recovery: library signout OK.");
        }
        catch (Exception ex)
        {
            log($"Auth recovery: library signout failed (continuing): {ex.Message}");
            if (debug) AppendAuthDebugReport(installRoot, $"Auth recovery: library signout failed: {ex}");
        }
    }

    private static bool IsRecoverableAuthExpiry(Exception ex)
    {
        var text = ex.ToString();

        return text.Contains("token_expired", StringComparison.OrdinalIgnoreCase)
            || text.Contains("XASU error=token_expired", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Interactive Microsoft authentication is required", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Cached RefreshToken", StringComparison.OrdinalIgnoreCase)
            || text.Contains("expired", StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateSessionOrThrow(MSession? session)
    {
        if (session == null ||
            string.IsNullOrWhiteSpace(session.AccessToken) ||
            string.IsNullOrWhiteSpace(session.UUID) ||
            string.IsNullOrWhiteSpace(session.Username))
        {
            throw new InvalidOperationException("Microsoft sign-in did not return a valid session.");
        }
    }

    // ---- Debug-only probes ----

    private static async Task AuthProbeMinecraftProfileAsync(string accessToken, Action<string> log, string installRoot, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("FabriFrogeLauncher/AuthDebug");

            var req = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            var line = $"Auth probe: GET /minecraft/profile => {(int)resp.StatusCode} {resp.ReasonPhrase}";
            log(line);
            AppendAuthDebugReport(installRoot, line);

            string body = "";
            try { body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); } catch { }

            if (!string.IsNullOrWhiteSpace(body))
            {
                var snippet = body.Length > 300 ? body.Substring(0, 300) : body;
                AppendAuthDebugReport(installRoot, $"Auth probe body (first 300): {snippet}");
            }
        }
        catch (Exception ex)
        {
            var line = $"Auth probe exception: {ex.Message}";
            log(line);
            AppendAuthDebugReport(installRoot, line);
        }
    }

    private static async Task ReachabilityProbeAsync(Action<string> log, string installRoot, CancellationToken ct)
    {
        var urls = new[]
        {
            "https://api.minecraftservices.com/",
            "https://api.minecraftservices.com/minecraft/profile",
            "https://user.auth.xboxlive.com/",
            "https://xsts.auth.xboxlive.com/"
        };

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("FabriFrogeLauncher/AuthDebug");

            foreach (var url in urls)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

                    var line = $"Reachability probe: {url} => {(int)resp.StatusCode} {resp.ReasonPhrase}";
                    log(line);
                    AppendAuthDebugReport(installRoot, line);
                }
                catch (Exception ex)
                {
                    var line = $"Reachability probe FAILED: {url} => {ex.Message}";
                    log(line);
                    AppendAuthDebugReport(installRoot, line);
                }
            }
        }
        catch (Exception ex)
        {
            var line = $"Reachability probe exception: {ex.Message}";
            log(line);
            AppendAuthDebugReport(installRoot, line);
        }
    }
}
