using DreamGrid.Launcher.Core.Email;
using DreamGrid.Launcher.Email;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>The mail accounts the grid sends through, kept with their passwords protected, in the owner's order.</summary>
public sealed class EmailAccountsTests : IDisposable
{
    private const string Secret = "abcd efgh ijkl mnop";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dg-emailaccounts-" + Guid.NewGuid().ToString("N"));
    private readonly IDataProtectionProvider _dp = new EphemeralDataProtectionProvider();

    public EmailAccountsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string File => Path.Combine(_dir, "accounts.json");

    private EmailAccounts Store() => new(File, _dp);

    private static EmailAccount Gmail(Guid? id = null) =>
        new(id ?? Guid.NewGuid(), "gmail", "smtp.gmail.com", 587, MailSecurity.StartTls, "me@gmail.com", null, "Avalon", "me@gmail.com",
            null, 500, null, false, 0, null, null);

    [Fact]
    public void Password_is_not_stored_in_clear()
    {
        var store = Store();
        Assert.True(store.Save(Gmail(), Secret));
        Assert.DoesNotContain("efgh", System.IO.File.ReadAllText(File));
        var saved = Assert.Single(store.All());
        Assert.Equal(Secret, store.PasswordOf(saved));
        Assert.Equal(1, saved.Order);
    }

    [Fact]
    public void Blank_password_keeps_stored_one()
    {
        var store = Store();
        var a = Gmail();
        store.Save(a, Secret);
        Assert.True(store.Save(a with { FromName = "New name" }, ""));
        Assert.True(store.Save(a with { FromName = "Newer" }, null));
        var saved = store.Find(a.Id)!;
        Assert.Equal("Newer", saved.FromName);
        Assert.Equal(Secret, store.PasswordOf(saved));
        Assert.True(store.Save(a, "other"));
        Assert.Equal("other", store.PasswordOf(store.Find(a.Id)!));
    }

    [Fact]
    public void Save_clears_problem()
    {
        var store = Store();
        var a = Gmail();
        store.Save(a, Secret);
        var now = DateTimeOffset.UtcNow;
        Assert.True(store.MarkProblem(a.Id, "The server refused the password", now));
        Assert.Equal("The server refused the password", store.Find(a.Id)!.Problem);
        Assert.Equal(now, store.Find(a.Id)!.ProblemSince);
        store.Save(store.Find(a.Id)!, null);
        Assert.Null(store.Find(a.Id)!.Problem);
        Assert.Null(store.Find(a.Id)!.ProblemSince);
    }

    [Fact]
    public void Move_and_remove_keep_order_contiguous()
    {
        var store = Store();
        var a = Gmail();
        var b = Gmail();
        var c = Gmail();
        store.Save(a, Secret);
        store.Save(b, Secret);
        store.Save(c, Secret);
        Assert.Equal([a.Id, b.Id, c.Id], store.All().Select(x => x.Id));
        Assert.True(store.Move(c.Id, -1));
        Assert.Equal([a.Id, c.Id, b.Id], store.All().Select(x => x.Id));
        Assert.False(store.Move(a.Id, -1)); // already first
        Assert.True(store.Remove(a.Id));
        Assert.Equal([1, 2], store.All().Select(x => x.Order));
        Assert.Equal([c.Id, b.Id], store.All().Select(x => x.Id));
        Assert.False(store.Remove(a.Id));
        // A new store over the same file sees the same order.
        Assert.Equal([c.Id, b.Id], Store().All().Select(x => x.Id));
    }

    [Fact]
    public void Damaged_file_reads_as_no_accounts_and_is_moved_aside()
    {
        System.IO.File.WriteAllText(File, "{not json");
        var store = Store();
        Assert.Empty(store.All());
        Assert.False(store.IsReady);
        Assert.Single(Directory.GetFiles(_dir, "accounts.json.damaged-*"));
    }

    [Fact]
    public void IsReady_false_when_all_paused_or_failing()
    {
        var store = Store();
        Assert.False(store.IsReady);
        var a = Gmail();
        store.Save(a, null); // no password at all
        Assert.False(store.IsReady);
        store.Save(a, Secret);
        Assert.True(store.IsReady);
        store.SetPaused(a.Id, true);
        Assert.False(store.IsReady);
        store.SetPaused(a.Id, false);
        store.MarkProblem(a.Id, "broken", DateTimeOffset.UtcNow);
        Assert.False(store.IsReady);
    }

    [Fact]
    public void Password_from_another_key_ring_reads_as_none()
    {
        var a = Gmail();
        Store().Save(a, Secret);
        var other = new EmailAccounts(File, new EphemeralDataProtectionProvider());
        Assert.Null(other.PasswordOf(other.Find(a.Id)!));
        Assert.False(other.IsReady);
    }
}
