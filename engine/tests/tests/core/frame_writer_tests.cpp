#include <gtest/gtest.h>

#include <chrono>
#include <cstdint>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

#include "../../../tools/SceneBake/FrameWriter.hpp"

// SceneBake 的像素写线程：写出的字节与顺序必须和同步写相同。

namespace
{
std::vector<std::uint8_t> Frame(std::uint8_t value) { return std::vector<std::uint8_t>(4096, value); }
} // namespace

// push 换回来的缓冲马上就会被下一帧的读回覆盖；写线程还在写的缓冲绝不能交回去。
TEST(FrameWriter, KeepsOrderAndNeverHandsBackTheFrameBeingWritten) {
    std::vector<std::uint8_t> written;
    FrameWriter writer([&](const std::vector<std::uint8_t>& frame) {
        std::this_thread::sleep_for(std::chrono::milliseconds(2));
        written.insert(written.end(), frame.begin(), frame.end());
    }, true);
    std::vector<std::uint8_t> expected;
    std::vector<std::uint8_t> pixels;
    for (int index = 0; index < 32; ++index) {
        pixels = Frame(static_cast<std::uint8_t>(index));
        expected.insert(expected.end(), pixels.begin(), pixels.end());
        writer.push(pixels);
        // 模拟渲染器立刻往换回来的缓冲里读回下一帧。
        for (auto& byte : pixels) byte = 0xee;
    }
    writer.finish();
    EXPECT_EQ(written, expected);
}

TEST(FrameWriter, ReportsWriteFailureAndStopsWriting) {
    int attempts = 0;
    FrameWriter writer([&](const std::vector<std::uint8_t>&) {
        ++attempts;
        throw std::runtime_error("frame consumer closed or failed");
    }, true);
    auto pixels = Frame(1);
    writer.push(pixels);
    pixels = Frame(2);
    try {
        writer.push(pixels);
        writer.finish();
        FAIL() << "write failure was swallowed";
    } catch (const std::runtime_error& error) {
        EXPECT_EQ(std::string(error.what()), "frame consumer closed or failed");
    }
    EXPECT_EQ(attempts, 1);
}

TEST(FrameWriter, SerialModeWritesOnCallerThread) {
    std::thread::id writer_thread;
    FrameWriter writer([&](const std::vector<std::uint8_t>&) { writer_thread = std::this_thread::get_id(); }, false);
    auto pixels = Frame(3);
    writer.push(pixels);
    EXPECT_EQ(writer_thread, std::this_thread::get_id());
    EXPECT_EQ(pixels, Frame(3));
    writer.finish();
}
