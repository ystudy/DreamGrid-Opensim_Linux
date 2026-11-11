using System.Globalization;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class BackgroundWorkTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (BackgroundWork Work, ConcurrentQueue<(LogLevel Level, string Category, string Message)> Logs) Create()
    {
        var logs = new ConcurrentQueue<(LogLevel, string, string)>();
        using var factory = LoggerFactory.Create(b => b.AddProvider(new CapturingLoggerProvider(logs)));
        return (new BackgroundWork(factory.CreateLogger<BackgroundWork>()), logs);
    }

    // Task.Run carries the starting request's culture; a resident page runs in the resident's language.
    [Fact]
    public async Task Work_runs_in_English_whatever_culture_started_it()
    {
        var (work, _) = Create();
        var seen = new TaskCompletionSource<(string, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var de = CultureInfo.GetCultureInfo("de");
        var (culture, ui) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        CultureInfo.CurrentCulture = de;
        CultureInfo.CurrentUICulture = de;
        try
        {
            work.Run("culture", _ =>
            {
                seen.SetResult((CultureInfo.CurrentCulture.Name, CultureInfo.CurrentUICulture.Name));
                return Task.CompletedTask;
            });
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = ui;
        }
        Assert.Equal(("en", "en"), await seen.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct));
    }

    [Fact]
    public async Task Reset_work_runs_in_English_whatever_culture_posted_it()
    {
        var logs = new ConcurrentQueue<(LogLevel, string, string)>();
        using var factory = LoggerFactory.Create(b => b.AddProvider(new CapturingLoggerProvider(logs)));
        var worker = new Guests.ResetWorker(factory.CreateLogger<Guests.ResetWorker>());
        await worker.StartAsync(Ct);
        try
        {
            var seen = new TaskCompletionSource<(string, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
            var de = CultureInfo.GetCultureInfo("de");
            var (culture, ui) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
            CultureInfo.CurrentCulture = de;
            CultureInfo.CurrentUICulture = de;
            try
            {
                worker.Post(() =>
                {
                    seen.SetResult((CultureInfo.CurrentCulture.Name, CultureInfo.CurrentUICulture.Name));
                    return Task.CompletedTask;
                });
            }
            finally
            {
                CultureInfo.CurrentCulture = culture;
                CultureInfo.CurrentUICulture = ui;
            }
            Assert.Equal(("en", "en"), await seen.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        }
        finally
        {
            await worker.StopAsync(Ct);
        }
    }

    [Fact]
    public async Task Stop_waits_for_running_work()
    {
        var (work, _) = Create();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = false;
        work.Run("slow", async _ =>
        {
            await release.Task;
            finished = true;
        });

        var stop = work.StopAsync(Ct);
        await Task.Delay(100, Ct);
        Assert.False(stop.IsCompleted);

        release.SetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.True(finished);
    }

    [Fact]
    public async Task Stop_cancels_token_seen_by_work()
    {
        var (work, _) = Create();
        var seen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        work.Run("waiter", async ct =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { seen.SetResult(ct.IsCancellationRequested); }
        });

        await work.StopAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.True(await seen.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct));
    }

    [Fact]
    public async Task Work_failure_is_logged_not_thrown()
    {
        var (work, logs) = Create();
        work.Run("exploder", _ => throw new InvalidOperationException("boom"));

        await work.StopAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Contains(logs, l => l.Level == LogLevel.Error && l.Message.Contains("exploder") && l.Message.Contains("boom"));
    }
}
