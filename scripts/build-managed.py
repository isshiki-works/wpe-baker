"""Developer-only build helper. Shipped applications never require Python."""
from __future__ import annotations
import argparse
import os
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parent.parent

def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("project", nargs="?", default="src/Baker.Cli/Baker.Cli.csproj")
    parser.add_argument("--run", action="store_true")
    parser.add_argument("arguments", nargs="*")
    args = parser.parse_args()
    dotnet = ROOT / ".dotnet/dotnet.exe"
    if not dotnet.is_file():
        parser.error("Run scripts/fetch-dotnet.py to prepare the project-local SDK first.")
    env = os.environ.copy()
    env.update({"DOTNET_ROOT": str(dotnet.parent), "DOTNET_CLI_HOME": str(ROOT / ".tools/dotnet-home"),
                "NUGET_PACKAGES": str(ROOT / ".tools/nuget-packages"), "DOTNET_CLI_UI_LANGUAGE": "en-US",
                "DOTNET_NOLOGO": "1", "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
                "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1"})
    command = [str(dotnet), "build", str(ROOT / args.project), "-c", "Release", "--nologo",
               "--ignore-failed-sources", "-p:NuGetAudit=false", "-p:UseSharedCompilation=false"]
    if args.run:
        command = [str(dotnet), "run", "--project", str(ROOT / args.project), "-c", "Release", "--no-build", "--", *args.arguments]
    result = subprocess.run(command, env=env, cwd=ROOT, capture_output=True, timeout=600,
                            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    sys.stdout.buffer.write(result.stdout)
    sys.stderr.buffer.write(result.stderr)
    return result.returncode

if __name__ == "__main__":
    raise SystemExit(main())
