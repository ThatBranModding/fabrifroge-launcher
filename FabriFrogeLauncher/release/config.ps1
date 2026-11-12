# release/config.ps1

# -----------------------------
# REQUIRED: your release config
# -----------------------------

# Pack identity
$PackId = "fabrifroge-neoforge-1.21.1"

# Remote root in SFTP (you land in / in FileZilla)
$RemoteRoot = "/"

# SFTP target (user@host)
$SftpHost = "launcher@ftp.fabrifroge.com"

# SSH/SFTP port
$SshPort = 22

$LauncherSelfPath = "/launcher/v2/self"
$PackPath         = "/launcher/v2"

$WinScpHostKey = "ssh-ed25519 255 jygYqLouvUiCQUTXy1r0EWmRXpoWSO/OHCPVwOBPiZg"

# Where your server serves files.fabrifroge.com from (on the server filesystem)
$RemoteRoot = "/C:/Users/Branserver/Downloads/nginx-1.26.3/www/files"

# Optional: if ISCC.exe not in default location
$IsccPath = "C:\Users\branj\AppData\Local\Programs\Inno Setup 6\ISCC.exe"




# WinSCP path (optional if default works)
$WinScpPath = "C:\Program Files (x86)\WinSCP\WinSCP.com"

$WinScpComPath = "C:\Program Files (x86)\WinSCP\WinSCP.com"


# SSH private key path (RECOMMENDED for one-click)
# Leave "" if you are NOT using a key (password login).
$SshKeyPath = ""


# Local paths
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

# If your .csproj is NOT in repo root, set the correct relative path here:
# Example if it lives in /FabriFrogeLauncher/FabriFrogeLauncher.csproj:
$LauncherProject = Join-Path $RepoRoot "FabriFrogeLauncher\FabriFrogeLauncher.csproj"

# Public base URL (no trailing slash)
$PublicBaseUrl = "https://files.fabrifroge.com"

# Local inputs for modpack-only publishing. The payload directory is the SHA-256
# tree itself (prefix/hash subdirectories), not a per-pack-version directory.
# Override with environment variables if needed.
$PackManifestDir = if ($env:FABRIFROGE_PACK_MANIFEST_DIR) { $env:FABRIFROGE_PACK_MANIFEST_DIR } else { "F:\neofroge stuff\FF LAUNCHER\FabriFrogeBuild\output\manifests" }
$PackFilesSha256Dir = if ($env:FABRIFROGE_PACK_FILES_DIR) { $env:FABRIFROGE_PACK_FILES_DIR } else { "F:\neofroge stuff\FF LAUNCHER\FabriFrogeBuild\output\files\sha256" }

# Optional: Cloudflare purge
$EnableCloudflarePurge = $false
$CloudflareZoneId = "c9edd170c58b59c9bb60ffb5d960f5a4"
# Set FABRIFROGE_CLOUDFLARE_API_TOKEN in the publishing shell if cache purging is enabled.
$CloudflareApiToken = $env:FABRIFROGE_CLOUDFLARE_API_TOKEN

# JSON URLs that may need cache purging.
$PurgeUrls = @(
  "$PublicBaseUrl$PackPath/latest.json"
)
