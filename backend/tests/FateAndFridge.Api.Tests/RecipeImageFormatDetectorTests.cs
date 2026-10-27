using System.Buffers.Binary;
using System.Text;
using FateAndFridge.Api.Services;
using FluentAssertions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Docker-free exhaustive checks for the M3-04 signature and dimension reader
/// (docs/M3_PLAN.md §1.7): valid JPEG/PNG/WebP headers, animated PNG/WebP
/// detection, truncation, wrong magic, and oversize dimensions. Every fixture
/// is generated in-code.
/// </summary>
public sealed class RecipeImageFormatDetectorTests
{
    [Fact]
    public void Probe_reads_png_dimensions()
    {
        var probe = RecipeImageFormatDetector.Probe(Png(320, 240));

        probe.Outcome.Should().Be(RecipeImageProbeOutcome.Valid);
        probe.Format.Should().Be(RecipeImageFormat.Png);
        probe.Width.Should().Be(320);
        probe.Height.Should().Be(240);
    }

    [Fact]
    public void Probe_reads_jpeg_dimensions_after_app_segments()
    {
        var probe = RecipeImageFormatDetector.Probe(Jpeg(800, 600));

        probe.Outcome.Should().Be(RecipeImageProbeOutcome.Valid);
        probe.Format.Should().Be(RecipeImageFormat.Jpeg);
        probe.Width.Should().Be(800);
        probe.Height.Should().Be(600);
    }

    [Fact]
    public void Probe_reads_webp_lossy_dimensions()
    {
        var probe = RecipeImageFormatDetector.Probe(WebpLossy(640, 480));

        probe.Outcome.Should().Be(RecipeImageProbeOutcome.Valid);
        probe.Format.Should().Be(RecipeImageFormat.WebP);
        probe.Width.Should().Be(640);
        probe.Height.Should().Be(480);
    }

    [Fact]
    public void Probe_reads_webp_lossless_dimensions()
    {
        var probe = RecipeImageFormatDetector.Probe(WebpLossless(300, 200));

        probe.Outcome.Should().Be(RecipeImageProbeOutcome.Valid);
        probe.Format.Should().Be(RecipeImageFormat.WebP);
        probe.Width.Should().Be(300);
        probe.Height.Should().Be(200);
    }

    [Fact]
    public void Probe_reads_webp_extended_canvas_dimensions()
    {
        var probe = RecipeImageFormatDetector.Probe(WebpExtended(1024, 768, animatedFlag: false, animChunk: false));

        probe.Outcome.Should().Be(RecipeImageProbeOutcome.Valid);
        probe.Format.Should().Be(RecipeImageFormat.WebP);
        probe.Width.Should().Be(1024);
        probe.Height.Should().Be(768);
    }

    [Fact]
    public void Probe_reports_dimensions_beyond_the_policy_limit()
    {
        var png = RecipeImageFormatDetector.Probe(Png(4096, 2160));
        png.Outcome.Should().Be(RecipeImageProbeOutcome.Valid);
        png.Width.Should().Be(4096);
        png.Height.Should().Be(2160);

        var jpeg = RecipeImageFormatDetector.Probe(Jpeg(5000, 4000));
        jpeg.Outcome.Should().Be(RecipeImageProbeOutcome.Valid);
        jpeg.Width.Should().Be(5000);
        jpeg.Height.Should().Be(4000);

        var webp = RecipeImageFormatDetector.Probe(WebpExtended(4096, 4096, animatedFlag: false, animChunk: false));
        webp.Outcome.Should().Be(RecipeImageProbeOutcome.Valid);
        webp.Width.Should().Be(4096);
        webp.Height.Should().Be(4096);
    }

    [Fact]
    public void Probe_flags_animated_png()
    {
        var probe = RecipeImageFormatDetector.Probe(Png(120, 90, animated: true));

        probe.Outcome.Should().Be(RecipeImageProbeOutcome.Animated);
        probe.Format.Should().Be(RecipeImageFormat.Png);
        probe.Width.Should().Be(120);
        probe.Height.Should().Be(90);
    }

    [Fact]
    public void Probe_treats_a_png_animation_chunk_after_idat_as_a_still_image()
    {
        using var stream = new MemoryStream();
        stream.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WritePngChunk(stream, "IHDR"u8, PngHeader(64, 64));
        WritePngChunk(stream, "IDAT"u8, [0x78, 0x9C]);
        WritePngChunk(stream, "acTL"u8, new byte[8]);
        WritePngChunk(stream, "IEND"u8, []);

        var probe = RecipeImageFormatDetector.Probe(stream.ToArray());

        probe.Outcome.Should().Be(
            RecipeImageProbeOutcome.Valid,
            "acTL after IDAT is not an animated PNG per the APNG specification");
    }

    [Fact]
    public void Probe_flags_animated_webp_via_the_vp8x_flag()
    {
        var probe = RecipeImageFormatDetector.Probe(
            WebpExtended(800, 600, animatedFlag: true, animChunk: false));

        probe.Outcome.Should().Be(RecipeImageProbeOutcome.Animated);
        probe.Format.Should().Be(RecipeImageFormat.WebP);
        probe.Width.Should().Be(800);
        probe.Height.Should().Be(600);
    }

    [Fact]
    public void Probe_flags_animated_webp_via_the_anim_chunk()
    {
        var probe = RecipeImageFormatDetector.Probe(
            WebpExtended(800, 600, animatedFlag: false, animChunk: true));

        probe.Outcome.Should().Be(
            RecipeImageProbeOutcome.Animated,
            "an ANIM chunk marks animation even without the VP8X flag");
    }

    [Fact]
    public void Probe_reports_unknown_for_unsupported_content()
    {
        var samples = new byte[][]
        {
            [],
            [0x00, 0x01, 0x02, 0x03, 0x04],
            Encoding.ASCII.GetBytes("GIF89a"),
            Encoding.ASCII.GetBytes("<svg xmlns=http://www.w3.org/2000/svg></svg>"),
            Encoding.ASCII.GetBytes("<!DOCTYPE html><html><body>hello</body></html>"),
            [0xFF, 0xFE, 0xFD],
        };

        foreach (var sample in samples)
        {
            RecipeImageFormatDetector.Probe(sample).Outcome.Should().Be(
                RecipeImageProbeOutcome.UnknownFormat,
                "the bytes are not a supported image header");
        }
    }

    [Fact]
    public void Probe_reports_unknown_for_riff_content_that_is_not_webp()
    {
        using var stream = new MemoryStream();
        stream.Write("RIFF"u8);
        stream.Write([0x04, 0x00, 0x00, 0x00]);
        stream.Write("WAVE"u8);

        RecipeImageFormatDetector.Probe(stream.ToArray()).Outcome.Should().Be(
            RecipeImageProbeOutcome.UnknownFormat);
    }

    [Fact]
    public void Probe_reports_malformed_for_truncated_supported_formats()
    {
        var truncated = new[]
        {
            new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A },
            new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D },
            Jpeg(10, 10)[..2],
            Jpeg(10, 10)[..5],
            Jpeg(10, 10)[..12],
            WebpLossy(10, 10)[..8],
            WebpLossy(10, 10)[..12],
            WebpLossy(10, 10)[..16],
            WebpLossy(10, 10)[..22],
        };

        foreach (var bytes in truncated)
        {
            RecipeImageFormatDetector.Probe(bytes).Outcome.Should().Be(
                RecipeImageProbeOutcome.Malformed,
                "the header is truncated");
        }
    }

    [Fact]
    public void Probe_reports_malformed_for_supported_headers_with_missing_payloads()
    {
        using var pngWithoutIdat = new MemoryStream();
        pngWithoutIdat.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WritePngChunk(pngWithoutIdat, "IHDR"u8, PngHeader(10, 10));
        WritePngChunk(pngWithoutIdat, "IEND"u8, []);

        using var jpegEndingBeforeSof = new MemoryStream();
        jpegEndingBeforeSof.Write([0xFF, 0xD8, 0xFF, 0xD9]);

        var malformed = new[]
        {
            pngWithoutIdat.ToArray(),
            jpegEndingBeforeSof.ToArray(),
            Webp(("VP8 "u8.ToArray(), [0x00, 0x00, 0x00, 0x9D, 0x01, 0x2A, 0x40, 0x01])),
            Webp(("XXXX"u8.ToArray(), [0x01, 0x02, 0x03, 0x04])),
        };

        foreach (var bytes in malformed)
        {
            RecipeImageFormatDetector.Probe(bytes).Outcome.Should().Be(
                RecipeImageProbeOutcome.Malformed,
                "a supported signature must carry a readable payload");
        }
    }

    [Fact]
    public void Probe_reports_malformed_for_zero_dimensions()
    {
        RecipeImageFormatDetector.Probe(Png(0, 20)).Outcome.Should()
            .Be(RecipeImageProbeOutcome.Malformed);
        RecipeImageFormatDetector.Probe(Jpeg(20, 0)).Outcome.Should()
            .Be(RecipeImageProbeOutcome.Malformed);
        RecipeImageFormatDetector.Probe(WebpLossy(0, 20)).Outcome.Should()
            .Be(RecipeImageProbeOutcome.Malformed);
    }

    [Fact]
    public void Probe_reports_malformed_for_png_with_a_non_ihdr_first_chunk()
    {
        using var stream = new MemoryStream();
        stream.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WritePngChunk(stream, "IDAT"u8, [0x78, 0x9C]);

        RecipeImageFormatDetector.Probe(stream.ToArray()).Outcome.Should()
            .Be(RecipeImageProbeOutcome.Malformed);
    }

    [Fact]
    public void Probe_reports_malformed_for_a_partial_signature_prefix()
    {
        RecipeImageFormatDetector.Probe([0x89, 0x50]).Outcome.Should()
            .Be(RecipeImageProbeOutcome.Malformed);
        RecipeImageFormatDetector.Probe("RIF"u8).Outcome.Should()
            .Be(RecipeImageProbeOutcome.Malformed);
    }

    [Fact]
    public void Content_type_and_extension_are_canonical()
    {
        RecipeImageFormatDetector.ContentType(RecipeImageFormat.Jpeg).Should().Be("image/jpeg");
        RecipeImageFormatDetector.ContentType(RecipeImageFormat.Png).Should().Be("image/png");
        RecipeImageFormatDetector.ContentType(RecipeImageFormat.WebP).Should().Be("image/webp");
        RecipeImageFormatDetector.FileExtension(RecipeImageFormat.Jpeg).Should().Be(".jpg");
        RecipeImageFormatDetector.FileExtension(RecipeImageFormat.Png).Should().Be(".png");
        RecipeImageFormatDetector.FileExtension(RecipeImageFormat.WebP).Should().Be(".webp");
    }

    [Fact]
    public void Declared_content_types_must_match_the_detected_format_when_present()
    {
        RecipeImageFormatDetector.IsDeclaredContentTypeCompatible(null, RecipeImageFormat.Jpeg)
            .Should().BeTrue("a missing multipart content type is permitted");
        RecipeImageFormatDetector.IsDeclaredContentTypeCompatible("  ", RecipeImageFormat.Png)
            .Should().BeTrue();
        RecipeImageFormatDetector.IsDeclaredContentTypeCompatible(
            "application/octet-stream",
            RecipeImageFormat.WebP).Should().BeTrue();
        RecipeImageFormatDetector.IsDeclaredContentTypeCompatible(
            "image/jpeg",
            RecipeImageFormat.Jpeg).Should().BeTrue();
        RecipeImageFormatDetector.IsDeclaredContentTypeCompatible(
            "IMAGE/JPEG; charset=binary",
            RecipeImageFormat.Jpeg).Should().BeTrue();

        RecipeImageFormatDetector.IsDeclaredContentTypeCompatible(
            "image/png",
            RecipeImageFormat.Jpeg).Should().BeFalse();
        RecipeImageFormatDetector.IsDeclaredContentTypeCompatible(
            "text/html",
            RecipeImageFormat.Png).Should().BeFalse();
        RecipeImageFormatDetector.IsDeclaredContentTypeCompatible(
            "image/svg+xml",
            RecipeImageFormat.WebP).Should().BeFalse();
        RecipeImageFormatDetector.IsDeclaredContentTypeCompatible(
            "image/jpg",
            RecipeImageFormat.Jpeg).Should().BeFalse("only the canonical MIME type matches");
    }

    private static byte[] Png(int width, int height, bool animated = false, bool includeIdat = true)
    {
        using var stream = new MemoryStream();
        stream.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WritePngChunk(stream, "IHDR"u8, PngHeader(width, height));
        if (animated)
        {
            WritePngChunk(stream, "acTL"u8, new byte[8]);
        }

        if (includeIdat)
        {
            WritePngChunk(stream, "IDAT"u8, [0x78, 0x9C]);
        }

        WritePngChunk(stream, "IEND"u8, []);
        return stream.ToArray();
    }

    private static byte[] PngHeader(int width, int height)
    {
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), (uint)height);
        header[8] = 8;
        header[9] = 6;
        return header;
    }

    private static void WritePngChunk(Stream stream, ReadOnlySpan<byte> type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        stream.Write(length);
        stream.Write(type);
        stream.Write(data);
        stream.Write(new byte[4]);
    }

    private static byte[] Jpeg(int width, int height)
    {
        using var stream = new MemoryStream();
        stream.Write([0xFF, 0xD8]);
        stream.Write([0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00]);
        stream.Write([0xFF, 0xC0, 0x00, 0x11, 0x08]);

        Span<byte> dimensions = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(dimensions[..2], (ushort)height);
        BinaryPrimitives.WriteUInt16BigEndian(dimensions[2..], (ushort)width);
        stream.Write(dimensions);

        stream.Write([0x03, 0x01, 0x11, 0x00, 0x02, 0x11, 0x00, 0x03, 0x11, 0x00]);
        stream.Write([0xFF, 0xD9]);
        return stream.ToArray();
    }

    private static byte[] WebpLossy(int width, int height)
    {
        var frame = new byte[10];
        frame[3] = 0x9D;
        frame[4] = 0x01;
        frame[5] = 0x2A;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(6, 2), (ushort)(width & 0x3FFF));
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(8, 2), (ushort)(height & 0x3FFF));
        return Webp(("VP8 "u8.ToArray(), frame));
    }

    private static byte[] WebpLossless(int width, int height) =>
        Webp(("VP8L"u8.ToArray(), WebpLosslessData(width, height)));

    private static byte[] WebpLosslessData(int width, int height)
    {
        var data = new byte[5];
        data[0] = 0x2F;
        var bits = ((width - 1) & 0x3FFF) | (((height - 1) & 0x3FFF) << 14);
        data[1] = (byte)bits;
        data[2] = (byte)(bits >> 8);
        data[3] = (byte)(bits >> 16);
        data[4] = (byte)(bits >> 24);
        return data;
    }

    private static byte[] WebpExtended(int width, int height, bool animatedFlag, bool animChunk)
    {
        var vp8x = new byte[10];
        vp8x[0] = animatedFlag ? (byte)0x02 : (byte)0x00;
        var encodedWidth = width - 1;
        var encodedHeight = height - 1;
        vp8x[4] = (byte)encodedWidth;
        vp8x[5] = (byte)(encodedWidth >> 8);
        vp8x[6] = (byte)(encodedWidth >> 16);
        vp8x[7] = (byte)encodedHeight;
        vp8x[8] = (byte)(encodedHeight >> 8);
        vp8x[9] = (byte)(encodedHeight >> 16);

        var chunks = new List<(byte[] Type, byte[] Data)>
        {
            ("VP8X"u8.ToArray(), vp8x),
        };
        if (animChunk)
        {
            chunks.Add(("ANIM"u8.ToArray(), new byte[6]));
        }

        chunks.Add(("VP8L"u8.ToArray(), WebpLosslessData(width, height)));
        return Webp(chunks.ToArray());
    }

    private static byte[] Webp(params (byte[] Type, byte[] Data)[] chunks)
    {
        using var body = new MemoryStream();
        Span<byte> size = stackalloc byte[4];
        foreach (var (type, data) in chunks)
        {
            body.Write(type);
            BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)data.Length);
            body.Write(size);
            body.Write(data);
            if ((data.Length & 1) == 1)
            {
                body.WriteByte(0);
            }
        }

        var bodyBytes = body.ToArray();
        using var stream = new MemoryStream();
        stream.Write("RIFF"u8);
        Span<byte> riffSize = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(riffSize, (uint)(4 + bodyBytes.Length));
        stream.Write(riffSize);
        stream.Write("WEBP"u8);
        stream.Write(bodyBytes);
        return stream.ToArray();
    }
}
