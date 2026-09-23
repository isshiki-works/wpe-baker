# owe-vk：wescene.vk 薄层（T4a）。标准 C++20、不链 vvk；VMA 只用声明，实现仍在 vvk（T4b 搬进引擎）。
# Vulkan::Vulkan 在 vvk 的目录作用域里，这里自己找一次（路径来自预设缓存变量）。
find_package(Vulkan REQUIRED)
add_library(owe-vk STATIC)
target_sources(owe-vk PUBLIC FILE_SET CXX_MODULES BASE_DIRS . FILES
  vk/mod.cppm
  vk/Unique.cppm
  vk/Descriptor.cppm)
target_sources(owe-vk PRIVATE
  vk/ToString.cpp)
target_include_directories(owe-vk PUBLIC ${PROJECT_SOURCE_DIR}/src/Vulkan)
target_link_libraries(owe-vk PUBLIC rstd.cppstd Vulkan::Vulkan GPUOpen::VulkanMemoryAllocator)

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
target_link_libraries(owe-vulkan PRIVATE owe-media)
target_link_libraries(owe-vulkan PUBLIC vvk::vvk)
target_link_libraries(owe-vulkan PUBLIC owe-vk)
target_link_libraries(owe-vulkan PRIVATE spirv-reflect)
target_link_libraries(owe-vulkan PUBLIC glslang::glslang glslang::SPIRV glslang::glslang-default-resource-limits)
