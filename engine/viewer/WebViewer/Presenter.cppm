module;

struct GLFWwindow;

export module viewer.web:presenter;

import rstd.cppstd;
import weweb;

export namespace weweb
{

// Abstract present backend: takes CEF DMA-BUF frames and shows them in a
// GLFW window. Two implementations: VulkanBlitter (default) and
// EglPresenter (selected via the WebViewer --presenter flag).
class Presenter {
public:
    virtual ~Presenter() = default;

    virtual bool Init(GLFWwindow* window) = 0;
    virtual void Shutdown()               = 0;

    virtual std::uint32_t Width() const  = 0;
    virtual std::uint32_t Height() const = 0;

    virtual bool Resize()                               = 0;
    virtual bool AcceptDmaBuf(const DmaBufFrame& frame) = 0;
    virtual bool RenderFrame()                          = 0;
};

} // namespace weweb
