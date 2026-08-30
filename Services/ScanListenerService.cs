using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using BFCrewSync.Models;

namespace BFCrewSync.Services;

/// <summary>
/// Desktop-side half of the scan round trip. Instead of polling on a timer,
/// this uses a FileSystemWatcher scoped to the one result filename — the
/// same event-driven "listener" pattern the Lua-side scripts already use
/// for file-based IPC, just running on the .NET side. Events are debounced
/// and reads are retried briefly, since the executor may still be mid-write
/// (sharing violation) the instant the OS fires the change notification.
/// </summary>
public class ScanListenerService : IDisposable
{
    public const string ResultFileName = "bf_crew_scan_result.txt";

    private readonly Dispatcher _dispatcher;
    private FileSystemWatcher? _watcher;
    private DateTime _lastEventUtc = DateTime.MinValue;

    public event Action<ScanResultPayload>? ResultReceived;
    public event Action<string>? ReadFailed;

    public ScanListenerService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    /// <summary>(Re)starts watching the given root directory for the result file.</summary>
    public void Start(string rootDirectory)
    {
        Stop();

        if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
            return;

        _watcher = new FileSystemWatcher(rootDirectory, ResultFileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.FileName,
        };
        _watcher.Changed += OnFsEvent;
        _watcher.Created += OnFsEvent;
        _watcher.Renamed += OnFsEvent;
        _watcher.EnableRaisingEvents = true;
    }

    public void Stop()
    {
        if (_watcher == null) return;
        _watcher.EnableRaisingEvents = false;
        _watcher.Changed -= OnFsEvent;
        _watcher.Created -= OnFsEvent;
        _watcher.Renamed -= OnFsEvent;
        _watcher.Dispose();
        _watcher = null;
    }

    private void OnFsEvent(object sender, FileSystemEventArgs e)
    {
        // A single logical write (e.g. tmp-file + atomic move/rename) can
        // fire more than one FS event — collapse anything within 150ms.
        var now = DateTime.UtcNow;
        if ((now - _lastEventUtc).TotalMilliseconds < 150) return;
        _lastEventUtc = now;

        _ = TryReadWithRetryAsync(e.FullPath);
    }

    private async Task TryReadWithRetryAsync(string path)
    {
        const int maxAttempts = 6;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await Task.Delay(60);
                if (!File.Exists(path)) continue;

                string json = await File.ReadAllTextAsync(path);
                if (string.IsNullOrWhiteSpace(json)) continue;

                var payload = JsonSerializer.Deserialize(json, ScanJsonContext.Default.ScanResultPayload);
                if (payload == null) continue;

                _dispatcher.Invoke(() => ResultReceived?.Invoke(payload));
                return;
            }
            catch (IOException)
            {
                // File is still being written by the executor — retry.
            }
            catch (JsonException ex)
            {
                _dispatcher.Invoke(() => ReadFailed?.Invoke($"Malformed scan result: {ex.Message}"));
                return;
            }
        }

        _dispatcher.Invoke(() => ReadFailed?.Invoke("Could not read scan result after several retries."));
    }

    public void Dispose() => Stop();
}
