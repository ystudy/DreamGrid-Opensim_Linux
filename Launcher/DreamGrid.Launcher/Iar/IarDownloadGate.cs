namespace DreamGrid.Launcher.Iar;

/// <summary>
/// One backup download stream per resident at a time, so a download program that opens many connections can't multiply the speed
/// limit (final review, Important 3). A browser's resume asks again after the first stream has ended. Safe to call from any thread.
/// </summary>
public sealed class IarDownloadGate
{
    private readonly Lock _gate = new();
    private readonly HashSet<Guid> _streaming = [];

    /// <summary>A ticket to dispose when the stream ends, or null while this resident already has one.</summary>
    public IDisposable? TryEnter(Guid principal)
    {
        lock (_gate)
            return _streaming.Add(principal) ? new Ticket(this, principal) : null;
    }

    private void Leave(Guid principal)
    {
        lock (_gate) _streaming.Remove(principal);
    }

    private sealed class Ticket(IarDownloadGate gate, Guid principal) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) gate.Leave(principal);
        }
    }
}
