using System.Text.RegularExpressions;
using DreamGrid.Launcher.Core.Security;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class BackupCodesTests
{
    [Fact]
    public void New_gives_ten_distinct_codes_in_the_right_shape()
    {
        var codes = BackupCodes.New();
        Assert.Equal(10, codes.Count);
        Assert.Equal(10, codes.Distinct().Count());
        foreach (var c in codes)
            Assert.Matches(new Regex("^[a-hjkmnp-z2-9]{5}-[a-hjkmnp-z2-9]{5}$"), c);
    }

    [Fact]
    public void Hash_ignores_case_dash_and_spaces()
    {
        Assert.Equal(BackupCodes.Hash("abcdefghij"), BackupCodes.Hash("ABCDE-fghij"));
        Assert.Equal(BackupCodes.Hash("abcdefghij"), BackupCodes.Hash(" abcde fghij "));
        Assert.NotEqual(BackupCodes.Hash("abcdefghij"), BackupCodes.Hash("abcdefghik"));
        Assert.Matches("^[0-9a-f]{64}$", BackupCodes.Hash("abcdefghij"));
    }
}
