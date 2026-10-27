using System.Diagnostics;

namespace Creatura.Cells;

/// <summary>
/// An exclusive, cross-process lock on a cell home: <c>&lt;home&gt;/ledger.lock</c> opened with <see cref="FileShare.None"/>
/// (an advisory <c>flock</c> on Unix, a share-mode lock on Windows). Every read-modify-write of the ledger and of a hook's
/// session state happens under it, because several host-tool sessions on one vessel fire hooks at the same moment and
/// the per-cell sequence number must be allocated by one writer at a time. The lock is released when the process ends,
/// so a crashed writer cannot leave it held.
/// </summary>
public sealed class LedgerLock : IDisposable
{
    public const string FileName = "ledger.lock";

    private readonly FileStream _stream;

    private LedgerLock(FileStream stream) => _stream = stream;

    /// <summary>The lock, or null when another holder kept it for longer than <paramref name="wait"/>.</summary>
    public static LedgerLock? TryAcquire(string homeRoot, TimeSpan wait)
    {
        OwnerOnly.CreateDirectory(homeRoot);
        var path = Path.Combine(homeRoot, FileName);
        var deadline = Stopwatch.GetTimestamp() + (long)(wait.TotalSeconds * Stopwatch.Frequency);
        var delay = 2;
        while (true)
        {
            try
            {
                var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                return new LedgerLock(new FileStream(path, options));
            }
            catch (IOException) when (Stopwatch.GetTimestamp() < deadline)
            {
                Thread.Sleep(delay);
                delay = Math.Min(delay * 2, 25);
            }
            catch (IOException)
            {
                return null;
            }
        }
    }

    /// <summary>The lock, waiting up to <paramref name="wait"/>; throws when it stays held (interactive commands).</summary>
    public static LedgerLock Acquire(string homeRoot, TimeSpan wait) =>
        TryAcquire(homeRoot, wait) ?? throw new IOException($"the cell home {homeRoot} is locked by another writer; try again");

    public void Dispose() => _stream.Dispose();
}
