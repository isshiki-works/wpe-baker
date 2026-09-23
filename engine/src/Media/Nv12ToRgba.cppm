module;

#include <vulkan/vulkan.h>

export module owe.media:nv12_to_rgba;
import rstd.cppstd;
import wescene.vk;

// NV12 → RGBA8 转换（T5b，迁自 wavsen yuv_to_rgba.cpp 的软件路径）。
// 只有软件解码帧这一条路：硬解帧的导入、BridgeForeign 的导出信号量不在这里。
// 计算着色器、采样器、上传与屏障逐项照旧，偶数尺寸输出与 wavsen 逐字节相同（对拍：media-nv12-parity-tests）；
// 奇数尺寸 wavsen 拒绝，这里支持（media-nv12-tests）。
export namespace owe::media
{

// 着色器算 rgb = M * (ycbcr + offset)；有限范围的缩放已折进矩阵系数。
struct YuvColorMatrix {
    float m_r[3]; // Y、Cb、Cr → R
    float m_g[3];
    float m_b[3];
    float offset[3]; // 乘矩阵前加到 (Y, Cb, Cr) 上
};

// 参数取解码器给帧标的编号（同 wavsen::video::ColorSpace / ColorRange）：
// colorspace 0=BT.709 1=BT.601 2=BT.2020，其余按 BT.709；range 1=全范围，其余按有限范围。
auto MakeYuvColorMatrix(std::uint32_t colorspace, std::uint32_t range) -> YuvColorMatrix;

class Nv12ToRgba {
public:
    // 计算着色器 GLSL 源码。调用方用引擎的着色器编译路径编成 SPIR-V 交给 Create。
    static auto ShaderSource() -> std::string_view;

    // 上传用的 Y/UV 平面与暂存缓冲按 max_w × max_h（奇数先补成偶数）一次分配，之后的帧都不能超过它。
    // 失败返回空指针，原因写进 error。
    static auto Create(VkPhysicalDevice phys, VkDevice device, std::uint32_t queue_family,
                       VkQueue queue, std::uint32_t max_w, std::uint32_t max_h,
                       std::span<const std::uint32_t> spirv, std::string& error)
        -> std::unique_ptr<Nv12ToRgba>;

    Nv12ToRgba(const Nv12ToRgba&)            = delete;
    Nv12ToRgba& operator=(const Nv12ToRgba&) = delete;
    ~Nv12ToRgba();

    // 把 width × height 的 NV12 写进 dst 左上角。宽高可以是奇数：Y 平面 width × height 紧密排列，
    // 其后 UV 交错平面 ceil(width/2) × ceil(height/2) 个 (U,V) 对紧密排列（行距 2×ceil(width/2) 字节）。
    // dst：RGBA8、带 STORAGE 用途，调用前后都在 SHADER_READ_ONLY_OPTIMAL（片元着色器读）。
    // 提交到构造时给的队列后即返回，不等 GPU；下一次 Convert 或析构才等上一次完成。
    // 失败返回 false，原因见 last_error()。
    auto Convert(VkImage dst, std::uint32_t width, std::uint32_t height,
                 std::span<const std::uint8_t> nv12, const YuvColorMatrix& matrix) -> bool;

    auto last_error() const -> const std::string& { return m_error; }

private:
    Nv12ToRgba() = default;

    auto Init(VkPhysicalDevice phys, std::uint32_t queue_family, std::span<const std::uint32_t> spirv)
        -> bool;
    auto Fail(std::string message) -> bool;

    VkDevice      m_device { VK_NULL_HANDLE };
    VkQueue       m_queue { VK_NULL_HANDLE };
    std::uint32_t m_max_w {};
    std::uint32_t m_max_h {};
    std::string   m_error;

    owe::vk::Unique<VkShaderModule>        m_shader;
    owe::vk::Unique<VkDescriptorSetLayout> m_set_layout;
    owe::vk::Unique<VkPipelineLayout>      m_pipeline_layout;
    owe::vk::Unique<VkPipeline>            m_pipeline;
    owe::vk::Unique<VkSampler>             m_sampler;

    // 内存先声明：析构时先销毁图像/缓冲再释放内存。
    owe::vk::Unique<VkDeviceMemory> m_y_memory;
    owe::vk::Unique<VkImage>        m_y_image;
    owe::vk::Unique<VkImageView>    m_y_view;
    owe::vk::Unique<VkDeviceMemory> m_uv_memory;
    owe::vk::Unique<VkImage>        m_uv_image;
    owe::vk::Unique<VkImageView>    m_uv_view;
    owe::vk::Unique<VkDeviceMemory> m_staging_memory;
    owe::vk::Unique<VkBuffer>       m_staging;
    std::uint8_t*                   m_staging_map { nullptr };

    owe::vk::DescriptorSetLease m_set;

    owe::vk::Unique<VkCommandPool> m_command_pool;
    VkCommandBuffer                m_command { VK_NULL_HANDLE };
    owe::vk::Unique<VkFence>       m_fence;
    bool                           m_fence_pending { false };

    // 上一次提交写的目标视图：GPU 可能还在用，等下一次 Convert 等过栅栏再换掉。
    owe::vk::Unique<VkImageView> m_target_view;
};

} // namespace owe::media
