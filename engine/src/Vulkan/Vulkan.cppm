module;

#ifdef _WIN32
#include <io.h>
#else
#include <unistd.h>
#endif
#include <cerrno>

// Macros only — VVK_CHECK family.
#include "vvk/macros.hpp"

export module wescene.vulkan;
import wescene.core;
import rstd;
import rstd.log;
import rstd.cppstd;
import wescene.types;

// Vulkan FFI: vvk::ffi::vulkan exposes the full Vk symbol surface as
// a comprehensive FFI module. Re-exported so downstream consumers
// (wescene.vulkan_render etc.) that `import wescene.vulkan;` still see
// every Vk type / enumerator / PFN_* without needing their own
// `import vulkan;`.
export import vvk;

// Re-export the host-only shader compile API. Lets existing consumers
// (VulkanRender/* etc.) keep their `import wescene.vulkan;` without
// caring that ShaderSpv / ShaderReflected / Preprocess / etc. now live
// in a separate module.
export import wescene.shader_compile;

using namespace rstd::prelude;

export namespace owe
{

inline void CloseExternalFileDescriptor(int fd) {
    if (fd < 0) return;
#ifdef _WIN32
    ::_close(fd);
#else
    ::close(fd);
#endif
}

// ---------- ExSwapchain (formerly Swapchain/ExSwapchain.hpp) ----------

namespace vulkan
{

inline VkCompareOp ToVkType(CompareOp op) {
    switch (op) {
    case CompareOp::Never: return VK_COMPARE_OP_NEVER;
    case CompareOp::Less: return VK_COMPARE_OP_LESS;
    case CompareOp::LessEqual: return VK_COMPARE_OP_LESS_OR_EQUAL;
    case CompareOp::Greater: return VK_COMPARE_OP_GREATER;
    case CompareOp::GreaterEqual: return VK_COMPARE_OP_GREATER_OR_EQUAL;
    case CompareOp::Equal: return VK_COMPARE_OP_EQUAL;
    case CompareOp::NotEqual: return VK_COMPARE_OP_NOT_EQUAL;
    case CompareOp::Always: return VK_COMPARE_OP_ALWAYS;
    }
    return VK_COMPARE_OP_NEVER;
}

struct ImageParameters {
    VkImage        handle {};
    VkImageView    view {};
    VkSampler      sampler {};
    VkExtent3D     extent {};
    rstd::uint32_t mipmap_level { 1 };
    u64            generation { 0 };

    ImageParameters()  = default;
    ~ImageParameters() = default;
};

} // namespace vulkan

// Snapshot of the negotiated buffer set the producer should render into.
// Fired by ExSwapchain implementations whenever readiness flips or the
// negotiated extent/format changes.
struct ExSwapchainReadyEvent {
    bool     ready;
    unsigned width;
    unsigned height;
    VkFormat format;
};

enum class FrameSurfaceAcquireKind
{
    QueueOrdered,
    BinarySemaphore,
    ExternalProtocol,
};

struct FrameSurfaceIdentity {
    u64 owner_generation { 0 };
    u32 image_index { 0 };
    u64 acquire_serial { 0 };

    bool valid() const noexcept { return owner_generation != u64() && acquire_serial != u64(); }
    bool operator==(const FrameSurfaceIdentity&) const = default;
};

enum class FrameSurfaceReuseKind
{
    QueueOrdered,
    PresentationAcquired,
    NeverSubmitted,
    ConsumerReleased,
};

struct FrameSurfaceReuseProof {
    FrameSurfaceReuseKind kind { FrameSurfaceReuseKind::QueueOrdered };
    u64                   release_point { 0 };

    bool valid() const noexcept {
        return (kind == FrameSurfaceReuseKind::ConsumerReleased) == (release_point != u64());
    }
};

struct FrameSurfaceAcquireDependency {
    FrameSurfaceAcquireKind kind { FrameSurfaceAcquireKind::QueueOrdered };
    VkSemaphore             semaphore { VK_NULL_HANDLE };

    bool valid() const noexcept {
        return (kind == FrameSurfaceAcquireKind::BinarySemaphore) == (semaphore != VK_NULL_HANDLE);
    }
};

struct FrameSurfaceLease {
    FrameSurfaceIdentity          identity;
    FrameSurfaceReuseProof        reuse;
    vulkan::ImageParameters       image;
    VkFormat                      format { VK_FORMAT_UNDEFINED };
    VkImageLayout                 initial_layout { VK_IMAGE_LAYOUT_UNDEFINED };
    rstd::uint32_t                initial_queue_family { VK_QUEUE_FAMILY_IGNORED };
    FrameSurfaceAcquireDependency acquire;
    VkImageLayout                 final_layout { VK_IMAGE_LAYOUT_UNDEFINED };
    rstd::uint32_t                final_queue_family { VK_QUEUE_FAMILY_IGNORED };
    bool                          discard_content { true };

    bool valid() const noexcept {
        const bool acquire_matches_reuse =
            (acquire.kind == FrameSurfaceAcquireKind::QueueOrdered &&
             reuse.kind == FrameSurfaceReuseKind::QueueOrdered) ||
            (acquire.kind == FrameSurfaceAcquireKind::BinarySemaphore &&
             reuse.kind == FrameSurfaceReuseKind::PresentationAcquired) ||
            (acquire.kind == FrameSurfaceAcquireKind::ExternalProtocol &&
             (reuse.kind == FrameSurfaceReuseKind::NeverSubmitted ||
              reuse.kind == FrameSurfaceReuseKind::ConsumerReleased));
        return identity.valid() && reuse.valid() && acquire_matches_reuse &&
               image.handle != VK_NULL_HANDLE && image.extent.width != 0 &&
               image.extent.height != 0 && image.extent.depth != 0 &&
               format != VK_FORMAT_UNDEFINED && acquire.valid() && discard_content &&
               final_layout != VK_IMAGE_LAYOUT_UNDEFINED &&
               initial_queue_family != VK_QUEUE_FAMILY_IGNORED &&
               final_queue_family != VK_QUEUE_FAMILY_IGNORED;
    }
};

enum class FrameSurfaceCompletionStatus
{
    Submitted,
    Aborted,
    NotPending,
    StaleIdentity,
    SessionLost,
    ProtocolError,
};

struct FrameSurfaceCompletionResult {
    FrameSurfaceCompletionStatus status { FrameSurfaceCompletionStatus::NotPending };
    FrameSurfaceIdentity         identity;
    i32                          error_code { 0 };

    bool completed() const noexcept {
        return status == FrameSurfaceCompletionStatus::Submitted ||
               status == FrameSurfaceCompletionStatus::Aborted;
    }
};

class ExSwapchain;

class FrameSurfaceCompletionCapability {
public:
    FrameSurfaceCompletionCapability() = default;
    ~FrameSurfaceCompletionCapability();

    FrameSurfaceCompletionCapability(const FrameSurfaceCompletionCapability&)            = delete;
    FrameSurfaceCompletionCapability& operator=(const FrameSurfaceCompletionCapability&) = delete;
    FrameSurfaceCompletionCapability(FrameSurfaceCompletionCapability&&) noexcept;
    FrameSurfaceCompletionCapability& operator=(FrameSurfaceCompletionCapability&&) noexcept;

    bool valid() const noexcept { return m_owner != nullptr && m_identity.valid(); }

    FrameSurfaceCompletionResult Submit(int producer_sync_fd);
    FrameSurfaceCompletionResult Abort();

private:
    friend class ExSwapchain;

    FrameSurfaceCompletionCapability(std::shared_ptr<ExSwapchain> owner,
                                     FrameSurfaceIdentity         identity)
        : m_owner(std::move(owner)), m_identity(identity) {}

    std::shared_ptr<ExSwapchain> m_owner;
    FrameSurfaceIdentity         m_identity;
};

enum class FrameSurfaceAcquireStatus
{
    Acquired,
    Suboptimal,
    Busy,
    NotReady,
    SessionLost,
    ProtocolError,
};

struct FrameSurfaceAcquireResult {
    FrameSurfaceAcquireStatus        status { FrameSurfaceAcquireStatus::NotReady };
    FrameSurfaceLease                lease;
    FrameSurfaceCompletionCapability completion;
    i32                              error_code { 0 };

    bool acquired() const noexcept {
        return (status == FrameSurfaceAcquireStatus::Acquired ||
                status == FrameSurfaceAcquireStatus::Suboptimal) &&
               lease.valid() && completion.valid();
    }
};

enum class TexTiling
{
    OPTIMAL,
    LINEAR
};

// Per-slot DMA-BUF descriptor surfaced to the IPC layer. Local backend
// fills these from its self-allocated `ExImageParameters`; bridge backend
// keeps them empty (the bridge already published the metadata via
// `bind_buffers` itself).
struct ExHandle {
    int   fd { -1 };
    i32   width { 0 };
    i32   height { 0 };
    usize size { 0 };

    rstd::uint32_t drm_fourcc { 0 };
    rstd::uint64_t drm_modifier { 0 };
    VkDeviceSize   plane0_offset { 0 };
    rstd::uint32_t plane0_stride { 0 };

    ExHandle() = default;
    ExHandle(int id): m_id(id) {};

    i32 id() const { return m_id; }

private:
    i32 m_id { 0 };
};

template<typename T>
class TripleSwapchain {
public:
    virtual ~TripleSwapchain() = default;

    TripleSwapchain(const TripleSwapchain&)            = delete;
    TripleSwapchain& operator=(const TripleSwapchain&) = delete;
    TripleSwapchain(TripleSwapchain&&)                 = delete;
    TripleSwapchain& operator=(TripleSwapchain&&)      = delete;

    T* eatFrame() {
        if (! dirty().exchange(false)) return nullptr;
        presented() = ready().exchange(presented());
        return presented();
    }
    void renderFrame() {
        inprogress() = ready().exchange(inprogress());
        dirty().exchange(true);
    }
    T* getInprogress() { return inprogress(); }

    rstd::array<T*, 3> snapshot_all_slots() {
        return rstd::array<T*, 3> { presented().load(), ready().load(), inprogress().load() };
    }

    virtual unsigned width() const  = 0;
    virtual unsigned height() const = 0;

protected:
    TripleSwapchain() = default;

    virtual std::atomic<T*>& presented()  = 0;
    virtual std::atomic<T*>& ready()      = 0;
    virtual std::atomic<T*>& inprogress() = 0;

private:
    std::atomic<bool>& dirty() { return m_dirty; };
    std::atomic<bool>  m_dirty { false };
};

// Producer-side abstraction over the offscreen swapchain. Two
// implementations exist:
//   - LocalExSwapchain: self-allocates 3 DMA-BUF-backed VkImages via the
//     Vulkan TextureCache; the standalone viewers and any in-process host
//     drive bind_buffers / frame_ready themselves and consume `eatFrame()`
//     / `snapshot_all_slots()`.
//   - BridgeExSwapchain: wraps a `ww_pool_t`; bridge owns the slot images
//     and completes submission through the acquired frame capability.
class ExSwapchain : public std::enable_shared_from_this<ExSwapchain> {
public:
    virtual ~ExSwapchain() = default;

    ExSwapchain(const ExSwapchain&)            = delete;
    ExSwapchain& operator=(const ExSwapchain&) = delete;
    ExSwapchain(ExSwapchain&&)                 = delete;
    ExSwapchain& operator=(ExSwapchain&&)      = delete;

    virtual void poll() {}

    virtual FrameSurfaceAcquireResult acquireRenderTarget() = 0;

    virtual int takeLastFrameSyncFd() { return -1; }

    virtual ExHandle*                 eatFrame() { return nullptr; }
    virtual rstd::array<ExHandle*, 3> snapshot_all_slots() {
        return rstd::array<ExHandle*, 3> { nullptr, nullptr, nullptr };
    }

    virtual unsigned width() const  = 0;
    virtual unsigned height() const = 0;
    virtual VkFormat format() const = 0;

    virtual bool ready() const = 0;

    virtual void setOnReadyChanged(std::function<void(const ExSwapchainReadyEvent&)>) = 0;

protected:
    ExSwapchain() = default;

    FrameSurfaceCompletionCapability MakeCompletionCapability(FrameSurfaceIdentity identity) {
        return FrameSurfaceCompletionCapability(shared_from_this(), identity);
    }

private:
    friend class FrameSurfaceCompletionCapability;

    virtual FrameSurfaceCompletionResult CompleteRendered(FrameSurfaceIdentity identity,
                                                          int producer_sync_fd)           = 0;
    virtual FrameSurfaceCompletionResult AbortRenderTarget(FrameSurfaceIdentity identity) = 0;
};

inline FrameSurfaceCompletionCapability::~FrameSurfaceCompletionCapability() {
    if (valid()) (void)Abort();
}

inline FrameSurfaceCompletionCapability::FrameSurfaceCompletionCapability(
    FrameSurfaceCompletionCapability&& other) noexcept
    : m_owner(std::move(other.m_owner)), m_identity(other.m_identity) {
    other.m_identity = {};
}

inline FrameSurfaceCompletionCapability&
FrameSurfaceCompletionCapability::operator=(FrameSurfaceCompletionCapability&& other) noexcept {
    if (this == &other) return *this;
    if (valid()) (void)Abort();
    m_owner          = std::move(other.m_owner);
    m_identity       = other.m_identity;
    other.m_identity = {};
    return *this;
}

inline FrameSurfaceCompletionResult FrameSurfaceCompletionCapability::Submit(int producer_sync_fd) {
    if (! valid()) {
        CloseExternalFileDescriptor(producer_sync_fd);
        return {};
    }
    auto owner    = std::move(m_owner);
    auto identity = m_identity;
    m_identity    = {};
    return owner->CompleteRendered(identity, producer_sync_fd);
}

inline FrameSurfaceCompletionResult FrameSurfaceCompletionCapability::Abort() {
    if (! valid()) return {};
    auto owner    = std::move(m_owner);
    auto identity = m_identity;
    m_identity    = {};
    return owner->AbortRenderTarget(identity);
}

namespace vulkan
{

// ---------- Instance.hpp ----------

struct Extension {
    bool             required { false };
    std::string_view name;
};

using InstanceLayer = Extension;

using CheckGpuOp = std::function<bool(const vvk::PhysicalDevice&)>;

constexpr std::string_view VALIDATION_LAYER_NAME = "VK_LAYER_KHRONOS_validation";

constexpr rstd::uint32_t WP_VULKAN_VERSION { VK_API_VERSION_1_1 };
constexpr const char*    WP_APPLICATION_NAME { "scene render" };

class Device;
class Instance {
public:
    Instance()  = default;
    ~Instance() = default;

    void Destroy();

    static bool Create(Instance&, std::span<const Extension>, std::span<const InstanceLayer>,
                       rstd::uint32_t api_version = WP_VULKAN_VERSION);
    bool ChoosePhysicalDevice(const CheckGpuOp& checkgpu, std::span<const rstd::uint8_t> uuid = {});

    const vvk::Instance&         inst() const;
    const vvk::PhysicalDevice&   gpu() const;
    const vvk::SurfaceKHR&       surface() const;
    rstd::uint32_t               api_version() const { return m_api_version; }
    std::span<const std::string> enabled_extensions() const { return m_enabled_extensions; }

    bool offscreen() const;
    void setSurface(VkSurfaceKHR);
    bool supportExt(std::string_view) const;
    bool supportLayer(std::string_view) const;

private:
    vvk::InstanceDispatch m_dld;
    vvk::Instance         m_vinst;

    vvk::DebugUtilsMessenger m_debug_utils;
    vvk::PhysicalDevice      m_gpu {};
    rstd::uint32_t           m_api_version { WP_VULKAN_VERSION };

    vvk::SurfaceKHR          m_surface {};
    Set<std::string>         m_extensions;
    std::vector<std::string> m_enabled_extensions;
    Set<std::string>         m_layers;
};

// ShaderSpv / Uni_ShaderSpv now live in wescene.shader_compile (re-exported above).

// ---------- Parameters.hpp ----------

struct QueueParameters {
    vvk::Queue     handle;
    rstd::uint32_t family_index;
};

struct VmaBufferParameters {
    vvk::VmaBuffer handle;
    VkDeviceSize   req_size;

    VmaBufferParameters();
    ~VmaBufferParameters();
    VmaBufferParameters(VmaBufferParameters&& o) noexcept;
    VmaBufferParameters& operator=(VmaBufferParameters&& o) noexcept;
};

struct BufferParameters {
    VkBuffer     handle;
    VkDeviceSize req_size;
    BufferParameters()  = default;
    ~BufferParameters() = default;
    BufferParameters(const VmaBufferParameters& o) noexcept
        : handle(*o.handle), req_size(o.req_size) {}
};

struct VmaImageParameters : NoCopy {
    vvk::VmaImage  handle;
    vvk::ImageView view;
    vvk::Sampler   sampler;
    VkExtent3D     extent;
    unsigned       mipmap_level { 1 };
    u64            generation { 0 };

    VmaImageParameters();
    ~VmaImageParameters();
    VmaImageParameters(VmaImageParameters&& o) noexcept;
    VmaImageParameters& operator=(VmaImageParameters&& o) noexcept;
};

struct ExImageParameters : NoCopy {
    vvk::DeviceMemory    mem {};
    VkMemoryRequirements mem_reqs {};

    vvk::Image     handle;
    vvk::ImageView view;
    vvk::Sampler   sampler;
    VkExtent3D     extent;
    unsigned       mipmap_level { 1 };
    u64            generation { 0 };
    int            fd { 0 };

    rstd::uint32_t drm_fourcc { 0 };
    rstd::uint64_t drm_modifier { 0 };
    VkDeviceSize   plane0_offset { 0 };
    rstd::uint32_t plane0_stride { 0 };

    ExImageParameters();
    ~ExImageParameters();
    ExImageParameters(ExImageParameters&& o) noexcept;
    ExImageParameters& operator=(ExImageParameters&& o) noexcept;
};

// `ImageParameters` itself is global-attached (defined in classic
// Swapchain/ExSwapchain.hpp). These free helpers replace the conversion
// ctors that used to live on it — those ctors needed module-attached
// Vma/Ex types which can't be visible in classic purview.
inline ImageParameters ToImageParameters(const VmaImageParameters& o) noexcept {
    ImageParameters out;
    out.handle       = *o.handle;
    out.view         = *o.view;
    out.sampler      = *o.sampler;
    out.extent       = o.extent;
    out.mipmap_level = o.mipmap_level;
    out.generation   = o.generation;
    return out;
}
inline ImageParameters ToImageParameters(const ExImageParameters& o) noexcept {
    ImageParameters out;
    out.handle       = *o.handle;
    out.view         = *o.view;
    out.sampler      = *o.sampler;
    out.extent       = o.extent;
    out.mipmap_level = o.mipmap_level;
    out.generation   = o.generation;
    return out;
}

struct ImageSlots : NoCopy {
    std::vector<VmaImageParameters> slots;

    ImageSlots();
    ~ImageSlots();
    ImageSlots(ImageSlots&& o) noexcept;
    ImageSlots& operator=(ImageSlots&& o) noexcept;
};

struct ImageSlotsRef {
    std::vector<ImageParameters> slots;

    std::ptrdiff_t active { 0 };

    auto& getActive() const {
        if (active > 0 && active >= std::ssize(slots)) return slots[0];
        return slots[static_cast<std::size_t>(active)];
    }
    ImageSlotsRef();
    ~ImageSlotsRef();
    ImageSlotsRef(const ImageSlots&);
};

// 纹理分配附带的运行时（视频纹理解码推进），随分配对象存活。
struct TextureAllocationRuntime {
    virtual ~TextureAllocationRuntime() = default;

    virtual void Pump(double seconds) = 0;
};

class TextureAllocation {
public:
    explicit TextureAllocation(
        ImageSlots slots, std::shared_ptr<TextureAllocationRuntime> runtime = {})
        : m_slots(rstd::move(slots)), m_runtime(rstd::move(runtime)) {}

    auto View() const -> ImageSlotsRef { return ImageSlotsRef(m_slots); }

private:
    ImageSlots                                m_slots;
    std::shared_ptr<TextureAllocationRuntime> m_runtime;
};

// ---------- Swapchain.hpp ----------

class Swapchain {
public:
    static bool                      Create(Device&, VkSurfaceKHR, VkExtent2D, Swapchain&);
    const vvk::SwapchainKHR&         handle() const;
    VkFormat                         format() const;
    VkExtent2D                       extent() const;
    VkPresentModeKHR                 presentMode() const;
    std::span<const ImageParameters> images() const;

private:
    vvk::SwapchainKHR            m_handle;
    VkSurfaceFormatKHR           m_format;
    VkExtent2D                   m_extent;
    VkPresentModeKHR             m_present_mode;
    std::vector<ImageParameters> m_images;
    std::vector<vvk::ImageView>  m_imageviews;
};

// ---------- TextureCache.hpp ----------

VkFormat             ToVkType(TextureFormat);
VkSamplerAddressMode ToVkType(TextureWrap);
VkFilter             ToVkType(TextureFilter);

using TexHash = usize;

struct TextureKey {
    i32                   width;
    i32                   height;
    VkImageUsageFlags     usage;
    TextureFormat         format;
    TextureSample         sample;
    unsigned              mipmap_level { 1 };
    VkSampleCountFlagBits samples { VK_SAMPLE_COUNT_1_BIT };

    static TexHash HashValue(const TextureKey&);
};

struct VideoDecoderObservation {
    std::string resource_key;
    std::uint64_t instance_id { 0 };
    std::string codec;
    std::optional<std::uint32_t> coded_width;
    std::optional<std::uint32_t> coded_height;
    std::string pixel_format;
    std::optional<std::int32_t> fps_num;
    std::optional<std::int32_t> fps_den;
    std::string fps_source;
    std::string decoder_kind;
    bool metadata_unknown { true };
    bool active { false };
    std::uint64_t opened_at_tick { 0 };
    std::uint64_t last_observed_active_tick { 0 };
    std::optional<std::uint64_t> first_observed_inactive_tick;
};

struct VideoDecoderInventory {
    bool observed { false };
    std::uint64_t observed_through_tick { 0 };
    std::uint64_t peak_active_instances { 0 };
    std::vector<VideoDecoderObservation> decoders;
};

class TextureCache : NoCopy, NoMove {
public:
    struct VideoRegistry;

    struct VideoDecodeOptions {
        std::string hwdec { "auto" };
        std::string render_node;
    };

    TextureCache(const Device&);
    ~TextureCache();

    void Clear();

    void SetVideoDecodeOptions(VideoDecodeOptions);

    Option<ExImageParameters> CreateExTex(u32 witdh, u32 height, VkFormat, VkImageTiling);
    rstd::Option<rstd::sync::Arc<TextureAllocation>>
    AllocateImportedTexture(const Image&, Option<rstd::sync::Arc<VideoPlaybackState>> playback);
    rstd::Option<rstd::sync::Arc<TextureAllocation>> AllocateTexture(TextureKey);

    /* Per-frame hook: advance every registered video-tex by `dt_seconds`,
     * pull as many decoded frames as needed to catch up to wall PTS,
     * convert NV12→RGBA on the CPU, and upload to the slot's stable
     * VkImage. No-op if no video textures are registered. */
    void PumpVideoTextures(double dt_seconds);
    VideoDecoderInventory ObserveVideoDecoders();

    /* vkCmdCopyBufferToImage a sub-rect of `atlas` into the supplied texture. */
    bool UploadFontAtlasRegion(ref<TextureAllocation> texture, const rstd::uint8_t* atlas,
                               rstd::uint32_t atlas_w, rstd::uint32_t x, rstd::uint32_t y,
                               rstd::uint32_t w, rstd::uint32_t h);

private:
    Option<VmaImageParameters> CreateTex(TextureKey);
    u64                        nextImageGeneration();
    void                       AssignImageGeneration(VmaImageParameters&);
    void                       AssignImageGeneration(ExImageParameters&);
    /* VIDEO-typed Image branch of AllocateImportedTexture: registers a wavsen
     * VideoDecoder + stable RGBA8 VkImage and returns an ImageSlotsRef
     * pointing at that same VkImage so material binding is transparent. */
    rstd::Option<rstd::sync::Arc<TextureAllocation>>
         CreateVideoTex(const Image&, Option<rstd::sync::Arc<VideoPlaybackState>> playback);
    void allocateCmd();
    vvk::CommandBuffers m_tex_cmds;
    vvk::CommandBuffer  m_tex_cmd;

    const Device&      m_device;
    VideoDecodeOptions m_video_decode_options;
    u64                m_next_image_generation { 1 };
    std::uint64_t      m_video_observation_tick { 0 };

    /* Opaque pImpl for the active video-tex set. Defined inside
     * TextureCache.cpp to keep wavsen.video out of the public
     * wescene.vulkan module interface. */
    Option<Box<VideoRegistry>> m_video_registry;
};

struct ImageUploadTicket {
    u64 value { 0 };

    bool        Valid() const noexcept { return value != u64(); }
    friend bool operator==(const ImageUploadTicket&, const ImageUploadTicket&) = default;
};

struct PreparedImageAllocation {
    rstd::sync::Arc<TextureAllocation> allocation;
    rstd::Option<ImageUploadTicket>    upload;
};

class ImageUploadManager;

class RecordedImageUploads : NoCopy {
public:
    RecordedImageUploads() = default;
    ~RecordedImageUploads();

    RecordedImageUploads(RecordedImageUploads&&) noexcept;
    RecordedImageUploads& operator=(RecordedImageUploads&&) noexcept;

    bool Valid() const noexcept;

private:
    struct State;
    friend class ImageUploadManager;
    friend class ImageUploadBatchLease;

    RecordedImageUploads(ImageUploadManager* owner, std::shared_ptr<State> state);
    void Reset();

    ImageUploadManager*    m_owner { nullptr };
    std::shared_ptr<State> m_state;
};

class ImageUploadBatchLease : NoCopy {
public:
    ImageUploadBatchLease() = default;
    ~ImageUploadBatchLease();

    ImageUploadBatchLease(ImageUploadBatchLease&&) noexcept;
    ImageUploadBatchLease& operator=(ImageUploadBatchLease&&) noexcept;

    bool                               Valid() const noexcept;
    std::span<const ImageUploadTicket> Tickets() const noexcept;

private:
    friend class ImageUploadManager;
    explicit ImageUploadBatchLease(std::shared_ptr<RecordedImageUploads::State> state);

    std::shared_ptr<RecordedImageUploads::State> m_state;
};

class ImageUploadManager : NoCopy, NoMove {
public:
    explicit ImageUploadManager(const Device&);
    ~ImageUploadManager();

    bool init();
    void destroy();

    auto QueueWrite(rstd::sync::Arc<TextureAllocation> allocation, const Image& image)
        -> Option<ImageUploadTicket>;
    auto QueueTransparentClear(rstd::sync::Arc<TextureAllocation> allocation)
        -> Option<ImageUploadTicket>;

    bool HasPendingUploads() const noexcept;
    bool RecordPendingUploads(vvk::CommandBuffer& command, RecordedImageUploads& recorded);
    auto CommitRecordedUploads(RecordedImageUploads&& recorded) -> Option<ImageUploadBatchLease>;
    void CancelRecordedUploads(const std::shared_ptr<RecordedImageUploads::State>& state);
    void DiscardPendingUploads();
    void Trim();

private:
    struct Impl;
    Box<Impl> m_impl;
};

// 资源准备阶段创建/分配纹理的后端。
struct ImagePrepareBackend {
    virtual ~ImagePrepareBackend() = default;

    virtual auto CreateImportedTexture(rstd::ref<Image>                            image,
                                       Option<rstd::sync::Arc<VideoPlaybackState>> playback)
        -> rstd::Option<PreparedImageAllocation> = 0;
    virtual auto AllocateTexture(TextureKey key)
        -> rstd::Option<rstd::sync::Arc<TextureAllocation>> = 0;
    virtual auto AllocateTransparentTexture(TextureKey key)
        -> rstd::Option<PreparedImageAllocation> = 0;
};

class ImagePrepareContext final : public ImagePrepareBackend {
public:
    ImagePrepareContext(TextureCache& textures, ImageUploadManager& uploads)
        : m_textures(textures), m_uploads(uploads) {}

    auto CreateImportedTexture(ref<Image>                                  image,
                               Option<rstd::sync::Arc<VideoPlaybackState>> playback)
        -> Option<PreparedImageAllocation> override;
    auto AllocateTexture(TextureKey key) -> Option<rstd::sync::Arc<TextureAllocation>> override;
    auto AllocateTransparentTexture(TextureKey key) -> Option<PreparedImageAllocation> override;

private:
    TextureCache&       m_textures;
    ImageUploadManager& m_uploads;
};

void RecordGenerateMipmaps(vvk::CommandBuffer&, const ImageParameters&);

// ---------- Device.hpp ----------

struct DeviceCapabilities {
    bool           timeline_semaphore { false };
    bool           synchronization2 { false };
    bool           push_descriptor { false };
    rstd::uint32_t max_push_descriptors { 0 };
    bool           multi_viewport { false };
    bool           shader_output_viewport_index { false };
    bool           sampled_depth_d32 { false };
    bool           depth_clamp { false };
    rstd::uint32_t max_geometry_output_vertices { 256 };
    rstd::uint32_t max_geometry_total_output_components { 1024 };
    bool           memory_budget { false };
    bool           external_memory_fd { false };
    bool           external_memory_dma_buf { false };
    bool           drm_format_modifier { false };
    bool           foreign_queue { false };
    rstd::uint32_t graphics_queue_family { VK_QUEUE_FAMILY_IGNORED };
    rstd::uint32_t present_queue_family { VK_QUEUE_FAMILY_IGNORED };

    bool shadow_multi_viewport() const noexcept {
        return multi_viewport && shader_output_viewport_index;
    }

    bool directional_shadow() const noexcept {
        return shadow_multi_viewport() && sampled_depth_d32;
    }
};

struct MemoryBudgetSnapshot {
    VkDeviceSize usage { 0 };
    VkDeviceSize budget { 0 };

    VkDeviceSize available() const noexcept { return budget > usage ? budget - usage : 0; }
};

// 显存预算来源（Device 实现）。
struct MemoryBudgetSource {
    virtual ~MemoryBudgetSource() = default;

    virtual auto MemoryBudget() const -> MemoryBudgetSnapshot = 0;
};

struct PipelineParameters;

class Device : NoCopy, NoMove, public MemoryBudgetSource {
public:
    Device();
    ~Device();

    static bool Create(Instance&, std::span<const Extension> exts, VkExtent2D extent, Device&);
    static bool CheckGPU(vvk::PhysicalDevice gpu, std::span<const Extension> exts,
                         VkSurfaceKHR surface);

    void Destroy();

    const auto&                  graphics_queue() const { return m_graphics_queue; }
    const auto&                  present_queue() const { return m_present_queue; }
    const auto&                  device() const { return m_device; }
    const auto&                  handle() const { return m_device; }
    const auto&                  gpu() const { return m_gpu; }
    VkInstance                   instance_handle() const { return m_instance; }
    rstd::uint32_t               instance_api_version() const { return m_instance_api_version; }
    std::span<const std::string> enabled_instance_extensions() const {
        return m_enabled_instance_extensions;
    }
    std::span<const std::string> enabled_device_extensions() const {
        return m_enabled_device_extensions;
    }
    const auto& limits() const { return m_limits; }
    const auto& vma_allocator() const { return *m_allocator; }
    const auto& cmd_pool() const { return m_command_pool; }
    const auto& swapchain() const { return m_swapchain; }
    const auto& out_extent() const { return m_extent; }
    const auto& capabilities() const { return m_capabilities; }
    void        set_out_extent(VkExtent2D v) { m_extent = v; }

    bool supportExt(std::string_view) const;

    VkDeviceSize GetUsage() const;
    auto         MemoryBudget() const -> MemoryBudgetSnapshot override;

private:
    std::vector<VkDeviceQueueCreateInfo> ChooseDeviceQueue(VkSurfaceKHR = {});

    vvk::DeviceDispatch     dld;
    VkInstance              m_instance { VK_NULL_HANDLE };
    rstd::uint32_t          m_instance_api_version { WP_VULKAN_VERSION };
    vvk::Device             m_device;
    vvk::PhysicalDevice     m_gpu;
    vvk::VmaAllocatorHandle m_allocator;

    VkPhysicalDeviceLimits   m_limits;
    DeviceCapabilities       m_capabilities;
    Set<std::string>         m_extensions;
    std::vector<std::string> m_enabled_instance_extensions;
    std::vector<std::string> m_enabled_device_extensions;

    Swapchain m_swapchain;

    vvk::CommandPool m_command_pool;

    QueueParameters m_graphics_queue;
    QueueParameters m_present_queue;

    VkExtent2D m_extent { 1, 1 };
};

// ---------- Util.hpp ----------

inline bool CreateStagingBuffer(VmaAllocator allocator, VkDeviceSize size,
                                VmaBufferParameters& buffer) {
    VkBufferCreateInfo ci {
        .sType = VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO,
        .pNext = nullptr,
        .size  = size,
        .usage = VK_BUFFER_USAGE_TRANSFER_SRC_BIT,
    };
    buffer.req_size = ci.size;

    VmaAllocationCreateInfo vma_info = {};
    vma_info.usage                   = VMA_MEMORY_USAGE_CPU_ONLY;
    VVK_CHECK_BOOL_RE(vvk::CreateBuffer(allocator, ci, vma_info, buffer.handle));
    return true;
}

// ---------- BufferManager.hpp ----------

class BufferAllocation {
public:
    BufferAllocation() = default;
    ~BufferAllocation();

    BufferAllocation(const BufferAllocation&)            = delete;
    BufferAllocation& operator=(const BufferAllocation&) = delete;

    BufferAllocation(BufferAllocation&& o) noexcept;
    BufferAllocation& operator=(BufferAllocation&& o) noexcept;

    explicit operator bool() const noexcept;

    VkBuffer     buffer() const noexcept;
    VkDeviceSize offset() const noexcept;
    VkDeviceSize size() const noexcept;

private:
    struct State;
    friend class BufferManager;
    friend class BufferUploadBatchLease;

    explicit BufferAllocation(std::shared_ptr<State> state);

    std::shared_ptr<State> m_state;
};

enum class BufferUploadClass
{
    Vertex,
    Index,
    Uniform,
    Storage,
    Transfer,
};

struct BufferAllocationRequest {
    VkDeviceSize      size { 0 };
    VkDeviceSize      alignment { 1 };
    BufferUploadClass usage { BufferUploadClass::Vertex };
};

struct BufferUploadTicket {
    u64 value { 0 };

    bool        Valid() const noexcept { return value != u64(); }
    friend bool operator==(const BufferUploadTicket&, const BufferUploadTicket&) = default;
};

class BufferManager;

class RecordedBufferUploads : NoCopy {
public:
    RecordedBufferUploads() = default;
    ~RecordedBufferUploads();

    RecordedBufferUploads(RecordedBufferUploads&&) noexcept;
    RecordedBufferUploads& operator=(RecordedBufferUploads&&) noexcept;

    bool Valid() const noexcept;

private:
    struct State;
    friend class BufferManager;
    friend class BufferUploadBatchLease;

    RecordedBufferUploads(BufferManager* owner, std::shared_ptr<State> state);
    void Reset();

    BufferManager*         m_owner { nullptr };
    std::shared_ptr<State> m_state;
};

class BufferUploadBatchLease : NoCopy {
public:
    BufferUploadBatchLease() = default;
    ~BufferUploadBatchLease();

    BufferUploadBatchLease(BufferUploadBatchLease&&) noexcept;
    BufferUploadBatchLease& operator=(BufferUploadBatchLease&&) noexcept;

    bool                                Valid() const noexcept;
    std::span<const BufferUploadTicket> Tickets() const noexcept;

private:
    friend class BufferManager;
    explicit BufferUploadBatchLease(std::shared_ptr<RecordedBufferUploads::State> state);

    std::shared_ptr<RecordedBufferUploads::State> m_state;
};

// 资源注册表分配/写入缓冲的后端（BufferManager 实现；单测可替身）。
struct BufferBackend {
    virtual ~BufferBackend() = default;

    virtual auto AllocateBuffer(const BufferAllocationRequest& request)
        -> rstd::Option<BufferAllocation> = 0;
    virtual auto QueueBufferWrite(rstd::mut_ref<BufferAllocation> allocation,
                                  rstd::slice<rstd::u8> content, VkDeviceSize destination_offset = 0)
        -> rstd::Option<BufferUploadTicket> = 0;
};

class BufferManager : NoCopy, NoMove, public BufferBackend {
public:
    explicit BufferManager(const Device&);
    ~BufferManager();

    auto AllocateBuffer(const BufferAllocationRequest& request)
        -> Option<BufferAllocation> override {
        return Allocate(request);
    }
    auto QueueBufferWrite(rstd::mut_ref<BufferAllocation> allocation, rstd::slice<rstd::u8> content,
                          VkDeviceSize destination_offset) -> Option<BufferUploadTicket> override {
        return QueueWrite(*allocation,
                          std::span<const rstd::uint8_t>(
                              reinterpret_cast<const rstd::uint8_t*>(content.as_raw_ptr()),
                              content.len().to_primitive()),
                          destination_offset);
    }

    bool init();
    void destroy();

    Option<BufferAllocation>   Allocate(const BufferAllocationRequest& request);
    Option<BufferUploadTicket> QueueWrite(BufferAllocation&              allocation,
                                          std::span<const rstd::uint8_t> data,
                                          VkDeviceSize                   destination_offset = 0);

    bool HasPendingUploads() const noexcept;
    bool RecordPendingUploads(vvk::CommandBuffer& cmd, RecordedBufferUploads& recorded);
    Option<BufferUploadBatchLease> CommitRecordedUploads(RecordedBufferUploads&& recorded);
    void CancelRecordedUploads(const std::shared_ptr<RecordedBufferUploads::State>& state);
    void Trim();

private:
    struct Impl;
    Box<Impl> m_impl;
};

// ---------- GraphicsPipeline.hpp ----------

struct PipelineParameters {
    vvk::Pipeline    handle;
    VkPipelineLayout layout { VK_NULL_HANDLE };
};

struct DescriptorSetInfo {
    bool push_descriptor { false };

    Vec<VkDescriptorSetLayoutBinding> bindings;

    auto clone() const -> DescriptorSetInfo {
        auto cloned = Vec<VkDescriptorSetLayoutBinding>::with_capacity(bindings.len());
        for (const auto& binding : bindings) {
            auto copied = binding;
            cloned.push(rstd::move(copied));
        }
        return DescriptorSetInfo {
            .push_descriptor = push_descriptor,
            .bindings        = rstd::move(cloned),
        };
    }
};

class GraphicsPipeline : NoCopy, NoMove {
public:
    GraphicsPipeline();
    ~GraphicsPipeline();

    void toDefault();
    bool create(const Device&, VkRenderPass, VkPipelineLayout, PipelineParameters&);

    VkPipelineMultisampleStateCreateInfo   multisample {};
    VkPipelineRasterizationStateCreateInfo raster {};
    VkPipelineDepthStencilStateCreateInfo  depth {};

    const ShaderSpv* getShaderSpv(VkShaderStageFlagBits) const;
    const auto&      pass() const { return m_pass; }

    GraphicsPipeline& setColorBlendStates(std::span<const VkPipelineColorBlendAttachmentState>);
    GraphicsPipeline& setColorBlendOptions(VkPipelineColorBlendStateCreateFlags,
                                           const rstd::array<float, 4>&);
    GraphicsPipeline& setLogicOp(bool enable, VkLogicOp);

    GraphicsPipeline& setRenderPass(vvk::RenderPass);
    GraphicsPipeline& addStage(Uni_ShaderSpv&&);
    GraphicsPipeline&
        addInputAttributeDescription(std::span<const VkVertexInputAttributeDescription>);
    GraphicsPipeline& addInputBindingDescription(std::span<const VkVertexInputBindingDescription>);
    GraphicsPipeline& setCreateInfoOptions(VkPipelineCreateFlags flags, rstd::uint32_t subpass);
    GraphicsPipeline& setTopology(VkPrimitiveTopology);
    GraphicsPipeline& setPrimitiveRestartEnable(bool);
    GraphicsPipeline& setViewportScissorCount(rstd::uint32_t viewport_count,
                                              rstd::uint32_t scissor_count);
    GraphicsPipeline& setDynamicStates(std::span<const VkDynamicState>);
    GraphicsPipeline& setSampleCount(VkSampleCountFlagBits);

private:
    vvk::RenderPass m_pass;

    VkPipelineCreateFlags m_create_flags { 0 };
    rstd::uint32_t        m_subpass { 0 };

    VkPipelineInputAssemblyStateCreateInfo         m_input_assembly {};
    std::vector<VkVertexInputBindingDescription>   m_input_bind_descriptions;
    std::vector<VkVertexInputAttributeDescription> m_input_attr_descriptions;

    VkPipelineViewportStateCreateInfo                m_view;
    VkPipelineColorBlendStateCreateInfo              m_color;
    std::vector<VkDynamicState>                      m_dynamic_states;
    std::vector<VkPipelineColorBlendAttachmentState> m_color_attachments;
    Map<VkShaderStageFlagBits, Uni_ShaderSpv>        m_stage_spv_map;
};

// ShaderReflected / GenReflect / VulkanTarget / ShaderCompUnit / ShaderCompOpt /
// CompileAndLinkShaderUnits / Preprocess all live in wescene.shader_compile
// (re-exported above).

// ---------- VertexInputState.hpp ----------

struct VertexInputState {
    VkPipelineInputAssemblyStateCreateInfo         input_assembly;
    VkPipelineVertexInputStateCreateInfo           input;
    std::vector<VkVertexInputBindingDescription>   bind_descriptions;
    std::vector<VkVertexInputAttributeDescription> attr_descriptions;
};

// ---------- LocalExSwapchain.hpp ----------

struct LocalExHandle : NoCopy {
    ExHandle          handle;
    ExImageParameters image;

    LocalExHandle()  = default;
    ~LocalExHandle() = default;
    LocalExHandle(LocalExHandle&& o) noexcept: handle(o.handle), image(std::move(o.image)) {}
    LocalExHandle& operator=(LocalExHandle&& o) noexcept {
        handle = o.handle;
        image  = std::move(o.image);
        return *this;
    }
};

// NB: this used to be `private TripleSwapchain<ExHandle>` in classic, but
// module-attached classes can't downcast `this` to a privately-inherited
// global-attached base — clang refuses the implicit `this` conversion at
// the qualified-call site. Public inheritance is semantically equivalent
// here (callers never reach for the base interface directly).
class LocalExSwapchain final : public ::owe::ExSwapchain,
                               public ::owe::TripleSwapchain<::owe::ExHandle> {
public:
    LocalExSwapchain(rstd::array<LocalExHandle, 3> handles, VkExtent2D ext,
                     rstd::uint32_t queue_family)
        : m_handles(std::move(handles)), m_extent(ext), m_queue_family(queue_family) {
        int index = 0;
        for (auto& h : m_handles) {
            auto& handle         = h.handle;
            handle               = ::owe::ExHandle(index++);
            handle.width         = (i32)h.image.extent.width;
            handle.height        = (i32)h.image.extent.height;
            handle.fd            = h.image.fd;
            handle.size          = usize(h.image.mem_reqs.size);
            handle.drm_fourcc    = h.image.drm_fourcc;
            handle.drm_modifier  = h.image.drm_modifier;
            handle.plane0_offset = h.image.plane0_offset;
            handle.plane0_stride = h.image.plane0_stride;
        }
        m_presented  = &m_handles[usize()].handle;
        m_ready      = &m_handles[usize(1)].handle;
        m_inprogress = &m_handles[usize(2)].handle;
    }

    ~LocalExSwapchain() override {
        int fd = m_last_sync_fd.exchange(-1, std::memory_order_acq_rel);
        CloseExternalFileDescriptor(fd);
    }

    ::owe::FrameSurfaceAcquireResult acquireRenderTarget() override {
        if (m_surface_pending) {
            return { .status = ::owe::FrameSurfaceAcquireStatus::Busy };
        }
        const u32 slot_index     = rstd::as_cast<u32>(this->getInprogress()->id());
        const u64 acquire_serial = m_next_acquire_serial++;
        if (acquire_serial == u64()) {
            return { .status     = ::owe::FrameSurfaceAcquireStatus::ProtocolError,
                     .error_code = i32(-EOVERFLOW) };
        }
        ::owe::FrameSurfaceLease lease {
            .identity = { .owner_generation = u64(1),
                          .image_index      = slot_index,
                          .acquire_serial   = acquire_serial },
            .reuse    = { .kind = ::owe::FrameSurfaceReuseKind::QueueOrdered },
            .image    = ToImageParameters(m_handles.at(rstd::as_cast<usize>(slot_index)).image),
            .format   = VK_FORMAT_R8G8B8A8_UNORM,
            .initial_layout       = VK_IMAGE_LAYOUT_GENERAL,
            .initial_queue_family = m_queue_family,
            .acquire              = { .kind = ::owe::FrameSurfaceAcquireKind::QueueOrdered },
            .final_layout         = VK_IMAGE_LAYOUT_GENERAL,
            .final_queue_family   = m_queue_family,
            .discard_content      = true,
        };
        if (! lease.valid()) {
            return { .status     = ::owe::FrameSurfaceAcquireStatus::ProtocolError,
                     .error_code = i32(-EINVAL) };
        }
        m_surface_pending  = true;
        m_pending_identity = lease.identity;
        auto completion    = MakeCompletionCapability(lease.identity);
        return { .status     = ::owe::FrameSurfaceAcquireStatus::Acquired,
                 .lease      = std::move(lease),
                 .completion = std::move(completion) };
    }

    int takeLastFrameSyncFd() override {
        return m_last_sync_fd.exchange(-1, std::memory_order_acq_rel);
    }

    ::owe::ExHandle* eatFrame() override {
        return this->TripleSwapchain<::owe::ExHandle>::eatFrame();
    }
    rstd::array<::owe::ExHandle*, 3> snapshot_all_slots() override {
        return this->TripleSwapchain<::owe::ExHandle>::snapshot_all_slots();
    }

    unsigned width() const override { return m_extent.width; }
    unsigned height() const override { return m_extent.height; }
    VkFormat format() const override { return VK_FORMAT_R8G8B8A8_UNORM; }

    bool ready() const override { return true; }

    void setOnReadyChanged(std::function<void(const ::owe::ExSwapchainReadyEvent&)> cb) override {
        if (cb) {
            ::owe::ExSwapchainReadyEvent e {
                .ready  = true,
                .width  = m_extent.width,
                .height = m_extent.height,
                .format = VK_FORMAT_R8G8B8A8_UNORM,
            };
            cb(e);
        }
    }

protected:
    std::atomic<::owe::ExHandle*>& presented() override { return m_presented; }
    std::atomic<::owe::ExHandle*>& ready() override { return m_ready; }
    std::atomic<::owe::ExHandle*>& inprogress() override { return m_inprogress; }

private:
    ::owe::FrameSurfaceCompletionResult CompleteRendered(::owe::FrameSurfaceIdentity identity,
                                                         int acquire_sync_fd) override {
        if (! m_surface_pending) {
            CloseExternalFileDescriptor(acquire_sync_fd);
            return { .status   = ::owe::FrameSurfaceCompletionStatus::NotPending,
                     .identity = identity };
        }
        if (identity != m_pending_identity) {
            CloseExternalFileDescriptor(acquire_sync_fd);
            return { .status   = ::owe::FrameSurfaceCompletionStatus::StaleIdentity,
                     .identity = identity };
        }
        m_surface_pending  = false;
        m_pending_identity = {};
        if (acquire_sync_fd >= 0) {
            int old = m_last_sync_fd.exchange(acquire_sync_fd, std::memory_order_acq_rel);
            CloseExternalFileDescriptor(old);
        }
        this->renderFrame();
        return { .status = ::owe::FrameSurfaceCompletionStatus::Submitted, .identity = identity };
    }

    ::owe::FrameSurfaceCompletionResult
    AbortRenderTarget(::owe::FrameSurfaceIdentity identity) override {
        if (! m_surface_pending) {
            return { .status   = ::owe::FrameSurfaceCompletionStatus::NotPending,
                     .identity = identity };
        }
        if (identity != m_pending_identity) {
            return { .status   = ::owe::FrameSurfaceCompletionStatus::StaleIdentity,
                     .identity = identity };
        }
        m_surface_pending  = false;
        m_pending_identity = {};
        return { .status = ::owe::FrameSurfaceCompletionStatus::Aborted, .identity = identity };
    }

    rstd::array<LocalExHandle, 3> m_handles;
    std::atomic<::owe::ExHandle*> m_presented { nullptr };
    std::atomic<::owe::ExHandle*> m_ready { nullptr };
    std::atomic<::owe::ExHandle*> m_inprogress { nullptr };
    VkExtent2D                    m_extent;
    rstd::uint32_t                m_queue_family { VK_QUEUE_FAMILY_IGNORED };
    std::atomic<int>              m_last_sync_fd { -1 };
    u64                           m_next_acquire_serial { 1 };
    ::owe::FrameSurfaceIdentity   m_pending_identity;
    bool                          m_surface_pending { false };
};

inline std::shared_ptr<LocalExSwapchain> CreateLocalExSwapchain(const Device& device,
                                                                TextureCache& textures, unsigned w,
                                                                unsigned h, VkImageTiling tiling) {
    rstd::array<LocalExHandle, 3> handles;
    for (auto& handle : handles) {
        if (auto rv = textures.CreateExTex(u32(w), u32(h), VK_FORMAT_R8G8B8A8_UNORM, tiling);
            rv.is_some())
            handle.image = rstd::move(rv).unwrap();
        else
            return nullptr;
    }
    return std::make_shared<LocalExSwapchain>(
        std::move(handles), VkExtent2D { w, h }, device.graphics_queue().family_index);
}

} // namespace vulkan
} // namespace owe
