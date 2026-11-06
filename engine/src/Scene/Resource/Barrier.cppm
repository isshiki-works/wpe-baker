export module wescene.resource_registry:barrier;
import rstd;
import wescene.vulkan;

export namespace owe::resource_registry
{

using namespace rstd::prelude;

enum class TextureStateKind
{
    Undefined,
    Sampled,
    DepthSampled,
    TransferSource,
    TransferDestination,
    ColorAttachment,
    DepthAttachment,
};

struct TextureSubresourceRange {
    VkImageAspectFlags aspect { VK_IMAGE_ASPECT_COLOR_BIT };
    u32                base_mip_level { 0 };
    u32                level_count { VK_REMAINING_MIP_LEVELS };
    u32                base_array_layer { 0 };
    u32                layer_count { VK_REMAINING_ARRAY_LAYERS };
};

struct PreparedImageBarrier {
    VkPipelineStageFlags src_stage { VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT };
    VkPipelineStageFlags dst_stage { VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT };
    VkDependencyFlags    dependency { VK_DEPENDENCY_BY_REGION_BIT };
    VkImageMemoryBarrier barrier {
        .sType = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER,
    };

    void Record(vvk::CommandBuffer& command) const {
        command.PipelineBarrier(src_stage, dst_stage, dependency, barrier);
    }
};

class PreparedBarrierBatch {
public:
    void Add(PreparedImageBarrier barrier) { m_barriers.push(rstd::move(barrier)); }

    void Clear() { m_barriers.clear(); }

    void Record(vvk::CommandBuffer& command) const {
        if (m_barriers.is_empty()) return;
        if (m_barriers.len() == usize(1)) {
            m_barriers[usize()].Record(command);
            return;
        }
        constexpr usize                       kStackCap { 16 };
        rstd::array<VkImageMemoryBarrier, 16> stack_barriers {};
        rstd::vec::Vec<VkImageMemoryBarrier>         heap_barriers;
        VkImageMemoryBarrier*                        out = stack_barriers.data();
        if (m_barriers.len() > kStackCap) {
            heap_barriers.resize(m_barriers.len(), VkImageMemoryBarrier {});
            out = heap_barriers.data();
        }
        VkPipelineStageFlags src_stage  = m_barriers[usize()].src_stage;
        VkPipelineStageFlags dst_stage  = m_barriers[usize()].dst_stage;
        VkDependencyFlags    dependency = m_barriers[usize()].dependency;
        usize                batch_len {};
        for (usize i {}; i < m_barriers.len(); ++i) {
            const auto& entry = m_barriers[i];
            if (batch_len > usize() && entry.dependency != dependency) {
                command.PipelineBarrier(
                    src_stage,
                    dst_stage,
                    dependency,
                    {},
                    {},
                    slice<VkImageMemoryBarrier>::from_raw_parts(out, batch_len));
                src_stage  = entry.src_stage;
                dst_stage  = entry.dst_stage;
                dependency = entry.dependency;
                batch_len  = usize();
            } else {
                src_stage |= entry.src_stage;
                dst_stage |= entry.dst_stage;
            }
            out[batch_len.to_primitive()] = entry.barrier;
            ++batch_len;
        }
        if (batch_len > usize()) {
            command.PipelineBarrier(
                src_stage,
                dst_stage,
                dependency,
                {},
                {},
                slice<VkImageMemoryBarrier>::from_raw_parts(out, batch_len));
        }
    }

    auto Size() const noexcept -> usize { return m_barriers.len(); }
    bool Empty() const noexcept { return m_barriers.is_empty(); }

    auto clone() const -> PreparedBarrierBatch {
        PreparedBarrierBatch cloned;
        for (const auto& barrier : m_barriers) cloned.Add(barrier);
        return cloned;
    }

private:
    rstd::vec::Vec<PreparedImageBarrier> m_barriers;
};

} // namespace owe::resource_registry
