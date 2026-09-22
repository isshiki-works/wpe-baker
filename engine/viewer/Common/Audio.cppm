export module viewer.audio;

export import wavsen.audio;
import owe.audio_response;
import rstd;

export namespace viewer
{

inline owe::audio::PcmWindow ConvertAudioWindow(const wavsen::audio::AudioPcmWindow& source) {
    owe::audio::PcmWindow destination {};
    destination.generation       = source.generation;
    destination.sequence         = source.sequence;
    destination.captured_at_ns   = source.captured_at_ns;
    destination.end_sample_frame = source.end_sample_frame;
    destination.sample_rate_hz   = source.sample_rate_hz;
    destination.channels         = source.channels;
    destination.frames           = source.frames;
    for (rstd::size_t index = 0; index < owe::audio::kSampleCount; ++index) {
        destination.samples[rstd::usize(index)] = source.samples[rstd::usize(index)].to_primitive();
    }
    return destination;
}

} // namespace viewer
