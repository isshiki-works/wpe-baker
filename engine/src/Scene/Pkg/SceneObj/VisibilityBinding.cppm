export module wescene.pkg.scene_obj:visibility_binding;
import rstd.cppstd;
import wescene.json;

using namespace rstd::literals;

export namespace owe::wpscene
{

struct VisibleUserBinding {
    std::string name;
    owe::Json   condition;
    bool        has_condition { false };

    bool empty() const { return name.empty(); }
};

struct UserValueBinding {
    std::string name;
    owe::Json   condition;
    bool        has_condition { false };

    bool empty() const { return name.empty(); }
};

inline void ReadVisibleUserBinding(const owe::Json& json, VisibleUserBinding& out) {
    out          = {};
    auto visible = json.get("visible"_str);
    if (visible.is_none() || ! (*visible)->is_object()) return;

    auto user = (*visible)->get("user"_str);
    if (user.is_none()) return;
    if ((*user)->is_string()) {
        out.name = rstd::cppstd::to_string(*(*user)->as_str());
        return;
    }

    if (! (*user)->is_object()) return;
    if (auto name = (*user)->get("name"_str); name.is_some()) {
        auto string = (*name)->as_str();
        if (string.is_some()) out.name = rstd::cppstd::to_string(*string);
    }
    if (auto condition = (*user)->get("condition"_str); condition.is_some()) {
        out.condition     = (*condition)->clone();
        out.has_condition = true;
    }
}

inline void ReadVisibleProperty(const owe::Json& json, bool& visible, VisibleUserBinding& out) {
    out        = {};
    auto value = json.get("visible"_str);
    if (value.is_none()) return;

    if ((*value)->is_boolean()) {
        visible = *(*value)->as_bool();
        return;
    }
    if (! (*value)->is_object()) return;

    if (auto initial = (*value)->get("value"_str); initial.is_some()) {
        if ((*initial)->is_boolean()) {
            visible = *(*initial)->as_bool();
        } else {
            auto numeric = (*initial)->as_f64();
            if (numeric.is_some()) {
                const auto value = numeric->to_primitive();
                if (value >= std::numeric_limits<int>::min() &&
                    value <= std::numeric_limits<int>::max())
                    visible = static_cast<int>(value) != 0;
            }
        }
    }
    ReadVisibleUserBinding(json, out);
}

inline void ReadUserValueBinding(const owe::Json& json, std::string_view field,
                                 UserValueBinding& out) {
    out        = {};
    auto value = json.get(rstd::cppstd::as_str(field).unwrap());
    if (value.is_none() || ! (*value)->is_object()) return;

    auto user = (*value)->get("user"_str);
    if (user.is_none()) return;

    if ((*user)->is_string()) {
        out.name = rstd::cppstd::to_string(*(*user)->as_str());
        return;
    }

    if (! (*user)->is_object()) return;
    if (auto name = (*user)->get("name"_str); name.is_some()) {
        auto string = (*name)->as_str();
        if (string.is_some()) out.name = rstd::cppstd::to_string(*string);
    }
    if (auto condition = (*user)->get("condition"_str); condition.is_some()) {
        out.condition     = (*condition)->clone();
        out.has_condition = true;
    }
}

} // namespace owe::wpscene
