using System;

namespace Vigil.Ui;

/// <summary>
/// The lock screen's state, mirroring the server's ui_state; it arrives from the watchdog over the pipe
/// (IpcSetState). The message is worded here from Reason (Strings.LockMessage, i18n D21).
/// </summary>
public sealed record UiState
{
    public required LockReason Reason { get; init; }
    public bool ShowTotpField { get; init; }
    public bool ShowRetryButton { get; init; }
    public bool ShowResumeButton { get; init; }   // "I'm back", pause only
    public bool ShowShutdownButton { get; init; } = true;
    public int MinutesRemaining { get; init; }
}

public enum LockReason
{
    Limit,
    Night,
    Manual,
    Pause,             // the child's own pause: time is not counted
    ServerUnreachable,
}

public static class LockReasonExtensions
{
    public static string ToWireFormat(this LockReason r) => r switch
    {
        LockReason.Limit             => "limit",
        LockReason.Night             => "night",
        LockReason.Manual            => "manual",
        LockReason.Pause             => "pause",
        LockReason.ServerUnreachable => "server_unreachable",
        _ => throw new ArgumentOutOfRangeException(nameof(r)),
    };

    public static LockReason FromWireFormat(string s) => s switch
    {
        "limit"             => LockReason.Limit,
        "night"             => LockReason.Night,
        "manual"            => LockReason.Manual,
        "pause"             => LockReason.Pause,
        "server_unreachable"=> LockReason.ServerUnreachable,
        _ => throw new ArgumentOutOfRangeException(nameof(s), $"Unknown lock reason: {s}"),
    };
}
