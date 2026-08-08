namespace Hysteria2Link.Plugin.Models;

internal enum SessionRole
{
    None,
    Host,
    Guest
}

internal enum SessionPhase
{
    Stopped,
    Preparing,
    Running,
    Stopping,
    Error
}

internal sealed record SessionSnapshot(
    SessionRole Role,
    SessionPhase Phase,
    string Message,
    string? Code = null,
    string? RealmName = null,
    int? HostPort = null,
    int? LocalPort = null,
    string? Description = null)
{
    public static SessionSnapshot Initial { get; } = new(
        SessionRole.None,
        SessionPhase.Stopped,
        "尚未建立联机连接。");
}
