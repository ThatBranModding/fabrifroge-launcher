Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "config.ps1")

function Fail([string]$message) { throw $message }
function Read-Secure([string]$prompt) {
  $sec = Read-Host -AsSecureString $prompt
  $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec)
  try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
  finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

if (-not (Test-Path $PackManifestDir)) { Fail "Pack manifest directory not found: $PackManifestDir" }
if (-not (Test-Path $PackFilesSha256Dir)) { Fail "Pack SHA-256 directory not found: $PackFilesSha256Dir" }
$manifest = Get-ChildItem -LiteralPath $PackManifestDir -File -Filter "*.json" |
  Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $manifest) { Fail "No pack manifests found in $PackManifestDir" }
$manifestData = Get-Content -LiteralPath $manifest.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
$versionProperty = $manifestData.PSObject.Properties["version"]
if (-not $versionProperty) { $versionProperty = $manifestData.PSObject.Properties["Version"] }
$packVersion = if ($versionProperty -and $versionProperty.Value) { [string]$versionProperty.Value } else { [IO.Path]::GetFileNameWithoutExtension($manifest.Name) }
if (-not (Test-Path $PackFilesSha256Dir)) { Fail "SHA-256 payload tree not found: $PackFilesSha256Dir" }

# Rewrite only the new-launcher manifest's payload URLs into its isolated v2
# namespace. The legacy launcher feed and payload paths stay untouched.
$legacyPayloadBase = "$PublicBaseUrl/launcher/files/sha256/"
$newPayloadBase = "$PublicBaseUrl$PackPath/files/sha256/"
if (-not $manifestData.files -or $manifestData.files.Count -eq 0) { Fail "Manifest has no files: $($manifest.FullName)" }
foreach ($file in $manifestData.files) {
  $url = [string]$file.url
  if ($url.StartsWith($legacyPayloadBase, [StringComparison]::OrdinalIgnoreCase)) {
    $relativePayloadPath = $url.Substring($legacyPayloadBase.Length)
  } elseif ($url.StartsWith($newPayloadBase, [StringComparison]::OrdinalIgnoreCase)) {
    $relativePayloadPath = $url.Substring($newPayloadBase.Length)
  } else {
    Fail "Unexpected payload URL in manifest: $url"
  }

  $payloadSegments = $relativePayloadPath -split '/'
  if ([string]::IsNullOrWhiteSpace($relativePayloadPath) -or $payloadSegments -contains '..' -or $payloadSegments -contains '.') {
    Fail "Unsafe payload path in manifest: $url"
  }
  $payloadPath = Join-Path $PackFilesSha256Dir ($payloadSegments -join '\')
  if (-not (Test-Path -LiteralPath $payloadPath -PathType Leaf)) {
    Fail "Payload referenced by manifest is missing: $payloadPath"
  }
  $file.url = $newPayloadBase + ($payloadSegments -join '/')
}

$out = Join-Path $PSScriptRoot "out"
New-Item -ItemType Directory -Force -Path $out | Out-Null
$latestPath = Join-Path $out "pack-latest.json"
@{ packId = $PackId; latestVersion = $packVersion; manifestUrl = "$PublicBaseUrl$PackPath/manifests/$packVersion.json" } |
  ConvertTo-Json | Set-Content -LiteralPath $latestPath -Encoding UTF8
$manifestOut = Join-Path $out "$packVersion.json"
$manifestData | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $manifestOut -Encoding UTF8

$winscp = if ($WinScpComPath -and (Test-Path $WinScpComPath)) { $WinScpComPath } else { (Get-Command WinSCP.com -ErrorAction Stop).Source }
if ($SftpHost -notmatch "^(?<user>[^@]+)@(?<host>.+)$") { Fail "SftpHost must use user@host format" }
$password = Read-Secure "SFTP password (input hidden)"
if (-not $WinScpHostKey) { Fail "Set a pinned WinScpHostKey in release/config.ps1 before publishing." }
$hostKey = " -hostkey=`"$WinScpHostKey`""
$scriptFile = Join-Path $env:TEMP "fabrifroge-publish-modpack.txt"
$logFile = Join-Path $env:TEMP "fabrifroge-publish-modpack.log"
$remote = $RemoteRoot.TrimEnd('/')
$commands = @(
  "option batch abort", "option confirm off", "option transfer binary",
  "open `"sftp://$($Matches.user):$password@$($Matches.host):$SshPort/`"$hostKey",
  "cd `"$remote`"",
  "option batch continue",
  "mkdir launcher", "mkdir launcher/v2", "mkdir launcher/v2/manifests", "mkdir launcher/v2/files", "mkdir launcher/v2/files/sha256",
  "option batch abort",
  "synchronize remote `"$PackFilesSha256Dir`" `"launcher/v2/files/sha256`"",
  "put `"$manifestOut`" `"launcher/v2/manifests/$packVersion.json`"",
  "put `"$latestPath`" `"launcher/v2/latest.json`"",
  "exit"
)
Set-Content -LiteralPath $scriptFile -Value ($commands -join "`r`n") -Encoding ASCII
try {
  & $winscp "/ini=nul" "/log=$logFile" "/script=$scriptFile"
  $winscpExitCode = $LASTEXITCODE
} finally {
  Remove-Item -LiteralPath $scriptFile -Force -ErrorAction SilentlyContinue
  $password = $null
}
if ($winscpExitCode -ne 0) { Fail "WinSCP pack publish failed. See $logFile" }
Write-Host "Published modpack $packVersion to $PackPath. The legacy /launcher/latest.json feed and launcher self-update feed were not changed."

if ($EnableCloudflarePurge -and $env:FABRIFROGE_CLOUDFLARE_API_TOKEN) {
  $body = @{ files = @("$PublicBaseUrl$PackPath/latest.json") } | ConvertTo-Json
  Invoke-RestMethod -Method Post -Uri "https://api.cloudflare.com/client/v4/zones/$CloudflareZoneId/purge_cache" `
    -Headers @{ Authorization = "Bearer $env:FABRIFROGE_CLOUDFLARE_API_TOKEN"; "Content-Type" = "application/json" } -Body $body | Out-Null
}
