module;

module wescene.utils;
import wescene.core;
import rstd;
import rstd.cppstd;

using namespace rstd::prelude;
using namespace owe;

FpsCounter::FpsCounter(): m_fps(0), m_frameCount(0), m_startTime(rstd::time::Instant::now()) {}

namespace
{
constexpr auto timeout = rstd::time::Duration::from_secs(u64(2));
}

void FpsCounter::RegisterFrame() {
    auto now  = rstd::time::Instant::now();
    auto diff = now - m_startTime;

    m_frameCount++;
    if (diff > timeout) {
        m_fps        = u32(double(m_frameCount.to_primitive()) / diff.as_secs_f64());
        m_frameCount = u32();
        m_startTime  = now;
        std::cerr << m_fps.to_primitive() << std::endl;
    }
}
