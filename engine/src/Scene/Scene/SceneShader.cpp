module;

module wescene.scene;
import rstd;

using namespace owe;
using namespace rstd::prelude;

void ShaderValue::fromSlice(slice<value_type> values) noexcept {
    m_size    = values.len();
    m_layout  = UniformValueLayout::Linear(values.len());
    m_dynamic = values.len() > m_value.len();
    if (m_dynamic) {
        m_dynamic_value.clear();
        m_dynamic_value.reserve(values.len());
        for (usize index {}; index < values.len(); ++index) {
            m_dynamic_value.push_back(values[index]);
        }
    } else {
        for (usize index {}; index < values.len(); ++index) m_value[index] = values[index];
    }
}
