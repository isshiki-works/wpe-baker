module;
#include <owe/compat.hpp>
#include <owe/std.hpp>
export module wescene.pkg.scene_obj:material;
import wescene.fs;
import :scene_document;
export import :field_binding;

export namespace owe

{
namespace wpscene
{

class MaterialPassBindItem {
public:
    bool        FromJson(const owe::NJson&);
    std::string name;
    i32         index;
};

class MaterialPass {
public:
    bool                                                FromJson(const owe::NJson&);
    void                                                Update(const MaterialPass&);
    u32                                                 id { 0 }; // pass id (PKGV0001+)
    std::vector<std::string>                            textures;
    std::vector<owe::NJson>                             usertextures; // PKGV0018+; polymorphic
    std::unordered_map<std::string, i32>                combos;
    std::unordered_map<std::string, std::vector<float>> constantshadervalues;
    FieldBindings                                       constantshadervalues_bindings;
    // Legacy `usershadervalues`: project.json key -> shader material key.
    std::unordered_map<std::string, std::string> user_shader_values;
    std::string                                  target;
    std::vector<MaterialPassBindItem>            bind;
};

class Material : public rstd::DefaultInClass<Material, rstd::clone::Clone> {
public:
    Material()                               = default;
    Material(const Material&)                = delete;
    Material& operator=(const Material&)     = delete;
    Material(Material&&) noexcept            = default;
    Material& operator=(Material&&) noexcept = default;

    bool        FromJson(const owe::NJson&);               // legacy
    bool        FromJson(const owe::NJson&, SceneVersion); // canonical
    auto        clone() const -> Material;
    void        MergePass(const MaterialPass&);
    void        MergeBindingOverrides(const std::vector<std::string>&             textures,
                                      const std::vector<owe::NJson>&              usertextures,
                                      const std::unordered_map<std::string, i32>& combos);
    std::string blending { "translucent" };
    std::string cullmode { "nocull" };
    std::string shader;
    std::string alphawriting { "default" };
    std::string depthtest { "disabled" };
    std::string depthwrite { "disabled" };
    std::vector<std::string>                            textures;
    std::vector<owe::NJson>                             usertextures;
    std::unordered_map<std::string, i32>                combos;
    std::unordered_map<std::string, std::vector<float>> constantshadervalues;
    FieldBindings                                       constantshadervalues_bindings;
    std::unordered_map<std::string, std::string>        user_shader_values;

    bool use_puppet { false };
};

} // namespace wpscene
} // namespace owe
