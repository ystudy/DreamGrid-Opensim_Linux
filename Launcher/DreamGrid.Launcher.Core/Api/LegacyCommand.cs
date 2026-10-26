namespace DreamGrid.Launcher.Core.Api;

/// <summary>A port-8001 command (spec 5): the minimum avatar level it needs and what it runs.</summary>
public sealed record LegacyCommand(string Name, int MinLevel, Func<LegacyRequest, CancellationToken, Task<string>> Run);

/// <summary>
/// A parsed port-8001 request. <see cref="Raw"/> holds every query/form parameter; the host project builds
/// it from ASP.NET's query and form collections (Core stays free of ASP.NET).
/// </summary>
public sealed record LegacyRequest(
    string Command,
    string? Avatar,
    string? Password,
    string? RegionName,
    Guid? RegionUuid,
    IReadOnlyDictionary<string, string> Raw);
