using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using DreamGrid.Launcher.Core.Guests;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Guests;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>
/// Robust's login asks the launcher whether an avatar must accept the Terms first, and says when they ticked "I agree" in the
/// viewer (<c>/dreamgrid/terms</c>, worklist 2.6). All texts are made up.
/// </summary>
public sealed class ViewerTermsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid Resident = Guid.Parse("3e000000-0000-0000-0000-000000000003");
    private static readonly DateTimeOffset Saved = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

    private static LegalSettings TermsText(int version) => new(
        new Dictionary<string, IReadOnlyDictionary<string, LegalText>>
        {
            ["en"] = new Dictionary<string, LegalText> { [LegalPages.Terms] = new("Test terms.", version, Saved) },
        });

    private static LauncherFactory Factory(LegalSettings? legal = null, string caller = "127.0.0.1") => new(s => s with
    {
        GridName = "Avalon",
        SetupToken = null,
        Setup = s.Setup with { OwnerCreated = true, Finished = true, Owner = new OwnerInfo("Test", "Owner", TestAuth.OwnerId) },
        Legal = legal ?? new LegalSettings(),
    })
    {
        Before = app => app.Use((ctx, next) =>
        {
            ctx.Connection.RemoteIpAddress = IPAddress.Parse(caller);
            return next(ctx);
        }),
    };

    private static TermsAcceptances Store(LauncherFactory f) => f.Services.GetRequiredService<TermsAcceptances>();

    private static async Task<(HttpStatusCode Status, string Body)> CheckAsync(LauncherFactory f, Guid id, int level = 0)
    {
        using var r = await f.CreateClient().GetAsync($"/dreamgrid/terms?id={id}&level={level}", Ct);
        return (r.StatusCode, await r.Content.ReadAsStringAsync(Ct));
    }

    private static async Task<(HttpStatusCode Status, string Body)> AgreeAsync(LauncherFactory f, Guid id, int level = 0, string ip = "198.51.100.7")
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["id"] = id.ToString(), ["level"] = level.ToString(), ["ip"] = ip, ["name"] = "Test Resident",
        });
        using var r = await f.CreateClient().PostAsync("/dreamgrid/terms", form, Ct);
        return (r.StatusCode, await r.Content.ReadAsStringAsync(Ct));
    }

    private static List<JsonElement> Audit(LauncherFactory f)
    {
        var path = Path.Combine(f.Paths.Logs, "audit.log");
        if (!File.Exists(path)) return [];
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(s).ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();
    }

    [Fact]
    public async Task Without_terms_text_nobody_is_asked()
    {
        await using var f = Factory();

        Assert.Equal((HttpStatusCode.OK, "OK"), await CheckAsync(f, Resident));
    }

    [Fact]
    public async Task A_resident_who_has_not_accepted_is_asked()
    {
        await using var f = Factory(TermsText(3));

        Assert.Equal((HttpStatusCode.OK, "ASK"), await CheckAsync(f, Resident));
    }

    [Fact]
    public async Task Agreeing_in_the_viewer_records_the_current_version_and_lets_them_in()
    {
        await using var f = Factory(TermsText(3));

        Assert.Equal((HttpStatusCode.OK, "OK"), await AgreeAsync(f, Resident));

        var accepted = Assert.Single(Store(f).Of(Resident));
        Assert.Equal(3, accepted.Version);
        Assert.Equal("198.51.100.7", accepted.Ip);
        Assert.Equal((HttpStatusCode.OK, "OK"), await CheckAsync(f, Resident));
        var entry = Assert.Single(Audit(f), e => e.GetProperty("action").GetString() == "terms-accepted");
        Assert.Equal("Test Resident", entry.GetProperty("actor").GetString());
        Assert.Equal(Resident, entry.GetProperty("actorId").GetGuid());
        Assert.Equal("terms v3 in the viewer", entry.GetProperty("target").GetString());
    }

    [Fact]
    public async Task Accepting_on_the_website_counts_in_the_viewer_too()
    {
        await using var f = Factory(TermsText(3));
        Store(f).Accept(Resident, 3, "203.0.113.9");

        Assert.Equal((HttpStatusCode.OK, "OK"), await CheckAsync(f, Resident));
    }

    [Fact]
    public async Task A_new_version_asks_again()
    {
        await using var f = Factory(TermsText(4));
        Store(f).Accept(Resident, 3, "203.0.113.9");

        Assert.Equal((HttpStatusCode.OK, "ASK"), await CheckAsync(f, Resident));
    }

    [Theory]
    [InlineData(100)] // supervisor
    [InlineData(200)] // admin
    public async Task Staff_are_never_asked(int level)
    {
        await using var f = Factory(TermsText(3));

        Assert.Equal((HttpStatusCode.OK, "OK"), await CheckAsync(f, Resident, level));
    }

    [Fact]
    public async Task The_owner_is_never_asked()
    {
        await using var f = Factory(TermsText(3));

        Assert.Equal((HttpStatusCode.OK, "OK"), await CheckAsync(f, TestAuth.OwnerId, level: 0));
    }

    [Fact]
    public async Task Agreeing_with_nothing_to_accept_records_nothing()
    {
        await using var f = Factory();

        Assert.Equal((HttpStatusCode.OK, "OK"), await AgreeAsync(f, Resident));

        Assert.Empty(Store(f).Of(Resident));
        Assert.DoesNotContain(Audit(f), e => e.GetProperty("action").GetString() == "terms-accepted");
    }

    [Fact]
    public async Task Agreeing_twice_records_once()
    {
        await using var f = Factory(TermsText(3));

        await AgreeAsync(f, Resident);
        await AgreeAsync(f, Resident);

        Assert.Single(Store(f).Of(Resident));
    }

    [Fact]
    public async Task An_address_that_is_not_an_address_is_not_kept()
    {
        await using var f = Factory(TermsText(3));

        await AgreeAsync(f, Resident, ip: "<script>");

        Assert.Null(Assert.Single(Store(f).Of(Resident)).Ip);
    }

    [Theory]
    [InlineData("/dreamgrid/terms?level=0")]
    [InlineData("/dreamgrid/terms?id=nope&level=0")]
    [InlineData("/dreamgrid/terms?id=3e000000-0000-0000-0000-000000000003&level=x")]
    public async Task A_check_without_an_avatar_is_400(string url)
    {
        await using var f = Factory(TermsText(3));

        using var r = await f.CreateClient().GetAsync(url, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Only_this_machine_may_ask_or_agree()
    {
        await using var f = Factory(TermsText(3), caller: "192.168.1.50");

        Assert.Equal(HttpStatusCode.Forbidden, (await CheckAsync(f, Resident)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await AgreeAsync(f, Resident)).Status);
        Assert.Empty(Store(f).Of(Resident));
    }
}
