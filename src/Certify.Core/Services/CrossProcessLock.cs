namespace Certify.Core.Services;

/// <summary>
/// Blokada "jedna operacja na certyfikatach naraz" dzialajaca miedzy procesami
/// (GUI, usluga Windows, tryb --renew). Lokalny SemaphoreSlim + plik otwarty z
/// FileShare.None w katalogu danych. System zamyka uchwyt pliku przy smierci
/// procesu, wiec blokada nie zostaje "osierocona" (w przeciwienstwie do nazwanego
/// semafora), a brak powinowactwa do watku (jak Mutex) pozwala zwalniac po await.
/// API jak SemaphoreSlim (Wait/WaitAsync/Release/CurrentCount).
/// </summary>
public sealed class CrossProcessLock
{
    public const string FileName = "operation.lock";

    // Krotka chwila na kolizje z sonda CurrentCount z innego procesu (ona tez otwiera plik).
    private static readonly TimeSpan MinFileWait = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(100);

    private readonly SemaphoreSlim _local = new(1, 1);
    private readonly string _path;
    private FileStream? _file;

    public CrossProcessLock(string lockFilePath) => _path = lockFilePath;

    /// <summary>1 = wolna (w tym procesie i w innych), 0 = zajeta.</summary>
    public int CurrentCount
    {
        get
        {
            if (_local.CurrentCount == 0) return 0;
            if (!TryOpen(out var f)) return 0;
            f?.Dispose();
            return 1;
        }
    }

    public bool Wait(int millisecondsTimeout)
    {
        var deadline = Deadline(millisecondsTimeout);
        if (!_local.Wait(millisecondsTimeout)) return false;
        while (true)
        {
            if (TryOpen(out _file)) return true;
            if (DateTime.UtcNow >= deadline) { _local.Release(); return false; }
            Thread.Sleep(Poll);
        }
    }

    public Task WaitAsync(CancellationToken ct = default) => WaitAsync(Timeout.Infinite, ct);

    public async Task<bool> WaitAsync(int millisecondsTimeout, CancellationToken ct = default)
    {
        var deadline = Deadline(millisecondsTimeout);
        if (!await _local.WaitAsync(millisecondsTimeout, ct)) return false;
        try
        {
            while (true)
            {
                if (TryOpen(out _file)) return true;
                if (DateTime.UtcNow >= deadline) { _local.Release(); return false; }
                await Task.Delay(Poll, ct);
            }
        }
        catch
        {
            _local.Release();
            throw;
        }
    }

    public void Release()
    {
        var f = _file;
        _file = null;
        f?.Dispose();
        _local.Release();
    }

    private static DateTime Deadline(int ms) => ms < 0
        ? DateTime.MaxValue
        : DateTime.UtcNow + TimeSpan.FromMilliseconds(Math.Max(ms, MinFileWait.TotalMilliseconds));

    /// <summary>
    /// True = plik zablokowany przez nas (albo nie da sie go uzyc - wtedy tylko blokada lokalna).
    /// False = trzyma go inny proces.
    /// </summary>
    private bool TryOpen(out FileStream? file)
    {
        file = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            file = new FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (UnauthorizedAccessException) { return true; } // brak praw do katalogu danych - tylko lokalnie
        catch (IOException) { return false; }                 // sharing violation = inny proces trzyma blokade
    }
}
