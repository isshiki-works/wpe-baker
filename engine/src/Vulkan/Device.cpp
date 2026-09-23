module;
#include <owe/compat.hpp>
#include <owe/std.hpp>

#include <type_traits>
#include <vulkan/vulkan.h>

#include "vvk/macros.hpp"

module wescene.vulkan;
import wescene.core;
import wescene.types;

using namespace rstd::prelude;
using namespace owe::vulkan;

namespace
{

void EnumateDeviceExts(const vvk::PhysicalDevice& gpu, owe::Set<std::string>& set) {
    rstd::vec::Vec<VkExtensionProperties> properties;
    VVK_CHECK_VOID_RE(gpu.EnumerateDeviceExtensionProperties(properties));
    for (auto& ext : properties) set.insert(ext.extensionName);
}

} // namespace

bool Device::CheckGPU(vvk::PhysicalDevice gpu, std::span<const Extension> exts,
                      VkSurfaceKHR surface) {
    auto props = gpu.GetQueueFamilyProperties();

    bool     has_graphics_queue { false };
    bool     has_present_queue { false };
    unsigned index { 0 };
    for (auto& prop : props) {
        if (prop.queueFlags & VK_QUEUE_GRAPHICS_BIT) has_graphics_queue = true;
        if (surface) {
            bool ok { false };
            VVK_CHECK(gpu.GetSurfaceSupportKHR(index, surface, ok));
            if (ok) has_present_queue = true;
        }
        index++;
    }
    if (! has_graphics_queue) return false;
    if (surface && ! has_present_queue) return false;

    Set<std::string> extensions;
    EnumateDeviceExts(gpu, extensions);
    bool requires_timeline_semaphore { false };
    for (auto& ext : exts) {
        if (ext.required) {
            if (! exists(extensions, ext.name)) return false;
            if (std::string_view(ext.name) == VK_KHR_TIMELINE_SEMAPHORE_EXTENSION_NAME) {
                requires_timeline_semaphore = true;
            }
        }
    }
    VkPhysicalDeviceTimelineSemaphoreFeaturesKHR timeline_features {
        .sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_TIMELINE_SEMAPHORE_FEATURES_KHR,
        .pNext = nullptr,
    };
    VkPhysicalDeviceFeatures2KHR features2 {
        .sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_FEATURES_2_KHR,
        .pNext = requires_timeline_semaphore ? &timeline_features : nullptr,
    };
    gpu.GetFeatures2KHR(features2);
    if (! features2.features.geometryShader) return false;
    if (requires_timeline_semaphore && ! timeline_features.timelineSemaphore) return false;
    return true;
}

std::vector<VkDeviceQueueCreateInfo> Device::ChooseDeviceQueue(VkSurfaceKHR surface) {
    std::vector<VkDeviceQueueCreateInfo> queues;

    auto props = m_gpu.GetQueueFamilyProperties();

    std::vector<rstd::uint32_t> graphic_indexs, present_indexs;
    rstd::uint32_t              index = 0;
    for (auto& prop : props) {
        if (prop.queueFlags & VK_QUEUE_GRAPHICS_BIT) graphic_indexs.push_back(index);
        index++;
    }
    m_graphics_queue.family_index           = graphic_indexs.front();
    const static float defaultQueuePriority = 0.0f;
    m_present_queue.family_index            = graphic_indexs.front();
    if (surface) {
        index = 0;
        for (auto& prop : props) {
            (void)prop;
            bool ok { false };
            VVK_CHECK(m_gpu.GetSurfaceSupportKHR(index, surface, ok))
            if (ok) present_indexs.push_back(index);
            index++;
        }
        if (present_indexs.empty()) {
            rstd_error("not find present queue");
        } else {
            m_present_queue.family_index = present_indexs.front();
        }
    }
    for (rstd::uint32_t i = 0; i < props.len().to_primitive(); ++i) {
        if (props[usize(i)].queueCount == 0) continue;
        VkDeviceQueueCreateInfo info {
            .sType            = VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO,
            .queueFamilyIndex = i,
            .queueCount       = 1,
            .pQueuePriorities = &defaultQueuePriority,
        };
        queues.push_back(info);
    }
    return queues;
}

bool Device::Create(Instance& inst, std::span<const Extension> exts, VkExtent2D extent,
                    Device& device) {
    device.dld                    = vvk::DeviceDispatch { inst.inst().Dispatch() };
    device.m_instance             = *inst.inst();
    device.m_instance_api_version = inst.api_version();
    device.m_gpu                  = inst.gpu();
    device.m_limits               = inst.gpu().GetProperties().limits;
    device.set_out_extent(extent);
    device.m_enabled_instance_extensions.assign(inst.enabled_extensions().begin(),
                                                inst.enabled_extensions().end());

    Set<std::string> tested_exts;
    {
        EnumateDeviceExts(inst.gpu(), device.m_extensions);
        for (auto& ext : exts) {
            bool ok = device.supportExt(ext.name);
            if (ok) tested_exts.insert(std::string(ext.name));
            if (ext.required && ! ok) {
                rstd_error("required vulkan device extension \"{}\" is not supported", ext.name);
                return false;
            }
        }
    }
    std::vector<const char*> tested_exts_c { tested_exts.size() };
    std::transform(
        tested_exts.begin(), tested_exts.end(), tested_exts_c.begin(), [](const auto& s) {
            return s.c_str();
        });
    device.m_enabled_device_extensions.assign(tested_exts.begin(), tested_exts.end());
    bool rq_surface = ! inst.offscreen();

    // The WE particle vertex ABI requires geometry shaders.
    VkPhysicalDeviceTimelineSemaphoreFeaturesKHR supported_timeline {
        .sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_TIMELINE_SEMAPHORE_FEATURES_KHR,
        .pNext = nullptr,
    };
    VkPhysicalDeviceSynchronization2FeaturesKHR supported_sync2 {
        .sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_SYNCHRONIZATION_2_FEATURES_KHR,
        .pNext = nullptr,
    };
    VkPhysicalDeviceVideoMaintenance1FeaturesKHR supported_video_maintenance {
        .sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VIDEO_MAINTENANCE_1_FEATURES_KHR,
    };
    const bool encode_requested = exists(tested_exts, VK_KHR_VIDEO_ENCODE_QUEUE_EXTENSION_NAME);
    if (encode_requested) supported_sync2.pNext = &supported_video_maintenance;
    VkPhysicalDeviceSamplerYcbcrConversionFeatures supported_ycbcr {
        .sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_SAMPLER_YCBCR_CONVERSION_FEATURES,
        .pNext = &supported_sync2,
    };
    supported_timeline.pNext = &supported_ycbcr;
    VkPhysicalDeviceFeatures2KHR supported2 {
        .sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_FEATURES_2_KHR,
        .pNext = &supported_timeline,
    };
    device.m_gpu.GetFeatures2KHR(supported2);
    if (encode_requested && !supported_video_maintenance.videoMaintenance1) {
        rstd_error("GPU encoding requires the videoMaintenance1 feature");
        return false;
    }
    if (! supported_timeline.timelineSemaphore) {
        rstd_error("required vulkan feature timelineSemaphore is not supported");
        return false;
    }
    if (! supported2.features.geometryShader) {
        rstd_error("required vulkan feature geometryShader is not supported");
        return false;
    }
    const bool enable_shader_output_viewport_index =
        exists(tested_exts, VK_EXT_SHADER_VIEWPORT_INDEX_LAYER_EXTENSION_NAME);
    const bool enable_multi_viewport =
        enable_shader_output_viewport_index && supported2.features.multiViewport;
    const auto d32_features =
        device.m_gpu.GetFormatProperties(VK_FORMAT_D32_SFLOAT).optimalTilingFeatures;
    const bool sampled_depth_d32 =
        (d32_features &
         (VK_FORMAT_FEATURE_SAMPLED_IMAGE_BIT | VK_FORMAT_FEATURE_DEPTH_STENCIL_ATTACHMENT_BIT)) ==
        (VK_FORMAT_FEATURE_SAMPLED_IMAGE_BIT | VK_FORMAT_FEATURE_DEPTH_STENCIL_ATTACHMENT_BIT);
    VkPhysicalDeviceFeatures enabled {};
    enabled.geometryShader    = VK_TRUE;
    enabled.sampleRateShading = supported2.features.sampleRateShading;
    enabled.samplerAnisotropy = supported2.features.samplerAnisotropy;
    enabled.multiViewport     = enable_multi_viewport ? VK_TRUE : VK_FALSE;
    enabled.depthClamp        = supported2.features.depthClamp;
    VkPhysicalDeviceTimelineSemaphoreFeaturesKHR enabled_timeline {
        .sType             = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_TIMELINE_SEMAPHORE_FEATURES_KHR,
        .pNext             = nullptr,
        .timelineSemaphore = VK_TRUE,
    };
    const bool enable_sync2 = exists(tested_exts, VK_KHR_SYNCHRONIZATION_2_EXTENSION_NAME) &&
                              supported_sync2.synchronization2;
    VkPhysicalDeviceSynchronization2FeaturesKHR enabled_sync2 {
        .sType            = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_SYNCHRONIZATION_2_FEATURES_KHR,
        .pNext            = nullptr,
        .synchronization2 = enable_sync2 ? VK_TRUE : VK_FALSE,
    };
    VkPhysicalDeviceVideoMaintenance1FeaturesKHR enabled_video_maintenance {
        .sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VIDEO_MAINTENANCE_1_FEATURES_KHR,
        .videoMaintenance1 = encode_requested ? VK_TRUE : VK_FALSE,
    };
    if (encode_requested) enabled_sync2.pNext = &enabled_video_maintenance;
    VkPhysicalDeviceSamplerYcbcrConversionFeatures enabled_ycbcr {
        .sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_SAMPLER_YCBCR_CONVERSION_FEATURES,
        .pNext = enable_sync2 ? &enabled_sync2 : nullptr,
        .samplerYcbcrConversion = supported_ycbcr.samplerYcbcrConversion,
    };
    enabled_timeline.pNext = &enabled_ycbcr;

    auto queue_create_infos = device.ChooseDeviceQueue(*inst.surface());
    VVK_CHECK_BOOL_RE(vvk::Device::Create(
        device.m_device,
        *device.m_gpu,
        rstd::slice<VkDeviceQueueCreateInfo>::from_raw_parts(queue_create_infos.data(),
                                                             usize(queue_create_infos.size())),
        rstd::slice<const char*>::from_raw_parts(tested_exts_c.data(), usize(tested_exts_c.size())),
        &enabled_timeline,
        device.dld,
        &enabled));

    device.m_graphics_queue.handle = device.m_device.GetQueue(device.m_graphics_queue.family_index);
    device.m_present_queue.handle  = device.m_device.GetQueue(device.m_present_queue.family_index);
    rstd::uint32_t max_push_descriptors {};
    if (exists(tested_exts, VK_KHR_PUSH_DESCRIPTOR_EXTENSION_NAME)) {
        VkPhysicalDevicePushDescriptorPropertiesKHR push_properties {
            .sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PUSH_DESCRIPTOR_PROPERTIES_KHR,
        };
        VkPhysicalDeviceProperties2 properties {
            .sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PROPERTIES_2,
            .pNext = &push_properties,
        };
        device.m_gpu.GetProperties2KHR(properties);
        max_push_descriptors = push_properties.maxPushDescriptors;
    }
    device.m_capabilities = DeviceCapabilities {
        .timeline_semaphore           = true,
        .synchronization2             = enable_sync2,
        .push_descriptor              = exists(tested_exts, VK_KHR_PUSH_DESCRIPTOR_EXTENSION_NAME),
        .max_push_descriptors         = max_push_descriptors,
        .multi_viewport               = enable_multi_viewport,
        .shader_output_viewport_index = enable_shader_output_viewport_index,
        .sampled_depth_d32            = sampled_depth_d32,
        .depth_clamp                  = supported2.features.depthClamp != VK_FALSE,
        .max_geometry_output_vertices = device.m_limits.maxGeometryOutputVertices,
        .max_geometry_total_output_components = device.m_limits.maxGeometryTotalOutputComponents,
        .memory_budget      = exists(tested_exts, VK_EXT_MEMORY_BUDGET_EXTENSION_NAME),
        .external_memory_fd = exists(tested_exts, VK_KHR_EXTERNAL_MEMORY_FD_EXTENSION_NAME),
        .external_memory_dma_buf =
            exists(tested_exts, VK_EXT_EXTERNAL_MEMORY_DMA_BUF_EXTENSION_NAME),
        .drm_format_modifier = exists(tested_exts, VK_EXT_IMAGE_DRM_FORMAT_MODIFIER_EXTENSION_NAME),
        .foreign_queue       = exists(tested_exts, VK_EXT_QUEUE_FAMILY_FOREIGN_EXTENSION_NAME),
        .graphics_queue_family = device.m_graphics_queue.family_index,
        .present_queue_family  = device.m_present_queue.family_index,
    };

    if (rq_surface) {
        if (! Swapchain::Create(device, *inst.surface(), extent, device.m_swapchain)) {
            rstd_error("create swapchain failed");
            return false;
        }
    }
    {
        VkCommandPoolCreateInfo info { .sType = VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO,
                                       .flags = VK_COMMAND_POOL_CREATE_TRANSIENT_BIT |
                                                VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT,
                                       .queueFamilyIndex = device.m_graphics_queue.family_index };
        VVK_CHECK_BOOL_RE(device.m_device.CreateCommandPool(info, device.m_command_pool));
    }
    {
        VmaAllocatorCreateInfo allocatorInfo = {};
        allocatorInfo.vulkanApiVersion       = device.m_instance_api_version;
        allocatorInfo.physicalDevice         = *device.m_gpu;
        allocatorInfo.device                 = *device.m_device;
        allocatorInfo.instance               = *inst.inst();
        VVK_CHECK_BOOL_RE(vvk::CreateVmaAllocator(allocatorInfo, device.m_allocator));
    }
    return true;
}

VkDeviceSize Device::GetUsage() const { return MemoryBudget().usage; }

auto Device::MemoryBudget() const -> MemoryBudgetSnapshot {
    auto properties = m_gpu.GetMemoryProperties().memoryProperties;
    rstd::array<VmaBudget, std::extent_v<decltype(properties.memoryHeaps)>> budgets {};
    vmaGetHeapBudgets(*m_allocator, budgets.data());
    MemoryBudgetSnapshot snapshot;
    for (rstd::uint32_t index = 0; index < properties.memoryHeapCount; ++index) {
        auto budget_index = usize(index);
        snapshot.usage += budgets[budget_index].usage;
        snapshot.budget += budgets[budget_index].budget != 0 ? budgets[budget_index].budget
                                                             : properties.memoryHeaps[index].size;
    }
    return snapshot;
}

void Device::Destroy() { VVK_CHECK(m_device.WaitIdle()); }

Device::Device() = default;
Device::~Device() {}

bool Device::supportExt(std::string_view name) const { return exists(m_extensions, name); }
