using System.IO;

namespace VxProxy;

/// <summary>
/// Watches the ProTee Labs shots directory and fires when a new shot
/// subdirectory appears. The shot's <c>ShotData.json</c> may not be written
/// yet when the event fires — callers should poll until the file is parseable.
/// </summary>
public class ShotFolderWatcher : IDisposable
{
    public static string DefaultShotsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ProTeeUnited", "Shots");

    private FileSystemWatcher? _watcher;
    private readonly string _shotsDirectory;
    private bool _disposed;

    public string ShotsDirectory => _shotsDirectory;
    public bool IsRunning { get; private set; }

    public event Action<string>? ShotDetected;   // path to new shot directory
    public event Action<string>? Log;
    public event Action<string>? Error;

    public ShotFolderWatcher(string shotsDirectory)
    {
        _shotsDirectory = shotsDirectory;
    }

    public bool Start()
    {
        if (IsRunning || _disposed) return IsRunning;

        if (!Directory.Exists(_shotsDirectory))
        {
            Error?.Invoke($"Shots directory not found: {_shotsDirectory}");
            return false;
        }

        _watcher = new FileSystemWatcher(_shotsDirectory)
        {
            NotifyFilter = NotifyFilters.DirectoryName,
            IncludeSubdirectories = false,
            InternalBufferSize = 16384,
            EnableRaisingEvents = true,
        };

        _watcher.Created += OnDirectoryCreated;
        _watcher.Error += OnWatcherError;

        IsRunning = true;
        Log?.Invoke($"Watching {_shotsDirectory} for new shots.");
        return true;
    }

    public void Stop()
    {
        if (!IsRunning) return;

        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Created -= OnDirectoryCreated;
            _watcher.Error -= OnWatcherError;
            _watcher.Dispose();
            _watcher = null;
        }

        IsRunning = false;
        Log?.Invoke("Folder watcher stopped.");
    }

    private void OnDirectoryCreated(object sender, FileSystemEventArgs e)
    {
        ShotDetected?.Invoke(e.FullPath);
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        var ex = e.GetException();
        Error?.Invoke($"Watcher error: {ex.Message}");

        // Self-heal after a brief delay.
        Task.Run(async () =>
        {
            await Task.Delay(2000);
            if (_disposed) return;
            try { Stop(); Start(); }
            catch (Exception restartEx) { Error?.Invoke($"Watcher restart failed: {restartEx.Message}"); }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
    }
}
