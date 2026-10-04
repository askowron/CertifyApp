using Certify.Core.Services;

namespace Certify.Tests;

// Dwie instancje na tym samym pliku = jak GUI i usluga Windows (FileShare.None
// koliduje takze w obrebie jednego procesu).
public class CrossProcessLockTests
{
    [Fact]
    public void SecondInstance_CannotAcquireWhileHeld()
    {
        using var tmp = new TempDir();
        var path = tmp.File(CrossProcessLock.FileName);
        var gui = new CrossProcessLock(path);
        var service = new CrossProcessLock(path);

        Assert.True(gui.Wait(0));
        Assert.Equal(0, service.CurrentCount);
        Assert.False(service.Wait(0));

        gui.Release();
        Assert.Equal(1, service.CurrentCount);
        Assert.True(service.Wait(0));
        Assert.Equal(0, gui.CurrentCount);
        service.Release();
    }

    [Fact]
    public async Task WaitAsync_AcquiresAfterOtherReleases()
    {
        using var tmp = new TempDir();
        var path = tmp.File(CrossProcessLock.FileName);
        var a = new CrossProcessLock(path);
        var b = new CrossProcessLock(path);

        await a.WaitAsync();
        var waiting = b.WaitAsync(5000);
        await Task.Delay(300);
        Assert.False(waiting.IsCompleted);
        a.Release();
        Assert.True(await waiting);
        b.Release();
    }

    [Fact]
    public async Task WaitAsync_TimesOutAndKeepsLocalLockFree()
    {
        using var tmp = new TempDir();
        var path = tmp.File(CrossProcessLock.FileName);
        var a = new CrossProcessLock(path);
        var b = new CrossProcessLock(path);

        Assert.True(a.Wait(0));
        Assert.False(await b.WaitAsync(300));
        a.Release();
        // Nieudana proba nie moze zostawic zajetego semafora lokalnego.
        Assert.True(await b.WaitAsync(0));
        b.Release();
    }

    [Fact]
    public async Task Cancellation_ReleasesLocalLock()
    {
        using var tmp = new TempDir();
        var path = tmp.File(CrossProcessLock.FileName);
        var a = new CrossProcessLock(path);
        var b = new CrossProcessLock(path);

        Assert.True(a.Wait(0));
        using var cts = new CancellationTokenSource(300);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => b.WaitAsync(cts.Token));
        a.Release();
        Assert.True(b.Wait(0));
        b.Release();
    }

    [Fact]
    public async Task RenewalServices_ShareLockThroughDataDirectory()
    {
        using var tmp = new TempDir();
        var first = new RenewalService(new CertificateStore(tmp.Settings()), new FakeCa((c, _) => FakeCa.Issue(c)), [], tmp.Settings());
        var second = new RenewalService(new CertificateStore(tmp.Settings()), new FakeCa((c, _) => FakeCa.Issue(c)), [], tmp.Settings());

        await first.OperationLock.WaitAsync();
        Assert.False(await second.OperationLock.WaitAsync(0));
        first.OperationLock.Release();
        Assert.True(await second.OperationLock.WaitAsync(0));
        second.OperationLock.Release();
    }
}
