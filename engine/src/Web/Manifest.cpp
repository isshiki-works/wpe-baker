module;

#include <cstdio>

module weweb;

import rstd.cppstd;

import :manifest;

using namespace rstd::literals;

namespace weweb
{

namespace
{

std::string LowerAscii(std::string s) {
    for (auto& c : s) {
        c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    }
    return s;
}

} // namespace

rstd::Option<WebManifest> LoadWebManifest(const std::filesystem::path& workshop_dir) {
    auto          pj_path = workshop_dir / "project.json";
    std::ifstream is(pj_path);
    if (! is) {
        std::fprintf(stderr, "weweb: cannot open %s\n", pj_path.c_str());
        return rstd::None();
    }

    // Invalid project.json is input data; keep parse failure on the
    // diagnostic return path.
    std::string source(std::istreambuf_iterator<char>(is), {});
    auto        parsed = owe::ParseJson(source, { .allow_comments = true });
    if (parsed.is_err()) {
        auto error = parsed.unwrap_err();
        std::fprintf(stderr,
                     "weweb: invalid JSON in %s at line %zu column %zu\n",
                     pj_path.c_str(),
                     error.line().to_primitive(),
                     error.column().to_primitive());
        return rstd::None();
    }
    auto root = parsed.unwrap();

    auto type = root.get("type"_str);
    if (type.is_none() || (*type)->as_str().is_none()) {
        std::fprintf(stderr, "weweb: %s is missing a string \"type\" field\n", pj_path.c_str());
        return rstd::None();
    }
    // WE corpus has both "web" and "Web" for the type field; fold case.
    auto normalized_type = LowerAscii(rstd::cppstd::to_string(*(*type)->as_str()));
    if (normalized_type != "web") {
        std::fprintf(stderr,
                     "weweb: %s has type=\"%s\", expected \"web\"\n",
                     pj_path.c_str(),
                     normalized_type.c_str());
        return rstd::None();
    }

    WebManifest m;
    m.entry_html = "index.html";
    if (auto file = root.get("file"_str); file.is_some()) {
        auto string = (*file)->as_str();
        if (string.is_some()) m.entry_html = rstd::cppstd::to_string(*string);
    }
    m.title = "Wallpaper";
    if (auto title = root.get("title"_str); title.is_some()) {
        auto string = (*title)->as_str();
        if (string.is_some()) m.title = rstd::cppstd::to_string(*string);
    }

    if (auto preview = root.get("preview"_str); preview.is_some()) {
        auto string = (*preview)->as_str();
        if (string.is_some()) m.preview = rstd::Some(rstd::cppstd::to_string(*string));
    }

    if (auto general = root.get("general"_str); general.is_some())
        if (auto properties = (*general)->get("properties"_str); properties.is_some())
            m.user_props = (*properties)->clone();

    return rstd::Some(rstd::move(m));
}

} // namespace weweb
