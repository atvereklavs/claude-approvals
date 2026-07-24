using Microsoft.Win32;

namespace ClaudeApprovals.App;

/// <summary>
/// Detects whether ANY app is currently using the microphone, via Windows'
/// CapabilityAccessManager consent store: while an app holds the mic, its
/// registry entry has LastUsedTimeStop == 0. Plain registry reads (both
/// packaged and NonPackaged trees), polled on a timer — no dependencies, no
/// permissions. Any failure reads as "not in use" (the feature can only ever
/// go quiet, never block approvals).
/// </summary>
public sealed class MicWatcher : IDisposable
{
    private const string Root =
        @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    private readonly System.Threading.Timer _timer;
    public bool InUse { get; private set; }
    public event Action<bool>? Changed;

    public MicWatcher(TimeSpan? interval = null)
    {
        var t = interval ?? TimeSpan.FromSeconds(5);
        _timer = new System.Threading.Timer(_ => Poll(), null, TimeSpan.Zero, t);
    }

    private void Poll()
    {
        var now = IsMicInUse();
        if (now == InUse) return;
        InUse = now;
        Changed?.Invoke(now);
    }

    public static bool IsMicInUse()
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(Root);
            if (root is null) return false;
            foreach (var appKeyName in root.GetSubKeyNames())
            {
                if (appKeyName == "NonPackaged")
                {
                    using var np = root.OpenSubKey(appKeyName);
                    if (np is null) continue;
                    foreach (var sub in np.GetSubKeyNames())
                    {
                        using var app = np.OpenSubKey(sub);
                        if (IsActive(app)) return true;
                    }
                }
                else
                {
                    using var app = root.OpenSubKey(appKeyName);
                    if (IsActive(app)) return true;
                }
            }
        }
        catch { /* read failure → not in use */ }
        return false;
    }

    private static bool IsActive(RegistryKey? app)
    {
        if (app is null) return false;
        // Started (has a start time) and not yet stopped (stop == 0).
        var stop = app.GetValue("LastUsedTimeStop");
        var start = app.GetValue("LastUsedTimeStart");
        return start is long s && s != 0 && stop is long e && e == 0;
    }

    public void Dispose() => _timer.Dispose();
}
