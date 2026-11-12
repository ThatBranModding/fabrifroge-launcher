# Apex domain setup

This makes `https://fabrifroge.com/` serve the public homepage from `www/index.html` and `/download/` serve the existing `www/download` directory. It leaves the current subdomain services and port 8080 proxy intact.

## Prerequisites

1. Create or confirm a Cloudflare DNS record for `fabrifroge.com` that points to this homeserver. Keep the existing proxy mode used for the other web hosts.
2. Issue an HTTPS certificate containing `fabrifroge.com` with win-acme. Include `www.fabrifroge.com` only if you also create its DNS record.
3. Copy `docs/index.html` from this repository to `C:\Users\Branserver\Downloads\nginx-1.26.3\www\index.html`.
4. The download page files are already placed in `www\download`.

## Nginx

Use `nginx-apex-site.conf.example` as the starting point. Replace its certificate paths with the actual paths from win-acme, then add the server blocks inside the existing `http {}` block in `conf\nginx.conf`. Do not remove the existing `web.fabrifroge.com` block; it continues proxying AMP to port 8080.

Before reloading, run from the Nginx directory:

```powershell
.\nginx.exe -t
if ($LASTEXITCODE -eq 0) { .\nginx.exe -s reload }
```

The `/download/` location is static and should work without another listener. Additional service paths such as `/wiki/` or `/media/` can be added later after checking each application's base-path and WebSocket requirements.
