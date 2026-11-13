using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace GridGate.Launcher.Core;

/// <summary>
/// One JSON state file, read at start and written atomically (<see cref="AtomicFile"/>): the shape every store under
/// <c>&lt;data&gt;/guests</c> uses. <see cref="Load"/> gives <c>empty()</c> for a missing file; a file that isn't valid JSON for
/// <typeparamref name="T"/> is moved aside as <c>&lt;name&gt;.damaged-&lt;utc yyyyMMddHHmmss&gt;</c> (so the next save does not
/// destroy it), with one warning naming the path, and also gives <c>empty()</c>; a file that can't be opened at all (access
/// denied, something in the way) is left where it is, with a warning, and gives <c>empty()</c>. <see cref="Save"/> returns
/// false instead of throwing, and a failure is logged once until a save succeeds again. Safe to call from any thread.
/// </summary>
public sealed class JsonStateFile<T>(string path, Func<T> empty, ILogger? log) where T : class
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly Lock _gate = new();
    private bool _saveFailing;

    public T Load()
    {
        lock (_gate)
        {
            string json;
            try
            {
                json = AtomicFile.ReadShared(path);
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                return empty();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log?.LogWarning("The file {Path} could not be read ({Error}); it is treated as empty and left where it is.", path, e.GetType().Name);
                return empty();
            }

            try
            {
                if (JsonSerializer.Deserialize<T>(json, Options) is { } state) return state;
            }
            catch (Exception e) when (e is JsonException or NotSupportedException)
            {
            }
            SetAside();
            return empty();
        }
    }

    /// <summary>Writes <paramref name="state"/>; false (logged once until a save succeeds) when it can't be written.</summary>
    public bool Save(T state)
    {
        lock (_gate)
        {
            try
            {
                // The host makes the guests folder owner-only at start; this only matters where it is missing.
                AtomicFile.WriteAllTextCreatingFolder(path, JsonSerializer.Serialize(state, Options));
                _saveFailing = false;
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (!_saveFailing)
                    log?.LogWarning("The file {Path} could not be saved ({Error}); the data is kept in memory only.", path, e.GetType().Name);
                _saveFailing = true;
                return false;
            }
        }
    }

    private void SetAside()
    {
        try
        {
            var aside = AtomicFile.SetAside(path);
            log?.LogWarning("The file {Path} could not be read and was set aside as {Aside}.", path, Path.GetFileName(aside));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log?.LogWarning("The file {Path} could not be read and could not be set aside ({Error}).", path, e.GetType().Name);
        }
    }
}
