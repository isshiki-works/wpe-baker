"""Build the pinned OWE graph with project-local tools on native Windows."""
from __future__ import annotations

import argparse
import json
import os
import pathlib
import shutil
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parents[1]
TOOLS = ROOT / ".tools"
DEPS = ROOT / ".deps"
ENGINE = ROOT / "engine"

def native_environment(toolchain: pathlib.Path | None = None, ffmpeg_root: pathlib.Path | None = None) -> dict[str, str]:
    toolchain = toolchain or TOOLS / "llvm-mingw"
    # Keep the archived Lito route's existing default. The supported CMake
    # driver explicitly supplies its selected FFmpeg development prefix.
    ffmpeg_root = ffmpeg_root or DEPS / "ffmpeg"
    environment = dict(os.environ)
    directories = [toolchain / "bin", TOOLS / "cmake/bin", TOOLS / "ninja", TOOLS / "lito/bin", TOOLS / "pkgconf/bin", DEPS / "install/bin", ffmpeg_root / "bin"]
    environment["PATH"] = os.pathsep.join(map(str, directories)) + os.pathsep + environment.get("PATH", "")
    environment["CC"] = str(toolchain / "bin/clang.exe")
    environment["CXX"] = str(toolchain / "bin/clang++.exe")
    environment["CMAKE_PREFIX_PATH"] = str(DEPS / "install")
    environment["PKG_CONFIG_PATH"] = os.pathsep.join(map(str, [ffmpeg_root / "lib/pkgconfig", DEPS / "install/lib/pkgconfig"]))
    environment["PKG_CONFIG_LIBDIR"] = environment["PKG_CONFIG_PATH"]
    environment["VULKAN_SDK"] = str(DEPS / "install")
    # Keep any tool-managed downloads/cache beneath the project as well.
    environment["XDG_CACHE_HOME"] = str(TOOLS / "cache")
    environment["XDG_DATA_HOME"] = str(TOOLS / "data")
    temporary = TOOLS / "tmp"
    temporary.mkdir(parents=True, exist_ok=True)
    environment["TMP"] = str(temporary)
    environment["TEMP"] = str(temporary)
    return environment

def configure() -> None:
    for directory in [DEPS / "install/bin", DEPS / "install/lib/pkgconfig", TOOLS / "cache", TOOLS / "data", ENGINE / ".lito"]:
        directory.mkdir(parents=True, exist_ok=True)
    wrapper = TOOLS / "cmake-lito.exe"
    wrapper_source = ROOT / "scripts/cmake-lito.cpp"
    if not wrapper.exists() or wrapper.stat().st_mtime < wrapper_source.stat().st_mtime:
        command = [str(TOOLS / "llvm-mingw/bin/clang++.exe"), "-std=c++20", "-O2", "-municode", "-static", str(wrapper_source), "-o", str(wrapper)]
        completed = subprocess.run(command, cwd=ROOT, env=native_environment(), capture_output=True, text=True, encoding="utf-8", errors="replace", creationflags=subprocess.CREATE_NO_WINDOW)
        if completed.returncode:
            raise RuntimeError(completed.stdout + completed.stderr)
    native_lock = DEPS / "native-lito.lock"
    if not native_lock.exists():
        shutil.copyfile(ENGINE / "lito.lock", native_lock)
    q = lambda path: json.dumps(str(path).replace("\\", "/"))
    config = [
        "[toolchain]",
        "cc = " + q(TOOLS / "llvm-mingw/bin/clang.exe"),
        "cxx = " + q(TOOLS / "llvm-mingw/bin/clang++.exe"),
        "ar = " + q(TOOLS / "llvm-mingw/bin/llvm-ar.exe"),
        'stdlib = "libc++"', "", "[tools.cmake]",
        "executable = " + q(wrapper),
        'generator = "Ninja"',
        'search-path = ["../.deps/install"]',
        "", "[lock]", 'path = "../.deps/native-lito.lock"',
    ]
    sources = [
        ("https://github.com/litocpp/rstd.git", "rstd"),
        ("https://github.com/litocpp/vvk.git", "vvk"),
        ("https://github.com/hypengw/wavsen.git", "wavsen"),
        ("https://github.com/hypengw/SPIRV-Reflect.git", "spirv-reflect"),
        ("https://gitlab.com/libeigen/eigen.git", "eigen"),
        ("https://github.com/KhronosGroup/glslang.git", "glslang"),
        ("https://github.com/quickjs-ng/quickjs.git", "quickjs"),
        ("https://github.com/GPUOpen-LibrariesAndSDKs/VulkanMemoryAllocator.git", "vma"),
    ]
    for source, name in sources:
        config += ["", "[patch." + json.dumps(source) + "]", 'path = "../.deps/' + name + '"']
    (ENGINE / ".lito/config.toml").write_text("\n".join(config) + "\n", encoding="utf-8")

def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--package", default="wpe-render")
    parser.add_argument("--profile", default="release")
    parser.add_argument("--jobs", type=int, default=4)
    parser.add_argument("--configure-only", action="store_true")
    options = parser.parse_args()
    configure()
    if options.configure_only:
        return
    command = [str(TOOLS / "lito/bin/lito.exe"), "-C", str(ENGINE), "build", "-p", options.package, "--profile", options.profile, "--offline", "-j", str(options.jobs), "--verbose"]
    print(subprocess.list2cmdline(command), flush=True)
    log_directory = TOOLS / "build-logs"
    log_directory.mkdir(exist_ok=True)
    with (log_directory / (options.package + ".log")).open("w", encoding="utf-8") as log:
        process = subprocess.Popen(command, cwd=ROOT, env=native_environment(), stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, encoding="utf-8", errors="replace", creationflags=subprocess.CREATE_NO_WINDOW)
        assert process.stdout is not None
        for line in process.stdout:
            if not line.startswith(("-- Up-to-date:", "-- Installing:")):
                print(line, end="", flush=True)
            log.write(line)
            log.flush()
        raise SystemExit(process.wait())

if __name__ == "__main__":
    main()
