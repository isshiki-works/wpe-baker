#pragma once
#include <string>
#include <span>

namespace utils
{
std::string genSha1(std::span<const char>);
} // namespace utils
