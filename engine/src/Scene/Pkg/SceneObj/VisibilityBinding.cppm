module;

#include "JsonNlohmann.hpp"

export module wescene.pkg.scene_obj:visibility_binding;
import rstd.cppstd;
import wescene.json;

using namespace rstd::literals;

export namespace owe::wpscene
{

struct VisibleUserBinding {
    std::string name;
    owe::Json   condition; // 仍是 rstd：只有用户属性链（C 组）读，读取处 ToRstd（过渡桥）
    bool        has_condition { false };

    bool empty() const { return name.empty(); }
};

struct UserValueBinding {
    std::string name;
    owe::Json   condition; // 仍是 rstd：只有用户属性链（C 组）读，读取处 ToRstd（过渡桥）
    bool        has_condition { false };

    bool empty() const { return name.empty(); }
};

inline void ReadVisibleUserBinding(const owe::NJson& json, VisibleUserBinding& out) {
    out          = {};
    auto visible = owe::Find(json, "visible");
    if (visible == nullptr || ! visible->is_object()) return;

    auto user = owe::Find(*visible, "user");
    if (user == nullptr) return;
    if (user->is_string()) {
        out.name = user->get_ref<const std::string&>();
        return;
    }

    if (! user->is_object()) return;
    if (auto name = owe::Find(*user, "name"); name != nullptr) {
        if (name->is_string()) out.name = name->get_ref<const std::string&>();
    }
    if (auto condition = owe::Find(*user, "condition"); condition != nullptr) {
        out.condition     = owe::ToRstd(*condition);
        out.has_condition = true;
    }
}

inline void ReadVisibleProperty(const owe::NJson& json, bool& visible, VisibleUserBinding& out) {
    out        = {};
    auto value = owe::Find(json, "visible");
    if (value == nullptr) return;

    if (value->is_boolean()) {
        visible = value->get<bool>();
        return;
    }
    if (! value->is_object()) return;

    if (auto initial = owe::Find(*value, "value"); initial != nullptr) {
        if (initial->is_boolean()) {
            visible = initial->get<bool>();
        } else {
            if (initial->is_number()) {
                const auto value = initial->get<double>();
                if (value >= std::numeric_limits<int>::min() &&
                    value <= std::numeric_limits<int>::max())
                    visible = static_cast<int>(value) != 0;
            }
        }
    }
    ReadVisibleUserBinding(json, out);
}

inline void ReadUserValueBinding(const owe::NJson& json, std::string_view field,
                                 UserValueBinding& out) {
    out        = {};
    auto value = owe::Find(json, field);
    if (value == nullptr || ! value->is_object()) return;

    auto user = owe::Find(*value, "user");
    if (user == nullptr) return;

    if (user->is_string()) {
        out.name = user->get_ref<const std::string&>();
        return;
    }

    if (! user->is_object()) return;
    if (auto name = owe::Find(*user, "name"); name != nullptr) {
        if (name->is_string()) out.name = name->get_ref<const std::string&>();
    }
    if (auto condition = owe::Find(*user, "condition"); condition != nullptr) {
        out.condition     = owe::ToRstd(*condition);
        out.has_condition = true;
    }
}

} // namespace owe::wpscene
