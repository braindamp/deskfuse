using System.Security.Cryptography;

namespace FocusWindow.Core.Session;

/// <summary>
/// Authenticated session's window-source registry. The wire sees only random IDs,
/// never HWND values; a source is resolvable only after a fresh catalog response.
/// The registry is owned by one PhoneSession, not shared between enrolled phones.
/// </summary>
internal sealed class WindowSourceCatalog(IWindowWorkflow workflow)
{
    private sealed record Source(string LogicalId, string InstanceKey, WindowItem Current);
    private Dictionary<string, Source> _byInstance = new(StringComparer.Ordinal);
    private Dictionary<string, Source> _byLogicalId = new(StringComparer.Ordinal);

    public IReadOnlyList<WindowItem> List()
    {
        // Rebuilding from current top-level windows both updates titles and revokes
        // cards whose HWND has disappeared. Reappearance creates a new logical ID.
        var next = new Dictionary<string, Source>(StringComparer.Ordinal);
        var publicList = new List<WindowItem>();
        foreach (var window in workflow.ListVisible().Take(100))
        {
            if (string.IsNullOrWhiteSpace(window.Id) ||
                string.IsNullOrWhiteSpace(window.Title) ||
                string.IsNullOrWhiteSpace(window.AppLabel))
                continue;

            // InstanceKey is host-internal (HWND + process/thread lifetime).
            // Legacy workflows only provide a handle; those do not promise
            // protection against HANDLE reuse with identical metadata.
            var key = window.InstanceKey ?? ("legacy:" + window.Id);
            if (next.ContainsKey(key)) continue;
            var id = _byInstance.TryGetValue(key, out var existing)
                ? existing.LogicalId
                : Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            var source = new Source(id, key, window);
            next.Add(key, source);
            publicList.Add(new WindowItem(id, window.Title, window.AppLabel, SessionStable: true));
        }
        _byInstance = next;
        _byLogicalId = next.Values.ToDictionary(s => s.LogicalId, StringComparer.Ordinal);
        return publicList;
    }

    /// <summary>
    /// Never focus an unlisted, closed or replaced source. A fresh live enumeration
    /// is required even if the phone presents a previously valid logical ID.
    /// </summary>
    public bool Activate(string logicalId)
    {
        if (string.IsNullOrWhiteSpace(logicalId) || !_byLogicalId.ContainsKey(logicalId))
            return false;
        List();
        return _byLogicalId.TryGetValue(logicalId, out var source) &&
               workflow.Activate(source.Current.Id);
    }
}
