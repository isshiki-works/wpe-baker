// SPDX-License-Identifier: MIT
// Offline CLI. The OWE libraries keep their upstream license.
#ifdef _WIN32
#define NOMINMAX
#include <windows.h>
#include <shellapi.h>
#include <fcntl.h>
#include <io.h>
#endif
#include <cstdio>
#include <filesystem>
#ifndef WPE_RENDER_SOURCE_DIGEST
#define WPE_RENDER_SOURCE_DIGEST "unrecorded"
#endif

import rstd;
import rstd.cppstd;
import rstd.log;
import wescene.scene_wallpaper;
import wescene.pkg.parse;
import wescene.json;

namespace fs = std::filesystem;
using namespace rstd::prelude;
using namespace rstd::literals;

struct DiagnosticLogger {
    rstd::log::EnvLogger sink { "info"_str };
    mutable std::atomic<uint64_t> errors { 0 };
    bool enabled(const rstd::log::Metadata& metadata) const { return sink.enabled(metadata); }
    void log(const rstd::log::Record& record) const {
        if (record.lvl() == rstd::log::Level::Error) errors.fetch_add(1);
        sink.log(record);
    }
    void flush() const { sink.flush(); }
};

namespace rstd {
template<> struct Impl<log::Log, DiagnosticLogger> : ImplBase<DiagnosticLogger> {
    bool enabled(log::Metadata const& value) const { return this->self().enabled(value); }
    void log(log::Record const& value) const { this->self().log(value); }
    void flush() const { this->self().flush(); }
};
}

namespace {
constexpr const char* kBase = "b866e8e711fdd7762385b23601affa1ea5539e3b";
DiagnosticLogger logger;

void RequireNoLoggedErrors() {
    if (auto count = logger.errors.load(); count != 0)
        throw std::runtime_error("renderer reported " + std::to_string(count) + " error(s); original errors are on stderr");
}

std::string Quote(std::string_view value) {
    return owe::Dump(owe::JsonFromStd(value));
}

std::string OptionalReal(const std::optional<double>& value) {
    if (!value) return "null";
    std::ostringstream text;
    text.precision(17);
    text << *value;
    return text.str();
}

fs::path Path(std::string_view value) {
    return fs::u8path(value.begin(), value.end());
}

std::string Utf8(const fs::path& path) {
    auto value = path.generic_u8string();
    return {reinterpret_cast<const char*>(value.data()), value.size()};
}

std::string ReadText(const fs::path& path) {
    if (fs::file_size(path) > 16 * 1024 * 1024)
        throw std::runtime_error("job JSON exceeds 16 MiB");
    std::ifstream stream(path, std::ios::binary);
    if (!stream) throw std::runtime_error("cannot read " + Utf8(path));
    std::string text(std::istreambuf_iterator<char>(stream), {});
    if (text.starts_with("\xef\xbb\xbf")) text.erase(0, 3);
    return text;
}

const owe::Json* Field(const owe::Json& object, std::string_view key) {
    auto field = object.get(rstd::cppstd::as_str(key).unwrap());
    return field.is_some() ? &**field : nullptr;
}

uint64_t Uint(const owe::Json& object, std::string_view key, uint64_t fallback) {
    auto* field = Field(object, key);
    if (!field) return fallback;
    auto value = field->as_u64();
    if (value.is_none()) throw std::runtime_error(std::string(key) + " must be an unsigned integer");
    return value->to_primitive();
}

double Number(const owe::Json& object, std::string_view key, double fallback) {
    auto* field = Field(object, key);
    if (!field) return fallback;
    double value {};
    if (!owe::GetJsonValue(*field, value) || !std::isfinite(value))
        throw std::runtime_error(std::string(key) + " must be a finite number");
    return value;
}

bool Bool(const owe::Json& object, std::string_view key, bool fallback) {
    auto* field = Field(object, key);
    if (!field) return fallback;
    auto value = field->as_bool();
    if (value.is_none()) throw std::runtime_error(std::string(key) + " must be a boolean");
    return *value;
}

std::string String(const owe::Json& object, std::string_view key, std::string fallback = {}) {
    auto* field = Field(object, key);
    if (!field) return fallback;
    auto value = field->as_str();
    if (value.is_none()) throw std::runtime_error(std::string(key) + " must be a string");
    return rstd::cppstd::to_string(*value);
}

std::vector<std::string> Arguments(int argc, char** argv) {
    std::vector<std::string> result;
#ifdef _WIN32
    int count = 0;
    auto** wide = CommandLineToArgvW(GetCommandLineW(), &count);
    if (!wide) throw std::runtime_error("CommandLineToArgvW failed");
    for (int i = 0; i < count; ++i) {
        int size = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, wide[i], -1, nullptr, 0, nullptr, nullptr);
        if (size <= 0) { LocalFree(wide); throw std::runtime_error("invalid command-line Unicode"); }
        std::string value(static_cast<size_t>(size), '\0');
        WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, wide[i], -1, value.data(), size, nullptr, nullptr);
        value.pop_back();
        result.push_back(std::move(value));
    }
    LocalFree(wide);
#else
    for (int i = 0; i < argc; ++i) result.emplace_back(argv[i]);
#endif
    return result;
}

void WriteText(const fs::path& path, std::string_view text) {
    std::ofstream file(path, std::ios::binary | std::ios::trunc);
    file.write(text.data(), static_cast<std::streamsize>(text.size()));
    file.flush();
    if (!file) throw std::runtime_error("cannot write " + Utf8(path));
}

struct PointerInput {
    double cursor_x { 0.5 }, cursor_y { 0.5 };
    bool cursor_in_window { false };
    uint32_t mouse_buttons_down { 0 };
};

struct Job {
    fs::path source, assets, cache, output;
    uint32_t width{}, height{}, fps_num{}, fps_den{};
    uint64_t frames{}, warmup{}, seed{}, readback_budget{};
    uint64_t output_stride { 1 };
    std::optional<uint64_t> output_phase;
    double epoch_ms{};
    bool raw_stdout{}, validation{}, gpu_timing{}, trace_scene{};
    owe::OfflineFrameInput input;
    std::vector<std::pair<uint64_t, PointerInput>> input_timeline;
    std::optional<owe::RenderCaptureTarget> capture_target;
    std::optional<owe::OrthographicCaptureViewport> orthographic_capture_viewport;
    owe::RenderLayerSelection layer_selection;
    std::vector<owe::OfflineVideoPlaybackRateOverride> video_rate_overrides;
    std::vector<rstd::uint8_t> device_uuid;
};

uint64_t Gcd(uint64_t lhs, uint64_t rhs) {
    while (rhs != 0) {
        const uint64_t remainder = lhs % rhs;
        lhs = rhs;
        rhs = remainder;
    }
    return lhs;
}

PointerInput ReadInput(const owe::Json& json, PointerInput input = {}) {
    if (!json.is_object()) throw std::runtime_error("input must be an object");
    input.cursor_x = Number(json, "cursor_x", input.cursor_x);
    input.cursor_y = Number(json, "cursor_y", input.cursor_y);
    input.cursor_in_window = Bool(json, "cursor_in_window", input.cursor_in_window);
    auto buttons = Uint(json, "mouse_buttons_down", input.mouse_buttons_down);
    if (buttons > 7) throw std::runtime_error("mouse_buttons_down accepts bits 0..2");
    input.mouse_buttons_down = static_cast<uint32_t>(buttons);
    if (input.cursor_x < 0 || input.cursor_x > 1 || input.cursor_y < 0 || input.cursor_y > 1)
        throw std::runtime_error("cursor coordinates must be normalized to 0..1");
    return input;
}

void ApplyPointerInput(owe::OfflineFrameInput& frame, const PointerInput& pointer) {
    // Keep the optional media snapshot and PCM payload owned by the frame input.
    // Pointer events must not copy or replace those independent input channels.
    frame.cursor_x = pointer.cursor_x;
    frame.cursor_y = pointer.cursor_y;
    frame.cursor_in_window = pointer.cursor_in_window;
    frame.mouse_buttons_down = pointer.mouse_buttons_down;
}

uint64_t AudioBoundary(uint64_t frame, uint32_t fps_num, uint32_t fps_den) {
    // The supported Clang toolchain provides 128-bit integer arithmetic on x64.
    // This avoids both floating-point drift and intermediate uint64 overflow.
    const auto samples = static_cast<unsigned __int128>(frame) * fps_den * 48000 / fps_num;
    if (samples > std::numeric_limits<uint64_t>::max())
        throw std::runtime_error("audio sample timeline exceeds uint64 range");
    return static_cast<uint64_t>(samples);
}

Job ReadJob(const owe::Json& json, const fs::path& base) {
    if (!json.is_object() || Uint(json, "schema_version", 0) != 1)
        throw std::runtime_error("unsupported job schema_version (expected 1)");
    auto resolve = [&](std::string_view key) {
        auto value = String(json, key);
        if (value.empty()) throw std::runtime_error(std::string(key) + " is required");
        auto path = Path(value);
        return fs::absolute(path.is_absolute() ? path : base / path).lexically_normal();
    };
    Job job;
    job.source = resolve("source");
    job.assets = resolve("assets");
    job.output = resolve("output_dir");
    job.cache = job.output / "shader-cache";
    if (fs::is_directory(job.source))
        job.source /= fs::is_regular_file(job.source / "scene.pkg") ? "scene.pkg" : "scene.json";
    if (!fs::is_regular_file(job.source)) throw std::runtime_error("source scene does not exist");
    if (!fs::is_directory(job.assets)) throw std::runtime_error("assets directory does not exist");
    auto narrow = [&](std::string_view key, uint64_t fallback, uint64_t max) {
        uint64_t value = Uint(json, key, fallback);
        if (value == 0 || value > max) throw std::runtime_error(std::string(key) + " is out of range");
        return static_cast<uint32_t>(value);
    };
    job.width = narrow("width", 640, std::numeric_limits<uint16_t>::max());
    job.height = narrow("height", 360, std::numeric_limits<uint16_t>::max());
    job.fps_num = narrow("fps_num", 60, std::numeric_limits<uint32_t>::max());
    job.fps_den = narrow("fps_den", 1, std::numeric_limits<uint32_t>::max());
    job.frames = Uint(json, "frames", 120);
    job.warmup = Uint(json, "warmup_frames", 0);
    job.output_stride = Uint(json, "output_frame_stride", 1);
    if (Field(json, "output_frame_phase")) job.output_phase = Uint(json, "output_frame_phase", 0);
    if (job.output_stride == 0 || (job.output_phase && *job.output_phase >= job.output_stride))
        throw std::runtime_error("invalid output frame stride or phase");
    job.seed = Uint(json, "seed", 0);
    job.epoch_ms = Number(json, "epoch_ms", 946684800000.0);
    job.readback_budget = Uint(json, "max_readback_bytes", 256ull * 1024 * 1024);
    job.raw_stdout = Bool(json, "raw_stdout", false);
    job.validation = Bool(json, "vulkan_validation", false);
    job.gpu_timing = Bool(json, "gpu_timing", false);
    job.trace_scene = Bool(json, "trace_scene", false);
    if (auto* overrides = Field(json, "offline_video_rate_overrides")) {
        auto array = overrides->as_array();
        if (array.is_none())
            throw std::runtime_error("offline_video_rate_overrides must be an array");
        for (const auto& item : *array.unwrap()) {
            if (!item.is_object() || Field(item, "owner_layer_id") == nullptr ||
                Field(item, "rate_numerator") == nullptr || Field(item, "rate_denominator") == nullptr)
                throw std::runtime_error("offline video rate override requires owner_layer_id, rate_numerator, and rate_denominator");
            const uint64_t owner = Uint(item, "owner_layer_id", std::numeric_limits<uint64_t>::max());
            uint64_t numerator = Uint(item, "rate_numerator", 0);
            uint64_t denominator = Uint(item, "rate_denominator", 0);
            if (owner > std::numeric_limits<int32_t>::max() || numerator == 0 || denominator == 0)
                throw std::runtime_error("offline video rate override values are out of range");
            const long double rate = static_cast<long double>(numerator) / denominator;
            if (!std::isfinite(rate) || rate < 0.98L || rate > 1.02L)
                throw std::runtime_error("offline video rate override must be within 2 percent of rate 1");
            const uint64_t divisor = Gcd(numerator, denominator);
            numerator /= divisor;
            denominator /= divisor;
            for (const auto& existing : job.video_rate_overrides) {
                if (existing.owner_layer_id == static_cast<int32_t>(owner))
                    throw std::runtime_error("offline video rate override owner_layer_id is duplicated");
            }
            job.video_rate_overrides.push_back(owe::OfflineVideoPlaybackRateOverride {
                .owner_layer_id = static_cast<int32_t>(owner),
                .rate_numerator = numerator,
                .rate_denominator = denominator,
            });
        }
    }
    if (auto* selection = Field(json, "layer_selection")) {
        if (!selection->is_object()) throw std::runtime_error("layer_selection must be an object");
        job.layer_selection.enabled = true;
        job.layer_selection.transparent_background = Bool(*selection, "transparent_background", false);
        job.layer_selection.include_postprocessing = Bool(*selection, "include_postprocessing", true);
        auto* ids = Field(*selection, "include_layers");
        if (ids == nullptr || ids->as_array().is_none())
            throw std::runtime_error("layer_selection.include_layers must be an integer array");
        for (const auto& id : *ids->as_array().unwrap()) {
            auto number = id.as_u64();
            if (number.is_none() || number->to_primitive() > std::numeric_limits<int32_t>::max())
                throw std::runtime_error("capture layer IDs must be non-negative int32 values");
            job.layer_selection.include_layers.push_back(static_cast<int32_t>(number->to_primitive()));
        }
        if (job.layer_selection.include_layers.empty()) throw std::runtime_error("capture layer selection is empty");
    }
    if (auto uuid = String(json, "device_uuid"); !uuid.empty()) {
        if (uuid.size() != 32) throw std::runtime_error("device_uuid must contain exactly 32 hexadecimal digits");
        auto nibble = [](char value) -> uint8_t {
            if (value >= '0' && value <= '9') return static_cast<uint8_t>(value - '0');
            if (value >= 'a' && value <= 'f') return static_cast<uint8_t>(value - 'a' + 10);
            if (value >= 'A' && value <= 'F') return static_cast<uint8_t>(value - 'A' + 10);
            throw std::runtime_error("device_uuid contains a non-hexadecimal digit");
        };
        for (std::size_t i = 0; i < uuid.size(); i += 2)
            job.device_uuid.push_back(static_cast<uint8_t>((nibble(uuid[i]) << 4) | nibble(uuid[i + 1])));
    }
    if (job.frames == 0 || job.frames > std::numeric_limits<uint64_t>::max() - job.warmup)
        throw std::runtime_error("invalid frame count");
    if (job.frames + job.warmup > std::numeric_limits<uint64_t>::max() / job.fps_den)
        throw std::runtime_error("frame timestamp numerator would overflow");
    (void)AudioBoundary(job.frames + job.warmup, job.fps_num, job.fps_den);
    if (uint64_t(job.width) * job.height * 4 > job.readback_budget)
        throw std::runtime_error("one RGBA frame exceeds max_readback_bytes");
    PointerInput initial_input;
    if (auto* input = Field(json, "input")) initial_input = ReadInput(*input);
    ApplyPointerInput(job.input, initial_input);
    if (auto* timeline = Field(json, "input_timeline")) {
        auto array = timeline->as_array();
        if (array.is_none()) throw std::runtime_error("input_timeline must be an array");
        auto input = initial_input;
        for (std::size_t i = 0; i < (*array)->len().to_primitive(); ++i) {
            const auto& event = (**array)[rstd::usize(i)];
            const auto frame = Uint(event, "frame", std::numeric_limits<uint64_t>::max());
            if (!event.is_object() || frame >= job.warmup + job.frames ||
                (!job.input_timeline.empty() && frame <= job.input_timeline.back().first))
                throw std::runtime_error("input_timeline frames must be in range and strictly increasing");
            input = ReadInput(event, input);
            job.input_timeline.emplace_back(frame, input);
        }
    }
    if (auto* capture = Field(json, "capture_target")) {
        if (!capture->is_object()) throw std::runtime_error("capture_target must be an object");
        owe::RenderCaptureTarget target;
        auto selector = [&](std::string_view key) -> int32_t {
            if (!Field(*capture, key)) return -1;
            const auto value = Uint(*capture, key, 0);
            if (value > std::numeric_limits<int32_t>::max())
                throw std::runtime_error(std::string(key) + " must be a nonnegative int32");
            return static_cast<int32_t>(value);
        };
        target.owner_layer_id = selector("owner_layer_id");
        target.authored_effect_id = selector("authored_effect_id");
        target.effect_ordinal = selector("effect_ordinal");
        target.texture_version = selector("texture_version");
        target.local_fbo = String(*capture, "local_fbo");
        target.runtime_render_target = String(*capture, "runtime_render_target");
        target.effect_terminal = Bool(*capture, "effect_terminal", false);
        target.exact_extent = Bool(*capture, "exact_extent", false);
        job.capture_target = std::move(target);
    }
    if (auto* viewport = Field(json, "orthographic_capture_viewport")) {
        if (!viewport->is_object())
            throw std::runtime_error("orthographic_capture_viewport must be an object");
        auto required_number = [&](std::string_view key) {
            if (!Field(*viewport, key))
                throw std::runtime_error("orthographic_capture_viewport." + std::string(key) +
                                         " is required");
            return Number(*viewport, key, 0.0);
        };
        owe::OrthographicCaptureViewport parsed {
            .center_x = required_number("center_x"),
            .center_y = required_number("center_y"),
            .width = required_number("width"),
            .height = required_number("height"),
        };
        if (parsed.width <= 0.0 || parsed.height <= 0.0)
            throw std::runtime_error("orthographic_capture_viewport width and height must be positive");
        job.orthographic_capture_viewport = parsed;
    }
    return job;
}

int Render(const fs::path& job_path) {
    const auto text = ReadText(job_path);
    auto parsed = owe::ParseJson(text);
    if (parsed.is_err()) throw std::runtime_error("invalid job JSON");
    auto json = parsed.unwrap();
    auto job = ReadJob(json, fs::absolute(job_path).parent_path());
    // A job owns a newly created directory. Failed artifacts stay there for diagnosis.
    if (fs::exists(job.output)) throw std::runtime_error("output_dir already exists; choose a new directory");
    fs::create_directories(job.output.parent_path());
    if (!fs::create_directory(job.output)) throw std::runtime_error("cannot create output_dir");
    WriteText(job.output / "request.json", text);
    uint64_t written = 0;
    uint64_t audio_written = 0;
    const auto start = std::chrono::steady_clock::now();
    owe::SceneWallpaper wallpaper;
    auto result = [&](std::string_view status, std::string_view error = {}) {
        std::array<uint8_t, 16> gpu_uuid {};
        wallpaper.deviceUuid(gpu_uuid.data());
        std::string gpu_hex;
        constexpr char hex[] = "0123456789abcdef";
        if (std::any_of(gpu_uuid.begin(), gpu_uuid.end(), [](uint8_t byte) { return byte != 0; }))
            for (auto byte : gpu_uuid) { gpu_hex.push_back(hex[byte >> 4]); gpu_hex.push_back(hex[byte & 15]); }
        std::ostringstream out;
        out << "{\"schema_version\":1,\"status\":" << Quote(status)
            << ",\"upstream_base\":" << Quote(kBase)
            << ",\"build_source_digest\":" << Quote(WPE_RENDER_SOURCE_DIGEST)
            << ",\"device_uuid\":" << (gpu_hex.empty() ? "null" : Quote(gpu_hex))
            << ",\"source\":" << Quote(Utf8(job.source))
            << ",\"width\":" << job.width << ",\"height\":" << job.height
            << ",\"fps_num\":" << job.fps_num << ",\"fps_den\":" << job.fps_den
            << ",\"requested_frames\":" << job.frames << ",\"written_frames\":" << written
            << ",\"output_frame_stride\":" << job.output_stride
            << ",\"output_frame_phase\":" << (job.output_phase ? std::to_string(*job.output_phase) : "null")
            << ",\"warmup_frames\":" << job.warmup << ",\"pixel_format\":\"rgba8\""
            << ",\"renderer_error_count\":" << logger.errors.load();
        const auto& source_script_errors = wallpaper.offlineSourceScriptErrors();
        out << ",\"source_script_error_count\":" << source_script_errors.size()
            << ",\"source_script_errors\":[";
        bool first_source_script_error = true;
        for (const auto& item : source_script_errors) {
            if (!first_source_script_error) out << ',';
            first_source_script_error = false;
            out << "{\"binding_id\":" << item.binding_id
                << ",\"owner_layer_id\":" << item.owner_layer_id
                << ",\"owner_name\":" << Quote(item.owner_name)
                << ",\"property\":" << Quote(item.property)
                << ",\"phase\":" << Quote(item.phase)
                << ",\"script_sha\":" << Quote(item.script_sha)
                << ",\"message\":" << Quote(item.message)
                << ",\"stack\":" << Quote(item.stack) << '}';
        }
        out << "]"
            << ",\"runtime_ik_chain_solves\":" << wallpaper.offlineIkChainSolves()
            << ",\"compiled_scene_passes\":" << wallpaper.readback().compiled_scene_passes;
        const auto& video_inventory = wallpaper.readback().video_decoders;
        auto optional_integer = [](const auto& value) { return value ? std::to_string(*value) : std::string("null"); };
        auto optional_text = [](const std::string& value) { return value.empty() ? std::string("null") : Quote(value); };
        out << ",\"runtime_video_decoders\":";
        if (!video_inventory.observed) out << "null";
        else {
            out << '[';
            bool first_decoder = true;
            for (const auto& decoder : video_inventory.decoders) {
                if (!first_decoder) out << ',';
                first_decoder = false;
                out << "{\"resource_key\":" << Quote(decoder.resource_key)
                    << ",\"instance_id\":" << decoder.instance_id
                    << ",\"codec\":" << optional_text(decoder.codec)
                    << ",\"coded_width\":" << optional_integer(decoder.coded_width)
                    << ",\"coded_height\":" << optional_integer(decoder.coded_height)
                    << ",\"coded_size_source\":\"codec_parameters\""
                    << ",\"pixel_format\":" << optional_text(decoder.pixel_format)
                    << ",\"pixel_format_source\":\"codec_parameters\""
                    << ",\"fps_num\":" << optional_integer(decoder.fps_num)
                    << ",\"fps_den\":" << optional_integer(decoder.fps_den)
                    << ",\"fps_source\":" << optional_text(decoder.fps_source)
                    << ",\"metadata_unknown\":" << (decoder.metadata_unknown ? "true" : "false")
                    << ",\"decoder_kind\":" << Quote(decoder.decoder_kind)
                    << ",\"active\":" << (decoder.active ? "true" : "false")
                    << ",\"opened_at_tick\":" << decoder.opened_at_tick
                    << ",\"last_observed_active_tick\":" << decoder.last_observed_active_tick
                    << ",\"first_observed_inactive_tick\":" << optional_integer(decoder.first_observed_inactive_tick) << '}';
            }
            out << ']';
        }
        out << ",\"runtime_video_decoder_observation\":{\"status\":" << Quote(video_inventory.observed ? "observed" : "not_observed")
            << ",\"lifecycle_clock\":\"texture_pump_tick\""
            << ",\"observed_through_tick\":" << (video_inventory.observed ? std::to_string(video_inventory.observed_through_tick) : "null")
            << ",\"opened_instances\":" << (video_inventory.observed ? std::to_string(video_inventory.decoders.size()) : "null")
            << ",\"peak_active_instances\":" << (video_inventory.observed ? std::to_string(video_inventory.peak_active_instances) : "null")
            << ",\"scope\":\"Cumulative union of successful decoder opens during this texture-cache lifetime, including expired instances. Shared texture allocations count once; distinct opens of the same resource count separately. Active means the decoder allocation is alive, including paused instances. Peak is sampled at every successful allocation and texture pump; lifecycle ticks are observations, not media frames or exact destruction times. Encoded dimensions and pixel format come from codec parameters before output scaling. Stream FPS metadata does not prove constant frame spacing. Decoder kind describes this renderer; offline capture defaults to software decoding and does not verify official-player hardware decode. Unobserved later resources and failed open attempts are not fabricated as successful instances.\"}"
            << ",\"gpu_timing\":{\"requested\":" << (wallpaper.readback().gpu_timing_requested ? "true" : "false")
            << ",\"supported\":" << (wallpaper.readback().gpu_timing_supported ? "true" : "false")
            << ",\"total_ms\":" << OptionalReal(wallpaper.readback().gpu_total_ms)
            << ",\"draw_ms\":" << OptionalReal(wallpaper.readback().gpu_draw_ms)
            << ",\"timestamp_valid_bits\":" << wallpaper.readback().timestamp_valid_bits
            << ",\"timestamp_period_ns\":" << OptionalReal(wallpaper.readback().timestamp_period_ns)
            << ",\"error_code\":" << static_cast<int32_t>(wallpaper.readback().gpu_timing_error_code)
            << ",\"message\":" << Quote(wallpaper.readback().gpu_timing_message) << '}'
            << ",\"audio_sample_frames\":" << audio_written << ",\"audio_sample_rate\":48000,\"audio_channels\":2"
            << ",\"capture_target\":" << (Field(json, "capture_target") ? owe::Dump(*Field(json, "capture_target")) : "null")
            << ",\"orthographic_capture_viewport\":" << (Field(json, "orthographic_capture_viewport") ? owe::Dump(*Field(json, "orthographic_capture_viewport")) : "null")
            << ",\"layer_selection\":" << (Field(json, "layer_selection") ? owe::Dump(*Field(json, "layer_selection")) : "null")
            << ",\"capture_source\":{\"render_target\":" << Quote(wallpaper.readback().source_render_target)
            << ",\"pass\":" << Quote(wallpaper.readback().source_pass)
            << ",\"texture_version\":" << wallpaper.readback().source_texture_version
            << ",\"width\":" << wallpaper.readback().source_width
            << ",\"height\":" << wallpaper.readback().source_height << '}'
            << ",\"runtime_video_rate_overrides\":" << wallpaper.offlineVideoRateOverrides()
            << ",\"wall_seconds\":" << std::chrono::duration<double>(std::chrono::steady_clock::now() - start).count()
            << ",\"error\":" << Quote(error) << ",\"diagnostics\":[";
        bool first = true;
        for (const auto& diagnostic : wallpaper.offlineDiagnostics()) {
            if (!first) out << ',';
            first = false;
            out << Quote(diagnostic);
        }
        out << "]";
        if (job.trace_scene) {
            out << ",\"runtime_layers\":" << wallpaper.offlineSceneDescription()
                << ",\"runtime_projection\":" << wallpaper.offlineProjection()
                << ",\"runtime_animation_periods\":" << wallpaper.offlineAnimationPeriods() << ",\"runtime_dependencies\":[";
            bool first_dependency = true;
            for (const auto& item : wallpaper.offlineDependencies()) {
                if (!first_dependency) out << ',';
                first_dependency = false;
                out << "{\"owner\":" << item.owner << ",\"target\":" << item.target
                    << ",\"operation\":" << Quote(item.operation) << ",\"property\":" << Quote(item.property)
                    << ",\"binding\":" << Quote(item.binding) << ",\"initialization\":" << (item.initialization ? "true" : "false") << '}';
            }
            out << ']';
        }
        out << "}\n";
        WriteText(job.output / "result.json", out.str());
    };
    try {
        result("running");
        owe::SceneWallpaperConfig config;
        config.source_pkg_path = Utf8(job.source);
        config.assets_dir = Utf8(job.assets);
        config.cache_dir = Utf8(job.cache);
        config.fps = static_cast<uint32_t>(std::max<uint64_t>(1, (uint64_t(job.fps_num) + job.fps_den - 1) / job.fps_den));
        config.muted = false; // Offline mode has no host audio device; preserve authored audio.
        if (auto* properties = Field(json, "user_properties")) {
            auto object = properties->as_object();
            if (object.is_none()) throw std::runtime_error("user_properties must be an object");
            (*object)->iter().for_each([&](auto entry) {
                auto [key, value] = entry;
                config.user_properties.insert(key->clone(), value->clone());
            });
        }
        owe::RenderInitInfo info;
        info.width = static_cast<uint16_t>(job.width);
        info.height = static_cast<uint16_t>(job.height);
        info.max_readback_bytes = job.readback_budget;
        info.enable_valid_layer = job.validation;
        info.capture_target = job.capture_target;
        info.orthographic_capture_viewport = job.orthographic_capture_viewport;
        info.layer_selection = job.layer_selection;
        info.uuid = job.device_uuid;
        info.gpu_timing = job.gpu_timing;
        owe::OfflineOptions offline;
        offline.seed = job.seed;
        offline.epoch_ms = job.epoch_ms;
        offline.fps_num = job.fps_num;
        offline.fps_den = job.fps_den;
        offline.trace_scene = job.trace_scene;
        offline.readback_stride = job.output_stride;
        offline.readback_phase = job.output_phase;
        if (Field(json, "output_frame_stride")) offline.readback_start = job.warmup;
        offline.video_rate_overrides = job.video_rate_overrides;
        if (!wallpaper.initOffline(std::move(config), std::move(info), offline))
            throw std::runtime_error(wallpaper.offlineError());
        RequireNoLoggedErrors();

        std::ofstream raw, index(job.output / "frames.jsonl", std::ios::binary);
        std::ofstream audio(job.output / "audio.f32le.partial", std::ios::binary);
        if (!index) throw std::runtime_error("cannot create frame index");
        if (!audio) throw std::runtime_error("cannot create audio stream");
        if (job.raw_stdout) {
#ifdef _WIN32
            if (_setmode(_fileno(stdout), _O_BINARY) < 0) throw std::runtime_error("cannot set binary stdout");
#endif
        } else {
            raw.open(job.output / "frames.rgba.partial", std::ios::binary);
            if (!raw) throw std::runtime_error("cannot create raw frame stream");
        }
        const double dt = static_cast<double>(job.fps_den) / job.fps_num;
        uint64_t next_audio_sample = 0;
        std::size_t next_input_event = 0;
        for (uint64_t frame = 0; frame < job.warmup + job.frames; ++frame) {
            auto frame_start = std::chrono::steady_clock::now();
            if (next_input_event < job.input_timeline.size() && job.input_timeline[next_input_event].first == frame)
                ApplyPointerInput(job.input, job.input_timeline[next_input_event++].second);
            if (!wallpaper.step(frame, dt, job.input)) throw std::runtime_error(wallpaper.offlineError());
            const double step_ms = std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now()-frame_start).count();
            RequireNoLoggedErrors();
            const auto& pixels = wallpaper.readback();
            const bool read_pixels = offline.readsFrame(frame);
            if (!pixels.completed() || pixels.frame_index != frame || pixels.width != job.width ||
                pixels.height != job.height || pixels.row_pitch != job.width * 4 ||
                pixels.pixels.size() != (read_pixels ? uint64_t(job.width) * job.height * 4 : 0))
                throw std::runtime_error("completed frame violates shape/index contract");
            const auto& pcm = wallpaper.audioReadback();
            const auto expected_audio_start = AudioBoundary(frame, job.fps_num, job.fps_den);
            const auto expected_audio_end = AudioBoundary(frame + 1, job.fps_num, job.fps_den);
            if (pcm.sample_rate != 48000 || pcm.channels != 2 || pcm.samples.size() != size_t(pcm.frame_count) * 2 ||
                pcm.sample_start != next_audio_sample || pcm.sample_start != expected_audio_start ||
                pcm.frame_count != expected_audio_end - expected_audio_start ||
                !std::all_of(pcm.samples.begin(), pcm.samples.end(), [](float value) { return std::isfinite(value); }))
                throw std::runtime_error("authored audio violates PCM contract");
            next_audio_sample = pcm.sample_start + pcm.frame_count;
            if (frame < job.warmup) continue;
            audio.write(reinterpret_cast<const char*>(pcm.samples.data()), static_cast<std::streamsize>(pcm.samples.size() * sizeof(float)));
            if (!audio) throw std::runtime_error("audio stream write failed");
            audio_written += pcm.frame_count;
            if (!read_pixels) continue;
            if (job.raw_stdout) {
                if (std::fwrite(pixels.pixels.data(), 1, pixels.pixels.size(), stdout) != pixels.pixels.size())
                    throw std::runtime_error("frame consumer closed or failed");
            } else {
                raw.write(reinterpret_cast<const char*>(pixels.pixels.data()), static_cast<std::streamsize>(pixels.pixels.size()));
                if (!raw) throw std::runtime_error("raw frame write failed");
            }
            index << "{\"frame\":" << (frame - job.warmup) << ",\"simulation_frame\":" << frame
                  << ",\"pts_num\":" << (frame - job.warmup) * job.fps_den << ",\"pts_den\":" << job.fps_num
                  << ",\"step_ms\":" << step_ms
                  << ",\"gpu_total_ms\":" << OptionalReal(pixels.gpu_total_ms)
                  << ",\"gpu_draw_ms\":" << OptionalReal(pixels.gpu_draw_ms)
                  << ",\"render_ms\":" << std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now()-frame_start).count()
                  << "}\n";
            if (!index) throw std::runtime_error("frame index write failed");
            ++written;
            if (written == 1 || written % 120 == 0 || written == job.frames)
                std::cerr << "wpe-render: " << written << '/' << job.frames << " frames\n";
        }
        if (audio_written != AudioBoundary(job.warmup + job.frames, job.fps_num, job.fps_den) -
                             AudioBoundary(job.warmup, job.fps_num, job.fps_den))
            throw std::runtime_error("final audio duration does not match video timeline");
        index.flush();
        if (!index) throw std::runtime_error("frame index flush failed");
        audio.flush();
        if (!audio) throw std::runtime_error("audio stream flush failed");
        audio.close();
        fs::rename(job.output / "audio.f32le.partial", job.output / "audio.f32le");
        if (job.raw_stdout) {
            if (std::fflush(stdout) != 0) throw std::runtime_error("frame consumer flush failed");
        } else {
            raw.flush();
            if (!raw) throw std::runtime_error("raw frame flush failed");
            raw.close();
            fs::rename(job.output / "frames.rgba.partial", job.output / "frames.rgba");
        }
        result("complete");
        return 0;
    } catch (const std::exception& error) {
        result("failed", error.what());
        throw;
    }
}

int Inspect(const fs::path& source) {
    auto path = source;
    if (fs::is_directory(path)) path /= fs::is_regular_file(path / "scene.pkg") ? "scene.pkg" : "scene.json";
    auto document = owe::wpscene::LoadSceneDocumentFromSource(Utf8(path));
    if (document.is_none()) throw std::runtime_error("scene metadata could not be parsed");
    RequireNoLoggedErrors();
    std::cout << "{\"schema_version\":1,\"status\":\"metadata_parsed\",\"source\":" << Quote(Utf8(path))
              << ",\"pkg_version\":" << document->metadata.pkg_version
              << ",\"objects\":" << document->objects.len().to_primitive()
              << ",\"render_validated\":false,\"objects_detail\":[";
    bool first = true;
    for (const auto& object : document->objects) {
        if (!first) std::cout << ',';
        first = false;
        std::cout << "{\"id\":" << object.metadata.id.to_primitive()
                  << ",\"name\":" << Quote(object.metadata.name)
                  << ",\"authored\":" << owe::Dump(object.authored) << '}';
    }
    std::cout << "]}\n";
    return 0;
}
} // namespace

int main(int argc, char** argv) {
    rstd::log::set_logger(logger);
    rstd::log::set_max_level(rstd::log::LevelFilter::Info);
    try {
        auto args = Arguments(argc, argv);
        if (args.size() == 2 && args[1] == "--version") {
            std::cout << "wpe-render 0.1-dev upstream=" << kBase << " source=" << WPE_RENDER_SOURCE_DIGEST
                      << " features=sparse-readback-v1\n";
            return 0;
        }
        if (args.size() == 4 && args[1] == "render" && args[2] == "--job") return Render(Path(args[3]));
        if (args.size() == 4 && args[1] == "inspect" && args[2] == "--source") return Inspect(Path(args[3]));
        std::cerr << "Usage: wpe-render render --job job.json\n"
                     "       wpe-render inspect --source scene.pkg\n";
        return 2;
    } catch (const std::exception& error) {
        std::cerr << "wpe-render: " << error.what() << '\n';
        return 1;
    }
}
