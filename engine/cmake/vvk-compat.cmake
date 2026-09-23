# vvk 改用 owe::compat 编译（T2-6）。
# vvk 的接口里直接用 rstd 类型（slice/Vec/Option/Arc/usize），引擎调用方已换成 compat，
# 两边必须是同一套类型。vvk 在 T4 整体换掉，这里不改 .deps 里的源码，而是在配置时把
# 源码复制到构建目录并做与引擎相同的机械替换：删 `import rstd*;`，在全局模块片段里包含
# <owe/compat.hpp> 与 <owe/std.hpp>。目标名、源文件清单与编译选项照抄 .deps/vvk/CMakeLists.txt。
set(_vvk_src "${WPE_DEPS}/vvk")
set(_vvk_gen "${CMAKE_BINARY_DIR}/vvk-compat")
set(_vvk_files
  src/lib.cpp src/ffi/vma/mod.cpp
  src/lib.cppm src/handle.cppm src/vma.cppm src/dispatch.cppm src/objects.cppm
  src/completion.cppm src/descriptor.cppm
  src/ffi/vulkan/mod.cppm src/ffi/vulkan/constants.cppm src/ffi/vulkan/types.cppm
  src/ffi/vulkan/functions.cppm src/ffi/vma/mod.cppm include/vvk/macros.hpp)
set(_vvk_inc "#include <owe/compat.hpp>\n#include <owe/std.hpp>\n")
foreach(_f IN LISTS _vvk_files)
  file(READ "${_vvk_src}/${_f}" _text)
  string(REGEX MATCH "(^|\n)[ \t]*import[ \t]+rstd[^;\n]*;|#include <rstd/" _uses "${_text}")
  if(_f MATCHES "[.]hpp$")
    string(REPLACE "#include <rstd/macro.hpp>" "#include <owe/compat.hpp>" _text "${_text}")
  elseif(_uses)
    string(REGEX REPLACE "(^|\n)[ \t]*import[ \t]+rstd(\\.[a-z_]+)?[ \t]*;" "" _text "${_text}")
    string(REGEX REPLACE "#include <rstd/[^>\n]*>\n" "" _text "${_text}")
    string(REGEX MATCH "(^|\n)module;\n" _gmf "${_text}")
    if(_gmf)
      string(REGEX REPLACE "(^|\n)module;\n" "\\1module;\n${_vvk_inc}" _text "${_text}")
    else()
      set(_text "module;\n${_vvk_inc}${_text}")
    endif()
  endif()
  set(_out "${_vvk_gen}/${_f}")
  set(_old "")
  if(EXISTS "${_out}")
    file(READ "${_out}" _old)
  endif()
  if(NOT _old STREQUAL _text)
    file(WRITE "${_out}" "${_text}")
  endif()
  set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS "${_vvk_src}/${_f}")
endforeach()

find_package(Vulkan REQUIRED)
add_library(vvk STATIC "${_vvk_gen}/src/lib.cpp" "${_vvk_gen}/src/ffi/vma/mod.cpp")
add_library(vvk::vvk ALIAS vvk)
target_compile_options(vvk PRIVATE -Wall -Wextra -Wpedantic -Wno-missing-field-initializers)
target_sources(vvk
  PUBLIC FILE_SET CXX_MODULES BASE_DIRS "${_vvk_gen}" FILES
    "${_vvk_gen}/src/lib.cppm"
    "${_vvk_gen}/src/handle.cppm"
    "${_vvk_gen}/src/vma.cppm"
    "${_vvk_gen}/src/dispatch.cppm"
    "${_vvk_gen}/src/objects.cppm"
    "${_vvk_gen}/src/completion.cppm"
    "${_vvk_gen}/src/descriptor.cppm"
    "${_vvk_gen}/src/ffi/vulkan/mod.cppm"
    "${_vvk_gen}/src/ffi/vulkan/constants.cppm"
    "${_vvk_gen}/src/ffi/vulkan/types.cppm"
    "${_vvk_gen}/src/ffi/vulkan/functions.cppm"
    "${_vvk_gen}/src/ffi/vma/mod.cppm"
  PUBLIC FILE_SET public_headers TYPE HEADERS BASE_DIRS "${_vvk_gen}/include" FILES "${_vvk_gen}/include/vvk/macros.hpp")
target_link_libraries(vvk PUBLIC owe-compat Vulkan::Vulkan GPUOpen::VulkanMemoryAllocator)
set_target_properties(vvk PROPERTIES CXX_EXTENSIONS OFF CXX_SCAN_FOR_MODULES ON POSITION_INDEPENDENT_CODE ON)
