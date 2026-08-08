using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Hysteria2Link.Plugin.Infrastructure;
using Hysteria2Link.Plugin.Models;

namespace Hysteria2Link.Plugin.Services;

internal sealed class HysteriaSessionService : IDisposable
{
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly object _snapshotLock = new();
    private readonly object _processLogLock = new();
    private readonly object _activeOperationLock = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, Task> _processLogTasks = new();
    private readonly HysteriaBinaryProvider _binaryProvider;
    private readonly HysteriaCertificateProvider _certificateProvider;
    private readonly ProcessJob _processJob;
    private readonly string _sessionsDirectory;
    private Process? _process;
    private LanBroadcaster? _broadcaster;
    private string? _activeSessionDirectory;
    private string? _lastProcessLine;
    private CancellationTokenSource? _activeOperationCancellation;
    private SessionSnapshot _snapshot = SessionSnapshot.Initial;
    private bool _lastStopWasGraceful;
    private bool _disposed;

    public HysteriaSessionService(string dataDirectory, PluginLog? log = null)
    {
        Directory.CreateDirectory(dataDirectory);
        _sessionsDirectory = Path.Combine(dataDirectory, "sessions");
        Directory.CreateDirectory(_sessionsDirectory);
        Log = log ?? new PluginLog();
        _binaryProvider = new HysteriaBinaryProvider(dataDirectory, Log);
        _processJob = new ProcessJob();
        _certificateProvider = new HysteriaCertificateProvider(dataDirectory, _processJob, Log);
        CleanupStaleSessionDirectories();
        Log.Info("Hysteria2 Realms 联机插件已初始化。");
    }

    public PluginLog Log { get; }

    public SessionSnapshot Snapshot
    {
        get
        {
            lock (_snapshotLock)
                return _snapshot;
        }
    }

    public event Action<SessionSnapshot>? SnapshotChanged;

    internal bool LastStopWasGraceful
    {
        get
        {
            lock (_processLogLock)
                return _lastStopWasGraceful;
        }
    }

    public async Task StartHostAsync(int port, string? description = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidatePort(port);
        description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        using var operationSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        var operationToken = operationSource.Token;
        SetActiveOperation(operationSource);
        var lockTaken = false;
        try
        {
            await EnsureLocalPortIsMinecraftAsync(port, operationToken).ConfigureAwait(false);
            await _operationLock.WaitAsync(operationToken).ConfigureAwait(false);
            lockTaken = true;
            ThrowIfActive();
            await CleanupInactiveSessionAsync().ConfigureAwait(false);
            SetSnapshot(new SessionSnapshot(
                SessionRole.Host,
                SessionPhase.Preparing,
                "正在准备 Hysteria2 Realms...",
                HostPort: port,
                Description: description));

            Process? process = null;
            string? sessionDirectory = null;
            try
            {
                var binaryPath = await _binaryProvider.EnsureAsync(
                    status => SetSnapshot(new SessionSnapshot(
                        SessionRole.Host,
                        SessionPhase.Preparing,
                        status,
                        HostPort: port,
                        Description: description)),
                    operationToken).ConfigureAwait(false);
                var certificate = await _certificateProvider.EnsureAsync(
                    binaryPath,
                    status => SetSnapshot(new SessionSnapshot(
                        SessionRole.Host,
                        SessionPhase.Preparing,
                        status,
                        HostPort: port,
                        Description: description)),
                    operationToken).ConfigureAwait(false);
                var link = RealmLinkCode.Create(port, certificate.PinSha256, description);
                sessionDirectory = CreateSessionDirectory("host");
                _activeSessionDirectory = sessionDirectory;
                var configPath = await HysteriaConfigWriter.WriteServerAsync(
                    sessionDirectory,
                    link,
                    certificate,
                    operationToken).ConfigureAwait(false);

                SetSnapshot(new SessionSnapshot(
                    SessionRole.Host,
                    SessionPhase.Preparing,
                    "正在通过 STUN 注册 Realm 并等待 UDP 打洞...",
                    RealmName: link.RealmName,
                    HostPort: port,
                    Description: description));
                var exitSource = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                var readySource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                process = StartProcess(
                    binaryPath,
                    ["server", "--config", configPath, "--disable-update-check"],
                    sessionDirectory,
                    exitSource,
                    line =>
                    {
                        if (line.Contains("server up and running", StringComparison.OrdinalIgnoreCase))
                            readySource.TrySetResult(true);
                    });
                _process = process;

                using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(operationToken);
                timeoutSource.CancelAfter(TimeSpan.FromSeconds(75));
                await WaitForReadyAsync(
                    readySource.Task,
                    exitSource.Task,
                    process,
                    "Hysteria 服务端",
                    timeoutSource.Token).ConfigureAwait(false);
                AttachUnexpectedExitHandler(process, SessionRole.Host);
                if (process.HasExited)
                    throw ProcessExitedException("Hysteria 服务端在 Realm 注册后立即退出", process);
                SetSnapshot(new SessionSnapshot(
                    SessionRole.Host,
                    SessionPhase.Running,
                    description is null
                        ? $"Realm 已注册，正在等待好友直连到 Minecraft 端口 {port}。"
                        : $"Realm 已注册，房间介绍「{description}」已随联机码分享，正在等待好友直连到 Minecraft 端口 {port}。",
                    link.ToString(),
                    link.RealmName,
                    HostPort: port,
                    Description: description));
                Log.Info($"房主 Realm 已注册: {link.RealmName} -> 127.0.0.1:{port}");
            }
            catch (Exception exception)
            {
                await StopProcessAsync(process).ConfigureAwait(false);
                if (ReferenceEquals(_process, process))
                    _process = null;
                DeleteSessionDirectory(sessionDirectory);
                if (string.Equals(_activeSessionDirectory, sessionDirectory, StringComparison.OrdinalIgnoreCase))
                    _activeSessionDirectory = null;
                SetSnapshot(new SessionSnapshot(
                    SessionRole.Host,
                    SessionPhase.Error,
                    FriendlyExceptionMessage(exception),
                    HostPort: port,
                    Description: description));
                Log.Error("创建 Hysteria2 Realm 联机失败", exception);
                throw;
            }
        }
        finally
        {
            ClearActiveOperation(operationSource);
            if (lockTaken)
                _operationLock.Release();
        }
    }

    public async Task StartGuestAsync(string rawCode, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var link = RealmLinkCode.Parse(rawCode);
        using var operationSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        var operationToken = operationSource.Token;
        SetActiveOperation(operationSource);
        var lockTaken = false;
        try
        {
            await _operationLock.WaitAsync(operationToken).ConfigureAwait(false);
            lockTaken = true;
            ThrowIfActive();
            await CleanupInactiveSessionAsync().ConfigureAwait(false);
            SetSnapshot(new SessionSnapshot(
                SessionRole.Guest,
                SessionPhase.Preparing,
                "正在准备 Hysteria2 Realms...",
                RealmName: link.RealmName,
                Description: link.Description));

            Process? process = null;
            string? sessionDirectory = null;
            try
            {
                var binaryPath = await _binaryProvider.EnsureAsync(
                    status => SetSnapshot(new SessionSnapshot(
                        SessionRole.Guest,
                        SessionPhase.Preparing,
                        status,
                        RealmName: link.RealmName,
                        Description: link.Description)),
                    operationToken).ConfigureAwait(false);
                var localPort = AllocateLoopbackPort();
                sessionDirectory = CreateSessionDirectory("guest");
                _activeSessionDirectory = sessionDirectory;
                var configPath = await HysteriaConfigWriter.WriteClientAsync(
                    sessionDirectory,
                    link,
                    localPort,
                    operationToken).ConfigureAwait(false);

                SetSnapshot(new SessionSnapshot(
                    SessionRole.Guest,
                    SessionPhase.Preparing,
                    "正在进行 STUN 探测、UDP 打洞和 Hysteria 握手...",
                    RealmName: link.RealmName,
                    LocalPort: localPort,
                    Description: link.Description));
                var exitSource = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                var readySource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                process = StartProcess(
                    binaryPath,
                    ["client", "--config", configPath, "--disable-update-check"],
                    sessionDirectory,
                    exitSource,
                    line =>
                    {
                        if (line.Contains("TCP forwarding listening", StringComparison.OrdinalIgnoreCase))
                            readySource.TrySetResult(true);
                    });
                _process = process;

                using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(operationToken);
                timeoutSource.CancelAfter(TimeSpan.FromSeconds(90));
                await WaitForReadyAsync(
                    readySource.Task,
                    exitSource.Task,
                    process,
                    "Hysteria 加入端",
                    timeoutSource.Token).ConfigureAwait(false);
                await WaitForLoopbackListenerAsync(
                    localPort,
                    exitSource.Task,
                    process,
                    timeoutSource.Token).ConfigureAwait(false);
                AttachUnexpectedExitHandler(process, SessionRole.Guest);
                if (process.HasExited)
                    throw ProcessExitedException("Hysteria 加入端在本地入口建立后立即退出", process);

                _broadcaster = new LanBroadcaster(link.Description ?? $"Hysteria P2P · {link.RealmName[..Math.Min(18, link.RealmName.Length)]}", localPort);
                SetSnapshot(new SessionSnapshot(
                    SessionRole.Guest,
                    SessionPhase.Running,
                    "P2P 连接和本地入口已建立，并已广播到 Minecraft 局域网列表。",
                    RealmName: link.RealmName,
                    LocalPort: localPort,
                    Description: link.Description));
                Log.Info($"加入端入口已建立: 127.0.0.1:{localPort} -> Realm {link.RealmName} -> 127.0.0.1:{link.MinecraftPort}");
                _ = ValidateGuestConnectionAsync(process, link, localPort);
            }
            catch (Exception exception)
            {
                await DisposeBroadcasterAsync().ConfigureAwait(false);
                await StopProcessAsync(process).ConfigureAwait(false);
                if (ReferenceEquals(_process, process))
                    _process = null;
                DeleteSessionDirectory(sessionDirectory);
                if (string.Equals(_activeSessionDirectory, sessionDirectory, StringComparison.OrdinalIgnoreCase))
                    _activeSessionDirectory = null;
                SetSnapshot(new SessionSnapshot(
                    SessionRole.Guest,
                    SessionPhase.Error,
                    FriendlyExceptionMessage(exception),
                    RealmName: link.RealmName,
                    Description: link.Description));
                Log.Error("加入 Hysteria2 Realm 联机失败", exception);
                throw;
            }
        }
        finally
        {
            ClearActiveOperation(operationSource);
            if (lockTaken)
                _operationLock.Release();
        }
    }

    public async Task StopAsync()
    {
        CancelActiveOperation();
        await _operationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_process is null && _broadcaster is null && Snapshot.Phase == SessionPhase.Stopped)
                return;

            var previous = Snapshot;
            SetSnapshot(previous with { Phase = SessionPhase.Stopping, Message = "正在断开 Hysteria2 联机..." });
            var process = _process;
            var sessionDirectory = _activeSessionDirectory;
            _process = null;
            _activeSessionDirectory = null;
            await DisposeBroadcasterAsync().ConfigureAwait(false);
            await StopProcessAsync(process).ConfigureAwait(false);
            DeleteSessionDirectory(sessionDirectory);
            SetSnapshot(SessionSnapshot.Initial);
            Log.Info("Hysteria2 联机已断开。");
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public void Shutdown()
    {
        _lifetimeCancellation.Cancel();
        CancelActiveOperation();
        try
        {
            StopAsync().GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            Log.Error("退出时清理 Hysteria 失败", exception);
        }
    }

    private Process StartProcess(
        string binaryPath,
        IReadOnlyList<string> arguments,
        string sessionDirectory,
        TaskCompletionSource<int> exitSource,
        Action<string>? lineObserver)
    {
        lock (_processLogLock)
        {
            _lastProcessLine = null;
            _lastStopWasGraceful = false;
        }

        var logPath = Path.Combine(sessionDirectory, "hysteria.log");
        var launcherPath = Path.Combine(sessionDirectory, "run-hysteria.cmd");
        var startGatePath = Path.Combine(sessionDirectory, "start.gate");
        WriteLauncherScript(launcherPath, startGatePath, logPath, binaryPath, arguments);
        var startInfo = new ProcessStartInfo
        {
            FileName = launcherPath,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = sessionDirectory
        };

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Exited += (_, _) =>
        {
            try
            {
                exitSource.TrySetResult(process.ExitCode);
            }
            catch
            {
                exitSource.TrySetResult(-1);
            }
        };

        if (!process.Start())
            throw new InvalidOperationException("Hysteria 进程启动失败。");
        try
        {
            _processJob.Assign(process);
            var logTask = MonitorProcessLogAsync(process, logPath, lineObserver);
            _processLogTasks[process.Id] = logTask;
            File.WriteAllText(startGatePath, "start", Encoding.ASCII);
            return process;
        }
        catch
        {
            var processId = TryGetProcessId(process);
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            if (processId >= 0)
                _processLogTasks.TryRemove(processId, out _);
            process.Dispose();
            throw;
        }
    }

    private static void WriteLauncherScript(
        string launcherPath,
        string startGatePath,
        string logPath,
        string binaryPath,
        IReadOnlyList<string> arguments)
    {
        var command = string.Join(" ", new[] { binaryPath }.Concat(arguments).Select(QuoteCmdArgument));
        var script = string.Join("\r\n",
        [
            "@echo off",
            "setlocal",
            "set \"HYSTERIA_LOG_LEVEL=info\"",
            "set \"HYSTERIA_LOG_FORMAT=json\"",
            "set \"HYSTERIA_DISABLE_UPDATE_CHECK=true\"",
            ":wait_for_start",
            $"if not exist {QuoteCmdArgument(startGatePath)} (",
            "  goto wait_for_start",
            ")",
            $"{command} 1>>{QuoteCmdArgument(logPath)} 2>&1",
            "exit /b %errorlevel%",
            string.Empty
        ]);
        File.WriteAllText(launcherPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static string QuoteCmdArgument(string value)
    {
        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    private async Task MonitorProcessLogAsync(
        Process process,
        string logPath,
        Action<string>? lineObserver)
    {
        try
        {
            while (!File.Exists(logPath))
            {
                if (process.HasExited)
                    return;
                await Task.Delay(50).ConfigureAwait(false);
            }

            await using var stream = new FileStream(
                logPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var emptyReadsAfterExit = 0;
            while (emptyReadsAfterExit < 2)
            {
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line is not null)
                {
                    emptyReadsAfterExit = 0;
                    HandleProcessLine("HY2", line, lineObserver);
                    continue;
                }

                if (process.HasExited)
                    emptyReadsAfterExit++;
                await Task.Delay(50).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            Log.Warning("读取 Hysteria 进程日志失败: " + exception.Message);
        }
    }

    private void HandleProcessLine(string stream, string? line, Action<string>? lineObserver)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        lock (_processLogLock)
        {
            _lastProcessLine = line.Trim();
            if (line.Contains("received signal, shutting down gracefully", StringComparison.OrdinalIgnoreCase))
                _lastStopWasGraceful = true;
        }
        Log.ProcessLine(stream, line);
        lineObserver?.Invoke(line);
    }

    private void AttachUnexpectedExitHandler(Process process, SessionRole role)
    {
        process.Exited += (_, _) => _ = Task.Run(async () =>
        {
            await _operationLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!ReferenceEquals(_process, process))
                    return;

                _process = null;
                var sessionDirectory = _activeSessionDirectory;
                _activeSessionDirectory = null;
                await DisposeBroadcasterAsync().ConfigureAwait(false);
                var exitCode = TryGetExitCode(process);
                await ReleaseProcessResourcesAsync(process).ConfigureAwait(false);
                DeleteSessionDirectory(sessionDirectory);
                SetSnapshot(new SessionSnapshot(
                    role,
                    SessionPhase.Error,
                    AppendLastProcessLine($"Hysteria 已意外退出（代码 {exitCode}）。")));
                Log.Warning($"Hysteria 已意外退出，代码 {exitCode}。");
            }
            finally
            {
                _operationLock.Release();
            }
        });
    }

    private async Task ValidateGuestConnectionAsync(Process process, RealmLinkCode link, int localPort)
    {
        var retryDelays = new[] { 1, 2, 4, 7, 10, 15 };
        for (var attempt = 1; attempt <= retryDelays.Length; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(retryDelays[attempt - 1]), CancellationToken.None).ConfigureAwait(false);
            if (!ReferenceEquals(_process, process))
                return;

            try
            {
                var status = await MinecraftStatusClient.QueryAsync(
                    "127.0.0.1",
                    localPort,
                    TimeSpan.FromSeconds(8)).ConfigureAwait(false);
                if (!ReferenceEquals(_process, process))
                    return;

                SetSnapshot(new SessionSnapshot(
                    SessionRole.Guest,
                    SessionPhase.Running,
                    $"已连接到 {status.Version}，本地入口为 127.0.0.1:{localPort}。",
                    RealmName: link.RealmName,
                    LocalPort: localPort,
                    Description: link.Description));
                Log.Info($"已验证房主 Minecraft 世界: {status.DisplayName}");
                return;
            }
            catch (Exception exception)
            {
                Log.Warning($"第 {attempt} 次验证房主世界失败: {exception.Message}");
            }
        }

        if (ReferenceEquals(_process, process))
        {
            SetSnapshot(new SessionSnapshot(
                SessionRole.Guest,
                SessionPhase.Running,
                "P2P 入口已建立，但尚未读取到房主世界；请确认房主仍在游戏且端口未变化。",
                RealmName: link.RealmName,
                LocalPort: localPort,
                Description: link.Description));
        }
    }

    private static async Task WaitForReadyAsync(
        Task readyTask,
        Task<int> exitTask,
        Process process,
        string processName,
        CancellationToken cancellationToken)
    {
        var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        var completed = await Task.WhenAny(readyTask, exitTask, cancellationTask).ConfigureAwait(false);
        if (completed == readyTask)
        {
            await readyTask.ConfigureAwait(false);
            return;
        }

        if (completed == exitTask)
            throw new InvalidOperationException($"{processName}在连接建立前退出（代码 {await exitTask.ConfigureAwait(false)}）。");

        cancellationToken.ThrowIfCancellationRequested();
        if (process.HasExited)
            throw new InvalidOperationException($"{processName}在连接建立前退出（代码 {TryGetExitCode(process)}）。");
    }

    private static async Task WaitForLoopbackListenerAsync(
        int port,
        Task<int> exitTask,
        Process process,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var attemptSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptSource.CancelAfter(TimeSpan.FromMilliseconds(500));
            using var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port, attemptSource.Token).ConfigureAwait(false);
                return;
            }
            catch (Exception exception) when (
                exception is SocketException
                || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                var delayTask = Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
                var completed = await Task.WhenAny(exitTask, delayTask).ConfigureAwait(false);
                if (completed == exitTask)
                    throw new InvalidOperationException($"Hysteria 在本地入口建立前退出（代码 {await exitTask.ConfigureAwait(false)}）。");
                await delayTask.ConfigureAwait(false);
                if (process.HasExited)
                    throw new InvalidOperationException($"Hysteria 在本地入口建立前退出（代码 {TryGetExitCode(process)}）。");
            }
        }
    }

    private static async Task EnsureLocalPortIsMinecraftAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            await MinecraftStatusClient.QueryAsync(
                "127.0.0.1",
                port,
                TimeSpan.FromSeconds(3),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"本地端口 {port} 不是可查询的 Minecraft Java 世界。", exception);
        }
    }

    private void SetActiveOperation(CancellationTokenSource source)
    {
        lock (_activeOperationLock)
        {
            if (_activeOperationCancellation is not null)
                throw new InvalidOperationException("当前已有联机操作正在进行。");
            _activeOperationCancellation = source;
        }
    }

    private void ClearActiveOperation(CancellationTokenSource source)
    {
        lock (_activeOperationLock)
        {
            if (ReferenceEquals(_activeOperationCancellation, source))
                _activeOperationCancellation = null;
        }
    }

    private void CancelActiveOperation()
    {
        CancellationTokenSource? source;
        lock (_activeOperationLock)
            source = _activeOperationCancellation;
        try
        {
            source?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The operation completed while cancellation was requested.
        }
    }

    private static int AllocateLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private string CreateSessionDirectory(string role)
    {
        var directory = Path.Combine(_sessionsDirectory, $"{role}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private void ThrowIfActive()
    {
        if (_process is not null || Snapshot.Phase is SessionPhase.Preparing or SessionPhase.Running or SessionPhase.Stopping)
            throw new InvalidOperationException("当前已有联机连接，请先断开。");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private async Task CleanupInactiveSessionAsync()
    {
        var process = _process;
        var sessionDirectory = _activeSessionDirectory;
        _process = null;
        _activeSessionDirectory = null;
        await DisposeBroadcasterAsync().ConfigureAwait(false);
        await StopProcessAsync(process).ConfigureAwait(false);
        DeleteSessionDirectory(sessionDirectory);
    }

    private async Task DisposeBroadcasterAsync()
    {
        var broadcaster = _broadcaster;
        _broadcaster = null;
        if (broadcaster is not null)
            await broadcaster.DisposeAsync().ConfigureAwait(false);
    }

    private async Task StopProcessAsync(Process? process)
    {
        if (process is null)
            return;

        try
        {
            if (!process.HasExited && !await TryStopGracefullyAsync(process).ConfigureAwait(false))
            {
                process.Kill(entireProcessTree: true);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or OperationCanceledException)
        {
            // The process already exited or did not stop in time.
        }
        finally
        {
            await ReleaseProcessResourcesAsync(process).ConfigureAwait(false);
        }
    }

    private async Task ReleaseProcessResourcesAsync(Process process)
    {
        var processId = TryGetProcessId(process);
        if (processId >= 0 && _processLogTasks.TryRemove(processId, out var logTask))
        {
            try
            {
                using var logTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await logTask.WaitAsync(logTimeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException)
            {
                // Process cleanup must continue even if the log tail is delayed.
            }
        }

        process.Dispose();
    }

    private static async Task<bool> TryStopGracefullyAsync(Process process)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "taskkill.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("/PID");
            startInfo.ArgumentList.Add(process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var taskKill = new Process { StartInfo = startInfo };
            if (!taskKill.Start())
                return false;

            using (var commandTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                await taskKill.WaitForExitAsync(commandTimeout.Token).ConfigureAwait(false);
            if (taskKill.ExitCode != 0)
                return process.HasExited;

            using var shutdownTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            await process.WaitForExitAsync(shutdownTimeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
            or System.ComponentModel.Win32Exception
            or OperationCanceledException)
        {
            return process.HasExited;
        }
    }

    private void CleanupStaleSessionDirectories()
    {
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(_sessionsDirectory))
                DeleteSessionDirectory(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Warning("清理旧 Hysteria 会话配置失败: " + exception.Message);
        }
    }

    private void DeleteSessionDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return;

        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Warning("清理 Hysteria 会话配置失败: " + exception.Message);
        }
    }

    private Exception ProcessExitedException(string message, Process process)
    {
        return new InvalidOperationException(AppendLastProcessLine($"{message}（代码 {TryGetExitCode(process)}）。"));
    }

    private string FriendlyExceptionMessage(Exception exception)
    {
        if (exception is OperationCanceledException)
            return AppendLastProcessLine("操作已取消，或等待 STUN、打洞与握手超时。");
        if (exception is TimeoutException)
            return AppendLastProcessLine(exception.Message);
        return AppendLastProcessLine(exception.Message);
    }

    private string AppendLastProcessLine(string message)
    {
        string? line;
        lock (_processLogLock)
            line = _lastProcessLine;
        if (string.IsNullOrWhiteSpace(line) || message.Contains(line, StringComparison.Ordinal))
            return message;
        if (line.Length > 320)
            line = line[..320] + "...";
        return $"{message} Hysteria: {line}";
    }

    private static int TryGetExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch
        {
            return -1;
        }
    }

    private static int TryGetProcessId(Process process)
    {
        try
        {
            return process.Id;
        }
        catch
        {
            return -1;
        }
    }

    private static void ValidatePort(int port)
    {
        if (port is <= 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), "Minecraft 端口必须在 1 到 65535 之间。");
    }

    private void SetSnapshot(SessionSnapshot snapshot)
    {
        lock (_snapshotLock)
            _snapshot = snapshot;
        SnapshotChanged?.Invoke(snapshot);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Shutdown();
        _processJob.Dispose();
        _lifetimeCancellation.Dispose();
        // Process exit callbacks may still be queued after Kill.
    }
}
