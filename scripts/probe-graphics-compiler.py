"""Reproduce one compiler failure into project-local scratch outputs."""
import ctypes
import importlib.util
import json
import pathlib
import subprocess

ROOT = pathlib.Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("native_build", ROOT / "scripts/build-native.py")
native = importlib.util.module_from_spec(spec)
spec.loader.exec_module(native)
database = json.loads((ROOT / "build/native/compile_commands.json").read_text(encoding="utf-8"))
entry = next(item for item in database if item["file"].endswith("Scene/Resource/Graphics.cppm"))
shell = ctypes.WinDLL("shell32")
shell.CommandLineToArgvW.argtypes = [ctypes.c_wchar_p, ctypes.POINTER(ctypes.c_int)]
shell.CommandLineToArgvW.restype = ctypes.POINTER(ctypes.c_wchar_p)
count = ctypes.c_int()
pointer = shell.CommandLineToArgvW(entry["command"], ctypes.byref(count))
command = [pointer[index] for index in range(count.value)]
ctypes.WinDLL("kernel32").LocalFree(pointer)
scratch = ROOT / ".tools/compiler-probes"
scratch.mkdir(exist_ok=True)
command = [argument for argument in command if argument != "-DFT_SIZEOF_LONG=8"]
command += ["-fmodules-reduced-bmi", "-o", str(scratch / "graphics-reduced.obj"), "-fmodule-output=" + str(scratch / "graphics-reduced.pcm")]
result = subprocess.run(command, cwd=entry["directory"], env=native.native_environment(), capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=60, creationflags=subprocess.CREATE_NO_WINDOW)
(scratch / "graphics-reduced.json").write_text(json.dumps({"command": command, "returncode": result.returncode, "stdout": result.stdout, "stderr": result.stderr}, indent=2) + "\n", encoding="utf-8")
print("Compiler probe exit:", result.returncode)
for line in result.stderr.splitlines():
    if "error:" in line: print(line)
raise SystemExit(result.returncode)
