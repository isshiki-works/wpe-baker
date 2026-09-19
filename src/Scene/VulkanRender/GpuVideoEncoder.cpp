#include "GpuVideoEncoder.hpp"
#include <array>
#include <algorithm>
#include <filesystem>
#include <fstream>
#include <sstream>
#include <cstring>
#include <stdexcept>
#include <vector>

extern "C" {
#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>
#include <libavutil/hwcontext.h>
#include <libavutil/hwcontext_vulkan.h>
#include <libavutil/opt.h>
}

import wescene.shader_compile;
import wescene.types;

namespace owe {
using namespace owe::vulkan;
namespace {
void Av(int result, const char* operation) {
    if (result >= 0) return;
    char error[AV_ERROR_MAX_STRING_SIZE] {};
    av_strerror(result, error, sizeof(error));
    throw std::runtime_error(std::string(operation) + ": " + error);
}
void Vk(VkResult result, const char* operation) {
    if (result != VK_SUCCESS)
        throw std::runtime_error(std::string(operation) + ": VkResult=" + std::to_string(result));
}
constexpr std::uint64_t timeout_ns = 30'000'000'000ull;
}

struct GpuVideoEncoder::Impl {
    VkDevice device {};
    VkPhysicalDevice gpu {};
    VkQueue queue {};
    std::uint32_t family {}, width {}, height {}, output_width {}, stride {};
    AVBufferRef *hardware {}, *frames {};
    AVCodecContext* codec {};
    AVFormatContext* mux {};
    AVStream* stream {};
    AVFrame* frame {};
    AVPacket* packet {};
    std::vector<const char*> instance_extensions, device_extensions;
    VkBuffer nv12 {};
    VkDeviceMemory memory {};
    VkCommandPool pool {};
    VkCommandBuffer command {};
    VkFence fence {};
    VkShaderModule shader {};
    VkDescriptorSetLayout descriptors {};
    VkPipelineLayout layout {};
    VkPipeline pipeline {};
    VkImage source_image {};
    VkImageView source_view {};
    GpuCaptureOptions capture;
    std::filesystem::path directory;
    std::ofstream retained;
    VkBuffer statistics {}, readback {};
    VkDeviceMemory statistics_memory {}, readback_memory {}, first_memory {};
    VkImage first_image {};
    VkImageView first_view {};
    void* mapped {};
    std::array<std::uint32_t, 8> bounds {};
    std::uint64_t readbacks {}, retained_count {};
    PFN_vkCmdPushDescriptorSetKHR push_descriptors {};
    bool finished {};

    ~Impl() {
        // On cancellation/error, queued codec work must finish before its resources/device disappear.
        if (device) vkDeviceWaitIdle(device);
        av_packet_free(&packet);
        av_frame_free(&frame);
        avcodec_free_context(&codec);
        av_buffer_unref(&frames);
        av_buffer_unref(&hardware);
        if (mux) { if (mux->pb) avio_closep(&mux->pb); avformat_free_context(mux); }
        if (pipeline) vkDestroyPipeline(device, pipeline, nullptr);
        if (source_view) vkDestroyImageView(device, source_view, nullptr);
        if (first_view) vkDestroyImageView(device, first_view, nullptr);
        if (first_image) vkDestroyImage(device, first_image, nullptr);
        if (first_memory) vkFreeMemory(device, first_memory, nullptr);
        if (statistics) vkDestroyBuffer(device, statistics, nullptr);
        if (statistics_memory) vkFreeMemory(device, statistics_memory, nullptr);
        if (mapped) vkUnmapMemory(device, readback_memory);
        if (readback) vkDestroyBuffer(device, readback, nullptr);
        if (readback_memory) vkFreeMemory(device, readback_memory, nullptr);
        if (layout) vkDestroyPipelineLayout(device, layout, nullptr);
        if (descriptors) vkDestroyDescriptorSetLayout(device, descriptors, nullptr);
        if (shader) vkDestroyShaderModule(device, shader, nullptr);
        if (fence) vkDestroyFence(device, fence, nullptr);
        if (pool) vkDestroyCommandPool(device, pool, nullptr);
        if (nv12) vkDestroyBuffer(device, nv12, nullptr);
        if (memory) vkFreeMemory(device, memory, nullptr);
    }

    void packets() {
        for (;;) {
            int result = avcodec_receive_packet(codec, packet);
            if (result == AVERROR(EAGAIN) || result == AVERROR_EOF) return;
            Av(result, "receive Vulkan encoded packet");
            // Native Vulkan encoders may omit the last packet's duration.
            // Every submitted frame occupies exactly one rational time-base tick.
            if (!packet->duration) packet->duration = 1;
            av_packet_rescale_ts(packet, codec->time_base, stream->time_base);
            packet->stream_index = stream->index;
            Av(av_interleaved_write_frame(mux, packet), "mux Vulkan encoded packet");
            av_packet_unref(packet);
        }
    }

    VkDeviceMemory allocate(const VkMemoryRequirements& requirements, VkMemoryPropertyFlags flags) {
        VkPhysicalDeviceMemoryProperties properties;
        vkGetPhysicalDeviceMemoryProperties(gpu, &properties);
        for (std::uint32_t i = 0; i < properties.memoryTypeCount; ++i) {
            if (!(requirements.memoryTypeBits & (1u << i)) || (properties.memoryTypes[i].propertyFlags & flags) != flags) continue;
            VkMemoryAllocateInfo allocation { .sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO,
                .allocationSize = requirements.size, .memoryTypeIndex = i };
            VkDeviceMemory value {};
            Vk(vkAllocateMemory(device, &allocation, nullptr, &value), "allocate GPU capture memory");
            return value;
        }
        throw std::runtime_error("No suitable GPU capture memory type");
    }

    void makeBuffer(VkDeviceSize size, VkBufferUsageFlags usage, VkMemoryPropertyFlags flags,
                    VkBuffer& buffer, VkDeviceMemory& allocation) {
        VkBufferCreateInfo info { .sType = VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO, .size = size,
            .usage = usage, .sharingMode = VK_SHARING_MODE_EXCLUSIVE };
        Vk(vkCreateBuffer(device, &info, nullptr, &buffer), "create GPU capture buffer");
        VkMemoryRequirements requirements;
        vkGetBufferMemoryRequirements(device, buffer, &requirements);
        allocation = allocate(requirements, flags);
        Vk(vkBindBufferMemory(device, buffer, allocation, 0), "bind GPU capture buffer");
    }

    void beginCapture() {
        Vk(vkResetCommandBuffer(command, 0), "reset capture command");
        VkCommandBufferBeginInfo begin { .sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO,
            .flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT };
        Vk(vkBeginCommandBuffer(command, &begin), "begin capture command");
    }

    void readCapture() {
        VkBufferMemoryBarrier to_host { .sType = VK_STRUCTURE_TYPE_BUFFER_MEMORY_BARRIER,
            .srcAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT, .dstAccessMask = VK_ACCESS_HOST_READ_BIT,
            .srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED, .dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            .buffer = readback, .offset = 0, .size = VK_WHOLE_SIZE };
        vkCmdPipelineBarrier(command, VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_HOST_BIT,
            0, 0, nullptr, 1, &to_host, 0, nullptr);
        Vk(vkEndCommandBuffer(command), "end capture command");
        Vk(vkResetFences(device, 1, &fence), "reset capture fence");
        VkSubmitInfo submit { .sType = VK_STRUCTURE_TYPE_SUBMIT_INFO, .commandBufferCount = 1, .pCommandBuffers = &command };
        auto* hw = reinterpret_cast<AVHWDeviceContext*>(hardware->data);
        auto* vk = reinterpret_cast<AVVulkanDeviceContext*>(hw->hwctx);
        vk->lock_queue(hw, family, 0);
        const auto result = vkQueueSubmit(queue, 1, &submit, fence);
        vk->unlock_queue(hw, family, 0);
        Vk(result, "submit capture copy");
        Vk(vkWaitForFences(device, 1, &fence, VK_TRUE, timeout_ns), "wait capture copy");
    }

    void retain(VkImage rgba, std::uint64_t index) {
        bool first = capture.collect_bounds && index == 0;
        bool keep = std::binary_search(capture.retain_frames.begin(), capture.retain_frames.end(), index);
        if (!first && !keep) return;
        beginCapture();
        VkBufferImageCopy copy { .imageSubresource = { VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, 1 },
            .imageExtent = { width, height, 1 } };
        vkCmdCopyImageToBuffer(command, rgba, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, readback, 1, &copy);
        readCapture();
        const auto bytes = static_cast<std::streamsize>(std::uint64_t(width) * height * 4);
        if (first) {
            std::ofstream file(directory / "first-frame.rgba", std::ios::binary);
            file.write(static_cast<const char*>(mapped), bytes);
            file.flush();
            if (!file) throw std::runtime_error("write first GPU reference frame");
        }
        if (keep) {
            retained.write(static_cast<const char*>(mapped), bytes);
            if (!retained) throw std::runtime_error("write retained GPU reference frame");
            ++retained_count;
        }
        ++readbacks;
    }
};

GpuVideoEncoder::GpuVideoEncoder(VkInstance instance, VkPhysicalDevice gpu, VkDevice device,
    std::uint32_t graphics_family, std::span<const std::string> instance_extensions,
    std::span<const std::string> device_extensions, std::uint32_t width, std::uint32_t height,
    bool packed_alpha, std::uint32_t fps_num, std::uint32_t fps_den, int qp,
    const std::string& codec_name, const std::string& path, GpuCaptureOptions capture) : impl(std::make_unique<Impl>()) {
    auto& p = *impl;
    p.device = device; p.gpu = gpu; p.family = graphics_family;
    p.width = width; p.height = height; p.output_width = width * (packed_alpha ? 2 : 1);
    p.stride = (p.output_width + 3u) & ~3u;
    p.capture = std::move(capture);
    p.directory = std::filesystem::u8path(path).parent_path();
    if (!p.capture.encoded_frames || p.capture.retain_frames.size() > 8 ||
        !std::is_sorted(p.capture.retain_frames.begin(), p.capture.retain_frames.end()) ||
        std::adjacent_find(p.capture.retain_frames.begin(), p.capture.retain_frames.end()) != p.capture.retain_frames.end())
        throw std::runtime_error("Invalid GPU capture frame selection");
    if (!width || !height || (width & 1) || (height & 1) || !fps_num || !fps_den ||
        fps_num > INT32_MAX || fps_den > INT32_MAX || qp < 0 || qp > 51 ||
        (codec_name != "h264_vulkan" && codec_name != "hevc_vulkan"))
        throw std::runtime_error("GPU encoding requires even dimensions, a rational FPS and Vulkan H.264/HEVC");
    vkGetDeviceQueue(device, graphics_family, 0, &p.queue);
    p.push_descriptors = reinterpret_cast<PFN_vkCmdPushDescriptorSetKHR>(vkGetDeviceProcAddr(device, "vkCmdPushDescriptorSetKHR"));
    if (!p.push_descriptors) throw std::runtime_error("GPU conversion requires push descriptors");
    p.hardware = av_hwdevice_ctx_alloc(AV_HWDEVICE_TYPE_VULKAN);
    if (!p.hardware) throw std::bad_alloc();
    auto* hw = reinterpret_cast<AVHWDeviceContext*>(p.hardware->data);
    auto* vk = reinterpret_cast<AVVulkanDeviceContext*>(hw->hwctx);
    vk->get_proc_addr = vkGetInstanceProcAddr;
    vk->inst = instance; vk->phys_dev = gpu; vk->act_dev = device;
    for (auto& extension : instance_extensions) p.instance_extensions.push_back(extension.c_str());
    for (auto& extension : device_extensions) p.device_extensions.push_back(extension.c_str());
    vk->enabled_inst_extensions = p.instance_extensions.data();
    vk->nb_enabled_inst_extensions = static_cast<int>(p.instance_extensions.size());
    vk->enabled_dev_extensions = p.device_extensions.data();
    vk->nb_enabled_dev_extensions = static_cast<int>(p.device_extensions.size());
    std::uint32_t count = 0;
    vkGetPhysicalDeviceQueueFamilyProperties2(gpu, &count, nullptr);
    std::vector<VkQueueFamilyProperties2> families(count);
    std::vector<VkQueueFamilyVideoPropertiesKHR> video(count);
    for (std::uint32_t i = 0; i < count; ++i) {
        video[i].sType = VK_STRUCTURE_TYPE_QUEUE_FAMILY_VIDEO_PROPERTIES_KHR;
        families[i].sType = VK_STRUCTURE_TYPE_QUEUE_FAMILY_PROPERTIES_2;
        families[i].pNext = &video[i];
    }
    vkGetPhysicalDeviceQueueFamilyProperties2(gpu, &count, families.data());
    if (count > std::size(vk->qf)) throw std::runtime_error("Too many Vulkan queue families");
    for (std::uint32_t i = 0; i < count; ++i) {
        if (!families[i].queueFamilyProperties.queueCount) continue;
        vk->qf[vk->nb_qf++] = { static_cast<int>(i), 1,
            static_cast<VkQueueFlagBits>(families[i].queueFamilyProperties.queueFlags),
            static_cast<VkVideoCodecOperationFlagBitsKHR>(video[i].videoCodecOperations) };
    }
    Av(av_hwdevice_ctx_init(p.hardware), "wrap renderer Vulkan device");
    p.frames = av_hwframe_ctx_alloc(p.hardware);
    if (!p.frames) throw std::bad_alloc();
    auto* frames = reinterpret_cast<AVHWFramesContext*>(p.frames->data);
    frames->format = AV_PIX_FMT_VULKAN; frames->sw_format = AV_PIX_FMT_NV12;
    frames->width = static_cast<int>(p.output_width); frames->height = static_cast<int>(height);
    auto* vkframes = reinterpret_cast<AVVulkanFramesContext*>(frames->hwctx);
    vkframes->usage = static_cast<VkImageUsageFlagBits>(VK_IMAGE_USAGE_VIDEO_ENCODE_SRC_BIT_KHR | VK_IMAGE_USAGE_TRANSFER_DST_BIT);
    Av(av_hwframe_ctx_init(p.frames), "allocate Vulkan encoder frame pool");
    if (vkframes->format[0] != VK_FORMAT_G8_B8R8_2PLANE_420_UNORM)
        throw std::runtime_error("Vulkan encoder needs one multiplane NV12 image");
    const auto* encoder = avcodec_find_encoder_by_name(codec_name.c_str());
    if (!encoder) throw std::runtime_error("Vulkan encoder is absent from libavcodec");
    p.codec = avcodec_alloc_context3(encoder);
    if (!p.codec) throw std::bad_alloc();
    p.codec->width = static_cast<int>(p.output_width); p.codec->height = static_cast<int>(height);
    p.codec->pix_fmt = AV_PIX_FMT_VULKAN; p.codec->sw_pix_fmt = AV_PIX_FMT_NV12;
    p.codec->time_base = { static_cast<int>(fps_den), static_cast<int>(fps_num) };
    p.codec->framerate = { static_cast<int>(fps_num), static_cast<int>(fps_den) };
    p.codec->gop_size = 250; p.codec->max_b_frames = 0;
    p.codec->color_range = AVCOL_RANGE_MPEG; p.codec->colorspace = AVCOL_SPC_BT709;
    p.codec->color_primaries = AVCOL_PRI_BT709; p.codec->color_trc = AVCOL_TRC_BT709;
    p.codec->hw_frames_ctx = av_buffer_ref(p.frames);
    p.codec->flags |= AV_CODEC_FLAG_GLOBAL_HEADER;
    Av(av_opt_set_int(p.codec->priv_data, "qp", qp, 0), "set Vulkan encoder QP");
    Av(avcodec_open2(p.codec, encoder, nullptr), "open Vulkan encoder");
    Av(avformat_alloc_output_context2(&p.mux, nullptr, "mp4", path.c_str()), "create GPU video muxer");
    p.stream = avformat_new_stream(p.mux, nullptr);
    if (!p.stream) throw std::bad_alloc();
    p.stream->time_base = p.codec->time_base;
    p.stream->avg_frame_rate = p.codec->framerate;
    Av(avcodec_parameters_from_context(p.stream->codecpar, p.codec), "copy GPU codec parameters");
    Av(avio_open(&p.mux->pb, path.c_str(), AVIO_FLAG_WRITE), "open GPU video output");
    AVDictionary* mux_options = nullptr;
    av_dict_set_int(&mux_options, "movie_timescale", fps_num, 0);
    av_dict_set_int(&mux_options, "video_track_timescale", fps_num, 0);
    int header_result = avformat_write_header(p.mux, &mux_options);
    av_dict_free(&mux_options);
    Av(header_result, "write GPU video header");
    p.frame = av_frame_alloc(); p.packet = av_packet_alloc();
    if (!p.frame || !p.packet) throw std::bad_alloc();

    p.makeBuffer(std::uint64_t(p.stride) * height * 3 / 2,
        VK_BUFFER_USAGE_STORAGE_BUFFER_BIT | VK_BUFFER_USAGE_TRANSFER_SRC_BIT,
        VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT, p.nv12, p.memory);
    if (p.capture.collect_bounds || !p.capture.retain_frames.empty()) {
        p.makeBuffer(std::max<std::uint64_t>(32, std::uint64_t(width) * height * 4), VK_BUFFER_USAGE_TRANSFER_DST_BIT,
            VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT, p.readback, p.readback_memory);
        Vk(vkMapMemory(device, p.readback_memory, 0, VK_WHOLE_SIZE, 0, &p.mapped), "map selected-frame readback");
    }
    if (!p.capture.retain_frames.empty()) {
        p.retained.open(p.directory / "retained-frames.rgba", std::ios::binary);
        if (!p.retained) throw std::runtime_error("open retained GPU frames");
    }
    if (p.capture.collect_bounds) {
        p.makeBuffer(32, VK_BUFFER_USAGE_STORAGE_BUFFER_BIT | VK_BUFFER_USAGE_TRANSFER_DST_BIT | VK_BUFFER_USAGE_TRANSFER_SRC_BIT,
            VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT, p.statistics, p.statistics_memory);
        p.bounds = {width, height, 0, 0, 255, 0, 0, 0};
        VkImageCreateInfo image { .sType = VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO, .imageType = VK_IMAGE_TYPE_2D,
            .format = VK_FORMAT_R8G8B8A8_UNORM, .extent = {width,height,1}, .mipLevels = 1, .arrayLayers = 1,
            .samples = VK_SAMPLE_COUNT_1_BIT, .tiling = VK_IMAGE_TILING_OPTIMAL,
            .usage = VK_IMAGE_USAGE_TRANSFER_DST_BIT | VK_IMAGE_USAGE_STORAGE_BIT, .sharingMode = VK_SHARING_MODE_EXCLUSIVE };
        Vk(vkCreateImage(device, &image, nullptr, &p.first_image), "create GPU first-frame reference");
        VkMemoryRequirements requirements;
        vkGetImageMemoryRequirements(device, p.first_image, &requirements);
        p.first_memory = p.allocate(requirements, VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT);
        Vk(vkBindImageMemory(device, p.first_image, p.first_memory, 0), "bind GPU first-frame reference");
        VkImageViewCreateInfo view { .sType = VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO, .image = p.first_image,
            .viewType = VK_IMAGE_VIEW_TYPE_2D, .format = VK_FORMAT_R8G8B8A8_UNORM,
            .subresourceRange = {VK_IMAGE_ASPECT_COLOR_BIT,0,1,0,1} };
        Vk(vkCreateImageView(device, &view, nullptr, &p.first_view), "view GPU first-frame reference");
    }

    const ShaderCompUnit unit { ShaderType::COMPUTE, R"glsl(#version 450
layout(local_size_x=8, local_size_y=8) in;
layout(binding=0, rgba8) readonly uniform image2D sourceImage;
layout(binding=1, std430) writeonly buffer Output { uint words[]; } outputData;
layout(binding=2, rgba8) readonly uniform image2D firstImage;
layout(binding=3, std430) buffer Statistics { uint data[8]; } stats;
layout(push_constant) uniform Dimensions { uint width; uint height; uint outputWidth; uint stride; uint flags; } dims;
shared uint blockStats[8];
shared uint compareFirst;
vec3 rgb(uint x, uint y) {
    vec4 p = imageLoad(sourceImage, ivec2(min(x, dims.outputWidth-1u) % dims.width, min(y, dims.height-1u)));
    return x < dims.width ? p.rgb : vec3(p.a);
}
uint byteValue(float x) { return uint(clamp(round(x),0.0,255.0)); }
float luma(vec3 p) { return 16.0 + 219.0 * dot(p,vec3(0.2126,0.7152,0.0722)); }
uvec2 chroma(vec3 p) { return uvec2(byteValue(128.0+224.0*dot(p,vec3(-0.114572,-0.385428,0.5))),
                                      byteValue(128.0+224.0*dot(p,vec3(0.5,-0.454153,-0.045847)))); }
void main() {
    uint x = gl_GlobalInvocationID.x*4u, y = gl_GlobalInvocationID.y;
    bool insideImage = x < dims.stride && y < dims.height;
    if ((dims.flags&1u)!=0u) {
        if (gl_LocalInvocationIndex==0u) {
            blockStats[0]=dims.width; blockStats[1]=dims.height; blockStats[2]=0u; blockStats[3]=0u;
            blockStats[4]=255u; blockStats[5]=0u; blockStats[6]=0u; blockStats[7]=0u;
            compareFirst = atomicOr(stats.data[6],0u)==0u ? 1u : 0u;
        }
        barrier();
        if (insideImage && x < dims.width) {
            uint lo=dims.width, hi=0u, minA=255u, maxA=0u, different=0u, content=0u;
            for (uint k=0u; k<4u && x+k<dims.width; ++k) {
                uvec4 p=uvec4(imageLoad(sourceImage,ivec2(x+k,y))*255.0+0.5);
                minA=min(minA,p.a); maxA=max(maxA,p.a);
                if (p.a!=0u || ((dims.flags&2u)!=0u && any(notEqual(p.rgb,uvec3(0))))) {
                    lo=min(lo,x+k); hi=max(hi,x+k); content=1u;
                }
                if (compareFirst!=0u && any(notEqual(p,uvec4(imageLoad(firstImage,ivec2(x+k,y))*255.0+0.5)))) different=1u;
            }
            atomicMin(blockStats[4],minA); atomicMax(blockStats[5],maxA);
            if (different!=0u) atomicOr(blockStats[6],1u);
            if (content!=0u) {
                atomicMin(blockStats[0],lo); atomicMin(blockStats[1],y);
                atomicMax(blockStats[2],hi); atomicMax(blockStats[3],y); atomicOr(blockStats[7],1u);
            }
        }
        barrier();
        if (gl_LocalInvocationIndex==0u) {
            atomicMin(stats.data[4],blockStats[4]); atomicMax(stats.data[5],blockStats[5]);
            if (blockStats[6]!=0u) atomicOr(stats.data[6],1u);
            if (blockStats[7]!=0u) {
                atomicMin(stats.data[0],blockStats[0]); atomicMin(stats.data[1],blockStats[1]);
                atomicMax(stats.data[2],blockStats[2]); atomicMax(stats.data[3],blockStats[3]); atomicOr(stats.data[7],1u);
            }
        }
    }
    if (!insideImage) return;
    uvec4 Y = uvec4(byteValue(luma(rgb(x,y))),byteValue(luma(rgb(x+1u,y))),
                   byteValue(luma(rgb(x+2u,y))),byteValue(luma(rgb(x+3u,y))));
    outputData.words[(y*dims.stride+x)/4u] = Y.x | (Y.y<<8u) | (Y.z<<16u) | (Y.w<<24u);
    if ((y&1u)==0u) {
        uvec2 a = chroma((rgb(x,y)+rgb(x+1u,y)+rgb(x,y+1u)+rgb(x+1u,y+1u))*0.25);
        uvec2 b = chroma((rgb(x+2u,y)+rgb(x+3u,y)+rgb(x+2u,y+1u)+rgb(x+3u,y+1u))*0.25);
        outputData.words[(dims.height*dims.stride+y/2u*dims.stride+x)/4u] = a.x | (a.y<<8u) | (b.x<<16u) | (b.y<<24u);
    }
}
)glsl", "main" };
    std::vector<Uni_ShaderSpv> code;
    if (!CompileAndLinkShaderUnits(std::span(&unit, 1), ShaderCompOpt {}, code) || code.size() != 1)
        throw std::runtime_error("compile GPU RGB/alpha to NV12 conversion");
    const auto& words = code.front()->spirv;
    VkShaderModuleCreateInfo shader { .sType = VK_STRUCTURE_TYPE_SHADER_MODULE_CREATE_INFO,
        .codeSize = words.size() * sizeof(unsigned int), .pCode = words.data() };
    Vk(vkCreateShaderModule(device, &shader, nullptr, &p.shader), "create GPU conversion shader");
    const std::array<VkDescriptorSetLayoutBinding, 4> bindings {{
        {0,VK_DESCRIPTOR_TYPE_STORAGE_IMAGE,1,VK_SHADER_STAGE_COMPUTE_BIT,nullptr},
        {1,VK_DESCRIPTOR_TYPE_STORAGE_BUFFER,1,VK_SHADER_STAGE_COMPUTE_BIT,nullptr},
        {2,VK_DESCRIPTOR_TYPE_STORAGE_IMAGE,1,VK_SHADER_STAGE_COMPUTE_BIT,nullptr},
        {3,VK_DESCRIPTOR_TYPE_STORAGE_BUFFER,1,VK_SHADER_STAGE_COMPUTE_BIT,nullptr} }};
    VkDescriptorSetLayoutCreateInfo descriptors { .sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_CREATE_INFO,
        .flags = VK_DESCRIPTOR_SET_LAYOUT_CREATE_PUSH_DESCRIPTOR_BIT_KHR, .bindingCount = 4, .pBindings = bindings.data() };
    Vk(vkCreateDescriptorSetLayout(device, &descriptors, nullptr, &p.descriptors), "create GPU conversion descriptors");
    VkPushConstantRange constants { VK_SHADER_STAGE_COMPUTE_BIT, 0, 20 };
    VkPipelineLayoutCreateInfo layout { .sType = VK_STRUCTURE_TYPE_PIPELINE_LAYOUT_CREATE_INFO,
        .setLayoutCount = 1, .pSetLayouts = &p.descriptors, .pushConstantRangeCount = 1, .pPushConstantRanges = &constants };
    Vk(vkCreatePipelineLayout(device, &layout, nullptr, &p.layout), "create GPU conversion layout");
    VkComputePipelineCreateInfo pipeline { .sType = VK_STRUCTURE_TYPE_COMPUTE_PIPELINE_CREATE_INFO,
        .stage = { .sType = VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO,
                   .stage = VK_SHADER_STAGE_COMPUTE_BIT, .module = p.shader, .pName = "main" }, .layout = p.layout };
    Vk(vkCreateComputePipelines(device, VK_NULL_HANDLE, 1, &pipeline, nullptr, &p.pipeline), "create GPU conversion pipeline");
    VkCommandPoolCreateInfo pool { .sType = VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO,
        .flags = VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT, .queueFamilyIndex = graphics_family };
    Vk(vkCreateCommandPool(device, &pool, nullptr, &p.pool), "create GPU conversion command pool");
    VkCommandBufferAllocateInfo command { .sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO,
        .commandPool = p.pool, .level = VK_COMMAND_BUFFER_LEVEL_PRIMARY, .commandBufferCount = 1 };
    Vk(vkAllocateCommandBuffers(device, &command, &p.command), "allocate GPU conversion command");
    VkFenceCreateInfo fence { .sType = VK_STRUCTURE_TYPE_FENCE_CREATE_INFO };
    Vk(vkCreateFence(device, &fence, nullptr, &p.fence), "create GPU conversion fence");
}

GpuVideoEncoder::~GpuVideoEncoder() = default;

void GpuVideoEncoder::encode(VkImage rgba, std::uint64_t index) {
    auto& p = *impl;
    if (p.finished) throw std::runtime_error("GPU encoder already finished");
    if (index >= p.capture.encoded_frames) { p.retain(rgba, index); return; }
    av_frame_unref(p.frame);
    Av(av_hwframe_get_buffer(p.frames, p.frame, 0), "get GPU encoder surface");
    auto* vkframe = reinterpret_cast<AVVkFrame*>(p.frame->data[0]);
    auto* frames = reinterpret_cast<AVHWFramesContext*>(p.frames->data);
    auto* vkframes = reinterpret_cast<AVVulkanFramesContext*>(frames->hwctx);
    // FFmpeg allocates shared graphics/encode images with CONCURRENT ownership.
    if (vkframe->img[1] || vkframe->queue_family[0] != VK_QUEUE_FAMILY_IGNORED)
        throw std::runtime_error("GPU encoder surface requires concurrent multiplane NV12");
    if (rgba != p.source_image) {
        if (p.source_view) vkDestroyImageView(p.device, p.source_view, nullptr);
        p.source_view = VK_NULL_HANDLE;
        VkImageViewCreateInfo view_info { .sType = VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO,
            .image = rgba, .viewType = VK_IMAGE_VIEW_TYPE_2D, .format = VK_FORMAT_R8G8B8A8_UNORM,
            .subresourceRange = { VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1 } };
        Vk(vkCreateImageView(p.device, &view_info, nullptr, &p.source_view), "view rendered RGBA image");
        p.source_image = rgba;
    }
    vkframes->lock_frame(frames, vkframe);
    try {
        Vk(vkResetCommandBuffer(p.command, 0), "reset GPU conversion command");
        VkCommandBufferBeginInfo begin { .sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO,
            .flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT };
        Vk(vkBeginCommandBuffer(p.command, &begin), "begin GPU conversion command");
        if (p.capture.collect_bounds) {
            if (index == 0) {
                vkCmdUpdateBuffer(p.command, p.statistics, 0, sizeof(p.bounds), p.bounds.data());
                VkImageMemoryBarrier first { .sType = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER,
                    .dstAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT, .oldLayout = VK_IMAGE_LAYOUT_UNDEFINED,
                    .newLayout = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, .srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
                    .dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED, .image = p.first_image,
                    .subresourceRange = { VK_IMAGE_ASPECT_COLOR_BIT,0,1,0,1 } };
                vkCmdPipelineBarrier(p.command,VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT,VK_PIPELINE_STAGE_TRANSFER_BIT,0,0,nullptr,0,nullptr,1,&first);
                VkImageCopy copy { .srcSubresource = {VK_IMAGE_ASPECT_COLOR_BIT,0,0,1},
                    .dstSubresource = {VK_IMAGE_ASPECT_COLOR_BIT,0,0,1}, .extent = {p.width,p.height,1} };
                vkCmdCopyImage(p.command,rgba,VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,p.first_image,VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,1,&copy);
                first.srcAccessMask=VK_ACCESS_TRANSFER_WRITE_BIT; first.dstAccessMask=VK_ACCESS_SHADER_READ_BIT;
                first.oldLayout=VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL; first.newLayout=VK_IMAGE_LAYOUT_GENERAL;
                vkCmdPipelineBarrier(p.command,VK_PIPELINE_STAGE_TRANSFER_BIT,VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT,0,0,nullptr,0,nullptr,1,&first);
            }
            VkBufferMemoryBarrier stats_ready { .sType=VK_STRUCTURE_TYPE_BUFFER_MEMORY_BARRIER,
                .srcAccessMask=index==0 ? VK_ACCESS_TRANSFER_WRITE_BIT : VK_ACCESS_SHADER_WRITE_BIT,
                .dstAccessMask=VK_ACCESS_SHADER_READ_BIT|VK_ACCESS_SHADER_WRITE_BIT,
                .srcQueueFamilyIndex=VK_QUEUE_FAMILY_IGNORED,.dstQueueFamilyIndex=VK_QUEUE_FAMILY_IGNORED,
                .buffer=p.statistics,.offset=0,.size=32 };
            vkCmdPipelineBarrier(p.command,index==0 ? VK_PIPELINE_STAGE_TRANSFER_BIT : VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT,
                VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT,0,0,nullptr,1,&stats_ready,0,nullptr);
        }
        VkImageMemoryBarrier source { .sType = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER,
            .srcAccessMask = VK_ACCESS_TRANSFER_READ_BIT | VK_ACCESS_TRANSFER_WRITE_BIT,
            .dstAccessMask = VK_ACCESS_SHADER_READ_BIT, .oldLayout = VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
            .newLayout = VK_IMAGE_LAYOUT_GENERAL, .srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            .dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED, .image = rgba,
            .subresourceRange = { VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1 } };
        vkCmdPipelineBarrier(p.command, VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT,
            0, 0, nullptr, 0, nullptr, 1, &source);
        vkCmdBindPipeline(p.command, VK_PIPELINE_BIND_POINT_COMPUTE, p.pipeline);
        VkDescriptorImageInfo image { .imageView = p.source_view, .imageLayout = VK_IMAGE_LAYOUT_GENERAL };
        VkDescriptorBufferInfo buffer { .buffer = p.nv12, .offset = 0, .range = VK_WHOLE_SIZE };
        VkDescriptorImageInfo first { .imageView=p.first_view ? p.first_view : p.source_view, .imageLayout=VK_IMAGE_LAYOUT_GENERAL };
        VkDescriptorBufferInfo stats { .buffer=p.statistics ? p.statistics : p.nv12, .offset=0, .range=32 };
        std::array<VkWriteDescriptorSet, 4> writes {{
            { .sType = VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET, .dstBinding = 0, .descriptorCount = 1,
              .descriptorType = VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, .pImageInfo = &image },
            { .sType = VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET, .dstBinding = 1, .descriptorCount = 1,
              .descriptorType = VK_DESCRIPTOR_TYPE_STORAGE_BUFFER, .pBufferInfo = &buffer },
            { .sType = VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET, .dstBinding = 2, .descriptorCount = 1,
              .descriptorType = VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, .pImageInfo = &first },
            { .sType = VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET, .dstBinding = 3, .descriptorCount = 1,
              .descriptorType = VK_DESCRIPTOR_TYPE_STORAGE_BUFFER, .pBufferInfo = &stats } }};
        p.push_descriptors(p.command, VK_PIPELINE_BIND_POINT_COMPUTE, p.layout, 0, 4, writes.data());
        std::array<std::uint32_t, 5> dimensions { p.width, p.height, p.output_width, p.stride,
            p.capture.collect_bounds ? (p.capture.bounds_include_rgb ? 3u : 1u) : 0u };
        vkCmdPushConstants(p.command, p.layout, VK_SHADER_STAGE_COMPUTE_BIT, 0, sizeof(dimensions), dimensions.data());
        vkCmdDispatch(p.command, (p.stride / 4 + 7) / 8, (p.height + 7) / 8, 1);
        VkBufferMemoryBarrier ready { .sType = VK_STRUCTURE_TYPE_BUFFER_MEMORY_BARRIER,
            .srcAccessMask = VK_ACCESS_SHADER_WRITE_BIT, .dstAccessMask = VK_ACCESS_TRANSFER_READ_BIT,
            .srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED, .dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            .buffer = p.nv12, .offset = 0, .size = VK_WHOLE_SIZE };
        vkCmdPipelineBarrier(p.command, VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT,
            0, 0, nullptr, 1, &ready, 0, nullptr);
        VkImageMemoryBarrier target { .sType = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER,
            .srcAccessMask = 0, .dstAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT,
            .oldLayout = vkframe->layout[0], .newLayout = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
            .srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED, .dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            .image = vkframe->img[0], .subresourceRange = { VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1 } };
        vkCmdPipelineBarrier(p.command, VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT,
            0, 0, nullptr, 0, nullptr, 1, &target);
        std::array<VkBufferImageCopy, 2> copies {{
            { .bufferOffset = 0, .bufferRowLength = p.stride, .bufferImageHeight = p.height,
              .imageSubresource = { VK_IMAGE_ASPECT_PLANE_0_BIT, 0, 0, 1 }, .imageExtent = { p.output_width, p.height, 1 } },
            { .bufferOffset = std::uint64_t(p.stride) * p.height, .bufferRowLength = p.stride / 2,
              .bufferImageHeight = p.height / 2, .imageSubresource = { VK_IMAGE_ASPECT_PLANE_1_BIT, 0, 0, 1 },
              .imageExtent = { p.output_width / 2, p.height / 2, 1 } } }};
        vkCmdCopyBufferToImage(p.command, p.nv12, vkframe->img[0], VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 2, copies.data());
        source.srcAccessMask = VK_ACCESS_SHADER_READ_BIT; source.dstAccessMask = VK_ACCESS_TRANSFER_READ_BIT;
        source.oldLayout = VK_IMAGE_LAYOUT_GENERAL; source.newLayout = VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL;
        vkCmdPipelineBarrier(p.command, VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT,
            0, 0, nullptr, 0, nullptr, 1, &source);
        Vk(vkEndCommandBuffer(p.command), "end GPU conversion command");
        Vk(vkResetFences(p.device, 1, &p.fence), "reset GPU conversion fence");
        std::uint64_t before = vkframe->sem_value[0], after = before + 1;
        VkTimelineSemaphoreSubmitInfo timeline { .sType = VK_STRUCTURE_TYPE_TIMELINE_SEMAPHORE_SUBMIT_INFO,
            .waitSemaphoreValueCount = 1, .pWaitSemaphoreValues = &before,
            .signalSemaphoreValueCount = 1, .pSignalSemaphoreValues = &after };
        VkPipelineStageFlags wait_stage = VK_PIPELINE_STAGE_ALL_COMMANDS_BIT;
        VkSubmitInfo submit { .sType = VK_STRUCTURE_TYPE_SUBMIT_INFO, .pNext = &timeline,
            .waitSemaphoreCount = 1, .pWaitSemaphores = &vkframe->sem[0], .pWaitDstStageMask = &wait_stage,
            .commandBufferCount = 1, .pCommandBuffers = &p.command,
            .signalSemaphoreCount = 1, .pSignalSemaphores = &vkframe->sem[0] };
        auto* hw = reinterpret_cast<AVHWDeviceContext*>(p.hardware->data);
        auto* vk = reinterpret_cast<AVVulkanDeviceContext*>(hw->hwctx);
        vk->lock_queue(hw, p.family, 0);
        const auto submitted = vkQueueSubmit(p.queue, 1, &submit, p.fence);
        vk->unlock_queue(hw, p.family, 0);
        Vk(submitted, "submit GPU conversion");
        vkframe->sem_value[0] = after;
        vkframe->layout[0] = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL;
        vkframe->access[0] = VK_ACCESS_TRANSFER_WRITE_BIT;
        Vk(vkWaitForFences(p.device, 1, &p.fence, VK_TRUE, timeout_ns), "wait GPU conversion");
    } catch (...) {
        vkframes->unlock_frame(frames, vkframe);
        vkDeviceWaitIdle(p.device);
        throw;
    }
    vkframes->unlock_frame(frames, vkframe);
    p.frame->pts = static_cast<std::int64_t>(index);
    p.frame->duration = 1;
    p.frame->color_range = p.codec->color_range; p.frame->colorspace = p.codec->colorspace;
    p.frame->color_primaries = p.codec->color_primaries; p.frame->color_trc = p.codec->color_trc;
    Av(avcodec_send_frame(p.codec, p.frame), "submit Vulkan encode frame");
    p.packets();
    p.retain(rgba, index);
}

void GpuVideoEncoder::finish() {
    auto& p = *impl;
    if (p.finished) return;
    Av(avcodec_send_frame(p.codec, nullptr), "flush Vulkan encoder");
    p.packets();
    Av(av_write_trailer(p.mux), "finish GPU video");
    Av(avio_closep(&p.mux->pb), "close GPU video");
    if (p.retained.is_open()) {
        p.retained.flush();
        if (!p.retained || p.retained_count != p.capture.retain_frames.size())
            throw std::runtime_error("Retained GPU frame sequence is incomplete");
        p.retained.close();
    }
    if (p.capture.collect_bounds) {
        p.beginCapture();
        VkBufferMemoryBarrier ready { .sType=VK_STRUCTURE_TYPE_BUFFER_MEMORY_BARRIER,
            .srcAccessMask=VK_ACCESS_SHADER_WRITE_BIT,.dstAccessMask=VK_ACCESS_TRANSFER_READ_BIT,
            .srcQueueFamilyIndex=VK_QUEUE_FAMILY_IGNORED,.dstQueueFamilyIndex=VK_QUEUE_FAMILY_IGNORED,
            .buffer=p.statistics,.offset=0,.size=32 };
        vkCmdPipelineBarrier(p.command,VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT,VK_PIPELINE_STAGE_TRANSFER_BIT,0,0,nullptr,1,&ready,0,nullptr);
        VkBufferCopy copy { .size=32 };
        vkCmdCopyBuffer(p.command,p.statistics,p.readback,1,&copy);
        p.readCapture();
        std::memcpy(p.bounds.data(),p.mapped,sizeof(p.bounds));
    }
    p.finished = true;
}

std::string GpuVideoEncoder::captureMetadata() const {
    const auto& p = *impl;
    std::ostringstream out;
    out << "{\"readback_frames\":" << p.readbacks;
    if (p.capture.collect_bounds) {
        bool content=p.bounds[7]!=0;
        out << ",\"alpha_bounds\":{\"has_content\":" << (content ? "true" : "false")
            << ",\"x\":" << (content ? p.bounds[0] : 0) << ",\"y\":" << (content ? p.bounds[1] : 0)
            << ",\"width\":" << (content ? p.bounds[2]-p.bounds[0]+1 : 0) << ",\"height\":" << (content ? p.bounds[3]-p.bounds[1]+1 : 0)
            << ",\"minimum_alpha\":" << p.bounds[4] << ",\"maximum_alpha\":" << p.bounds[5]
            << ",\"includes_rgb\":" << (p.capture.bounds_include_rgb ? "true" : "false")
            << ",\"pixel_identical_in_generated_interval\":" << (p.bounds[6] ? "false" : "true")
            << ",\"first_frame_rgba_path\":\"first-frame.rgba\",\"basis\":\"GPU reduction over every encoded native RGBA frame.\","
               "\"pixel_identity_scope\":\"Every encoded frame compared against the first frame on the GPU; excludes unencoded continuation frames.\"}";
    }
    if (!p.capture.retain_frames.empty()) {
        out << ",\"retained_frames\":{\"path\":\"retained-frames.rgba\",\"format\":\"rgba\",\"width\":" << p.width
            << ",\"height\":" << p.height << ",\"encoded_frames\":" << p.capture.encoded_frames << ",\"frame_indices\":[";
        bool comma=false;
        for (auto index:p.capture.retain_frames) { if(comma) out << ','; comma=true; out << index; }
        out << "],\"basis\":\"Selected renderer RGBA frames copied byte for byte before encoding.\"}";
    }
    out << '}';
    return out.str();
}
}
