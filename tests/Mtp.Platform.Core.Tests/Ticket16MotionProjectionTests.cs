using Mtp.Host;

namespace Mtp.Platform.Core.Tests;

public sealed class Ticket16MotionProjectionTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void SlowlyMovingItemNeverReversesBecauseNativeAndXamlRoundDifferentOrigins(double scale)
    {
        double previous = double.PositiveInfinity;
        for (int index = 0; index <= 200; index++)
        {
            double width = 100 + index * .01;
            double rightRelativeX = -40 - index * .002;
            double nativeLeft = -Pixel(width * scale);
            double viewport = TaskbarFrameProjection.ViewportWidth(width, scale);
            double screenX = nativeLeft + Pixel((viewport + rightRelativeX) * scale);
            Assert.True(screenX <= previous, $"Frame {index}, DPI scale {scale}: {previous} -> {screenX}");
            previous = screenX;
        }
    }

    [Theory]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void StationaryRightNeighborStaysAtTheSameScreenPixelThroughoutWidthChange(double scale)
    {
        const double x = -40.25;
        for (int index = 0; index <= 200; index++)
        {
            double width = 100 + index * .01;
            double viewport = TaskbarFrameProjection.ViewportWidth(width, scale);
            double screenX = -Pixel(width * scale) + Pixel((viewport + x) * scale);
            Assert.Equal(Pixel(x * scale), screenX);
        }
    }

    private static double Pixel(double value) => Math.Round(value, MidpointRounding.AwayFromZero);
}
