#pragma once

#include <vulkan/vulkan.h>
#include <cstdint>
#include <memory>
#include <span>
#include <string>

namespace owe {

// Owns the codec and conversion resources, never the renderer's Vulkan device.
// Frames remain on that device; only compressed packets are mapped by FFmpeg.
class GpuVideoEncoder {
public:
    GpuVideoEncoder(VkInstance, VkPhysicalDevice, VkDevice, std::uint32_t graphics_family,
                    std::span<const std::string> instance_extensions,
                    std::span<const std::string> device_extensions,
                    std::uint32_t width, std::uint32_t height, bool packed_alpha,
                    std::uint32_t fps_num, std::uint32_t fps_den, int qp,
                    const std::string& codec, const std::string& path);
    ~GpuVideoEncoder();
    GpuVideoEncoder(const GpuVideoEncoder&) = delete;
    GpuVideoEncoder& operator=(const GpuVideoEncoder&) = delete;
    // Input arrives and leaves in TRANSFER_SRC_OPTIMAL after its render fence.
    void encode(VkImage rgba, std::uint64_t index);
    void finish();
private:
    struct Impl;
    std::unique_ptr<Impl> impl;
};
}
