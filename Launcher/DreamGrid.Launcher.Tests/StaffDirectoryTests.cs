using DreamGrid.Launcher.Core.Database;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class StaffDirectoryTests
{
    [Fact]
    public void List_by_email_has_no_limit()
    {
        Assert.Contains("LOWER(TRIM(Email)) = @email", StaffDirectory.AllByEmail);
        Assert.DoesNotContain("LIMIT", StaffDirectory.AllByEmail);
    }

    [Fact]
    public async Task Fake_list_by_email_returns_every_match()
    {
        var robust = new FakeRobustAccounts()
            .Add("Ann", "Smith", 0, "p", email: "Shared@Example.com ")
            .Add("Bob", "Jones", -1, "p", email: "shared@example.com")
            .Add("Cat", "Brown", 0, "p", email: "other@example.com");
        var found = await new FakeStaffDirectory(robust).ListByEmailAsync("shared@example.com", CancellationToken.None);
        Assert.Equal(["Ann", "Bob"], found.Select(a => a.FirstName).Order());
    }
}
