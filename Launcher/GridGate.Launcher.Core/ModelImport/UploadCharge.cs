namespace GridGate.Launcher.Core.ModelImport;

/// <summary>Whether an upload may go ahead; when not, <see cref="Message"/> says why in words for the guest.</summary>
public sealed record ChargeResult(bool Allowed, string? Message);

/// <summary>The payment hook for Make the item: called once per item, before anything is written to Robust.</summary>
public interface IUploadCharge
{
    Task<ChargeResult> TryChargeAsync(Guid guest, ImportReport report, CancellationToken ct);
}

/// <summary>The proof of concept's charge: every upload is allowed and nothing is charged.</summary>
public sealed class FreeUploads : IUploadCharge
{
    public Task<ChargeResult> TryChargeAsync(Guid guest, ImportReport report, CancellationToken ct) =>
        Task.FromResult(new ChargeResult(true, null));
}
