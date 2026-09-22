#include <rstd/test/gtest.hpp>

#include <filesystem>
#include <fstream>

import rstd;
import rstd.cppstd;
import wescene.fs;
import wescene.pkg.parse;
import wescene.scene;
import wescene.types;

using namespace rstd::prelude;
using namespace rstd::literals;
using rstd::sync::Arc;

namespace
{

class TrackingImageParser {
public:
    auto Parse(ref<str> name) const -> Result<Arc<owe::Image>, owe::ImageParseError> {
        auto current  = m_active.fetch_add(1) + 1;
        auto observed = m_peak.load();
        while (current > observed && ! m_peak.compare_exchange_weak(observed, current)) {
        }

        std::this_thread::sleep_for(std::chrono::milliseconds(10));
        auto image = Arc<owe::Image>::make();
        image->key = rstd::cppstd::to_string(name);
        m_active.fetch_sub(1);
        return Ok(rstd::move(image));
    }

    auto ParseHeader(ref<str>) const -> Result<owe::ImageHeader, owe::ImageParseError> {
        return Ok(owe::ImageHeader {});
    }

    auto ParseMany(slice<String> names) const
        -> Vec<Result<Arc<owe::Image>, owe::ImageParseError>> {
        auto parser = dyn<owe::IImageParser>::from_ref(*this);
        return owe::ParseImages(parser.as_ref(), names);
    }

    int peak() const { return m_peak.load(); }

private:
    mutable std::atomic<int> m_active { 0 };
    mutable std::atomic<int> m_peak { 0 };
};

class MixedImageParser {
public:
    auto Parse(ref<str> name) const -> Result<Arc<owe::Image>, owe::ImageParseError> {
        if (rstd::cppstd::as_string_view(name) == "bad") {
            return Err(owe::ImageParseError {
                .kind    = owe::ImageParseErrorKind::DecodeFailed,
                .message = String::make("bad image"_str),
            });
        }
        auto image = Arc<owe::Image>::make();
        image->key = rstd::cppstd::to_string(name);
        return Ok(rstd::move(image));
    }

    auto ParseHeader(ref<str>) const -> Result<owe::ImageHeader, owe::ImageParseError> {
        return Ok(owe::ImageHeader {});
    }

    auto ParseMany(slice<String> names) const
        -> Vec<Result<Arc<owe::Image>, owe::ImageParseError>> {
        auto parser = dyn<owe::IImageParser>::from_ref(*this);
        return owe::ParseImages(parser.as_ref(), names);
    }
};

void WriteU32(std::ofstream& output, std::uint32_t value) {
    output.write(reinterpret_cast<const char*>(&value), sizeof(value));
}

TEST(ImageParser, BatchPreservesOrderAndBoundsConcurrency) {
    TrackingImageParser parser;
    Vec<String>         names;
    for (const char* name : { "0", "1", "2", "3", "4", "5", "6", "7" })
        names.push(String::make(rstd::cppstd::as_str(name).unwrap()));

    auto image_parser = dyn<owe::IImageParser>::from_ref(parser);
    auto images       = owe::ParseImages(image_parser.as_ref(), names.as_slice());

    ASSERT_EQ(images.len(), names.len());
    for (usize index {}; index < names.len(); ++index) {
        ASSERT_TRUE(images[index].is_ok());
        auto image = rstd::move(images[index]).unwrap_unchecked();
        EXPECT_EQ(image->key, rstd::cppstd::to_string(names[index].as_str()));
    }
    EXPECT_GT(parser.peak(), 1);
    EXPECT_LE(parser.peak(), 4);
}

TEST(ImageParser, TextureHeaderExposesFourthPackedComponent) {
    auto root = std::filesystem::temp_directory_path() /
                ("owe-image-parser-" + std::to_string(rstd::process::id().to_primitive()));
    std::filesystem::remove_all(root);
    std::filesystem::create_directories(root / "materials");
    {
        std::ofstream output(root / "materials" / "mask.tex", std::ios::binary);
        output.write("TEXV0005", 9);
        output.write("TEXI0001", 9);
        WriteU32(output, 4);
        WriteU32(output, 1u << 23);
        WriteU32(output, 1);
        WriteU32(output, 1);
        WriteU32(output, 1);
        WriteU32(output, 1);
        WriteU32(output, 0);
        output.write("TEXB0001", 9);
        WriteU32(output, 1);
        WriteU32(output, 1);
        WriteU32(output, 1);
        WriteU32(output, 1);
    }

    auto physical = owe::fs::make_physical_fs(owe::fs::ToPath(root.string()));
    ASSERT_TRUE(physical.is_ok());
    owe::fs::VFS vfs;
    ASSERT_TRUE(vfs.mount("/assets"_str, rstd::move(physical).unwrap_unchecked()).is_ok());

    owe::TexImageParser parser(&vfs);
    auto                parsed = parser.ParseHeader("mask"_str);
    ASSERT_TRUE(parsed.is_ok());
    auto header = rstd::move(parsed).unwrap_unchecked();
    EXPECT_EQ(header.extraHeader.at("compo1").val, 0);
    EXPECT_EQ(header.extraHeader.at("compo2").val, 0);
    EXPECT_EQ(header.extraHeader.at("compo3").val, 0);
    EXPECT_EQ(header.extraHeader.at("compo4").val, 1);

    std::filesystem::remove_all(root);
}

TEST(ImageParser, SceneBatchPreservesRuntimeParserAndErrorPositions) {
    owe::Scene scene;
    scene.SetImageParser(Box<dyn<owe::IImageParser>>::make(MixedImageParser {}));
    auto runtime = Arc<owe::Image>::make();
    runtime->key = "runtime-value";
    scene.RegisterRuntimeImage(String::make("runtime"_str), runtime.clone());

    Vec<String> names;
    for (const char* name : { "first", "runtime", "bad", "last" }) {
        names.push(String::make(rstd::cppstd::as_str(name).unwrap()));
    }
    auto images = scene.ParseImages(names.as_slice());

    ASSERT_EQ(images.len(), names.len());
    ASSERT_TRUE(images[usize(0)].is_ok());
    EXPECT_EQ((*images[usize(0)])->key, "first");
    ASSERT_TRUE(images[usize(1)].is_ok());
    EXPECT_TRUE(Arc<owe::Image>::ptr_eq(*images[usize(1)], runtime));
    ASSERT_TRUE(images[usize(2)].is_err());
    EXPECT_EQ(images[usize(2)].unwrap_err_unchecked().kind, owe::ImageParseErrorKind::DecodeFailed);
    ASSERT_TRUE(images[usize(3)].is_ok());
    EXPECT_EQ((*images[usize(3)])->key, "last");
}

} // namespace
