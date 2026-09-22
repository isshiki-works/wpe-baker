module;

#include <cstdio>
#include <unistd.h>

#define GLFW_INCLUDE_VULKAN
#include <GLFW/glfw3.h>

module viewer.web;

import rstd.cppstd;
import weweb;
import vvk;

namespace weweb
{

namespace
{

#define VK_CHECK(expr)                                                \
    do {                                                              \
        VkResult _r = (expr);                                         \
        if (_r != VK_SUCCESS) {                                       \
            std::fprintf(stderr,                                      \
                         "weweb: %s failed (VkResult=%d) at %s:%d\n", \
                         #expr,                                       \
                         static_cast<int>(_r),                        \
                         __FILE__,                                    \
                         __LINE__);                                   \
            return false;                                             \
        }                                                             \
    } while (0)

constexpr std::uint64_t kFenceTimeoutNs = 5'000'000'000ull; // 5s

VkFormat FormatToVk(DmaBufFormat f) {
    switch (f) {
    case DmaBufFormat::BGRA8_UNORM: return VK_FORMAT_B8G8R8A8_UNORM;
    case DmaBufFormat::RGBA8_UNORM: return VK_FORMAT_R8G8B8A8_UNORM;
    }
    return VK_FORMAT_B8G8R8A8_UNORM;
}

} // namespace

VulkanBlitter::VulkanBlitter() = default;

VulkanBlitter::~VulkanBlitter() { Shutdown(); }

bool VulkanBlitter::Init(GLFWwindow* window) {
    window_ = window;
    return CreateInstance() && CreateSurface(window) && PickPhysicalDevice() && CreateDevice() &&
           CreateCommandPool() && CreateSwapchain() && CreateSyncObjects();
}

void VulkanBlitter::Shutdown() {
    if (device_) vkDeviceWaitIdle(device_);

    DestroyOwnedImage();

    for (auto& s : img_avail_sem_)
        if (s) {
            vkDestroySemaphore(device_, s, nullptr);
            s = VK_NULL_HANDLE;
        }
    for (auto& s : render_done_sem_)
        if (s) vkDestroySemaphore(device_, s, nullptr);
    render_done_sem_.clear();
    for (auto& f : in_flight_fence_)
        if (f) {
            vkDestroyFence(device_, f, nullptr);
            f = VK_NULL_HANDLE;
        }

    if (cmd_pool_ != VK_NULL_HANDLE) {
        vkDestroyCommandPool(device_, cmd_pool_, nullptr);
        cmd_pool_ = VK_NULL_HANDLE;
    }
    DestroySwapchain();
    if (device_) {
        vkDestroyDevice(device_, nullptr);
        device_ = VK_NULL_HANDLE;
    }
    if (surface_ && instance_) {
        vkDestroySurfaceKHR(instance_, surface_, nullptr);
        surface_ = VK_NULL_HANDLE;
    }
    if (instance_) {
        vkDestroyInstance(instance_, nullptr);
        instance_ = VK_NULL_HANDLE;
    }
}

bool VulkanBlitter::CreateInstance() {
    VkApplicationInfo app {};
    app.sType              = VK_STRUCTURE_TYPE_APPLICATION_INFO;
    app.pApplicationName   = "weweb";
    app.applicationVersion = VK_MAKE_VERSION(0, 1, 0);
    app.pEngineName        = "weweb-blitter";
    app.engineVersion      = VK_MAKE_VERSION(0, 1, 0);
    // 1.1 promotes external_memory_capabilities into core, so we don't
    // need the instance extension.
    app.apiVersion = VK_API_VERSION_1_1;

    std::uint32_t glfw_count = 0;
    const char**  glfw_exts  = glfwGetRequiredInstanceExtensions(&glfw_count);
    if (! glfw_exts) {
        std::fprintf(stderr, "weweb: glfwGetRequiredInstanceExtensions failed\n");
        return false;
    }

    VkInstanceCreateInfo ci {};
    ci.sType                   = VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO;
    ci.pApplicationInfo        = &app;
    ci.enabledExtensionCount   = glfw_count;
    ci.ppEnabledExtensionNames = glfw_exts;

    VK_CHECK(vkCreateInstance(&ci, nullptr, &instance_));
    return true;
}

bool VulkanBlitter::CreateSurface(GLFWwindow* window) {
    VK_CHECK(glfwCreateWindowSurface(instance_, window, nullptr, &surface_));
    return true;
}

bool VulkanBlitter::PickPhysicalDevice() {
    std::uint32_t count = 0;
    VK_CHECK(vkEnumeratePhysicalDevices(instance_, &count, nullptr));
    if (count == 0) {
        std::fprintf(stderr, "weweb: no Vulkan physical devices\n");
        return false;
    }
    std::vector<VkPhysicalDevice> devs(count);
    VK_CHECK(vkEnumeratePhysicalDevices(instance_, &count, devs.data()));

    for (auto pd : devs) {
        std::uint32_t qcount = 0;
        vkGetPhysicalDeviceQueueFamilyProperties(pd, &qcount, nullptr);
        std::vector<VkQueueFamilyProperties> qfp(qcount);
        vkGetPhysicalDeviceQueueFamilyProperties(pd, &qcount, qfp.data());

        for (std::uint32_t i = 0; i < qcount; ++i) {
            if (! (qfp[i].queueFlags & VK_QUEUE_GRAPHICS_BIT)) continue;
            VkBool32 present = VK_FALSE;
            vkGetPhysicalDeviceSurfaceSupportKHR(pd, i, surface_, &present);
            if (! present) continue;

            std::uint32_t ecount = 0;
            vkEnumerateDeviceExtensionProperties(pd, nullptr, &ecount, nullptr);
            std::vector<VkExtensionProperties> exts(ecount);
            vkEnumerateDeviceExtensionProperties(pd, nullptr, &ecount, exts.data());
            bool has_swapchain = false, has_ext_mem_fd = false, has_dma_buf = false,
                 has_modifier = false, has_fmt_list = false;
            for (auto& e : exts) {
                if (std::strcmp(e.extensionName, VK_KHR_SWAPCHAIN_EXTENSION_NAME) == 0)
                    has_swapchain = true;
                if (std::strcmp(e.extensionName, VK_KHR_EXTERNAL_MEMORY_FD_EXTENSION_NAME) == 0)
                    has_ext_mem_fd = true;
                if (std::strcmp(e.extensionName, VK_EXT_EXTERNAL_MEMORY_DMA_BUF_EXTENSION_NAME) ==
                    0)
                    has_dma_buf = true;
                if (std::strcmp(e.extensionName, VK_EXT_IMAGE_DRM_FORMAT_MODIFIER_EXTENSION_NAME) ==
                    0)
                    has_modifier = true;
                if (std::strcmp(e.extensionName, VK_KHR_IMAGE_FORMAT_LIST_EXTENSION_NAME) == 0)
                    has_fmt_list = true;
            }
            if (! has_swapchain || ! has_ext_mem_fd || ! has_dma_buf || ! has_modifier ||
                ! has_fmt_list)
                continue;

            phys_         = pd;
            queue_family_ = i;
            vkGetPhysicalDeviceMemoryProperties(phys_, &mem_props_);
            return true;
        }
    }
    std::fprintf(stderr,
                 "weweb: no suitable Vulkan device "
                 "(need swapchain + external_memory_fd + dma_buf)\n");
    return false;
}

bool VulkanBlitter::CreateDevice() {
    float                   prio = 1.0f;
    VkDeviceQueueCreateInfo qi {};
    qi.sType            = VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO;
    qi.queueFamilyIndex = queue_family_;
    qi.queueCount       = 1;
    qi.pQueuePriorities = &prio;

    const char* dev_exts[] = {
        VK_KHR_SWAPCHAIN_EXTENSION_NAME,
        VK_KHR_EXTERNAL_MEMORY_FD_EXTENSION_NAME,
        VK_EXT_EXTERNAL_MEMORY_DMA_BUF_EXTENSION_NAME,
        // Required by VK_EXT_image_drm_format_modifier on Vulkan 1.1
        // (promoted to core in 1.2). Validation rejects the device otherwise.
        VK_KHR_IMAGE_FORMAT_LIST_EXTENSION_NAME,
        VK_EXT_IMAGE_DRM_FORMAT_MODIFIER_EXTENSION_NAME,
    };
    VkDeviceCreateInfo ci {};
    ci.sType                   = VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO;
    ci.queueCreateInfoCount    = 1;
    ci.pQueueCreateInfos       = &qi;
    ci.enabledExtensionCount   = static_cast<std::uint32_t>(std::size(dev_exts));
    ci.ppEnabledExtensionNames = dev_exts;

    VK_CHECK(vkCreateDevice(phys_, &ci, nullptr, &device_));
    vkGetDeviceQueue(device_, queue_family_, 0, &queue_);

    pfn_GetMemoryFdProperties_ = reinterpret_cast<PFN_vkGetMemoryFdPropertiesKHR>(
        vkGetDeviceProcAddr(device_, "vkGetMemoryFdPropertiesKHR"));
    if (! pfn_GetMemoryFdProperties_) {
        std::fprintf(stderr, "weweb: vkGetMemoryFdPropertiesKHR not available\n");
        return false;
    }
    return true;
}

bool VulkanBlitter::CreateCommandPool() {
    VkCommandPoolCreateInfo ci {};
    ci.sType            = VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO;
    ci.flags            = VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT;
    ci.queueFamilyIndex = queue_family_;
    VK_CHECK(vkCreateCommandPool(device_, &ci, nullptr, &cmd_pool_));

    VkCommandBufferAllocateInfo ai {};
    ai.sType              = VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO;
    ai.commandPool        = cmd_pool_;
    ai.level              = VK_COMMAND_BUFFER_LEVEL_PRIMARY;
    ai.commandBufferCount = kMaxFramesInFlight;
    VK_CHECK(vkAllocateCommandBuffers(device_, &ai, cmd_bufs_));
    return true;
}

bool VulkanBlitter::CreateSwapchain() {
    VkSurfaceCapabilitiesKHR caps {};
    VK_CHECK(vkGetPhysicalDeviceSurfaceCapabilitiesKHR(phys_, surface_, &caps));

    std::uint32_t fcount = 0;
    VK_CHECK(vkGetPhysicalDeviceSurfaceFormatsKHR(phys_, surface_, &fcount, nullptr));
    std::vector<VkSurfaceFormatKHR> formats(fcount);
    VK_CHECK(vkGetPhysicalDeviceSurfaceFormatsKHR(phys_, surface_, &fcount, formats.data()));

    swap_format_     = formats[0].format;
    swap_colorspace_ = formats[0].colorSpace;
    for (auto& f : formats) {
        if (f.format == VK_FORMAT_B8G8R8A8_UNORM) {
            swap_format_     = f.format;
            swap_colorspace_ = f.colorSpace;
            break;
        }
    }
    present_mode_ = VK_PRESENT_MODE_FIFO_KHR;

    if (caps.currentExtent.width != std::numeric_limits<std::uint32_t>::max()) {
        extent_ = caps.currentExtent;
    } else {
        int w = 0, h = 0;
        glfwGetFramebufferSize(window_, &w, &h);
        extent_.width = std::clamp<std::uint32_t>(
            static_cast<std::uint32_t>(w), caps.minImageExtent.width, caps.maxImageExtent.width);
        extent_.height = std::clamp<std::uint32_t>(
            static_cast<std::uint32_t>(h), caps.minImageExtent.height, caps.maxImageExtent.height);
    }
    if (extent_.width == 0 || extent_.height == 0) return true;

    std::uint32_t image_count = caps.minImageCount + 1;
    if (caps.maxImageCount > 0 && image_count > caps.maxImageCount) {
        image_count = caps.maxImageCount;
    }

    VkSwapchainCreateInfoKHR ci {};
    ci.sType            = VK_STRUCTURE_TYPE_SWAPCHAIN_CREATE_INFO_KHR;
    ci.surface          = surface_;
    ci.minImageCount    = image_count;
    ci.imageFormat      = swap_format_;
    ci.imageColorSpace  = swap_colorspace_;
    ci.imageExtent      = extent_;
    ci.imageArrayLayers = 1;
    ci.imageUsage       = VK_IMAGE_USAGE_TRANSFER_DST_BIT | VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT;
    ci.imageSharingMode = VK_SHARING_MODE_EXCLUSIVE;
    ci.preTransform     = caps.currentTransform;
    ci.compositeAlpha   = VK_COMPOSITE_ALPHA_OPAQUE_BIT_KHR;
    ci.presentMode      = present_mode_;
    ci.clipped          = VK_TRUE;
    ci.oldSwapchain     = VK_NULL_HANDLE;

    VK_CHECK(vkCreateSwapchainKHR(device_, &ci, nullptr, &swapchain_));

    std::uint32_t scount = 0;
    VK_CHECK(vkGetSwapchainImagesKHR(device_, swapchain_, &scount, nullptr));
    swap_images_.resize(scount);
    VK_CHECK(vkGetSwapchainImagesKHR(device_, swapchain_, &scount, swap_images_.data()));

    // Per-image render-done semaphore (see hpp comment).
    VkSemaphoreCreateInfo si {};
    si.sType = VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO;
    render_done_sem_.resize(scount, VK_NULL_HANDLE);
    for (std::uint32_t i = 0; i < scount; ++i) {
        VK_CHECK(vkCreateSemaphore(device_, &si, nullptr, &render_done_sem_[i]));
    }
    return true;
}

void VulkanBlitter::DestroySwapchain() {
    for (auto& s : render_done_sem_)
        if (s) vkDestroySemaphore(device_, s, nullptr);
    render_done_sem_.clear();
    if (swapchain_ != VK_NULL_HANDLE) {
        vkDestroySwapchainKHR(device_, swapchain_, nullptr);
        swapchain_ = VK_NULL_HANDLE;
    }
    swap_images_.clear();
}

bool VulkanBlitter::CreateSyncObjects() {
    VkSemaphoreCreateInfo si {};
    si.sType = VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO;
    VkFenceCreateInfo fi {};
    fi.sType = VK_STRUCTURE_TYPE_FENCE_CREATE_INFO;
    fi.flags = VK_FENCE_CREATE_SIGNALED_BIT;

    // render_done_sem_ is created per swapchain image in CreateSwapchain.
    for (std::uint32_t i = 0; i < kMaxFramesInFlight; ++i) {
        VK_CHECK(vkCreateSemaphore(device_, &si, nullptr, &img_avail_sem_[i]));
        VK_CHECK(vkCreateFence(device_, &fi, nullptr, &in_flight_fence_[i]));
    }
    return true;
}

bool VulkanBlitter::Resize() {
    if (! device_) return false;
    vkDeviceWaitIdle(device_);
    DestroySwapchain();
    if (! CreateSwapchain()) return false;
    return true;
}

std::uint32_t VulkanBlitter::FindMemoryType(std::uint32_t         type_bits,
                                            VkMemoryPropertyFlags props) const {
    for (std::uint32_t i = 0; i < mem_props_.memoryTypeCount; ++i) {
        if ((type_bits & (1u << i)) && (mem_props_.memoryTypes[i].propertyFlags & props) == props) {
            return i;
        }
    }
    return UINT32_MAX;
}

bool VulkanBlitter::EnsureOwnedImage(int width, int height) {
    if (owned_image_ != VK_NULL_HANDLE && owned_width_ == width && owned_height_ == height) {
        return true;
    }
    DestroyOwnedImage();

    VkImageCreateInfo ii {};
    ii.sType         = VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO;
    ii.imageType     = VK_IMAGE_TYPE_2D;
    ii.format        = VK_FORMAT_B8G8R8A8_UNORM;
    ii.extent        = { static_cast<std::uint32_t>(width), static_cast<std::uint32_t>(height), 1 };
    ii.mipLevels     = 1;
    ii.arrayLayers   = 1;
    ii.samples       = VK_SAMPLE_COUNT_1_BIT;
    ii.tiling        = VK_IMAGE_TILING_OPTIMAL;
    ii.usage         = VK_IMAGE_USAGE_TRANSFER_DST_BIT | VK_IMAGE_USAGE_TRANSFER_SRC_BIT;
    ii.sharingMode   = VK_SHARING_MODE_EXCLUSIVE;
    ii.initialLayout = VK_IMAGE_LAYOUT_UNDEFINED;
    VK_CHECK(vkCreateImage(device_, &ii, nullptr, &owned_image_));

    VkMemoryRequirements mr {};
    vkGetImageMemoryRequirements(device_, owned_image_, &mr);
    VkMemoryAllocateInfo mi {};
    mi.sType           = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO;
    mi.allocationSize  = mr.size;
    mi.memoryTypeIndex = FindMemoryType(mr.memoryTypeBits, VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT);
    if (mi.memoryTypeIndex == UINT32_MAX) {
        std::fprintf(stderr, "weweb: no DEVICE_LOCAL memory type for owned image\n");
        return false;
    }
    VK_CHECK(vkAllocateMemory(device_, &mi, nullptr, &owned_image_mem_));
    VK_CHECK(vkBindImageMemory(device_, owned_image_, owned_image_mem_, 0));

    owned_width_    = width;
    owned_height_   = height;
    owned_layout_   = VK_IMAGE_LAYOUT_UNDEFINED;
    owned_has_data_ = false;
    return true;
}

void VulkanBlitter::DestroyOwnedImage() {
    if (owned_image_ != VK_NULL_HANDLE) {
        vkDestroyImage(device_, owned_image_, nullptr);
        owned_image_ = VK_NULL_HANDLE;
    }
    if (owned_image_mem_ != VK_NULL_HANDLE) {
        vkFreeMemory(device_, owned_image_mem_, nullptr);
        owned_image_mem_ = VK_NULL_HANDLE;
    }
    owned_width_ = owned_height_ = 0;
    owned_layout_                = VK_IMAGE_LAYOUT_UNDEFINED;
    owned_has_data_              = false;
}

bool VulkanBlitter::AcceptDmaBuf(const DmaBufFrame& frame) {
    if (frame.plane_count < 1) return false;
    if (frame.coded_width <= 0 || frame.coded_height <= 0) return false;

    // Wait for *any* in-flight GPU work on owned_image_ from a prior
    // RenderFrame blit to finish. Single-threaded design, sub-ms cost.
    if (device_) vkDeviceWaitIdle(device_);

    if (! EnsureOwnedImage(frame.coded_width, frame.coded_height)) return false;

    // Dup the FD — vkAllocateMemory consumes ownership on success and
    // CEF reclaims its own FD when the callback returns.
    int dup_fd = ::dup(frame.planes[0].fd);
    if (dup_fd < 0) {
        std::fprintf(stderr, "weweb: dup(dmabuf fd) failed\n");
        return false;
    }

    // Look up which Vulkan memory types are valid for this FD.
    VkMemoryFdPropertiesKHR fd_props {};
    fd_props.sType = VK_STRUCTURE_TYPE_MEMORY_FD_PROPERTIES_KHR;
    VkResult fdr   = pfn_GetMemoryFdProperties_(
        device_, VK_EXTERNAL_MEMORY_HANDLE_TYPE_DMA_BUF_BIT_EXT, dup_fd, &fd_props);
    if (fdr != VK_SUCCESS) {
        std::fprintf(stderr, "weweb: vkGetMemoryFdPropertiesKHR=%d\n", static_cast<int>(fdr));
        ::close(dup_fd);
        return false;
    }

    // Create a temporary image that references the imported memory.
    // VK_EXT_image_drm_format_modifier is required on Mesa radv: a plain
    // TILING_LINEAR image with VK_EXT_external_memory_dma_buf isn't
    // enough — radv needs the explicit modifier + plane layout to map
    // the imported pages into the GPU page table correctly. CEF's
    // INVALID modifier (= no negotiated modifier; in practice the
    // implementation picked LINEAR with stride matching width*bpp) is
    // substituted with DRM_FORMAT_MOD_LINEAR (0).
    constexpr uint64_t DRM_FORMAT_MOD_INVALID = 0x00ffffffffffffffULL;
    constexpr uint64_t DRM_FORMAT_MOD_LINEAR  = 0x0;
    uint64_t           modifier =
        (frame.modifier == DRM_FORMAT_MOD_INVALID) ? DRM_FORMAT_MOD_LINEAR : frame.modifier;

    // VUID-VkImageDrmFormatModifierExplicitCreateInfoEXT-size-02267:
    // size must be 0; driver derives it from rowPitch + extent + format.
    VkSubresourceLayout plane_layout {};
    plane_layout.offset     = frame.planes[0].offset;
    plane_layout.rowPitch   = frame.planes[0].stride;
    plane_layout.arrayPitch = 0;
    plane_layout.depthPitch = 0;

    VkImageDrmFormatModifierExplicitCreateInfoEXT mod_info {};
    mod_info.sType = VK_STRUCTURE_TYPE_IMAGE_DRM_FORMAT_MODIFIER_EXPLICIT_CREATE_INFO_EXT;
    mod_info.drmFormatModifier           = modifier;
    mod_info.drmFormatModifierPlaneCount = 1;
    mod_info.pPlaneLayouts               = &plane_layout;

    VkExternalMemoryImageCreateInfo ext_img {};
    ext_img.sType       = VK_STRUCTURE_TYPE_EXTERNAL_MEMORY_IMAGE_CREATE_INFO;
    ext_img.pNext       = &mod_info;
    ext_img.handleTypes = VK_EXTERNAL_MEMORY_HANDLE_TYPE_DMA_BUF_BIT_EXT;

    VkImageCreateInfo ii {};
    ii.sType         = VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO;
    ii.pNext         = &ext_img;
    ii.imageType     = VK_IMAGE_TYPE_2D;
    ii.format        = FormatToVk(frame.format);
    ii.extent        = { static_cast<std::uint32_t>(frame.coded_width),
                         static_cast<std::uint32_t>(frame.coded_height),
                         1 };
    ii.mipLevels     = 1;
    ii.arrayLayers   = 1;
    ii.samples       = VK_SAMPLE_COUNT_1_BIT;
    ii.tiling        = VK_IMAGE_TILING_DRM_FORMAT_MODIFIER_EXT;
    ii.usage         = VK_IMAGE_USAGE_TRANSFER_SRC_BIT;
    ii.sharingMode   = VK_SHARING_MODE_EXCLUSIVE;
    ii.initialLayout = VK_IMAGE_LAYOUT_UNDEFINED;

    VkImage temp_img = VK_NULL_HANDLE;
    if (vkCreateImage(device_, &ii, nullptr, &temp_img) != VK_SUCCESS) {
        std::fprintf(stderr, "weweb: vkCreateImage(temp dma-buf) failed\n");
        ::close(dup_fd);
        return false;
    }

    VkMemoryRequirements mr {};
    vkGetImageMemoryRequirements(device_, temp_img, &mr);

    // Memory type must satisfy *both* the image's requirements AND the
    // FD's allowable types. ANDing memoryTypeBits accomplishes that.
    std::uint32_t allowed = mr.memoryTypeBits & fd_props.memoryTypeBits;
    std::uint32_t mtype   = UINT32_MAX;
    for (std::uint32_t i = 0; i < mem_props_.memoryTypeCount; ++i) {
        if (allowed & (1u << i)) {
            mtype = i;
            break;
        }
    }
    if (mtype == UINT32_MAX) {
        std::fprintf(stderr, "weweb: no compatible memory type for DMA-BUF\n");
        vkDestroyImage(device_, temp_img, nullptr);
        ::close(dup_fd);
        return false;
    }

    // Dedicated allocation for an imported DMA-BUF is the safe path.
    VkMemoryDedicatedAllocateInfo dedicated {};
    dedicated.sType = VK_STRUCTURE_TYPE_MEMORY_DEDICATED_ALLOCATE_INFO;
    dedicated.image = temp_img;

    VkImportMemoryFdInfoKHR import {};
    import.sType      = VK_STRUCTURE_TYPE_IMPORT_MEMORY_FD_INFO_KHR;
    import.pNext      = &dedicated;
    import.handleType = VK_EXTERNAL_MEMORY_HANDLE_TYPE_DMA_BUF_BIT_EXT;
    import.fd         = dup_fd;

    VkMemoryAllocateInfo mi {};
    mi.sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO;
    mi.pNext = &import;
    // Use the size CEF reported for the plane — Mesa ignores the
    // allocationSize for imports but the spec wants a valid-looking value.
    mi.allocationSize  = frame.planes[0].size > 0 ? frame.planes[0].size : mr.size;
    mi.memoryTypeIndex = mtype;

    VkDeviceMemory imported_mem = VK_NULL_HANDLE;
    VkResult       ar           = vkAllocateMemory(device_, &mi, nullptr, &imported_mem);
    if (ar != VK_SUCCESS) {
        std::fprintf(stderr, "weweb: vkAllocateMemory(import fd)=%d\n", static_cast<int>(ar));
        vkDestroyImage(device_, temp_img, nullptr);
        ::close(dup_fd);
        return false;
    }
    // From here on Vulkan owns the FD; we MUST NOT close dup_fd.

    if (vkBindImageMemory(device_, temp_img, imported_mem, frame.planes[0].offset) != VK_SUCCESS) {
        std::fprintf(stderr, "weweb: vkBindImageMemory(temp) failed\n");
        vkFreeMemory(device_, imported_mem, nullptr);
        vkDestroyImage(device_, temp_img, nullptr);
        return false;
    }

    // Copy temp_img → owned_image_. Use cmd_bufs_[0] as a scratch
    // buffer; we wait for it inline.
    VkCommandBuffer cmd = cmd_bufs_[0];
    vkResetCommandBuffer(cmd, 0);
    VkCommandBufferBeginInfo bi {};
    bi.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO;
    bi.flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;
    if (vkBeginCommandBuffer(cmd, &bi) != VK_SUCCESS) {
        vkFreeMemory(device_, imported_mem, nullptr);
        vkDestroyImage(device_, temp_img, nullptr);
        return false;
    }

    // temp_img: UNDEFINED → TRANSFER_SRC_OPTIMAL.
    VkImageMemoryBarrier b_src {};
    b_src.sType                       = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER;
    b_src.oldLayout                   = VK_IMAGE_LAYOUT_UNDEFINED;
    b_src.newLayout                   = VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL;
    b_src.srcQueueFamilyIndex         = VK_QUEUE_FAMILY_IGNORED;
    b_src.dstQueueFamilyIndex         = VK_QUEUE_FAMILY_IGNORED;
    b_src.image                       = temp_img;
    b_src.subresourceRange.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
    b_src.subresourceRange.levelCount = 1;
    b_src.subresourceRange.layerCount = 1;
    b_src.srcAccessMask               = 0;
    b_src.dstAccessMask               = VK_ACCESS_TRANSFER_READ_BIT;
    vkCmdPipelineBarrier(cmd,
                         VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT,
                         VK_PIPELINE_STAGE_TRANSFER_BIT,
                         0,
                         0,
                         nullptr,
                         0,
                         nullptr,
                         1,
                         &b_src);

    // owned_image_: <prev_layout> → TRANSFER_DST_OPTIMAL.
    VkImageMemoryBarrier b_dst = b_src;
    b_dst.oldLayout            = owned_layout_;
    b_dst.newLayout            = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL;
    b_dst.image                = owned_image_;
    b_dst.srcAccessMask        = 0;
    b_dst.dstAccessMask        = VK_ACCESS_TRANSFER_WRITE_BIT;
    vkCmdPipelineBarrier(cmd,
                         VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT,
                         VK_PIPELINE_STAGE_TRANSFER_BIT,
                         0,
                         0,
                         nullptr,
                         0,
                         nullptr,
                         1,
                         &b_dst);

    VkImageCopy region {};
    region.srcSubresource.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
    region.srcSubresource.layerCount = 1;
    region.dstSubresource.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
    region.dstSubresource.layerCount = 1;
    region.extent                    = { static_cast<std::uint32_t>(frame.coded_width),
                                         static_cast<std::uint32_t>(frame.coded_height),
                                         1 };
    vkCmdCopyImage(cmd,
                   temp_img,
                   VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
                   owned_image_,
                   VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
                   1,
                   &region);

    // owned_image_: TRANSFER_DST → TRANSFER_SRC (so RenderFrame can read).
    VkImageMemoryBarrier b_owned_src {};
    b_owned_src.sType                       = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER;
    b_owned_src.oldLayout                   = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL;
    b_owned_src.newLayout                   = VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL;
    b_owned_src.srcQueueFamilyIndex         = VK_QUEUE_FAMILY_IGNORED;
    b_owned_src.dstQueueFamilyIndex         = VK_QUEUE_FAMILY_IGNORED;
    b_owned_src.image                       = owned_image_;
    b_owned_src.subresourceRange.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
    b_owned_src.subresourceRange.levelCount = 1;
    b_owned_src.subresourceRange.layerCount = 1;
    b_owned_src.srcAccessMask               = VK_ACCESS_TRANSFER_WRITE_BIT;
    b_owned_src.dstAccessMask               = VK_ACCESS_TRANSFER_READ_BIT;
    vkCmdPipelineBarrier(cmd,
                         VK_PIPELINE_STAGE_TRANSFER_BIT,
                         VK_PIPELINE_STAGE_TRANSFER_BIT,
                         0,
                         0,
                         nullptr,
                         0,
                         nullptr,
                         1,
                         &b_owned_src);

    if (vkEndCommandBuffer(cmd) != VK_SUCCESS) {
        vkFreeMemory(device_, imported_mem, nullptr);
        vkDestroyImage(device_, temp_img, nullptr);
        return false;
    }

    VkSubmitInfo submit {};
    submit.sType              = VK_STRUCTURE_TYPE_SUBMIT_INFO;
    submit.commandBufferCount = 1;
    submit.pCommandBuffers    = &cmd;

    // Use the in_flight_fence_[0] which CreateSyncObjects signaled
    // initially. Reset, submit, then wait — must be done before this
    // function returns since CEF reclaims the FD.
    vkResetFences(device_, 1, &in_flight_fence_[0]);
    if (vkQueueSubmit(queue_, 1, &submit, in_flight_fence_[0]) != VK_SUCCESS) {
        std::fprintf(stderr, "weweb: vkQueueSubmit(import-copy) failed\n");
        vkFreeMemory(device_, imported_mem, nullptr);
        vkDestroyImage(device_, temp_img, nullptr);
        return false;
    }
    vkWaitForFences(device_, 1, &in_flight_fence_[0], VK_TRUE, kFenceTimeoutNs);

    vkDestroyImage(device_, temp_img, nullptr);
    vkFreeMemory(device_, imported_mem, nullptr);

    owned_layout_   = VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL;
    owned_has_data_ = true;
    return true;
}

bool VulkanBlitter::RenderFrame() {
    if (! swapchain_ || extent_.width == 0 || extent_.height == 0) {
        return false;
    }

    // Use cmd_bufs_[1] as the present cmd buffer to keep it disjoint
    // from the import-copy buffer at index 0.
    const std::uint32_t fi      = 1;
    VkFence             fence   = in_flight_fence_[fi];
    VkSemaphore         img_sem = img_avail_sem_[fi];
    VkCommandBuffer     cmd     = cmd_bufs_[fi];

    vkWaitForFences(device_, 1, &fence, VK_TRUE, kFenceTimeoutNs);

    std::uint32_t img_idx = 0;
    VkResult      acq     = vkAcquireNextImageKHR(
        device_, swapchain_, kFenceTimeoutNs, img_sem, VK_NULL_HANDLE, &img_idx);
    if (acq == VK_ERROR_OUT_OF_DATE_KHR) return false;
    if (acq != VK_SUCCESS && acq != VK_SUBOPTIMAL_KHR) {
        std::fprintf(stderr, "weweb: vkAcquireNextImageKHR=%d\n", static_cast<int>(acq));
        return true;
    }
    VkSemaphore done_sem = render_done_sem_[img_idx];

    vkResetFences(device_, 1, &fence);
    vkResetCommandBuffer(cmd, 0);

    VkCommandBufferBeginInfo bi {};
    bi.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO;
    bi.flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;
    VK_CHECK(vkBeginCommandBuffer(cmd, &bi));

    VkImage swap_img = swap_images_[img_idx];

    // swapchain_img: UNDEFINED → TRANSFER_DST.
    VkImageMemoryBarrier b {};
    b.sType                       = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER;
    b.oldLayout                   = VK_IMAGE_LAYOUT_UNDEFINED;
    b.newLayout                   = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL;
    b.srcQueueFamilyIndex         = VK_QUEUE_FAMILY_IGNORED;
    b.dstQueueFamilyIndex         = VK_QUEUE_FAMILY_IGNORED;
    b.image                       = swap_img;
    b.subresourceRange.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
    b.subresourceRange.levelCount = 1;
    b.subresourceRange.layerCount = 1;
    b.srcAccessMask               = 0;
    b.dstAccessMask               = VK_ACCESS_TRANSFER_WRITE_BIT;
    // srcStage matches the wait dstStage on img_sem (TRANSFER) so the
    // validator can chain "acquire-read → wait → barrier"; using
    // TOP_OF_PIPE produces a SYNC-HAZARD-WRITE-AFTER-READ.
    vkCmdPipelineBarrier(cmd,
                         VK_PIPELINE_STAGE_TRANSFER_BIT,
                         VK_PIPELINE_STAGE_TRANSFER_BIT,
                         0,
                         0,
                         nullptr,
                         0,
                         nullptr,
                         1,
                         &b);

    if (owned_has_data_ && owned_image_ != VK_NULL_HANDLE) {
        VkImageBlit blit {};
        blit.srcSubresource.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
        blit.srcSubresource.layerCount = 1;
        blit.dstSubresource.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
        blit.dstSubresource.layerCount = 1;
        blit.srcOffsets[0]             = { 0, 0, 0 };
        blit.srcOffsets[1]             = { owned_width_, owned_height_, 1 };
        blit.dstOffsets[0]             = { 0, 0, 0 };
        blit.dstOffsets[1]             = { static_cast<int32_t>(extent_.width),
                                           static_cast<int32_t>(extent_.height),
                                           1 };
        vkCmdBlitImage(cmd,
                       owned_image_,
                       VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
                       swap_img,
                       VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
                       1,
                       &blit,
                       VK_FILTER_LINEAR);
    } else {
        VkClearColorValue       clear {};
        VkImageSubresourceRange r {};
        r.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
        r.levelCount = 1;
        r.layerCount = 1;
        vkCmdClearColorImage(cmd, swap_img, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, &clear, 1, &r);
    }

    // swapchain_img: TRANSFER_DST → PRESENT_SRC.
    VkImageMemoryBarrier bp = b;
    bp.oldLayout            = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL;
    bp.newLayout            = VK_IMAGE_LAYOUT_PRESENT_SRC_KHR;
    bp.srcAccessMask        = VK_ACCESS_TRANSFER_WRITE_BIT;
    bp.dstAccessMask        = 0;
    vkCmdPipelineBarrier(cmd,
                         VK_PIPELINE_STAGE_TRANSFER_BIT,
                         VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT,
                         0,
                         0,
                         nullptr,
                         0,
                         nullptr,
                         1,
                         &bp);

    VK_CHECK(vkEndCommandBuffer(cmd));

    VkPipelineStageFlags wait_stage = VK_PIPELINE_STAGE_TRANSFER_BIT;
    VkSubmitInfo         submit {};
    submit.sType                = VK_STRUCTURE_TYPE_SUBMIT_INFO;
    submit.waitSemaphoreCount   = 1;
    submit.pWaitSemaphores      = &img_sem;
    submit.pWaitDstStageMask    = &wait_stage;
    submit.commandBufferCount   = 1;
    submit.pCommandBuffers      = &cmd;
    submit.signalSemaphoreCount = 1;
    submit.pSignalSemaphores    = &done_sem;
    VK_CHECK(vkQueueSubmit(queue_, 1, &submit, fence));

    VkPresentInfoKHR present {};
    present.sType              = VK_STRUCTURE_TYPE_PRESENT_INFO_KHR;
    present.waitSemaphoreCount = 1;
    present.pWaitSemaphores    = &done_sem;
    present.swapchainCount     = 1;
    present.pSwapchains        = &swapchain_;
    present.pImageIndices      = &img_idx;

    VkResult pres = vkQueuePresentKHR(queue_, &present);
    if (pres == VK_ERROR_OUT_OF_DATE_KHR || pres == VK_SUBOPTIMAL_KHR) {
        return false;
    }
    return true;
}

} // namespace weweb
