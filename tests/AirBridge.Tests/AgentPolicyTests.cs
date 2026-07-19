using System.Text.Json;
using AirBridge.Core;

namespace AirBridge.Tests;

public sealed class AgentPolicyTests
{
    private readonly AgentPolicy _policy = new();

    [Fact]
    public void RejectsUnknownTool()
    {
        using var args = JsonDocument.Parse("{}");
        Assert.False(_policy.Evaluate("run_powershell", args.RootElement).Allowed);
    }

    [Fact]
    public void PersistentActionRequiresConfirmation()
    {
        using var args = JsonDocument.Parse("{}");
        var decision = _policy.Evaluate("save_routing_rule", args.RootElement);
        Assert.False(decision.Allowed);
        Assert.True(decision.RequiresConfirmation);
    }

    [Fact]
    public void AcousticMeasurementRequiresExplicitConfirmation()
    {
        using var args = JsonDocument.Parse("{\"receiver_id\":\"speakerA\"}");
        Assert.True(_policy.Evaluate("measure_acoustic_delay", args.RootElement).RequiresConfirmation);
        Assert.True(_policy.Evaluate("measure_acoustic_delay", args.RootElement, userConfirmed: true).Allowed);
    }

    [Fact]
    public void AlignGroupRequiresAnExplicitlyAuthorizedMicrophoneAction()
    {
        using var args = JsonDocument.Parse("{\"receiver_ids\":[\"speakerA\",\"beam\"]}");
        Assert.True(_policy.Evaluate("align_group", args.RootElement).RequiresConfirmation);
        Assert.True(_policy.Evaluate("align_group", args.RootElement, userConfirmed: true).Allowed);
    }

    [Fact]
    public void ConfirmationIsBoundToCanonicalArguments()
    {
        var store = new ToolConfirmationStore();
        using var requested = JsonDocument.Parse("{\"receiver_ids\":[\"receiver-1\",\"receiver-2\"],\"mode\":\"group\"}");
        using var reordered = JsonDocument.Parse("{\"mode\":\"group\",\"receiver_ids\":[\"receiver-1\",\"receiver-2\"]}");
        using var receiversReordered = JsonDocument.Parse("{\"mode\":\"group\",\"receiver_ids\":[\"receiver-2\",\"receiver-1\"]}");
        using var changed = JsonDocument.Parse("{\"mode\":\"group\",\"receiver_ids\":[\"receiver-1\",\"receiver-3\"]}");

        store.Request("align_group", requested.RootElement);
        Assert.False(store.TryConsume("align_group", changed.RootElement));
        Assert.True(store.TryConsume("align_group", receiversReordered.RootElement));
        Assert.False(store.TryConsume("align_group", reordered.RootElement));
    }

    [Theory]
    [InlineData("align the speaker group", true)]
    [InlineData("please align the speakers in Speaker A", true)]
    [InlineData("I explicitly allow you to align the speakers", true)]
    [InlineData("I approve this alignment", true)]
    [InlineData("let's align the kitchen and office", true)]
    [InlineData("you have my permission to align the speaker group", true)]
    [InlineData("don't align the speakers", false)]
    [InlineData("tell me whether we should align the speakers", false)]
    public void DirectMicrophoneAuthorizationRequiresClearNonNegatedImperativeAndIsOneShot(string text, bool allowed)
    {
        var authorization = DirectMicrophoneAuthorization.FromUserText(text);
        Assert.Equal(allowed, authorization.TryConsume("align_group"));
        Assert.False(authorization.TryConsume("align_group"));
    }

    [Fact]
    public void BufferBoundsAreEnforced()
    {
        using var args = JsonDocument.Parse("{\"milliseconds\":99}");
        Assert.False(_policy.Evaluate("set_buffer_target", args.RootElement).Allowed);
    }

    [Fact]
    public void ReversibleRouteIsAllowed()
    {
        using var args = JsonDocument.Parse("{}");
        Assert.True(_policy.Evaluate("start_system_stream", args.RootElement).Allowed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(60)]
    [InlineData(500)]
    [InlineData(2000)]
    public void ReceiverAlignmentTrimAllowsDocumentedRange(int delayMs)
    {
        using var args = JsonDocument.Parse($$"""{"receiver_id":"speakerA","trim_ms":{{delayMs}}}""");
        Assert.True(_policy.Evaluate("set_alignment_trim", args.RootElement).Allowed);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2001)]
    public void ReceiverAlignmentTrimRejectsValuesOutsideDocumentedRange(int delayMs)
    {
        using var args = JsonDocument.Parse($$"""{"receiver_id":"speakerA","trim_ms":{{delayMs}}}""");
        Assert.False(_policy.Evaluate("set_alignment_trim", args.RootElement).Allowed);
    }

    [Fact]
    public void ReceiverAlignmentTrimReadIsAllowed()
    {
        using var args = JsonDocument.Parse("{}");
        Assert.True(_policy.Evaluate("get_alignment", args.RootElement).Allowed);
    }

    [Theory]
    [InlineData(10, true)]
    [InlineData(600, true)]
    [InlineData(9, false)]
    [InlineData(601, false)]
    public void SilenceStandbyEnforcesDocumentedRange(int seconds, bool allowed)
    {
        using var args = JsonDocument.Parse($$"""{"enabled":true,"after_seconds":{{seconds}}}""");
        Assert.Equal(allowed, _policy.Evaluate("set_standby", args.RootElement).Allowed);
    }
}
