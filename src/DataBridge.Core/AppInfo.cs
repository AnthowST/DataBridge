namespace DataBridge.Core;

/// <summary>Product identity, in one place (window title, log header, backup folder prefix, dialogs).</summary>
public static class AppInfo
{
    public const string Name = "DataBridge";

    /// <summary>Product signature, shown discreetly (header, home).</summary>
    public const string Baseline = "Copiez, sauvegardez et restaurez vos données";

    /// <summary>Prefix of new backup folders: DataBridge_NomPC_NomSession_Date_Heure.</summary>
    public const string BackupPrefix = "DataBridge";

    /// <summary>Product version ("2.0.0"), the same for the application and this library.</summary>
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";
}
