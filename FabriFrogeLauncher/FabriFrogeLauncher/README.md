# FabriFroge Launcher

Cross-platform migration of the Windows WPF launcher into an Avalonia/.NET 8 native desktop app. The original checkout at `C:\Users\branj\Documents\GitHub\fabrifroge-launcher` remains untouched.

## Launcher behavior

- Microsoft sign-in, cached-session restore, sign-out, account switching, and expired-session recovery when playing.
- Pack metadata checks, install/update, full repair, progress reporting, and streaming Minecraft logs.
- Host-OS Temurin Java 21 provisioning, NeoForge client install, and Minecraft launch.
- Install folder and RAM settings for the single default pack profile, the animated WebP background, launcher and pack update prompts, wiki link, and console drawer.
- Microsoft account sign-in uses MSAL and opens the user's default system browser on Windows, macOS, and Linux. The public client ID is built into the launcher; it is not a client secret.
- Launcher self-update apply mode supports ZIP and TAR.GZ platform packages. Update metadata can provide a `downloads` object keyed by .NET RID (such as `win-x64`, `osx-arm64`, and `linux-x64`). Legacy `url` is used only for `win-x64`.

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

Self-contained publish outputs and packages are in the workspace-level `dist/` directory. Six RIDs were published successfully: `win-x64`, `win-arm64`, `osx-x64`, `osx-arm64`, `linux-x64`, and `linux-arm64`. The `.zip` and `.tar.gz` packages are also present there. `make-artifacts.py` regenerates the packages after the six RID folders have been published. `.github/workflows/publish-fabrifroge-launcher.yml` builds and packages on native GitHub runners.

## Verification limits

The project targets six RIDs. macOS archives require the publisher's Apple Developer signing and notarization credentials to avoid Gatekeeper warnings. The hard-coded client ID must be allow-listed for Minecraft Xbox authentication. Configure per-RID launcher self-update URLs before distributing updates.
