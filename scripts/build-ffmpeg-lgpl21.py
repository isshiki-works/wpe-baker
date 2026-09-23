"""Build native shared FFmpeg decoder/audio libraries in an isolated prefix."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import pathlib
import re
import shutil
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parents[1]
DEST = ROOT / ".deps/ffmpeg-lgpl21"
PREFIX = DEST / "prefix"
TOOLS = ROOT / ".tools"
LLVM = TOOLS / "llvm-mingw-22/bin"
BASH = pathlib.Path(r"C:\Program Files\Git\bin\bash.exe")
# 源码补丁打在 sources/ffmpeg 上，源码包随之带上改动；内容见补丁里的注释
PATCH = ROOT / "scripts/ffmpeg-lgpl21.patch"

def environment():
    values = dict(os.environ)
    directories = [LLVM, TOOLS / "nasm", TOOLS / "ffmpeg-build/usr/bin", TOOLS / "ffmpeg-build/bin", TOOLS / "ninja", TOOLS / "pkgconf/bin", PREFIX / "bin", pathlib.Path(r"C:\Program Files\Git\usr\bin")]
    values["PATH"] = os.pathsep.join(map(str, directories)) + os.pathsep + values.get("PATH", "")
    values["CC"] = str(LLVM / "clang.exe")
    values["CXX"] = str(LLVM / "clang++.exe")
    values["AR"] = str(LLVM / "llvm-ar.exe")
    values["RANLIB"] = str(LLVM / "llvm-ranlib.exe")
    values["PKG_CONFIG"] = str(TOOLS / "pkgconf/bin/pkgconf.exe")
    values["PKG_CONFIG_PATH"] = str(PREFIX / "lib/pkgconfig")
    values["PYTHONPATH"] = str(TOOLS / "meson")
    values["MSYS2_PATH_TYPE"] = "inherit"
    values["CHERE_INVOKING"] = "1"
    values["TMP"] = values["TEMP"] = str(TOOLS / "tmp")
    return values

def run(command, cwd, label):
    logs = DEST / "logs"
    logs.mkdir(parents=True, exist_ok=True)
    print(label + ": " + subprocess.list2cmdline(list(map(str, command))), flush=True)
    with (logs / (label + ".log")).open("w", encoding="utf-8") as log:
        process = subprocess.Popen(list(map(str, command)), cwd=cwd, env=environment(), stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, encoding="utf-8", errors="replace", creationflags=subprocess.CREATE_NO_WINDOW)
        assert process.stdout
        count = 0
        for line in process.stdout:
            log.write(line)
            log.flush()
            count += 1
            if count % 50 == 0 or any(value in line.lower() for value in ["error", "failed", "license:", "installing", "configuration:"]):
                print(line, end="", flush=True)
        code = process.wait()
    if code: raise SystemExit(label + " failed (" + str(code) + "); see " + str(logs / (label + ".log")))

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--stage", choices=["dav1d", "configure", "build", "all"], default="all")
    parser.add_argument("--jobs", type=int, default=4)
    options = parser.parse_args()
    PREFIX.mkdir(parents=True, exist_ok=True)
    dav_build = DEST / "build/dav1d"
    if options.stage in ("dav1d", "all"):
        command = [sys.executable, "-m", "mesonbuild.mesonmain", "setup", dav_build, DEST / "sources/dav1d", "--prefix", PREFIX, "--libdir", "lib", "--buildtype", "release", "--default-library", "static", "--wrap-mode", "nodownload", "-Denable_tools=false", "-Denable_tests=false", "-Denable_examples=false"]
        if (dav_build / "meson-private/coredata.dat").exists(): command.append("--reconfigure")
        run(command, ROOT, "dav1d-configure")
        run([TOOLS / "ninja/ninja.exe", "-C", dav_build, "-j", options.jobs], ROOT, "dav1d-build")
        run([TOOLS / "ninja/ninja.exe", "-C", dav_build, "install"], ROOT, "dav1d-install")
    ff_build = DEST / "build/ffmpeg"
    ff_build.mkdir(parents=True, exist_ok=True)
    # Preserve Vulkan decode capability with the same pinned headers/compiler
    # already built for the renderer. This is a build tool, not a new library.
    glslang = TOOLS / "ffmpeg-build/bin/glslang.exe"
    glslang.parent.mkdir(parents=True, exist_ok=True)
    if not glslang.exists(): shutil.copyfile(ROOT / "build/native22/bin/glslang.exe", glslang)
    flags = [
        "--prefix=" + PREFIX.as_posix(), "--target-os=mingw32", "--arch=x86_64", "--cc=" + (LLVM / "clang.exe").as_posix(),
        "--cxx=" + (LLVM / "clang++.exe").as_posix(), "--ar=" + (LLVM / "llvm-ar.exe").as_posix(), "--ranlib=" + (LLVM / "llvm-ranlib.exe").as_posix(),
        "--nm=" + (LLVM / "llvm-nm.exe").as_posix(), "--strip=" + (LLVM / "llvm-strip.exe").as_posix(), "--windres=" + (LLVM / "llvm-windres.exe").as_posix(),
        "--pkg-config=" + (TOOLS / "pkgconf/bin/pkgconf.exe").as_posix(), "--pkg-config-flags=--static",
        "--disable-gpl", "--disable-version3", "--disable-nonfree", "--disable-autodetect", "--enable-shared", "--disable-static", "--disable-doc", "--disable-debug",
        "--disable-network", "--disable-avdevice", "--disable-avfilter", "--disable-programs", "--enable-ffprobe",
        "--disable-pthreads", "--enable-w32threads", "--enable-libdav1d", "--enable-vulkan", "--enable-d3d11va", "--enable-dxva2",
        "--glslc=" + glslang.as_posix(), "--extra-cflags=-I" + (ROOT / ".deps/vulkan-headers/include").as_posix(),
    ]
    configuration = {"source_commit": "38b88335f99e76ed89ff3c93f877fdefce736c13", "source_tag": "n8.1.2", "dav1d_commit": "54706fc6bc0cdecab7e9593974a4039cc038fca7", "dav1d_version": "1.5.4", "dav1d_license": "BSD-2-Clause", "shell": str(BASH), "flags": flags}
    (DEST / "build-configuration.json").write_text(json.dumps(configuration, indent=2) + "\n", encoding="utf-8")
    if options.stage in ("configure", "all"):
        patch = [BASH.parents[1] / "usr/bin/patch.exe", "-p1", "-s", "-f", "-d", DEST / "sources/ffmpeg", "-i", PATCH]
        # 反向试打能成功说明已打过；否则正向打，打不上（源码不是 n8.1.2 原样）就停
        if subprocess.run(list(map(str, patch + ["-R", "--dry-run"])), capture_output=True, creationflags=subprocess.CREATE_NO_WINDOW).returncode:
            run(patch, ROOT, "ffmpeg-patch")
        run([BASH, "--noprofile", "--norc", (DEST / "sources/ffmpeg/configure").as_posix(), *flags], ff_build, "ffmpeg-configure")
    if options.stage in ("build", "all"):
        # POSIX make resolves configure's /c/... paths using the Git Bash
        # runtime already on PATH; mingw32-make cannot resolve those paths.
        make = TOOLS / "ffmpeg-build/usr/bin/make.exe"
        run([make, "-j", options.jobs], ff_build, "ffmpeg-build")
        run([make, "install"], ff_build, "ffmpeg-install")

if __name__ == "__main__": main()
