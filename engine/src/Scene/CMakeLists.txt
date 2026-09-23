# owe-scene（原 lito.toml 的源文件清单与用法，T1 起手写维护）
add_library(owe-scene STATIC)
target_sources(owe-scene PUBLIC FILE_SET CXX_MODULES BASE_DIRS ../.. FILES
  Audio/SceneAudioResponse.cppm
  Lighting/Lighting.cppm
  OfflineSession.cppm
  Particle/Particle.cppm
  Particle/ParticleProgram.cppm
  Pkg/AssetVersion.cppm
  Pkg/Parse/Compat.cppm
  Pkg/Parse/Image/TexImageParser.cppm
  Pkg/Parse/Particle/ParticleParser.cppm
  Pkg/Parse/Particle/ParticleRuntime.cppm
  Pkg/Parse/Puppet/MdlParser.cppm
  Pkg/Parse/Puppet/Puppet.cppm
  Pkg/Parse/Puppet/PuppetCompat.cppm
  Pkg/Parse/Scene/SceneContext.cppm
  Pkg/Parse/Scene/SceneParser.cppm
  Pkg/Parse/Scene/SceneStages.cppm
  Pkg/Parse/Shader/ShaderLex.cppm
  Pkg/Parse/Shader/ShaderParser.cppm
  Pkg/Parse/Sound/SoundParser.cppm
  Pkg/Parse/Uniform/GlobalUniform.cppm
  Pkg/Parse/Uniform/Uniform.cppm
  Pkg/Parse/Uniform/UniformSource.cppm
  Pkg/Parse/mod.cppm
  Pkg/PkgFs.cppm
  Pkg/SceneObj/AnimationLayer.cppm
  Pkg/SceneObj/FieldBinding.cppm
  Pkg/SceneObj/ImageObject.cppm
  Pkg/SceneObj/LightObject.cppm
  Pkg/SceneObj/Material.cppm
  Pkg/SceneObj/MiscObject.cppm
  Pkg/SceneObj/Object.cppm
  Pkg/SceneObj/ParticleObject.cppm
  Pkg/SceneObj/SceneDocument.cppm
  Pkg/SceneObj/SoundObject.cppm
  Pkg/SceneObj/VisibilityBinding.cppm
  Pkg/SceneObj/mod.cppm
  Pkg/SpecNames.cppm
  Pkg/mod.cppm
  RenderGraph/DependencyGraph.cppm
  RenderGraph/Pass.cppm
  RenderGraph/PassNode.cppm
  RenderGraph/RGraph.cppm
  RenderGraph/RenderGraph.cppm
  RenderGraph/TexNode.cppm
  Resource/Barrier.cppm
  Resource/Buffer.cppm
  Resource/BufferRegistry.cppm
  Resource/Catalog.cppm
  Resource/Descriptor.cppm
  Resource/Error.cppm
  Resource/External.cppm
  Resource/Graphics.cppm
  Resource/Handle.cppm
  Resource/Lifetime.cppm
  Resource/Owner.cppm
  Resource/Plan.cppm
  Resource/Policy.cppm
  Resource/Prepared.cppm
  Resource/Registry.cppm
  Resource/Resource.cppm
  Resource/ResourceKey.cppm
  Resource/Shader.cppm
  Resource/ShaderRegistry.cppm
  Resource/State.cppm
  Resource/System.cppm
  Resource/Texture.cppm
  Resource/TextureRegistry.cppm
  Scene/Id.cppm
  Scene/Runtime.cppm
  Scene/Scene.cppm
  Scene/SceneVisibility.cppm
  Scene/Uniform.cppm
  SceneUserProperty.cppm
  Script/Script.cppm
  SpecNames.cppm
  Text/Text.cppm
  VulkanRender/BufferResolver.cppm
  VulkanRender/Pass/CopyPass.cppm
  VulkanRender/Pass/CustomShaderPass.cppm
  VulkanRender/Pass/FinPass.cppm
  VulkanRender/Pass/PassCommon.cppm
  VulkanRender/Pass/PrePass.cppm
  VulkanRender/Pass/VulkanPass.cppm
  VulkanRender/PipelineLayout.cppm
  VulkanRender/Program.cppm
  VulkanRender/Resource.cppm
  VulkanRender/ShaderReflectionCache.cppm
  VulkanRender/UniformBuffer.cppm
  VulkanRender/VulkanRender.cppm)
target_sources(owe-scene PRIVATE
  Audio/SceneAudioResponse.cpp
  OfflineSession.cpp
  Pkg/Parse/Image/SceneImageObjectParser.cpp
  Pkg/Parse/Image/TexImageParser.cpp
  Pkg/Parse/Particle/ParticleEmitter.cpp
  Pkg/Parse/Particle/ParticleParser.cpp
  Pkg/Parse/Particle/ParticleRawGenerator.cpp
  Pkg/Parse/Particle/ParticleRuntime.cpp
  Pkg/Parse/Particle/SceneParticleObjectParser.cpp
  Pkg/Parse/Puppet/MdlParser.cpp
  Pkg/Parse/Puppet/Puppet.cpp
  Pkg/Parse/Scene/SceneAnimationBinding.cpp
  Pkg/Parse/Scene/SceneContext.cpp
  Pkg/Parse/Scene/SceneDynamicAsset.cpp
  Pkg/Parse/Scene/SceneFinalize.cpp
  Pkg/Parse/Scene/SceneGeometryBuilder.cpp
  Pkg/Parse/Scene/SceneMaterialBuilder.cpp
  Pkg/Parse/Scene/SceneObjectExpansion.cpp
  Pkg/Parse/Scene/SceneObjectParsers.cpp
  Pkg/Parse/Scene/SceneParser.cpp
  Pkg/Parse/Scene/ScenePostProcess.cpp
  Pkg/Parse/Scene/SceneScriptBinding.cpp
  Pkg/Parse/Scene/SceneTextObjectParser.cpp
  Pkg/Parse/Shader/ShaderAnnot.cpp
  Pkg/Parse/Shader/ShaderParser.cpp
  Pkg/Parse/Sound/SoundParser.cpp
  Pkg/Parse/Uniform/Uniform.cpp
  Pkg/Parse/Uniform/UniformSource.cpp
  Pkg/PkgFs.cpp
  Pkg/SceneObj/FieldBinding.cpp
  Pkg/SceneObj/ImageObject.cpp
  Pkg/SceneObj/LightObject.cpp
  Pkg/SceneObj/Material.cpp
  Pkg/SceneObj/Object.cpp
  Pkg/SceneObj/ParticleObject.cpp
  Pkg/SceneObj/SceneDocument.cpp
  RenderGraph/DependencyGraph.cpp
  RenderGraph/PassNode.cpp
  RenderGraph/RenderGraph.cpp
  RenderGraph/TexNode.cpp
  Scene/Scene.cpp
  Scene/SceneCamera.cpp
  Scene/SceneIndexArray.cpp
  Scene/SceneNode.cpp
  Scene/SceneNodeLayer.cpp
  Scene/SceneShader.cpp
  Scene/SceneVertexArray.cpp
  SceneUserProperty.cpp
  Script/Script.cpp
  Text/Text.cpp
  VulkanRender/BufferResolver.cpp
  VulkanRender/Pass/CopyPass.cpp
  VulkanRender/Pass/CustomShaderPass.cpp
  VulkanRender/Pass/FinPass.cpp
  VulkanRender/Pass/PrePass.cpp
  VulkanRender/SceneToRenderGraph.cpp
  VulkanRender/ShaderReflectionCache.cpp
  VulkanRender/UniformBuffer.cpp
  VulkanRender/VulkanRender.cpp
  VulkanRender/GpuVideoEncoder.cpp)
target_include_directories(owe-scene PUBLIC ${PROJECT_SOURCE_DIR}/third_party ${PROJECT_SOURCE_DIR}/src ${PROJECT_SOURCE_DIR}/src/Scene ${PROJECT_SOURCE_DIR}/src/Scene/Scene ${PROJECT_SOURCE_DIR}/src/Scene/Particle ${PROJECT_SOURCE_DIR}/src/Scene/Script ${PROJECT_SOURCE_DIR}/src/Scene/VulkanRender)
target_compile_definitions(owe-scene PUBLIC "WAYWALLEN_WORKSHOP_DIR=\"workshop\"" "WAYWALLEN_ASSETS_DIR=\"assets\"")
target_link_libraries(owe-scene PUBLIC owe-core)
target_link_libraries(owe-scene PUBLIC owe-audio)
target_link_libraries(owe-scene PUBLIC owe-media)
target_link_libraries(owe-scene PUBLIC owe-types)
target_link_libraries(owe-scene PUBLIC owe-utils)
target_link_libraries(owe-scene PUBLIC owe-vulkan)
target_link_libraries(owe-scene PUBLIC rstd.std)
target_link_libraries(owe-scene PUBLIC rstd.cppstd)
target_link_libraries(owe-scene PUBLIC rstd.log)
target_link_libraries(owe-scene PUBLIC wavsen)
target_link_libraries(owe-scene PRIVATE dwrite)
target_link_libraries(owe-scene PUBLIC qjs lz4_static freetype)
