using HaPersonalAgent;

namespace HaPersonalAgent.Tests;

public class ApplicationInfoTests
{
    [Fact]
    public void ApplicationInfo_has_expected_skeleton_metadata()
    {
        Assert.Equal("Home Assistant Personal Agent", ApplicationInfo.Name);
        Assert.Equal("net8.0", ApplicationInfo.TargetFramework);
    }
}
