using System.Text;
using DreamGrid.Launcher.Core.Security;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class TotpTests
{
    private static readonly byte[] Rfc = Encoding.ASCII.GetBytes("12345678901234567890");

    private static DateTimeOffset At(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix);

    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    public void Rfc6238_sha1_vectors(long unix, string expected)
    {
        Assert.Equal(expected, Totp.Code(Rfc, Totp.StepAt(At(unix))));
    }

    [Fact]
    public void StepAt_is_unix_seconds_over_30()
    {
        Assert.Equal(1, Totp.StepAt(At(59)));
        Assert.Equal(2, Totp.StepAt(At(60)));
        Assert.Equal(0, Totp.StepAt(At(29)));
    }

    [Fact]
    public void Accepts_one_step_either_side_only()
    {
        var now = At(1_700_000_000);
        var step = Totp.StepAt(now);
        Assert.True(Totp.Verify(Rfc, Totp.Code(Rfc, step), now, null, out var m0));
        Assert.Equal(step, m0);
        Assert.True(Totp.Verify(Rfc, Totp.Code(Rfc, step - 1), now, null, out var m1));
        Assert.Equal(step - 1, m1);
        Assert.True(Totp.Verify(Rfc, Totp.Code(Rfc, step + 1), now, null, out var p1));
        Assert.Equal(step + 1, p1);
        Assert.False(Totp.Verify(Rfc, Totp.Code(Rfc, step - 2), now, null, out _));
        Assert.False(Totp.Verify(Rfc, Totp.Code(Rfc, step + 2), now, null, out _));
    }

    [Fact]
    public void Verify_refuses_reused_step()
    {
        var now = At(1_700_000_000);
        var step = Totp.StepAt(now);
        var code = Totp.Code(Rfc, step);
        Assert.True(Totp.Verify(Rfc, code, now, null, out var matched));
        // The same code again, and the code of any earlier step, are refused once that step is used, even inside the +-1 window.
        Assert.False(Totp.Verify(Rfc, code, now, matched, out _));
        Assert.False(Totp.Verify(Rfc, Totp.Code(Rfc, step - 1), now, matched, out _));
        // A later step in the window is still fine.
        Assert.True(Totp.Verify(Rfc, Totp.Code(Rfc, step + 1), now, matched, out var next));
        Assert.Equal(step + 1, next);
    }

    [Fact]
    public void Verify_ignores_spaces_and_refuses_malformed()
    {
        var now = At(1_700_000_000);
        var code = Totp.Code(Rfc, Totp.StepAt(now));
        Assert.True(Totp.Verify(Rfc, code[..3] + " " + code[3..], now, null, out _));
        Assert.False(Totp.Verify(Rfc, "", now, null, out _));
        Assert.False(Totp.Verify(Rfc, code + "0", now, null, out _));
        Assert.False(Totp.Verify(Rfc, "abcdef", now, null, out _));
    }

    [Fact]
    public void NewSecret_is_20_random_bytes()
    {
        var a = Totp.NewSecret();
        Assert.Equal(20, a.Length);
        Assert.NotEqual(a, Totp.NewSecret());
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("f", "MY")]
    [InlineData("fo", "MZXQ")]
    [InlineData("foo", "MZXW6")]
    [InlineData("foob", "MZXW6YQ")]
    [InlineData("fooba", "MZXW6YTB")]
    [InlineData("foobar", "MZXW6YTBOI")]
    public void Base32_rfc4648_vectors(string text, string expected)
    {
        Assert.Equal(expected, Totp.Base32(Encoding.ASCII.GetBytes(text)));
    }

    [Fact]
    public void OtpAuthUri_has_the_parts_and_escapes()
    {
        var uri = Totp.OtpAuthUri("Dream Grid", "Ann Admin", Rfc);
        Assert.Equal("otpauth://totp/Dream%20Grid:Ann%20Admin?secret=" + Totp.Base32(Rfc) +
                     "&issuer=Dream%20Grid&digits=6&period=30", uri);
    }
}
