using System.Reflection;

namespace Rafferty.Shared;

public static class AppVersion
{
    public static Version Current => Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0, 0);
    public static string Display => $"{Current.Major}.{Current.Minor}.{Current.Build}";
}
