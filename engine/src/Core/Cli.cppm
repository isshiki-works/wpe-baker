module;

#include <cstdio>
#include <cstring>

export module wescene.cli;

import rstd.argparse;
import rstd.cppstd;

using namespace rstd::prelude;

export namespace owe::cli
{

struct ParseExit {
    int code;
};

} // namespace owe::cli

namespace
{

void WriteMessage(ref<str> text, rstd::argparse::OutputTarget::Tag target) {
    FILE* stream = target == rstd::argparse::OutputTarget::Tag::Stdout ? stdout : stderr;
    auto  size   = text.size().to_primitive();
    std::fwrite(text.data(), 1, size, stream);
    if (size == 0 || text[usize(size - 1)] != u8('\n')) std::fputc('\n', stream);
}

auto Build(rstd::argparse::Command&& command)
    -> Result<rstd::argparse::Parser, owe::cli::ParseExit> {
    auto built = rstd::move(command).build();
    if (built.is_ok()) return Ok(rstd::move(built).unwrap());

    auto message = rstd::format("error: invalid command definition: {}\n", built.unwrap_err());
    WriteMessage(message.as_str(), rstd::argparse::OutputTarget::Tag::Stderr);
    return Err(owe::cli::ParseExit { 2 });
}

auto Finish(rstd::argparse::Parser&              parser,
            Result<rstd::argparse::ParseOutcome<rstd::argparse::Matches>,
                   rstd::argparse::ParseError>&& parsed)
    -> Result<rstd::argparse::Matches, owe::cli::ParseExit> {
    if (parsed.is_err()) {
        auto report = parser.render_error(parsed.as_ref().unwrap_err());
        WriteMessage(report.text(), report.target());
        return Err(owe::cli::ParseExit { report.exit_code().to_primitive() });
    }

    auto outcome = rstd::move(parsed).unwrap();
    if (outcome.is_Display()) {
        auto request = rstd::move(outcome).as_Display().request;
        WriteMessage(request.text(), request.target());
        return Err(owe::cli::ParseExit { request.exit_code().to_primitive() });
    }
    return Ok(rstd::move(outcome).as_Parsed().value);
}

} // namespace

export namespace owe::cli
{

auto ParseEnv(rstd::argparse::Command&& command) -> Result<rstd::argparse::Matches, ParseExit> {
    auto built = Build(rstd::move(command));
    if (built.is_err()) return Err(built.unwrap_err());
    auto parser = rstd::move(built).unwrap();
    return Finish(parser, parser.parse_env());
}

auto ParseArgs(rstd::argparse::Command&& command, int argc, char** argv)
    -> Result<rstd::argparse::Matches, ParseExit> {
    auto arguments = Vec<rstd::ffi::OsString>::with_capacity(static_cast<usize>(argc));
    for (int i = 0; i < argc; ++i) {
        auto bytes = slice<byte>::from_raw_parts(reinterpret_cast<const byte*>(argv[i]),
                                                 usize(std::strlen(argv[i])));
        auto os    = ref<rstd::ffi::OsStr>::from_encoded_bytes_unchecked(rstd::as_u8_slice(bytes));
        arguments.push(rstd::ffi::OsString::from(os));
    }

    auto built = Build(rstd::move(command));
    if (built.is_err()) return Err(built.unwrap_err());
    auto parser = rstd::move(built).unwrap();
    return Finish(parser, parser.parse_from(rstd::move(arguments)));
}

} // namespace owe::cli
