using System.Reflection;

namespace FateAndFridge.Api;

/// <summary>Product identity shared by endpoints and diagnostics.</summary>
public static class AppInfo
{
    /// <summary>Display name of the application.</summary>
    public const string Name = "Fate & Fridge";

    /// <summary>Assembly informational version without source-revision metadata.</summary>
    public static string Version =>
        typeof(AppInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            .Split('+', 2)[0]
        ?? "0.0.0";
}
