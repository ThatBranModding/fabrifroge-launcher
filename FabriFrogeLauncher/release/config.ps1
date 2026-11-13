# Shared, safe defaults for release scripts. Put machine-specific overrides in
# config.local.ps1 (ignored by Git) or set the corresponding FABRIFROGE_* env vars.
$PackId = if ($env:FABRIFROGE_PACK_ID) { $env:FABRIFROGE_PACK_ID } else { "fabrifroge-cobblemon" }
$RemoteRoot = if ($env:FABRIFROGE_SFTP_ROOT) { $env:FABRIFROGE_SFTP_ROOT } else { "/" }
$SftpHost = if ($env:FABRIFROGE_SFTP_HOST) { $env:FABRIFROGE_SFTP_HOST } else { "launcher@ftp.fabrifroge.com" }
$SshPort = if ($env:FABRIFROGE_SFTP_PORT) { [int]$env:FABRIFROGE_SFTP_PORT } else { 22 }
$LauncherSelfPath = "/launcher/v2/self"
$PackPath = "/launcher/v2"
$WinScpHostKey = "ssh-ed25519 255 jygYqLouvUiCQUTXy1r0EWmRXpoWSO/OHCPVwOBPiZg"
$WinScpComPath = $env:FABRIFROGE_WINSCP_PATH
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$LauncherProject = Join-Path $RepoRoot "FabriFrogeLauncher\FabriFrogeLauncher.csproj"
$PublicBaseUrl = "https://files.fabrifroge.com"
$PackManifestDir = if ($env:FABRIFROGE_PACK_MANIFEST_DIR) { $env:FABRIFROGE_PACK_MANIFEST_DIR } else { Join-Path $RepoRoot "out\manifests" }
$PackFilesSha256Dir = if ($env:FABRIFROGE_PACK_FILES_DIR) { $env:FABRIFROGE_PACK_FILES_DIR } else { Join-Path $RepoRoot "out\files\sha256" }
$EnableCloudflarePurge = $false
$CloudflareZoneId = "c9edd170c58b59c9bb60ffb5d960f5a4"
$CloudflareApiToken = $env:FABRIFROGE_CLOUDFLARE_API_TOKEN
$PurgeUrls = @("$PublicBaseUrl$PackPath/latest.json")

$localConfig = Join-Path $PSScriptRoot "config.local.ps1"
if (Test-Path -LiteralPath $localConfig) { . $localConfig }
