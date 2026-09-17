using System.Reflection;
using PSXRecomp.Core.Runtime.Input;
using PSXRecomp.Core.Runtime.Input.Ps1;

namespace PSXRecomp.Tests.Input;

/// <summary>
/// Structural checks over the public contract of the input domain: the
/// console-agnostic host layer must not reference any OS/device backend type nor
/// any console module's types, and the domain must not be conflated with memory
/// card, save-state, or runtime execution state.
/// </summary>
[Test]
public class InputAbstractionArchitectureTests
{
    private const string CommonNamespace = "PSXRecomp.Core.Runtime.Input";
    private const string Ps1Namespace = "PSXRecomp.Core.Runtime.Input.Ps1";

    private static readonly string[] BackendNamespaceTokens =
    {
        "Avalonia", "SDL", "XInput", "DirectInput", "SharpDX", "Windows.Gaming", "System.Windows",
    };

    private static readonly string[] ConflationTokens =
    {
        "MemoryCard", "SaveState", "ExecutionState",
    };

    private static readonly Type[] InputTypes = typeof(Ps1ControllerState).Assembly
        .GetTypes()
        .Where(t => t.IsPublic && t.Namespace is not null &&
                    (t.Namespace == CommonNamespace ||
                     t.Namespace.StartsWith(CommonNamespace + ".", StringComparison.Ordinal)))
        .OrderBy(t => t.FullName, StringComparer.Ordinal)
        .ToArray();

    private static readonly Type[] CommonTypes = InputTypes
        .Where(t => t.Namespace == CommonNamespace)
        .ToArray();

    [Fact]
    public void PublicContract_ReferencesNoDeviceOrOsBackendTypes()
    {
        foreach (var referenced in PublicContractTypes())
        {
            var ns = referenced.Namespace ?? string.Empty;
            foreach (var token in BackendNamespaceTokens)
            {
                ns.Should().NotContain(token, $"{referenced.FullName} must not leak a backend namespace into the input domain");
            }

            var assemblyName = referenced.Assembly.GetName().Name ?? string.Empty;
            (assemblyName == "PSXRecomp.Core" || assemblyName.StartsWith("System", StringComparison.Ordinal))
                .Should().BeTrue($"{referenced.FullName} lives in {assemblyName}, outside the input domain contract");
        }
    }

    [Fact]
    public void PublicContract_DoesNotConflateWithMemoryCardSaveOrExecutionState()
    {
        foreach (var referenced in PublicContractTypes())
        {
            var name = referenced.FullName ?? string.Empty;
            foreach (var token in ConflationTokens)
            {
                name.Should().NotContain(token,
                    $"{referenced.FullName} must not reference {token}; controller state is a distinct concept");
            }
        }
    }

    [Fact]
    public void CommonHostContract_ReferencesNoConsoleModuleTypes()
    {
        foreach (var type in CommonTypes)
        {
            foreach (var referenced in DeclaredReferences(type))
            {
                var ns = referenced.Namespace ?? string.Empty;
                ns.Should().NotStartWith(Ps1Namespace,
                    $"{type.FullName} must not reference the console module type {referenced.FullName}");
            }
        }
    }

    [Fact]
    public void SnapshotTypes_AreImmutableValueTypes_NotSharedMutableState()
    {
        typeof(Ps1ControllerState).IsValueType.Should().BeTrue();
        typeof(Ps1ControllerSnapshot).IsValueType.Should().BeTrue();
        typeof(ControllerInputSnapshot<Ps1Button>).IsValueType.Should().BeTrue();
        typeof(PhysicalInputId).IsValueType.Should().BeTrue();
    }

    private static IEnumerable<Type> PublicContractTypes()
    {
        var seen = new HashSet<Type>();
        var queue = new Queue<Type>();

        foreach (var t in InputTypes)
        {
            queue.Enqueue(t);
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!seen.Add(current))
            {
                continue;
            }

            yield return current;
            foreach (var referenced in DeclaredReferences(current))
            {
                queue.Enqueue(referenced);
            }
        }
    }

    private static IEnumerable<Type> DeclaredReferences(Type type)
    {
        foreach (var argument in type.GetGenericArguments())
        {
            yield return argument;

            if (argument.IsGenericParameter)
            {
                foreach (var constraint in argument.GetGenericParameterConstraints())
                {
                    yield return constraint;
                }
            }
        }

        foreach (var attribute in type.GetCustomAttributesData())
        {
            yield return attribute.AttributeType;
        }

        if (type.IsGenericParameter)
        {
            foreach (var constraint in type.GetGenericParameterConstraints())
            {
                yield return constraint;
            }

            yield break;
        }

        if (type.IsEnum)
        {
            yield return type.GetEnumUnderlyingType();
            yield break;
        }

        if (type.BaseType is { } baseType && baseType != typeof(object) && baseType != typeof(ValueType))
        {
            yield return baseType;
        }

        foreach (var iface in type.GetInterfaces())
        {
            yield return iface;
        }

        foreach (var ctor in type.GetConstructors())
        {
            foreach (var parameter in ctor.GetParameters())
            {
                yield return parameter.ParameterType;
            }
        }

        foreach (var property in type.GetProperties())
        {
            yield return property.PropertyType;
        }

        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            yield return field.FieldType;
        }

        foreach (var method in type.GetMethods(
                     BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            if (method.IsSpecialName)
            {
                continue;
            }

            yield return method.ReturnType;
            foreach (var parameter in method.GetParameters())
            {
                yield return parameter.ParameterType;
            }
        }
    }
}
