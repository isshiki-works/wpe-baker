module;
#include <owe/compat.hpp>
#include <owe/std.hpp>
export module wescene.resource:error;

export namespace owe::resource
{

using namespace rstd::prelude;

enum class ResourceErrorKind
{
    MissingDefinition,
    MissingContent,
    BackendFailure,
};

struct ResourceError {
    ResourceErrorKind kind { ResourceErrorKind::BackendFailure };
    String            message;
};

} // namespace owe::resource

namespace owe::compat
{

template<>
struct Impl<fmt::Display, owe::resource::ResourceError> : ImplBase<owe::resource::ResourceError> {
    auto fmt(fmt::Formatter& formatter) const -> bool {
        return formatter.write_fmt(fmt::Arguments::make("{}", this->self().message));
    }
};

} // namespace rstd

