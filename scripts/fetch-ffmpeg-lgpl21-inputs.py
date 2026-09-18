"""Fetch isolated native FFmpeg LGPL 2.1 build inputs, without installation."""
from __future__ import annotations

import concurrent.futures
import hashlib
import json
import pathlib
import shutil
import subprocess
import tempfile
import urllib.request
import zipfile

ROOT = pathlib.Path(__file__).resolve().parents[1]
CACHE = ROOT / ".tools/downloads"
TOOLS = ROOT / ".tools"
DEST = ROOT / ".deps/ffmpeg-lgpl21"

INPUTS = [
    ("make-msys", "https://repo.msys2.org/msys/x86_64/make-4.4.1-3-x86_64.pkg.tar.zst", "af0bdba17f06fe037f0194069adaa31a8fe45f1a11381501896aea1fae37bd5d", TOOLS / "ffmpeg-build", "tar"),
    ("make", "https://mirror.msys2.org/mingw/clang64/mingw-w64-clang-x86_64-make-4.4.1-5-any.pkg.tar.zst", "9054bdc76a9c63077a731ee7d6a801302f7b3a412d8d5db9bc263993688334f2", TOOLS / "ffmpeg-build", "tar"),
    ("gettext-runtime", "https://mirror.msys2.org/mingw/clang64/mingw-w64-clang-x86_64-gettext-runtime-1.0-1-any.pkg.tar.zst", "6f22a64727816c9d9c8289bac9493a3c50691da678de22489d0545114568bb90", TOOLS / "ffmpeg-build", "tar"),
    ("libiconv", "https://mirror.msys2.org/mingw/clang64/mingw-w64-clang-x86_64-libiconv-1.19-1-any.pkg.tar.zst", "26c4ac9f2023eecdfffb291cab40c60585a0cd0f72539ca03c5558f3ecd309ec", TOOLS / "ffmpeg-build", "tar"),
    ("nasm", "https://www.nasm.us/pub/nasm/releasebuilds/3.02/win64/nasm-3.02-win64.zip", None, TOOLS / "nasm", "zip"),
    ("meson", "https://files.pythonhosted.org/packages/07/68/b0117422eb0a46d9d8d9e328f0c5b5c835179bfc058688bca35c90c89eba/meson-1.12.0-py3-none-any.whl", "71f133147fa0fcfe8f4df49fa1045771064947834538409e5d97b3613aac8b4e", TOOLS / "meson", "wheel"),
    ("ffmpeg-lgpl21-source", "https://codeload.github.com/FFmpeg/FFmpeg/zip/38b88335f99e76ed89ff3c93f877fdefce736c13", None, DEST / "sources/ffmpeg", "zip"),
    ("dav1d-lgpl21-source", "https://codeload.github.com/videolan/dav1d/zip/54706fc6bc0cdecab7e9593974a4039cc038fca7", None, DEST / "sources/dav1d", "zip"),
]

def download(entry):
    name, url, expected, target, kind = entry
    download_url = url.replace("https://mirror.msys2.org/", "https://repo.msys2.org/")
    archive = CACHE / (name + (".tar.zst" if kind == "tar" else ".zip"))
    if not archive.exists():
        print("Downloading", name, flush=True)
        try:
            with urllib.request.urlopen(download_url, timeout=60) as response, archive.open("wb") as output:
                shutil.copyfileobj(response, output, 1024 * 1024)
        except Exception as error:
            raise RuntimeError(name + " download failed from " + download_url + ": " + str(error)) from error
    with archive.open("rb") as stream: digest = hashlib.file_digest(stream, "sha256").hexdigest()
    if expected and digest != expected: raise RuntimeError("SHA256 mismatch: " + name)
    return entry, archive, digest

def main():
    CACHE.mkdir(parents=True, exist_ok=True)
    records = []
    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as executor:
        downloaded = list(executor.map(download, INPUTS))
    # Serial extraction avoids racing shared package metadata/destination files.
    for (name, url, expected, target, kind), archive, digest in downloaded:
        marker = target / (".input-" + name)
        if not marker.exists():
            target.mkdir(parents=True, exist_ok=True)
            if kind == "tar":
                cmake = str(TOOLS / "cmake/bin/cmake.exe")
                listing = subprocess.run([cmake, "-E", "tar", "tf", str(archive)], capture_output=True, text=True, encoding="utf-8", errors="replace", creationflags=subprocess.CREATE_NO_WINDOW)
                if listing.returncode: raise RuntimeError(listing.stderr)
                for filename in listing.stdout.splitlines():
                    if pathlib.PurePosixPath(filename).is_absolute() or ".." in pathlib.PurePosixPath(filename).parts:
                        raise RuntimeError("Unsafe tar path")
                result = subprocess.run([cmake, "-E", "tar", "xf", str(archive)], cwd=target, capture_output=True, creationflags=subprocess.CREATE_NO_WINDOW)
                if result.returncode: raise RuntimeError(repr(result.stderr))
            else:
                with tempfile.TemporaryDirectory(dir=target.parent, prefix=name + "-") as directory:
                    unpacked = pathlib.Path(directory)
                    with zipfile.ZipFile(archive) as zipped:
                        for member in zipped.infolist():
                            if not (unpacked / member.filename).resolve().is_relative_to(unpacked.resolve()):
                                raise RuntimeError("Unsafe zip path")
                        zipped.extractall(unpacked)
                    children = list(unpacked.iterdir())
                    content_root = children[0] if kind == "zip" and len(children) == 1 and children[0].is_dir() else unpacked
                    shutil.copytree(content_root, target, dirs_exist_ok=True)
            marker.write_text(digest + "\n", encoding="utf-8")
        records.append({"name": name, "url": url, "sha256": digest, "upstream_digest_verified": expected is not None, "path": target.relative_to(ROOT).as_posix()})
        print("Ready", name, flush=True)
    (ROOT / "scripts/distribution-inputs.lock.json").write_text(json.dumps(records, indent=2) + "\n", encoding="utf-8")

if __name__ == "__main__": main()
