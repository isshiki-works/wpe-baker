module weweb;

import rstd.cppstd;

import :cef;
import :cef_internal;

namespace weweb
{

std::string BuildPropertyListenerSnippet(const owe::Json& props) {
    // The page side typically registers a listener like:
    //
    //   window.wallpaperPropertyListener = {
    //     applyUserProperties: function(props) { ... }
    //   };
    //
    // We hand it the project.json `general.properties` object verbatim;
    // each entry preserves its `type` and `value` fields, matching what
    // WE's own runtime delivers.
    std::string snippet =
        "(function(){"
        "  if (typeof window.wallpaperPropertyListener !== 'object') return;"
        "  if (typeof window.wallpaperPropertyListener.applyUserProperties !== 'function') return;"
        "  try {"
        "    window.wallpaperPropertyListener.applyUserProperties(";
    snippet += props.is_object() ? owe::Dump(props) : std::string { "{}" };
    snippet += "    );"
               "  } catch (e) {"
               "    console.error('weweb: applyUserProperties threw:', e);"
               "  }"
               "})();";
    return snippet;
}

void InjectUserProperties(CefRefPtr<CefBrowser> browser, const owe::Json& props) {
    if (! browser) return;
    auto frame = browser->GetMainFrame();
    if (! frame) return;
    frame->ExecuteJavaScript(
        BuildPropertyListenerSnippet(props), "weweb://internal/inject_user_properties.js", 0);
}

} // namespace weweb
