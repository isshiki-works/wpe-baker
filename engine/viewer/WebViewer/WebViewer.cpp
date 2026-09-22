// weweb standalone GLFW + Vulkan + CEF (OSR) viewer.

#include <GLFW/glfw3.h>

import rstd.argparse;
import rstd.cppstd;
import owe.audio_response;
import wescene.cli;
import weweb;
import wavsen.audio;
import viewer.common;
import viewer.audio;
import viewer.web;

namespace
{

using namespace rstd::prelude;
using namespace rstd::argparse;
using namespace rstd::literals;

struct WebViewerArgs {
    std::string workshop;
    std::string presenter;
    i32         width;
    i32         height;
    i32         remote_debugging_port;
};

std::string ToStdString(const String& value) { return rstd::cppstd::to_string(value.as_str()); }

template<typename T>
const T& Value(const Matches& matches, const ArgKey<T>& key) {
    auto value = matches.get_one(key);
    if (value.is_err() || value->is_none()) rstd::unreachable();
    return ***value;
}

auto ParseWebViewerArgs(int argc, char** argv) -> Result<WebViewerArgs, owe::cli::ParseExit> {
    auto command  = Command::make("webviewer"_str);
    auto workshop = command.add_arg(
        Arg<String>::value("workshop"_str, string_parser())
            .value_name("WORKSHOP"_str)
            .help("path to a workshop/<id>/ directory containing project.json + index.html"_str)
            .required());
    auto width  = command.add_arg(Arg<i32>::value("width"_str, from_str_parser<i32>())
                                      .long_name("width"_str)
                                      .help("initial window width in pixels"_str)
                                      .default_value("1280"_str));
    auto height = command.add_arg(Arg<i32>::value("height"_str, from_str_parser<i32>())
                                      .long_name("height"_str)
                                      .help("initial window height in pixels"_str)
                                      .default_value("720"_str));
    auto remote_debugging_port =
        command.add_arg(Arg<i32>::value("remote-debugging-port"_str, from_str_parser<i32>())
                            .long_name("remote-debugging-port"_str)
                            .help("if non-zero, expose chrome devtools on this localhost port"_str)
                            .default_value("0"_str));
    auto presenter = command.add_arg(Arg<String>::value("presenter"_str, string_parser())
                                         .long_name("presenter"_str)
                                         .help("present backend: egl (default) or vulkan"_str)
                                         .default_value("egl"_str));

    auto parsed = owe::cli::ParseArgs(rstd::move(command), argc, argv);
    if (parsed.is_err()) return Err(parsed.unwrap_err());
    auto matches = rstd::move(parsed).unwrap();
    return Ok(WebViewerArgs {
        .workshop              = ToStdString(Value(matches, workshop)),
        .presenter             = ToStdString(Value(matches, presenter)),
        .width                 = Value(matches, width),
        .height                = Value(matches, height),
        .remote_debugging_port = Value(matches, remote_debugging_port),
    });
}

struct ViewerCtx {
    weweb::BrowserHost* host { nullptr };
    weweb::Presenter*   presenter { nullptr };
    bool                need_swapchain_recreate { false };
};

// CEF mouse button codes match cef_mouse_button_type_t: 0=L, 1=M, 2=R.
int CefButtonFromGlfw(int glfw_button) {
    switch (glfw_button) {
    case GLFW_MOUSE_BUTTON_LEFT: return 0;
    case GLFW_MOUSE_BUTTON_MIDDLE: return 1;
    case GLFW_MOUSE_BUTTON_RIGHT: return 2;
    default: return -1;
    }
}

void OnFramebufferSize(GLFWwindow* w, int /*fb_w*/, int /*fb_h*/) {
    auto* ctx = static_cast<ViewerCtx*>(glfwGetWindowUserPointer(w));
    if (ctx) ctx->need_swapchain_recreate = true;
}

void OnCursorPos(GLFWwindow* w, double x, double y) {
    auto* ctx = static_cast<ViewerCtx*>(glfwGetWindowUserPointer(w));
    if (! ctx || ! ctx->host) return;
    bool left = glfwGetMouseButton(w, GLFW_MOUSE_BUTTON_LEFT) == GLFW_PRESS;
    ctx->host->OnMouseMove(static_cast<int>(x), static_cast<int>(y), left);
}

void OnMouseButton(GLFWwindow* w, int button, int action, int /*mods*/) {
    auto* ctx = static_cast<ViewerCtx*>(glfwGetWindowUserPointer(w));
    if (! ctx || ! ctx->host) return;
    int cef_btn = CefButtonFromGlfw(button);
    if (cef_btn < 0) return;
    double x = 0, y = 0;
    glfwGetCursorPos(w, &x, &y);
    ctx->host->OnMouseButton(
        static_cast<int>(x), static_cast<int>(y), cef_btn, action == GLFW_PRESS, /*click_count=*/1);
}

void OnScroll(GLFWwindow* w, double dx, double dy) {
    auto* ctx = static_cast<ViewerCtx*>(glfwGetWindowUserPointer(w));
    if (! ctx || ! ctx->host) return;
    double x = 0, y = 0;
    glfwGetCursorPos(w, &x, &y);
    // GLFW scroll units are "wheel notches"; CEF expects pixel-ish deltas.
    ctx->host->OnMouseWheel(static_cast<int>(x),
                            static_cast<int>(y),
                            static_cast<int>(dx * 40),
                            static_cast<int>(dy * 40));
}

void OnFocus(GLFWwindow* w, int focused) {
    auto* ctx = static_cast<ViewerCtx*>(glfwGetWindowUserPointer(w));
    if (ctx && ctx->host) ctx->host->OnFocus(focused == GLFW_TRUE);
}

} // namespace

int main(int argc, char** argv) {
    weweb::BrowserHost host;

    // CRITICAL: must run before any of our own arg parsing — CEF re-execs
    // this binary with `--type=zygote/--type=renderer/...` switches; we
    // must short-circuit those helper invocations immediately.
    if (int helper_exit = host.RunOrExitIfHelper(argc, argv); helper_exit >= 0) {
        return helper_exit;
    }

    auto parsed_args = ParseWebViewerArgs(argc, argv);
    if (parsed_args.is_err()) return parsed_args.unwrap_err().code;
    auto args = rstd::move(parsed_args).unwrap();

    auto workshop_dir = std::filesystem::path(args.workshop);
    if (! std::filesystem::is_directory(workshop_dir)) {
        std::cerr << "webviewer: not a directory: " << workshop_dir.string() << "\n";
        return 2;
    }

    auto presenter_name = args.presenter;
    if (presenter_name != "vulkan" && presenter_name != "egl") {
        std::cerr << "webviewer: --presenter must be 'vulkan' or 'egl', got '" << presenter_name
                  << "'\n";
        return 2;
    }

    auto manifest_opt = weweb::LoadWebManifest(workshop_dir);
    if (! manifest_opt) return 2;
    auto& manifest = *manifest_opt;

    // Force GLFW to use the X11 backend. CEF in OSR mode still ends up
    // initialising Ozone (clipboard, font fallback, …) and we run with
    // --ozone-platform=x11; matching the toolkit avoids cross-display
    // synchronization weirdness.
    viewer::InitGlfwPlatformHint(/*force_x11=*/false);
    if (! glfwInit()) {
        std::cerr << "webviewer: glfwInit failed\n";
        return 1;
    }
    if (presenter_name == "vulkan" && ! glfwVulkanSupported()) {
        std::cerr << "webviewer: glfw says Vulkan is not supported\n";
        glfwTerminate();
        return 1;
    }
    // EGL path manages its own GL context against the X11 window; we still
    // want GLFW to leave the window context-less either way.
    glfwWindowHint(GLFW_CLIENT_API, GLFW_NO_API);

    int         w_width  = args.width.to_primitive();
    int         w_height = args.height.to_primitive();
    GLFWwindow* window =
        glfwCreateWindow(w_width, w_height, manifest.title.c_str(), nullptr, nullptr);
    if (! window) {
        std::cerr << "webviewer: glfwCreateWindow failed\n";
        glfwTerminate();
        return 1;
    }

    std::unique_ptr<weweb::Presenter> presenter;
    if (presenter_name == "vulkan") {
        presenter = std::make_unique<weweb::VulkanBlitter>();
    } else {
        presenter = std::make_unique<weweb::EglPresenter>();
    }
    if (! presenter->Init(window)) {
        glfwDestroyWindow(window);
        glfwTerminate();
        return 1;
    }

    auto exe_dir = viewer::ExecutableDir(argv[0]);

    weweb::BrowserHost::InitOptions opts;
    opts.resources_dir = exe_dir;
    opts.locales_dir   = exe_dir / "locales";
    if (int port = args.remote_debugging_port.to_primitive(); port > 0) {
        opts.enable_remote_debugging = true;
        opts.remote_debugging_port   = port;
    }

    if (! host.Init(opts)) {
        presenter->Shutdown();
        glfwDestroyWindow(window);
        glfwTerminate();
        return 1;
    }

    // Hook DMA-BUF frames straight into the presenter's import path.
    // The callback runs synchronously inside CefDoMessageLoopWork on the
    // main thread; FDs in `frame` are valid only for the duration.
    weweb::Presenter* presenter_ptr = presenter.get();
    host.SetAcceleratedPaintCallback([presenter_ptr](const weweb::DmaBufFrame& frame) {
        presenter_ptr->AcceptDmaBuf(frame);
    });

    // Open the wallpaper at the swapchain extent — CEF renders straight
    // into our swapchain-pixel space, no rescaling needed.
    int initial_w = static_cast<int>(presenter->Width());
    int initial_h = static_cast<int>(presenter->Height());
    if (! host.OpenWallpaper(manifest, workshop_dir, initial_w, initial_h)) {
        host.Shutdown();
        presenter->Shutdown();
        glfwDestroyWindow(window);
        glfwTerminate();
        return 1;
    }

    ViewerCtx ctx;
    ctx.host      = &host;
    ctx.presenter = presenter.get();
    glfwSetWindowUserPointer(window, &ctx);
    glfwSetFramebufferSizeCallback(window, OnFramebufferSize);
    glfwSetCursorPosCallback(window, OnCursorPos);
    glfwSetMouseButtonCallback(window, OnMouseButton);
    glfwSetScrollCallback(window, OnScroll);
    glfwSetWindowFocusCallback(window, OnFocus);

    rstd::sync::atomic::Atomic<bool> audio_response_demand { false };
    host.SetAudioResponseDemandCallback([&audio_response_demand](bool active) {
        audio_response_demand.store(active, rstd::sync::atomic::Ordering::Release);
    });

    wavsen::audio::AudioCapture audio_capture;
    owe::audio::ResponseEngine  audio_response;
    unsigned                    audio_tick = 0;

    // Main loop. ~60Hz nominal. CefDoMessageLoopWork is cheap; the
    // Vulkan FIFO present pacing also throttles us.
    while (! glfwWindowShouldClose(window) && ! host.ShouldExit()) {
        glfwPollEvents();
        host.Pump();

        if (! audio_response_demand.load(rstd::sync::atomic::Ordering::Acquire)) {
            if (audio_capture.is_inited()) audio_capture.uninit();
            audio_response.end();
        } else {
            if (! audio_capture.is_inited() && ! audio_capture.init()) {
                std::cerr << "webviewer: audio capture init failed\n";
            }
        }
        if (audio_capture.is_inited() && (audio_tick++ & 1u) == 0) {
            wavsen::audio::AudioPcmWindow captured {};
            owe::audio::ResponseFrame     response {};
            auto                          window = owe::audio::PcmWindow {};
            if (audio_capture.snapshot(captured)) window = viewer::ConvertAudioWindow(captured);
            if (window.frames != 0 && audio_response.analyze(window, response)) {
                std::array<float, 128> arr {};
                for (std::size_t i = 0; i < 64; ++i) {
                    arr[i]      = response.left[rstd::usize(i)];
                    arr[64 + i] = response.right[rstd::usize(i)];
                }
                host.PushAudioData(arr.data(), arr.size());
            }
        }

        // CEF's internal pacing in OSR shared-texture mode goes quiet
        // after the first paint until something on the page is dirty.
        // Pages with rAF-driven animation expect a presenter to ask
        // for frames continuously. CEF dedupes internally to its
        // windowless_frame_rate (60), so an unconditional Invalidate
        // per loop iteration is the right pattern.
        host.Invalidate();

        if (ctx.need_swapchain_recreate) {
            int fbw = 0, fbh = 0;
            glfwGetFramebufferSize(window, &fbw, &fbh);
            if (fbw > 0 && fbh > 0) {
                if (! presenter->Resize()) {
                    std::cerr << "webviewer: presenter Resize failed\n";
                    break;
                }
                host.OnResize(static_cast<int>(presenter->Width()),
                              static_cast<int>(presenter->Height()));
                ctx.need_swapchain_recreate = false;
            } else {
                std::this_thread::sleep_for(std::chrono::milliseconds(16));
                continue;
            }
        }

        // Owned image was already updated synchronously inside Pump()
        // when CEF delivered an OnAcceleratedPaint frame.
        if (! presenter->RenderFrame()) {
            ctx.need_swapchain_recreate = true;
        }
    }

    host.Shutdown();
    presenter->Shutdown();
    glfwDestroyWindow(window);
    glfwTerminate();
    return 0;
}
