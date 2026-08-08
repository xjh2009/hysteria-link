using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Hysteria2Link.Plugin.Models;

internal sealed record RealmLinkCode(
    string SessionSecret,
    string RealmName,
    string AuthPassword,
    string ObfsPassword,
    string PinSha256,
    int MinecraftPort,
    string? Description = null)
{
    public const string RendezvousHost = "realm.hy2.io";
    public const string RendezvousToken = "public";
    private const int SecretLength = 16;
    private const int PinLength = 32;
    private const int PortLength = sizeof(ushort);
    private const int PayloadLength = SecretLength + PinLength + PortLength;
    private const int DescriptionMaxCharacters = 80;

    private static readonly Regex PayloadPattern = new(
        "^[A-Za-z0-9_-]{67,387}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PinPattern = new(
        "^[a-fA-F0-9]{64}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public string RealmUri => $"realm://{RendezvousToken}@{RendezvousHost}/{RealmName}";

    public static RealmLinkCode Create(int minecraftPort, string pinSha256, string? description = null)
    {
        ValidatePort(minecraftPort);
        var pin = NormalizePin(pinSha256);
        if (!PinPattern.IsMatch(pin))
            throw new ArgumentException("TLS 证书指纹格式无效。", nameof(pinSha256));

        var normalizedDescription = NormalizeDescription(description);
        var link = FromSecret(RandomNumberGenerator.GetBytes(SecretLength), minecraftPort, pin);
        return normalizedDescription is null ? link : link with { Description = normalizedDescription };
    }

    public static RealmLinkCode Parse(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
            throw new ArgumentException("请输入联机码。", nameof(rawValue));

        var encodedPayload = rawValue.Trim();
        if (!PayloadPattern.IsMatch(encodedPayload))
            throw new ArgumentException("联机码格式无效。", nameof(rawValue));

        byte[] payload;
        try
        {
            payload = DecodeBase64Url(encodedPayload);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("联机码载荷格式无效。", nameof(rawValue), exception);
        }

        if (payload.Length < PayloadLength)
            throw new ArgumentException("联机码载荷长度无效。", nameof(rawValue));

        var port = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(PayloadLength - PortLength, PortLength));
        ValidatePort(port);
        var pin = Convert.ToHexString(payload.AsSpan(SecretLength, PinLength)).ToLowerInvariant();
        var link = FromSecret(payload.AsSpan(0, SecretLength), port, pin);
        if (payload.Length == PayloadLength)
            return link;

        var description = Encoding.UTF8.GetString(payload.AsSpan(PayloadLength));
        return link with { Description = NormalizeDescription(description) };
    }

    public override string ToString()
    {
        var descriptionBytes = Description is null
            ? Array.Empty<byte>()
            : Encoding.UTF8.GetBytes(Description);
        var payload = new byte[PayloadLength + descriptionBytes.Length];
        DecodeBase64Url(SessionSecret).CopyTo(payload, 0);
        Convert.FromHexString(PinSha256).CopyTo(payload, SecretLength);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(PayloadLength - PortLength, PortLength), checked((ushort)MinecraftPort));
        descriptionBytes.CopyTo(payload, PayloadLength);
        return EncodeBase64Url(payload);
    }

    private static RealmLinkCode FromSecret(ReadOnlySpan<byte> secret, int minecraftPort, string pinSha256)
    {
        var secretBytes = secret.ToArray();
        var realmBytes = Derive(secretBytes, "realm");
        var authBytes = Derive(secretBytes, "auth");
        var obfsBytes = Derive(secretBytes, "obfs");
        return new RealmLinkCode(
            EncodeBase64Url(secretBytes),
            "mc-" + EncodeBase64Url(realmBytes.AsSpan(0, 16)),
            EncodeBase64Url(authBytes.AsSpan(0, 18)),
            EncodeBase64Url(obfsBytes.AsSpan(0, 18)),
            pinSha256,
            minecraftPort);
    }

    private static byte[] Derive(byte[] secret, string purpose)
    {
        using var hmac = new HMACSHA256(secret);
        return hmac.ComputeHash(Encoding.ASCII.GetBytes($"hy2realm:v1:{purpose}"));
    }

    private static string NormalizePin(string value)
    {
        return value.Trim().Replace(":", string.Empty).Replace("-", string.Empty).ToLowerInvariant();
    }

    private static string? NormalizeDescription(string? value)
    {
        var description = value?.Trim();
        if (string.IsNullOrEmpty(description))
            return null;
        if (description.Length > DescriptionMaxCharacters)
            throw new ArgumentException($"房间介绍不能超过 {DescriptionMaxCharacters} 个字符。", nameof(value));
        return description;
    }

    private static string EncodeBase64Url(ReadOnlySpan<byte> value)
    {
        return Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static byte[] DecodeBase64Url(string value)
    {
        var padding = (value.Length % 4) switch
        {
            0 => string.Empty,
            2 => "==",
            3 => "=",
            _ => throw new FormatException("Base64Url 长度无效。")
        };
        return Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + padding);
    }

    private static void ValidatePort(int port)
    {
        if (port is <= 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), "Minecraft 端口必须在 1 到 65535 之间。");
    }
}
