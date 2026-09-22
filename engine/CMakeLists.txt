cmake_minimum_required(VERSION 3.29)
project(wpe_baker_native VERSION 0.1.0 LANGUAGES C CXX)

# rstd 与本仓库的 C++20 模块只在 Clang 上编过（rstd 的 source_location 直接 #error），
# 所以只查编译器；平台相关的系统库（dwrite、shell32、synchronization）由各包自己声明。
if(NOT CMAKE_CXX_COMPILER_ID STREQUAL "Clang")
  message(FATAL_ERROR "engine 需要 Clang 编译（当前 ${CMAKE_CXX_COMPILER_ID}）")
endif()
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_CXX_SCAN_FOR_MODULES ON)
set(CMAKE_EXPORT_COMPILE_COMMANDS ON)
set(CMAKE_RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")
set(CMAKE_ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/lib")
set(CMAKE_LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/lib")
set(CMAKE_LINKER_TYPE LLD)

# 全部目标（含第三方）共用的编译选项，只在这里写：
# - 优化级别取 CMake Release 默认的 -O3 -DNDEBUG，不加 -march，x86-64 基线没有 FMA；
# - -ffp-contract=off 写死：以后换编译器或加 -march 时也不会把乘加合并成 FMA，像素结果逐字节不变；
# - 异常与 RTTI 保持 Clang 默认开启（rstd 上游 lito profile 要求关掉，但现有渲染器一直带着它们编，
#   这一步不改，避免动对照行为）。
add_compile_definitions(NOMINMAX WIN32_LEAN_AND_MEAN)
add_compile_options("$<$<COMPILE_LANGUAGE:CXX>:-fsized-deallocation>" -ffp-contract=off)
# PE 头不写时间戳：同一源码两次构建逐字节相同。
add_link_options(LINKER:--no-insert-timestamp)

get_filename_component(WPE_ROOT "${CMAKE_CURRENT_LIST_DIR}/.." ABSOLUTE)
set(WPE_DEPS "${WPE_ROOT}/.deps")
set(FETCHCONTENT_FULLY_DISCONNECTED ON CACHE BOOL "Never fetch unpinned dependencies" FORCE)
set(BUILD_TESTING OFF CACHE BOOL "" FORCE)
set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)

# 不含模块的第三方库（lz4/freetype/quickjs/glslang/vma/spirv-reflect/googletest）：
# WPE_THIRD_PARTY_DIR 指向按锁哈希预构建的目录时直接导入；否则在本构建里编。
# WPE_THIRD_PARTY_ONLY=ON 只编这些库并导出导入文件，供 scripts/build-native-cmake.py 生成预构建目录。
set(WPE_THIRD_PARTY_DIR "" CACHE PATH "预构建第三方库目录（空 = 在本构建内编译）")
option(WPE_THIRD_PARTY_ONLY "只构建并导出第三方库" OFF)
set(WPE_THIRD_PARTY_TARGETS lz4_static freetype qjs glslang SPIRV glslang-default-resource-limits
    OSDependent MachineIndependent GenericCodeGen glslang-standalone VulkanMemoryAllocator spirv-reflect
    gtest gtest_main)
if(WPE_THIRD_PARTY_DIR)
  include("${WPE_THIRD_PARTY_DIR}/wpe-third-party.cmake")
  # 导入目标默认按 -isystem 引头文件，会改变头文件搜索顺序；保持与树内编译相同的 -I。
  set_target_properties(${WPE_THIRD_PARTY_TARGETS} PROPERTIES SYSTEM OFF)
  add_library(glslang::glslang ALIAS glslang)
  add_library(glslang::SPIRV ALIAS SPIRV)
  add_library(glslang::glslang-default-resource-limits ALIAS glslang-default-resource-limits)
  add_library(GPUOpen::VulkanMemoryAllocator ALIAS VulkanMemoryAllocator)
  set(GLSLANG_VALIDATOR "$<TARGET_FILE:glslang-standalone>" CACHE STRING "Pinned built shader compiler" FORCE)
else()
  set(LZ4_BUILD_CLI OFF CACHE BOOL "" FORCE)
  add_subdirectory("${WPE_DEPS}/lz4/build/cmake" dependencies/lz4 EXCLUDE_FROM_ALL)

  set(FT_DISABLE_ZLIB ON CACHE BOOL "" FORCE)
  set(FT_DISABLE_BZIP2 ON CACHE BOOL "" FORCE)
  set(FT_DISABLE_PNG ON CACHE BOOL "" FORCE)
  set(FT_DISABLE_HARFBUZZ ON CACHE BOOL "" FORCE)
  set(FT_DISABLE_BROTLI ON CACHE BOOL "" FORCE)
  add_subdirectory("${WPE_DEPS}/freetype" dependencies/freetype EXCLUDE_FROM_ALL)

  set(QJS_BUILD_EXAMPLES OFF CACHE BOOL "" FORCE)
  set(QJS_BUILD_CLI_STATIC OFF CACHE BOOL "" FORCE)
  set(QJS_BUILD_CLI_WITH_MIMALLOC OFF CACHE BOOL "" FORCE)
  set(QJS_ENABLE_INSTALL OFF CACHE BOOL "" FORCE)
  add_subdirectory("${WPE_DEPS}/quickjs" dependencies/quickjs EXCLUDE_FROM_ALL)

  set(ENABLE_GLSLANG_BINARIES ON CACHE BOOL "" FORCE)
  set(ENABLE_HLSL ON CACHE BOOL "" FORCE)
  set(ENABLE_OPT OFF CACHE BOOL "" FORCE)
  set(ENABLE_CTEST OFF CACHE BOOL "" FORCE)
  set(BUILD_EXTERNAL OFF CACHE BOOL "" FORCE)
  set(GLSLANG_TESTS OFF CACHE BOOL "" FORCE)
  set(GLSLANG_ENABLE_INSTALL OFF CACHE BOOL "" FORCE)
  add_subdirectory("${WPE_DEPS}/glslang" dependencies/glslang EXCLUDE_FROM_ALL)
  set(GLSLANG_VALIDATOR "$<TARGET_FILE:glslang-standalone>" CACHE STRING "Pinned built shader compiler" FORCE)

  set(VMA_BUILD_DOCUMENTATION OFF CACHE BOOL "" FORCE)
  set(VMA_BUILD_SAMPLES OFF CACHE BOOL "" FORCE)
  set(VMA_ENABLE_INSTALL OFF CACHE BOOL "" FORCE)
  add_subdirectory("${WPE_DEPS}/vma" dependencies/vma EXCLUDE_FROM_ALL)

  add_library(spirv-reflect STATIC "${WPE_DEPS}/spirv-reflect/spirv_reflect.cpp")
  target_include_directories(spirv-reflect PUBLIC "${WPE_DEPS}/spirv-reflect")

  # 只给单测用；不编 gmock，不走 pthread（MinGW 上用 Win32 原生同步）。
  set(BUILD_GMOCK OFF CACHE BOOL "" FORCE)
  set(INSTALL_GTEST OFF CACHE BOOL "" FORCE)
  set(gtest_disable_pthreads ON CACHE BOOL "" FORCE)
  add_subdirectory("${WPE_DEPS}/googletest" dependencies/googletest EXCLUDE_FROM_ALL)

  if(WPE_THIRD_PARTY_ONLY)
    add_custom_target(wpe-third-party ALL DEPENDS ${WPE_THIRD_PARTY_TARGETS})
    export(TARGETS ${WPE_THIRD_PARTY_TARGETS} FILE "${CMAKE_BINARY_DIR}/wpe-third-party.cmake")
    return()
  endif()
endif()

# FFmpeg 开发前缀（wavsen 的解码后端经 pkg-config 找它）。
set(WPE_FFMPEG_ROOT "${WPE_DEPS}/ffmpeg-lgpl21/prefix" CACHE PATH "FFmpeg development prefix")
get_filename_component(WPE_FFMPEG_ROOT "${WPE_FFMPEG_ROOT}" ABSOLUTE BASE_DIR "${WPE_ROOT}")
foreach(_wpe_ffmpeg_lib avcodec avformat avutil swscale swresample)
  if(NOT EXISTS "${WPE_FFMPEG_ROOT}/lib/pkgconfig/lib${_wpe_ffmpeg_lib}.pc")
    message(FATAL_ERROR "Incomplete WPE_FFMPEG_ROOT: missing lib${_wpe_ffmpeg_lib}.pc in ${WPE_FFMPEG_ROOT}")
  endif()
endforeach()
set(ENV{PKG_CONFIG_PATH} "${WPE_FFMPEG_ROOT}/lib/pkgconfig;${WPE_DEPS}/install/lib/pkgconfig")
set(ENV{PKG_CONFIG_LIBDIR} "$ENV{PKG_CONFIG_PATH}")
message(STATUS "FFmpeg development prefix: ${WPE_FFMPEG_ROOT}")

# 带模块的依赖（rstd/vvk/wavsen）的 BMI 与编译器、选项绑定，留在主构建里编。
set(RSTD_BUILD_TESTING OFF CACHE BOOL "" FORCE)
set(RSTD_BUILD_BENCHMARKS OFF CACHE BOOL "" FORCE)
add_subdirectory("${WPE_DEPS}/rstd" dependencies/rstd EXCLUDE_FROM_ALL)
target_link_libraries(rstd.std PRIVATE synchronization)

set(VVK_FETCH_MISSING_DEPENDENCIES OFF CACHE BOOL "" FORCE)
add_subdirectory("${WPE_DEPS}/vvk" dependencies/vvk EXCLUDE_FROM_ALL)

set(WAVSEN_AUDIO_BACKEND offline CACHE STRING "Deterministic offline audio" FORCE)
set(WAVSEN_BUILD_TESTS ON CACHE BOOL "Real offline audio/video regression targets" FORCE)
add_subdirectory("${WPE_DEPS}/wavsen" dependencies/wavsen EXCLUDE_FROM_ALL)
add_library(wavsen INTERFACE)
target_link_libraries(wavsen INTERFACE wavsen::video wavsen::audio wavsen::decode)

add_library(wpe-eigen INTERFACE)
target_include_directories(wpe-eigen INTERFACE "${WPE_DEPS}/eigen")

# nlohmann/json 3.12（header-only）：取代 rstd::json 的解析与写出（T2-3）。
add_library(nlohmann-json INTERFACE)
target_include_directories(nlohmann-json INTERFACE "${WPE_DEPS}/nlohmann-json/single_include")

# CLI11 单头文件（wpe-render 的命令行）。
add_library(wpe-cli11 INTERFACE)
target_include_directories(wpe-cli11 INTERFACE "${WPE_DEPS}/cli11")

# 各包的源文件清单与用法（原 lito.toml）。
add_subdirectory(src/Core)
add_subdirectory(src/Audio)
add_subdirectory(src/Scene/types)
add_subdirectory(src/Scene/Utils)
add_subdirectory(src/Vulkan)
add_subdirectory(src/Scene)
add_subdirectory(tools/SceneBake)
add_subdirectory(tests/offline-vulkan)
add_subdirectory(tests)
