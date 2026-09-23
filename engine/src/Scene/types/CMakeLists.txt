# owe-types（原 lito.toml 的源文件清单与用法，T1 起手写维护）
add_library(owe-types STATIC)
target_sources(owe-types PUBLIC FILE_SET CXX_MODULES BASE_DIRS . FILES
  types.cppm)
target_sources(owe-types PRIVATE
  types.cpp)
target_link_libraries(owe-types PUBLIC owe-core)
target_link_libraries(owe-types PUBLIC owe-compat)
