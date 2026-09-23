# owe-media：引擎自己的媒体层（T5a 音频；T5b 迁入视频后 wavsen 整个退出）。
# FFmpeg 从 WPE_FFMPEG_ROOT 的 pkg-config 取（PKG_CONFIG_PATH 在顶层 CMakeLists 里设好）。
find_package(PkgConfig REQUIRED)
pkg_check_modules(OWE_FFMPEG REQUIRED IMPORTED_TARGET libavformat libavcodec libavutil libswresample libswscale)

add_library(owe-media STATIC)
target_sources(owe-media PUBLIC FILE_SET CXX_MODULES BASE_DIRS . FILES
  Media.cppm)
target_sources(owe-media PRIVATE
  AudioDecoder.cpp
  OfflineMixer.cpp
  VideoSource.cpp)
target_link_libraries(owe-media PUBLIC owe-core)
target_link_libraries(owe-media PUBLIC rstd.cppstd)
target_link_libraries(owe-media PRIVATE PkgConfig::OWE_FFMPEG)
