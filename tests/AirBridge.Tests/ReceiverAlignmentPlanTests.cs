using AirBridge.Core;

namespace AirBridge.Tests;

public sealed class ReceiverAlignmentPlanTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(10, 1764)]
    [InlineData(120, 21168)]
    [InlineData(500, 88200)]
    public void ConvertsMillisecondsToWholeCanonicalPcmFrames(int milliseconds, int expectedBytes)
    {
        var bytes = ReceiverAlignmentPlan.ToPcmByteCount(milliseconds);

        Assert.Equal(expectedBytes, bytes);
        Assert.Equal(0, bytes % (ReceiverAlignmentPlan.CanonicalChannels * ReceiverAlignmentPlan.CanonicalBytesPerSample));
    }

    [Fact]
    public void ProposesDifferenceFromSlowestMedianRoundedToTenMilliseconds()
    {
        var proposal = ReceiverAlignmentPlan.ProposeTrims(new Dictionary<string, int>
        {
            ["speakerA"] = 1813,
            ["office"] = 1958,
            ["bedroom"] = 1931
        });

        Assert.Equal(150, proposal["speakerA"]); // 1958 - 1813 = 145, midpoint rounds away from zero.
        Assert.Equal(0, proposal["office"]);
        Assert.Equal(30, proposal["bedroom"]); // 27 ms rounds to 30 ms.
    }

    [Fact]
    public void ProposalAndPersistedResolutionStayWithinSupportedRange()
    {
        var proposal = ReceiverAlignmentPlan.ProposeTrims(new Dictionary<string, int>
        {
            ["fast"] = 1000,
            ["slow"] = 1900
        });

        Assert.Equal(500, proposal["fast"]);
        Assert.Equal(0, proposal["slow"]);
        Assert.Equal(0, ReceiverAlignmentPlan.Resolve("fast", new Dictionary<string, int> { ["fast"] = -20 }));
        Assert.Equal(500, ReceiverAlignmentPlan.Resolve("slow", new Dictionary<string, int> { ["slow"] = 900 }));
        Assert.Equal(0, ReceiverAlignmentPlan.Resolve("missing", null));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(501)]
    public void RejectsUnsupportedTrimForByteConversion(int milliseconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReceiverAlignmentPlan.ToPcmByteCount(milliseconds));
    }

    [Fact]
    public void MeasurementIsolationFloorsOnlyNonTargetReceiversAndOriginalPlanIsRestorable()
    {
        var original = new Dictionary<string, int> { ["speakerA"] = 18, ["beam"] = 8, ["office"] = 42 };

        var isolated = ReceiverAlignmentPlan.MeasurementVolumePlan("beam", original);

        Assert.Equal(0, isolated["speakerA"]);
        Assert.Equal(8, isolated["beam"]);
        Assert.Equal(0, isolated["office"]);
        Assert.Equal(18, original["speakerA"]);
        Assert.Equal(42, original["office"]);
    }

    [Fact]
    public void ReportsPairwiseSkewAndIdentifiesEarlierReceiver()
    {
        var skews = ReceiverAlignmentPlan.PairwiseSkews(new Dictionary<string, int>
        {
            ["speakerA"] = 1840,
            ["beam"] = 1912
        });

        var skew = Assert.Single(skews);
        Assert.Equal(72, skew.SkewMilliseconds);
        Assert.Equal("speakerA", skew.EarlyReceiverId);
    }

    [Fact]
    public void EqualMediansProduceZeroTrimForEveryReceiver()
    {
        var proposal = ReceiverAlignmentPlan.ProposeTrims(new Dictionary<string, int>
        {
            ["speakerA"] = 1900,
            ["beam"] = 1900,
            ["office"] = 1900
        });
        Assert.All(proposal.Values, value => Assert.Equal(0, value));
    }

    [Fact]
    public void ExistingTrimIsRemovedBeforeComputingAbsoluteReplacementProposal()
    {
        var measured = new Dictionary<string, int> { ["speakerA"] = 1900, ["beam"] = 1900 };
        var current = new Dictionary<string, int> { ["speakerA"] = 100, ["beam"] = 0 };

        var untrimmed = ReceiverAlignmentPlan.RemoveAppliedTrims(measured, current);
        var proposal = ReceiverAlignmentPlan.ProposeTrims(untrimmed);

        Assert.Equal(1800, untrimmed["speakerA"]);
        Assert.Equal(1900, untrimmed["beam"]);
        Assert.Equal(100, proposal["speakerA"]);
        Assert.Equal(0, proposal["beam"]);
    }

    [Fact]
    public void AlignmentProposalRejectsChangedRouteMembershipOrTrimSnapshot()
    {
        var result = new GroupAlignmentResult([], [], new Dictionary<string, int> { ["speakerA"] = 60 }, false)
        {
            RouteStreamId = "route-a",
            RouteReceiverIds = ["speakerA", "beam"],
            BaselineTrimMilliseconds = new Dictionary<string, int> { ["speakerA"] = 20, ["beam"] = 0 }
        };
        var trims = new Dictionary<string, int> { ["speakerA"] = 20, ["beam"] = 0 };

        GroupAlignmentApplicability.Validate(result, "route-a", ["beam", "speakerA"], trims);
        Assert.Throws<InvalidOperationException>(() => GroupAlignmentApplicability.Validate(result, "route-b", ["beam", "speakerA"], trims));
        Assert.Throws<InvalidOperationException>(() => GroupAlignmentApplicability.Validate(result, "route-a", ["speakerA"], trims));
        Assert.Throws<InvalidOperationException>(() => GroupAlignmentApplicability.Validate(
            result,
            "route-a",
            ["beam", "speakerA"],
            new Dictionary<string, int> { ["speakerA"] = 30, ["beam"] = 0 }));
    }
}
