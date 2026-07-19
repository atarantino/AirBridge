using AirBridge.App;
using AirBridge.Core;

namespace AirBridge.Tests;

public sealed class ReceiverRowControlTests
{
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
