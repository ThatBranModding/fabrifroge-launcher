from __future__ import annotations

import argparse
import plistlib
import shutil
import subprocess
import tarfile
import zipfile
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument("--rid", required=True)
parser.add_argument("--version", required=True)
parser.add_argument("--source", required=True, type=Path)
parser.add_argument("--output", required=True, type=Path)
args = parser.parse_args()
rid = args.rid
args.output.mkdir(parents=True, exist_ok=True)

if rid.startswith("win-"):
    archive = args.output / f"FabriFrogeLauncher-{args.version}-{rid}.zip"
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as zf:
        for path in args.source.rglob("*"):
            if path.is_file():
                zf.write(path, path.relative_to(args.source).as_posix())
elif rid.startswith("linux-"):
    arch = "amd64" if rid.endswith("x64") else "arm64"
    root = args.output / f"deb-root-{rid}"
    if root.exists():
        shutil.rmtree(root)
    install = root / "opt" / "fabrifroge-launcher"
    shutil.copytree(args.source, install)
    executable = install / "FabriFrogeLauncher"
    executable.chmod(0o755)
    (root / "usr" / "bin").mkdir(parents=True)
    wrapper = root / "usr" / "bin" / "fabrifroge-launcher"
    wrapper.write_text('#!/bin/sh\nexec /opt/fabrifroge-launcher/FabriFrogeLauncher "$@"\n', encoding="utf-8")
    wrapper.chmod(0o755)
    desktop = root / "usr" / "share" / "applications" / "fabrifroge-launcher.desktop"
    desktop.parent.mkdir(parents=True, exist_ok=True)
    desktop.write_text("[Desktop Entry]\nName=FabriFroge Launcher\nComment=FabriFroge Minecraft launcher\nExec=fabrifroge-launcher\nTerminal=false\nType=Application\nCategories=Game;\n", encoding="utf-8")
    control = root / "DEBIAN" / "control"
    control.parent.mkdir(parents=True, exist_ok=True)
    control.write_text(f"Package: fabrifroge-launcher\nVersion: {args.version}\nSection: games\nPriority: optional\nArchitecture: {arch}\nMaintainer: FabriFroge\nDescription: FabriFroge Minecraft launcher\n", encoding="utf-8")
    subprocess.run(["dpkg-deb", "--build", "--root-owner-group", str(root), str(args.output / f"FabriFrogeLauncher-{args.version}-{rid}.deb")], check=True)
    shutil.rmtree(root)
elif rid.startswith("osx-"):
    app = args.output / "FabriFrogeLauncher.app"
    if app.exists():
        shutil.rmtree(app)
    macos = app / "Contents" / "MacOS"
    macos.mkdir(parents=True)
    for item in args.source.iterdir():
        destination = macos / item.name
        shutil.copytree(item, destination) if item.is_dir() else shutil.copy2(item, destination)
    executable = macos / "FabriFrogeLauncher"
    executable.chmod(0o755)
    info = {
        "CFBundleExecutable": "FabriFrogeLauncher",
        "CFBundleIdentifier": "com.fabrifroge.launcher",
        "CFBundleName": "FabriFroge Launcher",
        "CFBundlePackageType": "APPL",
        "CFBundleShortVersionString": args.version,
        "CFBundleVersion": args.version,
        "LSMinimumSystemVersion": "12.0",
    }
    with (app / "Contents" / "Info.plist").open("wb") as stream:
        plistlib.dump(info, stream)
    dmg_root = args.output / f"dmg-root-{rid}"
    if dmg_root.exists():
        shutil.rmtree(dmg_root)
    dmg_root.mkdir()
    shutil.copytree(app, dmg_root / app.name)
    (dmg_root / "Applications").symlink_to("/Applications")
    dmg = args.output / f"FabriFrogeLauncher-{args.version}-{rid}.dmg"
    subprocess.run(["hdiutil", "create", "-volname", "FabriFroge Launcher", "-srcfolder", str(dmg_root), "-ov", "-format", "UDZO", str(dmg)], check=True)
    shutil.rmtree(dmg_root)
else:
    raise SystemExit(f"Unsupported RID: {rid}")

# Keep a portable self-update archive beside the native installer.
if rid.startswith("win-"):
    pass
elif rid.startswith("linux-"):
    archive = args.output / f"FabriFrogeLauncher-{args.version}-{rid}.tar.gz"
    with tarfile.open(archive, "w:gz") as tf:
        for path in args.source.rglob("*"):
            if path.is_file():
                info = tf.gettarinfo(str(path), arcname=path.relative_to(args.source).as_posix())
                info.mode = 0o755 if path.name == "FabriFrogeLauncher" else 0o644
                with path.open("rb") as stream:
                    tf.addfile(info, stream)
elif rid.startswith("osx-"):
    archive = args.output / f"FabriFrogeLauncher-{args.version}-{rid}.tar.gz"
    with tarfile.open(archive, "w:gz") as tf:
        tf.add(app, arcname=app.name)
    shutil.rmtree(app)
