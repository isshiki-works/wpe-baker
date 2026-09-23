#pragma once

// VK_CHECK 系列宏（取代 vvk/macros.hpp 的 VVK_CHECK*，五种形态一一对应）。
// 宏不能从模块导出，所以单放一个头文件；模块单元在全局模块片段里 include。
// VK_SUBOPTIMAL_KHR 视为成功；其余非 VK_SUCCESS 结果先交给 OnCheckFailure，再执行 act。

#include <vulkan/vulkan.h>

namespace owe::vk
{

// 检查失败时调用。定义由链接方提供，本库不带：
// - 引擎（随 T4b 第一个 VK_CHECK 调用点加入）照 VVK_CHECK：
//   rstd_error("VkResult is \"{}\"", owe::vk::ToString(result)) 之后断言失败 panic，不返回；
// - vk-tests 记录后返回，好让各形态的 act 可测。
void OnCheckFailure(VkResult result) noexcept;

} // namespace owe::vk

#define VK_CHECK(f)         VK_CHECK_ACT(, f)
#define VK_CHECK_BOOL_RE(f) VK_CHECK_ACT(return false, f)
#define VK_CHECK_VOID_RE(f) VK_CHECK_ACT(return, f)
#define VK_CHECK_RE(f)      VK_CHECK_ACT(return _res, f)
#define VK_CHECK_ACT(act, f)                                   \
    {                                                          \
        VkResult _res = (f);                                   \
        if (_res != VK_SUCCESS && _res != VK_SUBOPTIMAL_KHR) { \
            ::owe::vk::OnCheckFailure(_res);                   \
            {                                                  \
                act;                                           \
            };                                                 \
        }                                                      \
    }
