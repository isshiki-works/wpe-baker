using System.Buffers.Binary;
using System.Text;

namespace Baker.Core;

/// <summary>Writes native WPE TEX containers without resampling or changing pixel order.</summary>
public static class TextureContainer
{
    public static async Task WriteRgbaAsync(string destination, uint width, uint height, ReadOnlyMemory<byte> rgba,
        CancellationToken cancellationToken = default)
    {
        if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue ||
            (UInt128)width * height * 4 != (ulong)rgba.Length)
            throw new ArgumentException("RGBA dimensions and byte count do not match.");
        using var header = new MemoryStream();
        using (var writer = new BinaryWriter(header, Encoding.ASCII, leaveOpen: true))
        {
            WritePreamble(writer, width, height, flags: 2);
            writer.Write(Encoding.ASCII.GetBytes("TEXB0001\0"));
            writer.Write(1); // image count
            writer.Write(1); // mip count
            writer.Write(width);
            writer.Write(height);
            writer.Write(rgba.Length);
        }
        await using var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true);
        await file.WriteAsync(header.ToArray(), cancellationToken);
        await file.WriteAsync(rgba, cancellationToken);
    }

    public static async Task WriteVideoAsync(string destination, string mp4Path, uint width, uint height,
        CancellationToken cancellationToken = default)
    {
        if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue)
            throw new ArgumentException("Video dimensions must fit the TEX dimension fields.");
        await using var input = File.OpenRead(mp4Path);
        // 上限依据见 EmbeddedVideoBudget.MaximumBytes（WPE 实测）；bake 在渲染前与编码后各查一次，这里只是最后一道。
        if (input.Length < 16 || input.Length > EmbeddedVideoBudget.MaximumBytes)
            throw new InvalidDataException("Embedded video must be between 16 bytes and 2 GiB - 1 byte; Wallpaper Engine does not display a larger TEX video.");
        byte[] signature = new byte[12];
        await input.ReadExactlyAsync(signature, cancellationToken);
        if (!signature.AsSpan(4, 4).SequenceEqual("ftyp"u8))
            throw new InvalidDataException("Expected an MP4 ftyp container; media contents must be verified with ffprobe before packaging.");
        input.Position = 0;
        using var header = new MemoryStream();
        using (var writer = new BinaryWriter(header, Encoding.ASCII, leaveOpen: true))
        {
            // Format is RGBA8 (0); flags 0x22 are clamp plus the native video flag.
            WritePreamble(writer, width, height, flags: 0x22);
            writer.Write(Encoding.ASCII.GetBytes("TEXB0004\0"));
            writer.Write(1); // image count
            writer.Write(-1); // no FreeImage container type; the body is an MP4
            writer.Write(0); // TEXB4 reserved
            writer.Write(1); // mip count
            writer.Write(width);
            writer.Write(height);
            writer.Write(0); // not LZ4 compressed
            writer.Write(0); // no LZ4 unpacked length
            writer.Write((int)input.Length);
        }
        if (header.Length != 91) throw new InvalidDataException("Unexpected TEX video header layout.");
        await using var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true);
        await file.WriteAsync(header.ToArray(), cancellationToken);
        await input.CopyToAsync(file, cancellationToken);
    }

    /// <summary>TEX 前导里的只读信息：像素格式、标志与存储尺寸。不解码像素。</summary>
    public readonly record struct TextureHeader(int Format, uint Flags, uint Width, uint Height)
    {
        /// <summary>0x20 是原生视频标志；此时容器体是 MP4，实际位深由解码器像素格式决定。</summary>
        public bool IsVideo => (Flags & 0x20) != 0;
    }

    /// <summary>写入端固化的 8bit 无符号格式取值。</summary>
    public const int FormatRgba8 = 0;

    /// <summary>只有已确证的 8bit 无符号格式才算已知；其余取值一律按未知处理。</summary>
    public static bool IsEightBitUnsignedFormat(int format) => format == FormatRgba8;

    /// <summary>只读解析 TEXV0005/TEXI0001 前导；其它容器版本一律返回 false（未知不放行）。</summary>
    public static bool TryReadHeader(ReadOnlySpan<byte> preamble, out TextureHeader header)
    {
        header = default;
        if (preamble.Length < 34 || !preamble[..9].SequenceEqual("TEXV0005\0"u8) ||
            !preamble.Slice(9, 9).SequenceEqual("TEXI0001\0"u8)) return false;
        header = new TextureHeader(
            BinaryPrimitives.ReadInt32LittleEndian(preamble.Slice(18, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(preamble.Slice(22, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(preamble.Slice(26, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(preamble.Slice(30, 4)));
        return true;
    }

    /// <summary>
    /// 图像尺寸（TEXV0005 前导里紧跟存储尺寸的那一对）。自动尺寸图层与其特效帧缓冲按它取尺寸；
    /// 生成的纹理两对相同，压缩纹理的存储尺寸可能按块对齐而更大。
    /// </summary>
    public static bool TryReadImageExtent(ReadOnlySpan<byte> preamble, out uint width, out uint height)
    {
        width = height = 0;
        if (preamble.Length < 42 || !TryReadHeader(preamble, out _)) return false;
        width = BinaryPrimitives.ReadUInt32LittleEndian(preamble.Slice(34, 4));
        height = BinaryPrimitives.ReadUInt32LittleEndian(preamble.Slice(38, 4));
        return width > 0 && height > 0;
    }

    /// <summary>从工程包或官方素材目录读取 .tex 的图像尺寸；读不到时给出可复述的原因。</summary>
    public static bool TryReadImageExtent(ProjectSource source, string? assetsDirectory, string resource,
        out uint width, out uint height, out string reason)
    {
        width = height = 0;
        if (!TryReadPreamble(source, assetsDirectory, resource, out byte[] preamble, out reason)) return false;
        if (TryReadImageExtent(preamble, out width, out height)) return true;
        reason = $"\"{resource}\" is not a TEXV0005/TEXI0001 container with a positive image extent";
        return false;
    }

    /// <summary>从工程包或官方素材目录读取一个 .tex 的前导；读不到或格式不认识时给出可复述的原因。</summary>
    public static bool TryReadHeader(ProjectSource source, string? assetsDirectory, string resource,
        out TextureHeader header, out string reason)
    {
        header = default;
        if (!TryReadPreamble(source, assetsDirectory, resource, out byte[] preamble, out reason)) return false;
        if (TryReadHeader(preamble, out header)) return true;
        reason = $"\"{resource}\" is not a TEXV0005/TEXI0001 container";
        return false;
    }

    private static bool TryReadPreamble(ProjectSource source, string? assetsDirectory, string resource,
        out byte[] preamble, out string reason)
    {
        ArgumentNullException.ThrowIfNull(source);
        preamble = [];
        reason = "";
        try
        {
            if (source.Contains(resource)) preamble = source.ReadPrefix(resource, 64);
            else
            {
                if (assetsDirectory is null)
                {
                    reason = $"\"{resource}\" is not in the project and no assets directory was given";
                    return false;
                }
                string path = ProjectSource.ContainedPath(assetsDirectory, resource);
                if (!File.Exists(path))
                {
                    reason = $"\"{resource}\" was not found in the project or the assets directory";
                    return false;
                }
                using var input = File.OpenRead(path);
                preamble = new byte[Math.Min(64, checked((int)input.Length))];
                input.ReadExactly(preamble);
            }
            return true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            reason = $"\"{resource}\" could not be read: {error.Message}";
            return false;
        }
    }

    private static void WritePreamble(BinaryWriter writer, uint width, uint height, uint flags)
    {
        writer.Write(Encoding.ASCII.GetBytes("TEXV0005\0TEXI0001\0"));
        writer.Write(0); // RGBA8 format
        writer.Write(flags);
        writer.Write(width);
        writer.Write(height);
        writer.Write(width); // logical extent equals stored extent for generated textures
        writer.Write(height);
        writer.Write(0);
    }
}
