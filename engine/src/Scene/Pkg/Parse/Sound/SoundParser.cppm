module;
#include <memory>

export module wescene.pkg.parse:sound_parser;
import rstd;
import wescene.core;
import owe.media;
import wescene.fs;
import wescene.scene;
import wescene.pkg.scene_obj;

export namespace owe
{

using rstd::sync::Arc;

class SoundParser {
public:
    static std::shared_ptr<SceneSoundControl> Parse(const wpscene::SoundObject&, fs::VFS&,
                                                    owe::media::OfflineMixer&, Scene*, Services*);
};

} // namespace owe
