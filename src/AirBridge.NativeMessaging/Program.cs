using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

// Chromium Native Messaging uses a 32-bit little-endian length followed by UTF-8 JSON.
var input = Console.OpenStandardInput();
var output = Console.OpenStandardOutput();
var header = new byte[4];
while (await ReadExactAsync(input, header))
{
    var length = BinaryPrimitives.ReadInt32LittleEndian(header);
    if (length is <= 0 or > 1024 * 1024) break;
    var payload = new byte[length];
    if (!await ReadExactAsync(input, payload)) break;
    using var request = JsonDocument.Parse(payload);
    var response = JsonSerializer.SerializeToUtf8Bytes(new
    {
        ok = true,
        type = "airbridge_sync",
        active = request.RootElement.TryGetProperty("active", out var active) && active.GetBoolean(),
        offsetMs = request.RootElement.TryGetProperty("offsetMs", out var offset) ? Math.Clamp(offset.GetInt32(), 0, 10000) : 2000
    });
    BinaryPrimitives.WriteInt32LittleEndian(header, response.Length);
    await output.WriteAsync(header);
    await output.WriteAsync(response);
    await output.FlushAsync();
}

static async Task<bool> ReadExactAsync(Stream stream, byte[] destination)
{
    var read = 0;
    while (read < destination.Length)
    {
        var count = await stream.ReadAsync(destination.AsMemory(read));
        if (count == 0) return false;
        read += count;
    }
    return true;
}

