using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Civil3DMcpPlugin;

/// <summary>
/// Scripting's default resolver, except that an assembly it finds beyond the listed references is read once.
/// <para>
/// A reference can depend on an assembly that isn't loaded yet, and Roslyn asks the resolver for it during every compile.
/// The default answers with a new reference each time, and each one holds a native copy of the file, which is how a
/// new script cost hundreds of MB that the cached script then kept alive. The GC can't see that memory.
/// </para>
/// </summary>
internal sealed class ScriptReferenceResolver : MetadataReferenceResolver
{
  private readonly MetadataReferenceResolver _inner;
  private readonly ConcurrentDictionary<string, PortableExecutableReference?> _resolved = new(StringComparer.OrdinalIgnoreCase);

  public ScriptReferenceResolver(MetadataReferenceResolver inner)
  {
    _inner = inner;
  }

  /// <summary>How many assemblies have been found beyond the listed references (logged after each compile).</summary>
  public int ResolvedCount => _resolved.Count;

  public override bool ResolveMissingAssemblies => _inner.ResolveMissingAssemblies;

  public override PortableExecutableReference? ResolveMissingAssembly(MetadataReference definition, AssemblyIdentity referenceIdentity)
  {
    return _resolved.GetOrAdd(referenceIdentity.GetDisplayName(), _ => _inner.ResolveMissingAssembly(definition, referenceIdentity));
  }

  public override ImmutableArray<PortableExecutableReference> ResolveReference(string reference, string? baseFilePath, MetadataReferenceProperties properties)
  {
    return _inner.ResolveReference(reference, baseFilePath, properties);
  }

  public override bool Equals(object? other) => ReferenceEquals(this, other);

  public override int GetHashCode() => _inner.GetHashCode();
}
