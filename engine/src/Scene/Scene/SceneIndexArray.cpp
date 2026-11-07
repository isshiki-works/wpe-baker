module;

#include <cstring>

module wescene.scene;
import rstd;
import rstd.cppstd;

using namespace rstd::prelude;
using namespace owe;

SceneIndexArray::SceneIndexArray(usize index_count) {
    m_data.resize(index_count, 0u);
}
SceneIndexArray::SceneIndexArray(slice<rstd::uint32_t> data)
    : m_size(data.len()) {
    m_data.resize(data.len(), 0u);
    if (! data.is_empty()) {
        std::memcpy(m_data.begin(),
                    data.as_raw_ptr(),
                    data.len().to_primitive() * sizeof(rstd::uint32_t));
    }
}

SceneIndexArray::SceneIndexArray(SceneIndexArray&& other) noexcept
    : m_data(rstd::move(other.m_data)),
      m_size(other.m_size),
      m_render_size(other.m_render_size),
      m_id(other.m_id),
      m_generation(other.m_generation) {}

bool SceneIndexArray::IncreaseCheckSet(usize nsize) {
    if (nsize > CapacitySizeof()) return false;
    if (nsize > DataSizeOf()) {
        m_size = nsize / Unit_Byte_Size + (nsize % Unit_Byte_Size == usize() ? usize() : usize(1));
    }
    return true;
}
