"""Build a portable H.264/HEVC/AAC encoder from pinned sources using local tools."""
from __future__ import annotations

import argparse
import json
import os
import pathlib
import subprocess

ROOT = pathlib.Path(__file__).resolve().parents[1]
DEST = ROOT / ".deps/ffmpeg-encoder-gpl2"
PREFIX = DEST / "prefix"
TOOLS = ROOT / ".tools"
LLVM = TOOLS / "llvm-mingw-22/bin"
BASH = pathlib.Path(r"C:\Program Files\Git\bin\bash.exe")
MAKE = TOOLS / "ffmpeg-build/usr/bin/make.exe"
CMAKE = TOOLS / "cmake/bin/cmake.exe"


def environment():
    values = dict(os.environ)
    paths = [LLVM, TOOLS / "nasm", MAKE.parent, TOOLS / "pkgconf/bin", PREFIX / "bin",
             pathlib.Path(r"C:\Program Files\Git\usr\bin"), pathlib.Path(r"C:\Program Files\Git\cmd")]
    values["PATH"] = os.pathsep.join(map(str, paths)) + os.pathsep + values.get("PATH", "")
    for name, executable in [("CC", "clang.exe"), ("CXX", "clang++.exe"), ("AR", "llvm-ar.exe"),
                             ("RANLIB", "llvm-ranlib.exe"), ("STRIP", "llvm-strip.exe"), ("RC", "llvm-windres.exe")]:
        values[name] = str(LLVM / executable)
    values["AS"] = str(TOOLS / "nasm/nasm.exe")
    values["PKG_CONFIG"] = str(TOOLS / "pkgconf/bin/pkgconf.exe")
    values["PKG_CONFIG_PATH"] = values["PKG_CONFIG_LIBDIR"] = str(PREFIX / "lib/pkgconfig")
    values["MSYS2_PATH_TYPE"] = "inherit"
    values["CHERE_INVOKING"] = "1"
    values["TMP"] = values["TEMP"] = str(TOOLS / "tmp")
    lock = json.loads((ROOT / "scripts/encoder-inputs.lock.json").read_text(encoding="utf-8"))
    values["SOURCE_DATE_EPOCH"] = str(lock["x264"]["commit_epoch"])
    return values


def run(command, cwd, label):
    logs = DEST / "logs"
    logs.mkdir(parents=True, exist_ok=True)
    print(label + ": " + subprocess.list2cmdline(list(map(str, command))), flush=True)
    with (logs / (label + ".log")).open("w", encoding="utf-8") as log:
        process = subprocess.Popen(list(map(str, command)), cwd=cwd, env=environment(),
                                   stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
                                   encoding="utf-8", errors="replace", creationflags=subprocess.CREATE_NO_WINDOW)
        assert process.stdout
        for count, line in enumerate(process.stdout, 1):
            log.write(line)
            log.flush()
            if count % 100 == 0 or any(word in line.lower() for word in ("error:", "failed", "license:", "installing")):
                print(line[:1200], end="", flush=True)
        code = process.wait()
    if code:
        raise SystemExit(f"{label} failed ({code}); see {logs / (label + '.log')}")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--stage", choices=("x264", "x265", "configure", "build", "all"), default="all")
    parser.add_argument("--jobs", type=int, default=4)
    options = parser.parse_args()
    PREFIX.mkdir(parents=True, exist_ok=True)
    x264_flags = ["--prefix=" + PREFIX.as_posix(), "--host=x86_64-w64-mingw32", "--enable-static",
                  "--disable-cli", "--disable-opencl", "--disable-avs", "--disable-swscale", "--disable-lavf",
                  "--disable-ffms", "--disable-gpac", "--disable-lsmash", "--disable-bashcompletion",
                  "--bit-depth=all", "--chroma-format=all"]
    x265_flags = ["-G", "Ninja", "-DCMAKE_BUILD_TYPE=Release", "-DCMAKE_SYSTEM_NAME=Windows",
                  "-DCMAKE_SYSTEM_PROCESSOR=AMD64",
                  "-DCMAKE_C_COMPILER=" + (LLVM / "clang.exe").as_posix(),
                  "-DCMAKE_CXX_COMPILER=" + (LLVM / "clang++.exe").as_posix(),
                  "-DCMAKE_MAKE_PROGRAM=" + (TOOLS / "ninja/ninja.exe").as_posix(),
                  "-DNASM_EXECUTABLE=" + (TOOLS / "nasm/nasm.exe").as_posix(),
                  "-DCMAKE_INSTALL_PREFIX=" + PREFIX.as_posix(),
                  "-DENABLE_SHARED=OFF", "-DENABLE_CLI=OFF", "-DENABLE_ASSEMBLY=ON",
                  "-DHIGH_BIT_DEPTH=OFF", "-DENABLE_LIBVMAF=OFF", "-DENABLE_SVT_HEVC=OFF"]
    ffmpeg_flags = [
        "--prefix=" + PREFIX.as_posix(), "--target-os=mingw32", "--arch=x86_64",
        *["--" + key + "=" + (LLVM / executable).as_posix() for key, executable in
          [("cc", "clang.exe"), ("cxx", "clang++.exe"), ("ar", "llvm-ar.exe"), ("ranlib", "llvm-ranlib.exe"),
           ("nm", "llvm-nm.exe"), ("strip", "llvm-strip.exe"), ("windres", "llvm-windres.exe")]],
        "--pkg-config=" + (TOOLS / "pkgconf/bin/pkgconf.exe").as_posix(), "--pkg-config-flags=--static",
        "--extra-ldflags=-Wl,-Bstatic",
        "--enable-gpl", "--disable-version3", "--disable-nonfree", "--disable-autodetect",
        "--enable-shared", "--disable-static", "--disable-doc", "--disable-debug", "--disable-network",
        "--disable-avdevice", "--disable-programs", "--enable-ffmpeg", "--enable-ffprobe",
        "--disable-pthreads", "--enable-w32threads", "--disable-hwaccels", "--disable-vulkan",
        "--enable-d3d11va",
        # FFmpeg's Makefile attaches the shared codec objects to the legacy names too.
        "--enable-hwaccel=h264_d3d11va,h264_d3d11va2,hevc_d3d11va,hevc_d3d11va2,av1_d3d11va,av1_d3d11va2",
        "--disable-encoders", "--enable-encoder=libx264,libx264rgb,libx265,h264_mf,hevc_mf,av1_mf,h264_nvenc,hevc_nvenc,aac,pcm_s16le,pcm_f32le,rawvideo",
        "--enable-libx264", "--enable-libx265", "--enable-mediafoundation", "--enable-ffnvcodec", "--enable-nvenc",
    ]
    configuration = {"x264": x264_flags, "x265": x265_flags, "ffmpeg": ffmpeg_flags,
                     "ffmpeg_build": "build/ffmpeg-hevc", "shell": str(BASH), "make": str(MAKE),
                     "source_date_epoch": environment()["SOURCE_DATE_EPOCH"], "patches": []}
    (DEST / "build-configuration.json").write_text(json.dumps(configuration, indent=2) + "\n", encoding="utf-8")
    x264_build = DEST / "build/x264"
    x264_build.mkdir(parents=True, exist_ok=True)
    if options.stage in ("x264", "all"):
        run([BASH, "--noprofile", "--norc", (DEST / "sources/x264/configure").as_posix(), *x264_flags], x264_build, "x264-configure")
        run([MAKE, "-j", options.jobs], x264_build, "x264-build")
        run([MAKE, "install-lib-static"], x264_build, "x264-install")
    x265_build = DEST / "build/x265-amd64"
    if options.stage in ("x265", "all"):
        run([CMAKE, "-S", DEST / "sources/x265/source", "-B", x265_build, *x265_flags], ROOT, "x265-configure")
        run([CMAKE, "--build", x265_build, "--target", "x265-static", "-j", options.jobs], ROOT, "x265-build")
        run([CMAKE, "--install", x265_build], ROOT, "x265-install")
    ffmpeg_build = DEST / "build/ffmpeg-hevc"
    ffmpeg_build.mkdir(parents=True, exist_ok=True)
    if options.stage in ("configure", "all"):
        # NVENC 只需头文件与 ffnvcodec.pc，驱动在运行时动态加载。
        run([MAKE, "install", "PREFIX=" + PREFIX.as_posix()], DEST / "sources/nv-codec-headers", "nv-codec-headers-install")
        run([BASH, "--noprofile", "--norc", (DEST / "sources/ffmpeg/configure").as_posix(), *ffmpeg_flags], ffmpeg_build, "ffmpeg-configure")
    if options.stage in ("build", "all"):
        run([MAKE, "-j", options.jobs], ffmpeg_build, "ffmpeg-build")
        run([MAKE, "install"], ffmpeg_build, "ffmpeg-install")


if __name__ == "__main__":
    main()
