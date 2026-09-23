module;

#include <memory>

export module wescene.scene:visibility;
import rstd;
import wescene.json;

using namespace rstd::prelude;
using namespace rstd::literals;

export namespace owe
{

// condition 用 shared_ptr 持有：这个结构体在 Scene.cppm、Lighting.cppm 的类内成员函数里被拷贝和析构，
// 直接放 NJson 会让这些接口单元生成 nlohmann 代码，clang 22 在那里崩溃。
struct SceneUserVisibilityBinding {
    String                       key;
    std::shared_ptr<const NJson> condition; // has_condition 为真时非空
    bool                         has_condition { false };

    bool empty() const { return key.is_empty(); }
};

const NJson& SceneUserPropertyPayload(const NJson& property);
auto         SceneJsonScalarString(const NJson& value) -> Option<String>;
bool         SceneJsonScalarEquals(const NJson& a, const NJson& b);

Option<bool> ResolveSceneUserVisibilityBinding(const SceneUserVisibilityBinding& binding,
                                               const NJson&                      property);
Option<bool> ResolveSceneUserVisibilityBinding(const SceneUserVisibilityBinding& binding,
                                               ref<str> key, const NJson& property);

} // namespace owe
