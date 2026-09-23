# owe-core（原 lito.toml 的源文件清单与用法，T1 起手写维护）
add_library(owe-core STATIC)
target_sources(owe-core PUBLIC FILE_SET CXX_MODULES BASE_DIRS ../.. FILES
  Core.cppm
  Fft.cppm
  Fs.cppm
  Fs/Fs.cppm
  IO/Binary.cppm
  Json.cppm
  UserProperty.cppm
  eigen.cppm)
target_sources(owe-core PRIVATE
  Fft.cpp
  Json.cpp
  UserProperty.cpp)
target_include_directories(owe-core PUBLIC ${PROJECT_SOURCE_DIR}/third_party ${PROJECT_SOURCE_DIR}/src ${PROJECT_SOURCE_DIR}/src/Core)
target_compile_definitions(owe-core PUBLIC "FT_SIZEOF_LONG=4" NOMINMAX WIN32_LEAN_AND_MEAN)
target_link_libraries(owe-core PUBLIC rstd.std)
target_link_libraries(owe-core PUBLIC rstd.cppstd)
target_link_libraries(owe-core PUBLIC rstd.log)
target_link_libraries(owe-core PUBLIC wpe-eigen)
target_link_libraries(owe-core PUBLIC nlohmann-json)
