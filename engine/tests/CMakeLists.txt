# owe-tests（原 lito.toml 的源文件清单与用法，T1 起手写维护）
add_library(owe-test-support STATIC)
target_sources(owe-test-support PUBLIC FILE_SET CXX_MODULES BASE_DIRS . FILES
  src/lib.cppm
  src/PkgHeader.cppm
  src/SceneParseProbe.cppm
  src/Corpus.cppm)
target_compile_definitions(owe-test-support PUBLIC "WAYWALLEN_WORKSHOP_DIR=\"workshop\"" "WAYWALLEN_ASSETS_DIR=\"assets\"" "WAYWALLEN_DEFAULTPROJECT_DIR=\"defaultprojects\"")
target_link_libraries(owe-test-support PUBLIC owe-core)
target_link_libraries(owe-test-support PUBLIC owe-audio)
target_link_libraries(owe-test-support PUBLIC owe-scene)
target_link_libraries(owe-test-support PUBLIC owe-vulkan)
target_link_libraries(owe-test-support PUBLIC rstd.std)
target_link_libraries(owe-test-support PUBLIC rstd.cppstd)
target_link_libraries(owe-test-support PUBLIC rstd.log)
target_link_libraries(owe-test-support PUBLIC wavsen)
target_link_libraries(owe-test-support PRIVATE vvk::vvk)
target_link_libraries(owe-test-support PRIVATE spirv-reflect)

add_executable(core-tests EXCLUDE_FROM_ALL)
target_sources(core-tests PRIVATE
  tests/core/json_tests.cpp
  tests/core/vfs_tests.cpp)
target_link_libraries(core-tests PRIVATE owe-test-support owe-core owe-audio owe-scene owe-vulkan rstd.std rstd.cppstd rstd.log gtest_main wavsen vvk::vvk spirv-reflect)
target_compile_options(core-tests PRIVATE -Wextra -Wno-missing-field-initializers -Wno-gnu-zero-variadic-macro-arguments)

add_executable(parser-tests EXCLUDE_FROM_ALL)
target_sources(parser-tests PRIVATE
  tests/parser/image_parser_tests.cpp
  tests/parser/material_parse_tests.cpp
  tests/parser/mdl_mesh_tests.cpp
  tests/parser/shader_lex_tests.cpp)
target_link_libraries(parser-tests PRIVATE owe-test-support owe-core owe-audio owe-scene owe-vulkan rstd.std rstd.cppstd rstd.log gtest_main wavsen vvk::vvk spirv-reflect)
target_compile_options(parser-tests PRIVATE -Wextra -Wno-missing-field-initializers -Wno-gnu-zero-variadic-macro-arguments)

add_executable(render-resource-tests EXCLUDE_FROM_ALL)
target_sources(render-resource-tests PRIVATE
  tests/render-resource/buffer_resolver_tests.cpp
  tests/render-resource/effect_command_tests.cpp
  tests/render-resource/render_graph_debug_tests.cpp
  tests/render-resource/render_resource_request_tests.cpp
  tests/render-resource/resource_model_tests.cpp
  tests/render-resource/resource_registry_tests.cpp)
target_link_libraries(render-resource-tests PRIVATE owe-test-support owe-core owe-audio owe-scene owe-vulkan rstd.std rstd.cppstd rstd.log gtest_main wavsen vvk::vvk spirv-reflect)
target_compile_options(render-resource-tests PRIVATE -Wextra -Wno-missing-field-initializers -Wno-gnu-zero-variadic-macro-arguments)

add_executable(effect-swap-gpu-tests EXCLUDE_FROM_ALL)
target_sources(effect-swap-gpu-tests PRIVATE
  tests/render-resource/effect_swap_gpu.cpp)
target_link_libraries(effect-swap-gpu-tests PRIVATE owe-test-support owe-core owe-audio owe-scene owe-vulkan rstd.std rstd.cppstd rstd.log wavsen vvk::vvk spirv-reflect)
target_compile_options(effect-swap-gpu-tests PRIVATE -Wextra -Wno-missing-field-initializers -Wno-gnu-zero-variadic-macro-arguments)

add_executable(scene-runtime-tests EXCLUDE_FROM_ALL)
target_sources(scene-runtime-tests PRIVATE
  tests/scene-runtime/particle_system_tests.cpp
  tests/scene-runtime/puppet_ik_tests.cpp
  tests/scene-runtime/scene_resource_index_tests.cpp
  tests/scene-runtime/scene_runtime_tests.cpp
  tests/scene-runtime/sprite_animation_tests.cpp
  tests/scene-runtime/scene_user_property_tests.cpp
  tests/scene-runtime/text_geometry_tests.cpp)
target_link_libraries(scene-runtime-tests PRIVATE owe-test-support owe-core owe-audio owe-scene owe-vulkan rstd.std rstd.cppstd rstd.log gtest_main wavsen vvk::vvk spirv-reflect)
target_compile_options(scene-runtime-tests PRIVATE -Wextra -Wno-missing-field-initializers -Wno-gnu-zero-variadic-macro-arguments)

add_executable(scene-parse-tests EXCLUDE_FROM_ALL)
target_sources(scene-parse-tests PRIVATE
  tests/scene-parse/scene_parse_tests.cpp)
target_link_libraries(scene-parse-tests PRIVATE owe-test-support owe-core owe-audio owe-scene owe-vulkan rstd.std rstd.cppstd rstd.log gtest_main wavsen vvk::vvk spirv-reflect)
target_compile_options(scene-parse-tests PRIVATE -Wextra -Wno-missing-field-initializers -Wno-gnu-zero-variadic-macro-arguments)

add_executable(script-runtime-tests EXCLUDE_FROM_ALL)
target_sources(script-runtime-tests PRIVATE
  tests/script-runtime/script_fault_isolation_tests.cpp
  tests/script-runtime/script_runtime_tests.cpp)
target_link_libraries(script-runtime-tests PRIVATE owe-test-support owe-core owe-audio owe-scene owe-vulkan rstd.std rstd.cppstd rstd.log gtest_main wavsen vvk::vvk spirv-reflect)
target_compile_options(script-runtime-tests PRIVATE -Wextra -Wno-missing-field-initializers -Wno-gnu-zero-variadic-macro-arguments)

add_executable(shader-parse-tests EXCLUDE_FROM_ALL)
target_sources(shader-parse-tests PRIVATE
  tests/shader-parse/shader_parse_tests.cpp)
target_link_libraries(shader-parse-tests PRIVATE owe-test-support owe-core owe-audio owe-scene owe-vulkan rstd.std rstd.cppstd rstd.log gtest_main wavsen vvk::vvk spirv-reflect)
target_compile_options(shader-parse-tests PRIVATE -Wextra -Wno-missing-field-initializers -Wno-gnu-zero-variadic-macro-arguments)

# 要读创意工坊语料：在 engine/ 下建 workshop 联接（每个子目录一个带 scene.pkg 的壁纸）后运行。
add_executable(version-corpus-tests EXCLUDE_FROM_ALL)
target_sources(version-corpus-tests PRIVATE
  tests/version-corpus/version_tests.cpp)
target_link_libraries(version-corpus-tests PRIVATE owe-test-support owe-core owe-audio owe-scene owe-vulkan rstd.std rstd.cppstd rstd.log gtest_main wavsen vvk::vvk spirv-reflect)
target_compile_options(version-corpus-tests PRIVATE -Wextra -Wno-missing-field-initializers -Wno-gnu-zero-variadic-macro-arguments)

# owe-media 单测（含从 wavsen 移植的三组音频测试）。
add_executable(media-tests EXCLUDE_FROM_ALL)
target_sources(media-tests PRIVATE
  tests/media/audio_decoder_tests.cpp
  tests/media/offline_mixer_tests.cpp
  tests/media/volume_ramp_tests.cpp)
target_link_libraries(media-tests PRIVATE owe-media gtest_main)
# 合成夹具（lavfi 正弦波截断/损坏，见 audio_decoder_tests.cpp 开头）。
target_compile_definitions(media-tests PRIVATE "OWE_MEDIA_FIXTURE_DIR=\"${CMAKE_CURRENT_SOURCE_DIR}/tests/media/fixtures\"")
target_compile_options(media-tests PRIVATE -Wextra -Wno-missing-field-initializers -Wno-gnu-zero-variadic-macro-arguments)

# T5a 对拍：wavsen 旧音频路径 vs owe-media，逐字节比较。T5b 删 wavsen 时连同 tests/media/media_parity_tests.cpp 一起删。
# 夹具目录由环境变量 OWE_MEDIA_PARITY_DIR 指定（语料音频不进仓库），没设时跳过。
add_executable(media-parity-tests EXCLUDE_FROM_ALL)
target_sources(media-parity-tests PRIVATE
  tests/media/media_parity_tests.cpp)
target_link_libraries(media-parity-tests PRIVATE owe-media gtest_main wavsen::audio)
target_compile_options(media-parity-tests PRIVATE -Wextra -Wno-missing-field-initializers -Wno-gnu-zero-variadic-macro-arguments)

# T5b 对拍：wavsen 旧视频解码 vs owe::media::VideoSource，逐帧 NV12 字节比较。T5b 删 wavsen 时连同
# tests/media/video_parity_tests.cpp 一起删。夹具目录由环境变量 OWE_VIDEO_PARITY_DIR 指定，没设时跳过。
add_executable(video-parity-tests EXCLUDE_FROM_ALL)
target_sources(video-parity-tests PRIVATE
  tests/media/video_parity_tests.cpp)
target_link_libraries(video-parity-tests PRIVATE owe-media gtest_main wavsen::video)
target_compile_options(video-parity-tests PRIVATE -Wextra -Wno-missing-field-initializers -Wno-gnu-zero-variadic-macro-arguments)

# wescene.vk 薄层单测（T4a）：不需要 GPU；Vulkan 入口由测试注入，VK_CHECK 的失败处理由测试提供。
add_executable(vk-tests EXCLUDE_FROM_ALL)
target_sources(vk-tests PRIVATE
  tests/vk/vk_tests.cpp)
target_link_libraries(vk-tests PRIVATE owe-vk gtest_main)
target_compile_options(vk-tests PRIVATE -Wextra -Wno-missing-field-initializers -Wno-gnu-zero-variadic-macro-arguments)
