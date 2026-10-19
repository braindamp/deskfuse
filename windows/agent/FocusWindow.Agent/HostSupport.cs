using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Forms;
using FocusWindow.Core;
using FocusWindow.Core.Session;
using FocusWindow.Core.Transport;
using QRCoder;
namespace FocusWindow.Agent;
internal static class AppData
{
    public static string Directory
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FocusWindow");
            System.IO.Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
/// <summary>
/// The pairing key + salt, protected with DPAPI (current user). Another Windows user on the same PC, or
/// someone who copies the file to another machine, cannot read it. The phone's own copy is separate.
/// </summary>
internal sealed class DpapiPairingStore : IPairingStore
{
    private readonly string _path = Path.Combine(AppData.Directory, "pairing.bin");
    public PairingState? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var json = ProtectedData.Unprotect(File.ReadAllBytes(_path), null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<PairingState>(json);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException)
        {
            return null; // unreadable pairing = make a new one; the user rescans once
        }
    }
    public void Save(PairingState state)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(state);
        File.WriteAllBytes(_path, ProtectedData.Protect(json, null, DataProtectionScope.CurrentUser));
    }
}
/// <summary>
/// Authorisation by a HUMAN. Holding the QR secret only gets a phone to this prompt. A phone the owner
/// approved before is remembered by its device id; a new one is shown with the 6-digit code that must
/// match the phone's screen (this comparison is what defeats an active attacker on the LAN).
/// Default is DENY after 60 s.
/// </summary>
internal sealed class ConsoleApproval : IApprovalPolicy, IEnrollmentApprovalPolicy
{
    private readonly string _path = Path.Combine(AppData.Directory, "approved-devices.json");
    // Legacy device approval never implicitly grants the new full-user file scope.
    private readonly string _fullFilesPath = Path.Combine(AppData.Directory, "approved-full-files.json");
    private readonly bool _autoApprove;
    private readonly ILog _log;
    public ConsoleApproval(bool autoApprove, ILog log)
    {
        _autoApprove = autoApprove;
        _log = log;
    }
    private HashSet<string> Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(path)) ?? new() : new(); }
        catch (Exception ex) when (ex is JsonException or IOException) { return new(); }
    }

    /// <summary>
    /// Explicit WINDOWS-side enrollment confirmation: a one-time consent for this
    /// device's desktop and full-user file scope. Never bypassed by --auto-approve,
    /// by an old device label/ID, or by a previous QR approval.
    /// </summary>
    public async Task<bool> ApproveNewPairingAsync(string deviceId, string deviceLabel,
        string verificationCode, CancellationToken ct)
    {
        if (!Environment.UserInteractive) return false;
        var granted = await ShowEnrollmentWindowAsync(deviceLabel, verificationCode, ct)
            .ConfigureAwait(false);
        if (granted)
        {
            var approved = Load(_path);
            approved.Add(deviceId);
            var fullFiles = Load(_fullFilesPath);
            fullFiles.Add(deviceId);
            File.WriteAllText(_path, JsonSerializer.Serialize(approved));
            File.WriteAllText(_fullFilesPath, JsonSerializer.Serialize(fullFiles));
        }
        return granted;
    }

    private static async Task<bool> ShowEnrollmentWindowAsync(
        string deviceLabel, string code, CancellationToken ct)
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var window = new Form
                {
                    Text = "DeskFuse — Allow this phone?",
                    Size = new System.Drawing.Size(460, 275),
                    StartPosition = FormStartPosition.CenterScreen,
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    MaximizeBox = false, MinimizeBox = false,
                    ShowInTaskbar = true, TopMost = true
                };
                var safeLabel = new string(deviceLabel.Where(c => !char.IsControl(c)).Take(80).ToArray());
                var message = new Label
                {
                    Text = $"Allow {safeLabel} to control this PC?\\n\\n" +
                        $"Check that both devices show: {code}\\n\\n" +
                        "Approval includes desktop control and access to your Windows files.\\n" +
                        "Only accept if the code on your phone matches.",
                    Location = new System.Drawing.Point(18, 17),
                    Size = new System.Drawing.Size(415, 157),
                    AutoSize = false
                };
                var allow = new Button
                {
                    Text = "Allow this phone",
                    Location = new System.Drawing.Point(238, 184),
                    Size = new System.Drawing.Size(175, 35),
                    DialogResult = DialogResult.OK
                };
                var deny = new Button
                {
                    Text = "Deny",
                    Location = new System.Drawing.Point(140, 184),
                    Size = new System.Drawing.Size(90, 35),
                    DialogResult = DialogResult.Cancel
                };
                window.Controls.Add(message);
                window.Controls.Add(allow);
                window.Controls.Add(deny);
                window.AcceptButton = allow;
                window.CancelButton = deny;
                using var timeout = new System.Windows.Forms.Timer { Interval = 60_000 };
                timeout.Tick += (_, _) => { timeout.Stop(); window.DialogResult = DialogResult.Cancel; window.Close(); };
                window.Shown += (_, _) => timeout.Start();
                using var cancellation = ct.Register(() =>
                {
                    try { if (window.IsHandleCreated && !window.IsDisposed)
                        window.BeginInvoke(new Action(() => window.Close())); } catch (InvalidOperationException) { }
                });
                result.TrySetResult(window.ShowDialog() == DialogResult.OK && !ct.IsCancellationRequested);
            }
            catch (Exception ex) { result.TrySetException(ex); }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return await result.Task.ConfigureAwait(false);
    }

    public async Task<bool> ApproveAsync(string deviceId, string deviceLabel, string verificationCode, CancellationToken ct)
    {
        var approved = Load(_path);
        var fullFiles = Load(_fullFilesPath);
        if (!_autoApprove && approved.Contains(deviceId) && fullFiles.Contains(deviceId))
        {
            _log.Info($"'{deviceLabel}' has previously approved full-user file scope; connecting");
            return true;
        }
        if (_autoApprove)
        {
            _log.Warn($"--auto-approve: letting '{deviceLabel}' in WITHOUT asking. Anyone holding the QR can use this PC.");
            return true;
        }
        Console.WriteLine();
        Console.WriteLine($"  A phone wants to use this PC:  {deviceLabel}");
        Console.WriteLine($"  Confirm this code matches the phone's screen:   {verificationCode}");
        Console.WriteLine("  This grants DESKTOP CONTROL and FULL FILE ACCESS as your signed-in Windows user.");
        Console.WriteLine("  The paired phone can read, upload and download accessible files and shares.");
        Console.WriteLine("  Future file-management commands may rename or delete files.");
        Console.Write("  Approve this device and the full-user file scope? [y/N] (60 s): ");
        var read = Task.Run(Console.ReadLine, ct);
        var winner = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(60), ct)).ConfigureAwait(false);
        if (winner != read) { Console.WriteLine("timed out: denied"); return false; }
        var yes = (await read.ConfigureAwait(false))?.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase) == true;
        if (yes)
        {
            approved.Add(deviceId);
            fullFiles.Add(deviceId);
            File.WriteAllText(_path, JsonSerializer.Serialize(approved));
            File.WriteAllText(_fullFilesPath, JsonSerializer.Serialize(fullFiles));
        }
        return yes;
    }
}
internal static class PairingQr
{
    /// <summary>Writes pairing.png next to the executable (and opens it unless told not to). The URI is always printed too.</summary>
    public static void Show(PairingOffer offer, bool open)
    {
        Console.WriteLine();
        Console.WriteLine("  Pair a phone by scanning pairing.png, or paste this link into the Android app:");
        Console.WriteLine("  " + offer.Uri);
        Console.WriteLine($"  Host {offer.HostAddress}:{offer.Port}  (the phone must be on the same network)");
        Console.WriteLine();
        try
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(offer.Uri, QRCodeGenerator.ECCLevel.M);
            var png = new PngByteQRCode(data).GetGraphic(pixelsPerModule: 10);
            var path = Path.Combine(AppContext.BaseDirectory, "pairing.png");
            File.WriteAllBytes(path, png);
            Console.WriteLine("  QR written to " + path);
            if (open) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Console.WriteLine("  (could not write/open the QR image: " + ex.Message + "; use the link above)");
        }
    }
}
