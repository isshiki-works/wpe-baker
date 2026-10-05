# Build dependencies

For a release rebuild, use the matching source archive and its `REBUILD.md`.
It contains the dependency sources, patches, build instructions and records
corresponding to that release.

## Pinned inputs

| Component | Input record | Build entry |
| --- | --- | --- |
| Native tools and renderer dependencies | `native-inputs.lock.json` | `bootstrap-native.py`, `build-native-cmake.py` |
| FFmpeg decoder and dav1d | `distribution-inputs.lock.json` | `fetch-ffmpeg-lgpl21-inputs.py`, `build-ffmpeg-lgpl21.py` |
| FFmpeg encoder, x264, x265 and GPU headers | `encoder-inputs.lock.json`, `encoder-build-tools.lock.json` | `fetch-encoder-inputs.py`, `build-ffmpeg-encoder-gpl2.py` |
| .NET SDK | `../global.json` | `fetch-dotnet.py` |

Tools and dependencies are prepared under `.tools/`, `.dotnet/` and `.deps/`.
The renderer uses `.deps/ffmpeg-lgpl21/prefix`; the encoder is packaged from
`.deps/ffmpeg-encoder-gpl2/portable`. They have separate verification records.
Component licenses are listed in `../THIRD-PARTY-NOTICES.md`.

## Build and package

After preparing the dependencies as described in `REBUILD.md`, run these commands
from the source root. Use a new build directory when changing the toolchain or
FFmpeg prefix.

```powershell
python scripts/build-native-cmake.py --target wpe-render --build-dir build/release
python scripts/package-portable.py --native-build-dir build/release --out dist/release
python scripts/package-source.py --native-build-dir build/release --output dist/release/WpeBaker-source.zip
```

Both packaging commands verify the selected renderer against the source files
and dependency bytes recorded in its `provenance` directory. Distribute the
portable archive together with its corresponding source archive.

Preserve the exact bytes in an existing verified build workspace, including
line endings. Rewriting files during checkout can invalidate source binding
even when the code is equivalent. Rebuild after changing recorded inputs;
do not edit the provenance records to make them match.
