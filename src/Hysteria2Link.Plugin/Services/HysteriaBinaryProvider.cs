using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Hysteria2Link.Plugin.Infrastructure;

namespace Hysteria2Link.Plugin.Services;

internal sealed class HysteriaBinaryProvider
{
    internal const string Version = "2.10.0";

    private static readonly IReadOnlyDictionary<Architecture, BinaryAsset> Assets =
        new Dictionary<Architecture, BinaryAsset>
        {
            [Architecture.X64] = new(
                "hysteria-windows-amd64.exe",
                "a0b4b1851919235b9424632b894b5232eec861c1c20e955e82e3dbc6698490d0"),
            [Architecture.Arm64] = new(
                "hysteria-windows-arm64.exe",
                "ea1d6123620aa8c79d6e5409372524a0f7f7d9c7cc60c5c40fdcff1a12466b8d"),
            [Architecture.X86] = new(
                "hysteria-windows-386.exe",
                "0882ba044cb6ade3ab4f6b3e464fd721a1bc2e0f3250becbfde2ba169176a092")
        };

    private readonly string _binaryPath;
    private readonly SemaphoreSlim _ensureLock = new(1, 1);
    private readonly PluginLog _log;
    private readonly BinaryAsset _asset;

    public HysteriaBinaryProvider(string dataDirectory, PluginLog log)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Hysteria2 联机插件目前只支持 Windows。");
        if (!Assets.TryGetValue(RuntimeInformation.OSArchitecture, out _asset!))
            throw new PlatformNotSupportedException($"暂不支持 {RuntimeInformation.OSArchitecture} 架构。");

        var toolDirectory = Path.Combine(dataDirectory, "tools", Version, RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant());
        Directory.CreateDirectory(toolDirectory);
        _binaryPath = Path.Combine(toolDirectory, "hysteria.exe");
        _log = log;
    }

    public async Task<string> EnsureAsync(Action<string>? reportStatus, CancellationToken cancellationToken)
    {
        await _ensureLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            reportStatus?.Invoke($"正在检查 Hysteria {Version}...");
            if (await HasExpectedHashAsync(_binaryPath, cancellationToken).ConfigureAwait(false))
            {
                reportStatus?.Invoke($"Hysteria {Version} 已准备完成。");
                return _binaryPath;
            }

            foreach (var candidate in FindInstalledCandidates())
            {
                if (!await HasExpectedHashAsync(candidate, cancellationToken).ConfigureAwait(false))
                    continue;

                reportStatus?.Invoke("正在使用电脑中已有的 Hysteria...");
                var copyPath = _binaryPath + ".copying";
                File.Copy(candidate, copyPath, overwrite: true);
                File.Move(copyPath, _binaryPath, overwrite: true);
                _log.Info($"复用已安装的 Hysteria {Version}: {candidate}");
                reportStatus?.Invoke($"Hysteria {Version} 已准备完成。");
                return _binaryPath;
            }

            var uri = DownloadUri();
            try
            {
                await DownloadAsync(uri, reportStatus, cancellationToken).ConfigureAwait(false);
                return _binaryPath;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                _log.Warning($"从 GitHub Release 下载 Hysteria 失败: {exception.Message}");
                throw new InvalidOperationException(
                    "无法从 GitHub Release 下载 Hysteria，请检查网络连接。",
                    exception);
            }
        }
        finally
        {
            _ensureLock.Release();
        }
    }

    private Uri DownloadUri()
    {
        return new Uri($"https://github.com/apernet/hysteria/releases/download/app%2Fv{Version}/{_asset.Name}");
    }

    private async Task DownloadAsync(Uri uri, Action<string>? reportStatus, CancellationToken cancellationToken)
    {
        reportStatus?.Invoke($"正在从 GitHub Release 下载 Hysteria {Version}...");
        var temporaryPath = _binaryPath + ".download";
        TryDeleteFile(temporaryPath);
        var installed = false;
        try
        {
            using var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.All
            };
            using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
            httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Hysteria2Link", "1.0"));
            using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var totalLength = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (var destination = new FileStream(
                             temporaryPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             1024 * 128,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[1024 * 128];
                long downloaded = 0;
                var lastReportedPercent = -1;
                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                        break;

                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    downloaded += read;
                    if (totalLength is > 0)
                    {
                        var percent = (int)(downloaded * 100 / totalLength.Value);
                        if (percent >= lastReportedPercent + 5)
                        {
                            lastReportedPercent = percent;
                            reportStatus?.Invoke($"正在从 GitHub Release 下载 Hysteria {Version}... {percent}%");
                        }
                    }
                }

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            reportStatus?.Invoke($"正在校验 Hysteria {Version}...");
            if (!await HasExpectedHashAsync(temporaryPath, cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("Hysteria SHA-256 校验失败，已删除下载文件。");

            File.Move(temporaryPath, _binaryPath, overwrite: true);
            installed = true;
            reportStatus?.Invoke($"Hysteria {Version} 已准备完成。");
            _log.Info($"Hysteria {Version} 下载并校验完成。");
        }
        finally
        {
            if (!installed)
                TryDeleteFile(temporaryPath);
        }
    }

    private IEnumerable<string> FindInstalledCandidates()
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var explicitPath = Environment.GetEnvironmentVariable("HYSTERIA_PATH");
        if (!string.IsNullOrWhiteSpace(explicitPath))
            candidates.Add(explicitPath.Trim('"'));

        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Hysteria", "hysteria.exe"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Hysteria", "hysteria.exe"));
        foreach (var pathDirectory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                candidates.Add(Path.Combine(pathDirectory.Trim('"'), "hysteria.exe"));
            }
            catch (ArgumentException)
            {
                // Ignore malformed PATH entries.
            }
        }

        return candidates.Where(File.Exists);
    }

    private async Task<bool> HasExpectedHashAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return false;

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 128,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).Equals(_asset.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // A later download attempt will retry cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // Preserve the original download error.
        }
    }

    private sealed record BinaryAsset(string Name, string Sha256);
}
