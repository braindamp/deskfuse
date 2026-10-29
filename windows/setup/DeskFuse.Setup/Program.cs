using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using DeskFuse.AudioBroker;
using DeskFuse.WindowsSecurity;
using QRCoder;

namespace DeskFuse.Setup;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            if (args.Length == 0)
            {
                using var client = BrokerControlClient.ConnectSetupActivationAsync(timeout.Token).GetAwaiter().GetResult();
                var framing = new EnrollmentFraming(new EnrollmentStreamChannel(client.Stream));
                framing.WriteAsync(EnrollmentFrameKind.BeginSetup, ReadOnlyMemory<byte>.Empty, timeout.Token).GetAwaiter().GetResult();
                var choices = new List<EnrollmentChoice>(); BrokerProofScope[]? maximum = null;
                while (true)
                {
                    var page = framing.ReadAsync(timeout.Token).GetAwaiter().GetResult();
                    if (page.Kind != EnrollmentFrameKind.SetupChoices) throw new InvalidDataException("Missing protected setup choices.");
                    var parsed = EnrollmentSetupSelection.ParseChoicePage(page.Body);
                    if (maximum is not null && !maximum.AsSpan().SequenceEqual(parsed.Maximum)) throw new InvalidDataException("Setup policy changed.");
                    maximum = parsed.Maximum; choices.AddRange(parsed.Choices);
                    if (choices.Count > 4096 || choices.Select(c => c.Id).Distinct().Count() != choices.Count) throw new InvalidDataException("Invalid protected choices.");
                    if (parsed.Last) break;
                }
                using var selection = new SetupSelectionWindow(choices.ToArray(), maximum!);
                if (selection.ShowDialog() != DialogResult.OK)
                { framing.WriteAsync(EnrollmentFrameKind.Cancel, ReadOnlyMemory<byte>.Empty, timeout.Token).GetAwaiter().GetResult(); return 0; }
                framing.WriteAsync(EnrollmentFrameKind.SelectSetup, selection.Selection.Encode(), timeout.Token).GetAwaiter().GetResult();
                var response = framing.ReadAsync(timeout.Token).GetAwaiter().GetResult();
                if (response.Kind != EnrollmentFrameKind.SetupLaunched || response.Body.Length != 16 || new Guid(response.Body, true) == Guid.Empty)
                    throw new InvalidDataException("The installed service did not launch protected setup.");
                return 0; // Only the separately service-created UI may confirm enrollment.
            }
            const string prefix = "--setup-registration=";
            if (args.Length != 1 || !args[0].StartsWith(prefix, StringComparison.Ordinal) ||
                !Guid.TryParseExact(args[0][prefix.Length..], "N", out var registration) || registration == Guid.Empty ||
                args[0] != prefix + registration.ToString("N")) throw new ArgumentException("Invalid protected setup bootstrap.");
            using var connection = BrokerControlClient.ConnectWorkerAsync(registration, true, timeout.Token).GetAwaiter().GetResult();
            using var window = new SetupWindow(registration, new(new EnrollmentStreamChannel(connection.Stream)), timeout.Token);
            Application.Run(window); return 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or
            OperationCanceledException or System.ComponentModel.Win32Exception or CryptographicException)
        {
            MessageBox.Show("Protected setup could not complete. " + error.Message, "DeskFuse setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}

internal sealed class SetupSelectionWindow : Form
{
    private sealed record Item(Guid Id, string Label) { public override string ToString() => Label; }
    private readonly ComboBox devices = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 600 };
    private readonly CheckedListBox permissions = new() { CheckOnClick = true, Width = 600, Height = 180 };
    private readonly BrokerProofScope[] scopes;
    internal EnrollmentSetupSelection Selection => new(((Item)devices.SelectedItem!).Id == Guid.Empty ? EnrollmentAction.New : EnrollmentAction.Replace,
        ((Item)devices.SelectedItem!).Id, permissions.CheckedIndices.Cast<int>().Select(i => scopes[i]).ToArray());
    internal SetupSelectionWindow(EnrollmentChoice[] choices, BrokerProofScope[] scopes)
    {
        this.scopes = scopes; Text = "DeskFuse — trusted phone setup"; AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        StartPosition = FormStartPosition.CenterScreen; MaximizeBox = false; MinimizeBox = false;
        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Padding = new Padding(20), WrapContents = false };
        layout.Controls.Add(new Label { Text = "Pair a new phone or replace an enrolled signing identity:", AutoSize = true });
        devices.Items.Add(new Item(Guid.Empty, "Pair a new phone"));
        foreach (var choice in choices) devices.Items.Add(new Item(choice.Id,
            $"Replace {choice.Id:N} — key {Convert.ToHexString(choice.KeySha256)[..16]}"));
        devices.SelectedIndex = 0; layout.Controls.Add(devices);
        layout.Controls.Add(new Label { Text = "Allow this phone to use:", AutoSize = true });
        foreach (var scope in scopes) permissions.Items.Add(scope switch
        {
            BrokerProofScope.Speaker => "Windows audio playback", BrokerProofScope.Display => "Windows displays",
            BrokerProofScope.Input => "Keyboard and pointer input", BrokerProofScope.ApplicationCapture => "Application presentation",
            BrokerProofScope.Microphone => "Phone microphone", BrokerProofScope.Camera => "Phone camera",
            BrokerProofScope.Files => "Files", _ => "Clipboard"
        }, scope == BrokerProofScope.Speaker);
        layout.Controls.Add(permissions);
        var proceed = new Button { Text = "Continue", AutoSize = true };
        proceed.Click += (_, _) => { if (permissions.CheckedItems.Count == 0) return; DialogResult = DialogResult.OK; Close(); };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        layout.Controls.Add(proceed); layout.Controls.Add(cancel); Controls.Add(layout); CancelButton = cancel;
    }
}

internal sealed class SetupWindow : Form
{
    private readonly Guid registration;
    private readonly EnrollmentFraming channel;
    private readonly CancellationTokenSource stop;
    private readonly Label account = new() { AutoSize = true, MaximumSize = new Size(600, 0) };
    private readonly Label permissions = new() { AutoSize = true, MaximumSize = new Size(600, 0) };
    private readonly Label location = new() { AutoSize = true, MaximumSize = new Size(600, 0) };
    private readonly Label status = new() { AutoSize = true, MaximumSize = new Size(600, 0), Text = "Waiting for the protected pairing offer…" };
    private readonly PictureBox qr = new() { Size = new Size(280, 280), SizeMode = PictureBoxSizeMode.Zoom };
    private readonly TextBox code = new() { ReadOnly = true, Multiline = true, Width = 590, Height = 60, Font = new Font(FontFamily.GenericMonospace, 12) };
    private readonly Button confirm = new() { Text = "Confirm after the phone verifies this PC", AutoSize = true, Enabled = false };
    private readonly Button cancel = new() { Text = "Cancel pairing", AutoSize = true };
    private readonly TaskCompletionSource result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private EnrollmentOffer? offer;
    private EnrollmentBinding? binding;
    private bool complete, closing;

    internal SetupWindow(Guid registration, EnrollmentFraming channel, CancellationToken ct)
    {
        this.registration = registration; this.channel = channel; stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Text = "DeskFuse — Pair this phone"; ClientSize = new Size(640, 580); MinimumSize = new Size(600, 550); StartPosition = FormStartPosition.CenterScreen;
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(18) };
        layout.Controls.AddRange([account, permissions, location, status, qr, code, confirm, cancel]); Controls.Add(layout);
        confirm.Click += async (_, _) => await Confirm();
        cancel.Click += (_, _) => Close();
        Shown += (_, _) => _ = ReadControl();
        FormClosing += RequestClose;
    }
    private async Task ReadControl()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var frame = await channel.ReadAsync(stop.Token).ConfigureAwait(false);
                switch (frame.Kind)
                {
                    case EnrollmentFrameKind.ShowOffer:
                        var signedOffer = EnrollmentFraming.ParseSigned(frame.Body);
                        var parsed = EnrollmentProtocol.ParseOffer(signedOffer.Message);
                        using (var process = Process.GetCurrentProcess())
                            if (parsed.UiRegistration != registration || parsed.UiProcessId != Environment.ProcessId ||
                                parsed.UiCreationFileTime != (ulong)process.StartTime.ToUniversalTime().ToFileTimeUtc())
                                throw new InvalidDataException("Setup offer targets another UI incarnation.");
                        if (!EnrollmentProtocol.Verify(parsed.HostSpki, signedOffer.Message, signedOffer.Signature)) throw new InvalidDataException("Invalid host offer signature.");
                        if (offer is not null && !EnrollmentProtocol.Offer(offer).AsSpan().SequenceEqual(signedOffer.Message))
                            throw new InvalidDataException("Setup offer changed during the ceremony.");
                        offer = parsed;
                        Apply(() =>
                        {
                            account.Text = "Pair for: " + AccountName(parsed.SubjectUserSid) +
                                (parsed.SubjectUserSid.AsSpan().SequenceEqual(parsed.ApproverUserSid) ? "" : "\nApproved by administrator: " + AccountName(parsed.ApproverUserSid));
                            permissions.Text = "Allow: " + string.Join(", ", parsed.Scopes);
                            status.Text = "Choose this PC on your phone to begin pairing.";
                        });
                        break;
                    case EnrollmentFrameKind.ShowFrozen:
                        if (offer is null) throw new InvalidDataException("Frozen setup arrived before the offer.");
                        var frozen = EnrollmentFraming.ParseSigned(frame.Body);
                        var transcript = EnrollmentProtocol.ParseTranscript(frozen.Message);
                        if (!EnrollmentProtocol.Offer(transcript.Offer).AsSpan().SequenceEqual(EnrollmentProtocol.Offer(offer)))
                            throw new InvalidDataException("Frozen candidate changed the offer.");
                        var candidate = EnrollmentProtocol.Binding(transcript);
                        if (!EnrollmentProtocol.Verify(offer.HostSpki, EnrollmentProtocol.Frozen(candidate.TranscriptSha256), frozen.Signature))
                            throw new InvalidDataException("Invalid frozen transcript signature.");
                        if (binding is not null && !EnrollmentProtocol.PhoneApproval(binding).AsSpan().SequenceEqual(EnrollmentProtocol.PhoneApproval(candidate)))
                            throw new InvalidDataException("The frozen phone candidate changed.");
                        binding = candidate;
                        Apply(() => ShowDigest(candidate));
                        break;
                    case EnrollmentFrameKind.SetupLocation:
                        if (frame.Body.Length is < 3 or > 257 || System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(frame.Body) == 0)
                            throw new InvalidDataException("Invalid setup locator.");
                        var name = new System.Text.UTF8Encoding(false, true).GetString(frame.Body.AsSpan(2));
                        if (name.Any(char.IsControl)) throw new InvalidDataException("Invalid PC locator name.");
                        Apply(() => location.Text = $"Connect from your phone to {name}:{System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(frame.Body)}");
                        break;
                    case EnrollmentFrameKind.Result:
                        if (frame.Body.Length != 1 || frame.Body[0] is < 1 or > 5) throw new InvalidDataException("Invalid setup result.");
                        complete = true; result.TrySetResult();
                        Apply(() =>
                        {
                            confirm.Enabled = false; cancel.Text = "Close";
                            status.Text = frame.Body[0] switch { 1 => "Phone paired successfully.", 2 => "Pairing canceled.", 3 => "Enrollment revoked.", 5 => "Existing pairing recovered. Finish on your phone.", _ => "Setup status is unresolved. Reopen trusted setup to recover it." };
                        });
                        return;
                    default: throw new InvalidDataException("Unexpected setup UI command.");
                }
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or UnauthorizedAccessException or CryptographicException)
        {
            result.TrySetException(error);
            Apply(() => { confirm.Enabled = false; status.Text = "Setup connection closed. Enrollment status may require recovery."; });
        }
    }
    private void ShowDigest(EnrollmentBinding value)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(EnrollmentProtocol.VerificationQr(value.TicketId, value.TranscriptSha256), QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(8);
        using var bytes = new MemoryStream(png, false); using var image = Image.FromStream(bytes);
        var previous = qr.Image; qr.Image = new Bitmap(image); previous?.Dispose();
        code.Text = string.Join(" ", EnrollmentProtocol.VerificationText(value.TranscriptSha256).Chunk(4).Select(chars => new string(chars)));
        status.Text = "Scan this QR or enter the complete code on your phone. Approve on the phone, then confirm here.";
        confirm.Enabled = true;
    }
    private async Task Confirm()
    {
        var current = binding; if (current is null || complete || closing) return;
        confirm.Enabled = false;
        try
        {
            await channel.WriteAsync(EnrollmentFrameKind.Confirm, EnrollmentProtocol.PhoneApproval(current), stop.Token);
            status.Text = "PC confirmed. Waiting for both approvals and the saved enrollment…";
        }
        catch (Exception error) when (error is IOException or OperationCanceledException)
        { status.Text = "PC confirmation could not be delivered. Reopen trusted setup to check status."; }
    }
    private async void RequestClose(object? sender, FormClosingEventArgs args)
    {
        if (complete) { stop.Cancel(); return; }
        args.Cancel = true;
        if (closing) return;
        closing = true; confirm.Enabled = false; cancel.Enabled = false;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await channel.WriteAsync(EnrollmentFrameKind.Cancel, binding is null ? ReadOnlyMemory<byte>.Empty : EnrollmentProtocol.PhoneApproval(binding), timeout.Token);
            await result.Task.WaitAsync(timeout.Token);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or UnauthorizedAccessException)
        { MessageBox.Show(this, "Cancellation status is unresolved. Reopen trusted setup before relying on this pairing.", "DeskFuse setup", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        finally { complete = true; Close(); }
    }
    private void Apply(Action action)
    {
        if (IsDisposed || Disposing) return;
        try { BeginInvoke(action); } catch (InvalidOperationException) when (IsDisposed || Disposing || !IsHandleCreated) { }
    }
    private static string AccountName(byte[] sid)
    { var value = new SecurityIdentifier(sid, 0); try { return value.Translate(typeof(NTAccount)).Value; } catch (IdentityNotMappedException) { return value.Value; } }
    protected override void Dispose(bool disposing)
    { if (disposing) { stop.Cancel(); qr.Image?.Dispose(); stop.Dispose(); } base.Dispose(disposing); }
}
