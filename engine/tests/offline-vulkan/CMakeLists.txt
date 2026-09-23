# owe-offline-vulkan-tests（原 lito.toml 的源文件清单与用法，T1 起手写维护）
add_executable(owe-offline-vulkan-test)
target_sources(owe-offline-vulkan-test PRIVATE
  headless_readback.cpp)
target_link_libraries(owe-offline-vulkan-test PRIVATE owe-scene)
target_link_libraries(owe-offline-vulkan-test PRIVATE owe-vulkan)
target_link_libraries(owe-offline-vulkan-test PRIVATE owe-compat)
