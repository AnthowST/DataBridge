// Moteur Outlook repris de Sauvegarde Outlook (même format de PST et mêmes propriétés de dossier :
// une sauvegarde faite par DataBridge peut aussi être restaurée avec Sauvegarde Outlook, et inversement).
#nullable disable
using System.Text;

namespace DataBridge.Core.Outlook;

public sealed class UserMessageException : Exception
{
    public UserMessageException(string message) : base(message) { }
}

/// <summary>Le PST est encore verrouillé par Outlook (normal juste après une sauvegarde, jusqu'au redémarrage d'Outlook).</summary>
public sealed class BackupLockedException : Exception
{
    public BackupLockedException() : base("Le fichier de sauvegarde est encore verrouillé par Outlook.") { }
}

public interface ISelectable
{
    bool Selected { get; set; }
}

public sealed class StoreEntry
{
    public string StoreId { get; set; }
    public string DisplayName { get; set; }
    public string FilePath { get; set; }
    public bool IsDefault { get; set; }
    public string Description { get; set; }
    public override string ToString() => Description;
}

public sealed class FolderNode
{
    public string EntryId { get; set; }
    public string Name { get; set; }
    /// <summary>Chemin depuis la racine de la boîte (racine exclue).</summary>
    public List<string> Path { get; set; } = new();
    /// <summary>Type de dossier spécial (Boîte de réception, Envoyés…) pour chaque niveau du chemin, 0 sinon.</summary>
    public List<int> PathSpecial { get; set; } = new();
    public int SpecialType { get; set; }
    public FolderNode Parent { get; set; }
    public List<FolderNode> Children { get; } = new();
    public List<MailEntry> Mails { get; } = new();
    /// <summary>Coché dans l'arborescence (sert à recréer aussi les dossiers vides).</summary>
    public bool Checked { get; set; }
    public string PathText => string.Join(" \\ ", Path);
}

public sealed class MailEntry : ISelectable
{
    public string EntryId { get; set; }
    public string Subject { get; set; }
    public string Sender { get; set; }
    public DateTime? Date { get; set; }
    public bool HasAttachments { get; set; }
    public FolderNode Folder { get; set; }
    public bool Selected { get; set; }
}

public sealed class ContactEntry : ISelectable
{
    public string EntryId { get; set; }
    public string Name { get; set; }
    public string Email { get; set; }
    public string Company { get; set; }
    public bool IsList { get; set; }
    public FolderNode Folder { get; set; }
    public bool Selected { get; set; }
}

public sealed class EventEntry : ISelectable
{
    public string EntryId { get; set; }
    public string Subject { get; set; }
    public string Location { get; set; }
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public bool IsRecurring { get; set; }
    public DateTime? SeriesStart { get; set; }
    public DateTime? SeriesEnd { get; set; }
    public bool NoEnd { get; set; }
    public string Recurrence { get; set; } = "";
    public FolderNode Folder { get; set; }
    public bool Selected { get; set; }
}

public sealed class AutocompleteInfo
{
    public bool Found { get; set; }
    public int Size { get; set; }
    public int? Entries { get; set; }
    public string Message { get; set; } = "";
}

public sealed class StoreContent
{
    public List<FolderNode> MailRoots { get; } = new();
    public List<FolderNode> ContactFolders { get; } = new();
    public List<FolderNode> CalendarFolders { get; } = new();
    public List<ContactEntry> Contacts { get; } = new();
    public List<EventEntry> Events { get; } = new();
    public AutocompleteInfo Autocomplete { get; set; } = new();
    public List<string> Warnings { get; } = new();
    /// <summary>Pour un PST créé par cette application : boîte d'origine et date de la sauvegarde.</summary>
    public string BackupInfo { get; set; } = "";

    /// <summary>Tous les dossiers de mails, parents avant enfants.</summary>
    public IEnumerable<FolderNode> AllMailFolders() => MailRoots.SelectMany(Flatten);
    public IEnumerable<MailEntry> AllMails() => AllMailFolders().SelectMany(f => f.Mails);

    static IEnumerable<FolderNode> Flatten(FolderNode f) => new[] { f }.Concat(f.Children.SelectMany(Flatten));
}

public sealed record MailJob(FolderNode Folder, List<MailEntry> Mails);

public sealed record ProgressInfo(int Done, int Total, string Text);

public sealed class ExportRequest
{
    public string StoreId { get; set; }
    public string StoreName { get; set; }
    public string DestDir { get; set; }
    public bool DetachPst { get; set; }
    public bool IncludeAutocomplete { get; set; }
    public List<MailJob> MailJobs { get; set; } = new();
    public List<ContactEntry> Contacts { get; set; } = new();
    public List<EventEntry> Events { get; set; } = new();
}

public sealed class RestoreRequest
{
    public string BackupStoreId { get; set; }
    public string BackupFile { get; set; }
    public string TargetStoreId { get; set; }
    public string TargetName { get; set; }
    public bool IntoNewFolder { get; set; }
    public bool SkipDuplicates { get; set; }
    public string ReportDir { get; set; }
    public List<MailJob> MailJobs { get; set; } = new();
    public List<ContactEntry> Contacts { get; set; } = new();
    public List<EventEntry> Events { get; set; } = new();
    public string AutocompleteFile { get; set; }
    public bool AutoAsContacts { get; set; }
    public bool AutoReplace { get; set; }
    public bool BackupOpenedDirectly { get; set; }
}

public sealed class Counts
{
    public int Requested, Saved, Duplicates, Ignored, Errors;
    public override string ToString() =>
        Requested == 0 ? "non demandé"
        : $"{Saved} copié(s)" + (Duplicates > 0 ? $", {Duplicates} déjà présent(s) dans la cible (non recopiés)" : "")
          + $", {Ignored} ignoré(s), {Errors} erreur(s)  (sur {Requested} sélectionné(s))";
}

public enum OperationKind { Backup, Restore }

public sealed class OperationResult
{
    public OperationKind Kind { get; set; }
    public DateTime Date { get; } = DateTime.Now;
    public string SourceName { get; set; }
    public string TargetName { get; set; }
    public string PstPath { get; set; }
    public bool Detached { get; set; }
    public string ReportPath { get; set; }
    public string Fatal { get; set; }
    public bool Cancelled { get; set; }
    public int NotProcessed { get; set; }
    public Counts Mails { get; } = new();
    public Counts Contacts { get; } = new();
    public Counts Events { get; } = new();
    public bool AutocompleteRequested { get; set; }
    public bool AutocompleteOk { get; set; }
    public bool BackupOpenedDirectly { get; set; }
    public List<string> AutocompleteLines { get; } = new();
    public List<string> AutocompleteFiles { get; } = new();
    public List<string> Notes { get; } = new();
    public List<string> Details { get; } = new();

    int TotalErrors => Mails.Errors + Contacts.Errors + Events.Errors;
    int TotalIgnored => Mails.Ignored + Contacts.Ignored + Events.Ignored;
    string Noun => Kind == OperationKind.Backup ? "Sauvegarde" : "Restauration";

    public bool HasProblems => Fatal != null || Cancelled || NotProcessed > 0 || TotalErrors > 0 || TotalIgnored > 0
                               || (AutocompleteRequested && !AutocompleteOk);

    public string Headline =>
        Fatal != null ? $"{Noun} interrompue par une erreur : elle est incomplète."
        : Cancelled ? $"{Noun} annulée : elle est incomplète."
        : HasProblems ? $"{Noun} terminée, mais avec des éléments ignorés ou en erreur (voir le compte rendu)."
        : $"{Noun} terminée sans erreur.";

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine(Kind == OperationKind.Backup ? "COMPTE RENDU DE SAUVEGARDE OUTLOOK" : "COMPTE RENDU DE RESTAURATION OUTLOOK");
        sb.AppendLine($"Date           : {Date:dd.MM.yyyy HH:mm:ss}");
        if (Kind == OperationKind.Backup)
            sb.AppendLine($"Boîte source   : {SourceName}");
        else
        {
            sb.AppendLine($"Sauvegarde     : {SourceName}");
            sb.AppendLine($"Boîte cible    : {TargetName}");
        }
        sb.AppendLine($"Résultat       : {Headline}");
        sb.AppendLine();
        if (PstPath != null)
            sb.AppendLine($"Fichier PST    : {PstPath}" + (Detached ? "  (retiré d'Outlook, le fichier reste sur le disque)" : "  (toujours ouvert dans Outlook)"));
        sb.AppendLine($"Mails          : {Mails}");
        sb.AppendLine($"Contacts       : {Contacts}");
        sb.AppendLine($"Calendrier     : {Events}");
        if (!AutocompleteRequested) sb.AppendLine("Autocomplétion : non demandée");
        else
        {
            if (AutocompleteLines.Count == 0) AutocompleteLines.Add("NON traitée (opération interrompue avant cette étape).");
            sb.AppendLine("Autocomplétion : " + AutocompleteLines[0]);
            foreach (var l in AutocompleteLines.Skip(1)) sb.AppendLine("                 " + l);
            foreach (var f in AutocompleteFiles) sb.AppendLine("                 Fichier : " + f);
        }
        if (NotProcessed > 0) sb.AppendLine($"Non traités    : {NotProcessed} élément(s) (annulation ou erreur bloquante)");
        if (Fatal != null) sb.AppendLine($"ERREUR BLOQUANTE : {Fatal}");
        sb.AppendLine();
        sb.AppendLine(Kind == OperationKind.Backup
            ? "Mode copie uniquement : aucun élément de la boîte source n'a été déplacé, supprimé ou enregistré."
            : "Mode ajout uniquement : aucun élément existant de la boîte cible n'a été modifié ou supprimé. "
              + (BackupOpenedDirectly ? "Le fichier de sauvegarde a été ouvert directement (à votre demande, car Outlook le verrouillait) ; son contenu n'a pas été modifié par l'application."
                                      : "Le fichier de sauvegarde d'origine n'a pas été ouvert (une copie de travail a été utilisée)."));
        foreach (var n in Notes) sb.AppendLine(n);
        if (Details.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Détail des éléments ignorés et des erreurs :");
            foreach (var d in Details) sb.AppendLine(" - " + d);
        }
        return sb.ToString();
    }
}
