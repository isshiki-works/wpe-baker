#define GLFW_INCLUDE_VULKAN
#include <GLFW/glfw3.h>
#include <cerrno>
#include <cstring>
#include <fcntl.h>
#include <unistd.h>

import rstd.cppstd;
import rstd.log;
import wavsen.audio;
import wescene.core;
import wescene.json;
import wescene.scene_wallpaper;
import wescene.utils;
import viewer.common;
import viewer.audio;

using namespace std;
using namespace rstd::prelude;
using namespace rstd::literals;

class StdinJsonControl {
public:
    explicit StdinJsonControl(bool enabled): m_enabled(enabled) {
        if (! m_enabled) return;
        m_original_flags = ::fcntl(STDIN_FILENO, F_GETFL, 0);
        if (m_original_flags < 0 ||
            ::fcntl(STDIN_FILENO, F_SETFL, m_original_flags | O_NONBLOCK) < 0) {
            std::cerr << "--stdin-json: cannot make stdin non-blocking\n";
            m_enabled = false;
        }
    }

    ~StdinJsonControl() {
        if (m_enabled && m_original_flags >= 0) {
            (void)::fcntl(STDIN_FILENO, F_SETFL, m_original_flags);
        }
    }

    void poll(owe::SceneWallpaper& wallpaper) {
        if (! m_enabled || m_eof) return;

        char buffer[4096];
        for (;;) {
            const auto count = ::read(STDIN_FILENO, buffer, sizeof(buffer));
            if (count > 0) {
                m_pending.append(buffer, static_cast<std::size_t>(count));
                consumeLines(wallpaper, false);
                continue;
            }
            if (count == 0) {
                m_eof = true;
                consumeLines(wallpaper, true);
                return;
            }
            if (errno == EINTR) continue;
            if (errno != EAGAIN && errno != EWOULDBLOCK) {
                std::cerr << "--stdin-json: stdin read failed: " << std::strerror(errno) << '\n';
                m_eof = true;
            }
            return;
        }
    }

private:
    void consumeLines(owe::SceneWallpaper& wallpaper, bool flush) {
        for (;;) {
            const auto newline = m_pending.find('\n');
            if (newline == std::string::npos) break;
            auto line = m_pending.substr(0, newline);
            m_pending.erase(0, newline + 1);
            consumeLine(wallpaper, std::move(line));
        }
        if (flush && ! m_pending.empty()) {
            consumeLine(wallpaper, std::move(m_pending));
            m_pending.clear();
        }
    }

    static void consumeLine(owe::SceneWallpaper& wallpaper, std::string line) {
        if (! line.empty() && line.back() == '\r') line.pop_back();
        if (line.empty()) return;

        auto parsed_result = owe::ParseJson(line);
        if (parsed_result.is_err()) {
            const auto error = parsed_result.unwrap_err();
            std::cerr << "--stdin-json: invalid JSON at line " << error.line().to_primitive()
                      << " column " << error.column().to_primitive() << '\n';
            return;
        }
        auto command = parsed_result.unwrap();
        if (! command.is_object()) {
            std::cerr << "--stdin-json: command must be a JSON object\n";
            return;
        }

        auto command_name = command.get("command"_str);
        if (command_name.is_none() || ! (**command_name).is_string()) {
            std::cerr << "--stdin-json: command requires a string name\n";
            return;
        }

        auto name = rstd::cppstd::as_string_view(*(**command_name).as_str());
        if (name == "set_user_property") {
            setUserProperty(wallpaper, command);
        } else if (name == "set_mpris") {
            setMpris(wallpaper, command);
        } else {
            std::cerr << "--stdin-json: unsupported command\n";
        }
    }

    static void setUserProperty(owe::SceneWallpaper& wallpaper, const owe::Json& command) {
        auto key   = command.get("key"_str);
        auto value = command.get("value"_str);
        if (key.is_none() || ! (**key).is_string() || value.is_none()) {
            std::cerr
                << "--stdin-json: set_user_property requires a string key and a value field\n";
            return;
        }

        auto property = rstd::cppstd::to_string(*(**key).as_str());
        if (property.empty()) {
            std::cerr << "--stdin-json: set_user_property requires a non-empty key\n";
            return;
        }
        wallpaper.setUserPropertyJson(property, (**value).clone());
        std::cout << "scene-viewer: queued user property '" << property << "'\n" << std::flush;
    }

    static bool readString(const owe::Json& command, rstd::ref<rstd::str> key, std::string& value) {
        auto field = command.get(key);
        if (field.is_none()) return true;
        auto text = (**field).as_str();
        if (text.is_none()) return false;
        value = rstd::cppstd::to_string(*text);
        return true;
    }

    static void setMpris(owe::SceneWallpaper& wallpaper, const owe::Json& command) {
        auto state = command.get("state"_str);
        if (state.is_none()) {
            std::cerr << "--stdin-json: set_mpris requires state 0, 1, or 2\n";
            return;
        }
        auto state_value = (**state).as_u64();
        if (state_value.is_none() || *state_value > rstd::u64(2)) {
            std::cerr << "--stdin-json: set_mpris requires state 0, 1, or 2\n";
            return;
        }

        owe::MediaStatus status;
        status.state = static_cast<uint32_t>(state_value->to_primitive());
        if (! readString(command, "title"_str, status.title) ||
            ! readString(command, "artist"_str, status.artist) ||
            ! readString(command, "album"_str, status.album) ||
            ! readString(command, "album_artist"_str, status.album_artist) ||
            ! readString(command, "art_url"_str, status.art_url) ||
            ! readString(command, "previous_art_url"_str, status.previous_art_url)) {
            std::cerr << "--stdin-json: set_mpris metadata fields must be strings\n";
            return;
        }

        auto title   = status.title;
        auto art_url = status.art_url;
        wallpaper.setMediaStatus(std::move(status));
        std::cout << "scene-viewer: queued MPRIS title '" << title << "' art '" << art_url << "'\n"
                  << std::flush;
    }

    bool        m_enabled { false };
    bool        m_eof { false };
    int         m_original_flags { -1 };
    std::string m_pending;
};

struct UserData {
    owe::SceneWallpaper* psw { nullptr };
    bool                 mouse_position_locked { false };

    uint16_t width;
    uint16_t height;
};

extern "C" {
void framebuffer_size_callback(GLFWwindow*, int width, int height) {}

void mouse_button_callback(GLFWwindow* win, int button, int action, int /*mods*/) {
    UserData* data = static_cast<UserData*>(glfwGetWindowUserPointer(win));
    if (! data || ! data->psw) return;
    // GLFW button numbering (0=left, 1=right, 2=middle) matches WE.
    if (action == GLFW_PRESS) data->psw->mouseButton(button, true);
    if (action == GLFW_RELEASE) data->psw->mouseButton(button, false);
}

void cursor_position_callback(GLFWwindow* win, double xpos, double ypos) {
    UserData* data = static_cast<UserData*>(glfwGetWindowUserPointer(win));
    if (! data || ! data->psw || data->mouse_position_locked) return;
    data->psw->mouseInput(xpos / data->width, ypos / data->height);
}

void cursor_enter_callback(GLFWwindow* win, int entered) {
    UserData* data = static_cast<UserData*>(glfwGetWindowUserPointer(win));
    if (! data || ! data->psw || data->mouse_position_locked) return;
    data->psw->mouseEnter(entered != 0);
}
}

Option<std::array<double, 2>> parseMousePosition(const std::string& value) {
    if (value.empty()) return None();
    const auto comma = value.find(',');
    if (comma == std::string::npos) return None();
    double x  = 0.0;
    double y  = 0.0;
    auto   xs = value.substr(0, comma);
    auto   ys = value.substr(comma + 1);
    auto   xr = std::from_chars(xs.data(), xs.data() + xs.size(), x);
    auto   yr = std::from_chars(ys.data(), ys.data() + ys.size(), y);
    if (xr.ec != std::errc {} || yr.ec != std::errc {}) return None();
    return Some(std::array { std::clamp(x, 0.0, 1.0), std::clamp(y, 0.0, 1.0) });
}

int main(int argc, char** argv) {
    static rstd::log::EnvLogger _logger;
    rstd::log::set_logger(_logger);
    rstd::log::set_max_level(_logger.filter());

    auto args                = viewer::ParseSceneViewerArgs(argc, argv);
    auto [w_width, w_height] = args.resolution;
    viewer::InitGlfwPlatformHint(/*force_x11=*/false);
    glfwInit();
    glfwWindowHint(GLFW_CLIENT_API, GLFW_NO_API);
    // Bulk-scan path: WP_HEADLESS=1 hides the window so a scan loop over
    // hundreds of pkgs doesn't spam the desktop. Compile/render still
    // runs against the offscreen surface — stderr captures shader errors.
    if (const char* hl = std::getenv("WP_HEADLESS"); hl && hl[0] == '1') {
        glfwWindowHint(GLFW_VISIBLE, GLFW_FALSE);
    }
    GLFWwindow* window = glfwCreateWindow(w_width, w_height, "WP", nullptr, nullptr);

    UserData data;
    data.width  = w_width;
    data.height = w_height;

    owe::RenderInitInfo info;
    info.enable_valid_layer = args.enable_valid_layer;
    info.width              = w_width;
    info.height             = w_height;
    info.msaa_samples       = args.msaa_samples.to_primitive();

    auto& sf_info = info.surface_info;
    {
        uint32_t glfwExtCount = 0;
        auto     exts         = glfwGetRequiredInstanceExtensions(&glfwExtCount);
        for (uint32_t i = 0; i < glfwExtCount; i++) {
            sf_info.instanceExts.emplace_back(exts[i]);
        }

        sf_info.createSurfaceOp = [window](VkInstance inst, VkSurfaceKHR* surface) {
            return glfwCreateWindowSurface(inst, window, nullptr, surface);
        };
    }

    if (window == nullptr) {
        std::cout << "Failed to create GLFW window" << std::endl;
        glfwTerminate();
        return -1;
    }

    auto* psw = new owe::SceneWallpaper();
    data.psw  = psw;

    std::atomic<bool> audio_response_demand { false };
    psw->setAudioResponseDemandCallback([&audio_response_demand](bool active) {
        audio_response_demand.store(active, std::memory_order_release);
    });

    psw->init();

    owe::SceneWallpaperConfig config;
    config.assets_dir      = std::move(args.assets_dir);
    config.source_pkg_path = std::move(args.scene_path);
    config.graphviz        = args.graphviz;
    config.load_bench      = owe::CreateSceneLoadBench(args.load_bench_output.as_str());
    config.fps             = static_cast<uint32_t>(args.fps.to_primitive());
    if (args.random_seed.is_some()) {
        config.random_seed = Some(*args.random_seed);
    }

    std::string cache_path = std::move(args.cache_path);
    if (cache_path.empty()) cache_path = viewer::DefaultCacheDir("wescene-renderer").string();
    config.cache_dir = std::move(cache_path);

    // Apply --user-properties FILE before the scene loads so the first
    // frame already reflects the user's edits. Mirrors the daemon path
    // (Init.user_properties): JSON object whose values can be strings,
    // numbers, or booleans.
    if (const auto& up_path = args.user_properties_path; ! up_path.empty()) {
        std::ifstream is(up_path);
        if (! is) {
            std::cerr << "--user-properties: cannot open '" << up_path << "'\n";
            return 1;
        }
        std::stringstream ss;
        ss << is.rdbuf();
        auto parsed_result = owe::ParseJson(ss.str(), { .allow_comments = true });
        if (parsed_result.is_err()) {
            auto error = parsed_result.unwrap_err();
            std::cerr << "--user-properties: '" << up_path << "' is invalid JSON at line "
                      << error.line().to_primitive() << " column " << error.column().to_primitive()
                      << '\n';
            return 1;
        }
        auto parsed = parsed_result.unwrap();
        if (! parsed.is_object()) {
            std::cerr << "--user-properties: '" << up_path << "' is not a JSON object\n";
            return 1;
        }
        auto object = parsed.as_object();
        (*object)->iter().for_each([&](auto entry) {
            auto [entry_key, entry_value] = entry;
            config.user_properties.insert(entry_key->clone(), entry_value->clone());
        });
    }

    psw->configure(std::move(config));
    psw->initVulkan(std::move(info));

    glfwSetWindowUserPointer(window, &data);

    glfwSetFramebufferSizeCallback(window, framebuffer_size_callback);
    glfwSetMouseButtonCallback(window, mouse_button_callback);
    glfwSetCursorPosCallback(window, cursor_position_callback);
    glfwSetCursorEnterCallback(window, cursor_enter_callback);

    auto locked_mouse          = parseMousePosition(args.mouse_position);
    data.mouse_position_locked = locked_mouse.is_some();
    auto apply_locked_mouse    = [&]() {
        if (! locked_mouse) return;
        psw->mouseEnter(true);
        psw->mouseInput((*locked_mouse)[0], (*locked_mouse)[1]);
        glfwSetCursorPos(window, (*locked_mouse)[0] * w_width, (*locked_mouse)[1] * w_height);
    };
    apply_locked_mouse();

    StdinJsonControl            stdin_control(args.stdin_json);
    wavsen::audio::AudioCapture audio_capture;
    auto                        next_audio_update = std::chrono::steady_clock::now();
    bool                        audio_ended       = true;
    auto                        update_audio      = [&] {
        if (! audio_response_demand.load(std::memory_order_acquire)) {
            if (audio_capture.is_inited()) audio_capture.uninit();
            if (! audio_ended) {
                psw->endAudioResponse();
                audio_ended = true;
            }
            return;
        }
        if (! audio_capture.is_inited() && ! audio_capture.init()) return;
        audio_ended    = false;
        const auto now = std::chrono::steady_clock::now();
        if (now < next_audio_update) return;
        next_audio_update = now + std::chrono::milliseconds(33);
        wavsen::audio::AudioPcmWindow window {};
        if (audio_capture.snapshot(window)) {
            psw->setAudioPcmWindow(viewer::ConvertAudioWindow(window));
        }
    };

    // Bulk-scan path: WP_COMPILE_ONLY=N waits N seconds after scene load
    // to let the async shader-compile pass drain, then exits. Skips the
    // render loop so no swapchain present is required (which would deadlock
    // with a hidden window). Use together with WP_HEADLESS=1.
    if (const char* co = std::getenv("WP_COMPILE_ONLY"); co && co[0] != '\0') {
        int seconds = std::atoi(co);
        if (seconds <= 0) seconds = 2;
        std::this_thread::sleep_for(std::chrono::seconds(seconds));
    } else {
        while (! glfwWindowShouldClose(window)) {
            glfwWaitEventsTimeout(1.0 / 30.0);
            stdin_control.poll(*psw);
            apply_locked_mouse();
            update_audio();
        }
    }
    delete psw;
    // wgl.Clear();
    glfwDestroyWindow(window);
    glfwTerminate();
    return 0;
}
