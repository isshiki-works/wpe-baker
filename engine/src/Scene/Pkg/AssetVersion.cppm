module;

#include <rstd/macro.hpp>
#include <cstdio>

export module wescene.pkg_asset_version;
import wescene.core;
import rstd.log;
import rstd.cppstd;

import wescene.fs;

export namespace owe
{

int32_t ReadAssetVersion(std::string_view prefix, fs::BinaryReader& file) {
    char str_v[9] { '\0' };
    file.Read(str_v, 9);
    if (! sstart_with(str_v, prefix)) return 0;

    char* str_int = str_v + 4;
    int   slot;
    auto [ptr, ec] { std::from_chars(str_int, std::end(str_v), slot) };
    if (ec != std::errc()) {
        rstd_error("read version of \'{}\' failed", std::string_view(str_v, 8));
        return 0;
    }
    return slot;
}

void WriteAssetVersion(std::string_view prefix, fs::BinaryWriter& file, int ver) {
    char buf[9] { '\0' };
    std::snprintf(buf, sizeof(buf), "%.4s%.4d", prefix.data(), ver);
    file.Write(buf, sizeof(buf));
}

int32_t ReadTexVersion(fs::BinaryReader& file) { return ReadAssetVersion("TEX", file); }
int32_t ReadMdlVersion(fs::BinaryReader& file) { return ReadAssetVersion("MDL", file); }

// DIY
int32_t ReadShaderCacheVersion(fs::BinaryReader& file) { return ReadAssetVersion("SPV", file); }
void    WriteShaderCacheVersion(fs::BinaryWriter& file, int ver) {
    WriteAssetVersion("SPVS", file, ver);
}

} // namespace owe
