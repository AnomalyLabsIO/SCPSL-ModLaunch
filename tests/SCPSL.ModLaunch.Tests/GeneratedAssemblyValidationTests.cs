using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using SCPSL.ModLaunch.ModPatch.Headless;
using Xunit;

namespace SCPSL.ModLaunch.Tests;

public sealed class GeneratedAssemblyValidationTests
{
    [Fact]
    public void CompilerGeneratedHelpersWithDifferentDeclaringTypesRemainValid()
    {
        using var game = new TestGame();
        string path = WriteAssembly(game.Root,
            ("Fixture", "First", TypeAttributes.Public, 0),
            ("Fixture", "Second", TypeAttributes.Public, 0),
            ("", "<>O", TypeAttributes.NestedPrivate, 1),
            ("", "<>O", TypeAttributes.NestedPrivate, 2),
            ("", "<>O", TypeAttributes.NotPublic, 0));
        GeneratedAssemblyValidation.Validate(path);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DuplicateHelpersAreRejectedWithinTheirActualScope(bool nested)
    {
        using var game = new TestGame();
        var visibility = nested ? TypeAttributes.NestedPublic : TypeAttributes.Public;
        string path = WriteAssembly(game.Root,
            ("Fixture", "Parent", TypeAttributes.Public, 0),
            ("", "<>O", visibility, nested ? 1 : 0),
            ("", "<>O", visibility, nested ? 1 : 0));
        var error = Assert.Throws<InvalidDataException>(() => GeneratedAssemblyValidation.Validate(path));
        Assert.Contains("duplicate type", error.Message);
    }

    [Fact]
    public void NestedNamespaceDoesNotHideDuplicateIdentity()
    {
        using var game = new TestGame();
        string path = WriteAssembly(game.Root,
            ("Fixture", "Parent", TypeAttributes.Public, 0),
            ("First", "Helper", TypeAttributes.NestedPublic, 1),
            ("Second", "Helper", TypeAttributes.NestedPublic, 1));
        Assert.Contains("duplicate type", Assert.Throws<InvalidDataException>(() => GeneratedAssemblyValidation.Validate(path)).Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MismatchedNestingIsRejectedEvenWithoutDuplicateNames(bool hasParent)
    {
        using var game = new TestGame();
        string path = WriteAssembly(game.Root,
            ("Fixture", "Parent", TypeAttributes.Public, 0),
            ("", "Helper", hasParent ? TypeAttributes.Public : TypeAttributes.NestedPublic, hasParent ? 1 : 0));
        Assert.Contains("invalid nesting", Assert.Throws<InvalidDataException>(() => GeneratedAssemblyValidation.Validate(path)).Message);
    }

    [Fact]
    public void DifferentTopLevelNamespacesDoNotCollide()
    {
        using var game = new TestGame();
        string path = WriteAssembly(game.Root,
            ("First", "Helper", TypeAttributes.Public, 0),
            ("Second", "Helper", TypeAttributes.Public, 0));
        GeneratedAssemblyValidation.Validate(path);
    }

    [Fact]
    public void InvalidDllIsRejectedBeforePublication()
    {
        using var game = new TestGame();
        string path = Path.Combine(game.Root, "broken.dll");
        File.WriteAllText(path, "not a managed PE");
        Assert.Throws<InvalidDataException>(() => GeneratedAssemblyValidation.Validate(path));
    }

    private static string WriteAssembly(string directory, params (string Namespace, string Name, TypeAttributes Visibility, int Parent)[] types)
    {
        var metadata = new MetadataBuilder();
        metadata.AddModule(0, metadata.GetOrAddString("Fixture.dll"), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        metadata.AddAssembly(metadata.GetOrAddString("Fixture"), new Version(1, 0), default, default, 0, AssemblyHashAlgorithm.None);
        metadata.AddTypeDefinition(TypeAttributes.NotPublic, default, metadata.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        foreach (var type in types)
        {
            var handle = metadata.AddTypeDefinition(type.Visibility, metadata.GetOrAddString(type.Namespace), metadata.GetOrAddString(type.Name),
                default, MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
            if (type.Parent > 0) metadata.AddNestedType(handle, MetadataTokens.TypeDefinitionHandle(type.Parent + 1));
        }
        var image = new BlobBuilder();
        new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
            new MetadataRootBuilder(metadata), new BlobBuilder(), flags: CorFlags.ILOnly).Serialize(image);
        string path = Path.Combine(directory, "Fixture.dll");
        File.WriteAllBytes(path, image.ToArray());
        return path;
    }
}
