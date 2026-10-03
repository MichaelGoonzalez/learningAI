using System.Text.Json;

namespace HandRaise.Application.Settings;

public sealed record NodeConnectionConfig(string NodeUrl, string ApiKey)
{
    public string ToJson() => JsonSerializer.Serialize(new
    {
        node_url = NodeUrl,
        api_key = ApiKey
    }, new JsonSerializerOptions { WriteIndented = true });

    public string ToConnectionString() =>
        $"VisionNodeConnection\nnode_url={NodeUrl}\napi_key={ApiKey}";
}
