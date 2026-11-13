using GridGate.Launcher.Core.Database;
using GridGate.Launcher.Core.OpenSim;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>The one "Robust didn't answer" rule.</summary>
public sealed class RobustFailureTests
{
    [Theory]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(TaskCanceledException))]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(InvalidDataException))]
    [InlineData(typeof(FileNotFoundException))] // a subclass counts, as `is` does today
    public void The_five_kinds_are_unreachable_while_the_caller_waits(Type kind) =>
        Assert.True(RobustFailure.IsUnreachable((Exception)Activator.CreateInstance(kind)!, CancellationToken.None));

    [Theory]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(TaskCanceledException))]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(InvalidDataException))]
    [InlineData(typeof(FileNotFoundException))]
    public void Nothing_is_unreachable_once_the_caller_gave_up(Type kind) =>
        Assert.False(RobustFailure.IsUnreachable((Exception)Activator.CreateInstance(kind)!, new CancellationToken(canceled: true)));

    [Theory]
    [InlineData(typeof(OperationCanceledException))]
    [InlineData(typeof(TimeoutException))]
    [InlineData(typeof(ArgumentException))]
    public void Other_failures_are_not_unreachable(Type kind) =>
        Assert.False(RobustFailure.IsUnreachable((Exception)Activator.CreateInstance(kind)!, CancellationToken.None));

    [Fact]
    public void The_staff_list_is_not_part_of_the_rule() =>
        Assert.False(RobustFailure.IsUnreachable(new StaffDirectoryUnavailableException("x"), CancellationToken.None));
}
