using DataBridge.Printers.Archive;
using DataBridge.Printers.Inventory;
using DataBridge.Printers.Machine;
using DataBridge.Printers.Model;
using DataBridge.Printers.Native;
using DataBridge.Printers.Reporting;
using DataBridge.Printers.Util;

namespace DataBridge.Printers.Restore;

/// <summary>What the engine actually applied, so that the verification compares against the intended result.</summary>
public sealed class AppliedState
{
    public string TargetName { get; set; } = "";
    public string? PortName { get; set; }
    public bool GlobalSettingsWritten { get; set; }
    public SettingsTier? GlobalTier { get; set; }
    public bool UserSettingsWritten { get; set; }
    public SettingsTier? UserTier { get; set; }
    public bool DriverDataWritten { get; set; }
    public string? SecuritySddl { get; set; }
    public bool ShareRequested { get; set; }
}

/// <summary>
/// Re-reads the restored printer from the spooler and compares it with the archive. "No error" is never taken
/// as success: every item reported as identical has been read back.
/// </summary>
public static class Verifier
{
    public static List<CheckItem> Verify(PrinterPlan plan, AppliedState applied, OpenedArchive archive, LiveInventory context)
    {
        var checks = new List<CheckItem>();
        var src = plan.Entry;
        LivePrinter live;
        try { live = PrinterInventory.ReadPrinter(applied.TargetName, 0, details: true, context); }
        catch (Exception ex)
        {
            checks.Add(new CheckItem("Configuration", "Imprimante", applied.TargetName, ex.Message, CheckStatus.Missing));
            return checks;
        }
        var t = live.Entry;
        if (t.DriverName == null && !plan.IsConnection)
        {
            checks.Add(new CheckItem("Configuration", "Imprimante", applied.TargetName, "introuvable", CheckStatus.Missing));
            return checks;
        }
        checks.Add(new CheckItem("Configuration", "Imprimante", applied.TargetName, t.Name, CheckStatus.Identical));

        void Compare(string category, string label, string? expected, string? actual, bool ignoreCase = true)
        {
            bool same = string.Equals(expected ?? "", actual ?? "", ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            checks.Add(new CheckItem(category, label, expected, actual, same ? CheckStatus.Identical : CheckStatus.Different));
        }

        Compare("Pilote", "Nom du pilote", src.DriverName, t.DriverName);
        if (plan.Driver != null && t.DriverName != null)
        {
            var installed = context.Drivers.FirstOrDefault(d => string.Equals(d.Name, t.DriverName, StringComparison.OrdinalIgnoreCase));
            Compare("Pilote", "Version du pilote", plan.Driver.Version, installed?.Version);
        }

        if (!plan.IsConnection)
        {
            Compare("Port", "Nom du port", applied.PortName, t.PortName);
            var port = context.Ports.FirstOrDefault(p => string.Equals(p.Name, t.PortName, StringComparison.OrdinalIgnoreCase));
            if (plan.Port != null)
            {
                bool same = port != null && port.SameConfigurationAs(plan.Port);
                checks.Add(new CheckItem("Port", "Configuration du port", plan.Port.Describe(), port?.Describe() ?? "introuvable",
                    same ? CheckStatus.Identical : CheckStatus.Different));
            }
            Compare("Configuration", "Commentaire", src.Comment, t.Comment, false);
            Compare("Configuration", "Emplacement", src.Location, t.Location, false);
            Compare("Configuration", "Processeur d'impression", src.PrintProcessor, t.PrintProcessor);
            Compare("Configuration", "Type de données", src.Datatype, t.Datatype);
            Compare("Configuration", "Attributs de la file", $"0x{src.Attributes & MachineOps.RestorableAttributes:X}", $"0x{t.Attributes & MachineOps.RestorableAttributes:X}");
            string expectedShare = applied.ShareRequested ? $"Partagée sous « {src.ShareName} »" : "Non partagée";
            string actualShare = t.IsShared ? $"Partagée sous « {t.ShareName} »" : "Non partagée";
            Compare("Partage", "Partage", expectedShare, actualShare);
        }

        string? BinName(short id) => live.Bins.FirstOrDefault(b => b.id == id).name;

        if (applied.GlobalSettingsWritten && src.GlobalSettings?.Summary != null)
            CompareSettings(checks, "Réglages par défaut", src.GlobalSettings, live.GlobalDevMode, applied.GlobalTier, archive, BinName);
        else if (src.GlobalSettings != null && !plan.IsConnection)
            checks.Add(new CheckItem("Réglages par défaut", "Réglages par défaut de la file", "dans l'archive", "non restaurés", CheckStatus.NotChecked));

        if (applied.UserSettingsWritten && src.UserSettings?.Summary != null)
            CompareSettings(checks, "Préférences utilisateur", src.UserSettings, live.UserDevMode, applied.UserTier, archive, BinName);

        if (applied.DriverDataWritten && src.DriverData != null)
        {
            var expected = Json.Deserialize<List<PrinterDataValue>>(File.ReadAllText(archive.VerifiedPath(src.DriverData.File)!)) ?? new();
            var actual = live.DriverData ?? new();
            int same = expected.Count(e => actual.Any(a => a.Key == e.Key && a.Name == e.Name && a.Data == e.Data));
            checks.Add(new CheckItem("Options du pilote", "Valeurs des données du pilote", $"{expected.Count} valeur(s)", $"{same} identique(s)",
                same == expected.Count ? CheckStatus.Identical : CheckStatus.Different));
        }

        if (applied.SecuritySddl != null)
        {
            bool same = t.Security != null && SecurityPlanner.SameAccess(applied.SecuritySddl, t.Security.Sddl);
            checks.Add(new CheckItem("Permissions", "Permissions de la file", $"{applied.SecuritySddl.Length} car. SDDL", same ? "identiques" : "différentes",
                same ? CheckStatus.Identical : CheckStatus.Different));
        }
        return checks;
    }

    private static void CompareSettings(List<CheckItem> checks, string category, SettingsLayer layer, byte[]? actualBytes, SettingsTier? tier,
        OpenedArchive archive, Func<short, string?> binName)
    {
        if (actualBytes == null || !DevMode.TryParse(actualBytes, out var actual))
        {
            checks.Add(new CheckItem(category, "Réglages", "présents", "illisibles", CheckStatus.Missing));
            return;
        }
        var expected = Labels.Describe(layer.Summary);
        var now = Labels.Describe(actual!.ToSummary(binName));
        foreach (var (label, value) in expected)
        {
            if (value == null) continue;
            string? got = now.FirstOrDefault(n => n.label == label).value;
            checks.Add(new CheckItem(category, label, value, got ?? "non défini",
                string.Equals(value, got, StringComparison.OrdinalIgnoreCase) ? CheckStatus.Identical : CheckStatus.Different));
        }
        if (tier == SettingsTier.FullDevMode && archive.ReadVerified(layer.DevModeFile) is byte[] srcBytes && DevMode.TryParse(srcBytes, out var src))
        {
            bool same = src!.PrivatePart.AsSpan().SequenceEqual(actual.PrivatePart);
            checks.Add(new CheckItem(category, "Options constructeur (données privées du pilote)", $"{src.DriverExtra} octets",
                same ? "identiques" : "modifiées par le pilote", same ? CheckStatus.Identical : CheckStatus.Different));
        }
        else if (tier != null)
        {
            checks.Add(new CheckItem(category, "Options constructeur (données privées du pilote)", "—",
                $"non comparables ({SettingsApplier.Describe(tier.Value)})", CheckStatus.NotChecked));
        }
    }
}
