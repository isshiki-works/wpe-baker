# owe::compat：rstd 核心类型的标准 C++23 垫片（纯头文件）。T2 集成步之前没有生产目标使用它。
add_library(owe-compat INTERFACE)
target_include_directories(owe-compat INTERFACE "${CMAKE_CURRENT_SOURCE_DIR}/include")
target_compile_features(owe-compat INTERFACE cxx_std_23)
