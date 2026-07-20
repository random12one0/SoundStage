using Soundstage.Core;
using Xunit;

namespace Soundstage.Core.Tests;

public class SmokeTests
{
    [Fact]
    public void CoreAssembly_Loads()
    {
        Assert.Equal("Soundstage", CoreInfo.ProductName);
    }
}
