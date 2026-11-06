// vigil_tray.exe — ikona w zasobniku systemowym do pauzy czasu dziecka.
// Uruchamiany przez Scheduled Task "Vigil Tray" (AtLogon, konto dziecka).
// Komunikuje się z watchdogiem przez named pipe \\.\pipe\VigilTray.

const string MutexName = "Vigil_Tray_v1";
using var mutex = new Mutex(true, MutexName, out bool isFirst);
if (!isFirst) return;  // Już działa — nie otwieraj drugiego

Application.EnableVisualStyles();
Application.SetCompatibleTextRenderingDefault(false);

using var cts = new CancellationTokenSource();

// Zamknij przy zamknięciu aplikacji
Application.ApplicationExit += (_, _) => cts.Cancel();

using var app = new TrayApp(cts);
Application.Run();
