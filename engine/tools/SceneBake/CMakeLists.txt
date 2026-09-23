# wpe-render（原 lito.toml 的源文件清单与用法，T1 起手写维护）
add_executable(wpe-render)
target_sources(wpe-render PRIVATE
  main.cpp)
target_link_libraries(wpe-render PRIVATE owe-core)
target_link_libraries(wpe-render PRIVATE owe-scene)
target_link_libraries(wpe-render PRIVATE shell32)
target_link_libraries(wpe-render PRIVATE wpe-cli11)
# 源码摘要由 scripts/build-native-cmake.py 经 -DWPE_RENDER_SOURCE_DIGEST 传入
if(WPE_RENDER_SOURCE_DIGEST)
  target_compile_definitions(wpe-render PRIVATE WPE_RENDER_SOURCE_DIGEST="${WPE_RENDER_SOURCE_DIGEST}")
endif()
