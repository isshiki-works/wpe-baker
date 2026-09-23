module;
#include <owe/compat.hpp>
#include <owe/std.hpp>

export module wescene.scene_user_property;

import wescene.json;
import wescene.scene;

using namespace rstd::prelude;

export namespace owe
{

struct SceneUserPropertyMutation {
    bool                    graph_changed {};
    bool                    diagnostics_changed {};
    Option<array<float, 3>> clear_color;
    Vec<SceneMaterialId>    texture_materials;
};

std::string             CanonicalSceneUserPropertyKey(std::string_view key);
Option<array<float, 3>> ResolveSceneUserPropertyColor(const NJson&);

class SceneUserPropertyApplier {
public:
    static SceneUserPropertyMutation Apply(Scene&, std::string_view key, const NJson&);
    static SceneUserPropertyMutation ApplyAll(Scene&, const NJson& properties);
    static Vec<SceneMaterialId>      ApplyTexture(Scene&, std::string_view key, const NJson&);
};

Vec<SceneUserPropertyDiagnostic> CollectSceneUserPropertyDiagnostics(const Scene&,
                                                                     std::string_view key);

} // namespace owe
