using System.Reflection;

namespace MediaSort.Services;

public static class VersionInfo
{
    public static string GetVersion()
    {
        var asm = Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        if (info != null && !string.IsNullOrWhiteSpace(info.InformationalVersion))
            return info.InformationalVersion;
        var fv = asm.GetCustomAttribute<AssemblyFileVersionAttribute>();
        if (fv != null && !string.IsNullOrWhiteSpace(fv.Version))
            return fv.Version;
        return asm.GetName().Version?.ToString() ?? "1.0.0";
    }
}
