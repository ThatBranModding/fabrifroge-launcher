# FabriFroge Launcher

FabriFroge Launcher is an Avalonia/.NET 8 desktop launcher for the FabriFroge Minecraft: Java Edition pack, designed to run natively on Windows, macOS, and Linux.

## Native installers and versioning

Launcher releases create Windows `.exe` installers, macOS `.dmg` installers, and Linux `.deb` installers for x64 and ARM64, plus portable ZIP/TAR.GZ archives for automatic updates. The macOS packages are unsigned until signed and notarized by the publisher.

Add the rotated homeserver password as the GitHub Actions repository secret `FABRIFROGE_SFTP_PASSWORD`, then push a version-matched tag such as `launcher-v1.1.6` to run `.github/workflows/release-launcher.yml`. The workflow publishes per-platform installers and SHA-256 update archives to the homeserver. The launcher update feed is separate from the modpack feed.

## Features

- Microsoft sign-in, cached-session restore, sign-out, account switching, and expired-session recovery.
- Pack metadata checks, install/update, full repair, progress reporting, and streaming Minecraft logs.
- Host-OS Temurin Java 21 provisioning, NeoForge client installation, and game launch.
- Install-folder and RAM settings for the single default pack profile.
- Animated WebP background, launcher and pack update prompts, wiki link, and console drawer.
- ZIP and TAR.GZ self-update support with per-platform URLs and required SHA-256 verification.

## Build

Install the .NET 8 SDK, then run from the repository root:

```powershell
dotnet build FabriFrogeLauncher/FabriFrogeLauncher/FabriFrogeLauncher.csproj -c Release
```

Publish a modpack with `FabriFrogeLauncher/run-publish-build.bat`; that script changes only pack metadata and files, never the launcher update feed or launcher binaries.

## Microsoft sign-in setup

This desktop launcher uses MSAL interactive sign-in through the operating system's default browser on Windows, macOS, and Linux. Its public client ID is built into the launcher (`29bba88a-ff97-4db8-ae02-574e23ed7521`), so players do not need to configure it. The publisher must configure the Entra app for personal Microsoft accounts, enable public-client flows, add `http://localhost` as the native redirect URI, and request Minecraft Xbox authentication allowlisting. Do not create a client secret for this desktop app. Microsoft sign-in can succeed while Minecraft authentication remains unavailable until allowlisting is approved.

See the [CmlLib MSAL client-ID guide](https://cmllib.github.io/CmlLib.Core-wiki/en/auth.microsoft/xboxauthnet.game.msal/clientid/) and [Minecraft App ID review guidance](https://help.minecraft.net/hc/en-us/articles/16254801392141).

## Project information

The project information page is in [`docs/index.html`](docs/index.html). GitHub Pages can serve it from the `main` branch's `/docs` folder.

FabriFroge Launcher is independent and is not affiliated with Microsoft, Mojang Studios, Xbox, or Minecraft.
