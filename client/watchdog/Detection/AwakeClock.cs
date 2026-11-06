using System.Runtime.Versioning;
using Vigil.Watchdog.Native;

namespace Vigil.Watchdog.Detection;

/// <summary>
/// Milliseconds the system has been awake since boot (sleep and hibernate excluded).
/// The server compares it across heartbeats to tell "PC was off/asleep" from "PC ran while the
/// watchdog was silent" — see server/app/tamper.py.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AwakeClock
{
    public static long NowMs => Win32.QueryUnbiasedInterruptTime(out var t) ? (long)(t / 10_000) : 0;
}
