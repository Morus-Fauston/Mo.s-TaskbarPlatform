using System.Buffers.Binary;
using Mtp.Contracts;

namespace Mtp.Host;

public sealed record ImageValidationResult(bool Accepted, string Code, int Width = 0, int Height = 0);

/// <summary>Checks encoded budget and dimensions before invoking a platform decoder; decoding must still validate the full image.</summary>
public static class ImageResourceValidation
{
    public static ImageValidationResult Validate(ReadOnlySpan<byte> encoded, ImageResourceFormat format)
    {
        if (encoded.Length > TemplateLimits.EncodedImageBytes) return new(false, "ImageEncodingBudgetExceeded");
        if (format == ImageResourceFormat.Png)
        {
            if (encoded.Length < 33 || !encoded[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
                BinaryPrimitives.ReadUInt32BigEndian(encoded[8..]) != 13 || !encoded.Slice(12, 4).SequenceEqual("IHDR"u8))
                return new(false, "InvalidImageHeader");
            return Dimensions(BinaryPrimitives.ReadUInt32BigEndian(encoded[16..]), BinaryPrimitives.ReadUInt32BigEndian(encoded[20..]));
        }
        if (format != ImageResourceFormat.Jpeg || encoded.Length < 4 || encoded[0] != 0xff || encoded[1] != 0xd8)
            return new(false, "InvalidImageHeader");
        int offset = 2;
        while (offset < encoded.Length)
        {
            if (encoded[offset++] != 0xff) return new(false, "InvalidImageHeader");
            while (offset < encoded.Length && encoded[offset] == 0xff) offset++;
            if (offset >= encoded.Length) break;
            byte marker = encoded[offset++];
            if (marker is 0x00 or 0xd8 or 0xd9 or 0xda || marker is >= 0xd0 and <= 0xd7)
                return new(false, "InvalidImageHeader");
            if (offset + 2 > encoded.Length) break;
            int length = BinaryPrimitives.ReadUInt16BigEndian(encoded[offset..]);
            if (length < 2 || length > encoded.Length - offset) return new(false, "InvalidImageHeader");
            if (marker is 0xc0 or 0xc1 or 0xc2)
            {
                if (length < 11 || encoded[offset + 2] != 8 || encoded[offset + 7] is not (1 or 3 or 4) ||
                    length != 8 + 3 * encoded[offset + 7]) return new(false, "InvalidImageHeader");
                return Dimensions(BinaryPrimitives.ReadUInt16BigEndian(encoded[(offset + 5)..]),
                    BinaryPrimitives.ReadUInt16BigEndian(encoded[(offset + 3)..]));
            }
            offset += length;
        }
        return new(false, "InvalidImageHeader");
    }

    private static ImageValidationResult Dimensions(uint width, uint height) =>
        width is 0 or > TemplateLimits.ImageEdgePixels || height is 0 or > TemplateLimits.ImageEdgePixels
            ? new(false, "ImageDimensionsExceeded") : new(true, "Accepted", (int)width, (int)height);
}
