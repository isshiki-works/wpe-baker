export module wescene.resource:error;
import rstd;

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

export namespace rstd
{

template<>
struct Impl<fmt::Display, owe::resource::ResourceError> : ImplBase<owe::resource::ResourceError> {
    auto fmt(fmt::Formatter& formatter) const -> bool {
        return formatter.write_fmt(fmt::Arguments::make("{}", this->self().message));
    }
};

} // namespace rstd

