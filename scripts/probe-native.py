"""Small native Windows C++20-module smoke test; no dependency downloads."""
from __future__ import annotations

import json
import os
import pathlib
import subprocess

ROOT = pathlib.Path(__file__).resolve().parents[1]
TOOLS = ROOT / ".tools"
PROBE = TOOLS / "module-probe"

def main() -> None:
    PROBE.mkdir(exist_ok=True)
    (PROBE / "probe.cppm").write_text(
        "module;\n#include <string>\nexport module probe;\n"
        "export std::string message() { return \"native-module-ok\"; }\n",
        encoding="utf-8",
    )
    (PROBE / "main.cpp").write_text(
        "#include <iostream>\nimport probe;\n"
        "int main() { std::cout << message() << '\\n'; }\n",
        encoding="utf-8",
    )
    env = dict(os.environ)
    env["PATH"] = str(TOOLS / "llvm-mingw" / "bin") + os.pathsep + env.get("PATH", "")
    clang = str(TOOLS / "llvm-mingw" / "bin" / "clang++.exe")
    commands = [
        [clang, "--version"],
        [clang, "-std=c++20", "--precompile", "probe.cppm", "-o", "probe.pcm"],
        [clang, "-std=c++20", "main.cpp", "probe.pcm", "-fprebuilt-module-path=.", "-o", "probe.exe"],
        [str(PROBE / "probe.exe")],
    ]
    records = []
    for command in commands:
        result = subprocess.run(command, cwd=PROBE, env=env, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=60, creationflags=subprocess.CREATE_NO_WINDOW)
        records.append({"command": command, "returncode": result.returncode, "stdout": result.stdout, "stderr": result.stderr})
        print(result.stdout, end="")
        print(result.stderr, end="")
        (PROBE / "result.json").write_text(json.dumps(records, indent=2) + "\n", encoding="utf-8")
        if result.returncode:
            raise SystemExit(result.returncode)

if __name__ == "__main__":
    main()
