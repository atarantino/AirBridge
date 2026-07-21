using AirBridge.App;
using AirBridge.Core;
using System.Windows.Forms;

namespace AirBridge.Tests;

public sealed class ReceiverRowControlTests
{
    [Fact]
    public void HiddenVolumeBoundsDoNotBlockCompactRowSelection()
    {
        using var row = new ReceiverRowControl();
        row.UseCompactLayout();
        row.Bind(new ReceiverInfo("speaker-a", "Speaker A", "local", false, DateTimeOffset.UtcNow));

        var volume = Assert.Single(row.Controls.OfType<OwnerDrawnSlider>());
        volume.SetBounds(20, 5, 200, 30);
        volume.Visible = false;
        ReceiverSelectionChangedEventArgs? changed = null;
        row.SelectionChanged += (_, args) => changed = args;

        typeof(ReceiverRowControl)
            .GetMethod("OnMouseUp", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(row, [new MouseEventArgs(MouseButtons.Left, 1, 40, 15, 0)]);

        Assert.NotNull(changed);
        Assert.Equal("speaker-a", changed.ReceiverId);
        Assert.True(changed.Selected);
        Assert.True(row.IsSelected);
    }

    [Theory]
    [InlineData(StreamState.Connecting)]
    [InlineData(StreamState.Negotiating)]
    [InlineData(StreamState.Buffering)]
    [InlineData(StreamState.Streaming)]
    [InlineData(StreamState.Reconnecting)]
    [InlineData(StreamState.Standby)]
    public void ActiveRouteStatesUseLiveFlyoutHighlight(StreamState state)
    {
        Assert.True(ReceiverRowControl.IsCompactPlaybackHighlighted(true, state));
    }

    [Theory]
    [InlineData(false, StreamState.Streaming)]
    [InlineData(true, StreamState.Idle)]
    [InlineData(true, StreamState.Failed)]
    public void InactiveOrDetachedStatesDoNotUseLiveFlyoutHighlight(bool streamActive, StreamState state)
    {
        Assert.False(ReceiverRowControl.IsCompactPlaybackHighlighted(streamActive, state));
    }
}
