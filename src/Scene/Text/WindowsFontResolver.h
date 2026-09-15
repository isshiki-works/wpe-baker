// SPDX-License-Identifier: MIT
#pragma once
#ifdef _WIN32
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <dwrite.h>
#include <filesystem>
#include <string>
#include <string_view>
#include <vector>

namespace owe::text::windows {

template<class T> class ComHandle {
    T* value_ = nullptr;
public:
    ComHandle() = default;
    ComHandle(const ComHandle&) = delete;
    ComHandle& operator=(const ComHandle&) = delete;
    ~ComHandle() { if (value_) value_->Release(); }
    T** put() { if (value_) value_->Release(); value_ = nullptr; return &value_; }
    T* get() const { return value_; }
    T* operator->() const { return value_; }
};

struct FontLocation {
    std::filesystem::path path;
    unsigned face_index = 0;
};

inline std::wstring Wide(std::string_view text) {
    if (text.empty()) return {};
    int count = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(), static_cast<int>(text.size()), nullptr, 0);
    if (count <= 0) return {};
    std::wstring result(static_cast<size_t>(count), L'\0');
    if (MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(), static_cast<int>(text.size()), result.data(), count) != count)
        return {};
    return result;
}

inline bool Collection(ComHandle<IDWriteFactory>& factory, ComHandle<IDWriteFontCollection>& collection) {
    return SUCCEEDED(DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED, __uuidof(IDWriteFactory),
                                        reinterpret_cast<IUnknown**>(factory.put()))) &&
           SUCCEEDED(factory->GetSystemFontCollection(collection.put(), FALSE));
}

inline FontLocation Locate(IDWriteFont* font) {
    ComHandle<IDWriteFontFace> face;
    if (FAILED(font->CreateFontFace(face.put()))) return {};
    UINT32 count = 0;
    if (FAILED(face->GetFiles(&count, nullptr)) || count != 1) return {};
    ComHandle<IDWriteFontFile> file;
    if (FAILED(face->GetFiles(&count, file.put()))) return {};
    ComHandle<IDWriteFontFileLoader> loader;
    ComHandle<IDWriteLocalFontFileLoader> local;
    if (FAILED(file->GetLoader(loader.put())) ||
        FAILED(loader->QueryInterface(__uuidof(IDWriteLocalFontFileLoader), reinterpret_cast<void**>(local.put())))) return {};
    const void* key = nullptr;
    UINT32 key_size = 0, path_size = 0;
    if (FAILED(file->GetReferenceKey(&key, &key_size)) ||
        FAILED(local->GetFilePathLengthFromKey(key, key_size, &path_size))) return {};
    std::wstring path(static_cast<size_t>(path_size) + 1, L'\0');
    if (FAILED(local->GetFilePathFromKey(key, key_size, path.data(), path_size + 1))) return {};
    path.resize(path_size);
    return {std::filesystem::path(path), face->GetIndex()};
}

inline FontLocation ResolveFamily(std::string_view family) {
    auto name = Wide(family);
    if (name.empty()) return {};
    ComHandle<IDWriteFactory> factory;
    ComHandle<IDWriteFontCollection> collection;
    if (!Collection(factory, collection)) return {};
    UINT32 index = 0;
    BOOL exists = FALSE;
    if (FAILED(collection->FindFamilyName(name.c_str(), &index, &exists)) || !exists) return {};
    ComHandle<IDWriteFontFamily> match;
    ComHandle<IDWriteFont> font;
    if (FAILED(collection->GetFontFamily(index, match.put())) ||
        FAILED(match->GetFirstMatchingFont(DWRITE_FONT_WEIGHT_NORMAL, DWRITE_FONT_STRETCH_NORMAL,
                                           DWRITE_FONT_STYLE_NORMAL, font.put()))) return {};
    return Locate(font.get());
}

inline FontLocation ResolveCodepoint(unsigned codepoint) {
    if (codepoint > 0x10ffff || (codepoint >= 0xd800 && codepoint <= 0xdfff)) return {};
    ComHandle<IDWriteFactory> factory;
    ComHandle<IDWriteFontCollection> collection;
    if (!Collection(factory, collection)) return {};
    for (UINT32 index = 0; index < collection->GetFontFamilyCount(); ++index) {
        ComHandle<IDWriteFontFamily> family;
        ComHandle<IDWriteFont> font;
        if (FAILED(collection->GetFontFamily(index, family.put())) ||
            FAILED(family->GetFirstMatchingFont(DWRITE_FONT_WEIGHT_NORMAL, DWRITE_FONT_STRETCH_NORMAL,
                                                DWRITE_FONT_STYLE_NORMAL, font.put()))) continue;
        BOOL present = FALSE;
        if (FAILED(font->HasCharacter(codepoint, &present)) || !present) continue;
        auto location = Locate(font.get());
        if (!location.path.empty()) return location;
    }
    return {};
}

inline std::vector<std::filesystem::path> FontRoots() {
    std::vector<std::filesystem::path> roots;
    wchar_t buffer[32768];
    UINT length = GetWindowsDirectoryW(buffer, 32768);
    if (length && length < 32768) roots.emplace_back(std::filesystem::path(buffer) / L"Fonts");
    DWORD local = GetEnvironmentVariableW(L"LOCALAPPDATA", buffer, 32768);
    if (local && local < 32768) roots.emplace_back(std::filesystem::path(buffer) / L"Microsoft" / L"Windows" / L"Fonts");
    return roots;
}
} // namespace owe::text::windows
#endif
