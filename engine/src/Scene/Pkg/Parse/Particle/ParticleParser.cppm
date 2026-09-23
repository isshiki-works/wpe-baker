export module wescene.pkg.parse:particle_parser;
import rstd;
import wescene.core;
import rstd.cppstd;
import wescene.json;
import wescene.scene;
import wescene.fs;
import wescene.particle.program;
import :particle_runtime;

export import wescene.pkg.scene_obj;

export namespace owe

{
class ParticleParser {
public:
    // services：离线作业的服务（不在离线作业里为空），部分初始化器在生成时就取随机数。
    static ParticleSpawnInstruction GenInitializer(const NJson&, u32 implicit_sequence_count,
                                                   Services* services);
    static std::unique_ptr<particle::ParticleUpdateProgram>
    GenOperator(const NJson&, ParticleInstanceModifiers, ParticleSubSystem&, usize operator_index);
    static std::unique_ptr<particle::ParticleEmitterProgram> GenEmitter(const wpscene::Emitter&,
                                                                        ParticleSubSystem&, usize);
    static ParticleSpawnInstruction                   GenOverride(ParticleInstanceModifiers);
};
} // namespace owe
