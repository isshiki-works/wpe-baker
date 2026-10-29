module;

#include <span>

export module wescene.resource_registry:system;
import rstd;
import rstd.cppstd;
import wescene.core;
import wescene.resource;
import wescene.vulkan;

import :owner;
import :prepared;

export namespace owe::resource_registry
{

using namespace rstd::prelude;

struct PipelinePreparation {
    resource::PipelineHandle   resource;
    resource::RenderPassHandle render_pass;
    vulkan::PipelineCacheKey   cache_key;
    vulkan::RenderPassCacheKey render_pass_key;
    bool                       cache_hit { false };
    u64                        cache_observed_count { 0 };
    bool                       render_pass_cache_hit { false };
    u64                        render_pass_cache_observed_count { 0 };
};

struct FramebufferPreparation {
    resource::FramebufferHandle resource;
    vulkan::FramebufferCacheKey cache_key;
    bool                        cache_hit { false };
    u64                         cache_observed_count { 0 };
};

struct RenderPassPreparation {
    resource::RenderPassHandle resource;
    vulkan::RenderPassCacheKey cache_key;
    bool                       cache_hit { false };
    u64                        cache_observed_count { 0 };
};

// 渲染 pass 准备管线/帧缓冲/渲染通道/描述符的接口（RenderResourceSystem 实现）。
struct GraphicsResourcePreparer {
    virtual ~GraphicsResourcePreparer() = default;

    virtual auto PreparePipeline(resource::PipelineUseHandle   pipeline_use,
                                 resource::RenderPassUseHandle render_pass_use,
                                 const vulkan::Device&         device,
                                 vulkan::PipelineResourceRequest request)
        -> Result<PipelinePreparation, resource::ResourceError> = 0;
    virtual auto PrepareFramebuffer(resource::FramebufferUseHandle                 framebuffer_use,
                                    resource::RenderPassUseHandle                  render_pass_use,
                                    const vulkan::Device&                          device,
                                    std::vector<vulkan::FramebufferAttachmentDesc> attachments,
                                    VkExtent2D                                     extent)
        -> Result<FramebufferPreparation, resource::ResourceError> = 0;
    virtual auto PrepareRenderPass(resource::RenderPassUseHandle use, const vulkan::Device& device,
                                   const vulkan::RenderPassResourceDesc& desc)
        -> Result<RenderPassPreparation, resource::ResourceError> = 0;
    virtual auto PrepareDescriptor(const vulkan::Device&          device,
                                   resource::PipelineLayoutHandle pipeline_layout, u32 set_index,
                                   slice<DescriptorImageBinding>  images,
                                   slice<DescriptorBufferBinding> buffers,
                                   DescriptorBindingReuse         reuse)
        -> Result<resource::DescriptorBindingHandle, resource::ResourceError> = 0;
    virtual auto UpdateDescriptorImages(resource::DescriptorBindingHandle handle,
                                        slice<DescriptorImageBinding>     images)
        -> Result<empty, resource::ResourceError> = 0;
};

// 把外部帧表面接入资源表（FinPass 用）。
struct ExternalResourcePreparer {
    virtual ~ExternalResourcePreparer() = default;

    virtual auto PrepareExternal(resource::ExternalUseHandle       external_use,
                                 resource::TextureUseHandle        source_use,
                                 const vulkan::DeviceCapabilities& capabilities,
                                 FrameSurfaceLease lease, rstd::uint32_t graphics_queue_family)
        -> Result<empty, resource::ResourceError> = 0;
};

class RenderResourceSystem final : public GraphicsResourcePreparer,
                                   public resource::BufferContentWriter,
                                   public ExternalResourcePreparer {
public:
    bool Initialize(const vulkan::Device& device) { return m_registries.Initialize(device); }

    void Reset() {
        m_prepare_rollback = None();
        m_prepared         = PreparedResourceTable {};
        m_registries.Reset();
    }

    bool Ready() const noexcept { return m_registries.Ready(); }

    auto
    PreparePlan(const resource::ResourcePlan& plan, ResourceContentProviders providers,
                resource::ResourcePlanSections sections = resource::ResourcePlanAll)
        -> Result<empty, resource::ResourceError> {
        auto started = BeginPreparePlan(plan, rstd::move(providers), sections);
        if (started.is_err()) return Err(rstd::move(started).unwrap_err_unchecked());
        auto session = rstd::move(started).unwrap_unchecked();
        while (true) {
            auto progress = ContinuePreparePlan(session);
            if (progress.is_err()) return Err(rstd::move(progress).unwrap_err_unchecked());
            if (progress.unwrap_unchecked() == ResourcePrepareProgress::Complete) break;
        }
        CommitPreparePlan();
        return Ok(empty {});
    }

    auto BeginPreparePlan(const resource::ResourcePlan& plan, ResourceContentProviders providers,
                          resource::ResourcePlanSections sections = resource::ResourcePlanAll)
        -> Result<ResourcePrepareSession, resource::ResourceError> {
        if (m_prepare_rollback.is_some()) {
            return Err(resource::ResourceError {
                .kind    = resource::ResourceErrorKind::BackendFailure,
                .message = rstd::format("resource prepare transaction is already active"),
            });
        }
        if (! m_registries.TextureEntries().BeginPrepareTransaction()) {
            return Err(resource::ResourceError {
                .kind    = resource::ResourceErrorKind::BackendFailure,
                .message = rstd::format("texture prepare transaction is already active"),
            });
        }
        vulkan::ImagePrepareContext image_context(m_registries.Textures(),
                                                  m_registries.ImageUploads());
        ResourcePrepareService service(m_registries.TextureEntries(),
                                       &image_context,
                                       m_registries.Buffers(),
                                       &m_registries.BufferManager(),
                                       m_registries.Shaders());
        const PreparedResourceTable* previous_table =
            sections == resource::ResourcePlanTextures ? &m_prepared : nullptr;
        auto started = service.Begin(plan, rstd::move(providers), sections, previous_table);
        if (started.is_err()) {
            AbortPreparePlan();
            return Err(rstd::move(started).unwrap_err_unchecked());
        }
        return Ok(rstd::move(started).unwrap_unchecked());
    }

    auto ContinuePreparePlan(ResourcePrepareSession& session)
        -> Result<ResourcePrepareProgress, resource::ResourceError> {
        vulkan::ImagePrepareContext image_context(m_registries.Textures(),
                                                  m_registries.ImageUploads());
        ResourcePrepareService service(m_registries.TextureEntries(),
                                       &image_context,
                                       m_registries.Buffers(),
                                       &m_registries.BufferManager(),
                                       m_registries.Shaders());
        auto                   progress = service.Continue(session);
        if (progress.is_err()) {
            AbortPreparePlan();
            return Err(rstd::move(progress).unwrap_err_unchecked());
        }
        if (progress.unwrap_unchecked() == ResourcePrepareProgress::BatchReady) {
            return Ok(ResourcePrepareProgress::BatchReady);
        }

        const auto& plan     = session.Plan();
        const auto  sections = session.Sections();
        auto        next     = rstd::move(session).TakeTable();
        if (resource::ResourcePlanIncludes(sections, resource::ResourcePlanTextures) &&
            ! m_registries.States().Compile(plan, next)) {
            AbortPreparePlan();
            return Err(resource::ResourceError {
                .kind    = resource::ResourceErrorKind::BackendFailure,
                .message = rstd::format("compile resource state plan failed"),
            });
        }
        auto rollback = m_prepared.ReplaceSectionsForRollback(rstd::move(next), sections);
        m_prepare_rollback = Some(PrepareRollback {
            .sections = sections,
            .table    = rstd::move(rollback),
        });
        return Ok(ResourcePrepareProgress::Complete);
    }

    void AbortPreparePlan() {
        m_registries.ImageUploads().DiscardPendingUploads();
        m_registries.TextureEntries().DiscardPendingUploads();
        m_registries.TextureEntries().AbortPrepareTransaction();
        auto rollback = m_prepare_rollback.take();
        if (rollback.is_none()) return;
        for (usize i {}; i < rollback->inserted_pipelines.len(); ++i) {
            (void)m_prepared.Remove(rollback->inserted_pipelines[i]);
        }
        for (usize i {}; i < rollback->inserted_render_passes.len(); ++i) {
            (void)m_prepared.Remove(rollback->inserted_render_passes[i]);
        }
        for (usize i {}; i < rollback->inserted_framebuffers.len(); ++i) {
            (void)m_prepared.Remove(rollback->inserted_framebuffers[i]);
        }
        for (usize i {}; i < rollback->inserted_descriptors.len(); ++i) {
            (void)m_prepared.Remove(rollback->inserted_descriptors[i]);
        }
        for (usize i {}; i < rollback->inserted_externals.len(); ++i) {
            (void)m_prepared.Remove(rollback->inserted_externals[i]);
        }
        for (usize i = rollback->updated_descriptors.len(); i > usize(); --i) {
            auto& item = rollback->updated_descriptors[i - usize(1)];
            (void)m_prepared.Remove(item.handle);
            (void)m_prepared.Insert(rstd::move(item));
        }
        for (usize i = rollback->removed_pipelines.len(); i > usize(); --i) {
            (void)m_prepared.Insert(rstd::move(rollback->removed_pipelines[i - usize(1)]));
        }
        for (usize i = rollback->removed_render_passes.len(); i > usize(); --i) {
            (void)m_prepared.Insert(rstd::move(rollback->removed_render_passes[i - usize(1)]));
        }
        for (usize i = rollback->removed_framebuffers.len(); i > usize(); --i) {
            (void)m_prepared.Insert(rstd::move(rollback->removed_framebuffers[i - usize(1)]));
        }
        for (usize i = rollback->removed_descriptors.len(); i > usize(); --i) {
            (void)m_prepared.Insert(rstd::move(rollback->removed_descriptors[i - usize(1)]));
        }
        for (usize i = rollback->removed_externals.len(); i > usize(); --i) {
            m_prepared.Insert(rstd::move(rollback->removed_externals[i - usize(1)]));
        }
        m_prepared.RestoreSectionsFromRollback(rstd::move(rollback->table), rollback->sections);
    }

    void CommitPreparePlan() {
        m_prepare_rollback = None();
        m_registries.TextureEntries().CommitPrepareTransaction();
        auto active = m_prepared.TextureResources();
        m_registries.TextureEntries().RetainActive(active.as_slice());
    }

    auto Prepared() const -> const PreparedResourceTable& { return m_prepared; }
    auto States() -> ResourceStateTracker& { return m_registries.States(); }

    void RemovePreparedGraphics(slice<resource::PipelineUseHandle>       pipelines,
                                slice<resource::RenderPassUseHandle>     render_passes,
                                slice<resource::FramebufferUseHandle>    framebuffers,
                                slice<resource::DescriptorBindingHandle> descriptors,
                                slice<resource::ExternalUseHandle>       externals) {
        for (usize index {}; index < pipelines.len(); ++index) {
            auto removed = m_prepared.Remove(pipelines[index]);
            if (removed.is_some() && m_prepare_rollback.is_some()) {
                m_prepare_rollback->removed_pipelines.push(rstd::move(*removed));
            }
        }
        for (usize index {}; index < render_passes.len(); ++index) {
            auto removed = m_prepared.Remove(render_passes[index]);
            if (removed.is_some() && m_prepare_rollback.is_some()) {
                m_prepare_rollback->removed_render_passes.push(rstd::move(*removed));
            }
        }
        for (usize index {}; index < framebuffers.len(); ++index) {
            auto removed = m_prepared.Remove(framebuffers[index]);
            if (removed.is_some() && m_prepare_rollback.is_some()) {
                m_prepare_rollback->removed_framebuffers.push(rstd::move(*removed));
            }
        }
        for (usize index {}; index < descriptors.len(); ++index) {
            auto removed = m_prepared.Remove(descriptors[index]);
            if (removed.is_some() && m_prepare_rollback.is_some()) {
                m_prepare_rollback->removed_descriptors.push(rstd::move(*removed));
            }
        }
        for (usize index {}; index < externals.len(); ++index) {
            auto removed = m_prepared.Remove(externals[index]);
            if (removed.is_some() && m_prepare_rollback.is_some()) {
                m_prepare_rollback->removed_externals.push(rstd::move(*removed));
            }
        }
    }

    void RemovePreparedDescriptor(resource::DescriptorBindingHandle descriptor) {
        auto removed = m_prepared.Remove(descriptor);
        if (removed.is_some() && m_prepare_rollback.is_some()) {
            m_prepare_rollback->removed_descriptors.push(rstd::move(*removed));
        }
    }

    auto PreparePipelineLayout(const vulkan::Device& device, const PipelineLayoutRequest& request)
        -> Result<resource::PipelineLayoutHandle, resource::ResourceError> {
        auto prepared = m_registries.PipelineLayouts().Ensure(
            device, request, m_registries.DescriptorLayouts());
        if (prepared.is_err()) return Err(rstd::move(prepared).unwrap_err_unchecked());
        return Ok(rstd::move(prepared).unwrap_unchecked().handle);
    }

    auto PreparePipeline(resource::PipelineUseHandle   pipeline_use,
                         resource::RenderPassUseHandle render_pass_use,
                         const vulkan::Device& device, vulkan::PipelineResourceRequest request)
        -> Result<PipelinePreparation, resource::ResourceError> override {
        PipelineResourceSystem system(device,
                                      m_registries.PipelineLayouts(),
                                      m_registries.PipelineCache(),
                                      m_registries.RenderPassCache());
        auto                   result = system.CreateGraphicsPipeline(std::move(request));
        if (result.is_none()) {
            return Err(resource::ResourceError {
                .kind    = resource::ResourceErrorKind::BackendFailure,
                .message = rstd::format("prepare graphics pipeline failed"),
            });
        }
        if (! m_prepared.Insert(PreparedRenderPass {
                .use       = render_pass_use,
                .resource  = result->render_pass,
                .cache_key = result->render_pass_key,
                .physical  = result->render_pass_physical.clone(),
            })) {
            return Err(resource::ResourceError {
                .kind    = resource::ResourceErrorKind::BackendFailure,
                .message = rstd::format("duplicate prepared graphics use"),
            });
        }
        if (m_prepare_rollback.is_some()) {
            m_prepare_rollback->inserted_render_passes.emplace_back(render_pass_use);
        }
        if (! m_prepared.Insert(PreparedPipeline {
                .use         = pipeline_use,
                .resource    = result->handle,
                .render_pass = result->render_pass,
                .physical    = result->pipeline.clone(),
            })) {
            return Err(resource::ResourceError {
                .kind    = resource::ResourceErrorKind::BackendFailure,
                .message = rstd::format("duplicate prepared graphics use"),
            });
        }
        if (m_prepare_rollback.is_some()) {
            m_prepare_rollback->inserted_pipelines.emplace_back(pipeline_use);
        }
        return Ok(PipelinePreparation {
            .resource                         = result->handle,
            .render_pass                      = result->render_pass,
            .cache_key                        = rstd::move(result->cache_key),
            .render_pass_key                  = rstd::move(result->render_pass_key),
            .cache_hit                        = result->cache_hit,
            .cache_observed_count             = result->cache_observed_count,
            .render_pass_cache_hit            = result->render_pass_cache_hit,
            .render_pass_cache_observed_count = result->render_pass_cache_observed_count,
        });
    }

    auto PrepareFramebuffer(resource::FramebufferUseHandle                 framebuffer_use,
                            resource::RenderPassUseHandle                  render_pass_use,
                            const vulkan::Device&                          device,
                            std::vector<vulkan::FramebufferAttachmentDesc> attachments,
                            VkExtent2D                                     extent)
        -> Result<FramebufferPreparation, resource::ResourceError> override {
        auto render_pass = m_prepared.Resolve(render_pass_use);
        if (render_pass.is_none()) {
            return Err(resource::ResourceError {
                .kind    = resource::ResourceErrorKind::MissingDefinition,
                .message = rstd::format("prepared render pass unavailable"),
            });
        }
        FramebufferResourceSystem system(
            device, m_registries.FramebufferCache(), m_registries.FramebufferDiagnostics());
        auto result = system.CreateFramebuffer(vulkan::FramebufferResourceRequest {
            .render_pass     = **(**render_pass).physical,
            .render_pass_key = (**render_pass).cache_key,
            .attachments     = rstd::move(attachments),
            .extent          = extent,
        });
        if (result.is_none()) {
            return Err(resource::ResourceError {
                .kind    = resource::ResourceErrorKind::BackendFailure,
                .message = rstd::format("prepare framebuffer failed"),
            });
        }
        if (! m_prepared.Insert(PreparedFramebuffer {
                .use      = framebuffer_use,
                .resource = result->handle,
                .physical = result->framebuffer.clone(),
            })) {
            return Err(resource::ResourceError {
                .kind    = resource::ResourceErrorKind::BackendFailure,
                .message = rstd::format("duplicate prepared framebuffer use"),
            });
        }
        if (m_prepare_rollback.is_some()) {
            m_prepare_rollback->inserted_framebuffers.emplace_back(framebuffer_use);
        }
        return Ok(FramebufferPreparation {
            .resource             = result->handle,
            .cache_key            = rstd::move(result->cache_key),
            .cache_hit            = result->cache_hit,
            .cache_observed_count = result->cache_observed_count,
        });
    }

    auto PrepareRenderPass(resource::RenderPassUseHandle use, const vulkan::Device& device,
                           const vulkan::RenderPassResourceDesc& desc)
        -> Result<RenderPassPreparation, resource::ResourceError> override {
        auto result = m_registries.RenderPasses().Ensure(device, desc);
        if (result.is_none()) {
            return Err(resource::ResourceError {
                .kind    = resource::ResourceErrorKind::BackendFailure,
                .message = rstd::format("prepare render pass failed"),
            });
        }
        if (! m_prepared.Insert(PreparedRenderPass {
                .use       = use,
                .resource  = result->handle,
                .cache_key = result->cache_key,
                .physical  = result->render_pass.clone(),
            })) {
            return Err(resource::ResourceError {
                .kind    = resource::ResourceErrorKind::BackendFailure,
                .message = rstd::format("duplicate prepared render pass use"),
            });
        }
        if (m_prepare_rollback.is_some()) {
            m_prepare_rollback->inserted_render_passes.emplace_back(use);
        }
        return Ok(RenderPassPreparation {
            .resource             = result->handle,
            .cache_key            = rstd::move(result->cache_key),
            .cache_hit            = result->cache_hit,
            .cache_observed_count = result->cache_observed_count,
        });
    }

    auto PrepareDescriptor(const vulkan::Device&          device,
                           resource::PipelineLayoutHandle pipeline_layout, u32 set_index,
                           slice<DescriptorImageBinding>  images,
                           slice<DescriptorBufferBinding> buffers, DescriptorBindingReuse reuse)
        -> Result<resource::DescriptorBindingHandle, resource::ResourceError> override {
        auto layout_resource = m_registries.PipelineLayouts().Resolve(pipeline_layout);
        if (layout_resource.is_none() ||
            rstd::as_cast<usize>(set_index) >= (**layout_resource).descriptor_layouts.len()) {
            return Err(resource::ResourceError {
                .kind    = resource::ResourceErrorKind::MissingDefinition,
                .message = rstd::format("pipeline descriptor layout unavailable"),
            });
        }
        auto layout = m_registries.DescriptorLayouts().Resolve(
            (**layout_resource).descriptor_layouts[rstd::as_cast<usize>(set_index)]);
        if (layout.is_none()) {
            return Err(resource::ResourceError {
                .kind    = resource::ResourceErrorKind::MissingDefinition,
                .message = rstd::format("descriptor layout unavailable"),
            });
        }
        auto prepared = m_registries.Descriptors().Prepare(
            device, set_index.to_primitive(), **layout, images, buffers, reuse);
        if (prepared.is_err()) return Err(rstd::move(prepared).unwrap_err_unchecked());
        auto binding = rstd::move(prepared).unwrap_unchecked();
        auto handle  = binding.handle;
        if (! m_prepared.Insert(rstd::move(binding))) {
            return Err(resource::ResourceError {
                .kind    = resource::ResourceErrorKind::BackendFailure,
                .message = rstd::format("duplicate descriptor binding {}", handle.index),
            });
        }
        if (m_prepare_rollback.is_some()) {
            m_prepare_rollback->inserted_descriptors.emplace_back(handle);
        }
        return Ok(handle);
    }

    auto UpdateDescriptorImages(resource::DescriptorBindingHandle handle,
                                slice<DescriptorImageBinding>     images)
        -> Result<empty, resource::ResourceError> override {
        auto prepared = m_prepared.ResolveMut(handle);
        if (prepared.is_none()) {
            return Err(resource::ResourceError {
                .kind = resource::ResourceErrorKind::MissingDefinition,
                .message =
                    rstd::format("prepared descriptor binding {} is unavailable", handle.index),
            });
        }
        if (m_prepare_rollback.is_some()) {
            m_prepare_rollback->updated_descriptors.push((**prepared).clone());
        }
        return (**prepared).UpdateImages(images);
    }

    bool HasPendingUploads() const {
        return m_registries.BufferManager().HasPendingUploads() ||
               m_registries.ImageUploads().HasPendingUploads();
    }

    bool RecordPendingUploads(vvk::CommandBuffer& command, vulkan::RecordedBufferUploads& buffers,
                              vulkan::RecordedImageUploads& images) {
        return m_registries.BufferManager().RecordPendingUploads(command, buffers) &&
               m_registries.ImageUploads().RecordPendingUploads(command, images);
    }

    bool RecordPendingUploads(vvk::CommandBuffer& command, vulkan::RecordedBufferUploads& buffers) {
        return m_registries.BufferManager().RecordPendingUploads(command, buffers);
    }

    auto UpdateBuffer(resource::BufferUseHandle use, slice<u8> content)
        -> Result<empty, resource::ResourceError> override {
        auto prepared = m_prepared.Resolve(use);
        if (prepared.is_none()) {
            return Err(resource::ResourceError {
                .kind    = resource::ResourceErrorKind::MissingDefinition,
                .message = rstd::format("prepared buffer use {} is unavailable", use.index),
            });
        }
        return m_registries.Buffers().Update(
            (**prepared).buffer.resource, content, &m_registries.BufferManager());
    }

    auto ReserveUpload() -> resource::ReadyToken { return m_registries.Uploads().Reserve(); }
    bool MarkUploadSubmitted(resource::ReadyToken token, vulkan::RecordedBufferUploads buffers,
                             vulkan::RecordedImageUploads images) {
        Option<vulkan::BufferUploadBatchLease> buffer_lease = None();
        Option<vulkan::ImageUploadBatchLease>  image_lease  = None();
        if (buffers.Valid()) {
            auto committed =
                m_registries.BufferManager().CommitRecordedUploads(rstd::move(buffers));
            if (committed.is_none()) return false;
            buffer_lease = Some(rstd::move(*committed));
        }
        if (images.Valid()) {
            auto committed = m_registries.ImageUploads().CommitRecordedUploads(rstd::move(images));
            if (committed.is_none()) return false;
            image_lease = Some(rstd::move(*committed));
        }
        if (buffer_lease.is_none() && image_lease.is_none()) return false;
        auto buffer_tickets = Vec<vulkan::BufferUploadTicket>::make();
        if (buffer_lease.is_some()) {
            auto tickets = buffer_lease->Tickets();
            buffer_tickets.extend_from_slice(tickets.data(), usize(tickets.size()));
        }
        auto image_tickets = Vec<vulkan::ImageUploadTicket>::make();
        if (image_lease.is_some()) {
            auto tickets = image_lease->Tickets();
            image_tickets.extend_from_slice(tickets.data(), usize(tickets.size()));
        }
        if (! m_registries.Uploads().MarkSubmitted(
                token, rstd::move(buffer_lease), rstd::move(image_lease))) {
            return false;
        }
        m_registries.Buffers().MarkUploadsSubmitted(
            std::span<const vulkan::BufferUploadTicket>(buffer_tickets.data(),
                                                        buffer_tickets.len().to_primitive()),
            Some(token));
        m_registries.TextureEntries().MarkUploadsSubmitted(
            std::span<const vulkan::ImageUploadTicket>(image_tickets.data(),
                                                       image_tickets.len().to_primitive()),
            Some(token));
        return true;
    }
    auto PendingUpload() -> Option<resource::ReadyToken> {
        return m_registries.Uploads().Pending();
    }
    void CompleteUploadsThrough(u64 value) { m_registries.Uploads().CompleteThrough(value); }

    auto BeginSubmission(vulkan::RecordedBufferUploads recorded               = {},
                         bool                          retain_prepared_leases = true)
        -> resource::CompletionToken {
        Option<vulkan::BufferUploadBatchLease> upload_lease = None();
        if (recorded.Valid()) {
            auto lease = m_registries.BufferManager().CommitRecordedUploads(rstd::move(recorded));
            if (lease.is_none()) return {};
            m_registries.Buffers().MarkUploadsSubmitted(lease->Tickets(), None());
            upload_lease = Some(rstd::move(*lease));
        }
        return m_registries.Submissions().Begin(
            m_prepared, rstd::move(upload_lease), retain_prepared_leases);
    }
    auto CompleteSubmission(resource::CompletionToken token) -> Option<SubmissionLease> {
        return m_registries.Submissions().Complete(token);
    }

    auto PrepareExternal(resource::ExternalUseHandle       external_use,
                         resource::TextureUseHandle        source_use,
                         const vulkan::DeviceCapabilities& capabilities, FrameSurfaceLease lease,
                         rstd::uint32_t graphics_queue_family)
        -> Result<empty, resource::ResourceError> override {
        auto source = m_prepared.Resolve(source_use);
        if (source.is_none()) {
            return Err(resource::ResourceError {
                .kind    = resource::ResourceErrorKind::MissingDefinition,
                .message = rstd::format("prepared external source unavailable"),
            });
        }
        auto prepared =
            m_registries.External().Prepare(capabilities, rstd::move(lease), graphics_queue_family);
        if (prepared.is_err()) return Err(rstd::move(prepared).unwrap_err_unchecked());
        if (m_prepare_rollback.is_some()) {
            auto existing = m_prepared.Remove(external_use);
            if (existing.is_some()) {
                m_prepare_rollback->removed_externals.push(rstd::move(*existing));
            } else {
                m_prepare_rollback->inserted_externals.emplace_back(external_use);
            }
        }
        m_prepared.Insert(PreparedExternalUse {
            .use   = external_use,
            .frame = rstd::move(prepared).unwrap_unchecked(),
        });
        return Ok(empty {});
    }

    void Collect(vulkan::MemoryBudgetSource* memory) {
        m_registries.PipelineCache().PruneExpired();
        m_registries.PipelineLayouts().PruneExpired();
        m_registries.RenderPassCache().PruneExpired();
        m_registries.FramebufferCache().PruneExpired();
        m_registries.Memory().Refresh(memory);
        if (! m_registries.Memory().ShouldEvictTransient()) return;
        m_registries.TextureEntries().EvictUnused();
        m_registries.Buffers().EvictUnused();
        m_registries.BufferManager().Trim();
        m_registries.ImageUploads().Trim();
    }

    void PumpVideoTextures(double seconds, Services* services, bool raster) {
        m_registries.Textures().PumpVideoTextures(seconds, services, raster);
    }
    void PrepareVideoTextures(Services* services, bool raster) {
        m_registries.Textures().PrepareVideoTextures(services, raster);
    }
    vulkan::VideoDecoderInventory ObserveVideoDecoders() { return m_registries.Textures().ObserveVideoDecoders(); }
    void SetVideoDecodeOptions(vulkan::TextureCache::VideoDecodeOptions options) {
        m_registries.Textures().SetVideoDecodeOptions(std::move(options));
    }
    bool UploadFontAtlasRegion(const std::string& key, const std::uint8_t* atlas,
                               std::uint32_t atlas_width, std::uint32_t x, std::uint32_t y,
                               std::uint32_t width, std::uint32_t height) {
        auto handle = m_registries.TextureEntries().Find(resource::TextureRequestKind::Imported,
                                                         rstd::cppstd::as_str(key).unwrap());
        if (handle.is_none()) return false;
        auto physical = m_registries.TextureEntries().ResolveCurrent(*handle);
        if (physical.is_none()) return false;
        return m_registries.Textures().UploadFontAtlasRegion(
            (**physical).allocation.deref(), atlas, atlas_width, x, y, width, height);
    }
    void ClearTextures() {
        m_prepare_rollback = None();
        m_prepared         = PreparedResourceTable {};
        m_registries.ImageUploads().DiscardPendingUploads();
        m_registries.TextureEntries().Reset();
        m_registries.Textures().Clear();
    }
    void ClearPreparedGraphics() { m_prepared.ClearPreparedState(); }
    void ClearTransientTextures() { m_registries.TextureEntries().ClearGraphResources(); }
    void EvictUnusedBuffers() { m_registries.Buffers().EvictUnused(); }

    auto PipelineDiagnostics() -> PipelineCacheDiagnostics& {
        return m_registries.PipelineDiagnostics();
    }
    auto FramebufferDiagnostics() -> FramebufferCacheDiagnostics& {
        return m_registries.FramebufferDiagnostics();
    }

private:
    struct PrepareRollback {
        resource::ResourcePlanSections             sections {};
        PreparedResourceTable                      table;
        Vec<PreparedPipeline>                      removed_pipelines;
        Vec<PreparedRenderPass>                    removed_render_passes;
        Vec<PreparedFramebuffer>                   removed_framebuffers;
        Vec<PreparedDescriptorBinding>             removed_descriptors;
        Vec<PreparedExternalUse>                   removed_externals;
        Vec<PreparedDescriptorBinding>             updated_descriptors;
        Vec<resource::PipelineUseHandle>           inserted_pipelines;
        Vec<resource::RenderPassUseHandle>         inserted_render_passes;
        Vec<resource::FramebufferUseHandle>        inserted_framebuffers;
        Vec<resource::DescriptorBindingHandle>     inserted_descriptors;
        Vec<resource::ExternalUseHandle>           inserted_externals;
    };

    ResourceRegistries      m_registries;
    PreparedResourceTable   m_prepared;
    Option<PrepareRollback> m_prepare_rollback;
};

} // namespace owe::resource_registry
