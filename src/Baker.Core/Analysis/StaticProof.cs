using System.Buffers.Binary;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Baker.Core;

/// <summary>
/// 没有任何已建模时间机制时的静止证明：由源场景与运行时证据证明这次捕获是一张静态图，或说清为什么证明不了。
/// 从 HybridLoopService 原样搬出（C2.2c），判据与理由文案不变。
/// </summary>
internal static class StaticProof
{
    /// <summary>
    /// 返回 null 表示这次捕获可由源与运行时证据证明为一张静态图；否则保留理由和可定位的所有者，供重新分配使用。
    /// 旧版返回 bool，判否时不留任何记录，是 plan 里"零候选零理由 unavailable"的直接来源。
    /// </summary>
    internal static SourceStaticUnresolved? Obstacle(JsonObject scene, ProjectSource source, string? assetsDirectory,
        JsonObject runtime, IReadOnlyCollection<int> bakedLayerIds)
    {
        static SourceStaticUnresolved Unproven(string detail, int? owner = null) => new(detail, owner, null);
        // 点名被烘图层的几种理由另带结构化的层名与"是不是粒子系统"，一行结论的中文据此说，不从英文明细里抠。
        SourceStaticUnresolved Named(string detail, int id, bool particle = false) => new(detail, id, new StaticLayerNaming(Name(id), particle));
        if (runtime["status"] is not JsonValue status || !status.TryGetValue<string>(out string? state) || state != "complete" || runtime["runtime_layers"] is not JsonArray layers ||
            runtime["runtime_dependencies"] is not JsonArray dependencies || runtime["runtime_animation_periods"] is not JsonArray periods)
            return Unproven("Runtime observation is incomplete, so a static capture cannot be proven.");
        var selected = new HashSet<int>(bakedLayerIds);
        if (selected.Count == 0) return Unproven("No layer is allocated to video, so there is nothing to capture.");
        if (scene["objects"] is not JsonArray objects) return Unproven("The source scene lists no objects.");
        bool noLights = !SceneAnalyzer.Walk(scene).OfType<JsonObject>().Any(node => node.ContainsKey("light"));
        var owners = new Dictionary<int, JsonObject>();
        foreach (JsonNode? node in objects)
        {
            if (node is not JsonObject owner || owner["id"] is not JsonValue idValue || !idValue.TryGetValue<int>(out int id) || !owners.TryAdd(id, owner))
                return Unproven("The source scene has an object without a unique integer id.");
        }
        string? Name(int id) => owners.TryGetValue(id, out JsonObject? owner) && owner["name"] is JsonValue name &&
            name.TryGetValue<string>(out string? text) && !string.IsNullOrWhiteSpace(text) ? text : null;
        string Describe(int id) => Name(id) is string text ? $"layer {id} \"{text}\"" : $"layer {id}";
        foreach (int id in selected)
            if (!owners.ContainsKey(id)) return Unproven($"Baked {Describe(id)} is absent from the source scene.");
        foreach (int id in selected)
            if (DynamicSourceMechanism(owners[id]) is (string key, string mechanism))
                return Named($"Baked {Describe(id)} contains {mechanism}; no analytic period was established for it either, so neither a loop nor a still image can be proven.", id, key == "particle");
        foreach (JsonNode? node in periods)
        {
            if (node is not JsonObject period || period["source_owner_layer_id"] is not JsonValue owner ||
                !owner.TryGetValue<int>(out int id) || period["mechanism"] is not JsonValue)
                return Unproven("A runtime animation period entry is malformed, so the observation cannot establish a still image.");
            if (selected.Contains(id)) return Unproven($"Runtime observation recorded an animation period on baked {Describe(id)}.", id);
        }
        foreach (JsonNode? node in dependencies)
        {
            if (node is not JsonObject dependency || dependency["owner"] is not JsonValue owner || dependency["target"] is not JsonValue target ||
                !owner.TryGetValue<int>(out int ownerId) || !target.TryGetValue<int>(out int targetId))
                return Unproven("A runtime dependency entry is malformed, so the observation cannot establish a still image.");
            if (!selected.Contains(ownerId) && !selected.Contains(targetId)) continue;
            if (dependency["operation"] is not JsonValue operation || !operation.TryGetValue<string>(out string? name) || name != "write" ||
                dependency["initialization"] is not JsonValue initialization || !initialization.TryGetValue<bool>(out bool initial) || !initial)
                return Named($"Baked {Describe(selected.Contains(ownerId) ? ownerId : targetId)} takes part in a runtime dependency that is not an initialization write.", selected.Contains(ownerId) ? ownerId : targetId);
        }
        foreach (int id in selected)
        {
            JsonObject[] observed = layers.OfType<JsonObject>().Where(layer =>
                layer["owner"] is JsonValue owner && owner.TryGetValue<int>(out int observedOwner) && observedOwner == id).ToArray();
            if (observed.Length == 0) return Unproven($"Runtime observation recorded no rendered layer for baked {Describe(id)}.", id);
            foreach (JsonObject layer in observed)
            {
                if (layer["has_mesh"] is not JsonValue mesh || !mesh.TryGetValue<bool>(out _) || layer["materials"] is not JsonArray materials)
                    return Unproven($"Runtime observation of baked {Describe(id)} omits its mesh flag or material list.", id);
                foreach (JsonNode? node in materials)
                {
                    if (node is not JsonObject material) return Unproven($"Runtime observation of baked {Describe(id)} has a malformed material entry.", id);
                    if (MaterialStaticObstacle(material, source, assetsDirectory, noLights) is string reason)
                        return Named($"Baked {Describe(id)}: {reason}.", id);
                }
            }
        }
        return null;
    }

    private static readonly (string Key, string Description)[] DynamicSourceMechanisms = [
        ("particle", "a particle system, whose emission and lifetimes are not a solved periodic mechanism"),
        ("puppet", "a puppet warp rig"),
        ("sound", "an audio source"),
        ("animation", "an authored animation"),
        ("animations", "authored animations"),
        ("animationlayers", "authored animation layers")];

    private static (string Key, string Description)? DynamicSourceMechanism(JsonObject owner)
    {
        foreach (JsonObject node in SceneAnalyzer.Walk(owner).OfType<JsonObject>())
        {
            if (node["script"] is not null) return ("script", "a script binding whose behavior over time is not proven");
            foreach (var (key, description) in DynamicSourceMechanisms)
                if (node.ContainsKey(key)) return (key, description);
        }
        return null;
    }

    /// <summary>
    /// 材质层面的静态障碍。role=effect 的材质与 <see cref="RuntimeTrackReader.AddMaterialClockUnresolved"/> 用同一口径：
    /// 特效的时间机制由 <see cref="ShaderPeriodAnalysis"/> 做源码级分析，走到这里说明它一个分量、一条未解析都没留下，
    /// 因此只拒绝运行时时钟与动态纹理，不再拿基础材质的 uniform 白名单去卡特效自带的参数（u_strength 之类）。
    /// 合成中间目标 _rt_* 是本层这一串 pass 自己的产物：跨层读写由上面的运行时依赖检查拦下，
    /// 读取整帧的图层更早就被判为必须实时、根本不在被烘集合里。
    /// </summary>
    private static string? MaterialStaticObstacle(JsonObject material, ProjectSource source, string? assetsDirectory, bool noLights)
    {
        if (!False(material["uses_audio_spectrum"])) return "a material reacts to the audio spectrum";
        if (!False(material["uses_system_media_thumbnail"])) return "a material reads the system media thumbnail";
        if (material["role"] is JsonNode role && (role is not JsonValue roleValue || !roleValue.TryGetValue<string>(out _)))
            return "a material role is not a string, so its temporal behavior is unknown";
        bool effect = string.Equals(material["role"]?.GetValue<string>(), "effect", StringComparison.Ordinal);
        if (material["active_uniforms"] is not JsonArray uniforms) return "a material omits its active uniform list";
        if (material["textures"] is not JsonArray textures) return "a material omits its texture list";
        foreach (JsonNode? node in uniforms)
        {
            if (node is not JsonValue value || !value.TryGetValue<string>(out string? name))
                return "a material active uniform entry is not a string";
            if (effect ? RuntimeTrackReader.IsRuntimeClock(name) : !StaticUniform(name, noLights))
                return $"material uniform \"{name}\" is not proven time-independent";
        }
        foreach (JsonNode? node in textures)
        {
            if (node is not JsonValue value || !value.TryGetValue<string>(out string? name))
                return "a material texture entry is not a string";
            if (effect && name.StartsWith("_rt_", StringComparison.Ordinal)) continue;
            if (!StaticTexture(source, assetsDirectory, name)) return $"material texture \"{name}\" is not a proven still image";
        }
        return null;
    }

    private static bool False(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out bool flag) && !flag;

    private static bool StaticUniform(string name, bool noLights) => name is "g_ModelViewProjectionMatrix" or "g_EyePosition" or
        "g_ModelMatrix" or "g_ViewProjectionMatrix" or "g_Color4" || Regex.IsMatch(name, @"^g_Texture\d+(?:Rotation|Translation|Resolution)$",
            RegexOptions.CultureInvariant) || noLights && name.StartsWith("g_Lights", StringComparison.Ordinal);

    private static bool StaticTexture(ProjectSource source, string? assetsDirectory, string texture)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(texture)) return true;
            if (texture.StartsWith("_rt_", StringComparison.Ordinal)) return false;
            string[] resources = texture.EndsWith(".tex", StringComparison.OrdinalIgnoreCase)
                ? [texture] : [texture, "materials/" + texture + ".tex"];
            foreach (string resource in resources)
            {
                if (source.Contains(resource))
                {
                    if (StaticTextureHeader(source.ReadPrefix(resource, 256))) return true;
                    continue;
                }
                // 官方 assets 目录里的通用纹理（util/white 之类）与项目包内资源同等看待：同一份头部校验。
                if (AssetTexturePrefix(assetsDirectory, resource) is byte[] header && StaticTextureHeader(header)) return true;
            }
            return false;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentOutOfRangeException) { return false; }
    }

    private static byte[]? AssetTexturePrefix(string? assetsDirectory, string resource)
    {
        if (assetsDirectory is null) return null;
        string path = ProjectSource.ContainedPath(assetsDirectory, resource);
        if (!File.Exists(path)) return null;
        using var file = File.OpenRead(path);
        var header = new byte[(int)Math.Min(file.Length, 256)];
        file.ReadExactly(header);
        return header;
    }

    private static bool StaticTextureHeader(byte[] header)
    {
        if (header.Length < 75 || !header.AsSpan(0, 9).SequenceEqual("TEXV0005\0"u8) ||
            !header.AsSpan(9, 9).SequenceEqual("TEXI0001\0"u8) || (BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(22, 4)) & 0x24) != 0 ||
            !header.AsSpan(46, 4).SequenceEqual("TEXB"u8) || header[53] is < (byte)'1' or > (byte)'4') return false;
        int version = header[53] - '0', offset = 55;
        int count = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(offset, 4)); offset += 4;
        if (count != 1) return false;
        if (version >= 3)
        {
            int type = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(offset, 4)); offset += 4;
            if (type == 100) return false;
            if (version >= 4) offset += 4;
            if (type != -1) return true;
        }
        if (offset + 16 + (version >= 2 ? 8 : 0) > header.Length) return false;
        // mip 数量之后紧跟第一级 mip 的宽高与数据，多级 mipmap 不改变这个偏移，也不会是视频容器
        // （封装的动图走 type==100 或下面的 ftyp/EBML 判据）。要求恰好一级会把普通带 mipmap 的
        // 贴图（官方 assets 里的 util/white 就是四级）误判成非静态。
        int mips = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(offset, 4)); offset += 12;
        if (mips < 1) return false;
        // TEXB0002 起每级 mip 带 [是否 LZ4 压缩, 解压后字节数]。压缩只改变存储，不改变内容：解出开头 12 个字节，
        // 照未压缩时的同一判据排除视频容器头。
        bool lz4 = false;
        int decodedSize = 0;
        if (version >= 2)
        {
            int compression = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(offset, 4));
            if (compression is not (0 or 1)) return false;
            lz4 = compression == 1;
            decodedSize = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(offset + 4, 4));
            offset += 8;
        }
        int size = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(offset, 4)); offset += 4;
        if (size < 0) return false;
        if (!lz4) return size < 12 || offset + 12 <= header.Length && !IsVideoContainerPrefix(header.AsSpan(offset, 12));
        if (decodedSize < 0) return false;
        if (decodedSize < 12) return true;
        return TryLz4Prefix(header.AsSpan(offset, Math.Min(size, header.Length - offset)), 12, out byte[] decoded) &&
            !IsVideoContainerPrefix(decoded);
    }

    private static bool IsVideoContainerPrefix(ReadOnlySpan<byte> prefix) =>
        prefix.Slice(4, 4).SequenceEqual("ftyp"u8) || prefix.Slice(0, 4).SequenceEqual(new byte[] { 0x1A, 0x45, 0xDF, 0xA3 });

    /// <summary>
    /// 只解 LZ4 块开头的 <paramref name="count"/> 个字节（token、literal、两字节回指距离、匹配长度，按 LZ4 块格式）。
    /// 输入在凑够之前用完或回指越界时返回 false，调用方按未证明处理。
    /// </summary>
    internal static bool TryLz4Prefix(ReadOnlySpan<byte> input, int count, out byte[] output)
    {
        output = [];
        var decoded = new List<byte>(count);
        int position = 0;
        while (decoded.Count < count)
        {
            if (position >= input.Length) return false;
            int token = input[position++];
            if (!TryLength(input, ref position, token >> 4, out int literal)) return false;
            for (int index = 0; index < literal && decoded.Count < count; ++index)
            {
                if (position >= input.Length) return false;
                decoded.Add(input[position++]);
            }
            if (decoded.Count >= count) break;
            if (position + 2 > input.Length) return false;
            int distance = input[position] | input[position + 1] << 8;
            position += 2;
            if (distance == 0 || distance > decoded.Count) return false;
            // 匹配长度下限（半字节 + 4）已够补满前缀时不读扩展长度字节：纯色大图的扩展字节可以长过预读范围。
            int match = (token & 15) + 4;
            if (match < count - decoded.Count)
            {
                if (!TryLength(input, ref position, token & 15, out match)) return false;
                match += 4;
            }
            for (int index = 0; index < match && decoded.Count < count; ++index) decoded.Add(decoded[decoded.Count - distance]);
        }
        output = [.. decoded];
        return true;

        static bool TryLength(ReadOnlySpan<byte> bytes, ref int at, int nibble, out int length)
        {
            length = nibble;
            if (nibble != 15) return true;
            int more;
            do
            {
                if (at >= bytes.Length) return false;
                more = bytes[at++];
                length += more;
            } while (more == 255);
            return true;
        }
    }
}
