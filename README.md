# FabriFroge Launcher

FabriFroge Launcher is an Avalonia/.NET 8 desktop launcher for the FabriFroge Minecraft: Java Edition pack, designed to run natively on Windows, macOS, and Linux.

## Platform packages

The native build workflow targets:

- Windows: `win-x64`, `win-arm64`
- macOS: `osx-x64`, `osx-arm64`
- Linux: `linux-x64`, `linux-arm64`

Packages are self-contained .NET publishes. Java 21 is provisioned by the launcher when needed. macOS archives are unsigned and may show Gatekeeper warnings until signed and notarized by the publisher.

## Features

- Microsoft sign-in, cached-session restore, sign-out, account switching, and expired-session recovery.
- Pack metadata checks, install/update, full repair, progress reporting, and streaming Minecraft logs.
- Host-OS Temurin Java 21 provisioning, NeoForge client installation, and game launch.
- Install-folder and RAM settings for the single default pack profile.
- Animated WebP background, launcher and pack update prompts, wiki link, and console drawer.
- ZIP and TAR.GZ self-update support with per-platform downloads in launcher metadata.

## Build

Install the .NET 8 SDK, then run from the repository root:

```powershell
dotnet build FabriFrogeLauncher/FabriFrogeLauncher/FabriFrogeLauncher.csproj -c Release
```

The GitHub Actions workflow at `.github/workflows/publish-fabrifroge-launcher.yml` builds all six RIDs on native Windows, macOS, and Linux runners. Run it manually from Actions or push a tag matching `launcher-v*` to build packages.

## Microsoft sign-in setup

This desktop launcher uses the MSAL device-code flow on all supported operating systems. Configure a Microsoft Entra public-client app for personal Microsoft accounts, enable public-client flows, and request Minecraft Xbox authentication approval for its Application (client) ID. Do not create a client secret for the desktop app. Enter the approved client ID in the launcher's settings on each OS.

See the [CmlLib MSAL client-ID guide](https://cmllib.github.io/CmlLib.Core-wiki/en/auth.microsoft/xboxauthnet.game.msal/clientid/) and [Minecraft App ID review guidance](https://help.minecraft.net/hc/en-us/articles/16254801392141).

## Project information

The project information page is in [`docs/index.html`](docs/index.html). GitHub Pages can serve it from the `main` branch's `/docs` folder.

FabriFroge Launcher is independent and is not affiliated with Microsoft, Mojang Studios, Xbox, or Minecraft.
