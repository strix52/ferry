using Ferry;
using Xunit;

namespace Ferry.Tests;

public class StartupModeTests
{
    [Theory]
    [InlineData(new string[0], true)]
    [InlineData(new[] { "--minimized" }, true)]
    [InlineData(new[] { "--show-window" }, false)]
    [InlineData(new[] { "--SHOW-WINDOW" }, false)]
    public void StartupDefaultsToTrayUnlessWindowRequested(string[] args, bool expected)
    {
        Assert.Equal(expected, App.ShouldStartInTray(args));
    }
}
