module;

#include <vulkan/vulkan.h>

// wescene.vk：取代 vvk 的 Vulkan 薄层（T4a 起，与 vvk 并存到 T4b 删掉 vvk）。
// 标准 C++20，接口里不出现 rstd 类型；Vulkan 调用直接走 vulkan-1 导出的全局函数。
// VK_CHECK 系列宏在 Check.hpp（宏不能从模块导出）。
export module wescene.vk;
export import :unique;
export import :descriptor;

export namespace owe::vk
{

// 文字逐字抄自 .deps/vvk/src/lib.cpp（错误信息会进日志），不要改。
const char* ToString(VkResult result) noexcept;
const char* ToString(VkFormat format) noexcept;
const char* ToString(VkColorSpaceKHR o) noexcept;

} // namespace owe::vk
