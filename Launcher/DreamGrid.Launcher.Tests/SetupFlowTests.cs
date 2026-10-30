using System.Net;
using System.Text.Json;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core.Database;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Plans;
using DreamGrid.Launcher.Core.Processes;
using DreamGrid.Launcher.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class SetupFlowTests
{
    private const string Token = "tok-1234567890abcdefghijklmnopqrstuv";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Before the owner exists: the setup link's token is in the settings; the plan and network steps are done.</summary>
    private static LauncherFactory Fresh(Func<SetupState, SetupState>? setup = null) => new(s => s with
    {
        SetupToken = Token,
        Plan = OwnerPlan.Local,
        Setup = setup is null ? s.Setup with { NetworkSet = true } : setup(s.Setup with { NetworkSet = true }),
    })
    {
        Before = TestAuth.Apply,
    };

    /// <summary>MariaDB as the installer leaves it: found (the test host's own search finds nothing).</summary>
    private static readonly MariaDbBinaries FoundMariaDb = new("mariadbd", "mariadb-install-db", null);

    /// <summary>Before the owner exists and before a plan is chosen, with MariaDB found unless <paramref name="mariaDb"/> says otherwise.</summary>
    private static LauncherFactory Unplanned(Func<LauncherSettings, LauncherSettings>? more = null, MariaDbBinaries? mariaDb = null) =>
        new(s =>
        {
            var seeded = s with { SetupToken = Token, Plan = null };
            return more is null ? seeded : more(seeded);
        })
        {
            Before = TestAuth.Apply,
            ExtraServices = services =>
            {
                services.RemoveAll<MariaDbBinaries>();
                services.AddSingleton(mariaDb ?? FoundMariaDb);
            },
        };

    /// <summary>After the owner exists (token spent); the client signs in through TestAuth.</summary>
    private static LauncherFactory AfterOwner(bool regionCreated = false) => new(s => s with
    {
        GridName = "Avalon",
        SetupToken = null,
        Plan = OwnerPlan.Local,
        Setup = new SetupState(WorldNamed: true, OwnerCreated: true, RegionCreated: regionCreated,
            Owner: new OwnerInfo("Test", "Owner", Guid.NewGuid()), NetworkSet: true),
    })
    {
        Before = TestAuth.Apply,
    };

    private static async Task<HttpClient> SetupClientAsync(LauncherFactory f)
    {
        var client = f.CreateClient(new() { AllowAutoRedirect = false });
        (await client.GetAsync("/setup?token=" + Token, Ct)).Dispose();
        return client;
    }

    private static async Task<HttpClient> SignedInClientAsync(LauncherFactory f)
    {
        var client = f.CreateClient(new() { AllowAutoRedirect = false });
        (await client.GetAsync("/__test/signin", Ct)).Dispose();
        return client;
    }

    private static string? Location(HttpResponseMessage r) => r.Headers.Location?.OriginalString;

    /// <summary>Loads the form page for its antiforgery token, then posts the fields.</summary>
    private static async Task<HttpResponseMessage> PostFormAsync(HttpClient client, string url, Dictionary<string, string> fields, string? tokenFrom = null)
    {
        // The token belongs to the client, not the page: another form page can supply it.
        var page = await client.GetStringAsync(tokenFrom ?? url.Split('?')[0], Ct);
        var token = new HtmlParser().ParseDocument(page).QuerySelector("input[name=__RequestVerificationToken]")?.GetAttribute("value");
        Assert.NotNull(token);
        var form = new Dictionary<string, string>(fields) { ["__RequestVerificationToken"] = token };
        return await client.PostAsync(url, new FormUrlEncodedContent(form), Ct);
    }

    private static Dictionary<string, string> Avatar(string password = "pass-word1", string? confirm = null) => new()
    {
        ["FirstName"] = "Test",
        ["LastName"] = "Owner",
        ["Password"] = password,
        ["Confirm"] = confirm ?? password,
        ["Email"] = "",
    };

    private static async Task<JsonElement> ProgressAsync(HttpClient client) =>
        JsonDocument.Parse(await client.GetStringAsync("/api/setup/progress", Ct)).RootElement;

    /// <summary>Polls the progress API until <paramref name="done"/> holds (5 s at most).</summary>
    private static async Task<JsonElement> WaitForProgressAsync(HttpClient client, Func<JsonElement, bool> done)
    {
        for (var i = 0; i < 100; i++)
        {
            var p = await ProgressAsync(client);
            if (done(p)) return p;
            await Task.Delay(50, Ct);
        }
        throw new TimeoutException("The setup progress never reached the expected state.");
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    [Fact]
    public async Task Checklist_lands_on_first_unfinished_step()
    {
        await using (var f = Fresh())
        {
            using var client = await SetupClientAsync(f);
            using var resp = await client.GetAsync("/setup", Ct);
            Assert.Equal("/setup/world", Location(resp));

            var page = new HtmlParser().ParseDocument(await client.GetStringAsync("/setup/world", Ct));
            var steps = page.QuerySelectorAll(".checklist li");
            Assert.Equal(["Choose your plan (done)", "Your network (done)", "Name your world", "Create your avatar", "Choose your first region", "Connect with Firestorm"],
                steps.Select(li => li.TextContent.Trim()));
            Assert.Contains("current", steps[2].ClassName);
            Assert.Equal(2, steps.Count(li => li.ClassList.Contains("done")));
        }

        await using (var f = Fresh(s => s with { WorldNamed = true }))
        {
            using var client = await SetupClientAsync(f);
            using var resp = await client.GetAsync("/setup", Ct);
            Assert.Equal("/setup/avatar", Location(resp));

            var page = new HtmlParser().ParseDocument(await client.GetStringAsync("/setup/avatar", Ct));
            var steps = page.QuerySelectorAll(".checklist li");
            Assert.Contains("done", steps[2].ClassName);
            Assert.Contains("current", steps[3].ClassName);
        }

        await using (var f = AfterOwner(regionCreated: true))
        {
            using var client = await SignedInClientAsync(f);
            using var resp = await client.GetAsync("/setup", Ct);
            Assert.Equal("/setup/connect", Location(resp));
        }
    }

    [Fact]
    public async Task Later_steps_send_back_to_the_first_unfinished_one()
    {
        await using var f = Fresh();
        using var client = await SetupClientAsync(f);

        foreach (var step in new[] { "/setup/avatar", "/setup/region", "/setup/connect" })
        {
            using var resp = await client.GetAsync(step, Ct);
            Assert.Equal("/setup", Location(resp));
        }

        // Before a plan is chosen, every later step (the network one too) sends back as well.
        await using var unplanned = Unplanned();
        using var early = await SetupClientAsync(unplanned);
        foreach (var step in new[] { "/setup/network", "/setup/world", "/setup/avatar" })
        {
            using var resp = await early.GetAsync(step, Ct);
            Assert.Equal("/setup", Location(resp));
        }
    }

    [Fact]
    public async Task Setup_link_lands_on_the_plan_cards()
    {
        await using var f = Unplanned();
        using var client = f.CreateClient(new() { AllowAutoRedirect = false });

        using (var link = await client.GetAsync("/setup?token=" + Token, Ct))
            Assert.Equal("/setup", Location(link));
        using (var resp = await client.GetAsync("/setup", Ct))
            Assert.Equal("/setup/plan", Location(resp));

        var page = new HtmlParser().ParseDocument(await client.GetStringAsync("/setup/plan", Ct));
        Assert.Contains("What will this computer be?", page.QuerySelector("h1")?.TextContent);
        var cards = page.QuerySelectorAll(".plan-card");
        Assert.Equal(PlanCatalog.All.Select(p => p.Id), cards.Select(c => c.GetAttribute("data-plan")));
        foreach (var (card, plan) in cards.Zip(PlanCatalog.All))
        {
            Assert.NotNull(card.QuerySelector($"a[href='/setup/plan/{plan.Id}']"));
            Assert.Contains(plan.Title, card.TextContent);
        }
        Assert.Contains("current", page.QuerySelectorAll(".checklist li")[0].ClassName);
    }

    [Fact]
    public async Task Plan_info_page_shows_diagram_advisories_and_change_rules()
    {
        await using var f = Unplanned();
        using var client = await SetupClientAsync(f);

        var page = new HtmlParser().ParseDocument(await client.GetStringAsync("/setup/plan/grid-asset", Ct));

        Assert.NotNull(page.QuerySelector("svg"));
        Assert.Contains("isn't advised", page.Body!.TextContent);
        foreach (var advisory in PlanCatalog.Get(OwnerPlan.GridAsset).Advisories)
            Assert.Contains(page.QuerySelectorAll(".plan-advisories li"), li => li.TextContent.Trim() == advisory);
        var changes = page.QuerySelectorAll(".plan-changes li");
        Assert.Contains(changes, li => li.TextContent.Contains('✘') && li.TextContent.Contains("Region Server"));
        Assert.Contains(changes, li => li.TextContent.Contains('✔') && li.TextContent.Contains("Local"));
        Assert.NotNull(page.QuerySelector("form[action='/setup/plan/grid-asset?handler=Choose']"));
        Assert.NotNull(page.QuerySelector("a[href='/setup/plan']"));

        using var unknown = await client.GetAsync("/setup/plan/nope", Ct);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Every_plan_page_offers_to_choose_it()
    {
        await using var f = Unplanned();
        using var client = await SetupClientAsync(f);

        foreach (var plan in PlanCatalog.All)
        {
            var page = new HtmlParser().ParseDocument(await client.GetStringAsync("/setup/plan/" + plan.Id, Ct));
            Assert.NotNull(page.QuerySelector($"form[action='/setup/plan/{plan.Id}?handler=Choose']"));
            Assert.DoesNotContain("Coming in a later DreamGrid update.", page.Body!.TextContent);
        }
        var cards = new HtmlParser().ParseDocument(await client.GetStringAsync("/setup/plan", Ct));
        Assert.Empty(cards.QuerySelectorAll(".plan-card.unavailable"));
    }

    [Fact]
    public async Task Every_plan_page_has_a_diagram_with_this_computer_highlighted()
    {
        await using var f = Unplanned();
        using var client = await SetupClientAsync(f);

        foreach (var plan in PlanCatalog.All)
        {
            var page = new HtmlParser().ParseDocument(await client.GetStringAsync("/setup/plan/" + plan.Id, Ct));
            var here = page.QuerySelector("svg .node.here");
            Assert.True(here is not null, plan.Id);
            Assert.Contains("This computer", here.TextContent);
            Assert.DoesNotContain("Robust", page.QuerySelector("svg")!.TextContent);
        }
    }

    [Fact]
    public async Task Choosing_local_saves_the_plan_and_moves_to_network()
    {
        await using var f = Unplanned();
        using var client = await SetupClientAsync(f);

        using var resp = await PostFormAsync(client, "/setup/plan/local?handler=Choose", new());

        Assert.Equal("/setup", Location(resp));
        Assert.Equal(OwnerPlan.Local, f.Store.LoadOrCreate().Plan);
        Assert.True(f.Grid.UpdateWriteConfig.Last());
        using var next = await client.GetAsync("/setup", Ct);
        Assert.Equal("/setup/network", Location(next));
    }

    [Fact]
    public async Task Cards_page_continue_chooses_the_picked_plan()
    {
        await using var f = Unplanned();
        using var client = await SetupClientAsync(f);

        using var resp = await PostFormAsync(client, "/setup/plan", new() { ["Id"] = "simple-public" });

        Assert.Equal("/setup", Location(resp));
        Assert.Equal(OwnerPlan.SimplePublic, f.Store.LoadOrCreate().Plan);
    }

    [Fact]
    public async Task Choosing_an_unknown_plan_is_refused()
    {
        await using var f = Unplanned();
        using var client = await SetupClientAsync(f);

        foreach (var id in new[] { "nope" })
        {
            using var resp = await PostFormAsync(client, $"/setup/plan/{id}?handler=Choose", new(), tokenFrom: "/setup/plan");
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            using var card = await PostFormAsync(client, "/setup/plan", new() { ["Id"] = id });
            Assert.Equal(HttpStatusCode.BadRequest, card.StatusCode);
        }
        Assert.Null(f.Store.LoadOrCreate().Plan);
        Assert.DoesNotContain("UpdateSettings", f.Grid.Snapshot());
    }

    [Fact]
    public async Task Missing_database_shows_why_and_saves_nothing()
    {
        await using var f = Unplanned(mariaDb: MariaDbBinaries.NotFound("MariaDB server not found; searched: X"));
        using var client = await SetupClientAsync(f);

        using var resp = await PostFormAsync(client, "/setup/plan/local?handler=Choose", new());

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var box = new HtmlParser().ParseDocument(await resp.Content.ReadAsStringAsync(Ct)).QuerySelector(".error-box")?.TextContent;
        Assert.Contains("Run the DreamGrid installer again", box);
        Assert.Contains("administrator", box);
        Assert.Contains("searched: X", box);
        Assert.Null(f.Store.LoadOrCreate().Plan);
    }

    [Fact]
    public async Task Half_done_v7_setup_resumes_at_the_cards_with_world_name_kept()
    {
        await using var f = Unplanned(s => s with { GridName = "Avalon", Setup = new SetupState(WorldNamed: true) });
        using var client = await SetupClientAsync(f);

        using (var resp = await client.GetAsync("/setup", Ct))
            Assert.Equal("/setup/plan", Location(resp));
        using (var resp = await PostFormAsync(client, "/setup/plan/local?handler=Choose", new()))
            Assert.Equal("/setup", Location(resp));
        using (var resp = await PostFormAsync(client, "/setup/network", new()))
            Assert.Equal("/setup", Location(resp));
        using (var resp = await client.GetAsync("/setup", Ct))
            Assert.Equal("/setup/avatar", Location(resp));

        var s = f.Store.LoadOrCreate();
        Assert.Equal("Avalon", s.GridName);
        Assert.True(s.Setup.NetworkSet);
    }

    [Fact]
    public async Task World_step_validates_name_and_saves()
    {
        await using var f = Fresh();
        using var client = await SetupClientAsync(f);

        foreach (var bad in new[] { "", "My \"World\"", "Two\nlines", new string('a', 65) })
        {
            using var resp = await PostFormAsync(client, "/setup/world", new() { ["GridName"] = bad });
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var page = new HtmlParser().ParseDocument(await resp.Content.ReadAsStringAsync(Ct));
            Assert.False(string.IsNullOrWhiteSpace(page.QuerySelector("[data-valmsg-for=GridName]")?.TextContent), $"no field error for '{bad}'");
        }
        Assert.False(f.Store.LoadOrCreate().Setup.WorldNamed);

        using (var resp = await PostFormAsync(client, "/setup/world", new() { ["GridName"] = "Avalon" }))
            Assert.Equal("/setup", Location(resp));

        var s = f.Store.LoadOrCreate();
        Assert.Equal("Avalon", s.GridName);
        Assert.True(s.Setup.WorldNamed);
        // GridName goes into the Robust and region INIs.
        Assert.True(f.Grid.UpdateWriteConfig.Last());
    }

    [Fact]
    public async Task World_step_shows_the_grid_name_message_for_quotes()
    {
        await using var f = Fresh();
        using var client = await SetupClientAsync(f);

        using var resp = await PostFormAsync(client, "/setup/world", new() { ["GridName"] = "My \"World\"" });

        var page = new HtmlParser().ParseDocument(await resp.Content.ReadAsStringAsync(Ct));
        Assert.Equal("Use 1 to 64 letters, digits, spaces or punctuation, without quotes or the symbols ; # $ [ ] { } = \\",
            page.QuerySelector("[data-valmsg-for=GridName]")?.TextContent.Trim());
        Assert.False(f.Store.LoadOrCreate().Setup.WorldNamed);
    }

    [Fact]
    public async Task World_step_starts_the_grid_only_when_it_is_stopped()
    {
        await using var f = Fresh();
        using var client = await SetupClientAsync(f);
        await f.Grid.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct); // the host's own start at launch
        int Starts() => f.Grid.Snapshot().Count(c => c == "StartAsync");
        var before = Starts();

        // Running (the fake's default): nothing to warm up.
        (await PostFormAsync(client, "/setup/world", new() { ["GridName"] = "Avalon" })).Dispose();
        await Task.Delay(200, Ct);
        Assert.Equal(before, Starts());

        var stopped = new ComponentStatus("database", ProcessState.Stopped, null, null, null, null);
        f.Grid.Status = new GridStatus(stopped, stopped with { Name = "robust" }, [], [], false, "http://127.0.0.1:8002");
        (await PostFormAsync(client, "/setup/world", new() { ["GridName"] = "Avalon" })).Dispose();
        for (var i = 0; i < 100 && Starts() == before; i++) await Task.Delay(50, Ct);
        Assert.Equal(before + 1, Starts());
    }

    [Fact]
    public async Task Avatar_step_rejects_password_with_space_and_mismatch()
    {
        await using var f = Fresh(s => s with { WorldNamed = true });
        using var client = await SetupClientAsync(f);

        foreach (var (password, confirm, field) in new[]
                 {
                     ("pass word1", "pass word1", "Password"),
                     ("short1", "short1", "Password"),
                     ("pass-word1", "pass-word2", "Confirm"),
                 })
        {
            using var resp = await PostFormAsync(client, "/setup/avatar", Avatar(password, confirm));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var page = new HtmlParser().ParseDocument(await resp.Content.ReadAsStringAsync(Ct));
            Assert.False(string.IsNullOrWhiteSpace(page.QuerySelector($"[data-valmsg-for={field}]")?.TextContent), $"no {field} error for '{password}'/'{confirm}'");
        }

        // Names are letters and digits only (the viewer's rule).
        foreach (var (first, last) in new[] { ("T", "Owner"), ("Te st", "Owner"), ("Test", "O.wner"), ("Test", new string('a', 32)) })
        {
            var form = Avatar();
            form["FirstName"] = first;
            form["LastName"] = last;
            using var resp = await PostFormAsync(client, "/setup/avatar", form);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }

        Assert.Equal(0, f.Grid.CreateOwnerCalls);
        Assert.Null(f.Store.LoadOrCreate().Setup.Owner);
    }

    [Fact]
    public async Task Avatar_step_creates_owner_spends_token_and_signs_in()
    {
        await using var f = Fresh(s => s with { WorldNamed = true });
        using var client = await SetupClientAsync(f);

        using var resp = await PostFormAsync(client, "/setup/avatar", Avatar("Secret-pass42"));

        Assert.Equal("/setup", Location(resp));
        Assert.Null(f.Store.LoadOrCreate().SetupToken);
        Assert.Equal(("Test", "Owner", "Secret-pass42", (string?)null), f.Grid.CreatedOwner);
        Assert.Contains(resp.Headers.GetValues("Set-Cookie"), c => c.StartsWith("dg-auth=", StringComparison.Ordinal));
        // The setup cookie is no use any more.
        Assert.Contains(resp.Headers.GetValues("Set-Cookie"), c => c.StartsWith("dg-setup=;", StringComparison.Ordinal));

        // Signed in: the gate now lets this client on to step 3.
        using var next = await client.GetAsync("/setup", Ct);
        Assert.Equal("/setup/region", Location(next));

        Assert.DoesNotContain(f.Logs, l => l.Message.Contains("Secret-pass42"));
        Assert.DoesNotContain(f.Logs, l => l.Message.Contains(Token) && !l.Message.StartsWith("Setup link:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Avatar_step_shows_a_plain_error_when_the_grid_cannot_create_the_owner()
    {
        await using var f = Fresh(s => s with { WorldNamed = true });
        f.Grid.OwnerError = new InvalidOperationException("Robust did not create Test Owner within 30 s.");
        using var client = await SetupClientAsync(f);

        using var resp = await PostFormAsync(client, "/setup/avatar", Avatar());

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("could not be created", await resp.Content.ReadAsStringAsync(Ct));
        Assert.Equal(Token, f.Store.LoadOrCreate().SetupToken); // not spent: the step can be tried again
    }

    [Fact]
    public async Task Avatar_double_submit_creates_owner_once()
    {
        await using var f = Fresh(s => s with { WorldNamed = true });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Grid.OnCreateOwner = async _ =>
        {
            entered.TrySetResult();
            await release.Task;
        };
        using var client = await SetupClientAsync(f);
        var page = await client.GetStringAsync("/setup/avatar", Ct);
        var token = new HtmlParser().ParseDocument(page).QuerySelector("input[name=__RequestVerificationToken]")!.GetAttribute("value")!;
        var form = new Dictionary<string, string>(Avatar()) { ["__RequestVerificationToken"] = token };

        var first = client.PostAsync("/setup/avatar", new FormUrlEncodedContent(form), Ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        // The second submit (a double click) comes in while the first is still creating the avatar.
        using (var second = await client.PostAsync("/setup/avatar", new FormUrlEncodedContent(form), Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct))
        {
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            Assert.Contains("Creating your avatar…", await second.Content.ReadAsStringAsync(Ct));
        }

        release.SetResult();
        using var done = await first.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal("/setup", Location(done));
        Assert.Equal(1, f.Grid.CreateOwnerCalls);
    }

    [Fact]
    public async Task Resume_after_restart_skips_completed_steps()
    {
        await using var f = AfterOwner();
        using var client = await SignedInClientAsync(f);

        using var setup = await client.GetAsync("/setup", Ct);
        using var avatar = await client.GetAsync("/setup/avatar", Ct);

        Assert.Equal("/setup/region", Location(setup));
        Assert.Equal("/setup", Location(avatar));
        Assert.Equal(0, f.Grid.CreateOwnerCalls);
    }

    private static LauncherFactory AfterOwnerWith(OwnerPlan plan) => new(s => s with
    {
        GridName = "Avalon",
        SetupToken = null,
        Plan = plan,
        Setup = new SetupState(WorldNamed: true, OwnerCreated: true,
            Owner: new OwnerInfo("Test", "Owner", Guid.NewGuid()), NetworkSet: true),
    })
    {
        Before = TestAuth.Apply,
    };

    [Fact]
    public async Task Grid_asset_region_step_can_be_skipped()
    {
        await using var f = AfterOwnerWith(OwnerPlan.GridAsset);
        using var client = await SignedInClientAsync(f);

        var page = new HtmlParser().ParseDocument(await client.GetStringAsync("/setup/region", Ct));
        Assert.Contains("Skip: no regions on this computer", page.Body!.TextContent);
        Assert.Contains("isn't advised", page.Body.TextContent);
        Assert.NotNull(page.QuerySelector("main > form input[name=RegionName]")); // the create form, not Skip's, is main's own

        using var skip = await PostFormAsync(client, "/setup/region?handler=Skip", new(), tokenFrom: "/setup/region");
        Assert.Equal("/setup/connect", Location(skip));
        Assert.True(f.Store.LoadOrCreate().Setup.RegionSkipped);
        using var setup = await client.GetAsync("/setup", Ct);
        Assert.Equal("/setup/connect", Location(setup));
    }

    [Fact]
    public async Task Connect_step_after_skipping_the_region_says_visitors_have_nowhere_to_arrive()
    {
        await using var f = AfterOwnerWith(OwnerPlan.GridAsset);
        using var client = await SignedInClientAsync(f);
        (await PostFormAsync(client, "/setup/region?handler=Skip", new(), tokenFrom: "/setup/region")).Dispose();

        var text = new HtmlParser().ParseDocument(await client.GetStringAsync("/setup/connect", Ct)).Body!.TextContent;

        Assert.Contains("nowhere to arrive", text);
        Assert.Contains("Region Server", text);
    }

    [Fact]
    public async Task Connect_step_with_a_region_does_not_say_visitors_have_nowhere_to_arrive()
    {
        await using var f = AfterOwner(regionCreated: true);
        using var client = await SignedInClientAsync(f);

        Assert.DoesNotContain("nowhere to arrive", await client.GetStringAsync("/setup/connect", Ct));
    }

    [Theory]
    [InlineData(OwnerPlan.Local)]
    [InlineData(OwnerPlan.SimplePublic)]
    public async Task Region_step_has_no_skip_for_other_plans(OwnerPlan plan)
    {
        await using var f = AfterOwnerWith(plan);
        using var client = await SignedInClientAsync(f);

        var html = await client.GetStringAsync("/setup/region", Ct);
        Assert.DoesNotContain("Skip: no regions", html);

        using var skip = await PostFormAsync(client, "/setup/region?handler=Skip", new(), tokenFrom: "/setup/region");
        Assert.False(f.Store.LoadOrCreate().Setup.RegionSkipped);
    }

    [Fact]
    public async Task Starter_option_hidden_without_starter_oar_shown_with_it()
    {
        await using var f = AfterOwner();
        using var client = await SignedInClientAsync(f);

        var without = new HtmlParser().ParseDocument(await client.GetStringAsync("/setup/region", Ct));
        Assert.Null(without.QuerySelector("input[name=Content][value=starter]"));
        Assert.NotNull(without.QuerySelector("input[name=Content][value=island][checked]"));
        Assert.Equal("Welcome", without.QuerySelector("input[name=RegionName]")?.GetAttribute("value"));

        var oar = Path.Combine(Path.GetDirectoryName(f.Paths.OpenSimProgram)!, "content", "starter-region.oar");
        Directory.CreateDirectory(Path.GetDirectoryName(oar)!);
        await File.WriteAllBytesAsync(oar, [1], Ct);

        var with = new HtmlParser().ParseDocument(await client.GetStringAsync("/setup/region", Ct));
        Assert.NotNull(with.QuerySelector("input[name=Content][value=starter][checked]"));
        Assert.NotNull(with.QuerySelector("input[name=Content][value=island]"));
        Assert.Contains("Starter island (trees, greenery and an info kiosk)", with.Body!.TextContent);
    }

    [Fact]
    public async Task Region_step_validates_the_name_like_the_controller()
    {
        await using var f = AfterOwner();
        using var client = await SignedInClientAsync(f);

        foreach (var bad in new[] { "", "A", "../evil", " Welcome", new string('a', 33) })
        {
            Assert.False(GridController.IsValidRegionName(bad));
            using var resp = await PostFormAsync(client, "/setup/region", new() { ["RegionName"] = bad, ["Content"] = "island" });
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var page = new HtmlParser().ParseDocument(await resp.Content.ReadAsStringAsync(Ct));
            Assert.False(string.IsNullOrWhiteSpace(page.QuerySelector("[data-valmsg-for=RegionName]")?.TextContent), $"no field error for '{bad}'");
        }
        Assert.DoesNotContain(f.Grid.Snapshot(), c => c.StartsWith("CreateRegion", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Region_step_runs_in_the_background_once_and_reports_ready()
    {
        await using var f = AfterOwner();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var defaultStages = f.Grid.OnCreateRegion;
        f.Grid.OnCreateRegion = async (progress, ct) =>
        {
            progress?.Report("Creating your region");
            await release.Task.WaitAsync(ct);
            await defaultStages(progress, ct);
        };
        using var client = await SignedInClientAsync(f);

        using (var resp = await PostFormAsync(client, "/setup/region", new() { ["RegionName"] = "Dréam Isle", ["Content"] = "island" }))
            Assert.Equal("/setup/region?working=1", Location(resp));

        var working = await WaitForProgressAsync(client, p => Str(p, "stage") == "Creating your region");
        Assert.True(working.GetProperty("running").GetBoolean());
        var page = new HtmlParser().ParseDocument(await client.GetStringAsync("/setup/region?working=1", Ct));
        Assert.Equal("3", page.QuerySelector("meta[http-equiv=refresh]")?.GetAttribute("content"));
        Assert.NotNull(page.QuerySelector("progress"));
        Assert.Contains("Creating your region", page.Body!.TextContent);

        // A second post while the first is still running (e.g. from another tab) only shows the progress again.
        Assert.Null(page.QuerySelector("form input[name=RegionName]"));
        using (var again = await PostFormAsync(client, "/setup/region", new() { ["RegionName"] = "Other", ["Content"] = "island" }, tokenFrom: "/setup/world"))
            Assert.Equal("/setup/region?working=1", Location(again));

        release.SetResult();
        var done = await WaitForProgressAsync(client, p => !p.GetProperty("running").GetBoolean());
        Assert.Equal("Ready", Str(done, "stage"));
        Assert.Equal(100, done.GetProperty("percent").GetInt32());
        Assert.Null(Str(done, "error"));
        Assert.Single(f.Grid.Snapshot(), c => c.StartsWith("CreateRegion", StringComparison.Ordinal));
        Assert.Contains("CreateRegion Dréam Isle island", f.Grid.Snapshot());
        Assert.True(f.Store.LoadOrCreate().Setup.RegionCreated);

        var ready = new HtmlParser().ParseDocument(await client.GetStringAsync("/setup/region?working=1", Ct));
        Assert.Null(ready.QuerySelector("meta[http-equiv=refresh]"));
        Assert.NotNull(ready.QuerySelector("a[href='/setup/connect']"));
    }

    [Fact]
    public async Task Region_step_reports_progress_and_error_excerpt()
    {
        await using var f = AfterOwner();
        Directory.CreateDirectory(f.Paths.Logs);
        await File.WriteAllTextAsync(Path.Combine(f.Paths.Logs, "robust.log"), "12:00:00 - [ERROR] robust exploded\n", Ct);
        var defaultStages = f.Grid.OnCreateRegion;
        f.Grid.OnCreateRegion = (progress, _) =>
        {
            progress?.Report("Creating your region");
            throw new InvalidOperationException("The region failed: boom");
        };
        using var client = await SignedInClientAsync(f);

        (await PostFormAsync(client, "/setup/region", new() { ["RegionName"] = "Welcome", ["Content"] = "island" })).Dispose();

        var failed = await WaitForProgressAsync(client, p => !p.GetProperty("running").GetBoolean() && Str(p, "error") is not null);
        Assert.False(string.IsNullOrWhiteSpace(Str(failed, "error")));
        Assert.Contains("robust exploded", Str(failed, "logExcerpt"));

        var page = new HtmlParser().ParseDocument(await client.GetStringAsync("/setup/region?working=1", Ct));
        Assert.Null(page.QuerySelector("meta[http-equiv=refresh]"));
        Assert.Contains("robust exploded", page.QuerySelector("details")?.TextContent);
        Assert.Contains("Show details", page.QuerySelector("details summary")?.TextContent);
        Assert.False(f.Store.LoadOrCreate().Setup.RegionCreated);

        // The step can be tried again.
        f.Grid.OnCreateRegion = defaultStages;
        using (var retry = await PostFormAsync(client, "/setup/region", new() { ["RegionName"] = "Welcome", ["Content"] = "island" }))
            Assert.Equal("/setup/region?working=1", Location(retry));
        var done = await WaitForProgressAsync(client, p => !p.GetProperty("running").GetBoolean() && Str(p, "stage") == "Ready");
        Assert.Null(Str(done, "error"));
        Assert.True(f.Store.LoadOrCreate().Setup.RegionCreated);
    }

    [Fact]
    public async Task Region_failure_headline_is_plain_and_the_message_is_in_the_details()
    {
        await using var f = AfterOwner();
        f.Grid.OnCreateRegion = (progress, _) =>
        {
            progress?.Report("Creating your region");
            throw new InvalidOperationException("Robust is not running; start it first.");
        };
        using var client = await SignedInClientAsync(f);

        (await PostFormAsync(client, "/setup/region", new() { ["RegionName"] = "Welcome", ["Content"] = "island" })).Dispose();

        var failed = await WaitForProgressAsync(client, p => !p.GetProperty("running").GetBoolean() && Str(p, "error") is not null);
        Assert.DoesNotContain("Robust", Str(failed, "error"));
        Assert.Contains("Creating your region", Str(failed, "error"));
        Assert.StartsWith("Robust is not running; start it first.", Str(failed, "logExcerpt"));

        var page = new HtmlParser().ParseDocument(await client.GetStringAsync("/setup/region?working=1", Ct));
        var box = page.QuerySelector(".error-box")!;
        Assert.DoesNotContain(box.QuerySelectorAll("p"), p => p.TextContent.Contains("Robust"));
        Assert.Contains("Robust is not running; start it first.", box.QuerySelector("details")?.TextContent);
    }

    [Fact]
    public async Task Region_failure_is_reported_even_when_the_log_cannot_be_read()
    {
        await using var f = AfterOwner();
        // A directory where robust.log should be: opening it for reading throws (not "file not found").
        Directory.CreateDirectory(Path.Combine(f.Paths.Logs, "robust.log"));
        var defaultStages = f.Grid.OnCreateRegion;
        f.Grid.OnCreateRegion = (progress, _) =>
        {
            progress?.Report("Creating your region");
            throw new InvalidOperationException("The region failed: boom");
        };
        using var client = await SignedInClientAsync(f);

        (await PostFormAsync(client, "/setup/region", new() { ["RegionName"] = "Welcome", ["Content"] = "island" })).Dispose();

        var failed = await WaitForProgressAsync(client, p => !p.GetProperty("running").GetBoolean() && Str(p, "error") is not null);
        Assert.Contains("The region failed: boom", Str(failed, "logExcerpt"));
        Assert.True(await f.WaitForLogAsync(l => l.Message.Contains("robust log could not be read")), "the unreadable log was not hit");

        f.Grid.OnCreateRegion = defaultStages;
        using (var retry = await PostFormAsync(client, "/setup/region", new() { ["RegionName"] = "Welcome", ["Content"] = "island" }))
            Assert.Equal("/setup/region?working=1", Location(retry));
        var done = await WaitForProgressAsync(client, p => !p.GetProperty("running").GetBoolean() && Str(p, "stage") == "Ready");
        Assert.Null(Str(done, "error"));
        Assert.True(f.Store.LoadOrCreate().Setup.RegionCreated);
    }

    [Fact]
    public async Task Region_step_starts_the_database_and_grid_service_when_they_are_stopped()
    {
        await using var f = AfterOwner();
        var stopped = new ComponentStatus("database", ProcessState.Stopped, null, null, null, null);
        f.Grid.Status = new GridStatus(stopped, stopped with { Name = "robust" }, [], [], false, "http://127.0.0.1:8002");
        using var client = await SignedInClientAsync(f);

        (await PostFormAsync(client, "/setup/region", new() { ["RegionName"] = "Welcome", ["Content"] = "island" })).Dispose();

        var done = await WaitForProgressAsync(client, p => !p.GetProperty("running").GetBoolean());
        Assert.Equal("Ready", Str(done, "stage"));
        var calls = f.Grid.Snapshot();
        var db = Array.IndexOf(calls, "Start database");
        var robust = Array.IndexOf(calls, "Start robust");
        var region = Array.FindIndex(calls, c => c.StartsWith("CreateRegion", StringComparison.Ordinal));
        Assert.True(db >= 0 && db < robust && robust < region, string.Join(", ", calls));
    }

    [Fact]
    public async Task Region_step_restarts_a_grid_service_running_with_an_old_world_name()
    {
        // The world was named while the first start was bringing Robust up: Robust still says the old name.
        await using var f = AfterOwner();
        var running = new ComponentStatus("database", ProcessState.Running, null, null, null, null);
        f.Grid.Status = new GridStatus(running, running with { Name = "robust" }, [], [], false, "http://127.0.0.1:8002",
            RobustOutdated: true);
        using var client = await SignedInClientAsync(f);

        (await PostFormAsync(client, "/setup/region", new() { ["RegionName"] = "Welcome", ["Content"] = "island" })).Dispose();

        var done = await WaitForProgressAsync(client, p => !p.GetProperty("running").GetBoolean());
        Assert.Equal("Ready", Str(done, "stage"));
        var calls = f.Grid.Snapshot();
        var restart = Array.IndexOf(calls, "Restart robust");
        var region = Array.FindIndex(calls, c => c.StartsWith("CreateRegion", StringComparison.Ordinal));
        Assert.True(restart >= 0 && restart < region, string.Join(", ", calls));
        Assert.DoesNotContain("Start database", calls);
    }

    [Fact]
    public async Task Region_step_leaves_a_current_grid_service_running()
    {
        await using var f = AfterOwner();
        var running = new ComponentStatus("database", ProcessState.Running, null, null, null, null);
        f.Grid.Status = new GridStatus(running, running with { Name = "robust" }, [], [], false, "http://127.0.0.1:8002");
        using var client = await SignedInClientAsync(f);

        (await PostFormAsync(client, "/setup/region", new() { ["RegionName"] = "Welcome", ["Content"] = "island" })).Dispose();

        var done = await WaitForProgressAsync(client, p => !p.GetProperty("running").GetBoolean());
        Assert.Equal("Ready", Str(done, "stage"));
        Assert.DoesNotContain(f.Grid.Snapshot(), c => c.EndsWith(" robust", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Progress_api_is_not_served_anonymously_after_the_owner_exists()
    {
        await using var f = AfterOwner();
        using var anonymous = f.CreateClient(new() { AllowAutoRedirect = false });
        using var resp = await anonymous.GetAsync("/api/setup/progress", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.DoesNotContain("stage", await resp.Content.ReadAsStringAsync(Ct));

        using var admin = await SignedInClientAsync(f);
        var json = await ProgressAsync(admin);
        Assert.True(json.TryGetProperty("percent", out _));
    }

    [Fact]
    public async Task Connect_step_shows_login_uri_and_finishes_setup()
    {
        await using var f = AfterOwner(regionCreated: true);
        using var client = await SignedInClientAsync(f);

        var html = await client.GetStringAsync("/setup/connect", Ct);
        var page = new HtmlParser().ParseDocument(html);
        Assert.Contains("http://127.0.0.1:8002", page.QuerySelector("#login-uri")?.TextContent);
        Assert.Contains("Pick Avalon on the login screen", page.Body!.TextContent);
        Assert.Contains("Log in as Test Owner", page.Body!.TextContent);
        Assert.Contains("Install Firestorm", page.Body!.TextContent);
        Assert.Contains("https://www.firestormviewer.org/", page.Body!.TextContent);

        using var resp = await PostFormAsync(client, "/setup/connect", new());

        Assert.Equal("/", Location(resp));
        Assert.True(f.Store.LoadOrCreate().Setup.Finished);
    }

    [Fact]
    public async Task Setup_resumes_after_the_sign_in_cookie_is_gone()
    {
        await using var f = AfterOwner();
        f.Robust.Add("Test", "Owner", 200, "pass-word1");
        // A new browser session: no sign-in cookie, no setup cookie, the old setup link.
        using var client = f.CreateClient(new() { AllowAutoRedirect = false });

        using var link = await client.GetAsync("/setup?token=" + Token, Ct);
        Assert.Equal("/login?ReturnUrl=%2Fsetup", Location(link));

        using var signIn = await PostFormAsync(client, "/login?ReturnUrl=%2Fsetup", new() { ["Name"] = "Test Owner", ["Password"] = "pass-word1" });
        Assert.Equal("/setup", Location(signIn));

        using var next = await client.GetAsync("/setup", Ct);
        Assert.Equal("/setup/region", Location(next));
    }

    private static readonly ComponentStatus StoppedDb = new("database", ProcessState.Stopped, null, null, null, null);

    [Fact]
    public async Task Avatar_step_names_the_taken_port_when_the_grid_cannot_start()
    {
        await using var f = Fresh(s => s with { WorldNamed = true });
        f.Grid.Status = new GridStatus(StoppedDb, StoppedDb with { Name = "robust" }, [], [], false, "http://127.0.0.1:8002");
        f.Grid.ComponentError = new PortConflictException("Port 8002 (Robust public) is already in use by another program.", 8002);
        using var client = await SetupClientAsync(f);

        using var resp = await PostFormAsync(client, "/setup/avatar", Avatar());

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var box = new HtmlParser().ParseDocument(await resp.Content.ReadAsStringAsync(Ct)).QuerySelector(".error-box > p")!.TextContent;
        Assert.Equal("Port 8002 is already in use by another program, so your world can't start. Close that program, then try again.", box);
        Assert.Equal(0, f.Grid.CreateOwnerCalls);
    }

    [Fact]
    public async Task Avatar_step_names_the_taken_port_from_the_last_failed_start()
    {
        await using var f = Fresh(s => s with { WorldNamed = true });
        f.Grid.Status = new GridStatus(StoppedDb, StoppedDb with { Name = "robust" }, [], [], false, "http://127.0.0.1:8002",
            new StartFailure("Port 8004 (Robust private) is already in use by another program.", 8004));
        f.Grid.OwnerError = new InvalidOperationException("Robust is not running.");
        using var client = await SetupClientAsync(f);

        using var resp = await PostFormAsync(client, "/setup/avatar", Avatar());

        var html = await resp.Content.ReadAsStringAsync(Ct);
        Assert.Contains("Port 8004 is already in use by another program", html);
        Assert.Contains("Close that program, then try again.", html);
        Assert.DoesNotContain("Advanced Settings", html);
    }

    [Fact]
    public async Task Avatar_step_treats_a_timeout_as_a_failure_not_a_crash()
    {
        await using var f = Fresh(s => s with { WorldNamed = true });
        f.Grid.OwnerError = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.");
        using var client = await SetupClientAsync(f);

        using var resp = await PostFormAsync(client, "/setup/avatar", Avatar());

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("could not be created", await resp.Content.ReadAsStringAsync(Ct));
        Assert.Equal(Token, f.Store.LoadOrCreate().SetupToken);
    }

    [Fact]
    public async Task Region_step_names_the_taken_port()
    {
        await using var f = AfterOwner();
        f.Grid.OnCreateRegion = (_, _) => throw new PortConflictException("Port 9000 (Region) is already in use by another program.", 9000);
        using var client = await SignedInClientAsync(f);

        (await PostFormAsync(client, "/setup/region", new() { ["RegionName"] = "Welcome", ["Content"] = "island" })).Dispose();

        var failed = await WaitForProgressAsync(client, p => !p.GetProperty("running").GetBoolean() && Str(p, "error") is not null);
        Assert.Equal("Port 9000 is already in use by another program, so your world can't start. Close that program, then try again.", Str(failed, "error"));
    }

    [Fact]
    public async Task Region_step_shows_ready_when_the_region_already_exists()
    {
        // After a restart the progress is empty, but the region was created before it.
        await using var f = AfterOwner(regionCreated: true);
        using var client = await SignedInClientAsync(f);

        var page = new HtmlParser().ParseDocument(await client.GetStringAsync("/setup/region", Ct));

        Assert.Contains("Your region is ready.", page.QuerySelector(".ok-box")?.TextContent);
        Assert.NotNull(page.QuerySelector("a[href='/setup/connect']"));
        Assert.Null(page.QuerySelector("form input[name=RegionName]"));

        using var post = await PostFormAsync(client, "/setup/region", new() { ["RegionName"] = "Other", ["Content"] = "island" }, tokenFrom: "/setup/connect");
        Assert.Equal("/setup/region", Location(post));
        Assert.DoesNotContain(f.Grid.Snapshot(), c => c.StartsWith("CreateRegion", StringComparison.Ordinal));
    }

    private const string PortConflictMessage = "Port 8002 (Robust public) is already in use by another program.";

    /// <summary>The grid is not running and its last start failed.</summary>
    private static void Fail(LauncherFactory f, StartFailure failure)
    {
        f.Grid.RegionState = ProcessState.Stopped;
        f.Grid.StartFailure = failure;
    }

    private static async Task WaitForCallsAsync(LauncherFactory f, int count)
    {
        for (var i = 0; i < 100 && f.Grid.Snapshot().Length < count; i++) await Task.Delay(50, Ct);
    }

    [Fact]
    public async Task Setup_shows_port_change_form_on_conflict()
    {
        await using var f = Fresh(s => s with { WorldNamed = true });
        Fail(f, new StartFailure(PortConflictMessage, 8002));
        using var client = await SetupClientAsync(f);

        var html = await client.GetStringAsync("/setup/avatar", Ct);

        var page = new HtmlParser().ParseDocument(html);
        var form = page.QuerySelector("form[action='/setup?handler=Port']");
        Assert.NotNull(form);
        Assert.Equal("post", form.GetAttribute("method"), ignoreCase: true);
        Assert.NotNull(form.QuerySelector("input[name=Port][type=number]"));
        Assert.NotNull(form.QuerySelector("input[name=__RequestVerificationToken]"));
        Assert.Contains("Use port", form.TextContent);
        Assert.Contains("Port 8002 is already in use by another program, so your world can't start.", page.Body!.TextContent);
        Assert.DoesNotContain("Robust", html);
    }

    [Fact]
    public async Task Setup_shows_no_port_form_without_a_conflict()
    {
        await using var f = Fresh(s => s with { WorldNamed = true });
        using var client = await SetupClientAsync(f);

        var page = new HtmlParser().ParseDocument(await client.GetStringAsync("/setup/avatar", Ct));

        Assert.Null(page.QuerySelector("form[action='/setup?handler=Port']"));
    }

    [Fact]
    public async Task Setup_port_change_saves_robust_public_port_and_starts()
    {
        await using var f = Fresh(s => s with { WorldNamed = true });
        Fail(f, new StartFailure(PortConflictMessage, 8002));
        using var client = await SetupClientAsync(f);
        await f.Grid.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct); // the launcher's own start at boot
        var before = f.Grid.Snapshot().Length;

        using var resp = await PostFormAsync(client, "/setup?handler=Port", new() { ["Port"] = "8102" }, tokenFrom: "/setup/avatar");

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Equal("/setup/avatar", Location(resp));
        await WaitForCallsAsync(f, before + 2);
        var saved = f.Store.LoadOrCreate();
        Assert.Equal(8102, saved.RobustPublicPort);
        Assert.Equal(8003, saved.RobustPrivatePort);
        Assert.Equal(["UpdateSettings", "StartAsync"], f.Grid.Snapshot()[before..]);
        Assert.True(f.Grid.UpdateWriteConfig[^1]);
    }

    [Fact]
    public async Task Setup_port_change_rejects_duplicate_port()
    {
        await using var f = Fresh(s => s with { WorldNamed = true });
        Fail(f, new StartFailure(PortConflictMessage, 8002));
        using var client = await SetupClientAsync(f);
        await f.Grid.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var before = f.Grid.Snapshot().Length;

        using var resp = await PostFormAsync(client, "/setup?handler=Port", new() { ["Port"] = "8003" }, tokenFrom: "/setup/avatar");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var page = new HtmlParser().ParseDocument(await resp.Content.ReadAsStringAsync(Ct));
        Assert.Contains("Port 8003 is already used by another part of DreamGrid", page.QuerySelector(".field-error")!.TextContent);
        Assert.Equal(8002, f.Store.LoadOrCreate().RobustPublicPort);
        Assert.Equal(before, f.Grid.Snapshot().Length);
    }

    [Fact]
    public async Task Setup_port_change_rejects_text_and_does_nothing_without_a_conflict()
    {
        await using var f = Fresh(s => s with { WorldNamed = true });
        using var client = await SetupClientAsync(f);

        using var none = await PostFormAsync(client, "/setup?handler=Port", new() { ["Port"] = "8102" }, tokenFrom: "/setup/avatar");
        Fail(f, new StartFailure(PortConflictMessage, 8002));
        using var text = await PostFormAsync(client, "/setup?handler=Port", new() { ["Port"] = "abc" }, tokenFrom: "/setup/avatar");

        Assert.Equal(HttpStatusCode.Redirect, none.StatusCode);
        Assert.Equal(HttpStatusCode.OK, text.StatusCode);
        Assert.Contains("whole number", await text.Content.ReadAsStringAsync(Ct));
        Assert.Equal(8002, f.Store.LoadOrCreate().RobustPublicPort);
        Assert.DoesNotContain("UpdateSettings", f.Grid.Snapshot());
    }

    [Fact]
    public async Task Setup_port_change_needs_the_antiforgery_token()
    {
        await using var f = Fresh(s => s with { WorldNamed = true });
        Fail(f, new StartFailure(PortConflictMessage, 8002));
        using var client = await SetupClientAsync(f);

        using var resp = await client.PostAsync("/setup?handler=Port", new FormUrlEncodedContent(new Dictionary<string, string> { ["Port"] = "8102" }), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(8002, f.Store.LoadOrCreate().RobustPublicPort);
    }

    [Fact]
    public async Task World_step_shows_start_failure()
    {
        await using var f = Fresh();
        Fail(f, new StartFailure("Could not open C:\\secret\\path", null));
        using var client = await SetupClientAsync(f);

        var page = new HtmlParser().ParseDocument(await client.GetStringAsync("/setup/world", Ct));

        Assert.Equal("Your world could not start.", page.QuerySelector(".error-box > p")!.TextContent);
        Assert.Null(page.QuerySelector("form[action='/setup?handler=Port']"));
    }

    [Fact]
    public async Task World_step_names_the_taken_port_and_offers_another()
    {
        await using var f = Fresh();
        Fail(f, new StartFailure(PortConflictMessage, 8002));
        using var client = await SetupClientAsync(f);

        var page = new HtmlParser().ParseDocument(await client.GetStringAsync("/setup/world", Ct));

        Assert.Contains("Port 8002 is already in use by another program", page.Body!.TextContent);
        Assert.NotNull(page.QuerySelector("form[action='/setup?handler=Port'] input[name=Port]"));
    }

    [Fact]
    public async Task Setup_port_fix_moves_a_region_port()
    {
        await using var f = Fresh(s => s with { WorldNamed = true });
        f.Store.Save(f.Store.LoadOrCreate() with
        {
            Programs = [new ProgramSettings("Welcome", 8004, 8011)],
            Regions = [new RegionSettings("Welcome", "Welcome", Guid.NewGuid(), UdpPort: 8005)],
        });
        Fail(f, new StartFailure("Port 8005 (Region Welcome UDP) is already in use by another program.", 8005));
        using var client = await SetupClientAsync(f);
        await f.Grid.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        using var resp = await PostFormAsync(client, "/setup?handler=Port", new() { ["Port"] = "8105" }, tokenFrom: "/setup/avatar");

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        var saved = f.Store.LoadOrCreate();
        Assert.Equal(8105, saved.Regions.Single().UdpPort);
        Assert.Equal(8004, saved.Programs.Single().HttpPort);
        Assert.Equal(8004, saved.RegionPort);
    }

    /// <summary>On the network step under <paramref name="plan"/>: the plan is chosen, nothing after it is.</summary>
    private static LauncherFactory OnNetwork(OwnerPlan plan) => new(s => s with { SetupToken = Token, Plan = plan })
    {
        Before = TestAuth.Apply,
    };

    private const string NotPublic = "That's an address only computers on your network can reach. Enter your public IP address or your grid's name.";

    [Theory]
    [InlineData("192.168.1.20")]
    [InlineData("10.0.0.5")]
    [InlineData("100.64.0.9")]
    [InlineData("127.0.0.1")]
    public async Task Network_step_for_simple_public_refuses_lan_addresses(string address)
    {
        await using var f = OnNetwork(OwnerPlan.SimplePublic);
        using var client = await SetupClientAsync(f);

        using var resp = await PostFormAsync(client, "/setup/network", new() { ["PublicAddress"] = address, ["Hypergrid"] = "on" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("only computers on your network can reach", await resp.Content.ReadAsStringAsync(Ct));
        var page = new HtmlParser().ParseDocument(await resp.Content.ReadAsStringAsync(Ct));
        Assert.Equal(NotPublic, page.QuerySelector(".field-error")!.TextContent.Trim());
        Assert.False(f.Store.LoadOrCreate().Setup.NetworkSet);
    }

    [Fact]
    public async Task Network_step_for_simple_public_requires_an_address()
    {
        await using var f = OnNetwork(OwnerPlan.SimplePublic);
        using var client = await SetupClientAsync(f);

        using var resp = await PostFormAsync(client, "/setup/network", new() { ["PublicAddress"] = "  " });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.False(f.Store.LoadOrCreate().Setup.NetworkSet);
    }

    [Fact]
    public async Task Network_step_for_simple_public_saves_address_and_switch()
    {
        await using var f = OnNetwork(OwnerPlan.SimplePublic);
        using var client = await SetupClientAsync(f);

        using var resp = await PostFormAsync(client, "/setup/network", new() { ["PublicAddress"] = "203.0.113.7", ["Hypergrid"] = "on" });

        Assert.Equal("/setup", Location(resp));
        var s = f.Store.LoadOrCreate();
        Assert.Equal("203.0.113.7", s.Hostname);
        Assert.True(s.Hypergrid);
        Assert.True(s.Setup.NetworkSet);
        Assert.True(f.Grid.UpdateWriteConfig.Last());
    }

    [Fact]
    public async Task Network_step_for_simple_public_without_the_switch_saves_it_off()
    {
        await using var f = OnNetwork(OwnerPlan.SimplePublic);
        using var client = await SetupClientAsync(f);

        using var resp = await PostFormAsync(client, "/setup/network", new() { ["PublicAddress"] = "world.example.com" });

        Assert.Equal("/setup", Location(resp));
        var s = f.Store.LoadOrCreate();
        Assert.Equal("world.example.com", s.Hostname);
        Assert.False(s.Hypergrid);
    }

    [Fact]
    public async Task Network_step_for_grid_asset_lan_forces_hypergrid_off()
    {
        await using var f = OnNetwork(OwnerPlan.GridAsset);
        using var client = await SetupClientAsync(f);

        using var resp = await PostFormAsync(client, "/setup/network",
            new() { ["AddressKind"] = "lan", ["PublicAddress"] = "203.0.113.7", ["Hypergrid"] = "on" });

        Assert.Equal("/setup", Location(resp));
        var s = f.Store.LoadOrCreate();
        Assert.False(s.Hypergrid);
        Assert.Null(s.Hostname);
        Assert.True(s.Setup.NetworkSet);
    }

    [Fact]
    public async Task Network_step_for_grid_asset_public_checks_the_address()
    {
        await using var f = OnNetwork(OwnerPlan.GridAsset);
        using var client = await SetupClientAsync(f);

        using (var bad = await PostFormAsync(client, "/setup/network", new() { ["AddressKind"] = "public", ["PublicAddress"] = "10.1.2.3" }))
        {
            Assert.Equal(HttpStatusCode.OK, bad.StatusCode);
            Assert.Contains("only computers on your network can reach", await bad.Content.ReadAsStringAsync(Ct));
        }
        using var ok = await PostFormAsync(client, "/setup/network",
            new() { ["AddressKind"] = "public", ["PublicAddress"] = "203.0.113.7", ["Hypergrid"] = "on" });
        Assert.Equal("/setup", Location(ok));
        var s = f.Store.LoadOrCreate();
        Assert.Equal("203.0.113.7", s.Hostname);
        Assert.True(s.Hypergrid);
    }

    [Fact]
    public async Task Network_step_for_grid_asset_shows_only_the_advisories_for_the_chosen_address_after_an_error()
    {
        await using var f = OnNetwork(OwnerPlan.GridAsset);
        using var client = await SetupClientAsync(f);

        using var pub = await PostFormAsync(client, "/setup/network", new() { ["AddressKind"] = "public", ["PublicAddress"] = "10.1.2.3" });
        var page = new HtmlParser().ParseDocument(await pub.Content.ReadAsStringAsync(Ct));

        Assert.True(page.QuerySelector("#advisories-lan")!.HasAttribute("hidden"));
        Assert.False(page.QuerySelector("#advisories-public")!.HasAttribute("hidden"));
        Assert.False(page.QuerySelector("#public-part")!.HasAttribute("hidden"));
    }

    [Fact]
    public async Task Network_step_for_local_has_no_switch_and_saves_the_detected_address()
    {
        await using var f = OnNetwork(OwnerPlan.Local);
        using var client = await SetupClientAsync(f);

        var page = new HtmlParser().ParseDocument(await client.GetStringAsync("/setup/network", Ct));
        Assert.Null(page.QuerySelector("input[name=Hypergrid]"));
        Assert.Null(page.QuerySelector("input[name=PublicAddress]"));

        using var resp = await PostFormAsync(client, "/setup/network", new() { ["PublicAddress"] = "203.0.113.7", ["Hypergrid"] = "on" });
        Assert.Equal("/setup", Location(resp));
        var s = f.Store.LoadOrCreate();
        Assert.Null(s.Hostname);
        Assert.False(s.Hypergrid);
        Assert.True(s.Setup.NetworkSet);
    }

    [Fact]
    public async Task Network_step_for_local_keeps_a_custom_network_hostname()
    {
        await using var f = new LauncherFactory(s => s with { SetupToken = Token, Plan = OwnerPlan.Local, Hostname = "192.168.1.50" })
        {
            Before = TestAuth.Apply,
        };
        using var client = await SetupClientAsync(f);

        using var resp = await PostFormAsync(client, "/setup/network", new());

        Assert.Equal("/setup", Location(resp));
        Assert.Equal("192.168.1.50", f.Store.LoadOrCreate().Hostname);
    }

    [Fact]
    public async Task Network_step_for_local_drops_a_public_hostname()
    {
        await using var f = new LauncherFactory(s => s with { SetupToken = Token, Plan = OwnerPlan.Local, Hostname = "world.example.com" })
        {
            Before = TestAuth.Apply,
        };
        using var client = await SetupClientAsync(f);

        using var resp = await PostFormAsync(client, "/setup/network", new());

        Assert.Equal("/setup", Location(resp));
        Assert.Null(f.Store.LoadOrCreate().Hostname);
    }

    [Fact]
    public async Task Router_advice_names_the_forwarded_ports()
    {
        await using var f = new LauncherFactory(s => s with { SetupToken = Token, Plan = OwnerPlan.SimplePublic, RobustPublicPort = 9102, RegionPort = 9204 })
        {
            Before = TestAuth.Apply,
        };
        using var client = await SetupClientAsync(f);

        var page = new HtmlParser().ParseDocument(await client.GetStringAsync("/setup/network", Ct));

        var advice = page.QuerySelector(".router-advice")!.TextContent;
        Assert.Contains("9102", advice);
        Assert.Contains("9204 upward for regions", advice);
        Assert.NotNull(page.QuerySelector("input[name=Hypergrid]"));
    }

    [Fact]
    public async Task Network_step_is_closed_until_the_plan_is_chosen()
    {
        await using var f = Unplanned();
        using var client = await SetupClientAsync(f);

        using var resp = await client.GetAsync("/setup/network", Ct);

        Assert.Equal("/setup", Location(resp));
    }

    [Fact]
    public async Task Fresh_grid_asset_page_has_both_advisory_lists_lan_shown_and_public_hidden_and_can_pick_public()
    {
        await using var f = OnNetwork(OwnerPlan.GridAsset);
        using var client = await SetupClientAsync(f);

        var page = new HtmlParser().ParseDocument(await client.GetStringAsync("/setup/network", Ct));

        var lan = page.QuerySelector("#advisories-lan")!;
        var pub = page.QuerySelector("#advisories-public")!;
        Assert.False(lan.HasAttribute("hidden"));
        Assert.True(pub.HasAttribute("hidden"));
        Assert.Contains("only computers on this network can visit", lan.TextContent);
        Assert.Contains("passwords cross the internet", pub.TextContent);
        Assert.Contains("Your router must forward", pub.TextContent);
        // The public choice and its field are in the form (the script only shows/hides them; without it all parts show).
        Assert.NotNull(page.QuerySelector("input[name=AddressKind][value=public]"));
        Assert.NotNull(page.QuerySelector("#public-part input[name=PublicAddress]"));
        Assert.NotNull(page.QuerySelector("#switch-part input[name=Hypergrid]"));
        Assert.NotNull(page.QuerySelector("noscript link[href=\"/css/noscript.css\"]"));
        Assert.NotNull(page.QuerySelector("script[src=\"/js/network.js\"]"));
    }

    // ---- Region Server (plan 6b) ----

    /// <summary>The Grid Asset Server the Region Server tests join: Avalon, hypergrid off, region ports 8004 to 8006.</summary>
    private static JoinedGrid Avalon(string? version = null) => new("192.168.1.5", 8002, 8003, "Avalon", "http://192.168.1.5:8002/",
        version ?? DreamGrid.Launcher.Core.LauncherVersion.Current, false, 8004, 8006);

    /// <summary>A Region Server before its owner exists; <paramref name="more"/> seeds the rest (join, steps done).</summary>
    private static LauncherFactory RegionServer(Func<LauncherSettings, LauncherSettings>? more = null)
    {
        var f = new LauncherFactory(s =>
        {
            var seeded = s with { SetupToken = Token, Plan = OwnerPlan.RegionServer };
            return more is null ? seeded : more(seeded);
        })
        {
            Before = TestAuth.Apply,
        };
        f.Join.Result = Avalon();
        return f;
    }

    /// <summary>A Region Server that has joined Avalon, with its network set unless <paramref name="networkSet"/> is false.</summary>
    private static LauncherFactory JoinedRegionServer(bool networkSet = true) => RegionServer(s => s with
    {
        Join = Avalon(),
        GridName = "Avalon",
        Setup = s.Setup with { WorldNamed = true, NetworkSet = networkSet },
    });

    /// <summary>A joined Region Server whose owner exists (token spent), at the region step.</summary>
    private static LauncherFactory RegionServerAtRegion() => RegionServer(s => s with
    {
        Join = Avalon(),
        GridName = "Avalon",
        SetupToken = null,
        Setup = new SetupState(WorldNamed: true, OwnerCreated: true, Owner: new OwnerInfo("Ann", "Lee", Guid.NewGuid()), NetworkSet: true),
    });

    [Fact]
    public async Task Region_server_card_can_be_chosen()
    {
        await using var f = Unplanned();
        using var client = await SetupClientAsync(f);

        using var resp = await PostFormAsync(client, "/setup/plan/region-server?handler=Choose", new(), tokenFrom: "/setup/plan");

        Assert.Equal("/setup", Location(resp));
        Assert.Equal(OwnerPlan.RegionServer, f.Store.LoadOrCreate().Plan);
        using var next = await client.GetAsync("/setup", Ct);
        Assert.Equal("/setup/join", Location(next));
    }

    [Fact]
    public async Task Join_shows_what_it_found_then_saves_on_confirm()
    {
        await using var f = RegionServer();
        using var client = await SetupClientAsync(f);

        var form = new HtmlParser().ParseDocument(await client.GetStringAsync("/setup/join", Ct));
        Assert.NotNull(form.QuerySelector("input[name=Address]"));
        Assert.Contains("current", form.QuerySelectorAll(".checklist li")[1].ClassName);

        using var first = await PostFormAsync(client, "/setup/join", new() { ["Address"] = "192.168.1.5" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var page = new HtmlParser().ParseDocument(await first.Content.ReadAsStringAsync(Ct));
        var text = page.Body!.TextContent;
        Assert.Contains("Avalon", text);
        Assert.Contains("Hypergrid: off", text);
        Assert.Contains(page.QuerySelectorAll("button"), b => b.TextContent.Trim() == "Join this grid");
        Assert.Contains(page.QuerySelectorAll("button, a"), b => b.TextContent.Trim() == "Change the address");
        Assert.Null(page.QuerySelector(".version-warning"));
        Assert.Null(f.Store.LoadOrCreate().Join);
        Assert.DoesNotContain("UpdateSettings", f.Grid.Snapshot());
        Assert.Equal(("192.168.1.5", 8002), f.Join.Reads.Single());

        // Confirm reads the file again: posted look-alike values are never trusted.
        using var confirm = await PostFormAsync(client, "/setup/join?handler=Confirm",
            new() { ["Address"] = "192.168.1.5", ["GridName"] = "Forged", ["Hypergrid"] = "true" }, tokenFrom: "/setup/join");
        Assert.Equal("/setup", Location(confirm));
        Assert.Equal(2, f.Join.Reads.Count);
        var s = f.Store.LoadOrCreate();
        Assert.Equal(Avalon(), s.Join);
        Assert.Equal("Avalon", s.GridName);
        Assert.True(s.Setup.WorldNamed);
        Assert.True(f.Grid.UpdateWriteConfig.Last());
        using var next = await client.GetAsync("/setup", Ct);
        Assert.Equal("/setup/network", Location(next));
    }

    [Fact]
    public async Task Join_change_the_address_shows_the_form_again()
    {
        await using var f = RegionServer();
        using var client = await SetupClientAsync(f);

        using var resp = await PostFormAsync(client, "/setup/join?handler=Change", new() { ["Address"] = "192.168.1.5" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var page = new HtmlParser().ParseDocument(await resp.Content.ReadAsStringAsync(Ct));
        Assert.Equal("192.168.1.5", page.QuerySelector("input[name=Address]:not([type=hidden])")?.GetAttribute("value"));
        Assert.Empty(f.Join.Reads);
        Assert.Null(f.Store.LoadOrCreate().Join);
    }

    [Fact]
    public async Task Join_warns_on_a_different_version()
    {
        await using var f = RegionServer();
        f.Join.Result = Avalon("6.9.0");
        using var client = await SetupClientAsync(f);

        using var first = await PostFormAsync(client, "/setup/join", new() { ["Address"] = "192.168.1.5:8002" });
        var page = new HtmlParser().ParseDocument(await first.Content.ReadAsStringAsync(Ct));
        var warning = page.QuerySelector(".version-warning")?.TextContent ?? "";
        Assert.Contains("6.9.0", warning);
        Assert.Contains(DreamGrid.Launcher.Core.LauncherVersion.Current, warning);

        using var confirm = await PostFormAsync(client, "/setup/join?handler=Confirm", new() { ["Address"] = "192.168.1.5:8002" });
        Assert.Equal("/setup", Location(confirm));
        Assert.Equal("6.9.0", f.Store.LoadOrCreate().Join?.Version);
    }

    [Fact]
    public async Task Join_refuses_a_bad_address()
    {
        await using var f = RegionServer();
        using var client = await SetupClientAsync(f);

        foreach (var url in new[] { "/setup/join", "/setup/join?handler=Confirm" })
        {
            using var resp = await PostFormAsync(client, url, new() { ["Address"] = "192.168.1.5:70000" });
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Contains("The port after the colon must be a number from 1 to 65535.", await resp.Content.ReadAsStringAsync(Ct));
        }
        Assert.Empty(f.Join.Reads);
        Assert.Null(f.Store.LoadOrCreate().Join);
    }

    [Fact]
    public async Task Join_shows_why_it_could_not_read()
    {
        await using var f = RegionServer();
        f.Join.Error = new GridJoinException("Nothing answered at 192.168.1.5:8002. Check the address, and that the Grid Asset Server is running.", noAnswer: true);
        using var client = await SetupClientAsync(f);

        foreach (var url in new[] { "/setup/join", "/setup/join?handler=Confirm" })
        {
            using var resp = await PostFormAsync(client, url, new() { ["Address"] = "192.168.1.5" });
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Contains("Nothing answered at 192.168.1.5:8002.", await resp.Content.ReadAsStringAsync(Ct));
        }
        Assert.Null(f.Store.LoadOrCreate().Join);
        Assert.False(f.Store.LoadOrCreate().Setup.WorldNamed);
    }

    [Fact]
    public async Task Region_server_network_suggests_a_port_after_the_grid_asset_servers()
    {
        await using var f = JoinedRegionServer(networkSet: false);
        using var client = await SetupClientAsync(f);

        var page = new HtmlParser().ParseDocument(await client.GetStringAsync("/setup/network", Ct));
        Assert.Equal("8007", page.QuerySelector("input[name=FirstRegionPort]")?.GetAttribute("value"));
        Assert.NotNull(page.QuerySelector("input[name=AddressKind][value=lan]"));
        Assert.NotNull(page.QuerySelector("input[name=AddressKind][value=public]"));

        foreach (var port in new[] { "8001", "80", "nope", "3307", "8011" })
        {
            using var refused = await PostFormAsync(client, "/setup/network", new() { ["AddressKind"] = "lan", ["FirstRegionPort"] = port });
            Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
            var error = new HtmlParser().ParseDocument(await refused.Content.ReadAsStringAsync(Ct)).QuerySelector("#port-error")?.TextContent;
            Assert.False(string.IsNullOrWhiteSpace(error), port);
            Assert.False(f.Store.LoadOrCreate().Setup.NetworkSet);
        }

        using var resp = await PostFormAsync(client, "/setup/network", new() { ["AddressKind"] = "lan", ["FirstRegionPort"] = "8007" });
        Assert.Equal("/setup", Location(resp));
        var s = f.Store.LoadOrCreate();
        Assert.Equal(8007, s.RegionPort);
        Assert.Null(s.Hostname);
        Assert.True(s.Setup.NetworkSet);
        Assert.True(f.Grid.UpdateWriteConfig.Last());
        using var next = await client.GetAsync("/setup", Ct);
        Assert.Equal("/setup/avatar", Location(next));
    }

    [Fact]
    public async Task Region_server_network_suggests_the_own_region_port_without_reported_ports()
    {
        await using var f = RegionServer(s => s with
        {
            Join = Avalon() with { RegionPortsFrom = null, RegionPortsTo = null },
            Setup = s.Setup with { WorldNamed = true },
        });
        using var client = await SetupClientAsync(f);

        var page = new HtmlParser().ParseDocument(await client.GetStringAsync("/setup/network", Ct));

        Assert.Equal("8004", page.QuerySelector("input[name=FirstRegionPort]")?.GetAttribute("value"));
    }

    [Fact]
    public async Task Region_server_network_from_the_internet_checks_the_address()
    {
        await using var f = JoinedRegionServer(networkSet: false);
        using var client = await SetupClientAsync(f);

        using (var refused = await PostFormAsync(client, "/setup/network",
                   new() { ["AddressKind"] = "public", ["PublicAddress"] = "192.168.1.20", ["FirstRegionPort"] = "8007" }))
        {
            Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
            Assert.Contains(NotPublic.Replace("'", "&#x27;"), await refused.Content.ReadAsStringAsync(Ct));
            Assert.False(f.Store.LoadOrCreate().Setup.NetworkSet);
        }

        using var resp = await PostFormAsync(client, "/setup/network",
            new() { ["AddressKind"] = "public", ["PublicAddress"] = "203.0.113.7", ["FirstRegionPort"] = "9000" });
        Assert.Equal("/setup", Location(resp));
        var s = f.Store.LoadOrCreate();
        Assert.Equal("203.0.113.7", s.Hostname);
        Assert.Equal(9000, s.RegionPort);
    }

    [Fact]
    public async Task Region_server_network_has_no_hypergrid_switch()
    {
        await using var f = JoinedRegionServer(networkSet: false);
        using var client = await SetupClientAsync(f);

        var page = new HtmlParser().ParseDocument(await client.GetStringAsync("/setup/network", Ct));

        Assert.Null(page.QuerySelector("input[name=Hypergrid]"));
        Assert.Contains("Hypergrid is off for this grid; the Grid Asset Server decides.", page.Body!.TextContent);
        Assert.Contains(page.QuerySelectorAll(".plan-advisories li"), li => li.TextContent.Contains("private port"));

        // Even a posted switch changes nothing.
        (await PostFormAsync(client, "/setup/network",
            new() { ["AddressKind"] = "public", ["PublicAddress"] = "203.0.113.7", ["FirstRegionPort"] = "8007", ["Hypergrid"] = "on" })).Dispose();
        Assert.True(f.Store.LoadOrCreate().Setup.NetworkSet);
        Assert.False(f.Store.LoadOrCreate().Hypergrid);
    }

    [Fact]
    public async Task Region_server_avatar_is_typed_by_name()
    {
        await using var f = JoinedRegionServer();
        f.Grid.EstateOwnerFound = ("Ann", "Lee");
        using var client = await SetupClientAsync(f);

        var page = new HtmlParser().ParseDocument(await client.GetStringAsync("/setup/avatar", Ct));
        Assert.Equal("Your avatar", page.QuerySelector("h1")?.TextContent.Trim());
        Assert.Null(page.QuerySelector("input[type=password]"));
        Assert.Null(page.QuerySelector("input[name=Email]"));
        Assert.Contains("Type the name of your avatar on Avalon. It becomes the owner of the regions on this computer. No password is needed here.",
            page.Body!.TextContent);

        using var resp = await PostFormAsync(client, "/setup/avatar", new() { ["FirstName"] = "ann", ["LastName"] = "lee" });

        Assert.Equal("/setup", Location(resp));
        Assert.Contains("SetEstateOwner ann lee", f.Grid.Snapshot());
        Assert.Equal(0, f.Grid.CreateOwnerCalls);
        var s = f.Store.LoadOrCreate();
        Assert.Equal(("Ann", "Lee"), (s.Setup.Owner?.FirstName, s.Setup.Owner?.LastName));
        Assert.Null(s.SetupToken);
        Assert.Contains(resp.Headers.GetValues("Set-Cookie"), c => c.StartsWith("dg-auth=", StringComparison.Ordinal));
        Assert.Contains(resp.Headers.GetValues("Set-Cookie"), c => c.StartsWith("dg-setup=;", StringComparison.Ordinal));
        using var next = await client.GetAsync("/setup", Ct);
        Assert.Equal("/setup/region", Location(next));
    }

    [Fact]
    public async Task Region_server_avatar_not_found_is_shown()
    {
        await using var f = JoinedRegionServer();
        f.Grid.EstateOwnerError = new ArgumentException(
            "There's no avatar called Ann Lee on Avalon. Check the spelling, or create the avatar on the Grid Asset Server first.");
        using var client = await SetupClientAsync(f);

        using var resp = await PostFormAsync(client, "/setup/avatar", new() { ["FirstName"] = "Ann", ["LastName"] = "Lee" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var page = new HtmlParser().ParseDocument(await resp.Content.ReadAsStringAsync(Ct));
        Assert.Contains("There's no avatar called", page.QuerySelector(".error-box")?.TextContent);
        Assert.Equal(Token, f.Store.LoadOrCreate().SetupToken);
        Assert.False(f.Store.LoadOrCreate().Setup.OwnerCreated);
    }

    [Fact]
    public async Task Region_server_avatar_shows_an_unreachable_grid_asset_server()
    {
        await using var f = JoinedRegionServer();
        f.Grid.EstateOwnerError = new GridAssetUnreachableException("192.168.1.5", 8002);
        using var client = await SetupClientAsync(f);

        using var resp = await PostFormAsync(client, "/setup/avatar", new() { ["FirstName"] = "Ann", ["LastName"] = "Lee" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var page = new HtmlParser().ParseDocument(await resp.Content.ReadAsStringAsync(Ct));
        Assert.Contains(GridAssetUnreachableException.MessageFor("192.168.1.5", 8002), page.QuerySelector(".error-box")?.TextContent);
    }

    [Fact]
    public async Task Region_server_avatar_starts_the_grid_not_a_grid_service()
    {
        await using var f = JoinedRegionServer();
        using var client = await SetupClientAsync(f);
        await f.Grid.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct); // the host's own start at launch
        var before = f.Grid.Snapshot().Count(c => c == "StartAsync");
        var stopped = new ComponentStatus("database", ProcessState.Stopped, null, null, null, null);
        f.Grid.Status = new GridStatus(stopped, stopped with { Name = "robust" }, [], [], false, "http://192.168.1.5:8002/");

        using var resp = await PostFormAsync(client, "/setup/avatar", new() { ["FirstName"] = "Ann", ["LastName"] = "Lee" });

        Assert.Equal("/setup", Location(resp));
        var calls = f.Grid.Snapshot();
        Assert.Equal(before + 1, calls.Count(c => c == "StartAsync"));
        Assert.DoesNotContain(calls, c => c.StartsWith("Start ", StringComparison.Ordinal) || c.StartsWith("Restart ", StringComparison.Ordinal));
        Assert.True(Array.LastIndexOf(calls, "StartAsync") < Array.IndexOf(calls, "SetEstateOwner Ann Lee"), string.Join(", ", calls));
    }

    [Fact]
    public async Task Region_server_skips_name_your_world()
    {
        await using var f = JoinedRegionServer();
        using var client = await SetupClientAsync(f);

        var page = new HtmlParser().ParseDocument(await client.GetStringAsync("/setup/avatar", Ct));
        Assert.Equal(["Choose your plan (done)", "Join the Grid Asset Server (done)", "Your network (done)", "Your avatar", "Choose your first region", "Connect with Firestorm"],
            page.QuerySelectorAll(".checklist li").Select(li => li.TextContent.Trim()));
        Assert.DoesNotContain(DreamGrid.Launcher.Setup.SetupSteps.For(f.Store.LoadOrCreate()), step => step.Path == "/setup/world");

        using var resp = await client.GetAsync("/setup/world", Ct);
        Assert.Equal("/setup", Location(resp));
    }

    [Fact]
    public async Task Region_server_region_step_starts_the_grid_instead_of_a_grid_service()
    {
        await using var f = RegionServerAtRegion();
        using var client = await SignedInClientAsync(f);
        await f.Grid.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct); // the host's own start at launch
        var before = f.Grid.Snapshot().Count(c => c == "StartAsync");
        var stopped = new ComponentStatus("database", ProcessState.Stopped, null, null, null, null);
        f.Grid.Status = new GridStatus(stopped, stopped with { Name = "robust" }, [], [], false, "http://192.168.1.5:8002/");

        (await PostFormAsync(client, "/setup/region", new() { ["RegionName"] = "Welcome", ["Content"] = "island" })).Dispose();

        var done = await WaitForProgressAsync(client, p => !p.GetProperty("running").GetBoolean());
        Assert.Equal("Ready", Str(done, "stage"));
        var calls = f.Grid.Snapshot();
        Assert.DoesNotContain(calls, c => c.StartsWith("Start ", StringComparison.Ordinal) || c.StartsWith("Restart ", StringComparison.Ordinal));
        Assert.Equal(before + 1, calls.Count(c => c == "StartAsync"));
        var region = Array.FindIndex(calls, c => c.StartsWith("CreateRegion", StringComparison.Ordinal));
        Assert.True(Array.LastIndexOf(calls, "StartAsync") < region, string.Join(", ", calls));
    }

    [Fact]
    public async Task Region_server_region_step_leaves_a_running_grid_alone()
    {
        await using var f = RegionServerAtRegion();
        using var client = await SignedInClientAsync(f);
        await f.Grid.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var before = f.Grid.Snapshot().Count(c => c == "StartAsync");

        (await PostFormAsync(client, "/setup/region", new() { ["RegionName"] = "Welcome", ["Content"] = "island" })).Dispose();

        var done = await WaitForProgressAsync(client, p => !p.GetProperty("running").GetBoolean());
        Assert.Equal("Ready", Str(done, "stage"));
        Assert.Equal(before, f.Grid.Snapshot().Count(c => c == "StartAsync"));
    }

    [Fact]
    public async Task Region_server_region_step_shows_an_unreachable_grid_asset_server()
    {
        await using var f = RegionServerAtRegion();
        using var client = await SignedInClientAsync(f);
        await f.Grid.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var running = new ComponentStatus("database", ProcessState.Running, null, null, null, null);
        f.Grid.Status = new GridStatus(running, running with { Name = "robust", State = ProcessState.Failed }, [], [], false, "http://192.168.1.5:8002/");
        f.Grid.OnStart = _ => throw new GridAssetUnreachableException("192.168.1.5", 8002);

        (await PostFormAsync(client, "/setup/region", new() { ["RegionName"] = "Welcome", ["Content"] = "island" })).Dispose();

        var done = await WaitForProgressAsync(client, p => !p.GetProperty("running").GetBoolean());
        Assert.Equal(GridAssetUnreachableException.MessageFor("192.168.1.5", 8002), Str(done, "error"));
        Assert.DoesNotContain(f.Grid.Snapshot(), c => c.StartsWith("CreateRegion", StringComparison.Ordinal));
    }

    // ---- Connected Server (plan 6c) ----

    /// <summary>What the other grid answers in these tests: OSGrid, with a gatekeeper (hypergrid on).</summary>
    private static OtherGridInfo OsgridInfo(bool gridAssetServer = false) =>
        new("OSGrid", "http://login.osgrid.org/", "http://gatekeeper.osgrid.org:8002/", gridAssetServer);

    private static ConnectedGrid Osgrid(string? gatekeeper = "http://gatekeeper.osgrid.org:8002/") =>
        new("osgrid", "OSGrid", "http://login.osgrid.org/", "http://login.osgrid.org/", "http://grid.osgrid.org/", gatekeeper);

    /// <summary>A Connected Server before its owner exists; <paramref name="more"/> seeds the rest (the other grid, steps done).</summary>
    private static LauncherFactory ConnectedServer(Func<LauncherSettings, LauncherSettings>? more = null)
    {
        var f = new LauncherFactory(s =>
        {
            var seeded = s with { SetupToken = Token, Plan = OwnerPlan.Connected };
            return more is null ? seeded : more(seeded);
        })
        {
            Before = TestAuth.Apply,
        };
        f.OtherGrid.Result = OsgridInfo();
        return f;
    }

    /// <summary>A Connected Server that has connected to OSgrid, with its network set unless <paramref name="networkSet"/> is false.</summary>
    private static LauncherFactory ConnectedToOsgrid(bool networkSet = true, string? gatekeeper = "http://gatekeeper.osgrid.org:8002/") => ConnectedServer(s => s with
    {
        Connected = Osgrid(gatekeeper),
        GridName = "OSGrid",
        Setup = s.Setup with { WorldNamed = true, NetworkSet = networkSet },
    });

    [Fact]
    public async Task Connected_server_card_can_be_chosen()
    {
        await using var f = Unplanned();
        using var client = await SetupClientAsync(f);

        using var resp = await PostFormAsync(client, "/setup/plan/connected-server?handler=Choose", new(), tokenFrom: "/setup/plan");

        Assert.Equal("/setup", Location(resp));
        Assert.Equal(OwnerPlan.Connected, f.Store.LoadOrCreate().Plan);
        using var next = await client.GetAsync("/setup", Ct);
        Assert.Equal("/setup/other-grid", Location(next));
    }

    [Fact]
    public async Task Osgrid_shows_what_it_found_then_saves_on_confirm()
    {
        await using var f = ConnectedServer();
        using var client = await SetupClientAsync(f);

        var form = new HtmlParser().ParseDocument(await client.GetStringAsync("/setup/other-grid", Ct));
        Assert.Contains("current", form.QuerySelectorAll(".checklist li")[1].ClassName);
        Assert.True(form.QuerySelector("input[name=Grid][value=osgrid]")!.HasAttribute("checked"));

        using var first = await PostFormAsync(client, "/setup/other-grid", new() { ["Grid"] = "osgrid" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var page = new HtmlParser().ParseDocument(await first.Content.ReadAsStringAsync(Ct));
        var text = page.Body!.TextContent;
        Assert.Contains("OSGrid", text);
        Assert.Contains("Hypergrid: on", text);
        Assert.Null(page.QuerySelector(".grid-asset-warning"));
        Assert.Contains(page.QuerySelectorAll("button"), b => b.TextContent.Trim() == "Connect to this grid");
        Assert.Contains(page.QuerySelectorAll("button, a"), b => b.TextContent.Trim() == "Change");
        Assert.Null(f.Store.LoadOrCreate().Connected);
        Assert.DoesNotContain("UpdateSettings", f.Grid.Snapshot());
        Assert.Equal(new Uri("http://login.osgrid.org/"), f.OtherGrid.Reads.Single());
        Assert.Equal(new Uri("http://grid.osgrid.org/"), f.OtherGrid.ServicesChecks.Single());

        // Confirm reads the grid again: posted look-alike values are never trusted.
        using var confirm = await PostFormAsync(client, "/setup/other-grid?handler=Confirm",
            new() { ["Grid"] = "osgrid", ["GridName"] = "Forged" }, tokenFrom: "/setup/other-grid");
        Assert.Equal("/setup", Location(confirm));
        Assert.Equal(2, f.OtherGrid.Reads.Count);
        var s = f.Store.LoadOrCreate();
        Assert.Equal(Osgrid(), s.Connected);
        Assert.Equal("OSGrid", s.GridName);
        Assert.True(s.Setup.WorldNamed);
        Assert.True(f.Grid.UpdateWriteConfig.Last());
        using var next = await client.GetAsync("/setup", Ct);
        Assert.Equal("/setup/network", Location(next));
    }

    [Fact]
    public async Task Another_grid_needs_both_addresses()
    {
        await using var f = ConnectedServer();
        using var client = await SetupClientAsync(f);

        foreach (var url in new[] { "/setup/other-grid", "/setup/other-grid?handler=Confirm" })
        {
            using var refused = await PostFormAsync(client, url,
                new() { ["Grid"] = "other", ["LoginAddress"] = "grid.example.org", ["ServicesAddress"] = "" }, tokenFrom: "/setup/other-grid");
            Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
            Assert.Contains("Type the other grid&#x27;s address", await refused.Content.ReadAsStringAsync(Ct));
        }
        Assert.Null(f.Store.LoadOrCreate().Connected);
        Assert.Empty(f.OtherGrid.ServicesChecks);

        using var confirm = await PostFormAsync(client, "/setup/other-grid?handler=Confirm",
            new() { ["Grid"] = "other", ["LoginAddress"] = "grid.example.org", ["ServicesAddress"] = "grid.example.org" }, tokenFrom: "/setup/other-grid");
        Assert.Equal("/setup", Location(confirm));
        var saved = f.Store.LoadOrCreate().Connected!;
        Assert.Null(saved.Preset);
        Assert.Equal("http://grid.example.org:8002/", saved.Address);
        Assert.Equal("http://grid.example.org:8003/", saved.ServicesUri);
        Assert.Equal("OSGrid", saved.GridName); // what the fake grid answered
        Assert.Equal(new Uri("http://grid.example.org:8003/"), f.OtherGrid.ServicesChecks.Single());
    }

    [Theory]
    [InlineData("false", "Hypergrid: off")]
    [InlineData("true", "Hypergrid: on")]
    public async Task Another_dreamgrid_says_hypergrid_as_its_own_switch_says(string dreamgridHypergrid, string expected)
    {
        await using var f = ConnectedServer();
        // A DreamGrid's Robust publishes a gatekeeper even with hypergrid off; its own switch decides.
        f.OtherGrid.Result = OtherGridInfoParser.Parse(new Uri("http://grid.example.org:8002/"),
            "<gridinfo><login>http://grid.example.org:8002/</login><gridname>Elsewhere</gridname>" +
            "<gatekeeper>http://grid.example.org:8002/</gatekeeper>" +
            $"<dreamgrid_hypergrid>{dreamgridHypergrid}</dreamgrid_hypergrid></gridinfo>");
        using var client = await SetupClientAsync(f);

        var form = new Dictionary<string, string> { ["Grid"] = "other", ["LoginAddress"] = "grid.example.org", ["ServicesAddress"] = "grid.example.org" };
        using var first = await PostFormAsync(client, "/setup/other-grid", form);
        var text = new HtmlParser().ParseDocument(await first.Content.ReadAsStringAsync(Ct)).Body!.TextContent;
        Assert.Contains(expected, text);

        using var confirm = await PostFormAsync(client, "/setup/other-grid?handler=Confirm", form, tokenFrom: "/setup/other-grid");
        Assert.Equal("/setup", Location(confirm));
        Assert.Equal(dreamgridHypergrid == "true", PlanCatalog.HypergridOn(f.Store.LoadOrCreate()));
    }

    [Fact]
    public async Task Another_grid_page_names_its_two_addresses()
    {
        await using var f = ConnectedServer();
        using var client = await SetupClientAsync(f);

        var page = new HtmlParser().ParseDocument(await client.GetStringAsync("/setup/other-grid", Ct));

        var text = page.Body!.TextContent;
        Assert.Contains("its login address (the one you'd give Firestorm)", text);
        Assert.Contains("its address for region servers (the grid gives this to people who host regions)", text);
        Assert.NotNull(page.QuerySelector("input[name=Grid][value=other]"));
        Assert.NotNull(page.QuerySelector("input[name=LoginAddress]"));
        Assert.NotNull(page.QuerySelector("input[name=ServicesAddress]"));
    }

    [Fact]
    public async Task Another_grid_that_is_a_grid_asset_server_is_warned_not_refused()
    {
        await using var f = ConnectedServer();
        f.OtherGrid.Result = OsgridInfo(gridAssetServer: true);
        using var client = await SetupClientAsync(f);

        using var first = await PostFormAsync(client, "/setup/other-grid",
            new() { ["Grid"] = "other", ["LoginAddress"] = "192.168.1.5", ["ServicesAddress"] = "192.168.1.5" });
        var page = new HtmlParser().ParseDocument(await first.Content.ReadAsStringAsync(Ct));
        Assert.Contains("This is a DreamGrid Grid Asset Server. The Region Server plan joins it more simply; go back to the plans to choose it.",
            page.QuerySelector(".grid-asset-warning")?.TextContent);
        Assert.Null(f.Store.LoadOrCreate().Connected);

        using var confirm = await PostFormAsync(client, "/setup/other-grid?handler=Confirm",
            new() { ["Grid"] = "other", ["LoginAddress"] = "192.168.1.5", ["ServicesAddress"] = "192.168.1.5" }, tokenFrom: "/setup/other-grid");
        Assert.Equal("/setup", Location(confirm));
        Assert.NotNull(f.Store.LoadOrCreate().Connected);
    }

    [Fact]
    public async Task Other_grid_errors_are_shown()
    {
        await using var f = ConnectedServer();
        using var client = await SetupClientAsync(f);

        f.OtherGrid.Error = new GridJoinException("Nothing answered at http://login.osgrid.org/. Check the address.", noAnswer: true);
        foreach (var url in new[] { "/setup/other-grid", "/setup/other-grid?handler=Confirm" })
        {
            using var resp = await PostFormAsync(client, url, new() { ["Grid"] = "osgrid" }, tokenFrom: "/setup/other-grid");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Contains("Nothing answered at http://login.osgrid.org/.", await resp.Content.ReadAsStringAsync(Ct));
        }

        f.OtherGrid.Error = null;
        f.OtherGrid.ServicesError = new GridJoinException("http://grid.osgrid.org/ answered, but not like a grid's services.");
        foreach (var url in new[] { "/setup/other-grid", "/setup/other-grid?handler=Confirm" })
        {
            using var resp = await PostFormAsync(client, url, new() { ["Grid"] = "osgrid" }, tokenFrom: "/setup/other-grid");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Contains("answered, but not like a grid&#x27;s services.", await resp.Content.ReadAsStringAsync(Ct));
        }
        var s = f.Store.LoadOrCreate();
        Assert.Null(s.Connected);
        Assert.False(s.Setup.WorldNamed);
        Assert.DoesNotContain("UpdateSettings", f.Grid.Snapshot());
    }

    [Fact]
    public async Task Other_grid_change_shows_the_form_again()
    {
        await using var f = ConnectedServer();
        using var client = await SetupClientAsync(f);

        using var resp = await PostFormAsync(client, "/setup/other-grid?handler=Change",
            new() { ["Grid"] = "other", ["LoginAddress"] = "grid.example.org", ["ServicesAddress"] = "grid.example.org:9003" }, tokenFrom: "/setup/other-grid");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var page = new HtmlParser().ParseDocument(await resp.Content.ReadAsStringAsync(Ct));
        Assert.True(page.QuerySelector("input[name=Grid][value=other]")!.HasAttribute("checked"));
        Assert.Equal("grid.example.org:9003", page.QuerySelector("input[name=ServicesAddress]:not([type=hidden])")?.GetAttribute("value"));
        Assert.Empty(f.OtherGrid.Reads);
        Assert.Null(f.Store.LoadOrCreate().Connected);
    }

    [Fact]
    public async Task Connected_network_defaults_to_the_internet_and_has_no_switch()
    {
        await using var f = ConnectedToOsgrid(networkSet: false);
        using var client = await SetupClientAsync(f);

        var page = new HtmlParser().ParseDocument(await client.GetStringAsync("/setup/network", Ct));

        Assert.Null(page.QuerySelector("input[name=Hypergrid]"));
        Assert.Contains("Hypergrid is on for OSGrid; that grid decides.", page.Body!.TextContent);
        Assert.True(page.QuerySelector("input[name=AddressKind][value=public]")!.HasAttribute("checked"));
        Assert.False(page.QuerySelector("input[name=AddressKind][value=lan]")!.HasAttribute("checked"));
        Assert.Equal(f.Store.LoadOrCreate().RegionPort.ToString(), page.QuerySelector("input[name=FirstRegionPort]")?.GetAttribute("value"));
        Assert.Contains(page.QuerySelectorAll(".plan-advisories li"), li => li.TextContent.Contains("reachable from the internet"));
        Assert.Contains(page.QuerySelectorAll(".plan-advisories li"),
            li => li.TextContent.Contains("uses your avatar on the other grid: that grid checks the password."));

        using var resp = await PostFormAsync(client, "/setup/network",
            new() { ["AddressKind"] = "public", ["PublicAddress"] = "203.0.113.7", ["FirstRegionPort"] = "9100", ["Hypergrid"] = "on" });
        Assert.Equal("/setup", Location(resp));
        var s = f.Store.LoadOrCreate();
        Assert.Equal("203.0.113.7", s.Hostname);
        Assert.Equal(9100, s.RegionPort);
        Assert.True(s.Setup.NetworkSet);
        Assert.False(s.Hypergrid);
    }

    [Fact]
    public async Task Connected_network_says_hypergrid_is_off_when_the_grid_has_no_gatekeeper()
    {
        await using var f = ConnectedToOsgrid(networkSet: false, gatekeeper: null);
        using var client = await SetupClientAsync(f);

        var page = new HtmlParser().ParseDocument(await client.GetStringAsync("/setup/network", Ct));

        Assert.Contains("Hypergrid is off for OSGrid; that grid decides.", page.Body!.TextContent);
    }

    [Fact]
    public async Task Connected_avatar_is_typed_by_name()
    {
        await using var f = ConnectedToOsgrid();
        f.Grid.EstateOwnerFound = ("Ann", "Lee");
        using var client = await SetupClientAsync(f);

        var page = new HtmlParser().ParseDocument(await client.GetStringAsync("/setup/avatar", Ct));
        Assert.Equal("Your avatar", page.QuerySelector("h1")?.TextContent.Trim());
        Assert.Null(page.QuerySelector("input[type=password]"));
        Assert.Null(page.QuerySelector("input[name=Email]"));
        Assert.Contains("Type the name of your avatar on OSGrid. It becomes the owner of the regions on this computer, and it is the avatar you sign in to this website with. No password is needed here.",
            page.Body!.TextContent);

        using var resp = await PostFormAsync(client, "/setup/avatar", new() { ["FirstName"] = "ann", ["LastName"] = "lee" });

        Assert.Equal("/setup", Location(resp));
        Assert.Contains("SetEstateOwner ann lee", f.Grid.Snapshot());
        Assert.Equal(0, f.Grid.CreateOwnerCalls);
        var s = f.Store.LoadOrCreate();
        Assert.Equal(("Ann", "Lee"), (s.Setup.Owner?.FirstName, s.Setup.Owner?.LastName));
        Assert.Null(s.SetupToken);
        Assert.Contains(resp.Headers.GetValues("Set-Cookie"), c => c.StartsWith("dg-auth=", StringComparison.Ordinal));
        using var next = await client.GetAsync("/setup", Ct);
        Assert.Equal("/setup/region", Location(next));
    }

    [Fact]
    public async Task Connected_avatar_errors_are_shown_as_they_are()
    {
        await using var f = ConnectedToOsgrid();
        using var client = await SetupClientAsync(f);

        f.Grid.EstateOwnerError = new ArgumentException("There's no avatar called Ann Lee on OSGrid. Check the spelling, or sign up with OSGrid first.");
        using var missing = await PostFormAsync(client, "/setup/avatar", new() { ["FirstName"] = "Ann", ["LastName"] = "Lee" });
        Assert.Contains("sign up with OSGrid first.", new HtmlParser().ParseDocument(await missing.Content.ReadAsStringAsync(Ct)).QuerySelector(".error-box")?.TextContent);

        f.Grid.EstateOwnerError = new OtherGridUnreachableException("OSGrid", "http://login.osgrid.org/");
        using var down = await PostFormAsync(client, "/setup/avatar", new() { ["FirstName"] = "Ann", ["LastName"] = "Lee" });
        Assert.Equal(HttpStatusCode.OK, down.StatusCode);
        Assert.Contains("OSGrid", new HtmlParser().ParseDocument(await down.Content.ReadAsStringAsync(Ct)).QuerySelector(".error-box")?.TextContent);
        Assert.False(f.Store.LoadOrCreate().Setup.OwnerCreated);
    }

    [Fact]
    public async Task Connected_skips_name_your_world()
    {
        await using var f = ConnectedToOsgrid();
        using var client = await SetupClientAsync(f);

        var page = new HtmlParser().ParseDocument(await client.GetStringAsync("/setup/avatar", Ct));
        Assert.Equal(["Choose your plan (done)", "Connect to the other grid (done)", "Your network (done)", "Your avatar", "Choose your first region", "Connect with Firestorm"],
            page.QuerySelectorAll(".checklist li").Select(li => li.TextContent.Trim()));
        Assert.DoesNotContain(DreamGrid.Launcher.Setup.SetupSteps.For(f.Store.LoadOrCreate()), step => step.Path == "/setup/world");

        using var resp = await client.GetAsync("/setup/world", Ct);
        Assert.Equal("/setup", Location(resp));
    }

    [Fact]
    public async Task Connected_other_grid_step_is_closed_once_the_owner_exists()
    {
        await using var f = ConnectedServer(s => s with
        {
            Connected = Osgrid(),
            SetupToken = null,
            Setup = new SetupState(WorldNamed: true, OwnerCreated: true, Owner: new OwnerInfo("Ann", "Lee", Guid.NewGuid()), NetworkSet: true),
        });
        using var client = await SignedInClientAsync(f);

        using var resp = await client.GetAsync("/setup/other-grid", Ct);

        Assert.Equal("/setup", Location(resp));
    }

    [Fact]
    public async Task Region_step_headlines_the_placement_message()
    {
        await using var f = ConnectedServer(s => s with
        {
            Connected = Osgrid(),
            GridName = "OSGrid",
            SetupToken = null,
            Setup = new SetupState(WorldNamed: true, OwnerCreated: true, Owner: new OwnerInfo("Ann", "Lee", Guid.NewGuid()), NetworkSet: true),
        });
        using var client = await SignedInClientAsync(f);
        await f.Grid.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        const string Message = "The squares near 1000,1000 on OSGrid's map are all taken. Try again, or ask the grid for a place.";
        f.Grid.OnCreateRegion = (_, _) => throw new ArgumentException(Message);

        (await PostFormAsync(client, "/setup/region", new() { ["RegionName"] = "Welcome", ["Content"] = "island" })).Dispose();

        var done = await WaitForProgressAsync(client, p => !p.GetProperty("running").GetBoolean());
        Assert.Equal(Message, Str(done, "error"));
    }

    /// <summary>A Connected Server on OSgrid at its region step, signed in, after the host's own start at launch.</summary>
    private static async Task<(LauncherFactory F, HttpClient Client)> ConnectedAtRegionAsync()
    {
        var f = ConnectedServer(s => s with
        {
            Connected = Osgrid(),
            GridName = "OSGrid",
            SetupToken = null,
            Setup = new SetupState(WorldNamed: true, OwnerCreated: true, Owner: new OwnerInfo("Ann", "Lee", Guid.NewGuid()), NetworkSet: true),
        });
        var client = await SignedInClientAsync(f);
        await f.Grid.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        return (f, client);
    }

    [Fact]
    public async Task Region_step_headlines_an_owner_reason_without_the_parameter_name()
    {
        var (f, client) = await ConnectedAtRegionAsync();
        await using var factory = f;
        using var http = client;
        const string Message = "A region name can't be longer than 32 characters.";
        f.Grid.OnCreateRegion = (_, _) => throw new ArgumentException(Message, "name");

        (await PostFormAsync(client, "/setup/region", new() { ["RegionName"] = "Welcome", ["Content"] = "island" })).Dispose();

        var done = await WaitForProgressAsync(client, p => !p.GetProperty("running").GetBoolean());
        Assert.Equal(Message, Str(done, "error"));
        Assert.DoesNotContain("(Parameter", Str(done, "error"));
    }

    [Fact]
    public async Task Region_step_does_not_headline_a_null_argument()
    {
        var (f, client) = await ConnectedAtRegionAsync();
        await using var factory = f;
        using var http = client;
        f.Grid.OnCreateRegion = (_, _) => throw new ArgumentNullException("content", "A bug, not a reason for the owner.");

        (await PostFormAsync(client, "/setup/region", new() { ["RegionName"] = "Welcome", ["Content"] = "island" })).Dispose();

        var done = await WaitForProgressAsync(client, p => !p.GetProperty("running").GetBoolean());
        var error = Str(done, "error")!;
        Assert.StartsWith("Your region could not be set up.", error);
        Assert.DoesNotContain("A bug", error);
    }

    [Theory]
    [InlineData(true, "Reaching the other grid")]
    [InlineData(false, "Reaching the Grid Asset Server")]
    public async Task Region_step_names_whose_grid_it_is_reaching(bool connected, string stage)
    {
        LauncherFactory f;
        HttpClient client;
        if (connected)
            (f, client) = await ConnectedAtRegionAsync();
        else
        {
            f = RegionServerAtRegion();
            client = await SignedInClientAsync(f);
            await f.Grid.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        }
        await using var factory = f;
        using var http = client;
        var running = new ComponentStatus("database", ProcessState.Running, null, null, null, null);
        f.Grid.Status = new GridStatus(running, running with { Name = "robust", State = ProcessState.Failed }, [], [], false, "http://127.0.0.1:8002/");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Grid.OnStart = ct => release.Task.WaitAsync(ct);

        (await PostFormAsync(client, "/setup/region", new() { ["RegionName"] = "Welcome", ["Content"] = "island" })).Dispose();

        var reaching = await WaitForProgressAsync(client, p => Str(p, "stage") == stage);
        Assert.True(reaching.GetProperty("running").GetBoolean());
        release.SetResult();
        await WaitForProgressAsync(client, p => !p.GetProperty("running").GetBoolean());
    }
}
