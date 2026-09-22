# engine/ 与上游的关系

## 上游

- 仓库：https://github.com/waywallen/open-wallpaper-engine （GPL-2.0-only，见同目录 `LICENSE`）
- 分叉基点：`b866e8e711fdd7762385b23601affa1ea5539e3b`
- 我们的改动：`b866e8e` 之上的普通提交，并入时的末端是 `d732d6223600cbb46a4fef5476a3bd749157b4d4`
  （`git log b866e8e..d732d62 -- .` 可看全部 Windows 离线渲染改动）。
- 并入方式：`git subtree add --prefix=engine`，合并提交 `cabe223`；engine 的历史提交保留原哈希。
  之后对 engine 的改动都直接提交在本仓库，不再回推到分叉仓库。

## 约定：上游只读，按需摘修复

- 上游只作参考来源，不向上游推送，也不整体合并上游新版本。需要看上游时加一个只读远端：

  ```
  git remote add upstream https://github.com/waywallen/open-wallpaper-engine.git
  git remote set-url --push upstream DISABLED
  git fetch upstream
  ```

- 只在上游某个具体修复对我们有用时摘取：`git cherry-pick -x <上游提交>`，改路径冲突后提交到
  engine/；提交信息保留 `(cherry picked from commit …)` 一行，便于日后追溯。
- 摘取的修复同样要过渲染器验证（对照版 `wpe-render-ref` 差分 + 语料），差异按登记制处理。
- 上游的构建方式（lito、Linux、查看器、waywallen 插件）不跟进：本仓库只用 `engine/CMakeLists.txt`
  这一套 CMake 构建，入口是 `scripts/build-native-cmake.py`。

## 依赖

渲染器用到的第三方库按 `scripts/native-inputs.lock.json` 锁定，我们对 rstd、vvk、wavsen 的改动
以补丁形式放在 `scripts/dependency-patches/`；许可与来源见仓库根目录 `THIRD-PARTY-NOTICES.md`。
