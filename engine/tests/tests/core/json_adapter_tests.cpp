#include <gtest/gtest.h>

#include <filesystem>
#include <fstream>

#include <new> // wescene.json 的全局模块片段带进 <new>，这里显式包含，免得与隐式 operator new 冲突
#include "JsonNlohmann.hpp"

import rstd;
import rstd.cppstd;
import owe.user_property;
import wescene.fs;
import wescene.json;
import wescene.testing.json_builder;

using namespace rstd::literals;

TEST(JsonAdapter, ParsesDumpsAndReportsMembers) {
    auto parsed = owe::ParseJson(R"({"z":[true,null],"a":1.0})");
    ASSERT_TRUE(parsed.is_ok());
    auto value = parsed.unwrap();
    EXPECT_TRUE(value.get("a"_str).is_some());
    EXPECT_TRUE(value.get("missing"_str).is_none());
    const std::string dynamic_key = "a";
    EXPECT_TRUE(value.get(rstd::cppstd::as_str(dynamic_key).unwrap()).is_some());
    const std::string_view mutable_key = "z";
    EXPECT_TRUE(value.get_mut(rstd::cppstd::as_str(mutable_key).unwrap()).is_some());
    auto z = value.get("z"_str);
    ASSERT_TRUE(z.is_some());
    EXPECT_TRUE((*z)->is_array());
    EXPECT_EQ(owe::Dump(value), R"({"a":1.0,"z":[true,null]})");
    EXPECT_EQ(owe::Dump(value, std::size_t { 2 }),
              "{\n  \"a\": 1.0,\n  \"z\": [\n    true,\n    null\n  ]\n}");
}

TEST(JsonAdapter, CommentsRequireExplicitOption) {
    EXPECT_TRUE(owe::ParseJson("/* comment */ null").is_err());
    auto parsed =
        owe::ParseJson("{/* comment */ \"value\": 1 // line\n}", { .allow_comments = true });
    ASSERT_TRUE(parsed.is_ok());
    auto value  = parsed.unwrap();
    auto member = value.get("value"_str);
    ASSERT_TRUE(member.is_some());
    EXPECT_EQ((*member)->as_i64().unwrap_or(rstd::i64()).to_primitive(), 1);
}

TEST(JsonAdapter, WpeVfsJsonAllowsTrailingCommasWithoutRelaxingDirectParse) {
    EXPECT_TRUE(owe::ParseJson("[1,]").is_err());
    EXPECT_TRUE(owe::ParseJson("{\"value\":1,}").is_err());

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

        auto parsed = owe::ReadJsonFile(vfs, "/assets/trailing.json");
        ASSERT_TRUE(parsed.is_ok());
        auto value = parsed.unwrap();
        const auto items = value.get("items"_str);
        ASSERT_TRUE(items.is_some());
        ASSERT_TRUE((*items)->is_array());
        const auto array = (*items)->as_array();
        ASSERT_TRUE(array.is_some());
        EXPECT_EQ((**array).len(), rstd::usize(2));
        const auto nested = (**array)[rstd::usize(1)].get("nested"_str);
        ASSERT_TRUE(nested.is_some());
        EXPECT_EQ(*(*nested)->as_str(), "comma,] remains text"_str);
    }

    std::filesystem::remove_all(root);
}

TEST(JsonAdapter, WpeVfsJsonStillRejectsConsecutiveCommas) {
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
        EXPECT_TRUE(owe::ReadJsonFile(vfs, "/assets/invalid.json").is_err());
    }

    std::filesystem::remove_all(root);
}

TEST(JsonAdapter, ClonesSubtreesExplicitly) {
    auto parsed = owe::ParseJson(R"({"nested":{"value":1}})");
    ASSERT_TRUE(parsed.is_ok());
    auto original = parsed.unwrap();
    auto clone    = original.clone();
    auto nested   = clone.get_mut("nested"_str);
    ASSERT_TRUE(nested.is_some());
    auto object = (*nested)->as_object_mut();
    ASSERT_TRUE(object.is_some());
    (*object)->insert(::alloc::string::String::make("value"_str),
                      rstd::into<owe::Json>(rstd::i32(2)));
    EXPECT_EQ(owe::Dump(original), R"({"nested":{"value":1}})");
    EXPECT_EQ(owe::Dump(clone), R"({"nested":{"value":2}})");
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

TEST(JsonAdapter, NativeProjectionsPreserveOptions) {
    auto parsed = owe::ParseJson(R"({"number":1.75,"bool":true,"text":"value"})");
    ASSERT_TRUE(parsed.is_ok());
    auto value  = parsed.unwrap();
    auto number = value.get("number"_str);
    ASSERT_TRUE(number.is_some());
    EXPECT_DOUBLE_EQ((*number)->as_f64().unwrap_or(rstd::f64()).to_primitive(), 1.75);
    auto boolean = value.get("bool"_str);
    ASSERT_TRUE(boolean.is_some());
    EXPECT_TRUE((*boolean)->as_bool().unwrap_or(false));
    EXPECT_TRUE((*boolean)->as_i64().is_none());
    auto text = value.get("text"_str);
    ASSERT_TRUE(text.is_some());
    EXPECT_EQ(rstd::cppstd::as_string_view(*(*text)->as_str()), "value");
    EXPECT_TRUE(value.get("missing"_str).is_none());
}

TEST(JsonAdapter, BuildsObjectsArraysAndIteratesWithoutKeyCopies) {
    auto array  = owe::MakeArray(1, true, "text");
    auto object = owe::MakeObject();
    ASSERT_TRUE(owe::SetMember(object, "items", std::move(array)));
    ASSERT_TRUE(owe::SetMember(object, "name", std::string("demo")));

    std::vector<std::string> keys;
    auto                     object_values = object.as_object();
    ASSERT_TRUE(object_values.is_some());
    (*object_values)->iter().for_each([&](auto entry) {
        auto [entry_key, entry_value] = entry;
        keys.push_back(rstd::cppstd::to_string(entry_key->as_str()));
    });
    EXPECT_EQ(keys, (std::vector<std::string> { "items", "name" }));
    EXPECT_EQ(owe::Dump(object), R"({"items":[1,true,"text"],"name":"demo"})");
}

TEST(JsonAdapter, ProductionOutputContractsRoundTrip) {
    auto property = owe::MakeObject();
    ASSERT_TRUE(owe::SetMember(property, "dynamic\"\\", "line\n\t"));

    const auto compact = owe::Dump(property);
    EXPECT_EQ(compact, R"({"dynamic\"\\":"line\n\t"})");

    auto parsed = owe::ParseJson(compact);
    ASSERT_TRUE(parsed.is_ok());
    auto reparsed = parsed.unwrap();
    EXPECT_EQ(owe::Dump(reparsed), compact);
}

TEST(JsonAdapter, LegacyGetJsonValueReadsScalarAndNamedValues) {
    auto parsed = owe::ParseJson(
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

TEST(JsonAdapter, LegacyGetJsonValuePreservesNumericConversions) {
    auto floating = owe::ParseJson("3.75");
    ASSERT_TRUE(floating.is_ok());
    std::int32_t integer = 0;
    EXPECT_TRUE(owe::GetJsonValue(floating.unwrap(), integer));
    EXPECT_EQ(integer, 3);

    auto negative = owe::ParseJson("-1");
    ASSERT_TRUE(negative.is_ok());
    std::uint32_t unsigned_integer = 0;
    EXPECT_TRUE(owe::GetJsonValue(negative.unwrap(), unsigned_integer));
    EXPECT_EQ(unsigned_integer, std::numeric_limits<std::uint32_t>::max());

    auto boolean = owe::ParseJson("true");
    ASSERT_TRUE(boolean.is_ok());
    double numeric_boolean = 0.0;
    EXPECT_TRUE(owe::GetJsonValue(boolean.unwrap(), numeric_boolean));
    EXPECT_DOUBLE_EQ(numeric_boolean, 1.0);
}

TEST(JsonAdapter, LegacyGetJsonValueReadsArrayFormats) {
    auto parsed =
        owe::ParseJson(R"({"vector":"1.5 2.5 3.5","pair":"8 9","single":4,"ints":"1 -2 3"})");
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

TEST(JsonAdapter, LegacyGetJsonValueReportsConversionFailure) {
    auto wrong_size = owe::ParseJson(R"("1 2")");
    ASSERT_TRUE(wrong_size.is_ok());
    std::array<float, 3> fixed { 7.0f, 8.0f, 9.0f };
    EXPECT_FALSE(owe::GetJsonValue(wrong_size.unwrap(), fixed));
    EXPECT_EQ(fixed, (std::array<float, 3> { 7.0f, 8.0f, 9.0f }));

    auto wrong_type = owe::ParseJson(R"("not a number")");
    ASSERT_TRUE(wrong_type.is_ok());
    double number = 2.0;
    EXPECT_FALSE(owe::GetJsonValue(wrong_type.unwrap(), number));
    EXPECT_DOUBLE_EQ(number, 2.0);
}
