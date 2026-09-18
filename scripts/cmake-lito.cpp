// Project-local workaround for Lito 0.7.1's CMake receipt parser on Windows.
// Forward to the unmodified, verified CMake and normalize only its two Lito
// receipt files after successful completion. No success or usage data is made up.
#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <iterator>
#include <string>
#include <vector>

namespace fs = std::filesystem;

std::wstring quoted(const std::wstring& value) {
    std::wstring result = L"\"";
    size_t slashes = 0;
    for (wchar_t c : value) {
        if (c == L'\\') { ++slashes; continue; }
        result.append(c == L'"' ? slashes * 2 + 1 : slashes, L'\\');
        result += c;
        slashes = 0;
    }
    result.append(slashes * 2, L'\\');
    return result + L'"';
}

void normalize(const fs::path& path, const std::string& magic) {
    if (!fs::is_regular_file(path)) return;
    std::ifstream input(path, std::ios::binary);
    std::string text(std::istreambuf_iterator<char>(input), {});
    input.close();
    if (!text.starts_with(magic + "\r\n")) return;
    std::string normalized;
    normalized.reserve(text.size());
    for (size_t i = 0; i < text.size(); ++i) {
        if (text[i] == '\r' && i + 1 < text.size() && text[i + 1] == '\n') continue;
        normalized += text[i];
    }
    std::ofstream output(path, std::ios::binary | std::ios::trunc);
    output.write(normalized.data(), static_cast<std::streamsize>(normalized.size()));
    if (!output) throw std::runtime_error("cannot normalize Lito CMake receipt");
}

int wmain(int argc, wchar_t** argv) {
    try {
        const auto executable = fs::absolute(argv[0]).parent_path() / "cmake/bin/cmake.exe";
        std::wstring command = quoted(executable.wstring());
        fs::path build;
        for (int i = 1; i < argc; ++i) {
            command += L' ' + quoted(argv[i]);
            const std::wstring argument(argv[i]);
            if ((argument == L"-B" || argument == L"--build") && i + 1 < argc)
                build = argv[i + 1];
            else if (argument.starts_with(L"-B") && argument.size() > 2)
                build = argument.substr(2);
        }
        STARTUPINFOW startup{sizeof(startup)};
        startup.dwFlags = STARTF_USESTDHANDLES;
        startup.hStdInput = GetStdHandle(STD_INPUT_HANDLE);
        startup.hStdOutput = GetStdHandle(STD_OUTPUT_HANDLE);
        startup.hStdError = GetStdHandle(STD_ERROR_HANDLE);
        PROCESS_INFORMATION process{};
        if (!CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr, TRUE,
                            CREATE_NO_WINDOW, nullptr, nullptr, &startup, &process)) {
            std::cerr << "cmake-lito: CreateProcessW failed: " << GetLastError() << '\n';
            return 125;
        }
        CloseHandle(process.hThread);
        WaitForSingleObject(process.hProcess, INFINITE);
        DWORD result = 125;
        GetExitCodeProcess(process.hProcess, &result);
        CloseHandle(process.hProcess);
        if (result == 0 && !build.empty()) {
            normalize(build / "lito-host-tools-v1.txt", "lito-cmake-host-tools-v1");
            normalize(build / "lito-assets-v2.txt", "lito-cmake-assets-v2");
        }
        return static_cast<int>(result);
    } catch (const std::exception& error) {
        std::cerr << "cmake-lito: " << error.what() << '\n';
        return 125;
    }
}
