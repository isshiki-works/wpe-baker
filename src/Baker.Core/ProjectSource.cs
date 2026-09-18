using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>Read-only project view. PKG offsets are checked before any resource is exposed.</summary>
public sealed class ProjectSource : IDisposable
{
    public const string DigestScope = "project-source-files-sha256-v2";
    private sealed record Entry(long Offset, int Length);
    private readonly FileStream? package;
    private readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonDocumentOptions WpeJsonOptions = new() { AllowTrailingCommas = true };
    public string SourcePath { get; }
    public string DirectoryPath { get; }
    public string Kind { get; }
    public int? PackageVersion { get; }
    public string SceneResource => Path.ChangeExtension(Path.GetFileName(SourcePath), ".json");

    public ProjectSource(string source)
    {
        source = Path.GetFullPath(source);
        if (Directory.Exists(source))
            source = File.Exists(Path.Combine(source, "project.json")) ? Path.Combine(source, "project.json")
                : File.Exists(Path.Combine(source, "scene.pkg")) ? Path.Combine(source, "scene.pkg")
                : File.Exists(Path.Combine(source, "scene.json")) ? Path.Combine(source, "scene.json")
                : Path.Combine(source, "project.json");
        // 用户手里的常是 scene.json，而作品里只放了 scene.pkg（渲染器本来就是包优先）。以前只在读到
        // project.json 之后才做这次替换，直接给 scene.json 路径的会被当成"来源不存在"；两处都认。
        if (!File.Exists(source) && Path.GetExtension(source).Equals(".json", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(Path.ChangeExtension(source, ".pkg"))) source = Path.ChangeExtension(source, ".pkg");
        if (!File.Exists(source)) throw new FileNotFoundException("Wallpaper source does not exist.", source);
        SourcePath = source;
        DirectoryPath = Path.GetDirectoryName(source)!;
        Kind = "scene";
        if (Path.GetFileName(source).Equals("project.json", StringComparison.OrdinalIgnoreCase))
        {
            var project = ParseWpeJsonObject(File.ReadAllText(source, Utf8), source);
            Kind = project["type"]?.GetValue<string>()?.ToLowerInvariant() ?? "unknown";
            if (Kind != "scene") return;
            var file = project["file"]?.GetValue<string>() ?? "scene.json";
            if (NormalizeResource(file).Contains('/')) throw new InvalidDataException("Nested scene entry files are not supported by this renderer integration.");
            source = ContainedPath(DirectoryPath, file);
        }
        // Match the renderer's package-first lookup for the selected scene entry.
        if (Path.GetExtension(source).Equals(".json", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.ChangeExtension(source, ".pkg")))
            source = Path.ChangeExtension(source, ".pkg");
        SourcePath = source;
        if (!File.Exists(source)) throw new FileNotFoundException("Selected scene entry does not exist.", source);
        if (!Path.GetExtension(source).Equals(".pkg", StringComparison.OrdinalIgnoreCase)) return;
        package = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var stamp = ReadString(64);
            if (!stamp.StartsWith("PKGV", StringComparison.Ordinal) ||
                !int.TryParse(stamp.AsSpan(4), out int version) || version is < 1 or > 24)
                throw new InvalidDataException($"Unsupported PKG stamp: {stamp}");
            PackageVersion = version;
            int count = ReadInt();
            if (count < 0 || count > 1_000_000 || count > package.Length / 13)
                throw new InvalidDataException("Invalid PKG entry count.");
            var directory = new List<(string Name, Entry Entry)>(count);
            for (int i = 0; i < count; ++i)
            {
                string name = NormalizeResource(ReadString(4096));
                int offset = ReadInt(), length = ReadInt();
                if (offset < 0 || length < 0) throw new InvalidDataException($"Invalid PKG entry: {name}");
                directory.Add((name, new Entry(offset, length)));
            }
            long header = package.Position;
            foreach (var (name, item) in directory)
            {
                long start = checked(header + item.Offset);
                if (start > package.Length || item.Length > package.Length - start)
                    throw new InvalidDataException($"PKG entry exceeds source: {name}");
                var absolute = item with { Offset = start };
                if (entries.TryGetValue(name, out var previous))
                {
                    // Official packages can repeat an identical font/resource. Windows
                    // has one path for those entries, so only byte-identical aliases can
                    // be represented without changing which resource is read.
                    if (!SamePayload(previous, absolute)) throw new InvalidDataException($"Conflicting duplicate PKG entry: {name}");
                }
                else entries.Add(name, absolute);
            }
        }
        catch { package.Dispose(); throw; }
    }

    private bool SamePayload(Entry first, Entry second)
    {
        if (first.Length != second.Length) return false;
        if (first.Offset == second.Offset) return true;
        Span<byte> left = stackalloc byte[8192], right = stackalloc byte[8192];
        for (int offset = 0; offset < first.Length;)
        {
            int count = Math.Min(left.Length, first.Length - offset);
            package!.Position = first.Offset + offset;
            package.ReadExactly(left[..count]);
            package.Position = second.Offset + offset;
            package.ReadExactly(right[..count]);
            if (!left[..count].SequenceEqual(right[..count])) return false;
            offset += count;
        }
        return true;
    }

    private int ReadInt()
    {
        Span<byte> value = stackalloc byte[4];
        package!.ReadExactly(value);
        return BinaryPrimitives.ReadInt32LittleEndian(value);
    }

    private string ReadString(int maximum)
    {
        int length = ReadInt();
        if (length < 0 || length > maximum) throw new InvalidDataException("Invalid PKG string length.");
        var bytes = new byte[length];
        package!.ReadExactly(bytes);
        return Utf8.GetString(bytes);
    }

    public static string NormalizeResource(string name)
    {
        name = name.Replace('\\', '/');
        var parts = name.Split('/');
        if (string.IsNullOrWhiteSpace(name) || Path.IsPathRooted(name) ||
            parts.Any(p => p is "" or "." or ".." || p.EndsWith('.') || p.EndsWith(' ') ||
                p.Any(c => c < 32 || "<>:\"|?*".Contains(c)) || IsDeviceName(p)))
            throw new InvalidDataException($"Unsafe resource path: {name}");
        return name;
    }

    private static bool IsDeviceName(string part)
    {
        string stem = part.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" ||
            (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '1' and <= '9');
    }

    public static string ContainedPath(string root, string resource)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string target = Path.GetFullPath(Path.Combine(root, NormalizeResource(resource)));
        if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Resource escaped project root.");
        EnsureNoReparsePoints(target, root);
        return target;
    }

    /// <summary>
    /// 不跟随链接（junction、卷挂载点、目录符号链接）：资源不能顺着项目<b>内部</b>的链接跳到别处。
    /// <paramref name="root"/> 自身与它的祖先不看——逃逸检查在上一行的 StartsWith 就已经完成，向上越过 root
    /// 对安全目的零贡献，却会把正常情况全拒掉：mklink /J 搬过的 Steam 库、NTFS 卷挂载点、重定向过的用户目录
    /// 都是祖先带 ReparsePoint，源与输出只要落在下面就 analyze/bake 直接失败。
    /// <paramref name="root"/> 为 null 时只看 <paramref name="path"/> 这一级（输出目录与成品目录的校验点）。
    /// </summary>
    public static void EnsureNoReparsePoints(string path, string? root = null)
    {
        string? current = Path.GetFullPath(path);
        string? boundary = root is null
            ? Path.GetDirectoryName(current)
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        while (current is not null && !string.Equals(current, boundary, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"Linked resource path is unsupported: {current}");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(current);
        }
    }

    public bool Contains(string resource)
    {
        resource = NormalizeResource(resource);
        return entries.ContainsKey(resource) || File.Exists(ContainedPath(DirectoryPath, resource));
    }

    public byte[] Read(string resource, int maximumBytes = 32 * 1024 * 1024)
    {
        resource = NormalizeResource(resource);
        // Package entries are authoritative; loose files include project metadata.
        if (entries.TryGetValue(resource, out var item))
        {
            if (item.Length > maximumBytes) throw new InvalidDataException($"Resource too large to read as metadata: {resource}");
            byte[] bytes = new byte[item.Length];
            int done = 0;
            while (done < bytes.Length)
            {
                int n = RandomAccess.Read(package!.SafeFileHandle, bytes.AsSpan(done), item.Offset + done);
                if (n == 0) throw new EndOfStreamException(resource);
                done += n;
            }
            return bytes;
        }
        string file = ContainedPath(DirectoryPath, resource);
        if (new FileInfo(file).Length > maximumBytes) throw new InvalidDataException($"Resource too large: {resource}");
        return File.ReadAllBytes(file);
    }

    /// <summary>Reads only a bounded resource prefix, including from packaged sources.</summary>
    public byte[] ReadPrefix(string resource, int maximumBytes)
    {
        if (maximumBytes < 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        resource = NormalizeResource(resource);
        if (entries.TryGetValue(resource, out var item))
        {
            int count = Math.Min(item.Length, maximumBytes);
            var bytes = new byte[count];
            package!.Position = item.Offset;
            package.ReadExactly(bytes);
            return bytes;
        }
        string path = ContainedPath(DirectoryPath, resource);
        using var input = File.OpenRead(path);
        var prefix = new byte[Math.Min(checked((int)Math.Min(input.Length, int.MaxValue)), maximumBytes)];
        input.ReadExactly(prefix);
        return prefix;
    }

    internal static JsonObject ParseWpeJsonObject(string json, string resource) =>
        JsonNode.Parse(json, documentOptions: WpeJsonOptions)?.AsObject()
        ?? throw new InvalidDataException($"Expected JSON object: {resource}");

    internal static JsonObject ParseWpeJsonObject(ReadOnlySpan<byte> utf8Json, string resource) =>
        JsonNode.Parse(utf8Json, documentOptions: WpeJsonOptions)?.AsObject()
        ?? throw new InvalidDataException($"Expected JSON object: {resource}");

    public JsonObject ReadJson(string resource) => ParseWpeJsonObject(Read(resource), resource);

    public async Task<string> SourceHashAsync(CancellationToken cancellationToken = default)
    {
        // A directory project depends on all its files, not only scene.json.
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in EnumerateLooseFiles().Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] name = Utf8.GetBytes(path);
            hash.AppendData(BitConverter.GetBytes(name.Length));
            hash.AppendData(name);
            await using var file = File.OpenRead(ContainedPath(DirectoryPath, path));
            hash.AppendData(BitConverter.GetBytes(file.Length));
            byte[] buffer = new byte[128 * 1024];
            int n;
            while ((n = await file.ReadAsync(buffer, cancellationToken)) != 0) hash.AppendData(buffer, 0, n);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private IEnumerable<string> EnumerateLooseFiles()
    {
        var pending = new Stack<string>();
        pending.Push(DirectoryPath);
        while (pending.TryPop(out var directory))
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                string resource = NormalizeResource(Path.GetRelativePath(DirectoryPath, path));
                // WPE writes this compiled-shader cache during ordinary playback. It is
                // derived data, not a source resource or a portable project dependency.
                if (resource.Equals("shaders/blobsSM40", StringComparison.OrdinalIgnoreCase)) continue;
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"Linked source resource is unsupported: {path}");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
                else yield return resource;
            }
        }
    }

    public async Task ExtractAsync(string destination, CancellationToken cancellationToken = default)
    {
        destination = Path.GetFullPath(destination);
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException("Extraction requires a new destination directory.");
        string rootPrefix = DirectoryPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (destination.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Extraction destination cannot be inside the source project.");
        EnsureNoReparsePoints(destination);
        var loose = EnumerateLooseFiles().Where(p => !p.Equals(Path.GetFileName(SourcePath), StringComparison.OrdinalIgnoreCase)
            || package is null).ToArray();
        var resources = loose.Concat(entries.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var resource in resources) _ = ContainedPath(destination, resource);
        Directory.CreateDirectory(destination);
        byte[] buffer = new byte[128 * 1024];
        foreach (var resource in resources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string target = ContainedPath(destination, resource);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, true);
            if (entries.TryGetValue(resource, out var item))
            {
                long done = 0;
                while (done < item.Length)
                {
                    int n = await RandomAccess.ReadAsync(package!.SafeFileHandle,
                        buffer.AsMemory(0, (int)Math.Min(buffer.Length, item.Length - done)), item.Offset + done, cancellationToken);
                    if (n == 0) throw new EndOfStreamException(resource);
                    await output.WriteAsync(buffer.AsMemory(0, n), cancellationToken);
                    done += n;
                }
            }
            else
            {
                await using var input = File.OpenRead(ContainedPath(DirectoryPath, resource));
                await input.CopyToAsync(output, cancellationToken);
            }
        }
    }

    public void Dispose() => package?.Dispose();
}
