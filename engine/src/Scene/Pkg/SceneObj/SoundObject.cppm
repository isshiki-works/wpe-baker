export module wescene.pkg.scene_obj:sound_object;
import rstd.cppstd;
import wavsen.audio;
import wescene.fs;

import wescene.json;
export import :field_binding;
import :visibility_binding;
import :scene_document;

using namespace rstd::literals;

export namespace owe

{

namespace wpscene
{

struct SoundObject {
    i32                      id { 0 };
    std::string              playbackmode { "loop" };
    std::array<float, 3>     origin { 0.0f, 0.0f, 0.0f };
    std::array<float, 3>     angles { 0.0f, 0.0f, 0.0f };
    std::array<float, 3>     scale { 1.0f, 1.0f, 1.0f };
    float                    maxtime { 10.0f };
    float                    mintime { 0.0f };
    float                    volume { 1.0f };
    bool                     visible { true };
    std::string              name;
    std::vector<std::string> sound;

    // Common cross-kind metadata.
    bool             locktransforms { false };
    bool             muteineditor { false };
    bool             nointerpolation { false };
    u32              parent { 0 };
    std::vector<i32> dependencies;
    owe::Json        instance;
    FieldBindings    field_bindings;

    // Sound-kind specifics.
    bool        startsilent { false };    // PKGV0002+
    bool        blockalign { false };     // PKGV0018+
    bool        spatialization { false }; // PKGV0023+
    std::string queuemode;                // PKGV0020+

    VisibleUserBinding visible_user;
    std::string        visible_user_key;
    std::string        volume_user_key;

    bool FromJson(const owe::Json& json, fs::VFS& vfs) {
        return FromJson(json, vfs, kSceneVersionUnknown);
    }

    bool FromJson(const owe::Json& json, fs::VFS&, SceneVersion /*v*/) {
        owe::GetJsonValue(json, "volume", volume);
        if (auto volume_json = json.get("volume"_str);
            volume_json.is_some() && (*volume_json)->is_object()) {
            if (auto user = (*volume_json)->get("user"_str); user.is_some()) {
                auto string = (*user)->as_str();
                if (string.is_some()) volume_user_key = rstd::cppstd::to_string(*string);
            }
        }
        owe::GetJsonValue(json, "playbackmode", playbackmode);
        owe::GetJsonValue(json, "origin", origin, false);
        owe::GetJsonValue(json, "angles", angles, false);
        owe::GetJsonValue(json, "scale", scale, false);
        owe::GetJsonValue(json, "mintime", mintime, false);
        owe::GetJsonValue(json, "maxtime", maxtime, false);
        ReadVisibleProperty(json, visible, visible_user);
        visible_user_key = visible_user.name;
        owe::GetJsonValue(json, "name", name, false);
        owe::GetJsonValue(json, "id", id, false);
        owe::GetJsonValue(json, "locktransforms", locktransforms, false);
        owe::GetJsonValue(json, "muteineditor", muteineditor, false);
        owe::GetJsonValue(json, "nointerpolation", nointerpolation, false);
        owe::GetJsonValue(json, "parent", parent, false);
        owe::GetJsonValue(json, "dependencies", dependencies, false);
        if (auto value = json.get("instance"_str); value.is_some()) instance = (*value)->clone();

        owe::GetJsonValue(json, "startsilent", startsilent, false);
        owe::GetJsonValue(json, "blockalign", blockalign, false);
        owe::GetJsonValue(json, "spatialization", spatialization, false);
        owe::GetJsonValue(json, "queuemode", queuemode, false);

        auto sound_json = json.get("sound"_str);
        if (sound_json.is_none()) return false;
        auto sound_array = (*sound_json)->as_array();
        if (sound_array.is_none()) return false;
        for (const auto& el : **sound_array) {
            std::string name;
            owe::GetJsonValue(el, name);
            if (! name.empty()) sound.push_back(name);
        }
        AbsorbAllFieldBindings(json, field_bindings);
        return true;
    }
};
} // namespace wpscene
} // namespace owe
