# release/publish-release.ps1
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Info($m) { Write-Host "[INFO] $m" -ForegroundColor Cyan }
function Warn($m) { Write-Host "[WARN] $m" -ForegroundColor Yellow }
function Fail($m) { Write-Host "[ERROR] $m" -ForegroundColor Red; throw $m }

# Load config
. (Join-Path $PSScriptRoot "config.ps1")

# Optional config vars (safe defaults)
if (-not (Get-Variable -Name WinScpHostKey -Scope Global -ErrorAction SilentlyContinue)) { $WinScpHostKey = "" }
if (-not (Get-Variable -Name WinScpComPath  -Scope Global -ErrorAction SilentlyContinue)) { $WinScpComPath  = "" }
if (-not (Get-Variable -Name InnoSetupPath  -Scope Global -ErrorAction SilentlyContinue)) { $InnoSetupPath  = "" }

# NEW: default pack artifact locations (override in config.ps1 if you want)
if (-not (Get-Variable -Name PackManifestDir -Scope Global -ErrorAction SilentlyContinue) -or [string]::IsNullOrWhiteSpace($PackManifestDir)) {
  $PackManifestDir = "F:\neofroge stuff\FF LAUNCHER\FabriFrogeBuild\output\manifests"
}
if (-not (Get-Variable -Name PackFilesSha256Dir -Scope Global -ErrorAction SilentlyContinue) -or [string]::IsNullOrWhiteSpace($PackFilesSha256Dir)) {
  $PackFilesSha256Dir = "F:\neofroge stuff\FF LAUNCHER\FabriFrogeBuild\output\files\sha256"
}

function Find-WinScpCom {
  if (-not [string]::IsNullOrWhiteSpace($WinScpComPath) -and (Test-Path $WinScpComPath)) {
    return $WinScpComPath
  }

  $cmd = Get-Command "WinSCP.com" -ErrorAction SilentlyContinue
  if ($cmd) { return $cmd.Source }

  $p1 = "C:\Program Files (x86)\WinSCP\WinSCP.com"
  if (Test-Path $p1) { return $p1 }

  Fail "Required tool not found: WinSCP.com. Install WinSCP and ensure WinSCP.com is on PATH (or set `$WinScpComPath in release/config.ps1)."
}

function Find-ISCC {
  # If config.ps1 set a path, accept either folder or ISCC.exe
  if (-not [string]::IsNullOrWhiteSpace($InnoSetupPath)) {
    if (Test-Path $InnoSetupPath) {
      if ($InnoSetupPath -like "*ISCC.exe") { return $InnoSetupPath }
      $p = Join-Path $InnoSetupPath "ISCC.exe"
      if (Test-Path $p) { return $p }
    }
  }

  # Try PATH
  $cmd = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
  if ($cmd) { return $cmd.Source }

  # Common installs
  $candidates = @(
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup\ISCC.exe"
  )
  foreach ($c in $candidates) { if (Test-Path $c) { return $c } }

  Fail "ISCC.exe (Inno Setup compiler) not found. Install Inno Setup 6 or set `$InnoSetupPath in release/config.ps1 to the folder or full path of ISCC.exe."
}

function Read-CsprojText([string]$path) {
  if (-not (Test-Path $path)) { Fail "Launcher project not found: $path" }
  return Get-Content -LiteralPath $path -Raw -Encoding UTF8
}

function Load-CsprojXml([string]$path) {
  $text = Read-CsprojText $path
  $xml = New-Object xml
  $xml.PreserveWhitespace = $true
  $xml.LoadXml($text)
  return $xml
}

function Get-NodeInnerText([System.Xml.XmlNode]$node) {
  if ($null -eq $node) { return "" }
  return ($node.InnerText).Trim()
}

function Get-CurrentLauncherVersion([string]$csprojPath) {
  $xml = Load-CsprojXml $csprojPath

  # Try no-namespace first
  $node = $xml.SelectSingleNode("//Project/PropertyGroup/Version")
  $v = Get-NodeInnerText $node
  if (-not [string]::IsNullOrWhiteSpace($v)) { return $v }

  # Try msbuild namespace variant
  $nsm = New-Object System.Xml.XmlNamespaceManager($xml.NameTable)
  $nsm.AddNamespace("msb", "http://schemas.microsoft.com/developer/msbuild/2003")
  $node2 = $xml.SelectSingleNode("//msb:Project/msb:PropertyGroup/msb:Version", $nsm)
  $v2 = Get-NodeInnerText $node2
  if (-not [string]::IsNullOrWhiteSpace($v2)) { return $v2 }

  return ""
}

function Bump-Patch([string]$ver) {
  $parts = $ver.Split(".")
  if ($parts.Count -lt 3) { Fail "Version must be at least x.y.z (found '$ver')." }
  $maj = [int]$parts[0]
  $min = [int]$parts[1]
  $pat = [int]$parts[2]
  $pat++
  return "$maj.$min.$pat"
}

function Set-LauncherVersion([string]$csprojPath, [string]$newVersion) {
  $xml = Load-CsprojXml $csprojPath
  $changed = $false

  # No-namespace common case
  $vNode = $xml.SelectSingleNode("//Project/PropertyGroup/Version")
  if ($null -ne $vNode) {
    $vNode.InnerText = $newVersion
    $changed = $true
  } else {
    # Namespace variant
    $nsm = New-Object System.Xml.XmlNamespaceManager($xml.NameTable)
    $nsm.AddNamespace("msb", "http://schemas.microsoft.com/developer/msbuild/2003")
    $vNode2 = $xml.SelectSingleNode("//msb:Project/msb:PropertyGroup/msb:Version", $nsm)
    if ($null -ne $vNode2) {
      $vNode2.InnerText = $newVersion
      $changed = $true
    }
  }

  if (-not $changed) { Fail "Could not find <Version> in csproj to update." }

  # If AssemblyVersion/FileVersion are literal (not $(Version)), update them too to x.y.z.0
  $av = "$newVersion.0"
  $fv = "$newVersion.0"

  $avNode = $xml.SelectSingleNode("//Project/PropertyGroup/AssemblyVersion")
  if ($null -ne $avNode) {
    if ($avNode.InnerText -notmatch '\$\(Version\)') { $avNode.InnerText = $av }
  }

  $fvNode = $xml.SelectSingleNode("//Project/PropertyGroup/FileVersion")
  if ($null -ne $fvNode) {
    if ($fvNode.InnerText -notmatch '\$\(Version\)') { $fvNode.InnerText = $fv }
  }

  $xml.Save($csprojPath)
}

function Read-Secure([string]$prompt) {
  $sec = Read-Host -AsSecureString $prompt
  $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec)
  try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
  finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

# -----------------------------
# NEW: Pack manifest/files auto-detect (no prompts)
# -----------------------------
function Resolve-LatestPackManifest([string]$manifestDir) {
  if ([string]::IsNullOrWhiteSpace($manifestDir)) { Fail "PackManifestDir is empty." }
  if (-not (Test-Path $manifestDir)) { Fail "PackManifestDir not found: $manifestDir" }

  $m = Get-ChildItem -LiteralPath $manifestDir -File -Filter "*.json" -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime -Descending |
       Select-Object -First 1

  if (-not $m) { Fail "No .json manifests found in: $manifestDir" }

  $jsonText = Get-Content -LiteralPath $m.FullName -Raw -Encoding UTF8
  $jsonText = $jsonText.TrimStart([char]0xFEFF, [char]0x200B)

  $obj = $null
  try { $obj = $jsonText | ConvertFrom-Json -ErrorAction Stop } catch { }

  $v = ""
  if ($obj -ne $null) {
    if ($obj.PSObject.Properties.Name -contains "version") { $v = [string]$obj.version }
    elseif ($obj.PSObject.Properties.Name -contains "Version") { $v = [string]$obj.Version }
  }

  if ([string]::IsNullOrWhiteSpace($v)) {
    # fallback: filename without extension
    $v = [IO.Path]::GetFileNameWithoutExtension($m.Name)
  }

  return [pscustomobject]@{
    Path    = $m.FullName
    Version = $v.Trim()
  }
}

function Resolve-PackFilesFolder([string]$sha256Dir, [string]$packVersion) {
  if ([string]::IsNullOrWhiteSpace($sha256Dir)) { Fail "PackFilesSha256Dir is empty." }
  if (-not (Test-Path $sha256Dir)) { Fail "PackFilesSha256Dir not found: $sha256Dir" }

  # If there is a subfolder named exactly like the version, use it
  if (-not [string]::IsNullOrWhiteSpace($packVersion)) {
    $byVersion = Join-Path $sha256Dir $packVersion
    if (Test-Path $byVersion) { return (Resolve-Path $byVersion).Path }
  }

  # Otherwise, pick most recently modified directory inside sha256Dir
  $d = Get-ChildItem -LiteralPath $sha256Dir -Directory -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime -Descending |
       Select-Object -First 1

  if ($d) { return (Resolve-Path $d.FullName).Path }

  # If sha256Dir itself directly contains files, return it
  $anyFiles = Get-ChildItem -LiteralPath $sha256Dir -File -ErrorAction SilentlyContinue | Select-Object -First 1
  if ($anyFiles) { return (Resolve-Path $sha256Dir).Path }

  Fail "No pack files found in: $sha256Dir (no subfolders and no files)."
}

# -----------------------------
# Step 0: Auto-bump launcher version
# -----------------------------
Info "=== Step 0: Version bump ==="

$csprojPath = $LauncherProject
$currentLauncherVer = Get-CurrentLauncherVersion $csprojPath
if ([string]::IsNullOrWhiteSpace($currentLauncherVer)) {
  Fail "Couldn't read launcher <Version> from csproj: $csprojPath"
}

Info "Current launcher <Version>: $currentLauncherVer"
$inputVer = Read-Host "Enter LAUNCHER version to publish (blank = auto-bump patch)"

if ([string]::IsNullOrWhiteSpace($inputVer)) {
  $newLauncherVer = Bump-Patch $currentLauncherVer
  Info "Auto-bumping patch: $currentLauncherVer -> $newLauncherVer"
} else {
  $newLauncherVer = $inputVer.Trim()
  Info "Using launcher version: $newLauncherVer"
}

Set-LauncherVersion $csprojPath $newLauncherVer
Info "Updated csproj version."

# -----------------------------
# Step 1: Build launcher
# -----------------------------
Info "=== Step 1: Build launcher ==="
Info "dotnet publish..."

$outDir = Join-Path $PSScriptRoot "out"
$launcherOut = Join-Path $outDir "launcher"
New-Item -ItemType Directory -Force -Path $launcherOut | Out-Null

dotnet publish $csprojPath -c Release -r win-x64 --self-contained false -o $launcherOut | Out-Host

# Detect version again (source of truth)
$launcherVersion = Get-CurrentLauncherVersion $csprojPath
if ([string]::IsNullOrWhiteSpace($launcherVersion)) { $launcherVersion = $newLauncherVer }
Info "Launcher version detected: $launcherVersion"

$zipName = "FabriFrogeLauncher-$launcherVersion.zip"
$zipPath = Join-Path $outDir $zipName
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

Info "Zipping launcher to: $zipPath"
Compress-Archive -Path (Join-Path $launcherOut "*") -DestinationPath $zipPath -Force

$sha = (Get-FileHash -Algorithm SHA256 -Path $zipPath).Hash.ToLowerInvariant()
Info "Launcher SHA256: $sha"

# -----------------------------
# Step 1b: Build installer (Inno Setup)
# -----------------------------
Info "=== Step 1b: Build installer ==="

$iss = Join-Path $PSScriptRoot "installer.iss"
if (-not (Test-Path $iss)) {
  Warn "Missing Inno Setup script: $iss (skipping installer build)"
  $installerPath = ""
} else {
  $isccExe = Find-ISCC
  Info "Using ISCC: $isccExe"

  # Compile installer. Note: installer.iss must include AppVersion/AppVerName and use {#AppVersion} or similar.
  & $isccExe ("/DAppVersion=$launcherVersion") $iss | Out-Host

  # Try to find the newest installer exe produced
  $maybe = Get-ChildItem -Path $PSScriptRoot -Recurse -File -Filter "*.exe" -ErrorAction SilentlyContinue |
           Where-Object { $_.Name -match "setup|installer|FabriFroge" } |
           Sort-Object LastWriteTime -Descending |
           Select-Object -First 1

  if ($maybe) {
    $installerPath = $maybe.FullName
    Info "Installer built: $installerPath"
  } else {
    $installerPath = ""
    Warn "Installer compile ran, but I couldn't locate the output .exe automatically (check your installer.iss OutputDir/OutputBaseFilename)."
  }
}

# -----------------------------
# Step 2: Generate JSONs + auto-detect pack artifacts
# -----------------------------
Info "=== Step 2: Generate JSONs ==="
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

# launcher self latest.json
$launcherSelfLatestObj = @{
  version = $launcherVersion
  url     = "$PublicBaseUrl$LauncherSelfPath/builds/$zipName"
  sha256  = $sha
}
$launcherSelfLatest = $launcherSelfLatestObj | ConvertTo-Json -Depth 10

$launcherSelfLatestPath = Join-Path $outDir "launcher-self-latest.json"
Set-Content -Path $launcherSelfLatestPath -Value $launcherSelfLatest -Encoding UTF8
Info "Wrote: $launcherSelfLatestPath"

# PACK: always use the most recent manifest.json in PackManifestDir
Info "Auto-detecting latest pack manifest from: $PackManifestDir"
$latestPack = Resolve-LatestPackManifest -manifestDir $PackManifestDir
$packManifestPath = $latestPack.Path
$packVersion = $latestPack.Version

if ([string]::IsNullOrWhiteSpace($packVersion)) {
  Fail "Auto-detected pack manifest but could not determine version."
}

Info "Pack manifest selected: $packManifestPath"
Info "Pack version detected: $packVersion"

# Pack latest.json
$packLatestObj = @{
  packId        = $PackId
  latestVersion = $packVersion
  manifestUrl   = "$PublicBaseUrl$LauncherPath/manifests/$packVersion.json"
}
$packLatest = $packLatestObj | ConvertTo-Json -Depth 10

$packLatestPath = Join-Path $outDir "pack-latest.json"
Set-Content -Path $packLatestPath -Value $packLatest -Encoding UTF8
Info "Wrote: $packLatestPath"

# PACK FILES: auto-detect from PackFilesSha256Dir
Info "Auto-detecting pack files folder from: $PackFilesSha256Dir"
$packFilesFolder = Resolve-PackFilesFolder -sha256Dir $PackFilesSha256Dir -packVersion $packVersion
Info "Pack files folder selected: $packFilesFolder"

# -----------------------------
# Step 3: Upload
# -----------------------------
Info "=== Step 3: Upload ==="

$winscp = Find-WinScpCom

# Parse SFTP host "user@host"
if ($SftpHost -notmatch "^(?<user>[^@]+)@(?<host>.+)$") {
  Fail "SftpHost must be like user@host (found '$SftpHost')"
}
$user = $Matches.user
$hostName = $Matches.host

$pw = Read-Secure "SFTP Password (input hidden)"

# Host key pinning
$hostKeySwitch = ""
if (-not [string]::IsNullOrWhiteSpace($WinScpHostKey)) {
  Info "Using pinned WinSCP host key from config.ps1"
  $hostKeySwitch = " -hostkey=""$WinScpHostKey"""
} else {
  Warn "WinSCP is set to accept ANY host key (insecure). Set `$WinScpHostKey in config.ps1 to pin it."
  $hostKeySwitch = " -hostkey=""*"""
}

$scriptPath = Join-Path $env:TEMP "winscp_publish.txt"
$logPath    = Join-Path $env:TEMP "winscp_publish.log"

# Robust WinSCP open line
$openLine = "open ""sftp://$user`:$pw@$hostName`:$SshPort/""$hostKeySwitch"

$lines = @()
$lines += "option batch continue"
$lines += "option confirm off"
$lines += "option transfer binary"
$lines += $openLine

# cd to RemoteRoot (your nginx www/files folder as seen by SFTP)
$lines += "cd ""$RemoteRoot"""

# Ensure directories (batch=continue will ignore if exists)
$lines += "mkdir launcher"
$lines += "mkdir launcher/self"
$lines += "mkdir launcher/self/builds"
$lines += "mkdir launcher/manifests"
$lines += "mkdir launcher/files"

# Upload launcher zip + launcher self latest
$lines += "put ""$zipPath"" ""launcher/self/builds/"""
$lines += "put ""$launcherSelfLatestPath"" ""launcher/self/latest.json"""

# Upload pack latest.json
$lines += "put ""$packLatestPath"" ""launcher/latest.json"""

# Upload pack manifest (ALWAYS)
if (-not (Test-Path $packManifestPath)) { Fail "Pack manifest not found: $packManifestPath" }
$lines += "put ""$packManifestPath"" ""launcher/manifests/$packVersion.json"""

# Upload pack files folder (ALWAYS)
if (-not (Test-Path $packFilesFolder)) { Fail "Pack files folder not found: $packFilesFolder" }
$lines += "mkdir launcher/files/$packVersion"
$lines += "put -resumesupport=on -transfer=binary -nopreservetime -r ""$packFilesFolder\*"" ""launcher/files/$packVersion/"""

# Optional: upload installer if we built one
if (-not [string]::IsNullOrWhiteSpace($installerPath) -and (Test-Path $installerPath)) {
  # Keep your existing App link: https://files.fabrifroge.com/launcher/installer/
  $lines += "mkdir launcher/installer"
  $lines += "put ""$installerPath"" ""launcher/installer/"""
}

$lines += "exit"

Set-Content -Path $scriptPath -Value ($lines -join "`r`n") -Encoding ASCII

Info "Uploading via WinSCP..."
& $winscp "/ini=nul" "/log=$logPath" "/script=$scriptPath" | Out-Host

Info "WinSCP log: $logPath"
Info "Publish complete."
Info "Pack published version: $packVersion"
Info "Pack manifest uploaded to: $PublicBaseUrl$LauncherPath/manifests/$packVersion.json"
Info "Pack files uploaded to:    $PublicBaseUrl$LauncherPath/files/$packVersion/"

# -----------------------------
# Optional: Cloudflare purge
# -----------------------------
if ($EnableCloudflarePurge -and -not [string]::IsNullOrWhiteSpace($CloudflareZoneId) -and -not [string]::IsNullOrWhiteSpace($CloudflareApiToken)) {
  try {
    Info "Cloudflare purge enabled. Purging must-be-fresh JSON URLs..."
    $body = @{ files = $PurgeUrls } | ConvertTo-Json -Depth 5
    Invoke-RestMethod `
      -Method POST `
      -Uri "https://api.cloudflare.com/client/v4/zones/$CloudflareZoneId/purge_cache" `
      -Headers @{ Authorization = "Bearer $CloudflareApiToken"; "Content-Type"="application/json" } `
      -Body $body | Out-Null
    Info "Cloudflare purge requested."
  } catch {
    Warn "Cloudflare purge failed: $($_.Exception.Message)"
  }
} else {
  Info "Cloudflare purge disabled."
  Info "If caching ever bites you, purge these URLs:"
  foreach ($u in $PurgeUrls) { Write-Host " - $u" }
}
