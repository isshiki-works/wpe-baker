# owe-media：引擎自己的媒体层（音频 T5a、视频 T5b，移植自 wavsen，MIT）。
# FFmpeg 的导入目标 PkgConfig::OWE_FFMPEG 在顶层 CMakeLists 里定义。

add_library(owe-media STATIC)
target_sources(owe-media PUBLIC FILE_SET CXX_MODULES BASE_DIRS . FILES
  Media.cppm
  Nv12ToRgba.cppm)
target_sources(owe-media PRIVATE
  AudioDecoder.cpp
  Nv12ToRgba.cpp
  OfflineMixer.cpp
  VideoSource.cpp)
target_link_libraries(owe-media PUBLIC owe-core)
target_link_libraries(owe-media PUBLIC rstd.cppstd)
target_link_libraries(owe-media PUBLIC owe-vk)
target_link_libraries(owe-media PRIVATE PkgConfig::OWE_FFMPEG)
