# CEF target and runtime integration.

if(NOT DEFINED LITO_CMAKE_DEPENDENCY_MODE)
    message(FATAL_ERROR "weweb: LITO_CMAKE_DEPENDENCY_MODE is unset")
endif()

set(_weweb_cef_source FALSE)
if(LITO_CMAKE_DEPENDENCY_MODE STREQUAL "source")
    if(NOT DEFINED LITO_CMAKE_DEPENDENCY_SOURCE_DIR)
        message(FATAL_ERROR "weweb: LITO_CMAKE_DEPENDENCY_SOURCE_DIR is unset")
    endif()
    set(_weweb_cef_source TRUE)
    set(CEF_ROOT "${LITO_CMAKE_DEPENDENCY_SOURCE_DIR}"
        CACHE PATH "CEF binary distribution root" FORCE)
    list(APPEND CMAKE_MODULE_PATH "${LITO_CMAKE_DEPENDENCY_SOURCE_DIR}/cmake")
elseif(NOT LITO_CMAKE_DEPENDENCY_MODE STREQUAL "find")
    message(FATAL_ERROR
        "weweb: unsupported CMake dependency mode '${LITO_CMAKE_DEPENDENCY_MODE}'")
endif()

if(NOT DEFINED PROJECT_ARCH)
    string(TOLOWER "${CMAKE_SYSTEM_PROCESSOR}" _weweb_system_processor)
    if(_weweb_system_processor MATCHES "^(aarch64|arm64)$")
        set(PROJECT_ARCH arm64)
    elseif(_weweb_system_processor MATCHES "^(x86_64|amd64)$")
        set(PROJECT_ARCH x86_64)
    endif()
endif()

find_package(CEF REQUIRED)

if(NOT _weweb_cef_source)
    if(NOT TARGET CEF::Library)
        message(FATAL_ERROR "weweb: installed CEF package does not provide CEF::Library")
    endif()
    if(NOT TARGET libcef_lib)
        add_library(libcef_lib INTERFACE)
        target_link_libraries(libcef_lib INTERFACE CEF::Library)
    endif()
    if(NOT TARGET libcef_dll_wrapper)
        if(NOT TARGET CEF::Wrapper)
            message(FATAL_ERROR "weweb: installed CEF package does not provide CEF::Wrapper")
        endif()
        add_library(libcef_dll_wrapper INTERFACE)
        target_link_libraries(libcef_dll_wrapper INTERFACE CEF::Wrapper)
    endif()
    if(CMAKE_CXX_COMPILER_ID MATCHES "Clang" AND TARGET libcef_dll_wrapper)
        get_target_property(_weweb_cef_wrapper_imported libcef_dll_wrapper IMPORTED)
        get_target_property(_weweb_cef_wrapper_type libcef_dll_wrapper TYPE)
        if(NOT _weweb_cef_wrapper_imported AND
           NOT _weweb_cef_wrapper_type STREQUAL "INTERFACE_LIBRARY")
            target_compile_options(libcef_dll_wrapper PRIVATE -Wno-undefined-var-template)
        endif()
    endif()
    if(COMMAND lito_export_asset_set)
        lito_export_asset_set(NAME runtime PROVIDED)
    endif()
    return()
endif()

# Minimal distributions only contain the Release binary directory.
set(CEF_BINARY_DIR "${CEF_BINARY_DIR_RELEASE}")
set(CEF_LIB_DEBUG  "${CEF_LIB_RELEASE}")

set_property(GLOBAL PROPERTY WEWEB_CEF_BINARY_DIR "${CEF_BINARY_DIR}")
set_property(GLOBAL PROPERTY WEWEB_CEF_BINARY_FILES "${CEF_BINARY_FILES}")
set_property(GLOBAL PROPERTY WEWEB_CEF_RESOURCE_DIR "${CEF_RESOURCE_DIR}")
set_property(GLOBAL PROPERTY WEWEB_CEF_RESOURCE_FILES "${CEF_RESOURCE_FILES}")

if(COMMAND lito_export_asset_set)
    lito_export_asset_set(
        NAME runtime
        ROOT "${CEF_BINARY_DIR}"
        FILES ${CEF_BINARY_FILES})

    set(_weweb_cef_resource_assets)
    foreach(_resource IN LISTS CEF_RESOURCE_FILES)
        if(_resource STREQUAL "locales")
            file(GLOB _weweb_cef_locales
                RELATIVE "${CEF_RESOURCE_DIR}"
                "${CEF_RESOURCE_DIR}/locales/*.pak")
            list(APPEND _weweb_cef_resource_assets ${_weweb_cef_locales})
        else()
            list(APPEND _weweb_cef_resource_assets "${_resource}")
        endif()
    endforeach()
    lito_export_asset_set(
        NAME runtime
        ROOT "${CEF_RESOURCE_DIR}"
        FILES ${_weweb_cef_resource_assets})
endif()

if(NOT TARGET libcef_dll_wrapper)
    add_subdirectory(
        "${CEF_LIBCEF_DLL_WRAPPER_PATH}"
        "${CMAKE_CURRENT_BINARY_DIR}/libcef_dll_wrapper"
        EXCLUDE_FROM_ALL)
endif()

if(NOT TARGET libcef_lib)
    ADD_LOGICAL_TARGET("libcef_lib" "${CEF_LIB_DEBUG}" "${CEF_LIB_RELEASE}")
endif()

target_compile_definitions(libcef_lib INTERFACE
    ${CEF_COMPILER_DEFINES}
    $<$<CONFIG:Debug>:${CEF_COMPILER_DEFINES_DEBUG}>
    $<$<CONFIG:Release>:${CEF_COMPILER_DEFINES_RELEASE}>)
target_include_directories(libcef_lib SYSTEM INTERFACE ${CEF_INCLUDE_PATH})
target_link_libraries(libcef_lib INTERFACE ${CEF_STANDARD_LIBS})

function(weweb_get_cef_runtime out_binary_dir out_binary_files out_resource_dir out_resource_files)
    get_property(_binary_dir GLOBAL PROPERTY WEWEB_CEF_BINARY_DIR)
    get_property(_binary_files GLOBAL PROPERTY WEWEB_CEF_BINARY_FILES)
    get_property(_resource_dir GLOBAL PROPERTY WEWEB_CEF_RESOURCE_DIR)
    get_property(_resource_files GLOBAL PROPERTY WEWEB_CEF_RESOURCE_FILES)
    set(${out_binary_dir} "${_binary_dir}" PARENT_SCOPE)
    set(${out_binary_files} "${_binary_files}" PARENT_SCOPE)
    set(${out_resource_dir} "${_resource_dir}" PARENT_SCOPE)
    set(${out_resource_files} "${_resource_files}" PARENT_SCOPE)
endfunction()

function(weweb_stage_cef_runtime target)
    weweb_get_cef_runtime(_binary_dir _binary_files _resource_dir _resource_files)
    COPY_FILES(${target} "${_binary_files}"   "${_binary_dir}"   "$<TARGET_FILE_DIR:${target}>")
    COPY_FILES(${target} "${_resource_files}" "${_resource_dir}" "$<TARGET_FILE_DIR:${target}>")
endfunction()
