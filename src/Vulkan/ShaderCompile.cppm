module;

// Vulkan headers only — no dispatch / loader. ShaderReflected exposes
// VkDescriptorSetLayoutBinding and VkFormat, which SPIRV-Reflect produces
// natively, so consumers can hand the reflection straight to a pipeline
// builder without translating types. We do NOT link Vulkan_LIBRARIES from
// this module's target; downstream binaries that only want shader
// compilation get the headers but no libvulkan dependency.
export module wescene.shader_compile;
import wescene.core;
export import vvk;
import wescene.types;
import rstd;
import rstd.cppstd;

using namespace rstd::prelude;

export namespace owe::vulkan
{

// ---------- Spv.hpp ----------

struct ShaderSpv {
    std::string entry_point { "main" };
    ShaderType  stage;

    std::vector<unsigned int> spirv;
};

using Uni_ShaderSpv = Box<ShaderSpv>;

// ---------- ShaderReflect.hpp ----------

struct ShaderReflected {
    struct BlockedUniform {
        int                   block_index;
        unsigned              offset;
        usize                 size { 0 };
        usize                 num { 1 };
        ShaderScalarKind      scalar_kind { ShaderScalarKind::Unknown };
        unsigned              scalar_width {};
        unsigned              vector_components { 1 };
        unsigned              matrix_rows {};
        unsigned              matrix_columns {};
        unsigned              matrix_stride {};
        ShaderMatrixMajor     matrix_major { ShaderMatrixMajor::None };
        unsigned              array_stride {};
        std::vector<unsigned> array_dimensions;
    };
    struct Block {
        int         index;
        unsigned    size;
        std::string name;
        unsigned    set { 0 };
        unsigned    binding { 0 };

        Map<std::string, BlockedUniform> member_map;
    };
    std::vector<Block> blocks;

    struct Binding {
        unsigned                     set { 0 };
        VkDescriptorSetLayoutBinding layout;
    };
    Map<std::string, Binding> binding_map;

    struct Input {
        unsigned location;
        VkFormat format;
    };
    Map<std::string, Input> input_location_map;
};

// Optional dependency evidence is the union of accessed members across stages;
// reflected block layouts still include every declared member.
bool GenReflect(std::span<const std::vector<unsigned int>> codes, std::vector<Uni_ShaderSpv>& spvs,
                ShaderReflected& ref, Set<std::string>* active_uniforms = nullptr);

// ---------- ShaderComp.hpp ----------

enum class VulkanTarget : unsigned
{
    Vulkan_1_0,
    Vulkan_1_1,
    Vulkan_1_2,
    Vulkan_1_3,
};

enum class SourceLang : unsigned
{
    Glsl,
    Hlsl,
};

struct ShaderCompUnit {
    ShaderType  stage;
    std::string src;
    std::string entry_point; // if empty, "main" (GLSL) or "main_<stage>" (HLSL).
    SourceLang  lang { SourceLang::Glsl };
};

struct ShaderCompOpt {
    VulkanTarget target { VulkanTarget::Vulkan_1_1 };
    bool         optimize { false };
};

bool CompileAndLinkShaderUnits(std::span<const ShaderCompUnit> compUnits, const ShaderCompOpt& opt,
                               std::vector<Uni_ShaderSpv>& spvs);

// Expand every `#if`, `#include` and `#define` in `src` so downstream
// regex passes see only live declarations with macros already resolved
// (e.g. `g_Bones[BONECOUNT]` becomes `g_Bones[4]`, `#if SKINNING=0`
// blocks vanish entirely). `lang` selects glslang's GLSL vs HLSL
// preprocessor. On failure returns false and leaves `out` untouched.
bool Preprocess(std::string_view src, ShaderType stage, SourceLang lang, std::string& out);

} // namespace owe::vulkan
