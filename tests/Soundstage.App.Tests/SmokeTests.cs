using Xunit;

namespace Soundstage.App.Tests;

public class SmokeTests
{
    [Fact]
    public void AppAssembly_Loads()
    {
        // Touching a type from the App assembly proves the WPF assembly loads on the test host.
        Assert.NotNull(typeof(Soundstage.App.App).Assembly);
    }
}
