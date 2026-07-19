using System.Text.Json;
using AirBridge.Core;

namespace AirBridge.Tests;

public sealed class OpenAiToolSchemaTests
{
    [Fact]
    public void AlignmentAndStandbyToolsUseSpecifiedStrictContracts()
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(OpenAiAgent.ToolDefinitions));
        var tools = document.RootElement.EnumerateArray().ToDictionary(item => item.GetProperty("name").GetString()!);

        Assert.Contains("get_alignment", tools.Keys);
        Assert.Contains("set_alignment_trim", tools.Keys);
        Assert.Contains("align_group", tools.Keys);
        Assert.Contains("get_standby", tools.Keys);
        Assert.Contains("set_standby", tools.Keys);
        Assert.True(tools["set_alignment_trim"].GetProperty("strict").GetBoolean());
        Assert.Empty(tools["get_alignment"].GetProperty("parameters").GetProperty("properties").EnumerateObject());
        Assert.Equal(0, tools["set_alignment_trim"].GetProperty("parameters").GetProperty("properties").GetProperty("trim_ms").GetProperty("minimum").GetInt32());
        Assert.Equal(ReceiverAlignmentPlan.MaximumTrimMilliseconds, tools["set_alignment_trim"].GetProperty("parameters").GetProperty("properties").GetProperty("trim_ms").GetProperty("maximum").GetInt32());
        Assert.Equal(10, tools["set_standby"].GetProperty("parameters").GetProperty("properties").GetProperty("after_seconds").GetProperty("minimum").GetInt32());
        Assert.Equal(600, tools["set_standby"].GetProperty("parameters").GetProperty("properties").GetProperty("after_seconds").GetProperty("maximum").GetInt32());
    }
}
