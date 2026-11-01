using System.Security.Cryptography;
using System.Text;
using DreamGrid.Launcher.Core.OpenSim;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class RobustPasswordsTests
{
    private static string Md5Hex(string s) => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(s)));

    [Fact]
    public void Hash_matches_opensim()
    {
        // AuthenticationServiceBase.SetPassword: Util.Md5Hash(Util.Md5Hash(password) + ":" + passwordSalt), lower-case hex.
        const string salt = "0123456789abcdef0123456789abcdef";
        Assert.Equal(Md5Hex(Md5Hex("secret-pass") + ":" + salt), OpenSimPassword.Hash("secret-pass", salt));
    }

    [Fact]
    public void New_salt_is_32_hex()
    {
        var a = OpenSimPassword.NewSalt();
        Assert.Matches("^[0-9a-f]{32}$", a);
        Assert.NotEqual(a, OpenSimPassword.NewSalt());
    }

    [Fact]
    public async Task Fake_accepts_the_new_password()
    {
        var robust = new FakeRobustAccounts().Add("Ann", "Smith", 0, "old-password", email: "ann@example.com");
        var ann = robust.All.Single();
        Assert.Equal("ann@example.com", ann.Email);

        await ((IRobustPasswords)robust).SetAsync(ann.PrincipalId, "new-password", CancellationToken.None);

        Assert.True(await robust.AuthenticateAsync(ann.PrincipalId, "new-password", CancellationToken.None));
        Assert.False(await robust.AuthenticateAsync(ann.PrincipalId, "old-password", CancellationToken.None));
    }

    [Fact]
    public async Task Fake_can_be_down()
    {
        var robust = new FakeRobustAccounts().Add("Ann", "Smith", 0, "old-password");
        robust.PasswordsDown = true;
        await Assert.ThrowsAsync<RobustPasswordsUnavailableException>(() =>
            ((IRobustPasswords)robust).SetAsync(robust.All.Single().PrincipalId, "new-password", CancellationToken.None));
    }
}
