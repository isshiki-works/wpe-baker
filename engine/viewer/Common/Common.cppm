module;

export module viewer.common;

import rstd.cppstd;

export import :arg;

export namespace viewer
{

std::filesystem::path ExecutableDir(const char* argv0);
std::filesystem::path DefaultCacheDir(std::string_view name);

void InitGlfwPlatformHint(bool force_x11);

} // namespace viewer
