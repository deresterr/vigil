using System;

namespace Vigil.Ui;

/// <summary>
/// The lock screen's state, mirroring the server's ui_state; it arrives from the watchdog over the pipe
/// (IpcSetState). The texts are worded here from Reason (Strings.LockTitle/LockDetail, i18n D21).
/// </summary>
public sealed record UiState
{
    public required LockReason Reason { get; init; }
    public bool ShowTotpField { get; init; }
    public bool ShowRetryButton { get; init; }
    public bool ShowResumeButton { get; init; }   // "Back to the computer", pause only
    public int MinutesRemaining { get; init; }
    /// <summary>Night lock: when the night ends, local "HH:mm"; null otherwise.</summary>
    public string? NightEndLocal { get; init; }
}

public enum LockReason
{
    Limit,
    Night,
    Manual,
    Pause,             // the child's own pause: time is not counted
    ServerUnreachable, // the watchdog's own fail-closed lock
    DeviceUnpaired,    // the same, but the server refused this computer's key (removed or re-paired in the panel)
}

public static class LockReasonExtensions
{
    public static LockReason FromWireFormat(string s) => s switch
    {
        "limit"              => LockReason.Limit,
        "night"              => LockReason.Night,
        "manual"             => LockReason.Manual,
        "pause"              => LockReason.Pause,
        "server_unreachable" => LockReason.ServerUnreachable,
        "device_unpaired"    => LockReason.DeviceUnpaired,
        _ => throw new ArgumentOutOfRangeException(nameof(s), $"Unknown lock reason: {s}"),
    };
}
