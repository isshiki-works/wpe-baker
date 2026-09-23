module;

#include <cstdio>
#ifdef _WIN32
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#else
#include <dlfcn.h>
#endif

module wescene.types;
import rstd.cppstd;

namespace owe
{

std::string ToString(const ImageType& type) {
#define IMG(x) \
    case ImageType::x: return #x;
    switch (type) {
        IMG(UNKNOWN);
        IMG(BMP);
        IMG(ICO);
        IMG(JPEG);
        IMG(JNG);
        IMG(PNG);
        IMG(VIDEO);
    default: std::fprintf(stderr, "[ERROR] Not valid image type: %d\n", (int)type); return "";
    }
#undef IMG
}

std::string ToString(const TextureFormat& format) {
#define FMT(x) \
    case TextureFormat::x: return #x;
    switch (format) {
        FMT(RGBA8);
        FMT(BC1);
        FMT(BC2);
        FMT(BC3);
        FMT(RGB8);
        FMT(RG8);
        FMT(R8);
        FMT(RGBA16F);
        FMT(D32F);
    default: std::fprintf(stderr, "[ERROR] Not valid tex format: %d\n", (int)format); return "";
    }
#undef FMT
}

} // namespace owe

namespace utils
{

DynamicLibrary::DynamicLibrary() = default;
DynamicLibrary::DynamicLibrary(const char* filename) { Open(filename); }
DynamicLibrary::~DynamicLibrary() { Close(); }
DynamicLibrary::DynamicLibrary(DynamicLibrary&& o) noexcept
    : handle(std::exchange(o.handle, nullptr)) {}
DynamicLibrary& DynamicLibrary::operator=(DynamicLibrary&& o) noexcept {
    Close();
    handle = std::exchange(o.handle, nullptr);
    return *this;
}
bool DynamicLibrary::Open(const char* filename) {
    Close();
#ifdef _WIN32
    if (filename == nullptr || filename[0] == '\0') return false;
    int size = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, filename, -1, nullptr, 0);
    if (size <= 0) return false;
    std::wstring wide(static_cast<std::size_t>(size), L'\0');
    if (MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, filename, -1, wide.data(), size) != size)
        return false;
    handle = reinterpret_cast<void*>(LoadLibraryExW(wide.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DEFAULT_DIRS));
#else
    handle = dlopen(filename, RTLD_NOW);
#endif
    return IsOpen();
}
bool DynamicLibrary::IsOpen() const { return handle != nullptr; }
void DynamicLibrary::Close() {
    if (IsOpen()) {
#ifdef _WIN32
        FreeLibrary(reinterpret_cast<HMODULE>(handle));
#else
        dlclose(handle);
#endif
        handle = nullptr;
    }
}
void* DynamicLibrary::GetSymbolAddr(const char* name) const {
#ifdef _WIN32
    if (handle == nullptr || name == nullptr) return nullptr;
    return reinterpret_cast<void*>(GetProcAddress(reinterpret_cast<HMODULE>(handle), name));
#else
    return reinterpret_cast<void*>(dlsym(handle, name));
#endif
}

} // namespace utils
