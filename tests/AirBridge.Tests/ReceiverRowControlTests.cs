using AirBridge.App;
using AirBridge.Core;
using System.Windows.Forms;

namespace AirBridge.Tests;

public sealed class ReceiverRowControlTests
{
    [Fact]
    public void FailedCompactRowKeepsSpaceForErrorTextWithoutShowingVolume()
    {
        using var row = new ReceiverRowControl();
        row.UseCompactLayout();
        row.Bind(new ReceiverInfo("speaker-a", "Speaker A", "local", false, DateTimeOffset.UtcNow));
        row.SetCompactStreamActive(true);
        row.SetPlaybackState(StreamState.Failed, "Password required");
        row.ApplyTextScale();
        Assert.True(row.Height >= UiGeometry.ScaleText(row, 64));
        Assert.False(Assert.Single(row.Controls.OfType<OwnerDrawnSlider>()).Visible);
        row.SetPlaybackState(StreamState.Idle);
        row.ApplyTextScale();
        Assert.Equal(UiGeometry.ScaleText(row, 44), row.Height);
    }
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
    [InlineData(false)]
    [InlineData(true)]
    public void UnchangedTelemetryRefreshDoesNotRelayoutOrRepaintTheRow(bool compact)
    {
        using var row = new ReceiverRowControl();
        if (compact) row.UseCompactLayout();
        row.Bind(new ReceiverInfo("speaker-a", "Speaker A", "local", false, DateTimeOffset.UtcNow), selected: true, volume: 40);
        row.CreateControl();
        var layouts = 0;
        var repaints = 0;
        row.Layout += (_, _) => layouts++;
        row.Invalidated += (_, _) => repaints++;

        // The once-a-second telemetry pass with nothing changed.
        for (var tick = 0; tick < 3; tick++)
        {
            row.SetPlaybackState(StreamState.Idle);
            row.SetSelected(true);
            row.SetVolume(40);
        }
        Assert.Equal(0, layouts);
        Assert.Equal(0, repaints);

        row.SetPlaybackState(StreamState.Streaming);
        Assert.True(layouts > 0);
        Assert.True(repaints > 0);
        repaints = 0;
        row.SetSelected(false);
        Assert.True(repaints > 0);
        Assert.False(row.IsSelected);
    }

    [Fact]
    public void RowAccessibilityFollowsVolumeAndRebindWithoutAStateChange()
    {
        using var row = new ReceiverRowControl();
        row.Bind(new ReceiverInfo("speaker-a", "Speaker A", "local", false, DateTimeOffset.UtcNow), volume: 40);
        row.SetVolume(65);
        Assert.Contains("volume 65 percent", row.AccessibleDescription);

        row.Bind(new ReceiverInfo("speaker-a", "Kitchen", "local", false, DateTimeOffset.UtcNow), volume: 65);
        Assert.Equal("Kitchen AirPlay speaker", row.AccessibleName);
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
