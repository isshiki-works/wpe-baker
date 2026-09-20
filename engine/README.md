# engine

The offline renderer is based on open-wallpaper-engine (GPL-2.0). Its source tree and full Git history are provided in the release source archive as `engine/` and `engine-upstream.bundle`. `SOURCE.md` and the package build records identify the renderer revision used by each release.

## Development source update

The development branch uses renderer commit `d732d6223600cbb46a4fef5476a3bd749157b4d4`. This update adds GPU capture, resizing and direct encoding, improves internal effect-resolution control, and preserves video clocks when hidden layers are recreated.

`development-update.patch` contains the source changes from base revision `2866b4ff22cde7f0c6cbc20f108d680bcdc1b8a0`. `development-source.json` records the patch checksum and resulting source-tree hash.

To prepare the development renderer, obtain the base revision from the source archive and its Git bundle, run `git apply --check` from the renderer source root, and apply the patch. After staging the resulting files, use `git write-tree` to compare the source-tree hash with `development-source.json`.
