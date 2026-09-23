module;
#include <owe/compat.hpp>
#include <owe/std.hpp>

export module wescene.vulkan_render:pre_pass;
import wescene.spec_names;
import wescene.vulkan;
import wescene.scene;

import :vulkan_pass;
import :resource;

export namespace owe::vulkan
{

class PrePass : public VulkanPass {
public:
    struct Desc {
        // in
        std::string_view result { rstd::cppstd::as_string_view(SpecTex_Default) };
        bool transparent_background { false };
        rstd::Option<resource::TextureUseHandle> result_use;
        rstd::Option<resource::TextureUseHandle> result_msaa_use;
        rstd::Option<TextureRequest>             result_request;
        rstd::Option<TextureRequest>             result_msaa_request;
        resource_registry::PreparedBarrierBatch  before_clear;
        resource_registry::PreparedBarrierBatch  after_clear;

        // prepared
        rstd::Option<resource::RenderPassUseHandle>  render_pass_use;
        rstd::Option<resource::FramebufferUseHandle> framebuffer_use;
        VkSampleCountFlagBits                        samples { VK_SAMPLE_COUNT_1_BIT };
        VkClearValue                                 clear_value;
    };

    PrePass(Desc&&);
    virtual ~PrePass();

    bool             setResultRequest(rstd::Option<TextureRequest>,
                                      rstd::Option<TextureRequest> msaa_request = rstd::None());
    void             setTransparentBackground(bool enabled);
    void             resetResourceUses();
    void             declareResources(ResourceDeclarationContext&) override;
    PassResourceUses resourceUses() const override;
    bool             prepareResourceStates(
        resource_registry::TextureStatePreparer*) override;
    std::vector<PassTextureRequestDiagnostic> textureRequestDiagnostics() const override;

    void prepare(Scene&, const Device&, PassPrepareContext&) override;
    void record(PassRecordContext&) override;
    void destory(const Device&) override;

private:
    Desc m_desc;
};

} // namespace owe::vulkan
