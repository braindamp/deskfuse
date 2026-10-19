using System.Text;
using System.Windows.Forms;
using FocusWindow.Core.Session;
namespace FocusWindow.Agent;

/// <summary>
/// Win32 clipboard belongs to an STA. The agent's async socket/session threads are MTA,
/// so each deliberate transfer runs on a short-lived STA worker. It cannot block the
/// codec thread, requires no hidden message-window pump, and does no background polling.
/// </summary>
internal sealed class WindowsClipboardBridge : IClipboardBridge
{
    public Task<string?> ReadTextAsync(CancellationToken ct) => OnStaAsync(() =>
    {
        if (!Clipboard.ContainsText(TextDataFormat.UnicodeText)) return null;
        var value = Clipboard.GetText(TextDataFormat.UnicodeText);
        return Encoding.UTF8.GetByteCount(value) <= 64 * 1024 ? value : null;
    }, ct);

    public Task WriteTextAsync(string text, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (Encoding.UTF8.GetByteCount(text) > 64 * 1024)
            throw new ArgumentOutOfRangeException(nameof(text), "64 KiB clipboard limit");
        return OnStaAsync(() =>
        {
            Clipboard.SetText(text, TextDataFormat.UnicodeText);
            return true;
        }, ct);
    }

    private static Task<T> OnStaAsync<T>(Func<T> action, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                if (ct.IsCancellationRequested) { tcs.TrySetCanceled(ct); return; }
                try { tcs.TrySetResult(action()); return; }
                catch (System.Runtime.InteropServices.ExternalException) when (attempt < 3)
                {
                    Thread.Sleep(40); // another local application temporarily owns the clipboard
                }
                catch (Exception ex) { tcs.TrySetException(ex); return; }
            }
            tcs.TrySetException(new IOException("Clipboard did not become available."));
        }) { IsBackground = true, Name = "DeskFuse-clipboard-STA" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }
}
