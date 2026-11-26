using System.Linq;
using System.Reflection;
using DevelApp.PublicApiSurface.Tests.Fixtures;
using Mono.Cecil;
using Xunit;

namespace DevelApp.PublicApiSurface.Tests;

public class ApiSurfaceTests
{
    private static IReadOnlyList<string> ExtractFixtureSurface()
    {
        var location = typeof(VisibleClass).Assembly.Location;
        using var assembly = AssemblyDefinition.ReadAssembly(location);
        return ApiSurface.Extract(
            assembly,
            type => type.Namespace?.StartsWith("DevelApp.PublicApiSurface.Tests.Fixtures", StringComparison.Ordinal) == true);
    }

    private static string TypeName => typeof(VisibleClass).FullName!;

    [Fact]
    public void PublicClassIsListedWithBaseAndInterfaces()
    {
        var surface = ExtractFixtureSurface();
        Assert.Contains($"T:{TypeName}", surface);
        Assert.Contains($"T:{TypeName} : base {typeof(SampleBase).FullName}", surface);
        Assert.Contains($"T:{TypeName} : implements {typeof(ISampleContract).FullName}", surface);
    }

    [Fact]
    public void PublicMembersAreListed()
    {
        var surface = ExtractFixtureSurface();
        Assert.Contains($"M:{TypeName}.Add(System.Int32,System.Int32)", surface);
        Assert.Contains($"P:{TypeName}.Name:get;set:{typeof(string).FullName}", surface);
        Assert.Contains($"E:{TypeName}.SomethingHappened:{typeof(EventHandler).FullName}", surface);
        Assert.Contains($"F:{TypeName}.PublicField:{typeof(int).FullName}", surface);
        Assert.Contains($"F:{TypeName}.MaxTokenLength:{typeof(int).FullName}:const=128", surface);
        Assert.Contains($"M:{TypeName}.#ctor()", surface);
    }

    [Fact]
    public void ProtectedMembersAreListed()
    {
        var surface = ExtractFixtureSurface();
        Assert.Contains($"M:{TypeName}.ProtectedMethod()", surface);
        Assert.Contains($"F:{TypeName}.ProtectedField:{typeof(int).FullName}", surface);
        // Cecil renders nested types with a '/' separator.
        Assert.Contains($"T:{TypeName}/NestedProtected", surface);
    }

    [Fact]
    public void InternalAndPrivateMembersAreExcluded()
    {
        var surface = ExtractFixtureSurface();
        Assert.DoesNotContain(surface, l => l.Contains("InternalField"));
        Assert.DoesNotContain(surface, l => l.Contains("InternalMethod"));
        Assert.DoesNotContain(surface, l => l.Contains("InternalProperty"));
        Assert.DoesNotContain(surface, l => l.Contains("InternalEvent"));
        Assert.DoesNotContain(surface, l => l.Contains("NestedInternal"));
        Assert.DoesNotContain(surface, l => l.Contains("NestedPrivate"));
        Assert.DoesNotContain(surface, l => l.Contains("_privateField"));
        Assert.DoesNotContain(surface, l => l.Contains("PrivateMethod"));
    }

    [Fact]
    public void InternalTypesAreExcludedEntirely()
    {
        var surface = ExtractFixtureSurface();
        Assert.DoesNotContain(surface, l => l.Contains(typeof(InternalClass).FullName!));
    }

    [Fact]
    public void PropertyAccessorVisibilityIsRepresented()
    {
        var surface = ExtractFixtureSurface();
        // TokenCount has a public getter and a private setter:
        // only the getter is surface.
        Assert.Contains($"P:{TypeName}.TokenCount:get:{typeof(int).FullName}", surface);
        Assert.DoesNotContain(surface, l => l.Contains("TokenCount:get;set"));
    }

    [Fact]
    public void MethodSignatureDetailsAreRepresented()
    {
        var surface = ExtractFixtureSurface();
        // params arrays, default values, by-ref markers, and generic arity
        // are all part of the interface contract.
        Assert.Contains($"M:{TypeName}.WithParams(params {typeof(string).FullName}[])", surface);
        Assert.Contains($"M:{TypeName}.WithDefaults(System.Int32=3,{typeof(string).FullName}=\"x\")", surface);
        Assert.Contains($"M:{TypeName}.ByReference(System.Int32&,System.Boolean&):System.Boolean", surface);
        Assert.Contains($"M:{TypeName}.Convert``2(TInput):TResult", surface);
    }

    [Fact]
    public void OverrideRelationIsRepresentedAsPublicMethod()
    {
        var surface = ExtractFixtureSurface();
        Assert.Contains($"M:{TypeName}.Reset()", surface);
    }

    [Fact]
    public void SurfaceIsSortedAndDeterministic()
    {
        var first = ExtractFixtureSurface();
        var second = ExtractFixtureSurface();
        Assert.Equal(first, second);
        var sorted = first.OrderBy(l => l, StringComparer.Ordinal).ToList();
        Assert.Equal(sorted, first.ToList());
    }

    [Fact]
    public void SurfaceDoesNotContainCompilerGeneratedBackingMembers()
    {
        var surface = ExtractFixtureSurface();
        Assert.DoesNotContain(surface, l => l.Contains("k__BackingField"));
        Assert.DoesNotContain(surface, l => l.Contains("add_"));
        Assert.DoesNotContain(surface, l => l.Contains("get_"));
    }
}
