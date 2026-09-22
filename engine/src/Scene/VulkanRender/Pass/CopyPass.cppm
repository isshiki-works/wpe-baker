module;

export module wescene.vulkan_render:copy_pass;
import rstd.cppstd;
import wescene.vulkan;
import wescene.scene;

import :vulkan_pass;
import :resource;

export namespace owe::vulkan
{

class CopyPass : public VulkanPass {
public:
    struct Desc {
        std::string                              src;
        std::string                              dst;
        rstd::Option<resource::TextureUseHandle> src_use;
        rstd::Option<resource::TextureUseHandle> dst_use;
        rstd::Option<TextureRequest>             src_request;
        rstd::Option<TextureRequest>             dst_request;
        bool                                     dst_matches_src { false };

        resource_registry::PreparedBarrierBatch before_barriers;
        resource_registry::PreparedBarrierBatch after_barriers;
    };

    CopyPass(Desc&&);
    virtual ~CopyPass();

    PassInvalidationFlags finalizeResourceRequests(Scene&) override;
    PassResourceUses      resourceUses() const override;
    bool                  prepareResourceStates(
        rstd::mut_ref<rstd::dyn<resource_registry::TextureStatePreparer>>) override;
    std::vector<PassTextureRequestDiagnostic> textureRequestDiagnostics() const override;

    void prepare(Scene&, const Device&, PassPrepareContext&) override;
    void record(PassRecordContext&) override;
    void destory(const Device&) override;

private:
    Desc m_desc;
};

} // namespace owe::vulkan
