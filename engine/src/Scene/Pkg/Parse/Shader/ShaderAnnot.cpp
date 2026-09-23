module;
#include <owe/compat.hpp>
#include <owe/std.hpp>

#include "JsonNlohmann.hpp"


module wescene.pkg.parse;
import :shader_lex;

using namespace rstd::prelude;
using namespace rstd::literals;

// WE shader annotation collector. Walks the source line by line and pulls
// `// [COMBO]` / `uniform NAME; // {json}` annotations into ShaderInfo.
// Collection is unconditional — #if/#endif dead-branch stripping is glslang's
// job downstream, and gating here creates chicken-and-egg cycles (texture
// combo flag inside `#if MASK == 1` depends on its own annotation).

namespace owe
{

namespace
{

using shader_lex::Cursor;
using shader_lex::LineWalker;

bool TryParseAnnotationJson(std::string_view source, NJson& result) {
    auto parsed = ParseNJson(source);
    if (parsed.is_err()) return false;
    result = parsed.unwrap();
    return true;
}

bool CanStartNumberToken(std::string_view source, std::size_t pos) {
    while (pos > 0) {
        --pos;
        char ch = source[pos];
        if (ch == ' ' || ch == '\t' || ch == '\r' || ch == '\n') continue;
        return ch == '[' || ch == '{' || ch == ':' || ch == ',';
    }
    return true;
}

Option<std::string> NormalizeAnnotationNumbers(std::string_view source) {
    std::string out;
    out.reserve(source.size());

    bool in_string = false;
    bool escaped   = false;
    bool changed   = false;
    for (std::size_t i = 0; i < source.size();) {
        char ch = source[i];
        if (in_string) {
            out.push_back(ch);
            if (escaped) {
                escaped = false;
            } else if (ch == '\\') {
                escaped = true;
            } else if (ch == '"') {
                in_string = false;
            }
            ++i;
            continue;
        }

        if (ch == '"') {
            in_string = true;
            out.push_back(ch);
            ++i;
            continue;
        }

        if ((ch == '-' || (ch >= '0' && ch <= '9')) && CanStartNumberToken(source, i)) {
            if (ch == '-') {
                if (i + 1 >= source.size() || source[i + 1] < '0' || source[i + 1] > '9') {
                    out.push_back(ch);
                    ++i;
                    continue;
                }
                out.push_back(ch);
                ++i;
            }
            while (i + 1 < source.size() && source[i] == '0' && source[i + 1] >= '0' &&
                   source[i + 1] <= '9') {
                changed = true;
                ++i;
            }
        }

        out.push_back(source[i]);
        ++i;
    }

    if (! changed) return None();
    return Some(rstd::move(out));
}

bool ParseAnnotationJson(std::string_view source, NJson& result) {
    if (TryParseAnnotationJson(source, result)) return true;
    auto normalized = NormalizeAnnotationNumbers(source);
    return normalized && TryParseAnnotationJson(*normalized, result);
}

void HandleComboLine(ShaderInfo* info, std::string_view line) {
    auto brace = line.find('{');
    if (brace == std::string_view::npos) return;
    NJson j;
    if (! ParseAnnotationJson(line.substr(brace), j)) return;
    if (Find(j, "combo") == nullptr) return;
    wpscene::Combo combo;
    combo.FromJson(j);
    if (combo.combo.is_empty()) return;
    info->combos[rstd::cppstd::to_string(combo.combo.as_str())] =
        std::to_string(combo.default_.to_primitive());
    info->combo_defs.push(rstd::move(combo));
}

void HandlePassLine(ShaderInfo* info, std::string_view line) {
    constexpr std::string_view prefix { "// [PASS] shadow" };
    auto                       offset = line.find(prefix);
    if (offset == std::string_view::npos) return;
    auto value = line.substr(offset + prefix.size());
    while (! value.empty() && (value.front() == ' ' || value.front() == '\t'))
        value.remove_prefix(1);
    while (! value.empty() && (value.back() == ' ' || value.back() == '\t' || value.back() == '\r'))
        value.remove_suffix(1);
    if (! value.empty()) info->shadow_pass = String::make(rstd::cppstd::as_str(value).unwrap());
}

void HandleUniformLine(ShaderInfo* info, std::span<const ShaderTexInfo> texinfos,
                       std::string_view line) {
    Cursor c(rstd::cppstd::as_str(line).unwrap());
    c.SkipHSpace();
    if (! c.MatchKeyword("uniform"_str)) return;
    c.SkipHSpace();
    auto tn = shader_lex::ReadTypeName(c);
    if (! tn) return;
    c.SkipHSpace();
    (void)c.ReadArraySuffix();
    c.SkipHSpace();
    if (! c.MatchChar(';')) return;

    // Find the trailing `// {json}` blob.
    while (! c.Eof() && c.Peek() != '/') c.Advance();
    if (! c.MatchPunct("//"_str)) return;
    while (! c.Eof() && c.Peek() != '{') c.Advance();
    if (c.Eof()) return;
    NJson sv_json;
    if (! ParseAnnotationJson(line.substr(c.Pos().to_primitive()), sv_json)) return;

    auto name = rstd::cppstd::as_string_view(tn->name);

    std::string material_key;
    GetJsonValue(sv_json, "material", material_key, false);
    if (! material_key.empty()) info->alias[material_key] = std::string(name);

    const bool        is_tex   = name.compare(0, 9, "g_Texture") == 0;
    const std::size_t texcount = texinfos.size();

    if (is_tex) {
        wpscene::UniformTex wput;
        wput.FromJson(sv_json);
        i32  index {};
        auto parsed = rstd::from_str<i32>(rstd::cppstd::as_str(name.substr(9)).unwrap());
        if (parsed.is_ok()) {
            index = rstd::move(parsed).unwrap();
        } else {
            rstd_error("invalid shader texture index: {}", name);
        }
        wput.slot = index;
        if (! wput.default_.is_empty()) {
            info->defTexs.push_back({ index, rstd::cppstd::to_string(wput.default_.as_str()) });
        }
        const bool has_texture = index >= i32() && rstd::as_cast<usize>(index) < usize(texcount);
        const std::size_t texture_index = rstd::as_cast<usize>(index).to_primitive();
        if (! wput.combo.is_empty()) {
            const bool enabled = has_texture && texinfos[texture_index].enabled;
            info->combos[rstd::cppstd::to_string(wput.combo.as_str())] = enabled ? "1" : "0";
        }
        if (has_texture && texinfos[texture_index].enabled) {
            auto&       compos = texinfos[texture_index].composEnabled;
            std::size_t num =
                std::min(compos.len().to_primitive(), wput.components.len().to_primitive());
            for (std::size_t i = 0; i < num; i++) {
                if (compos[usize(i)]) {
                    auto& combo = wput.components[usize(i)].combo;
                    info->combos[rstd::cppstd::to_string(combo.as_str())] = "1";
                }
            }
        }
        info->texture_uniforms.push(rstd::move(wput));
    } else {
        wpscene::UniformVar var;
        var.FromJson(sv_json, String::make(tn->name));
        if (auto value = Find(sv_json, "default"); value != nullptr) {
            ShaderValue sv;
            if (value->is_string()) {
                std::vector<float> values;
                GetJsonValue(*value, values);
                sv = std::span<const float>(values);
            } else if (value->is_number()) {
                sv.setSize(usize(1));
                GetJsonValue(*value, sv[usize()]);
            }
            info->svs[std::string(name)] = sv;
        }
        if (Find(sv_json, "combo") != nullptr) {
            std::string cname;
            GetJsonValue(sv_json, "combo", cname);
            if (! cname.empty()) info->combos[cname] = "1";
        }
        info->scalar_uniforms.push(rstd::move(var));
    }
}

} // namespace

void ParseShader(const std::string& src, ShaderInfo* info,
                 const std::vector<ShaderTexInfo>& texinfos_vec) {
    std::span<const ShaderTexInfo> texinfos(texinfos_vec.data(), texinfos_vec.size());
    LineWalker                     w(rstd::cppstd::as_str(src).unwrap());
    for (; ! w.Done(); w.Step()) {
        auto line = rstd::cppstd::as_string_view(w.Line());
        if (line.empty()) continue;
        // Helpers / forward decls above `void main()` are the annotated
        // region; the function body never carries new annotations.
        if (line.find("void main(") != std::string_view::npos) break;

        if (line.find("// [COMBO]") != std::string_view::npos) {
            HandleComboLine(info, line);
            continue;
        }
        if (line.find("// [PASS] shadow") != std::string_view::npos) {
            HandlePassLine(info, line);
            continue;
        }
        // Cheap pre-check: only attempt the full keyword match if the trimmed
        // line could plausibly start with `uniform`.
        Cursor probe(rstd::cppstd::as_str(line).unwrap());
        probe.SkipHSpace();
        if (probe.Eof() || probe.Peek() != 'u') continue;
        HandleUniformLine(info, texinfos, line);
    }
}

} // namespace owe
