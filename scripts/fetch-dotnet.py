"""Prepare the portable .NET 10 SDK in this project only."""
from __future__ import annotations

import hashlib
import concurrent.futures
import json
import os
import pathlib
import shutil
import subprocess
import urllib.request
import zipfile

ROOT = pathlib.Path(__file__).resolve().parents[1]
VERSION = "10.0.400"
URL = "https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.400/dotnet-sdk-10.0.400-win-x64.zip"
DOWNLOAD_URL = "https://dotnetcli.blob.core.windows.net/dotnet/Sdk/10.0.400/dotnet-sdk-10.0.400-win-x64.zip"
DOWNLOAD_BYTES = 300546129
SHA512 = "9b8b88590e4da131bfd0da7aa089d0fc04d5418d5f8607ec13d55dc5a17b4399afd54d496c12657fa05c6c6546dc5eab930f26ac6c50f2d3a7712c0fb378c366"

def main() -> None:
    archive = ROOT / ".tools/downloads" / ("dotnet-sdk-" + VERSION + "-win-x64.zip")
    destination = ROOT / ".dotnet"
    if archive.exists():
        with archive.open("rb") as stream:
            current_hash = hashlib.file_digest(stream, "sha512").hexdigest()
        if current_hash != SHA512:
            preserved = archive.with_name(archive.name + ".invalid-" + current_hash[:12])
            if preserved.exists(): raise RuntimeError("A repeated invalid SDK archive was preserved; inspect it before retrying")
            archive.rename(preserved)
            print("Preserved truncated/invalid prior download:", preserved.name, flush=True)
    if not archive.exists():
        print("Downloading portable .NET SDK", VERSION, flush=True)
        pieces = ROOT / ".tools/downloads" / ("dotnet-sdk-" + VERSION + ".parts")
        pieces.mkdir(exist_ok=True)
        block = 16 * 1024 * 1024
        def download_piece(index: int) -> pathlib.Path:
            start = index * block
            end = min(DOWNLOAD_BYTES, start + block) - 1
            part = pieces / (str(index).zfill(3) + ".part")
            expected_size = end - start + 1
            if part.exists() and part.stat().st_size == expected_size: return part
            request = urllib.request.Request(DOWNLOAD_URL, headers={"Range": f"bytes={start}-{end}"})
            with urllib.request.urlopen(request, timeout=45) as response:
                if response.status != 206 or response.headers.get("Content-Range") != f"bytes {start}-{end}/{DOWNLOAD_BYTES}":
                    raise RuntimeError("Unexpected SDK HTTP range response")
                with part.open("wb") as output:
                    shutil.copyfileobj(response, output, 1024 * 1024)
            if part.stat().st_size != expected_size: raise RuntimeError("Truncated SDK chunk " + str(index))
            print("SDK chunk", index + 1, "ready", flush=True)
            return part
        count = (DOWNLOAD_BYTES + block - 1) // block
        with concurrent.futures.ThreadPoolExecutor(max_workers=4) as executor:
            parts = list(executor.map(download_piece, range(count)))
        temporary = archive.with_suffix(".part")
        with temporary.open("wb") as output:
            for part in parts:
                with part.open("rb") as stream: shutil.copyfileobj(stream, output, 1024 * 1024)
        temporary.replace(archive)
    with archive.open("rb") as stream:
        actual = hashlib.file_digest(stream, "sha512").hexdigest()
    if actual != SHA512: raise RuntimeError(".NET SDK SHA512 mismatch")
    if not (destination / "sdk" / VERSION).is_dir():
        if destination.exists() and any(destination.iterdir()):
            raise RuntimeError("Existing .dotnet contents preserved; select an explicit side-by-side SDK directory")
        destination.mkdir(exist_ok=True)
        with zipfile.ZipFile(archive) as zipped:
            for member in zipped.infolist():
                if not (destination / member.filename).resolve().is_relative_to(destination.resolve()):
                    raise RuntimeError("Unsafe SDK archive path")
            zipped.extractall(destination)
    environment = dict(os.environ)
    environment.update(DOTNET_ROOT=str(destination), DOTNET_CLI_HOME=str(ROOT / ".tools/dotnet-home"), DOTNET_SKIP_FIRST_TIME_EXPERIENCE="1", DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_CLI_UI_LANGUAGE="en-US", DOTNET_NOLOGO="1", NUGET_PACKAGES=str(ROOT / ".tools/nuget-packages"))
    result = subprocess.run([str(destination / "dotnet.exe"), "--info"], cwd=ROOT, env=environment, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=60, creationflags=subprocess.CREATE_NO_WINDOW)
    record = {"sdk_version": VERSION, "url": URL, "download_url": DOWNLOAD_URL, "sha512": actual, "sha256": hashlib.sha256(archive.read_bytes()).hexdigest(), "metadata_url": "https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json", "returncode": result.returncode, "stdout": result.stdout, "stderr": result.stderr, "packs": [item.name for item in (destination / "packs").iterdir()]}
    (destination / "toolchain-verification.json").write_text(json.dumps(record, indent=2) + "\n", encoding="utf-8")
    print(result.stdout, end="")
    print(result.stderr, end="")
    raise SystemExit(result.returncode)

if __name__ == "__main__":
    main()
