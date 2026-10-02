using AirBridge.App;
using System.Drawing;

namespace AirBridge.Tests;

public sealed class TrayPopupPositionTests
{
    [Fact]
    public void NormalPopupStaysAtBottomRightWithMargin() =>
        Assert.Equal(new Point(1492, 472), TrayFlyoutForm.PopupLocation(new(0, 0, 1920, 1080), new(420, 600), 8));

    [Fact]
    public void OversizedQaRenderOnSmallDesktopDoesNotThrow() =>
        Assert.Equal(new Point(100, 100), TrayFlyoutForm.PopupLocation(new(100, 100, 640, 480), new(840, 1200), 8));

    [Fact]
    public void TallPopupKeepsItsHorizontalTrayAnchor() =>
        Assert.Equal(new Point(312, 100), TrayFlyoutForm.PopupLocation(new(100, 100, 640, 480), new(420, 900), 8));
}
