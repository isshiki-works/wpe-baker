export module weweb:manifest;

import rstd.cppstd;
import wescene.json;

export namespace weweb
{

struct WebManifest {
    std::string               title;
    std::string               entry_html;
    owe::Json                 user_props;
    rstd::Option<std::string> preview;
};

rstd::Option<WebManifest> LoadWebManifest(const std::filesystem::path& workshop_dir);

} // namespace weweb
