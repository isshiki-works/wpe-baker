# owe-utils（原 lito.toml 的源文件清单与用法，T1 起手写维护）
add_library(owe-utils STATIC)
target_sources(owe-utils PUBLIC FILE_SET CXX_MODULES BASE_DIRS ../../.. FILES
  Utils.cppm)
target_sources(owe-utils PRIVATE
  Algorism.cpp
  FpsCounter.cpp
  Sha.cpp)
target_include_directories(owe-utils PUBLIC ../../../third_party include include/Utils)
target_link_libraries(owe-utils PUBLIC owe-core)
target_link_libraries(owe-utils PUBLIC owe-types)
target_link_libraries(owe-utils PUBLIC rstd.std)
target_link_libraries(owe-utils PUBLIC rstd.cppstd)
target_link_libraries(owe-utils PUBLIC rstd.log)
