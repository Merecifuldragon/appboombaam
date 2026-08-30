using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using BFCrewSync.Models;
using BFCrewSync.Services;

namespace BFCrewSync;

public partial class MainWindow : Window
{
    private readonly SyncFileService _syncService = new();
    private readonly MemoryOptimizerService _optimizer = new();
    private readonly PerformanceMonitorService _perfMonitor = new();
    private readonly ScanListenerService _scanListener;

    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _perfTimer;
    private readonly DispatcherTimer _selfTrimTimer;
    private readonly DispatcherTimer _scanTimeoutTimer;

    private long? _activeTargetEpoch;
    private string _activeSyncId = "";
    private bool _triggerFired;
    private string? _pendingScanRequestId;

    public MainWindow()
    {
        InitializeComponent();

        // Precision clock + countdown: 30ms keeps the displayed .fff digits
        // smooth without meaningfully touching CPU.
        _clockTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(30)
        };
        _clockTimer.Tick += ClockTimer_Tick;
        _clockTimer.Start();

        // Perf overlay: 1s is plenty for a human-readable CPU/RAM readout.
        _perfTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _perfTimer.Tick += PerfTimer_Tick;
        _perfTimer.Start();

        // Passive self working-set trim every 2 minutes — keeps idle RAM low
        // without needing the user to hit "Optimize Now" themselves.
        _selfTrimTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
        {
            Interval = TimeSpan.FromMinutes(2)
        };
        _selfTrimTimer.Tick += (_, _) => _optimizer.TrimSelf();
        _selfTrimTimer.Start();

        // Crew-ID scan round trip: request -> executor listener responds ->
        // we read the response via FileSystemWatcher, not polling.
        _scanListener = new ScanListenerService(Dispatcher);
        _scanListener.ResultReceived += ScanListener_ResultReceived;
        _scanListener.ReadFailed += msg => { Log($"Scan read failed: {msg}"); ResetScanButton(); };

        _scanTimeoutTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _scanTimeoutTimer.Tick += ScanTimeout_Tick;

        Closed += (_, _) =>
        {
            _clockTimer.Stop();
            _perfTimer.Stop();
            _selfTrimTimer.Stop();
            _scanTimeoutTimer.Stop();
            _scanListener.Dispose();
            _perfMonitor.Dispose();
        };

        Log("Ready.");
    }

    // ================= Clock / countdown / trigger =================

    private void ClockTimer_Tick(object? sender, EventArgs e)
    {
        var nowIst = IstClockService.NowIst();
        IstClockText.Text = IstClockService.FormatPrecise(nowIst);

        if (_activeTargetEpoch is not long target)
        {
            CountdownText.Text = "--:--:--.---";
            return;
        }

        var remaining = IstClockService.CountdownTo(target);
        CountdownText.Text = IstClockService.FormatCountdown(remaining);

        if (!_triggerFired && remaining <= TimeSpan.Zero)
        {
            _triggerFired = true;
            FireExecutionTrigger();
        }
    }

    /// <summary>
    /// Fires the instant the target epoch is reached. The actual "Join Crew"
    /// action happens client-side in the registered Roblox routines, which
    /// are already polling bf_crew_sync.txt for this exact syncId/epoch —
    /// this just marks and logs the precise moment locally.
    /// </summary>
    private void FireExecutionTrigger()
    {
        var nowIst = IstClockService.NowIst();
        Log($"[{IstClockService.FormatPrecise(nowIst)}] TARGET REACHED — syncId {_activeSyncId} — join trigger fired.");
        CountdownText.Foreground = (Brush)FindResource("AccentGreen");
    }

    // ================= Perf overlay =================

    private void PerfTimer_Tick(object? sender, EventArgs e)
    {
        var sample = _perfMonitor.Sample();
        CpuText.Text = $"{sample.CpuPercent:0}%";
        RamText.Text = $"{sample.SystemRamPercent:0}%";
    }

    // ================= Root directory =================

    private void BrowseRoot_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose the root sync directory",
            Multiselect = false
        };

        if (dialog.ShowDialog() == true)
        {
            _syncService.RootDirectory = dialog.FolderName;
            RootDirBox.Text = dialog.FolderName;
            Log($"Root directory set: {dialog.FolderName}");
        }
    }

    // ================= Target resolution =================

    private bool TryResolveTarget(out long targetEpoch, out DateTime resolvedIst)
    {
        targetEpoch = 0;
        resolvedIst = default;

        var offsetText = OffsetMinutesBox.Text?.Trim();
        if (!string.IsNullOrEmpty(offsetText))
        {
            if (!int.TryParse(offsetText, out int minutes) || minutes <= 0)
            {
                Log("Invalid offset minutes — enter a positive whole number.");
                return false;
            }
            (targetEpoch, resolvedIst) = IstClockService.ResolveTargetFromOffset(minutes);
            return true;
        }

        var timeText = TargetTimeBox.Text?.Trim() ?? "";
        if (!IstClockService.TryResolveTargetEpoch(timeText, out targetEpoch, out resolvedIst))
        {
            Log($"Could not parse target time '{timeText}'. Use HH:mm:ss.");
            return false;
        }

        return true;
    }

    private bool ValidateCrewFields(out string owner, out string crewId, out string crewOwner)
    {
        owner = OwnerBox.Text?.Trim() ?? "";
        crewId = CrewIdBox.Text?.Trim() ?? "";
        crewOwner = CrewOwnerBox.Text?.Trim() ?? "";

        if (string.IsNullOrWhiteSpace(crewId))
        {
            Log("Crew ID is required.");
            return false;
        }
        return true;
    }

    // ================= Sync actions =================

    private void SetSync_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateCrewFields(out var owner, out var crewId, out var crewOwner)) return;
        if (!TryResolveTarget(out var targetEpoch, out var resolvedIst)) return;

        try
        {
            var payload = _syncService.WriteSetSync(owner, crewId, crewOwner, targetEpoch, LegacyFallbackCheck.IsChecked == true);

            _activeTargetEpoch = targetEpoch;
            _activeSyncId = payload.SyncId;
            _triggerFired = false;
            CountdownText.Foreground = (Brush)FindResource("TextPrimary");

            TargetInfoText.Text = $"Target: {IstClockService.FormatPrecise(resolvedIst)} IST · epoch {targetEpoch} · syncId {payload.SyncId}";
            Log($"SET SYNC written — crewId={crewId} target={IstClockService.FormatPrecise(resolvedIst)} IST syncId={payload.SyncId}");
        }
        catch (Exception ex)
        {
            Log($"SET SYNC failed: {ex.Message}");
        }
    }

    private void Register_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateCrewFields(out var owner, out var crewId, out var crewOwner)) return;
        if (!TryResolveTarget(out var targetEpoch, out var resolvedIst)) return;

        try
        {
            var payload = _syncService.WriteRegister(owner, crewId, crewOwner, targetEpoch, LegacyFallbackCheck.IsChecked == true);
            Log($"REGISTER written — crewId={crewId} target={IstClockService.FormatPrecise(resolvedIst)} IST syncId={payload.SyncId}");
        }
        catch (Exception ex)
        {
            Log($"REGISTER failed: {ex.Message}");
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        var owner = OwnerBox.Text?.Trim() ?? "";
        var crewId = CrewIdBox.Text?.Trim() ?? "";
        var crewOwner = CrewOwnerBox.Text?.Trim() ?? "";

        try
        {
            var payload = _syncService.WriteCancel(owner, crewId, crewOwner);
            _activeTargetEpoch = null;
            _triggerFired = false;
            CountdownText.Foreground = (Brush)FindResource("TextPrimary");
            TargetInfoText.Text = "Cancelled";
            Log($"CANCEL written — syncId={payload.SyncId}");
        }
        catch (Exception ex)
        {
            Log($"CANCEL failed: {ex.Message}");
        }
    }

    // ================= Crew ID scan =================

    private void ScanCrewId_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_syncService.RootDirectory))
        {
            Log("Set the root directory before scanning.");
            return;
        }

        try
        {
            _scanListener.Start(_syncService.RootDirectory);
            var payload = _syncService.WriteScanRequest();
            _pendingScanRequestId = payload.RequestId;

            ScanCrewIdButton.IsEnabled = false;
            ScanCrewIdButton.Content = "Scanning…";
            ScanStatusText.Text = "Waiting for the in-game scan listener to respond…";

            _scanTimeoutTimer.Stop();
            _scanTimeoutTimer.Start();

            Log($"Scan request written — requestId={payload.RequestId}.");
        }
        catch (Exception ex)
        {
            Log($"Scan request failed: {ex.Message}");
            ResetScanButton();
        }
    }

    private void ScanListener_ResultReceived(ScanResultPayload result)
    {
        // Ignore stale/duplicate responses from a previous request.
        if (_pendingScanRequestId == null || result.RequestId != _pendingScanRequestId)
            return;

        _scanTimeoutTimer.Stop();
        _pendingScanRequestId = null;

        if (result.Found && !string.IsNullOrWhiteSpace(result.CrewId))
        {
            CrewIdBox.Text = result.CrewId;
            if (!string.IsNullOrWhiteSpace(result.CrewOwner))
                CrewOwnerBox.Text = result.CrewOwner;

            ScanStatusText.Text = $"Found crew {result.CrewId}.";
            Log($"Scan result received — crewId={result.CrewId} crewOwner={result.CrewOwner}");
        }
        else
        {
            ScanStatusText.Text = "Executor reported no crew found.";
            Log("Scan result received — no crew found.");
        }

        ResetScanButton();
    }

    private void ScanTimeout_Tick(object? sender, EventArgs e)
    {
        _scanTimeoutTimer.Stop();
        if (_pendingScanRequestId == null) return; // already resolved

        _pendingScanRequestId = null;
        ScanStatusText.Text = "Timed out — is the in-game scan listener running?";
        Log("Scan timed out — no response within 10s.");
        ResetScanButton();
    }

    private void ResetScanButton()
    {
        ScanCrewIdButton.IsEnabled = true;
        ScanCrewIdButton.Content = "Scan Crew ID";
    }

    // ================= Optimizer =================

    private void OptimizeNow_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = _optimizer.OptimizeNow(LowerPriorityCheck.IsChecked == true);
            double freedMb = result.EstimatedBytesFreed / 1024.0 / 1024.0;
            OptimizerResultText.Text =
                $"Trimmed {result.ProcessesTrimmed} process(es), ~{freedMb:0.0} MB freed, in {result.Elapsed.TotalMilliseconds:0} ms.";
            Log(OptimizerResultText.Text);
        }
        catch (Exception ex)
        {
            Log($"Optimize failed: {ex.Message}");
        }
    }

    // ================= Log =================

    private void Log(string message)
    {
        var stamp = IstClockService.FormatPrecise(IstClockService.NowIst());
        LogList.Items.Insert(0, $"[{stamp}] {message}");
        while (LogList.Items.Count > 300)
            LogList.Items.RemoveAt(LogList.Items.Count - 1);
    }
}
