using RoboCapture.NikonAdapter;
using Xunit;
namespace RoboCapture.Core.Tests;

public sealed class NikonSdkInstallationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "robocapture-sdk-tests", Guid.NewGuid().ToString("N"));
    private static readonly string[] Names = ["DC_PTP_Config.config", "MaidLayer.config", "RangeValue.config"];

    [Fact]
    public void RepairsMissingAndEmptyProfilesWithoutReplacingExistingProfiles()
    {
        var master = Path.Combine(_root, "master");
        var user = Path.Combine(_root, "user");
        Directory.CreateDirectory(master);
        Directory.CreateDirectory(user);
        foreach (var name in Names) File.WriteAllText(Path.Combine(master, name), "master-" + name);
        File.WriteAllText(Path.Combine(user, Names[0]), "existing");
        File.WriteAllText(Path.Combine(user, Names[1]), "");
        NikonSdkInstallation.EnsureProfiles(master, user);
        NikonSdkInstallation.EnsureProfiles(master, user);
        Assert.Equal("existing", File.ReadAllText(Path.Combine(user, Names[0])));
        foreach (var name in Names.Skip(1))
            Assert.Equal("master-" + name, File.ReadAllText(Path.Combine(user, name)));
        Assert.Empty(Directory.GetFiles(user, "*.tmp"));
    }

    [Fact]
    public void ResolvesSdkFromApplicationOrCheckoutRegardlessOfWorkingDirectory()
    {
        var sdk = Path.Combine(_root, "vendor-sdks", "nikon");
        var executable = Path.Combine(_root, "src", "app", "bin");
        Directory.CreateDirectory(sdk);
        Directory.CreateDirectory(executable);
        Assert.Equal(sdk, NikonSdkInstallation.ResolveModuleDirectory("vendor-sdks/nikon", executable));
        Assert.Equal(sdk, NikonSdkInstallation.ResolveModuleDirectory(sdk, executable));
        var bundled = Path.Combine(executable, "vendor-sdks", "nikon");
        Directory.CreateDirectory(bundled);
        Assert.Equal(bundled, NikonSdkInstallation.ResolveModuleDirectory("vendor-sdks/nikon", executable));
    }

    [Fact]
    public void MissingMasterGivesActionableError()
    {
        var error = Assert.Throws<FileNotFoundException>(() =>
            NikonSdkInstallation.EnsureProfiles(Path.Combine(_root, "missing"), Path.Combine(_root, "user")));
        Assert.Contains(Names[0], error.Message);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
