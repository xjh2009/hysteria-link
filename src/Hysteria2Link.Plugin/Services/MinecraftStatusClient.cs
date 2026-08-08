using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Hysteria2Link.Plugin.Services;

internal sealed record MinecraftServerStatus(int Port, string Version, int OnlinePlayers, int MaximumPlayers, string Description)
{
    public string DisplayName => $"{Port} · {Version} · {OnlinePlayers}/{MaximumPlayers}";
}

internal static class MinecraftStatusClient
{
    private const int MaximumPacketLength = 2 * 1024 * 1024;

    public static async Task<MinecraftServerStatus> QueryAsync(
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var token = timeoutSource.Token;

        using var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(host, port, token).ConfigureAwait(false);
        await using var stream = client.GetStream();

        using var handshake = new MemoryStream();
        MinecraftProtocol.WriteVarInt(handshake, 0);
        MinecraftProtocol.WriteVarInt(handshake, 760);
        WriteString(handshake, host);
        Span<byte> portBytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(portBytes, checked((ushort)port));
        handshake.Write(portBytes);
        MinecraftProtocol.WriteVarInt(handshake, 1);

        await WritePacketAsync(stream, handshake.ToArray(), token).ConfigureAwait(false);
        await WritePacketAsync(stream, [0], token).ConfigureAwait(false);

        var packetLength = await ReadVarIntAsync(stream, token).ConfigureAwait(false);
        if (packetLength <= 0 || packetLength > MaximumPacketLength)
            throw new InvalidDataException("Minecraft 状态包长度无效。");

        var packetId = await ReadVarIntAsync(stream, token).ConfigureAwait(false);
        if (packetId != 0)
            throw new InvalidDataException("Minecraft 状态包 ID 无效。");

        var jsonLength = await ReadVarIntAsync(stream, token).ConfigureAwait(false);
        if (jsonLength <= 0 || jsonLength > MaximumPacketLength)
            throw new InvalidDataException("Minecraft 状态 JSON 长度无效。");

        var jsonBytes = new byte[jsonLength];
        await stream.ReadExactlyAsync(jsonBytes, token).ConfigureAwait(false);
        using var document = JsonDocument.Parse(jsonBytes);
        var root = document.RootElement;
        var version = root.TryGetProperty("version", out var versionNode) && versionNode.TryGetProperty("name", out var versionName)
            ? versionName.GetString() ?? "Minecraft"
            : "Minecraft";
        var online = 0;
        var maximum = 0;
        if (root.TryGetProperty("players", out var players))
        {
            if (players.TryGetProperty("online", out var onlineNode))
                online = onlineNode.GetInt32();
            if (players.TryGetProperty("max", out var maximumNode))
                maximum = maximumNode.GetInt32();
        }

        var description = root.TryGetProperty("description", out var descriptionNode)
            ? FlattenDescription(descriptionNode)
            : string.Empty;
        return new MinecraftServerStatus(port, version, online, maximum, description);
    }

    private static async Task WritePacketAsync(NetworkStream stream, byte[] packet, CancellationToken cancellationToken)
    {
        var length = MinecraftProtocol.EncodeVarInt(packet.Length);
        await stream.WriteAsync(length, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(packet, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        MinecraftProtocol.WriteVarInt(stream, bytes.Length);
        stream.Write(bytes);
    }

    private static async Task<int> ReadVarIntAsync(Stream stream, CancellationToken cancellationToken)
    {
        var value = 0;
        for (var index = 0; index < 5; index++)
        {
            var buffer = new byte[1];
            await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
            var current = buffer[0];
            value |= (current & 0x7F) << (7 * index);
            if ((current & 0x80) == 0)
                return value;
        }

        throw new InvalidDataException("Minecraft VarInt 超过 5 字节。");
    }

    private static string FlattenDescription(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.String)
            return node.GetString() ?? string.Empty;
        if (node.ValueKind != JsonValueKind.Object)
            return string.Empty;

        var builder = new StringBuilder();
        if (node.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            builder.Append(text.GetString());
        if (node.TryGetProperty("extra", out var extra) && extra.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in extra.EnumerateArray())
                builder.Append(FlattenDescription(child));
        }

        return builder.ToString();
    }
}
