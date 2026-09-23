#include <gtest/gtest.h>

#include <cstdint>
#include <memory>
#include <utility>
#include <vector>

#include <vulkan/vulkan.h>
#include <vk/Check.hpp>

import wescene.vk;

// wescene.vk 薄层单测：不需要 GPU。Unique 用计数 traits，描述符 arena 注入假的 Vulkan 入口，
// VK_CHECK 的失败处理由本文件提供（只记录、不 panic），好让各形态的 act 可测。

namespace
{

int      g_check_failures = 0;
VkResult g_last_failure   = VK_SUCCESS;

template<typename T>
T Fake(std::uintptr_t value) {
    return reinterpret_cast<T>(value);
}

} // namespace

void owe::vk::OnCheckFailure(VkResult result) noexcept {
    ++g_check_failures;
    g_last_failure = result;
}

namespace
{

// ---- Unique ----

struct CountingTraits {
    using Owner = int;
    static inline std::vector<std::pair<int, VkSampler>> destroyed;
    static void Destroy(int owner, VkSampler handle) noexcept {
        destroyed.emplace_back(owner, handle);
    }
};
using TestUnique = owe::vk::Unique<VkSampler, CountingTraits>;
using Destroyed  = std::vector<std::pair<int, VkSampler>>;

class UniqueTest : public ::testing::Test {
protected:
    void SetUp() override { CountingTraits::destroyed.clear(); }
};

TEST_F(UniqueTest, DestroysOnceWithItsOwner) {
    const auto handle = Fake<VkSampler>(0x10);
    {
        TestUnique unique(7, handle);
        EXPECT_EQ(*unique, handle);
        EXPECT_TRUE(CountingTraits::destroyed.empty());
    }
    EXPECT_EQ(CountingTraits::destroyed, (Destroyed { { 7, handle } }));
}

TEST_F(UniqueTest, EmptyHandleIsNotDestroyed) {
    {
        TestUnique empty;
        TestUnique null_handle(7, VK_NULL_HANDLE);
    }
    EXPECT_TRUE(CountingTraits::destroyed.empty());
}

TEST_F(UniqueTest, MoveConstructionTransfersOwnership) {
    const auto handle = Fake<VkSampler>(0x20);
    {
        TestUnique source(3, handle);
        {
            TestUnique target(std::move(source));
            EXPECT_EQ(*target, handle);
            EXPECT_EQ(*source, VK_NULL_HANDLE);
        }
        EXPECT_EQ(CountingTraits::destroyed, (Destroyed { { 3, handle } }));
    }
    EXPECT_EQ(CountingTraits::destroyed.size(), 1u);
}

TEST_F(UniqueTest, MoveAssignmentDestroysThePreviousHandleFirst) {
    const auto first  = Fake<VkSampler>(0x30);
    const auto second = Fake<VkSampler>(0x40);
    {
        TestUnique source(1, first);
        TestUnique target(2, second);
        target = std::move(source);
        EXPECT_EQ(CountingTraits::destroyed, (Destroyed { { 2, second } }));
        EXPECT_EQ(*target, first);
        EXPECT_EQ(*source, VK_NULL_HANDLE);
    }
    EXPECT_EQ(CountingTraits::destroyed, (Destroyed { { 2, second }, { 1, first } }));
}

TEST_F(UniqueTest, SelfMoveAssignmentKeepsTheHandle) {
    const auto handle = Fake<VkSampler>(0x50);
    {
        TestUnique  unique(4, handle);
        TestUnique& alias = unique;
        unique            = std::move(alias);
        EXPECT_TRUE(CountingTraits::destroyed.empty());
        EXPECT_EQ(*unique, handle);
    }
    EXPECT_EQ(CountingTraits::destroyed, (Destroyed { { 4, handle } }));
}

// ---- 描述符 arena ----

struct FakeVk {
    static inline int                                                creates       = 0;
    static inline int                                                allocs        = 0;
    static inline int                                                updates       = 0;
    static inline VkResult                                           create_result = VK_SUCCESS;
    static inline VkResult                                           alloc_result  = VK_SUCCESS;
    static inline VkDescriptorPoolCreateFlags                        pool_flags    = ~0u;
    static inline std::uint32_t                                      pool_max_sets = 0;
    static inline std::vector<VkDescriptorPoolSize>                  pool_sizes;
    static inline std::vector<std::pair<VkDevice, VkDescriptorPool>> destroyed;
    static inline VkDescriptorPool                                   alloc_pool    = VK_NULL_HANDLE;
    static inline VkDescriptorSetLayout                              alloc_layout  = VK_NULL_HANDLE;
    static inline VkDevice                                           update_device = VK_NULL_HANDLE;
    static inline std::vector<VkWriteDescriptorSet>                  writes;

    static void Reset() {
        creates = allocs = updates = 0;
        create_result = alloc_result = VK_SUCCESS;
        pool_flags                   = ~0u;
        pool_max_sets                = 0;
        pool_sizes.clear();
        destroyed.clear();
        alloc_pool    = VK_NULL_HANDLE;
        alloc_layout  = VK_NULL_HANDLE;
        update_device = VK_NULL_HANDLE;
        writes.clear();
    }

    static VKAPI_ATTR VkResult VKAPI_CALL CreatePool(VkDevice,
                                                     const VkDescriptorPoolCreateInfo* info,
                                                     const VkAllocationCallbacks*,
                                                     VkDescriptorPool* pool) {
        ++creates;
        pool_flags    = info->flags;
        pool_max_sets = info->maxSets;
        pool_sizes.assign(info->pPoolSizes, info->pPoolSizes + info->poolSizeCount);
        if (create_result != VK_SUCCESS) return create_result;
        *pool = Fake<VkDescriptorPool>(0x1000 + std::uintptr_t(creates));
        return VK_SUCCESS;
    }

    static VKAPI_ATTR void VKAPI_CALL DestroyPool(VkDevice device, VkDescriptorPool pool,
                                                  const VkAllocationCallbacks*) {
        destroyed.emplace_back(device, pool);
    }

    static VKAPI_ATTR VkResult VKAPI_CALL AllocateSets(VkDevice,
                                                       const VkDescriptorSetAllocateInfo* info,
                                                       VkDescriptorSet*                   sets) {
        ++allocs;
        alloc_pool   = info->descriptorPool;
        alloc_layout = info->pSetLayouts[0];
        if (alloc_result != VK_SUCCESS) return alloc_result;
        sets[0] = Fake<VkDescriptorSet>(0x2000 + std::uintptr_t(allocs));
        return VK_SUCCESS;
    }

    static VKAPI_ATTR void VKAPI_CALL UpdateSets(VkDevice device, std::uint32_t count,
                                                 const VkWriteDescriptorSet* descriptor_writes,
                                                 std::uint32_t, const VkCopyDescriptorSet*) {
        ++updates;
        update_device = device;
        writes.assign(descriptor_writes, descriptor_writes + count);
    }

    static owe::vk::DescriptorDeviceDispatch Dispatch() {
        return {
            .create_pool   = CreatePool,
            .destroy_pool  = DestroyPool,
            .allocate_sets = AllocateSets,
            .update_sets   = UpdateSets,
        };
    }
};

const VkDevice              kDevice = Fake<VkDevice>(0x99);
const VkDescriptorSetLayout kLayout = Fake<VkDescriptorSetLayout>(0x77);
const VkDescriptorPoolSize  kSizes[] {
    { .type = VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, .descriptorCount = 8192 },
    { .type = VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER, .descriptorCount = 2048 },
};

auto CreateArena() -> std::shared_ptr<owe::vk::DescriptorArenaGeneration> {
    auto created =
        owe::vk::DescriptorArenaGeneration::Create(kDevice, 2048, kSizes, FakeVk::Dispatch());
    EXPECT_TRUE(created.created());
    return created.arena;
}

class DescriptorArenaTest : public ::testing::Test {
protected:
    void SetUp() override { FakeVk::Reset(); }
};

TEST_F(DescriptorArenaTest, CreatesThePoolWithoutFreeDescriptorSetBit) {
    auto arena = CreateArena();
    EXPECT_EQ(FakeVk::creates, 1);
    EXPECT_EQ(FakeVk::pool_flags, 0u);
    EXPECT_EQ(FakeVk::pool_max_sets, 2048u);
    ASSERT_EQ(FakeVk::pool_sizes.size(), 2u);
    EXPECT_EQ(FakeVk::pool_sizes[0].type, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER);
    EXPECT_EQ(FakeVk::pool_sizes[0].descriptorCount, 8192u);
    EXPECT_EQ(FakeVk::pool_sizes[1].type, VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER);
    EXPECT_EQ(FakeVk::pool_sizes[1].descriptorCount, 2048u);
}

TEST_F(DescriptorArenaTest, CreateFailureReportsTheVulkanResult) {
    FakeVk::create_result = VK_ERROR_OUT_OF_DEVICE_MEMORY;
    auto created =
        owe::vk::DescriptorArenaGeneration::Create(kDevice, 2048, kSizes, FakeVk::Dispatch());
    EXPECT_FALSE(created.created());
    EXPECT_EQ(created.api_result, VK_ERROR_OUT_OF_DEVICE_MEMORY);
    EXPECT_TRUE(FakeVk::destroyed.empty());
}

TEST_F(DescriptorArenaTest, PoolIsDestroyedOnceAfterTheLastLease) {
    auto arena = CreateArena();
    auto first = owe::vk::DescriptorArenaGeneration::Allocate(arena, kLayout);
    ASSERT_TRUE(first.allocated());
    EXPECT_EQ(FakeVk::alloc_pool, Fake<VkDescriptorPool>(0x1001));
    EXPECT_EQ(FakeVk::alloc_layout, kLayout);
    auto second = owe::vk::DescriptorArenaGeneration::Allocate(arena, kLayout);
    ASSERT_TRUE(second.allocated());
    EXPECT_NE(first.lease.handle, second.lease.handle);

    arena.reset();
    first = {};
    EXPECT_TRUE(FakeVk::destroyed.empty());
    second = {};
    ASSERT_EQ(FakeVk::destroyed.size(), 1u);
    EXPECT_EQ(FakeVk::destroyed[0].first, kDevice);
    EXPECT_EQ(FakeVk::destroyed[0].second, Fake<VkDescriptorPool>(0x1001));
}

TEST_F(DescriptorArenaTest, AllocationFailureReportsTheVulkanResult) {
    auto arena           = CreateArena();
    FakeVk::alloc_result = VK_ERROR_OUT_OF_POOL_MEMORY;
    auto allocated       = owe::vk::DescriptorArenaGeneration::Allocate(arena, kLayout);
    EXPECT_FALSE(allocated.allocated());
    EXPECT_EQ(allocated.api_result, VK_ERROR_OUT_OF_POOL_MEMORY);
    EXPECT_FALSE(allocated.lease.valid());
}

TEST_F(DescriptorArenaTest, BatchWritesImagesThenBuffersInOneUpdate) {
    auto arena = CreateArena();
    auto lease = owe::vk::DescriptorArenaGeneration::Allocate(arena, kLayout).lease;

    const VkDescriptorBufferInfo buffer { .buffer = Fake<VkBuffer>(0x5),
                                          .offset = 16,
                                          .range  = 64 };
    const VkDescriptorImageInfo  image {
        .sampler     = Fake<VkSampler>(0x6),
        .imageView   = Fake<VkImageView>(0x7),
        .imageLayout = VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL,
    };
    owe::vk::DescriptorUpdateBatch batch;
    ASSERT_TRUE(batch.WriteBuffer(lease, 3, VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER, { &buffer, 1 }));
    ASSERT_TRUE(
        batch.WriteImage(lease, 1, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, { &image, 1 }));
    ASSERT_TRUE(batch.Commit());

    EXPECT_EQ(FakeVk::updates, 1);
    EXPECT_EQ(FakeVk::update_device, kDevice);
    ASSERT_EQ(FakeVk::writes.size(), 2u);
    EXPECT_EQ(FakeVk::writes[0].dstSet, lease.handle);
    EXPECT_EQ(FakeVk::writes[0].dstBinding, 1u);
    EXPECT_EQ(FakeVk::writes[0].dstArrayElement, 0u);
    EXPECT_EQ(FakeVk::writes[0].descriptorCount, 1u);
    EXPECT_EQ(FakeVk::writes[0].descriptorType, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER);
    EXPECT_EQ(FakeVk::writes[0].pBufferInfo, nullptr);
    EXPECT_EQ(FakeVk::writes[1].dstBinding, 3u);
    EXPECT_EQ(FakeVk::writes[1].descriptorType, VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER);
    EXPECT_EQ(FakeVk::writes[1].pImageInfo, nullptr);

    // 已提交的批次不再接受写入，也不再提交。
    EXPECT_FALSE(
        batch.WriteImage(lease, 2, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, { &image, 1 }));
    EXPECT_FALSE(batch.Commit());
    EXPECT_EQ(FakeVk::updates, 1);
}

TEST_F(DescriptorArenaTest, BatchRejectsUnallocatedLeasesAndEmptyWrites) {
    auto arena = CreateArena();
    auto lease = owe::vk::DescriptorArenaGeneration::Allocate(arena, kLayout).lease;
    const VkDescriptorImageInfo image {};

    owe::vk::DescriptorUpdateBatch batch;
    EXPECT_FALSE(batch.WriteImage({}, 0, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, { &image, 1 }));
    EXPECT_FALSE(batch.WriteImage(lease, 0, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, {}));
    EXPECT_TRUE(batch.Commit());
    EXPECT_EQ(FakeVk::updates, 0);
}

// ---- VK_CHECK ----

class VkCheckTest : public ::testing::Test {
protected:
    void SetUp() override {
        g_check_failures = 0;
        g_last_failure   = VK_SUCCESS;
    }
};

bool CheckPlain(VkResult result, bool& reached_end) {
    VK_CHECK(result);
    reached_end = true;
    return true;
}

bool CheckBool(VkResult result, bool& reached_end) {
    VK_CHECK_BOOL_RE(result);
    reached_end = true;
    return true;
}

void CheckVoid(VkResult result, bool& reached_end) {
    VK_CHECK_VOID_RE(result);
    reached_end = true;
}

VkResult CheckResult(VkResult result, bool& reached_end) {
    VK_CHECK_RE(result);
    reached_end = true;
    return VK_SUCCESS;
}

int CheckAct(VkResult result) {
    int acted = 0;
    VK_CHECK_ACT(acted = 1, result);
    return acted;
}

TEST_F(VkCheckTest, SuccessAndSuboptimalPassThrough) {
    for (auto result : { VK_SUCCESS, VK_SUBOPTIMAL_KHR }) {
        bool reached = false;
        EXPECT_TRUE(CheckPlain(result, reached));
        EXPECT_TRUE(reached);
        reached = false;
        EXPECT_TRUE(CheckBool(result, reached));
        EXPECT_TRUE(reached);
        reached = false;
        CheckVoid(result, reached);
        EXPECT_TRUE(reached);
        reached = false;
        EXPECT_EQ(CheckResult(result, reached), VK_SUCCESS);
        EXPECT_TRUE(reached);
        EXPECT_EQ(CheckAct(result), 0);
    }
    EXPECT_EQ(g_check_failures, 0);
}

TEST_F(VkCheckTest, FailureReportsThenRunsTheAction) {
    bool reached = false;
    EXPECT_TRUE(CheckPlain(VK_ERROR_DEVICE_LOST, reached));
    EXPECT_TRUE(reached);
    EXPECT_EQ(g_check_failures, 1);
    EXPECT_EQ(g_last_failure, VK_ERROR_DEVICE_LOST);

    reached = false;
    EXPECT_FALSE(CheckBool(VK_ERROR_OUT_OF_HOST_MEMORY, reached));
    EXPECT_FALSE(reached);
    EXPECT_EQ(g_last_failure, VK_ERROR_OUT_OF_HOST_MEMORY);

    reached = false;
    CheckVoid(VK_TIMEOUT, reached);
    EXPECT_FALSE(reached);
    EXPECT_EQ(g_last_failure, VK_TIMEOUT);

    reached = false;
    EXPECT_EQ(CheckResult(VK_ERROR_OUT_OF_POOL_MEMORY, reached), VK_ERROR_OUT_OF_POOL_MEMORY);
    EXPECT_FALSE(reached);

    EXPECT_EQ(CheckAct(VK_ERROR_INITIALIZATION_FAILED), 1);
    EXPECT_EQ(g_last_failure, VK_ERROR_INITIALIZATION_FAILED);
    EXPECT_EQ(g_check_failures, 5);
}

TEST_F(VkCheckTest, EvaluatesTheExpressionOnce) {
    int  calls = 0;
    auto call  = [&] {
        ++calls;
        return VK_ERROR_DEVICE_LOST;
    };
    VK_CHECK(call());
    EXPECT_EQ(calls, 1);
    EXPECT_EQ(g_check_failures, 1);
}

// ---- ToString（文字逐字来自 vvk，抽查） ----

TEST(VkToString, ResultNames) {
    EXPECT_STREQ(owe::vk::ToString(VK_SUCCESS), "VK_SUCCESS");
    EXPECT_STREQ(owe::vk::ToString(VK_SUBOPTIMAL_KHR), "VK_SUBOPTIMAL_KHR");
    EXPECT_STREQ(owe::vk::ToString(VK_ERROR_DEVICE_LOST), "VK_ERROR_DEVICE_LOST");
    EXPECT_STREQ(owe::vk::ToString(VK_ERROR_OUT_OF_POOL_MEMORY), "VK_ERROR_OUT_OF_POOL_MEMORY");
    EXPECT_STREQ(owe::vk::ToString(static_cast<VkResult>(-12345)), "VK_RESULT_UNKNOWN");
}

TEST(VkToString, FormatAndColorSpaceKeepVvkText) {
    // vvk 这两张表把 ## 写在字符串里，已知条目返回的就是这串原文（已登记为旧 bug，文字不改）。
    EXPECT_STREQ(owe::vk::ToString(VK_FORMAT_R8_UNORM), "VK_FORMAT_##str");
    EXPECT_STREQ(owe::vk::ToString(static_cast<VkFormat>(123456)), "VK_FORMAT_UNKNOWN");
    EXPECT_STREQ(owe::vk::ToString(VK_COLOR_SPACE_SRGB_NONLINEAR_KHR), "VK_##str");
    EXPECT_STREQ(owe::vk::ToString(static_cast<VkColorSpaceKHR>(123456)), "VK_COLOR_SPACE_UNKNOWN");
}

} // namespace
