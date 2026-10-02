namespace AirBridge.App;

/// <summary>Preview/fixture credentials never reach the Windows vault or a live API agent.</summary>
internal sealed class MemoryOpenAiCredentialStore : IOpenAiCredentialStore
{
    private string? _key;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_key);
    public string? Read() => _key;
    public void Write(string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        _key = apiKey.Trim();
    }
    public void Delete() => _key = null;
}
