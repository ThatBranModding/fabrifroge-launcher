using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CmlLib.Core.Auth;
using CmlLib.Core.Auth.Microsoft;

namespace FabriFrogeLauncher;

public static class AuthService
{
    private const string SessionFileName = "ms_session.json";

    // Primary location (THIS is what MainWindow should use)
    private static string GetPrimarySessionPath(string installRoot)
        => Path.Combine(installRoot, SessionFileName);

    // If you've ever saved it elsewhere previously, keep these as fallbacks.
    private static string[] GetCandidateSessionPaths(string installRoot)
        => new[]
        {
            // Primary
            GetPrimarySessionPath(installRoot),

            // Common "nested" case if installRoot accidentally included an extra FabriFroge
            Path.Combine(installRoot, "FabriFroge", SessionFileName),

            // Optional auth folder variant (if you used this in an earlier iteration)
            Path.Combine(installRoot, "auth", SessionFileName),
        };

    // DTO so we control JSON shape and stay compatible if MSession changes
    private sealed class SessionDto
    {
        public string? Username { get; set; }
        public string? UUID { get; set; }
        public string? AccessToken { get; set; }
        public string? ClientToken { get; set; }
        public string? UserType { get; set; }
    }

    public static MSession? TryLoadSession(string installRoot, Action<string> log)
    {
        try
        {
            var paths = GetCandidateSessionPaths(installRoot);

            foreach (var p in paths)
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

                // If it's clearly not valid, treat as missing
                if (string.IsNullOrWhiteSpace(dto.AccessToken) ||
                    string.IsNullOrWhiteSpace(dto.UUID) ||
                    string.IsNullOrWhiteSpace(dto.Username))
                {
                    log($"Auth cache incomplete (missing fields): {p}");
                    continue;
                }

                // Rebuild MSession
                var session = new MSession
                {
                    Username = dto.Username,
                    UUID = dto.UUID,
                    AccessToken = dto.AccessToken,
                    ClientToken = string.IsNullOrWhiteSpace(dto.ClientToken) ? Guid.NewGuid().ToString("N") : dto.ClientToken,
                    UserType = string.IsNullOrWhiteSpace(dto.UserType) ? "msa" : dto.UserType
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
        foreach (var p in GetCandidateSessionPaths(installRoot))
        {
            try
            {
                if (File.Exists(p))
                {
                    File.Delete(p);
                    log($"Deleted auth cache: {p}");
                }
            }
            catch (Exception ex)
            {
                log($"Failed to delete auth cache {p}: {ex.Message}");
            }
        }
    }

    public static bool HasCachedAccount(string installRoot)
    {
        foreach (var p in GetCandidateSessionPaths(installRoot))
            if (File.Exists(p)) return true;
        return false;
    }

    /// <summary>
    /// Microsoft sign-in using CmlLib.Core.Auth.Microsoft (JELoginHandler).
    /// This typically opens a browser/device-code flow.
    /// </summary>
    public static async Task<MSession> SignInMicrosoftAsync(string installRoot, Action<string> log, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            log("Starting Microsoft sign-in...");

            // Build the default Java Edition login handler
            // Docs: JELoginHandlerBuilder.BuildDefault(); await Authenticate(); :contentReference[oaicite:1]{index=1}
            var loginHandler = JELoginHandlerBuilder.BuildDefault();

            // Authenticate (this may prompt / open browser)
            var session = await loginHandler.Authenticate().ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();

            if (session == null ||
                string.IsNullOrWhiteSpace(session.AccessToken) ||
                string.IsNullOrWhiteSpace(session.UUID) ||
                string.IsNullOrWhiteSpace(session.Username))
            {
                throw new InvalidOperationException("Microsoft sign-in did not return a valid session.");
            }

            // Save immediately (MainWindow also calls SaveSession, but this is safe and helps if callers forget)
            SaveSession(installRoot, session, log);

            log($"Microsoft sign-in OK: {session.Username}");
            return session;
        }
        catch (OperationCanceledException)
        {
            log("Microsoft sign-in cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            log($"Microsoft sign-in failed: {ex}");
            throw;
        }
    }
}
