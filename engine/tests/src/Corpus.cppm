// Test corpus index.
//
// Walks workshop/* once, dumps every entry that has a scene.pkg via
// DumpWorkshop, and exposes lookup-by-version slices. The corpus is built
// lazily on first access.
//
// Skipped workshops (e.g. ones that hang MdlParser::Parse) are listed
// in kSkipIds and never parsed.

module;

#include <cstdio>
#include <new> // wescene.json 的全局模块片段带进 <new>，这里显式包含，免得与隐式 operator new 冲突

#include "JsonNlohmann.hpp"

export module wescene.testing.corpus;

import rstd;
import rstd.cppstd;
import wescene.json;
import wescene.pkg.parse;
import wescene.pkg_fs;
import wescene.fs;
import wescene.types;
import wescene.testing.pkg_header;

using namespace rstd::prelude;
using namespace rstd::literals;

export namespace owe::testing
{

struct WorkshopEntry {
    std::string id;
    std::string dir;
    owe::NJson  snapshot;
};

class Corpus {
public:
    // Returns the singleton, building it on first access.
    static const Corpus& instance();

    // All entries successfully dumped.
    const std::vector<WorkshopEntry>& entries() const { return entries_; }

    // Sorted unique sets of every version stamp observed across the corpus.
    const std::set<std::string>& pkg_versions() const { return pkg_versions_; }
    const std::set<int>&         texv_versions() const { return texv_versions_; }
    const std::set<int>&         texi_versions() const { return texi_versions_; }
    const std::set<int>&         texb_versions() const { return texb_versions_; }
    const std::set<int>&         texs_versions() const { return texs_versions_; }
    const std::set<int>&         tex_formats() const { return tex_formats_; }
    const std::set<int>&         mdlv_versions() const { return mdlv_versions_; }
    const std::set<int>&         mdls_versions() const { return mdls_versions_; }
    const std::set<int>&         mdla_versions() const { return mdla_versions_; }

    // Slice accessors.
    struct PkgRef {
        const WorkshopEntry* workshop;
    };
    struct TexRef {
        const WorkshopEntry* workshop;
        const owe::NJson*    tex;
    };
    struct MdlRef {
        const WorkshopEntry* workshop;
        const owe::NJson*    mdl;
    };

    std::vector<PkgRef> workshops_with_pkg(const std::string& pkgv) const;
    std::vector<TexRef> textures_with_texv(int v) const;
    std::vector<TexRef> textures_with_texi(int v) const;
    std::vector<TexRef> textures_with_texb(int v) const;
    std::vector<TexRef> textures_with_texs(int v) const;
    std::vector<TexRef> textures_with_format(int v) const;
    std::vector<MdlRef> mdls_with_mdlv(int v) const;
    std::vector<MdlRef> mdls_with_mdls(int v) const;
    std::vector<MdlRef> mdls_with_mdla(int v) const;

private:
    Corpus();
    void build();

    std::vector<WorkshopEntry> entries_;
    std::set<std::string>      pkg_versions_;
    std::set<int>              texv_versions_;
    std::set<int>              texi_versions_;
    std::set<int>              texb_versions_;
    std::set<int>              texs_versions_;
    std::set<int>              tex_formats_;
    std::set<int>              mdlv_versions_;
    std::set<int>              mdls_versions_;
    std::set<int>              mdla_versions_;
};

// Per-workshop JSON snapshot used by Corpus to index versions
// (textures via ReadTexMeta, puppets via full MdlParser::Parse).
// On failure returns a json object with `{"error": "..."}` and `err`
// is set to the same message.
owe::NJson DumpWorkshop(const std::string& workshop_dir, std::string& err);

} // namespace owe::testing

namespace owe::testing
{

namespace
{

namespace fs = std::filesystem;
using Json   = owe::NJson;

// 原 rstd as_i64：有符号整数、或不超过 INT64_MAX 的无符号整数才有值（浮点、布尔都没有）。
rstd::int64_t JsonI64Or(const Json& value, rstd::int64_t fallback) {
    if (! value.is_number_integer()) return fallback;
    if (value.is_number_unsigned() &&
        value.get<std::uint64_t>() > std::uint64_t(std::numeric_limits<std::int64_t>::max()))
        return fallback;
    return value.get<std::int64_t>();
}

auto JsonBoolOr(const Json* value, bool fallback) -> bool {
    return value != nullptr && value->is_boolean() ? value->get<bool>() : fallback;
}

auto JsonStringOr(const Json* value, std::string_view fallback) -> std::string_view {
    return value != nullptr && value->is_string()
               ? std::string_view(value->get_ref<const std::string&>())
               : fallback;
}

// 数字种类与原 JsonBuilder::IntoJson 相同：有符号→整数，无符号→无符号，浮点（含 f32）→double。
template<typename T>
Json SnapshotValue(const T& value) {
    if constexpr (requires { value.to_primitive(); })
        return SnapshotValue(value.to_primitive());
    else if constexpr (std::is_same_v<T, bool>)
        return Json(value);
    else if constexpr (std::is_integral_v<T> && std::is_signed_v<T>)
        return Json(static_cast<std::int64_t>(value));
    else if constexpr (std::is_integral_v<T>)
        return Json(static_cast<std::uint64_t>(value));
    else if constexpr (std::is_floating_point_v<T>)
        return Json(static_cast<double>(value));
    else
        return Json(std::string(std::string_view(value)));
}

inline Json SnapshotValue(const Json& value) { return value; }

inline Json SnapshotValue(ref<str> value) {
    return Json(std::string(rstd::cppstd::as_string_view(value)));
}

inline Json SnapshotValue(const String& value) { return SnapshotValue(value.as_str()); }

template<typename T>
Json SnapshotValue(const std::vector<T>& values) {
    auto out = Json::array();
    for (const auto& value : values) out.push_back(SnapshotValue(value));
    return out;
}

template<typename T, std::size_t N>
Json SnapshotValue(const std::array<T, N>& values) {
    auto out = Json::array();
    for (const auto& value : values) out.push_back(SnapshotValue(value));
    return out;
}

template<typename T>
Json SnapshotValue(const Vec<T>& values) {
    auto out = Json::array();
    for (const auto& value : values) out.push_back(SnapshotValue(value));
    return out;
}

template<typename T, rstd::size_t N>
Json SnapshotValue(const array<T, N>& values) {
    auto out = Json::array();
    for (const auto& value : values) out.push_back(SnapshotValue(value));
    return out;
}

template<typename T>
void SetSnapshot(Json& object, std::string_view key, const T& value) {
    object[std::string(key)] = SnapshotValue(value);
}

// Workshops that hang or crash the dumper.
const std::set<std::string> kSkipIds {
    "2435537849",
    "3346715292",
};

// The build can override the corpus roots. These fallbacks keep the tools
// runnable from the source root for ad-hoc development loops.
constexpr const char* kWorkshopDirMacro =
#ifdef WAYWALLEN_WORKSHOP_DIR
    WAYWALLEN_WORKSHOP_DIR
#else
    "workshop"
#endif
    ;

constexpr const char* kAssetsDirMacro =
#ifdef WAYWALLEN_ASSETS_DIR
    WAYWALLEN_ASSETS_DIR
#else
    ""
#endif
    ;

struct TexMeta {
    std::string path;
    int32_t     texv { 0 };
    int32_t     texi { 0 };
    int32_t     texb { 0 };
    int32_t     texs { 0 };
    int32_t     compo1 { 0 };
    int32_t     compo2 { 0 };
    int32_t     compo3 { 0 };
    int32_t     format { 0 };
    int32_t     image_type { 0 };
    int32_t     width { 0 };
    int32_t     height { 0 };
    int32_t     map_width { 0 };
    int32_t     map_height { 0 };
    int32_t     count { 0 };
    bool        is_sprite { false };
    int64_t     sprite_frames { 0 };
    bool        mipmap_pow2 { false };
    bool        mipmap_larger { false };
    int         wrap_s { 0 };
    int         wrap_t { 0 };
    int         min_filter { 0 };
    int         mag_filter { 0 };
    bool        ok { false };
};

TexMeta ReadTexMeta(owe::fs::VFS& vfs, const std::string& pkg_path) {
    TexMeta meta;
    meta.path = pkg_path;

    constexpr std::string_view prefix = "/materials/";
    constexpr std::string_view suffix = ".tex";
    if (pkg_path.compare(0, prefix.size(), prefix) != 0) return meta;
    if (pkg_path.size() < prefix.size() + suffix.size()) return meta;
    if (pkg_path.compare(pkg_path.size() - suffix.size(), suffix.size(), suffix) != 0) return meta;
    std::string name =
        pkg_path.substr(prefix.size(), pkg_path.size() - prefix.size() - suffix.size());

    owe::TexImageParser parser(&vfs);
    owe::ImageHeader    h;
    try {
        auto parsed = parser.ParseHeader(rstd::cppstd::as_str(name).unwrap());
        if (parsed.is_err()) return meta;
        h = rstd::move(parsed).unwrap_unchecked();
    } catch (const std::exception&) {
        return meta;
    }

    auto extra_val = [&](const std::string& k) -> int32_t {
        auto it = h.extraHeader.find(k);
        return it == h.extraHeader.end() ? 0 : it->second.val;
    };
    meta.texv          = extra_val("texv");
    meta.texi          = extra_val("texi");
    meta.texb          = extra_val("texb");
    meta.texs          = extra_val("texs");
    meta.compo1        = extra_val("compo1");
    meta.compo2        = extra_val("compo2");
    meta.compo3        = extra_val("compo3");
    meta.format        = static_cast<int32_t>(h.format);
    meta.image_type    = static_cast<int32_t>(h.type);
    meta.width         = h.width;
    meta.height        = h.height;
    meta.map_width     = h.mapWidth;
    meta.map_height    = h.mapHeight;
    meta.count         = h.count;
    meta.is_sprite     = h.isSprite;
    meta.sprite_frames = static_cast<int64_t>(h.spriteAnim.numFrames().to_primitive());
    meta.mipmap_pow2   = h.mipmap_pow2;
    meta.mipmap_larger = h.mipmap_larger;
    meta.wrap_s        = static_cast<int>(h.sample.wrapS);
    meta.wrap_t        = static_cast<int>(h.sample.wrapT);
    meta.min_filter    = static_cast<int>(h.sample.minFilter);
    meta.mag_filter    = static_cast<int>(h.sample.magFilter);
    meta.ok            = (meta.texv > 0 && meta.width > 0 && meta.height > 0);
    return meta;
}

bool ends_with(std::string_view s, std::string_view suffix) {
    return s.size() >= suffix.size() &&
           s.compare(s.size() - suffix.size(), suffix.size(), suffix) == 0;
}

void sort_by_path(Json& value) {
    if (! value.is_array()) return;
    std::vector<std::size_t> ordered;
    ordered.reserve(value.size());
    for (std::size_t index = 0; index < value.size(); ++index) ordered.push_back(index);
    std::sort(ordered.begin(), ordered.end(), [&](std::size_t a_index, std::size_t b_index) {
        const auto a_view = JsonStringOr(owe::Find(value[a_index], "path"), {});
        const auto b_view = JsonStringOr(owe::Find(value[b_index], "path"), {});
        return a_view.compare(b_view) < 0;
    });
    auto sorted = Json::array();
    for (auto index : ordered) sorted.push_back(value[index]);
    value = std::move(sorted);
}

template<typename Map>
Json map_to_json(const Map& m) {
    auto o = Json::object();
    for (const auto& [k, v] : m) SetSnapshot(o, k, v);
    return o;
}

Json dump_material(const owe::wpscene::Material& m) {
    auto out = Json::object();
    SetSnapshot(out, "shader", m.shader);
    SetSnapshot(out, "blending", m.blending);
    SetSnapshot(out, "cullmode", m.cullmode);
    SetSnapshot(out, "depthtest", m.depthtest);
    SetSnapshot(out, "depthwrite", m.depthwrite);
    SetSnapshot(out, "use_puppet", m.use_puppet);
    SetSnapshot(out, "textures", m.textures);
    out["combos"] = map_to_json(m.combos);
    out["constantshadervalues"] = map_to_json(m.constantshadervalues);
    return out;
}

Json dump_material_pass(const owe::wpscene::MaterialPass& p) {
    auto bind = Json::array();
    for (const auto& b : p.bind) {
        auto item = Json::object();
        SetSnapshot(item, "name", b.name);
        SetSnapshot(item, "index", b.index);
        bind.push_back(std::move(item));
    }
    auto out = Json::object();
    SetSnapshot(out, "target", p.target);
    SetSnapshot(out, "textures", p.textures);
    out["combos"] = map_to_json(p.combos);
    out["constantshadervalues"] = map_to_json(p.constantshadervalues);
    out["bind"] = std::move(bind);
    return out;
}

Json dump_effect_fbo(const owe::wpscene::EffectFbo& f) {
    auto out = Json::object();
    SetSnapshot(out, "name", f.name);
    SetSnapshot(out, "format", f.format);
    SetSnapshot(out, "scale", f.scale);
    return out;
}

// Field types in scene.json are inconsistent (origin can be either an
// array of floats or a "x y z" string), so we copy the raw value through
// instead of forcing a particular C++ type.
Json dump_object_common(const Json& obj) {
    auto        o  = Json::object();
    const auto* id = owe::Find(obj, "id");
    SetSnapshot(o, "id", id != nullptr ? static_cast<int>(JsonI64Or(*id, -1)) : -1);
    SetSnapshot(o, "name", JsonStringOr(owe::Find(obj, "name"), ""));
    // `visible` is sometimes a {script, value} object (scripted property);
    // json::value<bool> would throw type_error on that shape and tear down
    // the entire scene dump. Unwrap when present, default to true.
    bool visible = true;
    if (const auto* value = owe::Find(obj, "visible"); value != nullptr) {
        const auto* initial = owe::Find(*value, "value");
        visible             = JsonBoolOr(initial != nullptr ? initial : value, visible);
    }
    SetSnapshot(o, "visible", visible);
    constexpr std::array<std::string_view, 6> keys {
        "origin", "scale", "angles", "size", "parallaxDepth", "alignment",
    };
    for (auto key : keys) {
        if (const auto* value = owe::Find(obj, key); value != nullptr) o[std::string(key)] = *value;
    }
    return o;
}

Json dump_light_object(const Json& obj, owe::fs::VFS& vfs) {
    Json out = dump_object_common(obj);
    SetSnapshot(out, "kind", "light");
    owe::wpscene::LightObject lo;
    bool                      ok = false;
    try {
        ok = lo.FromJson(obj, vfs);
    } catch (const std::exception&) {
        ok = false;
    }
    SetSnapshot(out, "parsed", ok);
    if (! ok) return out;
    SetSnapshot(out, "light", lo.light);
    SetSnapshot(out, "color", lo.color);
    SetSnapshot(out, "intensity", lo.intensity);
    SetSnapshot(out, "radius", lo.radius);
    SetSnapshot(out, "origin_parsed", lo.origin);
    SetSnapshot(out, "scale_parsed", lo.scale);
    SetSnapshot(out, "angles_parsed", lo.angles);
    SetSnapshot(out, "visible_parsed", lo.visible);
    return out;
}

Json dump_particle_object(const Json& obj, owe::fs::VFS& vfs) {
    Json out = dump_object_common(obj);
    SetSnapshot(out, "kind", "particle");
    owe::wpscene::ParticleObject po;
    bool                         ok = false;
    try {
        ok = po.FromJson(obj, vfs);
    } catch (const std::exception&) {
        ok = false;
    }
    SetSnapshot(out, "parsed", ok);
    if (! ok) return out;
    SetSnapshot(out, "particle", po.particle);
    SetSnapshot(out, "origin_parsed", po.origin);
    SetSnapshot(out, "scale_parsed", po.scale);
    SetSnapshot(out, "angles_parsed", po.angles);
    SetSnapshot(out, "visible_parsed", po.visible);
    SetSnapshot(out, "emitter_count", static_cast<int>(po.particleObj.emitters.size()));
    SetSnapshot(out,
                "initializer_count",
                static_cast<int>(po.particleObj.initializers.size()));
    SetSnapshot(
        out, "operator_count", static_cast<int>(po.particleObj.operators.size()));
    SetSnapshot(out, "renderer_count", static_cast<int>(po.particleObj.renderers.size()));
    SetSnapshot(out, "controlpoint_count", static_cast<int>(po.particleObj.controlpoints.size()));
    SetSnapshot(out, "child_count", static_cast<int>(po.particleObj.children.size()));
    SetSnapshot(out, "maxcount", po.particleObj.maxcount);
    SetSnapshot(out, "starttime", static_cast<int>(po.particleObj.starttime));
    SetSnapshot(out, "animationmode", po.particleObj.animationmode);
    return out;
}

Json dump_sound_object(const Json& obj, owe::fs::VFS& vfs) {
    Json out = dump_object_common(obj);
    SetSnapshot(out, "kind", "sound");
    owe::wpscene::SoundObject so;
    bool                      ok = false;
    try {
        ok = so.FromJson(obj, vfs);
    } catch (const std::exception&) {
        ok = false;
    }
    SetSnapshot(out, "parsed", ok);
    if (! ok) return out;
    SetSnapshot(out, "playbackmode", so.playbackmode);
    SetSnapshot(out, "volume", so.volume);
    SetSnapshot(out, "mintime", so.mintime);
    SetSnapshot(out, "maxtime", so.maxtime);
    SetSnapshot(out, "visible_parsed", so.visible);
    SetSnapshot(out, "sound_paths", so.sound);
    return out;
}

Json dump_image_object(const Json& obj, owe::fs::VFS& vfs) {
    Json out = dump_object_common(obj);
    SetSnapshot(out, "kind", "image");
    owe::wpscene::ImageObject img;
    bool                      ok = false;
    try {
        ok = img.FromJson(obj, vfs);
    } catch (const std::exception&) {
        ok = false;
    }
    SetSnapshot(out, "parsed", ok);
    if (! ok) return out;
    SetSnapshot(out, "image", img.image);
    SetSnapshot(out, "color", img.color);
    SetSnapshot(out, "colorBlendMode", img.colorBlendMode);
    SetSnapshot(out, "alpha", img.alpha);
    SetSnapshot(out, "brightness", img.brightness);
    SetSnapshot(out, "fullscreen", img.fullscreen);
    SetSnapshot(out, "nopadding", img.nopadding);
    SetSnapshot(out, "origin_parsed", img.origin);
    SetSnapshot(out, "scale_parsed", img.scale);
    SetSnapshot(out, "angles_parsed", img.angles);
    SetSnapshot(out, "size_parsed", img.size);
    SetSnapshot(out, "visible_parsed", img.visible);
    SetSnapshot(out, "alignment_parsed", img.alignment);
    SetSnapshot(out, "puppet", img.puppet);
    out["material"] = dump_material(img.material);
    SetSnapshot(out, "effect_count", static_cast<int>(img.effects.size()));
    // ImageEffect::id and ::version are left uninitialised by the
    // parser when the source json omits them, so dumping their raw value
    // produces stack garbage. Skip them.
    auto effs = Json::array();
    for (const auto& e : img.effects) {
        auto je = Json::object();
        SetSnapshot(je, "name", e.name);
        SetSnapshot(je, "visible", e.visible);
        auto mats = Json::array();
        for (const auto& mm : e.materials) mats.push_back(dump_material(mm));
        je["materials"] = std::move(mats);
        auto passes = Json::array();
        for (const auto& p : e.passes) passes.push_back(dump_material_pass(p));
        je["passes"] = std::move(passes);
        auto fbos = Json::array();
        for (const auto& f : e.fbos) fbos.push_back(dump_effect_fbo(f));
        je["fbos"] = std::move(fbos);
        SetSnapshot(je, "material_count", static_cast<int>(e.materials.size()));
        SetSnapshot(je, "pass_count", static_cast<int>(e.passes.size()));
        SetSnapshot(je, "fbo_count", static_cast<int>(e.fbos.size()));
        effs.push_back(std::move(je));
    }
    out["effects"] = std::move(effs);
    return out;
}

template<typename Predicate>
std::vector<Corpus::TexRef> tex_filter(const std::vector<WorkshopEntry>& es, Predicate pred) {
    std::vector<Corpus::TexRef> out;
    for (const auto& e : es) {
        const auto* textures = owe::Find(e.snapshot, "textures");
        if (textures == nullptr || ! textures->is_array()) continue;
        for (const auto& t : *textures) {
            if (! JsonBoolOr(owe::Find(t, "ok"), false)) continue;
            if (pred(t)) out.push_back({ &e, &t });
        }
    }
    return out;
}

template<typename Predicate>
std::vector<Corpus::MdlRef> mdl_filter(const std::vector<WorkshopEntry>& es, Predicate pred) {
    std::vector<Corpus::MdlRef> out;
    for (const auto& e : es) {
        const auto* puppets = owe::Find(e.snapshot, "puppets");
        if (puppets == nullptr || ! puppets->is_array()) continue;
        for (const auto& m : *puppets) {
            if (pred(m)) out.push_back({ &e, &m });
        }
    }
    return out;
}

} // namespace

Json DumpWorkshop(const std::string& workshop_dir, std::string& err) {
    err.clear();
    auto out = Json::object();
    SetSnapshot(out, "workshop_dir", fs::path(workshop_dir).filename().string());

    const std::string pkg_path = workshop_dir + "/scene.pkg";
    if (! fs::exists(pkg_path)) {
        err = "scene.pkg not found at " + pkg_path;
        SetSnapshot(out, "error", err);
        return out;
    }

    std::string           pkg_version;
    std::vector<PkgEntry> pkg_entries;
    if (! ReadPkgHeader(pkg_path, pkg_version, pkg_entries)) {
        err = "failed to read pkg header";
        SetSnapshot(out, "error", err);
        return out;
    }

    bool has_scene_json = false;
    for (const auto& e : pkg_entries)
        if (e.path == "/scene.json") {
            has_scene_json = true;
            break;
        }

    auto jpkg = Json::object();
    SetSnapshot(jpkg, "version", pkg_version);
    SetSnapshot(jpkg, "file_count", static_cast<int>(pkg_entries.size()));
    SetSnapshot(jpkg, "has_scene_json", has_scene_json);
    out["pkg"] = std::move(jpkg);

    owe::fs::VFS vfs;
    auto         afs = owe::fs::make_physical_fs(owe::fs::ToPath(kAssetsDirMacro));
    if (afs.is_ok()) {
        (void)vfs.mount("/assets"_str, std::move(afs).unwrap_unchecked());
    }
    auto pfs = owe::fs::make_physical_fs(owe::fs::ToPath(workshop_dir));
    auto wfs = owe::fs::WPPkgFs::open(owe::fs::ToPath(pkg_path));
    if (wfs.is_err()) {
        err = "WPPkgFs::open failed";
        SetSnapshot(out, "error", err);
        return out;
    }
    (void)vfs.mount("/assets"_str, wfs->mount_handle());
    if (pfs.is_ok()) {
        (void)vfs.mount("/assets"_str, std::move(pfs).unwrap_unchecked());
    }

    if (has_scene_json) {
        auto stream = owe::fs::OpenBinary(vfs, "/assets/scene.json");
        if (stream.is_ok()) {
            std::string text        = stream->ReadAllStr();
            auto        parsed_json = owe::ParseNJson(text);
            if (parsed_json.is_ok()) {
                auto                        j = parsed_json.unwrap();
                owe::wpscene::SceneMetadata scene;
                bool                        parsed = scene.FromJson(j);
                auto                        jscene = Json::object();
                SetSnapshot(jscene, "parsed", parsed);
                SetSnapshot(jscene, "is_ortho", scene.general.isOrtho);
                auto ortho = Json::object();
                SetSnapshot(ortho, "width", scene.general.orthogonalprojection.width);
                SetSnapshot(ortho, "height", scene.general.orthogonalprojection.height);
                jscene["ortho"] = std::move(ortho);
                auto camera = Json::object();
                SetSnapshot(camera, "center", scene.camera.center);
                SetSnapshot(camera, "eye", scene.camera.eye);
                SetSnapshot(camera, "up", scene.camera.up);
                jscene["camera"] = std::move(camera);
                // cameraparallaxamount/delay/mouseinfluence are undefaulted
                // floats in SceneGeneral, so when the source scene.json
                // omits them the parser leaves stack garbage. Only emit them
                // when cameraparallax is enabled.
                auto jgen = Json::object();
                SetSnapshot(jgen, "clearcolor", scene.general.clearcolor);
                SetSnapshot(jgen, "ambientcolor", scene.general.ambientcolor);
                SetSnapshot(jgen, "skylightcolor", scene.general.skylightcolor);
                SetSnapshot(jgen, "cameraparallax", scene.general.cameraparallax);
                SetSnapshot(jgen, "zoom", scene.general.zoom);
                SetSnapshot(jgen, "fov", scene.general.fov);
                SetSnapshot(jgen, "nearz", scene.general.nearz);
                SetSnapshot(jgen, "farz", scene.general.farz);
                if (scene.general.cameraparallax) {
                    SetSnapshot(jgen, "cameraparallaxamount", scene.general.cameraparallaxamount);
                    SetSnapshot(jgen, "cameraparallaxdelay", scene.general.cameraparallaxdelay);
                    SetSnapshot(jgen,
                                "cameraparallaxmouseinfluence",
                                scene.general.cameraparallaxmouseinfluence);
                }
                jscene["general"] = std::move(jgen);
                auto jobjects = Json::array();
                if (const auto* objects = owe::Find(j, "objects");
                    objects != nullptr && objects->is_array())
                    for (const auto& obj : *objects) {
                        if (owe::Find(obj, "image") != nullptr)
                            jobjects.push_back(dump_image_object(obj, vfs));
                        else if (owe::Find(obj, "light") != nullptr)
                            jobjects.push_back(dump_light_object(obj, vfs));
                        else if (owe::Find(obj, "particle") != nullptr)
                            jobjects.push_back(dump_particle_object(obj, vfs));
                        else if (owe::Find(obj, "sound") != nullptr)
                            jobjects.push_back(dump_sound_object(obj, vfs));
                        else {
                            Json o = dump_object_common(obj);
                            SetSnapshot(o, "kind", "unknown");
                            jobjects.push_back(std::move(o));
                        }
                    }
                std::vector<std::size_t> ordered;
                ordered.reserve(jobjects.size());
                for (std::size_t index = 0; index < jobjects.size(); ++index)
                    ordered.push_back(index);
                auto object_id = [&](std::size_t index) {
                    const auto* id = owe::Find(jobjects[index], "id");
                    return id != nullptr ? JsonI64Or(*id, -1) : -1;
                };
                std::sort(
                    ordered.begin(), ordered.end(), [&](std::size_t a_index, std::size_t b_index) {
                        return object_id(a_index) < object_id(b_index);
                    });
                auto sorted_objects = Json::array();
                for (auto index : ordered) sorted_objects.push_back(jobjects[index]);
                SetSnapshot(jscene, "object_count", static_cast<int>(ordered.size()));
                jscene["objects"] = std::move(sorted_objects);
                out["scene"] = std::move(jscene);
            } else {
                auto error = Json::object();
                SetSnapshot(error, "parsed", false);
                SetSnapshot(error, "error", "invalid JSON");
                out["scene"] = std::move(error);
            }
        }
    }

    {
        auto jtex = Json::array();
        for (const auto& e : pkg_entries) {
            if (! ends_with(e.path, ".tex")) continue;
            if (e.path.rfind("/materials/", 0) != 0) continue;
            std::string vfs_path = "/assets" + e.path;
            TexMeta     m        = ReadTexMeta(vfs, e.path);
            auto        jm       = Json::object();
            SetSnapshot(jm, "path", e.path);
            SetSnapshot(jm, "ok", m.ok);
            SetSnapshot(jm, "texv", m.texv);
            SetSnapshot(jm, "texi", m.texi);
            SetSnapshot(jm, "texb", m.texb);
            SetSnapshot(jm, "texs", m.texs);
            SetSnapshot(jm, "compo1", m.compo1);
            SetSnapshot(jm, "compo2", m.compo2);
            SetSnapshot(jm, "compo3", m.compo3);
            SetSnapshot(jm, "format", m.format);
            SetSnapshot(jm, "image_type", m.image_type);
            SetSnapshot(jm, "width", m.width);
            SetSnapshot(jm, "height", m.height);
            SetSnapshot(jm, "map_width", m.map_width);
            SetSnapshot(jm, "map_height", m.map_height);
            SetSnapshot(jm, "count", m.count);
            SetSnapshot(jm, "is_sprite", m.is_sprite);
            SetSnapshot(jm, "sprite_frames", m.sprite_frames);
            SetSnapshot(jm, "mipmap_pow2", m.mipmap_pow2);
            SetSnapshot(jm, "mipmap_larger", m.mipmap_larger);
            SetSnapshot(jm, "wrap_s", m.wrap_s);
            SetSnapshot(jm, "wrap_t", m.wrap_t);
            SetSnapshot(jm, "min_filter", m.min_filter);
            SetSnapshot(jm, "mag_filter", m.mag_filter);
            jtex.push_back(std::move(jm));
        }
        sort_by_path(jtex);
        out["textures"] = std::move(jtex);
    }

    auto emit_flag = [](uint32_t flag) {
        auto flag_arr = Json::array();
        for (int byte_idx = 0; byte_idx < 4; ++byte_idx) {
            uint8_t     b = static_cast<uint8_t>((flag >> (byte_idx * 8)) & 0xFFu);
            std::string bits(8, '0');
            for (int i = 0; i < 8; ++i)
                if (b & (1u << (7 - i))) bits[i] = '1';
            flag_arr.push_back(std::move(bits));
        }
        return flag_arr;
    };

    auto jmdl = Json::array();
    for (const auto& e : pkg_entries) {
        if (! ends_with(e.path, ".mdl")) continue;
        // MdlParser::Parse expects a path relative to /assets without the
        // leading slash.
        std::string rel = e.path;
        if (! rel.empty() && rel.front() == '/') rel.erase(0, 1);
        Mdl  mdl;
        bool ok = false;
        try {
            ok = owe::MdlParser::Parse(rstd::cppstd::as_str(rel).unwrap(), vfs, mdl, nullptr);
        } catch (const std::exception&) {
            ok = false;
        }
        auto jm = Json::object();
        SetSnapshot(jm, "path", e.path);
        SetSnapshot(jm, "ok", ok);
        SetSnapshot(jm, "mdlv", mdl.header.mdlv);
        jm["flag"] = emit_flag(mdl.header.mdl_flag);
        SetSnapshot(jm, "skin_count", static_cast<int64_t>(mdl.header.skin_count));
        SetSnapshot(jm, "mesh_count", static_cast<int64_t>(mdl.header.mesh_count));
        SetSnapshot(jm, "mdls", mdl.mdls);
        SetSnapshot(jm, "mdla", mdl.mdla);
        const Mdl::Mesh* m0 = mdl.meshes.is_empty() ? nullptr : &mdl.meshes[usize()];
        SetSnapshot(jm,
                    "mat_json_file",
                    m0 && ! m0->mat_json_files.is_empty() ? m0->mat_json_files[usize()].as_str()
                                                          : ref<str>());
        SetSnapshot(
            jm, "vertex_count", m0 ? static_cast<int>(m0->positions.len().to_primitive()) : 0);
        SetSnapshot(jm, "index_count", m0 ? static_cast<int>(m0->indices.len().to_primitive()) : 0);
        SetSnapshot(
            jm, "vert_extra_count", m0 ? static_cast<int>(m0->part_uv2.len().to_primitive()) : 0);
        SetSnapshot(jm, "part_count", m0 ? static_cast<int>(m0->parts.len().to_primitive()) : 0);
        auto parts_arr = Json::array();
        if (m0) {
            for (const auto& pt : m0->parts) {
                auto part = Json::object();
                SetSnapshot(part, "id", static_cast<int64_t>(pt.id));
                SetSnapshot(part, "start", static_cast<int64_t>(pt.start));
                SetSnapshot(part, "size", static_cast<int64_t>(pt.size));
                parts_arr.push_back(std::move(part));
            }
        }
        jm["parts"] = std::move(parts_arr);
        SetSnapshot(jm,
                    "bones",
                    ok && mdl.puppet.is_some()
                        ? static_cast<int>((*mdl.puppet)->bones.len().to_primitive())
                        : 0);
        SetSnapshot(jm,
                    "anims",
                    ok && mdl.puppet.is_some()
                        ? static_cast<int>((*mdl.puppet)->anims.len().to_primitive())
                        : 0);
        if (ok && mdl.puppet.is_some()) {
            auto bones = Json::array();
            for (const auto& b : (*mdl.puppet)->bones) {
                auto jb = Json::object();
                SetSnapshot(jb, "name", b.name);
                SetSnapshot(jb, "bind_parent", static_cast<int64_t>(b.bind_parent));
                SetSnapshot(jb, "anim_parent", static_cast<int64_t>(b.anim_parent));
                SetSnapshot(jb, "has_sim_json", ! b.simulation_json.is_empty());
                SetSnapshot(jb, "has_file_skin_pivot", b.has_file_skin_pivot);
                SetSnapshot(jb,
                            "centroid_offset",
                            std::array { b.vertex_centroid_offset.x(),
                                         b.vertex_centroid_offset.y(),
                                         b.vertex_centroid_offset.z() });
                std::array<double, 4> col_sums { 0, 0, 0, 0 };
                for (int c = 0; c < 4; ++c)
                    for (int r = 0; r < 4; ++r)
                        col_sums[static_cast<std::size_t>(c)] += b.local_bind.matrix()(r, c);
                SetSnapshot(jb, "transform_col_sums", col_sums);
                bones.push_back(std::move(jb));
            }
            jm["bone_tree"] = std::move(bones);
            SetSnapshot(jm,
                        "attachment_count",
                        static_cast<int>((*mdl.puppet)->attachments.len().to_primitive()));
            auto atts = Json::array();
            for (const auto& a : (*mdl.puppet)->attachments) {
                auto attachment = Json::object();
                SetSnapshot(attachment, "name", a.name);
                atts.push_back(std::move(attachment));
            }
            jm["attachments"] = std::move(atts);

            auto anims = Json::array();
            for (const auto& a : (*mdl.puppet)->anims) {
                auto ja = Json::object();
                SetSnapshot(ja, "id", a.id);
                SetSnapshot(ja, "fps", a.fps);
                SetSnapshot(ja, "length", a.length);
                SetSnapshot(ja, "name", a.name);
                SetSnapshot(ja, "mode", static_cast<int>(a.mode));
                SetSnapshot(
                    ja, "bone_track_count", static_cast<int>(a.bone_tracks.len().to_primitive()));
                SetSnapshot(ja, "has_trans", a.trans.is_some());
                SetSnapshot(ja,
                            "blend_curves_count",
                            static_cast<int>(a.blend_curves.len().to_primitive()));
                SetSnapshot(
                    ja, "v4_events_count", static_cast<int>(a.v4_events.len().to_primitive()));
                SetSnapshot(ja, "has_aabb", a.has_aabb);
                SetSnapshot(ja,
                            "scalar_curves_count",
                            static_cast<int>(a.scalar_curves.len().to_primitive()));
                SetSnapshot(ja, "events_count", static_cast<int>(a.events.len().to_primitive()));
                int total_frames = 0;
                for (const auto& bt : a.bone_tracks)
                    total_frames += static_cast<int>(bt.frames.len().to_primitive());
                SetSnapshot(ja, "total_bone_frames", total_frames);
                auto moved = Json::array();
                for (usize ti {}; ti < a.bone_tracks.len(); ++ti) {
                    const auto& tk = a.bone_tracks[ti];
                    if (tk.frames.is_empty()) continue;
                    const auto& f0      = tk.frames[usize()];
                    bool        any_pos = false, any_sc = false, any_an = false;
                    for (const auto& fr : tk.frames) {
                        if ((fr.position - f0.position).norm() > 0.5f) any_pos = true;
                        if ((fr.scale - f0.scale).norm() > 0.01f) any_sc = true;
                        if ((fr.angle - f0.angle).norm() > 0.001f) any_an = true;
                    }
                    if (any_pos || any_sc || any_an) {
                        auto item = Json::object();
                        SetSnapshot(item, "i", static_cast<int>(ti.to_primitive()));
                        SetSnapshot(item, "p", any_pos);
                        SetSnapshot(item, "s", any_sc);
                        SetSnapshot(item, "a", any_an);
                        moved.push_back(std::move(item));
                    }
                }
                ja["moved_bones"] = std::move(moved);
                anims.push_back(std::move(ja));
            }
            jm["anim_tracks"] = std::move(anims);
        }
        jmdl.push_back(std::move(jm));
    }
    sort_by_path(jmdl);
    out["puppets"] = std::move(jmdl);

    return out;
}

const Corpus& Corpus::instance() {
    static const Corpus c;
    return c;
}

Corpus::Corpus() { build(); }

void Corpus::build() {
    fs::path root { kWorkshopDirMacro };
    if (! fs::exists(root) || ! fs::is_directory(root)) {
        std::fprintf(stderr, "corpus: workshop dir %s missing\n", root.string().c_str());
        return;
    }

    std::vector<fs::path> dirs;
    for (auto& e : fs::directory_iterator(root)) {
        if (! e.is_directory()) continue;
        if (! fs::exists(e.path() / "scene.pkg")) continue;
        dirs.push_back(e.path());
    }
    std::sort(dirs.begin(), dirs.end());

    entries_.reserve(dirs.size());
    for (const auto& d : dirs) {
        std::string id = d.filename().string();
        if (kSkipIds.contains(id)) continue;

        std::string err;
        auto        snap = DumpWorkshop(d.string(), err);
        if (! err.empty()) {
            std::fprintf(stderr, "corpus: skip %s: %s\n", id.c_str(), err.c_str());
            continue;
        }

        WorkshopEntry e { std::move(id), d.string(), std::move(snap) };

        if (const auto* pkg = owe::Find(e.snapshot, "pkg"); pkg != nullptr) {
            const auto* version = owe::Find(*pkg, "version");
            if (version != nullptr && version->is_string())
                pkg_versions_.insert(version->get<std::string>());
        }
        auto insert_stamp = [](std::set<int>& into, const Json& from, std::string_view key,
                               rstd::int64_t fallback) {
            if (const auto* value = owe::Find(from, key); value != nullptr)
                into.insert(static_cast<int>(JsonI64Or(*value, fallback)));
        };
        if (const auto* textures = owe::Find(e.snapshot, "textures");
            textures != nullptr && textures->is_array())
            for (const auto& t : *textures) {
                if (! JsonBoolOr(owe::Find(t, "ok"), false)) continue;
                insert_stamp(texv_versions_, t, "texv", 0);
                insert_stamp(texi_versions_, t, "texi", 0);
                insert_stamp(texb_versions_, t, "texb", 0);
                insert_stamp(texs_versions_, t, "texs", 0);
                insert_stamp(tex_formats_, t, "format", -1);
            }
        if (const auto* puppets = owe::Find(e.snapshot, "puppets");
            puppets != nullptr && puppets->is_array())
            for (const auto& m : *puppets) {
                insert_stamp(mdlv_versions_, m, "mdlv", 0);
                insert_stamp(mdls_versions_, m, "mdls", 0);
                insert_stamp(mdla_versions_, m, "mdla", 0);
            }
        entries_.push_back(std::move(e));
    }

    std::fprintf(stderr,
                 "corpus: indexed %zu workshops; pkgv=%zu texv=%zu texi=%zu texb=%zu "
                 "texs=%zu fmt=%zu mdlv=%zu mdls=%zu mdla=%zu\n",
                 entries_.size(),
                 pkg_versions_.size(),
                 texv_versions_.size(),
                 texi_versions_.size(),
                 texb_versions_.size(),
                 texs_versions_.size(),
                 tex_formats_.size(),
                 mdlv_versions_.size(),
                 mdls_versions_.size(),
                 mdla_versions_.size());
}

std::vector<Corpus::PkgRef> Corpus::workshops_with_pkg(const std::string& v) const {
    std::vector<PkgRef> out;
    for (const auto& e : entries_) {
        const auto* pkg     = owe::Find(e.snapshot, "pkg");
        const auto* version = pkg != nullptr ? owe::Find(*pkg, "version") : nullptr;
        if (version != nullptr && version->is_string() && version->get_ref<const std::string&>() == v)
            out.push_back({ &e });
    }
    return out;
}

std::vector<Corpus::TexRef> Corpus::textures_with_texv(int v) const {
    return tex_filter(entries_, [v](const Json& t) {
        const auto* value = owe::Find(t, "texv");
        return value != nullptr && JsonI64Or(*value, -1) == v;
    });
}
std::vector<Corpus::TexRef> Corpus::textures_with_texi(int v) const {
    return tex_filter(entries_, [v](const Json& t) {
        const auto* value = owe::Find(t, "texi");
        return value != nullptr && JsonI64Or(*value, -1) == v;
    });
}
std::vector<Corpus::TexRef> Corpus::textures_with_texb(int v) const {
    return tex_filter(entries_, [v](const Json& t) {
        const auto* value = owe::Find(t, "texb");
        return value != nullptr && JsonI64Or(*value, -1) == v;
    });
}
std::vector<Corpus::TexRef> Corpus::textures_with_texs(int v) const {
    return tex_filter(entries_, [v](const Json& t) {
        const auto* value = owe::Find(t, "texs");
        return value != nullptr && JsonI64Or(*value, -1) == v;
    });
}
std::vector<Corpus::TexRef> Corpus::textures_with_format(int v) const {
    return tex_filter(entries_, [v](const Json& t) {
        const auto* value = owe::Find(t, "format");
        return value != nullptr && JsonI64Or(*value, -1) == v;
    });
}
std::vector<Corpus::MdlRef> Corpus::mdls_with_mdlv(int v) const {
    return mdl_filter(entries_, [v](const Json& m) {
        const auto* value = owe::Find(m, "mdlv");
        return value != nullptr && JsonI64Or(*value, -1) == v;
    });
}
std::vector<Corpus::MdlRef> Corpus::mdls_with_mdls(int v) const {
    return mdl_filter(entries_, [v](const Json& m) {
        const auto* value = owe::Find(m, "mdls");
        return value != nullptr && JsonI64Or(*value, -1) == v;
    });
}
std::vector<Corpus::MdlRef> Corpus::mdls_with_mdla(int v) const {
    return mdl_filter(entries_, [v](const Json& m) {
        const auto* value = owe::Find(m, "mdla");
        return value != nullptr && JsonI64Or(*value, -1) == v;
    });
}

} // namespace owe::testing
