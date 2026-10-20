namespace FocusWindow.Agent.Media;

/// <summary>
/// Reconnects within this host reuse the same enrolled device's explicitly
/// launched application. Avoid spawning another app/process on every Wi-Fi
/// interruption, and preserve documents. Unknown pre-existing windows cannot
/// enter this catalog. Source attachments are exclusive, even on future backends.
/// </summary>
internal sealed class WorkspaceApplicationCatalog : IDisposable
{
    private readonly Dictionary<string, WorkspaceApplicationSource> _sources = new(StringComparer.Ordinal);
    public WorkspaceApplicationSource Get(string enrollmentId, string executable, string? arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(enrollmentId);
        lock (_sources)
        {
            if (_sources.TryGetValue(enrollmentId, out var source)) return source;
            source = new WorkspaceApplicationSource(executable, arguments);
            _sources.Add(enrollmentId, source);
            return source;
        }
    }
    public void Dispose()
    {
        // SessionHost has already joined every GPU worker at this point.
        // Release process handles; never kill apps with unsaved documents.
        lock (_sources)
        {
            foreach (var source in _sources.Values) source.DisposeAsync().GetAwaiter().GetResult();
            _sources.Clear();
        }
    }
}
