# owe-tests（原 lito.toml 的源文件清单与用法，T1 起手写维护）
add_library(owe-test-support STATIC)
target_sources(owe-test-support PUBLIC FILE_SET CXX_MODULES BASE_DIRS . FILES
  src/lib.cppm
  src/JsonBuilder.cppm
  src/PkgHeader.cppm
  src/SceneParseProbe.cppm
  src/SceneKeys.cppm
  src/Corpus.cppm)
target_compile_definitions(owe-test-support PUBLIC "WAYWALLEN_WORKSHOP_DIR=\"workshop\"" "WAYWALLEN_ASSETS_DIR=\"assets\"" "WAYWALLEN_FIXTURE_DIR=\"tests/fixtures\"" "WAYWALLEN_DEFAULTPROJECT_DIR=\"defaultprojects\"")
target_link_libraries(owe-test-support PUBLIC owe-core)
target_link_libraries(owe-test-support PUBLIC owe-audio)
target_link_libraries(owe-test-support PUBLIC owe-scene)
target_link_libraries(owe-test-support PUBLIC owe-vulkan)
target_link_libraries(owe-test-support PUBLIC rstd.std)
target_link_libraries(owe-test-support PUBLIC rstd.cppstd)
target_link_libraries(owe-test-support PUBLIC rstd.json)
target_link_libraries(owe-test-support PUBLIC rstd.log)
target_link_libraries(owe-test-support PUBLIC rstd.bench)
target_link_libraries(owe-test-support PUBLIC wavsen)
target_link_libraries(owe-test-support PRIVATE vvk::vvk)
target_link_libraries(owe-test-support PRIVATE spirv-reflect)

add_executable(core-tests EXCLUDE_FROM_ALL)
target_sources(core-tests PRIVATE
  tests/core/json_adapter_tests.cpp
  tests/core/load_bench_tests.cpp
  tests/core/vfs_tests.cpp)
target_link_libraries(core-tests PRIVATE owe-test-support owe-core owe-audio owe-scene owe-vulkan rstd.std rstd.cppstd rstd.json rstd.log rstd.bench gtest_main wavsen vvk::vvk spirv-reflect)
target_compile_options(core-tests PRIVATE -Wextra -Wno-missing-field-initializers -Wno-gnu-zero-variadic-macro-arguments)

add_executable(parser-tests EXCLUDE_FROM_ALL)
target_sources(parser-tests PRIVATE
  tests/parser/image_parser_tests.cpp
  tests/parser/material_parse_tests.cpp
  tests/parser/mdl_mesh_tests.cpp
  tests/parser/shader_lex_tests.cpp)
target_link_libraries(parser-tests PRIVATE owe-test-support owe-core owe-audio owe-scene owe-vulkan rstd.std rstd.cppstd rstd.json rstd.log rstd.bench gtest_main wavsen vvk::vvk spirv-reflect)
target_compile_options(parser-tests PRIVATE -Wextra -Wno-missing-field-initializers -Wno-gnu-zero-variadic-macro-arguments)

add_executable(render-resource-tests EXCLUDE_FROM_ALL)
target_sources(render-resource-tests PRIVATE
  tests/render-resource/buffer_resolver_tests.cpp
  tests/render-resource/effect_command_tests.cpp
  tests/render-resource/render_graph_debug_tests.cpp
  tests/render-resource/render_resource_request_tests.cpp
  tests/render-resource/resource_model_tests.cpp
  tests/render-resource/resource_registry_tests.cpp)
target_link_libraries(render-resource-tests PRIVATE owe-test-support owe-core owe-audio owe-scene owe-vulkan rstd.std rstd.cppstd rstd.json rstd.log rstd.bench gtest_main wavsen vvk::vvk spirv-reflect)
target_compile_options(render-resource-tests PRIVATE -Wextra -Wno-missing-field-initializers -Wno-gnu-zero-variadic-macro-arguments)

add_executable(effect-swap-gpu-tests EXCLUDE_FROM_ALL)
target_sources(effect-swap-gpu-tests PRIVATE
  tests/render-resource/effect_swap_gpu.cpp)
target_link_libraries(effect-swap-gpu-tests PRIVATE owe-test-support owe-core owe-audio owe-scene owe-vulkan rstd.std rstd.cppstd rstd.json rstd.log rstd.bench wavsen vvk::vvk spirv-reflect)
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
target_link_libraries(scene-runtime-tests PRIVATE owe-test-support owe-core owe-audio owe-scene owe-vulkan rstd.std rstd.cppstd rstd.json rstd.log rstd.bench gtest_main wavsen vvk::vvk spirv-reflect)
target_compile_options(scene-runtime-tests PRIVATE -Wextra -Wno-missing-field-initializers -Wno-gnu-zero-variadic-macro-arguments)

add_executable(scene-parse-tests EXCLUDE_FROM_ALL)
target_sources(scene-parse-tests PRIVATE
  tests/scene-parse/scene_parse_tests.cpp)
target_link_libraries(scene-parse-tests PRIVATE owe-test-support owe-core owe-audio owe-scene owe-vulkan rstd.std rstd.cppstd rstd.json rstd.log rstd.bench gtest_main wavsen vvk::vvk spirv-reflect)
target_compile_options(scene-parse-tests PRIVATE -Wextra -Wno-missing-field-initializers -Wno-gnu-zero-variadic-macro-arguments)

add_executable(script-runtime-tests EXCLUDE_FROM_ALL)
target_sources(script-runtime-tests PRIVATE
  tests/script-runtime/script_fault_isolation_tests.cpp
  tests/script-runtime/script_runtime_tests.cpp)
target_link_libraries(script-runtime-tests PRIVATE owe-test-support owe-core owe-audio owe-scene owe-vulkan rstd.std rstd.cppstd rstd.json rstd.log rstd.bench gtest_main wavsen vvk::vvk spirv-reflect)
target_compile_options(script-runtime-tests PRIVATE -Wextra -Wno-missing-field-initializers -Wno-gnu-zero-variadic-macro-arguments)

add_executable(shader-parse-tests EXCLUDE_FROM_ALL)
target_sources(shader-parse-tests PRIVATE
  tests/shader-parse/shader_parse_tests.cpp)
target_link_libraries(shader-parse-tests PRIVATE owe-test-support owe-core owe-audio owe-scene owe-vulkan rstd.std rstd.cppstd rstd.json rstd.log rstd.bench gtest_main wavsen vvk::vvk spirv-reflect)
target_compile_options(shader-parse-tests PRIVATE -Wextra -Wno-missing-field-initializers -Wno-gnu-zero-variadic-macro-arguments)

# T2-3 过渡期 JSON 语义差分（rstd.json 对 nlohmann），rstd 摘除时删。
add_executable(json-semantic-diff EXCLUDE_FROM_ALL src/bin/json_semantic_diff.cpp)
target_link_libraries(json-semantic-diff PRIVATE owe-core rstd.std rstd.cppstd rstd.json)
