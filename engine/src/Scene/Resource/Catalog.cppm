export module wescene.resource:catalog;
import rstd;
import wescene.types;
import :error;
import :texture;
import :buffer;
import :shader;

export namespace owe::resource
{

using namespace rstd::prelude;

struct TextureCatalog {
    using Trait                  = TextureCatalog;
    static constexpr bool direct = false;

    template<typename Self, typename = void>
    struct Api {
        using Trait = TextureCatalog;

        auto ResolveTexture(TextureDefinitionId id) const -> Option<TextureRequest> {
            return rstd::trait_call<0>(this, id);
        }

        auto FindTexture(ref<str> name) const -> Option<TextureRequest> {
            return rstd::trait_call<1>(this, name);
        }
    };

    template<typename T>
    using Funcs = TraitFuncs<&T::ResolveTexture, &T::FindTexture>;
};

struct TextureLoader {
    using Trait                  = TextureLoader;
    static constexpr bool direct = false;

    template<typename Self, typename = void>
    struct Api {
        using Trait = TextureLoader;

        auto LoadTexture(ref<str> key) const -> Result<rstd::sync::Arc<Image>, ResourceError> {
            return rstd::trait_call<0>(this, key);
        }
    };

    template<typename T>
    using Funcs = TraitFuncs<&T::LoadTexture>;
};

struct TextureContentProvider {
    using Trait                  = TextureContentProvider;
    static constexpr bool direct = false;

    template<typename Self, typename = void>
    struct Api {
        using Trait = TextureContentProvider;

        auto ResolveTextureContent(const TextureRequest& request) const
            -> Result<ImportedTextureContentIdentity, ResourceError> {
            return rstd::trait_call<0>(this, request);
        }

        auto OpenTextureLoader() const
            -> Result<rstd::sync::Arc<dyn<TextureLoader>>, ResourceError> {
            return rstd::trait_call<1>(this);
        }

        auto ResolveVideoPlayback(const TextureRequest& request) const
            -> Option<rstd::sync::Arc<VideoPlaybackState>> {
            return rstd::trait_call<2>(this, request);
        }
    };

    template<typename T>
    using Funcs =
        TraitFuncs<&T::ResolveTextureContent, &T::OpenTextureLoader, &T::ResolveVideoPlayback>;
};

struct TexturePrepareObserver {
    using Trait                  = TexturePrepareObserver;
    static constexpr bool direct = false;

    template<typename Self, typename = void>
    struct Api {
        using Trait = TexturePrepareObserver;

        void BeginTexturePlan() { rstd::trait_call<0>(this); }
        void EndTexturePlan() { rstd::trait_call<1>(this); }
        void BeginTextureDecode() { rstd::trait_call<2>(this); }
        void EndTextureDecode() { rstd::trait_call<3>(this); }
        void BeginTextureUpload() { rstd::trait_call<4>(this); }
        void EndTextureUpload() { rstd::trait_call<5>(this); }
    };

    template<typename T>
    using Funcs = TraitFuncs<&T::BeginTexturePlan, &T::EndTexturePlan, &T::BeginTextureDecode,
                             &T::EndTextureDecode, &T::BeginTextureUpload, &T::EndTextureUpload>;
};

struct BufferCatalog {
    using Trait                  = BufferCatalog;
    static constexpr bool direct = false;

    template<typename Self, typename = void>
    struct Api {
        using Trait = BufferCatalog;

        auto ResolveBuffer(BufferDefinitionId id) const -> Option<BufferRequest> {
            return rstd::trait_call<0>(this, id);
        }
    };

    template<typename T>
    using Funcs = TraitFuncs<&T::ResolveBuffer>;
};

struct BufferContentProvider {
    using Trait                  = BufferContentProvider;
    static constexpr bool direct = false;

    template<typename Self, typename = void>
    struct Api {
        using Trait = BufferContentProvider;

        auto LoadBuffer(const BufferRequest& request) -> Result<slice<u8>, ResourceError> {
            return rstd::trait_call<0>(this, request);
        }
    };

    template<typename T>
    using Funcs = TraitFuncs<&T::LoadBuffer>;
};

struct BufferContentWriter {
    using Trait                  = BufferContentWriter;
    static constexpr bool direct = false;

    template<typename Self, typename = void>
    struct Api {
        using Trait = BufferContentWriter;

        auto UpdateBuffer(BufferUseHandle use, slice<u8> content) -> Result<empty, ResourceError> {
            return rstd::trait_call<0>(this, use, content);
        }
    };

    template<typename T>
    using Funcs = TraitFuncs<&T::UpdateBuffer>;
};

struct ShaderCatalog {
    using Trait                  = ShaderCatalog;
    static constexpr bool direct = false;

    template<typename Self, typename = void>
    struct Api {
        using Trait = ShaderCatalog;

        auto ResolveShader(ShaderDefinitionId id) const -> Option<ShaderRequest> {
            return rstd::trait_call<0>(this, id);
        }
    };

    template<typename T>
    using Funcs = TraitFuncs<&T::ResolveShader>;
};

struct ShaderArtifactProvider {
    using Trait                  = ShaderArtifactProvider;
    static constexpr bool direct = false;

    template<typename Self, typename = void>
    struct Api {
        using Trait = ShaderArtifactProvider;

        auto LoadShader(const ShaderRequest& request) -> Result<ShaderArtifact, ResourceError> {
            return rstd::trait_call<0>(this, request);
        }
    };

    template<typename T>
    using Funcs = TraitFuncs<&T::LoadShader>;
};

struct TextureLogicalState {
    TextureHandle handle;
    u64           definition_version { 0 };
    u64           content_version { 0 };
};

struct TextureLogicalRegistryView {
    using Trait                  = TextureLogicalRegistryView;
    static constexpr bool direct = false;

    template<typename Self, typename = void>
    struct Api {
        using Trait = TextureLogicalRegistryView;

        auto ResolveTextureState(TextureHandle handle) const -> Option<TextureLogicalState> {
            return rstd::trait_call<0>(this, handle);
        }
    };

    template<typename T>
    using Funcs = TraitFuncs<&T::ResolveTextureState>;
};

} // namespace owe::resource
