"""Extract the official pkgconf MSI without installing it or invoking msiexec."""
from __future__ import annotations

import ctypes
import hashlib
import json
import pathlib
import shutil
import subprocess
import urllib.request

ROOT = pathlib.Path(__file__).resolve().parents[1]
URL = "https://github.com/pkgconf/pkgconf/releases/download/pkgconf-3.0.7/pkgconf-x64-3.0.7.msi"
DIGEST = "7a316dba4a4498ea952b746c82deed41c597657095a0f776b75654191b45ae44"
BASE = ROOT / ".tools/pkgconf"

def main() -> None:
    BASE.mkdir(parents=True, exist_ok=True)
    source = ROOT / ".tools/downloads/pkgconf-x64-3.0.7.msi"
    if not source.exists():
        with urllib.request.urlopen(URL, timeout=60) as response, source.open("wb") as output:
            shutil.copyfileobj(response, output)
    with source.open("rb") as stream:
        if hashlib.file_digest(stream, "sha256").hexdigest() != DIGEST:
            raise RuntimeError("pkgconf MSI SHA256 mismatch")
    msi = ctypes.WinDLL("msi")
    handle = ctypes.c_uint
    msi.MsiOpenDatabaseW.argtypes = [ctypes.c_wchar_p, ctypes.c_void_p, ctypes.POINTER(handle)]
    msi.MsiDatabaseOpenViewW.argtypes = [handle, ctypes.c_wchar_p, ctypes.POINTER(handle)]
    msi.MsiViewExecute.argtypes = [handle, handle]
    msi.MsiViewFetch.argtypes = [handle, ctypes.POINTER(handle)]
    msi.MsiRecordGetStringW.argtypes = [handle, ctypes.c_uint, ctypes.c_wchar_p, ctypes.POINTER(ctypes.c_uint)]
    msi.MsiRecordReadStream.argtypes = [handle, ctypes.c_uint, ctypes.c_void_p, ctypes.POINTER(ctypes.c_uint)]
    msi.MsiCloseHandle.argtypes = [handle]
    def check(code: int) -> None:
        if code: raise OSError("MSI read-only API error", code)
    def string(record: handle, column: int) -> str:
        length = ctypes.c_uint(32768)
        text = ctypes.create_unicode_buffer(length.value)
        check(msi.MsiRecordGetStringW(record, column, text, ctypes.byref(length)))
        return text.value
    database = handle()
    check(msi.MsiOpenDatabaseW(str(source), None, ctypes.byref(database)))
    def rows(sql: str):
        view = handle()
        check(msi.MsiDatabaseOpenViewW(database, sql, ctypes.byref(view)))
        try:
            check(msi.MsiViewExecute(view, 0))
            while True:
                record = handle()
                code = msi.MsiViewFetch(view, ctypes.byref(record))
                if code == 259: break
                check(code)
                try: yield record
                finally: msi.MsiCloseHandle(record)
        finally: msi.MsiCloseHandle(view)
    try:
        filenames = {string(row, 1): string(row, 2).split("|")[-1] for row in rows("SELECT `File`, `FileName` FROM `File`")}
        cabinets = [string(row, 1)[1:] for row in rows("SELECT `Cabinet` FROM `Media`") if string(row, 1).startswith("#")]
        unpacked = BASE / "unpacked"
        unpacked.mkdir(exist_ok=True)
        for cabinet in cabinets:
            cabinet_path = BASE / pathlib.Path(cabinet).name
            escaped = cabinet.replace("'", "''")
            for row in rows("SELECT `Data` FROM `_Streams` WHERE `Name` = '" + escaped + "'"):
                with cabinet_path.open("wb") as output:
                    buffer = ctypes.create_string_buffer(65536)
                    while True:
                        size = ctypes.c_uint(len(buffer))
                        check(msi.MsiRecordReadStream(row, 1, buffer, ctypes.byref(size)))
                        if not size.value: break
                        output.write(buffer.raw[:size.value])
            completed = subprocess.run([r"C:\Windows\System32\expand.exe", "-F:*", str(cabinet_path), str(unpacked)], capture_output=True, timeout=60, creationflags=subprocess.CREATE_NO_WINDOW)
            if completed.returncode: raise RuntimeError(repr(completed.stderr))
        for key, name in filenames.items():
            matches = list(unpacked.rglob(key))
            if not matches: continue
            destination = BASE / ("bin" if pathlib.Path(name).suffix.lower() in (".exe", ".dll") else "resources") / name
            destination.parent.mkdir(exist_ok=True)
            shutil.copyfile(matches[0], destination)
        binaries = list((BASE / "bin").glob("*.exe"))
        if not binaries: raise RuntimeError("MSI had no extracted executable")
        print("Extracted", ", ".join(item.name for item in binaries))
    finally:
        msi.MsiCloseHandle(database)
    lock = ROOT / "scripts/native-inputs.lock.json"
    entries = json.loads(lock.read_text(encoding="utf-8"))
    if not any(item["url"] == URL for item in entries):
        entries.append({"name": "pkgconf", "url": URL, "sha256": DIGEST, "path": ".tools/pkgconf", "extraction": "read-only MSI database and cabinet extraction; no installation"})
        lock.write_text(json.dumps(entries, indent=2) + "\n", encoding="utf-8")

if __name__ == "__main__":
    main()
