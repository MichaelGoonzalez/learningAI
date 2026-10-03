using System.Text.Json;
using HandRaise.Application.Settings;

namespace HandRaise.Application.Tests;

public sealed class NodeConnectionConfigTests
{
    [Fact]
    public void ToJson_ExportsExpectedSchemaForReact()
    {
        var config = new NodeConnectionConfig("http://192.168.1.50:5080", "test-api-key-12345");
        var json = config.ToJson();

        Assert.Contains("\"node_url\": \"http://192.168.1.50:5080\"", json);
        Assert.Contains("\"api_key\": \"test-api-key-12345\"", json);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("http://192.168.1.50:5080", root.GetProperty("node_url").GetString());
        Assert.Equal("test-api-key-12345", root.GetProperty("api_key").GetString());
    }

    [Fact]
    public void ToConnectionString_ProducesExpectedFormat()
    {
        var config = new NodeConnectionConfig("http://127.0.0.1:5080", "my-key-abc");
        var connStr = config.ToConnectionString();

        Assert.Contains("VisionNodeConnection", connStr);
        Assert.Contains("node_url=http://127.0.0.1:5080", connStr);
        Assert.Contains("api_key=my-key-abc", connStr);
    }
}
