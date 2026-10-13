using System.Reflection;

namespace Chargehand.Kernel;

/// <summary>The version every host reports, from the one version source (Directory.Build.props).</summary>
public static class BuildInfo
{
    public static string Version { get; } =
        typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
}
