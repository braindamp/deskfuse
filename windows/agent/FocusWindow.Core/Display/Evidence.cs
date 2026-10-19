using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace FocusWindow.Core.Display;
public enum EvidenceStatus { Pass, Fail, Warn, Info, Skip }
public sealed record EvidenceItem(
    string Id,
    string Title,
    EvidenceStatus Status,
    string? Expected,
    string? Actual,
    string? Note);
/// <summary>
/// The negotiation never says "density verified" in prose. It produces this list: each claim
/// with the value expected, the value measured, and a verdict. The same list is shown on the
/// phone's diagnostics, written to disk by the density-probe command, and used by tests.
/// </summary>
public sealed class EvidenceLog
{
    private readonly List<EvidenceItem> _items = new();
    public IReadOnlyList<EvidenceItem> Items => _items;
    public bool HasFailure => _items.Any(i => i.Status == EvidenceStatus.Fail);
    public EvidenceItem? Find(string id) => _items.FirstOrDefault(i => i.Id == id);
    public void Add(string id, string title, EvidenceStatus status, string? expected = null, string? actual = null, string? note = null) =>
        _items.Add(new EvidenceItem(id, title, status, expected, actual, note));
    public void Pass(string id, string title, string? expected = null, string? actual = null, string? note = null) =>
        Add(id, title, EvidenceStatus.Pass, expected, actual, note);
    public void Fail(string id, string title, string? expected, string? actual, string? note = null) =>
        Add(id, title, EvidenceStatus.Fail, expected, actual, note);
    public void Warn(string id, string title, string? expected = null, string? actual = null, string? note = null) =>
        Add(id, title, EvidenceStatus.Warn, expected, actual, note);
    public void Info(string id, string title, string? actual = null, string? note = null) =>
        Add(id, title, EvidenceStatus.Info, null, actual, note);
    public void Skip(string id, string title, string note) =>
        Add(id, title, EvidenceStatus.Skip, null, null, note);
    public string ToMarkdown(string heading)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {heading}").AppendLine();
        sb.AppendLine("| Status | Check | Expected | Actual | Note |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var i in _items)
        {
            static string Cell(string? s) => (s ?? "").Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
            sb.AppendLine($"| {i.Status.ToString().ToUpperInvariant()} | {Cell(i.Title)} | {Cell(i.Expected)} | {Cell(i.Actual)} | {Cell(i.Note)} |");
        }
        return sb.ToString();
    }
    public JsonArray ToJson()
    {
        var arr = new JsonArray();
        foreach (var i in _items)
        {
            arr.Add(new JsonObject
            {
                ["id"] = i.Id,
                ["title"] = i.Title,
                ["status"] = i.Status.ToString().ToLowerInvariant(),
                ["expected"] = i.Expected,
                ["actual"] = i.Actual,
                ["note"] = i.Note,
            });
        }
        return arr;
    }
    public string ToJsonString() => ToJson().ToJsonString(new JsonSerializerOptions { WriteIndented = true });
}
