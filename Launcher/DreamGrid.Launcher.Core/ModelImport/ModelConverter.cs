using System.Diagnostics;
using System.Text.Json;
using DreamGrid.Launcher.Core.Grid;
using Microsoft.Extensions.Logging;

namespace DreamGrid.Launcher.Core.ModelImport;

public enum ConverterOutcome { Done, Failed, TimedOut }

/// <summary><see cref="Report"/> is set only when <see cref="Outcome"/> is <see cref="ConverterOutcome.Done"/>.</summary>
public sealed record ConverterRun(ConverterOutcome Outcome, ImportReport? Report);

public interface IModelConverter
{
    Task<ConverterRun> CheckAsync(string inputFile, ImportLimits limits, ImportOptions options, string jobDir, CancellationToken ct);
}

/// <summary>
/// Runs <c>GridGate.ModelImport.dll check</c> as a child process with a memory limit, a time limit and below-normal priority (so
/// running regions keep the processor first), and reads the report it writes. Only JSON files cross between the two programs.
/// </summary>
public sealed class ModelConverter(LauncherPaths paths, TimeProvider time, ILogger<ModelConverter> log, TimeSpan? timeLimit = null) : IModelConverter
{
    public static readonly TimeSpan DefaultTimeLimit = TimeSpan.FromMinutes(2);
    /// <summary>1.5 GB: the converter fails with out-of-memory instead of growing.</summary>
    public const string HeapHardLimit = "0x60000000";

    /// <summary>Lets tests swap the real command for FakeChild.</summary>
    public Func<ProcessStartInfo, ProcessStartInfo>? StartForTests { get; init; }

    public async Task<ConverterRun> CheckAsync(string inputFile, ImportLimits limits, ImportOptions options, string jobDir, CancellationToken ct)
    {
        var limitsFile = Path.Combine(jobDir, "limits.json");
        var optionsFile = Path.Combine(jobDir, "options.json");
        var reportFile = Path.Combine(jobDir, "report.json");
        await File.WriteAllTextAsync(limitsFile, JsonSerializer.Serialize(limits, ContractJson.Options), ct);
        await File.WriteAllTextAsync(optionsFile, JsonSerializer.Serialize(options, ContractJson.Options), ct);

        var psi = new ProcessStartInfo(GridController.DotnetHost()) { WorkingDirectory = paths.RuntimeOpenSim };
        foreach (var a in new[]
        {
            Path.Combine(paths.RuntimeOpenSim, "GridGate.ModelImport.dll"), "check",
            "--input", inputFile, "--limits", limitsFile, "--options", optionsFile, "--out", jobDir,
        })
            psi.ArgumentList.Add(a);
        psi.Environment["DOTNET_GCHeapHardLimit"] = HeapHardLimit;
        if (StartForTests is not null) psi = StartForTests(psi);
        psi.UseShellExecute = false;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.CreateNoWindow = true;

        Process process;
        try { process = Process.Start(psi) ?? throw new InvalidOperationException("no process"); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            log.LogError(e, "Model import: the converter could not be started");
            return new ConverterRun(ConverterOutcome.Failed, null);
        }
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            // It exited already, or the system won't lower it: it runs at normal priority.
        }

        using (process)
        {
            // Read both pipes so a chatty child never blocks on a full one.
            var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
            var limit = timeLimit ?? DefaultTimeLimit;
            using var timeout = new CancellationTokenSource(limit, time);
            using var both = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            try
            {
                await process.WaitForExitAsync(both.Token);
            }
            catch (OperationCanceledException)
            {
                Kill(process, log);
                if (ct.IsCancellationRequested) throw;
                log.LogWarning("Model import: the converter ran over {Limit} and was stopped", limit);
                return new ConverterRun(ConverterOutcome.TimedOut, null);
            }
            await Task.WhenAll(stdout, stderr);

            if (process.ExitCode != 0)
            {
                log.LogWarning("Model import: the converter exited with {Code}: {Message}", process.ExitCode, (await stderr).Trim());
                return new ConverterRun(ConverterOutcome.Failed, null);
            }
        }

        try
        {
            var report = JsonSerializer.Deserialize<ImportReport>(await File.ReadAllTextAsync(reportFile, ct), ContractJson.Options);
            if (report is not null) return new ConverterRun(ConverterOutcome.Done, report);
            log.LogWarning("Model import: the converter's report was empty");
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            log.LogWarning("Model import: the converter's report could not be read: {Message}", e.Message);
        }
        return new ConverterRun(ConverterOutcome.Failed, null);
    }

    // Never throws, so the caller's time-out or cancel path always runs. Kill(entireProcessTree) throws AggregateException when
    // part of the tree could not be stopped.
    private static void Kill(Process process, ILogger log)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch (AggregateException e)
        {
            log.LogWarning(e, "Model import: the converter could not be stopped completely: {Message}", e.Message);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }
}
