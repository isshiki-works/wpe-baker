# owe-audio（原 lito.toml 的源文件清单与用法，T1 起手写维护）
add_library(owe-audio STATIC)
target_sources(owe-audio PUBLIC FILE_SET CXX_MODULES BASE_DIRS . FILES
  AudioResponse.cppm)
target_sources(owe-audio PRIVATE
  AudioResponse.cpp)
target_link_libraries(owe-audio PUBLIC owe-core)
target_link_libraries(owe-audio PUBLIC owe-compat)
