# FabriFroge Launcher

Cross-platform migration of the Windows WPF launcher into an Avalonia/.NET 8 native desktop app. The original checkout at `C:\Users\branj\Documents\GitHub\fabrifroge-launcher` remains untouched.

## Launcher behavior

- Microsoft sign-in, cached-session restore, sign-out, account switching, and expired-session recovery when playing.
- Pack metadata checks, install/update, full repair, progress reporting, and streaming Minecraft logs.
- Host-OS Temurin Java 21 provisioning, NeoForge client install, and Minecraft launch.
- Install folder and RAM settings for the single default pack profile, the animated WebP background, launcher and pack update prompts, wiki link, and console drawer.
- Microsoft account sign-in uses MSAL and opens the user's default system browser on Windows, macOS, and Linux. The public client ID is built into the launcher; it is not a client secret.
- Launcher self-update accepts only ZIP/TAR.GZ packages keyed by OS and architecture, each with its own required SHA-256 checksum. There is no fallback to the legacy Windows-only launcher package.

## UI

The Avalonia window follows the supplied reference: custom title bar, full-window Lapras animation, translucent left panel, account and install controls, installed/latest version status, and a yellow Play control with the options menu. WebP playback runs whenever the window is visible, including when it loses keyboard focus, and pauses while minimized or hidden. The frame buffer is copied before dispatch to prevent frames being overwritten before rendering.

## Build and packages

Requires .NET 8 SDK for development. Published apps are self-contained; first launch downloads Java 21 if it is missing. Online access is needed for pack downloads, Java, Microsoft authentication, and launcher metadata.

Build: `dotnet build FabriFrogeLauncher.csproj -c Release`.

## Microsoft sign-in setup

MSAL requires a publisher-owned Microsoft Entra public-client app. The launcher uses the client ID `29bba88a-ff97-4db8-ae02-574e23ed7521`; it is built into the app, so players do not need to configure it.

1. Create an app registration in Microsoft Entra ID that supports personal Microsoft accounts.
2. Configure the app as a public/native client, allow public client flows, and register `http://localhost` as the native redirect URI.
3. Do not create or distribute a client secret for this desktop app.
4. Submit the client ID for Minecraft Xbox authentication allowlisting. Until it is approved and allowlisted, Microsoft sign-in may work but Minecraft authentication can still be rejected.
5. Sign in. The launcher opens the operating system's default browser for Microsoft authentication and returns to the launcher when complete.

See [CmlLib's MSAL client-ID guide](https://cmllib.github.io/CmlLib.Core-wiki/en/auth.microsoft/xboxauthnet.game.msal/clientid/) and [Microsoft's Minecraft client allow-list guidance](https://help.minecraft.net/hc/en-us/articles/16254801392141).

### Native installers and release flow

The `.github/workflows/release-launcher.yml` workflow builds self-contained release packages for all six RIDs (`win-x64`, `win-arm64`, `osx-x64`, `osx-arm64`, `linux-x64`, and `linux-arm64`):

- Windows: `.exe` setup installers and portable `.zip` archives for x64 and ARM64.
- macOS: `.dmg` installers and `.tar.gz` self-update archives for Intel and Apple silicon. The DMGs include an Applications shortcut. They are unsigned and may show Gatekeeper warnings until signed and notarized by the publisher.
- Linux: `.deb` installers and `.tar.gz` self-update archives for x64 and ARM64.

Set the project version, then push a matching tag like `launcher-v1.1.8.4`. Add the rotated server password to GitHub repository Actions secrets as `FABRIFROGE_SFTP_PASSWORD`. The workflow creates a GitHub release and uploads installers, self-update archives, checksums, and `launcher-self-latest.json` to `files.fabrifroge.com`; the cross-platform launcher reads `/launcher/v2/self/latest.json`. The legacy Windows-only updater remains at `/launcher/self/latest.json`. Cross-platform modpack data comes from `/launcher/v2/`.

Modpack publishing is independent. Run `FabriFrogeLauncher/run-publish-build.bat` or `release/publish-modpack.ps1` to upload only pack metadata and files. It does not modify the launcher feed or upload a launcher binary. The old `publish-release.ps1` remains as a compatibility entry point to the modpack-only publisher.

## Verification limits

The project targets six RIDs. macOS installers need Apple Developer signing and notarization to avoid Gatekeeper warnings. The hard-coded client ID must be allow-listed for Minecraft Xbox authentication.
