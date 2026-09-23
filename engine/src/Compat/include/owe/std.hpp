// owe/std.hpp：取代 `import rstd.cppstd;`（T2-6）。
// rstd.cppstd 以一个模块把整套标准库名字转出；换成头文件后，原先 import 它的单元改在
// 全局模块片段里包含本头。清单取 rstd.cppstd 的包含表，去掉引擎零使用的
// <regex>/<future>/<coroutine>/<memory_resource>。T6 去模块时改为按需包含 + PCH。
#pragma once

#include <cassert>
#include <cctype>
#include <cerrno>
#include <cmath>
#include <cstdarg>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <ctime>

#include <algorithm>
#include <array>
#include <atomic>
#include <bit>
#include <bitset>
#include <charconv>
#include <chrono>
#include <compare>
#include <concepts>
#include <condition_variable>
#include <deque>
#include <expected>
#include <filesystem>
#include <format>
#include <fstream>
#include <functional>
#include <initializer_list>
#include <iostream>
#include <iterator>
#include <limits>
#include <list>
#include <map>
#include <memory>
#include <mutex>
#include <new>
#include <numeric>
#include <optional>
#include <queue>
#include <random>
#include <ranges>
#include <set>
#include <shared_mutex>
#include <source_location>
#include <span>
#include <sstream>
#include <stack>
#include <stdexcept>
#include <string>
#include <string_view>
#include <system_error>
#include <thread>
#include <tuple>
#include <type_traits>
#include <typeindex>
#include <typeinfo>
#include <unordered_map>
#include <unordered_set>
#include <utility>
#include <variant>
#include <vector>
