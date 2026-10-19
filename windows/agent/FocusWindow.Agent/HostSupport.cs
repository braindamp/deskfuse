using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
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
internal sealed class ConsoleApproval : IApprovalPolicy
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
