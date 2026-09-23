module;
#include <owe/compat.hpp>
#include <owe/std.hpp>
export module wescene.resource:plan;
import :handle;
import :error;
import :texture;
import :buffer;
import :shader;

export namespace owe::resource
{

using namespace rstd::prelude;

enum class ResourceAccess
{
    Read,
    Write,
    ReadWrite,
};

struct TexturePlanEntry {
    TextureUseHandle handle;
    TextureRequest   request;
    String           allocation_key;
    ResourceAccess   access { ResourceAccess::Read };
    u32              version { 0 };
};

struct BufferPlanEntry {
    BufferUseHandle handle;
    BufferRequest   request;
    ResourceAccess  access { ResourceAccess::Read };
    u32             version { 0 };
};

struct ShaderPlanEntry {
    ShaderUseHandle handle;
    ShaderRequest   request;
    u32             version { 0 };
};

struct ReadyToken {
    u64 value { 0 };

    bool        Valid() const noexcept { return value != u64(); }
    friend bool operator==(const ReadyToken&, const ReadyToken&) = default;
};

struct CompletionToken {
    u64 value { 0 };

    bool        Valid() const noexcept { return value != u64(); }
    friend bool operator==(const CompletionToken&, const CompletionToken&) = default;
};

struct ResourcePlan {
    u64                              generation { 0 };
    rstd::vec::Vec<TexturePlanEntry> textures;
    rstd::vec::Vec<BufferPlanEntry>  buffers;
    rstd::vec::Vec<ShaderPlanEntry>  shaders;

    auto DeclareTexture(TextureRequest request, ResourceAccess access) -> TextureUseHandle {
        if (generation == u64()) return {};

        u64 next_index {};
        for (const auto& entry : textures) {
            if (entry.handle.generation != generation || ! entry.handle.Valid() ||
                entry.handle.index < next_index) {
                continue;
            }
            if (entry.handle.index == u64::MAX - u64(1)) return {};
            next_index = entry.handle.index + u64(1);
        }

        auto handle = TextureUseHandle {
            .index      = next_index,
            .generation = generation,
        };
        textures.push(TexturePlanEntry {
            .handle  = handle,
            .request = rstd::move(request),
            .access  = access,
        });
        return handle;
    }

    bool UpdateTextureRequest(TextureUseHandle handle, TextureRequest request) {
        if (handle.generation != generation) return false;
        for (auto& entry : textures) {
            if (entry.handle != handle) continue;
            entry.request = rstd::move(request);
            return true;
        }
        return false;
    }
};

using ResourcePlanSections = rstd::uint32_t;

enum class ResourcePlanSection : ResourcePlanSections
{
    Textures = 1u << 0u,
    Buffers  = 1u << 1u,
    Shaders  = 1u << 2u,
};

inline constexpr ResourcePlanSections ResourcePlanTextures {
    static_cast<ResourcePlanSections>(ResourcePlanSection::Textures),
};
inline constexpr ResourcePlanSections ResourcePlanBuffers {
    static_cast<ResourcePlanSections>(ResourcePlanSection::Buffers),
};
inline constexpr ResourcePlanSections ResourcePlanShaders {
    static_cast<ResourcePlanSections>(ResourcePlanSection::Shaders),
};
inline constexpr ResourcePlanSections ResourcePlanAll {
    ResourcePlanTextures | ResourcePlanBuffers | ResourcePlanShaders,
};

inline constexpr bool ResourcePlanIncludes(ResourcePlanSections sections,
                                           ResourcePlanSections section) {
    return (sections & section) == section;
}

// 按段遍历资源计划的访问者。
struct ResourcePlanVisitor {
    virtual ~ResourcePlanVisitor() = default;

    virtual auto VisitTexture(const TexturePlanEntry& entry) -> Result<empty, ResourceError> = 0;
    virtual auto VisitBuffer(const BufferPlanEntry& entry) -> Result<empty, ResourceError>   = 0;
    virtual auto VisitShader(const ShaderPlanEntry& entry) -> Result<empty, ResourceError>   = 0;
};

inline auto VisitResourcePlan(const ResourcePlan& plan, ResourcePlanVisitor* visitor,
                              ResourcePlanSections sections = ResourcePlanAll)
    -> Result<empty, ResourceError> {
    if (ResourcePlanIncludes(sections, ResourcePlanTextures)) {
        for (const auto& entry : plan.textures) {
            auto result = visitor->VisitTexture(entry);
            if (result.is_err()) return result;
        }
    }
    if (ResourcePlanIncludes(sections, ResourcePlanBuffers)) {
        for (const auto& entry : plan.buffers) {
            auto result = visitor->VisitBuffer(entry);
            if (result.is_err()) return result;
        }
    }
    if (ResourcePlanIncludes(sections, ResourcePlanShaders)) {
        for (const auto& entry : plan.shaders) {
            auto result = visitor->VisitShader(entry);
            if (result.is_err()) return result;
        }
    }
    return Ok(empty {});
}

} // namespace owe::resource
