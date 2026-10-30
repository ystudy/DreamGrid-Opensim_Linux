namespace DreamGrid.Launcher.Setup;

/// <summary>
/// What the shared hypergrid switch (Pages/Shared/_HypergridSwitch.cshtml) shows.
/// </summary>
/// <param name="On">Whether the switch is currently on.</param>
/// <param name="Allowed">Whether the owner can use the switch; when false only <paramref name="Reason"/> is shown.</param>
/// <param name="Reason">Why the switch is unavailable (plain words), shown when <paramref name="Allowed"/> is false.</param>
/// <param name="PublicPort">The grid's public port (RobustPublicPort), named in the router advice.</param>
/// <param name="RegionPort">The first region port (RegionPort), named in the router advice.</param>
public sealed record HypergridSwitchModel(bool On, bool Allowed, string? Reason, int PublicPort, int RegionPort);
