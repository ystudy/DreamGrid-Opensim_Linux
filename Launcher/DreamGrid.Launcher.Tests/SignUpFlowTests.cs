using System.Net;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>Sign-up from end to end through the real website: the owner opens it, a new person signs up, confirms and signs in.</summary>
public sealed class SignUpFlowTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Password = "tall-green-kettle";

    private static async Task<IHtmlDocument> PageAsync(HttpClient client, string url) =>
        new HtmlParser().ParseDocument(await client.GetStringAsync(url, Ct));

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url, Dictionary<string, string> fields, string tokenFrom)
    {
        var token = (await PageAsync(client, tokenFrom)).QuerySelector("input[name=__RequestVerificationToken]")!.GetAttribute("value")!;
        return await client.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string>(fields) { ["__RequestVerificationToken"] = token }), Ct);
    }

    private static async Task OwnerOpensAsync(SignUpsTests.Rig rig)
    {
        var owner = rig.F.CreateClient(new() { AllowAutoRedirect = false });
        (await owner.GetAsync("/__test/signin?role=owner", Ct)).Dispose();
        using var r = await PostAsync(owner, "/security/legal?handler=SignUp", new() { ["SignUpOpen"] = "true" }, "/security/legal");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
    }

    private static Task<HttpResponseMessage> SignInAsync(HttpClient client) =>
        PostAsync(client, "/guest/sign-in", new() { ["Name"] = "Bea Example", ["Password"] = Password }, "/guest/sign-in");

    [Fact]
    public async Task Full_sign_up_with_email()
    {
        await using var rig = SignUpsTests.Factory(open: false);
        await OwnerOpensAsync(rig);
        var person = rig.F.CreateClient(new() { AllowAutoRedirect = false });

        using (var signUp = await SignUpPageTests.PostSignUpAsync(person))
            Assert.Contains(SignUpPageTests.CheckEmail, await signUp.Content.ReadAsStringAsync(Ct));
        using (var early = await SignInAsync(person))
            Assert.Contains("Confirm your email first.", await early.Content.ReadAsStringAsync(Ct));

        var token = SignUpsTests.LinkToken(Assert.Single(await SignUpsTests.SentAsync(rig, 1)).Email);
        using (var confirm = await person.GetAsync($"/guest/confirm?token={token}", Ct))
            Assert.Equal("/guest/connect", confirm.Headers.Location!.OriginalString);
        Assert.Contains("Log in as Bea Example", (await PageAsync(person, "/guest/connect")).QuerySelector("main")!.TextContent);

        // A fresh browser signs in with the password typed at sign-up.
        var later = rig.F.CreateClient(new() { AllowAutoRedirect = false });
        using var signIn = await SignInAsync(later);
        Assert.Equal(HttpStatusCode.Redirect, signIn.StatusCode);
        Assert.Equal("/guest", signIn.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Full_sign_up_without_email()
    {
        await using var rig = SignUpsTests.Factory(open: false, email: false);
        await OwnerOpensAsync(rig);
        var person = rig.F.CreateClient(new() { AllowAutoRedirect = false });

        using (var signUp = await SignUpPageTests.PostSignUpAsync(person))
            Assert.Equal("/guest/connect", signUp.Headers.Location!.OriginalString);
        Assert.Contains("Log in as Bea Example", (await PageAsync(person, "/guest/connect")).QuerySelector("main")!.TextContent);

        var later = rig.F.CreateClient(new() { AllowAutoRedirect = false });
        using var signIn = await SignInAsync(later);
        Assert.Equal("/guest", signIn.Headers.Location!.OriginalString);
        Assert.Empty(await SignUpsTests.SentAsync(rig, 1));
    }
}
