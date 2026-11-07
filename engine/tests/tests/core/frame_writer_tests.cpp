#include <gtest/gtest.h>

#include <chrono>
#include <array>
#include <atomic>
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
    FrameWriter writer([&](std::span<const std::uint8_t> frame) {
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
    FrameWriter writer([&](std::span<const std::uint8_t>) {
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
    FrameWriter writer([&](std::span<const std::uint8_t>) { writer_thread = std::this_thread::get_id(); }, false);
    auto pixels = Frame(3);
    writer.push(pixels);
    EXPECT_EQ(writer_thread, std::this_thread::get_id());
    EXPECT_EQ(pixels, Frame(3));
    writer.finish();
}

TEST(FrameWriter, BorrowedFrameUsesOriginalStorageAndCompletesSynchronously) {
    auto pixels = Frame(4);
    const auto caller = std::this_thread::get_id();
    bool consumed = false;
    FrameWriter writer([&](std::span<const std::uint8_t> frame) {
        EXPECT_EQ(frame.data(), pixels.data());
        EXPECT_EQ(frame.size(), pixels.size());
        EXPECT_EQ(std::this_thread::get_id(), caller);
        consumed = true;
    }, false);
    writer.pushBorrowed(pixels);
    EXPECT_TRUE(consumed);
    writer.finish();
}

TEST(FrameWriter, BorrowedFrameCannotEnterAsyncQueueAndPropagatesWriteFailure) {
    auto pixels = Frame(5);
    FrameWriter asynchronous([](std::span<const std::uint8_t>) { FAIL(); }, true);
    EXPECT_THROW(asynchronous.pushBorrowed(pixels), std::logic_error);
    FrameWriter failing([](std::span<const std::uint8_t>) { throw std::runtime_error("closed"); }, false);
    EXPECT_THROW(failing.pushBorrowed(pixels), std::runtime_error);
}

TEST(FrameWriter, BorrowedTokensReturnOnlyAfterConsumptionAndKeepTwoSlotOrder) {
    std::array<std::vector<std::uint8_t>, 2> slots { Frame(0), Frame(0) };
    std::array<bool, 2> held {};
    std::vector<std::uint8_t> written;
    std::atomic<unsigned> consumed { 0 };
    FrameWriter writer([&](std::span<const std::uint8_t> frame) {
        std::this_thread::sleep_for(std::chrono::milliseconds(2));
        written.push_back(frame.front());
        ++consumed;
    }, true);
    FrameWriter::BorrowedFrame token;
    for (unsigned serial = 1; serial <= 8; ++serial) {
        const unsigned slot = (serial - 1) % 2;
        ASSERT_FALSE(held[slot]);
        slots[slot].assign(4096, static_cast<std::uint8_t>(serial));
        held[slot] = true;
        token = { slots[slot], serial, slot };
        writer.pushBorrowed(token);
        if (serial == 1) {
            EXPECT_EQ(token.serial, 0);
        } else {
            EXPECT_EQ(token.serial, serial - 1);
            EXPECT_EQ(token.slot, (serial - 2) % 2);
            EXPECT_GE(consumed.load(), token.serial);
            EXPECT_EQ(token.pixels.data(), slots[token.slot].data());
            held[token.slot] = false;
            token = {};
        }
    }
    writer.finishBorrowed(token);
    EXPECT_EQ(token.serial, 8);
    EXPECT_EQ(consumed.load(), 8);
    held[token.slot] = false;
    EXPECT_FALSE(held[0]);
    EXPECT_FALSE(held[1]);
    EXPECT_EQ(written, (std::vector<std::uint8_t> { 1, 2, 3, 4, 5, 6, 7, 8 }));
}

TEST(FrameWriter, BorrowedTokensRejectStaleSerialAndUnreturnedSlot) {
    auto pixels = Frame(6);
    FrameWriter writer([](std::span<const std::uint8_t>) {}, true);
    FrameWriter::BorrowedFrame first { pixels, 1, 0 };
    writer.pushBorrowed(first);
    FrameWriter::BorrowedFrame stale { pixels, 1, 1 };
    EXPECT_THROW(writer.pushBorrowed(stale), std::logic_error);
    EXPECT_EQ(stale.serial, 1);
    FrameWriter::BorrowedFrame same_slot { pixels, 2, 0 };
    EXPECT_THROW(writer.pushBorrowed(same_slot), std::logic_error);
    EXPECT_EQ(same_slot.serial, 2);
    writer.finishBorrowed(first);
    EXPECT_EQ(first.serial, 1);
    EXPECT_THROW(writer.finishBorrowed(first), std::logic_error);
}

TEST(FrameWriter, BorrowedWriteFailureStopsNewConsumptionWithoutReturningBusyToken) {
    auto pixels = Frame(7);
    int attempts = 0;
    FrameWriter writer([&](std::span<const std::uint8_t>) {
        ++attempts;
        throw std::runtime_error("closed");
    }, true);
    FrameWriter::BorrowedFrame first { pixels, 1, 0 };
    writer.pushBorrowed(first);
    FrameWriter::BorrowedFrame next { pixels, 2, 1 };
    EXPECT_THROW(writer.pushBorrowed(next), std::runtime_error);
    EXPECT_EQ(next.serial, 2);
    EXPECT_EQ(next.slot, 1);
    EXPECT_EQ(attempts, 1);
    EXPECT_THROW(writer.finishBorrowed(first), std::runtime_error);
}
