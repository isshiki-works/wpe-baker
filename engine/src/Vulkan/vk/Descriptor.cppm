module;
// 先让全局 operator new 可见，避免 Clang 22 在 make_shared 处
// 合成第二个重载（同 Core/IO/Binary.cppm）。
#include <new>

#include <vulkan/vulkan.h>

export module wescene.vk:descriptor;
import rstd.cppstd;

// 描述符 arena，迁自 .deps/vvk/src/descriptor.cppm：判断与返回码照旧，
// 只把 rstd 的 Arc/Option/slice/Vec 换成 std。去掉的只有没人读的接口面：
// DescriptorUpdateResult 的状态枚举与 write_count（Commit 改回 bool）、
// array_element 参数（恒为 0）、pool() 访问器、租约的 clone()（租约可直接拷贝）。
//
// 池按"代"管理：建池不带 VK_DESCRIPTOR_POOL_CREATE_FREE_DESCRIPTOR_SET_BIT，
// 集合从不单独释放；每个租约持有所属代的 shared_ptr，
// 最后一个引用放掉时整池销毁。
export namespace owe::vk
{

// 建池/分配/写入/销毁四个入口。默认走 vulkan-1 导出的全局函数；单测注入假函数。
struct DescriptorDeviceDispatch {
    PFN_vkCreateDescriptorPool   create_pool { nullptr };
    PFN_vkDestroyDescriptorPool  destroy_pool { nullptr };
    PFN_vkAllocateDescriptorSets allocate_sets { nullptr };
    PFN_vkUpdateDescriptorSets   update_sets { nullptr };

    static DescriptorDeviceDispatch Vulkan() noexcept {
        return DescriptorDeviceDispatch {
            .create_pool   = vkCreateDescriptorPool,
            .destroy_pool  = vkDestroyDescriptorPool,
            .allocate_sets = vkAllocateDescriptorSets,
            .update_sets   = vkUpdateDescriptorSets,
        };
    }

    bool valid() const noexcept {
        return create_pool != nullptr && destroy_pool != nullptr && allocate_sets != nullptr &&
               update_sets != nullptr;
    }
};

class DescriptorArenaGeneration;

struct DescriptorArenaCreateResult {
    VkResult                                   api_result { VK_SUCCESS };
    std::shared_ptr<DescriptorArenaGeneration> arena;

    bool created() const noexcept { return api_result == VK_SUCCESS && arena != nullptr; }
};

// 租约是普通值类型：拷贝即多持有一份所属代。
struct DescriptorSetLease {
    VkDescriptorSet                            handle { VK_NULL_HANDLE };
    VkDescriptorSetLayout                      layout { VK_NULL_HANDLE };
    std::shared_ptr<DescriptorArenaGeneration> owner;

    bool valid() const noexcept {
        return handle != VK_NULL_HANDLE && layout != VK_NULL_HANDLE && owner != nullptr;
    }
};

struct DescriptorSetAllocationResult {
    VkResult           api_result { VK_SUCCESS };
    DescriptorSetLease lease;

    bool allocated() const noexcept { return api_result == VK_SUCCESS && lease.valid(); }
};

class DescriptorArenaGeneration final {
public:
    DescriptorArenaGeneration(VkDevice device, VkDescriptorPool pool,
                              DescriptorDeviceDispatch dispatch) noexcept
        : m_device(device), m_pool(pool), m_dispatch(dispatch) {}

    DescriptorArenaGeneration(const DescriptorArenaGeneration&)            = delete;
    DescriptorArenaGeneration& operator=(const DescriptorArenaGeneration&) = delete;

    ~DescriptorArenaGeneration() {
        if (m_pool != VK_NULL_HANDLE && m_dispatch.destroy_pool != nullptr) {
            m_dispatch.destroy_pool(m_device, m_pool, nullptr);
        }
    }

    static DescriptorArenaCreateResult
    Create(VkDevice device, std::uint32_t max_sets, std::span<const VkDescriptorPoolSize> sizes,
           DescriptorDeviceDispatch dispatch = DescriptorDeviceDispatch::Vulkan()) {
        if (device == VK_NULL_HANDLE || max_sets == 0 || sizes.empty() || ! dispatch.valid()) {
            return { .api_result = VK_ERROR_INITIALIZATION_FAILED };
        }

        VkDescriptorPoolCreateInfo create_info {
            .sType         = VK_STRUCTURE_TYPE_DESCRIPTOR_POOL_CREATE_INFO,
            .pNext         = nullptr,
            .flags         = 0,
            .maxSets       = max_sets,
            .poolSizeCount = static_cast<std::uint32_t>(sizes.size()),
            .pPoolSizes    = sizes.data(),
        };
        VkDescriptorPool pool   = VK_NULL_HANDLE;
        const auto       result = dispatch.create_pool(device, &create_info, nullptr, &pool);
        if (result != VK_SUCCESS || pool == VK_NULL_HANDLE) {
            return { .api_result = result == VK_SUCCESS ? VK_ERROR_INITIALIZATION_FAILED : result };
        }
        return DescriptorArenaCreateResult {
            .api_result = VK_SUCCESS,
            .arena      = std::make_shared<DescriptorArenaGeneration>(device, pool, dispatch),
        };
    }

    static DescriptorSetAllocationResult
    Allocate(const std::shared_ptr<DescriptorArenaGeneration>& arena,
             VkDescriptorSetLayout                             layout) {
        const auto* self = arena.get();
        if (layout == VK_NULL_HANDLE || self->m_pool == VK_NULL_HANDLE ||
            self->m_dispatch.allocate_sets == nullptr) {
            return { .api_result = VK_ERROR_INITIALIZATION_FAILED };
        }
        VkDescriptorSetAllocateInfo allocate_info {
            .sType              = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_ALLOCATE_INFO,
            .pNext              = nullptr,
            .descriptorPool     = self->m_pool,
            .descriptorSetCount = 1,
            .pSetLayouts        = &layout,
        };
        VkDescriptorSet set = VK_NULL_HANDLE;
        const auto result   = self->m_dispatch.allocate_sets(self->m_device, &allocate_info, &set);
        if (result != VK_SUCCESS || set == VK_NULL_HANDLE) {
            return { .api_result = result == VK_SUCCESS ? VK_ERROR_INITIALIZATION_FAILED : result };
        }
        return DescriptorSetAllocationResult {
            .api_result = VK_SUCCESS,
            .lease =
                DescriptorSetLease {
                    .handle = set,
                    .layout = layout,
                    .owner  = arena,
                },
        };
    }

    VkDevice                        device() const noexcept { return m_device; }
    const DescriptorDeviceDispatch& dispatch() const noexcept { return m_dispatch; }

private:
    VkDevice                 m_device { VK_NULL_HANDLE };
    VkDescriptorPool         m_pool { VK_NULL_HANDLE };
    DescriptorDeviceDispatch m_dispatch;
};

// 攒一批写入，Commit 时一次 vkUpdateDescriptorSets：
// 先全部图像写入、后全部缓冲写入，各按加入顺序。
// 空批次算提交成功；已提交的批次不再接受写入或再次提交；租约须来自同一设备。
class DescriptorUpdateBatch {
public:
    bool WriteImage(DescriptorSetLease lease, std::uint32_t binding,
                    VkDescriptorType                       descriptor_type,
                    std::span<const VkDescriptorImageInfo> image_infos) {
        if (m_committed || ! lease.valid() || image_infos.empty()) return false;
        m_image_writes.push_back(ImageWrite {
            .lease   = std::move(lease),
            .binding = binding,
            .type    = descriptor_type,
            .infos   = { image_infos.begin(), image_infos.end() },
        });
        return true;
    }

    bool WriteBuffer(DescriptorSetLease lease, std::uint32_t binding,
                     VkDescriptorType                        descriptor_type,
                     std::span<const VkDescriptorBufferInfo> buffer_infos) {
        if (m_committed || ! lease.valid() || buffer_infos.empty()) return false;
        m_buffer_writes.push_back(BufferWrite {
            .lease   = std::move(lease),
            .binding = binding,
            .type    = descriptor_type,
            .infos   = { buffer_infos.begin(), buffer_infos.end() },
        });
        return true;
    }

    bool Commit() {
        if (m_committed) return false;
        if (m_image_writes.empty() && m_buffer_writes.empty()) {
            m_committed = true;
            return true;
        }

        const auto* owner = FirstOwner();
        if (owner == nullptr || owner->device() == VK_NULL_HANDLE ||
            owner->dispatch().update_sets == nullptr || ! AllUseDevice(owner->device())) {
            return false;
        }

        std::vector<VkWriteDescriptorSet> writes;
        writes.reserve(m_image_writes.size() + m_buffer_writes.size());
        for (const auto& write : m_image_writes) {
            writes.push_back(VkWriteDescriptorSet {
                .sType            = VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET,
                .pNext            = nullptr,
                .dstSet           = write.lease.handle,
                .dstBinding       = write.binding,
                .dstArrayElement  = 0,
                .descriptorCount  = static_cast<std::uint32_t>(write.infos.size()),
                .descriptorType   = write.type,
                .pImageInfo       = write.infos.data(),
                .pBufferInfo      = nullptr,
                .pTexelBufferView = nullptr,
            });
        }
        for (const auto& write : m_buffer_writes) {
            writes.push_back(VkWriteDescriptorSet {
                .sType            = VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET,
                .pNext            = nullptr,
                .dstSet           = write.lease.handle,
                .dstBinding       = write.binding,
                .dstArrayElement  = 0,
                .descriptorCount  = static_cast<std::uint32_t>(write.infos.size()),
                .descriptorType   = write.type,
                .pImageInfo       = nullptr,
                .pBufferInfo      = write.infos.data(),
                .pTexelBufferView = nullptr,
            });
        }
        owner->dispatch().update_sets(
            owner->device(), static_cast<std::uint32_t>(writes.size()), writes.data(), 0, nullptr);
        m_committed = true;
        return true;
    }

private:
    struct ImageWrite {
        DescriptorSetLease                 lease;
        std::uint32_t                      binding {};
        VkDescriptorType                   type { VK_DESCRIPTOR_TYPE_MAX_ENUM };
        std::vector<VkDescriptorImageInfo> infos;
    };

    struct BufferWrite {
        DescriptorSetLease                  lease;
        std::uint32_t                       binding {};
        VkDescriptorType                    type { VK_DESCRIPTOR_TYPE_MAX_ENUM };
        std::vector<VkDescriptorBufferInfo> infos;
    };

    const DescriptorArenaGeneration* FirstOwner() const noexcept {
        if (! m_image_writes.empty()) return m_image_writes.front().lease.owner.get();
        if (! m_buffer_writes.empty()) return m_buffer_writes.front().lease.owner.get();
        return nullptr;
    }

    bool AllUseDevice(VkDevice device) const noexcept {
        for (const auto& write : m_image_writes) {
            if (! write.lease.valid() || write.lease.owner->device() != device) return false;
        }
        for (const auto& write : m_buffer_writes) {
            if (! write.lease.valid() || write.lease.owner->device() != device) return false;
        }
        return true;
    }

    std::vector<ImageWrite>  m_image_writes;
    std::vector<BufferWrite> m_buffer_writes;
    bool                     m_committed { false };
};

} // namespace owe::vk
