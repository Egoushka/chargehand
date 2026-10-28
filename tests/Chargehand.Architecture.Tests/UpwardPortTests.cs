using System.Reflection;

namespace Chargehand.Architecture.Tests;

/// <summary>
/// ADR 0060, amendment of 2026-10-14 (option B): a lower module calls a higher one only through a named port, an
/// interface or abstract type it declares and a higher module implements. Every such port is listed here.
/// </summary>
public class UpwardPortTests
{
    /// <summary>
    /// Full names of the allowed upward ports. The amendment allowed two, added by C3's Tasks slice; P3 (<c>ITaskState</c>, Tasks to Batches) went when task state moved into the
    /// journal (ADR 0061, amendment of 2026-10-14). Research implements P4, the only one left, and <c>DrivenRun</c> wires it.
    /// </summary>
    internal static readonly HashSet<string> Allowed =
    [
        // P4 Tasks -> Research proof.
        "Chargehand.Modules.Tasks.Api.ITaskResults",
    ];

    [Fact]
    public void Upward_ports_are_allowlisted()
    {
        Assert.Empty(UpwardPorts().Where(p => !Allowed.Contains(p.Port)).Select(p => $"{p.Implementer} implements {p.Port}"));
    }

    // Hosts are left out: the test project does not reference them, and wiring ports together is their job.
    internal static IEnumerable<(string Port, string Implementer)> UpwardPorts() =>
        from project in Skeleton.ProjectFiles().Select(Skeleton.Name)
        where !project.StartsWith("Chargehand.Hosts.", StringComparison.Ordinal) && !Skeleton.Standalone.Contains(project)
        let layer = Skeleton.Layers[project]
        from type in Assembly.Load(project).GetTypes()
        from port in Ports(type)
        let owner = port.Assembly.GetName().Name!
        where owner != project && Skeleton.Layers.TryGetValue(owner, out var portLayer) && portLayer < layer
        select (Name(port), type.FullName!);

    private static IEnumerable<Type> Ports(Type type)
    {
        foreach (var contract in type.GetInterfaces())
        {
            yield return contract;
        }

        for (var parent = type.BaseType; parent is not null; parent = parent.BaseType)
        {
            if (parent.IsAbstract)
            {
                yield return parent;
            }
        }
    }

    private static string Name(Type port) => (port.IsGenericType ? port.GetGenericTypeDefinition() : port).FullName!;
}
