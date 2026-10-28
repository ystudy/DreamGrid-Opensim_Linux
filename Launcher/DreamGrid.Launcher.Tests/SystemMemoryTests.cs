using DreamGrid.Launcher.Core.Grid;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class SystemMemoryTests
{
    [Fact]
    public void MemInfo_reads_MemAvailable() =>
        Assert.Equal(5_200_000_000, SystemMemory.ParseMemInfo("MemTotal:        8000000 kB\nMemAvailable:  5078125 kB\n"));

    [Fact]
    public void MemInfo_without_MemAvailable_is_null() =>
        Assert.Null(SystemMemory.ParseMemInfo("MemTotal: 8000000 kB\nMemFree: 100 kB\n"));
}
