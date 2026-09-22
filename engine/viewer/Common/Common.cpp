module;

#include <GLFW/glfw3.h>

module viewer.common;

import rstd.cppstd;

namespace viewer
{

std::filesystem::path ExecutableDir(const char* argv0) {
    namespace fs = std::filesystem;
    std::error_code ec;
    auto            self = fs::read_symlink("/proc/self/exe", ec);
    if (! ec) return self.parent_path();
    return fs::path(argv0 ? argv0 : "").parent_path();
}

std::filesystem::path DefaultCacheDir(std::string_view name) {
    namespace fs = std::filesystem;
    if (const char* cache = std::getenv("XDG_CACHE_HOME"); cache != nullptr && cache[0] != '\0') {
        return fs::path(cache) / name;
    }
    if (const char* home = std::getenv("HOME"); home != nullptr && home[0] != '\0') {
        return fs::path(home) / ".cache" / name;
    }
    return {};
}

void InitGlfwPlatformHint(bool force_x11) {
    if (force_x11) {
        glfwInitHint(GLFW_PLATFORM, GLFW_PLATFORM_X11);
        return;
    }
    const char* x11_env = std::getenv("WP_GLFW_X11");
    if (x11_env && x11_env[0] == '1') {
        glfwInitHint(GLFW_PLATFORM, GLFW_PLATFORM_X11);
    }
}

} // namespace viewer
