// 差分测试 compat 一侧：同一份 cases.inc，rstd 名字经 owe/compat.hpp 的别名解析到 owe::compat。
#include "difftest.hpp"
#include <owe/compat.hpp>

namespace side_compat
{
#include "cases.inc"
}
