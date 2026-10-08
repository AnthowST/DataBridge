using DataBridge.Printers.Archive;
using DataBridge.Printers.Inventory;
using DataBridge.Printers.Model;
using DataBridge.Printers.Native;
using static DataBridge.Printers.Native.NativeMethods;

namespace DataBridge.Printers.Restore;

public enum SettingsTier
{
    /// <summary>Same driver and version: the complete DEVMODE (public + private part) is reused.</summary>
    FullDevMode,
    /// <summary>Different driver version: the PrintTicket is converted by the target driver (vendor features it recognizes are kept).</summary>
    PrintTicket,
    /// <summary>Last resort: only the documented public fields are copied.</summary>
    PublicFields,
}

public sealed record ComputedSettings(byte[] DevMode, SettingsTier Tier, List<string> Skipped);

/// <summary>
/// Computes the DEVMODE to write on the target printer. Runs in the user's process (DocumentProperties and
/// PrintTicket conversion only need PRINTER_ACCESS_USE); the elevated worker only writes the global result.
/// </summary>
public static class SettingsApplier
{
    public static ComputedSettings Compute(string printerName, SettingsLayer layer, OpenedArchive archive, bool sameDriverAndVersion, bool userLayer)
    {
        byte[]? source = archive.ReadVerified(layer.DevModeFile);
        byte[]? ticket = archive.ReadVerified(layer.PrintTicketFile);
        var errors = new List<string>();
        using var h = PrinterHandle.Open(printerName, PRINTER_ACCESS_USE);

        if (sameDriverAndVersion && source != null && !layer.PrivatePartExcluded && DevMode.TryParse(source, out var dm))
        {
            try
            {
                dm!.SetDeviceName(printerName);
                return new ComputedSettings(Spooler.ValidateDevMode(h, printerName, dm.Bytes), SettingsTier.FullDevMode, new());
            }
            catch (Exception ex) { errors.Add("DEVMODE complet refusé par le pilote : " + ex.Message); }
        }

        if (ticket != null)
        {
            try
            {
                byte[] converted = Tickets.TicketXmlToDevMode(printerName, ticket, userLayer);
                var validated = Spooler.ValidateDevMode(h, printerName, converted);
                // Bins are compared by name: re-apply the tray if the ticket conversion lost it.
                var skipped = new List<string>(errors);
                if (layer.Summary?.DefaultSourceName != null && DevMode.TryParse(validated, out var vdm))
                {
                    var bins = SafeBins(printerName);
                    var now = vdm!.ToSummary(id => bins.FirstOrDefault(b => b.id == id).name);
                    if (!string.Equals(now.DefaultSourceName, layer.Summary.DefaultSourceName, StringComparison.OrdinalIgnoreCase))
                    {
                        skipped.AddRange(vdm.ApplySummary(new DevModeSummary { DefaultSource = layer.Summary.DefaultSource, DefaultSourceName = layer.Summary.DefaultSourceName },
                            name => BinId(bins, name)));
                        validated = Spooler.ValidateDevMode(h, printerName, vdm.Bytes);
                    }
                }
                return new ComputedSettings(validated, SettingsTier.PrintTicket, skipped);
            }
            catch (Exception ex) { errors.Add("Conversion du PrintTicket impossible : " + ex.Message); }
        }

        if (layer.Summary == null) throw new InvalidOperationException("Aucun réglage exploitable dans l'archive. " + string.Join(" ", errors));
        byte[] baseDevMode = (userLayer ? null : TryGlobal(h)) ?? Spooler.ValidateDevMode(h, printerName, null);
        var target = new DevMode(baseDevMode);
        var targetBins = SafeBins(printerName);
        var notApplied = target.ApplySummary(layer.Summary, name => BinId(targetBins, name));
        notApplied.InsertRange(0, errors);
        return new ComputedSettings(Spooler.ValidateDevMode(h, printerName, target.Bytes), SettingsTier.PublicFields, notApplied);
    }

    private static byte[]? TryGlobal(PrinterHandle h)
    {
        try { return Spooler.GetDevModeLevel(h, 8) ?? Spooler.GetPrinter2(h).devMode; }
        catch { return null; }
    }

    internal static List<(short id, string name)> SafeBins(string printerName)
    {
        try
        {
            string? port = null;
            using (var h = PrinterHandle.Open(printerName, PRINTER_ACCESS_USE)) port = Spooler.GetPrinter2(h).info.pPortName;
            return Spooler.GetBins(printerName, port);
        }
        catch { return new(); }
    }

    private static short? BinId(List<(short id, string name)> bins, string name)
    {
        foreach (var b in bins)
            if (string.Equals(b.name, name, StringComparison.OrdinalIgnoreCase)) return b.id;
        return null;
    }

    public static string Describe(SettingsTier tier) => tier switch
    {
        SettingsTier.FullDevMode => "réglages complets (même pilote, même version)",
        SettingsTier.PrintTicket => "via PrintTicket (pilote différent : options reconnues seulement)",
        _ => "paramètres standards uniquement",
    };
}
