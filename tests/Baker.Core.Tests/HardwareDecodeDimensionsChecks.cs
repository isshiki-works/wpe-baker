using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Baker.Core;

/// <summary>硬件解码尺寸预检：限制表边界、补边几何与回放取样矩形、拒绝文案。</summary>
internal static class HardwareDecodeDimensionsChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var h264 = HardwareDecodeDimensions.H264;
        var hevc = HardwareDecodeDimensions.Hevc;
        check(h264.MinimumWidth == 48 && h264.MinimumHeight == 64 && h264.MaximumWidth == 4096 && h264.MaximumHeight == 4096 &&
            hevc.MinimumWidth == 144 && hevc.MinimumHeight == 144 && hevc.MaximumWidth == 8192 && hevc.MaximumHeight == 8192 &&
            hevc.MaximumLumaSamples == 35651584 && h264.Sources.Length > 0 && hevc.Sources.Length > 0 &&
            h264.Sources.Concat(hevc.Sources).All(source => source.Length > 20),
            "hardware decode limits table carries the strictest published values with a source for each codec");

        // ---- 最小边界 ----
        var exactMinimum = HardwareDecodeDimensions.Evaluate(48, 64, false, 60, 1);
        check(exactMinimum.Status == HardwareDecodeDimensions.PassStatus && exactMinimum.SoftwareEncoder == "libx264" &&
            !exactMinimum.Padded && exactMinimum.StoredWidth == 48 && exactMinimum.StoredHeight == 64,
            "an H.264 canvas exactly at the 48x64 minimum passes without padding");
        var strip = HardwareDecodeDimensions.Evaluate(1920, 6, true, 60, 1);
        check(strip.Status == HardwareDecodeDimensions.PaddedStatus && strip.SoftwareEncoder == "libx264" &&
            strip.PaddedWidth == 1920 && strip.PaddedHeight >= 64 && strip.StoredWidth == 3840 &&
            strip.OffsetX == 0 && strip.OffsetY % 2 == 0 && strip.PaddedHeight - strip.ContentHeight - strip.OffsetY == strip.OffsetY,
            "the 3691570025 strip (packed 3840x6) is centred into an H.264 canvas at least 64 rows tall with even symmetric offsets");
        var narrowPacked = HardwareDecodeDimensions.Evaluate(16, 64, true, 60, 1);
        check(narrowPacked.Status == HardwareDecodeDimensions.PaddedStatus && narrowPacked.StoredWidth >= 48 &&
            narrowPacked.PaddedWidth % 2 == 0 && narrowPacked.OffsetX % 2 == 0,
            "the minimum width applies to the packed side-by-side frame, each half padded evenly");
        var wideStrip = HardwareDecodeDimensions.Evaluate(4098, 6, false, 60, 1);
        check(wideStrip.SoftwareEncoder == "libx265" && wideStrip.PaddedHeight >= 144 && wideStrip.PaddedWidth == 4098 &&
            wideStrip.Status == HardwareDecodeDimensions.PaddedStatus,
            "a strip wider than 4096 switches to HEVC and is padded to the HEVC 144-pixel minimum");
        var tinyHevc = HardwareDecodeDimensions.Evaluate(4096, 2, true, 60, 1);
        check(tinyHevc.SoftwareEncoder == "libx265" && tinyHevc.PaddedHeight >= 144 &&
            HardwareDecodeDimensions.Evaluate(tinyHevc.PaddedWidth, tinyHevc.PaddedHeight, true, 60, 1).Status == HardwareDecodeDimensions.PassStatus,
            "padding converges: the padded canvas itself passes the preflight with the same encoder");

        // ---- 最大边界 ----
        var maximum = HardwareDecodeDimensions.Evaluate(4096, 3160, true, 60, 1);
        check(maximum.Status == HardwareDecodeDimensions.PassStatus && maximum.SoftwareEncoder == "libx265" && maximum.StoredWidth == 8192,
            "a packed HEVC canvas exactly 8192 wide passes");
        var justOver = HardwareDecodeDimensions.Evaluate(4098, 3160, true, 60, 1);
        check(justOver.Rejected && justOver.Violations.Count == 1 && justOver.Violations[0] is { Measure: "width", Actual: 8196, Limit: 8192 },
            "a packed HEVC canvas 8196 wide is rejected on width alone");
        var atlas = HardwareDecodeDimensions.Evaluate(5108, 3160, true, 60, 1);
        var atlasOpaque = HardwareDecodeDimensions.Evaluate(5108, 3160, false, 60, 1);
        check(atlas.Rejected && atlas.StoredWidth == 10216 && atlas.StoredHeight == 3160 && !atlas.Padded &&
            atlas.Violations.Single() is { Measure: "width", Actual: 10216, Limit: 8192 } &&
            atlasOpaque.Status == HardwareDecodeDimensions.PassStatus && atlasOpaque.SoftwareEncoder == "libx265",
            "side-by-side alpha pushes the 3753921460 atlas (5108x3160) to 10216 wide and is rejected, while the opaque layout would pass");
        check(HardwareDecodeDimensions.FitCeiling(5108, 3160, true) == (4096u, 2532u) &&
            HardwareDecodeDimensions.Evaluate(4096, 2532, true, 60, 1).Status == HardwareDecodeDimensions.PassStatus &&
            HardwareDecodeDimensions.FitCeiling(5108, 3160, false) == (5108u, 3160u),
            "an over-ceiling packed atlas scales down uniformly to the HEVC ceiling; one within it is left alone");
        var tall = HardwareDecodeDimensions.Evaluate(2, 8194, false, 60, 1);
        check(tall.Rejected && tall.Violations.Any(violation => violation.Measure == "height" && violation.Actual == 8194),
            "height beyond 8192 is rejected even when padding widened the canvas");
        check(HardwareDecodeDimensions.Evaluate(8192, 4352, false, 60, 1).Status == HardwareDecodeDimensions.PassStatus &&
            HardwareDecodeDimensions.Evaluate(8192, 8192, false, 60, 1) is { Rejected: true } square &&
            square.Violations.Single().Measure == "luma_samples",
            "HEVC level 6.2 luma picture size admits 8192x4352 and rejects 8192x8192");

        // ---- 奇数 ----
        var odd = HardwareDecodeDimensions.Evaluate(1001, 501, false, 60, 1);
        check(odd.Status == HardwareDecodeDimensions.PaddedStatus && odd.PaddedWidth == 1002 && odd.PaddedHeight == 502 &&
            odd.OffsetX == 0 && odd.OffsetY == 0 && DecodeDimensions.Grow(1001, 1001) == (1002u, 0u) &&
            DecodeDimensions.Grow(6, 64) == (66u, 30u) && DecodeDimensions.Grow(64, 48) == (64u, 0u),
            "odd content is aligned to even 4:2:0 dimensions by padding at the far edge only");
        check(PlaybackEncodeProfileSelect(3840, 2160) == "libx264" && PlaybackEncodeProfileSelect(4097, 2) == "libx265" &&
            PlaybackEncodeProfileSelect(2, 4097) == "libx265" && PlaybackEncodeProfileSelect(4096, 2320) == "libx265",
            "encoder selection reads the same H.264 table and keeps its previous 4096-pixel and level 5.2 boundaries");

        // ---- 补边后显示矩形不变 ----
        var content = new EncodedContentRegion((int)strip.PaddedWidth, (int)strip.PaddedHeight, (int)strip.OffsetX, (int)strip.OffsetY,
            (int)strip.ContentWidth, (int)strip.ContentHeight);
        int halfWidth = content.PaddedWidth, paddedHeight = content.PaddedHeight;
        byte[] frame = new byte[halfWidth * 2 * paddedHeight * 3];
        Array.Fill(frame, (byte)199);  // 补边故意写成非零，确认取出的只有内容
        byte[] expected = new byte[content.Width * 2 * content.Height * 3];
        for (int row = 0; row < content.Height; ++row)
            for (int half = 0; half < 2; ++half)
                for (int column = 0; column < content.Width; ++column)
                    for (int channel = 0; channel < 3; ++channel)
                    {
                        byte value = (byte)((row * 31 + column * 7 + half * 101 + channel) & 0x7f);
                        frame[((content.OffsetY + row) * halfWidth * 2 + half * halfWidth + content.OffsetX + column) * 3 + channel] = value;
                        expected[(row * content.Width * 2 + half * content.Width + column) * 3 + channel] = value;
                    }
        check(content.Extract(frame, 2).AsSpan().SequenceEqual(expected),
            "the seam validator measures exactly the content rectangle of both halves and none of the padding");

        MethodInfo fragmentMethod = typeof(NativeRenderRunner).Assembly.GetType("Baker.Core.EffectPrefixCache")!
            .GetMethod("DecoderFragment", BindingFlags.Static | BindingFlags.NonPublic)!;
        string Fragment(uint width, uint height, bool packed, EncodedContentRegion? region) =>
            (string)fragmentMethod.Invoke(null, [width, height, packed, region])!;
        const uint legacyWidth = 3840;
        double legacyEdge = .5 / legacyWidth;
        check(Fragment(legacyWidth, 6, true, null) == FormattableString.Invariant($"// SPDX-License-Identifier: MIT\nuniform sampler2D g_Texture0;\nvarying vec2 v_TexCoord;\nvoid main(){{ vec3 rgb=texSample2D(g_Texture0,vec2(clamp(v_TexCoord.x*0.5,{legacyEdge:R},{.5-legacyEdge:R}),v_TexCoord.y)).rgb; float a=texSample2D(g_Texture0,vec2(clamp(v_TexCoord.x*0.5+0.5,{.5+legacyEdge:R},{1-legacyEdge:R}),v_TexCoord.y)).r; gl_FragColor=vec4(rgb,a); }}\n"),
            "an unpadded packed cache keeps the historical decoder shader byte for byte");

        string padded = Fragment(strip.StoredWidth, strip.StoredHeight, true, content);
        double[] numbers = Regex.Matches(padded, @"clamp\(([-0-9.Ee]+)\+v_TexCoord\.([xy])\*([-0-9.Ee]+),([-0-9.Ee]+),([-0-9.Ee]+)\)")
            .SelectMany(match => new[] { match.Groups[1].Value, match.Groups[3].Value, match.Groups[4].Value, match.Groups[5].Value })
            .Select(text => double.Parse(text, CultureInfo.InvariantCulture)).ToArray();
        // 片段里 x、y 两个夹取各出现两次（rgb 与 alpha 共用同一表达式），前四个数是 x，接着四个是 y。
        double storedW = strip.StoredWidth, storedH = strip.StoredHeight;
        bool xMaps = numbers.Length >= 8 &&
            Math.Abs(numbers[0] * storedW - content.OffsetX) < 1e-9 && Math.Abs((numbers[0] + numbers[1]) * storedW - (content.OffsetX + content.Width)) < 1e-9 &&
            Math.Abs(numbers[2] * storedW - (content.OffsetX + .5)) < 1e-9 && Math.Abs(numbers[3] * storedW - (content.OffsetX + content.Width - .5)) < 1e-9;
        bool yMaps = numbers.Length >= 8 &&
            Math.Abs(numbers[4] * storedH - content.OffsetY) < 1e-9 && Math.Abs((numbers[4] + numbers[5]) * storedH - (content.OffsetY + content.Height)) < 1e-9 &&
            Math.Abs(numbers[6] * storedH - (content.OffsetY + .5)) < 1e-9 && Math.Abs(numbers[7] * storedH - (content.OffsetY + content.Height - .5)) < 1e-9;
        // 上下翻转取样：1-(top+span) 仍等于 top，说明内容矩形在翻转后落在同一位置。
        bool flipInvariant = numbers.Length >= 8 && Math.Abs(1 - (numbers[4] + numbers[5]) - numbers[4]) < 1e-9;
        check(xMaps && yMaps && flipInvariant && padded.Contains("vec2(0.5+clamp(", StringComparison.Ordinal),
            "the padded decoder maps UV 0..1 onto exactly the original content rectangle, clamped half a texel inside, and stays centred under a vertical flip");
        string opaquePadded = Fragment(wideStrip.StoredWidth, wideStrip.StoredHeight, false,
            new((int)wideStrip.PaddedWidth, (int)wideStrip.PaddedHeight, (int)wideStrip.OffsetX, (int)wideStrip.OffsetY, (int)wideStrip.ContentWidth, (int)wideStrip.ContentHeight));
        check(opaquePadded.Contains("float a=1.0;", StringComparison.Ordinal) && !opaquePadded.Contains("0.5+clamp", StringComparison.Ordinal),
            "a padded opaque cache decodes RGB from the content rectangle and keeps full alpha");

        // ---- 分组裁剪区扩到下限，显示几何随裁剪区 ----
        var small = new CacheRegion(1920, 1080, 100, 100, 34, 34);
        var grown = HardwareDecodeDimensions.GrowRegion(small, true, 60, 1);
        check(grown.X <= small.X && grown.Y <= small.Y && grown.X + grown.Width >= small.X + small.Width &&
            grown.Y + grown.Height >= small.Y + small.Height && grown.Height >= 64 && grown.Width * 2 >= 48 &&
            grown.X % 2 == 0 && grown.Y % 2 == 0 && grown.Width % 2 == 0 && grown.Height % 2 == 0 &&
            HardwareDecodeDimensions.Evaluate((uint)grown.Width, (uint)grown.Height, true, 60, 1).Status == HardwareDecodeDimensions.PassStatus,
            "an undersized group crop grows inside its capture to the decode minimum and still contains the alpha bounds");
        var edge = HardwareDecodeDimensions.GrowRegion(new CacheRegion(1920, 1080, 0, 1060, 34, 20), false, 60, 1);
        check(edge.Y + edge.Height == 1080 && edge.Height >= 64 && edge.X == 0 && edge.Width >= 48,
            "a crop at the capture edge grows inward instead of leaving the capture");
        var cramped = new CacheRegion(40, 40, 0, 0, 40, 40);
        check(HardwareDecodeDimensions.GrowRegion(cramped, false, 60, 1) == cramped,
            "a capture smaller than the minimum keeps its crop; the preflight records it instead of inventing pixels");

        // ---- 文案 ----
        JsonObject preflight = atlas.ToJson();
        check(preflight["status"]?.GetValue<string>() == "rejected" && preflight["encoded_extent"]?.ToJsonString() == "[10216,3160]" &&
            preflight["violations"]?.AsArray().Count == 1 && preflight["limits"]?["sources"]?.AsArray().Count == hevc.Sources.Length &&
            strip.ToJson()["padding"]?["top"]?.GetValue<uint>() == strip.OffsetY &&
            strip.ToJson()["padding"]?["bottom"]?.GetValue<uint>() == strip.OffsetY,
            "bake.json preflight records the encoded extent, violations, sources and the padding on every side");

        // ---- TEX 图像尺寸 ----
        byte[] preamble = new byte[64];
        Encoding.ASCII.GetBytes("TEXV0005\0TEXI0001\0").CopyTo(preamble, 0);
        BitConverter.GetBytes(0).CopyTo(preamble, 18); BitConverter.GetBytes(2u).CopyTo(preamble, 22);
        BitConverter.GetBytes(8192u).CopyTo(preamble, 26); BitConverter.GetBytes(32u).CopyTo(preamble, 30);
        BitConverter.GetBytes(5120u).CopyTo(preamble, 34); BitConverter.GetBytes(21u).CopyTo(preamble, 38);
        check(TextureContainer.TryReadImageExtent(preamble, out uint imageWidth, out uint imageHeight) && imageWidth == 5120 && imageHeight == 21 &&
            !TextureContainer.TryReadImageExtent(preamble.AsSpan(0, 40), out _, out _),
            "analyze reads the TEX image extent rather than the block-aligned storage extent");
    }

    private static string PlaybackEncodeProfileSelect(uint width, uint height) => (string)typeof(NativeRenderRunner)
        .GetMethod("PlaybackEncoder", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [width, height, 60u, 1u])!;
}
