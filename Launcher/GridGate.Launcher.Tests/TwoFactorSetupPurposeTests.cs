using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>The data-protection purposes seal the setup form on screen; a changed string breaks forms already open.</summary>
public sealed class TwoFactorSetupPurposeTests
{
    [Fact]
    public void Staff_setup_purpose_is_pinned() =>
        Assert.Equal("GridGate.TwoFactorSetup.v1", GridGate.Launcher.Pages.Profile.TwoFactorModel.SetupPurpose);

    [Fact]
    public void Resident_setup_purpose_is_pinned() =>
        Assert.Equal("GridGate.GuestTwoFactorSetup.v1", GridGate.Launcher.Pages.Guest.TwoFactorModel.SetupPurpose);
}
