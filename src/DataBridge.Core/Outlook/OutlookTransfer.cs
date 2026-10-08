namespace DataBridge.Core.Outlook;

/// <summary>What is transferred from a mailbox: always everything in each checked category (no choice item by item).</summary>
[Flags]
public enum OutlookCategories
{
    None = 0,
    Mails = 1,
    Calendar = 2,
    Contacts = 4,
    Autocomplete = 8,
    All = Mails | Calendar | Contacts | Autocomplete,
}

public static class OutlookCategoryText
{
    public static string Describe(OutlookCategories c)
    {
        var parts = new List<string>();
        if (c.HasFlag(OutlookCategories.Mails)) parts.Add("mails");
        if (c.HasFlag(OutlookCategories.Calendar)) parts.Add("calendrier");
        if (c.HasFlag(OutlookCategories.Contacts)) parts.Add("contacts");
        if (c.HasFlag(OutlookCategories.Autocomplete)) parts.Add("autocomplétion");
        return parts.Count == 0 ? "rien" : string.Join(", ", parts);
    }

    /// <summary>Manifest names: mails, calendar, contacts, autocomplete.</summary>
    public static List<string> ToNames(OutlookCategories c)
    {
        var names = new List<string>();
        if (c.HasFlag(OutlookCategories.Mails)) names.Add("mails");
        if (c.HasFlag(OutlookCategories.Calendar)) names.Add("calendar");
        if (c.HasFlag(OutlookCategories.Contacts)) names.Add("contacts");
        if (c.HasFlag(OutlookCategories.Autocomplete)) names.Add("autocomplete");
        return names;
    }

    public static OutlookCategories FromNames(IEnumerable<string>? names)
    {
        var c = OutlookCategories.None;
        foreach (var n in names ?? [])
        {
            c |= n.ToLowerInvariant() switch
            {
                "mails" => OutlookCategories.Mails,
                "calendar" => OutlookCategories.Calendar,
                "contacts" => OutlookCategories.Contacts,
                "autocomplete" => OutlookCategories.Autocomplete,
                _ => OutlookCategories.None,
            };
        }
        return c;
    }
}

/// <summary>Backup of one mailbox into a new PST inside the backup folder (Outlook\Boite).</summary>
public sealed record OutlookBackupJob(string StoreId, string StoreName, OutlookCategories Categories)
{
    public const string DestinationRelative = @"Outlook\Boite";
}

/// <summary>Restore of the mailbox of a backup into a mailbox of this Outlook profile, explicitly chosen.</summary>
public sealed record OutlookRestoreJob(
    string? PstPath,
    string? AutocompletePath,
    string TargetStoreId,
    string TargetName,
    OutlookCategories Categories,
    bool IntoNewFolder,
    bool ReplaceAutocompleteCache);

/// <summary>Outcome of a mailbox backup or restore (the copy / restore log and the final screen read it).</summary>
public sealed class OutlookTransferResult
{
    public OutlookCategories Categories { get; init; }
    public string MailboxName { get; init; } = "";
    /// <summary>Result of the Outlook engine (null when nothing could start: Outlook closed, mailbox not found...).</summary>
    public OperationResult? Detail { get; set; }
    public string? Error { get; set; }
    public bool Cancelled { get; set; }
    public List<string> Warnings { get; } = [];

    public bool Ok => Error is null && !Cancelled && Detail is { HasProblems: false };
    public int Mails => Detail?.Mails.Saved ?? 0;
    public int Contacts => Detail?.Contacts.Saved ?? 0;
    public int Events => Detail?.Events.Saved ?? 0;
    public int Duplicates => (Detail?.Mails.Duplicates ?? 0) + (Detail?.Contacts.Duplicates ?? 0) + (Detail?.Events.Duplicates ?? 0);
    public int Errors => (Detail?.Mails.Errors ?? 0) + (Detail?.Contacts.Errors ?? 0) + (Detail?.Events.Errors ?? 0);
    public bool AutocompleteOk => Detail is { AutocompleteRequested: true, AutocompleteOk: true };
    public string? PstPath => Detail?.PstPath;
    public string? ReportPath => Detail?.ReportPath;
    public string? AutocompleteFile => Detail?.AutocompleteFiles.FirstOrDefault(f => f.EndsWith(".bin", StringComparison.OrdinalIgnoreCase));

    public string Headline =>
        Error != null ? Error
        : Cancelled || Detail?.Cancelled == true ? "Interrompu par l'utilisateur."
        : Detail is null ? "Non traité."
        : Detail.Headline;

    /// <summary>Lines for the logs (counters, files, problems). Never a mail content, an address book or a password.</summary>
    public IEnumerable<string> LogLines()
    {
        yield return $"Boîte aux lettres : {MailboxName}";
        yield return $"Catégories        : {OutlookCategoryText.Describe(Categories)}";
        yield return $"Résultat          : {Headline}";
        if (Detail is { } d)
        {
            if (d.PstPath != null) yield return $"Fichier PST       : {d.PstPath}";
            yield return $"Mails             : {d.Mails}";
            yield return $"Contacts          : {d.Contacts}";
            yield return $"Calendrier        : {d.Events}";
            if (d.AutocompleteRequested)
                foreach (var l in d.AutocompleteLines) yield return $"Autocomplétion    : {l}";
            foreach (var f in d.AutocompleteFiles) yield return $"Fichier           : {f}";
            if (d.NotProcessed > 0) yield return $"Non traités       : {d.NotProcessed} élément(s)";
            if (d.ReportPath != null) yield return $"Compte rendu      : {d.ReportPath}";
            foreach (var n in d.Notes) yield return n;
        }
        foreach (var w in Warnings) yield return "Attention : " + w;
    }
}

/// <summary>
/// Runs the Outlook engine (taken from Sauvegarde Outlook) for DataBridge: everything in the checked categories, no
/// item-by-item choice. Backup: the source mailbox is only read, each item goes through a temporary .msg copy into a
/// new PST, which is then removed from the Outlook profile. Restore: a working copy of the PST is opened (never the
/// backup itself), the target mailbox only receives additions, duplicates are skipped. Nothing is ever sent.
/// </summary>
public static class OutlookTransfer
{
    public const string NotRunningMessage =
        "Outlook classique n'est pas ouvert : ouvrez Outlook, attendez qu'il soit complètement chargé, puis relancez.";

    public static OutlookTransferResult Backup(OutlookBackupJob job, string backupRoot, Action<string> status, Action<int, int> progress, CancellationToken ct)
    {
        var result = new OutlookTransferResult { Categories = job.Categories, MailboxName = job.StoreName };
        if (!OutlookSession.IsOutlookRunning())
        {
            result.Error = NotRunningMessage;
            return result;
        }
        try
        {
            var destination = Path.Combine(backupRoot, OutlookBackupJob.DestinationRelative);
            Directory.CreateDirectory(destination);
            var session = OutlookSession.Shared;
            status("Lecture de la boîte aux lettres…");
            var content = session.Run(s => s.LoadStore(job.StoreId, new Reporter<string>(status), ct));
            result.Warnings.AddRange(content.Warnings);
            if (job.Categories.HasFlag(OutlookCategories.Autocomplete) && !content.Autocomplete.Found)
                result.Warnings.Add("Autocomplétion non sauvegardée : " + content.Autocomplete.Message);
            var request = BuildExport(job, content, destination);
            result.Detail = session.Run(s => s.Export(request, new Reporter<ProgressInfo>(p =>
            {
                progress(p.Done, p.Total);
                status(p.Text);
            }), ct));
            result.Cancelled = result.Detail.Cancelled;
        }
        catch (OperationCanceledException)
        {
            result.Cancelled = true;
        }
        catch (Exception ex)
        {
            result.Error = ex is UserMessageException ? ex.Message : "Sauvegarde Outlook impossible : " + ex.Message;
        }
        return result;
    }

    /// <summary>Everything of the checked categories (every mail folder is recreated, even empty).</summary>
    public static ExportRequest BuildExport(OutlookBackupJob job, StoreContent content, string destination) => new()
    {
        StoreId = job.StoreId,
        StoreName = job.StoreName,
        DestDir = destination,
        DetachPst = true,
        IncludeAutocomplete = job.Categories.HasFlag(OutlookCategories.Autocomplete) && content.Autocomplete.Found,
        MailJobs = job.Categories.HasFlag(OutlookCategories.Mails)
            ? content.AllMailFolders().Select(f => new MailJob(f, f.Mails.ToList())).ToList()
            : [],
        Contacts = job.Categories.HasFlag(OutlookCategories.Contacts) ? content.Contacts.ToList() : [],
        Events = job.Categories.HasFlag(OutlookCategories.Calendar) ? content.Events.ToList() : [],
    };

    public static OutlookTransferResult Restore(OutlookRestoreJob job, string reportFolder, Action<string> status, Action<int, int> progress, CancellationToken ct)
    {
        var result = new OutlookTransferResult { Categories = job.Categories, MailboxName = job.TargetName };
        if (!OutlookSession.IsOutlookRunning())
        {
            result.Error = NotRunningMessage;
            return result;
        }

        var session = OutlookSession.Shared;
        var needsPst = job.Categories.HasFlag(OutlookCategories.Mails) || job.Categories.HasFlag(OutlookCategories.Calendar)
                       || job.Categories.HasFlag(OutlookCategories.Contacts);
        StoreEntry? backup = null;
        try
        {
            Directory.CreateDirectory(reportFolder);
            StoreContent? content = null;
            if (needsPst && job.PstPath != null)
            {
                status("Copie de travail du fichier PST de la sauvegarde…");
                try
                {
                    backup = session.Run(s => s.OpenBackup(job.PstPath, direct: false, new Reporter<string>(status)));
                }
                catch (BackupLockedException)
                {
                    result.Error = "Le fichier PST de la sauvegarde est verrouillé par Outlook (il vient sans doute d'être créé sur ce PC). "
                                   + "Redémarrez Outlook, puis relancez la restauration de la boîte aux lettres.";
                    return result;
                }
                status("Lecture de la sauvegarde…");
                content = session.Run(s => s.LoadStore(backup.StoreId, new Reporter<string>(status), ct));
            }
            var request = BuildRestore(job, backup, content, reportFolder);
            result.Detail = session.Run(s => s.Restore(request, new Reporter<ProgressInfo>(p =>
            {
                progress(p.Done, p.Total);
                status(p.Text);
            }), ct));
            result.Cancelled = result.Detail.Cancelled;
        }
        catch (OperationCanceledException)
        {
            result.Cancelled = true;
        }
        catch (Exception ex)
        {
            result.Error = ex is UserMessageException ? ex.Message : "Restauration Outlook impossible : " + ex.Message;
        }
        finally
        {
            if (backup != null)
            {
                try
                {
                    session.Run(s =>
                    {
                        s.CloseBackup();
                        return true;
                    });
                }
                catch
                {
                    // The working copy is removed at the next opening.
                }
            }
        }
        return result;
    }

    /// <summary>Everything of the checked categories, merged into the target (special folders mapped), duplicates skipped.</summary>
    public static RestoreRequest BuildRestore(OutlookRestoreJob job, StoreEntry? backup, StoreContent? content, string reportFolder)
    {
        var auto = job.Categories.HasFlag(OutlookCategories.Autocomplete) && job.AutocompletePath != null;
        return new RestoreRequest
        {
            BackupStoreId = backup?.StoreId,
            BackupFile = job.PstPath ?? job.AutocompletePath ?? "",
            TargetStoreId = job.TargetStoreId,
            TargetName = job.TargetName,
            IntoNewFolder = job.IntoNewFolder,
            SkipDuplicates = true,
            ReportDir = reportFolder,
            MailJobs = content != null && job.Categories.HasFlag(OutlookCategories.Mails)
                ? content.AllMailFolders().Select(f => new MailJob(f, f.Mails.ToList())).ToList()
                : [],
            Contacts = content != null && job.Categories.HasFlag(OutlookCategories.Contacts) ? content.Contacts.ToList() : [],
            Events = content != null && job.Categories.HasFlag(OutlookCategories.Calendar) ? content.Events.ToList() : [],
            AutocompleteFile = auto ? job.AutocompletePath : null,
            AutoAsContacts = auto,
            AutoReplace = auto && job.ReplaceAutocompleteCache,
            BackupOpenedDirectly = false,
        };
    }
}
