// 差分测试 rstd 一侧：cases.inc 按真实 rstd 编译。
#include "difftest.hpp"
#include <rstd/macro.hpp>

import rstd;
import rstd.cppstd;
import rstd.log;

namespace side_rstd
{
#include "cases.inc"
}
