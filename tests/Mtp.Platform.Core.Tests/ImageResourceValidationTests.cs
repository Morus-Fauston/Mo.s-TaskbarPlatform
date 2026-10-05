using System.Buffers.Binary;
using Mtp.Contracts;
using Mtp.Host;

namespace Mtp.Platform.Core.Tests;

public sealed class ImageResourceValidationTests
{
    [Fact]
    public void Png_header_accepts_bounded_dimensions_and_rejects_wrong_format_and_oversized_edges()
    {
        var image = PngHeader(64, 32);
        Assert.Equal(new ImageValidationResult(true, "Accepted", 64, 32), ImageResourceValidation.Validate(image, ImageResourceFormat.Png));
        Assert.False(ImageResourceValidation.Validate(image, ImageResourceFormat.Jpeg).Accepted);
        Assert.Equal("ImageDimensionsExceeded", ImageResourceValidation.Validate(PngHeader(1025, 32), ImageResourceFormat.Png).Code);
        Assert.False(ImageResourceValidation.Validate(PngHeader(0, 1), ImageResourceFormat.Png).Accepted);
        Assert.False(ImageResourceValidation.Validate(PngHeader(uint.MaxValue, 1), ImageResourceFormat.Png).Accepted);
    }

    [Fact]
    public void Jpeg_walks_bounded_segments_before_dimensions_and_rejects_truncation_or_forged_lengths()
    {
        byte[] image = [0xff, 0xd8, 0xff, 0xe0, 0, 4, 0xab, 0xcd, 0xff, 0xc0, 0, 17, 8, 0, 32, 0, 64, 3,
            1, 0x11, 0, 2, 0x11, 0, 3, 0x11, 0, 0xff, 0xd9];
        Assert.Equal(new ImageValidationResult(true, "Accepted", 64, 32), ImageResourceValidation.Validate(image, ImageResourceFormat.Jpeg));
        for (int length = 0; length < 27; length++) Assert.False(ImageResourceValidation.Validate(image.AsSpan(0, length), ImageResourceFormat.Jpeg).Accepted);
        image[5] = 255;
        Assert.False(ImageResourceValidation.Validate(image, ImageResourceFormat.Jpeg).Accepted);
        Assert.False(ImageResourceValidation.Validate([0xff, 0xd8, 0xff, 0xd9], ImageResourceFormat.Jpeg).Accepted);
        Assert.False(ImageResourceValidation.Validate([0xff, 0xd8, 0xff, 0xff], ImageResourceFormat.Jpeg).Accepted);
    }

    [Fact]
    public void Encoded_budget_is_checked_before_parsing_and_only_known_formats_are_accepted()
    {
        var image = new byte[TemplateLimits.EncodedImageBytes + 1];
        PngHeader(1, 1).CopyTo(image, 0);
        Assert.Equal("ImageEncodingBudgetExceeded", ImageResourceValidation.Validate(image, ImageResourceFormat.Png).Code);
        Assert.True(ImageResourceValidation.Validate(image.AsSpan(0, TemplateLimits.EncodedImageBytes), ImageResourceFormat.Png).Accepted);
        Assert.False(ImageResourceValidation.Validate(PngHeader(1, 1), (ImageResourceFormat)99).Accepted);
        Assert.False(ImageResourceValidation.Validate("https://example.test/image.png"u8, ImageResourceFormat.Png).Accepted);
    }

    internal static byte[] PngHeader(uint width, uint height)
    {
        byte[] bytes = new byte[33];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), width);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), height);
        bytes[24] = 8;
        bytes[25] = 6;
        return bytes;
    }
}
