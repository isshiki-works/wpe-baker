module;
#include <memory>

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



// 按 key 加载导入纹理的像素（可在解码线程上调用）。
struct TextureLoader {
    virtual ~TextureLoader() = default;

    virtual auto LoadTexture(ref<str> key) const
        -> Result<rstd::sync::Arc<Image>, ResourceError> = 0;
};

// 导入纹理的内容身份、加载器与视频播放状态。
struct TextureContentProvider {
    virtual ~TextureContentProvider() = default;

    virtual auto ResolveTextureContent(const TextureRequest& request) const
        -> Result<ImportedTextureContentIdentity, ResourceError> = 0;
    virtual auto OpenTextureLoader() const
        -> Result<std::shared_ptr<TextureLoader>, ResourceError> = 0;
    virtual auto ResolveVideoPlayback(const TextureRequest& request) const
        -> Option<rstd::sync::Arc<VideoPlaybackState>> = 0;
};

// 缓冲初始内容。
struct BufferContentProvider {
    virtual ~BufferContentProvider() = default;

    virtual auto LoadBuffer(const BufferRequest& request) -> Result<slice<u8>, ResourceError> = 0;
};

// 把字节内容写入某个缓冲使用。
struct BufferContentWriter {
    virtual ~BufferContentWriter() = default;

    virtual auto UpdateBuffer(BufferUseHandle use, slice<u8> content)
        -> Result<empty, ResourceError> = 0;
};

// 着色器产物。
struct ShaderArtifactProvider {
    virtual ~ShaderArtifactProvider() = default;

    virtual auto LoadShader(const ShaderRequest& request) -> Result<ShaderArtifact, ResourceError> = 0;
};

struct TextureLogicalState {
    TextureHandle handle;
    u64           definition_version { 0 };
    u64           content_version { 0 };
};


} // namespace owe::resource
