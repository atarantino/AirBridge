using AirBridge.App;

namespace AirBridge.Tests;

public sealed class OpenAiCredentialStoreTests
{
    [Fact]
    public void CredentialManagerRoundTripAndDeleteUsesIsolatedTarget()
    {
        var store = new WindowsOpenAiCredentialStore($"AirBridge.Tests/{Guid.NewGuid():N}");
        try
        {
            Assert.False(store.IsConfigured);
            store.Write("  test-api-key-value  ");
            Assert.True(store.IsConfigured);
            Assert.Equal("test-api-key-value", store.Read());
            store.Delete();
            Assert.False(store.IsConfigured);
            store.Delete();
        }
        finally { store.Delete(); }
    }

    [Fact]
    public void CredentialManagerRejectsEmptySecret()
    {
        var store = new WindowsOpenAiCredentialStore($"AirBridge.Tests/{Guid.NewGuid():N}");
        Assert.Throws<ArgumentException>(() => store.Write("   "));
    }
}
