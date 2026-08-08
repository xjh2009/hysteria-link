using System.Text.Json;
using Hysteria2Link.Plugin.Models;

namespace Hysteria2Link.Plugin.Services;

internal static class HysteriaConfigWriter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true
    };

    public static async Task<string> WriteServerAsync(
        string sessionDirectory,
        RealmLinkCode link,
        CertificateMaterial certificate,
        CancellationToken cancellationToken)
    {
        var config = new Dictionary<string, object?>
        {
            ["listen"] = link.RealmUri,
            ["tls"] = new Dictionary<string, object?>
            {
                ["cert"] = certificate.CertificatePath,
                ["key"] = certificate.PrivateKeyPath,
                ["sniGuard"] = "disable"
            },
            ["auth"] = new Dictionary<string, object?>
            {
                ["type"] = "password",
                ["password"] = link.AuthPassword
            },
            ["obfs"] = new Dictionary<string, object?>
            {
                ["type"] = "salamander",
                ["salamander"] = new Dictionary<string, object?>
                {
                    ["password"] = link.ObfsPassword
                }
            },
            ["disableUDP"] = true,
            ["acl"] = new Dictionary<string, object?>
            {
                ["inline"] = new[]
                {
                    $"direct(127.0.0.1, tcp/{link.MinecraftPort})",
                    "reject(all)"
                }
            }
        };

        return await WriteAsync(sessionDirectory, "server.json", config, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<string> WriteClientAsync(
        string sessionDirectory,
        RealmLinkCode link,
        int localPort,
        CancellationToken cancellationToken)
    {
        var config = new Dictionary<string, object?>
        {
            ["server"] = link.RealmUri,
            ["auth"] = link.AuthPassword,
            ["tls"] = new Dictionary<string, object?>
            {
                ["insecure"] = true,
                ["pinSHA256"] = link.PinSha256
            },
            ["obfs"] = new Dictionary<string, object?>
            {
                ["type"] = "salamander",
                ["salamander"] = new Dictionary<string, object?>
                {
                    ["password"] = link.ObfsPassword
                }
            },
            ["tcpForwarding"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["listen"] = $"127.0.0.1:{localPort}",
                    ["remote"] = $"127.0.0.1:{link.MinecraftPort}"
                }
            }
        };

        return await WriteAsync(sessionDirectory, "client.json", config, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> WriteAsync(
        string sessionDirectory,
        string fileName,
        object config,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(sessionDirectory);
        var path = Path.Combine(sessionDirectory, fileName);
        var json = JsonSerializer.Serialize(config, Options);
        await File.WriteAllTextAsync(path, json, cancellationToken).ConfigureAwait(false);
        return path;
    }
}
