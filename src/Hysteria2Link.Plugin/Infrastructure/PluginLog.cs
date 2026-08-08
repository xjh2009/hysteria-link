using PCL.Core.Logging;

namespace Hysteria2Link.Plugin.Infrastructure;

internal sealed class PluginLog
{
    public const string ModuleName = "Hysteria2Link";
    private readonly bool _enabled;

    public PluginLog(bool enabled = true)
    {
        _enabled = enabled;
    }

    public void Info(string message)
    {
        if (_enabled)
            LogWrapper.Info(ModuleName, message);
    }

    public void Warning(string message)
    {
        if (_enabled)
            LogWrapper.Warn(ModuleName, message);
    }

    public void Error(string message, Exception? exception = null)
    {
        if (_enabled)
            LogWrapper.Error(exception, ModuleName, message);
    }

    public void ProcessLine(string stream, string line)
    {
        if (!_enabled || string.IsNullOrWhiteSpace(line))
            return;

        var message = $"[hysteria/{stream}] {line.Trim()}";
        if (line.Contains("error", StringComparison.OrdinalIgnoreCase)
            || line.Contains("fatal", StringComparison.OrdinalIgnoreCase))
        {
            LogWrapper.Warn(ModuleName, message);
            return;
        }

        LogWrapper.Debug(ModuleName, message);
    }
}
