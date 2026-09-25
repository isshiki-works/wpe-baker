using System.Runtime.InteropServices;
using System.Text;

namespace Baker.Core;

public enum VulkanDeviceType : uint
{
    Other = 0,
    IntegratedGpu = 1,
    DiscreteGpu = 2,
    VirtualGpu = 3,
    Cpu = 4
}

/// <summary>UUID and optional LUID contain the Vulkan-reported bytes in memory order.</summary>
/// <param name="DriverVersion">The implementation-defined raw driver version; its encoding is vendor-specific.</param>
/// <param name="VideoEncode">驱动是否提供 VK_KHR_video_encode_queue（渲染器 GPU 直编的前提；Intel Windows 驱动目前没有）。</param>
public sealed record VulkanDeviceInfo(string Name, string DeviceUuid, uint VendorId, uint DeviceId,
    VulkanDeviceType DeviceType, uint ApiVersion, uint DriverVersion, string? WindowsLuid,
    uint? WindowsNodeMask, bool VideoEncode)
{
    public string ApiVersionText
    {
        get
        {
            uint variant = ApiVersion >> 29;
            string version = $"{(ApiVersion >> 22) & 0x7f}.{(ApiVersion >> 12) & 0x3ff}.{ApiVersion & 0xfff}";
            return variant == 0 ? version : $"{variant}:{version}";
        }
    }
}

/// <summary>Read-only Windows Vulkan enumeration. Creates no surface, window or logical device.</summary>
public static class VulkanDevices
{
    private const uint Vulkan11 = (1u << 22) | (1u << 12);
    private const uint ApplicationInfoType = 0;
    private const uint InstanceCreateInfoType = 1;
    private const uint PhysicalDeviceProperties2Type = 1000059001;
    private const uint PhysicalDeviceIdPropertiesType = 1000071004;
    private const int Success = 0;
    private const int Incomplete = 5;

    /// <summary>Enumerates stable device UUIDs through Vulkan 1.1; throws on loader or driver failure.</summary>
    public static IReadOnlyList<VulkanDeviceInfo> Enumerate()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("VulkanDevices uses the Windows system Vulkan loader.");

        // Load the system loader only; no working-directory DLL or SDK executable is used.
        nint library = NativeLibrary.Load("vulkan-1.dll", typeof(VulkanDevices).Assembly,
            DllImportSearchPath.System32);
        nint instance = 0;
        DestroyInstanceDelegate? destroy = null;
        try
        {
            var getProc = Marshal.GetDelegateForFunctionPointer<GetInstanceProcAddrDelegate>(
                NativeLibrary.GetExport(library, "vkGetInstanceProcAddr"));
            // Resolve the loader's core destruction trampoline before creating any resource.
            destroy = Marshal.GetDelegateForFunctionPointer<DestroyInstanceDelegate>(
                NativeLibrary.GetExport(library, "vkDestroyInstance"));
            var create = InstanceFunction<CreateInstanceDelegate>(getProc, 0, "vkCreateInstance");
            using var application = new NativeBuffer(Marshal.SizeOf<VkApplicationInfo>());
            Marshal.StructureToPtr(new VkApplicationInfo { sType = ApplicationInfoType, apiVersion = Vulkan11 },
                application.Pointer, false);
            var createInfo = new VkInstanceCreateInfo
            {
                sType = InstanceCreateInfoType,
                pApplicationInfo = application.Pointer
            };
            Check(create(in createInfo, 0, out nint created), "vkCreateInstance (Vulkan 1.1)");
            instance = created;
            if (instance == 0)
                throw new InvalidOperationException("vkCreateInstance returned success with a null instance.");

            var enumerate = InstanceFunction<EnumeratePhysicalDevicesDelegate>(getProc, instance,
                "vkEnumeratePhysicalDevices");
            var getProperties = InstanceFunction<GetPhysicalDeviceProperties2Delegate>(getProc, instance,
                "vkGetPhysicalDeviceProperties2");
            var getExtensions = InstanceFunction<EnumerateDeviceExtensionPropertiesDelegate>(getProc, instance,
                "vkEnumerateDeviceExtensionProperties");

            // Enumeration may change between its count and fill calls (for example, hot-plug).
            for (int attempt = 0; attempt < 4; attempt++)
            {
                uint capacity = 0;
                Check(enumerate(instance, ref capacity, 0), "vkEnumeratePhysicalDevices (count)");
                if (capacity == 0) return Array.Empty<VulkanDeviceInfo>();
                int byteCount = checked((int)capacity * nint.Size);
                using var handles = new NativeBuffer(byteCount);
                uint count = capacity;
                int result = enumerate(instance, ref count, handles.Pointer);
                if (result == Incomplete) continue;
                Check(result, "vkEnumeratePhysicalDevices (devices)");
                if (count > capacity)
                    throw new InvalidOperationException("Vulkan returned more device handles than the supplied capacity.");

                var devices = new List<VulkanDeviceInfo>(checked((int)count));
                for (int i = 0; i < count; i++)
                {
                    nint device = Marshal.ReadIntPtr(handles.Pointer, checked(i * nint.Size));
                    if (device == 0)
                        throw new InvalidOperationException("Vulkan enumerated a null physical-device handle.");
                    devices.Add(ReadDevice(device, getProperties, getExtensions));
                }
                return devices.AsReadOnly();
            }
            throw new InvalidOperationException("Vulkan device enumeration remained incomplete after four attempts.");
        }
        finally
        {
            try
            {
                if (instance != 0) destroy!(instance, 0);
            }
            finally { NativeLibrary.Free(library); }
        }
    }

    private static VulkanDeviceInfo ReadDevice(nint device, GetPhysicalDeviceProperties2Delegate getProperties,
        EnumerateDeviceExtensionPropertiesDelegate getExtensions)
    {
        using var identity = new NativeBuffer(Marshal.SizeOf<VkPhysicalDeviceIdProperties>());
        using var properties = new NativeBuffer(Marshal.SizeOf<VkPhysicalDeviceProperties2>());
        WriteHeader<VkPhysicalDeviceIdProperties>(identity.Pointer, PhysicalDeviceIdPropertiesType, 0);
        WriteHeader<VkPhysicalDeviceProperties2>(properties.Pointer, PhysicalDeviceProperties2Type, identity.Pointer);
        getProperties(device, properties.Pointer);
        var info = Marshal.PtrToStructure<VkPhysicalDeviceProperties2>(properties.Pointer).properties;
        var id = Marshal.PtrToStructure<VkPhysicalDeviceIdProperties>(identity.Pointer);
        int terminator = Array.IndexOf(info.deviceName, (byte)0);
        string name = Encoding.UTF8.GetString(info.deviceName, 0,
            terminator < 0 ? info.deviceName.Length : terminator);
        return new VulkanDeviceInfo(name, Hex(id.deviceUUID), info.vendorID, info.deviceID,
            (VulkanDeviceType)info.deviceType, info.apiVersion, info.driverVersion,
            id.deviceLUIDValid != 0 ? Hex(id.deviceLUID) : null,
            id.deviceLUIDValid != 0 ? id.deviceNodeMask : null, HasExtension(device, getExtensions, "VK_KHR_video_encode_queue"));
    }

    private static bool HasExtension(nint device, EnumerateDeviceExtensionPropertiesDelegate getExtensions, string name)
    {
        const int PropertiesSize = 260; // VkExtensionProperties: char extensionName[256] + uint32_t specVersion
        uint count = 0;
        if (getExtensions(device, 0, ref count, 0) != Success || count == 0) return false;
        using var properties = new NativeBuffer(checked((int)count * PropertiesSize));
        if (getExtensions(device, 0, ref count, properties.Pointer) is not (Success or Incomplete)) return false;
        for (int i = 0; i < count; i++)
            if (Marshal.PtrToStringUTF8(properties.Pointer + i * PropertiesSize) == name) return true;
        return false;
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private static T InstanceFunction<T>(GetInstanceProcAddrDelegate getProc, nint instance, string name)
        where T : Delegate
    {
        nint address = getProc(instance, name);
        if (address == 0) throw new EntryPointNotFoundException($"The system Vulkan loader did not provide {name}.");
        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    private static void WriteHeader<T>(nint buffer, uint type, nint next) where T : struct
    {
        Marshal.WriteInt32(buffer, Marshal.OffsetOf<T>("sType").ToInt32(), checked((int)type));
        Marshal.WriteIntPtr(buffer, Marshal.OffsetOf<T>("pNext").ToInt32(), next);
    }

    private static void Check(int result, string operation)
    {
        if (result == Success) return;
        string name = result switch
        {
            -1 => "VK_ERROR_OUT_OF_HOST_MEMORY",
            -2 => "VK_ERROR_OUT_OF_DEVICE_MEMORY",
            -3 => "VK_ERROR_INITIALIZATION_FAILED",
            -4 => "VK_ERROR_DEVICE_LOST",
            -6 => "VK_ERROR_LAYER_NOT_PRESENT",
            -7 => "VK_ERROR_EXTENSION_NOT_PRESENT",
            -8 => "VK_ERROR_FEATURE_NOT_PRESENT",
            -9 => "VK_ERROR_INCOMPATIBLE_DRIVER",
            _ => "VkResult"
        };
        throw new InvalidOperationException($"{operation} failed with {name} ({result}).");
    }

    private sealed class NativeBuffer : IDisposable
    {
        public nint Pointer { get; private set; }
        public NativeBuffer(int size)
        {
            byte[] zeroes = new byte[size];
            Pointer = Marshal.AllocHGlobal(size);
            try { Marshal.Copy(zeroes, 0, Pointer, size); }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            nint pointer = Pointer;
            Pointer = 0;
            if (pointer != 0) Marshal.FreeHGlobal(pointer);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint GetInstanceProcAddrDelegate(nint instance, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int CreateInstanceDelegate(in VkInstanceCreateInfo createInfo, nint allocator, out nint instance);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void DestroyInstanceDelegate(nint instance, nint allocator);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int EnumeratePhysicalDevicesDelegate(nint instance, ref uint count, nint devices);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void GetPhysicalDeviceProperties2Delegate(nint device, nint properties);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int EnumerateDeviceExtensionPropertiesDelegate(nint device, nint layerName, ref uint count, nint properties);

    // Field order/types are from Khronos Vulkan-Headers, vulkan_core.h:
    // VkApplicationInfo / VkInstanceCreateInfo / VkPhysicalDeviceProperties2 /
    // VkPhysicalDeviceProperties / VkPhysicalDeviceLimits / VkPhysicalDeviceIDProperties.
    // Full native structs determine buffer sizes; no guessed byte offsets or unsafe code.
    // VkDeviceSize = uint64_t; VkBool32/VkSampleCountFlags = uint32_t; size_t = nuint.
    [StructLayout(LayoutKind.Sequential)]
    private struct VkApplicationInfo
    {
        public uint sType;
        public nint pNext, pApplicationName;
        public uint applicationVersion;
        public nint pEngineName;
        public uint engineVersion, apiVersion;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkInstanceCreateInfo
    {
        public uint sType;
        public nint pNext;
        public uint flags;
        public nint pApplicationInfo;
        public uint enabledLayerCount;
        public nint ppEnabledLayerNames;
        public uint enabledExtensionCount;
        public nint ppEnabledExtensionNames;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkPhysicalDeviceProperties2
    {
        public uint sType;
        public nint pNext;
        public VkPhysicalDeviceProperties properties;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkPhysicalDeviceIdProperties
    {
        public uint sType;
        public nint pNext;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] deviceUUID;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] driverUUID;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public byte[] deviceLUID;
        public uint deviceNodeMask, deviceLUIDValid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkPhysicalDeviceProperties
    {
        public uint apiVersion, driverVersion, vendorID, deviceID, deviceType;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public byte[] deviceName;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] pipelineCacheUUID;
        public VkPhysicalDeviceLimits limits;
        public VkPhysicalDeviceSparseProperties sparseProperties;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkPhysicalDeviceSparseProperties
    {
        public uint residencyStandard2DBlockShape, residencyStandard2DMultisampleBlockShape,
            residencyStandard3DBlockShape, residencyAlignedMipSize, residencyNonResidentStrict;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkPhysicalDeviceLimits
    {
        public uint maxImageDimension1D, maxImageDimension2D, maxImageDimension3D, maxImageDimensionCube,
            maxImageArrayLayers, maxTexelBufferElements, maxUniformBufferRange, maxStorageBufferRange,
            maxPushConstantsSize, maxMemoryAllocationCount, maxSamplerAllocationCount;
        public ulong bufferImageGranularity, sparseAddressSpaceSize;
        public uint maxBoundDescriptorSets, maxPerStageDescriptorSamplers, maxPerStageDescriptorUniformBuffers,
            maxPerStageDescriptorStorageBuffers, maxPerStageDescriptorSampledImages, maxPerStageDescriptorStorageImages,
            maxPerStageDescriptorInputAttachments, maxPerStageResources, maxDescriptorSetSamplers,
            maxDescriptorSetUniformBuffers, maxDescriptorSetUniformBuffersDynamic, maxDescriptorSetStorageBuffers,
            maxDescriptorSetStorageBuffersDynamic, maxDescriptorSetSampledImages, maxDescriptorSetStorageImages,
            maxDescriptorSetInputAttachments, maxVertexInputAttributes, maxVertexInputBindings,
            maxVertexInputAttributeOffset, maxVertexInputBindingStride, maxVertexOutputComponents,
            maxTessellationGenerationLevel, maxTessellationPatchSize, maxTessellationControlPerVertexInputComponents,
            maxTessellationControlPerVertexOutputComponents, maxTessellationControlPerPatchOutputComponents,
            maxTessellationControlTotalOutputComponents, maxTessellationEvaluationInputComponents,
            maxTessellationEvaluationOutputComponents, maxGeometryShaderInvocations, maxGeometryInputComponents,
            maxGeometryOutputComponents, maxGeometryOutputVertices, maxGeometryTotalOutputComponents,
            maxFragmentInputComponents, maxFragmentOutputAttachments, maxFragmentDualSrcAttachments,
            maxFragmentCombinedOutputResources, maxComputeSharedMemorySize;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public uint[] maxComputeWorkGroupCount;
        public uint maxComputeWorkGroupInvocations;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public uint[] maxComputeWorkGroupSize;
        public uint subPixelPrecisionBits, subTexelPrecisionBits, mipmapPrecisionBits,
            maxDrawIndexedIndexValue, maxDrawIndirectCount;
        public float maxSamplerLodBias, maxSamplerAnisotropy;
        public uint maxViewports;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] public uint[] maxViewportDimensions;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] public float[] viewportBoundsRange;
        public uint viewportSubPixelBits;
        public nuint minMemoryMapAlignment;
        public ulong minTexelBufferOffsetAlignment, minUniformBufferOffsetAlignment, minStorageBufferOffsetAlignment;
        public int minTexelOffset;
        public uint maxTexelOffset;
        public int minTexelGatherOffset;
        public uint maxTexelGatherOffset;
        public float minInterpolationOffset, maxInterpolationOffset;
        public uint subPixelInterpolationOffsetBits, maxFramebufferWidth, maxFramebufferHeight, maxFramebufferLayers,
            framebufferColorSampleCounts, framebufferDepthSampleCounts, framebufferStencilSampleCounts,
            framebufferNoAttachmentsSampleCounts, maxColorAttachments, sampledImageColorSampleCounts,
            sampledImageIntegerSampleCounts, sampledImageDepthSampleCounts, sampledImageStencilSampleCounts,
            storageImageSampleCounts, maxSampleMaskWords, timestampComputeAndGraphics;
        public float timestampPeriod;
        public uint maxClipDistances, maxCullDistances, maxCombinedClipAndCullDistances, discreteQueuePriorities;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] public float[] pointSizeRange;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] public float[] lineWidthRange;
        public float pointSizeGranularity, lineWidthGranularity;
        public uint strictLines, standardSampleLocations;
        public ulong optimalBufferCopyOffsetAlignment, optimalBufferCopyRowPitchAlignment, nonCoherentAtomSize;
    }
}
