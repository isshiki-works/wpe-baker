module;

#include <vulkan/vulkan.h>
// 只取 VMA 的声明部分；实现（VMA_IMPLEMENTATION 那个 TU）本步仍由 vvk 提供。
#include <vk_mem_alloc.h>

export module wescene.vk:unique;
import rstd.cppstd;

// 独占句柄：离开作用域时按 Traits 销毁一次，只可移动。
// Traits 给出 Owner（销毁时要带的上级对象）与 static void Destroy(Owner, T)。
// 空句柄（T {}）不销毁，与 vvk::Handle 相同。
export namespace owe::vk
{

template<typename T>
struct DestroyTraits;

template<typename T, typename Traits = DestroyTraits<T>>
class Unique {
public:
    using Owner = typename Traits::Owner;

    Unique() noexcept = default;
    Unique(Owner owner, T handle) noexcept: m_owner(owner), m_handle(handle) {}

    Unique(const Unique&)            = delete;
    Unique& operator=(const Unique&) = delete;

    Unique(Unique&& other) noexcept
        : m_owner(other.m_owner), m_handle(std::exchange(other.m_handle, T {})) {}

    // 先把对方的句柄拿过来再释放自己的：自移动时拿走的就是自己的句柄，不会被销毁。
    Unique& operator=(Unique&& other) noexcept {
        const Owner owner  = other.m_owner;
        const T     handle = std::exchange(other.m_handle, T {});
        Release();
        m_owner  = owner;
        m_handle = handle;
        return *this;
    }

    ~Unique() { Release(); }

    const T& operator*() const noexcept { return m_handle; }

private:
    void Release() noexcept {
        if (m_handle != T {}) Traits::Destroy(m_owner, m_handle);
    }

    Owner m_owner {};
    T     m_handle {};
};

// 设备级对象：vkDestroyX(device, handle, nullptr)。
template<typename T, auto DestroyFn>
struct DeviceDestroy {
    using Owner = VkDevice;
    static void Destroy(VkDevice device, T handle) noexcept { DestroyFn(device, handle, nullptr); }
};

// 只放本步切换点（Scene/Resource/Descriptor.cppm）与 T5b Nv12ToRgba 软件路径要用的类型，
// 其余等 T4b 用到时再加。命令缓冲随命令池销毁一并释放，描述符池与集合走 DescriptorArenaGeneration。
template<>
struct DestroyTraits<VkDescriptorSetLayout>
    : DeviceDestroy<VkDescriptorSetLayout, vkDestroyDescriptorSetLayout> {};
template<>
struct DestroyTraits<VkSampler> : DeviceDestroy<VkSampler, vkDestroySampler> {};
template<>
struct DestroyTraits<VkImage> : DeviceDestroy<VkImage, vkDestroyImage> {};
template<>
struct DestroyTraits<VkImageView> : DeviceDestroy<VkImageView, vkDestroyImageView> {};
template<>
struct DestroyTraits<VkBuffer> : DeviceDestroy<VkBuffer, vkDestroyBuffer> {};
template<>
struct DestroyTraits<VkDeviceMemory> : DeviceDestroy<VkDeviceMemory, vkFreeMemory> {};
template<>
struct DestroyTraits<VkShaderModule> : DeviceDestroy<VkShaderModule, vkDestroyShaderModule> {};
template<>
struct DestroyTraits<VkPipelineLayout> : DeviceDestroy<VkPipelineLayout, vkDestroyPipelineLayout> {
};
template<>
struct DestroyTraits<VkPipeline> : DeviceDestroy<VkPipeline, vkDestroyPipeline> {};
template<>
struct DestroyTraits<VkCommandPool> : DeviceDestroy<VkCommandPool, vkDestroyCommandPool> {};
template<>
struct DestroyTraits<VkFence> : DeviceDestroy<VkFence, vkDestroyFence> {};
template<>
struct DestroyTraits<VkSemaphore> : DeviceDestroy<VkSemaphore, vkDestroySemaphore> {};

// VMA 分配物：销毁时连同分配一起还给分配器（vmaDestroyBuffer/vmaDestroyImage）。
// 与 DeviceDestroy 一样做成模板，用到时才实例化，本库自身不引用 VMA 符号。
struct VmaOwner {
    VmaAllocator  allocator {};
    VmaAllocation allocation {};
};

template<typename T, auto DestroyFn>
struct VmaDestroy {
    using Owner = VmaOwner;
    static void Destroy(VmaOwner owner, T handle) noexcept {
        DestroyFn(owner.allocator, handle, owner.allocation);
    }
};

using VmaBuffer = Unique<VkBuffer, VmaDestroy<VkBuffer, vmaDestroyBuffer>>;
using VmaImage  = Unique<VkImage, VmaDestroy<VkImage, vmaDestroyImage>>;

} // namespace owe::vk
