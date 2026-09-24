#include "GpuVideoEncoder.hpp"
#include <array>
#include <algorithm>
#include <filesystem>
#include <fstream>
#include <sstream>
#include <cstring>
#include <stdexcept>
#include <vector>
#include <chrono>
#include <cstdlib>

extern "C" {
#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>
#include <libavutil/hwcontext.h>
#include <libavutil/hwcontext_vulkan.h>
#include <libavutil/opt.h>
#include <libavutil/mathematics.h>
}
#define NOMINMAX
#define FFNV_LOG_FUNC(logctx, msg, ...)
#define FFNV_DEBUG_LOG_FUNC(logctx, msg, ...)
#include <ffnvcodec/dynlink_loader.h>
#include <vulkan/vulkan_win32.h>

import wescene.shader_compile;
import wescene.types;

namespace owe {
using namespace owe::vulkan;
namespace {
// FFmpeg 把同一个编码会话的帧轮流提交到编码族的每条队列；NVIDIA 上每条队列是一个 NVENC 引擎，
// 一个会话跨两个引擎在多进程并发时报 VK_ERROR_DEVICE_LOST（ARCH6b：3757825891 三组并发必现，只给 FFmpeg 一条队列则不出）。
// 所以 FFmpeg 只看到一条编码队列，取队列时换成本进程那条，多进程并行仍能用上第二个引擎。
std::uint32_t pinned_encode_family = UINT32_MAX, pinned_encode_queue = 0;
// 本进程用哪条编码队列：先占到哪条的命名互斥量就用哪条（句柄不关，进程退出时系统释放），两条都有人占时按 PID 奇偶。
// 原来只按 PID 奇偶，两个并发编码进程有一半概率挤在同一个引擎上（PERFM 实测 1080p H.264：同队列各 0.96、异队列各 1.73 G 像素/s）。
std::uint32_t EncodeQueueForThisProcess() {
    for (std::uint32_t queue = 0; queue < 2; ++queue) {
        HANDLE mutex = CreateMutexW(nullptr, FALSE, queue ? L"Local\\wpe-render-encode-queue-1" : L"Local\\wpe-render-encode-queue-0");
        if (!mutex) continue;
        const DWORD wait = WaitForSingleObject(mutex, 0);
        if (wait == WAIT_OBJECT_0 || wait == WAIT_ABANDONED) return queue;
        CloseHandle(mutex);
    }
    return GetCurrentProcessId() / 4 % 2; // Windows 的 PID 都是 4 的倍数
}
PFN_vkGetDeviceQueue device_queue = nullptr;
PFN_vkGetDeviceProcAddr device_proc = nullptr;
VKAPI_ATTR void VKAPI_CALL PinnedDeviceQueue(VkDevice device, std::uint32_t family, std::uint32_t index, VkQueue* queue) {
    device_queue(device, family, family == pinned_encode_family ? pinned_encode_queue : index, queue);
}
VKAPI_ATTR PFN_vkVoidFunction VKAPI_CALL PinnedDeviceProc(VkDevice device, const char* name) {
    if (!std::strcmp(name, "vkGetDeviceQueue")) {
        device_queue = reinterpret_cast<PFN_vkGetDeviceQueue>(device_proc(device, name));
        return reinterpret_cast<PFN_vkVoidFunction>(PinnedDeviceQueue);
    }
    return device_proc(device, name);
}
VKAPI_ATTR PFN_vkVoidFunction VKAPI_CALL PinnedInstanceProc(VkInstance instance, const char* name) {
    if (!std::strcmp(name, "vkGetDeviceProcAddr")) {
        device_proc = reinterpret_cast<PFN_vkGetDeviceProcAddr>(vkGetInstanceProcAddr(instance, name));
        return reinterpret_cast<PFN_vkVoidFunction>(PinnedDeviceProc);
    }
    return vkGetInstanceProcAddr(instance, name);
}
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

// 编码帧池的 img_flags。留 0 时 FFmpeg 不看驱动、自动补 MUTABLE_FORMAT|ALIAS|EXTENDED_USAGE；驱动对编码输入
// 格式报告的 imageCreateFlags 不含其中某位时（如 NVIDIA 不报 ALIAS），图像与编码 profile 不兼容
// （VUID-vkCmdEncodeVideoKHR-pEncodeInfo-08206）。这里查 NV12 编码输入格式，与那三位取交集。
// profile 按 FFmpeg 8.1 的选法：h264_vulkan 取 Constrained Baseline/Main/High 里驱动支持的最后一个（即支持 High 时用 High），
// hevc_vulkan 对 NV12 只用 Main。VIDEO_PROFILE_INDEPENDENT 是 FFmpeg 无 profile 列表时本来就加的，放进来让交集为空时
// img_flags 仍非 0、不触发自动补位。查不到（驱动不支持该 profile 或没有 NV12 条目）返回 0，保持 FFmpeg 默认。
VkImageCreateFlags EncoderInputFlags(VkInstance instance, VkPhysicalDevice gpu, bool hevc) {
    auto query = reinterpret_cast<PFN_vkGetPhysicalDeviceVideoFormatPropertiesKHR>(
        vkGetInstanceProcAddr(instance, "vkGetPhysicalDeviceVideoFormatPropertiesKHR"));
    if (!query) return 0;
    VkVideoEncodeH264ProfileInfoKHR h264 { .sType = VK_STRUCTURE_TYPE_VIDEO_ENCODE_H264_PROFILE_INFO_KHR,
        .stdProfileIdc = STD_VIDEO_H264_PROFILE_IDC_HIGH };
    VkVideoEncodeH265ProfileInfoKHR h265 { .sType = VK_STRUCTURE_TYPE_VIDEO_ENCODE_H265_PROFILE_INFO_KHR,
        .stdProfileIdc = STD_VIDEO_H265_PROFILE_IDC_MAIN };
    VkVideoProfileInfoKHR profile { .sType = VK_STRUCTURE_TYPE_VIDEO_PROFILE_INFO_KHR,
        .pNext = hevc ? static_cast<const void*>(&h265) : &h264,
        .videoCodecOperation = hevc ? VK_VIDEO_CODEC_OPERATION_ENCODE_H265_BIT_KHR : VK_VIDEO_CODEC_OPERATION_ENCODE_H264_BIT_KHR,
        .chromaSubsampling = VK_VIDEO_CHROMA_SUBSAMPLING_420_BIT_KHR,
        .lumaBitDepth = VK_VIDEO_COMPONENT_BIT_DEPTH_8_BIT_KHR, .chromaBitDepth = VK_VIDEO_COMPONENT_BIT_DEPTH_8_BIT_KHR };
    VkVideoProfileListInfoKHR list { .sType = VK_STRUCTURE_TYPE_VIDEO_PROFILE_LIST_INFO_KHR,
        .profileCount = 1, .pProfiles = &profile };
    VkPhysicalDeviceVideoFormatInfoKHR info { .sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VIDEO_FORMAT_INFO_KHR,
        .pNext = &list, .imageUsage = VK_IMAGE_USAGE_VIDEO_ENCODE_SRC_BIT_KHR | VK_IMAGE_USAGE_TRANSFER_DST_BIT };
    std::uint32_t count = 0;
    if (query(gpu, &info, &count, nullptr) != VK_SUCCESS) return 0;
    std::vector<VkVideoFormatPropertiesKHR> formats(count, { .sType = VK_STRUCTURE_TYPE_VIDEO_FORMAT_PROPERTIES_KHR });
    if (query(gpu, &info, &count, formats.data()) != VK_SUCCESS) return 0;
    for (std::uint32_t i = 0; i < count; ++i)
        if (formats[i].format == VK_FORMAT_G8_B8R8_2PLANE_420_UNORM && formats[i].imageTiling == VK_IMAGE_TILING_OPTIMAL)
            return (formats[i].imageCreateFlags & (VK_IMAGE_CREATE_MUTABLE_FORMAT_BIT | VK_IMAGE_CREATE_ALIAS_BIT |
                VK_IMAGE_CREATE_EXTENDED_USAGE_BIT)) | VK_IMAGE_CREATE_VIDEO_PROFILE_INDEPENDENT_BIT_KHR;
    return 0;
}
}

struct GpuVideoEncoder::Impl {
    VkDevice device {};
    VkPhysicalDevice gpu {};
    VkQueue queue {};
    std::uint32_t family {}, width {}, height {}, output_width {}, output_height {}, stride {}, color_width {};
    AVBufferRef *hardware {}, *frames {};
    AVCodecContext* codec {};
    AVFormatContext* mux {};
    AVStream* stream {};
    AVFormatContext* head_mux {};
    AVStream* head_stream {};
    std::string output_path, body_path, head_path;
    std::uint64_t encoded_packets {}, observed_frames {};
    AVFrame* frame {};
    AVPacket* packet {};
    std::vector<const char*> instance_extensions, device_extensions;
    VkBuffer nv12 {};
    VkDeviceMemory memory {};
    VkBuffer loop_head {};
    VkDeviceMemory loop_head_memory {};
    VkDeviceSize loop_head_stride {};
    VkCommandPool pool {};
    VkCommandBuffer command {};
    VkFence fence {};
    VkShaderModule shader {};
    VkDescriptorSetLayout descriptors {};
    VkPipelineLayout layout {};
    VkPipeline pipeline {};
    VkImage source_image {};
    VkImageView source_view {};
    VkShaderModule resize_shader {};
    VkPipeline resize_pipeline {};
    VkImage horizontal_image {}, resized_image {};
    VkImageView horizontal_view {}, resized_view {};
    VkDeviceMemory horizontal_memory {}, resized_memory {};
    bool resizing {};
    GpuCaptureOptions capture;
    std::filesystem::path directory;
    std::ofstream retained;
    std::ofstream loop_window;
    std::uint64_t loop_window_count {};
    VkBuffer statistics {}, readback {};
    VkDeviceMemory statistics_memory {}, readback_memory {}, first_memory {};
    VkImage first_image {};
    VkImageView first_view {};
    void* mapped {};
    std::array<std::uint32_t, 8> bounds {};
    std::uint64_t readbacks {}, retained_count {};
    PFN_vkCmdPushDescriptorSetKHR push_descriptors {};
    bool finished {};
    bool conversion_pending {};
    std::int64_t quality_level {}, async_depth {};
    // NVIDIA 上换 NVENC SDK 编码（ARCHN）：Vulkan Video 在 5090 上只用到 1 个编码引擎（ARCH4 实测 HEVC 0.52 GP/s，
    // 多进程也不涨）。转换着色器把 NV12 写进可导出的显存环形缓冲，经 CUDA 外部内存交给 NVENC，帧不回 CPU；
    // HEVC 用分条编码（splitEncodeMode）把一帧切给多个引擎。其他厂商与加载失败时仍走 FFmpeg 的 Vulkan 编码器。
    static constexpr std::uint32_t nv_ring = 8;
    CudaFunctions* cu {};
    NvencFunctions* nvenc_dl {};
    NV_ENCODE_API_FUNCTION_LIST nv { NV_ENCODE_API_FUNCTION_LIST_VER };
    CUcontext cuda {};
    CUexternalMemory cuda_memory {};
    CUdeviceptr cuda_base {};
    void* nvenc {};
    std::array<NV_ENC_REGISTERED_PTR, nv_ring> nv_registered {};
    std::array<NV_ENC_OUTPUT_PTR, nv_ring> nv_bitstreams {};
    std::array<NV_ENC_INPUT_PTR, nv_ring> nv_inputs {};
    VkDeviceSize nv_slot_bytes {};
    std::uint64_t nv_sent {}, nv_collected {};
    bool nv_pending {}, nv_pending_idr {};
    std::int64_t nv_pending_pts {};

    ~Impl() {
        // On cancellation/error, queued codec work must finish before its resources/device disappear.
        if (device) vkDeviceWaitIdle(device);
        if (nvenc) {
            for (auto input : nv_inputs) if (input) nv.nvEncUnmapInputResource(nvenc, input);
            for (auto resource : nv_registered) if (resource) nv.nvEncUnregisterResource(nvenc, resource);
            for (auto bitstream : nv_bitstreams) if (bitstream) nv.nvEncDestroyBitstreamBuffer(nvenc, bitstream);
            nv.nvEncDestroyEncoder(nvenc);
        }
        if (cuda) {
            cu->cuCtxPushCurrent(cuda);
            if (cuda_base) cu->cuMemFree(cuda_base);
            if (cuda_memory) cu->cuDestroyExternalMemory(cuda_memory);
            cu->cuCtxPopCurrent(nullptr);
            cu->cuCtxDestroy(cuda);
        }
        nvenc_free_functions(&nvenc_dl);
        cuda_free_functions(&cu);
        av_packet_free(&packet);
        av_frame_free(&frame);
        avcodec_free_context(&codec);
        av_buffer_unref(&frames);
        av_buffer_unref(&hardware);
        if (mux) { if (mux->pb) avio_closep(&mux->pb); avformat_free_context(mux); }
        if (head_mux) { if (head_mux->pb) avio_closep(&head_mux->pb); avformat_free_context(head_mux); }
        if (pipeline) vkDestroyPipeline(device, pipeline, nullptr);
        if (resize_pipeline) vkDestroyPipeline(device, resize_pipeline, nullptr);
        if (resize_shader) vkDestroyShaderModule(device, resize_shader, nullptr);
        if (horizontal_view) vkDestroyImageView(device, horizontal_view, nullptr);
        if (horizontal_image) vkDestroyImage(device, horizontal_image, nullptr);
        if (horizontal_memory) vkFreeMemory(device, horizontal_memory, nullptr);
        if (resized_view) vkDestroyImageView(device, resized_view, nullptr);
        if (resized_image) vkDestroyImage(device, resized_image, nullptr);
        if (resized_memory) vkFreeMemory(device, resized_memory, nullptr);
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
        if (loop_head) vkDestroyBuffer(device, loop_head, nullptr);
        if (loop_head_memory) vkFreeMemory(device, loop_head_memory, nullptr);
    }

    void openMux(const std::string& path, AVFormatContext*& context, AVStream*& track,
                 const AVCodecParameters* compressed_parameters = nullptr) {
        Av(avformat_alloc_output_context2(&context, nullptr, "mp4", path.c_str()), "create GPU video muxer");
        track = avformat_new_stream(context, nullptr);
        if (!track) throw std::bad_alloc();
        track->time_base = codec->time_base;
        track->avg_frame_rate = codec->framerate;
        Av(compressed_parameters ? avcodec_parameters_copy(track->codecpar,compressed_parameters)
                                : avcodec_parameters_from_context(track->codecpar,codec), "copy GPU codec parameters");
        Av(avio_open(&context->pb, path.c_str(), AVIO_FLAG_WRITE), "open GPU video output");
        AVDictionary* options = nullptr;
        av_dict_set_int(&options, "movie_timescale", codec->framerate.num, 0);
        av_dict_set_int(&options, "video_track_timescale", codec->framerate.num, 0);
        int result = avformat_write_header(context, &options);
        av_dict_free(&options);
        Av(result, "write GPU video header");
    }

    void packets() {
        for (;;) {
            int result = avcodec_receive_packet(codec, packet);
            if (result == AVERROR(EAGAIN) || result == AVERROR_EOF) return;
            Av(result, "receive Vulkan encoded packet");
            writePacket();
        }
    }

    void writePacket() {
        {
            // Native Vulkan encoders may omit the last packet's duration.
            // Every submitted frame occupies exactly one rational time-base tick.
            if (!packet->duration) packet->duration = 1;
            const auto body_frames = static_cast<std::int64_t>(capture.encoded_frames-capture.crossfade_frames);
            bool head = head_mux && packet->pts >= body_frames;
            auto* track = head ? head_stream : stream;
            if (head) { packet->pts -= body_frames; packet->dts -= body_frames; }
            av_packet_rescale_ts(packet, codec->time_base, track->time_base);
            packet->stream_index = track->index;
            Av(av_interleaved_write_frame(head ? head_mux : mux, packet), "mux Vulkan encoded packet");
            av_packet_unref(packet);
            ++encoded_packets;
        }
    }

    void assembleLoop() {
        // Both segments came from this codec. Each starts with a forced IDR and
        // uses the same SPS/PPS; restore head+body without decoding or encoding.
        avformat_free_context(mux); mux = nullptr; stream = nullptr;
        std::uint64_t copied = 0;
        for (const auto& path : {head_path,body_path}) {
            AVFormatContext* input = nullptr;
            Av(avformat_open_input(&input,path.c_str(),nullptr,nullptr),"open GPU loop segment");
            try {
                if (input->nb_streams != 1 || input->streams[0]->codecpar->codec_id != codec->codec_id)
                    throw std::runtime_error("Unexpected GPU loop segment stream");
                // MP4 demux packets are length-prefixed AVC/HEVC, unlike the
                // encoder's Annex-B packets. Carry the demuxer's codec data so
                // the muxer does not interpret those lengths as start codes.
                if (!mux) openMux(output_path,mux,stream,input->streams[0]->codecpar);
                const auto expected = path==head_path ? capture.crossfade_frames : capture.encoded_frames-capture.crossfade_frames;
                std::uint64_t segment_frames = 0;
                int result;
                while ((result=av_read_frame(input,packet)) >= 0) {
                    if (packet->stream_index != 0 || segment_frames >= expected ||
                        (segment_frames==0 && !(packet->flags & AV_PKT_FLAG_KEY)))
                        throw std::runtime_error("GPU loop segment is not an independently decodable frame sequence");
                    // A single known CFR video stream, no B frames. Rebuild its
                    // timeline from the packet sequence, retaining the exact rational interval.
                    packet->pts = packet->dts = av_rescale_q(static_cast<std::int64_t>(copied),codec->time_base,stream->time_base);
                    packet->duration = av_rescale_q(1,codec->time_base,stream->time_base);
                    packet->stream_index=stream->index; packet->pos=-1;
                    Av(av_interleaved_write_frame(mux,packet),"remux GPU loop packet");
                    av_packet_unref(packet); ++segment_frames; ++copied;
                }
                if (result != AVERROR_EOF) Av(result,"read GPU loop segment");
                if (segment_frames != expected) throw std::runtime_error("Incomplete GPU loop segment");
            } catch (...) { avformat_close_input(&input); throw; }
            avformat_close_input(&input);
        }
        if (copied != capture.encoded_frames) throw std::runtime_error("GPU loop frame count changed during assembly");
        Av(av_write_trailer(mux),"finish assembled GPU loop");
        Av(avio_closep(&mux->pb),"close assembled GPU loop");
        // The caller removes these job-local segments after validating the final video.
    }

    // preferred: extra flags tried first (e.g. HOST_CACHED for readback); falls back to flags alone.
    VkDeviceMemory allocate(const VkMemoryRequirements& requirements, VkMemoryPropertyFlags flags,
                            VkMemoryPropertyFlags preferred = 0) {
        VkPhysicalDeviceMemoryProperties properties;
        vkGetPhysicalDeviceMemoryProperties(gpu, &properties);
        for (const auto wanted : {flags | preferred, flags})
        for (std::uint32_t i = 0; i < properties.memoryTypeCount; ++i) {
            if (!(requirements.memoryTypeBits & (1u << i)) || (properties.memoryTypes[i].propertyFlags & wanted) != wanted) continue;
            VkMemoryAllocateInfo allocation { .sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO,
                .allocationSize = requirements.size, .memoryTypeIndex = i };
            VkDeviceMemory value {};
            Vk(vkAllocateMemory(device, &allocation, nullptr, &value), "allocate GPU capture memory");
            return value;
        }
        throw std::runtime_error("No suitable GPU capture memory type");
    }

    void makeBuffer(VkDeviceSize size, VkBufferUsageFlags usage, VkMemoryPropertyFlags flags,
                    VkBuffer& buffer, VkDeviceMemory& allocation, VkMemoryPropertyFlags preferred = 0) {
        VkBufferCreateInfo info { .sType = VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO, .size = size,
            .usage = usage, .sharingMode = VK_SHARING_MODE_EXCLUSIVE };
        Vk(vkCreateBuffer(device, &info, nullptr, &buffer), "create GPU capture buffer");
        VkMemoryRequirements requirements;
        vkGetBufferMemoryRequirements(device, buffer, &requirements);
        allocation = allocate(requirements, flags, preferred);
        Vk(vkBindBufferMemory(device, buffer, allocation, 0), "bind GPU capture buffer");
    }

    void makeResizeImage(std::uint32_t image_width, std::uint32_t image_height, VkFormat format,
                         VkImage& image, VkImageView& view, VkDeviceMemory& allocation) {
        VkFormatProperties properties;
        vkGetPhysicalDeviceFormatProperties(gpu, format, &properties);
        if (!(properties.optimalTilingFeatures & VK_FORMAT_FEATURE_STORAGE_IMAGE_BIT))
            throw std::runtime_error("GPU Lanczos resize requires storage images in RGBA32F and RGBA8");
        VkImageCreateInfo info { .sType = VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO, .imageType = VK_IMAGE_TYPE_2D,
            .format = format, .extent = {image_width,image_height,1}, .mipLevels = 1, .arrayLayers = 1,
            .samples = VK_SAMPLE_COUNT_1_BIT, .tiling = VK_IMAGE_TILING_OPTIMAL,
            .usage = VK_IMAGE_USAGE_STORAGE_BIT, .sharingMode = VK_SHARING_MODE_EXCLUSIVE };
        Vk(vkCreateImage(device, &info, nullptr, &image), "create GPU resize image");
        VkMemoryRequirements requirements;
        vkGetImageMemoryRequirements(device, image, &requirements);
        allocation = allocate(requirements, VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT);
        Vk(vkBindImageMemory(device, image, allocation, 0), "bind GPU resize image");
        VkImageViewCreateInfo view_info { .sType = VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO, .image = image,
            .viewType = VK_IMAGE_VIEW_TYPE_2D, .format = format,
            .subresourceRange = {VK_IMAGE_ASPECT_COLOR_BIT,0,1,0,1} };
        Vk(vkCreateImageView(device, &view_info, nullptr, &view), "view GPU resize image");
    }

    void waitConversion() {
        if (!conversion_pending) return;
        Vk(vkWaitForFences(device, 1, &fence, VK_TRUE, timeout_ns), "wait before reusing GPU conversion resources");
        conversion_pending = false;
        if (nv_pending) nvSubmit();
    }

    void Nv(NVENCSTATUS status, const char* operation) {
        if (status != NV_ENC_SUCCESS)
            throw std::runtime_error(std::string(operation) + ": NVENCSTATUS=" + std::to_string(status) + " " +
                                     (nvenc ? nv.nvEncGetLastErrorString(nvenc) : ""));
    }
    void Cu(CUresult result, const char* operation) {
        if (result != CUDA_SUCCESS) throw std::runtime_error(std::string(operation) + ": CUresult=" + std::to_string(result));
    }

    // 加载驱动库、按 LUID 找到同一块卡并开 NVENC 会话；任何一步不可用返回 false，调用方改走 Vulkan Video。
    bool openNvenc(std::span<const std::string> device_extensions, bool hevc, int qp) {
        if (!hevc || std::find(device_extensions.begin(), device_extensions.end(),
                               VK_KHR_EXTERNAL_MEMORY_WIN32_EXTENSION_NAME) == device_extensions.end() ||
            cuda_load_functions(&cu, nullptr) || nvenc_load_functions(&nvenc_dl, nullptr) || cu->cuInit(0) != CUDA_SUCCESS)
            return false;
        VkPhysicalDeviceIDProperties id { .sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_ID_PROPERTIES };
        VkPhysicalDeviceProperties2 properties { .sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PROPERTIES_2, .pNext = &id };
        vkGetPhysicalDeviceProperties2(gpu, &properties);
        int count = 0;
        if (!id.deviceLUIDValid || cu->cuDeviceGetCount(&count) != CUDA_SUCCESS) return false;
        for (int i = 0; i < count && !cuda; ++i) {
            CUdevice candidate {}; char luid[8] {}; unsigned mask {};
            if (cu->cuDeviceGet(&candidate, i) == CUDA_SUCCESS && cu->cuDeviceGetLuid(luid, &mask, candidate) == CUDA_SUCCESS &&
                std::memcmp(luid, id.deviceLUID, 8) == 0 && cu->cuCtxCreate(&cuda, 0, candidate) == CUDA_SUCCESS)
                cu->cuCtxPopCurrent(nullptr);
        }
        NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS open { .version = NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS_VER,
            .deviceType = NV_ENC_DEVICE_TYPE_CUDA, .device = cuda, .apiVersion = NVENCAPI_VERSION };
        if (!cuda || nvenc_dl->NvEncodeAPICreateInstance(&nv) != NV_ENC_SUCCESS ||
            nv.nvEncOpenEncodeSessionEx(&open, &nvenc) != NV_ENC_SUCCESS) { nvenc = nullptr; return false; }
        NV_ENC_PRESET_CONFIG preset { .version = NV_ENC_PRESET_CONFIG_VER, .presetCfg = { .version = NV_ENC_CONFIG_VER } };
        Nv(nv.nvEncGetEncodePresetConfigEx(nvenc, NV_ENC_CODEC_HEVC_GUID, NV_ENC_PRESET_P3_GUID, NV_ENC_TUNING_INFO_HIGH_QUALITY, &preset),
           "read NVENC preset");
        auto config = preset.presetCfg;
        config.gopLength = 250; config.frameIntervalP = 1;
        config.rcParams.rateControlMode = NV_ENC_PARAMS_RC_CONSTQP;
        config.rcParams.constQP = { std::uint32_t(qp), std::uint32_t(qp), std::uint32_t(qp) };
        auto& hevc_config = config.encodeCodecConfig.hevcConfig;
        hevc_config.idrPeriod = 250;
        auto& vui = hevc_config.hevcVUIParameters;
        vui.videoSignalTypePresentFlag = 1; vui.videoFormat = NV_ENC_VUI_VIDEO_FORMAT_UNSPECIFIED; vui.videoFullRangeFlag = 0;
        vui.colourDescriptionPresentFlag = 1; vui.colourPrimaries = NV_ENC_VUI_COLOR_PRIMARIES_BT709;
        vui.transferCharacteristics = NV_ENC_VUI_TRANSFER_CHARACTERISTIC_BT709; vui.colourMatrix = NV_ENC_VUI_MATRIX_COEFFS_BT709;
        NV_ENC_INITIALIZE_PARAMS init { .version = NV_ENC_INITIALIZE_PARAMS_VER, .encodeGUID = NV_ENC_CODEC_HEVC_GUID,
            .presetGUID = NV_ENC_PRESET_P3_GUID, .encodeWidth = output_width, .encodeHeight = output_height,
            .darWidth = output_width, .darHeight = output_height,
            .frameRateNum = std::uint32_t(codec->framerate.num), .frameRateDen = std::uint32_t(codec->framerate.den),
            .enablePTD = 1, .encodeConfig = &config, .maxEncodeWidth = output_width, .maxEncodeHeight = output_height,
            .tuningInfo = NV_ENC_TUNING_INFO_HIGH_QUALITY };
        // 强制三条带：4K HEVC p3 单会话 2.3 → 5.7 GP/s（runs/ARCHN/bench.json）；驱动不收（如极扁的尺寸）就退回自动
        init.splitEncodeMode = NV_ENC_SPLIT_THREE_FORCED_MODE;
        if (nv.nvEncInitializeEncoder(nvenc, &init) != NV_ENC_SUCCESS) {
            init.splitEncodeMode = NV_ENC_SPLIT_AUTO_MODE;
            Nv(nv.nvEncInitializeEncoder(nvenc, &init), "initialize NVENC HEVC");
        }
        std::array<std::uint8_t, 1024> header {};
        std::uint32_t header_size = 0;
        NV_ENC_SEQUENCE_PARAM_PAYLOAD sequence { .version = NV_ENC_SEQUENCE_PARAM_PAYLOAD_VER,
            .inBufferSize = header.size(), .spsppsBuffer = header.data(), .outSPSPPSPayloadSize = &header_size };
        Nv(nv.nvEncGetSequenceParams(nvenc, &sequence), "read NVENC parameter sets");
        codec->extradata = static_cast<std::uint8_t*>(av_mallocz(header_size + AV_INPUT_BUFFER_PADDING_SIZE));
        if (!codec->extradata) throw std::bad_alloc();
        std::memcpy(codec->extradata, header.data(), header_size);
        codec->extradata_size = static_cast<int>(header_size);
        stride = (output_width + 255u) & ~255u;
        return true;
    }

    // 转换输出的环形缓冲：一块可导出的专用显存，导入 CUDA 后每槽登记为 NVENC 输入。
    void exportRing() {
        nv_slot_bytes = (VkDeviceSize(stride) * output_height * 3 / 2 + 255) & ~VkDeviceSize(255);
        VkExternalMemoryBufferCreateInfo external { .sType = VK_STRUCTURE_TYPE_EXTERNAL_MEMORY_BUFFER_CREATE_INFO,
            .handleTypes = VK_EXTERNAL_MEMORY_HANDLE_TYPE_OPAQUE_WIN32_BIT };
        VkBufferCreateInfo info { .sType = VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO, .pNext = &external,
            .size = nv_slot_bytes * nv_ring, .usage = VK_BUFFER_USAGE_STORAGE_BUFFER_BIT, .sharingMode = VK_SHARING_MODE_EXCLUSIVE };
        Vk(vkCreateBuffer(device, &info, nullptr, &nv12), "create NVENC input ring");
        VkMemoryRequirements requirements;
        vkGetBufferMemoryRequirements(device, nv12, &requirements);
        VkPhysicalDeviceMemoryProperties properties;
        vkGetPhysicalDeviceMemoryProperties(gpu, &properties);
        std::uint32_t type = 0;
        while (type < properties.memoryTypeCount && (!(requirements.memoryTypeBits & (1u << type)) ||
               !(properties.memoryTypes[type].propertyFlags & VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT))) ++type;
        VkExportMemoryAllocateInfo exported { .sType = VK_STRUCTURE_TYPE_EXPORT_MEMORY_ALLOCATE_INFO,
            .handleTypes = VK_EXTERNAL_MEMORY_HANDLE_TYPE_OPAQUE_WIN32_BIT };
        VkMemoryDedicatedAllocateInfo dedicated { .sType = VK_STRUCTURE_TYPE_MEMORY_DEDICATED_ALLOCATE_INFO,
            .pNext = &exported, .buffer = nv12 };
        VkMemoryAllocateInfo allocation { .sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO, .pNext = &dedicated,
            .allocationSize = requirements.size, .memoryTypeIndex = type };
        Vk(vkAllocateMemory(device, &allocation, nullptr, &memory), "allocate NVENC input ring");
        Vk(vkBindBufferMemory(device, nv12, memory, 0), "bind NVENC input ring");
        auto get_handle = reinterpret_cast<PFN_vkGetMemoryWin32HandleKHR>(vkGetDeviceProcAddr(device, "vkGetMemoryWin32HandleKHR"));
        VkMemoryGetWin32HandleInfoKHR handle_info { .sType = VK_STRUCTURE_TYPE_MEMORY_GET_WIN32_HANDLE_INFO_KHR,
            .memory = memory, .handleType = VK_EXTERNAL_MEMORY_HANDLE_TYPE_OPAQUE_WIN32_BIT };
        HANDLE handle {};
        Vk(get_handle ? get_handle(device, &handle_info, &handle) : VK_ERROR_EXTENSION_NOT_PRESENT, "export NVENC input ring");
        Cu(cu->cuCtxPushCurrent(cuda), "bind CUDA context");
        CUDA_EXTERNAL_MEMORY_HANDLE_DESC imported {};
        imported.type = CU_EXTERNAL_MEMORY_HANDLE_TYPE_OPAQUE_WIN32; imported.handle.win32.handle = handle;
        imported.size = requirements.size; imported.flags = 1; // CUDA_EXTERNAL_MEMORY_DEDICATED
        CUresult result = cu->cuImportExternalMemory(&cuda_memory, &imported);
        CloseHandle(handle);
        CUDA_EXTERNAL_MEMORY_BUFFER_DESC mapped_ring {};
        mapped_ring.size = nv_slot_bytes * nv_ring;
        if (result == CUDA_SUCCESS) result = cu->cuExternalMemoryGetMappedBuffer(&cuda_base, cuda_memory, &mapped_ring);
        cu->cuCtxPopCurrent(nullptr);
        Cu(result, "import NVENC input ring into CUDA");
        for (std::uint32_t slot = 0; slot < nv_ring; ++slot) {
            NV_ENC_REGISTER_RESOURCE resource { .version = NV_ENC_REGISTER_RESOURCE_VER,
                .resourceType = NV_ENC_INPUT_RESOURCE_TYPE_CUDADEVICEPTR, .width = output_width, .height = output_height,
                .pitch = stride, .resourceToRegister = reinterpret_cast<void*>(cuda_base + slot * nv_slot_bytes),
                .bufferFormat = NV_ENC_BUFFER_FORMAT_NV12, .bufferUsage = NV_ENC_INPUT_IMAGE };
            Nv(nv.nvEncRegisterResource(nvenc, &resource), "register NVENC input");
            nv_registered[slot] = resource.registeredResource;
            NV_ENC_CREATE_BITSTREAM_BUFFER bitstream { .version = NV_ENC_CREATE_BITSTREAM_BUFFER_VER };
            Nv(nv.nvEncCreateBitstreamBuffer(nvenc, &bitstream), "create NVENC bitstream buffer");
            nv_bitstreams[slot] = bitstream.bitstreamBuffer;
        }
    }

    void nvSubmit() {
        nv_pending = false;
        const auto slot = nv_sent % nv_ring;
        NV_ENC_MAP_INPUT_RESOURCE map { .version = NV_ENC_MAP_INPUT_RESOURCE_VER, .registeredResource = nv_registered[slot] };
        Nv(nv.nvEncMapInputResource(nvenc, &map), "map NVENC input");
        nv_inputs[slot] = map.mappedResource;
        NV_ENC_PIC_PARAMS picture { .version = NV_ENC_PIC_PARAMS_VER, .inputWidth = output_width, .inputHeight = output_height,
            .inputPitch = stride, .encodePicFlags = nv_pending_idr ? std::uint32_t(NV_ENC_PIC_FLAG_FORCEIDR | NV_ENC_PIC_FLAG_OUTPUT_SPSPPS) : 0u,
            .inputTimeStamp = std::uint64_t(nv_pending_pts), .inputBuffer = map.mappedResource,
            .outputBitstream = nv_bitstreams[slot], .bufferFmt = NV_ENC_BUFFER_FORMAT_NV12,
            .pictureStruct = NV_ENC_PIC_STRUCT_FRAME };
        Nv(nv.nvEncEncodePicture(nvenc, &picture), "submit NVENC frame");
        ++nv_sent;
    }

    // 取回最早一帧的码流（阻塞到它编完）并放回它的输入槽
    void nvCollect() {
        const auto slot = nv_collected % nv_ring;
        NV_ENC_LOCK_BITSTREAM lock { .version = NV_ENC_LOCK_BITSTREAM_VER, .outputBitstream = nv_bitstreams[slot] };
        Nv(nv.nvEncLockBitstream(nvenc, &lock), "lock NVENC bitstream");
        const int result = av_new_packet(packet, static_cast<int>(lock.bitstreamSizeInBytes));
        if (result >= 0) std::memcpy(packet->data, lock.bitstreamBufferPtr, lock.bitstreamSizeInBytes);
        nv.nvEncUnlockBitstream(nvenc, nv_bitstreams[slot]);
        Av(result, "allocate NVENC packet");
        packet->pts = packet->dts = static_cast<std::int64_t>(lock.outputTimeStamp);
        if (lock.pictureType == NV_ENC_PIC_TYPE_IDR) packet->flags |= AV_PKT_FLAG_KEY;
        Nv(nv.nvEncUnmapInputResource(nvenc, nv_inputs[slot]), "unmap NVENC input");
        nv_inputs[slot] = nullptr;
        ++nv_collected;
        writePacket();
    }

    void beginCapture() {
        waitConversion();
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
        bool window=capture.retain_loop_window && (index<capture.crossfade_frames ||
            (index>=capture.encoded_frames && index-capture.encoded_frames<capture.crossfade_frames));
        if (!first && !keep && !window) return;
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
        if (window) {
            loop_window.write(static_cast<const char*>(mapped),bytes);
            if (!loop_window) throw std::runtime_error("write original GPU loop window");
            ++loop_window_count;
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
    p.width = width; p.height = height;
    p.capture = std::move(capture);
    p.capture.crop_width = p.capture.crop_width ? p.capture.crop_width : width;
    p.capture.crop_height = p.capture.crop_height ? p.capture.crop_height : height;
    if (!p.capture.crop_width || !p.capture.crop_height ||
        std::uint64_t(p.capture.crop_x)+p.capture.crop_width>width ||
        std::uint64_t(p.capture.crop_y)+p.capture.crop_height>height ||
        p.capture.crossfade_frames>=p.capture.encoded_frames || p.capture.crossfade_frames>UINT32_MAX/255u-1u)
        throw std::runtime_error("GPU crop or loop crossfade lies outside its captured interval");
    if ((p.capture.resize_width==0)!=(p.capture.resize_height==0) ||
        p.capture.resize_width>p.capture.crop_width || p.capture.resize_height>p.capture.crop_height ||
        ((p.capture.resize_width|p.capture.resize_height)&1u))
        throw std::runtime_error("GPU resize requires paired even dimensions no larger than the crop");
    p.resizing = p.capture.resize_width &&
        (p.capture.resize_width!=p.capture.crop_width || p.capture.resize_height!=p.capture.crop_height);
    if (!p.resizing && ((p.capture.crop_x|p.capture.crop_y|p.capture.crop_width|p.capture.crop_height)&1u))
        throw std::runtime_error("GPU encoding without resize requires even crop coordinates and dimensions");
    if (p.resizing && p.capture.crossfade_frames)
        throw std::runtime_error("GPU resize cannot be combined with loop crossfade");
    p.color_width = p.resizing ? p.capture.resize_width : p.capture.crop_width;
    p.output_height = p.resizing ? p.capture.resize_height : p.capture.crop_height;
    if (p.color_width>std::uint32_t(INT32_MAX)/(packed_alpha ? 2u : 1u) || p.output_height>INT32_MAX)
        throw std::runtime_error("GPU encoder dimensions exceed the codec range");
    p.output_width = p.color_width * (packed_alpha ? 2 : 1);
    p.stride = (p.output_width + 3u) & ~3u;
    p.directory = std::filesystem::u8path(path).parent_path();
    p.output_path = path;
    if (!p.capture.encoded_frames || p.capture.retain_frames.size() > 32 ||
        !std::is_sorted(p.capture.retain_frames.begin(), p.capture.retain_frames.end()) ||
        std::adjacent_find(p.capture.retain_frames.begin(), p.capture.retain_frames.end()) != p.capture.retain_frames.end())
        throw std::runtime_error("Invalid GPU capture frame selection");
    if (!width || !height || !fps_num || !fps_den ||
        fps_num > INT32_MAX || fps_den > INT32_MAX || qp < 0 || qp > 51 ||
        (codec_name != "h264_vulkan" && codec_name != "hevc_vulkan"))
        throw std::runtime_error("GPU encoding requires positive capture dimensions, a rational FPS and Vulkan H.264/HEVC");
    vkGetDeviceQueue(device, graphics_family, 0, &p.queue);
    p.push_descriptors = reinterpret_cast<PFN_vkCmdPushDescriptorSetKHR>(vkGetDeviceProcAddr(device, "vkCmdPushDescriptorSetKHR"));
    if (!p.push_descriptors) throw std::runtime_error("GPU conversion requires push descriptors");
    p.hardware = av_hwdevice_ctx_alloc(AV_HWDEVICE_TYPE_VULKAN);
    if (!p.hardware) throw std::bad_alloc();
    auto* hw = reinterpret_cast<AVHWDeviceContext*>(p.hardware->data);
    auto* vk = reinterpret_cast<AVVulkanDeviceContext*>(hw->hwctx);
    vk->get_proc_addr = PinnedInstanceProc;
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
        // Device::ChooseDeviceQueue 给编码族建了两条队列；FFmpeg 只用本进程那一条（见 PinnedDeviceQueue）。
        const auto& family = families[i].queueFamilyProperties;
        if ((family.queueFlags & VK_QUEUE_VIDEO_ENCODE_BIT_KHR) && family.queueCount > 1) {
            pinned_encode_family = i;
            static const std::uint32_t queue = EncodeQueueForThisProcess();
            pinned_encode_queue = queue;
        }
        vk->qf[vk->nb_qf++] = { static_cast<int>(i), 1,
            static_cast<VkQueueFlagBits>(families[i].queueFamilyProperties.queueFlags),
            static_cast<VkVideoCodecOperationFlagBitsKHR>(video[i].videoCodecOperations) };
    }
    Av(av_hwdevice_ctx_init(p.hardware), "wrap renderer Vulkan device");
    p.frames = av_hwframe_ctx_alloc(p.hardware);
    if (!p.frames) throw std::bad_alloc();
    auto* frames = reinterpret_cast<AVHWFramesContext*>(p.frames->data);
    frames->format = AV_PIX_FMT_VULKAN; frames->sw_format = AV_PIX_FMT_NV12;
    frames->width = static_cast<int>(p.output_width); frames->height = static_cast<int>(p.output_height);
    auto* vkframes = reinterpret_cast<AVVulkanFramesContext*>(frames->hwctx);
    vkframes->usage = static_cast<VkImageUsageFlagBits>(VK_IMAGE_USAGE_VIDEO_ENCODE_SRC_BIT_KHR | VK_IMAGE_USAGE_TRANSFER_DST_BIT);
    vkframes->img_flags = EncoderInputFlags(instance, gpu, codec_name == "hevc_vulkan");
    Av(av_hwframe_ctx_init(p.frames), "allocate Vulkan encoder frame pool");
    if (vkframes->format[0] != VK_FORMAT_G8_B8R8_2PLANE_420_UNORM)
        throw std::runtime_error("Vulkan encoder needs one multiplane NV12 image");
    const auto* encoder = avcodec_find_encoder_by_name(codec_name.c_str());
    if (!encoder) throw std::runtime_error("Vulkan encoder is absent from libavcodec");
    p.codec = avcodec_alloc_context3(encoder);
    if (!p.codec) throw std::bad_alloc();
    p.codec->width = static_cast<int>(p.output_width); p.codec->height = static_cast<int>(p.output_height);
    p.codec->pix_fmt = AV_PIX_FMT_VULKAN; p.codec->sw_pix_fmt = AV_PIX_FMT_NV12;
    p.codec->time_base = { static_cast<int>(fps_den), static_cast<int>(fps_num) };
    p.codec->framerate = { static_cast<int>(fps_num), static_cast<int>(fps_den) };
    p.codec->gop_size = 250; p.codec->max_b_frames = 0;
    p.codec->color_range = AVCOL_RANGE_MPEG; p.codec->colorspace = AVCOL_SPC_BT709;
    p.codec->color_primaries = AVCOL_PRI_BT709; p.codec->color_trc = AVCOL_TRC_BT709;
    p.codec->hw_frames_ctx = av_buffer_ref(p.frames);
    p.codec->flags |= AV_CODEC_FLAG_GLOBAL_HEADER;
    Av(av_opt_set_int(p.codec->priv_data, "qp", qp, 0), "set Vulkan encoder QP");
    Av(av_opt_get_int(p.codec->priv_data, "quality", 0, &p.quality_level), "read Vulkan encoder quality level");
    Av(av_opt_get_int(p.codec->priv_data, "async_depth", 0, &p.async_depth), "read Vulkan encoder depth");
    if (!p.openNvenc(device_extensions, codec_name == "hevc_vulkan", qp))
        Av(avcodec_open2(p.codec, encoder, nullptr), "open Vulkan encoder");
    if (p.capture.crossfade_frames) {
        p.body_path=path+".body.mp4"; p.head_path=path+".head.mp4";
        p.openMux(p.body_path,p.mux,p.stream);
        p.openMux(p.head_path,p.head_mux,p.head_stream);
        VkPhysicalDeviceProperties properties;
        vkGetPhysicalDeviceProperties(gpu,&properties);
        const auto frame_bytes=std::uint64_t(p.color_width)*p.output_height*4;
        if (frame_bytes>properties.limits.maxStorageBufferRange)
            throw std::runtime_error("GPU loop reference exceeds the storage-buffer range");
        const auto alignment=std::max<VkDeviceSize>(4,properties.limits.minStorageBufferOffsetAlignment);
        p.loop_head_stride=(frame_bytes+alignment-1)/alignment*alignment;
        p.makeBuffer(p.loop_head_stride*p.capture.crossfade_frames,
            VK_BUFFER_USAGE_STORAGE_BUFFER_BIT,VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT,p.loop_head,p.loop_head_memory);
    } else p.openMux(path,p.mux,p.stream);
    p.frame = av_frame_alloc(); p.packet = av_packet_alloc();
    if (!p.frame || !p.packet) throw std::bad_alloc();

    if (p.nvenc) p.exportRing();
    else p.makeBuffer(std::max<std::uint64_t>(32,std::uint64_t(p.stride) * p.output_height * 3 / 2),
        VK_BUFFER_USAGE_STORAGE_BUFFER_BIT | VK_BUFFER_USAGE_TRANSFER_SRC_BIT,
        VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT, p.nv12, p.memory);
    if (p.capture.collect_bounds || !p.capture.retain_frames.empty() || p.capture.retain_loop_window) {
        p.makeBuffer(std::max<std::uint64_t>(32, std::uint64_t(width) * height * 4), VK_BUFFER_USAGE_TRANSFER_DST_BIT,
            VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT, p.readback, p.readback_memory,
            VK_MEMORY_PROPERTY_HOST_CACHED_BIT); // CPU reads of uncached memory ran at ~156 MB/s (8K retained frames)
        Vk(vkMapMemory(device, p.readback_memory, 0, VK_WHOLE_SIZE, 0, &p.mapped), "map selected-frame readback");
    }
    if (!p.capture.retain_frames.empty()) {
        p.retained.open(p.directory / "retained-frames.rgba", std::ios::binary);
        if (!p.retained) throw std::runtime_error("open retained GPU frames");
    }
    if (p.capture.retain_loop_window) {
        if (!p.capture.crossfade_frames) throw std::runtime_error("Original loop windows require a crossfade");
        p.loop_window.open(p.directory/"loop-window.rgba",std::ios::binary);
        if (!p.loop_window) throw std::runtime_error("open original GPU loop window");
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
    if (p.resizing) {
        p.makeResizeImage(p.color_width,p.capture.crop_height,VK_FORMAT_R32G32B32A32_SFLOAT,
            p.horizontal_image,p.horizontal_view,p.horizontal_memory);
        p.makeResizeImage(p.color_width,p.output_height,VK_FORMAT_R8G8B8A8_UNORM,
            p.resized_image,p.resized_view,p.resized_memory);
    }

    const ShaderCompUnit unit { ShaderType::COMPUTE, R"glsl(#version 450
layout(local_size_x=8, local_size_y=8) in;
layout(binding=0, rgba8) readonly uniform image2D sourceImage;
layout(binding=1, std430) writeonly buffer Output { uint words[]; } outputData;
layout(binding=2, rgba8) readonly uniform image2D firstImage;
layout(binding=3, std430) buffer Statistics { uint data[8]; } stats;
layout(binding=4, std430) buffer LoopHead { uint rgba[]; } loopHead;
layout(binding=6, rgba8) readonly uniform image2D resizedImage;
layout(push_constant) uniform Dimensions {
    uint width; uint height; uint colorWidth; uint outputHeight; uint outputWidth; uint stride;
    uint cropX; uint cropY; uint flags; uint blendNumerator; uint blendDenominator;
} dims;
shared uint blockStats[8];
shared uint compareFirst;
vec3 rgb(uint x, uint y) {
    uvec2 q=uvec2(min(x,dims.outputWidth-1u)%dims.colorWidth,min(y,dims.outputHeight-1u));
    vec4 p=(dims.flags&16u)!=0u ? imageLoad(resizedImage,ivec2(q)) :
        imageLoad(sourceImage,ivec2(q+uvec2(dims.cropX,dims.cropY)));
    if ((dims.flags&8u)!=0u) {
        uint stored=loopHead.rgba[q.y*dims.colorWidth+q.x];
        uvec4 head=uvec4(stored&255u,(stored>>8u)&255u,(stored>>16u)&255u,stored>>24u);
        uvec4 tail=uvec4(p*255.0+0.5);
        // Same per-channel truncation as the lossless RGB/alpha blend, before chroma conversion.
        p=vec4((head*(dims.blendDenominator-dims.blendNumerator)+tail*dims.blendNumerator)/dims.blendDenominator)/255.0;
    }
    return x < dims.colorWidth ? p.rgb : vec3(p.a);
}
uint byteValue(float x) { return uint(clamp(round(x),0.0,255.0)); }
float luma(vec3 p) { return 16.0 + 219.0 * dot(p,vec3(0.2126,0.7152,0.0722)); }
uvec2 chroma(vec3 p) { return uvec2(byteValue(128.0+224.0*dot(p,vec3(-0.114572,-0.385428,0.5))),
                                      byteValue(128.0+224.0*dot(p,vec3(0.5,-0.454153,-0.045847)))); }
void main() {
    uint x = gl_GlobalInvocationID.x*4u, y = gl_GlobalInvocationID.y;
    bool insideImage = x < dims.stride && y < dims.outputHeight;
    if ((dims.flags&4u)!=0u && y<dims.outputHeight) {
        for (uint k=0u;k<4u && x+k<dims.colorWidth;++k) {
            uvec4 p=uvec4(imageLoad(sourceImage,ivec2(x+k+dims.cropX,y+dims.cropY))*255.0+0.5);
            loopHead.rgba[y*dims.colorWidth+x+k]=p.r|(p.g<<8u)|(p.b<<16u)|(p.a<<24u);
        }
    }
    if ((dims.flags&1u)!=0u) {
        if (gl_LocalInvocationIndex==0u) {
            blockStats[0]=dims.width; blockStats[1]=dims.height; blockStats[2]=0u; blockStats[3]=0u;
            blockStats[4]=255u; blockStats[5]=0u; blockStats[6]=0u; blockStats[7]=0u;
            compareFirst = atomicOr(stats.data[6],0u)==0u ? 1u : 0u;
        }
        barrier();
        if (y<dims.height && x<dims.width) {
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
    // Initial loop-head frames are only cached; they are encoded after blending.
    if (!insideImage || (dims.flags&4u)!=0u) return;
    uvec4 Y = uvec4(byteValue(luma(rgb(x,y))),byteValue(luma(rgb(x+1u,y))),
                   byteValue(luma(rgb(x+2u,y))),byteValue(luma(rgb(x+3u,y))));
    outputData.words[(y*dims.stride+x)/4u] = Y.x | (Y.y<<8u) | (Y.z<<16u) | (Y.w<<24u);
    if ((y&1u)==0u) {
        uvec2 a = chroma((rgb(x,y)+rgb(x+1u,y)+rgb(x,y+1u)+rgb(x+1u,y+1u))*0.25);
        uvec2 b = chroma((rgb(x+2u,y)+rgb(x+3u,y)+rgb(x+2u,y+1u)+rgb(x+3u,y+1u))*0.25);
        outputData.words[(dims.outputHeight*dims.stride+y/2u*dims.stride+x)/4u] = a.x | (a.y<<8u) | (b.x<<16u) | (b.y<<24u);
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
    const std::array<VkDescriptorSetLayoutBinding, 7> bindings {{
        {0,VK_DESCRIPTOR_TYPE_STORAGE_IMAGE,1,VK_SHADER_STAGE_COMPUTE_BIT,nullptr},
        {1,VK_DESCRIPTOR_TYPE_STORAGE_BUFFER,1,VK_SHADER_STAGE_COMPUTE_BIT,nullptr},
        {2,VK_DESCRIPTOR_TYPE_STORAGE_IMAGE,1,VK_SHADER_STAGE_COMPUTE_BIT,nullptr},
        {3,VK_DESCRIPTOR_TYPE_STORAGE_BUFFER,1,VK_SHADER_STAGE_COMPUTE_BIT,nullptr},
        {4,VK_DESCRIPTOR_TYPE_STORAGE_BUFFER,1,VK_SHADER_STAGE_COMPUTE_BIT,nullptr},
        {5,VK_DESCRIPTOR_TYPE_STORAGE_IMAGE,1,VK_SHADER_STAGE_COMPUTE_BIT,nullptr},
        {6,VK_DESCRIPTOR_TYPE_STORAGE_IMAGE,1,VK_SHADER_STAGE_COMPUTE_BIT,nullptr} }};
    VkDescriptorSetLayoutCreateInfo descriptors { .sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_CREATE_INFO,
        .flags = VK_DESCRIPTOR_SET_LAYOUT_CREATE_PUSH_DESCRIPTOR_BIT_KHR, .bindingCount = 7, .pBindings = bindings.data() };
    Vk(vkCreateDescriptorSetLayout(device, &descriptors, nullptr, &p.descriptors), "create GPU conversion descriptors");
    VkPushConstantRange constants { VK_SHADER_STAGE_COMPUTE_BIT, 0, 44 };
    VkPipelineLayoutCreateInfo layout { .sType = VK_STRUCTURE_TYPE_PIPELINE_LAYOUT_CREATE_INFO,
        .setLayoutCount = 1, .pSetLayouts = &p.descriptors, .pushConstantRangeCount = 1, .pPushConstantRanges = &constants };
    Vk(vkCreatePipelineLayout(device, &layout, nullptr, &p.layout), "create GPU conversion layout");
    VkComputePipelineCreateInfo pipeline { .sType = VK_STRUCTURE_TYPE_COMPUTE_PIPELINE_CREATE_INFO,
        .stage = { .sType = VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO,
                   .stage = VK_SHADER_STAGE_COMPUTE_BIT, .module = p.shader, .pName = "main" }, .layout = p.layout };
    Vk(vkCreateComputePipelines(device, VK_NULL_HANDLE, 1, &pipeline, nullptr, &p.pipeline), "create GPU conversion pipeline");
    if (p.resizing) {
        // Pixel-center mapping and scale-widened sinc/sinc support match a
        // separable Lanczos3 downsample. Keep negative lobes through the float
        // horizontal pass; clamp/quantize only after filtering both axes.
        const ShaderCompUnit resize_unit { ShaderType::COMPUTE, R"glsl(#version 450
layout(local_size_x=8, local_size_y=8) in;
layout(binding=0, rgba8) readonly uniform image2D sourceImage;
layout(binding=5, rgba32f) uniform image2D horizontalImage;
layout(binding=6, rgba8) writeonly uniform image2D resizedImage;
layout(push_constant) uniform Resize {
    uint cropX; uint cropY; uint cropWidth; uint cropHeight;
    uint width; uint height; uint vertical;
} dims;
float lanczos3(float distance) {
    float x=abs(distance);
    if (x<0.000001) return 1.0;
    if (x>=3.0) return 0.0;
    float angle=3.14159265358979323846*x;
    return sin(angle)*sin(angle/3.0)/(angle*angle/3.0);
}
void main() {
    uvec2 q=gl_GlobalInvocationID.xy;
    bool vertical=dims.vertical!=0u;
    if (q.x>=dims.width || q.y>=(vertical ? dims.height : dims.cropHeight)) return;
    uint sourceSize=vertical ? dims.cropHeight : dims.cropWidth;
    uint targetSize=vertical ? dims.height : dims.width;
    vec4 value;
    if (sourceSize==targetSize) {
        value=vertical ? imageLoad(horizontalImage,ivec2(q)) :
            imageLoad(sourceImage,ivec2(q+uvec2(dims.cropX,dims.cropY)));
    } else {
        float scale=float(sourceSize)/float(targetSize);
        float center=(float(vertical ? q.y : q.x)+0.5)*scale-0.5;
        float support=3.0*scale;
        vec4 sum=vec4(0.0);
        float weights=0.0;
        int last=int(floor(center+support));
        for (int tap=int(ceil(center-support));tap<=last;++tap) {
            float weight=lanczos3((float(tap)-center)/scale);
            int sampleIndex=clamp(tap,0,int(sourceSize)-1);
            vec4 sampleValue=vertical ? imageLoad(horizontalImage,ivec2(q.x,sampleIndex)) :
                imageLoad(sourceImage,ivec2(sampleIndex+int(dims.cropX),int(q.y+dims.cropY)));
            sum+=sampleValue*weight;
            weights+=weight;
        }
        value=sum/weights;
    }
    if (vertical) imageStore(resizedImage,ivec2(q),floor(clamp(value,0.0,1.0)*255.0+0.5)/255.0);
    else imageStore(horizontalImage,ivec2(q),value);
}
)glsl", "main" };
        std::vector<Uni_ShaderSpv> resize_code;
        if (!CompileAndLinkShaderUnits(std::span(&resize_unit,1),ShaderCompOpt {},resize_code) || resize_code.size()!=1)
            throw std::runtime_error("compile GPU Lanczos3 resize");
        const auto& resize_words=resize_code.front()->spirv;
        VkShaderModuleCreateInfo resize_shader { .sType=VK_STRUCTURE_TYPE_SHADER_MODULE_CREATE_INFO,
            .codeSize=resize_words.size()*sizeof(unsigned int), .pCode=resize_words.data() };
        Vk(vkCreateShaderModule(device,&resize_shader,nullptr,&p.resize_shader),"create GPU resize shader");
        pipeline.stage.module=p.resize_shader;
        Vk(vkCreateComputePipelines(device,VK_NULL_HANDLE,1,&pipeline,nullptr,&p.resize_pipeline),"create GPU resize pipeline");
    }
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

void GpuVideoEncoder::waitConversion() { impl->waitConversion(); }

void GpuVideoEncoder::encode(VkImage rgba, std::uint64_t index, bool asynchronous) {
    auto& p = *impl;
    if (p.finished) throw std::runtime_error("GPU encoder already finished");
    p.waitConversion();
    const auto fade=p.capture.crossfade_frames;
    if (index>=p.capture.encoded_frames+fade) { p.retain(rgba,index); return; }
    const bool cache_head=fade && index<fade;
    const bool blend_head=fade && index>=p.capture.encoded_frames;
    const auto head_index=blend_head ? index-p.capture.encoded_frames : cache_head ? index : 0;
    ++p.observed_frames;
    AVVkFrame* vkframe {};
    auto* frames = reinterpret_cast<AVHWFramesContext*>(p.frames->data);
    auto* vkframes = reinterpret_cast<AVVulkanFramesContext*>(frames->hwctx);
    if (p.nvenc) {
        while (p.nv_sent - p.nv_collected >= p.nv_ring) p.nvCollect();
    } else {
        av_frame_unref(p.frame);
        Av(av_hwframe_get_buffer(p.frames, p.frame, 0), "get GPU encoder surface");
        vkframe = reinterpret_cast<AVVkFrame*>(p.frame->data[0]);
        // FFmpeg allocates shared graphics/encode images with CONCURRENT ownership.
        if (vkframe->img[1] || vkframe->queue_family[0] != VK_QUEUE_FAMILY_IGNORED)
            throw std::runtime_error("GPU encoder surface requires concurrent multiplane NV12");
    }
    if (rgba != p.source_image) {
        if (p.source_view) vkDestroyImageView(p.device, p.source_view, nullptr);
        p.source_view = VK_NULL_HANDLE;
        VkImageViewCreateInfo view_info { .sType = VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO,
            .image = rgba, .viewType = VK_IMAGE_VIEW_TYPE_2D, .format = VK_FORMAT_R8G8B8A8_UNORM,
            .subresourceRange = { VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1 } };
        Vk(vkCreateImageView(p.device, &view_info, nullptr, &p.source_view), "view rendered RGBA image");
        p.source_image = rgba;
    }
    if (vkframe) vkframes->lock_frame(frames, vkframe);
    try {
        Vk(vkResetCommandBuffer(p.command, 0), "reset GPU conversion command");
        VkCommandBufferBeginInfo begin { .sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO,
            .flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT };
        Vk(vkBeginCommandBuffer(p.command, &begin), "begin GPU conversion command");
        if (blend_head) {
            VkBufferMemoryBarrier head_ready { .sType=VK_STRUCTURE_TYPE_BUFFER_MEMORY_BARRIER,
                .srcAccessMask=VK_ACCESS_SHADER_WRITE_BIT,.dstAccessMask=VK_ACCESS_SHADER_READ_BIT,
                .srcQueueFamilyIndex=VK_QUEUE_FAMILY_IGNORED,.dstQueueFamilyIndex=VK_QUEUE_FAMILY_IGNORED,
                .buffer=p.loop_head,.offset=head_index*p.loop_head_stride,
                .size=std::uint64_t(p.color_width)*p.output_height*4 };
            vkCmdPipelineBarrier(p.command,VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT,VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT,
                0,0,nullptr,1,&head_ready,0,nullptr);
        }
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
        VkDescriptorImageInfo image { .imageView = p.source_view, .imageLayout = VK_IMAGE_LAYOUT_GENERAL };
        VkDescriptorBufferInfo buffer { .buffer = p.nv12, .offset = (p.nv_sent % p.nv_ring) * p.nv_slot_bytes,
            .range = p.nvenc ? p.nv_slot_bytes : VK_WHOLE_SIZE };
        VkDescriptorImageInfo first { .imageView=p.first_view ? p.first_view : p.source_view, .imageLayout=VK_IMAGE_LAYOUT_GENERAL };
        VkDescriptorBufferInfo stats { .buffer=p.statistics ? p.statistics : p.nv12, .offset=0, .range=32 };
        VkDescriptorBufferInfo loop { .buffer=p.loop_head ? p.loop_head : p.nv12,
            .offset=p.loop_head ? head_index*p.loop_head_stride : 0,
            .range=p.loop_head ? std::uint64_t(p.color_width)*p.output_height*4 : 32 };
        VkDescriptorImageInfo horizontal { .imageView=p.horizontal_view, .imageLayout=VK_IMAGE_LAYOUT_GENERAL };
        VkDescriptorImageInfo resized { .imageView=p.resized_view ? p.resized_view : p.source_view,
            .imageLayout=VK_IMAGE_LAYOUT_GENERAL };
        std::array<VkWriteDescriptorSet, 7> writes {{
            { .sType = VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET, .dstBinding = 0, .descriptorCount = 1,
              .descriptorType = VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, .pImageInfo = &image },
            { .sType = VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET, .dstBinding = 1, .descriptorCount = 1,
              .descriptorType = VK_DESCRIPTOR_TYPE_STORAGE_BUFFER, .pBufferInfo = &buffer },
            { .sType = VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET, .dstBinding = 2, .descriptorCount = 1,
              .descriptorType = VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, .pImageInfo = &first },
            { .sType = VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET, .dstBinding = 3, .descriptorCount = 1,
              .descriptorType = VK_DESCRIPTOR_TYPE_STORAGE_BUFFER, .pBufferInfo = &stats },
            { .sType=VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET,.dstBinding=4,.descriptorCount=1,
              .descriptorType=VK_DESCRIPTOR_TYPE_STORAGE_BUFFER,.pBufferInfo=&loop },
            { .sType=VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET,.dstBinding=6,.descriptorCount=1,
              .descriptorType=VK_DESCRIPTOR_TYPE_STORAGE_IMAGE,.pImageInfo=&resized },
            { .sType=VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET,.dstBinding=5,.descriptorCount=1,
              .descriptorType=VK_DESCRIPTOR_TYPE_STORAGE_IMAGE,.pImageInfo=&horizontal } }};
        p.push_descriptors(p.command, VK_PIPELINE_BIND_POINT_COMPUTE, p.layout, 0, p.resizing ? 7 : 6, writes.data());
        if (p.resizing) {
            std::array<VkImageMemoryBarrier,2> scratch;
            for (std::size_t i=0;i<scratch.size();++i) {
                scratch[i] = { .sType=VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER,
                    .srcAccessMask=index==0 ? VkAccessFlags(0) : VK_ACCESS_SHADER_READ_BIT|VK_ACCESS_SHADER_WRITE_BIT,
                    .dstAccessMask=VK_ACCESS_SHADER_WRITE_BIT,
                    .oldLayout=index==0 ? VK_IMAGE_LAYOUT_UNDEFINED : VK_IMAGE_LAYOUT_GENERAL,
                    .newLayout=VK_IMAGE_LAYOUT_GENERAL, .srcQueueFamilyIndex=VK_QUEUE_FAMILY_IGNORED,
                    .dstQueueFamilyIndex=VK_QUEUE_FAMILY_IGNORED, .image=i==0 ? p.horizontal_image : p.resized_image,
                    .subresourceRange={VK_IMAGE_ASPECT_COLOR_BIT,0,1,0,1} };
            }
            vkCmdPipelineBarrier(p.command,index==0 ? VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT : VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT,
                VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT,0,0,nullptr,0,nullptr,2,scratch.data());
            vkCmdBindPipeline(p.command,VK_PIPELINE_BIND_POINT_COMPUTE,p.resize_pipeline);
            std::array<std::uint32_t,7> resize_dimensions { p.capture.crop_x,p.capture.crop_y,
                p.capture.crop_width,p.capture.crop_height,p.color_width,p.output_height,0 };
            vkCmdPushConstants(p.command,p.layout,VK_SHADER_STAGE_COMPUTE_BIT,0,sizeof(resize_dimensions),resize_dimensions.data());
            vkCmdDispatch(p.command,(p.color_width+7)/8,(p.capture.crop_height+7)/8,1);
            scratch[0].srcAccessMask=VK_ACCESS_SHADER_WRITE_BIT;
            scratch[0].dstAccessMask=VK_ACCESS_SHADER_READ_BIT;
            scratch[0].oldLayout=VK_IMAGE_LAYOUT_GENERAL;
            vkCmdPipelineBarrier(p.command,VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT,VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT,
                0,0,nullptr,0,nullptr,1,&scratch[0]);
            resize_dimensions[6]=1;
            vkCmdPushConstants(p.command,p.layout,VK_SHADER_STAGE_COMPUTE_BIT,0,sizeof(resize_dimensions),resize_dimensions.data());
            vkCmdDispatch(p.command,(p.color_width+7)/8,(p.output_height+7)/8,1);
            scratch[1].srcAccessMask=VK_ACCESS_SHADER_WRITE_BIT;
            scratch[1].dstAccessMask=VK_ACCESS_SHADER_READ_BIT;
            scratch[1].oldLayout=VK_IMAGE_LAYOUT_GENERAL;
            vkCmdPipelineBarrier(p.command,VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT,VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT,
                0,0,nullptr,0,nullptr,1,&scratch[1]);
        }
        vkCmdBindPipeline(p.command,VK_PIPELINE_BIND_POINT_COMPUTE,p.pipeline);
        std::uint32_t flags=(p.capture.collect_bounds ? (p.capture.bounds_include_rgb ? 3u : 1u) : 0u) |
            (cache_head ? 4u : 0u) | (blend_head ? 8u : 0u) | (p.resizing ? 16u : 0u);
        std::array<std::uint32_t, 11> dimensions { p.width,p.height,p.color_width,p.output_height,p.output_width,p.stride,
            p.capture.crop_x,p.capture.crop_y,flags,blend_head ? fade-static_cast<std::uint32_t>(head_index) : 0u,fade+1 };
        vkCmdPushConstants(p.command, p.layout, VK_SHADER_STAGE_COMPUTE_BIT, 0, sizeof(dimensions), dimensions.data());
        const auto scan_width=p.capture.collect_bounds ? std::max(p.stride,p.width) : p.stride;
        const auto scan_height=p.capture.collect_bounds ? p.height : p.output_height;
        vkCmdDispatch(p.command, (scan_width + 31) / 32, (scan_height + 7) / 8, 1);
        VkBufferMemoryBarrier ready { .sType = VK_STRUCTURE_TYPE_BUFFER_MEMORY_BARRIER,
            .srcAccessMask = VK_ACCESS_SHADER_WRITE_BIT, .dstAccessMask = VK_ACCESS_TRANSFER_READ_BIT,
            .srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED, .dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            .buffer = p.nv12, .offset = 0, .size = VK_WHOLE_SIZE };
        vkCmdPipelineBarrier(p.command, VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT,
            0, 0, nullptr, 1, &ready, 0, nullptr);
        if (vkframe) {
            VkImageMemoryBarrier target { .sType = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER,
                .srcAccessMask = 0, .dstAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT,
                .oldLayout = vkframe->layout[0], .newLayout = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
                .srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED, .dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
                .image = vkframe->img[0], .subresourceRange = { VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1 } };
            vkCmdPipelineBarrier(p.command, VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT,
                0, 0, nullptr, 0, nullptr, 1, &target);
            std::array<VkBufferImageCopy, 2> copies {{
                { .bufferOffset = 0, .bufferRowLength = p.stride, .bufferImageHeight = p.output_height,
                  .imageSubresource = { VK_IMAGE_ASPECT_PLANE_0_BIT, 0, 0, 1 }, .imageExtent = { p.output_width, p.output_height, 1 } },
                { .bufferOffset = std::uint64_t(p.stride) * p.output_height, .bufferRowLength = p.stride / 2,
                  .bufferImageHeight = p.output_height / 2, .imageSubresource = { VK_IMAGE_ASPECT_PLANE_1_BIT, 0, 0, 1 },
                  .imageExtent = { p.output_width / 2, p.output_height / 2, 1 } } }};
            if (!cache_head)
                vkCmdCopyBufferToImage(p.command, p.nv12, vkframe->img[0], VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 2, copies.data());
        }
        source.srcAccessMask = VK_ACCESS_SHADER_READ_BIT; source.dstAccessMask = VK_ACCESS_TRANSFER_READ_BIT;
        source.oldLayout = VK_IMAGE_LAYOUT_GENERAL; source.newLayout = VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL;
        vkCmdPipelineBarrier(p.command, VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT,
            0, 0, nullptr, 0, nullptr, 1, &source);
        Vk(vkEndCommandBuffer(p.command), "end GPU conversion command");
        Vk(vkResetFences(p.device, 1, &p.fence), "reset GPU conversion fence");
        std::uint64_t before = vkframe ? vkframe->sem_value[0] : 0, after = before + 1;
        VkTimelineSemaphoreSubmitInfo timeline { .sType = VK_STRUCTURE_TYPE_TIMELINE_SEMAPHORE_SUBMIT_INFO,
            .waitSemaphoreValueCount = 1, .pWaitSemaphoreValues = &before,
            .signalSemaphoreValueCount = 1, .pSignalSemaphoreValues = &after };
        VkPipelineStageFlags wait_stage = VK_PIPELINE_STAGE_ALL_COMMANDS_BIT;
        VkSubmitInfo submit { .sType = VK_STRUCTURE_TYPE_SUBMIT_INFO, .pNext = &timeline,
            .waitSemaphoreCount = vkframe ? 1u : 0u, .pWaitSemaphores = vkframe ? &vkframe->sem[0] : nullptr,
            .pWaitDstStageMask = &wait_stage, .commandBufferCount = 1, .pCommandBuffers = &p.command,
            .signalSemaphoreCount = vkframe ? 1u : 0u, .pSignalSemaphores = vkframe ? &vkframe->sem[0] : nullptr };
        if (!vkframe) submit.pNext = nullptr;
        auto* hw = reinterpret_cast<AVHWDeviceContext*>(p.hardware->data);
        auto* vk = reinterpret_cast<AVVulkanDeviceContext*>(hw->hwctx);
        vk->lock_queue(hw, p.family, 0);
        const auto submitted = vkQueueSubmit(p.queue, 1, &submit, p.fence);
        vk->unlock_queue(hw, p.family, 0);
        Vk(submitted, "submit GPU conversion");
        p.conversion_pending = true;
        if (vkframe) {
            vkframe->sem_value[0] = after;
            vkframe->layout[0] = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL;
            vkframe->access[0] = VK_ACCESS_TRANSFER_WRITE_BIT;
        } else if (!cache_head) {
            // 等本帧转换完成（下次 waitConversion）再交给 NVENC，渲染与转换仍可重叠
            p.nv_pending = true; p.nv_pending_pts = static_cast<std::int64_t>(index-fade);
            p.nv_pending_idr = blend_head && head_index==0;
        }
        if (!asynchronous) p.waitConversion();
    } catch (...) {
        if (vkframe) vkframes->unlock_frame(frames, vkframe);
        vkDeviceWaitIdle(p.device);
        throw;
    }
    if (vkframe) vkframes->unlock_frame(frames, vkframe);
    if (cache_head || p.nvenc) { p.retain(rgba,index); return; }
    p.frame->pts = static_cast<std::int64_t>(index-fade);
    if (blend_head && head_index==0) p.frame->pict_type=AV_PICTURE_TYPE_I;
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
    if (p.nvenc) {
        p.waitConversion();
        while (p.nv_collected < p.nv_sent) p.nvCollect();
    } else {
        Av(avcodec_send_frame(p.codec, nullptr), "flush Vulkan encoder");
        p.packets();
    }
    if (p.encoded_packets != p.capture.encoded_frames) throw std::runtime_error("Incomplete GPU encoded frame sequence");
    Av(av_write_trailer(p.mux), "finish GPU video");
    Av(avio_closep(&p.mux->pb), "close GPU video");
    if (p.head_mux) {
        Av(av_write_trailer(p.head_mux),"finish GPU loop head");
        Av(avio_closep(&p.head_mux->pb),"close GPU loop head");
        p.assembleLoop();
    }
    if (p.retained.is_open()) {
        p.retained.flush();
        if (!p.retained || p.retained_count != p.capture.retain_frames.size())
            throw std::runtime_error("Retained GPU frame sequence is incomplete");
        p.retained.close();
    }
    if (p.loop_window.is_open()) {
        p.loop_window.flush();
        if (!p.loop_window || p.loop_window_count!=std::uint64_t(p.capture.crossfade_frames)*2)
            throw std::runtime_error("Original GPU loop window is incomplete");
        p.loop_window.close();
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
    out << "{\"readback_frames\":" << p.readbacks
        << ",\"encoded_packets\":" << p.encoded_packets
        << ",\"quality_level\":" << p.quality_level << ",\"async_depth\":" << p.async_depth
        << ",\"encoder\":\"" << (p.nvenc ? "nvenc_sdk" : "vulkan_video") << '"';
    if (p.capture.retain_loop_window)
        out << ",\"loop_window\":{\"path\":\"loop-window.rgba\",\"format\":\"rgba\",\"width\":" << p.width
            << ",\"height\":" << p.height << ",\"crossfade_frames\":" << p.capture.crossfade_frames
            << ",\"loop_frames\":" << p.capture.encoded_frames << ",\"frame_count\":" << p.loop_window_count << '}';
    out << ",\"crop\":{\"capture_width\":" << p.width << ",\"capture_height\":" << p.height
        << ",\"x\":" << p.capture.crop_x << ",\"y\":" << p.capture.crop_y
        << ",\"width\":" << p.capture.crop_width << ",\"height\":" << p.capture.crop_height << '}';
    if (p.resizing)
        out << ",\"resize\":{\"width\":" << p.color_width << ",\"height\":" << p.output_height
            << ",\"filter\":\"lanczos3\"}";
    if (p.capture.crossfade_frames)
        out << ",\"loop_crossfade\":{\"status\":\"applied\",\"crossfade_frames\":" << p.capture.crossfade_frames
            << ",\"loop_frames\":" << p.capture.encoded_frames
            << ",\"method\":\"GPU integer RGBA blend; independent head/body GOPs restored by packet-only remux.\"}";
    if (p.capture.collect_bounds) {
        bool content=p.bounds[7]!=0;
        out << ",\"alpha_bounds\":{\"has_content\":" << (content ? "true" : "false")
            << ",\"x\":" << (content ? p.bounds[0] : 0) << ",\"y\":" << (content ? p.bounds[1] : 0)
            << ",\"width\":" << (content ? p.bounds[2]-p.bounds[0]+1 : 0) << ",\"height\":" << (content ? p.bounds[3]-p.bounds[1]+1 : 0)
            << ",\"minimum_alpha\":" << p.bounds[4] << ",\"maximum_alpha\":" << p.bounds[5]
            << ",\"includes_rgb\":" << (p.capture.bounds_include_rgb ? "true" : "false")
            << ",\"pixel_identical_in_generated_interval\":" << (p.bounds[6] ? "false" : "true")
            << ",\"observed_frames\":" << p.observed_frames
            << ",\"first_frame_rgba_path\":\"first-frame.rgba\",\"basis\":\"GPU reduction over all source frames used by this output, including crossfade continuation when requested.\","
               "\"pixel_identity_scope\":\"Source RGBA compared against the first frame on the GPU; ordinary unencoded continuation is excluded.\"}";
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

std::uint64_t GpuVideoEncoder::readbackFrames() const { return impl->readbacks; }
}
