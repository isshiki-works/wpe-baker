#include <gtest/gtest.h>
#include <new>

import rstd;
import rstd.cppstd;
import wescene.core;

TEST(DependencyTrace, OnlyAnUnrecordedEdgeBeyondTheCapMakesEvidenceIncomplete) {
    owe::Services services;
    services.trace_scene = true;
    auto edge = [](std::int32_t owner) {
        return owe::OfflineDependency { owner, 1, "read", "origin", "binding", false };
    };
    for (std::int32_t owner = 0; owner < 10000; ++owner) services.trace(edge(owner));
    ASSERT_EQ(services.dependencies.size(), 10000u);
    ASSERT_TRUE(services.dependencies_complete);

    services.trace(edge(0));
    services.trace(edge(9999));
    EXPECT_TRUE(services.dependencies_complete);
    EXPECT_TRUE(services.diagnostics.empty());

    services.trace(edge(10000));
    EXPECT_FALSE(services.dependencies_complete);
    EXPECT_FALSE(services.failed); // Rendering can succeed while its trace is incomplete.
    EXPECT_EQ(services.dependencies.size(), 10000u);
    EXPECT_EQ(services.dependency_keys.size(), 10000u);
    ASSERT_EQ(services.diagnostics.size(), 1u);
    services.trace(edge(0));
    services.trace(edge(10000));
    services.trace(edge(10001));
    EXPECT_FALSE(services.dependencies_complete);
    EXPECT_EQ(services.dependencies.size(), 10000u);
    EXPECT_EQ(services.dependency_keys.size(), 10000u);
    EXPECT_EQ(services.diagnostics.size(), 1u);
}
