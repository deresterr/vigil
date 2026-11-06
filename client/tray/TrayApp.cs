/// <summary>
/// Ikonka pauzy w zasobniku systemowym.
///
/// Zachowanie:
///   Lewy klik         — włącz / wyłącz pauzę (toggle)
///   Najechanie myszką — tooltip "Pozostało: X min" (aktualizowany przez watchdoga)
///
/// Ikona jest statyczna — przy aktywnej pauzie i tak jest niewidoczna pod ekranem blokady.
/// Brak menu kontekstowego — jeden przycisk, jedno działanie.
/// </summary>
public sealed class TrayApp : IDisposable
{
    private readonly NotifyIcon _tray;
    private readonly PipeClient _pipe;
    private readonly CancellationTokenSource _cts;
    private readonly Icon _icon;


    public TrayApp(CancellationTokenSource cts)
    {
        _cts = cts;
        _icon = IconPainter.TrayIcon();

        _tray = new NotifyIcon
        {
            Icon = _icon,
            Text = "Ładowanie...",
            Visible = true,
        };

        // Inicjalizuj pipe przed rejestracją handlera — kompilator wymaga by _pipe
        // był przypisany zanim lambda go użyje (CS8602).
        _pipe = new PipeClient();
        _pipe.StateReceived += OnStateReceived;
        // Make a dead connection visible instead of showing a stale time.
        _pipe.Disconnected += () => _tray.Text = "Vigil: brak połączenia";

        // Lewy klik = toggle pauzy. Prawy klik nic nie robi (brak menu).
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                _pipe.SendPauseToggle();
        };

        TrayLog.Write("tray started");
        Task.Run(() => _pipe.RunAsync(_cts.Token), _cts.Token);
    }

    private void OnStateReceived(TrayStateUpdate update)
    {
        // Tooltip = ile zostało minut. Widoczne gdy dziecko najedzie na ikonę.
        var tip = update.MinutesRemaining > 0
            ? $"Pozostało: {FormatTime(update.MinutesRemaining)}"
            : "Komputer aktywny";

        // NotifyIcon.Text jest thread-safe — Shell_NotifyIconW można wołać z dowolnego wątku
        _tray.Text = tip;
    }

    private static string FormatTime(int minutes)
    {
        if (minutes < 60) return $"{minutes} min";
        int h = minutes / 60, m = minutes % 60;
        return m > 0 ? $"{h} h {m} min" : $"{h} h";
    }

    public void Dispose()
    {
        _tray.Visible = false;
        _tray.Dispose();
        _pipe.Dispose();
        _icon.Dispose();
    }
}
