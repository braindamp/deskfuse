using FocusWindow.Core.Session;

namespace FocusWindow.Core.Transport;

/// <summary>
/// A first-time enrollment is different from a reconnect. A remembered device ID
/// must never silently authorize issuance of a new secret.
/// </summary>
public interface IEnrollmentApprovalPolicy
{
    Task<bool> ApproveNewPairingAsync(string deviceId, string deviceLabel,
        string verificationCode, CancellationToken ct);
}

public static class EnrollmentApproval
{
    /// <summary>Default-deny for host applications that have not installed an enrollment UI.</summary>
    public static Task<bool> ApproveNewPairingAsync(this IApprovalPolicy approval,
        string deviceId, string deviceLabel, string verificationCode, CancellationToken ct) =>
        approval is IEnrollmentApprovalPolicy enrollment
            ? enrollment.ApproveNewPairingAsync(deviceId, deviceLabel, verificationCode, ct)
            : Task.FromResult(false);
}
