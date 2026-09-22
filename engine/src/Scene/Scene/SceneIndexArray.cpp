module;

module wescene.scene;
import rstd;
import rstd.cppstd;

using namespace rstd::prelude;
using namespace owe;

SceneIndexArray::SceneIndexArray(usize index_count)
    : m_data(Vec<rstd::uint32_t>::with_capacity(index_count)) {
    for (usize i {}; i < index_count; ++i) m_data.push(0);
}
SceneIndexArray::SceneIndexArray(slice<rstd::uint32_t> data)
    : m_data(Vec<rstd::uint32_t>::with_capacity(data.len())), m_size(data.len()) {
    for (rstd::uint32_t value : data) m_data.push(rstd::move(value));
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
