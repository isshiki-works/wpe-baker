module;

#include <cstdio>

#include <EGL/egl.h>
#include <EGL/eglext.h>
#include <GLES3/gl3.h>
#include <GLES2/gl2ext.h>

#define GLFW_EXPOSE_NATIVE_X11
#define GLFW_EXPOSE_NATIVE_WAYLAND
#include <GLFW/glfw3.h>
#include <GLFW/glfw3native.h>

#include <wayland-egl.h>

module viewer.web;

import rstd.cppstd;
import weweb;

namespace weweb
{

namespace
{

// DRM fourcc codes — keep inline to avoid a libdrm header dependency.
// Mapping matches the user-supplied reference: CEF reverses RGBA/BGRA.
constexpr std::uint32_t kDrmFmtBgra8888 = 0x34324142; // 'BA24'
constexpr std::uint32_t kDrmFmtAbgr8888 = 0x34324241; // 'AB24'
// Used only by the init-time capability dump (NVIDIA may advertise a
// different alpha-channel ordering than what CEF actually emits).
constexpr std::uint32_t kDrmFmtArgb8888 = 0x34325241; // 'AR24'
constexpr std::uint32_t kDrmFmtXrgb8888 = 0x34325258; // 'XR24'

// 'invalid' modifier per drm_fourcc.h. Vulkan path treats it as LINEAR;
// for EGL the cleaner equivalent is to omit modifier attrs entirely so
// the driver picks its own layout from stride+fourcc.
constexpr std::uint64_t kDrmModInvalid = 0x00ffffffffffffffULL;

constexpr EGLint kPlaneFD[] = {
    EGL_DMA_BUF_PLANE0_FD_EXT,
    EGL_DMA_BUF_PLANE1_FD_EXT,
    EGL_DMA_BUF_PLANE2_FD_EXT,
    EGL_DMA_BUF_PLANE3_FD_EXT,
};
constexpr EGLint kPlaneOff[] = {
    EGL_DMA_BUF_PLANE0_OFFSET_EXT,
    EGL_DMA_BUF_PLANE1_OFFSET_EXT,
    EGL_DMA_BUF_PLANE2_OFFSET_EXT,
    EGL_DMA_BUF_PLANE3_OFFSET_EXT,
};
constexpr EGLint kPlanePitch[] = {
    EGL_DMA_BUF_PLANE0_PITCH_EXT,
    EGL_DMA_BUF_PLANE1_PITCH_EXT,
    EGL_DMA_BUF_PLANE2_PITCH_EXT,
    EGL_DMA_BUF_PLANE3_PITCH_EXT,
};
constexpr EGLint kPlaneModLo[] = {
    EGL_DMA_BUF_PLANE0_MODIFIER_LO_EXT,
    EGL_DMA_BUF_PLANE1_MODIFIER_LO_EXT,
    EGL_DMA_BUF_PLANE2_MODIFIER_LO_EXT,
    EGL_DMA_BUF_PLANE3_MODIFIER_LO_EXT,
};
constexpr EGLint kPlaneModHi[] = {
    EGL_DMA_BUF_PLANE0_MODIFIER_HI_EXT,
    EGL_DMA_BUF_PLANE1_MODIFIER_HI_EXT,
    EGL_DMA_BUF_PLANE2_MODIFIER_HI_EXT,
    EGL_DMA_BUF_PLANE3_MODIFIER_HI_EXT,
};

std::uint32_t DrmFourccFor(DmaBufFormat f) {
    switch (f) {
    case DmaBufFormat::BGRA8_UNORM: return kDrmFmtBgra8888;
    case DmaBufFormat::RGBA8_UNORM: return kDrmFmtAbgr8888;
    }
    return 0;
}

const char* FormatStr(DmaBufFormat f) {
    switch (f) {
    case DmaBufFormat::BGRA8_UNORM: return "BGRA8";
    case DmaBufFormat::RGBA8_UNORM: return "RGBA8";
    }
    return "?";
}

const char* EglErrStr(EGLint e) {
    switch (e) {
    case EGL_SUCCESS: return "SUCCESS";
    case EGL_NOT_INITIALIZED: return "NOT_INITIALIZED";
    case EGL_BAD_ACCESS: return "BAD_ACCESS";
    case EGL_BAD_ALLOC: return "BAD_ALLOC";
    case EGL_BAD_ATTRIBUTE: return "BAD_ATTRIBUTE";
    case EGL_BAD_CONFIG: return "BAD_CONFIG";
    case EGL_BAD_CONTEXT: return "BAD_CONTEXT";
    case EGL_BAD_CURRENT_SURFACE: return "BAD_CURRENT_SURFACE";
    case EGL_BAD_DISPLAY: return "BAD_DISPLAY";
    case EGL_BAD_MATCH: return "BAD_MATCH";
    case EGL_BAD_NATIVE_PIXMAP: return "BAD_NATIVE_PIXMAP";
    case EGL_BAD_NATIVE_WINDOW: return "BAD_NATIVE_WINDOW";
    case EGL_BAD_PARAMETER: return "BAD_PARAMETER";
    case EGL_BAD_SURFACE: return "BAD_SURFACE";
    case EGL_CONTEXT_LOST: return "CONTEXT_LOST";
    default: return "?";
    }
}

const char* GlErrStr(GLenum e) {
    switch (e) {
    case GL_NO_ERROR: return "NO_ERROR";
    case GL_INVALID_ENUM: return "INVALID_ENUM";
    case GL_INVALID_VALUE: return "INVALID_VALUE";
    case GL_INVALID_OPERATION: return "INVALID_OPERATION";
    case GL_INVALID_FRAMEBUFFER_OPERATION: return "INVALID_FRAMEBUFFER_OPERATION";
    case GL_OUT_OF_MEMORY: return "OUT_OF_MEMORY";
    default: return "?";
    }
}

const char* FboStatusStr(GLenum s) {
    switch (s) {
    case GL_FRAMEBUFFER_COMPLETE: return "COMPLETE";
    case GL_FRAMEBUFFER_INCOMPLETE_ATTACHMENT: return "INCOMPLETE_ATTACHMENT";
    case GL_FRAMEBUFFER_INCOMPLETE_MISSING_ATTACHMENT: return "INCOMPLETE_MISSING_ATTACHMENT";
    case GL_FRAMEBUFFER_INCOMPLETE_DIMENSIONS: return "INCOMPLETE_DIMENSIONS";
    case GL_FRAMEBUFFER_UNSUPPORTED: return "UNSUPPORTED";
    case GL_FRAMEBUFFER_INCOMPLETE_MULTISAMPLE: return "INCOMPLETE_MULTISAMPLE";
    default: return "?";
    }
}

bool DebugVerbose() {
    static bool v = std::getenv("WW_EGL_DEBUG") != nullptr;
    return v;
}

} // namespace

EglPresenter::EglPresenter() = default;
EglPresenter::~EglPresenter() { Shutdown(); }

bool EglPresenter::Init(GLFWwindow* window) {
    window_ = window;

    const int platform       = glfwGetPlatform();
    void*     native_display = nullptr;
    EGLenum   egl_platform   = 0;

    if (platform == GLFW_PLATFORM_WAYLAND) {
        native_display = glfwGetWaylandDisplay();
        egl_platform   = EGL_PLATFORM_WAYLAND_KHR;
    } else if (platform == GLFW_PLATFORM_X11) {
        native_display = glfwGetX11Display();
        egl_platform   = EGL_PLATFORM_X11_KHR;
    } else {
        std::fprintf(
            stderr, "weweb-egl: unsupported GLFW platform %d (need X11 or Wayland)\n", platform);
        return false;
    }
    if (! native_display) {
        std::fprintf(stderr, "weweb-egl: GLFW returned a null native display\n");
        return false;
    }

    egl_display_ = eglGetPlatformDisplay(egl_platform, native_display, nullptr);
    if (egl_display_ == EGL_NO_DISPLAY) {
        std::fprintf(stderr,
                     "weweb-egl: eglGetPlatformDisplay(%s) failed (0x%x)\n",
                     egl_platform == EGL_PLATFORM_WAYLAND_KHR ? "Wayland" : "X11",
                     eglGetError());
        return false;
    }

    EGLint egl_major = 0, egl_minor = 0;
    if (! eglInitialize(egl_display_, &egl_major, &egl_minor)) {
        std::fprintf(stderr, "weweb-egl: eglInitialize failed (0x%x)\n", eglGetError());
        return false;
    }

    if (! eglBindAPI(EGL_OPENGL_ES_API)) {
        std::fprintf(stderr, "weweb-egl: eglBindAPI(GLES) failed (0x%x)\n", eglGetError());
        return false;
    }

    const EGLint cfg_attribs[] = {
        EGL_RENDERABLE_TYPE,
        EGL_OPENGL_ES3_BIT,
        EGL_SURFACE_TYPE,
        EGL_WINDOW_BIT,
        EGL_RED_SIZE,
        8,
        EGL_GREEN_SIZE,
        8,
        EGL_BLUE_SIZE,
        8,
        EGL_ALPHA_SIZE,
        8,
        EGL_NONE,
    };
    EGLint num_cfg = 0;
    if (! eglChooseConfig(egl_display_, cfg_attribs, &egl_config_, 1, &num_cfg) || num_cfg < 1) {
        std::fprintf(stderr, "weweb-egl: eglChooseConfig found no RGBA8 window config\n");
        return false;
    }

    const EGLint ctx_attribs[] = {
        EGL_CONTEXT_CLIENT_VERSION,
        3,
        EGL_NONE,
    };
    egl_context_ = eglCreateContext(egl_display_, egl_config_, EGL_NO_CONTEXT, ctx_attribs);
    if (egl_context_ == EGL_NO_CONTEXT) {
        std::fprintf(stderr, "weweb-egl: eglCreateContext(GLES3) failed (0x%x)\n", eglGetError());
        return false;
    }

    int fbw_init = 0, fbh_init = 0;
    glfwGetFramebufferSize(window_, &fbw_init, &fbh_init);
    if (fbw_init <= 0) fbw_init = 1;
    if (fbh_init <= 0) fbh_init = 1;

    EGLNativeWindowType native_window = 0;
    if (platform == GLFW_PLATFORM_WAYLAND) {
        wl_surface* wls = glfwGetWaylandWindow(window_);
        if (! wls) {
            std::fprintf(stderr, "weweb-egl: glfwGetWaylandWindow returned null\n");
            return false;
        }
        wl_egl_window_ = wl_egl_window_create(wls, fbw_init, fbh_init);
        if (! wl_egl_window_) {
            std::fprintf(stderr, "weweb-egl: wl_egl_window_create failed\n");
            return false;
        }
        native_window = reinterpret_cast<EGLNativeWindowType>(wl_egl_window_);
    } else {
        Window x_window = glfwGetX11Window(window_);
        if (! x_window) {
            std::fprintf(stderr, "weweb-egl: glfwGetX11Window returned null\n");
            return false;
        }
        native_window = static_cast<EGLNativeWindowType>(x_window);
    }

    egl_surface_ = eglCreateWindowSurface(egl_display_, egl_config_, native_window, nullptr);
    if (egl_surface_ == EGL_NO_SURFACE) {
        std::fprintf(stderr, "weweb-egl: eglCreateWindowSurface failed (0x%x)\n", eglGetError());
        return false;
    }

    if (! eglMakeCurrent(egl_display_, egl_surface_, egl_surface_, egl_context_)) {
        std::fprintf(stderr, "weweb-egl: eglMakeCurrent failed (0x%x)\n", eglGetError());
        return false;
    }

    if (! LoadFunctionPointers()) return false;

    {
        const char* plat_str    = (platform == GLFW_PLATFORM_WAYLAND) ? "wayland"
                                  : (platform == GLFW_PLATFORM_X11)   ? "x11"
                                                                      : "?";
        const char* egl_vendor  = eglQueryString(egl_display_, EGL_VENDOR);
        const char* egl_version = eglQueryString(egl_display_, EGL_VERSION);
        const char* egl_apis    = eglQueryString(egl_display_, EGL_CLIENT_APIS);
        const char* egl_exts    = eglQueryString(egl_display_, EGL_EXTENSIONS);
        std::fprintf(stderr,
                     "weweb-egl: platform=%s egl=%d.%d vendor=%s\n",
                     plat_str,
                     egl_major,
                     egl_minor,
                     egl_vendor ? egl_vendor : "?");
        std::fprintf(stderr, "weweb-egl: EGL_VERSION = %s\n", egl_version ? egl_version : "?");
        std::fprintf(stderr, "weweb-egl: EGL_CLIENT_APIS = %s\n", egl_apis ? egl_apis : "?");
        const bool has_dmabuf =
            egl_exts && std::strstr(egl_exts, "EGL_EXT_image_dma_buf_import") != nullptr;
        const bool has_dmabuf_mods =
            egl_exts && std::strstr(egl_exts, "EGL_EXT_image_dma_buf_import_modifiers") != nullptr;
        std::fprintf(stderr,
                     "weweb-egl: EGL_EXT_image_dma_buf_import = %s\n",
                     has_dmabuf ? "yes" : "MISSING");
        std::fprintf(stderr,
                     "weweb-egl: EGL_EXT_image_dma_buf_import_modifiers = %s\n",
                     has_dmabuf_mods ? "yes" : "no");
        if (DebugVerbose() && egl_exts) {
            std::fprintf(stderr, "weweb-egl: EGL_EXTENSIONS = %s\n", egl_exts);
        }

        const auto* gl_vendor   = glGetString(GL_VENDOR);
        const auto* gl_renderer = glGetString(GL_RENDERER);
        const auto* gl_version  = glGetString(GL_VERSION);
        std::fprintf(stderr,
                     "weweb-egl: GL_VENDOR   = %s\n",
                     gl_vendor ? reinterpret_cast<const char*>(gl_vendor) : "?");
        std::fprintf(stderr,
                     "weweb-egl: GL_RENDERER = %s\n",
                     gl_renderer ? reinterpret_cast<const char*>(gl_renderer) : "?");
        std::fprintf(stderr,
                     "weweb-egl: GL_VERSION  = %s\n",
                     gl_version ? reinterpret_cast<const char*>(gl_version) : "?");

        // Dump the modifier list NVIDIA's EGL will accept for the formats
        // CEF emits. Helps spot mismatches before the first import — e.g.
        // if NV only advertises NVIDIA-specific tilings and CEF will hand
        // us LINEAR, the import is doomed regardless of attribute layout.
        if (has_dmabuf_mods && fn_eglQueryDmaBufModifiersEXT_) {
            const std::uint32_t formats[] = {
                kDrmFmtArgb8888, kDrmFmtAbgr8888, kDrmFmtBgra8888, kDrmFmtXrgb8888
            };
            const char* fmt_names[] = { "ARGB8888", "ABGR8888", "BGRA8888", "XRGB8888" };
            for (std::size_t fi = 0; fi < std::size(formats); ++fi) {
                EGLint n = 0;
                if (! fn_eglQueryDmaBufModifiersEXT_(
                        egl_display_, static_cast<EGLint>(formats[fi]), 0, nullptr, nullptr, &n) ||
                    n <= 0) {
                    std::fprintf(
                        stderr, "weweb-egl: %s: 0 modifiers (unsupported)\n", fmt_names[fi]);
                    continue;
                }
                std::vector<EGLuint64KHR> mods(static_cast<std::size_t>(n));
                if (! fn_eglQueryDmaBufModifiersEXT_(egl_display_,
                                                     static_cast<EGLint>(formats[fi]),
                                                     n,
                                                     mods.data(),
                                                     nullptr,
                                                     &n)) {
                    continue;
                }
                std::fprintf(stderr, "weweb-egl: %s: %d modifiers", fmt_names[fi], n);
                for (EGLint mi = 0; mi < n; ++mi) {
                    std::fprintf(stderr, " 0x%016llx", static_cast<unsigned long long>(mods[mi]));
                }
                std::fputc('\n', stderr);
            }
        } else {
            std::fprintf(stderr,
                         "weweb-egl: cannot query supported modifiers "
                         "(EGL_EXT_image_dma_buf_import_modifiers missing)\n");
        }
    }

    int fbw = 0, fbh = 0;
    glfwGetFramebufferSize(window_, &fbw, &fbh);
    width_  = static_cast<std::uint32_t>(fbw > 0 ? fbw : 0);
    height_ = static_cast<std::uint32_t>(fbh > 0 ? fbh : 0);

    glGenFramebuffers(1, &blit_read_fbo_);
    glGenFramebuffers(1, &blit_draw_fbo_);
    return true;
}

bool EglPresenter::LoadFunctionPointers() {
    fn_eglCreateImageKHR_ =
        reinterpret_cast<PFNEGLCREATEIMAGEKHRPROC>(eglGetProcAddress("eglCreateImageKHR"));
    fn_eglDestroyImageKHR_ =
        reinterpret_cast<PFNEGLDESTROYIMAGEKHRPROC>(eglGetProcAddress("eglDestroyImageKHR"));
    fn_glEGLImageTargetTexture2DOES_ = reinterpret_cast<PFNGLEGLIMAGETARGETTEXTURE2DOESPROC>(
        eglGetProcAddress("glEGLImageTargetTexture2DOES"));
    if (! fn_eglCreateImageKHR_ || ! fn_eglDestroyImageKHR_ || ! fn_glEGLImageTargetTexture2DOES_) {
        std::fprintf(stderr,
                     "weweb-egl: missing required extension entry points "
                     "(EGL_KHR_image_base + GL_OES_EGL_image)\n");
        return false;
    }
    // Optional: only used by the capability dump in Init.
    fn_eglQueryDmaBufModifiersEXT_ = reinterpret_cast<PFNEGLQUERYDMABUFMODIFIERSEXTPROC>(
        eglGetProcAddress("eglQueryDmaBufModifiersEXT"));
    return true;
}

void EglPresenter::Shutdown() {
    if (egl_display_ != EGL_NO_DISPLAY) {
        eglMakeCurrent(egl_display_, EGL_NO_SURFACE, EGL_NO_SURFACE, EGL_NO_CONTEXT);
    }
    DestroyOwnedTexture();
    if (blit_read_fbo_) {
        glDeleteFramebuffers(1, &blit_read_fbo_);
        blit_read_fbo_ = 0;
    }
    if (blit_draw_fbo_) {
        glDeleteFramebuffers(1, &blit_draw_fbo_);
        blit_draw_fbo_ = 0;
    }
    if (egl_surface_ != EGL_NO_SURFACE) {
        eglDestroySurface(egl_display_, egl_surface_);
        egl_surface_ = EGL_NO_SURFACE;
    }
    if (wl_egl_window_) {
        wl_egl_window_destroy(wl_egl_window_);
        wl_egl_window_ = nullptr;
    }
    if (egl_context_ != EGL_NO_CONTEXT) {
        eglDestroyContext(egl_display_, egl_context_);
        egl_context_ = EGL_NO_CONTEXT;
    }
    if (egl_display_ != EGL_NO_DISPLAY) {
        eglTerminate(egl_display_);
        egl_display_ = EGL_NO_DISPLAY;
    }
}

bool EglPresenter::Resize() {
    int fbw = 0, fbh = 0;
    glfwGetFramebufferSize(window_, &fbw, &fbh);
    width_  = static_cast<std::uint32_t>(fbw > 0 ? fbw : 0);
    height_ = static_cast<std::uint32_t>(fbh > 0 ? fbh : 0);
    // Wayland: EGL won't notice the GLFW window resize on its own — the
    // wl_egl_window has to be told. X11 picks it up via the X server.
    if (wl_egl_window_ && fbw > 0 && fbh > 0) {
        wl_egl_window_resize(wl_egl_window_, fbw, fbh, 0, 0);
    }
    return true;
}

bool EglPresenter::EnsureOwnedTexture(int w, int h) {
    if (owned_tex_ && owned_w_ == w && owned_h_ == h) return true;
    DestroyOwnedTexture();

    glGenTextures(1, &owned_tex_);
    glBindTexture(GL_TEXTURE_2D, owned_tex_);
    glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA8, w, h, 0, GL_RGBA, GL_UNSIGNED_BYTE, nullptr);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
    glBindTexture(GL_TEXTURE_2D, 0);

    owned_w_        = w;
    owned_h_        = h;
    owned_has_data_ = false;
    return true;
}

void EglPresenter::DestroyOwnedTexture() {
    if (owned_tex_) {
        glDeleteTextures(1, &owned_tex_);
        owned_tex_ = 0;
    }
    owned_w_ = owned_h_ = 0;
    owned_has_data_     = false;
}

bool EglPresenter::AcceptDmaBuf(const DmaBufFrame& frame) {
    if (frame.plane_count < 1) return false;
    if (frame.coded_width <= 0 || frame.coded_height <= 0) return false;

    const std::uint32_t fourcc = DrmFourccFor(frame.format);
    if (! fourcc) return false;

    const bool verbose    = DebugVerbose() || import_count_ == 0;
    auto       dump_frame = [&]() {
        std::fprintf(stderr,
                     "weweb-egl: import #%u %dx%d (visible %dx%d) fmt=%s fourcc=0x%08x "
                     "mod=0x%016llx planes=%d\n",
                     import_count_,
                     frame.coded_width,
                     frame.coded_height,
                     frame.visible_width,
                     frame.visible_height,
                     FormatStr(frame.format),
                     fourcc,
                     static_cast<unsigned long long>(frame.modifier),
                     frame.plane_count);
        for (int i = 0; i < frame.plane_count; ++i) {
            std::fprintf(stderr,
                         "weweb-egl:   plane[%d] fd=%d offset=%llu stride=%u size=%llu\n",
                         i,
                         frame.planes[i].fd,
                         static_cast<unsigned long long>(frame.planes[i].offset),
                         frame.planes[i].stride,
                         static_cast<unsigned long long>(frame.planes[i].size));
        }
    };
    if (verbose) dump_frame();

    // NVIDIA's EGL refuses imports that omit the modifier attribute or
    // pass DRM_FORMAT_MOD_INVALID; Mesa is happy to derive layout from
    // stride alone. CEF reports INVALID when no modifier was negotiated —
    // in our observed cases that's effectively LINEAR (stride == width*bpp).
    // Mirror VulkanBlitter::AcceptDmaBuf and substitute LINEAR explicitly
    // so both backends agree and NVIDIA accepts the import.
    constexpr std::uint64_t kDrmModLinear = 0x0;
    const std::uint64_t     modifier =
        (frame.modifier == kDrmModInvalid) ? kDrmModLinear : frame.modifier;

    std::vector<EGLint> attrs = {
        EGL_WIDTH,
        static_cast<EGLint>(frame.coded_width),
        EGL_HEIGHT,
        static_cast<EGLint>(frame.coded_height),
        EGL_LINUX_DRM_FOURCC_EXT,
        static_cast<EGLint>(fourcc),
    };
    for (int i = 0; i < frame.plane_count; ++i) {
        attrs.push_back(kPlaneFD[i]);
        attrs.push_back(static_cast<EGLint>(frame.planes[i].fd));
        attrs.push_back(kPlaneOff[i]);
        attrs.push_back(static_cast<EGLint>(frame.planes[i].offset));
        attrs.push_back(kPlanePitch[i]);
        attrs.push_back(static_cast<EGLint>(frame.planes[i].stride));
        attrs.push_back(kPlaneModLo[i]);
        attrs.push_back(static_cast<EGLint>(modifier & 0xffffffffu));
        attrs.push_back(kPlaneModHi[i]);
        attrs.push_back(static_cast<EGLint>((modifier >> 32) & 0xffffffffu));
    }
    attrs.push_back(EGL_NONE);

    EGLImageKHR image = fn_eglCreateImageKHR_(egl_display_,
                                              EGL_NO_CONTEXT,
                                              EGL_LINUX_DMA_BUF_EXT,
                                              static_cast<EGLClientBuffer>(nullptr),
                                              attrs.data());
    if (image == EGL_NO_IMAGE_KHR) {
        EGLint e = eglGetError();
        std::fprintf(
            stderr, "weweb-egl: eglCreateImageKHR(DMA-BUF) failed: %s (0x%x)\n", EglErrStr(e), e);
        if (! verbose) dump_frame();
        ++import_count_;
        return false;
    }

    GLuint temp_tex = 0;
    glGenTextures(1, &temp_tex);
    glBindTexture(GL_TEXTURE_2D, temp_tex);
    fn_glEGLImageTargetTexture2DOES_(GL_TEXTURE_2D, image);
    GLenum target_err = glGetError();
    if (verbose || target_err != GL_NO_ERROR) {
        std::fprintf(
            stderr, "weweb-egl: glEGLImageTargetTexture2DOES glerr=%s\n", GlErrStr(target_err));
    }
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_NEAREST);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_NEAREST);
    glBindTexture(GL_TEXTURE_2D, 0);

    if (! EnsureOwnedTexture(frame.coded_width, frame.coded_height)) {
        glDeleteTextures(1, &temp_tex);
        fn_eglDestroyImageKHR_(egl_display_, image);
        ++import_count_;
        return false;
    }

    glBindFramebuffer(GL_READ_FRAMEBUFFER, blit_read_fbo_);
    glFramebufferTexture2D(GL_READ_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, temp_tex, 0);
    glBindFramebuffer(GL_DRAW_FRAMEBUFFER, blit_draw_fbo_);
    glFramebufferTexture2D(GL_DRAW_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, owned_tex_, 0);

    GLenum read_status = glCheckFramebufferStatus(GL_READ_FRAMEBUFFER);
    GLenum draw_status = glCheckFramebufferStatus(GL_DRAW_FRAMEBUFFER);
    if (verbose || read_status != GL_FRAMEBUFFER_COMPLETE ||
        draw_status != GL_FRAMEBUFFER_COMPLETE) {
        std::fprintf(stderr,
                     "weweb-egl: import FBO read=%s draw=%s\n",
                     FboStatusStr(read_status),
                     FboStatusStr(draw_status));
    }

    glBlitFramebuffer(0,
                      0,
                      frame.coded_width,
                      frame.coded_height,
                      0,
                      0,
                      frame.coded_width,
                      frame.coded_height,
                      GL_COLOR_BUFFER_BIT,
                      GL_NEAREST);

    GLenum blit_err = glGetError();
    if (verbose || blit_err != GL_NO_ERROR) {
        std::fprintf(stderr, "weweb-egl: import-blit glerr=%s\n", GlErrStr(blit_err));
    }

    // Block until the GPU is done reading the imported buffer — CEF
    // reclaims the FD as soon as we return.
    glFinish();

    glBindFramebuffer(GL_READ_FRAMEBUFFER, 0);
    glBindFramebuffer(GL_DRAW_FRAMEBUFFER, 0);
    glDeleteTextures(1, &temp_tex);
    fn_eglDestroyImageKHR_(egl_display_, image);

    owned_has_data_ = true;
    ++import_count_;
    return true;
}

bool EglPresenter::RenderFrame() {
    int fbw = 0, fbh = 0;
    glfwGetFramebufferSize(window_, &fbw, &fbh);
    if (fbw <= 0 || fbh <= 0) return false;
    width_  = static_cast<std::uint32_t>(fbw);
    height_ = static_cast<std::uint32_t>(fbh);

    const bool verbose = DebugVerbose() || render_count_ == 0;
    if (verbose) {
        std::fprintf(stderr,
                     "weweb-egl: present #%u fb=%dx%d owned=%dx%d has_data=%d\n",
                     render_count_,
                     fbw,
                     fbh,
                     owned_w_,
                     owned_h_,
                     owned_has_data_ ? 1 : 0);
    }

    glViewport(0, 0, fbw, fbh);

    if (owned_has_data_ && owned_tex_) {
        glBindFramebuffer(GL_READ_FRAMEBUFFER, blit_read_fbo_);
        glFramebufferTexture2D(
            GL_READ_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, owned_tex_, 0);
        glBindFramebuffer(GL_DRAW_FRAMEBUFFER, 0);
        GLenum read_status = glCheckFramebufferStatus(GL_READ_FRAMEBUFFER);
        if (verbose || read_status != GL_FRAMEBUFFER_COMPLETE) {
            std::fprintf(stderr, "weweb-egl: present read FBO=%s\n", FboStatusStr(read_status));
        }
        // CEF data is top-down; default FB origin is bottom-left. Flip Y
        // by writing the dst Y range in reverse.
        glBlitFramebuffer(0, 0, owned_w_, owned_h_, 0, fbh, fbw, 0, GL_COLOR_BUFFER_BIT, GL_LINEAR);
        GLenum blit_err = glGetError();
        if (verbose || blit_err != GL_NO_ERROR) {
            std::fprintf(stderr, "weweb-egl: present-blit glerr=%s\n", GlErrStr(blit_err));
        }
        glBindFramebuffer(GL_READ_FRAMEBUFFER, 0);
    } else {
        glClearColor(0.f, 0.f, 0.f, 1.f);
        glClear(GL_COLOR_BUFFER_BIT);
    }

    if (! eglSwapBuffers(egl_display_, egl_surface_)) {
        EGLint e = eglGetError();
        std::fprintf(stderr, "weweb-egl: eglSwapBuffers failed: %s (0x%x)\n", EglErrStr(e), e);
        ++render_count_;
        return false;
    }
    ++render_count_;
    return true;
}

} // namespace weweb
