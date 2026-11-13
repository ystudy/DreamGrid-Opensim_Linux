using GridGate.Launcher.Core;
using GridGate.Launcher.Guests;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>A new account remembers the language of the page it was signed up from.</summary>
public sealed partial class SignUpsTests
{
    private static string? SavedFor(Rig rig, string first) =>
        rig.F.Services.GetRequiredService<ResidentLanguages>().Get(rig.F.Robust.All.Single(a => a.FirstName == first).PrincipalId);

    [Fact]
    public async Task Sign_up_from_a_Spanish_page_saves_Spanish_when_open()
    {
        await using var rig = Factory(email: false);

        var r = await rig.SignUps.SubmitAsync(Form(), Caller, Ct, "es");

        Assert.Equal(SignUpOutcome.SignedUp, r.Outcome);
        Assert.Equal("es", SavedFor(rig, "Bea"));
    }

    [Fact]
    public async Task Sign_up_from_a_Spanish_page_saves_Spanish_when_email_confirms()
    {
        await using var rig = Factory();

        var r = await rig.SignUps.SubmitAsync(Form(), Caller, Ct, "es");

        Assert.Equal(SignUpOutcome.CheckEmail, r.Outcome);
        Assert.Equal("es", SavedFor(rig, "Bea"));
    }

    [Fact]
    public async Task Sign_up_from_a_Spanish_page_saves_Spanish_when_staff_approve()
    {
        await using var rig = Factory(email: false, before: f => f.Store.Save(f.Store.LoadOrCreate() is var s
            ? s with { Bots = s.Bots with { AdminApproval = true } } : null!));

        var r = await rig.SignUps.SubmitAsync(Form(), Caller, Ct, "es");

        Assert.Equal(SignUpOutcome.WaitingForStaff, r.Outcome);
        Assert.Equal("es", SavedFor(rig, "Bea"));
    }

    [Fact]
    public async Task A_refused_sign_up_saves_no_language()
    {
        await using var rig = Factory();

        await rig.SignUps.SubmitAsync(Form(password: "short", terms: false), Caller, Ct, "es");

        Assert.DoesNotContain(rig.F.Robust.All, a => a.FirstName == "Bea");
    }
}
