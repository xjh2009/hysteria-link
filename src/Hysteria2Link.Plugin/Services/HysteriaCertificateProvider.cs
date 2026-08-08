using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Hysteria2Link.Plugin.Infrastructure;

namespace Hysteria2Link.Plugin.Services;

internal sealed record CertificateMaterial(string CertificatePath, string PrivateKeyPath, string PinSha256);

internal sealed class HysteriaCertificateProvider
{
    private readonly string _certificateDirectory;
    private readonly string _certificatePath;
    private readonly string _privateKeyPath;
    private readonly SemaphoreSlim _ensureLock = new(1, 1);
    private readonly ProcessJob _processJob;
    private readonly PluginLog _log;

    public HysteriaCertificateProvider(string dataDirectory, ProcessJob processJob, PluginLog log)
    {
        _certificateDirectory = Path.Combine(dataDirectory, "certificate");
        _certificatePath = Path.Combine(_certificateDirectory, "server.crt");
        _privateKeyPath = Path.Combine(_certificateDirectory, "server.key");
        _processJob = processJob;
        _log = log;
        Directory.CreateDirectory(_certificateDirectory);
    }

    public async Task<CertificateMaterial> EnsureAsync(
        string binaryPath,
        Action<string>? reportStatus,
        CancellationToken cancellationToken)
    {
        await _ensureLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            reportStatus?.Invoke("正在检查 Hysteria TLS 证书...");
            if (TryReadMaterial(out var material))
                return material;

            TryDelete(_certificatePath);
            TryDelete(_privateKeyPath);
            reportStatus?.Invoke("正在生成 Hysteria TLS 证书...");
            var startInfo = new ProcessStartInfo
            {
                FileName = binaryPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = _certificateDirectory
            };
            foreach (var argument in new[]
                     {
                         "cert",
                         "--host", "realm.hy2.io",
                         "--cert", _certificatePath,
                         "--key", _privateKeyPath,
                         "--valid-for", "8760h",
                         "--overwrite"
                     })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
                throw new InvalidOperationException("Hysteria 证书生成进程启动失败。");
            try
            {
                _processJob.Assign(process);
            }
            catch
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
                throw;
            }
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                try
                {
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    // The process already exited while cancellation was handled.
                }

                if (cancellationToken.IsCancellationRequested)
                    throw;
                throw new TimeoutException("Hysteria TLS 证书生成超时。");
            }

            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Hysteria TLS 证书生成失败（代码 {process.ExitCode}）：{error.Trim()}{output.Trim()}");
            if (!TryReadMaterial(out material))
                throw new InvalidDataException("Hysteria 已生成证书，但证书或私钥无法读取。");

            reportStatus?.Invoke("Hysteria TLS 证书已准备完成。");
            return material;
        }
        finally
        {
            _ensureLock.Release();
        }
    }

    private bool TryReadMaterial(out CertificateMaterial material)
    {
        material = null!;
        if (!File.Exists(_certificatePath) || !File.Exists(_privateKeyPath))
            return false;

        try
        {
            using var certificate = X509Certificate2.CreateFromPemFile(_certificatePath, _privateKeyPath);
            if (!certificate.HasPrivateKey || certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow.AddDays(30))
                return false;

            var pin = Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256)).ToLowerInvariant();
            material = new CertificateMaterial(_certificatePath, _privateKeyPath, pin);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // The following certificate command reports a more actionable error.
        }
        catch (UnauthorizedAccessException)
        {
            // The following certificate command reports a more actionable error.
        }
    }
}
