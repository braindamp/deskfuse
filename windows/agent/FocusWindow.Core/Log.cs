namespace FocusWindow.Core;
/// <summary>Minimal logging seam so Core stays dependency-free and testable.</summary>
public interface ILog
{
    void Info(string message);
    void Warn(string message);
    void Error(string message);
}
public sealed class NullLog : ILog
{
    public static readonly NullLog Instance = new();
    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message) { }
}
/// <summary>Console + optional file logger. Never throws: logging must not take the agent down.</summary>
public sealed class ConsoleLog : ILog, IDisposable
{
    private readonly StreamWriter? _file;
    private readonly object _gate = new();
    public ConsoleLog(string? path = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            _file = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            { AutoFlush = true };
        }
        catch
        {
            _file = null;
        }
    }
    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERR ", message);
    private void Write(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}";
        lock (_gate)
        {
            try
            {
                Console.WriteLine(line);
                _file?.WriteLine(line);
            }
            catch
            {
                // swallow: see class comment
            }
        }
    }
    public void Dispose() => _file?.Dispose();
}
