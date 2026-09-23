using System.Reflection;
using DotNetCore.HayateOP.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HayateOP.Tests.DI;

/// <summary>
/// L8 moved the Microsoft.Extensions.Logging bridge out of <c>DotNetCore.HayateOP.Logging</c> and into
/// <c>DotNetCore.HayateOP.DependencyInjection</c>, leaving an obsolete shell in the old namespace so that
/// 2.x source keeps compiling. These cases pin the three halves of that: the new home, the shell's
/// existence and shape, and the fact that the shell is a separate type rather than the one the container
/// hands out.
/// The shell is reached by reflection on purpose: this file imports the moved namespace and nothing else,
/// because adding <c>using DotNetCore.HayateOP.Logging;</c> would make every moved name ambiguous
/// (CS0104) — the same cost the shell imposes on a consumer, and measured here rather than assumed. The
/// direct spelling, which is the part a consumer actually experiences, lives in
/// <see cref="LegacyLoggingNamespaceCallSiteTests"/>.
/// </summary>
public class LoggingNamespaceMoveTests
{
    private const string OldNamespace = "DotNetCore.HayateOP.Logging";
    private const string NewNamespace = "DotNetCore.HayateOP.DependencyInjection";
    private const string BridgeAssembly = "DotNetCore.HayateOP.Extensions.DependencyInjection";

    private static readonly string[] ShellNames =
    {
        "HayateMicrosoftLoggerAdapter",
        "HayateMicrosoftLoggerAdapter`1",
        "HayateMicrosoftLoggerFactory"
    };

    private static Type Shell(string typeName) =>
        Type.GetType(OldNamespace + "." + typeName + ", " + BridgeAssembly, throwOnError: true);

    [Fact]
    public void MovedTypes_ShouldLiveInTheDependencyInjectionNamespace()
    {
        Assert.Equal(NewNamespace, typeof(HayateMicrosoftLoggerAdapter).Namespace);
        Assert.Equal(NewNamespace, typeof(HayateMicrosoftLoggerAdapter<>).Namespace);
        Assert.Equal(NewNamespace, typeof(HayateMicrosoftLoggerFactory).Namespace);
    }

    [Fact]
    public void CompatibilityShells_ShouldStillResolveFromTheOldNamespace()
    {
        foreach (var name in ShellNames)
        {
            Assert.NotNull(Shell(name));
        }
    }

    [Fact]
    public void CompatibilityShells_ShouldBeObsoleteButNotBuildBreaks()
    {
        foreach (var name in ShellNames)
        {
            var obsolete = Shell(name).GetCustomAttribute<ObsoleteAttribute>();

            Assert.NotNull(obsolete);
            // A warning, not an error: 2.x source has to keep compiling without edits.
            Assert.False(obsolete.IsError);
            Assert.Contains(NewNamespace, obsolete.Message);
        }
    }

    [Fact]
    public void CompatibilityShells_ShouldDeriveFromTheMovedTypes()
    {
        // The shell adds nothing of its own, so deriving is the whole implementation. If this breaks, the
        // shell stopped being a shim and started being a copy that can drift.
        Assert.Equal(typeof(HayateMicrosoftLoggerAdapter), Shell("HayateMicrosoftLoggerAdapter").BaseType);
        // The generic shell's base is the *constructed* Base<T>, with T being the shell's own type
        // parameter — not the open definition — so the comparison has to go through
        // GetGenericTypeDefinition(). Comparing against BaseType directly fails here.
        Assert.Equal(typeof(HayateMicrosoftLoggerAdapter<>),
            Shell("HayateMicrosoftLoggerAdapter`1").BaseType.GetGenericTypeDefinition());
        Assert.Equal(typeof(HayateMicrosoftLoggerFactory), Shell("HayateMicrosoftLoggerFactory").BaseType);
    }

    [Fact]
    public void CompatibilityShells_ShouldBeDistinctTypesFromTheMovedOnes()
    {
        // A derived shell is not a type forward: the two names are two types, and the container hands out
        // the moved one. Anything comparing types across the two namespaces has to expect that.
        Assert.NotEqual(Shell("HayateMicrosoftLoggerAdapter"), typeof(HayateMicrosoftLoggerAdapter));
        Assert.NotEqual(Shell("HayateMicrosoftLoggerFactory"), typeof(HayateMicrosoftLoggerFactory));

        using var melFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace));
        var created = new HayateMicrosoftLoggerFactory(melFactory).CreateLogger("pool");

        Assert.IsType<HayateMicrosoftLoggerAdapter>(created);
        Assert.NotEqual(Shell("HayateMicrosoftLoggerAdapter"), created.GetType());
    }

    [Fact]
    public void ShellFactory_ShouldBehaveAsTheMovedOne()
    {
        // Source compatibility is only half of it: the shell has to work as well, and it does so through
        // the base class's members. Constructed by reflection because the type is obsolete; driven through
        // the IHayateLoggerFactory contract — qualified, since this file does not import the namespace
        // the interface still lives in — so that the assertions are about behaviour, not shape.
        using var melFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace));
        var instance = (DotNetCore.HayateOP.Logging.IHayateLoggerFactory)Activator.CreateInstance(
            Shell("HayateMicrosoftLoggerFactory"), new object[] { melFactory });

        var logger = instance.CreateLogger("pool");

        Assert.IsType<HayateMicrosoftLoggerAdapter>(logger);
    }
}
