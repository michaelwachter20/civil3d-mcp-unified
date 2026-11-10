// The plugin's only reflection: late-bound property, field and method access for Civil 3D members missing from the
// referenced API or differing between releases, COM late binding (sheet sets, AutoCAD COM objects), and the loaded
// assembly list Roslyn scripts compile against. tests/reflection_boundary.test.ts fails if reflection appears in any
// other plugin file. Serves no JSON-RPC method; used through CivilObjectUtils and directly by command classes,
// ScriptEngine and SheetSetCom. Lookups are cached per type and name. Reads never throw (null/false on
// failure); it never writes files and never touches the drawing except through the setters callers invoke.
using System.Collections.Concurrent;
using System.Reflection;

namespace Civil3DMcpPlugin;

/// <summary>
/// Narrow compatibility boundary for Civil 3D members that are unavailable in
/// the referenced managed API or vary between supported host versions.
/// Documented Autodesk members should be called directly instead.
/// </summary>
internal static class Civil3DCompatibility
{
  internal readonly record struct ParameterShape(Type Type);
  private sealed record CachedProperty(PropertyInfo? Value);
  private sealed record CachedMethods(MethodInfo[] Values);
  private sealed record CachedField(FieldInfo? Value);
  private sealed record CachedType(Type? Value);

  private readonly record struct PropertyKey(Type Type, string Name, bool IsStatic);
  private readonly record struct MethodKey(Type Type, string Name, bool IsStatic, int ArgumentCount);
  private readonly record struct MethodFamilyKey(Type Type, string Name, bool IsStatic);
  private readonly record struct LoadedStaticMethodKey(string Name, Type FirstParameterType, int ArgumentCount);

  private static readonly ConcurrentDictionary<PropertyKey, CachedProperty> PropertyCache = new();
  private static readonly ConcurrentDictionary<PropertyKey, CachedField> FieldCache = new();
  private static readonly ConcurrentDictionary<Type, PropertyInfo[]> ScalarPropertyCache = new();
  private static readonly ConcurrentDictionary<MethodKey, CachedMethods> MethodCache = new();
  private static readonly ConcurrentDictionary<MethodFamilyKey, CachedMethods> MethodFamilyCache = new();
  private static readonly ConcurrentDictionary<LoadedStaticMethodKey, CachedMethods> LoadedStaticMethodCache = new();
  private static readonly ConcurrentDictionary<string, CachedType> TypeCache = new(StringComparer.Ordinal);

  /// <summary>
  /// A public instance property read late-bound and converted to <typeparamref name="T"/> (Convert.ChangeType);
  /// default when the target is null, the property is missing, its getter throws or the value doesn't convert.
  /// </summary>
  public static T? GetPropertyValue<T>(object? target, string propertyName)
  {
    var raw = GetPropertyValue(target, propertyName);
    if (raw == null)
    {
      return default;
    }

    if (raw is T typed)
    {
      return typed;
    }

    try
    {
      var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
      if (targetType.IsInstanceOfType(raw))
      {
        return (T)(object)raw;
      }

      return (T)(object)Convert.ChangeType(raw, targetType)!;
    }
    catch
    {
      return default;
    }
  }

  /// <summary>A public instance property read late-bound (finding an inherited getter if needed); null on any failure.</summary>
  public static object? GetPropertyValue(object? target, string propertyName)
  {
    if (target == null)
    {
      return null;
    }

    var property = ResolveProperty(target.GetType(), propertyName, isStatic: false);
    if (property == null)
    {
      return null;
    }

    try
    {
      return ReadableProperty(property).GetValue(target);
    }
    catch
    {
      return null;
    }
  }

  /// <summary>An indexed property (indexer) read late-bound with <paramref name="indexes"/>; null on any failure.</summary>
  public static object? GetIndexedPropertyValue(object? target, string propertyName, params object?[] indexes)
  {
    if (target == null)
    {
      return null;
    }

    var property = ResolveProperty(target.GetType(), propertyName, isStatic: false);
    try
    {
      return property?.GetValue(target, indexes);
    }
    catch
    {
      return null;
    }
  }

  /// <summary>A public instance field read late-bound; null on any failure.</summary>
  public static object? GetFieldValue(object? target, string fieldName)
  {
    if (target == null)
    {
      return null;
    }

    var type = target.GetType();
    var key = new PropertyKey(type, fieldName, IsStatic: false);
    var field = FieldCache.GetOrAdd(key, static item =>
      new CachedField(item.Type.GetField(item.Name, BindingFlags.Public | BindingFlags.Instance))).Value;
    try
    {
      return field?.GetValue(target);
    }
    catch
    {
      return null;
    }
  }

  /// <summary>
  /// Writes a public instance property late-bound, converting <paramref name="value"/> to its type (enum names parsed
  /// case-insensitively). True when written; false when the target is null, the property is missing or read-only, or
  /// conversion or the setter fails. Changes the object (the drawing, when it is database-resident and open for write).
  /// </summary>
  public static bool TrySetProperty(object? target, string propertyName, object? value)
  {
    if (target == null)
    {
      return false;
    }

    var property = ResolveProperty(target.GetType(), propertyName, isStatic: false);
    if (property?.CanWrite != true)
    {
      return false;
    }

    try
    {
      var targetType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
      var converted = value == null || targetType.IsInstanceOfType(value)
        ? value
        : targetType.IsEnum && value is string enumText
          ? Enum.Parse(targetType, enumText, ignoreCase: true)
          : Convert.ChangeType(value, targetType);
      property.SetValue(target, converted);
      return true;
    }
    catch
    {
      return false;
    }
  }

  /// <summary>Every readable, non-indexed string, bool, int or double property of <paramref name="target"/> by name; getters that throw are left out.</summary>
  public static IReadOnlyDictionary<string, object?> GetReadableScalarProperties(object target)
  {
    var properties = ScalarPropertyCache.GetOrAdd(target.GetType(), static type =>
      type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
        .Where(property =>
        {
          var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
          return propertyType == typeof(string)
            || propertyType == typeof(bool)
            || propertyType == typeof(int)
            || propertyType == typeof(double);
        })
        .ToArray());

    var values = new Dictionary<string, object?>(StringComparer.Ordinal);
    foreach (var property in properties)
    {
      try
      {
        values[property.Name] = property.GetValue(target);
      }
      catch
      {
        // Some Autodesk wrappers throw for state-dependent getters. Omit them.
      }
    }

    return values;
  }

  /// <summary>
  /// Calls a public instance method late-bound, trying each overload with the same argument count. Null when none
  /// matches; an exception thrown by the method itself propagates (wrapped in TargetInvocationException).
  /// </summary>
  public static object? InvokeMethod(object? target, string methodName, params object?[] arguments)
  {
    if (target == null)
    {
      return null;
    }

    TryInvokeCandidates(target.GetType(), target, methodName, isStatic: false, arguments, out var result);
    return result;
  }

  /// <summary>As <see cref="InvokeMethod"/>, but says whether a matching method was found and called.</summary>
  public static bool TryInvokeMethod(object? target, string methodName, out object? result, params object?[] arguments)
  {
    result = null;
    return target != null
      && TryInvokeCandidates(target.GetType(), target, methodName, isStatic: false, arguments, out result);
  }

  /// <summary>Calls a public static method of <paramref name="type"/> late-bound; null when no overload matches.</summary>
  public static object? InvokeStaticMethod(Type type, string methodName, params object?[] arguments)
  {
    TryInvokeCandidates(type, null, methodName, isStatic: true, arguments, out var result);
    return result;
  }

  /// <summary>As <see cref="InvokeStaticMethod"/>, but says whether a matching method was found and called.</summary>
  public static bool TryInvokeStaticMethod(Type type, string methodName, out object? result, params object?[] arguments)
  {
    return TryInvokeCandidates(type, null, methodName, isStatic: true, arguments, out result);
  }

  /// <summary>
  /// Tries every public static overload of <paramref name="methodName"/>, fewest parameters first: for each,
  /// <paramref name="argumentBuilder"/> gets the parameter types and returns the arguments (or null to skip it). The
  /// first call that succeeds wins; <paramref name="invokedArguments"/> returns the array used, so by-ref outputs can
  /// be read. Overloads that reject their arguments, including by throwing inside, are skipped.
  /// </summary>
  public static bool TryInvokeStaticOverloads(
    Type type,
    string methodName,
    Func<ParameterShape[], object?[]?> argumentBuilder,
    out object? result,
    out object?[]? invokedArguments)
  {
    result = null;
    invokedArguments = null;
    var key = new MethodFamilyKey(type, methodName, IsStatic: true);
    var methods = MethodFamilyCache.GetOrAdd(key, static item =>
      new CachedMethods(item.Type.GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Where(method => method.Name == item.Name)
        .OrderBy(method => method.GetParameters().Length)
        .ToArray())).Values;

    foreach (var method in methods)
    {
      var shapes = method.GetParameters()
        .Select(parameter => new ParameterShape(
          parameter.ParameterType.IsByRef
            ? parameter.ParameterType.GetElementType()!
            : parameter.ParameterType))
        .ToArray();
      var arguments = argumentBuilder(shapes);
      if (arguments == null)
      {
        continue;
      }

      try
      {
        result = method.Invoke(null, arguments);
        invokedArguments = arguments;
        return true;
      }
      catch (ArgumentException)
      {
      }
      catch (TargetParameterCountException)
      {
      }
      catch (TargetInvocationException)
      {
        // The runtime-selected overload rejected its synthesized defaults.
        // Continue so another compatible Civil 3D overload can be attempted.
      }
    }

    return false;
  }

  /// <summary>
  /// Finds a public static method named <paramref name="methodName"/> in any loaded assembly whose first parameter
  /// accepts <paramref name="firstParameterType"/> (an extension method such as Database.GetCivilCatchmentGroups) and
  /// calls the first that accepts the arguments. The scan of every loaded type runs once per key and is cached.
  /// </summary>
  public static bool TryInvokeLoadedStaticMethod(
    string methodName,
    Type firstParameterType,
    out object? result,
    params object?[] arguments)
  {
    result = null;
    var key = new LoadedStaticMethodKey(methodName, firstParameterType, arguments.Length);
    var methods = LoadedStaticMethodCache.GetOrAdd(key, static item =>
    {
      var matches = new List<MethodInfo>();
      foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
      {
        Type[] types;
        try
        {
          types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
          types = exception.Types.Where(type => type != null).Cast<Type>().ToArray();
        }
        catch
        {
          continue;
        }

        foreach (var type in types)
        {
          matches.AddRange(type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name == item.Name)
            .Where(method => method.GetParameters().Length == item.ArgumentCount)
            .Where(method => method.GetParameters().Length > 0
              && method.GetParameters()[0].ParameterType.IsAssignableFrom(item.FirstParameterType)));
        }
      }
      return new CachedMethods(matches.ToArray());
    }).Values;

    foreach (var method in methods)
    {
      try
      {
        result = method.Invoke(null, arguments);
        return true;
      }
      catch (ArgumentException)
      {
      }
      catch (TargetParameterCountException)
      {
      }
    }
    return false;
  }

  /// <summary>A COM object by its registered name, for libraries with no .NET API (the sheet set library).</summary>
  public static T CreateComObject<T>(string progId) =>
    (T)Activator.CreateInstance(Type.GetTypeFromProgID(progId, throwOnError: true)!)!;

  /// <summary>
  /// The first of <paramref name="fullNames"/> that resolves to a type, as an assembly-qualified name or by full name
  /// in any loaded assembly; null when none does. Results (including misses) are cached.
  /// </summary>
  public static Type? FindLoadedType(params string[] fullNames)
  {
    foreach (var fullName in fullNames)
    {
      var cached = TypeCache.GetOrAdd(fullName, static candidateName =>
      {
        var assemblyQualifiedType = Type.GetType(candidateName, throwOnError: false, ignoreCase: false);
        if (assemblyQualifiedType != null)
        {
          return new CachedType(assemblyQualifiedType);
        }

        var commaIndex = candidateName.IndexOf(',');
        var normalizedName = commaIndex >= 0
          ? candidateName[..commaIndex].Trim()
          : candidateName;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
          var type = assembly.GetType(normalizedName, throwOnError: false, ignoreCase: false);
          if (type != null)
          {
            return new CachedType(type);
          }
        }

        return new CachedType(null);
      });
      if (cached.Value != null)
      {
        return cached.Value;
      }
    }

    return null;
  }

  /// <summary>A property of an AutoCAD COM object: those have no .NET properties to find, only named calls.</summary>
  public static object? GetComProperty(object? target, string propertyName)
  {
    return target?.GetType().InvokeMember(propertyName, BindingFlags.GetProperty, null, target, null);
  }

  /// <summary>
  /// The declaration of <paramref name="property"/> that has a getter. A class can override only the setter of a
  /// virtual property, and reflection then hides the base's getter: every Civil 3D style overrides Name that way, so its
  /// name read as null, and each named style looked like it wasn't in the drawing.
  /// </summary>
  private static PropertyInfo ReadableProperty(PropertyInfo property)
  {
    for (var type = property.DeclaringType?.BaseType; !property.CanRead && type != null; type = type.BaseType)
    {
      var inherited = type.GetProperty(property.Name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
      if (inherited?.CanRead == true)
      {
        return inherited;
      }
    }

    return property;
  }

  /// <summary>The public property of that name (cached, misses too); null when the type has none.</summary>
  private static PropertyInfo? ResolveProperty(Type type, string propertyName, bool isStatic)
  {
    var key = new PropertyKey(type, propertyName, isStatic);
    return PropertyCache.GetOrAdd(key, static item =>
    {
      var flags = BindingFlags.Public | (item.IsStatic ? BindingFlags.Static : BindingFlags.Instance);
      return new CachedProperty(item.Type.GetProperty(item.Name, flags));
    }).Value;
  }

  /// <summary>
  /// Calls the first public overload with the right name and argument count that accepts the arguments. Overloads
  /// that refuse the argument types are skipped; an exception from inside a called method propagates.
  /// </summary>
  private static bool TryInvokeCandidates(
    Type type,
    object? target,
    string methodName,
    bool isStatic,
    object?[] arguments,
    out object? result)
  {
    result = null;
    var key = new MethodKey(type, methodName, isStatic, arguments.Length);
    var methods = MethodCache.GetOrAdd(key, static item =>
    {
      var flags = BindingFlags.Public | (item.IsStatic ? BindingFlags.Static : BindingFlags.Instance);
      return new CachedMethods(item.Type
        .GetMethods(flags)
        .Where(method => method.Name == item.Name && method.GetParameters().Length == item.ArgumentCount)
        .ToArray());
    }).Values;

    foreach (var method in methods)
    {
      try
      {
        result = method.Invoke(target, arguments);
        return true;
      }
      catch (ArgumentException)
      {
        // Try the next overload. Invocation exceptions from a compatible
        // overload are allowed to propagate to the command's explicit error path.
      }
      catch (TargetParameterCountException)
      {
      }
    }

    return false;
  }

  /// <summary>
  /// File locations of every non-dynamic assembly loaded in the host process.
  /// Used by <see cref="ScriptEngine"/> to give Roslyn scripts the same API
  /// surface Civil 3D itself has loaded (AutoCAD, Civil 3D, BCL, add-ins).
  /// </summary>
  public static string[] GetLoadedAssemblyLocations()
  {
    return AppDomain.CurrentDomain.GetAssemblies()
      .Where(assembly => !assembly.IsDynamic)
      .Select(assembly =>
      {
        try
        {
          return assembly.Location;
        }
        catch (NotSupportedException)
        {
          return string.Empty;
        }
      })
      .Where(location => !string.IsNullOrEmpty(location) && File.Exists(location))
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .ToArray();
  }

  /// <summary>
  /// Some third-party assemblies loaded into Civil 3D carry malformed InternalsVisibleTo attributes;
  /// Roslyn throws a NullReferenceException on them while binding ANY script. Skip those (logged).
  /// </summary>
  public static bool IsUsableScriptReference(string path)
  {
    try
    {
      using var stream = File.OpenRead(path);
      using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
      if (!pe.HasMetadata) return false;
      var md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
      if (!md.IsAssembly) return false;
      foreach (var handle in md.GetAssemblyDefinition().GetCustomAttributes())
      {
        var attribute = md.GetCustomAttribute(handle);
        if (!IsInternalsVisibleTo(md, attribute)) continue;
        var blob = md.GetBlobReader(attribute.Value);
        if (blob.Length < 2 || blob.ReadUInt16() != 1 || blob.ReadSerializedString() == null)
        {
          PluginLog.Warn("Script", $"Skipping script reference with a malformed InternalsVisibleTo attribute: {path}");
          return false;
        }
      }

      return true;
    }
    catch (System.Exception exception)
    {
      PluginLog.Warn("Script", $"Skipping unreadable script reference {path}: {exception.Message}");
      return false;
    }
  }

  /// <summary>Whether a custom attribute's constructor belongs to a type named InternalsVisibleToAttribute (reference or definition).</summary>
  private static bool IsInternalsVisibleTo(System.Reflection.Metadata.MetadataReader md, System.Reflection.Metadata.CustomAttribute attribute)
  {
    System.Reflection.Metadata.EntityHandle type = default;
    if (attribute.Constructor.Kind == System.Reflection.Metadata.HandleKind.MemberReference)
    {
      type = md.GetMemberReference((System.Reflection.Metadata.MemberReferenceHandle)attribute.Constructor).Parent;
    }
    else if (attribute.Constructor.Kind == System.Reflection.Metadata.HandleKind.MethodDefinition)
    {
      type = md.GetMethodDefinition((System.Reflection.Metadata.MethodDefinitionHandle)attribute.Constructor).GetDeclaringType();
    }

    if (type.Kind == System.Reflection.Metadata.HandleKind.TypeReference)
    {
      var reference = md.GetTypeReference((System.Reflection.Metadata.TypeReferenceHandle)type);
      return md.GetString(reference.Name) == "InternalsVisibleToAttribute";
    }

    if (type.Kind == System.Reflection.Metadata.HandleKind.TypeDefinition)
    {
      var definition = md.GetTypeDefinition((System.Reflection.Metadata.TypeDefinitionHandle)type);
      return md.GetString(definition.Name) == "InternalsVisibleToAttribute";
    }

    return false;
  }
}
