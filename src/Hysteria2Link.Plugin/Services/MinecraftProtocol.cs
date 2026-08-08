namespace Hysteria2Link.Plugin.Services;

internal static class MinecraftProtocol
{
    public static byte[] EncodeVarInt(int value)
    {
        using var stream = new MemoryStream(5);
        WriteVarInt(stream, value);
        return stream.ToArray();
    }

    public static int DecodeVarInt(ReadOnlySpan<byte> bytes, out int bytesRead)
    {
        var result = 0;
        bytesRead = 0;
        while (bytesRead < bytes.Length)
        {
            var current = bytes[bytesRead++];
            result |= (current & 0x7F) << (7 * (bytesRead - 1));
            if ((current & 0x80) == 0)
                return result;
            if (bytesRead >= 5)
                throw new InvalidDataException("Minecraft VarInt 超过 5 字节。");
        }

        throw new EndOfStreamException("Minecraft VarInt 数据不完整。");
    }

    public static void WriteVarInt(Stream stream, int value)
    {
        var unsignedValue = unchecked((uint)value);
        do
        {
            var current = (byte)(unsignedValue & 0x7F);
            unsignedValue >>= 7;
            if (unsignedValue != 0)
                current |= 0x80;
            stream.WriteByte(current);
        } while (unsignedValue != 0);
    }
}
