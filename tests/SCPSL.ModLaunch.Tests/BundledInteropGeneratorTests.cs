using System.Reflection;
using System.Runtime.Loader;
using SCPSL.ModLaunch.ModPatch.Headless;
using Xunit;

namespace SCPSL.ModLaunch.Tests;

public sealed class BundledInteropGeneratorTests
{
    [Fact]
    public void BundledGeneratorHasTheFixedRevisionAndMelonLoaderCompatibleAssemblyIdentity()
    {
        var context = new AssemblyLoadContext("BundledGeneratorTest", isCollectible: true);
        try
        {
            var assembly = BundledInteropGenerator.Load(context);
            Assert.Equal("Il2CppInterop.Generator", assembly.GetName().Name);
            Assert.Equal(new Version(1, 5, 1, 0), assembly.GetName().Version);
            Assert.Equal("1.5.3+" + BundledInteropGenerator.Revision,
                assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
            var common = Assert.Single(assembly.GetReferencedAssemblies(), name => name.Name == "Il2CppInterop.Common");
            Assert.Equal(new Version(1, 5, 1, 0), common.Version);
        }
        finally { context.Unload(); }
    }
}
