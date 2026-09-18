// SPDX-License-Identifier: MIT
#include "../engine/src/Scene/Text/WindowsFontResolver.h"
#include <iostream>

int main() {
#ifdef _WIN32
    using namespace owe::text::windows;
    auto arial = ResolveFamily("Arial");
    if (arial.path.empty() || !std::filesystem::is_regular_file(arial.path)) return 1;
    if (!ResolveFamily("WpeBaker Missing Family 9F73E1").path.empty()) return 2;
    auto cjk = ResolveCodepoint(0x4E2D);
    if (cjk.path.empty() || !std::filesystem::is_regular_file(cjk.path)) return 3;
    if (!ResolveCodepoint(0x110000).path.empty()) return 4;
    std::cout << "native-font-ok arial-face=" << arial.face_index << " cjk-face=" << cjk.face_index << '\n';
    return 0;
#else
    return 77;
#endif
}
