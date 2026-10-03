namespace FateAndFridge.Api.Services;

/// <summary>Local image formats accepted for recipe images (docs/M3_PLAN.md §1.7).</summary>
public enum RecipeImageFormat
{
    Jpeg,
    Png,
    WebP,
}

/// <summary>Outcome of inspecting the header bytes of an uploaded recipe image.</summary>
public enum RecipeImageProbeOutcome
{
    /// <summary>A supported still image whose dimensions were read.</summary>
    Valid,

    /// <summary>Not a supported format (SVG, HTML, GIF, other RIFF, or arbitrary bytes).</summary>
    UnknownFormat,

    /// <summary>A supported signature with truncated or corrupt structure.</summary>
    Malformed,

    /// <summary>A supported animated image (APNG or animated WebP).</summary>
    Animated,
}

/// <summary>
/// Result of <see cref="RecipeImageFormatDetector.Probe"/>: the detected format,
/// pixel dimensions, and whether the image is animated or unreadable. Failures
/// carry no format.
/// </summary>
public readonly record struct RecipeImageProbe(
    RecipeImageProbeOutcome Outcome,
    RecipeImageFormat? Format,
    int Width,
    int Height)
{
    public static RecipeImageProbe Valid(RecipeImageFormat format, int width, int height) =>
        new(RecipeImageProbeOutcome.Valid, format, width, height);

    public static RecipeImageProbe Animated(RecipeImageFormat format, int width, int height) =>
        new(RecipeImageProbeOutcome.Animated, format, width, height);

    public static RecipeImageProbe Unknown() =>
        new(RecipeImageProbeOutcome.UnknownFormat, null, 0, 0);

    public static RecipeImageProbe Malformed() =>
        new(RecipeImageProbeOutcome.Malformed, null, 0, 0);
}

/// <summary>
/// Dependency-free image signature and dimension reader for the M3-04 content
/// policy (docs/M3_PLAN.md §1.7, docs/SECURITY.md §9). Only JPEG, PNG, and WebP
/// are recognized; everything else is <see cref="RecipeImageProbeOutcome.UnknownFormat"/>.
/// Truncated or structurally corrupt supported formats are
/// <see cref="RecipeImageProbeOutcome.Malformed"/>, and APNG (<c>acTL</c>) and
/// animated WebP (VP8X animation flag or <c>ANIM</c> chunk) are
/// <see cref="RecipeImageProbeOutcome.Animated"/>. The caller decides size and
/// dimension limits.
/// </summary>
public static class RecipeImageFormatDetector
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly byte[] RiffSignature = "RIFF"u8.ToArray();

    private static readonly byte[] WebpSignature = "WEBP"u8.ToArray();

    private static readonly byte[] IhdrType = "IHDR"u8.ToArray();

    private static readonly byte[] ActlType = "acTL"u8.ToArray();

    private static readonly byte[] IdatType = "IDAT"u8.ToArray();

    private static readonly byte[] Vp8xType = "VP8X"u8.ToArray();

    private static readonly byte[] Vp8Type = "VP8 "u8.ToArray();

    private static readonly byte[] Vp8lType = "VP8L"u8.ToArray();

    private static readonly byte[] AnimType = "ANIM"u8.ToArray();

    /// <summary>Canonical MIME type of a detected format.</summary>
    public static string ContentType(RecipeImageFormat format) => format switch
    {
        RecipeImageFormat.Jpeg => "image/jpeg",
        RecipeImageFormat.Png => "image/png",
        RecipeImageFormat.WebP => "image/webp",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };

    /// <summary>Server-generated file extension of a detected format.</summary>
    public static string FileExtension(RecipeImageFormat format) => format switch
    {
        RecipeImageFormat.Jpeg => ".jpg",
        RecipeImageFormat.Png => ".png",
        RecipeImageFormat.WebP => ".webp",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };

    /// <summary>
    /// True when the declared media type is absent/unspecified or exactly the
    /// detected format's canonical type. An absent value is permitted; any other
    /// present value is a mismatch.
    /// </summary>
    public static bool IsDeclaredContentTypeCompatible(
        string? declaredContentType,
        RecipeImageFormat format)
    {
        if (string.IsNullOrWhiteSpace(declaredContentType))
        {
            return true;
        }

        var separator = declaredContentType.IndexOf(';', StringComparison.Ordinal);
        var mediaType = (separator < 0 ? declaredContentType : declaredContentType[..separator]).Trim();
        if (mediaType.Length == 0
            || string.Equals(mediaType, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(mediaType, ContentType(format), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Inspects the header bytes of an uploaded image and returns its detected
    /// format, dimensions, and animation state. Never throws for arbitrary input.
    /// </summary>
    public static RecipeImageProbe Probe(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
        {
            return RecipeImageProbe.Unknown();
        }

        if (bytes.StartsWith(PngSignature))
        {
            return ParsePng(bytes);
        }

        if (MatchesPartialPrefix(bytes, PngSignature))
        {
            return RecipeImageProbe.Malformed();
        }

        if (bytes[0] == 0xFF && (bytes.Length == 1 || bytes[1] == 0xD8))
        {
            return ParseJpeg(bytes);
        }

        if (bytes.StartsWith(RiffSignature))
        {
            return ParseWebp(bytes);
        }

        if (MatchesPartialPrefix(bytes, RiffSignature))
        {
            return RecipeImageProbe.Malformed();
        }

        return RecipeImageProbe.Unknown();
    }

    /// <summary>
    /// PNG (RFC 2083): signature, mandatory first IHDR chunk, and the first
    /// <c>acTL</c> before <c>IDAT</c> marks an animated PNG.
    /// </summary>
    private static RecipeImageProbe ParsePng(ReadOnlySpan<byte> bytes)
    {
        var position = PngSignature.Length;
        var width = 0;
        var height = 0;
        var sawHeader = false;

        while (position + 8 <= bytes.Length)
        {
            var length = ReadUInt32BigEndian(bytes[position..]);
            var type = bytes.Slice(position + 4, 4);
            var dataStart = position + 8;
            var chunkTotal = 8L + length + 4L;
            if (chunkTotal > bytes.Length - position)
            {
                return RecipeImageProbe.Malformed();
            }

            if (!sawHeader)
            {
                if (length != 13u || !type.SequenceEqual(IhdrType))
                {
                    return RecipeImageProbe.Malformed();
                }

                var declaredWidth = ReadUInt32BigEndian(bytes[(dataStart + 0)..]);
                var declaredHeight = ReadUInt32BigEndian(bytes[(dataStart + 4)..]);
                if (declaredWidth is 0 or > int.MaxValue || declaredHeight is 0 or > int.MaxValue)
                {
                    return RecipeImageProbe.Malformed();
                }

                width = (int)declaredWidth;
                height = (int)declaredHeight;
                sawHeader = true;
            }
            else if (type.SequenceEqual(ActlType))
            {
                return RecipeImageProbe.Animated(RecipeImageFormat.Png, width, height);
            }
            else if (type.SequenceEqual(IdatType))
            {
                return RecipeImageProbe.Valid(RecipeImageFormat.Png, width, height);
            }

            position += (int)chunkTotal;
        }

        return RecipeImageProbe.Malformed();
    }

    /// <summary>
    /// JPEG (ITU T.81): SOI plus the first SOF segment. APP/COM segments are
    /// skipped by their declared length; EOI or SOS before a SOF means the
    /// header is truncated.
    /// </summary>
    private static RecipeImageProbe ParseJpeg(ReadOnlySpan<byte> bytes)
    {
        var position = 2;
        while (position < bytes.Length)
        {
            if (bytes[position] != 0xFF)
            {
                return RecipeImageProbe.Malformed();
            }

            while (position < bytes.Length && bytes[position] == 0xFF)
            {
                position++;
            }

            if (position >= bytes.Length)
            {
                return RecipeImageProbe.Malformed();
            }

            var marker = bytes[position];
            position++;

            if (marker == 0xD8)
            {
                continue;
            }

            if (marker is 0x01 or (>= 0xD0 and <= 0xD7))
            {
                continue;
            }

            if (marker is 0xD9 or 0xDA)
            {
                return RecipeImageProbe.Malformed();
            }

            if (position + 2 > bytes.Length)
            {
                return RecipeImageProbe.Malformed();
            }

            var segmentLength = (bytes[position] << 8) | bytes[position + 1];
            if (segmentLength < 2 || position + segmentLength > bytes.Length)
            {
                return RecipeImageProbe.Malformed();
            }

            if (IsStartOfFrame(marker))
            {
                if (segmentLength < 8)
                {
                    return RecipeImageProbe.Malformed();
                }

                var height = (bytes[position + 3] << 8) | bytes[position + 4];
                var width = (bytes[position + 5] << 8) | bytes[position + 6];
                if (width == 0 || height == 0)
                {
                    return RecipeImageProbe.Malformed();
                }

                return RecipeImageProbe.Valid(RecipeImageFormat.Jpeg, width, height);
            }

            position += segmentLength;
        }

        return RecipeImageProbe.Malformed();
    }

    /// <summary>
    /// WebP (RIFF container): VP8 (lossy), VP8L (lossless), and VP8X (extended)
    /// with dimensions read from the respective headers. The VP8X animation flag
    /// or an <c>ANIM</c> chunk marks an animated image.
    /// </summary>
    private static RecipeImageProbe ParseWebp(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12)
        {
            return RecipeImageProbe.Malformed();
        }

        if (!bytes.Slice(8, 4).SequenceEqual(WebpSignature))
        {
            return RecipeImageProbe.Unknown();
        }

        var riffSize = ReadUInt32LittleEndian(bytes[4..]);
        if (8L + riffSize > bytes.Length)
        {
            return RecipeImageProbe.Malformed();
        }

        const int firstChunkPosition = 12;
        if (firstChunkPosition + 8 > bytes.Length)
        {
            return RecipeImageProbe.Malformed();
        }

        var chunkSize = ReadUInt32LittleEndian(bytes[(firstChunkPosition + 4)..]);
        var chunkType = bytes.Slice(firstChunkPosition, 4);
        var chunkTotal = 8L + chunkSize;
        if (firstChunkPosition + chunkTotal > bytes.Length)
        {
            return RecipeImageProbe.Malformed();
        }

        if (chunkType.SequenceEqual(Vp8xType))
        {
            if (chunkSize < 10)
            {
                return RecipeImageProbe.Malformed();
            }

            var data = bytes.Slice(firstChunkPosition + 8, 10);
            var animated = (data[0] & 0x02) != 0;
            var width = 1 + (data[4] | (data[5] << 8) | (data[6] << 16));
            var height = 1 + (data[7] | (data[8] << 8) | (data[9] << 16));

            animated |= ContainsChunk(
                bytes,
                firstChunkPosition + (int)chunkTotal,
                AnimType);

            return animated
                ? RecipeImageProbe.Animated(RecipeImageFormat.WebP, width, height)
                : RecipeImageProbe.Valid(RecipeImageFormat.WebP, width, height);
        }

        if (chunkType.SequenceEqual(Vp8Type))
        {
            if (chunkSize < 10)
            {
                return RecipeImageProbe.Malformed();
            }

            var data = bytes.Slice(firstChunkPosition + 8, 10);
            if (data[3] != 0x9D || data[4] != 0x01 || data[5] != 0x2A)
            {
                return RecipeImageProbe.Malformed();
            }

            var width = (data[6] | (data[7] << 8)) & 0x3FFF;
            var height = (data[8] | (data[9] << 8)) & 0x3FFF;
            if (width == 0 || height == 0)
            {
                return RecipeImageProbe.Malformed();
            }

            return RecipeImageProbe.Valid(RecipeImageFormat.WebP, width, height);
        }

        if (chunkType.SequenceEqual(Vp8lType))
        {
            if (chunkSize < 5)
            {
                return RecipeImageProbe.Malformed();
            }

            var data = bytes.Slice(firstChunkPosition + 8, 5);
            if (data[0] != 0x2F)
            {
                return RecipeImageProbe.Malformed();
            }

            var bits = data[1] | (data[2] << 8) | (data[3] << 16) | (data[4] << 24);
            var width = (bits & 0x3FFF) + 1;
            var height = ((bits >> 14) & 0x3FFF) + 1;
            return RecipeImageProbe.Valid(RecipeImageFormat.WebP, width, height);
        }

        return RecipeImageProbe.Malformed();
    }

    /// <summary>
    /// Scans RIFF chunks (with even-size padding) from <paramref name="position"/>
    /// for the first chunk of <paramref name="type"/>. Stops cleanly at the end
    /// of the buffer; used only for animation detection after VP8X.
    /// </summary>
    private static bool ContainsChunk(ReadOnlySpan<byte> bytes, int position, ReadOnlySpan<byte> type)
    {
        while (position + 8 <= bytes.Length)
        {
            var chunkSize = ReadUInt32LittleEndian(bytes[(position + 4)..]);
            var chunkTotal = 8L + chunkSize + (chunkSize % 2);
            if (position + chunkTotal > bytes.Length)
            {
                return false;
            }

            if (bytes.Slice(position, 4).SequenceEqual(type))
            {
                return true;
            }

            position += (int)chunkTotal;
        }

        return false;
    }

    private static bool IsStartOfFrame(byte marker) =>
        marker is 0xC0 or 0xC1 or 0xC2 or 0xC3
            or 0xC5 or 0xC6 or 0xC7
            or 0xC9 or 0xCA or 0xCB
            or 0xCD or 0xCE or 0xCF;

    private static bool MatchesPartialPrefix(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> prefix) =>
        bytes.Length < prefix.Length && prefix[..bytes.Length].SequenceEqual(bytes);

    private static uint ReadUInt32BigEndian(ReadOnlySpan<byte> bytes) =>
        ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];

    private static uint ReadUInt32LittleEndian(ReadOnlySpan<byte> bytes) =>
        bytes[0] | ((uint)bytes[1] << 8) | ((uint)bytes[2] << 16) | ((uint)bytes[3] << 24);
}
