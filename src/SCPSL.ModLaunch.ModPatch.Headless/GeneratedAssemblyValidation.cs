using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace SCPSL.ModLaunch.ModPatch.Headless;

/// <summary>Checks generated metadata without loading or executing game assemblies.</summary>
internal static class GeneratedAssemblyValidation
{
    internal static int ValidateDirectory(string directory)
    {
        string[] assemblies = Directory.GetFiles(directory, "*.dll", SearchOption.TopDirectoryOnly);
        if (assemblies.Length == 0) throw new InvalidDataException("No generated assemblies were produced.");
        foreach (string assembly in assemblies) Validate(assembly);
        return assemblies.Length;
    }

    internal static void Validate(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            var metadata = pe.GetMetadataReader();
            var identities = new HashSet<(TypeDefinitionHandle Parent, string Namespace, string Name)>();
            foreach (var handle in metadata.TypeDefinitions)
            {
                var type = metadata.GetTypeDefinition(handle);
                var parent = type.GetDeclaringType();
                string name = metadata.GetString(type.Name);
                // The CLR scopes nested names by their declaring type, not by their namespace field.
                string ns = parent.IsNil ? metadata.GetString(type.Namespace) : "";
                if (!identities.Add((parent, ns, name)))
                    throw new InvalidDataException($"Generated assembly {Path.GetFileName(path)} contains a duplicate type: {ns}.{name}.");

                var visibility = type.Attributes & TypeAttributes.VisibilityMask;
                bool nestedVisibility = visibility is not TypeAttributes.Public and not TypeAttributes.NotPublic;
                if (nestedVisibility == parent.IsNil)
                    throw new InvalidDataException($"Generated assembly {Path.GetFileName(path)} has invalid nesting for type {name}.");
            }
        }
        catch (BadImageFormatException ex)
        {
            throw new InvalidDataException($"Generated assembly {Path.GetFileName(path)} has invalid managed metadata.", ex);
        }
    }
}
