using FocusWindow.Core.Display;
using FocusWindow.Core.Media;
using FocusWindow.Core.Transport;
namespace FocusWindow.Core.Session;
public sealed record HostOptions
{
    public int Port { get; init; } = 47810;
    public string Bind { get; init; } = "0.0.0.0";
    public int Fps { get; init; } = 60;
    public int BitrateKbps { get; init; } = 20_000;
    public int MaxRefreshHz { get; init; } = 120;
    public string FfmpegPath { get; init; } = "ffmpeg";
    public string HostName { get; init; } = Environment.MachineName;
    public string HostVersion { get; init; } = "0.3.0-dev";
    public string Channel { get; init; } = "dev";
    public bool DrawRulerProbe { get; init; }
    public bool NewPairing { get; init; }
    // --auto-approve must never imply full filesystem access.
    public bool AllowFullUserFiles { get; init; } = true;
    public bool SingleSession { get; init; }
}
/// <summary>Decides whether a phone that just authenticated may use this PC (default: ask the user).</summary>
public interface IApprovalPolicy
{
    Task<bool> ApproveAsync(string deviceId, string deviceLabel, string verificationCode, CancellationToken ct);
}
public sealed class AutoApprove : IApprovalPolicy
{
    public Task<bool> ApproveAsync(string deviceId, string deviceLabel, string verificationCode, CancellationToken ct) =>
        Task.FromResult(true);
}
/// <summary>Explicit, text-only clipboard operations; no background clipboard monitoring.</summary>
public interface IClipboardBridge
{
    Task<string?> ReadTextAsync(CancellationToken ct);
    Task WriteTextAsync(string text, CancellationToken ct);
}
public sealed record WindowItem(string Id, string Title, string AppLabel);
/// <summary>Explicit window picker: list and focus only; never move/resize physical PC windows.</summary>
public interface IWindowWorkflow
{
    IReadOnlyList<WindowItem> ListVisible();
    bool Activate(string id);
}
public sealed record HostServices(
    Func<IDisplayBackend> CreateBackend,
    Func<IInputSink> CreateInput,
    Func<IPhysicalDisplayGuard> CreateGuard,
    IApprovalPolicy Approval,
    IPairingStore PairingStore,
    Action<NegotiationOutcome>? OnNegotiated = null,
    IClipboardBridge? Clipboard = null,
    IWindowWorkflow? Windows = null);
