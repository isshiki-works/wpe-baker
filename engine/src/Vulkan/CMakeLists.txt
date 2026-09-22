# owe-vulkan（原 lito.toml 的源文件清单与用法，T1 起手写维护）
add_library(owe-vulkan STATIC)
target_sources(owe-vulkan PUBLIC FILE_SET CXX_MODULES BASE_DIRS . FILES
  ShaderCompile.cppm
  Vulkan.cppm)
target_sources(owe-vulkan PRIVATE
  BufferManager.cpp
  Device.cpp
  GraphicsPipeline.cpp
  Instance.cpp
  Parameters.cpp
  Shader.cpp
  Swapchain.cpp
  TextureCache.cpp)
target_include_directories(owe-vulkan PUBLIC ${PROJECT_SOURCE_DIR}/src/Vulkan)
target_link_libraries(owe-vulkan PUBLIC owe-core)
target_link_libraries(owe-vulkan PUBLIC owe-types)
target_link_libraries(owe-vulkan PRIVATE owe-utils)
target_link_libraries(owe-vulkan PUBLIC rstd.std)
target_link_libraries(owe-vulkan PUBLIC rstd.cppstd)
target_link_libraries(owe-vulkan PUBLIC rstd.log)
target_link_libraries(owe-vulkan PRIVATE wavsen)
target_link_libraries(owe-vulkan PUBLIC vvk::vvk)
target_link_libraries(owe-vulkan PRIVATE spirv-reflect)
target_link_libraries(owe-vulkan PUBLIC glslang::glslang glslang::SPIRV glslang::glslang-default-resource-limits)
