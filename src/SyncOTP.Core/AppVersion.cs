using System.Reflection;

namespace SyncOTP.Core;

/// <summary>
/// The app version, as set by &lt;Version&gt; in Directory.Build.props and baked into the assembly at
/// build time. Read once and cached, since reflection isn't free and the value never changes at runtime.
/// </summary>
public static class AppVersion
{
    /// <summary>The informational version (e.g. "0.1.0"), without any build-metadata suffix.</summary>
    public static string Display { get; } = Resolve();

    private static string Resolve()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (!string.IsNullOrEmpty(informational))
        {
            // Strip a "+<git-sha>" build-metadata suffix, if the SDK added one.
            var plusIndex = informational.IndexOf('+');
            return plusIndex < 0 ? informational : informational[..plusIndex];
        }

        return assembly.GetName().Version?.ToString() ?? "unknown";
    }
}
