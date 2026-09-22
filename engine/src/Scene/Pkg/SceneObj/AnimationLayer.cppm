module;

export module wescene.pkg.scene_obj:animation_layer;
import rstd;
import rstd.cppstd;
import wescene.json;
export import wescene.pkg.puppet;

using namespace rstd::prelude;
using namespace rstd::literals;

export namespace owe::wpscene
{

inline void ReadPuppetAnimationLayers(const owe::Json&                          json,
                                      std::vector<PuppetLayer::AnimationLayer>& out) {
    auto layers = json.get("animationlayers"_str);
    if (layers.is_none()) return;
    auto array = (*layers)->as_array();
    if (array.is_none()) return;
    for (const auto& jLayer : **array) {
        PuppetLayer::AnimationLayer layer;
        owe::GetJsonValue(jLayer, "animation", layer.id);
        owe::GetJsonValue(jLayer, "blend", layer.blend);
        owe::GetJsonValue(jLayer, "rate", layer.rate);
        if (auto visible = jLayer.get("visible"_str); visible.is_some()) {
            layer.visible_binding = Some((*visible)->clone());
            if ((*visible)->is_boolean()) {
                layer.visible = *(*visible)->as_bool();
            } else if ((*visible)->is_object()) {
                if (auto value = (*visible)->get("value"_str); value.is_some()) {
                    if ((*value)->is_boolean()) {
                        layer.visible = *(*value)->as_bool();
                    } else if (auto numeric = (*value)->as_f64(); numeric.is_some()) {
                        layer.visible = numeric->to_primitive() != 0.0;
                    }
                }
                layer.visible_can_change = (*visible)->get("script"_str).is_some() ||
                                           (*visible)->get("user"_str).is_some();
            }
        }
        owe::GetJsonValue(jLayer, "id", layer.layer_id, false);
        std::string name;
        owe::GetJsonValue(jLayer, "name", name, false);
        layer.name = String::make(rstd::cppstd::as_str(name).unwrap());
        owe::GetJsonValue(jLayer, "additive", layer.additive, false);
        owe::GetJsonValue(jLayer, "blendin", layer.blendin, false);
        owe::GetJsonValue(jLayer, "blendout", layer.blendout, false);
        owe::GetJsonValue(jLayer, "blendtime", layer.blendtime, false);
        out.push_back(std::move(layer));
    }
}

} // namespace owe::wpscene
