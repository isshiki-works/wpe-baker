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

inline void ReadPuppetAnimationLayers(const owe::NJson&                         json,
                                      std::vector<PuppetLayer::AnimationLayer>& out) {
    auto layers = owe::Find(json, "animationlayers");
    if (layers == nullptr) return;
    if (! layers->is_array()) return;
    for (const auto& jLayer : *layers) {
        PuppetLayer::AnimationLayer layer;
        owe::GetJsonValue(jLayer, "animation", layer.id);
        owe::GetJsonValue(jLayer, "blend", layer.blend);
        owe::GetJsonValue(jLayer, "rate", layer.rate);
        if (auto visible = owe::Find(jLayer, "visible"); visible != nullptr) {
            layer.visible_binding = Some(owe::NJson(*visible));
            if (visible->is_boolean()) {
                layer.visible = visible->get<bool>();
            } else if (visible->is_object()) {
                if (auto value = owe::Find(*visible, "value"); value != nullptr) {
                    if (value->is_boolean()) {
                        layer.visible = value->get<bool>();
                    } else if (value->is_number()) {
                        layer.visible = value->get<double>() != 0.0;
                    }
                }
                layer.visible_can_change = owe::Find(*visible, "script") != nullptr ||
                                           owe::Find(*visible, "user") != nullptr;
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
