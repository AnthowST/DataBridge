using DataBridge.Printers.Model;

namespace DataBridge.Printers.Util;

/// <summary>French display labels.</summary>
public static class Labels
{
    public static string Kind(PrinterKind k) => k switch
    {
        PrinterKind.TcpIp => "Réseau TCP/IP",
        PrinterKind.SharedConnection => "Connexion partagée (UNC)",
        PrinterKind.MachineConnection => "Connexion partagée (machine)",
        PrinterKind.Usb => "USB",
        PrinterKind.Wsd => "WSD",
        PrinterKind.Ipp => "IPP",
        PrinterKind.Virtual => "Virtuelle",
        PrinterKind.UniversalPrint => "Universal Print",
        _ => "Autre",
    };

    public static string Support(SupportLevel s) => s == SupportLevel.Supported ? "Pris en charge" : "Détectée seulement";

    public static string State(ComponentState s) => s switch
    {
        ComponentState.Exported => "Exporté",
        ComponentState.Partial => "Partiel",
        ComponentState.NotExported => "Non exporté",
        ComponentState.NotApplicable => "Sans objet",
        ComponentState.Failed => "Échec",
        _ => s.ToString(),
    };

    public static string Orientation(short v) => v switch { 1 => "Portrait", 2 => "Paysage", _ => v.ToString() };
    public static string Color(short v) => v switch { 1 => "Noir et blanc", 2 => "Couleur", _ => v.ToString() };
    public static string Duplex(short v) => v switch { 1 => "Recto", 2 => "Recto verso (bord long)", 3 => "Recto verso (bord court)", _ => v.ToString() };
    public static string Collate(short v) => v switch { 0 => "Non", 1 => "Oui", _ => v.ToString() };

    public static string Quality(short v) => v switch
    {
        -1 => "Brouillon",
        -2 => "Basse",
        -3 => "Moyenne",
        -4 => "Haute",
        > 0 => $"{v} ppp",
        _ => v.ToString(),
    };

    public static string Paper(short v) => v switch
    {
        1 => "Letter",
        5 => "Legal",
        8 => "A3",
        9 => "A4",
        11 => "A5",
        13 => "B5 (JIS)",
        256 => "Personnalisé",
        _ => $"n° {v}",
    };

    /// <summary>(label, value) pairs of a summary, in a stable order — used for display and comparison.</summary>
    public static List<(string label, string? value)> Describe(DevModeSummary? s)
    {
        var list = new List<(string, string?)>();
        if (s == null) return list;
        list.Add(("Format", s.FormName ?? (s.PaperSize is short p ? Paper(p) : null)));
        list.Add(("Orientation", s.Orientation is short o ? Orientation(o) : null));
        list.Add(("Couleur", s.Color is short c ? Color(c) : null));
        list.Add(("Recto verso", s.Duplex is short d ? Duplex(d) : null));
        list.Add(("Qualité", s.PrintQuality is short q ? Quality(q) : null));
        list.Add(("Résolution verticale", s.YResolution is short y ? $"{y} ppp" : null));
        list.Add(("Bac", s.DefaultSourceName ?? s.DefaultSource?.ToString()));
        list.Add(("Copies", s.Copies?.ToString()));
        list.Add(("Assemblage", s.Collate is short col ? Collate(col) : null));
        list.Add(("Pages par feuille", s.Nup?.ToString()));
        list.Add(("Échelle", s.Scale is short sc ? $"{sc} %" : null));
        return list;
    }
}
