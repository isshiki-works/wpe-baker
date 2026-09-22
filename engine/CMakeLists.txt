cmake_minimum_required(VERSION 3.29)
project(wpe_baker_native VERSION 0.1.0 LANGUAGES C CXX)

if(NOT WIN32 OR NOT CMAKE_CXX_COMPILER_ID STREQUAL "Clang")
  message(FATAL_ERROR "This build entry targets native Windows with Clang")
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
add_compile_definitions(NOMINMAX WIN32_LEAN_AND_MEAN)
add_compile_options("$<$<COMPILE_LANGUAGE:CXX>:-fsized-deallocation>")

get_filename_component(WPE_ROOT "${CMAKE_CURRENT_LIST_DIR}/.." ABSOLUTE)
set(WPE_DEPS "${WPE_ROOT}/.deps")
# Preserve an existing build's pkg-config choice. New build directories use
# the separately built and verified LGPL 2.1 development libraries.
if(DEFINED WAVSEN_AVFORMAT_PREFIX AND NOT "${WAVSEN_AVFORMAT_PREFIX}" STREQUAL "")
  set(_wpe_ffmpeg_default "${WAVSEN_AVFORMAT_PREFIX}")
else()
  set(_wpe_ffmpeg_default "${WPE_DEPS}/ffmpeg-lgpl21/prefix")
endif()
set(WPE_FFMPEG_ROOT "${_wpe_ffmpeg_default}" CACHE PATH "FFmpeg development prefix")
get_filename_component(WPE_FFMPEG_ROOT "${WPE_FFMPEG_ROOT}" ABSOLUTE BASE_DIR "${WPE_ROOT}")
if(DEFINED WAVSEN_AVFORMAT_PREFIX AND NOT "${WAVSEN_AVFORMAT_PREFIX}" STREQUAL "")
  get_filename_component(_wpe_ffmpeg_cached "${WAVSEN_AVFORMAT_PREFIX}" ABSOLUTE)
  string(TOLOWER "${_wpe_ffmpeg_cached}" _wpe_ffmpeg_cached_lower)
  string(TOLOWER "${WPE_FFMPEG_ROOT}" _wpe_ffmpeg_root_lower)
  if(NOT _wpe_ffmpeg_cached_lower STREQUAL _wpe_ffmpeg_root_lower)
    message(FATAL_ERROR "This build already uses FFmpeg ${_wpe_ffmpeg_cached}; use a separate build directory for ${WPE_FFMPEG_ROOT}")
  endif()
endif()
foreach(_wpe_ffmpeg_lib avcodec avformat avutil swscale swresample)
  if(NOT EXISTS "${WPE_FFMPEG_ROOT}/lib/pkgconfig/lib${_wpe_ffmpeg_lib}.pc")
    message(FATAL_ERROR "Incomplete WPE_FFMPEG_ROOT: missing lib${_wpe_ffmpeg_lib}.pc in ${WPE_FFMPEG_ROOT}")
  endif()
endforeach()
set(ENV{PKG_CONFIG_PATH} "${WPE_FFMPEG_ROOT}/lib/pkgconfig;${WPE_DEPS}/install/lib/pkgconfig")
set(ENV{PKG_CONFIG_LIBDIR} "$ENV{PKG_CONFIG_PATH}")
message(STATUS "FFmpeg development prefix: ${WPE_FFMPEG_ROOT}")
set(FETCHCONTENT_FULLY_DISCONNECTED ON CACHE BOOL "Never fetch unpinned dependencies" FORCE)
set(BUILD_TESTING OFF CACHE BOOL "" FORCE)
set(RSTD_BUILD_TESTING OFF CACHE BOOL "" FORCE)
set(RSTD_BUILD_BENCHMARKS OFF CACHE BOOL "" FORCE)
add_subdirectory("${WPE_DEPS}/rstd" dependencies/rstd EXCLUDE_FROM_ALL)
target_link_libraries(rstd.std PRIVATE synchronization)

# rstd's CMake project does not include its Lito-only test support target.
# Native shader and scene-runtime test executables import rstd.test, so expose
# that existing module here without enabling rstd's full test suite.
add_library(rstd.test STATIC)
target_sources(rstd.test
  PUBLIC FILE_SET CXX_MODULES BASE_DIRS "${WPE_DEPS}/rstd/src/test/src" FILES
    "${WPE_DEPS}/rstd/src/test/src/lib.cppm"
    "${WPE_DEPS}/rstd/src/test/src/model.cppm"
    "${WPE_DEPS}/rstd/src/test/src/runner.cppm"
    "${WPE_DEPS}/rstd/src/test/src/temp_dir.cppm"
  PRIVATE "${WPE_DEPS}/rstd/src/test/src/lib.cpp")
target_include_directories(rstd.test PUBLIC "${WPE_DEPS}/rstd/src/test/include")
target_link_libraries(rstd.test PUBLIC rstd.std)

set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
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

set(VMA_BUILD_DOCUMENTATION OFF CACHE BOOL "" FORCE)
set(VMA_BUILD_SAMPLES OFF CACHE BOOL "" FORCE)
set(VMA_ENABLE_INSTALL OFF CACHE BOOL "" FORCE)
add_subdirectory("${WPE_DEPS}/vma" dependencies/vma EXCLUDE_FROM_ALL)
set(VVK_FETCH_MISSING_DEPENDENCIES OFF CACHE BOOL "" FORCE)
add_subdirectory("${WPE_DEPS}/vvk" dependencies/vvk EXCLUDE_FROM_ALL)

set(WAVSEN_AUDIO_BACKEND offline CACHE STRING "Deterministic offline audio" FORCE)
set(WAVSEN_BUILD_TESTS ON CACHE BOOL "Real offline audio/video regression targets" FORCE)
set(GLSLANG_VALIDATOR "$<TARGET_FILE:glslang-standalone>" CACHE STRING "Pinned built shader compiler" FORCE)
add_subdirectory("${WPE_DEPS}/wavsen" dependencies/wavsen EXCLUDE_FROM_ALL)
add_library(wavsen INTERFACE)
target_link_libraries(wavsen INTERFACE wavsen::video wavsen::audio wavsen::decode)

add_library(spirv-reflect STATIC "${WPE_DEPS}/spirv-reflect/spirv_reflect.cpp")
target_include_directories(spirv-reflect PUBLIC "${WPE_DEPS}/spirv-reflect")

add_library(wpe-eigen INTERFACE)
target_include_directories(wpe-eigen INTERFACE "${WPE_DEPS}/eigen")

# Generated solely from the engine's tracked lito manifests; platform dependency
# mapping lives in scripts/build-native-cmake.py and keeps the same source pins.
include("${WPE_ROOT}/.tools/generated/owe-targets.cmake")
