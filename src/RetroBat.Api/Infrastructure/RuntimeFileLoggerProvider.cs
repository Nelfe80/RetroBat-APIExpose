using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// General file logger for APIExpose runtime diagnostics. APIExpose runs as a hidden
/// window (--hide-console), so Console output is lost; without this, every existing
/// _logger.LogInformation/Warning/Debug (marquee snapshot published, physical media
/// published, "Unable to publish…") vanishes - leaving black-marquee incidents (e.g.
/// after a media migration) impossible to diagnose. This provider captures ALL of
/// them into .log/apiexpose-runtime.log with no code change to the services.
///
/// Async + non-blocking (bounded channel, DropWrite on flood), single background
/// writer keeping one StreamWriter open, batch flush, size rotation (10 MiB × 3).
/// Adapted from MarqueeManager's SimpleFileLoggerProvider.
/// </summary>
public sealed class RuntimeFileLoggerProvider : ILoggerProvider
{
    private const long RotateBytes = 10L * 1024 * 1024;
    private const int KeepFiles = 3;

    private readonly string _path;
    private readonly LogLevel _minLevel;
    private readonly ConcurrentDictionary<string, RuntimeFileLogger> _loggers = new();
    private readonly Channel<string> _channel = Channel.CreateBounded<string>(
        new BoundedChannelOptions(16384)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite // a log flood never stalls APIExpose
        });
    private readonly Task _writer;

    public RuntimeFileLoggerProvider(string path, LogLevel minLevel, bool resetOnStartup)
    {
        _path = path;
        _minLevel = minLevel;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        if (resetOnStartup)
        {
            TryReset();
        }
        _writer = Task.Run(WriteLoopAsync);
    }

    public ILogger CreateLogger(string categoryName)
        => _loggers.GetOrAdd(categoryName, name => new RuntimeFileLogger(name, _minLevel, this));

    internal void Enqueue(string line) => _channel.Writer.TryWrite(line);

    /// <summary>Les sessions precedentes gardees au demarrage : « .session-1 » est la derniere.</summary>
    internal const int SessionsGardees = 3;

    /// <summary>Le journal d'une session precedente : 1 = la derniere avant celle-ci.</summary>
    public static string SessionPath(string path, int rang) => $"{path}.session-{rang}";

    /// <summary>
    /// LA SESSION QUI A PLANTE N'EST PLUS EFFACEE. Le demarrage vidait le journal : une API qui
    /// plante puis redemarre perdait la seule trace de ce qui s'etait passe (rapport de testeur, 2026-09-27 :
    /// 45 minutes sans API, rien pour dire pourquoi). Le journal de la session precedente passe en
    /// « .session-1 », les plus anciennes glissent, la plus vieille part. Les tranches de taille
    /// (« .1 », « .2 ») d'une session ne sont pas gardees : c'est la fin qui compte.
    /// </summary>
    private void TryReset()
    {
        try
        {
            var plusVieille = SessionPath(_path, SessionsGardees);
            if (File.Exists(plusVieille)) File.Delete(plusVieille);
            for (var i = SessionsGardees - 1; i >= 1; i--)
            {
                var de = SessionPath(_path, i);
                if (File.Exists(de)) File.Move(de, SessionPath(_path, i + 1), overwrite: true);
            }
            if (File.Exists(_path)) File.Move(_path, SessionPath(_path, 1), overwrite: true);
            for (var i = 1; i < KeepFiles; i++)
            {
                var rolled = $"{_path}.{i}";
                if (File.Exists(rolled)) File.Delete(rolled);
            }
        }
        catch { /* a reset failure just keeps the old content */ }
    }

    private async Task WriteLoopAsync()
    {
        StreamWriter? stream = null;
        long written = 0;
        try
        {
            (stream, written) = OpenStream();
            var reader = _channel.Reader;
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (reader.TryRead(out var line))
                {
                    if (written >= RotateBytes)
                    {
                        stream.Dispose();
                        Rotate();
                        (stream, written) = OpenStream();
                    }
                    stream.WriteLine(line);
                    written += line.Length + Environment.NewLine.Length;
                }
                await stream.FlushAsync().ConfigureAwait(false);
            }
        }
        catch { /* logging must never crash APIExpose */ }
        finally { stream?.Dispose(); }
    }

    private (StreamWriter Stream, long Bytes) OpenStream()
    {
        var info = new FileInfo(_path);
        var bytes = info.Exists ? info.Length : 0;
        var stream = new StreamWriter(new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read), Encoding.UTF8);
        return (stream, bytes);
    }

    private void Rotate()
    {
        try
        {
            var oldest = $"{_path}.{KeepFiles - 1}";
            if (File.Exists(oldest)) File.Delete(oldest);
            for (var i = KeepFiles - 2; i >= 1; i--)
            {
                var from = $"{_path}.{i}";
                if (File.Exists(from)) File.Move(from, $"{_path}.{i + 1}", overwrite: true);
            }
            if (File.Exists(_path)) File.Move(_path, $"{_path}.1", overwrite: true);
        }
        catch { /* keep appending to the current file on rotation failure */ }
    }

    public void Dispose()
    {
        _channel.Writer.TryComplete();
        try { _writer.Wait(TimeSpan.FromSeconds(3)); } catch { /* drain best effort */ }
        _loggers.Clear();
    }
}

public sealed class RuntimeFileLogger : ILogger
{
    private readonly string _categoryName;
    private readonly LogLevel _minLevel;
    private readonly RuntimeFileLoggerProvider _provider;

    public RuntimeFileLogger(string categoryName, LogLevel minLevel, RuntimeFileLoggerProvider provider)
    {
        _categoryName = categoryName;
        _minLevel = minLevel;
        _provider = provider;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= _minLevel;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var message = formatter(state, exception);
        var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{logLevel}] [{_categoryName}] {message}";
        if (exception != null)
        {
            entry += Environment.NewLine + exception;
        }
        _provider.Enqueue(entry);
    }
}
