module;

#include <rstd/macro.hpp>
#include "vvk/macros.hpp"

module wescene.vulkan;
import wescene.core;
import wescene.types;
import rstd;
import rstd.log;
import rstd.cppstd;

using namespace rstd::prelude;
using namespace owe::vulkan;

constexpr rstd::array<InstanceLayer, 0> base_inst_layers {};

constexpr rstd::array<Extension, 1> base_inst_exts { Extension {
    true, VK_EXT_DEBUG_UTILS_EXTENSION_NAME } };

namespace
{

VkBool32 DebugUtilsMessengerCallback(VkDebugUtilsMessageSeverityFlagBitsEXT messageSeverity,
                                     VkDebugUtilsMessageTypeFlagsEXT /*messageType*/,
                                     const VkDebugUtilsMessengerCallbackDataEXT* pCallbackData,
                                     void* /*pUserData*/) {
    if (messageSeverity >= VK_DEBUG_UTILS_MESSAGE_SEVERITY_ERROR_BIT_EXT) {
        rstd_error("validation layer: {}", pCallbackData->pMessage);
    } else if (messageSeverity >= VK_DEBUG_UTILS_MESSAGE_SEVERITY_WARNING_BIT_EXT) {
        rstd_warn("validation layer: {}", pCallbackData->pMessage);
    }
    return VK_FALSE;
}

vvk::DebugUtilsMessenger SetupDebugCallback(vvk::Instance& instance) {
    return instance.CreateDebugUtilsMessenger(VkDebugUtilsMessengerCreateInfoEXT {
        .sType           = VK_STRUCTURE_TYPE_DEBUG_UTILS_MESSENGER_CREATE_INFO_EXT,
        .pNext           = nullptr,
        .flags           = 0,
        .messageSeverity = VK_DEBUG_UTILS_MESSAGE_SEVERITY_ERROR_BIT_EXT |
                           VK_DEBUG_UTILS_MESSAGE_SEVERITY_WARNING_BIT_EXT |
                           VK_DEBUG_UTILS_MESSAGE_SEVERITY_INFO_BIT_EXT |
                           VK_DEBUG_UTILS_MESSAGE_SEVERITY_VERBOSE_BIT_EXT,
        .messageType     = VK_DEBUG_UTILS_MESSAGE_TYPE_GENERAL_BIT_EXT |
                           VK_DEBUG_UTILS_MESSAGE_TYPE_VALIDATION_BIT_EXT |
                           VK_DEBUG_UTILS_MESSAGE_TYPE_PERFORMANCE_BIT_EXT,
        .pfnUserCallback = DebugUtilsMessengerCallback,
        .pUserData       = nullptr,
    });
}

VkResult CreatInstance(vvk::Instance* inst, std::span<const std::string_view> exts,
                       std::span<const std::string_view> layers, vvk::InstanceDispatch& dld,
                       rstd::uint32_t api_version) {
    VkApplicationInfo app_info {
        .sType              = VK_STRUCTURE_TYPE_APPLICATION_INFO,
        .pNext              = nullptr,
        .pApplicationName   = WP_APPLICATION_NAME,
        .applicationVersion = api_version,
        .pEngineName        = "vulkan",
        .apiVersion         = api_version,
    };

    std::vector<const char*> extension_names_c;
    std::transform(exts.begin(), exts.end(), std::back_inserter(extension_names_c), [](auto& ext) {
        return ext.data();
    });

    std::vector<const char*> layer_names_c;
    std::transform(
        layers.begin(), layers.end(), std::back_inserter(layer_names_c), [](auto& layer) {
            return layer.data();
        });

    return vvk::Instance::Create(
        *inst,
        app_info,
        rstd::slice<const char*>::from_raw_parts(layer_names_c.data(), usize(layer_names_c.size())),
        rstd::slice<const char*>::from_raw_parts(extension_names_c.data(),
                                                 usize(extension_names_c.size())),
        dld);
}
void EnumateExts(owe::Set<std::string>& set, const vvk::InstanceDispatch& dld) {
    if (auto rv = vvk::EnumerateInstanceExtensionProperties(dld); rv.is_some()) {
        for (const auto& ext : *rv) set.insert(ext.extensionName);
    }
}
void EnumateLayers(owe::Set<std::string>& set, const vvk::InstanceDispatch& dld) {
    if (auto rv = vvk::EnumerateInstanceLayerProperties(dld); rv.is_some()) {
        for (const auto& ext : *rv) set.insert(ext.layerName);
    }
}
} // namespace

bool Instance::ChoosePhysicalDevice(const CheckGpuOp&              checkgpu,
                                    std::span<const rstd::uint8_t> uuid) {
    auto deviceList = m_vinst.EnumeratePhysicalDevices();

    auto logGpu = [](const VkPhysicalDeviceProperties& props) {
        rstd_info("vulkan device: {}", props.deviceName);
    };

    vvk::PhysicalDevice        final_gpu;
    VkPhysicalDeviceProperties final_props;

    for (const auto& d : deviceList) {
        VkPhysicalDeviceIDProperties device_id_props {
            .sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_ID_PROPERTIES, .pNext = nullptr
        };
        VkPhysicalDeviceProperties2 props2 { .sType =
                                                 VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PROPERTIES_2,
                                             .pNext = &device_id_props };
        d.GetProperties2KHR(props2);
        auto& props = props2.properties;
        if (uuid.size() > 0) {
            decltype(uuid) device_uuid { device_id_props.deviceUUID };
            if (std::equal(uuid.begin(), uuid.end(), device_uuid.begin(), device_uuid.end())) {
                final_props = props;
                final_gpu   = d;
                break;
            }
        } else {
            if (checkgpu(d)) {
                final_props = props;
                final_gpu   = d;
                break;
            }
        }
    }
    if (final_gpu) {
        logGpu(final_props);
        m_gpu = final_gpu;
        return true;
    } else {
        rstd_error("failed to find GPU with vulkan support");
        return false;
    }
}

const vvk::Instance&       Instance::inst() const { return m_vinst; }
const vvk::PhysicalDevice& Instance::gpu() const { return m_gpu; }
const vvk::SurfaceKHR&     Instance::surface() const { return m_surface; }

bool Instance::offscreen() const { return ! m_surface; }

void Instance::setSurface(VkSurfaceKHR sf) {
    m_surface = vvk::SurfaceKHR(sf, *m_vinst, m_vinst.Dispatch());
}

bool Instance::supportExt(std::string_view name) const { return exists(m_extensions, name); }
bool Instance::supportLayer(std::string_view name) const { return exists(m_layers, name); }

void Instance::Destroy() {}

bool Instance::Create(Instance& inst, std::span<const Extension> instExts,
                      std::span<const InstanceLayer> instLayers, rstd::uint32_t api_version) {
    if (! vvk::Load(inst.m_dld)) return false;

    EnumateExts(inst.m_extensions, inst.m_dld);
    Set<std::string>                           exts, layers;
    rstd::array<std::span<const Extension>, 2> test_exts_array {
        std::span<const Extension>(base_inst_exts.data(), base_inst_exts.len().to_primitive()),
        instExts
    };
    for (auto& test_exts : test_exts_array) {
        for (auto& ext : test_exts) {
            bool ok = inst.supportExt(ext.name);
            if (ok) exts.insert(std::string(ext.name));
            if (ext.required && ! ok) {
                rstd_error("required vulkan instance extension \"{}\" is not supported", ext.name);
                return false;
            }
        }
    }

    EnumateLayers(inst.m_layers, inst.m_dld);
    rstd::array<std::span<const InstanceLayer>, 2> test_layers_array {
        std::span<const InstanceLayer>(base_inst_layers.data(),
                                       base_inst_layers.len().to_primitive()),
        instLayers
    };
    for (auto& test_layers : test_layers_array) {
        for (auto& layer : test_layers) {
            bool ok = inst.supportLayer(layer.name);
            if (ok) layers.insert(std::string(layer.name));
            if (layer.required && ! ok) {
                rstd_error("required vulkan instance layer \"{}\" is not supported", layer.name);
                return false;
            }
        }
    }

    std::vector<std::string_view> exts_vec { exts.begin(), exts.end() },
        layers_vec { layers.begin(), layers.end() };

    VVK_CHECK_BOOL_RE(CreatInstance(&inst.m_vinst, exts_vec, layers_vec, inst.m_dld, api_version));
    vvk::Load(*inst.m_vinst, inst.m_dld);
    inst.m_api_version = api_version;
    inst.m_enabled_extensions.assign(exts.begin(), exts.end());

    inst.m_debug_utils = SetupDebugCallback(inst.m_vinst);
    return true;
}
