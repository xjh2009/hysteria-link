using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Hysteria2Link.Plugin.Models;
using Hysteria2Link.Plugin.Services;
using Hysteria2Link.Plugin.UI;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        TestRealmLinkCode();
        TestVarInt();
        await TestMinecraftStatusAsync();
        await TestConfigsAsync();
        TestPackageContract();
        if (args.Contains("--kernel", StringComparer.OrdinalIgnoreCase)
            || args.Contains("--network", StringComparer.OrdinalIgnoreCase))
        {
            await TestKernelAsync();
        }
        if (args.Contains("--network", StringComparer.OrdinalIgnoreCase))
            await TestRealmRoundTripAsync();
        Console.WriteLine("ALL_SMOKE_TESTS=OK");
    }

    private static void TestRealmLinkCode()
    {
        const string pin = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var link = RealmLinkCode.Create(25565, pin);
        var serialized = link.ToString();
        var parsed = RealmLinkCode.Parse(serialized);
        Assert(serialized.Length == 67, "Share code is not compact.");
        Assert(serialized.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'), "Share code is not Base64Url.");
        Assert(!serialized.Contains(RealmLinkCode.RendezvousHost, StringComparison.OrdinalIgnoreCase), "Public Realm host must not be included in share codes.");
        Assert(!serialized.Contains("://", StringComparison.Ordinal), "Share code must contain only the compact payload.");
        Assert(parsed == link, "Share code round trip failed.");
        Assert(parsed.RealmUri == $"realm://public@realm.hy2.io/{parsed.RealmName}", "Realm URI is invalid.");
        Assert(parsed.AuthPassword != parsed.ObfsPassword, "Authentication and obfuscation secrets must differ.");

        var described = RealmLinkCode.Create(25565, pin, "周日晚 8 点生存服务器，萌新友好");
        var describedCode = described.ToString();
        var parsedDescribed = RealmLinkCode.Parse(describedCode);
        Assert(describedCode.Length > 67, "Description must lengthen the share code.");
        Assert(parsedDescribed.Description == "周日晚 8 点生存服务器，萌新友好", "Description round trip failed.");
        Assert(parsedDescribed.RealmName == described.RealmName, "Description must not change Realm identity.");
        Assert(parsedDescribed.AuthPassword == described.AuthPassword, "Description must not change secrets.");
        Assert(parsedDescribed.MinecraftPort == 25565, "Description must not change the Minecraft port.");

        Assert(RealmLinkCode.Parse(serialized).Description is null, "Legacy 67-char code must stay description-less.");
        Assert(RealmLinkCode.Create(25565, pin, "   ").Description is null, "Blank description must be dropped.");
        Assert(RealmLinkCode.Create(25565, pin, "  " + new string('h', 80) + "  ").Description!.Length == 80, "Description must be trimmed but preserved up to 80 chars.");
        AssertThrows<ArgumentException>(() => RealmLinkCode.Create(25565, pin, new string('h', 81)));

        AssertThrows<ArgumentException>(() => RealmLinkCode.Parse("https://realm.hy2.io/not-a-code"));
        AssertThrows<ArgumentException>(() => RealmLinkCode.Parse(serialized + "!"));
        AssertThrows<ArgumentOutOfRangeException>(() => RealmLinkCode.Create(0, pin));
        Console.WriteLine("REALM_LINK_CODE=OK");
    }

    private static void TestVarInt()
    {
        foreach (var value in new[] { 0, 1, 127, 128, 25565, int.MaxValue, -1 })
        {
            var encoded = MinecraftProtocol.EncodeVarInt(value);
            var decoded = MinecraftProtocol.DecodeVarInt(encoded, out var bytesRead);
            Assert(decoded == value, $"VarInt round trip failed for {value}.");
            Assert(bytesRead == encoded.Length, "VarInt byte count is wrong.");
        }
        Console.WriteLine("MINECRAFT_VARINT=OK");
    }

    private static async Task TestMinecraftStatusAsync()
    {
        await using var server = new FakeMinecraftServer();
        var status = await MinecraftStatusClient.QueryAsync(
            "127.0.0.1",
            server.Port,
            TimeSpan.FromSeconds(5));
        Assert(status.Version == "Hysteria2LinkSmoke", "Fake Minecraft status server returned the wrong version.");
        Console.WriteLine("MINECRAFT_STATUS=OK");
    }

    private static async Task TestConfigsAsync()
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"hysteria-link-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            const string pin = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
            var link = RealmLinkCode.Create(25576, pin);
            var certificate = new CertificateMaterial(
                Path.Combine(temporaryRoot, "server.crt"),
                Path.Combine(temporaryRoot, "server.key"),
                pin);
            var serverPath = await HysteriaConfigWriter.WriteServerAsync(
                Path.Combine(temporaryRoot, "host"),
                link,
                certificate,
                CancellationToken.None);
            var clientPath = await HysteriaConfigWriter.WriteClientAsync(
                Path.Combine(temporaryRoot, "guest"),
                link,
                32123,
                CancellationToken.None);

            using var server = JsonDocument.Parse(await File.ReadAllTextAsync(serverPath));
            var serverRoot = server.RootElement;
            Assert(serverRoot.GetProperty("listen").GetString() == link.RealmUri, "Server Realm URI is wrong.");
            Assert(serverRoot.GetProperty("disableUDP").GetBoolean(), "Server UDP proxying must be disabled.");
            Assert(serverRoot.GetProperty("tls").GetProperty("sniGuard").GetString() == "disable", "Server SNI guard is wrong.");
            Assert(!serverRoot.TryGetProperty("realm", out _), "Automatic router port mapping must remain disabled.");
            var acl = serverRoot.GetProperty("acl").GetProperty("inline").EnumerateArray().Select(node => node.GetString()).ToArray();
            Assert(acl.SequenceEqual(new[] { "direct(127.0.0.1, tcp/25576)", "reject(all)" }), "Server ACL is not restricted to Minecraft.");

            using var client = JsonDocument.Parse(await File.ReadAllTextAsync(clientPath));
            var clientRoot = client.RootElement;
            Assert(clientRoot.GetProperty("server").GetString() == link.RealmUri, "Client Realm URI is wrong.");
            Assert(clientRoot.GetProperty("tls").GetProperty("insecure").GetBoolean(), "Pinned self-signed TLS must enable insecure mode.");
            Assert(clientRoot.GetProperty("tls").GetProperty("pinSHA256").GetString() == pin, "Client TLS pin is wrong.");
            Assert(!clientRoot.TryGetProperty("realm", out _), "Automatic router port mapping must remain disabled.");
            var forwarding = clientRoot.GetProperty("tcpForwarding")[0];
            Assert(forwarding.GetProperty("listen").GetString() == "127.0.0.1:32123", "Client listener is not loopback-only.");
            Assert(forwarding.GetProperty("remote").GetString() == "127.0.0.1:25576", "Client forwarding target is wrong.");
            Console.WriteLine("HYSTERIA_CONFIGS=OK");
        }
        finally
        {
            if (Directory.Exists(temporaryRoot))
                Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    private static void TestPackageContract()
    {
        var root = ProjectRoot();
        using var pluginJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "plugin.json")));
        Assert(pluginJson.RootElement.GetProperty("entryAssembly").GetString() == "lib/PCL.Hysteria2LinkPlugin.dll", "entryAssembly is invalid.");
        Assert(pluginJson.RootElement.GetProperty("pclCoreVersion").GetString() == "2026.07.2", "pclCoreVersion is invalid.");

        using var mixinJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "mixins", "xjh2009.hysteria.link.mixins.json")));
        Assert(mixinJson.RootElement.GetProperty("package").GetString() == "Hysteria2Link.Plugin.Mixins", "Mixin package is invalid.");

        var uiFields = typeof(PageToolsHysteria2Link)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
            .Select(field => field.Name)
            .ToHashSet();
        foreach (var requiredName in new[]
                 {
                     "_textGuestCode", "_textRoomIntro", "_comboWorldList", "_btnJoin", "_btnCreate", "_panActive", "_btnActiveCopy", "_labActiveState"
                 })
        {
            Assert(uiFields.Contains(requiredName), $"UI field {requiredName} is missing.");
        }

        Assert(HysteriaBinaryProvider.Version == "2.10.0", "Pinned Hysteria version is wrong.");
        Console.WriteLine("PACKAGE_CONTRACT=OK");
    }

    private static async Task TestKernelAsync()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "hysteria-link-kernel-smoke");
        Directory.CreateDirectory(dataDirectory);
        var provider = new HysteriaBinaryProvider(dataDirectory, new Hysteria2Link.Plugin.Infrastructure.PluginLog(enabled: false));
        var binaryPath = await provider.EnsureAsync(Console.WriteLine, CancellationToken.None);
        Assert(File.Exists(binaryPath), "Hysteria binary was not prepared.");

        var startInfo = new ProcessStartInfo
        {
            FileName = binaryPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("version");
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start Hysteria version command.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = (await outputTask) + (await errorTask);
        Assert(process.ExitCode == 0, "Hysteria version command failed.");
        Assert(output.Contains("2.10.0", StringComparison.OrdinalIgnoreCase), "Hysteria version output is wrong.");

        var silentLog = new Hysteria2Link.Plugin.Infrastructure.PluginLog(enabled: false);
        using var processJob = new Hysteria2Link.Plugin.Infrastructure.ProcessJob();
        var certificates = new HysteriaCertificateProvider(dataDirectory, processJob, silentLog);
        var material = await certificates.EnsureAsync(binaryPath, Console.WriteLine, CancellationToken.None);
        Assert(File.Exists(material.CertificatePath) && File.Exists(material.PrivateKeyPath), "TLS certificate was not created.");
        Assert(material.PinSha256.Length == 64, "TLS pin is invalid.");
        Console.WriteLine("HYSTERIA_KERNEL=OK");
    }

    private static async Task TestRealmRoundTripAsync()
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), "hysteria-link-network-smoke");
        Directory.CreateDirectory(temporaryRoot);
        await using var minecraftServer = new FakeMinecraftServer();
        using var hostService = new HysteriaSessionService(
            Path.Combine(temporaryRoot, "host"),
            new Hysteria2Link.Plugin.Infrastructure.PluginLog(enabled: false));
        using var guestService = new HysteriaSessionService(
            Path.Combine(temporaryRoot, "guest"),
            new Hysteria2Link.Plugin.Infrastructure.PluginLog(enabled: false));
        hostService.SnapshotChanged += snapshot => Console.WriteLine($"HOST_STATE={snapshot.Phase}: {snapshot.Message}");
        guestService.SnapshotChanged += snapshot => Console.WriteLine($"GUEST_STATE={snapshot.Phase}: {snapshot.Message}");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var hostStarted = false;
        var guestStarted = false;
        try
        {
            await hostService.StartHostAsync(minecraftServer.Port, cancellationToken: timeout.Token);
            hostStarted = true;
            Assert(hostService.Snapshot.Phase == SessionPhase.Running, "Host Realm did not enter running state.");
            Assert(RealmLinkCode.Parse(hostService.Snapshot.Code).MinecraftPort == minecraftServer.Port, "Host share code contains the wrong port.");

            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    await guestService.StartGuestAsync(hostService.Snapshot.Code!, timeout.Token);
                    guestStarted = true;
                    break;
                }
                catch (Exception exception) when (attempt < 3 && !timeout.IsCancellationRequested)
                {
                    Console.WriteLine($"REALM_PUNCH_RETRY={attempt}: {exception.Message}");
                    await guestService.StopAsync();
                    await Task.Delay(TimeSpan.FromSeconds(2), timeout.Token);
                }
            }

            Assert(guestStarted, "Guest Realm did not enter running state after retries.");
            var localPort = guestService.Snapshot.LocalPort ?? throw new InvalidOperationException("Guest local port was not created.");
            var status = await MinecraftStatusClient.QueryAsync("127.0.0.1", localPort, TimeSpan.FromSeconds(15));
            Assert(status.Version == "Hysteria2LinkSmoke", "P2P round trip returned the wrong Minecraft status.");
            Console.WriteLine($"REALM_P2P_ROUNDTRIP=OK ({hostService.Snapshot.RealmName} -> 127.0.0.1:{localPort})");
        }
        finally
        {
            await guestService.StopAsync();
            await hostService.StopAsync();
            if (guestStarted)
                Assert(guestService.LastStopWasGraceful, "Guest Hysteria process did not stop gracefully.");
            if (hostStarted)
                Assert(hostService.LastStopWasGraceful, "Host Hysteria process did not stop gracefully.");
            if (Directory.Exists(temporaryRoot))
                Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    private static string ProjectRoot()
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void AssertThrows<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name} was not thrown.");
    }

    private sealed class FakeMinecraftServer : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Task _acceptTask;

        public FakeMinecraftServer()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptTask = AcceptLoopAsync(_cancellation.Token);
        }

        public int Port { get; }

        private async Task AcceptLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                    _ = HandleClientAsync(client, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private static async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
        {
            using (client)
            await using (var stream = client.GetStream())
            {
                await ReadPacketAsync(stream, cancellationToken);
                await ReadPacketAsync(stream, cancellationToken);

                const string json = "{\"version\":{\"name\":\"Hysteria2LinkSmoke\",\"protocol\":760},\"players\":{\"max\":8,\"online\":1},\"description\":{\"text\":\"Smoke\"}}";
                var jsonBytes = Encoding.UTF8.GetBytes(json);
                using var payload = new MemoryStream();
                MinecraftProtocol.WriteVarInt(payload, 0);
                MinecraftProtocol.WriteVarInt(payload, jsonBytes.Length);
                payload.Write(jsonBytes);
                var payloadBytes = payload.ToArray();
                var packetLength = MinecraftProtocol.EncodeVarInt(payloadBytes.Length);
                await stream.WriteAsync(packetLength, cancellationToken);
                await stream.WriteAsync(payloadBytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
        }

        private static async Task ReadPacketAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            var length = await ReadVarIntAsync(stream, cancellationToken);
            if (length < 0 || length > 2 * 1024 * 1024)
                throw new InvalidDataException("Invalid test Minecraft packet length.");
            var payload = new byte[length];
            await stream.ReadExactlyAsync(payload, cancellationToken);
        }

        private static async Task<int> ReadVarIntAsync(Stream stream, CancellationToken cancellationToken)
        {
            var result = 0;
            for (var index = 0; index < 5; index++)
            {
                var buffer = new byte[1];
                await stream.ReadExactlyAsync(buffer, cancellationToken);
                var current = buffer[0];
                result |= (current & 0x7F) << (7 * index);
                if ((current & 0x80) == 0)
                    return result;
            }

            throw new InvalidDataException("Invalid test Minecraft VarInt.");
        }

        public async ValueTask DisposeAsync()
        {
            _cancellation.Cancel();
            _listener.Stop();
            try
            {
                await _acceptTask;
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
            _cancellation.Dispose();
        }
    }
}
