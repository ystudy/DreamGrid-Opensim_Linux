using System.Net;
using DreamGrid.Launcher;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Api;
using DreamGrid.Launcher.Core.Config;
using DreamGrid.Launcher.Core.Database;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Guests;
using DreamGrid.Launcher.Core.Logs;
using DreamGrid.Launcher.Endpoints;
using DreamGrid.Launcher.Core.Modules;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Setup;
using DreamGrid.Launcher.Security;
using DreamGrid.Launcher.Guests;
using DreamGrid.Launcher.Setup;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.DataProtection;

// Installer and owner verbs (--version, admin-link, stop-grid, set-web-port, reset-two-factor) run instead of the host.
var processEnv = new Dictionary<string, string?>
{
    ["DREAMGRID_DATA"] = Environment.GetEnvironmentVariable("DREAMGRID_DATA"),
    ["DREAMGRID_OPENSIM"] = Environment.GetEnvironmentVariable("DREAMGRID_OPENSIM"),
    ["DREAMGRID_MARIADB_BIN"] = Environment.GetEnvironmentVariable("DREAMGRID_MARIADB_BIN"),
    ["ProgramData"] = Environment.GetEnvironmentVariable("ProgramData"),
};
if (await CommandLine.RunVerbAsync(args, processEnv, Console.Out, CancellationToken.None) is { } verbExit)
    return verbExit;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSystemd();
builder.Host.UseWindowsService(o => o.ServiceName = "DreamGrid");

// The configuration carries the environment, so DREAMGRID_* can also come from the host settings (tests).
var config = builder.Configuration;
var env = new Dictionary<string, string?>
{
    ["DREAMGRID_DATA"] = config["DREAMGRID_DATA"],
    ["DREAMGRID_OPENSIM"] = config["DREAMGRID_OPENSIM"],
    ["DREAMGRID_MARIADB_BIN"] = config["DREAMGRID_MARIADB_BIN"],
    ["ProgramData"] = Environment.GetEnvironmentVariable("ProgramData"),
};
LauncherPaths paths;
InstanceLock? instanceLock;
SettingsStore store;
LauncherSettings settings;
try
{
    var dataDir = LauncherPaths.ResolveData(args, env, OperatingSystem.IsWindows());
    instanceLock = InstanceLock.TryAcquire(dataDir);
    if (instanceLock is null)
    {
        using var held = LoggerFactory.Create(b => b.AddConsole());
        held.CreateLogger("DreamGrid.Launcher")
            .LogCritical("Another DreamGrid launcher is already running with data directory {Data}.", dataDir);
        return 3;
    }
    paths = LauncherPaths.Resolve(args, env, OperatingSystem.IsWindows());
    store = new SettingsStore(paths.SettingsFile);
    settings = store.LoadOrCreate();
    // The --web-port host option (development) is the one settings change made outside the controller: at startup, under
    // the lock, before anything reads the port. It applies again on every start; the installers use set-web-port instead.
    if (CommandLine.ParseWebPort(args) is { } webPort)
    {
        if (CommandLine.ApplyWebPort(store, webPort) is { } portError)
            throw new ArgumentException(portError);
        settings = store.LoadOrCreate();
    }
    LauncherPaths.CreateOwnerOnly(paths.Data);
    LauncherPaths.CreateOwnerOnly(paths.DataProtectionKeys);
    LauncherPaths.CreateOwnerOnly(paths.Security);
    LauncherPaths.CreateOwnerOnly(paths.Guests);
}
catch (Exception e) when (e is ArgumentException or SettingsCorruptException or IOException or UnauthorizedAccessException)
{
    // Nothing can be served without the data directory and settings: one plain line, then a non-zero exit
    // (2; the taken web port is 1).
    using (var early = LoggerFactory.Create(b => b.AddConsole()))
        early.CreateLogger("DreamGrid.Launcher").LogCritical("The launcher cannot start: {Reason}", e.Message);
    return 2;
}

using var lockHandle = instanceLock; // held for the host's lifetime
builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Any, settings.WebPort));
// The request log line includes the query string, which carries ?password= (MachineId or an avatar's).
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
// Health polls of children that are still booting would log a stack trace every few seconds.
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
// Routing, page and result lines for every request (the dashboard polls every 5 s) would flood launcher.log.
// Microsoft.Hosting.Lifetime ("Now listening on…") is outside this category and stays at Information.
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
// Console output a command's reply window missed is discarded; keep it in launcher.log (rare, and echoes are redacted).
builder.Logging.AddFilter("DreamGrid.Launcher.Core.OpenSim.RestConsoleClient", LogLevel.Debug);

builder.Logging.AddProvider(new RollingFileLoggerProvider(Path.Combine(paths.Logs, "launcher.log")));

// Keys for the sign-in and antiforgery cookies live in the (owner-only) data directory, not the service account's
// profile, so cookies stay valid across restarts and the keys go wherever the data goes.
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(paths.DataProtectionKeys))
    .SetApplicationName("DreamGrid");
builder.Services.AddSingleton<Microsoft.AspNetCore.Routing.MatcherPolicy, LegacyRootPolicy>();
builder.Services.AddAuthentication(AuthSchemes.Admin).AddCookie(AuthSchemes.Admin, o =>
{
    o.Cookie.Name = "dg-auth";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.LoginPath = "/login";
    o.ExpireTimeSpan = StaffSignIn.Expiry;
    o.SlidingExpiration = true;
    // The session, the stamp and (every 5 minutes) the level, on every request.
    o.Events.OnValidatePrincipal = c => c.HttpContext.RequestServices.GetRequiredService<StaffPrincipalValidator>().ValidateAsync(c);
    // API callers get a status code, never a redirect to the login page.
    o.Events.OnRedirectToLogin = c => ApiOr(c, StatusCodes.Status401Unauthorized);
    o.Events.OnRedirectToAccessDenied = Denied;
})
// The guest door's own cookie: a session cookie (no persistent expiry), 8 hours sliding, never read by the staff pages. The
// stamp and (every 5 minutes) the level are checked on every request to a guest page. No API takes it, so it only redirects.
.AddCookie(AuthSchemes.Guest, o =>
{
    o.Cookie.Name = "dg-guest";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.LoginPath = "/guest/sign-in";
    o.ExpireTimeSpan = StaffSignIn.Expiry;
    o.SlidingExpiration = true;
    o.Events.OnValidatePrincipal = c => c.HttpContext.RequestServices.GetRequiredService<GuestPrincipalValidator>().ValidateAsync(c);
    // Where there is no guest side the pages do not exist: a caller who isn't signed in gets 404, not a redirect to a sign-in
    // page that doesn't exist either (the policy challenges before GuestSideOnly runs).
    o.Events.OnRedirectToLogin = c =>
    {
        if (!AdminRule.HasStaffPages(c.HttpContext.RequestServices.GetRequiredService<Func<LauncherSettings>>()()))
            c.Response.StatusCode = StatusCodes.Status404NotFound;
        else
            c.Response.Redirect(c.RedirectUri);
        return Task.CompletedTask;
    };
});
// Whatever has no decision of its own needs the admin role; the open routes say .AllowAnonymous() (or a page convention).
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(AuthSchemes.OwnerPolicy, p => p.AddAuthenticationSchemes(AuthSchemes.Admin).RequireRole(AuthSchemes.OwnerRole))
    .AddPolicy(AuthSchemes.AdminPolicy, p => p.AddAuthenticationSchemes(AuthSchemes.Admin).RequireRole(AuthSchemes.AdminRole))
    .AddPolicy(AuthSchemes.StaffPolicy, p => p.AddAuthenticationSchemes(AuthSchemes.Admin).RequireRole(AuthSchemes.StaffRole))
    // Only the guest cookie: a staff cookie opens no guest page, and a guest cookie opens no staff page.
    .AddPolicy(AuthSchemes.GuestPolicy, p => p.AddAuthenticationSchemes(AuthSchemes.Guest).RequireRole(AuthSchemes.GuestRole))
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().AddAuthenticationSchemes(AuthSchemes.Admin).RequireRole(AuthSchemes.AdminRole).Build());
builder.Services.AddRazorPages(o =>
{
    // Every page needs the admin role except these; the setup pages are guarded by SetupGate. A person's own pages under
    // /Profile and the People page are for every staff member, and the Security and Staff pages are the owner's. (Two folder
    // policies on one page would both have to pass, so those folders are left out of the admin one rather than given a second.)
    // The Staff and People pages do not exist (404) where there are no supervisors, nor do the Guest pages (the guest side).
    string[] ownFolders = ["/Profile/", "/Security/", "/Staff/", "/People/", "/Guest/"];
    o.Conventions.AddFolderApplicationModelConvention("/", m =>
    {
        if (!ownFolders.Any(f => m.ViewEnginePath.StartsWith(f, StringComparison.OrdinalIgnoreCase)))
            m.EndpointMetadata.Add(new AuthorizeAttribute(AuthSchemes.AdminPolicy));
    });
    o.Conventions.AuthorizeFolder("/Profile", AuthSchemes.StaffPolicy);
    o.Conventions.AuthorizeFolder("/Security", AuthSchemes.OwnerPolicy);
    o.Conventions.AuthorizeFolder("/Staff", AuthSchemes.OwnerPolicy);
    o.Conventions.AuthorizeFolder("/People", AuthSchemes.StaffPolicy);
    o.Conventions.AddFolderApplicationModelConvention("/Staff", m => m.Filters.Add(new StaffPagesOnly()));
    o.Conventions.AddFolderApplicationModelConvention("/People", m => m.Filters.Add(new StaffPagesOnly()));
    o.Conventions.AuthorizeFolder("/Guest", AuthSchemes.GuestPolicy);
    o.Conventions.AddFolderApplicationModelConvention("/Guest", m => m.Filters.Add(new GuestSideOnly()));
    // A guest accepts the current Terms before any other guest page (the Terms page, sign-out and sign-in excepted, see TermsGate).
    o.Conventions.AddFolderApplicationModelConvention("/Guest", m => m.Filters.Add(new TermsGate()));
    // The public front page ("/" shows it to anyone not signed in as staff, see SetupGate); like the guest pages it is not there
    // on a Region Server or a Connected Server.
    // The operator's legal pages and the sign-up page are public in the same way.
    foreach (var publicPage in new[] { "/Home", "/Legal", "/SignUp" })
        o.Conventions.AddPageApplicationModelConvention(publicPage, m => m.Filters.Add(new GuestSideOnly()));
    // The owner's legal editor: the Security folder's owner policy, and not there where there is no guest side.
    o.Conventions.AddPageApplicationModelConvention("/Security/Legal", m => m.Filters.Add(new GuestSideOnly()));
    // The same for the bot-protection page.
    o.Conventions.AddPageApplicationModelConvention("/Security/Bots", m => m.Filters.Add(new GuestSideOnly()));
    foreach (var open in new[] { "/Login", "/Login/Code", "/Logout", "/Welcome", "/Waiting", "/SetupDone", "/Setup", "/Home", "/Legal", "/SignUp", "/Guest/SignIn" })
        o.Conventions.AllowAnonymousToPage(open);
    o.Conventions.AllowAnonymousToFolder("/Setup");
    // The dashboard's forms post here: a POST to "/" belongs to the port-8001 contract (LegacyRootPolicy).
    o.Conventions.AddPageRoute("/Index", "dashboard");
});
// The dashboard's script sends the token in this header to /api/grid/*.
// The site's cookies have short names of their own (SiteFacts.Cookies lists them all).
builder.Services.AddAntiforgery(o =>
{
    o.HeaderName = "RequestVerificationToken";
    o.Cookie.Name = "dg-af";
});
builder.Services.Configure<Microsoft.AspNetCore.Mvc.CookieTempDataProviderOptions>(o => o.Cookie.Name = "dg-notice");
builder.Services.AddGrid(paths, store);
// Robust's map tiles, fetched on the browser's behalf (the browser never reaches Robust's port).
builder.Services.AddHttpClient(RegionsApi.RobustMapClient, c => c.Timeout = TimeSpan.FromSeconds(5));
builder.Services.AddSingleton<LogReader>();
builder.Services.AddSingleton(sp => new AuditLog(Path.Combine(paths.Logs, "audit.log"), sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton(sp => new LoginThrottle(sp.GetRequiredService<TimeProvider>(),
    Path.Combine(paths.Security, "throttle.json"), sp.GetRequiredService<ILogger<LoginThrottle>>()));
builder.Services.AddSingleton(sp => new StaffSessions(Path.Combine(paths.Security, "sessions.json"),
    sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<StaffSessions>>()));
builder.Services.AddSingleton(sp => new TwoFactorStore(Path.Combine(paths.Security, "twofactor.json"),
    sp.GetRequiredService<IDataProtectionProvider>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<TwoFactorStore>>()));
builder.Services.AddSingleton<ITwoFactorStatus>(sp => sp.GetRequiredService<TwoFactorStore>());
builder.Services.AddSingleton(sp => new SuspendedStore(Path.Combine(paths.Security, "suspended.json"), sp.GetRequiredService<ILogger<SuspendedStore>>()));
builder.Services.AddSingleton<IStaffDirectory>(sp => new StaffDirectory(sp.GetRequiredService<Func<LauncherSettings>>()));
builder.Services.AddSingleton<StaffActions>();
builder.Services.AddSingleton<StaffPrincipalValidator>();
builder.Services.AddSingleton(sp => new GuestStamps(Path.Combine(paths.Guests, "stamps.json"), sp.GetRequiredService<ILogger<GuestStamps>>()));
builder.Services.AddSingleton<GuestPrincipalValidator>();
builder.Services.AddSingleton(sp => new TermsAcceptances(Path.Combine(paths.Guests, "terms.json"), sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILogger<TermsAcceptances>>()));
builder.Services.AddSingleton(sp => new RegionRights(Path.Combine(paths.Guests, "region-rights.json"), sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILogger<RegionRights>>()));
builder.Services.AddSingleton(sp => new EmailTokens(Path.Combine(paths.Guests, "email-tokens.json"), sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILogger<EmailTokens>>()));
// The proof-of-work key lives in the owner-only security folder; one key for the life of the install. The image CAPTCHA signs with
// the same key (its signatures carry a label of their own), so it is read once, when the first of the two is needed.
var powKey = new Lazy<byte[]>(() => ProofOfWork.LoadOrCreateKey(Path.Combine(paths.Security, "pow.key")));
builder.Services.AddSingleton(sp => new ProofOfWork(powKey.Value, sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton(sp => new ImageCaptcha(powKey.Value, sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<BotGuard>();
builder.Services.AddSingleton<LegacyCommandApi>();
builder.Services.AddSingleton<IOnlineUsers>(sp => new OnlineUsers(sp.GetRequiredService<Func<LauncherSettings>>(),
    sp.GetRequiredService<ILogger<OnlineUsers>>()));
builder.Services.AddSingleton<GridActions>();
builder.Services.AddSingleton<RegionActions>();
builder.Services.AddSingleton<RegionRestarts>();
builder.Services.AddSingleton<SetupProgress>();
builder.Services.AddSingleton<SetupRunner>();
builder.Services.AddSingleton<RegionJob>();
builder.Services.AddSingleton<OarLibrary>();
builder.Services.AddSingleton<UploadPartFiles>();
// The add-region form's memory advice; tests swap it.
builder.Services.AddSingleton<Func<long?>>(_ => SystemMemory.AvailableBytes);
// The reason a CPU priority can't be chosen on this computer; tests swap it.
builder.Services.AddSingleton<Func<CpuPriority, string?>>(_ => RegionSettingsRules.PriorityUnavailable);
builder.Services.AddHostedService<GridHostedService>();
// Moves regions into their owner's estate and back while they run; registered after GridHostedService, so it stops first.
builder.Services.AddHostedService<RegionEstateSync>();
// Registered after GridHostedService, so it stops first: running work is awaited before the controller detaches.
builder.Services.AddSingleton<BackgroundWork>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BackgroundWork>());
builder.Services.AddSingleton<OarJobs>();

var app = builder.Build();
// Routing is stated twice, so the endpoint is known to everything below (ApiEndpoint.Is) and picked again once SetupGate has
// shown the front page at "/" (it changes the path to /home and drops the endpoint chosen for "/"). A request that already has
// an endpoint is not matched again.
app.UseRouting();
// First, so static files, API answers and the plain-text replies carry the browser security headers too.
app.UseMiddleware<SecurityHeaders>();
app.UseStaticFiles();
// Callers outside the owner's allow-list get 404 before anything reads their cookie.
app.UseMiddleware<AdminNetworkFilter>();
app.UseAuthentication();
app.UseMiddleware<AuditMiddleware>();
// Someone signed in only to set up two-factor reaches nothing else (before SetupGate, which can send "/" onwards).
app.UseMiddleware<TwoFactorGate>();
app.UseMiddleware<SetupGate>();
app.UseRouting();
app.UseAuthorization();
LegacyEndpoints.Map(app);
LogsApi.Map(app);
BotsApi.Map(app);
RegionsApi.Map(app);
LibraryApi.Map(app);
StatusApi.Map(app);
SetupApi.Map(app);
app.MapRazorPages();

app.Lifetime.ApplicationStarted.Register(() =>
{
    // Plan 3's installers print and open this link; it is shown until the owner avatar exists.
    var s = app.Services.GetRequiredService<IGridController>().CurrentSettings();
    if (s.SetupToken is { } token && !s.Setup.OwnerCreated)
        app.Logger.LogInformation("Setup link: http://{Host}:{Port}/setup?token={Token}",
            HostAddress.Effective(s.Hostname), s.WebPort, token);
});

// Uploads and saves cut off by the last stop leave parts behind; nothing is using them yet.
app.Services.GetRequiredService<OarLibrary>().ClearStaging();

try
{
    await app.StartAsync();
}
catch (Exception e) when (IsAddressInUse(e))
{
    // The website cannot show this one: nothing is listening.
    app.Logger.LogCritical("Port {Port} (Web) is already in use by another program.", settings.WebPort);
    await app.DisposeAsync();
    return 1;
}

await app.WaitForShutdownAsync();
await app.DisposeAsync();
return 0;

static Task ApiOr(RedirectContext<CookieAuthenticationOptions> c, int apiStatus)
{
    if (ApiEndpoint.Is(c.HttpContext))
        c.Response.StatusCode = apiStatus;
    else
        c.Response.Redirect(c.RedirectUri);
    return Task.CompletedTask;
}

// A signed-in person whose role doesn't include the page: a 403 with a short page of its own (the sign-in page would
// only send them back here).
static Task Denied(RedirectContext<CookieAuthenticationOptions> c)
{
    c.Response.StatusCode = StatusCodes.Status403Forbidden;
    if (ApiEndpoint.Is(c.HttpContext)) return Task.CompletedTask;
    c.Response.ContentType = "text/html; charset=utf-8";
    return c.Response.WriteAsync("""
        <!DOCTYPE html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>Not allowed</title>
        <link rel="stylesheet" href="/lib/bootstrap/bootstrap.min.css">
        <link rel="stylesheet" href="/css/site.css">
        </head>
        <body>
        <main class="container py-4">
        <h1>You can't open this page</h1>
        <p>Your role doesn't include this page.</p>
        <p><a href="/">Back to your start page</a></p>
        </main>
        </body>
        </html>
        """);
}

static bool IsAddressInUse(Exception? e)
{
    for (; e is not null; e = e.InnerException)
        if (e is AddressInUseException) return true;
    return false;
}

public partial class Program { }
