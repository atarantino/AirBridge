using AirBridge.Core;

namespace AirBridge.Tests;

public sealed class RuntimeProfileTests
{
    [Fact]
    public void ProductionKeepsExistingStorageAndCredentialTarget()
    {
        var root = Path.Combine(Path.GetTempPath(), "airbridge-user");
        Assert.Equal(Path.Combine(root, "AirBridge"), RuntimeProfile.ResolveDataDirectory(null, root));
        Assert.Equal("AirBridge/OpenAI API Key", RuntimeProfile.ResolveCredentialTarget(null));
    }

    [Fact]
    public void WorktreeProfilesHaveSeparateStableCredentialTargets()
    {
        var first = Path.Combine(Path.GetTempPath(), "airbridge-tree-a");
        var second = Path.Combine(Path.GetTempPath(), "airbridge-tree-b");
        Assert.NotEqual(RuntimeProfile.ResolveCredentialTarget(first), RuntimeProfile.ResolveCredentialTarget(second));
        Assert.Equal(RuntimeProfile.ResolveCredentialTarget(first), RuntimeProfile.ResolveCredentialTarget(first + Path.DirectorySeparatorChar));
        Assert.NotEqual(RuntimeProfile.DefaultCredentialTarget, RuntimeProfile.ResolveCredentialTarget(first));
    }

    [Fact]
    public void ExplicitSettingsPathsStayIndependentOfProfiles()
    {
        var firstPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json");
        try
        {
            var store = new SettingsStore(firstPath);
            store.Save(new AirBridgeSettings { DefaultReceiverName = "Fixture speaker" });
            Assert.Equal(firstPath, store.Path);
            Assert.Equal("Fixture speaker", store.Load().DefaultReceiverName);
        }
        finally { Directory.Delete(Path.GetDirectoryName(firstPath)!, true); }
    }
}
