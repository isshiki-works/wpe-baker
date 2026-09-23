#pragma once
// NV12 转换器 GPU 单测的公共夹具：裸 Vulkan 设备（Vulkan 1.1 + VK_KHR_timeline_semaphore，
// wavsen 对拍要用时间线）、视频纹理目标图像（同 TextureCache::CreateVideoTex）与确定性 NV12。

#include <vulkan/vulkan.h>

#include <cstdint>
#include <string>
#include <vector>

namespace nv12_test
{

struct Gpu {
    VkInstance       instance { VK_NULL_HANDLE };
    VkPhysicalDevice phys { VK_NULL_HANDLE };
    VkDevice         device { VK_NULL_HANDLE };
    std::uint32_t    family { UINT32_MAX };
    VkQueue          queue { VK_NULL_HANDLE };
    VkCommandPool    pool { VK_NULL_HANDLE };
    VkCommandBuffer  cmd { VK_NULL_HANDLE };
    std::string      skip;

    Gpu() {
        const VkApplicationInfo app { .sType      = VK_STRUCTURE_TYPE_APPLICATION_INFO,
                                      .apiVersion = VK_API_VERSION_1_1 };
        const VkInstanceCreateInfo ici { .sType            = VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO,
                                         .pApplicationInfo = &app };
        if (vkCreateInstance(&ici, nullptr, &instance) != VK_SUCCESS) {
            skip = "vkCreateInstance failed";
            return;
        }
        std::uint32_t count = 0;
        vkEnumeratePhysicalDevices(instance, &count, nullptr);
        std::vector<VkPhysicalDevice> devices(count);
        vkEnumeratePhysicalDevices(instance, &count, devices.data());
        for (auto candidate : devices) {
            std::uint32_t qn = 0;
            vkGetPhysicalDeviceQueueFamilyProperties(candidate, &qn, nullptr);
            std::vector<VkQueueFamilyProperties> qs(qn);
            vkGetPhysicalDeviceQueueFamilyProperties(candidate, &qn, qs.data());
            for (std::uint32_t i = 0; i < qn; ++i) {
                if ((qs[i].queueFlags & VK_QUEUE_GRAPHICS_BIT) &&
                    (qs[i].queueFlags & VK_QUEUE_COMPUTE_BIT)) {
                    phys   = candidate;
                    family = i;
                    break;
                }
            }
            if (phys) break;
        }
        if (! phys) {
            skip = "no graphics+compute queue";
            return;
        }
        const float priority = 1.0f;
        const VkDeviceQueueCreateInfo qci { .sType            = VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO,
                                            .queueFamilyIndex = family,
                                            .queueCount       = 1,
                                            .pQueuePriorities = &priority };
        VkPhysicalDeviceTimelineSemaphoreFeaturesKHR timeline {
            .sType             = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_TIMELINE_SEMAPHORE_FEATURES_KHR,
            .timelineSemaphore = VK_TRUE,
        };
        const char*              ext = VK_KHR_TIMELINE_SEMAPHORE_EXTENSION_NAME;
        const VkDeviceCreateInfo dci { .sType                   = VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO,
                                       .pNext                   = &timeline,
                                       .queueCreateInfoCount    = 1,
                                       .pQueueCreateInfos       = &qci,
                                       .enabledExtensionCount   = 1,
                                       .ppEnabledExtensionNames = &ext };
        if (vkCreateDevice(phys, &dci, nullptr, &device) != VK_SUCCESS) {
            skip = "vkCreateDevice(timeline semaphore) failed";
            return;
        }
        vkGetDeviceQueue(device, family, 0, &queue);
        const VkCommandPoolCreateInfo pci { .sType = VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO,
                                            .flags = VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT,
                                            .queueFamilyIndex = family };
        vkCreateCommandPool(device, &pci, nullptr, &pool);
        const VkCommandBufferAllocateInfo cai { .sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO,
                                                .commandPool        = pool,
                                                .level              = VK_COMMAND_BUFFER_LEVEL_PRIMARY,
                                                .commandBufferCount = 1 };
        vkAllocateCommandBuffers(device, &cai, &cmd);
    }

    ~Gpu() {
        if (device) {
            vkDeviceWaitIdle(device);
            vkDestroyCommandPool(device, pool, nullptr);
            vkDestroyDevice(device, nullptr);
        }
        if (instance) vkDestroyInstance(instance, nullptr);
    }

    std::uint32_t MemoryType(std::uint32_t bits, VkMemoryPropertyFlags want) const {
        VkPhysicalDeviceMemoryProperties mp {};
        vkGetPhysicalDeviceMemoryProperties(phys, &mp);
        for (std::uint32_t i = 0; i < mp.memoryTypeCount; ++i) {
            if ((bits & (1u << i)) && (mp.memoryTypes[i].propertyFlags & want) == want) return i;
        }
        return UINT32_MAX;
    }

    template<typename F>
    void Submit(F&& record) {
        vkResetCommandBuffer(cmd, 0);
        const VkCommandBufferBeginInfo bi { .sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO,
                                            .flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT };
        vkBeginCommandBuffer(cmd, &bi);
        record(cmd);
        vkEndCommandBuffer(cmd);
        const VkSubmitInfo si { .sType              = VK_STRUCTURE_TYPE_SUBMIT_INFO,
                                .commandBufferCount = 1,
                                .pCommandBuffers    = &cmd };
        vkQueueSubmit(queue, 1, &si, VK_NULL_HANDLE);
        vkQueueWaitIdle(queue);
    }
};

inline void ImageBarrier(VkCommandBuffer cmd, VkImage image, VkAccessFlags src_access,
                  VkAccessFlags dst_access, VkImageLayout from, VkImageLayout to,
                  VkPipelineStageFlags src_stage, VkPipelineStageFlags dst_stage) {
    const VkImageMemoryBarrier b { .sType               = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER,
                                   .srcAccessMask       = src_access,
                                   .dstAccessMask       = dst_access,
                                   .oldLayout           = from,
                                   .newLayout           = to,
                                   .srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
                                   .dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
                                   .image               = image,
                                   .subresourceRange    = { VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1 } };
    vkCmdPipelineBarrier(cmd, src_stage, dst_stage, 0, 0, nullptr, 0, nullptr, 1, &b);
}

// 视频纹理目标：同 TextureCache::CreateVideoTex，RGBA8 清成不透明黑后停在 SHADER_READ_ONLY。
struct Target {
    Gpu&           gpu;
    std::uint32_t  w, h;
    VkImage        image { VK_NULL_HANDLE };
    VkDeviceMemory memory { VK_NULL_HANDLE };

    Target(Gpu& g, std::uint32_t width, std::uint32_t height): gpu(g), w(width), h(height) {
        const VkImageCreateInfo ici {
            .sType       = VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO,
            .imageType   = VK_IMAGE_TYPE_2D,
            .format      = VK_FORMAT_R8G8B8A8_UNORM,
            .extent      = { w, h, 1 },
            .mipLevels   = 1,
            .arrayLayers = 1,
            .samples     = VK_SAMPLE_COUNT_1_BIT,
            .tiling      = VK_IMAGE_TILING_OPTIMAL,
            .usage = VK_IMAGE_USAGE_TRANSFER_DST_BIT | VK_IMAGE_USAGE_TRANSFER_SRC_BIT |
                     VK_IMAGE_USAGE_STORAGE_BIT | VK_IMAGE_USAGE_SAMPLED_BIT,
            .sharingMode   = VK_SHARING_MODE_EXCLUSIVE,
            .initialLayout = VK_IMAGE_LAYOUT_UNDEFINED,
        };
        vkCreateImage(gpu.device, &ici, nullptr, &image);
        VkMemoryRequirements mr {};
        vkGetImageMemoryRequirements(gpu.device, image, &mr);
        const VkMemoryAllocateInfo mai {
            .sType           = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO,
            .allocationSize  = mr.size,
            .memoryTypeIndex = gpu.MemoryType(mr.memoryTypeBits, VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT),
        };
        vkAllocateMemory(gpu.device, &mai, nullptr, &memory);
        vkBindImageMemory(gpu.device, image, memory, 0);
        gpu.Submit([&](VkCommandBuffer cmd) {
            const VkImageSubresourceRange range { VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1 };
            ImageBarrier(cmd,
                         image,
                         0,
                         VK_ACCESS_TRANSFER_WRITE_BIT,
                         VK_IMAGE_LAYOUT_UNDEFINED,
                         VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
                         VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT,
                         VK_PIPELINE_STAGE_TRANSFER_BIT);
            const VkClearColorValue black { .float32 = { 0.0f, 0.0f, 0.0f, 1.0f } };
            vkCmdClearColorImage(cmd, image, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, &black, 1, &range);
            ImageBarrier(cmd,
                         image,
                         VK_ACCESS_TRANSFER_WRITE_BIT,
                         VK_ACCESS_SHADER_READ_BIT,
                         VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
                         VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL,
                         VK_PIPELINE_STAGE_TRANSFER_BIT,
                         VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT);
        });
    }

    ~Target() {
        vkDeviceWaitIdle(gpu.device);
        vkDestroyImage(gpu.device, image, nullptr);
        vkFreeMemory(gpu.device, memory, nullptr);
    }

    std::vector<std::uint8_t> Read() {
        vkQueueWaitIdle(gpu.queue); // 等转换器的提交
        const VkDeviceSize       bytes = VkDeviceSize(w) * h * 4;
        const VkBufferCreateInfo bci { .sType = VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO,
                                       .size  = bytes,
                                       .usage = VK_BUFFER_USAGE_TRANSFER_DST_BIT };
        VkBuffer buffer = VK_NULL_HANDLE;
        vkCreateBuffer(gpu.device, &bci, nullptr, &buffer);
        VkMemoryRequirements mr {};
        vkGetBufferMemoryRequirements(gpu.device, buffer, &mr);
        const VkMemoryAllocateInfo mai {
            .sType           = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO,
            .allocationSize  = mr.size,
            .memoryTypeIndex = gpu.MemoryType(mr.memoryTypeBits,
                                              VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT |
                                                  VK_MEMORY_PROPERTY_HOST_COHERENT_BIT),
        };
        VkDeviceMemory mem = VK_NULL_HANDLE;
        vkAllocateMemory(gpu.device, &mai, nullptr, &mem);
        vkBindBufferMemory(gpu.device, buffer, mem, 0);
        gpu.Submit([&](VkCommandBuffer cmd) {
            ImageBarrier(cmd,
                         image,
                         VK_ACCESS_SHADER_READ_BIT,
                         VK_ACCESS_TRANSFER_READ_BIT,
                         VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL,
                         VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
                         VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT,
                         VK_PIPELINE_STAGE_TRANSFER_BIT);
            const VkBufferImageCopy region { .imageSubresource = { VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, 1 },
                                             .imageExtent      = { w, h, 1 } };
            vkCmdCopyImageToBuffer(
                cmd, image, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, buffer, 1, &region);
            ImageBarrier(cmd,
                         image,
                         VK_ACCESS_TRANSFER_READ_BIT,
                         VK_ACCESS_SHADER_READ_BIT,
                         VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
                         VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL,
                         VK_PIPELINE_STAGE_TRANSFER_BIT,
                         VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT);
        });
        void* mapped = nullptr;
        vkMapMemory(gpu.device, mem, 0, VK_WHOLE_SIZE, 0, &mapped);
        std::vector<std::uint8_t> out(static_cast<const std::uint8_t*>(mapped),
                                      static_cast<const std::uint8_t*>(mapped) + bytes);
        vkUnmapMemory(gpu.device, mem);
        vkDestroyBuffer(gpu.device, buffer, nullptr);
        vkFreeMemory(gpu.device, mem, nullptr);
        return out;
    }
};

// NV12 字节数：Y 平面 w×h，UV 平面 ceil(w/2)×ceil(h/2) 个 (U,V) 对。
inline std::size_t Nv12Size(std::uint32_t w, std::uint32_t h) {
    return std::size_t(w) * h + std::size_t((w + 1) / 2) * ((h + 1) / 2) * 2;
}

// 确定性的 NV12：一半随机字节、一半平滑渐变，覆盖 0/255 两端（有限范围的越界值要被钳制）。
inline std::vector<std::uint8_t> MakeNv12(std::uint32_t w, std::uint32_t h, std::uint32_t seed) {
    std::vector<std::uint8_t> nv12(Nv12Size(w, h));
    std::uint32_t             state = seed * 2654435761u + 1u;
    for (std::size_t i = 0; i < nv12.size(); ++i) {
        state = state * 1664525u + 1013904223u;
        const bool noise = ((i / 97) & 1) != 0;
        nv12[i] = noise ? std::uint8_t(state >> 24) : std::uint8_t((i * 7 + seed * 31) & 0xff);
    }
    return nv12;
}

} // namespace nv12_test
