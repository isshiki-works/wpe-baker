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

template<>
struct Impl<fmt::Debug, owe::resource::ResourceError> : ImplBase<owe::resource::ResourceError> {
    auto fmt(fmt::Formatter& formatter) const -> bool {
        return formatter.write_fmt(fmt::Arguments::make("ResourceError(kind={}, message={})",
                                                        static_cast<int>(this->self().kind),
                                                        this->self().message));
    }
};

template<>
struct Impl<error::Error, owe::resource::ResourceError>
    : DefaultInImpl<error::Error, owe::resource::ResourceError> {};

} // namespace rstd

static_assert(rstd::Impled<owe::resource::ResourceError, rstd::error::Error>);
