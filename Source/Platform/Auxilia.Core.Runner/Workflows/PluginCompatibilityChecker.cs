using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Auxilia.Core.Runner.Workflows;

/// <summary>
/// Pre-flight linker check for injected slot plugins: every member a plugin references on a
/// shared assembly must exist in the copy the workflow image ships. Catches plugin/image build
/// skew (a stale image next to a freshly built plugin) at LAUNCH with a clear message instead
/// of a cryptic <see cref="MissingMethodException"/> mid-session. Debug/Release and version
/// numbers are irrelevant — only the referenced member surface is compared.
/// </summary>
internal static class PluginCompatibilityChecker
{
    /// <summary>Simple assembly names the plugin references (e.g. "Auxilia.Workflows.AiAgent").</summary>
    internal static IReadOnlyList<string> ReferencedAssemblyNames(byte[] pluginAssembly)
    {
        using var pe = new PEReader(new MemoryStream(pluginAssembly));
        var metadata = pe.GetMetadataReader();
        return metadata.AssemblyReferences
            .Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name))
            .ToList();
    }

    /// <summary>
    /// Members the plugin references into any of <paramref name="targetAssembliesByName"/>
    /// (keyed by simple assembly name) that the target does NOT define — empty = compatible.
    /// </summary>
    internal static IReadOnlyList<string> FindMissingReferences(
        byte[] pluginAssembly, IReadOnlyDictionary<string, byte[]> targetAssembliesByName)
    {
        var definedByAssembly = targetAssembliesByName.ToDictionary(
            kv => kv.Key, kv => DefinedMembers(kv.Value), StringComparer.OrdinalIgnoreCase);

        var missing = new List<string>();
        using var pe = new PEReader(new MemoryStream(pluginAssembly));
        var metadata = pe.GetMetadataReader();
        foreach (var handle in metadata.MemberReferences)
        {
            var member = metadata.GetMemberReference(handle);
            // Nested types and TypeSpec parents (generic instantiations) are skipped — the
            // plain namespace-scoped surface covers the contract assemblies this guards.
            if (member.Parent.Kind != HandleKind.TypeReference)
                continue;
            var type = metadata.GetTypeReference((TypeReferenceHandle)member.Parent);
            if (type.ResolutionScope.Kind != HandleKind.AssemblyReference)
                continue;
            var assemblyName = metadata.GetString(
                metadata.GetAssemblyReference((AssemblyReferenceHandle)type.ResolutionScope).Name);
            if (!definedByAssembly.TryGetValue(assemblyName, out var defined))
                continue;
            var key = $"{metadata.GetString(type.Namespace)}.{metadata.GetString(type.Name)}"
                      + "::" + metadata.GetString(member.Name);
            if (!defined.Contains(key))
                missing.Add($"{assemblyName}: {key}");
        }
        return missing.Distinct().ToList();
    }

    /// <summary>"Namespace.Type::member" for every method and field the assembly defines.</summary>
    private static HashSet<string> DefinedMembers(byte[] assembly)
    {
        var defined = new HashSet<string>(StringComparer.Ordinal);
        using var pe = new PEReader(new MemoryStream(assembly));
        var metadata = pe.GetMetadataReader();
        foreach (var typeHandle in metadata.TypeDefinitions)
        {
            var type = metadata.GetTypeDefinition(typeHandle);
            var typeName = $"{metadata.GetString(type.Namespace)}.{metadata.GetString(type.Name)}";
            foreach (var methodHandle in type.GetMethods())
                defined.Add(typeName + "::" + metadata.GetString(
                    metadata.GetMethodDefinition(methodHandle).Name));
            foreach (var fieldHandle in type.GetFields())
                defined.Add(typeName + "::" + metadata.GetString(
                    metadata.GetFieldDefinition(fieldHandle).Name));
        }
        return defined;
    }
}
