export module wescene.scene:id;
import rstd;

using namespace rstd::prelude;

export namespace owe
{

template<typename Tag>
struct SceneResourceId {
    u32 index { u32::MAX };
    u32 generation { 0 };

    bool Valid() const noexcept { return index != u32::MAX && generation != u32(); }

    friend bool operator==(const SceneResourceId&, const SceneResourceId&) = default;
};

struct SceneNodeIdTag;
struct SceneEffectIdTag;
struct SceneMaterialIdTag;
struct SceneMeshIdTag;
struct SceneDrawItemIdTag;
struct SceneTextureIdTag;
struct SceneRenderTargetIdTag;
struct SceneCameraIdTag;

using SceneNodeId         = SceneResourceId<SceneNodeIdTag>;
using SceneEffectId       = SceneResourceId<SceneEffectIdTag>;
using SceneMaterialId     = SceneResourceId<SceneMaterialIdTag>;
using SceneMeshId         = SceneResourceId<SceneMeshIdTag>;
using SceneDrawItemId     = SceneResourceId<SceneDrawItemIdTag>;
using SceneTextureId      = SceneResourceId<SceneTextureIdTag>;
using SceneRenderTargetId = SceneResourceId<SceneRenderTargetIdTag>;
using SceneCameraId       = SceneResourceId<SceneCameraIdTag>;

} // namespace owe
