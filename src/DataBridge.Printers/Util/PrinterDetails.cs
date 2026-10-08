using System.Text;
using DataBridge.Printers.Inventory;
using DataBridge.Printers.Model;
using DataBridge.Printers.Native;

namespace DataBridge.Printers.Util;

/// <summary>Plain-text description of a live printer (details panel of the export screen).</summary>
public static class PrinterDetails
{
    public static string Details(LivePrinter lp, LiveInventory inv)
    {
        var e = lp.Entry;
        var sb = new StringBuilder();
        sb.AppendLine(e.Name);
        sb.AppendLine(new string('─', Math.Min(48, e.Name.Length + 4)));
        sb.AppendLine($"Type          : {Labels.Kind(e.Kind)}");
        sb.AppendLine($"Prise en charge : {Labels.Support(e.Support)}");
        if (e.SupportNote != null) sb.AppendLine($"  {e.SupportNote}");
        if (e.IsDefault) sb.AppendLine("Imprimante par défaut de l'utilisateur courant");
        if (e.IsOffline) sb.AppendLine("Hors ligne (export à partir des informations locales)");
        if (!string.IsNullOrEmpty(e.Comment)) sb.AppendLine($"Commentaire   : {e.Comment}");
        if (!string.IsNullOrEmpty(e.Location)) sb.AppendLine($"Emplacement   : {e.Location}");
        sb.AppendLine();

        if (e.Kind == PrinterKind.SharedConnection)
        {
            sb.AppendLine("CONNEXION À UN SERVEUR D'IMPRESSION");
            sb.AppendLine($"Serveur       : {e.ServerName}");
            sb.AppendLine($"Partage       : {e.ShareName}");
            sb.AppendLine("La connexion sera recréée telle quelle (pas de conversion en file locale).");
        }
        else
        {
            var port = inv.Ports.FirstOrDefault(p => string.Equals(p.Name, e.PortName, StringComparison.OrdinalIgnoreCase));
            sb.AppendLine("PORT");
            sb.AppendLine($"Nom           : {e.PortName}");
            if (port != null)
            {
                sb.AppendLine($"Moniteur      : {port.Monitor}");
                if (port.Kind == PortKind.StandardTcpIp)
                {
                    sb.AppendLine($"Adresse       : {port.HostAddress}");
                    sb.AppendLine($"Protocole     : {port.Protocol}, port {port.PortNumber}");
                    if (port.Protocol == "LPR") sb.AppendLine($"File LPR      : {port.LprQueue} (comptage d'octets : {(port.LprByteCounting ? "oui" : "non")})");
                    sb.AppendLine($"SNMP          : {(port.SnmpEnabled ? $"activé, index {port.SnmpDevIndex}" : "désactivé")}");
                }
            }
            sb.AppendLine();
            sb.AppendLine("PARTAGE (depuis ce PC)");
            sb.AppendLine(e.IsShared ? $"Partagée sous « {e.ShareName} »{(e.IsPublished ? ", publiée dans l'annuaire" : "")}" : "Non partagée");
        }
        sb.AppendLine();

        var drv = inv.Drivers.FirstOrDefault(d => string.Equals(d.Name, e.DriverName, StringComparison.OrdinalIgnoreCase));
        sb.AppendLine("PILOTE");
        sb.AppendLine($"Nom           : {e.DriverName}");
        if (drv != null)
        {
            sb.AppendLine($"Fabricant     : {drv.Manufacturer}");
            sb.AppendLine($"Version       : {drv.Version} (v{drv.MajorVersion}), {drv.Environment}");
            sb.AppendLine($"Package       : {drv.InfOriginalName} {(drv.PublishedInf != null ? "(" + drv.PublishedInf + ")" : "")}");
            sb.AppendLine($"Origine       : {(drv.IsInbox ? "intégré à Windows (non exporté)" : "tiers" + (drv.Signer != null ? ", signé par " + drv.Signer : ""))}");
        }
        sb.AppendLine();

        string? BinName(short id) => lp.Bins.FirstOrDefault(b => b.id == id).name;
        sb.AppendLine("RÉGLAGES PAR DÉFAUT DE LA FILE");
        AppendSettings(sb, lp.GlobalDevMode, BinName);
        sb.AppendLine();
        sb.AppendLine("PRÉFÉRENCES DE L'UTILISATEUR COURANT");
        if (lp.UserDevMode == null) sb.AppendLine("Aucune : l'utilisateur utilise les valeurs de la file.");
        else
        {
            if (lp.GlobalDevMode != null && lp.UserDevMode.AsSpan().SequenceEqual(lp.GlobalDevMode)) sb.AppendLine("(identiques aux valeurs de la file)");
            AppendSettings(sb, lp.UserDevMode, BinName);
        }
        sb.AppendLine();
        sb.AppendLine("OPTIONS ET DONNÉES DU PILOTE");
        sb.AppendLine(lp.DriverData == null ? "Non lues" : $"{lp.DriverData.Count} valeur(s) brutes (restaurables avec le même pilote, même version)");
        if (lp.Bins.Count > 0) sb.AppendLine("Bacs déclarés : " + string.Join(", ", lp.Bins.Select(b => b.name)));

        if (e.Security != null)
        {
            sb.AppendLine();
            sb.AppendLine("PERMISSIONS");
            foreach (var a in e.Security.Aces.GroupBy(a => a.Account ?? a.Sid))
            {
                var first = a.First();
                string port = first.Portability switch
                {
                    SidPortability.WellKnown => "universel",
                    SidPortability.Domain => "domaine",
                    SidPortability.LocalAccount => "compte local, non transférable tel quel",
                    _ => "introuvable",
                };
                sb.AppendLine($"{(a.Any(x => x.IsDeny) ? "Refus " : "Accès ")} {a.Key} ({port})");
            }
        }
        if (lp.ReadIssues.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("LIMITES DE LECTURE");
            foreach (var i in lp.ReadIssues) sb.AppendLine($"- {i.Component} : {i.Detail}");
        }
        return sb.ToString();
    }

    private static void AppendSettings(StringBuilder sb, byte[]? bytes, Func<short, string?> binName)
    {
        if (bytes == null || !DevMode.TryParse(bytes, out var dm)) { sb.AppendLine("Non disponibles"); return; }
        foreach (var (label, value) in Labels.Describe(dm!.ToSummary(binName)))
            if (value != null) sb.AppendLine($"{label,-14}: {value}");
        sb.AppendLine($"Partie privée : {dm.DriverExtra} octets (propres au pilote)");
    }

}
