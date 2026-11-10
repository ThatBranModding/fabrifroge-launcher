# FabriFroge Launcher

An Avalonia/.NET 8 native desktop app for FabriFroge, running natively on Windows, macOS, and Linux. See the repository-root README for platform packages, build steps, and Microsoft sign-in setup.

## Launcher behavior

- Microsoft sign-in, cached-session restore, sign-out, account switching, and expired-session recovery when playing.
- Pack metadata checks, install/update, full repair, progress reporting, and streaming Minecraft logs.
- Host-OS Temurin Java 21 provisioning, NeoForge client install, and Minecraft launch.
- Install folder and RAM settings for the single default pack profile, the animated WebP background, launcher and pack update prompts, wiki link, and console drawer.
- Microsoft account sign-in uses the same MSAL device-code flow on Windows, macOS, and Linux. The public client ID is configured in the launcher options and saved in `launcher_settings.json`; it is not a client secret. The flow presents a code and opens Microsoft's verification page.
- Launcher self-update apply mode supports ZIP and TAR.GZ platform packages. Update metadata can provide a `downloads` object keyed by .NET RID (such as `win-x64`, `osx-arm64`, and `linux-x64`). Legacy `url` is used only for `win-x64`.

## UI

The Avalonia window follows the supplied reference: custom title bar, full-window Lapras animation, translucent left panel, account and install controls, installed/latest version status, and a yellow Play control with the options menu. WebP playback runs whenever the window is visible, including when it loses keyboard focus, and pauses while minimized or hidden. The frame buffer is copied before dispatch to prevent frames being overwritten before rendering.

## Build and packages

Requires .NET 8 SDK for development. Published apps are self-contained; first launch downloads Java 21 if it is missing. Online access is needed for pack downloads, Java, Microsoft authentication, and launcher metadata.

Build: `dotnet build FabriFrogeLauncher.csproj -c Release`.

## Microsoft sign-in setup

MSAL requires a publisher-owned Microsoft Entra public-client app; the original Windows-only WPF flow's built-in Minecraft client ID cannot be reused for a consistent cross-platform MSAL flow.

1. Create an app registration in Microsoft Entra ID that supports personal Microsoft accounts.
2. Configure it as a public/native client and allow public client flows. Register `http://localhost` as the native redirect URI, as required by the CmlLib MSAL setup.
3. Copy its **Application (client) ID**. Do not create or distribute a client secret for this desktop app.
4. Have the app ID allow-listed for Minecraft Xbox authentication. Without this, Xbox/Minecraft token exchange can be rejected even when Microsoft sign-in succeeds.
5. Open the launcher options, paste the same public client ID into **Microsoft app client ID**, and save. Repeat on each OS installation.
6. Sign in. The launcher will show a device code and verification page; finish login in the browser and return to the launcher.

See [CmlLib's MSAL client-ID guide](https://cmllib.github.io/CmlLib.Core-wiki/en/auth.microsoft/xboxauthnet.game.msal/clientid/) and [Microsoft's Minecraft client allow-list guidance](https://help.minecraft.net/hc/en-us/articles/16254801392141).

The repository workflow builds self-contained packages for six RIDs: `win-x64`, `win-arm64`, `osx-x64`, `osx-arm64`, `linux-x64`, and `linux-arm64`.

## Verification limits

The updated project builds cleanly and all six target RIDs publish. The local machine is Windows, so macOS/Linux app launch cannot be smoke-tested here. macOS archives are unsigned; eliminating Gatekeeper warnings requires the publisher's Apple Developer signing and notarization credentials. The launcher requires a publisher-owned client ID allow-listed for Minecraft Xbox authentication, configured in launcher settings. Configure per-RID launcher self-update URLs before distributing updates.
