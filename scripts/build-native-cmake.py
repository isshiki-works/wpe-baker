"""Native CMake module build using the engine source lists and pinned deps.

Lito 0.7.1's preprocessor cannot scan the MinGW SDK intrinsic macros, so CMake
uses Clang's own dependency scanner. Nothing is downloaded by this command.
"""
from __future__ import annotations

import argparse
import importlib.util
import os
import pathlib
import re
import subprocess
import tomllib

ROOT = pathlib.Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("build_native", ROOT / "scripts/build-native.py")
base = importlib.util.module_from_spec(spec)
spec.loader.exec_module(base)
provenance_spec = importlib.util.spec_from_file_location("native_provenance", ROOT / "scripts/native-provenance.py")
provenance = importlib.util.module_from_spec(provenance_spec)
provenance_spec.loader.exec_module(provenance)
TOOLS, DEPS, ENGINE = base.TOOLS, base.DEPS, base.ENGINE

PACKAGES = ["src/Core", "src/Audio", "src/Scene/types", "src/Scene/Utils", "src/Vulkan", "src/Scene", "tools/SceneBake", "tests/offline-vulkan", "tests"]
TEST_TARGETS = {"core-tests", "parser-tests", "render-resource-tests", "effect-swap-gpu-tests", "shader-parse-tests", "scene-parse-tests", "scene-runtime-tests", "script-runtime-tests"}
DEPENDENCY_MAP = {"rstd-std": "rstd.std", "rstd-core": "rstd.core", "rstd-cppstd": "rstd.cppstd", "rstd-json": "rstd.json", "rstd-log": "rstd.log", "rstd-argparse": "rstd.argparse", "rstd-bench": "rstd.bench", "rstd-test": "rstd.test", "vvk": "vvk::vvk"}

def quote(value) -> str:
    return '"' + str(value).replace("\\", "/").replace('"', '\\"') + '"'

def generate(source_digest: str | None = None) -> None:
    destination = TOOLS / "generated/owe-targets.cmake"
    destination.parent.mkdir(exist_ok=True)
    lines = ["# Generated from pinned engine lito source manifests."]
    for relative in PACKAGES:
        directory = ENGINE / relative
        manifest = tomllib.loads((directory / "lito.toml").read_text(encoding="utf-8"))
        name = manifest["package"]["name"]
        target = manifest.get("lib") or manifest["bin"][0]
        target_name = target["name"]
        source_root = (directory / manifest["package"].get("source-root", ".")).resolve()
        sources = [(source_root / source).resolve() for source in target["sources"]]
        missing = [str(source) for source in sources if not source.exists()]
        if missing: raise RuntimeError("Missing source files: " + ", ".join(missing))
        interfaces = [source for source in sources if source.suffix == ".cppm"]
        implementations = [source for source in sources if source.suffix != ".cppm"]
        lines.append(("add_library(" + target_name + " STATIC)" if "lib" in manifest else "add_executable(" + target_name + ")"))
        if interfaces:
            lines.append("target_sources(" + target_name + " PUBLIC FILE_SET CXX_MODULES BASE_DIRS " + quote(source_root) + " FILES\n  " + "\n  ".join(map(quote, interfaces)) + ")")
        if implementations:
            lines.append("target_sources(" + target_name + " PRIVATE\n  " + "\n  ".join(map(quote, implementations)) + ")")
        usage = dict(manifest.get("usage", {}))
        for condition in manifest.get("when", []):
            if condition["condition"] == 'target.os == "windows"':
                for key, values in condition["usage"].items():
                    usage[key] = list(usage.get(key, [])) + values
        for kind in ("public", "private"):
            includes = usage.get(kind + "-include-directories", [])
            if includes:
                lines.append("target_include_directories(" + target_name + " " + kind.upper() + " " + " ".join(quote((source_root / path).resolve()) for path in includes) + ")")
            definitions = usage.get(kind + "-definitions", [])
            if definitions:
                lines.append("target_compile_definitions(" + target_name + " " + kind.upper() + " " + " ".join(map(quote, definitions)) + ")")
        for dependency, value in manifest.get("dependencies", {}).items():
            visibility = "PUBLIC" if value.get("visibility", "private") == "public" else "PRIVATE"
            lines.append("target_link_libraries(" + target_name + " " + visibility + " " + DEPENDENCY_MAP.get(dependency, dependency) + ")")
        if usage.get("system-libraries"):
            lines.append("target_link_libraries(" + target_name + " PRIVATE " + " ".join(usage["system-libraries"]) + ")")
        if name == "owe-core": lines.append("target_link_libraries(owe-core PUBLIC wpe-eigen)")
        if name == "owe-vulkan": lines.append("target_link_libraries(owe-vulkan PUBLIC glslang::glslang glslang::SPIRV glslang::glslang-default-resource-limits)")
        if name == "owe-scene": lines.append("target_link_libraries(owe-scene PUBLIC qjs lz4_static freetype)")
        if target_name == "wpe-render" and source_digest:
            lines.append('target_compile_definitions(wpe-render PRIVATE WPE_RENDER_SOURCE_DIGEST="' + source_digest + '")')
        if relative == "tests":
            for test in manifest.get("test", []):
                if test["name"] not in TEST_TARGETS:
                    continue
                test_sources = [(source_root / source).resolve() for source in test["sources"]]
                missing = [str(source) for source in test_sources if not source.exists()]
                if missing:
                    raise RuntimeError("Missing test source files: " + ", ".join(missing))
                test_interfaces = [source for source in test_sources if source.suffix == ".cppm"]
                test_implementations = [source for source in test_sources if source.suffix != ".cppm"]
                lines.append("add_executable(" + test["name"] + " EXCLUDE_FROM_ALL)")
                if test_interfaces:
                    lines.append("target_sources(" + test["name"] + " PUBLIC FILE_SET CXX_MODULES BASE_DIRS " + quote(source_root) + " FILES\n  " + "\n  ".join(map(quote, test_interfaces)) + ")")
                if test_implementations:
                    lines.append("target_sources(" + test["name"] + " PRIVATE\n  " + "\n  ".join(map(quote, test_implementations)) + ")")
                lines.append("target_link_libraries(" + test["name"] + " PRIVATE owe-test-support " + " ".join(DEPENDENCY_MAP.get(dependency, dependency) for dependency in manifest.get("dependencies", {})) + ")")
                if usage.get("options"):
                    lines.append("target_compile_options(" + test["name"] + " PRIVATE " + " ".join(map(quote, usage["options"])) + ")")
    contents = "\n".join(lines) + "\n"
    if not destination.exists() or destination.read_text(encoding="utf-8") != contents:
        destination.write_text(contents, encoding="utf-8")

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
    if not ffmpeg_root.is_relative_to(DEPS.resolve()):
        raise SystemExit("FFmpeg development prefix must remain in the project .deps/ tree")
    required = [ffmpeg_root / directory for directory in ("include", "lib", "bin")]
    required += [ffmpeg_root / "lib/pkgconfig" / (name + ".pc") for name in ("libavcodec", "libavformat", "libavutil", "libswscale", "libswresample")]
    missing = [str(path) for path in required if not path.exists()]
    if missing:
        raise SystemExit("Incomplete FFmpeg development prefix: " + ", ".join(missing))
    cache = build / "CMakeCache.txt"
    if cache.exists():
        text = cache.read_text(encoding="utf-8")
        cached = re.search(r"^WPE_FFMPEG_ROOT:[^=]+=(.+)$", text, re.MULTILINE)
        if cached is None:
            cached = re.search(r"^WAVSEN_AVFORMAT_PREFIX:[^=]+=(.+)$", text, re.MULTILINE)
        if cached and pathlib.Path(cached[1].strip()).resolve() != ffmpeg_root:
            raise SystemExit("Build directory already uses FFmpeg " + cached[1].strip() + "; choose a separate --build-dir for " + str(ffmpeg_root))

def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--target", default="wpe-render")
    parser.add_argument("--jobs", type=int, default=4)
    parser.add_argument("--configure-only", action="store_true")
    parser.add_argument("--toolchain", default=".tools/llvm-mingw-22", help="Project-local LLVM-MinGW directory")
    parser.add_argument("--build-dir", default="build/native-release22", help="Separate output directory for this compiler and FFmpeg prefix")
    parser.add_argument("--ffmpeg-root", default=".deps/ffmpeg-lgpl21/prefix", help="Project-local FFmpeg development prefix (default: verified LGPL 2.1 build)")
    options = parser.parse_args()
    toolchain = (ROOT / options.toolchain).resolve()
    build = (ROOT / options.build_dir).resolve()
    ffmpeg_root = (ROOT / options.ffmpeg_root).resolve()
    if not toolchain.is_relative_to(TOOLS.resolve()) or not build.is_relative_to((ROOT / "build").resolve()):
        raise SystemExit("Toolchain and build directories must remain in the project .tools/ and build/ trees")
    validate_ffmpeg_root(ffmpeg_root, build)
    input_snapshot = provenance.snapshot(ffmpeg_root)
    generate(input_snapshot["source_sha256"])
    environment = base.native_environment(toolchain, ffmpeg_root)
    vk_import = DEPS / "install/lib/vulkan-1.dll.a"
    vk_import.parent.mkdir(parents=True, exist_ok=True)
    if not vk_import.exists():
        run([str(toolchain / "bin/llvm-dlltool.exe"), "-m", "i386:x86-64", "-d", str(DEPS / "vulkan-loader/loader/vulkan-1.def"), "-l", str(vk_import)], "vulkan-import.log", environment)
    cmake = str(TOOLS / "cmake/bin/cmake.exe")
    configuration = [cmake, "-S", str(ENGINE), "-B", str(build), "-G", "Ninja", "-DCMAKE_BUILD_TYPE=Release", "-DCMAKE_C_COMPILER=" + str(toolchain / "bin/clang.exe"), "-DCMAKE_CXX_COMPILER=" + str(toolchain / "bin/clang++.exe"), "-DCMAKE_MAKE_PROGRAM=" + str(TOOLS / "ninja/ninja.exe"), "-DCMAKE_CXX_COMPILER_CLANG_SCAN_DEPS=" + str(toolchain / "bin/clang-scan-deps.exe"), "-DPKG_CONFIG_EXECUTABLE=" + str(TOOLS / "pkgconf/bin/pkgconf.exe"), "-DVulkan_INCLUDE_DIR=" + str(DEPS / "vulkan-headers/include"), "-DVulkan_LIBRARY=" + str(vk_import)]
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
