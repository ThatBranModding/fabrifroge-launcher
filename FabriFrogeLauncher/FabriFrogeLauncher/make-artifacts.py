from pathlib import Path
import shutil, tarfile, zipfile

root = Path(__file__).resolve().parent / "artifacts"
for rid in ("win-x64", "win-arm64"):
    source = root / rid
    out = root / f"FabriFrogeLauncher-{rid}.zip"
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as archive:
        for path in source.rglob("*"):
            if path.is_file(): archive.write(path, path.relative_to(source).as_posix())
for rid in ("linux-x64", "linux-arm64"):
    source = root / rid
    executable = source / "FabriFrogeLauncher"
    app = source / "FabriFrogeLauncher.app"
    if app.exists(): shutil.rmtree(app)
    for path in source.rglob("*"):
        if path.is_file():
            mode = 0o755 if path.name == "FabriFrogeLauncher" else 0o644
            path.chmod(mode)
        elif path.is_dir(): path.chmod(0o755)
    with tarfile.open(root / f"FabriFrogeLauncher-{rid}.tar.gz", "w:gz", format=tarfile.PAX_FORMAT) as archive:
        for path in source.rglob("*"):
            info = archive.gettarinfo(str(path), arcname=path.relative_to(source).as_posix())
            info.mode = 0o755 if path.is_dir() or path.name == "FabriFrogeLauncher" else 0o644
            if path.is_file():
                with path.open("rb") as data: archive.addfile(info, data)
            else: archive.addfile(info)
for rid in ("osx-x64", "osx-arm64"):
    source = root / rid
    app = root / "packages" / rid / "FabriFrogeLauncher.app"
    if app.exists(): shutil.rmtree(app)
    macos = app / "Contents" / "MacOS"
    macos.mkdir(parents=True)
    for path in source.iterdir():
        if path.name != "FabriFrogeLauncher.app":
            destination = macos / path.name
            if path.is_dir(): shutil.copytree(path, destination)
            else: shutil.copy2(path, destination)
    executable = macos / "FabriFrogeLauncher"
    executable.chmod(0o755)
    (app / "Contents" / "Info.plist").write_text('''<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict><key>CFBundleExecutable</key><string>FabriFrogeLauncher</string><key>CFBundleIdentifier</key><string>com.fabrifroge.launcher</string><key>CFBundleName</key><string>FabriFroge Launcher</string><key>CFBundlePackageType</key><string>APPL</string><key>CFBundleVersion</key><string>1.1.2</string><key>LSMinimumSystemVersion</key><string>12.0</string></dict></plist>''', encoding="utf-8")
    for path in app.rglob("*"):
        if path.is_dir(): path.chmod(0o755)
        elif path.name == "FabriFrogeLauncher": path.chmod(0o755)
        else: path.chmod(0o644)
    with tarfile.open(root / f"FabriFrogeLauncher-{rid}.tar.gz", "w:gz", format=tarfile.PAX_FORMAT) as archive:
        for path in app.rglob("*"):
            info = archive.gettarinfo(str(path), arcname=(Path(app.name) / path.relative_to(app)).as_posix())
            info.mode = 0o755 if path.is_dir() or path.name == "FabriFrogeLauncher" else 0o644
            if path.is_file():
                with path.open("rb") as data: archive.addfile(info, data)
            else: archive.addfile(info)
