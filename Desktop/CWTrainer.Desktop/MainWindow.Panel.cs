using System.IO.Ports;
using CWTrainer.Desktop.Qso;

namespace CWTrainer.Desktop;

public partial class MainWindow
{
    private const int PanelIntervalMs = 50;
    private const int PanelRefreshMs = 400;
    private const int PanelSnapshotMaxAgeMs = 250;
    private const int PanelRxHoldMs = 200;

    private sealed record PanelSnapshot(long TimeMs, long FrequencyHz, EmissionMode Mode, int Dbm, bool Rx);

    private volatile bool _panelSupported;
    private volatile SerialPort? _panelPort;
    private volatile PanelSnapshot? _panelSnapshot;
    private System.Threading.Timer? _panelTimer;

    private void UpdatePanelSnapshot()
    {
        if (_panelPort == null || _linkLost || _qso == null || _qsoAudio == null)
            return;

        long now = Environment.TickCount64;
        bool rx = !_qso.IsTransmitting && _qsoAudio.HasRecentStationSignal(PanelRxHoldMs);
        int dbm = (int)Math.Clamp(Math.Round(_qso.SMeterDbm), -127, -13);
        _panelSnapshot = new(now, _qso.FrequencyHz, _qso.Mode, dbm, rx);
    }

    private void StartPanelUpdates()
    {
        if (!_panelSupported || _port is not { } port)
            return;

        _panelPort = port;
        string lastCommand = "PANEL,OFF";
        long lastSentMs = 0;
        _panelTimer = new System.Threading.Timer(_ =>
        {
            // A panel frame is expendable. Let control commands take precedence
            // and always read the latest snapshot instead of queuing stale frames.
            if (!Monitor.TryEnter(_writeLock))
                return;

            try
            {
                if (_panelPort != port || _linkLost || !port.IsOpen)
                    return;

                long now = Environment.TickCount64;
                PanelSnapshot? snapshot = _panelSnapshot;
                string command = snapshot == null || now - snapshot.TimeMs > PanelSnapshotMaxAgeMs
                    ? "PANEL,OFF"
                    : FormattableString.Invariant(
                        $"PANEL,QSO,{snapshot.FrequencyHz},{snapshot.Mode.ToString().ToUpperInvariant()},{snapshot.Dbm},{(snapshot.Rx ? 1 : 0)}");

                if (command == lastCommand && (command == "PANEL,OFF" || now - lastSentMs < PanelRefreshMs))
                    return;

                port.Write(command + "\n");
                lastCommand = command;
                lastSentMs = now;
            }
            catch (Exception ex) when (ex is System.IO.IOException or InvalidOperationException or TimeoutException)
            {
                // The regular heartbeat owns link failure reporting.
            }
            finally
            {
                Monitor.Exit(_writeLock);
            }
        }, null, PanelIntervalMs, PanelIntervalMs);
    }

    private void StopPanelUpdates()
    {
        _panelPort = null;
        _panelTimer?.Dispose();
        _panelTimer = null;
        _panelSnapshot = null;
    }
}
