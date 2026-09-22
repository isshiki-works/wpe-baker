"""Native Windows build of wpe-render: CMake + Ninja + project-local Clang 22.

The configuration lives in engine/CMakeLists.txt (one hand-written CMakeLists per
package) and engine/CMakePresets.json. Third-party libraries without C++ modules
are built once per lock hash under build/third-party/<key>/ and imported.
Nothing is downloaded by this command.
"""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
import pathlib
import re
import subprocess

ROOT = pathlib.Path(__file__).resolve().parents[1]
TOOLS, DEPS, ENGINE = ROOT / ".tools", ROOT / ".deps", ROOT / "engine"
DEFAULT_TOOLCHAIN = ".tools/llvm-mingw-22"
DEFAULT_FFMPEG = ".deps/ffmpeg-lgpl21/prefix"
provenance_spec = importlib.util.spec_from_file_location("native_provenance", ROOT / "scripts/native-provenance.py")
provenance = importlib.util.module_from_spec(provenance_spec)
provenance_spec.loader.exec_module(provenance)

# 预构建第三方库的缓存键：这些锁条目 + 定义它们编译方式的 CMake 文件。
THIRD_PARTY_INPUTS = ["glslang", "freetype", "quickjs", "lz4", "vma", "spirv-reflect", "llvm-mingw-22", "cmake", "ninja"]

def native_environment(toolchain: pathlib.Path, ffmpeg_root: pathlib.Path) -> dict[str, str]:
    environment = dict(os.environ)
    directories = [toolchain / "bin", TOOLS / "cmake/bin", TOOLS / "ninja", TOOLS / "pkgconf/bin", DEPS / "install/bin", ffmpeg_root / "bin"]
    environment["PATH"] = os.pathsep.join(map(str, directories)) + os.pathsep + environment.get("PATH", "")
    environment["CMAKE_PREFIX_PATH"] = str(DEPS / "install")
    environment["PKG_CONFIG_PATH"] = os.pathsep.join(map(str, [ffmpeg_root / "lib/pkgconfig", DEPS / "install/lib/pkgconfig"]))
    environment["PKG_CONFIG_LIBDIR"] = environment["PKG_CONFIG_PATH"]
    environment["VULKAN_SDK"] = str(DEPS / "install")
    temporary = TOOLS / "tmp"
    temporary.mkdir(parents=True, exist_ok=True)
    environment["TMP"] = str(temporary)
    environment["TEMP"] = str(temporary)
    return environment

def run(command: list[str], log_name: str, environment: dict[str, str]) -> None:
    logs = TOOLS / "build-logs"
    logs.mkdir(exist_ok=True)
    print(subprocess.list2cmdline(command), flush=True)
    with (logs / log_name).open("w", encoding="utf-8") as log:
        process = subprocess.Popen(command, cwd=ROOT, env=environment, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, encoding="utf-8", errors="replace", creationflags=subprocess.CREATE_NO_WINDOW)
        assert process.stdout
        for line in process.stdout:
            log.write(line)
            log.flush()
            progress = re.match(r"^\[(\d+)/(\d+)\]", line)
            if ((progress and (int(progress[1]) % 25 == 0 or "Linking" in line))
                or line.startswith(("FAILED:", "ninja:", "CMake Error", "-- Configuring done", "-- Generating done", "-- Build files"))
                or " error:" in line or "fatal error:" in line):
                print(line, end="", flush=True)
        code = process.wait()
        if code: raise SystemExit(code)

def validate_ffmpeg_root(ffmpeg_root: pathlib.Path, build: pathlib.Path) -> None:
    if not ffmpeg_root.resolve().is_relative_to(DEPS.resolve()):
        raise SystemExit("FFmpeg development prefix must remain in the project .deps/ tree")
    required = [ffmpeg_root / directory for directory in ("include", "lib", "bin")]
    required += [ffmpeg_root / "lib/pkgconfig" / (name + ".pc") for name in ("libavcodec", "libavformat", "libavutil", "libswscale", "libswresample")]
    missing = [str(path) for path in required if not path.exists()]
    if missing:
        raise SystemExit("Incomplete FFmpeg development prefix: " + ", ".join(missing))
    cache = build / "CMakeCache.txt"
    if cache.exists():
        cached = re.search(r"^WPE_FFMPEG_ROOT:[^=]+=(.+)$", cache.read_text(encoding="utf-8"), re.MULTILINE)
        if cached and pathlib.Path(cached[1].strip()).resolve() != ffmpeg_root.resolve():
            raise SystemExit("Build directory already uses FFmpeg " + cached[1].strip() + "; choose a separate --build-dir for " + str(ffmpeg_root))

def third_party_key(toolchain: pathlib.Path) -> str:
    lock = {entry["name"]: entry for entry in json.loads((ROOT / "scripts/native-inputs.lock.json").read_text(encoding="utf-8"))}
    digest = hashlib.sha256()
    for name in THIRD_PARTY_INPUTS:
        digest.update(json.dumps([name, lock[name]["url"], lock[name]["sha256"]]).encode("utf-8"))
    for path in (ENGINE / "CMakeLists.txt", ENGINE / "CMakePresets.json"):
        digest.update(path.read_bytes())
    digest.update(toolchain.relative_to(ROOT).as_posix().encode("utf-8"))
    return digest.hexdigest()[:16]

def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--target", default="wpe-render")
    parser.add_argument("--jobs", type=int, default=4)
    parser.add_argument("--configure-only", action="store_true")
    parser.add_argument("--toolchain", default=DEFAULT_TOOLCHAIN, help="Project-local LLVM-MinGW directory")
    parser.add_argument("--build-dir", default="build/native-release22", help="Separate output directory for this compiler and FFmpeg prefix")
    parser.add_argument("--ffmpeg-root", default=DEFAULT_FFMPEG, help="Project-local FFmpeg development prefix (default: verified LGPL 2.1 build)")
    options = parser.parse_args()
    toolchain = pathlib.Path(os.path.abspath(ROOT / options.toolchain))
    build = (ROOT / options.build_dir).resolve()
    ffmpeg_root = pathlib.Path(os.path.abspath(ROOT / options.ffmpeg_root))
    if not toolchain.resolve().is_relative_to(TOOLS.resolve()) or not build.is_relative_to((ROOT / "build").resolve()):
        raise SystemExit("Toolchain and build directories must remain in the project .tools/ and build/ trees")
    validate_ffmpeg_root(ffmpeg_root, build)
    input_snapshot = provenance.snapshot(ffmpeg_root)
    environment = native_environment(toolchain, ffmpeg_root)
    vk_import = DEPS / "install/lib/vulkan-1.dll.a"
    vk_import.parent.mkdir(parents=True, exist_ok=True)
    if not vk_import.exists():
        run([str(toolchain / "bin/llvm-dlltool.exe"), "-m", "i386:x86-64", "-d", str(DEPS / "vulkan-loader/loader/vulkan-1.def"), "-l", str(vk_import)], "vulkan-import.log", environment)
    cmake = str(TOOLS / "cmake/bin/cmake.exe")
    # 预设里是默认工具链与 FFmpeg 前缀；只有命令行改了它们才覆盖。
    preset = [cmake, "-S", str(ENGINE), "--preset", "release"]
    if options.toolchain != DEFAULT_TOOLCHAIN:
        preset += ["-DCMAKE_C_COMPILER=" + str(toolchain / "bin/clang.exe"), "-DCMAKE_CXX_COMPILER=" + str(toolchain / "bin/clang++.exe"), "-DCMAKE_CXX_COMPILER_CLANG_SCAN_DEPS=" + str(toolchain / "bin/clang-scan-deps.exe")]
    third_party = ROOT / "build/third-party" / third_party_key(toolchain)
    if not (third_party / "complete").exists():
        run(preset + ["-B", str(third_party), "-DWPE_THIRD_PARTY_ONLY=ON"], "third-party-configure.log", environment)
        run([cmake, "--build", str(third_party), "-j", str(options.jobs)], "third-party-build.log", environment)
        (third_party / "complete").write_text("", encoding="utf-8")
    configuration = preset + ["-B", str(build), "-DWPE_THIRD_PARTY_DIR=" + str(third_party), "-DWPE_RENDER_SOURCE_DIGEST=" + input_snapshot["source_sha256"]]
    if options.ffmpeg_root != DEFAULT_FFMPEG:
        configuration.append("-DWPE_FFMPEG_ROOT=" + str(ffmpeg_root))
    run(configuration, build.name + "-configure.log", environment)
    if not options.configure_only:
        log_target = options.target.replace("/", "_").replace("\\", "_")
        run([cmake, "--build", str(build), "--target", options.target, "-j", str(options.jobs)], build.name + "-" + log_target + ".log", environment)
        binary = build / "bin" / (options.target + ".exe")
        if binary.exists():
            result = provenance.finalize(input_snapshot, options.target, binary, environment, build, toolchain)
            print("Build provenance:", result["status"], result["binary"]["sha256"], flush=True)
            if result["status"] != "verified-source-binding":
                raise SystemExit("Sources changed during the build; rebuild after edits settle before testing this executable")

if __name__ == "__main__":
    main()
