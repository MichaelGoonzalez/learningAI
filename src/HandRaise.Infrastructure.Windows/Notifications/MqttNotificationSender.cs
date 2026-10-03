using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using HandRaise.Application.Notifications;
using HandRaise.Domain.Analytics;
using HandRaise.Domain.Notifications;

namespace HandRaise.Infrastructure.Windows.Notifications;

public sealed class MqttNotificationSender : INotificationSender
{
    public NotificationDestinationType SupportedType => NotificationDestinationType.Mqtt;

    public async Task<NotificationSenderResult> SendAsync(
        NotificationDestination destination,
        WebhookPayload payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(payload);

        if (destination.Configuration == null ||
            !destination.Configuration.TryGetValue("broker", out var brokerObj) ||
            brokerObj is null ||
            string.IsNullOrWhiteSpace(brokerObj.ToString()))
        {
            return new NotificationSenderResult(false, null, "Broker MQTT no configurado.");
        }

        var broker = brokerObj.ToString()!;
        var tls = false;
        if (destination.Configuration.TryGetValue("tls", out var tlsObj) && tlsObj is not null)
        {
            _ = bool.TryParse(tlsObj.ToString(), out tls);
        }

        var port = tls ? 8883 : 1883;
        if (destination.Configuration.TryGetValue("port", out var portObj) && portObj is not null)
        {
            if (int.TryParse(portObj.ToString(), out var parsedPort) && parsedPort > 0)
            {
                port = parsedPort;
            }
        }

        var topicTemplate = "visioncontrol/{node_id}/alerts";
        if (destination.Configuration.TryGetValue("topic", out var topicObj) && topicObj is not null && !string.IsNullOrWhiteSpace(topicObj.ToString()))
        {
            topicTemplate = topicObj.ToString()!;
        }

        var resolvedTopic = topicTemplate
            .Replace("{node_id}", payload.NodeId, StringComparison.OrdinalIgnoreCase)
            .Replace("{site_id}", payload.SiteId, StringComparison.OrdinalIgnoreCase)
            .Replace("{camera_id}", payload.Alert.CameraId, StringComparison.OrdinalIgnoreCase)
            .Replace("{severity}", payload.Alert.Severity.ToString().ToLowerInvariant(), StringComparison.OrdinalIgnoreCase);

        var clientId = $"vc-edge-{payload.NodeId}";
        if (destination.Configuration.TryGetValue("client_id", out var clientObj) && clientObj is not null && !string.IsNullOrWhiteSpace(clientObj.ToString()))
        {
            clientId = clientObj.ToString()!;
        }

        string? username = null;
        if (destination.Configuration.TryGetValue("username", out var userObj) && userObj is not null)
        {
            username = userObj.ToString();
        }

        string? password = null;
        if (destination.Configuration.TryGetValue("password", out var passObj) && passObj is not null)
        {
            var p = passObj.ToString();
            if (p != "********") password = p;
        }

        var qos = 0;
        if (destination.Configuration.TryGetValue("qos", out var qosObj) && qosObj is not null)
        {
            if (int.TryParse(qosObj.ToString(), out var parsedQos) && (parsedQos == 0 || parsedQos == 1))
            {
                qos = parsedQos;
            }
        }

        var timeoutMs = 5000;
        if (destination.Configuration.TryGetValue("timeout_ms", out var timeoutObj) && timeoutObj is not null)
        {
            if (int.TryParse(timeoutObj.ToString(), out var parsedTimeout) && parsedTimeout > 0)
            {
                timeoutMs = parsedTimeout;
            }
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));

        try
        {
            using var tcpClient = new TcpClient();
            await tcpClient.ConnectAsync(broker, port, cts.Token);

            Stream networkStream = tcpClient.GetStream();
            if (tls)
            {
                var sslStream = new SslStream(networkStream, false, (sender, cert, chain, errors) => true);
                await sslStream.AuthenticateAsClientAsync(broker);
                networkStream = sslStream;
            }

            // 1. Send CONNECT Packet
            var connectPacket = BuildConnectPacket(clientId, username, password);
            await networkStream.WriteAsync(connectPacket, cts.Token);
            await networkStream.FlushAsync(cts.Token);

            // 2. Read CONNACK Packet (4 bytes: 0x20, 0x02, ackFlags, returnCode)
            var connackHeader = new byte[4];
            var bytesRead = await ReadExactAsync(networkStream, connackHeader, 0, 4, cts.Token);
            if (bytesRead < 4 || connackHeader[0] != 0x20 || connackHeader[3] != 0x00)
            {
                var returnCode = bytesRead >= 4 ? connackHeader[3] : -1;
                return new NotificationSenderResult(false, null, $"MQTT CONNACK rechazado con código de retorno: {returnCode}");
            }

            // 3. Send PUBLISH Packet
            var jsonPayload = JsonSerializer.Serialize(payload, AnalyticJsonDefaults.Options);
            var payloadBytes = Encoding.UTF8.GetBytes(jsonPayload);
            var packetId = (ushort)Random.Shared.Next(1, 65535);
            var publishPacket = BuildPublishPacket(resolvedTopic, payloadBytes, qos, packetId);

            await networkStream.WriteAsync(publishPacket, cts.Token);
            await networkStream.FlushAsync(cts.Token);

            // 4. If QoS 1, read PUBACK (4 bytes: 0x40, 0x02, idMsb, idLsb)
            if (qos == 1)
            {
                var pubackHeader = new byte[4];
                var pubackRead = await ReadExactAsync(networkStream, pubackHeader, 0, 4, cts.Token);
                if (pubackRead < 4 || pubackHeader[0] != 0x40)
                {
                    return new NotificationSenderResult(false, null, "MQTT PUBACK no recibido o inválido.");
                }
            }

            // 5. Send DISCONNECT (0xE0, 0x00)
            try
            {
                var disconnectPacket = new byte[] { 0xE0, 0x00 };
                await networkStream.WriteAsync(disconnectPacket, cts.Token);
                await networkStream.FlushAsync(cts.Token);
            }
            catch
            {
                // Ignore disconnect send errors on shutdown
            }

            return new NotificationSenderResult(
                Success: true,
                StatusCode: 0,
                Error: null,
                Metadata: new Dictionary<string, object?>
                {
                    ["topic"] = resolvedTopic,
                    ["broker"] = $"{broker}:{port}",
                    ["qos"] = qos
                });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new NotificationSenderResult(false, null, $"MQTT timeout tras {timeoutMs}ms");
        }
        catch (Exception ex)
        {
            return new NotificationSenderResult(false, null, $"Error publicando a MQTT broker '{broker}:{port}': {ex.Message}");
        }
    }

    private static byte[] BuildConnectPacket(string clientId, string? username, string? password)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // Variable Header: Protocol Name "MQTT", Level 4, Connect Flags, KeepAlive 60s
        writer.Write((byte)0x00);
        writer.Write((byte)0x04);
        writer.Write(Encoding.ASCII.GetBytes("MQTT"));
        writer.Write((byte)0x04); // Protocol Level 4 (MQTT 3.1.1)

        byte connectFlags = 0x02; // Clean Session
        if (!string.IsNullOrEmpty(username)) connectFlags |= 0x80;
        if (!string.IsNullOrEmpty(password)) connectFlags |= 0x40;
        writer.Write(connectFlags);

        // KeepAlive 60
        writer.Write((byte)0x00);
        writer.Write((byte)0x3C);

        // Payload: ClientId, Username, Password
        WriteMqttString(writer, clientId);
        if (!string.IsNullOrEmpty(username)) WriteMqttString(writer, username);
        if (!string.IsNullOrEmpty(password)) WriteMqttString(writer, password);

        var variableAndPayload = ms.ToArray();

        // Assemble with Fixed Header (0x10) + Remaining Length
        return AssemblePacket(0x10, variableAndPayload);
    }

    private static byte[] BuildPublishPacket(string topic, byte[] payload, int qos, ushort packetId)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // Topic
        WriteMqttString(writer, topic);

        // Packet ID if QoS > 0
        if (qos > 0)
        {
            writer.Write((byte)(packetId >> 8));
            writer.Write((byte)(packetId & 0xFF));
        }

        // Payload
        writer.Write(payload);

        var variableAndPayload = ms.ToArray();
        byte fixedHeader = (byte)(0x30 | (qos << 1));
        return AssemblePacket(fixedHeader, variableAndPayload);
    }

    private static void WriteMqttString(BinaryWriter writer, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        writer.Write((byte)(bytes.Length >> 8));
        writer.Write((byte)(bytes.Length & 0xFF));
        writer.Write(bytes);
    }

    private static byte[] AssemblePacket(byte fixedHeader, byte[] body)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(fixedHeader);

        // Encode remaining length
        var length = body.Length;
        do
        {
            var digit = (byte)(length % 128);
            length /= 128;
            if (length > 0) digit |= 0x80;
            ms.WriteByte(digit);
        } while (length > 0);

        ms.Write(body, 0, body.Length);
        return ms.ToArray();
    }

    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset + totalRead, count - totalRead), cancellationToken);
            if (read == 0) break;
            totalRead += read;
        }
        return totalRead;
    }
}
