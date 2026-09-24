"""Prepare isolated, pinned FFmpeg/x264/x265 sources and an offline x264 bundle."""
from __future__ import annotations

import hashlib
import json
import pathlib
import shutil
import subprocess
import tarfile
import urllib.request
import zipfile

ROOT = pathlib.Path(__file__).resolve().parents[1]
DEST = ROOT / ".deps/ffmpeg-encoder-gpl2"
CACHE = ROOT / ".tools/downloads"
X264_URL = "https://code.videolan.org/videolan/x264.git"
X264_REV = "b35605ace3ddf7c1a5d67a2eb553f034aef41d55"
# version.sh compares HEAD with origin/master to compute the revision number.
X264_MASTER = "0480cb05fa188d37ae87e8f4fd8f1aea3711f7ee"
FFMPEG_REV = "38b88335f99e76ed89ff3c93f877fdefce736c13"
FFMPEG_SHA = "c3453fbfc7ca25423f4984a83ceda01949d458a8bc04f9d68fab7c392f75b3ab"
X265_URL = "https://download.videolan.org/pub/videolan/x265/x265_4.2.tar.gz"
X265_SHA = "40b1ea0453e0309f0eba934e0ddf533f8f6295966679e8894e8f1c1c8d5e1210"
# NVENC 头文件（MIT，只含头文件，运行时动态加载驱动）：FFmpeg 8.1 configure 首选 ffnvcodec >= 12.1.14.0。
# 发布包内容与 git 标签 n12.1.14.0 逐文件相同（9/24 核对）。
NVCODEC_URL = "https://github.com/FFmpeg/nv-codec-headers/releases/download/n12.1.14.0/nv-codec-headers-12.1.14.0.tar.gz"
NVCODEC_SHA = "62b30ab37e4e9be0d0c5b37b8fee4b094e38e570984d56e1135a6b6c2c164c9f"


def sha256(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def git(arguments, directory=None):
    result = subprocess.run(["git", "-c", "core.autocrlf=false", *map(str, arguments)],
                            cwd=directory or ROOT, capture_output=True, text=True,
                            encoding="utf-8", errors="replace", creationflags=subprocess.CREATE_NO_WINDOW)
    if result.returncode:
        raise RuntimeError(result.stderr)
    return result.stdout.strip()


def pinned_tarball(url, digest, target):
    archive = CACHE / url.rsplit("/", 1)[1]
    if not archive.exists():
        with urllib.request.urlopen(url, timeout=60) as response:
            data = response.read()
        if hashlib.sha256(data).hexdigest() != digest:
            raise RuntimeError("Downloaded source failed the pinned SHA256 check: " + url)
        archive.write_bytes(data)
    if sha256(archive) != digest:
        raise RuntimeError("The pinned source archive has changed: " + archive.name)
    with tarfile.open(archive) as tar:
        for member in tar:
            relative = pathlib.PurePosixPath(member.name)
            if relative.is_absolute() or ".." in relative.parts or not (member.isfile() or member.isdir()):
                raise RuntimeError("Unsafe source archive member: " + member.name)
            path = target.joinpath(*relative.parts[1:])
            if member.isdir():
                path.mkdir(parents=True, exist_ok=True)
            else:
                with tar.extractfile(member) as source:
                    data = source.read()
                if path.exists() and path.read_bytes() != data:
                    raise RuntimeError("Preserving locally changed source: " + str(path))
                path.parent.mkdir(parents=True, exist_ok=True)
                if not path.exists():
                    path.write_bytes(data)
    return archive


def main():
    sources = DEST / "sources"
    sources.mkdir(parents=True, exist_ok=True)
    CACHE.mkdir(parents=True, exist_ok=True)
    x265_archive = pinned_tarball(X265_URL, X265_SHA, sources / "x265")
    nvcodec_archive = pinned_tarball(NVCODEC_URL, NVCODEC_SHA, sources / "nv-codec-headers")
    x264 = sources / "x264"
    bundle = CACHE / f"x264-{X264_REV}.bundle"
    if not (x264 / ".git").exists():
        if x264.exists() and any(x264.iterdir()):
            raise RuntimeError("Refusing to replace a nonempty x264 source directory")
        if bundle.exists():
            git(["clone", "--branch", "source-pin", bundle, x264])
            git(["fetch", bundle, "refs/heads/master-pin:refs/remotes/origin/master"], x264)
        else:
            git(["clone", "--branch", "stable", X264_URL, x264])
    if git(["status", "--porcelain"], x264):
        raise RuntimeError("x264 source has local changes; preserve them instead of replacing the checkout")
    for revision in (X264_REV, X264_MASTER):
        try:
            git(["cat-file", "-e", revision + "^{commit}"], x264)
        except RuntimeError:
            git(["fetch", X264_URL, revision], x264)
    git(["checkout", "--detach", X264_REV], x264)
    git(["update-ref", "refs/remotes/origin/master", X264_MASTER], x264)
    git(["update-ref", "refs/heads/source-pin", X264_REV], x264)
    git(["update-ref", "refs/heads/master-pin", X264_MASTER], x264)
    if not bundle.exists():
        git(["bundle", "create", bundle, "refs/heads/source-pin", "refs/heads/master-pin"], x264)
    git(["bundle", "verify", bundle], x264)
    x264_zip = CACHE / f"x264-{X264_REV}.zip"
    if not x264_zip.exists():
        git(["archive", "--format=zip", f"--prefix=x264-{X264_REV}/", "-o", x264_zip, X264_REV], x264)
    patch = DEST / "patches/x264.patch"
    patch.parent.mkdir(exist_ok=True)
    patch.write_text(git(["diff", "--binary", X264_REV], x264), encoding="utf-8")

    ffmpeg_zip = CACHE / "ffmpeg-lgpl21-source.zip"
    if not ffmpeg_zip.exists() or sha256(ffmpeg_zip) != FFMPEG_SHA:
        raise RuntimeError("The pinned FFmpeg 8.1.2 archive is missing or changed; run fetch-ffmpeg-lgpl21-inputs.py")
    ffmpeg = sources / "ffmpeg"
    if not ffmpeg.exists():
        ffmpeg.mkdir()
        with zipfile.ZipFile(ffmpeg_zip) as archive:
            for member in archive.infolist():
                relative = pathlib.PurePosixPath(member.filename)
                if relative.is_absolute() or ".." in relative.parts:
                    raise RuntimeError("Unsafe source archive path")
                path = ffmpeg.joinpath(*relative.parts[1:])
                if member.is_dir():
                    path.mkdir(parents=True, exist_ok=True)
                else:
                    path.parent.mkdir(parents=True, exist_ok=True)
                    with archive.open(member) as source, path.open("wb") as target:
                        shutil.copyfileobj(source, target)
    records = {
        "schema_version": 1,
        "ffmpeg": {"tag": "n8.1.2", "revision": FFMPEG_REV,
                   "url": f"https://codeload.github.com/FFmpeg/FFmpeg/zip/{FFMPEG_REV}",
                   "archive": ffmpeg_zip.relative_to(ROOT).as_posix(), "sha256": FFMPEG_SHA},
        "x264": {"url": X264_URL, "revision": X264_REV, "version_master_revision": X264_MASTER,
                 "commit_epoch": int(git(["show", "-s", "--format=%ct", X264_REV], x264)),
                 "archive": x264_zip.relative_to(ROOT).as_posix(), "sha256": sha256(x264_zip),
                 "bundle": bundle.relative_to(ROOT).as_posix(), "bundle_sha256": sha256(bundle),
                 "patch": patch.relative_to(ROOT).as_posix(), "patch_sha256": sha256(patch),
                 "license": "GPL-2.0-or-later", "license_evidence": "COPYING and x264.h copyright header"},
        "x265": {"version": "4.2", "version_string": "4.2+1-e444744", "url": X265_URL,
                 "archive": x265_archive.relative_to(ROOT).as_posix(), "sha256": X265_SHA,
                 "upstream_digest_url": "https://get.videolan.org/x265/x265_4.2.tar.gz",
                 "upstream_digest_verified": True, "license": "GPL-2.0-or-later",
                 "license_evidence": "COPYING and source/x265.h copyright header"},
        "nv-codec-headers": {"tag": "n12.1.14.0", "url": NVCODEC_URL,
                             "archive": nvcodec_archive.relative_to(ROOT).as_posix(), "sha256": NVCODEC_SHA,
                             "license": "MIT", "license_evidence": "header comments in include/ffnvcodec/*.h"},
    }
    (ROOT / "scripts/encoder-inputs.lock.json").write_text(json.dumps(records, indent=2) + "\n", encoding="utf-8")
    print("Ready: pinned FFmpeg 8.1.2, x264", X264_REV, "and x265 4.2")


if __name__ == "__main__":
    main()
