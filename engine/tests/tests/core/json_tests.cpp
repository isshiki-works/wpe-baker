#include <owe/compat.hpp>
#include <owe/std.hpp>
#include <gtest/gtest.h>

#include <filesystem>
#include <fstream>

#include <new> // wescene.json 的全局模块片段带进 <new>，这里显式包含，免得与隐式 operator new 冲突
#include "JsonNlohmann.hpp"

import owe.user_property;
import wescene.fs;
import wescene.json;

using namespace rstd::literals;

// 原 json_adapter_tests 里与值类型无关的语义用例，rstd 版删除后改读 NJson；
// 只测 rstd API 本身的用例（clone、as_object_mut、rstd 投影、JsonBuilder）随 rstd 版删掉。

TEST(Json, ParsesDumpsAndReportsMembers) {
    auto parsed = owe::ParseNJson(R"({"z":[true,null],"a":1.0})");
    ASSERT_TRUE(parsed.is_ok());
    auto value = parsed.unwrap();
    EXPECT_NE(owe::Find(value, "a"), nullptr);
    EXPECT_EQ(owe::Find(value, "missing"), nullptr);
    const auto* z = owe::Find(value, "z");
    ASSERT_NE(z, nullptr);
    EXPECT_TRUE(z->is_array());
    EXPECT_EQ(owe::Dump(value), R"({"a":1.0,"z":[true,null]})");
    EXPECT_EQ(owe::Dump(value, rstd::usize(2)),
              "{\n  \"a\": 1.0,\n  \"z\": [\n    true,\n    null\n  ]\n}");
}

TEST(Json, CommentsRequireExplicitOption) {
    EXPECT_TRUE(owe::ParseNJson("/* comment */ null").is_err());
    auto parsed =
        owe::ParseNJson("{/* comment */ \"value\": 1 // line\n}", { .allow_comments = true });
    ASSERT_TRUE(parsed.is_ok());
    auto        value  = parsed.unwrap();
    const auto* member = owe::Find(value, "value");
    ASSERT_NE(member, nullptr);
    ASSERT_TRUE(member->is_number_integer());
    EXPECT_EQ(member->get<std::int64_t>(), 1);
}

TEST(Json, WpeVfsJsonAllowsTrailingCommasWithoutRelaxingDirectParse) {
    EXPECT_TRUE(owe::ParseNJson("[1,]").is_err());
    EXPECT_TRUE(owe::ParseNJson("{\"value\":1,}").is_err());

    const auto root = std::filesystem::temp_directory_path() /
                      ("owe-json-trailing-" + std::to_string(rstd::process::id().to_primitive()));
    std::filesystem::remove_all(root);
    std::filesystem::create_directories(root);
    {
        std::ofstream output(root / "trailing.json");
        output << R"({"items":[1,{"nested":"comma,] remains text",},],})";
    }

    {
        owe::fs::VFS vfs;
        auto physical = owe::fs::make_physical_fs(owe::fs::ToPath(root.string()));
        ASSERT_TRUE(physical.is_ok());
        ASSERT_TRUE(vfs.mount("/assets"_str, std::move(physical).unwrap_unchecked()).is_ok());

        auto parsed = owe::ReadNJsonFile(vfs, "/assets/trailing.json");
        ASSERT_TRUE(parsed.is_ok());
        auto        value = parsed.unwrap();
        const auto* items = owe::Find(value, "items");
        ASSERT_NE(items, nullptr);
        ASSERT_TRUE(items->is_array());
        EXPECT_EQ(items->size(), 2u);
        const auto* nested = owe::Find((*items)[1], "nested");
        ASSERT_NE(nested, nullptr);
        EXPECT_EQ(nested->get<std::string>(), "comma,] remains text");
    }

    std::filesystem::remove_all(root);
}

TEST(Json, WpeVfsJsonStillRejectsConsecutiveCommas) {
    const auto root = std::filesystem::temp_directory_path() /
                      ("owe-json-double-comma-" + std::to_string(rstd::process::id().to_primitive()));
    std::filesystem::remove_all(root);
    std::filesystem::create_directories(root);
    {
        std::ofstream output(root / "invalid.json");
        output << R"({"items":[1,,2]})";
    }

    {
        owe::fs::VFS vfs;
        auto physical = owe::fs::make_physical_fs(owe::fs::ToPath(root.string()));
        ASSERT_TRUE(physical.is_ok());
        ASSERT_TRUE(vfs.mount("/assets"_str, std::move(physical).unwrap_unchecked()).is_ok());
        EXPECT_TRUE(owe::ReadNJsonFile(vfs, "/assets/invalid.json").is_err());
    }

    std::filesystem::remove_all(root);
}

TEST(UserProperty, TextInputWireValuesStayStrings) {
    auto schema =
        owe::ParseNJson(R"({"type":"textinput","text":"Text","order":7,"value":"default"})")
            .unwrap();

    for (const auto& raw :
         { std::string("12"), std::string("true"), std::string("提醒喝水"), std::string() }) {
        auto patch  = owe::MakeUserPropertyWirePatch(raw);
        auto merged = owe::MergeUserPropertyDescriptor(schema, patch);
        const auto* value = owe::Find(merged, "value");
        ASSERT_NE(value, nullptr);
        ASSERT_TRUE(value->is_string());
        EXPECT_EQ(value->get<std::string>(), raw);
        EXPECT_NE(owe::Find(merged, "text"), nullptr);
        EXPECT_NE(owe::Find(merged, "order"), nullptr);
    }
}

TEST(UserProperty, NonTextWireValuesKeepExistingJsonCoercion) {
    auto schema = owe::ParseNJson(R"({"type":"slider","value":0})").unwrap();
    auto patch  = owe::MakeUserPropertyWirePatch("1.5");
    auto merged = owe::MergeUserPropertyDescriptor(schema, patch);
    const auto* value = owe::Find(merged, "value");
    ASSERT_NE(value, nullptr);
    ASSERT_TRUE(value->is_number());
    EXPECT_DOUBLE_EQ(value->get<double>(), 1.5);
}

TEST(UserProperty, UnknownTypeDefersWireValueCoercion) {
    auto patch  = owe::MakeUserPropertyWirePatch("12");
    auto merged = owe::MergeUserPropertyDescriptor(owe::NJson(""), patch);
    const auto* value = owe::Find(merged, "value");
    ASSERT_NE(value, nullptr);
    ASSERT_TRUE(value->is_string());
    EXPECT_EQ(value->get<std::string>(), "12");
}

TEST(Json, ProductionOutputContractsRoundTrip) {
    owe::NJson property = owe::NJson::object();
    property["dynamic\"\\"] = "line\n\t";

    const auto compact = owe::Dump(property);
    EXPECT_EQ(compact, R"({"dynamic\"\\":"line\n\t"})");

    auto parsed = owe::ParseNJson(compact);
    ASSERT_TRUE(parsed.is_ok());
    auto reparsed = parsed.unwrap();
    EXPECT_EQ(owe::Dump(reparsed), compact);
}

TEST(Json, GetJsonValueReadsScalarAndNamedValues) {
    auto parsed = owe::ParseNJson(
        R"({"bound":{"value":12.75},"plain":7,"text":"hello","flag":true,"null":null})");
    ASSERT_TRUE(parsed.is_ok());
    auto json = parsed.unwrap();

    float bound = 0.0f;
    EXPECT_TRUE(owe::GetJsonValue(json, "bound", bound));
    EXPECT_FLOAT_EQ(bound, 12.75f);

    std::int32_t plain = 0;
    EXPECT_TRUE(owe::GetJsonValue(json, "plain", plain));
    EXPECT_EQ(plain, 7);

    std::string text;
    EXPECT_TRUE(owe::GetJsonValue(json, "text", text));
    EXPECT_EQ(text, "hello");

    bool flag = false;
    EXPECT_TRUE(owe::GetJsonValue(json, "flag", flag));
    EXPECT_TRUE(flag);

    std::int32_t unchanged = 41;
    EXPECT_FALSE(owe::GetJsonValue(json, "missing", unchanged, false));
    EXPECT_EQ(unchanged, 41);
    EXPECT_FALSE(owe::GetJsonValue(json, "null", unchanged, false));
    EXPECT_EQ(unchanged, 41);
}

TEST(Json, GetJsonValuePreservesNumericConversions) {
    auto floating = owe::ParseNJson("3.75");
    ASSERT_TRUE(floating.is_ok());
    std::int32_t integer = 0;
    EXPECT_TRUE(owe::GetJsonValue(floating.unwrap(), integer));
    EXPECT_EQ(integer, 3);

    auto negative = owe::ParseNJson("-1");
    ASSERT_TRUE(negative.is_ok());
    std::uint32_t unsigned_integer = 0;
    EXPECT_TRUE(owe::GetJsonValue(negative.unwrap(), unsigned_integer));
    EXPECT_EQ(unsigned_integer, std::numeric_limits<std::uint32_t>::max());

    auto boolean = owe::ParseNJson("true");
    ASSERT_TRUE(boolean.is_ok());
    double numeric_boolean = 0.0;
    EXPECT_TRUE(owe::GetJsonValue(boolean.unwrap(), numeric_boolean));
    EXPECT_DOUBLE_EQ(numeric_boolean, 1.0);
}

TEST(Json, GetJsonValueReadsArrayFormats) {
    auto parsed =
        owe::ParseNJson(R"({"vector":"1.5 2.5 3.5","pair":"8 9","single":4,"ints":"1 -2 3"})");
    ASSERT_TRUE(parsed.is_ok());
    auto json = parsed.unwrap();

    std::array<float, 3> fixed {};
    EXPECT_TRUE(owe::GetJsonValue(json, "vector", fixed));
    EXPECT_EQ(fixed, (std::array<float, 3> { 1.5f, 2.5f, 3.5f }));

    std::vector<float> dynamic { 9.0f, 8.0f, 7.0f, 6.0f };
    EXPECT_TRUE(owe::GetJsonValue(json, "vector", dynamic));
    EXPECT_EQ(dynamic, (std::vector<float> { 1.5f, 2.5f, 3.5f, 6.0f }));

    EXPECT_TRUE(owe::GetJsonValue(json, "single", fixed));
    EXPECT_EQ(fixed, (std::array<float, 3> { 4.0f, 0.0f, 0.0f }));

    std::array<float, 2> pair {};
    EXPECT_TRUE(owe::GetJsonValue(json, "pair", pair));
    EXPECT_EQ(pair, (std::array<float, 2> { 8.0f, 9.0f }));

    std::array<int, 3> fixed_integers {};
    EXPECT_TRUE(owe::GetJsonValue(json, "ints", fixed_integers));
    EXPECT_EQ(fixed_integers, (std::array<int, 3> { 1, -2, 3 }));

    std::vector<std::int32_t> integers;
    EXPECT_TRUE(owe::GetJsonValue(json, "ints", integers));
    EXPECT_EQ(integers, (std::vector<std::int32_t> { 1, -2, 3 }));
}

TEST(Json, GetJsonValueReportsConversionFailure) {
    auto wrong_size = owe::ParseNJson(R"("1 2")");
    ASSERT_TRUE(wrong_size.is_ok());
    std::array<float, 3> fixed { 7.0f, 8.0f, 9.0f };
    EXPECT_FALSE(owe::GetJsonValue(wrong_size.unwrap(), fixed));
    EXPECT_EQ(fixed, (std::array<float, 3> { 7.0f, 8.0f, 9.0f }));

    auto wrong_type = owe::ParseNJson(R"("not a number")");
    ASSERT_TRUE(wrong_type.is_ok());
    double number = 2.0;
    EXPECT_FALSE(owe::GetJsonValue(wrong_type.unwrap(), number));
    EXPECT_DOUBLE_EQ(number, 2.0);
}
