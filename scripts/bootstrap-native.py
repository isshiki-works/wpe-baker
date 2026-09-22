"""Fetch project-local, pinned Windows tools and upstream build inputs.

No machine-wide installation or environment changes are performed.
Run with the existing Python 3 installation.
"""
from __future__ import annotations

import concurrent.futures
import hashlib
import json
import pathlib
import shutil
import sys
import tarfile
import tempfile
import urllib.request
import zipfile

ROOT = pathlib.Path(__file__).resolve().parents[1]
TOOLS = ROOT / ".tools"
DEPS = ROOT / ".deps"
LOCK_PATH = ROOT / "scripts" / "native-inputs.lock.json"
LOCKED = {
    entry["url"]: entry["sha256"]
    for entry in (json.loads(LOCK_PATH.read_text(encoding="utf-8")) if LOCK_PATH.exists() else [])
}

TOOLS_INPUTS = [
    ("llvm-mingw-22", "https://github.com/mstorsjo/llvm-mingw/releases/download/20260616/llvm-mingw-20260616-ucrt-x86_64.zip", "b9b68a4d276e16fa25802aaba458e4638f64b3884c290aaccdc2d87083b6ca35"),
    ("cmake", "https://github.com/Kitware/CMake/releases/download/v4.4.3/cmake-4.4.3-windows-x86_64.zip", "4d52ebab7193a698651639ed80d8d04fd903358843572cf44c7fd234cb7c26ab"),
    ("ninja", "https://github.com/ninja-build/ninja/releases/download/v1.13.2/ninja-win.zip", "07fc8261b42b20e71d1720b39068c2e14ffcee6396b76fb7a795fb460b78dc65"),
]

# Pinned source revisions (LZ4 / FreeType / Vulkan-Headers use release tags).
SOURCES = [
    ("rstd", "https://github.com/litocpp/rstd", "456fec5cc2b87acdb56800e298b5712ea69cdd47"),
    ("vvk", "https://github.com/litocpp/vvk", "f53d60cc70938d0485802750deeb15d18ba033ea"),
    ("wavsen", "https://github.com/hypengw/wavsen", "77dfd33d07112c05df4682e08b98e19153ebe3ab"),
    ("spirv-reflect", "https://github.com/hypengw/SPIRV-Reflect", "355785128c1b6ba808e3a7d0e344814fe6cff502"),
    ("glslang", "https://github.com/KhronosGroup/glslang", "275822a6261ee689aadb1da5f09a0ec2f058685c"),
    ("quickjs", "https://github.com/quickjs-ng/quickjs", "3c051980ab7e783dfbfb1c70c014ce5e05ecf24c"),
    ("vma", "https://github.com/GPUOpen-LibrariesAndSDKs/VulkanMemoryAllocator", "3aa921224c154a0d2c43912bc88e1c42ce1f7607"),
    ("lz4", "https://github.com/lz4/lz4", "v1.10.0"),
    ("freetype", "https://github.com/freetype/freetype", "VER-2-14-1"),
    ("vulkan-headers", "https://github.com/KhronosGroup/Vulkan-Headers", "v1.4.321"),
]

def archive_suffix(url: str) -> str:
    """缓存文件名按 URL 后缀区分：zip、tar.gz 或单个头文件（CLI11 只发布单头）。"""
    return ".tar.gz" if url.endswith(".tar.gz") else ".hpp" if url.endswith(".hpp") else ".zip"

def obtain(name: str, url: str, expected: str | None, base: pathlib.Path) -> dict:
    expected = expected or LOCKED.get(url)
    base.mkdir(parents=True, exist_ok=True)
    cache = base / "downloads"
    cache.mkdir(exist_ok=True)
    suffix = archive_suffix(url)
    archive = cache / (name + suffix)
    if not archive.exists():
        temporary = archive.with_suffix(".part")
        print("Downloading", name, flush=True)
        request = urllib.request.Request(url, headers={"User-Agent": "wpe-baker-native-bootstrap/1"})
        with urllib.request.urlopen(request, timeout=120) as response, temporary.open("wb") as output:
            shutil.copyfileobj(response, output, 1024 * 1024)
        temporary.replace(archive)
    with archive.open("rb") as archive_file:
        digest = hashlib.file_digest(archive_file, "sha256").hexdigest()
    if expected and expected != digest:
        raise RuntimeError(f"SHA256 mismatch for {name}: {digest}")
    destination = base / name
    if not destination.exists() and suffix == ".hpp":
        destination.mkdir()
        shutil.copyfile(archive, destination / url.rsplit("/", 1)[1])
    elif not destination.exists():
        with tempfile.TemporaryDirectory(prefix=name + "-", dir=base) as temporary:
            unpack = pathlib.Path(temporary)
            if suffix == ".tar.gz":
                with tarfile.open(archive) as packed:
                    packed.extractall(unpack, filter="data")
            else:
                with zipfile.ZipFile(archive) as zipped:
                    for member in zipped.infolist():
                        resolved = (unpack / member.filename).resolve()
                        if not resolved.is_relative_to(unpack.resolve()):
                            raise RuntimeError("Unsafe archive member: " + member.filename)
                    zipped.extractall(unpack)
            children = list(unpack.iterdir())
            if len(children) == 1 and children[0].is_dir():
                shutil.move(str(children[0]), str(destination))
            else:
                destination.mkdir()
                for child in children:
                    shutil.move(str(child), str(destination / child.name))
    print("Ready", name, digest, flush=True)
    return {"name": name, "url": url, "sha256": digest, "path": str(destination.relative_to(ROOT))}

def source(item: tuple[str, str, str]) -> dict:
    name, repository, revision = item
    owner_repo = repository.removeprefix("https://github.com/")
    value = obtain(name, f"https://codeload.github.com/{owner_repo}/zip/{revision}", None, DEPS)
    value.update(repository=repository, revision=revision)
    return value

def main() -> None:
    mode = sys.argv[1] if len(sys.argv) > 1 else "all"
    operations = []
    if mode in ("tools", "all"):
        operations += [(obtain, (*item, TOOLS)) for item in TOOLS_INPUTS]
    if mode in ("deps", "all"):
        operations += [(source, (item,)) for item in SOURCES]
    records = []
    errors = []
    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as executor:
        futures = [executor.submit(function, *arguments) for function, arguments in operations]
        for future in concurrent.futures.as_completed(futures):
            try:
                records.append(future.result())
            except Exception as error:
                errors.append(str(error))
                print("ERROR", error, flush=True)
    TOOLS.mkdir(exist_ok=True)
    (TOOLS / ("inputs-" + mode + ".json")).write_text(json.dumps(records, indent=2) + "\n", encoding="utf-8")
    if errors:
        raise SystemExit("; ".join(errors))

if __name__ == "__main__":
    main()
