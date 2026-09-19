#include "GpuVideoEncoder.hpp"
#include <array>
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
};

GpuVideoEncoder::GpuVideoEncoder(VkInstance instance, VkPhysicalDevice gpu, VkDevice device,
    std::uint32_t graphics_family, std::span<const std::string> instance_extensions,
    std::span<const std::string> device_extensions, std::uint32_t width, std::uint32_t height,
    bool packed_alpha, std::uint32_t fps_num, std::uint32_t fps_den, int qp,
    const std::string& codec_name, const std::string& path) : impl(std::make_unique<Impl>()) {
    auto& p = *impl;
    p.device = device; p.gpu = gpu; p.family = graphics_family;
    p.width = width; p.height = height; p.output_width = width * (packed_alpha ? 2 : 1);
    p.stride = (p.output_width + 3u) & ~3u;
    if (!width || !height || (width & 1) || (height & 1) || !fps_num || !fps_den || qp < 0 || qp > 51 ||
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
    Av(avformat_write_header(p.mux, nullptr), "write GPU video header");
    p.frame = av_frame_alloc(); p.packet = av_packet_alloc();
    if (!p.frame || !p.packet) throw std::bad_alloc();

    VkBufferCreateInfo buffer { .sType = VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO,
        .size = std::uint64_t(p.stride) * height * 3 / 2,
        .usage = VK_BUFFER_USAGE_STORAGE_BUFFER_BIT | VK_BUFFER_USAGE_TRANSFER_SRC_BIT,
        .sharingMode = VK_SHARING_MODE_EXCLUSIVE };
    Vk(vkCreateBuffer(device, &buffer, nullptr, &p.nv12), "create GPU NV12 buffer");
    VkMemoryRequirements requirements;
    vkGetBufferMemoryRequirements(device, p.nv12, &requirements);
    VkPhysicalDeviceMemoryProperties memory;
    vkGetPhysicalDeviceMemoryProperties(gpu, &memory);
    std::uint32_t memory_type = memory.memoryTypeCount;
    for (std::uint32_t i = 0; i < memory.memoryTypeCount; ++i)
        if ((requirements.memoryTypeBits & (1u << i)) && (memory.memoryTypes[i].propertyFlags & VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT)) { memory_type = i; break; }
    if (memory_type == memory.memoryTypeCount) throw std::runtime_error("No device-local conversion memory");
    VkMemoryAllocateInfo allocation { .sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO,
        .allocationSize = requirements.size, .memoryTypeIndex = memory_type };
    Vk(vkAllocateMemory(device, &allocation, nullptr, &p.memory), "allocate GPU NV12 memory");
    Vk(vkBindBufferMemory(device, p.nv12, p.memory, 0), "bind GPU NV12 memory");

    const ShaderCompUnit unit { ShaderType::COMPUTE, R"glsl(#version 450
layout(local_size_x=8, local_size_y=8) in;
layout(binding=0, rgba8) readonly uniform image2D sourceImage;
layout(binding=1, std430) writeonly buffer Output { uint words[]; } outputData;
layout(push_constant) uniform Dimensions { uint width; uint height; uint outputWidth; uint stride; } dims;
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
    if (x >= dims.stride || y >= dims.height) return;
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
    const std::array<VkDescriptorSetLayoutBinding, 2> bindings {{
        {0,VK_DESCRIPTOR_TYPE_STORAGE_IMAGE,1,VK_SHADER_STAGE_COMPUTE_BIT,nullptr},
        {1,VK_DESCRIPTOR_TYPE_STORAGE_BUFFER,1,VK_SHADER_STAGE_COMPUTE_BIT,nullptr} }};
    VkDescriptorSetLayoutCreateInfo descriptors { .sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_CREATE_INFO,
        .flags = VK_DESCRIPTOR_SET_LAYOUT_CREATE_PUSH_DESCRIPTOR_BIT_KHR, .bindingCount = 2, .pBindings = bindings.data() };
    Vk(vkCreateDescriptorSetLayout(device, &descriptors, nullptr, &p.descriptors), "create GPU conversion descriptors");
    VkPushConstantRange constants { VK_SHADER_STAGE_COMPUTE_BIT, 0, 16 };
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
        std::array<VkWriteDescriptorSet, 2> writes {{
            { .sType = VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET, .dstBinding = 0, .descriptorCount = 1,
              .descriptorType = VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, .pImageInfo = &image },
            { .sType = VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET, .dstBinding = 1, .descriptorCount = 1,
              .descriptorType = VK_DESCRIPTOR_TYPE_STORAGE_BUFFER, .pBufferInfo = &buffer } }};
        p.push_descriptors(p.command, VK_PIPELINE_BIND_POINT_COMPUTE, p.layout, 0, 2, writes.data());
        std::array<std::uint32_t, 4> dimensions { p.width, p.height, p.output_width, p.stride };
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
        VkPipelineStageFlags wait_stage = VK_PIPELINE_STAGE_TRANSFER_BIT;
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
}

void GpuVideoEncoder::finish() {
    auto& p = *impl;
    if (p.finished) return;
    Av(avcodec_send_frame(p.codec, nullptr), "flush Vulkan encoder");
    p.packets();
    Av(av_write_trailer(p.mux), "finish GPU video");
    Av(avio_closep(&p.mux->pb), "close GPU video");
    p.finished = true;
}
}
