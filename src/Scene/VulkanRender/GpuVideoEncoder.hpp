#pragma once

#include <vulkan/vulkan.h>
#include <cstdint>
#include <memory>
#include <span>
#include <string>
#include <vector>

namespace owe {

struct GpuCaptureOptions {
    bool collect_bounds { false }, bounds_include_rgb { false };
    std::uint64_t encoded_frames { UINT64_MAX };
    std::vector<std::uint64_t> retain_frames;
    std::uint32_t crossfade_frames { 0 };
    std::uint32_t crop_x { 0 }, crop_y { 0 }, crop_width { 0 }, crop_height { 0 };
    bool retain_loop_window { false };
};

// Owns the codec and conversion resources, never the renderer's Vulkan device.
// Frames remain on that device; only compressed packets are mapped by FFmpeg.
class GpuVideoEncoder {
public:
    GpuVideoEncoder(VkInstance, VkPhysicalDevice, VkDevice, std::uint32_t graphics_family,
                    std::span<const std::string> instance_extensions,
                    std::span<const std::string> device_extensions,
                    std::uint32_t width, std::uint32_t height, bool packed_alpha,
                    std::uint32_t fps_num, std::uint32_t fps_den, int qp,
                    const std::string& codec, const std::string& path, GpuCaptureOptions capture = {});
    ~GpuVideoEncoder();
    GpuVideoEncoder(const GpuVideoEncoder&) = delete;
    GpuVideoEncoder& operator=(const GpuVideoEncoder&) = delete;
    // Input arrives and leaves in TRANSFER_SRC_OPTIMAL. Asynchronous input must
    // have its render submission ordered before conversion on this graphics queue.
    void encode(VkImage rgba, std::uint64_t index, bool asynchronous = false);
    void waitConversion();
    void finish();
    std::string captureMetadata() const;
    std::uint64_t readbackFrames() const;
private:
    struct Impl;
    std::unique_ptr<Impl> impl;
};
}
