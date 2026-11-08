using System;

namespace Vigil.Ui;

/// <summary>
/// Stan UI lockscreen, mirroring serwerowego ui_state z heartbeat response.
/// Lockscreen otrzymuje to przez named pipe od watchdoga (IpcSetState). The message is worded here from
/// Reason (Strings.LockMessage, i18n D21).
/// </summary>
public sealed record UiState
{
    public required LockReason Reason { get; init; }
    public bool ShowTotpField { get; init; }
    public bool ShowRetryButton { get; init; }
    public bool ShowResumeButton { get; init; }   // "Jestem z powrotem" — tylko w trybie Pause
    public bool ShowShutdownButton { get; init; } = true;
    public int MinutesRemaining { get; init; }
}

public enum LockReason
{
    Limit,
    Night,
    Manual,
    Pause,             // dziecko samo włączyło pauzę — czas nie leci
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
