using System.Runtime.InteropServices;
namespace DeskFuse.AudioBroker;
// Optional local diagnostics only. Never changes control results or throws.
internal static class StartupTrace
{
    private static readonly Guid Provider = new("e47848a1-9465-4513-9851-7e2deab15620");
    internal static void Write(string text)
    {
        try {
            if (text.Length > 1024) text = text[..1024];
            var id = Provider;
            if (EventRegister(ref id, IntPtr.Zero, IntPtr.Zero, out var handle) != 0) return;
            try { _ = EventWriteString(handle, 4, 1, text); }
            finally { _ = EventUnregister(handle); }
        } catch { /* Diagnostic failure cannot change ownership or retirement. */ }
    }
    [DllImport("advapi32.dll")] private static extern uint EventRegister(ref Guid id, IntPtr callback, IntPtr context, out ulong handle);
    [DllImport("advapi32.dll", CharSet=CharSet.Unicode)] private static extern uint EventWriteString(ulong handle, byte level, ulong keyword, string text);
    [DllImport("advapi32.dll")] private static extern uint EventUnregister(ulong handle);
}
