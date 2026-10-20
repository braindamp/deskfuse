using System.Runtime.InteropServices;
using System.Windows.Forms;
using FocusWindow.Core.Display;

namespace FocusWindow.Agent.Media;

/// <summary>
/// Host-owned PMv2 window on the selected *virtual* monitor. Does not move,
/// resize, or focus any physical-desktop application window.
///
/// Structural integration stage: this is an empty owned workspace source,
/// NOT yet an independent clone of arbitrary native Windows applications.
/// </summary>
internal sealed class WorkspaceWindowHost : IAsyncDisposable
{
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    private Form? _form;
    private Thread? _thread;

    public IntPtr Handle => _form?.Handle ?? IntPtr.Zero;
    public RectI Bounds { get; private set; } = new(0, 0, 0, 0);
    public int Dpi { get; private set; }

    public async Task StartAsync(RectI monitor, int width, int height, int requiredDpi, CancellationToken ct)
    {
        if (_thread is not null) throw new InvalidOperationException("Workspace source already started");
        if (monitor.Width < width || monitor.Height < height)
            throw new InvalidOperationException("Virtual backing monitor is smaller than the requested viewport");
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() =>
        {
            try
            {
                using var form = new Form
                {
                    FormBorderStyle = FormBorderStyle.None,
                    StartPosition = FormStartPosition.Manual,
                    ShowInTaskbar = true, // WGC must see a real top-level HWND
                    TopMost = false,
                    Location = new System.Drawing.Point(monitor.X + (monitor.Width - width) / 2, monitor.Y),
                    ClientSize = new System.Drawing.Size(width, height),
                    Text = "DeskFuse Workspace",
                    BackColor = System.Drawing.Color.FromArgb(20, 20, 25),
                };
                _form = form;
                form.Shown += (_, _) =>
                {
                    try
                    {
                        Dpi = checked((int)GetDpiForWindow(form.Handle));
                        Bounds = new RectI(form.Left, form.Top, form.ClientSize.Width, form.ClientSize.Height);
                        if (Dpi != requiredDpi || form.ClientSize.Width != width ||
                            form.ClientSize.Height != height)
                        {
                            throw new InvalidOperationException(
                                $"Native phone viewport mismatch: {Dpi} DPI, {form.ClientSize}");
                        }
                        ready.TrySetResult(true);
                    }
                    catch (Exception ex) { ready.TrySetException(ex); form.Close(); }
                };
                Application.Run(form);
            }
            catch (Exception ex) { ready.TrySetException(ex); }
            finally { _form = null; }
        }) { IsBackground = true, Name = "DeskFuse Workspace Window" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        using var cancellation = ct.Register(() => ready.TrySetCanceled(ct));
        await ready.Task.ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        var form = _form;
        if (form is not null && !form.IsDisposed)
        {
            try { form.BeginInvoke((Action)(() => form.Close())); }
            catch (InvalidOperationException) { }
        }
        // Never block the UI/session teardown indefinitely on a hung window.
        if (_thread is not null && _thread.IsAlive)
            _thread.Join(TimeSpan.FromSeconds(2));
        _thread = null;
        return ValueTask.CompletedTask;
    }
}
