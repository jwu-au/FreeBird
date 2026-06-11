using FluentAssertions;
using FreeBird.Core.Provisioning;

namespace FreeBird.Core.Tests.Provisioning;

public class AppContextBaseDirectoryProviderTests
{
    [Fact]
    public void GetBaseDirectory_Returns_NonEmptyPath()
    {
        var provider = new AppContextBaseDirectoryProvider();
        var dir = provider.GetBaseDirectory();
        dir.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void GetBaseDirectory_Returns_ExistingDirectory()
    {
        var provider = new AppContextBaseDirectoryProvider();
        var dir = provider.GetBaseDirectory();
        Directory.Exists(dir).Should().BeTrue($"AppContext.BaseDirectory should be a real directory; got {dir}");
    }

    [Fact]
    public void GetBaseDirectory_StripsTrailingSeparator()
    {
        var provider = new AppContextBaseDirectoryProvider();
        var dir = provider.GetBaseDirectory();
        dir.Should().NotEndWith(Path.DirectorySeparatorChar.ToString());
        dir.Should().NotEndWith(Path.AltDirectorySeparatorChar.ToString());
    }

    [Fact]
    public void GetBaseDirectory_AbsolutePath()
    {
        var provider = new AppContextBaseDirectoryProvider();
        var dir = provider.GetBaseDirectory();
        Path.IsPathRooted(dir).Should().BeTrue();
    }
}
