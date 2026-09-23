module;
export module wescene.pkg.parse:tex_image_parser;
import rstd;
import rstd.cppstd;
import wescene.types;
import wescene.scene;
import wescene.fs;
import wescene.json;

using namespace rstd::prelude;
using rstd::sync::Arc;

export namespace owe

{

// Sub-version stamps embedded in a `.tex` file. All four are read from
// independent "TEXX0000" stamps interleaved with the header / sprite
// payload (see `LoadHeader` and the sprite branch of `ParseHeader`).
//
// Observed corpus distribution (732 pkgs, 15399 textures across PKGV0001..23):
//   texv: always 5
//   texi: always 1
//   texb: 1 (PKGV0001 only; 4)  |  2 (early; 170)  |  3 (historic; 10096)  |  4 (PKGV0022+; 5129)
//   texs: 0 (non-sprite)  |  2 (early sprites; 15)  |  3 (current sprites; 491, including 90 with
//   texb=4)
//          texs == 1 is documented in legacy code but never observed.
//
// Full binary layout, by version. All ints are little-endian int32
// unless noted; "TEXX####" stamps are 9-byte ASCII strings.
//
//   ┌── texv stamp ("TEXV0005")
//   ├── texi stamp ("TEXI0001")
//   ├── format       int32  (0=RGBA8, 4=BC3, 6=BC2, 7=BC1, 8=RG8, 9=R8)
//   ├── flags        uint32 (bit0=noInterpolation bit1=clampUVs bit2=sprite
//   │                        bit20..23=compo1..4)
//   ├── width/height int32 ×2  pow-2 texture coord size (or pic size when not pow-2)
//   ├── map_w/h      int32 ×2  original picture size (== width/height when pow-2)
//   ├── reserved_a   int32  unused, never observed != 0
//   ├── texb stamp ("TEXB0001"..0004)
//   ├── count        int32  number of image slots
//   ├── image_type   int32  if texb >= 3   (-1=UNKNOWN, FreeImage enum otherwise)
//   ├── variant_n    uint32 if texb >= 4   变体条件表条数（语料里 0 占绝大多数）
//   ├── per variant condition (× variant_n):
//   │       group uint32, id uint32, flags uint32, 以 \0 结尾的 JSON 条件
//   │
//   ├── per slot (× count):
//   │   ├── mip_count int32
//   │   └── per mip:
//   │       ├── mip_w/mip_h int32 ×2
//   │       ├── lz4_compressed   int32  if texb >= 2
//   │       ├── decompressed_sz  int32  if texb >= 2
//   │       ├── src_size         int32
//   │       ├── src_size bytes (LZ4 if compressed; image-container body when
//   │       │   texb>=3 + image_type valid; raw pixel data otherwise)
//   │       └── 变体补丁块 if variant_n > 0：uint32 组数，每组 { uint32 条数，每条
//   │           { uint32 (官方不读), id, x, y, w, h, FreeImage 格式, size (均 uint32), size 字节 } }
//   │
//   └── if flags.sprite:
//       ├── texs stamp ("TEXS0001"..0003)  ← only valid texs values
//       ├── frame_count int32
//       ├── atlas_w/h   int32 ×2  if texs >= 3
//       └── per frame (× frame_count):
//           ├── image_id int32
//           ├── frametime float32
//           └── (x, y, xAxis[0..1], yAxis[0..1])  6 ×
//               int32 if texs == 1, float32 otherwise
//
// The predicate methods below collapse texb / texs version drift into a
// single source of truth so the parser body and the sprite branch share
// the same dispatch rules.
struct TexFormatVersion {
    std::int32_t texv { 0 };
    std::int32_t texi { 0 };
    std::int32_t texb { 0 };
    std::int32_t texs { 0 };
    // texb >= 4 头部的变体条件表条数。
    std::uint32_t variant_count { 0 };

    // texb >= 2 — body has per-mip { LZ4_compressed, decompressed_size } prelude.
    constexpr bool body_has_lz4_prelude() const noexcept { return texb >= 2; }
    // texb >= 3 — header carries an int32 image_type slot before the mip body
    // (UNKNOWN/-1 for raw pixel data, or a FreeImage-style enum for png/jpg
    // containers). Pre-fix this was gated on `texb == 3`, which silently
    // dropped the slot for texb=4 and misaligned the entire body parse on
    // PKGV0022+ assets.
    constexpr bool body_has_image_type() const noexcept { return texb >= 3; }
    // texb >= 4 — image_type 之后是变体条件表条数（见上面的布局）。
    constexpr bool body_has_variant_table() const noexcept { return texb >= 4; }
    // texs == 1 — sprite frame coordinates are int pixels (legacy; never
    // observed in our corpus). Otherwise floats.
    constexpr bool sprite_frame_coords_int() const noexcept { return texs == 1; }
    // texs >= 3 — sprite section carries an extra trailing { width, height }
    // pair after framecount (atlas dimensions).
    constexpr bool sprite_has_atlas_size() const noexcept { return texs >= 3; }
    constexpr bool valid() const noexcept { return texv != 0 && texi != 0 && texb != 0; }
};

auto ParseImages(const IImageParser* parser, slice<String> names, usize max_workers = usize(4))
    -> Vec<Result<Arc<Image>, ImageParseError>>;

auto ProbeVideoDuration(fs::VFS&, ref<str>) -> Option<f64>;

class TexImageParser final : public IImageParser {
public:
    // user_properties：当前用户属性（属性名 → 描述对象），按它选贴图变体；为空时只用基础图。
    TexImageParser(fs::VFS* vfs, std::shared_ptr<const NJson> user_properties = nullptr)
        : m_vfs(vfs), m_user_properties(rstd::move(user_properties)) {}

    auto Parse(ref<str> name) const -> Result<Arc<Image>, ImageParseError> override;
    auto ParseMany(slice<String> names) const -> Vec<Result<Arc<Image>, ImageParseError>> override;
    auto ParseHeader(ref<str> name) const -> Result<ImageHeader, ImageParseError> override;

private:
    fs::VFS*                     m_vfs;
    std::shared_ptr<const NJson> m_user_properties;
};
} // namespace owe
