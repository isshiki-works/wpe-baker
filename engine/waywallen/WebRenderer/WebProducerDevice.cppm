export module waywallen.web_producer_device;

import rstd.cppstd;
import vvk;
import weweb;

export namespace ww_wescene
{

class WebProducerDevice {
public:
    WebProducerDevice();
    ~WebProducerDevice();

    WebProducerDevice(const WebProducerDevice&)            = delete;
    WebProducerDevice& operator=(const WebProducerDevice&) = delete;

    // Build instance + pick first device with the required extensions
    // + create transfer queue + command pool + sync objects. Returns
    // false on failure (logged to stderr).
    bool Init();

    // Tear everything down. Idempotent.
    void Shutdown();

    // Prefer the physical device whose VK_EXT_physical_device_drm render node
    // matches this path. Must be called before Init.
    void SetRenderNode(const std::string& path);

    // Vulkan handles (caller-owned by this class) — bridge consumes
    // these via `ww_pool_vulkan_init_t`.
    VkInstance       Instance() const { return instance_; }
    VkPhysicalDevice Physical() const { return phys_; }
    VkDevice         Device() const { return device_; }
    VkQueue          Queue() const { return queue_; }
    uint32_t         QueueFamily() const { return queue_family_; }

    // 16-byte UUIDs from VkPhysicalDeviceIDProperties; valid post-Init.
    const uint8_t* DeviceUuid() const { return device_uuid_; }
    const uint8_t* DriverUuid() const { return driver_uuid_; }

    // CEF DMA-BUF wrapped as a temporary VkImage. Lives until
    // `DestroyImported` is called. The blit must finish reading
    // `image` before destruction — `BlitToSlot` ensures that with a
    // CPU-side fence wait before returning.
    struct ImportedFrame {
        VkImage        image { VK_NULL_HANDLE };
        VkDeviceMemory memory { VK_NULL_HANDLE };
        uint32_t       width { 0 };
        uint32_t       height { 0 };
        VkFormat       format { VK_FORMAT_UNDEFINED };
        bool           ok { false };
    };

    // Import a CEF DMA-BUF as a temporary VkImage. The plane FD is
    // dup()ed internally so the caller can hand the original back to
    // CEF on callback return. `frame.modifier == DRM_FORMAT_MOD_INVALID`
    // is folded onto LINEAR (matches CEF's stride-equals-width*bpp
    // observation).
    ImportedFrame Import(const ::weweb::DmaBufFrame& frame);

    // Free the temp image + memory. Must be called from the same
    // thread; the GPU must already be finished reading (BlitToSlot
    // waits a fence on the caller's behalf).
    void DestroyImported(ImportedFrame& imp);

    // Record vkCmdBlitImage `imp.image → slot_image` (scaled to
    // `slot_extent`), submit, signal an exportable timeline. Blocks on
    // a CPU fence so `imp` is safe to destroy when this returns. On
    // success returns a sync_file fd suitable for
    // `ww_bridge_pool_submit_slot` (caller hands ownership to bridge);
    // returns -1 on failure.
    int BlitToSlot(const ImportedFrame& imp, VkImage slot_image, VkExtent2D slot_extent);

    // Upload a CEF CPU paint buffer into a bridge slot. `slot_format`
    // must be the VkFormat published by BridgeProducerCore for the
    // currently acquired slot.
    int UploadToSlot(const ::weweb::CpuPaintFrame& frame, VkImage slot_image,
                     VkExtent2D slot_extent, VkFormat slot_format);

private:
    bool CreateInstance();
    bool PickPhysicalDevice();
    bool CreateDevice();
    bool CreateCommandPool();
    bool CreateSyncObjects();
    bool BeginTransferCommands(const char* op);
    int  SubmitTransferCommands(const char* op, bool wait_for_completion);
    void RestoreTransferFence();
    bool EnsureCpuUploadResources(const ::weweb::CpuPaintFrame& frame);
    void DestroyCpuUploadResources();

    uint32_t FindMemoryType(uint32_t bits, VkMemoryPropertyFlags props) const;

    VkInstance                       instance_ { VK_NULL_HANDLE };
    VkPhysicalDevice                 phys_ { VK_NULL_HANDLE };
    VkPhysicalDeviceMemoryProperties mem_props_ {};
    VkDevice                         device_ { VK_NULL_HANDLE };
    uint32_t                         queue_family_ { 0 };
    VkQueue                          queue_ { VK_NULL_HANDLE };

    uint8_t     device_uuid_[16] {};
    uint8_t     driver_uuid_[16] {};
    std::string render_node_;

    VkCommandPool   cmd_pool_ { VK_NULL_HANDLE };
    VkCommandBuffer blit_cmd_ { VK_NULL_HANDLE };
    VkFence         blit_fence_ { VK_NULL_HANDLE };
    VkSemaphore     blit_sem_ { VK_NULL_HANDLE };

    PFN_vkGetMemoryFdPropertiesKHR pfn_GetMemoryFdProperties_ { nullptr };
    PFN_vkGetSemaphoreFdKHR        pfn_GetSemaphoreFd_ { nullptr };

    VkBuffer       cpu_staging_buffer_ { VK_NULL_HANDLE };
    VkDeviceMemory cpu_staging_memory_ { VK_NULL_HANDLE };
    void*          cpu_staging_map_ { nullptr };
    VkDeviceSize   cpu_staging_size_ { 0 };
    bool           cpu_staging_coherent_ { false };
};

} // namespace ww_wescene
