// Moteur Outlook repris de Sauvegarde Outlook (même format de PST et mêmes propriétés de dossier :
// une sauvegarde faite par DataBridge peut aussi être restaurée avec Sauvegarde Outlook, et inversement).
#nullable disable
using System.Text.RegularExpressions;

namespace DataBridge.Core.Outlook;

/// <summary>
/// Dossiers spéciaux d'Outlook (valeurs OlDefaultFolders). Servent à replacer, à la restauration,
/// la « Boîte de réception » sauvegardée dans la boîte de réception de la cible même si son nom diffère
/// (langue, fournisseur IMAP : « Inbox », « Sent », « Trash »…).
/// </summary>
public static class SpecialFolders
{
    public const int Deleted = 3, Outbox = 4, Sent = 5, Inbox = 6, Calendar = 9, Contacts = 10, Drafts = 16, Junk = 23;

    public static readonly int[] All = { Inbox, Sent, Drafts, Deleted, Junk, Calendar, Contacts };

    static readonly Dictionary<int, string[]> Names = new()
    {
        [Inbox] = new[] { "Boîte de réception", "Inbox", "Posteingang", "Posta in arrivo", "Courrier entrant", "Bandeja de entrada" },
        [Sent] = new[] { "Éléments envoyés", "Sent Items", "Sent", "Sent Messages", "Sent Mail", "Envoyés", "Messages envoyés", "Gesendete Elemente", "Gesendet", "Posta inviata", "Inviati", "Elementos enviados" },
        [Drafts] = new[] { "Brouillons", "Drafts", "Entwürfe", "Bozze", "Borradores" },
        [Deleted] = new[] { "Éléments supprimés", "Deleted Items", "Deleted Messages", "Trash", "Corbeille", "Gelöschte Elemente", "Papierkorb", "Posta eliminata", "Cestino", "Elementos eliminados", "Papelera" },
        [Junk] = new[] { "Courrier indésirable", "Junk", "Junk E-mail", "Junk Email", "Spam", "Pourriel", "Junk-E-Mail", "Posta indesiderata", "Correo no deseado" },
        [Calendar] = new[] { "Calendrier", "Calendar", "Kalender", "Calendario" },
        [Contacts] = new[] { "Contacts", "Kontakte", "Contatti", "Contactos" },
    };

    /// <summary>Devine le type d'un dossier de premier niveau d'après son nom (« Calendrier (Cet ordinateur uniquement) » compris).</summary>
    public static int Guess(string name, int defaultItemType)
    {
        if (string.IsNullOrWhiteSpace(name)) return 0;
        string n = Regex.Replace(name, @"\s*\(.*\)\s*$", "").Trim();
        foreach (var (type, names) in Names)
        {
            bool kindOk = type == Calendar ? defaultItemType == 1 : type == Contacts ? defaultItemType == 2 : defaultItemType == 0;
            if (kindOk && names.Any(x => string.Equals(x, n, StringComparison.CurrentCultureIgnoreCase))) return type;
        }
        return 0;
    }
}
